#pragma once

#include "CoreMinimal.h"
#include "Components/ActorComponent.h"
#include "LibraRuntimeTypes.h"
#include "LibraRuntimeHandles.h"
#include "LibraProfileMeasurement.h"
#include "LibraEffectComponent.generated.h"

class ULibraEffectAsset;
struct FLibraEffectState;

UCLASS(ClassGroup = (Effects), meta = (BlueprintSpawnableComponent, DisplayName = "Libra Effect"))
class LIBRARUNTIMEFORUNREAL_API ULibraEffectComponent : public UActorComponent
{
    GENERATED_BODY()

public:
    ULibraEffectComponent();
    virtual ~ULibraEffectComponent() override;

    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Libra Runtime")
    TObjectPtr<ULibraEffectAsset> Effect;

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
    ELibraRuntimeResult Play(bool bRestart = true);

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    void Pause();

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    void Stop();

    UFUNCTION(BlueprintCallable, Category = "Libra Runtime")
    ELibraRuntimeResult EvaluateAtTime(double evaluationTimeSeconds);

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

private:
    ELibraRuntimeResult EnsureState();
    ELibraRuntimeResult QueueEvaluation(double timeSeconds);
    bool ReadComponents(FGuid nodeId, double (&values)[4], uint8 width) const;
    bool HasCurrentState() const;
    TSharedPtr<FLibraEffectState, ESPMode::NotThreadSafe> NativeState;
};
