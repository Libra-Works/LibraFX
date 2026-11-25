#pragma once

#include "CoreMinimal.h"
#include "Components/ActorComponent.h"
#include "LibraRuntimeTypes.h"
#include "LibraSpawnableComponent.h"
#include <cstddef>

#include "LibraEffectComponent.generated.h"

namespace liberafx::host { struct ParameterDeltaUpdate; }
class ULibraGpuEffectAsset;
class ULibraGpuEffectRuntime;
class UCameraComponent;

UCLASS(ClassGroup = (Effects), meta = (BlueprintSpawnableComponent, DisplayName = "Libra Effect"))
class LIBRARUNTIMEFORUNREAL_API ULibraEffectComponent : public UActorComponent
{
    GENERATED_BODY()

public:
    void ClearPreviewDebugDraw();
    ULibraEffectComponent();
    virtual ~ULibraEffectComponent() override;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime", meta = (EditCondition = "GpuEffect == nullptr"))
    TObjectPtr<ULibraSpawnableAsset> Package;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime", meta = (EditCondition = "Package == nullptr"))
    TObjectPtr<ULibraGpuEffectAsset> GpuEffect;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra Runtime|Coordinates")
    FIntVector EffectAxes = FIntVector(2, 3, 1);

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra Runtime|Coordinates", meta = (ClampMin = "0.000000001"))
    double HostUnitsPerMetre = 100;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra Runtime|Performance")
    bool bUseSharedSimulation = true;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime|Rendering")
    TArray<FLibraSpawnableAssetBinding> AssetBindings;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime|Rendering", meta = (ClampMin = "0.0"))
    float GpuSpawnPerSecond = 100;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime|Rendering")
    int32 GpuEmitterSeed = 1;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime|Limits", meta = (ClampMin = "1"))
    int32 MaxActivePerTemplate = 32;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime|Limits", meta = (ClampMin = "1024"))
    int64 MemoryBudgetBytesPerTemplate = 4 * 1024 * 1024;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime|Limits", meta = (ClampMin = "1"))
    int32 EventCapacity = 256;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime|Limits", meta = (ClampMin = "1", DisplayName = "Instruction Budget"))
    int32 PackageInstructionBudget = 10000;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    bool bRootFinished = false;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    FString LastError;

    UPROPERTY(BlueprintAssignable, Category = "Libra Runtime")
    FOnLibraSpawnableHostEvent OnHostEvent;

    UPROPERTY(BlueprintAssignable, Category = "Libra Runtime")
    FOnLibraGameIoEvent OnGameIoEvent;

    UPROPERTY(BlueprintAssignable, Category = "Libra Runtime")
    FOnLibraSpawnableRuntimeError OnRuntimeError;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra Runtime")
    bool bAutoPlay = true;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Libra Runtime", meta = (ClampMin = "0.0"))
    double PlayRate = 1;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Libera FX|Playback")
    TObjectPtr<UCameraComponent> EffectCameraOverride;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    double TimeSeconds = 0;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    double LastEvaluatedTimeSeconds = 0;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    bool bIsPlaying = false;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    ELibraRuntimeResult LastResult = ELibraRuntimeResult::NotReady;

    // Read values in this game-thread event, before the next asynchronous evaluation starts.
    UPROPERTY(BlueprintAssignable, Category = "Libra Runtime")
    FOnLibraEvaluationCompleted OnEvaluationCompleted;

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult SetPackage(ULibraSpawnableAsset* newPackage);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult SetGpuEffect(ULibraGpuEffectAsset* newEffect);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult RequestNaturalEnd();

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult ForceTerminate();

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult ForceTerminateChild(FGuid containerId, int64 instanceId);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Signals")
    ELibraRuntimeResult NotifyEndRequest(FGuid userSignalNodeId);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Signals")
    ELibraRuntimeResult NotifyChildEndRequest(FGuid containerId, int64 instanceId, FGuid userSignalNodeId);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Signals")
    ELibraRuntimeResult DispatchNodeSignal(FGuid nodeId);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Game I/O")
    ELibraRuntimeResult FireGameSignal(const FString& name);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Game I/O")
    ELibraRuntimeResult SetGameProperty(const FString& name, const TArray<double>& values);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Game I/O")
    ELibraRuntimeResult SetOwnerMatrix(const TArray<double>& values);

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

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult ReadRootValue(FGuid nodeId, FVector4& value, int32& componentCount) const;

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|CPU Particles")
    ELibraRuntimeResult PopCpuParticleEvent(FLibraCpuParticleEvent& particleEvent, bool& hasEvent);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|CPU Particles")
    ELibraRuntimeResult PopScopedCpuParticleEvent(FGuid containerId, int64 instanceId,
        FLibraCpuParticleEvent& particleEvent, bool& hasEvent);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult ReadChildValue(FGuid containerId, int64 instanceId, FGuid nodeId,
        FVector4& value, int32& componentCount) const;

    UFUNCTION(BlueprintPure, Category = "Libra Runtime|Rendering")
    int32 GetRenderObjectCount() const;

    UFUNCTION(BlueprintPure, Category = "Libra Runtime|Rendering")
    int32 GetNativeDrawPrimitiveCount() const;

    void GetNativeRenderSubmissionStats(int32& logicalRenderObjects, int32& nativePrimitives,
        int32& opaqueBatches, int32& cpuParticleBatches, int32& liveCpuParticles,
        int32& allocatedInstanceCapacity, uint64& submittedBytes) const;

    void GetDrawSnapshots(TArray<FTransform>& transforms, TArray<FBoxSphereBounds>& bounds,
        TArray<FGuid>& drawIds, TArray<FGuid>& containerIds, TArray<int64>& instanceIds) const;

    UFUNCTION(BlueprintPure, Category = "Libra Runtime|Rendering")
    int32 GetGpuInstanceCount() const;

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult Play(bool bRestart = true);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    void Pause();

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    void Stop();

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult EvaluateAtTime(double evaluationTimeSeconds);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Debugger")
    ELibraRuntimeResult AttachExecutionDebugger(const FString& invitation);

    UFUNCTION(BlueprintPure, Category = "Libra Runtime|Debugger")
    int64 GetPackageDebuggerGeneration() const;

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Debugger")
    ELibraRuntimeResult AttachPackageDebugger(int64 generation, FGuid containerId, int64 instanceId,
        const FString& invitation);

    UFUNCTION(BlueprintCallable, CallInEditor, Category = "Libra Runtime|Debugger")
    void DetachExecutionDebugger();

#if WITH_EDITORONLY_DATA
    UPROPERTY(EditInstanceOnly, Transient, NonTransactional, Category = "Libra Runtime|Debugger")
    FString LocalDebuggerInvitation;
#endif

    UFUNCTION(CallInEditor, Category = "Libra Runtime|Debugger")
    void AttachLocalDebugger();

    ELibraRuntimeResult SetNodeParameters(const liberafx::host::ParameterDeltaUpdate* updates,
        std::size_t updateCount);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    bool GetScalar(FGuid nodeId, double& value) const;

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    bool GetVector2(FGuid nodeId, FVector2D& value) const;

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    bool GetVector3(FGuid nodeId, FVector& value) const;

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    bool GetVector4(FGuid nodeId, FVector4& value) const;

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    bool GetLinearColor(FGuid nodeId, FLinearColor& value) const;

    virtual void TickComponent(float deltaTime, ELevelTick tickType,
        FActorComponentTickFunction* tickFunction) override;
    virtual void BeginDestroy() override;

protected:
    virtual void BeginPlay() override;
    virtual void EndPlay(const EEndPlayReason::Type endPlayReason) override;
    virtual void OnComponentDestroyed(bool bDestroyingHierarchy) override;

private:
    bool bSuppressPreviewDebugDraw = false;
    ELibraRuntimeResult PlayPackage(bool bRestart);
    ELibraRuntimeResult EnsureGpuPlayback();
    ELibraRuntimeResult ReportPackageError(ELibraRuntimeResult result, const FString& message);
    void SyncPackageState();
    void ReleasePackagePlayback();
    void ReleaseGpuPlayback();
    UFUNCTION()
    void ForwardHostEvent(const FLibraSpawnableHostEvent& event);
    UFUNCTION()
    void ForwardGameIoEvent(const FLibraGameIoEvent& event);
    UFUNCTION()
    void ForwardRuntimeError(ELibraRuntimeResult result);
    UPROPERTY(Transient)
    TObjectPtr<ULibraSpawnableComponent> PackagePlayback;
    UPROPERTY(Transient)
    TObjectPtr<ULibraGpuEffectRuntime> GpuPlayback;
    bool bStopping = false;
    bool ReadComponents(FGuid nodeId, double (&values)[4], uint8 width) const;
};
