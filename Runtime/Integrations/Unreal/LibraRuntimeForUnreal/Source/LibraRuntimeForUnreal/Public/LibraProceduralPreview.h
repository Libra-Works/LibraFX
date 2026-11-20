#pragma once
#include "CoreMinimal.h"
#include "LibraRuntimeTypes.h"

class AActor;
class FJsonObject;
class ULibraEffectComponent;

// Root-scene editor preview uses the same resources as compiled package playback.
class LIBRARUNTIMEFORUNREAL_API FLibraProceduralPreview final
{
public:
    FLibraProceduralPreview();
    ~FLibraProceduralPreview();
    bool Configure(AActor& actor, const TSharedPtr<FJsonObject>& scene, FString& error);
    bool Update(ULibraEffectComponent& effect, double time, FString& error);
    void Clear();
private:
    struct FImpl;
    TUniquePtr<FImpl> Impl;
};
