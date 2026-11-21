#include "LibraSpawnableComponent.h"
#include "LibraProceduralMaterial.h"
#include "Materials/MaterialInterface.h"
#include "LibraExecutionDebugAdapter.h"
#include "LibraTaskScheduler.h"
#include "LibraSharedEffectWorld.h"
#include "liberafx/host/LbSpawnableDebugJob.h"

#include "LibraNativeState.h"
#include "LibraSpawnableAsset.h"
#include "LibraSpawnableRenderState.h"
#include "LibraSkeletalPoseRuntime.h"
#include "LibraQualityContext.h"
#include "Camera/CameraComponent.h"
#include "Camera/PlayerCameraManager.h"
#include "Engine/World.h"
#include "EngineGlobals.h"
#include "GameFramework/PlayerController.h"
#include "liberafx/LbSpawnableHost.h"
#include "liberafx/LbSpawnablePackage.h"
#include "liberafx/LbCpuParticles.h"
#include "Misc/ScopeExit.h"
#include "ProfilingDebugging/CpuProfilerTrace.h"

#include <array>
#include <memory>

namespace {
auto PublishLibraQuality(liberafx::SpawnableHost& host) -> liberafx::Result
{
    auto snapshot = liberafx::HostQualitySnapshot{};
    auto result = ReadLibraQualitySnapshot(GFrameCounter, snapshot);
    if (result == liberafx::Result::Busy) return result;
    return host.setHostQuality(snapshot);
}

auto PublishLibraFrameInputs(liberafx::SpawnableHost& host, const ULibraSpawnableComponent& effect)
    -> liberafx::Result
{
    constexpr auto portName = "__LFX_CAMERA_V1";
    for (const auto& port : host.package().gameIoPorts)
    {
        if (port.name != portName || port.kind != static_cast<uint8>(liberafx::NamedIoKind::InputProperty))
        {
            continue;
        }
        auto position = FVector::ZeroVector;
        auto valid = false;
        if (auto* camera = effect.EffectCameraOverride.Get(); IsValid(camera))
        {
            position = camera->GetComponentLocation();
            valid = true;
        }
        else if (auto* world = effect.GetWorld(); world && world->IsGameWorld())
        {
            auto* owner = effect.GetOwner();
            auto* player = Cast<APlayerController>(owner);
            if (!player && owner)
            {
                player = Cast<APlayerController>(owner->GetInstigatorController());
            }
            if (!player || !player->IsLocalController())
            {
                player = nullptr;
                for (auto iter = world->GetPlayerControllerIterator(); iter; ++iter)
                {
                    if (auto* candidate = iter->Get(); candidate && candidate->IsLocalController())
                    {
                        player = candidate;
                        break;
                    }
                }
            }
            if (player && player->PlayerCameraManager && player->GetViewTarget())
            {
                position = player->PlayerCameraManager->GetCameraLocation();
                valid = true;
            }
        }
        auto values = std::array<double, 4>{position.X, position.Y, position.Z, valid ? 1.0 : 0.0};
        const auto converted = liberafx::convertCoordinates(host.coordinateBasis(), liberafx::SpatialValueKind::Position,
            false, values.data(), 3, values.data());
        if (converted != liberafx::Result::Ok) return converted;
        auto result = host.setGameProperty(portName, values.data(), values.size());
        if (result != liberafx::Result::Ok)
        {
            return result;
        }
        break;
    }
    return PublishLibraQuality(host);
}

auto LibraGuid(const uint8 bytes[16]) -> FGuid
{
    auto words = std::array<uint32, 4>{};
    for (auto index = 0; index < 4; ++index)
        for (auto byte = 0; byte < 4; ++byte)
            words[index] = (words[index] << 8) | bytes[index * 4 + byte];
    return FGuid(words[0], words[1], words[2], words[3]);
}

auto LibraIoEventKind(liberafx::NamedIoKind kind) -> ELibraGameIoEventKind
{
    return static_cast<ELibraGameIoEventKind>(kind);
}
}

ULibraSpawnableComponent::ULibraSpawnableComponent()
{
    PrimaryComponentTick.bCanEverTick = true;
    PrimaryComponentTick.bStartWithTickEnabled = false;
    bAutoActivate = true;
    for (const auto* blend : {TEXT("Alpha"), TEXT("Additive")})
    {
        for (const auto* side : {TEXT("Double"), TEXT("Single")})
        {
            const auto path = FString::Printf(TEXT("/LibraRuntimeForUnreal/Materials/M_Procedural_%s_%s.M_Procedural_%s_%s"), blend, side, blend, side);
            if (auto* material = LoadObject<UMaterialInterface>(nullptr, *path, nullptr, LOAD_NoWarn))
            {
                ProceduralMaterialMasters.Add(material);
            }
        }
    }
}

bool ULibraSpawnableComponent::EnsureProceduralMaterialMasters(FString& error)
{
    auto* defaults = GetMutableDefault<ULibraSpawnableComponent>();
    for (auto blend = 0; blend < 2; ++blend)
    {
        for (auto side = 0; side < 2; ++side)
        {
            auto* material = LibraProceduralMaterial::Resolve(blend != 0, side == 0, error);
            if (!material)
            {
                return false;
            }
            defaults->ProceduralMaterialMasters.AddUnique(material);
        }
    }
    return true;
}

class FLibraSpawnableDebugState final
{
public:
#if PLATFORM_WINDOWS
    FLibraSpawnableDebugState(liberafx::SpawnableHost& owner, liberafx::SpawnableDebugTarget selected, uint64 identity)
        : host(owner), target(selected), job(owner),
          client(identity, selected.hostGeneration, selected.scope.containerId, selected.scope.instanceId) {}
    ~FLibraSpawnableDebugState()
    {
        client.stop();
        scheduler.Wait();
        if (hooked) host.setDebugHooks(target, {});
    }
    auto Submit(liberafx::host::SpawnableDebugJob::Request request) -> liberafx::Result
    {
        auto result = job.submit(request, scheduler.GetScheduler());
        if (result == liberafx::Result::Ok) last = request;
        return result;
    }
    liberafx::SpawnableHost& host;
    liberafx::SpawnableDebugTarget target;
    liberafx::host::SpawnableDebugJob job;
    liberafx::host::WindowsExecutionDebugClient client;
    FLibraTaskScheduler scheduler;
    liberafx::host::SpawnableDebugJob::Request last{};
    int32 attempts = 0;
    bool hooked = false;
#endif
};

void FLibraSpawnableHostDeleter::operator()(liberafx::SpawnableHost* value) const
{
    if (Pool) { Pool->release({LeaseSlot,LeaseGeneration}); }
    else { delete value; }
}

void FLibraSpawnableRenderStateDeleter::operator()(FLibraSpawnableRenderState* value) const
{
    delete value;
}

void FLibraSpawnableDebugStateDeleter::operator()(FLibraSpawnableDebugState* value) const
{
    delete value;
}

void FLibraSkeletalPoseRuntimeDeleter::operator()(FLibraSkeletalPoseRuntime* value) const
{
    delete value;
}

ULibraSpawnableComponent::~ULibraSpawnableComponent() = default;

int64 ULibraSpawnableComponent::GetDebuggerGeneration() const
{
    check(IsInGameThread());
    return NativeHost && !DebugJobPending() ? static_cast<int64>(NativeHost->debugGeneration()) : 0;
}

auto ULibraSpawnableComponent::AttachExecutionDebugger(int64 generation, FGuid containerId,
    int64 instanceId, const FString& invitation) -> ELibraRuntimeResult
{
    check(IsInGameThread());
#if PLATFORM_WINDOWS
    if (!NativeHost) return ELibraRuntimeResult::NotReady;
    if (bSourceInitialAdvancePending) return ELibraRuntimeResult::Busy;
    if (DebugState || EventDrainDepth) return ELibraRuntimeResult::Busy;
    if (generation <= 0 || static_cast<uint64>(generation) != NativeHost->debugGeneration())
        return ELibraRuntimeResult::InvalidHandle;
    if (instanceId < 0 || containerId.IsValid() != (instanceId > 0)) return ELibraRuntimeResult::InvalidArgument;
    auto scope = liberafx::SpawnableScope{};
    if (containerId.IsValid())
    {
        scope.kind = liberafx::SpawnableScopeKind::Instance;
        LibraNodeIdBytes(containerId, scope.containerId);
        scope.instanceId = static_cast<uint64>(instanceId);
    }
    auto target = liberafx::SpawnableDebugTarget{};
    auto result = NativeHost->debugTarget(scope, target);
    if (result != liberafx::Result::Ok) return ToLibraResult(result);
    auto candidate = FLibraSpawnableDebugStatePtr(
        new FLibraSpawnableDebugState(*NativeHost, target, DebugPackageIdentity));
    result = NativeHost->setDebugHooks(target, candidate->client.hooks());
    if (result != liberafx::Result::Ok) return ToLibraResult(result);
    candidate->hooked = true;
    if (!candidate->client.start(TCHAR_TO_UTF8(*invitation))) return ELibraRuntimeResult::InvalidArgument;
    DebugState = MoveTemp(candidate);
    SetComponentTickEnabled(true);
    return ELibraRuntimeResult::Ok;
#else
    return ELibraRuntimeResult::UnsupportedBinary;
#endif
}

void ULibraSpawnableComponent::AttachLocalDebugger()
{
#if WITH_EDITORONLY_DATA
    auto invitation = MoveTemp(LocalDebuggerInvitation);
    LocalDebuggerInvitation.Reset();
    Report(AttachExecutionDebugger(GetDebuggerGeneration(), DebugContainerId, DebugInstanceId, invitation.TrimStartAndEnd()));
#endif
}

bool ULibraSpawnableComponent::DebugJobPending() const
{
#if PLATFORM_WINDOWS
    return DebugState && DebugState->job.pending();
#else
    return false;
#endif
}

void ULibraSpawnableComponent::ReleaseDebugger()
{
#if PLATFORM_WINDOWS
    if (DebugJobPending() && DebugState->last.operation == liberafx::host::SpawnableDebugOperation::Advance)
        RealTimeSeconds = DebugState->last.time;
#endif
    DebugState.Reset(); // Disconnect, then join, then remove hooks.
    if (NativeHost) TimeSeconds = NativeHost->timeSnapshot().effectTime;
}

void ULibraSpawnableComponent::DetachExecutionDebugger()
{
    check(IsInGameThread());
    if (!DebugState) return;
    auto generation = HostGeneration;
    ReleaseDebugger();
    DrainEvents();
    if (HostGeneration == generation) RefreshRendering();
    if (HostGeneration == generation && !bIsPlaying) SetComponentTickEnabled(false);
}

void ULibraSpawnableComponent::BeginPlay()
{
    Super::BeginPlay();
    if (bUsePreparedHosts) { PrewarmEffect(); }
    if (bAutoPlay) Play();
}

void ULibraSpawnableComponent::EndPlay(const EEndPlayReason::Type endPlayReason)
{
    Stop();
    Super::EndPlay(endPlayReason);
}

void ULibraSpawnableComponent::BeginDestroy()
{
    ReleaseHost();
    Super::BeginDestroy();
}

void ULibraSpawnableComponent::OnComponentDestroyed(bool bDestroyingHierarchy)
{
    ReleaseHost();
    Super::OnComponentDestroyed(bDestroyingHierarchy);
}

auto ULibraSpawnableComponent::Play(bool bRestart) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    const auto coordinates = liberafx::CoordinateBasis{{EffectAxes.X, EffectAxes.Y, EffectAxes.Z}, HostUnitsPerMetre};
    if (!liberafx::validCoordinateBasis(coordinates)) return Report(ELibraRuntimeResult::InvalidArgument);
    if (!IsRegistered()) return Report(ELibraRuntimeResult::NotReady);
    if (!Package) return Report(ELibraRuntimeResult::InvalidArgument);
    if (bStopping) return Report(ELibraRuntimeResult::Busy);
    if (NativeHost && !bRestart)
    {
        if (bRootFinished) return Report(ELibraRuntimeResult::NotReady);
        bIsPlaying = true;
        SetComponentTickEnabled(true);
        return ELibraRuntimeResult::Ok;
    }
    if (NativeHost)
    {
        auto stopResult = Stop();
        if (stopResult != ELibraRuntimeResult::Ok) return stopResult;
    }

    auto package = liberafx::SpawnablePackage{};
    auto packageBytes = Package->GetPackageBytes();
    auto result = liberafx::loadSpawnablePackage(packageBytes.GetData(), packageBytes.Num(), package);
    if (result != liberafx::Result::Ok) return Report(ToLibraResult(result));
    LastError.Reset();
    auto rendering = FLibraSpawnableRenderStatePtr(new FLibraSpawnableRenderState(*this));
    if (!rendering->Prepare(package)) return Report(ELibraRuntimeResult::UnsupportedBinary);
    auto options = liberafx::SpawnableHostOptions{};
    options.maxActivePerTemplate = static_cast<std::size_t>(FMath::Max(1, MaxActivePerTemplate));
    options.memoryBudgetBytesPerTemplate = static_cast<std::size_t>(FMath::Max<int64>(1024, MemoryBudgetBytesPerTemplate));
    options.eventCapacity = static_cast<std::size_t>(FMath::Max(1, EventCapacity));
    options.instructionBudget = static_cast<std::size_t>(FMath::Max(1, InstructionBudget));
    auto host = liberafx::host::PlaybackHost{};
    const auto registry = LibraNativeNodeSnapshot();
    const auto projectNodes = registry->ProjectDescriptors();
    auto preparedOptions = liberafx::SpawnableHostPoolOptions{};
    preparedOptions.capacity=static_cast<std::size_t>(FMath::Clamp(PreparedHostCapacity,1,1024));
    preparedOptions.preparationWorkers=static_cast<std::size_t>(FMath::Clamp(PreparationWorkers,1,16));
    preparedOptions.host=options;
    result = bUsePreparedHosts && projectNodes.IsEmpty()
        ? LibraSharedEffects::Acquire(GetWorld(),packageBytes.GetData(),packageBytes.Num(),preparedOptions,host)
        : liberafx::Result::NotReady;
    if (result==liberafx::Result::NotReady) {
        auto ordinary=std::unique_ptr<liberafx::SpawnableHost>{};
        result=liberafx::SpawnableHost::create(MoveTemp(package),options,projectNodes.GetData(),projectNodes.Num(),ordinary);
        if (result==liberafx::Result::Ok) { host.reset(ordinary.release()); }
    }
    if (result == liberafx::Result::Ok) result = host->setCoordinateBasis(coordinates);
    if (result == liberafx::Result::Ok) result = host->setTimeScale(PlayRate);
    if (result != liberafx::Result::Ok) return Report(ToLibraResult(result));
    NativeHost.Reset(host.release());
    NativeHost.GetDeleter().Pool=host.get_deleter().pool;
    NativeHost.GetDeleter().LeaseSlot=host.get_deleter().lease.slot;
    NativeHost.GetDeleter().LeaseGeneration=host.get_deleter().lease.generation;
#if PLATFORM_WINDOWS
    DebugPackageIdentity = liberafx::host::ExecutionDebugProtocol::programIdentity(packageBytes.GetData(), packageBytes.Num());
#endif
    ++HostGeneration;
    RenderState = MoveTemp(rendering);
    SkeletalPoseState = FLibraSkeletalPoseRuntimePtr(new FLibraSkeletalPoseRuntime());
    if (!SkeletalPoseState->Initialize(*NativeHost, *this))
    {
        ReleaseHost();
        return Report(ELibraRuntimeResult::InvalidArgument);
    }
    bSourceInitialAdvancePending = SkeletalPoseState->HasSources();
    if (bSourceInitialAdvancePending || !NativeHost->package().cpuPrograms.empty())
    {
        PrimaryComponentTick.TickGroup = TG_PostUpdateWork;
    }
    if (!RenderState->Spawn(FGuid{}, 0, 0, 0))
    {
        ReleaseHost();
        return Report(ELibraRuntimeResult::UnsupportedBinary);
    }
    TimeSeconds = 0;
    bRootFinished = false;
    RealTimeSeconds = 0;
    bForceTerminated = false;
    CancelledBatchScopes.Reset();
    bIsPlaying = true;
    SetComponentTickEnabled(true);

    auto* current = NativeHost.Get();
    auto generation = HostGeneration;
    if (bSourceInitialAdvancePending)
    {
        LastResult = ELibraRuntimeResult::Ok;
        return LastResult;
    }
    result = PublishLibraFrameInputs(*current, *this);
    if (result == liberafx::Result::Ok) result = current->advance(0, 0);
    DrainEvents();
    if (HostGeneration != generation) return LastResult;
    if (bForceTerminated) return Report(ELibraRuntimeResult::Ok);
    for (auto attempt = 0; result == liberafx::Result::QueueRejected && attempt < 16; ++attempt)
    {
        result = current->advance(0, 0);
        DrainEvents();
        if (HostGeneration != generation) return LastResult;
        if (bForceTerminated) return Report(ELibraRuntimeResult::Ok);
    }
    if (result != liberafx::Result::Ok)
    {
        if (result != liberafx::Result::QueueRejected)
        {
            ReleaseHost();
        }
        return Report(ToLibraResult(result));
    }
    if (!RefreshRendering()) return LastResult;
    LastResult = ELibraRuntimeResult::Ok;
    return LastResult;
}

void ULibraSpawnableComponent::Pause()
{
    check(IsInGameThread());
    bIsPlaying = false;
    SetComponentTickEnabled(DebugJobPending());
}

auto ULibraSpawnableComponent::RequestNaturalEnd() -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!NativeHost) return Report(ELibraRuntimeResult::NotReady);
#if PLATFORM_WINDOWS
    if (DebugState) {
        if (EventDrainDepth) return ELibraRuntimeResult::Busy;
        auto request = liberafx::host::SpawnableDebugJob::Request{};
        request.operation = liberafx::host::SpawnableDebugOperation::NaturalEnd;
        auto result = DebugState->Submit(request);
        if (result == liberafx::Result::Ok) SetComponentTickEnabled(true);
        return ToLibraResult(result);
    }
#endif
    auto generation = HostGeneration;
    auto result = NativeHost->requestNaturalEnd();
    DrainEvents();
    if (generation != HostGeneration || !RefreshRendering()) return LastResult;
    return Report(ToLibraResult(result));
}

auto ULibraSpawnableComponent::ForceTerminate() -> ELibraRuntimeResult
{
    return ForceTerminateScope(FGuid{}, 0, true);
}

auto ULibraSpawnableComponent::ForceTerminateChild(FGuid containerId, int64 instanceId) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!containerId.IsValid() || instanceId == 0) return Report(ELibraRuntimeResult::InvalidArgument);
    return ForceTerminateScope(containerId, instanceId, false);
}

auto ULibraSpawnableComponent::ForceTerminateScope(FGuid containerId, int64 instanceId,
    bool wholeEffect) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!NativeHost) return Report(ELibraRuntimeResult::NotReady);
    if (DebugJobPending()) return ELibraRuntimeResult::Busy;
    auto scope = liberafx::SpawnableScope{};
    if (!wholeEffect)
    {
        scope.kind = liberafx::SpawnableScopeKind::Instance;
        LibraNodeIdBytes(containerId, scope.containerId);
        scope.instanceId = static_cast<liberafx::GeneratorInstanceId>(instanceId);
    }
    auto generation = HostGeneration;
    auto result = liberafx::Result::QueueRejected;
    for (auto attempt = 0; attempt < 16; ++attempt)
    {
        result = NativeHost->forceTerminate(scope);
        if (result == liberafx::Result::Ok)
        {
            // A reentrant drain may already have popped a retired target's
            // notice. Duplicate native force need not emit another event, but
            // its adapter snapshot must still be retired now.
            if (wholeEffect)
            {
                bForceTerminated = bRootFinished = true;
                bIsPlaying = false;
                SetComponentTickEnabled(false);
                RenderState.Reset();
            }
            else
            {
                CancelledBatchScopes.FindOrAdd(containerId).Add(instanceId);
                if (RenderState) RenderState->Stop(containerId, instanceId);
            }
        }
        if (HostGeneration != generation) return LastResult;
        DrainEvents(); // releases render/GPU state before broadcasting kind 5
        if (HostGeneration != generation) return LastResult;
        if (result != liberafx::Result::QueueRejected) break;
    }
    return Report(ToLibraResult(result));
}

auto ULibraSpawnableComponent::NotifyEndRequest(FGuid userSignalNodeId) -> ELibraRuntimeResult
{
    return NotifyEndRequestScope(FGuid{}, 0, userSignalNodeId, true);
}

auto ULibraSpawnableComponent::NotifyChildEndRequest(FGuid containerId, int64 instanceId,
    FGuid userSignalNodeId) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!containerId.IsValid() || instanceId == 0) return Report(ELibraRuntimeResult::InvalidArgument);
    return NotifyEndRequestScope(containerId, instanceId, userSignalNodeId, false);
}

auto ULibraSpawnableComponent::NotifyEndRequestScope(FGuid containerId, int64 instanceId,
    FGuid userSignalNodeId, bool wholeEffect) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!NativeHost) return Report(ELibraRuntimeResult::NotReady);
    if (!userSignalNodeId.IsValid()) return Report(ELibraRuntimeResult::InvalidArgument);
    auto scope = liberafx::SpawnableScope{};
    if (!wholeEffect)
    {
        scope.kind = liberafx::SpawnableScopeKind::Instance;
        LibraNodeIdBytes(containerId, scope.containerId);
        scope.instanceId = static_cast<liberafx::GeneratorInstanceId>(instanceId);
    }
    uint8 nodeBytes[16]{};
    LibraNodeIdBytes(userSignalNodeId, nodeBytes);
#if PLATFORM_WINDOWS
    if (DebugState) {
        if (EventDrainDepth) return ELibraRuntimeResult::Busy;
        auto request = liberafx::host::SpawnableDebugJob::Request{};
        request.operation = liberafx::host::SpawnableDebugOperation::NotifyEnd;
        request.scope = scope;
        FMemory::Memcpy(request.node.data(), nodeBytes, 16);
        auto result = DebugState->Submit(request);
        if (result == liberafx::Result::Ok) SetComponentTickEnabled(true);
        return ToLibraResult(result);
    }
#endif
    auto generation = HostGeneration;
    auto result = NativeHost->notifyEndRequest(scope, nodeBytes);
    DrainEvents();
    if (generation != HostGeneration || !RefreshRendering()) return LastResult;
    return Report(ToLibraResult(result));
}

auto ULibraSpawnableComponent::Stop() -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!NativeHost)
    {
        bIsPlaying = false;
        SetComponentTickEnabled(false);
        return ELibraRuntimeResult::Ok;
    }
    if (bStopping) return ELibraRuntimeResult::Busy;
    TGuardValue<bool> stopping(bStopping, true);
    ReleaseDebugger();
    if (bForceTerminated)
    {
        ReleaseHost();
        bForceTerminated = bRootFinished = false;
        TimeSeconds = 0;
        RealTimeSeconds = 0;
        return Report(ELibraRuntimeResult::Ok);
    }
    auto* current = NativeHost.Get();
    auto generation = HostGeneration;
    auto result = liberafx::Result::QueueRejected;
    for (auto attempt = 0; result == liberafx::Result::QueueRejected && attempt < 16; ++attempt)
    {
        result = current->stop();
        DrainEvents();
        if (HostGeneration != generation) return LastResult;
    }
    if (result != liberafx::Result::Ok) return Report(ToLibraResult(result));
    ReleaseHost();
    bIsPlaying = false;
    bRootFinished = false;
    TimeSeconds = 0;
    RealTimeSeconds = 0;
    SetComponentTickEnabled(false);
    LastResult = ELibraRuntimeResult::Ok;
    return LastResult;
}

auto ULibraSpawnableComponent::DispatchSignal(FGuid nodeId) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!NativeHost) return Report(ELibraRuntimeResult::NotReady);
    if (!nodeId.IsValid()) return Report(ELibraRuntimeResult::InvalidArgument);
    auto generation = HostGeneration;
    uint8 bytes[16]{};
    LibraNodeIdBytes(nodeId, bytes);
#if PLATFORM_WINDOWS
    if (DebugState) {
        if (EventDrainDepth) return ELibraRuntimeResult::Busy;
        auto request = liberafx::host::SpawnableDebugJob::Request{};
        request.operation = liberafx::host::SpawnableDebugOperation::Signal;
        FMemory::Memcpy(request.node.data(), bytes, 16);
        auto result = DebugState->Submit(request);
        if (result == liberafx::Result::Ok) SetComponentTickEnabled(true);
        return ToLibraResult(result);
    }
#endif
    auto result = NativeHost->dispatchSignal(bytes);
    DrainEvents();
    if (generation != HostGeneration || !RefreshRendering()) return LastResult;
    return Report(ToLibraResult(result));
}

auto ULibraSpawnableComponent::FireGameSignal(const FString& name) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!NativeHost) return Report(ELibraRuntimeResult::NotReady);
    if (name.IsEmpty()) return Report(ELibraRuntimeResult::InvalidArgument);
    auto generation = HostGeneration;
    auto utf8Name = FTCHARToUTF8(*name);
#if PLATFORM_WINDOWS
    if (DebugState) {
        if (EventDrainDepth) return ELibraRuntimeResult::Busy;
        auto request = liberafx::host::SpawnableDebugJob::Request{};
        if (static_cast<size_t>(utf8Name.Length()) >= request.name.size()) return ELibraRuntimeResult::InvalidArgument;
        request.operation = liberafx::host::SpawnableDebugOperation::GameSignal;
        FMemory::Memcpy(request.name.data(), utf8Name.Get(), utf8Name.Length());
        auto result = DebugState->Submit(request);
        if (result == liberafx::Result::Ok) SetComponentTickEnabled(true);
        return ToLibraResult(result);
    }
#endif
    auto result = NativeHost->fireGameSignal(utf8Name.Get());
    DrainEvents();
    if (generation != HostGeneration || !RefreshRendering()) return LastResult;
    return Report(ToLibraResult(result));
}

auto ULibraSpawnableComponent::SetGameProperty(const FString& name, const TArray<double>& values) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!NativeHost) return Report(ELibraRuntimeResult::NotReady);
    if (name.IsEmpty() || values.IsEmpty()) return Report(ELibraRuntimeResult::InvalidArgument);
    if (DebugJobPending()) return ELibraRuntimeResult::Busy;
    auto utf8Name = FTCHARToUTF8(*name);
    return Report(ToLibraResult(NativeHost->setGameProperty(utf8Name.Get(), values.GetData(), values.Num())));
}

FMatrix ULibraSpawnableComponent::EffectToHostMatrix() const
{
    const auto coordinates = NativeHost ? NativeHost->coordinateBasis() :
        liberafx::CoordinateBasis{{EffectAxes.X, EffectAxes.Y, EffectAxes.Z}, HostUnitsPerMetre};
    auto lanes = std::array<double, 16>{1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1};
    if (liberafx::convertCoordinates(coordinates, liberafx::SpatialValueKind::CanonicalDrawTransform,
        true, lanes.data(), lanes.size(), lanes.data()) != liberafx::Result::Ok) return FMatrix::Identity;
    auto matrix = FMatrix::Identity;
    for (auto row = 0; row < 4; ++row)
        for (auto column = 0; column < 4; ++column) matrix.M[column][row] = lanes[row * 4 + column];
    return matrix;
}

auto ULibraSpawnableComponent::SetGameBoolProperty(const FString& name, bool value) -> ELibraRuntimeResult
{
    return SetGameProperty(name, TArray<double>{value ? 1.0 : 0.0});
}

auto ULibraSpawnableComponent::SetOwnerMatrix(const TArray<double>& values) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!NativeHost) return Report(ELibraRuntimeResult::NotReady);
    if (values.Num() != 16) return Report(ELibraRuntimeResult::InvalidArgument);
    if (DebugJobPending()) return ELibraRuntimeResult::Busy;
    return Report(ToLibraResult(NativeHost->setOwnerMatrix(values.GetData(), values.Num())));
}

auto ULibraSpawnableComponent::SetTimeScale(double ratio) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!FMath::IsFinite(ratio) || ratio < 0 || ratio > 1000000) return Report(ELibraRuntimeResult::InvalidArgument);
    if (NativeHost) {
        const auto result = NativeHost->setTimeScale(ratio);
        if (result != liberafx::Result::Ok) return Report(ToLibraResult(result));
    }
    PlayRate = ratio;
    return Report(ELibraRuntimeResult::Ok);
}

auto ULibraSpawnableComponent::GetInstanceTarget(FGuid containerId, int64 instanceId, int64& generation) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    generation = 0;
    if (!NativeHost) return ELibraRuntimeResult::NotReady;
    if (DebugJobPending()) return ELibraRuntimeResult::Busy;
    auto scope = liberafx::SpawnableScope{};
    scope.kind = liberafx::SpawnableScopeKind::Instance;
    LibraNodeIdBytes(containerId, scope.containerId);
    scope.instanceId = static_cast<uint64>(instanceId);
    auto target = liberafx::SpawnableDebugTarget{};
    auto result = NativeHost->debugTarget(scope, target);
    if (result == liberafx::Result::Ok) generation = static_cast<int64>(target.hostGeneration);
    return ToLibraResult(result);
}

auto ULibraSpawnableComponent::SetInstanceProperty(int64 generation, FGuid containerId, int64 instanceId,
    const FString& name, const TArray<double>& values) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (name.Len() != FCString::Strlen(*name)) return ELibraRuntimeResult::InvalidArgument;
    if (!NativeHost) return ELibraRuntimeResult::NotReady;
    if (DebugJobPending()) return ELibraRuntimeResult::Busy;
    auto target = liberafx::SpawnableDebugTarget{};
    target.hostGeneration = static_cast<uint64>(generation);
    target.scope.kind = liberafx::SpawnableScopeKind::Instance;
    LibraNodeIdBytes(containerId, target.scope.containerId);
    target.scope.instanceId = static_cast<uint64>(instanceId);
    auto utf8 = FTCHARToUTF8(*name);
    return ToLibraResult(NativeHost->setInstanceProperty(target, utf8.Get(), values.GetData(), values.Num()));
}

auto ULibraSpawnableComponent::GetInstanceProperty(int64 generation, FGuid containerId, int64 instanceId,
    const FString& name, TArray<double>& values) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    values.Reset();
    if (name.Len() != FCString::Strlen(*name)) return ELibraRuntimeResult::InvalidArgument;
    if (!NativeHost) return ELibraRuntimeResult::NotReady;
    if (DebugJobPending()) return ELibraRuntimeResult::Busy;
    auto target = liberafx::SpawnableDebugTarget{};
    target.hostGeneration = static_cast<uint64>(generation);
    target.scope.kind = liberafx::SpawnableScopeKind::Instance;
    LibraNodeIdBytes(containerId, target.scope.containerId);
    target.scope.instanceId = static_cast<uint64>(instanceId);
    auto utf8 = FTCHARToUTF8(*name);
    auto lanes = std::array<double, 16>{};
    auto width = uint8{};
    auto result = NativeHost->instanceProperty(target, utf8.Get(), lanes.data(), width);
    if (result == liberafx::Result::Ok) values.Append(lanes.data(), width);
    return ToLibraResult(result);
}

auto ULibraSpawnableComponent::FireInstanceSignal(int64 generation, FGuid containerId, int64 instanceId,
    const FString& name) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (name.Len() != FCString::Strlen(*name)) return ELibraRuntimeResult::InvalidArgument;
    if (!NativeHost) return ELibraRuntimeResult::NotReady;
    if (DebugJobPending()) return ELibraRuntimeResult::Busy;
#if PLATFORM_WINDOWS
    if (DebugState) return ELibraRuntimeResult::Busy;
#endif
    auto target = liberafx::SpawnableDebugTarget{};
    target.hostGeneration = static_cast<uint64>(generation);
    target.scope.kind = liberafx::SpawnableScopeKind::Instance;
    LibraNodeIdBytes(containerId, target.scope.containerId);
    target.scope.instanceId = static_cast<uint64>(instanceId);
    auto utf8 = FTCHARToUTF8(*name);
    auto ownerGeneration = HostGeneration;
    auto result = NativeHost->fireInstanceSignal(target, utf8.Get());
    DrainEvents();
    if (ownerGeneration != HostGeneration || !RefreshRendering()) return LastResult;
    return Report(ToLibraResult(result));
}

auto ULibraSpawnableComponent::ReadRootValue(FGuid nodeId, FVector4& value,
    int32& componentCount) const -> ELibraRuntimeResult
{
    check(IsInGameThread());
    value = FVector4(0, 0, 0, 0);
    componentCount = 0;
    if (DebugJobPending()) return ELibraRuntimeResult::Busy;
    if (!NativeHost) return ELibraRuntimeResult::NotReady;
    if (!nodeId.IsValid()) return ELibraRuntimeResult::InvalidArgument;
    uint8 nodeBytes[16]{};
    auto values = std::array<double, 4>{};
    auto width = uint8{};
    LibraNodeIdBytes(nodeId, nodeBytes);
    auto result = NativeHost->rootValue(nodeBytes, values.data(), width);
    if (result != liberafx::Result::Ok) return ToLibraResult(result);
    value = FVector4(values[0], values[1], values[2], values[3]);
    componentCount = width;
    return ELibraRuntimeResult::Ok;
}

auto ULibraSpawnableComponent::ReadChildValue(FGuid containerId, int64 instanceId, FGuid nodeId,
    FVector4& value, int32& componentCount) const -> ELibraRuntimeResult
{
    check(IsInGameThread());
    value = FVector4(0, 0, 0, 0);
    componentCount = 0;
    if (DebugJobPending()) return ELibraRuntimeResult::Busy;
    if (!NativeHost) return ELibraRuntimeResult::NotReady;
    if (!containerId.IsValid() || !nodeId.IsValid() || instanceId <= 0)
        return ELibraRuntimeResult::InvalidArgument;
    uint8 containerBytes[16]{};
    uint8 nodeBytes[16]{};
    auto values = std::array<double, 4>{};
    auto width = uint8{};
    LibraNodeIdBytes(containerId, containerBytes);
    LibraNodeIdBytes(nodeId, nodeBytes);
    auto result = NativeHost->childValue(containerBytes, static_cast<liberafx::GeneratorInstanceId>(instanceId),
        nodeBytes, values.data(), width);
    if (result != liberafx::Result::Ok) return ToLibraResult(result);
    value = FVector4(values[0], values[1], values[2], values[3]);
    componentCount = width;
    return ELibraRuntimeResult::Ok;
}

ELibraRuntimeResult ULibraSpawnableComponent::PopCpuParticleEvent(
    FLibraCpuParticleEvent& particleEvent, bool& hasEvent)
{
    return PopScopedCpuParticleEvent(FGuid{}, 0, particleEvent, hasEvent);
}

ELibraRuntimeResult ULibraSpawnableComponent::PopScopedCpuParticleEvent(
    FGuid containerId, int64 instanceId, FLibraCpuParticleEvent& particleEvent, bool& hasEvent)
{
    particleEvent = {};
    hasEvent = false;
    if (!IsInGameThread() || DebugJobPending())
    {
        return ELibraRuntimeResult::Busy;
    }
    if (!NativeHost)
    {
        return ELibraRuntimeResult::NotReady;
    }
    if (instanceId < 0 || containerId.IsValid() != (instanceId > 0))
    {
        return ELibraRuntimeResult::InvalidArgument;
    }
    auto scope = liberafx::SpawnableScope{};
    if (containerId.IsValid())
    {
        scope.kind = liberafx::SpawnableScopeKind::Instance;
        LibraNodeIdBytes(containerId, scope.containerId);
        scope.instanceId = static_cast<liberafx::GeneratorInstanceId>(instanceId);
    }
    auto event = liberafx::CpuParticleEvent{};
    hasEvent = NativeHost->popCpuParticleEvent(scope, event);
    if (hasEvent)
    {
        particleEvent.SystemId = LibraGuid(event.systemId);
        particleEvent.NodeId = LibraGuid(event.nodeId);
        particleEvent.ParticleId = FString::Printf(TEXT("%llu"), static_cast<unsigned long long>(event.particleId));
        particleEvent.Time = event.time;
        particleEvent.Values = FVector4(event.values[0], event.values[1], event.values[2], event.values[3]);
        particleEvent.Kind = static_cast<int32>(event.kind);
        particleEvent.Width = static_cast<int32>(event.width);
    }
    return ELibraRuntimeResult::Ok;
}

auto ULibraSpawnableComponent::ReadCpuParticleCount(FGuid systemId, uint32& count) const
    -> ELibraRuntimeResult
{
    return ReadCpuParticleCount(FGuid{}, 0, systemId, count);
}

auto ULibraSpawnableComponent::ReadCpuParticleCount(FGuid containerId, int64 instanceId,
    FGuid systemId, uint32& count) const -> ELibraRuntimeResult
{
    check(IsInGameThread());
    count = 0;
    if (DebugJobPending())
    {
        return ELibraRuntimeResult::Busy;
    }
    if (!NativeHost)
    {
        return ELibraRuntimeResult::NotReady;
    }
    if (!systemId.IsValid() || instanceId < 0 || containerId.IsValid() != (instanceId > 0))
    {
        return ELibraRuntimeResult::InvalidArgument;
    }
    uint8 systemBytes[16]{};
    LibraNodeIdBytes(systemId, systemBytes);
    auto scope = liberafx::SpawnableScope{};
    if (containerId.IsValid())
    {
        scope.kind = liberafx::SpawnableScopeKind::Instance;
        LibraNodeIdBytes(containerId, scope.containerId);
        scope.instanceId = static_cast<liberafx::GeneratorInstanceId>(instanceId);
    }
    auto nativeCount = std::size_t{};
    auto result = NativeHost->cpuParticleCount(scope, systemBytes, nativeCount);
    if (result != liberafx::Result::Ok)
    {
        return ToLibraResult(result);
    }
    if (nativeCount > MAX_uint32)
    {
        return ELibraRuntimeResult::InvalidBinary;
    }
    count = static_cast<uint32>(nativeCount);
    return ELibraRuntimeResult::Ok;
}

auto ULibraSpawnableComponent::ReadCpuParticleBatch(FGuid containerId, int64 instanceId, FGuid systemId,
    SIZE_T first, liberafx::CpuParticleSnapshot* snapshots, liberafx::CpuParticleTiming* timings,
    SIZE_T capacity, SIZE_T& written) const -> ELibraRuntimeResult
{
    check(IsInGameThread()); written=0;
    if (DebugJobPending()) return ELibraRuntimeResult::Busy;
    if (!NativeHost) return ELibraRuntimeResult::NotReady;
    if (!systemId.IsValid() || instanceId<0 || containerId.IsValid()!=(instanceId>0)) return ELibraRuntimeResult::InvalidArgument;
    uint8 bytes[16]{}; LibraNodeIdBytes(systemId,bytes);
    auto scope=liberafx::SpawnableScope{};
    if (containerId.IsValid()) { scope.kind=liberafx::SpawnableScopeKind::Instance; LibraNodeIdBytes(containerId,scope.containerId); scope.instanceId=instanceId; }
    return ToLibraResult(NativeHost->cpuParticleRead(scope,bytes,first,snapshots,timings,capacity,written));
}

auto ULibraSpawnableComponent::ReadCpuParticleTiming(FGuid containerId, int64 instanceId,
    FGuid systemId, SIZE_T index, double& age, double& life) const -> ELibraRuntimeResult
{
    check(IsInGameThread()); age=life=0;
    if (DebugJobPending()) return ELibraRuntimeResult::Busy;
    if (!NativeHost) return ELibraRuntimeResult::NotReady;
    if (!systemId.IsValid() || instanceId<0 || containerId.IsValid()!=(instanceId>0)) return ELibraRuntimeResult::InvalidArgument;
    uint8 bytes[16]{}; LibraNodeIdBytes(systemId,bytes);
    auto scope=liberafx::SpawnableScope{};
    if (containerId.IsValid()) { scope.kind=liberafx::SpawnableScopeKind::Instance; LibraNodeIdBytes(containerId,scope.containerId); scope.instanceId=instanceId; }
    auto timing=liberafx::CpuParticleTiming{};
    const auto result=NativeHost->cpuParticleTiming(scope,bytes,index,timing);
    if (result==liberafx::Result::Ok) { age=timing.age; life=timing.life; }
    return ToLibraResult(result);
}

auto ULibraSpawnableComponent::ReadCpuParticleAt(FGuid systemId, SIZE_T index,
    uint64& particleId, double& positionX, double& positionY, double& positionZ,
    double& colorR, double& colorG, double& colorB, double& colorA,
    double& sizeX, double& sizeY) const -> ELibraRuntimeResult
{
    return ReadCpuParticleAt(FGuid{}, 0, systemId, index, particleId,
        positionX, positionY, positionZ, colorR, colorG, colorB, colorA, sizeX, sizeY);
}

auto ULibraSpawnableComponent::ReadCpuParticleAt(FGuid containerId, int64 instanceId,
    FGuid systemId, SIZE_T index, uint64& particleId,
    double& positionX, double& positionY, double& positionZ,
    double& colorR, double& colorG, double& colorB, double& colorA,
    double& sizeX, double& sizeY) const -> ELibraRuntimeResult
{
    check(IsInGameThread());
    particleId = 0;
    if (DebugJobPending())
    {
        return ELibraRuntimeResult::Busy;
    }
    if (!NativeHost)
    {
        return ELibraRuntimeResult::NotReady;
    }
    if (!systemId.IsValid() || instanceId < 0 || containerId.IsValid() != (instanceId > 0))
    {
        return ELibraRuntimeResult::InvalidArgument;
    }
    uint8 systemBytes[16]{};
    LibraNodeIdBytes(systemId, systemBytes);
    auto scope = liberafx::SpawnableScope{};
    if (containerId.IsValid())
    {
        scope.kind = liberafx::SpawnableScopeKind::Instance;
        LibraNodeIdBytes(containerId, scope.containerId);
        scope.instanceId = static_cast<liberafx::GeneratorInstanceId>(instanceId);
    }
    auto particle = liberafx::CpuParticleSnapshot{};
    const auto result = NativeHost->cpuParticleAt(scope, systemBytes, index, particle);
    if (result != liberafx::Result::Ok)
    {
        return ToLibraResult(result);
    }
    particleId = particle.id;
    positionX = particle.position[0];
    positionY = particle.position[1];
    positionZ = particle.position[2];
    colorR = particle.color[0];
    colorG = particle.color[1];
    colorB = particle.color[2];
    colorA = particle.color[3];
    sizeX = particle.size[0];
    sizeY = particle.size[1];
    return ELibraRuntimeResult::Ok;
}

int32 ULibraSpawnableComponent::GetRenderObjectCount() const { return RenderState ? RenderState->DrawCount() : 0; }
int32 ULibraSpawnableComponent::GetNativeDrawPrimitiveCount() const
{
    return RenderState ? RenderState->NativeDrawPrimitiveCount() : 0;
}

void ULibraSpawnableComponent::GetNativeRenderSubmissionStats(int32& logicalRenderObjects,
    int32& nativePrimitives, int32& opaqueBatches, int32& cpuParticleBatches,
    int32& liveCpuParticles, int32& allocatedInstanceCapacity, uint64& submittedBytes) const
{
    logicalRenderObjects = 0;
    nativePrimitives = 0;
    opaqueBatches = 0;
    cpuParticleBatches = 0;
    liveCpuParticles = 0;
    allocatedInstanceCapacity = 0;
    submittedBytes = 0;
    if (RenderState)
    {
        RenderState->GetNativeRenderSubmissionStats(logicalRenderObjects, nativePrimitives,
            opaqueBatches, cpuParticleBatches, liveCpuParticles, allocatedInstanceCapacity,
            submittedBytes);
    }
}

void ULibraSpawnableComponent::UpdateOwnerPresentation()
{
    check(IsInGameThread());
    if (RenderState)
    {
        RenderState->UpdateOwnerPresentation();
    }
}

void ULibraSpawnableComponent::GetDrawSnapshots(TArray<FTransform>& transforms,
    TArray<FBoxSphereBounds>& bounds, TArray<FGuid>& drawIds,
    TArray<FGuid>& containerIds, TArray<int64>& instanceIds) const
{
    transforms.Reset();
    bounds.Reset();
    drawIds.Reset();
    containerIds.Reset();
    instanceIds.Reset();
    if (RenderState)
    {
        RenderState->GetDrawSnapshots(transforms, bounds, drawIds, containerIds, instanceIds);
    }
}
int32 ULibraSpawnableComponent::GetGpuInstanceCount() const { return RenderState ? RenderState->GpuCount() : 0; }

void ULibraSpawnableComponent::TickDebugger(float deltaTime)
{
#if PLATFORM_WINDOWS
    if (!DebugState || !NativeHost) return;
    auto generation = HostGeneration;
    auto* state = DebugState.Get();
    if (state->job.pending())
    {
        if (!state->scheduler.IsCompleted()) return;
        auto result = liberafx::Result::NotReady;
        if (state->job.poll(result) != liberafx::Result::Ok) return;
        state->scheduler.Wait();
        auto request = state->last;
        auto advance = request.operation == liberafx::host::SpawnableDebugOperation::Advance;
        if (advance) {
            RealTimeSeconds = request.time;
            TimeSeconds = NativeHost->timeSnapshot().effectTime;
        }
        DrainEvents();
        if (generation != HostGeneration || DebugState.Get() != state || bForceTerminated) return;
        if (advance && result == liberafx::Result::QueueRejected && ++state->attempts < 16)
        {
            result = state->Submit(request);
            if (result == liberafx::Result::Ok) return;
        }
        if (result != liberafx::Result::Ok)
        {
            if (advance) { bIsPlaying = false; SetComponentTickEnabled(false); }
            Report(ToLibraResult(result));
        }
        else if (RefreshRendering()) LastResult = ELibraRuntimeResult::Ok;
        if (generation == HostGeneration && !bIsPlaying && !DebugJobPending()) SetComponentTickEnabled(false);
        return;
    }
    if (!bIsPlaying) { SetComponentTickEnabled(false); return; }
    auto request = liberafx::host::SpawnableDebugJob::Request{};
    request.time = RealTimeSeconds + static_cast<double>(deltaTime);
    request.frameDelta = deltaTime;
    if (!FMath::IsFinite(request.time) || !FMath::IsFinite(PlayRate) || PlayRate < 0)
    {
        ReleaseHost();
        Report(ELibraRuntimeResult::InvalidArgument);
        return;
    }
    auto result = SkeletalPoseState
        ? SkeletalPoseState->Publish(*NativeHost, *this) : liberafx::Result::Ok;
    if (result == liberafx::Result::Ok)
    {
        result = PublishLibraFrameInputs(*NativeHost, *this);
    }
    state->attempts = 0;
    if (result == liberafx::Result::Ok) result = NativeHost->setTimeScale(PlayRate);
    if (result == liberafx::Result::Ok) result = state->Submit(request);
    if (result != liberafx::Result::Ok)
    {
        bIsPlaying = false;
        SetComponentTickEnabled(false);
        Report(ToLibraResult(result));
    }
#endif
}

void ULibraSpawnableComponent::TickComponent(float deltaTime, ELevelTick tickType,
    FActorComponentTickFunction* tickFunction)
{
    TRACE_CPUPROFILER_EVENT_SCOPE(Libra_SpawnableAdvance);
    Super::TickComponent(deltaTime, tickType, tickFunction);
    if (DebugState) { TickDebugger(deltaTime); return; }
    if (!bIsPlaying || !NativeHost) return;
    if (!FMath::IsFinite(PlayRate) || PlayRate < 0)
    {
        ReleaseHost();
        Report(ELibraRuntimeResult::InvalidArgument);
        return;
    }
    auto* current = NativeHost.Get();
    auto generation = HostGeneration;
    auto nextTime = RealTimeSeconds + static_cast<double>(deltaTime);
    if (!FMath::IsFinite(nextTime))
    {
        ReleaseHost();
        Report(ELibraRuntimeResult::NonFinite);
        return;
    }
    if (bSourceInitialAdvancePending)
    {
        auto result = SkeletalPoseState
            ? SkeletalPoseState->Publish(*current, *this)
            : liberafx::Result::NotReady;
        if (result == liberafx::Result::Ok)
        {
            result = PublishLibraFrameInputs(*current, *this);
        }
        if (result == liberafx::Result::Ok) result = current->advance(0, 0);
        DrainEvents();
        if (HostGeneration != generation || bForceTerminated) return;
        for (auto attempt = 0; result == liberafx::Result::QueueRejected && attempt < 16; ++attempt)
        {
            result = current->advance(0, 0);
            DrainEvents();
            if (HostGeneration != generation || bForceTerminated) return;
        }
        if (result != liberafx::Result::Ok)
        {
            ReleaseHost();
            Report(ToLibraResult(result));
            return;
        }
        bSourceInitialAdvancePending = false;
        if (!RefreshRendering()) return;
        LastResult = ELibraRuntimeResult::Ok;
        return;
    }
    auto result = SkeletalPoseState
        ? SkeletalPoseState->Publish(*current, *this) : liberafx::Result::Ok;
    if (result == liberafx::Result::Ok)
    {
        result = PublishLibraFrameInputs(*current, *this);
    }
    if (result == liberafx::Result::Ok) result = current->setTimeScale(PlayRate);
    if (result == liberafx::Result::Ok && bUseSharedSimulation &&
        LibraSharedEffects::Queue(*this,*current,generation,nextTime,deltaTime)) { return; }
    if (result == liberafx::Result::Ok) result = current->advance(nextTime, deltaTime);
    CompleteSharedFrame(current,generation,deltaTime,static_cast<uint8>(result));

}

bool ULibraSpawnableComponent::SharedFrameIsCurrent(liberafx::SpawnableHost* host,uint64 generation) const
{
    return NativeHost.Get()==host && HostGeneration==generation && bIsPlaying && !bForceTerminated && !DebugState;
}
bool ULibraSpawnableComponent::UsesPreparedStorage() const
{
    return NativeHost && static_cast<bool>(NativeHost.GetDeleter().Pool);
}
void ULibraSpawnableComponent::CompleteSharedFrame(liberafx::SpawnableHost* current,uint64 generation,double deltaTime,uint8 status,int32 batchSize)
{
    if (!SharedFrameIsCurrent(current,generation)) { return; }
    LastSimulationBatchSize = batchSize;
    auto result=static_cast<liberafx::Result>(status);
    RealTimeSeconds = current->timeSnapshot().realTime;
    TimeSeconds = current->timeSnapshot().effectTime;
    DrainEvents();
    if (HostGeneration != generation) return;
    if (bForceTerminated) return;
    for (auto attempt = 0; result == liberafx::Result::QueueRejected && attempt < 16; ++attempt)
    {
        result = current->advance(RealTimeSeconds, deltaTime);
        TimeSeconds = current->timeSnapshot().effectTime;
        DrainEvents();
        if (HostGeneration != generation) return;
        if (bForceTerminated) return;
    }
    if (result != liberafx::Result::Ok)
    {
        if (result != liberafx::Result::QueueRejected)
        {
            ReleaseHost();
        }
        Report(ToLibraResult(result));
        return;
    }
    if (!RefreshRendering()) return;
    LastResult = ELibraRuntimeResult::Ok;
    bRootFinished = current->rootFinished();
    if (bRootFinished)
    {
        bIsPlaying = false;
        SetComponentTickEnabled(false);
    }
}

ELibraRuntimeResult ULibraSpawnableComponent::PrewarmEffect()
{
    if (!IsInGameThread() || !Package) { return ELibraRuntimeResult::NotReady; }
    const auto registry=LibraNativeNodeSnapshot();
    if (!registry->ProjectDescriptors().IsEmpty()) { return ELibraRuntimeResult::UnsupportedBinary; }
    auto options=liberafx::SpawnableHostPoolOptions{};
    options.capacity=static_cast<std::size_t>(FMath::Clamp(PreparedHostCapacity,1,1024));
    options.preparationWorkers=static_cast<std::size_t>(FMath::Clamp(PreparationWorkers,1,16));
    options.host.eventCapacity=static_cast<std::size_t>(FMath::Max(1,EventCapacity));
    options.host.instructionBudget=static_cast<std::size_t>(FMath::Max(1,InstructionBudget));
    const auto bytes=Package->GetPackageBytes();
    return ToLibraResult(LibraSharedEffects::Prepare(GetWorld(),bytes.GetData(),bytes.Num(),options));
}

auto ULibraSpawnableComponent::Report(ELibraRuntimeResult result) -> ELibraRuntimeResult
{
    LastResult = result;
    if (result != ELibraRuntimeResult::Ok) OnRuntimeError.Broadcast(result);
    return result;
}

bool ULibraSpawnableComponent::RefreshRendering()
{
    if (!NativeHost || DebugJobPending()) return false;
    if (RenderState && !RenderState->Update(TimeSeconds, NativeHost->rootFinished()))
    {
        ReleaseHost();
        Report(ELibraRuntimeResult::InvalidValueType);
        return false;
    }
    bRootFinished = NativeHost->rootFinished();
    if (bRootFinished)
    {
        bIsPlaying = false;
        SetComponentTickEnabled(false);
    }
    return true;
}

void ULibraSpawnableComponent::DrainEvents()
{
    if (!NativeHost || DebugJobPending()) return;
    ++EventDrainDepth;
    ON_SCOPE_EXIT
    {
        if (--EventDrainDepth == 0) CancelledBatchScopes.Reset();
    };
    auto* current = NativeHost.Get();
    auto generation = HostGeneration;
    auto hostEvents = TArray<FLibraSpawnableHostEvent>{};
    auto gameIoEvents = TArray<FLibraGameIoEvent>{};
    auto invalidPortIndex = false;
    auto event = liberafx::SpawnableHostEvent{};
    while (NativeHost->popEvent(event))
    {
        auto hostEvent = FLibraSpawnableHostEvent{};
        hostEvent.Kind = static_cast<ELibraSpawnableHostEventKind>(event.kind);
        hostEvent.ContainerId = LibraGuid(event.containerId);
        hostEvent.InstanceId = static_cast<int64>(event.instanceId);
        hostEvent.TimeSeconds = event.time;
        hostEvent.Seed = static_cast<int64>(event.seed);
        hostEvent.SignalNodeId = LibraGuid(event.signalNodeId);
        hostEvents.Add(MoveTemp(hostEvent));
    }
    auto ioEvent = liberafx::NamedIoEvent{};
    while (NativeHost->popGameIoEvent(ioEvent))
    {
        auto gameIoEvent = FLibraGameIoEvent{};
        gameIoEvent.ContainerId = LibraGuid(ioEvent.identity.containerId);
        gameIoEvent.InstanceId = static_cast<int64>(ioEvent.identity.instanceId);
        gameIoEvent.PortIndex = static_cast<int32>(ioEvent.portIndex);
        gameIoEvent.Kind = LibraIoEventKind(ioEvent.kind);
        gameIoEvent.bIsColor = ioEvent.isColor;
        gameIoEvent.Values.Append(ioEvent.values, ioEvent.width);
        const auto& ports = NativeHost->package().gameIoPorts;
        if (ioEvent.identity.instanceId != 0) {
            auto name = static_cast<const char*>(nullptr);
            if (NativeHost->instancePortName(ioEvent.identity.containerId, ioEvent.portIndex, name) == liberafx::Result::Ok)
                gameIoEvent.PortName = UTF8_TO_TCHAR(name);
            else invalidPortIndex = true;
        }
        else if (ioEvent.portIndex < ports.size())
            gameIoEvent.PortName = UTF8_TO_TCHAR(ports[ioEvent.portIndex].name.c_str());
        else
            invalidPortIndex = true;
        gameIoEvents.Add(MoveTemp(gameIoEvent));
    }
    for (const auto& hostEvent : hostEvents)
    {
        if (HostGeneration != generation) return;
        const auto wholeForce = hostEvent.Kind == ELibraSpawnableHostEventKind::ForceTerminated &&
            !hostEvent.ContainerId.IsValid() && hostEvent.InstanceId == 0;
        if (hostEvent.Kind != ELibraSpawnableHostEventKind::ForceTerminated)
        {
            const auto* cancelled = CancelledBatchScopes.Find(hostEvent.ContainerId);
            if (bForceTerminated || (cancelled && cancelled->Contains(hostEvent.InstanceId))) continue;
        }
        if (wholeForce)
        {
            bForceTerminated = bRootFinished = true;
            bIsPlaying = false;
            SetComponentTickEnabled(false);
            RenderState.Reset(); // destroys root/children and stops every GPU owner
        }
        if (RenderState && hostEvent.Kind == ELibraSpawnableHostEventKind::Spawn)
        {
            // A whole lifetime can complete in one advance; never resurrect its engine objects.
            auto alreadyStopped = hostEvents.ContainsByPredicate([&](const auto& later)
            {
                return (later.Kind == ELibraSpawnableHostEventKind::ForceTerminated &&
                        !later.ContainerId.IsValid() && later.InstanceId == 0) ||
                    ((later.Kind == ELibraSpawnableHostEventKind::Stop ||
                      later.Kind == ELibraSpawnableHostEventKind::ForceTerminated) &&
                     later.ContainerId == hostEvent.ContainerId && later.InstanceId == hostEvent.InstanceId);
            });
            if (!alreadyStopped && !RenderState->Spawn(hostEvent.ContainerId, hostEvent.InstanceId,
                hostEvent.TimeSeconds, static_cast<uint64>(hostEvent.Seed)))
            {
                ReleaseHost();
                Report(ELibraRuntimeResult::UnsupportedBinary);
                return;
            }
        }
        if (RenderState && hostEvent.Kind == ELibraSpawnableHostEventKind::Stop)
            RenderState->Stop(hostEvent.ContainerId, hostEvent.InstanceId);
        if (!wholeForce && hostEvent.Kind == ELibraSpawnableHostEventKind::ForceTerminated)
        {
            CancelledBatchScopes.FindOrAdd(hostEvent.ContainerId).Add(hostEvent.InstanceId);
            if (RenderState) RenderState->Stop(hostEvent.ContainerId, hostEvent.InstanceId);
        }
        if (HostGeneration != generation) return;
        OnHostEvent.Broadcast(hostEvent);
    }
    for (const auto& gameIoEvent : gameIoEvents)
    {
        if (HostGeneration != generation) return;
        if (bForceTerminated) return;
        OnGameIoEvent.Broadcast(gameIoEvent);
    }
    if (invalidPortIndex) Report(ELibraRuntimeResult::InvalidBinary);
}

void ULibraSpawnableComponent::ReleaseHost()
{
    ++HostGeneration;
    ReleaseDebugger();
    if (SkeletalPoseState) SkeletalPoseState->RemoveTickPrerequisites(*this);
    SkeletalPoseState.Reset();
    bSourceInitialAdvancePending = false;
    PrimaryComponentTick.TickGroup = TG_PrePhysics;
    RenderState.Reset();
    NativeHost.Reset();
    LastSimulationBatchSize = 0;
    bIsPlaying = false;
    SetComponentTickEnabled(false);
}
