#include "LibraGpuEffectRuntime.h"

#include "CommonRenderResources.h"
#include "Engine/AssetManager.h"
#include "Engine/StreamableManager.h"
#include "Engine/Texture2D.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "GlobalShader.h"
#include "GlobalRenderResources.h"
#include "LibraGpuEffectAsset.h"
#include "PipelineStateCache.h"
#include "PostProcess/PostProcessMaterialInputs.h"
#include "ProfilingDebugging/CpuProfilerTrace.h"
#include "RenderGraphBuilder.h"
#include "RenderGraphEvent.h"
#include "RenderGraphUtils.h"
#include "RenderingThread.h"
#include "RHITextureReference.h"
#include "RHIGPUReadback.h"
#include "SceneView.h"
#include "SceneViewExtension.h"
#include "ScreenPass.h"
#include "ShaderParameterStruct.h"

static constexpr int32 LibraGpuFlowMapTextureCount = 64;

#define LIBRA_GPU_FLOWMAP_PARAMETER(Index) SHADER_PARAMETER_TEXTURE(Texture2D, FlowMapTexture##Index)

class FLibraGpuInterpreterCS : public FGlobalShader
{
    DECLARE_GLOBAL_SHADER(FLibraGpuInterpreterCS);
    SHADER_USE_PARAMETER_STRUCT(FLibraGpuInterpreterCS, FGlobalShader);

    BEGIN_SHADER_PARAMETER_STRUCT(FParameters, )
        SHADER_PARAMETER_RDG_BUFFER_UAV(RWStructuredBuffer<LibraParticle>, Particles)
        SHADER_PARAMETER_RDG_BUFFER_UAV(RWStructuredBuffer<uint4>, Registers)
        // Keep the 64 texture SRV slots available on shader models with a 64-SRV limit.
        SHADER_PARAMETER_RDG_BUFFER_UAV(RWStructuredBuffer<uint4>, Instructions)
        LIBRA_GPU_FLOWMAP_PARAMETER(0)
        LIBRA_GPU_FLOWMAP_PARAMETER(1)
        LIBRA_GPU_FLOWMAP_PARAMETER(2)
        LIBRA_GPU_FLOWMAP_PARAMETER(3)
        LIBRA_GPU_FLOWMAP_PARAMETER(4)
        LIBRA_GPU_FLOWMAP_PARAMETER(5)
        LIBRA_GPU_FLOWMAP_PARAMETER(6)
        LIBRA_GPU_FLOWMAP_PARAMETER(7)
        LIBRA_GPU_FLOWMAP_PARAMETER(8)
        LIBRA_GPU_FLOWMAP_PARAMETER(9)
        LIBRA_GPU_FLOWMAP_PARAMETER(10)
        LIBRA_GPU_FLOWMAP_PARAMETER(11)
        LIBRA_GPU_FLOWMAP_PARAMETER(12)
        LIBRA_GPU_FLOWMAP_PARAMETER(13)
        LIBRA_GPU_FLOWMAP_PARAMETER(14)
        LIBRA_GPU_FLOWMAP_PARAMETER(15)
        LIBRA_GPU_FLOWMAP_PARAMETER(16)
        LIBRA_GPU_FLOWMAP_PARAMETER(17)
        LIBRA_GPU_FLOWMAP_PARAMETER(18)
        LIBRA_GPU_FLOWMAP_PARAMETER(19)
        LIBRA_GPU_FLOWMAP_PARAMETER(20)
        LIBRA_GPU_FLOWMAP_PARAMETER(21)
        LIBRA_GPU_FLOWMAP_PARAMETER(22)
        LIBRA_GPU_FLOWMAP_PARAMETER(23)
        LIBRA_GPU_FLOWMAP_PARAMETER(24)
        LIBRA_GPU_FLOWMAP_PARAMETER(25)
        LIBRA_GPU_FLOWMAP_PARAMETER(26)
        LIBRA_GPU_FLOWMAP_PARAMETER(27)
        LIBRA_GPU_FLOWMAP_PARAMETER(28)
        LIBRA_GPU_FLOWMAP_PARAMETER(29)
        LIBRA_GPU_FLOWMAP_PARAMETER(30)
        LIBRA_GPU_FLOWMAP_PARAMETER(31)
        LIBRA_GPU_FLOWMAP_PARAMETER(32)
        LIBRA_GPU_FLOWMAP_PARAMETER(33)
        LIBRA_GPU_FLOWMAP_PARAMETER(34)
        LIBRA_GPU_FLOWMAP_PARAMETER(35)
        LIBRA_GPU_FLOWMAP_PARAMETER(36)
        LIBRA_GPU_FLOWMAP_PARAMETER(37)
        LIBRA_GPU_FLOWMAP_PARAMETER(38)
        LIBRA_GPU_FLOWMAP_PARAMETER(39)
        LIBRA_GPU_FLOWMAP_PARAMETER(40)
        LIBRA_GPU_FLOWMAP_PARAMETER(41)
        LIBRA_GPU_FLOWMAP_PARAMETER(42)
        LIBRA_GPU_FLOWMAP_PARAMETER(43)
        LIBRA_GPU_FLOWMAP_PARAMETER(44)
        LIBRA_GPU_FLOWMAP_PARAMETER(45)
        LIBRA_GPU_FLOWMAP_PARAMETER(46)
        LIBRA_GPU_FLOWMAP_PARAMETER(47)
        LIBRA_GPU_FLOWMAP_PARAMETER(48)
        LIBRA_GPU_FLOWMAP_PARAMETER(49)
        LIBRA_GPU_FLOWMAP_PARAMETER(50)
        LIBRA_GPU_FLOWMAP_PARAMETER(51)
        LIBRA_GPU_FLOWMAP_PARAMETER(52)
        LIBRA_GPU_FLOWMAP_PARAMETER(53)
        LIBRA_GPU_FLOWMAP_PARAMETER(54)
        LIBRA_GPU_FLOWMAP_PARAMETER(55)
        LIBRA_GPU_FLOWMAP_PARAMETER(56)
        LIBRA_GPU_FLOWMAP_PARAMETER(57)
        LIBRA_GPU_FLOWMAP_PARAMETER(58)
        LIBRA_GPU_FLOWMAP_PARAMETER(59)
        LIBRA_GPU_FLOWMAP_PARAMETER(60)
        LIBRA_GPU_FLOWMAP_PARAMETER(61)
        LIBRA_GPU_FLOWMAP_PARAMETER(62)
        LIBRA_GPU_FLOWMAP_PARAMETER(63)
        SHADER_PARAMETER_SAMPLER(SamplerState, FlowMapSampler)
        SHADER_PARAMETER(uint32, Capacity)
        SHADER_PARAMETER(uint32, Count)
        SHADER_PARAMETER(uint32, SpawnStart)
        SHADER_PARAMETER(uint32, SpawnCount)
        SHADER_PARAMETER(float, DeltaTime)
        SHADER_PARAMETER(float, Time)
        SHADER_PARAMETER(uint32, EmitterSeed)
        SHADER_PARAMETER(uint32, FrameIndex)
        SHADER_PARAMETER(FVector3f, EmitterPosition)
        SHADER_PARAMETER(uint32, RegisterCount)
        SHADER_PARAMETER(uint32, InstructionCount)
        SHADER_PARAMETER(uint32, Stage)
        SHADER_PARAMETER(uint32, DefaultAge)
    END_SHADER_PARAMETER_STRUCT()

    static bool ShouldCompilePermutation(const FGlobalShaderPermutationParameters& parameters)
    {
        return IsFeatureLevelSupported(parameters.Platform, ERHIFeatureLevel::SM5);
    }
};

#undef LIBRA_GPU_FLOWMAP_PARAMETER

#define LIBRA_GPU_BIND_FLOWMAP_TEXTURE(Index) \
    case Index: parameters.FlowMapTexture##Index = texture; break

static void BindFlowMapTexture(FLibraGpuInterpreterCS::FParameters& parameters, int32 textureIndex, FRHITexture* texture)
{
    switch (textureIndex)
    {
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(0);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(1);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(2);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(3);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(4);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(5);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(6);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(7);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(8);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(9);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(10);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(11);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(12);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(13);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(14);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(15);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(16);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(17);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(18);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(19);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(20);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(21);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(22);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(23);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(24);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(25);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(26);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(27);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(28);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(29);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(30);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(31);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(32);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(33);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(34);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(35);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(36);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(37);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(38);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(39);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(40);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(41);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(42);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(43);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(44);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(45);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(46);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(47);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(48);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(49);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(50);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(51);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(52);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(53);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(54);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(55);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(56);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(57);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(58);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(59);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(60);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(61);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(62);
        LIBRA_GPU_BIND_FLOWMAP_TEXTURE(63);
        default:
        {
            checkNoEntry();
            break;
        }
    }
}

#undef LIBRA_GPU_BIND_FLOWMAP_TEXTURE

IMPLEMENT_GLOBAL_SHADER(FLibraGpuInterpreterCS, "/LibraGpu/Private/LibraGpuInterpreter.usf", "MainCS", SF_Compute);

class FLibraGpuBillboardVS : public FGlobalShader
{
    DECLARE_GLOBAL_SHADER(FLibraGpuBillboardVS);
    SHADER_USE_PARAMETER_STRUCT(FLibraGpuBillboardVS, FGlobalShader);
    BEGIN_SHADER_PARAMETER_STRUCT(FParameters, )
        SHADER_PARAMETER_RDG_BUFFER_SRV(StructuredBuffer<LibraParticle>, ParticleData)
        SHADER_PARAMETER(FMatrix44f, ViewProjection)
        SHADER_PARAMETER(FVector3f, PreViewTranslation)
        SHADER_PARAMETER(FVector3f, CameraRight)
        SHADER_PARAMETER(FVector3f, CameraUp)
        SHADER_PARAMETER(float, SizeScale)
        SHADER_PARAMETER(FVector4f, DrawRow0)
        SHADER_PARAMETER(FVector4f, DrawRow1)
        SHADER_PARAMETER(FVector4f, DrawRow2)
    END_SHADER_PARAMETER_STRUCT()
    static bool ShouldCompilePermutation(const FGlobalShaderPermutationParameters& parameters)
    {
        return IsFeatureLevelSupported(parameters.Platform, ERHIFeatureLevel::SM5);
    }
};

class FLibraGpuBillboardPS : public FGlobalShader
{
    DECLARE_GLOBAL_SHADER(FLibraGpuBillboardPS);
    SHADER_USE_PARAMETER_STRUCT(FLibraGpuBillboardPS, FGlobalShader);
    BEGIN_SHADER_PARAMETER_STRUCT(FParameters, )
        SHADER_PARAMETER_TEXTURE(Texture2D, BillboardTexture)
        SHADER_PARAMETER_SAMPLER(SamplerState, BillboardSampler)
        RENDER_TARGET_BINDING_SLOTS()
    END_SHADER_PARAMETER_STRUCT()
    static bool ShouldCompilePermutation(const FGlobalShaderPermutationParameters& parameters)
    {
        return IsFeatureLevelSupported(parameters.Platform, ERHIFeatureLevel::SM5);
    }
};

IMPLEMENT_GLOBAL_SHADER(FLibraGpuBillboardVS, "/LibraGpu/Private/LibraGpuBillboard.usf", "MainVS", SF_Vertex);
IMPLEMENT_GLOBAL_SHADER(FLibraGpuBillboardPS, "/LibraGpu/Private/LibraGpuBillboard.usf", "MainPS", SF_Pixel);

BEGIN_SHADER_PARAMETER_STRUCT(FLibraGpuBillboardPassParameters, )
    SHADER_PARAMETER_STRUCT_INCLUDE(FLibraGpuBillboardVS::FParameters, VS)
    SHADER_PARAMETER_STRUCT_INCLUDE(FLibraGpuBillboardPS::FParameters, PS)
END_SHADER_PARAMETER_STRUCT()

static void AddLibraGpuBillboardRasterPass(FRDGBuilder& graph,
    FLibraGpuBillboardPassParameters* parameters, FIntRect rect, int32 count,
    ERHIFeatureLevel::Type featureLevel, const TCHAR* profileName,
    ELibraGpuBillboardBlend blend = ELibraGpuBillboardBlend::Alpha,
    const FMatrix44f* drawTransform = nullptr)
{
    auto matrix = drawTransform ? *drawTransform : FMatrix44f::Identity;
    parameters->VS.DrawRow0 = FVector4f(matrix.M[0][0], matrix.M[0][1], matrix.M[0][2], matrix.M[0][3]);
    parameters->VS.DrawRow1 = FVector4f(matrix.M[1][0], matrix.M[1][1], matrix.M[1][2], matrix.M[1][3]);
    parameters->VS.DrawRow2 = FVector4f(matrix.M[2][0], matrix.M[2][1], matrix.M[2][2], matrix.M[2][3]);
    auto shaderMap = GetGlobalShaderMap(featureLevel);
    TShaderMapRef<FLibraGpuBillboardVS> vertexShader(shaderMap);
    TShaderMapRef<FLibraGpuBillboardPS> pixelShader(shaderMap);
    RDG_EVENT_SCOPE(graph, "LibraGpu.Billboards");
    graph.AddPass(RDG_EVENT_NAME("%s.Billboards", profileName), parameters, ERDGPassFlags::Raster,
        [parameters, vertexShader, pixelShader, rect, count, blend](FRHICommandList& commandList)
        {
            FGraphicsPipelineStateInitializer pipeline;
            commandList.ApplyCachedRenderTargets(pipeline);
            pipeline.BlendState = blend == ELibraGpuBillboardBlend::Additive ?
                TStaticBlendState<CW_RGBA, BO_Add, BF_SourceAlpha, BF_One, BO_Add, BF_One, BF_One>::GetRHI() :
                TStaticBlendState<CW_RGBA, BO_Add, BF_SourceAlpha, BF_InverseSourceAlpha,
                    BO_Add, BF_One, BF_InverseSourceAlpha>::GetRHI();
            pipeline.RasterizerState = TStaticRasterizerState<FM_Solid, CM_None>::GetRHI();
            pipeline.DepthStencilState = TStaticDepthStencilState<false, CF_Always>::GetRHI();
            pipeline.BoundShaderState.VertexDeclarationRHI = GEmptyVertexDeclaration.VertexDeclarationRHI;
            pipeline.BoundShaderState.VertexShaderRHI = vertexShader.GetVertexShader();
            pipeline.BoundShaderState.PixelShaderRHI = pixelShader.GetPixelShader();
            pipeline.PrimitiveType = PT_TriangleList;
            SetGraphicsPipelineState(commandList, pipeline, 0);
            commandList.SetViewport(rect.Min.X, rect.Min.Y, 0, rect.Max.X, rect.Max.Y, 1);
            SetShaderParameters(commandList, vertexShader, vertexShader.GetVertexShader(), parameters->VS);
            SetShaderParameters(commandList, pixelShader, pixelShader.GetPixelShader(), parameters->PS);
            commandList.SetStreamSource(0, nullptr, 0);
            commandList.DrawPrimitive(0, 2, count);
        });
}

namespace
{
uint32 ComponentPacketWord(const TArray<uint8>& bytes, int32 offset)
{
    return uint32(bytes[offset]) | uint32(bytes[offset + 1]) << 8 |
        uint32(bytes[offset + 2]) << 16 | uint32(bytes[offset + 3]) << 24;
}

struct FLibraGpuFrame
{
    uint32 SpawnStart = 0;
    uint32 SpawnCount = 0;
    uint32 Seed = 0;
    uint32 Frame = 0;
    float Delta = 0;
    float Time = 0;
    FVector3f Position = FVector3f::ZeroVector;
};

static bool IsFiniteAffineDrawTransform(const FMatrix44f& matrix)
{
    for (auto row = 0; row < 4; ++row)
        for (auto column = 0; column < 4; ++column)
            if (!FMath::IsFinite(matrix.M[row][column])) return false;
    return FMath::Abs(matrix.M[3][0]) <= 1e-5f && FMath::Abs(matrix.M[3][1]) <= 1e-5f &&
        FMath::Abs(matrix.M[3][2]) <= 1e-5f && FMath::Abs(matrix.M[3][3] - 1) <= 1e-5f;
}

static FMatrix44f RelativeToEmitter(const FMatrix44f& authored, const FVector3f& emitterPosition)
{
    auto result = authored;
    for (auto row = 0; row < 3; ++row)
        result.M[row][3] -= result.M[row][0] * emitterPosition.X +
            result.M[row][1] * emitterPosition.Y + result.M[row][2] * emitterPosition.Z;
    return result;
}

static bool TransformGpuBounds(const FBox& source, const FMatrix44f& matrix, FBox& result)
{
    if (!source.IsValid || !IsFiniteAffineDrawTransform(matrix)) return false;
    result = FBox(ForceInit);
    for (auto corner = 0; corner < 8; ++corner)
    {
        auto point = FVector3f(
            (corner & 1) == 0 ? source.Min.X : source.Max.X,
            (corner & 2) == 0 ? source.Min.Y : source.Max.Y,
            (corner & 4) == 0 ? source.Min.Z : source.Max.Z);
        auto transformed = FVector3f(
            matrix.M[0][0] * point.X + matrix.M[0][1] * point.Y + matrix.M[0][2] * point.Z + matrix.M[0][3],
            matrix.M[1][0] * point.X + matrix.M[1][1] * point.Y + matrix.M[1][2] * point.Z + matrix.M[1][3],
            matrix.M[2][0] * point.X + matrix.M[2][1] * point.Y + matrix.M[2][2] * point.Z + matrix.M[2][3]);
        if (!FMath::IsFinite(transformed.X) || !FMath::IsFinite(transformed.Y) ||
            !FMath::IsFinite(transformed.Z)) return false;
        result += FVector(transformed);
    }
    return result.IsValid != 0;
}

struct FLibraGpuRenderTexture
{
    bool bBuiltinWhite = false;
    FTextureReferenceRHIRef Reference;

    FRHITexture* GetTexture() const
    {
        if (bBuiltinWhite) return GWhiteTexture ? GWhiteTexture->TextureRHI.GetReference() : nullptr;
        return Reference.GetReference();
    }
};
}

struct FLibraGpuState
{
    int32 Capacity = 0;
    FString ProfileName;
    uint32 MaximumRegisters = 0;
    TArray<FLibraGpuKernelPacket> Kernels;
    TArray<FLibraGpuFlowStep> Flow;
    TArray<FLibraGpuRenderTexture> Textures;
    FMatrix44f DrawTransform = FMatrix44f::Identity;
    TRefCountPtr<FRDGPooledBuffer> Particles;
    TRefCountPtr<FRDGPooledBuffer> Registers;
    TArray<TRefCountPtr<FRDGPooledBuffer>> Programs;
#if WITH_DEV_AUTOMATION_TESTS
    TArray<uint8> TestInitialParticles;
#endif

    void Dispatch(FRHICommandListImmediate& commandList, const FLibraGpuFrame& frame)
    {
        FRDGBuilder graph(commandList);
        FRDGBufferRef particles;
        FRDGBufferRef registers;
        if (!Particles)
        {
#if WITH_DEV_AUTOMATION_TESTS
            if (TestInitialParticles.Num() == Capacity * 80)
                particles = CreateStructuredBuffer(graph, TEXT("LibraGpu.TestParticles"), 80, Capacity,
                    TestInitialParticles.GetData(), TestInitialParticles.Num());
            else
#endif
            {
                particles = graph.CreateBuffer(FRDGBufferDesc::CreateStructuredDesc(80, Capacity), TEXT("LibraGpu.Particles"));
                AddClearUAVPass(graph, graph.CreateUAV(particles), 0);
            }
            registers = graph.CreateBuffer(FRDGBufferDesc::CreateStructuredDesc(16, FMath::Max(1u, uint32(Capacity) * MaximumRegisters)), TEXT("LibraGpu.Registers"));
            Particles = graph.ConvertToExternalBuffer(particles);
            Registers = graph.ConvertToExternalBuffer(registers);
            Programs.Reserve(Kernels.Num());
            for (const auto& kernel : Kernels)
            {
                auto program = CreateStructuredBuffer(graph, TEXT("LibraGpu.Program"), 16,
                    kernel.Bytes.Num() / 16, kernel.Bytes.GetData(), kernel.Bytes.Num());
                Programs.Add(graph.ConvertToExternalBuffer(program));
            }
        }
        else
        {
            particles = graph.RegisterExternalBuffer(Particles);
            registers = graph.RegisterExternalBuffer(Registers);
        }
        TShaderMapRef<FLibraGpuInterpreterCS> shader(GetGlobalShaderMap(GMaxRHIFeatureLevel));
        for (const auto& step : Flow)
        {
            if (step.Kind != TEXT("Spawn") && step.Kind != TEXT("Update")) continue;
            const auto& kernel = Kernels[step.Index];
            auto program = graph.RegisterExternalBuffer(Programs[step.Index]);
            auto dispatch = [&](uint32 start, uint32 count)
            {
                RDG_EVENT_SCOPE(graph, "LibraGpu.Simulate");
                auto parameters = graph.AllocParameters<FLibraGpuInterpreterCS::FParameters>();
                parameters->Particles = graph.CreateUAV(particles);
                parameters->Registers = graph.CreateUAV(registers);
                parameters->Instructions = graph.CreateUAV(program);
                for (auto textureIndex = 0; textureIndex < LibraGpuFlowMapTextureCount; ++textureIndex)
                {
                    auto texture = textureIndex < Textures.Num() ? Textures[textureIndex].GetTexture() : nullptr;
                    if (!texture)
                    {
                        texture = GWhiteTexture ? GWhiteTexture->TextureRHI.GetReference() : nullptr;
                    }
                    BindFlowMapTexture(*parameters, textureIndex, texture);
                }
                parameters->FlowMapSampler = TStaticSamplerState<SF_Bilinear, AM_Clamp, AM_Clamp, AM_Clamp>::GetRHI();
                parameters->Capacity = Capacity;
                parameters->Count = Capacity;
                parameters->SpawnStart = start;
                parameters->SpawnCount = count;
                parameters->DeltaTime = frame.Delta;
                parameters->Time = frame.Time;
                parameters->EmitterSeed = frame.Seed;
                parameters->FrameIndex = frame.Frame;
                parameters->EmitterPosition = frame.Position;
                parameters->RegisterCount = ComponentPacketWord(kernel.Bytes, 16);
                parameters->InstructionCount = ComponentPacketWord(kernel.Bytes, 12);
                parameters->Stage = kernel.bUpdate ? 1 : 0;
                parameters->DefaultAge = ComponentPacketWord(kernel.Bytes, 48);
                FComputeShaderUtils::AddPass(graph, RDG_EVENT_NAME("%s.Interpret.%s", *ProfileName, *kernel.Id),
                    shader, parameters, FIntVector(FMath::DivideAndRoundUp(Capacity, 64), 1, 1));
            };
            if (step.Kind == TEXT("Spawn"))
            {
                auto first = FMath::Min(frame.SpawnCount, uint32(Capacity) - frame.SpawnStart);
                if (first) dispatch(frame.SpawnStart, first);
                if (frame.SpawnCount > first) dispatch(0, frame.SpawnCount - first);
            }
            else dispatch(0, 0);
        }
        graph.Execute();
    }
};

class FLibraGpuViewExtension final : public FSceneViewExtensionBase
{
public:
    FLibraGpuViewExtension(const FAutoRegister& autoRegister,
        TSharedRef<FLibraGpuState, ESPMode::ThreadSafe> state, FSceneInterface* scene)
        : FSceneViewExtensionBase(autoRegister), State(MoveTemp(state)), Scene(scene) {}

    void SubscribeToPostProcessingPass(EPostProcessingPass pass, const FSceneView& view,
        FPostProcessingPassDelegateArray& callbacks, bool enabled) override
    {
        if (pass == EPostProcessingPass::Tonemap && view.Family && view.Family->Scene == Scene)
            callbacks.Add(FPostProcessingPassDelegate::CreateRaw(this, &FLibraGpuViewExtension::Render));
    }

    FScreenPassTexture Render(FRDGBuilder& graph, const FSceneView& view, const FPostProcessMaterialInputs& inputs)
    {
        auto sceneColor = FScreenPassTexture::CopyFromSlice(graph, inputs.GetInput(EPostProcessMaterialInput::SceneColor));
        auto output = inputs.OverrideOutput;
        if (!output.IsValid())
            output = FScreenPassRenderTarget::CreateFromInput(graph, sceneColor, ERenderTargetLoadAction::ENoAction, TEXT("LibraGpu.Billboards"));
        if (sceneColor.Texture != output.Texture)
            AddDrawTexturePass(graph, FScreenPassViewInfo(view), sceneColor, output);
        if (!State->Particles) return output;
        auto particles = graph.RegisterExternalBuffer(State->Particles);
        for (const auto& step : State->Flow)
        {
            if (step.Kind != TEXT("RenderBillboard") || !State->Textures.IsValidIndex(step.Index) ||
                !State->Textures[step.Index].GetTexture()) continue;
            auto parameters = graph.AllocParameters<FLibraGpuBillboardPassParameters>();
            parameters->VS.ParticleData = graph.CreateSRV(particles);
            parameters->VS.ViewProjection = FMatrix44f(view.ViewMatrices.GetTranslatedViewProjectionMatrix());
            parameters->VS.PreViewTranslation = FVector3f(view.ViewMatrices.GetPreViewTranslation());
            parameters->VS.CameraRight = FVector3f(view.ViewMatrices.GetInvViewMatrix().GetUnitAxis(EAxis::X));
            parameters->VS.CameraUp = FVector3f(view.ViewMatrices.GetInvViewMatrix().GetUnitAxis(EAxis::Y));
            parameters->VS.SizeScale = step.SizeScale;
            parameters->PS.BillboardTexture = State->Textures[step.Index].GetTexture();
            parameters->PS.BillboardSampler = TStaticSamplerState<SF_Bilinear, AM_Clamp, AM_Clamp, AM_Clamp>::GetRHI();
            parameters->PS.RenderTargets[0] = FRenderTargetBinding(output.Texture, ERenderTargetLoadAction::ELoad);
            AddLibraGpuBillboardRasterPass(graph, parameters, output.ViewRect, State->Capacity,
                view.GetFeatureLevel(), *State->ProfileName, step.BillboardBlend, &State->DrawTransform);
        }
        return output;
    }

private:
    TSharedRef<FLibraGpuState, ESPMode::ThreadSafe> State;
    FSceneInterface* Scene;
};

ULibraGpuEffectRuntime::ULibraGpuEffectRuntime() = default;

ULibraGpuEffectRuntime::~ULibraGpuEffectRuntime() = default;

bool ULibraGpuEffectRuntime::SetEffect(ULibraGpuEffectAsset* newEffect)
{
    FString error;
    if (newEffect && !newEffect->ValidateProgram(error))
    {
        LastError = MoveTemp(error);
        return false;
    }
    Stop();
    Effect = newEffect;
    LastError.Reset();
    return true;
}

bool ULibraGpuEffectRuntime::ConfigureEmitter(float emitterSpawnRate, int32 seed)
{
    if (!FMath::IsFinite(emitterSpawnRate) || emitterSpawnRate < 0)
    {
        LastError = TEXT("GPU spawn rate must be finite and nonnegative.");
        return false;
    }
    SpawnPerSecond = emitterSpawnRate;
    EmitterSeed = seed;
    LastError.Reset();
    return true;
}

bool ULibraGpuEffectRuntime::SetDrawTransform(const FMatrix44f& drawTransform, bool connected)
{
    check(IsInGameThread());
    TRACE_CPUPROFILER_EVENT_SCOPE(LibraGpu_SetDrawTransform);
    if (connected && !IsFiniteAffineDrawTransform(drawTransform))
    {
        LastError = TEXT("GPU draw Matrix must be finite and affine so its bounds remain conservative.");
        return false;
    }
    auto nextAuthoredTransform = connected ? drawTransform : FMatrix44f::Identity;
    auto nextDrawTransform = nextAuthoredTransform;
    if (connected)
    {
        auto owner = Owner.Get();
        if (!owner || owner->GetActorLocation().ContainsNaN())
        {
            LastError = TEXT("GPU draw Matrix requires a finite emitter anchor.");
            return false;
        }
        auto anchor = FVector3f(owner->GetActorLocation());
        if (!FMath::IsFinite(anchor.X) || !FMath::IsFinite(anchor.Y) || !FMath::IsFinite(anchor.Z))
        {
            LastError = TEXT("GPU draw Matrix emitter anchor exceeds finite float range.");
            return false;
        }
        nextDrawTransform = RelativeToEmitter(nextAuthoredTransform, anchor);
        if (!IsFiniteAffineDrawTransform(nextDrawTransform))
        {
            LastError = TEXT("GPU draw Matrix relative to its emitter anchor exceeds finite float range.");
            return false;
        }
    }
    AuthoredDrawTransform = nextAuthoredTransform;
    DrawTransform = nextDrawTransform;
    bHasDrawTransform = connected;
    if (State)
    {
        auto state = State;
        auto nextTransform = DrawTransform;
        ENQUEUE_RENDER_COMMAND(LibraGpuSetDrawTransform)([state, nextTransform](FRHICommandListImmediate&)
        {
            TRACE_CPUPROFILER_EVENT_SCOPE(LibraGpu_ApplyDrawTransform);
            state->DrawTransform = nextTransform;
        });
    }
    LastError.Reset();
    return true;
}

void ULibraGpuEffectRuntime::LoadSoftEffect()
{
    ++LoadGeneration;
    if (LoadHandle) LoadHandle->CancelHandle();
    LoadHandle.Reset();
    if (SoftEffect.IsNull()) return;
    auto generation = LoadGeneration;
    auto weak = TWeakObjectPtr<ULibraGpuEffectRuntime>(this);
    auto path = SoftEffect.ToSoftObjectPath();
    LoadHandle = UAssetManager::GetStreamableManager().RequestAsyncLoad(path,
        FStreamableDelegate::CreateLambda([weak, generation, path]()
        {
            auto runtime = weak.Get();
            if (!runtime || runtime->LoadGeneration != generation || runtime->SoftEffect.ToSoftObjectPath() != path) return;
            auto loaded = runtime->SoftEffect.Get();
            runtime->LoadHandle.Reset();
            if (!loaded) runtime->LastError = TEXT("GPU effect failed to load.");
            else if (runtime->SetEffect(loaded) && runtime->bAutoPlay) runtime->Play();
        }));
}

bool ULibraGpuEffectRuntime::ResetEffect()
{
    auto owner = Owner.Get();
    if (!owner || !owner->GetWorld())
    {
        LastError = TEXT("GPU playback requires a live owner actor and world.");
        return false;
    }
    FString error;
    if (!Effect || !Effect->ValidateProgram(error))
    {
        LastError = Effect ? MoveTemp(error) : TEXT("No GPU effect is assigned.");
        return false;
    }
    if (GMaxRHIFeatureLevel < ERHIFeatureLevel::SM5)
    {
        LastError = TEXT("GPU register interpreter requires Shader Model 5.");
        return false;
    }
    auto state = MakeShared<FLibraGpuState, ESPMode::ThreadSafe>();
    state->Capacity = Effect->GetCapacity();
    state->ProfileName = GetPathName();
    state->Kernels = Effect->GetKernels();
    state->Flow = Effect->GetFlow();
    state->DrawTransform = DrawTransform;
    for (const auto& texture : Effect->GetTextures())
    {
        FLibraGpuRenderTexture binding;
        binding.bBuiltinWhite = texture.bBuiltinWhite;
        if (texture.Texture) binding.Reference = texture.Texture->TextureReference.TextureReferenceRHI;
        if (texture.bFlowMap && !binding.GetTexture())
        {
            LastError = TEXT("FlowMap texture has no initialized GPU resource.");
            return false;
        }
        state->Textures.Add(MoveTemp(binding));
    }
    for (const auto& kernel : state->Kernels) state->MaximumRegisters = FMath::Max(state->MaximumRegisters, ComponentPacketWord(kernel.Bytes, 16));
    State = MoveTemp(state);
    ViewExtension.Reset();
    if (owner->GetWorld()->Scene)
        ViewExtension = FSceneViewExtensions::NewExtension<FLibraGpuViewExtension>(State.ToSharedRef(), owner->GetWorld()->Scene);
    SpawnCursor = FrameIndex = 0;
    Elapsed = SpawnRemainder = 0;
    LastError.Reset();
    return true;
}

bool ULibraGpuEffectRuntime::Play()
{
    if (!State && !ResetEffect()) return false;
    bIsPlaying = true;
    return true;
}

void ULibraGpuEffectRuntime::Pause() { bIsPlaying = false; }

bool ULibraGpuEffectRuntime::GetPreviewBounds(FBox& outBounds) const
{
    if (!Effect || !LocalPreviewBounds.IsValid ||
        LocalPreviewBounds.Min.ContainsNaN() || LocalPreviewBounds.Max.ContainsNaN() ||
        !Owner.IsValid() || (!bHasDrawTransform && Owner->GetActorLocation().ContainsNaN()) ||
        LocalPreviewBounds.Min.X > LocalPreviewBounds.Max.X ||
        LocalPreviewBounds.Min.Y > LocalPreviewBounds.Max.Y ||
        LocalPreviewBounds.Min.Z > LocalPreviewBounds.Max.Z) return false;
    if (bHasDrawTransform)
    {
        if (!TransformGpuBounds(LocalPreviewBounds, AuthoredDrawTransform, outBounds)) return false;
    }
    else outBounds = LocalPreviewBounds.ShiftBy(Owner->GetActorLocation());
    return !outBounds.Min.ContainsNaN() && !outBounds.Max.ContainsNaN();
}

void ULibraGpuEffectRuntime::Stop()
{
    bIsPlaying = false;
    ++LoadGeneration;
    if (LoadHandle) LoadHandle->CancelHandle();
    LoadHandle.Reset();
    ViewExtension.Reset();
    State.Reset();
    SpawnCursor = FrameIndex = 0;
    Elapsed = SpawnRemainder = 0;
    DrawTransform = FMatrix44f::Identity;
    AuthoredDrawTransform = FMatrix44f::Identity;
    bHasDrawTransform = false;
}

void ULibraGpuEffectRuntime::Initialize(AActor* owner)
{
    Stop();
    Owner = owner;
    if (!owner)
    {
        LastError = TEXT("GPU runtime owner is unavailable.");
    }
}

void ULibraGpuEffectRuntime::Tick(float deltaTime)
{
    TRACE_CPUPROFILER_EVENT_SCOPE(LibraGpu_SubmitFrame);
    if (!bIsPlaying || !State || !FMath::IsFinite(deltaTime) || deltaTime < 0 ||
        !FMath::IsFinite(SpawnPerSecond) || SpawnPerSecond < 0)
    {
        return;
    }
    auto owner = Owner.Get();
    if (!owner || !owner->GetWorld())
    {
        return;
    }
    auto position = FVector3f(owner->GetActorLocation());
    if (!FMath::IsFinite(position.X) || !FMath::IsFinite(position.Y) || !FMath::IsFinite(position.Z))
    {
        return;
    }
    Elapsed += deltaTime;
    SpawnRemainder = FMath::Min(SpawnRemainder + double(SpawnPerSecond) * deltaTime, double(State->Capacity));
    auto count = uint32(SpawnRemainder);
    SpawnRemainder -= count;
    FLibraGpuFrame frame;
    frame.SpawnStart = SpawnCursor;
    frame.SpawnCount = count;
    frame.Seed = uint32(EmitterSeed);
    frame.Frame = FrameIndex++;
    frame.Delta = deltaTime;
    frame.Time = float(FMath::Min(Elapsed, double(MAX_flt)));
    frame.Position = position;
    SpawnCursor = (SpawnCursor + count) % uint32(State->Capacity);
    auto state = State;
    ENQUEUE_RENDER_COMMAND(LibraGpuTick)([state, frame](FRHICommandListImmediate& commandList)
    {
        state->Dispatch(commandList, frame);
    });
}

void ULibraGpuEffectRuntime::BeginDestroy()
{
    ++LoadGeneration;
    if (LoadHandle) LoadHandle->CancelHandle();
    LoadHandle.Reset();
    Stop();
    Super::BeginDestroy();
}

#if !UE_BUILD_SHIPPING
bool ULibraGpuEffectRuntime::VerifyGpuSmoke(FString& report)
{
    if (!State || GUsingNullRHI || GMaxRHIFeatureLevel < ERHIFeatureLevel::SM5)
    {
        report = TEXT("No playing SM5 GPU instance.");
        return false;
    }
    auto state = State;
    auto sampleCount = FMath::Min(state->Capacity, 32);
    TArray<uint8> bytes;
    bytes.SetNumZeroed(sampleCount * 80);
    auto copied = false;
    ENQUEUE_RENDER_COMMAND(LibraGpuDevelopmentReadback)([state, &bytes, &copied](FRHICommandListImmediate& commandList)
    {
        if (!state->Particles) return;
        FRHIGPUBufferReadback readback(TEXT("LibraGpu.DevelopmentSmoke"));
        FRDGBuilder graph(commandList);
        AddEnqueueCopyPass(graph, &readback, graph.RegisterExternalBuffer(state->Particles), bytes.Num());
        graph.Execute();
        readback.Wait(commandList, readback.GetLastCopyGPUMask());
        auto data = readback.Lock(bytes.Num());
        if (data)
        {
            FMemory::Memcpy(bytes.GetData(), data, bytes.Num());
            copied = true;
        }
        readback.Unlock();
    });
    FlushRenderingCommands();
    if (!copied)
    {
        report = TEXT("GPU readback was not available.");
        return false;
    }
    auto alive = 0;
    for (auto i = 0; i < sampleCount; ++i)
    {
        uint32 live = 0;
        FMemory::Memcpy(&live, bytes.GetData() + i * 80 + 68, sizeof(live));
        if (live == 0) continue;
        ++alive;
        for (auto offset = 0; offset < 64; offset += 4)
        {
            uint32 bits = 0;
            FMemory::Memcpy(&bits, bytes.GetData() + i * 80 + offset, sizeof(bits));
            if ((bits & 0x7f800000u) == 0x7f800000u)
            {
                report = TEXT("GPU readback contained a nonfinite live particle.");
                return false;
            }
        }
    }
    report = FString::Printf(TEXT("GpuRegisterInterpreter capacity=%d sampled=%d alive=%d nativeTextures=%d"),
        state->Capacity, sampleCount, alive, state->Textures.Num());
    return alive > 0;
}
#endif

#if WITH_DEV_AUTOMATION_TESTS
#include "Dom/JsonObject.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/Base64.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace
{
void TestWord(TArray<uint8>& bytes, int32 offset, uint32 value)
{
    for (auto i = 0; i < 4; ++i) bytes[offset + i] = uint8(value >> (i * 8));
}

FLibraGpuKernelPacket TestPacket(bool update, float movement)
{
    FLibraGpuKernelPacket packet;
    packet.bUpdate = update;
    packet.Bytes.SetNumZeroed(update ? 320 : 64);
    FMemory::Memcpy(packet.Bytes.GetData(), "LGPK", 4);
    TestWord(packet.Bytes, 4, 1);
    TestWord(packet.Bytes, 8, 64);
    TestWord(packet.Bytes, 12, update ? 4 : 0);
    TestWord(packet.Bytes, 16, update ? 3 : 0);
    TestWord(packet.Bytes, 20, update ? 1 : 0);
    TestWord(packet.Bytes, 24, 80);
    TestWord(packet.Bytes, 28, 48);
    TestWord(packet.Bytes, 48, update ? 1 : 0);
    if (!update) return packet;
    for (auto n = 0; n < 4; ++n)
    {
        auto base = 64 + n * 64;
        TestWord(packet.Bytes, base + 8, MAX_uint32);
        TestWord(packet.Bytes, base + 12, MAX_uint32);
        TestWord(packet.Bytes, base + 16, MAX_uint32);
        TestWord(packet.Bytes, base + 28, MAX_uint32);
        TestWord(packet.Bytes, base + 20, 5);
    }
    TestWord(packet.Bytes, 64, 1);
    TestWord(packet.Bytes, 128, 0);
    TestWord(packet.Bytes, 132, 1);
    uint32 bits;
    FMemory::Memcpy(&bits, &movement, sizeof(bits));
    TestWord(packet.Bytes, 160, bits);
    TestWord(packet.Bytes, 192, 3);
    TestWord(packet.Bytes, 196, 2);
    TestWord(packet.Bytes, 200, 0);
    TestWord(packet.Bytes, 204, 1);
    TestWord(packet.Bytes, 256, 18);
    TestWord(packet.Bytes, 260, MAX_uint32);
    TestWord(packet.Bytes, 264, 2);
    return packet;
}

FLibraGpuKernelPacket FlowMapTestPacket()
{
    FLibraGpuKernelPacket packet;
    packet.Id = TEXT("wind");
    packet.bUpdate = true;
    packet.Bytes.SetNumZeroed(256);
    FMemory::Memcpy(packet.Bytes.GetData(), "LGPK", 4);
    TestWord(packet.Bytes, 4, 1);
    TestWord(packet.Bytes, 8, 64);
    TestWord(packet.Bytes, 12, 3);
    TestWord(packet.Bytes, 16, 2);
    TestWord(packet.Bytes, 20, 1);
    TestWord(packet.Bytes, 24, 80);
    TestWord(packet.Bytes, 28, 48);
    TestWord(packet.Bytes, 48, 1);

    auto missing = MAX_uint32;
    TestWord(packet.Bytes, 64, 1);
    TestWord(packet.Bytes, 68, 0);
    TestWord(packet.Bytes, 72, missing);
    TestWord(packet.Bytes, 76, missing);
    TestWord(packet.Bytes, 80, missing);
    TestWord(packet.Bytes, 84, 5);
    TestWord(packet.Bytes, 88, 0);
    TestWord(packet.Bytes, 92, missing);

    TestWord(packet.Bytes, 128, 41);
    TestWord(packet.Bytes, 132, 1);
    TestWord(packet.Bytes, 136, 0);
    TestWord(packet.Bytes, 140, missing);
    TestWord(packet.Bytes, 144, missing);
    TestWord(packet.Bytes, 148, 5);
    TestWord(packet.Bytes, 152, 0);
    TestWord(packet.Bytes, 156, 1);
    for (auto index = 0; index < 4; ++index)
    {
        float value = index < 2 ? -1.0f : 2.0f;
        uint32 bits = 0;
        FMemory::Memcpy(&bits, &value, sizeof(bits));
        TestWord(packet.Bytes, 160 + index * 4, bits);
    }

    TestWord(packet.Bytes, 192, 18);
    TestWord(packet.Bytes, 196, missing);
    TestWord(packet.Bytes, 200, 1);
    TestWord(packet.Bytes, 204, missing);
    TestWord(packet.Bytes, 208, missing);
    TestWord(packet.Bytes, 212, 5);
    TestWord(packet.Bytes, 216, 1);
    TestWord(packet.Bytes, 220, missing);
    return packet;
}

FString FlowMapTestBundle(const FString& texturePath)
{
    auto packet = FlowMapTestPacket();
    return FString::Printf(
        TEXT("{\"version\":1,\"purpose\":\"SourceExport\",\"runtimeExecuted\":false,")
        TEXT("\"canonicalJson\":\"{}\",\"usf\":\"source only\",\"metadata\":{\"version\":1,")
        TEXT("\"capacity\":1,\"particleStride\":80,\"uniformBufferBytes\":48,")
        TEXT("\"contentHash\":\"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a\",")
        TEXT("\"sourceHash\":\"cc8bac8c92ab27cddfc0cbf92f17d23b43f2cae747744de80f25fe546a24ff5e\",")
        TEXT("\"purpose\":\"SourceExport\",\"runtimeExecuted\":false,\"format\":\"Usf\",")
        TEXT("\"resources\":[{\"id\":\"wind\",\"kind\":\"FlowMap2D\",\"register\":0,")
        TEXT("\"flowMap\":{\"axisU\":\"Y\",\"axisV\":\"X\",\"worldMinimumU\":-1,\"worldMaximumU\":1,")
        TEXT("\"worldMinimumV\":-1,\"worldMaximumV\":1}}],")
        TEXT("\"flow\":[{\"kind\":\"Update\",\"kernelId\":\"wind\"}]},")
        TEXT("\"kernels\":[{\"id\":\"wind\",\"stage\":\"Update\",\"packet\":\"%s\"}],")
        TEXT("\"textures\":[{\"id\":\"wind\",\"objectPath\":\"%s\"}]}"),
        *FBase64::Encode(packet.Bytes), *texturePath);
}

struct FTestParticle
{
    FVector4f PositionAge = FVector4f(0, 0, 0, 0);
    FVector4f VelocityLifetime = FVector4f(0, 0, 0, 0);
    FVector4f Color = FVector4f(0, 0, 0, 0);
    FVector4f SizeRotation = FVector4f(0, 0, 0, 0);
    FUintVector4 SeedAlive = FUintVector4(0, 0, 0, 0);
};
static_assert(sizeof(FTestParticle) == 80);

TSharedRef<FLibraGpuState, ESPMode::ThreadSafe> TestState(float movement)
{
    auto state = MakeShared<FLibraGpuState, ESPMode::ThreadSafe>();
    state->Capacity = 1;
    state->MaximumRegisters = 3;
    state->Kernels.Add(TestPacket(false, 0));
    state->Kernels.Add(TestPacket(true, movement));
    FLibraGpuFlowStep spawn;
    spawn.Kind = TEXT("Spawn");
    spawn.Index = 0;
    FLibraGpuFlowStep update;
    update.Kind = TEXT("Update");
    update.Index = 1;
    state->Flow = {spawn, update};
    return state;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGpuInterpreterShaderCompileTest,
    "Libra.GPU.Compute.InterpreterShaderCompilation",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGpuInterpreterShaderCompileTest::RunTest(const FString& parameters)
{
    TShaderMapRef<FLibraGpuInterpreterCS> shader(GetGlobalShaderMap(GMaxRHIFeatureLevel));
    TestTrue(TEXT("GPU interpreter shader is compiled for the current feature level"), shader.IsValid());
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGpuInterpreterReadbackTest,
    "Libra.GPU.Compute.SpawnUpdateReadback",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGpuInterpreterReadbackTest::RunTest(const FString& parameters)
{
    if (GUsingNullRHI || GMaxRHIFeatureLevel < ERHIFeatureLevel::SM5)
    {
        AddError(TEXT("A real SM5 GPU is required for this test."));
        return false;
    }
    auto first = TestState(1);
    auto edited = TestState(3);
    FTestParticle result[3]{};
    ENQUEUE_RENDER_COMMAND(LibraGpuReadbackTest)([first, edited, &result](FRHICommandListImmediate& commandList)
    {
        auto read = [&commandList](const TSharedRef<FLibraGpuState, ESPMode::ThreadSafe>& state, FTestParticle& output)
        {
            FRHIGPUBufferReadback readback(TEXT("LibraGpu.TestReadback"));
            FRDGBuilder graph(commandList);
            AddEnqueueCopyPass(graph, &readback, graph.RegisterExternalBuffer(state->Particles), sizeof(FTestParticle));
            graph.Execute();
            readback.Wait(commandList, readback.GetLastCopyGPUMask());
            FMemory::Memcpy(&output, readback.Lock(sizeof(FTestParticle)), sizeof(FTestParticle));
            readback.Unlock();
        };
        FLibraGpuFrame frame;
        frame.SpawnCount = 1;
        frame.Delta = 0.5f;
        first->Dispatch(commandList, frame);
        read(first, result[0]);
        edited->Dispatch(commandList, frame);
        read(edited, result[1]);
        first->Dispatch(commandList, frame);
        read(first, result[2]);
    });
    FlushRenderingCommands();
    TestTrue(TEXT("Spawn and update produced a live particle"), result[0].SeedAlive.Y == 1);
    TestTrue(TEXT("GPU position changed"), FMath::IsNearlyEqual(result[0].PositionAge.X, 1.0f));
    TestTrue(TEXT("GPU age advanced"), FMath::IsNearlyEqual(result[0].PositionAge.W, 0.5f));
    TestTrue(TEXT("GPU color initialized"), FMath::IsNearlyEqual(result[0].Color.X, 1.0f));
    TestTrue(TEXT("Different IR produced different GPU position"), FMath::IsNearlyEqual(result[1].PositionAge.X, 3.0f));
    TestTrue(TEXT("Lifetime killed the particle"), result[2].SeedAlive.Y == 0);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGpuBillboardReadbackTest,
    "Libra.GPU.Render.BillboardFramebuffer",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGpuBillboardReadbackTest::RunTest(const FString& parameters)
{
    if (GUsingNullRHI || GMaxRHIFeatureLevel < ERHIFeatureLevel::SM5)
    {
        AddError(TEXT("A real SM5 GPU is required for framebuffer proof."));
        return false;
    }
    auto state = TestState(0);
    int32 litPixels[3]{}, leftPixels = 0, rightPixels = 0;
    auto translated = FMatrix44f::Identity;
    translated.M[0][3] = 0.75f;
    ENQUEUE_RENDER_COMMAND(LibraGpuFramebufferTest)([state, &litPixels, &leftPixels, &rightPixels, translated](FRHICommandListImmediate& commandList)
    {
        auto render = [&commandList](const TSharedRef<FLibraGpuState, ESPMode::ThreadSafe>& renderState,
            int32& lit, int32* left, int32* right, const FMatrix44f& drawTransform)
        {
            FRHIGPUTextureReadback readback(TEXT("LibraGpu.FramebufferProof"));
            FRDGBuilder graph(commandList);
            auto target = graph.CreateTexture(FRDGTextureDesc::Create2D(FIntPoint(32, 32),
                PF_R8G8B8A8, FClearValueBinding::Black,
                TexCreate_RenderTargetable | TexCreate_ShaderResource), TEXT("LibraGpu.TestFramebuffer"));
            AddClearRenderTargetPass(graph, target);
            auto draw = graph.AllocParameters<FLibraGpuBillboardPassParameters>();
            draw->VS.ParticleData = graph.CreateSRV(graph.RegisterExternalBuffer(renderState->Particles));
            draw->VS.ViewProjection = FMatrix44f::Identity;
            draw->VS.PreViewTranslation = FVector3f::ZeroVector;
            draw->VS.CameraRight = FVector3f(1, 0, 0);
            draw->VS.CameraUp = FVector3f(0, 1, 0);
            draw->VS.SizeScale = 1;
            draw->PS.BillboardTexture = GWhiteTexture->TextureRHI;
            draw->PS.BillboardSampler = TStaticSamplerState<SF_Bilinear>::GetRHI();
            draw->PS.RenderTargets[0] = FRenderTargetBinding(target, ERenderTargetLoadAction::ELoad);
            AddLibraGpuBillboardRasterPass(graph, draw, FIntRect(0, 0, 32, 32), 1,
                GMaxRHIFeatureLevel, TEXT("LibraGpu.TestBillboards"),
                ELibraGpuBillboardBlend::Alpha, &drawTransform);
            AddEnqueueCopyPass(graph, &readback, target);
            graph.Execute();
            readback.Wait(commandList, readback.GetLastCopyGPUMask());
            int32 pitch = 0;
            auto pixels = static_cast<const uint8*>(readback.Lock(pitch));
            if (pixels)
            {
                for (auto y = 0; y < 32; ++y)
                    for (auto x = 0; x < 32; ++x)
                    {
                        auto pixel = pixels + (y * pitch + x) * 4;
                        if (pixel[0] > 16 || pixel[1] > 16 || pixel[2] > 16)
                        {
                            ++lit;
                            if (left && x < 16) ++*left;
                            if (right && x >= 16) ++*right;
                        }
                    }
            }
            readback.Unlock();
        };
        FLibraGpuFrame frame;
        frame.SpawnCount = 1;
        frame.Delta = 0.5f;
        state->Dispatch(commandList, frame);
        render(state, litPixels[0], nullptr, nullptr, FMatrix44f::Identity);
        auto anchoredState = TestState(0);
        auto anchoredFrame = frame;
        anchoredFrame.Position = FVector3f(10, 0, 0);
        anchoredState->Dispatch(commandList, anchoredFrame);
        render(anchoredState, litPixels[1], &leftPixels, &rightPixels,
            RelativeToEmitter(translated, anchoredFrame.Position));
        state->Dispatch(commandList, frame);
        render(state, litPixels[2], nullptr, nullptr, FMatrix44f::Identity);
    });
    FlushRenderingCommands();
    TestTrue(TEXT("GPU billboard vertex/pixel pipeline produced framebuffer pixels"), litPixels[0] > 512);
    TestTrue(TEXT("Connected draw Matrix rebases world-space particles and transforms the draw"), litPixels[1] > 512);
    TestTrue(TEXT("Translated draw Matrix moves billboard pixels to the expected side"), rightPixels > leftPixels);
    TestEqual(TEXT("Dead particles produce no framebuffer pixels"), litPixels[2], 0);
    auto sourceBounds = FBox(FVector(-1), FVector(1));
    auto rotatedScaled = FMatrix44f::Identity;
    rotatedScaled.M[0][0] = 0;
    rotatedScaled.M[0][1] = -1;
    rotatedScaled.M[1][0] = 2;
    rotatedScaled.M[1][1] = 0;
    rotatedScaled.M[0][3] = 0.75f;
    auto transformedBounds = FBox(ForceInit);
    TestTrue(TEXT("Affine draw Matrix produces conservative transformed bounds"),
        TransformGpuBounds(sourceBounds, rotatedScaled, transformedBounds));
    TestTrue(TEXT("Transformed bounds include authored translation and rotation/scale"),
        FMath::IsNearlyEqual(transformedBounds.Min.X, -0.25, 1e-5) &&
        FMath::IsNearlyEqual(transformedBounds.Max.X, 1.75, 1e-5) &&
        FMath::IsNearlyEqual(transformedBounds.Min.Y, -2.0, 1e-5) &&
        FMath::IsNearlyEqual(transformedBounds.Max.Y, 2.0, 1e-5));
    AddInfo(FString::Printf(TEXT("GPU framebuffer proof: identity=%d translated=%d dead=%d pixels"), litPixels[0], litPixels[1], litPixels[2]));
    return true;
}

namespace
{
FString LibraGpuFixturePath(const FString& name)
{
    auto plugin = IPluginManager::Get().FindPlugin(TEXT("LibraRuntimeForUnreal"));
    return plugin ? FPaths::Combine(plugin->GetBaseDir(),
        TEXT("Source/LibraGpuRuntime/Private/Tests/Fixtures"), name) : FString();
}

TSharedPtr<FJsonObject> LibraGpuReadReference(const FString& name)
{
    FString text;
    TSharedPtr<FJsonObject> result;
    if (FFileHelper::LoadFileToString(text, *LibraGpuFixturePath(name)))
        FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(text), result);
    return result;
}

ULibraGpuEffectAsset* LibraGpuReadFixture(const FString& name, FString& error)
{
    FString text;
    if (!FFileHelper::LoadFileToString(text, *LibraGpuFixturePath(name + TEXT(".lfxgpu"))))
    {
        error = TEXT("Missing generated canonical UE GPU fixture.");
        return nullptr;
    }
    auto asset = NewObject<ULibraGpuEffectAsset>();
    return asset->ImportBundle(text, error) ? asset : nullptr;
}

FVector4f LibraGpuReferenceVector(const TSharedPtr<FJsonObject>& object, const FString& field,
    FVector4f fallback = FVector4f(0, 0, 0, 0))
{
    const TArray<TSharedPtr<FJsonValue>>* values = nullptr;
    if (!object->TryGetArrayField(field, values)) return fallback;
    auto result = fallback;
    for (auto i = 0; i < FMath::Min(values->Num(), 4); ++i) result[i] = float((*values)[i]->AsNumber());
    return result;
}

FTestParticle LibraGpuReferenceInput(const TSharedPtr<FJsonObject>& reference)
{
    FTestParticle result;
    result.PositionAge = LibraGpuReferenceVector(reference, TEXT("inputPosition"));
    result.VelocityLifetime = LibraGpuReferenceVector(reference, TEXT("inputVelocity"));
    result.VelocityLifetime.W = 1000;
    result.Color = LibraGpuReferenceVector(reference, TEXT("inputColor"), FVector4f(1, 1, 1, 1));
    result.SizeRotation = FVector4f(1, 1, 0, 0);
    double seed = 0;
    reference->TryGetNumberField(TEXT("seed"), seed);
    result.SeedAlive = FUintVector4(uint32(seed), 1, 0, 0);
    return result;
}

bool LibraGpuEvaluateReferences(ULibraGpuEffectAsset* asset, const FString& kernelId,
    const TArray<FTestParticle>& inputs, TArray<FTestParticle>& outputs)
{
    auto kernel = asset->GetKernels().FindByPredicate([&kernelId](const FLibraGpuKernelPacket& packet) { return packet.Id == kernelId; });
    if (!kernel || inputs.IsEmpty()) return false;
    auto state = MakeShared<FLibraGpuState, ESPMode::ThreadSafe>();
    state->Capacity = inputs.Num();
    state->Kernels.Add(*kernel);
    state->MaximumRegisters = ComponentPacketWord(kernel->Bytes, 16);
    for (const auto& texture : asset->GetTextures())
    {
        FLibraGpuRenderTexture binding;
        binding.bBuiltinWhite = texture.bBuiltinWhite;
        if (texture.Texture)
        {
            binding.Reference = texture.Texture->TextureReference.TextureReferenceRHI;
        }
        state->Textures.Add(MoveTemp(binding));
    }
    FLibraGpuFlowStep step;
    step.Kind = kernel->bUpdate ? TEXT("Update") : TEXT("Spawn");
    step.Index = 0;
    state->Flow.Add(step);
    state->TestInitialParticles.SetNumUninitialized(inputs.Num() * sizeof(FTestParticle));
    FMemory::Memcpy(state->TestInitialParticles.GetData(), inputs.GetData(), state->TestInitialParticles.Num());
    outputs.SetNum(inputs.Num());
    auto copied = false;
    ENQUEUE_RENDER_COMMAND(LibraGpuReferenceReadback)([state, &outputs, &copied](FRHICommandListImmediate& commandList)
    {
        FLibraGpuFrame frame;
        state->Dispatch(commandList, frame);
        FRHIGPUBufferReadback readback(TEXT("LibraGpu.CanonicalReference"));
        FRDGBuilder graph(commandList);
        auto bytes = outputs.Num() * sizeof(FTestParticle);
        AddEnqueueCopyPass(graph, &readback, graph.RegisterExternalBuffer(state->Particles), bytes);
        graph.Execute();
        readback.Wait(commandList, readback.GetLastCopyGPUMask());
        auto data = readback.Lock(bytes);
        if (data)
        {
            FMemory::Memcpy(outputs.GetData(), data, bytes);
            copied = true;
        }
        readback.Unlock();
    });
    FlushRenderingCommands();
    return copied;
}

bool LibraGpuReferenceClose(FAutomationTestBase& test, const FString& label, const FVector4f& actual,
    const FVector4f& expected, int32 components, float absolute, float relative = 0)
{
    auto passed = true;
    for (auto i = 0; i < components; ++i)
        passed &= test.TestTrue(FString::Printf(TEXT("%s lane%d actual=%.9g expected=%.9g"), *label, i, actual[i], expected[i]),
            FMath::IsFinite(actual[i]) && FMath::Abs(actual[i] - expected[i]) <= absolute + relative * FMath::Abs(expected[i]));
    return passed;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGpuDefaultEmitterRendererTest,
    "Libra.GPU.Render.AuthoredDefaultEmitterBuiltinWhite",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGpuDefaultEmitterRendererTest::RunTest(const FString& parameters)
{
    if (GUsingNullRHI || GMaxRHIFeatureLevel < ERHIFeatureLevel::SM5)
    {
        AddError(TEXT("A real SM5 GPU is required for authored starter rendering proof."));
        return false;
    }
    FString error;
    auto asset = LibraGpuReadFixture(TEXT("white-emitter"), error);
    if (!asset) { AddError(error); return false; }
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, FName(TEXT("LibraGpuStarterRenderWorld")),
        nullptr, true, ERHIFeatureLevel::Num, &init);
    auto actor = world->SpawnActor<AActor>();
    if (!actor) { world->DestroyWorld(false); return false; }
    auto runtime = NewObject<ULibraGpuEffectRuntime>(actor);
    runtime->Initialize(actor);
    TestTrue(TEXT("Actual GPU runtime accepts the canonical default starter"), runtime->SetEffect(asset));
    TestTrue(TEXT("Actual GPU runtime starts playback"), runtime->Play());
    runtime->Tick(0.2f);
    auto state = runtime->State;
    if (!state)
    {
        AddError(runtime->LastError);
        runtime->Stop();
        world->DestroyWorld(false);
        return false;
    }
    auto copied = false;
    FTestParticle first;
    auto readParticle = [&copied, &first, &state]()
    {
        copied = false;
        ENQUEUE_RENDER_COMMAND(LibraGpuStarterParticle)([&copied, &first, state](FRHICommandListImmediate& commandList)
        {
            if (!state->Particles) return;
            FRHIGPUBufferReadback readback(TEXT("LibraGpu.StarterParticle"));
            FRDGBuilder graph(commandList);
            AddEnqueueCopyPass(graph, &readback, graph.RegisterExternalBuffer(state->Particles), sizeof(first));
            graph.Execute();
            readback.Wait(commandList, readback.GetLastCopyGPUMask());
            if (auto data = readback.Lock(sizeof(first)))
            {
                FMemory::Memcpy(&first, data, sizeof(first));
                copied = true;
            }
            readback.Unlock();
        });
        FlushRenderingCommands();
    };
    readParticle();
    TestTrue(TEXT("Actual starter Spawn+Update dispatched to GPU"), copied);
    TestEqual(TEXT("Canonical Size stays 0.1; visibility is not a synthetic large particle"), first.SizeRotation.X, 0.1f);
    TestTrue(TEXT("Canonical gravity/position/age execute on GPU"),
        FMath::IsNearlyEqual(first.VelocityLifetime.Y, 1.038f, 0.0001f) &&
        FMath::IsNearlyEqual(first.PositionAge.Y, 0.2076f, 0.0001f) &&
        FMath::IsNearlyEqual(first.PositionAge.W, 0.2f, 0.0001f) && first.SeedAlive.Y == 1);
    int32 litPixels[3]{};
    int32 centerGreen[2]{};
    ENQUEUE_RENDER_COMMAND(LibraGpuStarterFramebuffer)([state, &litPixels, &centerGreen](FRHICommandListImmediate& commandList)
    {
        auto render = [&commandList, state](int32& lit, int32* green, ELibraGpuBillboardBlend blend, float sizeScale)
        {
            FRHIGPUTextureReadback readback(TEXT("LibraGpu.AuthoredStarterFramebuffer"));
            FRDGBuilder graph(commandList);
            auto target = graph.CreateTexture(FRDGTextureDesc::Create2D(FIntPoint(128, 128),
                PF_R8G8B8A8, FClearValueBinding::Black, TexCreate_RenderTargetable | TexCreate_ShaderResource),
                TEXT("LibraGpu.StarterFramebuffer"));
            AddClearRenderTargetPass(graph, target);
            auto draw = graph.AllocParameters<FLibraGpuBillboardPassParameters>();
            draw->VS.ParticleData = graph.CreateSRV(graph.RegisterExternalBuffer(state->Particles));
            // Perspective at 200 UE units, 90-degree FOV, Y-forward/Z-up: not an identity/huge-quad test.
            draw->VS.ViewProjection = FMatrix44f(
                FPlane4f(1, 0, 0, 0), FPlane4f(0, 0, 0, 1),
                FPlane4f(0, 1, 0, 0), FPlane4f(0, 0, 100, 200));
            draw->VS.PreViewTranslation = FVector3f::ZeroVector;
            draw->VS.CameraRight = FVector3f(1, 0, 0);
            draw->VS.CameraUp = FVector3f(0, 0, 1);
            draw->VS.SizeScale = sizeScale;
            draw->PS.BillboardTexture = state->Textures[0].GetTexture();
            draw->PS.BillboardSampler = TStaticSamplerState<SF_Bilinear>::GetRHI();
            draw->PS.RenderTargets[0] = FRenderTargetBinding(target, ERenderTargetLoadAction::ELoad);
            AddLibraGpuBillboardRasterPass(graph, draw, FIntRect(0, 0, 128, 128), state->Capacity,
                GMaxRHIFeatureLevel, TEXT("LibraGpu.TestStarter"), blend);
            AddEnqueueCopyPass(graph, &readback, target);
            graph.Execute();
            readback.Wait(commandList, readback.GetLastCopyGPUMask());
            int32 pitch = 0;
            if (auto pixels = static_cast<const uint8*>(readback.Lock(pitch)))
            {
                for (auto y = 0; y < 128; ++y)
                    for (auto x = 0; x < 128; ++x)
                    {
                        auto pixel = pixels + (y * pitch + x) * 4;
                        if (pixel[0] > 16 || pixel[1] > 16 || pixel[2] > 16) ++lit;
                    }
                if (green) *green = pixels[(64 * pitch + 64) * 4 + 1];
            }
            readback.Unlock();
        };
        render(litPixels[0], nullptr, state->Flow.Last().BillboardBlend, state->Flow.Last().SizeScale);
        render(litPixels[1], nullptr, state->Flow.Last().BillboardBlend, 1);
        render(litPixels[2], &centerGreen[0], ELibraGpuBillboardBlend::Alpha, 100);
        render(litPixels[2], &centerGreen[1], ELibraGpuBillboardBlend::Additive, 100);
    });
    FlushRenderingCommands();
    TestTrue(TEXT("Explicit white/default Size0.1 renders visible pixels at a realistic camera distance"), litPixels[0] >= 16);
    TestTrue(TEXT("Explicit renderer sizeScale fixes the centimeter subpixel starter"), litPixels[0] > litPixels[1]);
    TestTrue(TEXT("Additive and alpha material presets actually produce different framebuffer colors"),
        centerGreen[1] > centerGreen[0] + 30);
    runtime->Pause();
    auto paused = first;
    runtime->Tick(0.25f);
    readParticle();
    TestTrue(TEXT("Pause preserves actual GPU particle bytes"), copied && FMemory::Memcmp(&first, &paused, sizeof(first)) == 0);
    TestTrue(TEXT("Resume retains the same GPU state"), runtime->Play());
    runtime->Tick(0.1f);
    readParticle();
    TestTrue(TEXT("Resume advances real GPU age"), copied && first.PositionAge.W > paused.PositionAge.W);
    auto previousState = TWeakPtr<FLibraGpuState, ESPMode::ThreadSafe>(state);
    FString editedBundle;
    if (FFileHelper::LoadFileToString(editedBundle, *LibraGpuFixturePath(TEXT("white-emitter.lfxgpu"))))
    {
        editedBundle = editedBundle.Replace(TEXT("builtin:unlit-additive"), TEXT("builtin:unlit-alpha"));
        auto alphaAsset = ULibraGpuEffectAsset::CreateInMemory(actor, editedBundle, error);
        TestTrue(TEXT("Accepted material edit can replace the GPU runtime effect"), alphaAsset && runtime->SetEffect(alphaAsset));
        TestTrue(TEXT("Material edit starts a fresh renderer snapshot"), runtime->Play() && runtime->State != state);
        if (runtime->State)
            TestEqual(TEXT("Replacement actually selects alpha instead of additive"),
                runtime->State->Flow.Last().BillboardBlend, ELibraGpuBillboardBlend::Alpha);
    }
    else AddError(TEXT("Missing starter fixture for material replacement test."));
    state.Reset();
    FlushRenderingCommands();
    TestFalse(TEXT("Replacing the effect releases the previous render state"), previousState.IsValid());
    FBox bounds;
    TestFalse(TEXT("No invented dynamic particle bounds"), runtime->GetPreviewBounds(bounds));
    runtime->LocalPreviewBounds = FBox(FVector(-100), FVector(100));
    TestTrue(TEXT("Explicit conservative framing bounds are available"), runtime->GetPreviewBounds(bounds));
    auto finalState = TWeakPtr<FLibraGpuState, ESPMode::ThreadSafe>(runtime->State);
    TestTrue(TEXT("A live renderer exists before Stop"), finalState.IsValid());
    runtime->Tick(0.1f);
    runtime->Stop();
    TestFalse(TEXT("Stop detaches the render state on the game thread"), runtime->HasRuntimeState());
    FlushRenderingCommands();
    TestFalse(TEXT("Stopped render state is released after queued rendering drains"), finalState.IsValid());
    world->DestroyWorld(false);
    FlushRenderingCommands();
    AddInfo(FString::Printf(TEXT("Canonical starter GPU render: size0.1 scale100=%d pixels, legacy scale1=%d; pause/resume verified"),
        litPixels[0], litPixels[1]));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGpuExtendedMathTest,
    "Libra.GPU.Compute.ExtendedMathCanonicalReferences",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGpuExtendedMathTest::RunTest(const FString& parameters)
{
    if (GUsingNullRHI || GMaxRHIFeatureLevel < ERHIFeatureLevel::SM5) { AddError(TEXT("Real SM5 GPU required.")); return false; }
    FString error;
    auto asset = LibraGpuReadFixture(TEXT("math"), error);
    auto reference = LibraGpuReadReference(TEXT("math.reference.json"));
    if (!asset || !reference) { AddError(FString::Printf(TEXT("Canonical math fixture unavailable: %s"), *error)); return false; }
    auto absolute = float(reference->GetNumberField(TEXT("absoluteTolerance")));
    auto relative = float(reference->GetNumberField(TEXT("relativeTolerance")));
    TMap<FString, TArray<TSharedPtr<FJsonObject>>> groups;
    for (const auto& value : reference->GetArrayField(TEXT("cases")))
    {
        auto item = value->AsObject();
        groups.FindOrAdd(item->GetStringField(TEXT("kernelId"))).Add(item);
    }
    auto compared = 0;
    for (const auto& group : groups)
    {
        TArray<FTestParticle> inputs, outputs;
        for (const auto& item : group.Value) inputs.Add(LibraGpuReferenceInput(item));
        if (!LibraGpuEvaluateReferences(asset, group.Key, inputs, outputs))
        {
            AddError(FString::Printf(TEXT("GPU reference dispatch failed: %s"), *group.Key));
            return false;
        }
        for (auto i = 0; i < outputs.Num(); ++i)
        {
            auto target = group.Value[i]->GetStringField(TEXT("outputAttribute"));
            FVector4f actual(0, 0, 0, 0);
            auto components = 4;
            if (target == TEXT("Color")) actual = outputs[i].Color;
            else if (target == TEXT("Position")) { actual = outputs[i].PositionAge; components = 3; }
            else if (target == TEXT("Rotation")) { actual.X = outputs[i].SizeRotation.Z; components = 1; }
            else { AddError(TEXT("Unknown canonical math output attribute.")); return false; }
            LibraGpuReferenceClose(*this, group.Key, actual, LibraGpuReferenceVector(group.Value[i], TEXT("expected")),
                components, absolute, relative);
            TestEqual(TEXT("Math output remains alive"), outputs[i].SeedAlive.Y, 1u);
            ++compared;
        }
    }
    AddInfo(FString::Printf(TEXT("Canonical GPU math proof: %d cases across %d kernels"), compared, groups.Num()));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGpuNoiseReferenceTest,
    "Libra.GPU.Compute.NoiseCanonicalReferencesAndCurlDivergence",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGpuNoiseReferenceTest::RunTest(const FString& parameters)
{
    if (GUsingNullRHI || GMaxRHIFeatureLevel < ERHIFeatureLevel::SM5) { AddError(TEXT("Real SM5 GPU required.")); return false; }
    FString error;
    auto asset = LibraGpuReadFixture(TEXT("noise"), error);
    auto reference = LibraGpuReadReference(TEXT("noise.reference.json"));
    if (!asset || !reference) { AddError(FString::Printf(TEXT("Canonical noise fixture unavailable: %s"), *error)); return false; }
    const auto& cases = reference->GetArrayField(TEXT("cases"));
    TArray<FTestParticle> inputs, outputs;
    for (const auto& value : cases) inputs.Add(LibraGpuReferenceInput(value->AsObject()));
    if (!LibraGpuEvaluateReferences(asset, TEXT("noise"), inputs, outputs)) { AddError(TEXT("GPU noise dispatch failed.")); return false; }
    auto tolerance = float(reference->GetNumberField(TEXT("floatTolerance")));
    TMap<FString, FVector3f> curl;
    for (auto i = 0; i < cases.Num(); ++i)
    {
        auto item = cases[i]->AsObject();
        auto label = item->GetStringField(TEXT("label"));
        LibraGpuReferenceClose(*this, label + TEXT(".curl"), outputs[i].PositionAge,
            LibraGpuReferenceVector(item, TEXT("expectedPosition")), 3, tolerance);
        LibraGpuReferenceClose(*this, label + TEXT(".velocity"), outputs[i].VelocityLifetime,
            LibraGpuReferenceVector(item, TEXT("expectedVelocity")), 3, tolerance);
        LibraGpuReferenceClose(*this, label + TEXT(".color"), outputs[i].Color,
            LibraGpuReferenceVector(item, TEXT("expectedColor")), 4, tolerance);
        LibraGpuReferenceClose(*this, label + TEXT(".size"), outputs[i].SizeRotation,
            LibraGpuReferenceVector(item, TEXT("expectedSize")), 2, tolerance);
        TestEqual(TEXT("Noise retains live finite particle"), outputs[i].SeedAlive.Y, 1u);
        curl.Add(label, FVector3f(outputs[i].PositionAge.X, outputs[i].PositionAge.Y, outputs[i].PositionAge.Z));
    }
    auto h = float(reference->GetNumberField(TEXT("finiteDifferenceEpsilon")));
    auto divergence = ((curl[TEXT("divergence_plus_x")].X - curl[TEXT("divergence_minus_x")].X) +
        (curl[TEXT("divergence_plus_y")].Y - curl[TEXT("divergence_minus_y")].Y) +
        (curl[TEXT("divergence_plus_z")].Z - curl[TEXT("divergence_minus_z")].Z)) / (2 * h);
    TestTrue(TEXT("Analytic GPU curl field has near-zero central divergence"),
        FMath::IsFinite(divergence) && FMath::Abs(divergence) <= reference->GetNumberField(TEXT("divergenceTolerance")));
    TestTrue(TEXT("Noise seed variation is observable"), !curl[TEXT("positive")].Equals(curl[TEXT("seed_one")], tolerance));

    auto guards = LibraGpuReadFixture(TEXT("noiseguards"), error);
    if (!guards) { AddError(error); return false; }
    TArray<FTestParticle> guardInput = {LibraGpuReferenceInput(cases[0]->AsObject())};
    TArray<FTestParticle> guardOutput;
    if (!LibraGpuEvaluateReferences(guards, TEXT("noiseGuards"), guardInput, guardOutput)) return false;
    LibraGpuReferenceClose(*this, TEXT("Computed invalid noise parameters return zero position"), guardOutput[0].PositionAge,
        FVector4f(0, 0, 0, 0), 3, tolerance);
    LibraGpuReferenceClose(*this, TEXT("Computed invalid noise parameters return zero velocity"), guardOutput[0].VelocityLifetime,
        FVector4f(0, 0, 0, 0), 3, tolerance);
    TestEqual(TEXT("Guarded noise result stays alive"), guardOutput[0].SeedAlive.Y, 1u);
    AddInfo(FString::Printf(TEXT("Canonical GPU noise proof: %d cases, curl divergence=%.9g"), cases.Num(), divergence));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGpuFlowMapReadbackTest,
    "Libra.GPU.Compute.FlowMapTextureSampling",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGpuFlowMapReadbackTest::RunTest(const FString& parameters)
{
    if (GUsingNullRHI || GMaxRHIFeatureLevel < ERHIFeatureLevel::SM5)
    {
        AddError(TEXT("A real SM5 GPU is required for FlowMap sampling verification."));
        return false;
    }

    auto texture = UTexture2D::CreateTransient(2, 2, PF_B8G8R8A8);
    if (!TestNotNull(TEXT("Transient FlowMap texture is created"), texture))
    {
        return false;
    }
    texture->SRGB = false;
    texture->Filter = TF_Bilinear;
    auto textureName = FString::Printf(TEXT("LibraGpuFlowMap_%s"), *FGuid::NewGuid().ToString(EGuidFormats::Digits));
    if (!texture->Rename(*textureName, GetTransientPackage(), REN_DontCreateRedirectors))
    {
        AddError(TEXT("Transient FlowMap texture could not be named for native asset binding."));
        return false;
    }
    if (!texture->GetPlatformData() || texture->GetPlatformData()->Mips.IsEmpty())
    {
        AddError(TEXT("Transient FlowMap texture has no writable mip."));
        return false;
    }
    auto& mip = texture->GetPlatformData()->Mips[0];
    auto pixels = static_cast<FColor*>(mip.BulkData.Lock(LOCK_READ_WRITE));
    if (!pixels)
    {
        mip.BulkData.Unlock();
        AddError(TEXT("Transient FlowMap texture mip could not be locked."));
        return false;
    }
    pixels[0] = FColor::Black;
    pixels[1] = FColor(255, 128, 0, 255);
    pixels[2] = FColor::Black;
    pixels[3] = FColor::Black;
    mip.BulkData.Unlock();
    texture->UpdateResource();
    FlushRenderingCommands();

    FString error;
    auto asset = NewObject<ULibraGpuEffectAsset>();
    if (!asset->ImportBundle(FlowMapTestBundle(texture->GetPathName()), error) ||
        !asset->ValidateProgram(error))
    {
        AddError(FString::Printf(TEXT("FlowMap GPU asset failed validation: %s"), *error));
        return false;
    }
    FTestParticle input;
    input.PositionAge = FVector4f(-0.5f, 0.5f, 0, 0);
    input.VelocityLifetime.W = 1000;
    input.SeedAlive = FUintVector4(1, 1, 0, 0);
    TArray<FTestParticle> outputs;
    if (!LibraGpuEvaluateReferences(asset, TEXT("wind"), {input}, outputs))
    {
        AddError(TEXT("FlowMap GPU kernel dispatch or readback failed."));
        return false;
    }
    TestTrue(TEXT("FlowMap red channel maps to selected U axis"),
        FMath::IsNearlyEqual(outputs[0].VelocityLifetime.Y, 1.0f, 0.02f));
    TestTrue(TEXT("FlowMap green channel is decoded from unsigned to signed values"),
        FMath::IsNearlyZero(outputs[0].VelocityLifetime.X, 0.02f));
    TestTrue(TEXT("FlowMap leaves the unused world axis at zero"),
        FMath::IsNearlyZero(outputs[0].VelocityLifetime.Z, 0.02f));
    TestEqual(TEXT("FlowMap writes a finite live particle"), outputs[0].SeedAlive.Y, 1u);
    AddInfo(TEXT("FlowMap world-axis projection, texture sampling and signed RG decoding executed on the GPU."));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGpuSharedSupportTest,
    "Libra.GPU.Assets.CanonicalSharedShaderSupport",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGpuSharedSupportTest::RunTest(const FString& parameters)
{
    auto plugin = IPluginManager::Get().FindPlugin(TEXT("LibraRuntimeForUnreal"));
    if (!plugin) return false;
    TArray<uint8> actual, canonical;
    auto loaded = FFileHelper::LoadFileToArray(actual, *FPaths::Combine(plugin->GetBaseDir(), TEXT("Shaders/Private/LibraGpuSupport.ush"))) &&
        FFileHelper::LoadFileToArray(canonical, *LibraGpuFixturePath(TEXT("LibraGpuSupport.hlsl")));
    TestTrue(TEXT("Shared helper fixtures exist"), loaded);
    TestTrue(TEXT("UE helper is byte-identical to the canonical Core export"), loaded && actual == canonical);
    return true;
}
#endif
