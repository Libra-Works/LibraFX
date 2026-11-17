#pragma once

#include "CoreMinimal.h"
#include "Components/ActorComponent.h"
#include "LibraRuntimeTypes.h"
#include "LibraSpawnableComponent.generated.h"

namespace liberafx { class SpawnableHost; }
class ULibraSpawnableAsset;
class FLibraSpawnableRenderState;

USTRUCT(BlueprintType)
struct FLibraSpawnableAssetBinding
{
    GENERATED_BODY()

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra Runtime")
    FString Identity;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra Runtime")
    TObjectPtr<UObject> Asset;
};

UCLASS(HideDropdown, ClassGroup = (Effects), meta = (DisplayName = "Libra Spawnable (Internal)"))
class LIBRARUNTIMEFORUNREAL_API ULibraSpawnableComponent : public UActorComponent
{
    GENERATED_BODY()

public:
    ULibraSpawnableComponent();
    virtual ~ULibraSpawnableComponent() override;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime")
    TObjectPtr<ULibraSpawnableAsset> Package;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime|Rendering")
    TArray<FLibraSpawnableAssetBinding> AssetBindings;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime|Rendering", meta = (ClampMin = "0.0"))
    float GpuSpawnPerSecond = 100;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    FString LastError;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra Runtime")
    bool bAutoPlay = true;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra Runtime", meta = (ClampMin = "0.0"))
    double PlayRate = 1;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime", meta = (ClampMin = "1"))
    int32 MaxActivePerTemplate = 32;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime", meta = (ClampMin = "1024"))
    int64 MemoryBudgetBytesPerTemplate = 4 * 1024 * 1024;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime", meta = (ClampMin = "1"))
    int32 EventCapacity = 128;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime", meta = (ClampMin = "1"))
    int32 InstructionBudget = 10000;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    double TimeSeconds = 0;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    bool bIsPlaying = false;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    bool bRootFinished = false;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    ELibraRuntimeResult LastResult = ELibraRuntimeResult::NotReady;

    UPROPERTY(BlueprintAssignable, Category = "Libra Runtime")
    FOnLibraSpawnableHostEvent OnHostEvent;

    UPROPERTY(BlueprintAssignable, Category = "Libra Runtime")
    FOnLibraGameIoEvent OnGameIoEvent;

    UPROPERTY(BlueprintAssignable, Category = "Libra Runtime")
    FOnLibraSpawnableRuntimeError OnRuntimeError;

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult Play(bool bRestart = true);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    void Pause();

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult RequestNaturalEnd();

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult Stop();

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Signals")
    ELibraRuntimeResult DispatchSignal(FGuid nodeId);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Game I/O")
    ELibraRuntimeResult FireGameSignal(const FString& name);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Game I/O")
    ELibraRuntimeResult SetGameProperty(const FString& name, const TArray<double>& values);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Game I/O")
    ELibraRuntimeResult SetOwnerMatrix(const TArray<double>& values);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult ReadRootValue(FGuid nodeId, FVector4& value, int32& componentCount) const;

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult ReadChildValue(FGuid containerId, int64 instanceId, FGuid nodeId,
        FVector4& value, int32& componentCount) const;

    UFUNCTION(BlueprintPure, Category = "Libra Runtime|Rendering")
    int32 GetRenderObjectCount() const;

    UFUNCTION(BlueprintPure, Category = "Libra Runtime|Rendering")
    int32 GetGpuInstanceCount() const;

    virtual void TickComponent(float deltaTime, ELevelTick tickType,
        FActorComponentTickFunction* tickFunction) override;
    virtual void BeginDestroy() override;

protected:
    virtual void BeginPlay() override;
    virtual void EndPlay(const EEndPlayReason::Type endPlayReason) override;
    virtual void OnComponentDestroyed(bool bDestroyingHierarchy) override;

private:
    ELibraRuntimeResult Report(ELibraRuntimeResult result);
    void DrainEvents();
    void ReleaseHost();
    bool RefreshRendering();
    TUniquePtr<liberafx::SpawnableHost> NativeHost;
    TUniquePtr<FLibraSpawnableRenderState> RenderState;
    uint64 HostGeneration = 0;
    bool bStopping = false;
};
