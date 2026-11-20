#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "LibraGpuEffectRuntime.generated.h"

class AActor;
class ULibraGpuEffectAsset;
struct FLibraGpuState;
struct FLibraGpuFluid2DConfiguration;
struct FStreamableHandle;
class FLibraGpuViewExtension;
namespace liberafx::gpu { struct Fluid2DSettings; }

UCLASS(Transient)
class LIBRAGPURUNTIME_API ULibraGpuEffectRuntime : public UObject
{
    GENERATED_BODY()

public:
    ULibraGpuEffectRuntime();
    virtual ~ULibraGpuEffectRuntime() override;

    void Initialize(AActor* owner);
    void Tick(float deltaTime);

    UPROPERTY(Transient)
    TObjectPtr<ULibraGpuEffectAsset> Effect;

    UPROPERTY(Transient)
    TSoftObjectPtr<ULibraGpuEffectAsset> SoftEffect;

    bool bAutoPlay = true;

    float SpawnPerSecond = 100;

    int32 EmitterSeed = 1;

    bool bIsPlaying = false;

    FString LastError;

    FBox LocalPreviewBounds = FBox(ForceInit);

    bool GetPreviewBounds(FBox& outBounds) const;
    bool HasRuntimeState() const { return State.IsValid(); }
    double GetSimulationTime() const { return Elapsed; }

    bool SetEffect(ULibraGpuEffectAsset* newEffect);

    bool ConfigureEmitter(float emitterSpawnRate, int32 seed);

    // Fluid scopes use the shipped LibraFluid2D global shader and an engine-owned
    // RGBA16F output texture. No author-created component or user shader is used.
    bool SetFluid2D(const liberafx::gpu::Fluid2DSettings& settings, float sizeScale,
        const FMatrix44f& localToWorld, bool visible);
    bool SetFluidPresentation(const FMatrix44f& localToWorld, bool visible);
    bool IsFluid2D() const;

    // Updates billboard placement only. Compute dispatches and particle storage
    // continue to use their original simulation-space positions.
    bool SetDrawTransform(const FMatrix44f& drawTransform, bool connected);

    void LoadSoftEffect();

    bool Play();

    void Pause();

    void Stop();

    bool ResetEffect();

#if !UE_BUILD_SHIPPING
    // Explicit development-only diagnostic; never used by gameplay rendering.
    bool VerifyGpuSmoke(FString& report);
    bool VerifyFluid2DReadback(TArray<FLinearColor>& pixels, FString& report);
    bool VerifyFluid2DSettings(uint32 width, uint32 height, uint32 pressureIterations);
    bool VerifyFluid2DPresentation(const FMatrix44f& localToWorld, bool visible);
#endif

    virtual void BeginDestroy() override;

private:
#if WITH_DEV_AUTOMATION_TESTS
    friend class FLibraGpuDefaultEmitterRendererTest;
    friend class FLibraGpuFluid2DExecutionTest;
#endif
    TSharedPtr<FLibraGpuState, ESPMode::ThreadSafe> State;
    TSharedPtr<FLibraGpuFluid2DConfiguration, ESPMode::ThreadSafe> FluidConfiguration;
    TSharedPtr<FLibraGpuViewExtension, ESPMode::ThreadSafe> ViewExtension;
    TSharedPtr<FStreamableHandle> LoadHandle;
    TWeakObjectPtr<AActor> Owner;
    uint64 LoadGeneration = 0;
    uint32 SpawnCursor = 0;
    uint32 FrameIndex = 0;
    double Elapsed = 0;
    double SpawnRemainder = 0;
    FMatrix44f DrawTransform = FMatrix44f::Identity;
    FMatrix44f AuthoredDrawTransform = FMatrix44f::Identity;
    bool bHasDrawTransform = false;
};
