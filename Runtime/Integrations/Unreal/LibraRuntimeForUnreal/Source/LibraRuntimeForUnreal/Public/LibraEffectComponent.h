#pragma once

#include "CoreMinimal.h"
#include "Components/ActorComponent.h"
#include "LibraRuntimeTypes.h"
#include "LibraRuntimeHandles.h"
#include "LibraProfileMeasurement.h"
#include "LibraSpawnableComponent.h"
#include "LibraEffectComponent.generated.h"

class ULibraEffectAsset;
class ULibraGpuEffectAsset;
class ULibraGpuEffectRuntime;
struct FLibraEffectState;

UCLASS(ClassGroup = (Effects), meta = (BlueprintSpawnableComponent, DisplayName = "Libra Effect"))
class LIBRARUNTIMEFORUNREAL_API ULibraEffectComponent : public UActorComponent
{
    GENERATED_BODY()

public:
    void ClearPreviewDebugDraw();
    ULibraEffectComponent();
    virtual ~ULibraEffectComponent() override;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime", meta = (EditCondition = "Package == nullptr && GpuEffect == nullptr"))
    TObjectPtr<ULibraEffectAsset> Effect;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime", meta = (EditCondition = "Effect == nullptr && GpuEffect == nullptr"))
    TObjectPtr<ULibraSpawnableAsset> Package;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime", meta = (EditCondition = "Effect == nullptr && Package == nullptr"))
    TObjectPtr<ULibraGpuEffectAsset> GpuEffect;

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

    UPROPERTY(EditAnywhere, Category = "Libra Runtime", meta = (ClampMin = "1024"))
    int64 MaximumWorkingBytes = 16 * 1024 * 1024;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime|Profiling")
    bool bProfileNodes = false;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime|Profiling")
    bool bProfileAccessors = false;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime|Profiling")
    bool bRecordProfiling = false;

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Profiling")
    ELibraRuntimeResult ConfigureProfiling(bool enableNodeScopes, bool enableAccessorScopes);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Profiling")
    ELibraRuntimeResult ConfigureRecording(bool enabled, bool enableNodeScopes = true, bool enableAccessorScopes = false);

    bool CaptureProfiling(TArray<FLibraProfileMeasurement>& measurements, uint64& dropped) const;
    bool IsEvaluationPending() const;

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
    ELibraRuntimeResult SetEffect(ULibraEffectAsset* newEffect);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult SetPackage(ULibraSpawnableAsset* newPackage);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult SetGpuEffect(ULibraGpuEffectAsset* newEffect);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult RequestNaturalEnd();

    // Package path: no author-added internal Spawnable component is required.
    // Plain bytecode evaluation has no scoped lifecycle and is rejected.
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
    ELibraRuntimeResult AttachPackageDebugger(int64 generation, FGuid containerId, int64 instanceId, const FString& invitation);

    UFUNCTION(BlueprintCallable, CallInEditor, Category = "Libra Runtime|Debugger")
    void DetachExecutionDebugger();

#if WITH_EDITORONLY_DATA
    // Never serialized or placed in the editor transaction history.
    UPROPERTY(EditInstanceOnly, Transient, NonTransactional, Category = "Libra Runtime|Debugger")
    FString LocalDebuggerInvitation;
#endif

    UFUNCTION(CallInEditor, Category = "Libra Runtime|Debugger")
    void AttachLocalDebugger();

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult SetParameterComponent(FGuid nodeId, double value,
        ELibraParameterInput input = ELibraParameterInput::A, int32 component = 0);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Binding")
    ELibraRuntimeResult BindValue(FGuid nodeId, FLibraValueHandle& handle);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Binding")
    ELibraRuntimeResult BindParameter(FGuid nodeId, FLibraParameterHandle& handle,
        ELibraParameterInput input = ELibraParameterInput::A, int32 component = 0);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Binding")
    ELibraRuntimeResult BindSignal(FGuid nodeId, FLibraSignalHandle& handle);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Binding")
    ELibraRuntimeResult ReadBoundValue(const FLibraValueHandle& handle, FVector4& value, int32& componentCount) const;

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Binding")
    ELibraRuntimeResult GetValueType(FGuid nodeId, ELibraValueType& valueType);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Binding")
    ELibraRuntimeResult ReadBoundInt32Value(const FLibraValueHandle& handle, int32& value) const;

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Binding")
    ELibraRuntimeResult SetBoundInt32Parameter(const FLibraParameterHandle& handle, int32 value);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Binding")
    ELibraRuntimeResult SetBoundParameter(const FLibraParameterHandle& handle, double value);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Binding")
    ELibraRuntimeResult ReadBoundParameter(const FLibraParameterHandle& handle, double& value) const;

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime|Signals")
    ELibraRuntimeResult DispatchSignal(const FLibraSignalHandle& handle, int32 instructionBudget = 10000);

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

    virtual void TickComponent(float deltaTime, ELevelTick tickType, FActorComponentTickFunction* tickFunction) override;
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
    ELibraRuntimeResult EnsureState();
    ELibraRuntimeResult QueueEvaluation(double timeSeconds, double frameDelta);
    bool ReadComponents(FGuid nodeId, double (&values)[4], uint8 width) const;
    bool HasCurrentState() const;
    TSharedPtr<FLibraEffectState, ESPMode::NotThreadSafe> NativeState;
};
