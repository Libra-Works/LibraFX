#pragma once

#include "CoreMinimal.h"
#include "UObject/StrongObjectPtr.h"
#include "liberafx/host/LbFlexiblePlaneAdapter.h"

class AActor;
class USceneComponent;
class UTexture;
class UMaterialInterface;
class ULibraFlexiblePlaneComponent;
class FJsonObject;

using FLibraFlexiblePlaneHistoryScope = liberafx::flexible_adapter::HistoryScope;
using FLibraFlexiblePlaneRead = TFunction<LibraResult(const uint8*, double*, uint8&)>;

struct FLibraFlexiblePlaneDefinition
{
    liberafx::flexible_adapter::Definition native;
    FString assetPath;
    FGuid nodeId;
};

// All calls and destruction must be on the game thread. Resolve texture and
// a compatible material explicitly before create; no fallback is substituted.
class FLibraFlexiblePlaneRenderer final
{
public:
    static bool Prepare(const TSharedPtr<FJsonObject>& node, FLibraFlexiblePlaneDefinition& output,
        FString& error);
    static bool Create(AActor& actor, USceneComponent& parent, const FLibraFlexiblePlaneDefinition& definition,
        UTexture* texture, UMaterialInterface* material, FName textureParameter,
        TUniquePtr<FLibraFlexiblePlaneRenderer>& output, FString& error);
    ~FLibraFlexiblePlaneRenderer();

    // Callback reads a root or a particular child VM; history is shared only
    // within that same root/child instance scope and reset on reuse/seek.
    bool Update(double time, const FVector& cameraWorld, const FLibraFlexiblePlaneRead& read,
        FLibraFlexiblePlaneHistoryScope& history, FString& error);
private:
    explicit FLibraFlexiblePlaneRenderer(const FLibraFlexiblePlaneDefinition& definition);
    FLibraFlexiblePlaneDefinition definition_;
    liberafx::flexible_adapter::Geometry geometry_;
    TStrongObjectPtr<ULibraFlexiblePlaneComponent> component_;
};
