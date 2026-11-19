#include "LibraFlexiblePlaneProxy.h"
#include "LibraFlexiblePlaneComponent.h"
#include "Materials/MaterialInterface.h"
#include "Misc/EngineVersionComparison.h"
#include "SceneManagement.h"
#include "SceneView.h"

FLibraFlexiblePlaneProxy::FLibraFlexiblePlaneProxy(const ULibraFlexiblePlaneComponent& component)
    : FPrimitiveSceneProxy(&component), vertices_(component.GetRibbonVertices()),
      indices_(component.GetRibbonIndices()), materialProxy_(component.GetMaterial(0)->GetRenderProxy()),
#if UE_VERSION_OLDER_THAN(5, 7, 0)
      relevance_(component.GetMaterial(0)->GetRelevance_Concurrent(GetScene().GetFeatureLevel()))
#else
      relevance_(component.GetMaterial(0)->GetRelevance_Concurrent(GetScene().GetShaderPlatform()))
#endif
{
}

SIZE_T FLibraFlexiblePlaneProxy::GetTypeHash() const
{
    static auto unique = 0;
    return reinterpret_cast<SIZE_T>(&unique);
}

void FLibraFlexiblePlaneProxy::GetDynamicMeshElements(const TArray<const FSceneView*>& views,
    const FSceneViewFamily&, uint32 visibility, FMeshElementCollector& collector) const
{
    for (auto index = 0; index < views.Num(); ++index)
    {
        if ((visibility & (1u << index)) == 0)
        {
            continue;
        }
        auto builder = FDynamicMeshBuilder(views[index]->GetFeatureLevel());
        builder.AddVertices(vertices_);
        for (auto triangle = 0; triangle < indices_.Num(); triangle += 3)
        {
            builder.AddTriangle(static_cast<int32>(indices_[triangle]),
                static_cast<int32>(indices_[triangle + 1]), static_cast<int32>(indices_[triangle + 2]));
        }
        builder.GetMesh(GetLocalToWorld(), materialProxy_, SDPG_World, true, false, index, collector);
    }
}

FPrimitiveViewRelevance FLibraFlexiblePlaneProxy::GetViewRelevance(const FSceneView* view) const
{
    auto relevance = FPrimitiveViewRelevance{};
    relevance.bDrawRelevance = IsShown(view);
    relevance.bDynamicRelevance = true;
    relevance.bRenderInMainPass = ShouldRenderInMainPass();
    relevance.bShadowRelevance = false;
    relevance_.SetPrimitiveViewRelevance(relevance);
    return relevance;
}

uint32 FLibraFlexiblePlaneProxy::GetMemoryFootprint() const
{
    return static_cast<uint32>(sizeof(*this) + FPrimitiveSceneProxy::GetAllocatedSize() +
        vertices_.GetAllocatedSize() + indices_.GetAllocatedSize());
}
