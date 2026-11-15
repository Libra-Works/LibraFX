#include "LibraEffectComponent.h"

#include "LibraEffectAsset.h"
#include "LibraNativeState.h"
#include "LibraTaskScheduler.h"
#include "LibraCpuProfiler.h"

struct FLibraEffectState final
{
    TSharedPtr<const FCompiledLibraProgram, ESPMode::ThreadSafe> compiled;
    TArray<double> work;
    TArray<double> defaults;
    FLibraCpuProfiler profiler;
    liberafx::Instance instance;
    FLibraTaskScheduler scheduler;
    double requestedTime = 0;
    bool pending = false;

    ~FLibraEffectState() { Wait(); }
    void Wait()
    {
        scheduler.Wait();
        pending = false;
    }

};

namespace {
auto CreateLibraState(TSharedPtr<const FCompiledLibraProgram, ESPMode::ThreadSafe> compiled,
    int64 maximumWorkingBytes, bool profileNodes, bool profileAccessors, bool recordProfiling,
    TSharedPtr<FLibraEffectState, ESPMode::NotThreadSafe>& out) -> ELibraRuntimeResult
{
    if (compiled->result != liberafx::Result::Ok) return ToLibraResult(compiled->result);
    auto workBytes = compiled->program.workBytes();
    auto defaultsCount = compiled->program.parameterValueCount();
    if (maximumWorkingBytes <= 0 || workBytes > static_cast<uint64>(maximumWorkingBytes) ||
        defaultsCount > (static_cast<uint64>(maximumWorkingBytes) - workBytes) / sizeof(double) ||
        workBytes / sizeof(double) > MAX_int32 || defaultsCount > MAX_int32)
        return ELibraRuntimeResult::InsufficientWork;
    auto state = MakeShared<FLibraEffectState, ESPMode::NotThreadSafe>();
    state->compiled = MoveTemp(compiled);
    state->work.SetNumZeroed(static_cast<int32>(workBytes / sizeof(double)));
    state->defaults.SetNumZeroed(static_cast<int32>(defaultsCount));
    state->profiler.Initialize(state->compiled->bytecode, static_cast<uint32>(state->compiled->program.nodeCount()),
        profileNodes, recordProfiling);
    state->instance.setProfiling(state->profiler.GetHooks(profileNodes, profileAccessors));
    auto result = state->instance.initialize(state->compiled->program, state->work.GetData(), state->work.Num(),
        state->defaults.GetData(), state->defaults.Num());
    if (result != liberafx::Result::Ok) return ToLibraResult(result);
    out = MoveTemp(state);
    return ELibraRuntimeResult::Ok;
}
}

ULibraEffectComponent::ULibraEffectComponent()
{
    PrimaryComponentTick.bCanEverTick = true;
    PrimaryComponentTick.bStartWithTickEnabled = false;
    bAutoActivate = true;
}

ULibraEffectComponent::~ULibraEffectComponent() = default;

void ULibraEffectComponent::BeginPlay()
{
    Super::BeginPlay();
    if (bAutoPlay) Play();
}

void ULibraEffectComponent::EndPlay(const EEndPlayReason::Type endPlayReason)
{
    Stop();
    Super::EndPlay(endPlayReason);
}

void ULibraEffectComponent::BeginDestroy()
{
    NativeState.Reset();
    Super::BeginDestroy();
}

auto ULibraEffectComponent::EnsureState() -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!Effect) return ELibraRuntimeResult::InvalidArgument;
    auto compiled = Effect->GetCompiledProgram();
    if (compiled->result != liberafx::Result::Ok) return ToLibraResult(compiled->result);
    if (NativeState && NativeState->compiled == compiled) return ELibraRuntimeResult::Ok;
    if (NativeState && NativeState->pending) return ELibraRuntimeResult::Busy;
    return CreateLibraState(MoveTemp(compiled), MaximumWorkingBytes, bProfileNodes, bProfileAccessors, bRecordProfiling, NativeState);
}

auto ULibraEffectComponent::SetEffect(ULibraEffectAsset* newEffect) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    TSharedPtr<FLibraEffectState, ESPMode::NotThreadSafe> candidate;
    if (newEffect)
    {
        auto result = CreateLibraState(newEffect->GetCompiledProgram(), MaximumWorkingBytes,
            bProfileNodes, bProfileAccessors, bRecordProfiling, candidate);
        if (result != ELibraRuntimeResult::Ok) return result;
    }
    Stop();
    Effect = newEffect;
    NativeState = MoveTemp(candidate);
    LastResult = newEffect ? ELibraRuntimeResult::Ok : ELibraRuntimeResult::InvalidArgument;
    return LastResult;
}

auto ULibraEffectComponent::ConfigureProfiling(bool enableNodeScopes, bool enableAccessorScopes) -> ELibraRuntimeResult
{
    return ConfigureRecording(bRecordProfiling, enableNodeScopes, enableAccessorScopes);
}

auto ULibraEffectComponent::ConfigureRecording(bool enabled, bool enableNodeScopes, bool enableAccessorScopes) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (NativeState)
    {
        if (NativeState->pending) return ELibraRuntimeResult::Busy;
        auto result = NativeState->instance.setProfiling(NativeState->profiler.GetHooks(enableNodeScopes, enableAccessorScopes));
        if (result != liberafx::Result::Ok) return ToLibraResult(result);
        NativeState->profiler.Initialize(NativeState->compiled->bytecode,
            static_cast<uint32>(NativeState->compiled->program.nodeCount()), enableNodeScopes, enabled);
    }
    bProfileNodes = enableNodeScopes;
    bProfileAccessors = enableAccessorScopes;
    bRecordProfiling = enabled;
    return ELibraRuntimeResult::Ok;
}

bool ULibraEffectComponent::CaptureProfiling(TArray<FLibraProfileMeasurement>& measurements, uint64& dropped) const
{
    check(IsInGameThread());
    measurements.Reset();
    dropped = 0;
    return NativeState && NativeState->profiler.Snapshot(measurements, dropped);
}

auto ULibraEffectComponent::Play(bool bRestart) -> ELibraRuntimeResult
{
    if (!IsRegistered()) return LastResult = ELibraRuntimeResult::NotReady;
    if (bRestart)
    {
        if (NativeState) NativeState->Wait();
        TimeSeconds = 0;
    }
    auto result = EnsureState();
    if (result != ELibraRuntimeResult::Ok) return LastResult = result;
    bIsPlaying = true;
    Activate();
    if (NativeState->pending) return LastResult = ELibraRuntimeResult::Ok;
    auto queued = QueueEvaluation(TimeSeconds);
    if (queued != ELibraRuntimeResult::Ok)
    {
        bIsPlaying = false;
        SetComponentTickEnabled(false);
    }
    return queued;
}

void ULibraEffectComponent::Pause()
{
    bIsPlaying = false;
    SetComponentTickEnabled(NativeState && NativeState->pending);
}

void ULibraEffectComponent::Stop()
{
    bIsPlaying = false;
    NativeState.Reset();
    TimeSeconds = 0;
    LastEvaluatedTimeSeconds = 0;
    LastResult = ELibraRuntimeResult::NotReady;
    SetComponentTickEnabled(false);
}

auto ULibraEffectComponent::EvaluateAtTime(double evaluationTimeSeconds) -> ELibraRuntimeResult
{
    if (!IsRegistered()) return LastResult = ELibraRuntimeResult::NotReady;
    if (!FMath::IsFinite(evaluationTimeSeconds)) return LastResult = ELibraRuntimeResult::NonFinite;
    if (NativeState && NativeState->pending) return LastResult = ELibraRuntimeResult::Busy;
    bIsPlaying = false;
    return QueueEvaluation(evaluationTimeSeconds);
}

auto ULibraEffectComponent::QueueEvaluation(double timeSeconds) -> ELibraRuntimeResult
{
    auto result = EnsureState();
    if (result != ELibraRuntimeResult::Ok) return LastResult = result;
    auto& state = *NativeState;
    if (state.pending) return LastResult = ELibraRuntimeResult::Busy;
    state.requestedTime = timeSeconds;
    auto nativeResult = state.instance.updateAsync(timeSeconds, state.scheduler.GetScheduler());
    LastResult = ToLibraResult(nativeResult);
    if (nativeResult == liberafx::Result::Ok)
    {
        state.pending = true;
        TimeSeconds = timeSeconds;
        SetComponentTickEnabled(true);
    }
    return LastResult;
}

void ULibraEffectComponent::TickComponent(float deltaTime, ELevelTick tickType, FActorComponentTickFunction* tickFunction)
{
    Super::TickComponent(deltaTime, tickType, tickFunction);
    if (bIsPlaying)
    {
        auto next = TimeSeconds + static_cast<double>(deltaTime) * PlayRate;
        if (!FMath::IsFinite(next) || !FMath::IsFinite(PlayRate) || PlayRate < 0)
        {
            bIsPlaying = false;
            NativeState.Reset();
            LastResult = ELibraRuntimeResult::NonFinite;
            SetComponentTickEnabled(false);
            OnEvaluationCompleted.Broadcast(LastResult, TimeSeconds);
            return;
        }
        else TimeSeconds = next;
    }
    if (NativeState && NativeState->pending)
    {
        if (!NativeState->scheduler.IsCompleted()) return;
        auto result = ToLibraResult(NativeState->instance.result());
        auto completedTime = NativeState->requestedTime;
        NativeState->Wait();
        if (!Effect || NativeState->compiled != Effect->GetCompiledProgram())
        {
            NativeState.Reset();
            LastResult = ELibraRuntimeResult::NotReady;
        }
        else
        {
            LastResult = result;
            LastEvaluatedTimeSeconds = completedTime;
            if (result != ELibraRuntimeResult::Ok) bIsPlaying = false;
            OnEvaluationCompleted.Broadcast(result, completedTime);
        }
    }
    if (!IsRegistered() || !IsActive()) return;
    if (!bIsPlaying)
    {
        SetComponentTickEnabled(NativeState && NativeState->pending);
        return;
    }
    if (NativeState && NativeState->pending) return;
    if (QueueEvaluation(TimeSeconds) != ELibraRuntimeResult::Ok)
    {
        bIsPlaying = false;
        SetComponentTickEnabled(false);
    }
}

auto ULibraEffectComponent::SetParameterComponent(FGuid nodeId, double value,
    ELibraParameterInput input, int32 component) -> ELibraRuntimeResult
{
    if (!nodeId.IsValid() || component < 0 || component > 3) return ELibraRuntimeResult::InvalidArgument;
    auto result = EnsureState();
    if (result != ELibraRuntimeResult::Ok) return result;
    if (NativeState->pending) return ELibraRuntimeResult::Busy;
    uint8 bytes[16]{};
    LibraNodeIdBytes(nodeId, bytes);
    return ToLibraResult(NativeState->instance.setParameter(bytes, static_cast<uint8>(input),
        static_cast<uint8>(component), value));
}

auto ULibraEffectComponent::HasCurrentState() const -> bool
{
    check(IsInGameThread());
    return NativeState && Effect && NativeState->compiled == Effect->GetCompiledProgram();
}

auto ULibraEffectComponent::BindValue(FGuid nodeId, FLibraValueHandle& handle) -> ELibraRuntimeResult
{
    handle = {};
    if (!nodeId.IsValid()) return ELibraRuntimeResult::InvalidArgument;
    auto result = EnsureState();
    if (result != ELibraRuntimeResult::Ok) return result;
    uint8 bytes[16]{};
    LibraNodeIdBytes(nodeId, bytes);
    result = ToLibraResult(NativeState->instance.bindValue(bytes, handle.native));
    if (result == ELibraRuntimeResult::Ok) { handle.owner = this; handle.NodeId = nodeId; }
    return result;
}

auto ULibraEffectComponent::BindParameter(FGuid nodeId, FLibraParameterHandle& handle,
    ELibraParameterInput input, int32 component) -> ELibraRuntimeResult
{
    handle = {};
    if (!nodeId.IsValid() || component < 0 || component > 3) return ELibraRuntimeResult::InvalidArgument;
    auto result = EnsureState();
    if (result != ELibraRuntimeResult::Ok) return result;
    uint8 bytes[16]{};
    LibraNodeIdBytes(nodeId, bytes);
    result = ToLibraResult(NativeState->instance.bindParameter(bytes, static_cast<uint8>(input),
        static_cast<uint8>(component), handle.native));
    if (result == ELibraRuntimeResult::Ok) { handle.owner = this; handle.NodeId = nodeId; }
    return result;
}

auto ULibraEffectComponent::BindSignal(FGuid nodeId, FLibraSignalHandle& handle) -> ELibraRuntimeResult
{
    handle = {};
    if (!nodeId.IsValid()) return ELibraRuntimeResult::InvalidArgument;
    auto result = EnsureState();
    if (result != ELibraRuntimeResult::Ok) return result;
    uint8 bytes[16]{};
    LibraNodeIdBytes(nodeId, bytes);
    result = ToLibraResult(NativeState->instance.bindSignal(bytes, handle.native));
    if (result == ELibraRuntimeResult::Ok) { handle.owner = this; handle.NodeId = nodeId; }
    return result;
}

auto ULibraEffectComponent::ReadBoundValue(const FLibraValueHandle& handle, FVector4& value,
    int32& componentCount) const -> ELibraRuntimeResult
{
    value = FVector4(0, 0, 0, 0);
    componentCount = 0;
    if (handle.owner.Get() != this || !HasCurrentState()) return ELibraRuntimeResult::InvalidHandle;
    if (NativeState->pending) return ELibraRuntimeResult::Busy;
    double values[4]{};
    auto result = ToLibraResult(NativeState->instance.value(handle.native, values));
    if (result != ELibraRuntimeResult::Ok) return result;
    value = FVector4(values[0], values[1], values[2], values[3]);
    componentCount = handle.native.componentCount;
    return result;
}

auto ULibraEffectComponent::ReadBoundParameter(const FLibraParameterHandle& handle, double& value) const -> ELibraRuntimeResult
{
    value = 0;
    if (handle.owner.Get() != this || !HasCurrentState()) return ELibraRuntimeResult::InvalidHandle;
    if (NativeState->pending) return ELibraRuntimeResult::Busy;
    return ToLibraResult(NativeState->instance.parameter(handle.native, value));
}

auto ULibraEffectComponent::SetBoundParameter(const FLibraParameterHandle& handle, double value) -> ELibraRuntimeResult
{
    if (handle.owner.Get() != this || !HasCurrentState()) return ELibraRuntimeResult::InvalidHandle;
    if (NativeState->pending) return ELibraRuntimeResult::Busy;
    return ToLibraResult(NativeState->instance.setParameter(handle.native, value));
}

auto ULibraEffectComponent::DispatchSignal(const FLibraSignalHandle& handle, int32 instructionBudget) -> ELibraRuntimeResult
{
    if (handle.owner.Get() != this || !HasCurrentState()) return ELibraRuntimeResult::InvalidHandle;
    if (instructionBudget <= 0) return ELibraRuntimeResult::InvalidArgument;
    auto state = NativeState;
    if (state->pending) return ELibraRuntimeResult::Busy;
    return ToLibraResult(state->instance.dispatchSignal(handle.native, LastEvaluatedTimeSeconds,
        static_cast<size_t>(instructionBudget)));
}

auto ULibraEffectComponent::ReadComponents(FGuid nodeId, double (&values)[4], uint8 width) const -> bool
{
    check(IsInGameThread());
    if (!NativeState || NativeState->pending || !nodeId.IsValid() || !Effect ||
        NativeState->compiled != Effect->GetCompiledProgram()) return false;
    uint8 bytes[16]{};
    LibraNodeIdBytes(nodeId, bytes);
    auto actualWidth = uint8{};
    return NativeState->instance.valueComponents(bytes, values, actualWidth) == liberafx::Result::Ok && actualWidth == width;
}

auto ULibraEffectComponent::GetScalar(FGuid nodeId, double& value) const -> bool
{
    value = 0;
    double values[4]{};
    if (!ReadComponents(nodeId, values, 1)) return false;
    value = values[0];
    return true;
}

auto ULibraEffectComponent::GetVector2(FGuid nodeId, FVector2D& value) const -> bool
{
    value = FVector2D::ZeroVector;
    double values[4]{};
    if (!ReadComponents(nodeId, values, 2)) return false;
    value = FVector2D(values[0], values[1]);
    return true;
}

auto ULibraEffectComponent::GetVector3(FGuid nodeId, FVector& value) const -> bool
{
    value = FVector::ZeroVector;
    double values[4]{};
    if (!ReadComponents(nodeId, values, 3)) return false;
    value = FVector(values[0], values[1], values[2]);
    return true;
}

auto ULibraEffectComponent::GetVector4(FGuid nodeId, FVector4& value) const -> bool
{
    value = FVector4(0, 0, 0, 0);
    double values[4]{};
    if (!ReadComponents(nodeId, values, 4)) return false;
    value = FVector4(values[0], values[1], values[2], values[3]);
    return true;
}

auto ULibraEffectComponent::GetLinearColor(FGuid nodeId, FLinearColor& value) const -> bool
{
    value = FLinearColor::Transparent;
    double values[4]{};
    if (!ReadComponents(nodeId, values, 4)) return false;
    for (auto component : values)
        if (FMath::Abs(component) > TNumericLimits<float>::Max()) return false;
    value = FLinearColor(static_cast<float>(values[0]), static_cast<float>(values[1]),
        static_cast<float>(values[2]), static_cast<float>(values[3]));
    return true;
}
