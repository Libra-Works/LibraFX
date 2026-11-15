#include "LibraGpuEffectComponent.h"

#include "CommonRenderResources.h"
#include "Engine/AssetManager.h"
#include "Engine/StreamableManager.h"
#include "Engine/Texture2D.h"
#include "Engine/World.h"
#include "GlobalShader.h"
#include "GlobalRenderResources.h"
#include "LibraGpuEffectAsset.h"
#include "PipelineStateCache.h"
#include "PostProcess/PostProcessMaterialInputs.h"
#include "RenderGraphBuilder.h"
#include "RenderGraphUtils.h"
#include "RenderingThread.h"
#include "RHITextureReference.h"
#include "SceneView.h"
#include "SceneViewExtension.h"
#include "ScreenPass.h"
#include "ShaderParameterStruct.h"

class FLibraGpuInterpreterCS : public FGlobalShader
{
    DECLARE_GLOBAL_SHADER(FLibraGpuInterpreterCS);
    SHADER_USE_PARAMETER_STRUCT(FLibraGpuInterpreterCS, FGlobalShader);

    BEGIN_SHADER_PARAMETER_STRUCT(FParameters, )
        SHADER_PARAMETER_RDG_BUFFER_UAV(RWStructuredBuffer<LibraParticle>, Particles)
        SHADER_PARAMETER_RDG_BUFFER_UAV(RWStructuredBuffer<uint4>, Registers)
        SHADER_PARAMETER_RDG_BUFFER_SRV(StructuredBuffer<uint4>, Instructions)
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
    ERHIFeatureLevel::Type featureLevel)
{
    auto shaderMap = GetGlobalShaderMap(featureLevel);
    TShaderMapRef<FLibraGpuBillboardVS> vertexShader(shaderMap);
    TShaderMapRef<FLibraGpuBillboardPS> pixelShader(shaderMap);
    graph.AddPass(RDG_EVENT_NAME("LibraGpu.Billboards"), parameters, ERDGPassFlags::Raster,
        [parameters, vertexShader, pixelShader, rect, count](FRHICommandList& commandList)
        {
            FGraphicsPipelineStateInitializer pipeline;
            commandList.ApplyCachedRenderTargets(pipeline);
            pipeline.BlendState = TStaticBlendState<CW_RGBA, BO_Add, BF_SourceAlpha, BF_InverseSourceAlpha,
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
}

struct FLibraGpuState
{
    int32 Capacity = 0;
    uint32 MaximumRegisters = 0;
    TArray<FLibraGpuKernelPacket> Kernels;
    TArray<FLibraGpuFlowStep> Flow;
    TArray<FTextureReferenceRHIRef> Textures;
    TRefCountPtr<FRDGPooledBuffer> Particles;
    TRefCountPtr<FRDGPooledBuffer> Registers;
    TArray<TRefCountPtr<FRDGPooledBuffer>> Programs;

    void Dispatch(FRHICommandListImmediate& commandList, const FLibraGpuFrame& frame)
    {
        FRDGBuilder graph(commandList);
        FRDGBufferRef particles;
        FRDGBufferRef registers;
        if (!Particles)
        {
            particles = graph.CreateBuffer(FRDGBufferDesc::CreateStructuredDesc(80, Capacity), TEXT("LibraGpu.Particles"));
            registers = graph.CreateBuffer(FRDGBufferDesc::CreateStructuredDesc(16, FMath::Max(1u, uint32(Capacity) * MaximumRegisters)), TEXT("LibraGpu.Registers"));
            AddClearUAVPass(graph, graph.CreateUAV(particles), 0);
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
                auto parameters = graph.AllocParameters<FLibraGpuInterpreterCS::FParameters>();
                parameters->Particles = graph.CreateUAV(particles);
                parameters->Registers = graph.CreateUAV(registers);
                parameters->Instructions = graph.CreateSRV(program);
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
                FComputeShaderUtils::AddPass(graph, RDG_EVENT_NAME("LibraGpu.Interpret"),
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
            if (step.Kind != TEXT("RenderBillboard") || !State->Textures.IsValidIndex(step.Index) || !State->Textures[step.Index]) continue;
            auto parameters = graph.AllocParameters<FLibraGpuBillboardPassParameters>();
            parameters->VS.ParticleData = graph.CreateSRV(particles);
            parameters->VS.ViewProjection = FMatrix44f(view.ViewMatrices.GetTranslatedViewProjectionMatrix());
            parameters->VS.PreViewTranslation = FVector3f(view.ViewMatrices.GetPreViewTranslation());
            parameters->VS.CameraRight = FVector3f(view.ViewMatrices.GetInvViewMatrix().GetUnitAxis(EAxis::X));
            parameters->VS.CameraUp = FVector3f(view.ViewMatrices.GetInvViewMatrix().GetUnitAxis(EAxis::Y));
            parameters->PS.BillboardTexture = State->Textures[step.Index];
            parameters->PS.BillboardSampler = TStaticSamplerState<SF_Bilinear, AM_Clamp, AM_Clamp, AM_Clamp>::GetRHI();
            parameters->PS.RenderTargets[0] = FRenderTargetBinding(output.Texture, ERenderTargetLoadAction::ELoad);
            AddLibraGpuBillboardRasterPass(graph, parameters, output.ViewRect, State->Capacity, view.GetFeatureLevel());
        }
        return output;
    }

private:
    TSharedRef<FLibraGpuState, ESPMode::ThreadSafe> State;
    FSceneInterface* Scene;
};

ULibraGpuEffectComponent::ULibraGpuEffectComponent()
{
    PrimaryComponentTick.bCanEverTick = true;
}

ULibraGpuEffectComponent::~ULibraGpuEffectComponent() = default;

bool ULibraGpuEffectComponent::SetEffect(ULibraGpuEffectAsset* newEffect)
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

bool ULibraGpuEffectComponent::ConfigureEmitter(float emitterSpawnRate, int32 seed)
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

void ULibraGpuEffectComponent::LoadSoftEffect()
{
    ++LoadGeneration;
    if (LoadHandle) LoadHandle->CancelHandle();
    LoadHandle.Reset();
    if (SoftEffect.IsNull()) return;
    auto generation = LoadGeneration;
    auto weak = TWeakObjectPtr<ULibraGpuEffectComponent>(this);
    auto path = SoftEffect.ToSoftObjectPath();
    LoadHandle = UAssetManager::GetStreamableManager().RequestAsyncLoad(path,
        FStreamableDelegate::CreateLambda([weak, generation, path]()
        {
            auto component = weak.Get();
            if (!component || component->LoadGeneration != generation || component->SoftEffect.ToSoftObjectPath() != path) return;
            auto loaded = component->SoftEffect.Get();
            component->LoadHandle.Reset();
            if (!loaded) component->LastError = TEXT("GPU effect failed to load.");
            else if (component->SetEffect(loaded) && component->bAutoPlay) component->Play();
        }));
}

bool ULibraGpuEffectComponent::ResetEffect()
{
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
    state->Kernels = Effect->GetKernels();
    state->Flow = Effect->GetFlow();
    for (const auto& texture : Effect->GetTextures())
        state->Textures.Add(texture.Texture->TextureReference.TextureReferenceRHI);
    for (const auto& kernel : state->Kernels) state->MaximumRegisters = FMath::Max(state->MaximumRegisters, ComponentPacketWord(kernel.Bytes, 16));
    State = MoveTemp(state);
    ViewExtension.Reset();
    if (GetWorld() && GetWorld()->Scene)
        ViewExtension = FSceneViewExtensions::NewExtension<FLibraGpuViewExtension>(State.ToSharedRef(), GetWorld()->Scene);
    SpawnCursor = FrameIndex = 0;
    Elapsed = SpawnRemainder = 0;
    LastError.Reset();
    return true;
}

bool ULibraGpuEffectComponent::Play()
{
    if (!State && !ResetEffect()) return false;
    bIsPlaying = true;
    return true;
}

void ULibraGpuEffectComponent::Pause() { bIsPlaying = false; }

void ULibraGpuEffectComponent::Stop()
{
    bIsPlaying = false;
    ++LoadGeneration;
    if (LoadHandle) LoadHandle->CancelHandle();
    LoadHandle.Reset();
    ViewExtension.Reset();
    State.Reset();
    SpawnCursor = FrameIndex = 0;
    Elapsed = SpawnRemainder = 0;
}

void ULibraGpuEffectComponent::TickComponent(float deltaTime, ELevelTick tickType, FActorComponentTickFunction* tickFunction)
{
    Super::TickComponent(deltaTime, tickType, tickFunction);
    if (!bIsPlaying || !State || !FMath::IsFinite(deltaTime) || deltaTime < 0 ||
        !FMath::IsFinite(SpawnPerSecond) || SpawnPerSecond < 0) return;
    auto position = FVector3f(GetComponentLocation());
    if (!FMath::IsFinite(position.X) || !FMath::IsFinite(position.Y) || !FMath::IsFinite(position.Z)) return;
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

void ULibraGpuEffectComponent::BeginPlay()
{
    Super::BeginPlay();
    if (Effect && bAutoPlay) Play();
    else if (!SoftEffect.IsNull()) LoadSoftEffect();
}

void ULibraGpuEffectComponent::EndPlay(const EEndPlayReason::Type endPlayReason)
{
    ++LoadGeneration;
    if (LoadHandle) LoadHandle->CancelHandle();
    LoadHandle.Reset();
    Stop();
    Super::EndPlay(endPlayReason);
}

void ULibraGpuEffectComponent::BeginDestroy()
{
    ++LoadGeneration;
    if (LoadHandle) LoadHandle->CancelHandle();
    LoadHandle.Reset();
    Stop();
    Super::BeginDestroy();
}

#if WITH_DEV_AUTOMATION_TESTS
#include "Misc/AutomationTest.h"
#include "RHIGPUReadback.h"

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
            commandList.SubmitAndBlockUntilGPUIdle();
            if (readback.IsReady())
            {
                FMemory::Memcpy(&output, readback.Lock(sizeof(FTestParticle)), sizeof(FTestParticle));
                readback.Unlock();
            }
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
    int32 litPixels[2]{};
    ENQUEUE_RENDER_COMMAND(LibraGpuFramebufferTest)([state, &litPixels](FRHICommandListImmediate& commandList)
    {
        auto render = [&commandList, state](int32& lit)
        {
            FRHIGPUTextureReadback readback(TEXT("LibraGpu.FramebufferProof"));
            FRDGBuilder graph(commandList);
            auto target = graph.CreateTexture(FRDGTextureDesc::Create2D(FIntPoint(32, 32),
                PF_R8G8B8A8, FClearValueBinding::Black,
                TexCreate_RenderTargetable | TexCreate_ShaderResource), TEXT("LibraGpu.TestFramebuffer"));
            AddClearRenderTargetPass(graph, target);
            auto draw = graph.AllocParameters<FLibraGpuBillboardPassParameters>();
            draw->VS.ParticleData = graph.CreateSRV(graph.RegisterExternalBuffer(state->Particles));
            draw->VS.ViewProjection = FMatrix44f::Identity;
            draw->VS.PreViewTranslation = FVector3f::ZeroVector;
            draw->VS.CameraRight = FVector3f(1, 0, 0);
            draw->VS.CameraUp = FVector3f(0, 1, 0);
            draw->PS.BillboardTexture = GWhiteTexture->TextureRHI;
            draw->PS.BillboardSampler = TStaticSamplerState<SF_Bilinear>::GetRHI();
            draw->PS.RenderTargets[0] = FRenderTargetBinding(target, ERenderTargetLoadAction::ELoad);
            AddLibraGpuBillboardRasterPass(graph, draw, FIntRect(0, 0, 32, 32), 1, GMaxRHIFeatureLevel);
            AddEnqueueCopyPass(graph, &readback, target);
            graph.Execute();
            commandList.SubmitAndBlockUntilGPUIdle();
            if (!readback.IsReady()) return;
            int32 pitch = 0;
            auto pixels = static_cast<const uint8*>(readback.Lock(pitch));
            if (pixels)
            {
                for (auto y = 0; y < 32; ++y)
                    for (auto x = 0; x < 32; ++x)
                    {
                        auto pixel = pixels + (y * pitch + x) * 4;
                        if (pixel[0] > 16 || pixel[1] > 16 || pixel[2] > 16) ++lit;
                    }
            }
            readback.Unlock();
        };
        FLibraGpuFrame frame;
        frame.SpawnCount = 1;
        frame.Delta = 0.5f;
        state->Dispatch(commandList, frame);
        render(litPixels[0]);
        state->Dispatch(commandList, frame);
        render(litPixels[1]);
    });
    FlushRenderingCommands();
    TestTrue(TEXT("GPU billboard vertex/pixel pipeline produced framebuffer pixels"), litPixels[0] > 512);
    TestEqual(TEXT("Dead particles produce no framebuffer pixels"), litPixels[1], 0);
    AddInfo(FString::Printf(TEXT("GPU framebuffer proof: live=%d dead=%d nonblack pixels"), litPixels[0], litPixels[1]));
    return true;
}
#endif
