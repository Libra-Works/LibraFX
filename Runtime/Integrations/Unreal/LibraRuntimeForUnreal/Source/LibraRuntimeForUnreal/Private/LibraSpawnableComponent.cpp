#include "LibraSpawnableComponent.h"

#include "LibraNativeState.h"
#include "LibraSpawnableAsset.h"
#include "LibraSpawnableRenderState.h"
#include "liberafx/SpawnableHost.h"
#include "liberafx/SpawnablePackage.h"

#include <array>
#include <memory>

namespace {
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
}

ULibraSpawnableComponent::~ULibraSpawnableComponent() = default;

void ULibraSpawnableComponent::BeginPlay()
{
    Super::BeginPlay();
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
    auto rendering = MakeUnique<FLibraSpawnableRenderState>(*this);
    if (!rendering->Prepare(package)) return Report(ELibraRuntimeResult::UnsupportedBinary);
    auto options = liberafx::SpawnableHostOptions{};
    options.maxActivePerTemplate = static_cast<std::size_t>(FMath::Max(1, MaxActivePerTemplate));
    options.memoryBudgetBytesPerTemplate = static_cast<std::size_t>(FMath::Max<int64>(1024, MemoryBudgetBytesPerTemplate));
    options.eventCapacity = static_cast<std::size_t>(FMath::Max(1, EventCapacity));
    options.instructionBudget = static_cast<std::size_t>(FMath::Max(1, InstructionBudget));
    auto host = std::unique_ptr<liberafx::SpawnableHost>{};
    result = liberafx::SpawnableHost::create(MoveTemp(package), options, host);
    if (result != liberafx::Result::Ok) return Report(ToLibraResult(result));
    NativeHost.Reset(host.release());
    ++HostGeneration;
    RenderState = MoveTemp(rendering);
    if (!RenderState->Spawn(FGuid{}, 0, 0, 0))
    {
        ReleaseHost();
        return Report(ELibraRuntimeResult::UnsupportedBinary);
    }
    TimeSeconds = 0;
    bRootFinished = false;
    bIsPlaying = true;
    SetComponentTickEnabled(true);

    auto* current = NativeHost.Get();
    auto generation = HostGeneration;
    result = current->advance(0, 0);
    DrainEvents();
    if (HostGeneration != generation) return LastResult;
    for (auto attempt = 0; result == liberafx::Result::QueueRejected && attempt < 16; ++attempt)
    {
        result = current->advance(0, 0);
        DrainEvents();
        if (HostGeneration != generation) return LastResult;
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

auto ULibraSpawnableComponent::RequestNaturalEnd() -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!NativeHost) return Report(ELibraRuntimeResult::NotReady);
    auto generation = HostGeneration;
    auto result = NativeHost->requestNaturalEnd();
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
    auto utf8Name = FTCHARToUTF8(*name);
    return Report(ToLibraResult(NativeHost->setGameProperty(utf8Name.Get(), values.GetData(), values.Num())));
}

auto ULibraSpawnableComponent::SetOwnerMatrix(const TArray<double>& values) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!NativeHost) return Report(ELibraRuntimeResult::NotReady);
    if (values.Num() != 16) return Report(ELibraRuntimeResult::InvalidArgument);
    return Report(ToLibraResult(NativeHost->setOwnerMatrix(values.GetData(), values.Num())));
}

auto ULibraSpawnableComponent::ReadRootValue(FGuid nodeId, FVector4& value,
    int32& componentCount) const -> ELibraRuntimeResult
{
    check(IsInGameThread());
    value = FVector4(0, 0, 0, 0);
    componentCount = 0;
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

int32 ULibraSpawnableComponent::GetRenderObjectCount() const { return RenderState ? RenderState->DrawCount() : 0; }
int32 ULibraSpawnableComponent::GetGpuInstanceCount() const { return RenderState ? RenderState->GpuCount() : 0; }

void ULibraSpawnableComponent::TickComponent(float deltaTime, ELevelTick tickType,
    FActorComponentTickFunction* tickFunction)
{
    Super::TickComponent(deltaTime, tickType, tickFunction);
    if (!bIsPlaying || !NativeHost) return;
    if (!FMath::IsFinite(PlayRate) || PlayRate < 0)
    {
        ReleaseHost();
        Report(ELibraRuntimeResult::InvalidArgument);
        return;
    }
    auto* current = NativeHost.Get();
    auto generation = HostGeneration;
    auto nextTime = TimeSeconds + static_cast<double>(deltaTime) * PlayRate;
    if (!FMath::IsFinite(nextTime))
    {
        ReleaseHost();
        Report(ELibraRuntimeResult::NonFinite);
        return;
    }
    TimeSeconds = nextTime;
    auto result = current->advance(TimeSeconds, deltaTime);
    DrainEvents();
    if (HostGeneration != generation) return;
    for (auto attempt = 0; result == liberafx::Result::QueueRejected && attempt < 16; ++attempt)
    {
        result = current->advance(TimeSeconds, deltaTime);
        DrainEvents();
        if (HostGeneration != generation) return;
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

auto ULibraSpawnableComponent::Report(ELibraRuntimeResult result) -> ELibraRuntimeResult
{
    LastResult = result;
    if (result != ELibraRuntimeResult::Ok) OnRuntimeError.Broadcast(result);
    return result;
}

bool ULibraSpawnableComponent::RefreshRendering()
{
    if (!NativeHost) return false;
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
    if (!NativeHost) return;
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
        if (ioEvent.portIndex < ports.size())
            gameIoEvent.PortName = UTF8_TO_TCHAR(ports[ioEvent.portIndex].name.c_str());
        else
            invalidPortIndex = true;
        gameIoEvents.Add(MoveTemp(gameIoEvent));
    }
    for (const auto& hostEvent : hostEvents)
    {
        if (HostGeneration != generation) return;
        if (RenderState && hostEvent.Kind == ELibraSpawnableHostEventKind::Spawn)
        {
            // A whole lifetime can complete in one advance; never resurrect its engine objects.
            auto alreadyStopped = hostEvents.ContainsByPredicate([&](const auto& later)
            {
                return later.Kind == ELibraSpawnableHostEventKind::Stop &&
                    later.ContainerId == hostEvent.ContainerId && later.InstanceId == hostEvent.InstanceId;
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
        OnHostEvent.Broadcast(hostEvent);
    }
    for (const auto& gameIoEvent : gameIoEvents)
    {
        if (HostGeneration != generation) return;
        OnGameIoEvent.Broadcast(gameIoEvent);
    }
    if (invalidPortIndex) Report(ELibraRuntimeResult::InvalidBinary);
}

void ULibraSpawnableComponent::ReleaseHost()
{
    ++HostGeneration;
    RenderState.Reset();
    NativeHost.Reset();
    bIsPlaying = false;
    SetComponentTickEnabled(false);
}
