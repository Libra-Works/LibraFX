#include "LibraFlexiblePlaneComponent.h"
#include "LibraFlexiblePlaneProxy.h"
#include "Materials/MaterialInterface.h"

void ULibraFlexiblePlaneComponent::SetRibbon(TArray<FDynamicMeshVertex>&& vertices,
    TArray<uint32>&& indices)
{
    check(IsInGameThread());
    vertices_ = MoveTemp(vertices);
    indices_ = MoveTemp(indices);
    UpdateBounds();
    MarkRenderStateDirty();
}

FPrimitiveSceneProxy* ULibraFlexiblePlaneComponent::CreateSceneProxy()
{
    if (vertices_.IsEmpty() || indices_.IsEmpty() || !GetMaterial(0))
    {
        return nullptr;
    }
    return new FLibraFlexiblePlaneProxy(*this);
}

FBoxSphereBounds ULibraFlexiblePlaneComponent::CalcBounds(const FTransform& localToWorld) const
{
    auto bounds = FBox(ForceInit);
    for (const auto& vertex : vertices_)
    {
        bounds += FVector(vertex.Position);
    }
    if (!bounds.IsValid)
    {
        bounds += FVector::ZeroVector;
    }
    return FBoxSphereBounds(bounds).TransformBy(localToWorld);
}

int32 ULibraFlexiblePlaneComponent::GetNumMaterials() const
{
    return 1;
}

void ULibraFlexiblePlaneComponent::GetUsedMaterials(TArray<UMaterialInterface*>& materials, bool) const
{
    if (auto* material = GetMaterial(0))
    {
        materials.Add(material);
    }
}
