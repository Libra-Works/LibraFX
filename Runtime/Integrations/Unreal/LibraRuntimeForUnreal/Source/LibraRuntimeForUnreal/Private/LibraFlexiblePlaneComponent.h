#pragma once

#include "CoreMinimal.h"
#include "Components/MeshComponent.h"
#include "DynamicMeshBuilder.h"
#include "LibraFlexiblePlaneComponent.generated.h"

// The game thread publishes complete immutable snapshots to a scene proxy.
UCLASS(Transient)
class ULibraFlexiblePlaneComponent final : public UMeshComponent
{
    GENERATED_BODY()
public:
    void SetRibbon(TArray<FDynamicMeshVertex>&& vertices, TArray<uint32>&& indices);
    virtual FPrimitiveSceneProxy* CreateSceneProxy() override;
    virtual FBoxSphereBounds CalcBounds(const FTransform& localToWorld) const override;
    virtual int32 GetNumMaterials() const override;
    virtual void GetUsedMaterials(TArray<UMaterialInterface*>& materials, bool debug = false) const override;

    const TArray<FDynamicMeshVertex>& GetRibbonVertices() const { return vertices_; }
    const TArray<uint32>& GetRibbonIndices() const { return indices_; }
private:
    TArray<FDynamicMeshVertex> vertices_;
    TArray<uint32> indices_;
};
