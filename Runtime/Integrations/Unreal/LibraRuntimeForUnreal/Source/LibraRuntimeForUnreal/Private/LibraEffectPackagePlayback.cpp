#include "LibraEffectComponent.h"

#include "LibraSpawnableAsset.h"
#include "GameFramework/Actor.h"

auto ULibraEffectComponent::ReportPackageError(ELibraRuntimeResult result, const FString& message) -> ELibraRuntimeResult
{
    LastResult = result;
    LastError = message;
    OnRuntimeError.Broadcast(result);
    return result;
}

auto ULibraEffectComponent::SetPackage(ULibraSpawnableAsset* newPackage) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (bStopping) return ELibraRuntimeResult::Busy;
    if (newPackage)
    {
        auto result = newPackage->ValidatePackage();
        if (result != ELibraRuntimeResult::Ok) return LastResult = result;
    }
    Stop();
    if (Package && LastResult != ELibraRuntimeResult::Ok) return LastResult;
    Effect = nullptr;
    Package = newPackage;
    if (PackagePlayback) PackagePlayback->Package = nullptr;
    LastError.Reset();
    return LastResult = newPackage ? ELibraRuntimeResult::Ok : ELibraRuntimeResult::NotReady;
}

auto ULibraEffectComponent::PlayPackage(bool bRestart) -> ELibraRuntimeResult
{
    if (!GetOwner()) return LastResult = ELibraRuntimeResult::NotReady;
    if (bProfileNodes || bProfileAccessors || bRecordProfiling)
        return ReportPackageError(ELibraRuntimeResult::UnsupportedBinary,
            TEXT("Effect packages do not yet support standalone bytecode profiling flags."));
    if (!PackagePlayback)
    {
        PackagePlayback = NewObject<ULibraSpawnableComponent>(GetOwner(), NAME_None, RF_Transient);
        PackagePlayback->bAutoPlay = false;
        PackagePlayback->OnHostEvent.AddDynamic(this, &ULibraEffectComponent::ForwardHostEvent);
        PackagePlayback->OnGameIoEvent.AddDynamic(this, &ULibraEffectComponent::ForwardGameIoEvent);
        PackagePlayback->OnRuntimeError.AddDynamic(this, &ULibraEffectComponent::ForwardRuntimeError);
        GetOwner()->AddInstanceComponent(PackagePlayback);
        PackagePlayback->RegisterComponent();
    }
    auto playback = PackagePlayback;
    auto restart = bRestart || playback->Package != Package;
    playback->Package = Package;
    playback->AssetBindings = AssetBindings;
    playback->GpuSpawnPerSecond = GpuSpawnPerSecond;
    playback->MaxActivePerTemplate = MaxActivePerTemplate;
    playback->MemoryBudgetBytesPerTemplate = MemoryBudgetBytesPerTemplate;
    playback->EventCapacity = EventCapacity;
    playback->InstructionBudget = PackageInstructionBudget;
    playback->PlayRate = PlayRate;
    auto result = playback->Play(restart);
    if (PackagePlayback != playback) return LastResult;
    SyncPackageState();
    return LastResult = result;
}

void ULibraEffectComponent::SyncPackageState()
{
    if (!PackagePlayback) return;
    TimeSeconds = LastEvaluatedTimeSeconds = PackagePlayback->TimeSeconds;
    bIsPlaying = PackagePlayback->bIsPlaying;
    bRootFinished = PackagePlayback->bRootFinished;
    LastResult = PackagePlayback->LastResult;
    LastError = PackagePlayback->LastError;
    // The public component is the only tick owner, including after callbacks.
    PackagePlayback->SetComponentTickEnabled(false);
    SetComponentTickEnabled(bIsPlaying);
}

void ULibraEffectComponent::ReleasePackagePlayback()
{
    auto playback = PackagePlayback;
    PackagePlayback = nullptr;
    if (playback)
    {
        playback->OnHostEvent.RemoveAll(this);
        playback->OnGameIoEvent.RemoveAll(this);
        playback->OnRuntimeError.RemoveAll(this);
        playback->DestroyComponent();
    }
    bIsPlaying = false;
    SetComponentTickEnabled(false);
}

void ULibraEffectComponent::ForwardHostEvent(const FLibraSpawnableHostEvent& event)
{
    SyncPackageState();
    OnHostEvent.Broadcast(event);
}

void ULibraEffectComponent::ForwardGameIoEvent(const FLibraGameIoEvent& event)
{
    SyncPackageState();
    OnGameIoEvent.Broadcast(event);
}

void ULibraEffectComponent::ForwardRuntimeError(ELibraRuntimeResult result)
{
    SyncPackageState();
    OnRuntimeError.Broadcast(result);
}

auto ULibraEffectComponent::RequestNaturalEnd() -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!Package || !PackagePlayback) return LastResult = ELibraRuntimeResult::NotReady;
    auto playback = PackagePlayback;
    auto result = playback->RequestNaturalEnd();
    if (PackagePlayback == playback) SyncPackageState();
    return LastResult = result;
}

auto ULibraEffectComponent::DispatchNodeSignal(FGuid nodeId) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!Package || !PackagePlayback) return LastResult = ELibraRuntimeResult::NotReady;
    auto playback = PackagePlayback;
    auto result = playback->DispatchSignal(nodeId);
    if (PackagePlayback == playback) SyncPackageState();
    return LastResult = result;
}

auto ULibraEffectComponent::FireGameSignal(const FString& name) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!Package || !PackagePlayback) return LastResult = ELibraRuntimeResult::NotReady;
    auto playback = PackagePlayback;
    auto result = playback->FireGameSignal(name);
    if (PackagePlayback == playback) SyncPackageState();
    return LastResult = result;
}

auto ULibraEffectComponent::SetGameProperty(const FString& name, const TArray<double>& values) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!Package || !PackagePlayback) return LastResult = ELibraRuntimeResult::NotReady;
    return LastResult = PackagePlayback->SetGameProperty(name, values);
}

auto ULibraEffectComponent::SetOwnerMatrix(const TArray<double>& values) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!Package || !PackagePlayback) return LastResult = ELibraRuntimeResult::NotReady;
    return LastResult = PackagePlayback->SetOwnerMatrix(values);
}

auto ULibraEffectComponent::ReadRootValue(FGuid nodeId, FVector4& value, int32& componentCount) const -> ELibraRuntimeResult
{
    value = FVector4(0, 0, 0, 0);
    componentCount = 0;
    return Package && PackagePlayback ? PackagePlayback->ReadRootValue(nodeId, value, componentCount) : ELibraRuntimeResult::NotReady;
}

auto ULibraEffectComponent::ReadChildValue(FGuid containerId, int64 instanceId, FGuid nodeId,
    FVector4& value, int32& componentCount) const -> ELibraRuntimeResult
{
    value = FVector4(0, 0, 0, 0);
    componentCount = 0;
    return Package && PackagePlayback ? PackagePlayback->ReadChildValue(containerId, instanceId, nodeId, value, componentCount)
        : ELibraRuntimeResult::NotReady;
}

int32 ULibraEffectComponent::GetRenderObjectCount() const
{
    return PackagePlayback ? PackagePlayback->GetRenderObjectCount() : 0;
}

int32 ULibraEffectComponent::GetGpuInstanceCount() const
{
    return PackagePlayback ? PackagePlayback->GetGpuInstanceCount() : 0;
}
