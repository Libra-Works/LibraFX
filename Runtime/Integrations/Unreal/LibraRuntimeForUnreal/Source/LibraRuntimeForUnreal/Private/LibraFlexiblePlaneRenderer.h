#pragma once

#include "CoreMinimal.h"
#include "DynamicMeshBuilder.h"
#include "RenderCommandFence.h"
#include "UObject/StrongObjectPtr.h"
#include "liberafx/host/LbFlexiblePlaneAdapter.h"

class AActor;
class UTexture;
class UMaterialInterface;
class UMaterialInstanceDynamic;
class ULibraSpawnableComponent;
class FLibraNativeFlexiblePlanePrimitive;
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
    static bool Create(ULibraSpawnableComponent& owner, const FLibraFlexiblePlaneDefinition& definition,
        UTexture* texture, UMaterialInterface* material, const FTransform& worldTransform,
        FName textureParameter,
        TUniquePtr<FLibraFlexiblePlaneRenderer>& output, FString& error);
    ~FLibraFlexiblePlaneRenderer();

    // Callback reads a root or a particular child VM; history is shared only
    // within that same root/child instance scope and reset on reuse/seek.
    bool Update(double time, const FTransform& worldTransform, const FVector& cameraWorld,
        const FLibraFlexiblePlaneRead& read,
        FLibraFlexiblePlaneHistoryScope& history, FString& error);
    void SetOwnerPresentation(const FTransform& worldTransform, bool visible);
    uint64 GetSubmittedBytes() const;
private:
    struct FGeometryUploadBuffer
    {
        TArray<FDynamicMeshVertex> Vertices;
        TArray<uint32> Indices;
        // The proxy swaps its previous geometry into this slot; reuse it after the RT command.
        FRenderCommandFence Fence;
    };

    explicit FLibraFlexiblePlaneRenderer(const FLibraFlexiblePlaneDefinition& definition);
    FLibraFlexiblePlaneDefinition definition_;
    liberafx::flexible_adapter::Geometry geometry_;
    TStrongObjectPtr<UMaterialInstanceDynamic> materialInstance_;
    FGeometryUploadBuffer geometryUploadBuffers_[2];
    int32 nextGeometryUploadBuffer_ = 0;
    TUniquePtr<FLibraNativeFlexiblePlanePrimitive> primitive_;
};
