#pragma once

#include "CoreMinimal.h"
#include "DynamicMeshBuilder.h"
#include "PrimitiveSceneInfoData.h"
#include "PrimitiveSceneDesc.h"
#include "RenderCommandFence.h"
#include "UObject/StrongObjectPtr.h"

class ULibraSpawnableComponent;
class UStaticMesh;
class UMaterialInterface;

class FLibraNativeInstancedMeshPrimitive final
{
public:
    FLibraNativeInstancedMeshPrimitive();
    bool Create(ULibraSpawnableComponent& owner, UStaticMesh* mesh,
        TArrayView<TObjectPtr<UMaterialInterface>> materials, const FTransform& primitiveTransform);
    bool CreateWithInstances(ULibraSpawnableComponent& owner, UStaticMesh* mesh,
        TArrayView<TObjectPtr<UMaterialInterface>> materials,
        TConstArrayView<FTransform> worldTransforms, TConstArrayView<FLinearColor> customData,
        const FTransform& primitiveTransform);
    bool Update(TConstArrayView<FTransform> worldTransforms,
        TConstArrayView<FLinearColor> customData, const FTransform& primitiveTransform);
    void SetVisible(bool visible);
    void Remove();
    ~FLibraNativeInstancedMeshPrimitive();
    int32 GetAllocatedInstanceCapacity() const;

    int32 LiveInstanceCount = 0;
    uint64 SubmittedBytes = 0;
    FBoxSphereBounds WorldBounds;

private:
    struct FImpl;
    TUniquePtr<FImpl> Impl;
};

class FLibraNativeMeshPrimitive final
{
public:
    bool Create(ULibraSpawnableComponent& owner, UStaticMesh* mesh,
        TArrayView<TObjectPtr<UMaterialInterface>> materials, const FTransform& worldTransform,
        int32 translucentSortPriority, FGuid drawId, FGuid containerId, int64 instanceId);
    void SetTransform(const FTransform& worldTransform);
    void SetVisible(bool visible);
    void Remove();
    bool HasSceneProxy() const;
    ~FLibraNativeMeshPrimitive();

    FTransform WorldTransform = FTransform::Identity;
    FBoxSphereBounds WorldBounds;
    uint64 SubmittedBytes = 0;
    FGuid DrawId;
    FGuid ContainerId;
    int64 InstanceId = 0;

private:
    FPrimitiveSceneDesc SceneDesc;
    FPrimitiveSceneInfoData SceneData;
    FCustomPrimitiveData CustomPrimitiveData;
    FBoxSphereBounds LocalBounds;
    FSceneInterface* Scene = nullptr;
    FRenderCommandFence RemovalFence;
    TStrongObjectPtr<UObject> OwnerObject;
    TStrongObjectPtr<UStaticMesh> MeshObject;
    TArray<TStrongObjectPtr<UMaterialInterface>> MaterialObjects;
    bool bAdded = false;
    bool bRemovalPending = false;
};

class FLibraNativeFlexiblePlanePrimitive final
{
public:
    bool Create(ULibraSpawnableComponent& owner, UMaterialInterface* material,
        const FTransform& worldTransform);
    void SetGeometry(TArray<FDynamicMeshVertex>& vertices, TArray<uint32>& indices,
        FRenderCommandFence& uploadFence, const FTransform& worldTransform);
    void SetTransform(const FTransform& worldTransform);
    void SetVisible(bool visible);
    void Remove();
    ~FLibraNativeFlexiblePlanePrimitive();

    FTransform WorldTransform = FTransform::Identity;
    FBoxSphereBounds WorldBounds;
    uint64 SubmittedBytes = 0;

private:
    FPrimitiveSceneDesc SceneDesc;
    FPrimitiveSceneInfoData SceneData;
    FCustomPrimitiveData CustomPrimitiveData;
    FBoxSphereBounds LocalBounds;
    FSceneInterface* Scene = nullptr;
    FRenderCommandFence RemovalFence;
    TStrongObjectPtr<UObject> OwnerObject;
    TStrongObjectPtr<UMaterialInterface> MaterialObject;
    bool bAdded = false;
    bool bRemovalPending = false;
};
