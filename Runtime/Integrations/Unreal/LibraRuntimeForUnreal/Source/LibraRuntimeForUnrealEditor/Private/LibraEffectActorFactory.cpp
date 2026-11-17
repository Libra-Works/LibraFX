#include "LibraEffectActorFactory.h"

#include "LibraEffectActor.h"
#include "LibraEffectAsset.h"
#include "LibraEffectComponent.h"
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
        assetData.IsInstanceOf(ULibraSpawnableAsset::StaticClass()))) return true;
    error = NSLOCTEXT("LibraRuntimeForUnreal", "InvalidEffectActorAsset",
        "Select a LIBERA FX compiled effect or effect package.");
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
    if (actor->HasActorBegunPlay() && component->bAutoPlay) component->Play();
}

UObject* ULibraEffectActorFactory::GetAssetFromActorInstance(AActor* actor)
{
    auto effectActor = Cast<ALibraEffectActor>(actor);
    if (!effectActor || !effectActor->EffectComponent) return nullptr;
    auto component = effectActor->EffectComponent;
    return component->Package ? static_cast<UObject*>(component->Package.Get()) : component->Effect.Get();
}
