#pragma once

#include "DynamicMeshBuilder.h"
#include "Materials/MaterialRelevance.h"
#include "PrimitiveSceneProxy.h"

class ULibraFlexiblePlaneComponent;

class FLibraFlexiblePlaneProxy final : public FPrimitiveSceneProxy
{
public:
    explicit FLibraFlexiblePlaneProxy(const ULibraFlexiblePlaneComponent& component);
    virtual SIZE_T GetTypeHash() const override;
    virtual void GetDynamicMeshElements(const TArray<const FSceneView*>& views,
        const FSceneViewFamily& family, uint32 visibility, FMeshElementCollector& collector) const override;
    virtual FPrimitiveViewRelevance GetViewRelevance(const FSceneView* view) const override;
    virtual uint32 GetMemoryFootprint() const override;
private:
    TArray<FDynamicMeshVertex> vertices_;
    TArray<uint32> indices_;
    const FMaterialRenderProxy* materialProxy_;
    FMaterialRelevance relevance_;
};
