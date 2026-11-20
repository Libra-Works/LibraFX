#include "LibraProceduralScope.h"
#include "liberafx/host/LbProceduralCache.h"
#include "LibraProceduralScopeInternal.h"
#include "LibraProceduralGeometry.h"
#include "LibraProceduralMaterial.h"
#include "LibraProceduralGpu.h"
#include "LibraNativeRenderPrimitives.h"
#include "LibraSpawnableComponent.h"
#include "GameFramework/Actor.h"
#include "Engine/Texture2D.h"
#include "Materials/MaterialInstanceDynamic.h"

struct FLibraProceduralScope::FImpl
{
    struct FResource
    {
        liberafx::procedural_adapter::Identity Key;
        std::shared_ptr<FResource> RetainedDependencies[4];
        TUniquePtr<liberafx::procedural_adapter::Resource> Native;
        TUniquePtr<FLibraProceduralGpu> Gpu;
        TSharedPtr<FLibraProceduralGeometry, ESPMode::ThreadSafe> Geometry;
        struct FUpload
        {
            TArray<FDynamicMeshVertex> Vertices;
            TArray<uint32> Indices;
            FRenderCommandFence Fence;
        };
        FUpload Uploads[3];
        uint32 NextUpload = 0;
        uint64 Revision = 0, VertexRevision = 0, IndexRevision = 0, Dependencies[4]{};
        FVector Pivot = FVector::ZeroVector;
        FBoxSphereBounds Bounds;
        ~FResource()
        {
            for (auto& upload : Uploads)
            {
                upload.Fence.Wait();
            }
        }
    };
    struct FDraw
    {
        TUniquePtr<FLibraNativeFlexiblePlanePrimitive> Primitive;
        TStrongObjectPtr<UMaterialInstanceDynamic> Material;
        FTransform Local = FTransform::Identity;
    };
    TSharedPtr<FLibraProceduralDefinition> Definition;
    std::map<FString, std::shared_ptr<FResource>> Resources;
    std::vector<FDraw> Draws;
    static FVector Convert(const float* value) { return FVector(value[0], value[2], value[1]) * 100; }
    static FVector3f Direction(const float* value) { return FVector3f(value[0], value[2], value[1]); }
    bool Upload(FResource& resource)
    {
        auto view = LibraProceduralModelView{};
        if (resource.Native->view(view) != LIBRA_OK)
        {
            return false;
        }
        resource.Pivot = Convert(view.pivot);
        resource.Bounds = FBoxSphereBounds(FBox(Convert(view.boundsMin), Convert(view.boundsMax)));
        if (view.vertexRevision == resource.VertexRevision && view.indexRevision == resource.IndexRevision)
        {
            return true;
        }
        auto& upload = resource.Uploads[resource.NextUpload];
        if (!upload.Fence.IsFenceComplete() || upload.Vertices.Num() != view.vertexCount || upload.Indices.Num() != view.indexCount)
        {
            return false;
        }
        for (auto i = 0u; i < view.vertexCount; ++i)
        {
            const auto& v = view.vertices[i]; auto& out = upload.Vertices[i];
            out.Position = FVector3f(Convert(v.position));
            out.TextureCoordinate[0] = FVector2f(v.uv[0], v.uv[1]); out.TextureCoordinate[1] = FVector2f(v.uv2[0], v.uv2[1]);
            const auto normal = Direction(v.normal), tangent = Direction(v.tangent);
            out.SetTangents(tangent, FVector3f::CrossProduct(normal, tangent) * -v.tangent[3], normal);
            out.Color = FLinearColor(v.color[0], v.color[1], v.color[2], v.color[3]).ToFColor(false);
        }
        for (auto i = 0u; i < view.indexCount; i += 3)
        {
            upload.Indices[i] = view.indices[i]; upload.Indices[i + 1] = view.indices[i + 2]; upload.Indices[i + 2] = view.indices[i + 1];
        }
        resource.Geometry->Submit(upload.Vertices, upload.Indices, resource.IndexRevision != view.indexRevision, upload.Fence);
        resource.VertexRevision = view.vertexRevision; resource.IndexRevision = view.indexRevision;
        resource.NextUpload = (resource.NextUpload + 1) % 3;
        return true;
    }
};
FLibraProceduralScope::FLibraProceduralScope() : Impl(MakeUnique<FImpl>()) {}
FLibraProceduralScope::~FLibraProceduralScope() = default;
bool FLibraProceduralScope::Create(ULibraSpawnableComponent& owner,
    const TSharedPtr<FLibraProceduralDefinition>& definition, FString& error)
{
    Impl->Definition = definition;
    if (!definition || definition->Draws.empty())
    {
        return true;
    }
    if (!owner.GetWorld())
    {
        error = TEXT("Procedural rendering requires a world owner.");
        return false;
    }
    for (auto phase = 0u; phase < 3; ++phase)
    {
        for (const auto& [id, data] : definition->Resources)
        {
            if (data.Native.kind != phase)
            {
                continue;
            }
            static auto cache = liberafx::procedural_adapter::StaticResourceCache<FImpl::FResource>{};
            auto key = liberafx::procedural_adapter::staticIdentity(data.Native);
            if (!key.empty()) key.push_back(static_cast<uint8>(owner.GetWorld()->GetFeatureLevel()));
            for (auto slot = 0; slot < 4 && !key.empty(); ++slot)
            {
                if (!data.Textures[slot].IsEmpty())
                    liberafx::procedural_adapter::appendDependency(key, Impl->Resources.at(data.Textures[slot])->Key);
            }
            if (auto shared = cache.find(key))
            {
                Impl->Resources.emplace(id, std::move(shared));
                continue;
            }
            auto resource = std::make_shared<FImpl::FResource>();
            resource->Key = key;
            resource->Native = MakeUnique<liberafx::procedural_adapter::Resource>(data.Native);
            if (resource->Native->initialize() != LIBRA_OK)
            {
                error = TEXT("Cannot initialize procedural resource.");
                return false;
            }
            if (phase == 0)
            {
                auto view = LibraProceduralModelView{};
                if (resource->Native->view(view) != LIBRA_OK)
                {
                    return false;
                }
                resource->Geometry = MakeShared<FLibraProceduralGeometry, ESPMode::ThreadSafe>(owner.GetWorld()->GetFeatureLevel());
                for (auto& upload : resource->Uploads)
                {
                    upload.Vertices.SetNumUninitialized(view.vertexCount); upload.Indices.SetNumUninitialized(view.indexCount);
                }
                if (!Impl->Upload(*resource))
                {
                    error = TEXT("Cannot upload procedural model.");
                    return false;
                }
            }
            else
            {
                UTexture* textures[4]{};
                for (auto i = 0; i < 4; ++i)
                {
                    if (!data.Textures[i].IsEmpty())
                    {
                        resource->RetainedDependencies[i] = Impl->Resources.at(data.Textures[i]);
                        textures[i] = resource->RetainedDependencies[i]->Gpu->GetTexture();
                    }
                }
                resource->Gpu = MakeUnique<FLibraProceduralGpu>();
                if (!resource->Gpu->Initialize(data.Native.resolution, data.Native.pixel.operationCount,
                    data.Native.pixel.outputRegister, MakeArrayView(textures), error))
                {
                    return false;
                }
            }
            cache.insert(key, resource);
            Impl->Resources.emplace(id, std::move(resource));
        }
    }
    for (const auto& data : definition->Draws)
    {
        const auto* material = data.Material.IsEmpty() ? nullptr : Impl->Resources.at(data.Material).get();
        const auto format = material ? material->Native->definition() : liberafx::procedural_adapter::Definition{};
        auto* master = LibraProceduralMaterial::Resolve(format.blend == 1, format.doubleSided, error);
        if (!master)
        {
            return false;
        }
        auto draw = FImpl::FDraw{};
        draw.Material.Reset(UMaterialInstanceDynamic::Create(master, &owner));
        auto* texture = material ? material->Gpu->GetTexture() : LibraProceduralMaterial::WhiteTexture();
        draw.Material->SetTextureParameterValue(TEXT("Texture"), texture);
        draw.Material->SetScalarParameterValue(TEXT("Emission"), material ? format.emission : 1);
        draw.Primitive = MakeUnique<FLibraNativeFlexiblePlanePrimitive>();
        auto& model = *Impl->Resources.at(data.Model);
        draw.Primitive->SetLocalBounds(model.Bounds);
        if (!draw.Primitive->Create(owner, draw.Material.Get(), FTransform::Identity, model.Geometry))
        {
            error = TEXT("Cannot create procedural native draw primitive.");
            return false;
        }
        draw.Primitive->SetVisible(false);
        Impl->Draws.push_back(MoveTemp(draw));
    }
    return true;
}
bool FLibraProceduralScope::Update(const liberafx::procedural_adapter::Read& read, double time,
    const FTransform& ownerTransform, bool visible, FString& error)
{
    if (!Impl->Definition)
    {
        return true;
    }
    for (auto phase = 0u; phase < 3; ++phase)
    {
        for (auto& [id, resource] : Impl->Resources)
        {
            const auto& definition = Impl->Definition->Resources.at(id);
            if (definition.Native.kind != phase)
            {
                continue;
            }
            if (!resource->Key.empty() && resource->Revision != 0)
            {
                continue;
            }
            auto dirty = false;
            const auto result = resource->Native->update(read, time, dirty);
            if (result != LIBRA_OK)
            {
                error = FString::Printf(TEXT("Procedural evaluation failed (%d)."), result);
                return false;
            }
            for (auto i = 0; i < 4; ++i)
            {
                if (!definition.Textures[i].IsEmpty())
                {
                    const auto revision = Impl->Resources.at(definition.Textures[i])->Revision;
                    dirty = dirty || resource->Dependencies[i] != revision; resource->Dependencies[i] = revision;
                }
            }
            if (dirty)
            {
                const auto& operations = resource->Native->operations();
                if (resource->Geometry ? !Impl->Upload(*resource) : !resource->Gpu->Update(operations.data(), operations.size(), time))
                {
                    error = TEXT("Procedural upload capacity is busy or unavailable.");
                    return false;
                }
                ++resource->Revision;
            }
        }
    }
    for (auto i = std::size_t{}; i < Impl->Draws.size(); ++i)
    {
        const auto& data = Impl->Definition->Draws[i]; auto& draw = Impl->Draws[i];
        float values[4][4]{};
        for (auto c = 0u; c < 4; ++c)
        {
            if (liberafx::procedural_adapter::read(data.Inputs[c], read, values[c]) != LIBRA_OK)
            {
                error = TEXT("Cannot read procedural draw input.");
                return false;
            }
        }
        draw.Local = FTransform(FRotator(values[1][1], values[1][2], values[1][0]),
            FVector(values[0][0], values[0][1], values[0][2]), FVector(values[2][0], values[2][1], values[2][2]));
        const auto& model = *Impl->Resources.at(data.Model);
        draw.Local.SetTranslation(draw.Local.GetTranslation() - draw.Local.TransformVector(model.Pivot));
        draw.Primitive->SetLocalBounds(model.Bounds);
        draw.Material->SetVectorParameterValue(TEXT("Tint"), FLinearColor(values[3][0], values[3][1], values[3][2], values[3][3]));
    }
    SetPresentation(ownerTransform, visible);
    return true;
}
void FLibraProceduralScope::SetPresentation(const FTransform& transform, bool visible)
{
    for (auto& draw : Impl->Draws)
    {
        draw.Primitive->SetTransform(draw.Local * transform); draw.Primitive->SetVisible(visible);
    }
}
int32 FLibraProceduralScope::DrawCount() const { return Impl->Draws.size(); }
