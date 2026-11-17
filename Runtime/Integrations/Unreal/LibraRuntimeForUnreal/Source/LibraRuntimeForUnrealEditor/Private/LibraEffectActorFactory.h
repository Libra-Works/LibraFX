#pragma once

#include "ActorFactories/ActorFactory.h"
#include "LibraEffectActorFactory.generated.h"

UCLASS()
class ULibraEffectActorFactory : public UActorFactory
{
    GENERATED_BODY()

public:
    ULibraEffectActorFactory();
    virtual bool CanCreateActorFrom(const FAssetData& assetData, FText& error) override;
    virtual UObject* GetAssetFromActorInstance(AActor* actor) override;

protected:
    virtual void PostSpawnActor(UObject* asset, AActor* actor) override;
};
