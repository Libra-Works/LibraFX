#include "LibraEffectComponent.h"

#include "Components/SceneComponent.h"
#include "GameFramework/Actor.h"
#include "LibraGpuEffectAsset.h"
#include "LibraGpuEffectComponent.h"

auto ULibraEffectComponent::SetGpuEffect(ULibraGpuEffectAsset* newEffect) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (bStopping)
    {
        return LastResult = ELibraRuntimeResult::Busy;
    }
    if (newEffect)
    {
        FString error;
        if (!newEffect->ValidateProgram(error))
        {
            LastError = MoveTemp(error);
            return LastResult = ELibraRuntimeResult::InvalidArgument;
        }
    }
    Stop();
    if (Package && LastResult != ELibraRuntimeResult::Ok)
    {
        return LastResult;
    }
    Effect = nullptr;
    Package = nullptr;
    if (PackagePlayback)
    {
        PackagePlayback->Package = nullptr;
    }
    NativeState.Reset();
    ReleaseGpuPlayback();
    GpuEffect = newEffect;
    LastError.Reset();
    return LastResult = newEffect ? ELibraRuntimeResult::Ok : ELibraRuntimeResult::NotReady;
}

auto ULibraEffectComponent::EnsureGpuPlayback() -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (!GpuEffect || Effect || Package)
    {
        return ELibraRuntimeResult::InvalidArgument;
    }
    auto actor = GetOwner();
    if (!actor)
    {
        return ELibraRuntimeResult::NotReady;
    }
    if (!GpuPlayback)
    {
        auto playback = NewObject<ULibraGpuEffectComponent>(actor, NAME_None, RF_Transient);
        playback->bAutoPlay = false;
        if (auto root = actor->GetRootComponent())
        {
            playback->SetupAttachment(root);
        }
        actor->AddInstanceComponent(playback);
        GpuPlayback = playback;
        playback->RegisterComponent();
    }
    if (GpuPlayback->Effect != GpuEffect && !GpuPlayback->SetEffect(GpuEffect))
    {
        LastError = GpuPlayback->LastError;
        ReleaseGpuPlayback();
        return ELibraRuntimeResult::InvalidArgument;
    }
    if (!GpuPlayback->ConfigureEmitter(GpuSpawnPerSecond, GpuEmitterSeed))
    {
        LastError = GpuPlayback->LastError;
        ReleaseGpuPlayback();
        return ELibraRuntimeResult::InvalidArgument;
    }
    return ELibraRuntimeResult::Ok;
}

void ULibraEffectComponent::ReleaseGpuPlayback()
{
    auto playback = GpuPlayback;
    GpuPlayback = nullptr;
    if (playback)
    {
        playback->DestroyComponent();
    }
}
