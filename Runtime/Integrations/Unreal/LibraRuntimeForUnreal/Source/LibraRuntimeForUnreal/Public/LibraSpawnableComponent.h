#pragma once

#include "CoreMinimal.h"
#include "Components/ActorComponent.h"
#include "LibraRuntimeTypes.h"
#include "LibraSpawnableComponent.generated.h"

namespace liberafx { class SpawnableHost; }
class ULibraSpawnableAsset;
class UCameraComponent;
class UMaterialInterface;
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

USTRUCT(BlueprintType)
struct FLibraCpuParticleEvent
{
    GENERATED_BODY()

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime|CPU Particles")
    FGuid SystemId;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime|CPU Particles")
    FGuid NodeId;

    // Decimal string preserves the full unsigned native ID in Blueprint.
    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime|CPU Particles")
    FString ParticleId;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime|CPU Particles")
    double Time = 0;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime|CPU Particles")
    FVector4 Values = FVector4(0, 0, 0, 0);

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime|CPU Particles")
    int32 Kind = 0;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime|CPU Particles")
    int32 Width = 0;
};

UCLASS(HideDropdown, ClassGroup = (Effects), meta = (DisplayName = "Libra Spawnable (Internal)"))
class LIBRARUNTIMEFORUNREAL_API ULibraSpawnableComponent : public UActorComponent
{
    GENERATED_BODY()

public:
    void ClearPreviewDebugDraw();
    bool IsPreviewDebugDrawSuppressed() const { return bSuppressPreviewDebugDraw; }
    ULibraSpawnableComponent();
    static bool EnsureProceduralMaterialMasters(FString& error);
    // Effect-space axes expressed in host axes: +X=1, +Y=2, +Z=3, with optional sign.
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra Runtime|Coordinates")
    FIntVector EffectAxes = FIntVector(2, 3, 1);

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra Runtime|Coordinates", meta = (ClampMin = "0.000000001"))
    double HostUnitsPerMetre = 100;

    FMatrix EffectToHostMatrix() const;
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

    UPROPERTY()
    TObjectPtr<UCameraComponent> EffectCameraOverride;

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
    double RealTimeSeconds = 0;
    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult SetTimeScale(double ratio);

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

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Game I/O")
    ELibraRuntimeResult SetGameBoolProperty(const FString& name, bool value);

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

    ELibraRuntimeResult ReadCpuParticleCount(FGuid systemId, uint32& count) const;
    ELibraRuntimeResult PopCpuParticleEvent(FLibraCpuParticleEvent& particleEvent, bool& hasEvent);
    ELibraRuntimeResult PopScopedCpuParticleEvent(FGuid containerId, int64 instanceId,
        FLibraCpuParticleEvent& particleEvent, bool& hasEvent);
    ELibraRuntimeResult ReadCpuParticleCount(FGuid containerId, int64 instanceId,
        FGuid systemId, uint32& count) const;
    ELibraRuntimeResult ReadCpuParticleAt(FGuid containerId, int64 instanceId,
        FGuid systemId, SIZE_T index, uint64& particleId,
        double& positionX, double& positionY, double& positionZ,
        double& colorR, double& colorG, double& colorB, double& colorA,
        double& sizeX, double& sizeY) const;
    ELibraRuntimeResult ReadCpuParticleAt(FGuid systemId, SIZE_T index, uint64& particleId,
        double& positionX, double& positionY, double& positionZ,
        double& colorR, double& colorG, double& colorB, double& colorA,
        double& sizeX, double& sizeY) const;

    UFUNCTION(BlueprintPure, Category = "Libra Runtime|Rendering")
    int32 GetRenderObjectCount() const;

    UFUNCTION(BlueprintPure, Category = "Libra Runtime|Rendering")
    int32 GetNativeDrawPrimitiveCount() const;
    void GetNativeRenderSubmissionStats(int32& logicalRenderObjects, int32& nativePrimitives,
        int32& opaqueBatches, int32& cpuParticleBatches, int32& liveCpuParticles,
        int32& allocatedInstanceCapacity, uint64& submittedBytes) const;
    void UpdateOwnerPresentation();
    void GetDrawSnapshots(TArray<FTransform>& transforms, TArray<FBoxSphereBounds>& bounds,
        TArray<FGuid>& drawIds, TArray<FGuid>& containerIds, TArray<int64>& instanceIds) const;

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
    // Hard references on the component CDO keep the shipped masters in cooked builds.
    UPROPERTY()
    TArray<TObjectPtr<UMaterialInterface>> ProceduralMaterialMasters;
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
