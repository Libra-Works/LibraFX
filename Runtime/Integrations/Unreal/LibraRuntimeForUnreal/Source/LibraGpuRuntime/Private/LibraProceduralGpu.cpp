#include "LibraProceduralGpu.h"
#include "Engine/TextureRenderTarget2D.h"
#include "TextureResource.h"
#include "GlobalShader.h"
#include "GlobalRenderResources.h"
#include "RenderGraphBuilder.h"
#include "RenderGraphUtils.h"
#include "RenderCommandFence.h"
#include "ShaderParameterStruct.h"
#include "UObject/Package.h"

class FLibraProceduralCS final : public FGlobalShader
{
    DECLARE_GLOBAL_SHADER(FLibraProceduralCS);
    SHADER_USE_PARAMETER_STRUCT(FLibraProceduralCS, FGlobalShader);
    BEGIN_SHADER_PARAMETER_STRUCT(FParameters, )
        SHADER_PARAMETER(uint32, LibraProceduralOperationCount)
        SHADER_PARAMETER(uint32, LibraProceduralOutputRegister)
        SHADER_PARAMETER(uint32, LibraProceduralResolution)
        SHADER_PARAMETER(float, LibraProceduralLocalTime)
        SHADER_PARAMETER_RDG_BUFFER_SRV(StructuredBuffer<PixelOperation>, LibraProceduralOperations)
        SHADER_PARAMETER_RDG_TEXTURE_UAV(RWTexture2D<float4>, LibraProceduralOutput)
        SHADER_PARAMETER_RDG_TEXTURE(Texture2D, LibraProceduralTexture0)
        SHADER_PARAMETER_RDG_TEXTURE(Texture2D, LibraProceduralTexture1)
        SHADER_PARAMETER_RDG_TEXTURE(Texture2D, LibraProceduralTexture2)
        SHADER_PARAMETER_RDG_TEXTURE(Texture2D, LibraProceduralTexture3)
        SHADER_PARAMETER_SAMPLER(SamplerState, sampler_linear_repeat)
    END_SHADER_PARAMETER_STRUCT()
    static bool ShouldCompilePermutation(const FGlobalShaderPermutationParameters& parameters)
    {
        return IsFeatureLevelSupported(parameters.Platform, ERHIFeatureLevel::SM5);
    }
};
IMPLEMENT_GLOBAL_SHADER(FLibraProceduralCS, "/LibraGpu/Private/LibraProcedural.usf", "Generate", SF_Compute);

struct FLibraProceduralGpu::FState
{
    TRefCountPtr<FRDGPooledBuffer> Operations;
    FTextureRHIRef Output;
    FTextureRHIRef Textures[4];
    uint32 Resolution = 0, Count = 0, OutputRegister = 0;
    struct FUpload
    {
        TArray<LibraProceduralPixelOperation> Values;
        FRenderCommandFence Fence;
    };
    FUpload Uploads[3];
    uint32 NextUpload = 0;
};
FLibraProceduralGpu::FLibraProceduralGpu() : State(MakeShared<FState, ESPMode::ThreadSafe>()) {}
FLibraProceduralGpu::~FLibraProceduralGpu()
{
    for (auto& upload : State->Uploads)
    {
        upload.Fence.Wait();
    }
    const auto state = State;
    ENQUEUE_RENDER_COMMAND(LibraProceduralRelease)([state](FRHICommandListImmediate&)
    {
        state->Operations.SafeRelease(); state->Output.SafeRelease();
        for (auto& texture : state->Textures)
        {
            texture.SafeRelease();
        }
    });
}
bool FLibraProceduralGpu::Initialize(uint32 resolution, uint32 count, uint32 outputRegister,
    TConstArrayView<UTexture*> textures, FString& error)
{
    if (resolution < 16 || resolution > 1024 || (resolution & (resolution - 1)) != 0 || count == 0 || count > 32 ||
        outputRegister == 0 || outputRegister > count || textures.Num() != 4)
    {
        error = TEXT("Invalid procedural GPU resource limits.");
        return false;
    }
    Output.Reset(NewObject<UTextureRenderTarget2D>(GetTransientPackage()));
    Output->bCanCreateUAV = true; Output->bAutoGenerateMips = false;
    Output->AddressX = TA_Wrap; Output->AddressY = TA_Wrap;
    Output->InitCustomFormat(resolution, resolution, PF_FloatRGBA, true);
    Output->UpdateResourceImmediate(true);
    auto* outputResource = Output->GameThread_GetRenderTargetResource();
    if (!outputResource)
    {
        error = TEXT("Cannot create procedural render target.");
        return false;
    }
    State->Resolution = resolution; State->Count = count; State->OutputRegister = outputRegister;
    for (auto& upload : State->Uploads)
    {
        upload.Values.SetNumUninitialized(count);
    }
    auto inputs = TArray<FTextureResource*>{}; inputs.Reserve(4);
    for (auto* texture : textures)
    {
        inputs.Add(texture ? texture->GetResource() : nullptr);
    }
    const auto state = State;
    ENQUEUE_RENDER_COMMAND(LibraProceduralInitialize)([state, outputResource, inputs = MoveTemp(inputs)](FRHICommandListImmediate&)
    {
        state->Output = outputResource->GetRenderTargetTexture();
        for (auto i = 0; i < 4; ++i)
        {
            state->Textures[i] = inputs[i] ? inputs[i]->TextureRHI : GWhiteTexture->TextureRHI;
        }
    });
    return true;
}
bool FLibraProceduralGpu::Update(const LibraProceduralPixelOperation* operations, uint32 count, double time)
{
    if (!operations || count != State->Count || !FMath::IsFinite(time) || FMath::Abs(time) > 1e9)
    {
        return false;
    }
    auto& upload = State->Uploads[State->NextUpload];
    if (!upload.Fence.IsFenceComplete())
    {
        return false;
    }
    FMemory::Memcpy(upload.Values.GetData(), operations, count * sizeof(LibraProceduralPixelOperation));
    const auto state = State; const auto slot = State->NextUpload;
    ENQUEUE_RENDER_COMMAND(LibraProceduralDispatch)([state, slot, time](FRHICommandListImmediate& commandList)
    {
        if (!state->Output.IsValid())
        {
            return;
        }
        auto graph = FRDGBuilder(commandList);
        auto* buffer = state->Operations.IsValid() ? graph.RegisterExternalBuffer(state->Operations) :
            graph.CreateBuffer(FRDGBufferDesc::CreateStructuredDesc(sizeof(LibraProceduralPixelOperation), state->Count), TEXT("LibraProcedural.Operations"));
        graph.QueueBufferUpload(buffer, state->Uploads[slot].Values.GetData(), state->Count * sizeof(LibraProceduralPixelOperation), ERDGInitialDataFlags::NoCopy);
        auto* output = graph.RegisterExternalTexture(CreateRenderTarget(state->Output, TEXT("LibraProcedural.Output")));
        auto* parameters = graph.AllocParameters<FLibraProceduralCS::FParameters>();
        parameters->LibraProceduralOperationCount = state->Count; parameters->LibraProceduralOutputRegister = state->OutputRegister;
        parameters->LibraProceduralResolution = state->Resolution; parameters->LibraProceduralLocalTime = static_cast<float>(time);
        parameters->LibraProceduralOperations = graph.CreateSRV(buffer); parameters->LibraProceduralOutput = graph.CreateUAV(output);
        parameters->LibraProceduralTexture0 = graph.RegisterExternalTexture(CreateRenderTarget(state->Textures[0], TEXT("LibraProcedural.Texture0")));
        parameters->LibraProceduralTexture1 = graph.RegisterExternalTexture(CreateRenderTarget(state->Textures[1], TEXT("LibraProcedural.Texture1")));
        parameters->LibraProceduralTexture2 = graph.RegisterExternalTexture(CreateRenderTarget(state->Textures[2], TEXT("LibraProcedural.Texture2")));
        parameters->LibraProceduralTexture3 = graph.RegisterExternalTexture(CreateRenderTarget(state->Textures[3], TEXT("LibraProcedural.Texture3")));
        parameters->sampler_linear_repeat = TStaticSamplerState<SF_Bilinear, AM_Wrap, AM_Wrap, AM_Wrap>::GetRHI();
        TShaderMapRef<FLibraProceduralCS> shader(GetGlobalShaderMap(GMaxRHIFeatureLevel));
        FComputeShaderUtils::AddPass(graph, RDG_EVENT_NAME("LibraProcedural.Generate"), shader, parameters,
            FIntVector((state->Resolution + 7) / 8, (state->Resolution + 7) / 8, 1));
        graph.QueueBufferExtraction(buffer, &state->Operations);
        graph.Execute();
    });
    upload.Fence.BeginFence(); State->NextUpload = (State->NextUpload + 1) % 3;
    return true;
}
UTexture* FLibraProceduralGpu::GetTexture() const { return Output.Get(); }
