#include "LibraEffectComponent.h"

#include "LibraEffectAsset.h"
#include "LibraGpuEffectAsset.h"
#include "LibraGpuEffectRuntime.h"
#include "LibraNativeState.h"
#include "LibraTaskScheduler.h"
#include "LibraCpuProfiler.h"
#include "LibraExecutionDebugAdapter.h"
#include "LibraQualityContext.h"
#include "EngineGlobals.h"
#include "Engine/World.h"
#include "ProfilingDebugging/CpuProfilerTrace.h"

struct FLibraEffectState final
{
    TSharedPtr<const FCompiledLibraProgram, ESPMode::ThreadSafe> compiled;
    TArray<double, TAlignedHeapAllocator<64>> work;
    TArray<double, TAlignedHeapAllocator<64>> defaults;
    FLibraCpuProfiler profiler;
    liberafx::Instance instance;
    FLibraTaskScheduler scheduler;
    double requestedTime = 0;
    bool pending = false;
#if PLATFORM_WINDOWS
    TUniquePtr<liberafx::host::WindowsExecutionDebugClient> executionDebug;
#endif
    bool signalJob = false;
    liberafx::Job evaluationJob = nullptr;
    void* evaluationData = nullptr;
    liberafx::Result evaluationResult = liberafx::Result::NotReady;

    static bool EnqueueEvaluation(void* context, liberafx::Job job, void* data) noexcept
    {
        auto& state = *static_cast<FLibraEffectState*>(context);
        state.evaluationJob = job;
        state.evaluationData = data;
        auto scheduler = state.scheduler.GetScheduler();
        return scheduler.enqueue(scheduler.context, [](void* payload) noexcept
        {
            auto& item = *static_cast<FLibraEffectState*>(payload);
            item.evaluationJob(item.evaluationData);
            item.evaluationResult = item.instance.result();
            if (item.evaluationResult == liberafx::Result::Ok)
                item.evaluationResult = item.instance.dispatchGeneratedSignals(10000);
        }, &state);
    }
    liberafx::SignalHandle debugSignal;
    size_t debugSignalBudget = 0;
    liberafx::Result debugSignalResult = liberafx::Result::NotReady;

    ~FLibraEffectState() { DetachDebugger(); Wait(); }
    void DetachDebugger()
    {
#if PLATFORM_WINDOWS
        if (executionDebug)
        {
            executionDebug->stop();
            Wait();
            instance.setDebugHooks({});
            executionDebug.Reset();
        }
#endif
    }
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
    auto workCount = workBytes / sizeof(double) + (workBytes % sizeof(double) != 0 ? 1u : 0u);
    auto defaultsCount = compiled->program.parameterValueCount();
    auto workAlignment = compiled->program.workAlignment();
    auto defaultsAlignment = compiled->program.defaultsAlignment();
    if (workAlignment == 0 || workAlignment > 64 || defaultsAlignment == 0 || defaultsAlignment > 64)
        return ELibraRuntimeResult::InvalidArgument;
    if (maximumWorkingBytes <= 0 || workCount > MAX_int32 || defaultsCount > MAX_int32)
        return ELibraRuntimeResult::InsufficientWork;
    auto allocatedWorkBytes = workCount * sizeof(double);
    if (allocatedWorkBytes > static_cast<uint64>(maximumWorkingBytes) ||
        defaultsCount > (static_cast<uint64>(maximumWorkingBytes) - allocatedWorkBytes) / sizeof(double))
        return ELibraRuntimeResult::InsufficientWork;
    auto state = MakeShared<FLibraEffectState, ESPMode::NotThreadSafe>();
    state->compiled = MoveTemp(compiled);
    state->work.SetNumZeroed(static_cast<int32>(workCount));
    state->defaults.SetNumZeroed(static_cast<int32>(defaultsCount));
    if ((state->work.Num() != 0 && reinterpret_cast<UPTRINT>(state->work.GetData()) % workAlignment != 0) ||
        (state->defaults.Num() != 0 && reinterpret_cast<UPTRINT>(state->defaults.GetData()) % defaultsAlignment != 0))
        return ELibraRuntimeResult::InvalidArgument;
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

auto ULibraEffectComponent::AttachExecutionDebugger(const FString& invitation) -> ELibraRuntimeResult
{
    check(IsInGameThread());
#if PLATFORM_WINDOWS
    if (Package) return AttachPackageDebugger(GetPackageDebuggerGeneration(), FGuid{}, 0, invitation);
    auto ready = EnsureState();
    if (ready != ELibraRuntimeResult::Ok) return ready;
    auto& state = *NativeState;
    if (state.pending || state.executionDebug) return ELibraRuntimeResult::Busy;
    auto& bytes = state.compiled->bytecode;
    auto candidate = MakeUnique<liberafx::host::WindowsExecutionDebugClient>(
        liberafx::host::ExecutionDebugProtocol::programIdentity(bytes.GetData(), bytes.Num()));
    auto result = state.instance.setDebugHooks(candidate->hooks());
    if (result != liberafx::Result::Ok) return ToLibraResult(result);
    if (!candidate->start(TCHAR_TO_UTF8(*invitation)))
    {
        state.instance.setDebugHooks({});
        return ELibraRuntimeResult::InvalidArgument;
    }
    state.executionDebug = MoveTemp(candidate);
    return ELibraRuntimeResult::Ok;
#else
    return ELibraRuntimeResult::UnsupportedBinary;
#endif
}

void ULibraEffectComponent::DetachExecutionDebugger()
{
    check(IsInGameThread());
    if (Package && PackagePlayback)
    {
        PackagePlayback->DetachExecutionDebugger();
        SyncPackageState();
        return;
    }
    if (NativeState) NativeState->DetachDebugger();
}

void ULibraEffectComponent::AttachLocalDebugger()
{
#if WITH_EDITORONLY_DATA
    auto invitation = MoveTemp(LocalDebuggerInvitation);
    LocalDebuggerInvitation.Reset();
    LastResult = AttachExecutionDebugger(invitation.TrimStartAndEnd());
#endif
}

void ULibraEffectComponent::BeginPlay()
{
    Super::BeginPlay();
    if (bAutoPlay) Play();
}

void ULibraEffectComponent::EndPlay(const EEndPlayReason::Type endPlayReason)
{
    Stop();
    ReleasePackagePlayback();
    ReleaseGpuPlayback();
    Super::EndPlay(endPlayReason);
}

void ULibraEffectComponent::BeginDestroy()
{
    ReleasePackagePlayback();
    ReleaseGpuPlayback();
    NativeState.Reset();
    Super::BeginDestroy();
}

void ULibraEffectComponent::OnComponentDestroyed(bool bDestroyingHierarchy)
{
    ReleasePackagePlayback();
    ReleaseGpuPlayback();
    NativeState.Reset();
    Super::OnComponentDestroyed(bDestroyingHierarchy);
}

auto ULibraEffectComponent::EnsureState() -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (Package) return ReportPackageError(ELibraRuntimeResult::UnsupportedBinary,
        TEXT("Raw bytecode bindings are unavailable for effect packages; use root/child values and named game I/O."));
    if (GpuEffect) return ELibraRuntimeResult::UnsupportedBinary;
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
    if (bStopping) return ELibraRuntimeResult::Busy;
    TSharedPtr<FLibraEffectState, ESPMode::NotThreadSafe> candidate;
    if (newEffect)
    {
        auto result = CreateLibraState(newEffect->GetCompiledProgram(), MaximumWorkingBytes,
            bProfileNodes, bProfileAccessors, bRecordProfiling, candidate);
        if (result != ELibraRuntimeResult::Ok) return result;
    }
    Stop();
    if (Package && LastResult != ELibraRuntimeResult::Ok) return LastResult;
    Package = nullptr;
    if (PackagePlayback) PackagePlayback->Package = nullptr;
    GpuEffect = nullptr;
    ReleaseGpuPlayback();
    Effect = newEffect;
    NativeState = MoveTemp(candidate);
    LastError.Reset();
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
    if (Package || GpuEffect) return ReportPackageError(ELibraRuntimeResult::UnsupportedBinary,
        TEXT("Per-node recording is currently supported only for standalone CPU bytecode."));
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

bool ULibraEffectComponent::IsEvaluationPending() const
{
    check(IsInGameThread());
    return NativeState && NativeState->pending;
}

auto ULibraEffectComponent::Play(bool bRestart) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (bStopping) return LastResult = ELibraRuntimeResult::Busy;
    if (!IsRegistered()) return LastResult = ELibraRuntimeResult::NotReady;
    if ((Effect && Package) || (Effect && GpuEffect) || (Package && GpuEffect))
    {
        LastError = TEXT("Assign exactly one of Effect, Package, or GpuEffect.");
        return LastResult = ELibraRuntimeResult::InvalidArgument;
    }
    if (GpuEffect)
    {
        if (PackagePlayback && PackagePlayback->Package)
        {
            auto result = PackagePlayback->Stop();
            if (result != ELibraRuntimeResult::Ok) return LastResult = result;
            PackagePlayback->Package = nullptr;
        }
        NativeState.Reset();
        auto result = EnsureGpuPlayback();
        if (result != ELibraRuntimeResult::Ok) return LastResult = result;
        if (bRestart) GpuPlayback->Stop();
        if (!GpuPlayback->Play())
        {
            LastError = GpuPlayback->LastError;
            bIsPlaying = false;
            SetComponentTickEnabled(false);
            return LastResult = ELibraRuntimeResult::UnsupportedBinary;
        }
        LastError.Reset();
        bIsPlaying = true;
        TimeSeconds = LastEvaluatedTimeSeconds = GpuPlayback->GetSimulationTime();
        Activate();
        SetComponentTickEnabled(true);
        return LastResult = ELibraRuntimeResult::Ok;
    }
    if (Package)
    {
        NativeState.Reset();
        return PlayPackage(bRestart);
    }
    if (PackagePlayback && PackagePlayback->Package)
    {
        auto result = PackagePlayback->Stop();
        if (result != ELibraRuntimeResult::Ok) return LastResult = result;
        PackagePlayback->Package = nullptr;
    }
    if (bRestart)
    {
        if (NativeState && NativeState->pending)
        {
            NativeState->DetachDebugger();
            NativeState->Wait();
        }
        TimeSeconds = 0;
    }
    auto result = EnsureState();
    if (result != ELibraRuntimeResult::Ok) return LastResult = result;
    bIsPlaying = true;
    Activate();
    if (NativeState->pending) return LastResult = ELibraRuntimeResult::Ok;
    auto queued = QueueEvaluation(TimeSeconds, GetWorld() ? GetWorld()->GetDeltaSeconds() : 0);
    if (queued != ELibraRuntimeResult::Ok)
    {
        bIsPlaying = false;
        SetComponentTickEnabled(false);
    }
    return queued;
}

void ULibraEffectComponent::Pause()
{
    if (GpuEffect && GpuPlayback)
    {
        GpuPlayback->Pause();
        bIsPlaying = false;
        TimeSeconds = LastEvaluatedTimeSeconds = GpuPlayback->GetSimulationTime();
        SetComponentTickEnabled(false);
        return;
    }
    if (PackagePlayback && Package)
    {
        PackagePlayback->Pause();
        SyncPackageState();
        return;
    }
    bIsPlaying = false;
    SetComponentTickEnabled(NativeState && NativeState->pending);
}

void ULibraEffectComponent::Stop()
{
    check(IsInGameThread());
    if (bStopping) { LastResult = ELibraRuntimeResult::Busy; return; }
    TGuardValue<bool> stopping(bStopping, true);
    if (PackagePlayback)
    {
        auto result = PackagePlayback->Stop();
        SyncPackageState();
        if (result != ELibraRuntimeResult::Ok) { LastResult = result; return; }
    }
    if (GpuPlayback) GpuPlayback->Stop();
    bIsPlaying = false;
    NativeState.Reset();
    TimeSeconds = 0;
    LastEvaluatedTimeSeconds = 0;
    bRootFinished = false;
    LastResult = Package || Effect || GpuEffect
        ? ELibraRuntimeResult::Ok
        : ELibraRuntimeResult::NotReady;
    SetComponentTickEnabled(false);
}

auto ULibraEffectComponent::EvaluateAtTime(double evaluationTimeSeconds) -> ELibraRuntimeResult
{
    if (Package || GpuEffect) return ReportPackageError(ELibraRuntimeResult::UnsupportedBinary,
        TEXT("Package and GPU effect playback are forward-only; use Play, Pause and Tick rather than seeking."));
    if (!IsRegistered()) return LastResult = ELibraRuntimeResult::NotReady;
    if (!FMath::IsFinite(evaluationTimeSeconds)) return LastResult = ELibraRuntimeResult::NonFinite;
    if (NativeState && NativeState->pending) return LastResult = ELibraRuntimeResult::Busy;
    bIsPlaying = false;
    return QueueEvaluation(evaluationTimeSeconds, GetWorld() ? GetWorld()->GetDeltaSeconds() : 0);
}

auto ULibraEffectComponent::QueueEvaluation(double timeSeconds, double frameDelta) -> ELibraRuntimeResult
{
    auto result = EnsureState();
    if (result != ELibraRuntimeResult::Ok) return LastResult = result;
    auto& state = *NativeState;
    if (state.pending) return LastResult = ELibraRuntimeResult::Busy;
    state.requestedTime = timeSeconds;
    state.signalJob = false;
    auto contextResult = timeSeconds >= 0 ? state.instance.setTimeContext(timeSeconds, frameDelta) :
        state.instance.clearTimeContext();
    if (contextResult != liberafx::Result::Ok) return LastResult = ToLibraResult(contextResult);
    auto quality = liberafx::HostQualitySnapshot{};
    auto qualityResult = ReadLibraQualitySnapshot(GFrameCounter, quality);
    if (qualityResult == liberafx::Result::Busy) return LastResult = ToLibraResult(qualityResult);
    qualityResult = state.instance.setHostQuality(quality);
    if (qualityResult != liberafx::Result::Ok) return LastResult = ToLibraResult(qualityResult);
    auto scheduler = liberafx::JobScheduler{&FLibraEffectState::EnqueueEvaluation, &state};
    auto nativeResult = state.instance.updateAsync(timeSeconds, scheduler);
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
    TRACE_CPUPROFILER_EVENT_SCOPE(Libra_EffectComponentTick);
    Super::TickComponent(deltaTime, tickType, tickFunction);
    if (Package && PackagePlayback)
    {
        auto playback = PackagePlayback;
        playback->PlayRate = PlayRate;
        playback->TickComponent(deltaTime, tickType, nullptr);
        if (PackagePlayback == playback)
        {
            SyncPackageState();
            OnEvaluationCompleted.Broadcast(LastResult, LastEvaluatedTimeSeconds);
        }
        return;
    }
    if (GpuEffect && GpuPlayback)
    {
        GpuPlayback->Tick(deltaTime);
        bIsPlaying = GpuPlayback->bIsPlaying;
        TimeSeconds = LastEvaluatedTimeSeconds = GpuPlayback->GetSimulationTime();
        if (!bIsPlaying) SetComponentTickEnabled(false);
        return;
    }
    if (bIsPlaying)
    {
#if PLATFORM_WINDOWS
        // Do not accumulate game/preview time while a debug worker is in flight.
        if (NativeState && NativeState->executionDebug && NativeState->pending &&
            !NativeState->scheduler.IsCompleted()) return;
#endif
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
        auto nativeResult = NativeState->signalJob ? NativeState->debugSignalResult : NativeState->evaluationResult;
        auto result = ToLibraResult(nativeResult);
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
    if (QueueEvaluation(TimeSeconds, static_cast<double>(deltaTime)) != ELibraRuntimeResult::Ok)
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
    return !Package && !GpuEffect && NativeState && Effect &&
        NativeState->compiled == Effect->GetCompiledProgram();
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

auto ULibraEffectComponent::GetValueType(FGuid nodeId, ELibraValueType& valueType) -> ELibraRuntimeResult
{
    valueType = ELibraValueType::Unknown;
    auto handle = FLibraValueHandle{};
    auto result = BindValue(nodeId, handle);
    if (result != ELibraRuntimeResult::Ok) return result;
    auto type = liberafx::ValueType::None;
    result = ToLibraResult(NativeState->compiled->program.valueType(handle.native.nodeIndex, type));
    if (result == ELibraRuntimeResult::Ok) valueType = static_cast<ELibraValueType>(type);
    return result;
}

auto ULibraEffectComponent::ReadBoundInt32Value(const FLibraValueHandle& handle, int32& value) const -> ELibraRuntimeResult
{
    value = 0;
    if (handle.owner.Get() != this || !HasCurrentState()) return ELibraRuntimeResult::InvalidHandle;
    if (NativeState->pending) return ELibraRuntimeResult::Busy;
    return ToLibraResult(NativeState->instance.valueInt32(handle.native, value));
}

auto ULibraEffectComponent::SetBoundInt32Parameter(const FLibraParameterHandle& handle, int32 value) -> ELibraRuntimeResult
{
    if (handle.owner.Get() != this || !HasCurrentState()) return ELibraRuntimeResult::InvalidHandle;
    if (NativeState->pending) return ELibraRuntimeResult::Busy;
    return ToLibraResult(NativeState->instance.setParameterInt32(handle.native, value));
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
#if PLATFORM_WINDOWS
    if (state->executionDebug)
    {
        state->requestedTime = LastEvaluatedTimeSeconds;
        state->debugSignal = handle.native;
        state->debugSignalBudget = static_cast<size_t>(instructionBudget);
        auto scheduler = state->scheduler.GetScheduler();
        auto execute = [](void* data) noexcept
        {
            auto& job = *static_cast<FLibraEffectState*>(data);
            job.debugSignalResult = job.instance.dispatchSignal(job.debugSignal, job.requestedTime, job.debugSignalBudget);
        };
        if (!scheduler.enqueue(scheduler.context, execute, state.Get())) return ELibraRuntimeResult::QueueRejected;
        state->signalJob = state->pending = true;
        SetComponentTickEnabled(true);
        return ELibraRuntimeResult::Ok; // Completion follows OnEvaluationCompleted.
    }
#endif
    return ToLibraResult(state->instance.dispatchSignal(handle.native, LastEvaluatedTimeSeconds,
        static_cast<size_t>(instructionBudget)));
}

auto ULibraEffectComponent::ReadComponents(FGuid nodeId, double (&values)[4], uint8 width) const -> bool
{
    check(IsInGameThread());
    if (Package)
    {
        auto value = FVector4{};
        auto actualWidth = int32{};
        if (ReadRootValue(nodeId, value, actualWidth) != ELibraRuntimeResult::Ok || actualWidth != width) return false;
        for (auto lane = 0; lane < width; ++lane) values[lane] = value[lane];
        return true;
    }
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
