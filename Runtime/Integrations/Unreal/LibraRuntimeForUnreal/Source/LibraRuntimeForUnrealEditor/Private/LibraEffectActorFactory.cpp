#include "LibraEffectActorFactory.h"

#include "LibraEffectActor.h"
#include "LibraEffectAsset.h"
#include "LibraEffectComponent.h"
#include "LibraGpuEffectAsset.h"
#include "LibraSpawnableAsset.h"
#include "AssetRegistry/AssetData.h"

ULibraEffectActorFactory::ULibraEffectActorFactory()
{
    DisplayName = NSLOCTEXT("LibraRuntimeForUnreal", "EffectActor", "Libra Effect");
    NewActorClass = ALibraEffectActor::StaticClass();
    bShowInEditorQuickMenu = false;
    bUseSurfaceOrientation = false;
}

bool ULibraEffectActorFactory::CanCreateActorFrom(const FAssetData& assetData, FText& error)
{
    if (assetData.IsValid() && (assetData.IsInstanceOf(ULibraEffectAsset::StaticClass()) ||
        assetData.IsInstanceOf(ULibraSpawnableAsset::StaticClass()) ||
        assetData.IsInstanceOf(ULibraGpuEffectAsset::StaticClass()))) return true;
    error = NSLOCTEXT("LibraRuntimeForUnreal", "InvalidEffectActorAsset",
        "Select a LIBERA FX compiled effect, effect package, or GPU effect.");
    return false;
}

void ULibraEffectActorFactory::PostSpawnActor(UObject* asset, AActor* actor)
{
    Super::PostSpawnActor(asset, actor);
    auto effectActor = CastChecked<ALibraEffectActor>(actor);
    auto component = effectActor->EffectComponent;
    component->Modify();
    component->Effect = Cast<ULibraEffectAsset>(asset);
    component->Package = Cast<ULibraSpawnableAsset>(asset);
    component->GpuEffect = Cast<ULibraGpuEffectAsset>(asset);
    if (actor->HasActorBegunPlay() && component->bAutoPlay) component->Play();
}

UObject* ULibraEffectActorFactory::GetAssetFromActorInstance(AActor* actor)
{
    auto effectActor = Cast<ALibraEffectActor>(actor);
    if (!effectActor || !effectActor->EffectComponent) return nullptr;
    auto component = effectActor->EffectComponent;
    if (component->Package) return component->Package.Get();
    if (component->Effect) return component->Effect.Get();
    return component->GpuEffect.Get();
}
