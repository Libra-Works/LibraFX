#pragma once

#include "CoreMinimal.h"
#include "Components/SceneComponent.h"
#include "LibraGpuEffectComponent.generated.h"

class ULibraGpuEffectAsset;
struct FLibraGpuState;
struct FStreamableHandle;
class FLibraGpuViewExtension;

UCLASS(ClassGroup = (Effects), meta = (BlueprintSpawnableComponent, DisplayName = "Libra GPU Effect"))
class LIBRAGPURUNTIME_API ULibraGpuEffectComponent : public USceneComponent
{
    GENERATED_BODY()

public:
    ULibraGpuEffectComponent();
    virtual ~ULibraGpuEffectComponent() override;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra GPU")
    TObjectPtr<ULibraGpuEffectAsset> Effect;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra GPU")
    TSoftObjectPtr<ULibraGpuEffectAsset> SoftEffect;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra GPU")
    bool bAutoPlay = true;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra GPU", meta = (ClampMin = "0"))
    float SpawnPerSecond = 100;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra GPU")
    int32 EmitterSeed = 1;

    UPROPERTY(BlueprintReadOnly, Category = "Libra GPU")
    bool bIsPlaying = false;

    UPROPERTY(BlueprintReadOnly, Category = "Libra GPU")
    FString LastError;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra GPU|Preview")
    FBox LocalPreviewBounds = FBox(ForceInit);

    bool GetPreviewBounds(FBox& outBounds) const;
    double GetSimulationTime() const { return Elapsed; }

    UFUNCTION(BlueprintCallable, Category = "Libra GPU")
    bool SetEffect(ULibraGpuEffectAsset* newEffect);

    UFUNCTION(BlueprintCallable, Category = "Libra GPU")
    bool ConfigureEmitter(float emitterSpawnRate, int32 seed);

    UFUNCTION(BlueprintCallable, Category = "Libra GPU")
    void LoadSoftEffect();

    UFUNCTION(BlueprintCallable, Category = "Libra GPU")
    bool Play();

    UFUNCTION(BlueprintCallable, Category = "Libra GPU")
    void Pause();

    UFUNCTION(BlueprintCallable, Category = "Libra GPU")
    void Stop();

    UFUNCTION(BlueprintCallable, Category = "Libra GPU")
    bool ResetEffect();

#if !UE_BUILD_SHIPPING
    // Explicit development-only diagnostic; never used by gameplay rendering.
    bool VerifyGpuSmoke(FString& report);
#endif

    virtual void TickComponent(float deltaTime, ELevelTick tickType, FActorComponentTickFunction* tickFunction) override;
    virtual void BeginDestroy() override;

protected:
    virtual void BeginPlay() override;
    virtual void EndPlay(const EEndPlayReason::Type endPlayReason) override;

private:
#if WITH_DEV_AUTOMATION_TESTS
    friend class FLibraGpuDefaultEmitterRendererTest;
#endif
    TSharedPtr<FLibraGpuState, ESPMode::ThreadSafe> State;
    TSharedPtr<FLibraGpuViewExtension, ESPMode::ThreadSafe> ViewExtension;
    TSharedPtr<FStreamableHandle> LoadHandle;
    uint64 LoadGeneration = 0;
    uint32 SpawnCursor = 0;
    uint32 FrameIndex = 0;
    double Elapsed = 0;
    double SpawnRemainder = 0;
};
