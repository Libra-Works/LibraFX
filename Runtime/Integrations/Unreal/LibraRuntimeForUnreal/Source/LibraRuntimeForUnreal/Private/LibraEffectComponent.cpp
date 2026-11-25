#include "LibraEffectComponent.h"

#include "LibraGpuEffectRuntime.h"
#include "GameFramework/Actor.h"

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
    if (!Package || !PackagePlayback)
    {
        return ELibraRuntimeResult::NotReady;
    }
    return AttachPackageDebugger(GetPackageDebuggerGeneration(), FGuid{}, 0, invitation);
}

void ULibraEffectComponent::DetachExecutionDebugger()
{
    check(IsInGameThread());
    if (Package && PackagePlayback)
    {
        PackagePlayback->DetachExecutionDebugger();
        SyncPackageState();
    }
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
    if (bAutoPlay)
    {
        Play();
    }
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
    Super::BeginDestroy();
}

void ULibraEffectComponent::OnComponentDestroyed(bool bDestroyingHierarchy)
{
    ReleasePackagePlayback();
    ReleaseGpuPlayback();
    Super::OnComponentDestroyed(bDestroyingHierarchy);
}

auto ULibraEffectComponent::Play(bool bRestart) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (bStopping)
    {
        return LastResult = ELibraRuntimeResult::Busy;
    }
    if (!IsRegistered())
    {
        return LastResult = ELibraRuntimeResult::NotReady;
    }
    if (Package && GpuEffect)
    {
        LastError = TEXT("Assign either an LFXP package or a standalone GPU effect, not both.");
        return LastResult = ELibraRuntimeResult::InvalidArgument;
    }
    if (GpuEffect)
    {
        if (PackagePlayback && PackagePlayback->Package)
        {
            auto result = PackagePlayback->Stop();
            if (result != ELibraRuntimeResult::Ok)
            {
                return LastResult = result;
            }
            PackagePlayback->Package = nullptr;
        }
        auto result = EnsureGpuPlayback();
        if (result != ELibraRuntimeResult::Ok)
        {
            return LastResult = result;
        }
        if (bRestart)
        {
            GpuPlayback->Stop();
        }
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
        return PlayPackage(bRestart);
    }
    LastError = TEXT("Assign a compiled .lfxp package before playback.");
    return LastResult = ELibraRuntimeResult::NotReady;
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
    SetComponentTickEnabled(false);
}

void ULibraEffectComponent::Stop()
{
    check(IsInGameThread());
    if (bStopping)
    {
        LastResult = ELibraRuntimeResult::Busy;
        return;
    }
    TGuardValue<bool> stopping(bStopping, true);
    if (PackagePlayback)
    {
        auto result = PackagePlayback->Stop();
        SyncPackageState();
        if (result != ELibraRuntimeResult::Ok)
        {
            LastResult = result;
            return;
        }
    }
    if (GpuPlayback)
    {
        GpuPlayback->Stop();
    }
    bIsPlaying = false;
    TimeSeconds = 0;
    LastEvaluatedTimeSeconds = 0;
    bRootFinished = false;
    LastResult = Package || GpuEffect ? ELibraRuntimeResult::Ok : ELibraRuntimeResult::NotReady;
    SetComponentTickEnabled(false);
}

auto ULibraEffectComponent::EvaluateAtTime(double evaluationTimeSeconds) -> ELibraRuntimeResult
{
    if (Package || GpuEffect)
    {
        return ReportPackageError(ELibraRuntimeResult::UnsupportedBinary,
            TEXT("Compiled packages are forward-only; use Play, Pause and Tick rather than seeking."));
    }
    return LastResult = ELibraRuntimeResult::NotReady;
}

void ULibraEffectComponent::TickComponent(float deltaTime, ELevelTick tickType,
    FActorComponentTickFunction* tickFunction)
{
    Super::TickComponent(deltaTime, tickType, tickFunction);
    if (Package && PackagePlayback)
    {
        auto playback = PackagePlayback;
        playback->PlayRate = PlayRate;
        playback->EffectCameraOverride = EffectCameraOverride;
        playback->UpdateOwnerPresentation();
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
        if (!bIsPlaying)
        {
            SetComponentTickEnabled(false);
        }
    }
}

bool ULibraEffectComponent::ReadComponents(FGuid nodeId, double (&values)[4], uint8 width) const
{
    check(IsInGameThread());
    if (!Package || width == 0 || width > 4)
    {
        return false;
    }
    auto value = FVector4{};
    auto actualWidth = int32{};
    if (ReadRootValue(nodeId, value, actualWidth) != ELibraRuntimeResult::Ok || actualWidth != width)
    {
        return false;
    }
    for (auto lane = 0; lane < width; ++lane)
    {
        values[lane] = value[lane];
    }
    return true;
}

auto ULibraEffectComponent::GetScalar(FGuid nodeId, double& value) const -> bool
{
    value = 0;
    double values[4]{};
    if (!ReadComponents(nodeId, values, 1))
    {
        return false;
    }
    value = values[0];
    return true;
}

auto ULibraEffectComponent::GetVector2(FGuid nodeId, FVector2D& value) const -> bool
{
    value = FVector2D::ZeroVector;
    double values[4]{};
    if (!ReadComponents(nodeId, values, 2))
    {
        return false;
    }
    value = FVector2D(values[0], values[1]);
    return true;
}

auto ULibraEffectComponent::GetVector3(FGuid nodeId, FVector& value) const -> bool
{
    value = FVector::ZeroVector;
    double values[4]{};
    if (!ReadComponents(nodeId, values, 3))
    {
        return false;
    }
    value = FVector(values[0], values[1], values[2]);
    return true;
}

auto ULibraEffectComponent::GetVector4(FGuid nodeId, FVector4& value) const -> bool
{
    value = FVector4(0, 0, 0, 0);
    double values[4]{};
    if (!ReadComponents(nodeId, values, 4))
    {
        return false;
    }
    value = FVector4(values[0], values[1], values[2], values[3]);
    return true;
}

auto ULibraEffectComponent::GetLinearColor(FGuid nodeId, FLinearColor& value) const -> bool
{
    value = FLinearColor::Transparent;
    double values[4]{};
    if (!ReadComponents(nodeId, values, 4))
    {
        return false;
    }
    for (auto component : values)
    {
        if (FMath::Abs(component) > TNumericLimits<float>::Max())
        {
            return false;
        }
    }
    value = FLinearColor(static_cast<float>(values[0]), static_cast<float>(values[1]),
        static_cast<float>(values[2]), static_cast<float>(values[3]));
    return true;
}
