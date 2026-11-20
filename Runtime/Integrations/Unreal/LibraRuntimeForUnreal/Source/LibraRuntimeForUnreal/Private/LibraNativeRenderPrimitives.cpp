#include "LibraNativeRenderPrimitives.h"
#include "LibraProceduralGeometry.h"

#include "LibraSpawnableComponent.h"
#include "Engine/StaticMesh.h"
#include "GameFramework/Actor.h"
#include "InstanceDataSceneProxy.h"
#include "InstancedStaticMeshSceneProxyDesc.h"
#include "Materials/MaterialInterface.h"
#include "Misc/EngineVersionComparison.h"
#include "PrimitiveSceneProxy.h"
#include "PrimitiveSceneProxyDesc.h"
#include "RenderCommandFence.h"
#include "SceneManagement.h"
#include "SceneView.h"
#include "StaticMeshSceneProxy.h"
#include "StaticMeshSceneProxyDesc.h"

class FLibraMutableInstanceDataSceneProxy final : public FInstanceDataSceneProxy
{
public:
    explicit FLibraMutableInstanceDataSceneProxy(FInstanceSceneDataBuffers&& buffers)
        : FInstanceDataSceneProxy(MoveTemp(buffers))
    {
    }

    int32 GetAllocatedInstanceCapacity() const
    {
        return AllocatedInstanceCapacity.load(std::memory_order_relaxed);
    }

    void UpdateData(TArray<FRenderTransform>& transforms, TArray<float>& customData,
        const FTransform& primitiveTransform)
    {
        ENQUEUE_RENDER_COMMAND(LibraInstanceDataUpdate)(
            [this, &transforms, &customData, primitiveTransform](FRHICommandListImmediate&)
            {
                WriteData(transforms, customData, primitiveTransform);
            });
    }

    void InitializeData(TArray<FRenderTransform>& transforms, TArray<float>& customData,
        const FTransform& primitiveTransform)
    {
        WriteData(transforms, customData, primitiveTransform);
    }

private:
    void WriteData(TArray<FRenderTransform>& transforms, TArray<float>& customData,
        const FTransform& primitiveTransform)
    {
        auto& buffers = InstanceSceneDataBuffers;
        auto writerTag = FInstanceSceneDataBuffers::FAccessTag(PointerHash(this));
        auto view = buffers.BeginWriteAccess(writerTag);
        buffers.SetPrimitiveLocalToWorld(primitiveTransform.ToMatrixWithScale(), writerTag);
        for (auto& transform : transforms)
        {
            transform = buffers.ComputeInstanceToPrimitiveRelative(
                transform.ToMatrix44f(), writerTag);
        }
        view.InstanceToPrimitiveRelative.Reset();
        view.InstanceToPrimitiveRelative.Append(transforms);
        view.PrevInstanceToPrimitiveRelative.Reset();
        view.InstanceCustomData.Reset();
        view.InstanceCustomData.Append(customData);
        view.NumCustomDataFloats = 4;
        view.Flags.bHasPerInstanceCustomData = true;
        AllocatedInstanceCapacity.store(FMath::Min(
            view.InstanceToPrimitiveRelative.Max(), view.InstanceCustomData.Max() / 4),
            std::memory_order_relaxed);
        buffers.EndWriteAccess(writerTag);
    }

    std::atomic<int32> AllocatedInstanceCapacity = 0;
};

class FLibraInstancedStaticMeshSceneProxy final : public FInstancedStaticMeshSceneProxy
{
public:
    FLibraInstancedStaticMeshSceneProxy(const FInstancedStaticMeshSceneProxyDesc& description,
        ERHIFeatureLevel::Type featureLevel)
        : FInstancedStaticMeshSceneProxy(description, featureLevel)
    {
    }

    virtual FPrimitiveViewRelevance GetViewRelevance(const FSceneView* view) const override
    {
        auto relevance = FInstancedStaticMeshSceneProxy::GetViewRelevance(view);
        if (!bOwnerVisible.load(std::memory_order_relaxed))
        {
            relevance.bDrawRelevance = false;
            relevance.bShadowRelevance = false;
        }
        return relevance;
    }

    void SetOwnerVisible(bool visible)
    {
        bOwnerVisible.store(visible, std::memory_order_relaxed);
    }

private:
    std::atomic<bool> bOwnerVisible = true;
};

class FLibraStaticMeshSceneProxy final : public FStaticMeshSceneProxy
{
public:
    explicit FLibraStaticMeshSceneProxy(const FStaticMeshSceneProxyDesc& description)
        : FStaticMeshSceneProxy(description, false)
    {
    }

    virtual FPrimitiveViewRelevance GetViewRelevance(const FSceneView* view) const override
    {
        auto relevance = FStaticMeshSceneProxy::GetViewRelevance(view);
        if (!bOwnerVisible.load(std::memory_order_relaxed))
        {
            relevance.bDrawRelevance = false;
            relevance.bShadowRelevance = false;
        }
        return relevance;
    }

    void SetOwnerVisible(bool visible)
    {
        bOwnerVisible.store(visible, std::memory_order_relaxed);
    }

private:
    std::atomic<bool> bOwnerVisible = true;
};

class FLibraFlexiblePlaneSceneProxy final : public FPrimitiveSceneProxy
{
public:
    FLibraFlexiblePlaneSceneProxy(const FPrimitiveSceneProxyDesc& description,
        UMaterialInterface& material, TSharedPtr<FLibraProceduralGeometry, ESPMode::ThreadSafe> proceduralGeometry)
        : FPrimitiveSceneProxy(description), ProceduralGeometry(MoveTemp(proceduralGeometry))
    {
        MaterialProxy = material.GetRenderProxy();
#if UE_VERSION_OLDER_THAN(5, 7, 0)
        MaterialRelevance = material.GetRelevance_Concurrent(GetScene().GetFeatureLevel());
#else
        MaterialRelevance = material.GetRelevance_Concurrent(GetScene().GetShaderPlatform());
#endif
    }

    virtual SIZE_T GetTypeHash() const override
    {
        static auto unique = 0;
        return reinterpret_cast<SIZE_T>(&unique);
    }

    virtual void GetDynamicMeshElements(const TArray<const FSceneView*>& views,
        const FSceneViewFamily&, uint32 visibility, FMeshElementCollector& collector) const override
    {
        for (auto viewIndex = 0; viewIndex < views.Num(); ++viewIndex)
        {
            if ((visibility & (1u << viewIndex)) == 0 || !bOwnerVisible.load(std::memory_order_relaxed))
            {
                continue;
            }
            if (ProceduralGeometry)
            {
                ProceduralGeometry->Draw(*this, MaterialProxy, viewIndex, collector);
                continue;
            }
            auto builder = FDynamicMeshBuilder(views[viewIndex]->GetFeatureLevel());
            builder.AddVertices(Vertices);
            for (auto triangle = 0; triangle < Indices.Num(); triangle += 3)
            {
                builder.AddTriangle(static_cast<int32>(Indices[triangle]),
                    static_cast<int32>(Indices[triangle + 1]),
                    static_cast<int32>(Indices[triangle + 2]));
            }
            builder.GetMesh(GetLocalToWorld(), MaterialProxy, SDPG_World, true, false,
                viewIndex, collector);
        }
    }

    virtual FPrimitiveViewRelevance GetViewRelevance(const FSceneView* view) const override
    {
        auto relevance = FPrimitiveViewRelevance{};
        relevance.bDrawRelevance = IsShown(view) && bOwnerVisible.load(std::memory_order_relaxed);
        relevance.bDynamicRelevance = true;
        relevance.bRenderInMainPass = ShouldRenderInMainPass();
        relevance.bShadowRelevance = false;
        MaterialRelevance.SetPrimitiveViewRelevance(relevance);
        return relevance;
    }

    virtual uint32 GetMemoryFootprint() const override
    {
        return static_cast<uint32>(sizeof(*this) + GetAllocatedSize() +
            Vertices.GetAllocatedSize() + Indices.GetAllocatedSize());
    }

    void SetOwnerVisible(bool visible)
    {
        bOwnerVisible.store(visible, std::memory_order_relaxed);
    }

    void SetGeometry(TArray<FDynamicMeshVertex>& vertices, TArray<uint32>& indices)
    {
        Swap(Vertices, vertices);
        Swap(Indices, indices);
    }

private:
    TArray<FDynamicMeshVertex> Vertices;
    TArray<uint32> Indices;
    const FMaterialRenderProxy* MaterialProxy = nullptr;
    FMaterialRelevance MaterialRelevance;
    std::atomic<bool> bOwnerVisible = true;
    TSharedPtr<FLibraProceduralGeometry, ESPMode::ThreadSafe> ProceduralGeometry;
};

struct FLibraFlexiblePlaneSceneProxyDesc final : FPrimitiveSceneProxyDesc
{
    UMaterialInterface* Material = nullptr;

    virtual void GetUsedMaterials(TArray<UMaterialInterface*>& outMaterials,
        bool bGetDebugMaterials = false) const override
    {
        static_cast<void>(bGetDebugMaterials);
        if (Material)
        {
            outMaterials.Add(Material);
        }
    }
};

namespace LibraNativeRenderPrimitives
{
void SetInitialSceneState(FPrimitiveSceneDesc& sceneDesc, FPrimitiveSceneInfoData& sceneData,
    FCustomPrimitiveData& customPrimitiveData, UObject& ownerObject, FSceneInterface& scene,
    const FTransform& worldTransform, const FBoxSphereBounds& localBounds,
    FBoxSphereBounds& worldBounds)
{
    sceneDesc.PrimitiveSceneData = &sceneData;
    sceneDesc.PrimitiveUObject = &ownerObject;
    sceneDesc.Mobility = EComponentMobility::Movable;
    sceneDesc.RenderMatrix = worldTransform.ToMatrixWithScale();
    sceneDesc.AttachmentRootPosition = worldTransform.GetLocation();
    sceneDesc.LocalBounds = localBounds;
    sceneDesc.Bounds = localBounds.TransformBy(worldTransform);
    worldBounds = sceneDesc.Bounds;
    static_cast<void>(customPrimitiveData);
    static_cast<void>(scene);
}
}

struct FLibraNativeInstancedMeshPrimitive::FImpl
{
    struct FUploadBuffer
    {
        TArray<FRenderTransform> Transforms;
        TArray<float> CustomData;
        // Keep the referenced arrays stable until their queued render-thread copy has finished.
        FRenderCommandFence Fence;
    };

    FPrimitiveSceneDesc SceneDesc;
    FPrimitiveSceneInfoData SceneData;
    FCustomPrimitiveData CustomPrimitiveData;
    FSceneInterface* Scene = nullptr;
    FRenderCommandFence RemovalFence;
    TStrongObjectPtr<UObject> OwnerObject;
    TStrongObjectPtr<UStaticMesh> Mesh;
    TArray<TStrongObjectPtr<UMaterialInterface>> Materials;
    TSharedPtr<FLibraMutableInstanceDataSceneProxy, ESPMode::ThreadSafe> InstanceDataProxy;
    FUploadBuffer UploadBuffers[2];
    int32 NextUploadBuffer = 0;
    FBoxSphereBounds MeshBounds;
    bool bAdded = false;
    bool bRemovalPending = false;
};

bool FLibraNativeInstancedMeshPrimitive::Create(ULibraSpawnableComponent& owner, UStaticMesh* mesh,
    TArrayView<TObjectPtr<UMaterialInterface>> materials, const FTransform& primitiveTransform)
{
    auto transforms = TArray<FTransform>{};
    auto customData = TArray<FLinearColor>{};
    return CreateWithInstances(owner, mesh, materials, transforms, customData,
        primitiveTransform);
}

bool FLibraNativeInstancedMeshPrimitive::CreateWithInstances(ULibraSpawnableComponent& owner,
    UStaticMesh* mesh, TArrayView<TObjectPtr<UMaterialInterface>> materials,
    TConstArrayView<FTransform> worldTransforms, TConstArrayView<FLinearColor> customData,
    const FTransform& primitiveTransform)
{
    check(IsInGameThread());
    auto* actor = owner.GetOwner();
    auto* world = actor ? actor->GetWorld() : nullptr;
    if (!actor || !world || !world->Scene || !mesh || !mesh->GetRenderData() ||
        materials.IsEmpty() || worldTransforms.Num() != customData.Num())
    {
        return false;
    }
    if (!mesh->GetRenderData()->IsInitialized())
    {
        mesh->InitResources();
    }
    Impl = MakeUnique<FImpl>();
    auto& impl = *Impl;
    impl.Scene = world->Scene;
    impl.OwnerObject.Reset(&owner);
    impl.Mesh.Reset(mesh);
    impl.MeshBounds = mesh->GetBounds();
    for (const auto& material : materials)
    {
        if (!material.Get())
        {
            return false;
        }
        impl.Materials.Emplace(material.Get());
    }

    auto instanceBuffers = FInstanceSceneDataBuffers{};
    auto writerTag = FInstanceSceneDataBuffers::FAccessTag(PointerHash(&instanceBuffers));
    auto view = instanceBuffers.BeginWriteAccess(writerTag);
    instanceBuffers.SetPrimitiveLocalToWorld(primitiveTransform.ToMatrixWithScale(), writerTag);
    view.InstanceLocalBounds.Add(FRenderBounds(impl.MeshBounds));
    view.NumCustomDataFloats = 4;
    view.Flags.bHasPerInstanceCustomData = true;
    instanceBuffers.EndWriteAccess(writerTag);
    impl.InstanceDataProxy = MakeShared<FLibraMutableInstanceDataSceneProxy, ESPMode::ThreadSafe>(
        MoveTemp(instanceBuffers));

    auto initialTransforms = TArray<FRenderTransform>{};
    auto initialCustomData = TArray<float>{};
    auto localBounds = FBox(ForceInit);
    initialTransforms.Reserve(worldTransforms.Num());
    initialCustomData.Reserve(worldTransforms.Num() * 4);
    for (auto index = 0; index < worldTransforms.Num(); ++index)
    {
        const auto localTransform = worldTransforms[index].GetRelativeTransform(primitiveTransform);
        initialTransforms.Emplace(FMatrix44f(localTransform.ToMatrixWithScale()));
        localBounds += impl.MeshBounds.TransformBy(localTransform).GetBox();
        const auto& color = customData[index];
        initialCustomData.Add(static_cast<float>(color.R));
        initialCustomData.Add(static_cast<float>(color.G));
        initialCustomData.Add(static_cast<float>(color.B));
        initialCustomData.Add(static_cast<float>(color.A));
    }
    if (!localBounds.IsValid)
    {
        localBounds = impl.MeshBounds.GetBox();
    }
    const auto localBoxBounds = FBoxSphereBounds(localBounds);
    impl.InstanceDataProxy->InitializeData(initialTransforms, initialCustomData,
        primitiveTransform);

    impl.SceneDesc.PrimitiveSceneData = &impl.SceneData;
    impl.SceneDesc.PrimitiveUObject = &owner;
    impl.SceneDesc.Mobility = EComponentMobility::Movable;
    impl.SceneDesc.RenderMatrix = primitiveTransform.ToMatrixWithScale();
    impl.SceneDesc.AttachmentRootPosition = primitiveTransform.GetLocation();
    impl.SceneDesc.LocalBounds = localBoxBounds;
    impl.SceneDesc.Bounds = localBoxBounds.TransformBy(primitiveTransform);

    auto materialOverrides = TArray<TObjectPtr<UMaterialInterface>>{};
    materialOverrides.Reserve(impl.Materials.Num());
    for (const auto& material : impl.Materials)
    {
        materialOverrides.Add(material.Get());
    }
    auto proxyDesc = FInstancedStaticMeshSceneProxyDesc{};
    proxyDesc.StaticMesh = mesh;
    proxyDesc.OverrideMaterials = MakeArrayView(materialOverrides);
    proxyDesc.InstanceDataSceneProxy = impl.InstanceDataProxy;
    proxyDesc.Scene = world->Scene;
    proxyDesc.World = world;
    proxyDesc.Owner = actor;
    proxyDesc.Component = &owner;
    proxyDesc.CustomPrimitiveData = &impl.CustomPrimitiveData;
    proxyDesc.FeatureLevel = world->Scene->GetFeatureLevel();
    proxyDesc.Mobility = EComponentMobility::Movable;
    proxyDesc.CastShadow = false;
    proxyDesc.bCastDynamicShadow = false;
    proxyDesc.bCastStaticShadow = false;
    proxyDesc.bIsVisible = true;
    proxyDesc.bRenderInMainPass = true;
    proxyDesc.bIsInstancedStaticMesh = true;
    impl.SceneData.SceneProxy = new FLibraInstancedStaticMeshSceneProxy(proxyDesc,
        world->Scene->GetFeatureLevel());
    if (!impl.SceneData.SceneProxy)
    {
        return false;
    }
    impl.Scene->AddPrimitive(&impl.SceneDesc);
    impl.bAdded = true;
    WorldBounds = impl.SceneDesc.Bounds;
    LiveInstanceCount = worldTransforms.Num();
    SubmittedBytes = uint64(LiveInstanceCount) *
        (sizeof(FRenderTransform) + sizeof(float) * 4) + sizeof(FTransform) +
        sizeof(FBoxSphereBounds) * 2;
    return true;
}

bool FLibraNativeInstancedMeshPrimitive::Update(TConstArrayView<FTransform> worldTransforms,
    TConstArrayView<FLinearColor> customData, const FTransform& primitiveTransform)
{
    check(IsInGameThread());
    if (!Impl || worldTransforms.Num() != customData.Num() ||
        !Impl->InstanceDataProxy.IsValid() || !Impl->bAdded)
    {
        return false;
    }

    auto& upload = Impl->UploadBuffers[Impl->NextUploadBuffer];
    if (!upload.Fence.IsFenceComplete())
    {
        upload.Fence.Wait();
    }
    upload.Transforms.Reset();
    upload.CustomData.Reset();
    upload.Transforms.Reserve(worldTransforms.Num());
    upload.CustomData.Reserve(worldTransforms.Num() * 4);
    auto localBounds = FBox(ForceInit);
    for (auto index = 0; index < worldTransforms.Num(); ++index)
    {
        const auto localTransform = worldTransforms[index].GetRelativeTransform(primitiveTransform);
        upload.Transforms.Emplace(FMatrix44f(localTransform.ToMatrixWithScale()));
        localBounds += Impl->MeshBounds.TransformBy(localTransform).GetBox();
        const auto& color = customData[index];
        upload.CustomData.Add(static_cast<float>(color.R));
        upload.CustomData.Add(static_cast<float>(color.G));
        upload.CustomData.Add(static_cast<float>(color.B));
        upload.CustomData.Add(static_cast<float>(color.A));
    }
    if (!localBounds.IsValid)
    {
        localBounds += FVector::ZeroVector;
    }
    const auto localBoxBounds = FBoxSphereBounds(localBounds);
    WorldBounds = localBoxBounds.TransformBy(primitiveTransform);
    Impl->SceneDesc.RenderMatrix = primitiveTransform.ToMatrixWithScale();
    Impl->SceneDesc.AttachmentRootPosition = primitiveTransform.GetLocation();
    Impl->SceneDesc.LocalBounds = localBoxBounds;
    Impl->SceneDesc.Bounds = WorldBounds;
    Impl->InstanceDataProxy->UpdateData(upload.Transforms, upload.CustomData, primitiveTransform);
    upload.Fence.BeginFence();
    Impl->NextUploadBuffer = (Impl->NextUploadBuffer + 1) % UE_ARRAY_COUNT(Impl->UploadBuffers);
    Impl->Scene->UpdatePrimitiveInstances(&Impl->SceneDesc);
    Impl->Scene->UpdatePrimitiveTransform(&Impl->SceneDesc);
    LiveInstanceCount = worldTransforms.Num();
    SubmittedBytes = uint64(LiveInstanceCount) *
        (sizeof(FRenderTransform) + sizeof(float) * 4) + sizeof(FTransform) +
        sizeof(FBoxSphereBounds) * 2;
    return true;
}

int32 FLibraNativeInstancedMeshPrimitive::GetAllocatedInstanceCapacity() const
{
    if (!Impl || !Impl->InstanceDataProxy.IsValid())
    {
        return 0;
    }
    const auto* mutableProxy = static_cast<const FLibraMutableInstanceDataSceneProxy*>(
        Impl->InstanceDataProxy.Get());
    return mutableProxy->GetAllocatedInstanceCapacity();
}

void FLibraNativeInstancedMeshPrimitive::SetVisible(bool visible)
{
    check(IsInGameThread());
    if (Impl)
    {
        if (auto* proxy = static_cast<FLibraInstancedStaticMeshSceneProxy*>(Impl->SceneDesc.GetSceneProxy()))
        {
            proxy->SetOwnerVisible(visible);
        }
    }
}

void FLibraNativeInstancedMeshPrimitive::Remove()
{
    check(IsInGameThread());
    if (Impl && Impl->bAdded && Impl->Scene)
    {
        Impl->Scene->RemovePrimitive(&Impl->SceneDesc);
        Impl->Scene->ReleasePrimitive(&Impl->SceneDesc);
        Impl->bAdded = false;
        Impl->bRemovalPending = true;
        Impl->RemovalFence.BeginFence();
        LiveInstanceCount = 0;
    }
}

FLibraNativeInstancedMeshPrimitive::FLibraNativeInstancedMeshPrimitive() = default;

FLibraNativeInstancedMeshPrimitive::~FLibraNativeInstancedMeshPrimitive()
{
    check(IsInGameThread());
    Remove();
    if (Impl && Impl->bRemovalPending)
    {
        Impl->RemovalFence.Wait();
    }
}

bool FLibraNativeMeshPrimitive::Create(ULibraSpawnableComponent& owner, UStaticMesh* mesh,
    TArrayView<TObjectPtr<UMaterialInterface>> materials, const FTransform& worldTransform,
    int32 translucentSortPriority, FGuid drawId, FGuid containerId, int64 instanceId)
{
    check(IsInGameThread());
    auto* actor = owner.GetOwner();
    auto* world = actor ? actor->GetWorld() : nullptr;
    if (!actor || !world || !world->Scene || !mesh || !mesh->GetRenderData())
    {
        return false;
    }
    if (!mesh->GetRenderData()->IsInitialized())
    {
        mesh->InitResources();
    }

    auto proxyDesc = FStaticMeshSceneProxyDesc{};
    proxyDesc.StaticMesh = mesh;
    proxyDesc.OverrideMaterials = materials;
    proxyDesc.Scene = world->Scene;
    proxyDesc.World = world;
    proxyDesc.Owner = actor;
    proxyDesc.Component = &owner;
    proxyDesc.CustomPrimitiveData = &CustomPrimitiveData;
    proxyDesc.FeatureLevel = world->Scene->GetFeatureLevel();
    proxyDesc.Mobility = EComponentMobility::Movable;
    proxyDesc.CastShadow = false;
    proxyDesc.bCastDynamicShadow = false;
    proxyDesc.bCastStaticShadow = false;
    proxyDesc.bIsVisible = true;
    proxyDesc.bRenderInMainPass = true;
    proxyDesc.TranslucencySortPriority = translucentSortPriority;
    Scene = world->Scene;
    OwnerObject.Reset(&owner);
    MeshObject.Reset(mesh);
    MaterialObjects.Reset(materials.Num());
    for (const auto& material : materials)
    {
        MaterialObjects.Emplace(material.Get());
    }
    DrawId = drawId;
    ContainerId = containerId;
    InstanceId = instanceId;
    LocalBounds = mesh->GetBounds();
    LibraNativeRenderPrimitives::SetInitialSceneState(SceneDesc, SceneData,
        CustomPrimitiveData, *OwnerObject.Get(), *Scene, worldTransform, LocalBounds, WorldBounds);
    SceneData.SceneProxy = new FLibraStaticMeshSceneProxy(proxyDesc);
    if (!SceneData.SceneProxy)
    {
        return false;
    }
    Scene->AddPrimitive(&SceneDesc);
    bAdded = true;
    return true;
}

void FLibraNativeMeshPrimitive::SetTransform(const FTransform& worldTransform)
{
    check(IsInGameThread());
    WorldTransform = worldTransform;
    SceneDesc.RenderMatrix = worldTransform.ToMatrixWithScale();
    SceneDesc.AttachmentRootPosition = worldTransform.GetLocation();
    WorldBounds = LocalBounds.TransformBy(worldTransform);
    SceneDesc.Bounds = WorldBounds;
    SubmittedBytes = sizeof(FMatrix) + sizeof(FVector) + sizeof(FBoxSphereBounds) * 2;
    if (bAdded && Scene)
    {
        Scene->UpdatePrimitiveTransform(&SceneDesc);
    }
}

void FLibraNativeMeshPrimitive::SetVisible(bool visible)
{
    check(IsInGameThread());
    if (auto* proxy = static_cast<FLibraStaticMeshSceneProxy*>(SceneDesc.GetSceneProxy()))
    {
        proxy->SetOwnerVisible(visible);
    }
}

bool FLibraNativeMeshPrimitive::HasSceneProxy() const
{
    return SceneDesc.GetSceneProxy() != nullptr;
}

void FLibraNativeMeshPrimitive::Remove()
{
    check(IsInGameThread());
    if (bAdded && Scene)
    {
        Scene->RemovePrimitive(&SceneDesc);
        Scene->ReleasePrimitive(&SceneDesc);
        bAdded = false;
        bRemovalPending = true;
        RemovalFence.BeginFence();
    }
}

FLibraNativeMeshPrimitive::~FLibraNativeMeshPrimitive()
{
    check(IsInGameThread());
    Remove();
    if (bRemovalPending)
    {
        RemovalFence.Wait();
    }
    // FScene owns the proxy until its deferred primitive-scene update deletes it.
}

bool FLibraNativeFlexiblePlanePrimitive::Create(ULibraSpawnableComponent& owner,
    UMaterialInterface* material, const FTransform& worldTransform,
    TSharedPtr<FLibraProceduralGeometry, ESPMode::ThreadSafe> proceduralGeometry)
{
    check(IsInGameThread());
    auto* actor = owner.GetOwner();
    auto* world = actor ? actor->GetWorld() : nullptr;
    if (!actor || !world || !world->Scene || !material)
    {
        return false;
    }
    auto proxyDesc = FLibraFlexiblePlaneSceneProxyDesc{};
    proxyDesc.Scene = world->Scene;
    proxyDesc.World = world;
    proxyDesc.Owner = actor;
    proxyDesc.Component = &owner;
    proxyDesc.Material = material;
    proxyDesc.CustomPrimitiveData = &CustomPrimitiveData;
    proxyDesc.FeatureLevel = world->Scene->GetFeatureLevel();
    proxyDesc.Mobility = EComponentMobility::Movable;
    proxyDesc.CastShadow = false;
    proxyDesc.bCastDynamicShadow = false;
    proxyDesc.bCastStaticShadow = false;
    proxyDesc.bIsVisible = true;
    proxyDesc.bRenderInMainPass = true;
    Scene = world->Scene;
    OwnerObject.Reset(&owner);
    MaterialObject.Reset(material);
    LibraNativeRenderPrimitives::SetInitialSceneState(SceneDesc, SceneData,
        CustomPrimitiveData, *OwnerObject.Get(), *Scene, worldTransform, LocalBounds, WorldBounds);
    SceneData.SceneProxy = new FLibraFlexiblePlaneSceneProxy(proxyDesc, *material, MoveTemp(proceduralGeometry));
    if (!SceneData.SceneProxy)
    {
        return false;
    }
    Scene->AddPrimitive(&SceneDesc);
    bAdded = true;
    return true;
}

void FLibraNativeFlexiblePlanePrimitive::SetGeometry(TArray<FDynamicMeshVertex>& vertices,
    TArray<uint32>& indices, FRenderCommandFence& uploadFence, const FTransform& worldTransform)
{
    check(IsInGameThread());
    const auto geometryBytes = uint64(vertices.Num()) * sizeof(FDynamicMeshVertex) +
        uint64(indices.Num()) * sizeof(uint32);
    auto bounds = FBox(ForceInit);
    for (const auto& vertex : vertices)
    {
        bounds += FVector(vertex.Position);
    }
    if (!bounds.IsValid)
    {
        bounds += FVector::ZeroVector;
    }
    LocalBounds = FBoxSphereBounds(bounds);
    SetTransform(worldTransform);
    SubmittedBytes += geometryBytes;
    if (auto* proxy = static_cast<FLibraFlexiblePlaneSceneProxy*>(SceneDesc.GetSceneProxy()))
    {
        ENQUEUE_RENDER_COMMAND(LibraFlexiblePlaneGeometry)(
            [proxy, &vertices, &indices](FRHICommandListImmediate&)
            {
                proxy->SetGeometry(vertices, indices);
            });
        uploadFence.BeginFence();
    }
}

void FLibraNativeFlexiblePlanePrimitive::SetLocalBounds(const FBoxSphereBounds& bounds)
{
    LocalBounds = bounds;
}

void FLibraNativeFlexiblePlanePrimitive::SetTransform(const FTransform& worldTransform)
{
    check(IsInGameThread());
    WorldTransform = worldTransform;
    SceneDesc.RenderMatrix = worldTransform.ToMatrixWithScale();
    SceneDesc.AttachmentRootPosition = worldTransform.GetLocation();
    WorldBounds = LocalBounds.TransformBy(worldTransform);
    SceneDesc.LocalBounds = LocalBounds;
    SceneDesc.Bounds = WorldBounds;
    SubmittedBytes = sizeof(FMatrix) + sizeof(FVector) + sizeof(FBoxSphereBounds) * 2;
    if (bAdded && Scene)
    {
        Scene->UpdatePrimitiveTransform(&SceneDesc);
    }
}

void FLibraNativeFlexiblePlanePrimitive::SetVisible(bool visible)
{
    check(IsInGameThread());
    if (auto* proxy = static_cast<FLibraFlexiblePlaneSceneProxy*>(SceneDesc.GetSceneProxy()))
    {
        proxy->SetOwnerVisible(visible);
    }
}

void FLibraNativeFlexiblePlanePrimitive::Remove()
{
    check(IsInGameThread());
    if (bAdded && Scene)
    {
        Scene->RemovePrimitive(&SceneDesc);
        Scene->ReleasePrimitive(&SceneDesc);
        bAdded = false;
        bRemovalPending = true;
        RemovalFence.BeginFence();
    }
}

FLibraNativeFlexiblePlanePrimitive::~FLibraNativeFlexiblePlanePrimitive()
{
    check(IsInGameThread());
    Remove();
    if (bRemovalPending)
    {
        RemovalFence.Wait();
    }
    // FScene owns the proxy until its deferred primitive-scene update deletes it.
}
