#pragma once

#include "CoreMinimal.h"
#include "Components/ActorComponent.h"
#include "LibraRuntimeTypes.h"
#include "LibraSpawnableComponent.generated.h"

namespace liberafx { class SpawnableHost; }
class ULibraSpawnableAsset;
class FLibraSpawnableRenderState;
class FLibraSpawnableDebugState;
class FLibraSkeletalPoseRuntime;

// Keep deletion of these private runtime types out of the UHT-generated
// vtable-helper translation unit, where only forward declarations are visible.
struct FLibraSpawnableHostDeleter
{
    void operator()(liberafx::SpawnableHost* value) const;
};

struct FLibraSpawnableRenderStateDeleter
{
    void operator()(FLibraSpawnableRenderState* value) const;
};

struct FLibraSpawnableDebugStateDeleter
{
    void operator()(FLibraSpawnableDebugState* value) const;
};

struct FLibraSkeletalPoseRuntimeDeleter
{
    void operator()(FLibraSkeletalPoseRuntime* value) const;
};

using FLibraSpawnableHostPtr = TUniquePtr<liberafx::SpawnableHost, FLibraSpawnableHostDeleter>;
using FLibraSpawnableRenderStatePtr = TUniquePtr<FLibraSpawnableRenderState, FLibraSpawnableRenderStateDeleter>;
using FLibraSpawnableDebugStatePtr = TUniquePtr<FLibraSpawnableDebugState, FLibraSpawnableDebugStateDeleter>;
using FLibraSkeletalPoseRuntimePtr = TUniquePtr<FLibraSkeletalPoseRuntime, FLibraSkeletalPoseRuntimeDeleter>;

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
    void ClearPreviewDebugDraw();
    bool IsPreviewDebugDrawSuppressed() const { return bSuppressPreviewDebugDraw; }
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

    // Force never dispatches natural Destroy or an automatic End phase.
    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult ForceTerminate();

    // InstanceId is an opaque 64-bit identity; preserve event bits, including sign.
    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult ForceTerminateChild(FGuid containerId, int64 instanceId);

    // User-authored signal entry; this notification does not select End policy.
    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Signals")
    ELibraRuntimeResult NotifyEndRequest(FGuid userSignalNodeId);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Signals")
    ELibraRuntimeResult NotifyChildEndRequest(FGuid containerId, int64 instanceId, FGuid userSignalNodeId);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult Stop();

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Signals")
    ELibraRuntimeResult DispatchSignal(FGuid nodeId);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Game I/O")
    ELibraRuntimeResult FireGameSignal(const FString& name);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Game I/O")
    ELibraRuntimeResult SetGameProperty(const FString& name, const TArray<double>& values);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Instance Game I/O")
    ELibraRuntimeResult GetInstanceTarget(FGuid containerId, int64 instanceId, int64& generation);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Instance Game I/O")
    ELibraRuntimeResult SetInstanceProperty(int64 generation, FGuid containerId, int64 instanceId,
        const FString& name, const TArray<double>& values);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Instance Game I/O")
    ELibraRuntimeResult GetInstanceProperty(int64 generation, FGuid containerId, int64 instanceId,
        const FString& name, TArray<double>& values);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Instance Game I/O")
    ELibraRuntimeResult FireInstanceSignal(int64 generation, FGuid containerId, int64 instanceId, const FString& name);

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

    UFUNCTION(BlueprintPure, Category = "Libra Runtime|Debugger")
    int64 GetDebuggerGeneration() const;

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Debugger")
    ELibraRuntimeResult AttachExecutionDebugger(int64 generation, FGuid containerId, int64 instanceId, const FString& invitation);

    UFUNCTION(CallInEditor, Category = "Libra Runtime|Debugger")
    void AttachLocalDebugger();

    UFUNCTION(BlueprintCallable, CallInEditor, Category = "Libra Runtime|Debugger")
    void DetachExecutionDebugger();
    bool HasPendingDebugWork() const { return DebugJobPending(); }

#if WITH_EDITORONLY_DATA
    UPROPERTY(EditInstanceOnly, Transient, NonTransactional, Category = "Libra Runtime|Debugger")
    FString LocalDebuggerInvitation;
    UPROPERTY(EditInstanceOnly, Transient, NonTransactional, Category = "Libra Runtime|Debugger")
    FGuid DebugContainerId;
    UPROPERTY(EditInstanceOnly, Transient, NonTransactional, Category = "Libra Runtime|Debugger")
    int64 DebugInstanceId = 0;
#endif

    virtual void TickComponent(float deltaTime, ELevelTick tickType,
        FActorComponentTickFunction* tickFunction) override;
    virtual void BeginDestroy() override;

protected:
    virtual void BeginPlay() override;
    virtual void EndPlay(const EEndPlayReason::Type endPlayReason) override;
    virtual void OnComponentDestroyed(bool bDestroyingHierarchy) override;

private:
    bool bSuppressPreviewDebugDraw = false;
    ELibraRuntimeResult ForceTerminateScope(FGuid containerId, int64 instanceId, bool wholeEffect);
    ELibraRuntimeResult NotifyEndRequestScope(FGuid containerId, int64 instanceId,
        FGuid userSignalNodeId, bool wholeEffect);
    ELibraRuntimeResult Report(ELibraRuntimeResult result);
    void DrainEvents();
    void ReleaseHost();
    bool RefreshRendering();
    bool DebugJobPending() const;
    void TickDebugger(float deltaTime);
    void ReleaseDebugger();
    FLibraSpawnableHostPtr NativeHost;
    FLibraSpawnableRenderStatePtr RenderState;
    FLibraSpawnableDebugStatePtr DebugState;
    FLibraSkeletalPoseRuntimePtr SkeletalPoseState;
    uint64 DebugPackageIdentity = 0;
    uint64 HostGeneration = 0;
    bool bStopping = false;
    bool bSourceInitialAdvancePending = false;
    bool bForceTerminated = false;
    int32 EventDrainDepth = 0;
    TMap<FGuid, TSet<int64>> CancelledBatchScopes;
};
