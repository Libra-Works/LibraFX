#include "LibraEffectActor.h"

#include "LibraEffectComponent.h"
#include "Components/SceneComponent.h"

ALibraEffectActor::ALibraEffectActor()
{
    PrimaryActorTick.bCanEverTick = false;
    RootComponent = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
    EffectComponent = CreateDefaultSubobject<ULibraEffectComponent>(TEXT("LibraEffect"));
}
