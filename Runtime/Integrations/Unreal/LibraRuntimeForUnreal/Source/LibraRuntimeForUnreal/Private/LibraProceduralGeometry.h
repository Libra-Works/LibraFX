#pragma once
#include "CoreMinimal.h"
#include "DynamicMeshBuilder.h"
#include "RenderCommandFence.h"

class FPrimitiveSceneProxy;
class FMeshElementCollector;
class FMaterialRenderProxy;

// One retained GPU geometry allocation shared by every draw of a scope producer.
class FLibraProceduralGeometry final
{
public:
    explicit FLibraProceduralGeometry(ERHIFeatureLevel::Type featureLevel);
    ~FLibraProceduralGeometry();
    void Submit(TArray<FDynamicMeshVertex>& vertices, TArray<uint32>& indices,
        bool indicesChanged, FRenderCommandFence& fence);
    void Draw(const FPrimitiveSceneProxy& proxy, const FMaterialRenderProxy* material,
        int32 viewIndex, FMeshElementCollector& collector) const;
private:
    struct FState;
    TSharedPtr<FState, ESPMode::ThreadSafe> State;
};
