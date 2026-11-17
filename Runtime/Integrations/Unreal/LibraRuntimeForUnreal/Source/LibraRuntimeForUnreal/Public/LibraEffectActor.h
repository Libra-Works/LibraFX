#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "LibraEffectActor.generated.h"

class ULibraEffectComponent;

UCLASS()
class LIBRARUNTIMEFORUNREAL_API ALibraEffectActor : public AActor
{
    GENERATED_BODY()

public:
    ALibraEffectActor();

    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Libra Runtime")
    TObjectPtr<ULibraEffectComponent> EffectComponent;
};
