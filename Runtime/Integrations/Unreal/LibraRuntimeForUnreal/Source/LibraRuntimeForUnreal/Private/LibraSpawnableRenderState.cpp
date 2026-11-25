#include "LibraSpawnableRenderState.h"
#include "LibraParticleSpriteData.h"
#include "LibraDebugDrawScope.h"
#include "LibraFlexiblePlaneRenderer.h"
#include "LibraProceduralScope.h"
#include "LibraNativeRenderPrimitives.h"

#include "LibraSpawnableComponent.h"
#include "LibraSpawnableAsset.h"
#include "LibraGpuEffectAsset.h"
#include "LibraGpuEffectRuntime.h"
#include "LibraFluid2DJson.h"
#include "liberafx/gpu/LbFluid2D.h"
#include "Camera/CameraComponent.h"
#include "Camera/PlayerCameraManager.h"
#include "Components/StaticMeshComponent.h"
#include "Dom/JsonObject.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture.h"
#include "Engine/Texture2D.h"
#include "TextureResource.h"
#include "GameFramework/Actor.h"
#include "GameFramework/PlayerController.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Materials/MaterialInterface.h"
#include "Math/RotationMatrix.h"
#include "Misc/PackageName.h"
#include "RHI.h"
#if WITH_EDITOR
#include "LevelEditorViewport.h"
#endif
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UObject/StrongObjectPtr.h"
#include "liberafx/LbSceneBindings.h"
#include "LibraSpriteCompositionRender.h"
#include "liberafx/LbSpawnablePackage.h"
#include <algorithm>
#include "liberafx/LbParticleAtlas.h"

namespace LibraCpuParticleRendering
{
struct FBatch
{
    TMap<uint32,TUniquePtr<FBatch>> Frames;
    TStrongObjectPtr<UMaterialInstanceDynamic> Material;
    TUniquePtr<FLibraNativeInstancedMeshPrimitive> Primitive;
    TArray<FTransform> Transforms;
    TArray<FLinearColor> Colors;
};
template<class Batch, class Visit> void ForEach(Batch& batch, Visit visit)
{
    visit(batch);
    for (auto& pair : batch.Frames) visit(*pair.Value);
}
bool ReadAtlas(const TSharedPtr<FJsonObject>& object, liberafx::ParticleAtlas& out)
{
    const auto value=object->TryGetField(TEXT("flipbook"));
    if (!value.IsValid() || value->Type==EJson::Null) return true;
    const auto atlas=value->Type==EJson::Object?value->AsObject():nullptr;
    if (!atlas || !atlas->TryGetBoolField(TEXT("enabled"),out.enabled) ||
        !atlas->TryGetBoolField(TEXT("loop"),out.loop) || !atlas->TryGetBoolField(TEXT("overLife"),out.overLife) ||
        !atlas->TryGetNumberField(TEXT("fps"),out.fps)) return false;
    for (const auto field : {TEXT("columns"),TEXT("rows"),TEXT("startFrame")}) {
        double number=0;
        if (!atlas->TryGetNumberField(field,number) || !FMath::IsFinite(number) || number<0 || number>256 || FMath::FloorToDouble(number)!=number) return false;
        if (FCString::Strcmp(field,TEXT("columns"))==0) out.columns=static_cast<uint32>(number);
        if (FCString::Strcmp(field,TEXT("rows"))==0) out.rows=static_cast<uint32>(number);
        if (FCString::Strcmp(field,TEXT("startFrame"))==0) out.startFrame=static_cast<uint32>(number);
    }
    return out.valid();
}
}

namespace LibraOpaqueBatching
{
auto HasSafePositiveInstanceTransform(const FTransform& worldTransform,
    const FTransform& primitiveTransform) -> bool
{
    const auto worldMatrix = worldTransform.ToMatrixWithScale();
    const auto relativeTransform = worldTransform.GetRelativeTransform(primitiveTransform);
    const auto relativeMatrix = relativeTransform.ToMatrixWithScale();
    const auto worldDeterminant = worldMatrix.Determinant();
    const auto relativeDeterminant = relativeMatrix.Determinant();
    return !worldMatrix.ContainsNaN() && !relativeMatrix.ContainsNaN() &&
        FMath::IsFinite(worldDeterminant) && FMath::IsFinite(relativeDeterminant) &&
        worldDeterminant > 1e-8 && relativeDeterminant > 1e-8;
}
}

namespace {
auto Text(const std::vector<std::uint8_t>& bytes) -> FString
{
    if (bytes.empty()) return {};
    auto converted = FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(bytes.data()), bytes.size());
    return FString(converted.Length(), converted.Get());
}

auto Guid(const std::array<std::uint8_t, 16>& bytes) -> FGuid
{
    auto words = std::array<uint32, 4>{};
    for (auto index = 0; index < 4; ++index)
        for (auto lane = 0; lane < 4; ++lane)
            words[index] = (words[index] << 8) | bytes[index * 4 + lane];
    return FGuid(words[0], words[1], words[2], words[3]);
}

bool Integer(const TSharedPtr<FJsonObject>& object, const TCHAR* name, int32& value)
{
    auto number = double{};
    if (!object->TryGetNumberField(name, number) || !FMath::IsFinite(number) ||
        number < MIN_int32 || number > MAX_int32 || FMath::FloorToDouble(number) != number) return false;
    value = static_cast<int32>(number);
    return true;
}

bool ReadFluid2DFloat(const TSharedPtr<FJsonObject>& object, const TCHAR* name, float& value)
{
    auto number = double{};
    if (!object->TryGetNumberField(name, number) || !FMath::IsFinite(number) ||
        number < -double(MAX_flt) || number > double(MAX_flt)) return false;
    value = static_cast<float>(number);
    return FMath::IsFinite(value);
}

bool ReadFluid2DUint(const TSharedPtr<FJsonObject>& object, const TCHAR* name,
    uint32 minimum, uint32 maximum, uint32& value)
{
    auto number = double{};
    if (!object->TryGetNumberField(name, number) || !FMath::IsFinite(number) ||
        number < minimum || number > maximum || FMath::FloorToDouble(number) != number) return false;
    value = static_cast<uint32>(number);
    return true;
}

bool ReadFluid2DSettings(const TSharedPtr<FJsonObject>& object,
    liberafx::gpu::Fluid2DSettings& settings)
{
    using namespace liberafx::gpu;
    if (!LibraReadFluidModelProperties(object, settings))
    {
        return false;
    }

    settings.vorticity = 0;
    return ReadFluid2DUint(object, TEXT("version"), 1, 1, settings.version) &&
        ReadFluid2DUint(object, TEXT("width"), 8, 1024, settings.width) &&
        ReadFluid2DUint(object, TEXT("height"), 8, 1024, settings.height) &&
        ReadFluid2DFloat(object, TEXT("domainWidth"), settings.domainWidth) &&
        ReadFluid2DFloat(object, TEXT("domainHeight"), settings.domainHeight) &&
        ReadFluid2DUint(object, TEXT("pressureIterations"), 1, 128, settings.pressureIterations) &&
        ReadFluid2DFloat(object, TEXT("maximumStep"), settings.maximumStep) &&
        ReadFluid2DUint(object, TEXT("maximumSubsteps"), 1, 64, settings.maximumSubsteps) &&
        ReadFluid2DFloat(object, TEXT("velocityDissipation"), settings.velocityDissipation) &&
        ReadFluid2DFloat(object, TEXT("densityDissipation"), settings.densityDissipation) &&
        object->TryGetBoolField(TEXT("sourceEnabled"), settings.sourceEnabled) &&
        ReadFluid2DFloat(object, TEXT("sourceX"), settings.sourceX) &&
        ReadFluid2DFloat(object, TEXT("sourceY"), settings.sourceY) &&
        ReadFluid2DFloat(object, TEXT("sourceRadius"), settings.sourceRadius) &&
        ReadFluid2DFloat(object, TEXT("sourceDensity"), settings.sourceDensity) &&
        ReadFluid2DFloat(object, TEXT("sourceVelocityX"), settings.sourceVelocityX) &&
        ReadFluid2DFloat(object, TEXT("sourceVelocityY"), settings.sourceVelocityY) &&
        ReadFluid2DFloat(object, TEXT("buoyancy"), settings.buoyancy) &&
        ReadFluid2DFloat(object, TEXT("colorR"), settings.colorR) &&
        ReadFluid2DFloat(object, TEXT("colorG"), settings.colorG) &&
        ReadFluid2DFloat(object, TEXT("colorB"), settings.colorB) &&
        ReadFluid2DFloat(object, TEXT("colorA"), settings.colorA) &&
        (!object->HasField(TEXT("vorticity")) || ReadFluid2DFloat(object, TEXT("vorticity"), settings.vorticity)) &&
        validFluid2DSettings(settings);
}

struct FFluidProgram
{
    FGuid NodeId;
    liberafx::gpu::Fluid2DSettings Settings;
    float SizeScale = 100;
    bool bAutomaticPlane = true;
};

bool ReadFluidProgram(const TSharedPtr<FJsonObject>& object, FFluidProgram& program)
{
    if (!object.IsValid() || object->Values.Num() != 3 + (object->HasField(TEXT("automaticPlane")) ? 1 : 0)) return false;
    if (object->HasField(TEXT("automaticPlane")) && !object->TryGetBoolField(TEXT("automaticPlane"), program.bAutomaticPlane)) return false;
    FString nodeId;
    FGuid parsedNodeId;
    const TSharedPtr<FJsonObject>* settings = nullptr;
    const TSharedPtr<FJsonObject>* renderer = nullptr;
    FString material;
    auto sizeScale = double{};
    if (!object->TryGetStringField(TEXT("nodeId"), nodeId) || !FGuid::Parse(nodeId, parsedNodeId) ||
        !parsedNodeId.IsValid() || !object->TryGetObjectField(TEXT("settings"), settings) ||
        !object->TryGetObjectField(TEXT("renderer"), renderer) || !renderer->IsValid() ||
        !renderer->Get()->TryGetStringField(TEXT("material"), material) ||
        !LibraFluidMaterialMatches(*settings, material) ||
        !renderer->Get()->TryGetNumberField(TEXT("sizeScale"), sizeScale) ||
        !FMath::IsFinite(sizeScale) || sizeScale <= 0 || sizeScale > 10000 ||
        float(sizeScale) <= 0 || !ReadFluid2DSettings(*settings, program.Settings))
        return false;
    program.NodeId = parsedNodeId;
    program.SizeScale = static_cast<float>(sizeScale);
    return true;
}

bool Finite(const FVector4& value)
{
    for (auto index = 0; index < 4; ++index)
        if (!FMath::IsFinite(value[index]) || FMath::Abs(value[index]) > MAX_flt) return false;
    return true;
}

bool HasParameter(UMaterialInterface* material, const FMaterialParameterInfo& info, const FString& kind)
{
    auto infos = TArray<FMaterialParameterInfo>{};
    auto ids = TArray<FGuid>{};
    if (kind == TEXT("scalar")) material->GetAllScalarParameterInfo(infos, ids);
    else if (kind == TEXT("vector")) material->GetAllVectorParameterInfo(infos, ids);
    else if (kind == TEXT("texture")) material->GetAllTextureParameterInfo(infos, ids);
    return infos.Contains(info);
}

struct FInput
{
    FString Id, Kind;
    FGuid Source;
    int32 Width = 0, Component = 0, Slot = 0;
    FVector4 Defaults{0, 0, 0, 0};
    FMaterialParameterInfo Parameter;
    bool bMaterial = false;
};

struct FSpriteCell
{
    FGuid Id;
    int32 PixelWidth = 0, PixelHeight = 0;
    float U = 0, V = 0, Width = 0, Height = 0;
};

struct FSpriteFrame
{
    FGuid Id, CellId;
    int32 CellIndex = INDEX_NONE;
    double DurationFrames = 0;
};

struct FSpriteAnimation
{
    bool bPresent = false, bLoop = true;
    int32 TextureWidth = 0, TextureHeight = 0, StartFrame = 0, EndFrame = 0;
    double Fps = 0;
    int32 Version = 0;
    FLibraSpriteCompositionRender Composition;
    TArray<FSpriteCell> Cells;
    TArray<FSpriteFrame> Frames;
    TArray<double> Durations;
};

struct FDraw
{
    FGuid Id;
    bool bSprite = false, bBillboard = false, bOpaqueBatchCandidate = false, bGeneratedMain = false;
    FSpriteAnimation SpriteAnimation;
    TStrongObjectPtr<UStaticMesh> Mesh;
    TArray<TStrongObjectPtr<UMaterialInterface>> Materials;
    struct FTexture { int32 Slot; FMaterialParameterInfo Parameter; TStrongObjectPtr<UTexture> Texture; };
    TArray<FTexture> Textures;
    TArray<FInput> Inputs;
    // Draws are evaluated synchronously on the game thread; reuse definition
    // scratch across every instance rather than building a map in each tick.
    TMap<FString, FVector4> Values;
    TArray<TPair<int32, FName>> TintTargets;
};

struct FCpuParticleDraw
{
    bool HasSpriteAnimation = false;
    liberafx::ParticleSpriteAnimation SpriteAnimation;
    liberafx::ParticleAtlas Atlas;
    TArray<FGuid> DrawRows;
    FGuid Id;
    float DrawScale = 1;
    bool bAdditive = false;
    TStrongObjectPtr<UStaticMesh> Mesh;
    TStrongObjectPtr<UTexture> Texture;
    TStrongObjectPtr<UMaterialInterface> Material;
    FString ProceduralTexture;
    FString ProceduralMaterial;
    bool bCustomMaterial = false;
};

struct FFlexiblePlaneDraw
{
    FLibraFlexiblePlaneDefinition Definition;
    TStrongObjectPtr<UTexture> Texture;
    TStrongObjectPtr<UMaterialInterface> Material;
    TArray<TPair<FName, TStrongObjectPtr<UTexture>>> HostTextures;
};

struct FDrawBatch
{
    FString Key;
    TArray<int32> DrawIndices;
    TArray<FTransform> Transforms;
    TArray<FLinearColor> CustomData;
    TUniquePtr<FLibraNativeInstancedMeshPrimitive> Primitive;
};

struct FGeneratedDrawTexture
{
    FGuid NodeId, ProducerNodeId;
    FMaterialParameterInfo Parameter;
    int32 Slot = 0;
    FString OutputId;
    bool bMainTexture = false;
};

struct FDefinition
{
    TSharedPtr<FLibraProceduralDefinition> Procedural;
    TSharedPtr<FJsonObject> DebugConfiguration;
    int32 DebugCount = 0;
    TArray<FDraw> Draws;
    TArray<FCpuParticleDraw> CpuParticles;
    TArray<FFlexiblePlaneDraw> FlexiblePlanes;
    TArray<TStrongObjectPtr<ULibraGpuEffectAsset>> Gpu;
    TArray<FGuid> GpuNodeIds;
    TArray<FFluidProgram> FluidPrograms;
    TArray<FGeneratedDrawTexture> GeneratedDrawTextures;
    TMap<FGuid, TArray<FGuid>> GpuDrawTransforms;
    TMap<FGuid, TArray<FGuid>> GpuEmitterTransforms;
    uint64 GpuBytes = 0;
};

struct FInstance
{
    TUniquePtr<FLibraProceduralScope> Procedural;
    TUniquePtr<FLibraDebugDrawScope> Debug;
    FGuid Container;
    int64 Id = 0;
    double Started = 0, Previous = 0;
    FTransform PresentationRootTransform = FTransform::Identity;
    bool bHasPresentationRootTransform = false;
    FDefinition* Definition = nullptr;
    TArray<TUniquePtr<FLibraNativeMeshPrimitive>> Draws;
    TArray<FTransform> DrawLocalTransforms;
    TArray<FDrawBatch> DrawBatches;
    TArray<TArray<TStrongObjectPtr<UMaterialInstanceDynamic>>> DrawMaterials;
    TArray<LibraCpuParticleRendering::FBatch> CpuDraws;
    TArray<TUniquePtr<FLibraFlexiblePlaneRenderer>> FlexiblePlanes;
    FLibraFlexiblePlaneHistoryScope FlexibleHistory;
    TArray<TStrongObjectPtr<ULibraGpuEffectRuntime>> Gpu;
    TArray<FGuid> GpuRuntimeNodeIds;
    TArray<bool> GpuRuntimeIsFluid;
    ~FInstance()
    {
        for (auto& runtime : Gpu)
        {
            if (runtime.IsValid())
            {
                runtime->Stop();
            }
        }
    }
};
}

struct FLibraSpawnableRenderState::FImpl
{
    ULibraSpawnableComponent& Owner;
    TMap<FGuid, TUniquePtr<FDefinition>> Definitions;
    TArray<TUniquePtr<FInstance>> Instances;
    uint64 GpuBytes = 0;
    explicit FImpl(ULibraSpawnableComponent& owner) : Owner(owner) {}
    bool Fail(const FString& message) { Owner.LastError = message; return false; }

    FString DrawBatchKey(const FDraw& draw) const
    {
        auto key = FString{};
        key.Appendf(TEXT("%llu;"), static_cast<unsigned long long>(
            reinterpret_cast<UPTRINT>(draw.Mesh.Get())));
        for (const auto& material : draw.Materials)
        {
            key.Appendf(TEXT("%llu;"), static_cast<unsigned long long>(
                reinterpret_cast<UPTRINT>(material.Get())));
        }
        for (const auto& input : draw.Inputs)
        {
            if (!input.bMaterial)
            {
                continue;
            }
            key.Appendf(TEXT("%d:%s:%d:%d:%s:"), input.Slot, *input.Parameter.Name.ToString(),
                static_cast<int32>(input.Parameter.Association), input.Parameter.Index, *input.Kind);
            const auto value = draw.Values.FindRef(input.Id);
            for (auto lane = 0; lane < 4; ++lane)
            {
                const auto laneValue = static_cast<float>(value[lane]);
                auto bits = uint32{};
                FMemory::Memcpy(&bits, &laneValue, sizeof(bits));
                key.Appendf(TEXT("%08x"), bits);
            }
            key.AppendChar(TEXT(';'));
        }
        for (const auto& target : draw.TintTargets)
        {
            key.Appendf(TEXT("%d:%s:"), target.Key, *target.Value.ToString());
            const auto tint = draw.Values.FindRef(TEXT("tint"));
            for (auto lane = 0; lane < 4; ++lane)
            {
                const auto laneValue = static_cast<float>(tint[lane]);
                auto bits = uint32{};
                FMemory::Memcpy(&bits, &laneValue, sizeof(bits));
                key.Appendf(TEXT("%08x"), bits);
            }
            key.AppendChar(TEXT(';'));
        }
        for (const auto& texture : draw.Textures)
        {
            key.Appendf(TEXT("%d:%s:%llu;"), texture.Slot, *texture.Parameter.Name.ToString(),
                static_cast<unsigned long long>(reinterpret_cast<UPTRINT>(texture.Texture.Get())));
        }
        return key;
    }

    template<class T> T* Resolve(const FString& identity, ELibraPackageResourceKind kind,
        bool bAllowPackageOverride = true)
    {
        for (const auto& binding : Owner.AssetBindings)
            if (binding.Identity == identity) return Cast<T>(binding.Asset);
        if (bAllowPackageOverride && Owner.Package)
            if (auto* asset = Owner.Package->FindResourceOverride(identity, kind))
                return Cast<T>(asset);
        if (!FPackageName::IsValidObjectPath(identity)) return nullptr;
        return LoadObject<T>(nullptr, *identity);
    }

    bool ReadParameter(const TSharedPtr<FJsonObject>& object, FMaterialParameterInfo& info, int32& slot)
    {
        auto name = FString{};
        auto association = int32{2}, index = int32{-1};
        if (!object->TryGetStringField(TEXT("name"), name) || name.IsEmpty() ||
            !Integer(object, TEXT("slot"), slot) || slot < 0 ||
            !Integer(object, TEXT("association"), association) || association < 0 || association > 2 ||
            !Integer(object, TEXT("index"), index) || index < -1) return Fail(TEXT("Invalid material parameter identity."));
        info = FMaterialParameterInfo(FName(*name), static_cast<EMaterialParameterAssociation>(association), index);
        return true;
    }

    bool ReadSpriteAnimation(const TSharedPtr<FJsonObject>& object, FSpriteAnimation& animation, bool billboard)
    {
        const TSharedPtr<FJsonObject>* data = nullptr;
        if (!object->TryGetObjectField(TEXT("spriteAnimation"), data)) return true;
        animation.bPresent = true;
        auto version = int32{}, start = int32{}, end = int32{};
        auto fps = double{};
        auto cells = static_cast<const TArray<TSharedPtr<FJsonValue>>*>(nullptr);
        auto frames = static_cast<const TArray<TSharedPtr<FJsonValue>>*>(nullptr);
        if (!Integer(*data, TEXT("version"), version) || (version != 1 && version != 2) ||
            !Integer(*data, TEXT("textureWidth"), animation.TextureWidth) ||
            !Integer(*data, TEXT("textureHeight"), animation.TextureHeight) ||
            animation.TextureWidth < 1 || animation.TextureWidth > 8192 ||
            animation.TextureHeight < 1 || animation.TextureHeight > 8192 ||
            !(*data)->TryGetNumberField(TEXT("fps"), fps) || !FMath::IsFinite(fps) ||
            fps <= 0 || fps > 10000 || !Integer(*data, TEXT("startFrame"), start) ||
            !Integer(*data, TEXT("endFrame"), end) ||
            !(*data)->TryGetBoolField(TEXT("loop"), animation.bLoop) ||
            !(*data)->TryGetArrayField(TEXT("cells"), cells) || cells->Num() < 1 || cells->Num() > 512 ||
            !(*data)->TryGetArrayField(TEXT("frames"), frames) || frames->Num() < 1 || frames->Num() > 4096)
            return Fail(TEXT("Sprite animation metadata is missing, unsupported or out of range."));
        animation.Fps = fps;
        animation.Version = version;
        animation.StartFrame = start;
        animation.EndFrame = end;
        if (start < 0 || end < start || end >= frames->Num())
            return Fail(TEXT("Sprite playback range is outside its frame sequence."));

        auto cellIds = TSet<FGuid>{};
        auto names = TSet<FString>{};
        for (const auto& value : *cells)
        {
            auto cellObject = value.IsValid() && value->Type == EJson::Object ? value->AsObject() : nullptr;
            auto cell = FSpriteCell{};
            auto identity = FString{}, name = FString{};
            auto x = int32{}, y = int32{}, width = int32{}, height = int32{};
            auto u = double{}, v = double{}, uvWidth = double{}, uvHeight = double{};
            if (!cellObject || !cellObject->TryGetStringField(TEXT("id"), identity) ||
                !FGuid::Parse(identity, cell.Id) || !cell.Id.IsValid() ||
                !cellObject->TryGetStringField(TEXT("name"), name) || name.TrimStartAndEnd().IsEmpty() ||
                name.Len() > 80 || names.Contains(name.TrimStartAndEnd().ToLower()) ||
                !Integer(cellObject, TEXT("x"), x) || !Integer(cellObject, TEXT("y"), y) ||
                !Integer(cellObject, TEXT("width"), width) || !Integer(cellObject, TEXT("height"), height) ||
                x < 0 || y < 0 || width <= 0 || height <= 0 ||
                static_cast<int64>(x) + width > animation.TextureWidth ||
                static_cast<int64>(y) + height > animation.TextureHeight ||
                !cellObject->TryGetNumberField(TEXT("u"), u) ||
                !cellObject->TryGetNumberField(TEXT("v"), v) ||
                !cellObject->TryGetNumberField(TEXT("uvWidth"), uvWidth) ||
                !cellObject->TryGetNumberField(TEXT("uvHeight"), uvHeight) ||
                !FMath::IsFinite(u) || !FMath::IsFinite(v) ||
                !FMath::IsFinite(uvWidth) || !FMath::IsFinite(uvHeight) ||
                FMath::Abs(u - static_cast<double>(x) / animation.TextureWidth) > 1e-9 ||
                FMath::Abs(v - static_cast<double>(y) / animation.TextureHeight) > 1e-9 ||
                FMath::Abs(uvWidth - static_cast<double>(width) / animation.TextureWidth) > 1e-9 ||
                FMath::Abs(uvHeight - static_cast<double>(height) / animation.TextureHeight) > 1e-9 ||
                cellIds.Contains(cell.Id))
                return Fail(TEXT("Sprite animation contains an invalid or duplicate pixel/UV rectangle."));
            names.Add(name.TrimStartAndEnd().ToLower());
            cellIds.Add(cell.Id);
            cell.U = static_cast<float>(u);
            cell.V = static_cast<float>(v);
            cell.Width = static_cast<float>(uvWidth);
            cell.Height = static_cast<float>(uvHeight);
            cell.PixelWidth = width;
            cell.PixelHeight = height;
            animation.Cells.Add(cell);
        }

        auto frameIds = TSet<FGuid>{};
        for (const auto& value : *frames)
        {
            auto frameObject = value.IsValid() && value->Type == EJson::Object ? value->AsObject() : nullptr;
            auto frame = FSpriteFrame{};
            auto identity = FString{}, cellIdentity = FString{};
            if (!frameObject || !frameObject->TryGetStringField(TEXT("id"), identity) ||
                !FGuid::Parse(identity, frame.Id) || !frame.Id.IsValid() || frameIds.Contains(frame.Id) ||
                !frameObject->TryGetStringField(TEXT("cellId"), cellIdentity) ||
                !FGuid::Parse(cellIdentity, frame.CellId) || !frame.CellId.IsValid() ||
                !frameObject->TryGetNumberField(TEXT("durationFrames"), frame.DurationFrames) ||
                !FMath::IsFinite(frame.DurationFrames) || frame.DurationFrames <= 0 ||
                frame.DurationFrames > 1000000)
                return Fail(TEXT("Sprite animation contains an invalid frame identity or duration."));
            frame.CellIndex = animation.Cells.IndexOfByPredicate(
                [&](const auto& cell) { return cell.Id == frame.CellId; });
            if (frame.CellIndex == INDEX_NONE)
                return Fail(TEXT("Sprite animation frame references a missing stable cell ID."));
            frameIds.Add(frame.Id);
            animation.Durations.Add(frame.DurationFrames);
            animation.Frames.Add(frame);
        }
        return version != 2 || animation.Composition.Read(*data, fps, animation.bLoop,
            animation.Durations, start, end, billboard) || Fail(TEXT("Invalid layered sprite clock, order or pose keys."));
    }

    TStrongObjectPtr<UTexture2D> RadialGlow;
    UTexture2D* MakeWhiteTexture(bool radial = false)
    {
        if (radial && RadialGlow.IsValid()) return RadialGlow.Get();
        const auto size = radial ? 64 : 1;
        auto* texture = UTexture2D::CreateTransient(size, size, PF_B8G8R8A8);
        if (!texture || !texture->GetPlatformData() || texture->GetPlatformData()->Mips.IsEmpty())
            return nullptr;
        auto& mip = texture->GetPlatformData()->Mips[0];
        auto* pixel = static_cast<FColor*>(mip.BulkData.Lock(LOCK_READ_WRITE));
        if (!pixel)
        {
            mip.BulkData.Unlock();
            return nullptr;
        }
        for (auto y = 0; y < size; ++y) for (auto x = 0; x < size; ++x) {
            const auto u = (x + .5) / size * 2 - 1, v = (y + .5) / size * 2 - 1;
            const auto alpha = radial ? FMath::Clamp(1 - FMath::Sqrt(u * u + v * v), 0.0, 1.0) : 1.0;
            pixel[y * size + x] = FColor(255, 255, 255, static_cast<uint8>(FMath::RoundToInt(alpha * 255)));
        }
        mip.BulkData.Unlock();
        texture->UpdateResource();
        if (radial) RadialGlow.Reset(texture);
        return texture;
    }

    bool ReadDraws(const std::vector<std::uint8_t>& bytes, FDefinition& definition)
    {
        if (bytes.empty()) return true;
        auto root = TSharedPtr<FJsonObject>{};
        auto reader = TJsonReaderFactory<>::Create(Text(bytes));
        auto type = FString{};
        const TArray<TSharedPtr<FJsonValue>>* nodes = nullptr;
        if (!FJsonSerializer::Deserialize(reader, root) || !root ||
            !root->TryGetStringField(TEXT("type"), type) || type != TEXT("render_config") ||
            !root->TryGetArrayField(TEXT("nodes"), nodes) || nodes->Num() > 128)
            return Fail(TEXT("Invalid render configuration."));
        auto proceduralError = FString{};
        if (!LibraProcedural::Prepare(root, definition.Procedural, proceduralError))
        {
            return Fail(proceduralError);
        }
        definition.GpuBytes += LibraProcedural::Bytes(definition.Procedural);
        const TArray<TSharedPtr<FJsonValue>>* cpuParticles = nullptr;
        if (root->HasField(TEXT("cpuParticles")) &&
            !root->TryGetArrayField(TEXT("cpuParticles"), cpuParticles))
        {
            return Fail(TEXT("CPU particle renderer metadata must be an array."));
        }
        if (cpuParticles && cpuParticles->Num() > 128)
        {
            return Fail(TEXT("CPU particle renderer count exceeds 128."));
        }
        auto cpuVersion = double{};
        if ((root->HasField(TEXT("cpuParticleVersion")) || (cpuParticles && !cpuParticles->IsEmpty())) &&
            (!root->TryGetNumberField(TEXT("cpuParticleVersion"), cpuVersion) ||
             (cpuVersion != 1 && cpuVersion != 2 && cpuVersion != 3 && cpuVersion != 4 && cpuVersion != 5)))
        {
            return Fail(TEXT("Unsupported CPU particle scene metadata version."));
        }
        for (const auto emitterBinding : {false, true})
        {
        auto& transformMap = emitterBinding ? definition.GpuEmitterTransforms : definition.GpuDrawTransforms;
        const TArray<TSharedPtr<FJsonValue>>* gpuDrawTransforms = nullptr;
        if (root->TryGetArrayField(emitterBinding ? TEXT("gpuEmitterTransforms") : TEXT("gpuDrawTransforms"), gpuDrawTransforms))
        {
            if (gpuDrawTransforms->Num() > 16)
                return Fail(TEXT("Render configuration exceeds the 16 GPU draw Matrix binding limit."));
            for (const auto& value : *gpuDrawTransforms)
            {
                auto object = value.IsValid() && value->Type == EJson::Object ? value->AsObject() : nullptr;
                auto id = FString{};
                auto nodeId = FGuid{};
                const TArray<TSharedPtr<FJsonValue>>* rowIds = nullptr;
                if (!object || !object->TryGetStringField(TEXT("nodeId"), id) ||
                    !FGuid::Parse(id, nodeId) || !nodeId.IsValid() ||
                    transformMap.Contains(nodeId) ||
                    !object->TryGetArrayField(TEXT("rowNodeIds"), rowIds) || rowIds->Num() != 4)
                    return Fail(TEXT("Invalid or duplicate GPU draw Matrix binding."));
                auto rows = TArray<FGuid>{};
                rows.Reserve(4);
                for (const auto& rowValue : *rowIds)
                {
                    auto rowIdText = FString{};
                    auto rowId = FGuid{};
                    if (!rowValue.IsValid() || !rowValue->TryGetString(rowIdText) ||
                        !FGuid::Parse(rowIdText, rowId) || !rowId.IsValid())
                        return Fail(TEXT("GPU draw Matrix contains an invalid FloatX4 row identity."));
                    rows.Add(rowId);
                }
                transformMap.Add(nodeId, MoveTemp(rows));
            }
        }
        }
        auto ids = TSet<FGuid>{};
        using EKind = ELibraPackageResourceKind;
        for (const auto& item : *nodes)
        {
            auto object = item.IsValid() && item->Type == EJson::Object ? item->AsObject() : nullptr;
            auto draw = FDraw{};
            auto id = FString{}, kind = FString{}, asset = FString{};
            if (object && object->TryGetStringField(TEXT("kind"), kind) && kind == TEXT("proceduralModel"))
            {
                continue;
            }
            if (object && object->TryGetStringField(TEXT("kind"), kind) &&
                kind == TEXT("flexiblePlane"))
            {
                auto plane = FFlexiblePlaneDraw{};
                auto error = FString{};
                if (!FLibraFlexiblePlaneRenderer::Prepare(object, plane.Definition, error) ||
                    ids.Contains(plane.Definition.nodeId))
                {
                    return Fail(error.IsEmpty() ? TEXT("Duplicate Flexible Plane identity.") : error);
                }
                const bool isProceduralPlane = plane.Definition.assetPath == TEXT("builtin:procedural-texture") ||
                    plane.Definition.assetPath == TEXT("builtin:procedural-material");
                if (isProceduralPlane)
                {
                    plane.Texture.Reset(MakeWhiteTexture());
                }
                else
                {
                    plane.Texture.Reset(Resolve<UTexture>(plane.Definition.assetPath, EKind::Texture));
                }
                const auto materialIdentity = plane.Definition.hostMaterialPath.IsEmpty()
                    ? FString(TEXT("builtin:flexible-plane"))
                    : plane.Definition.hostMaterialPath;
                plane.Material.Reset(Resolve<UMaterialInterface>(materialIdentity, EKind::Material));
                if ((!plane.Texture.IsValid() && plane.Definition.assetPath != TEXT("builtin:generated-texture")) ||
                    !plane.Material.IsValid() ||
                    (plane.Definition.hostMaterialPath.IsEmpty() &&
                     !HasParameter(plane.Material.Get(), FMaterialParameterInfo(TEXT("Texture")), TEXT("texture"))))
                {
                    return Fail(TEXT("Flexible Plane requires its Texture binding and a compatible Material."));
                }
                for (const auto& hostTex : plane.Definition.hostTextures)
                {
                    auto* resolvedTex = Resolve<UTexture>(hostTex.Value, EKind::Texture);
                    if (!resolvedTex)
                    {
                        return Fail(TEXT("Missing Flexible Plane host material texture: ") + hostTex.Value);
                    }
                    plane.HostTextures.Emplace(hostTex.Key, TStrongObjectPtr<UTexture>(resolvedTex));
                }
                ids.Add(plane.Definition.nodeId);
                definition.FlexiblePlanes.Add(MoveTemp(plane));
                continue;
            }
            if (object && object->TryGetStringField(TEXT("kind"), kind) &&
                (kind == TEXT("debugText") || kind == TEXT("debugModel")))
            {
                if (!object->TryGetStringField(TEXT("nodeId"), id) || !FGuid::Parse(id, draw.Id) ||
                    !draw.Id.IsValid() || ids.Contains(draw.Id)) return Fail(TEXT("Invalid Debug renderer identity."));
                ids.Add(draw.Id);
                definition.DebugConfiguration = root;
                ++definition.DebugCount;
                continue;
            }
            if (!object || !object->TryGetStringField(TEXT("nodeId"), id) || !FGuid::Parse(id, draw.Id) ||
                !draw.Id.IsValid() || ids.Contains(draw.Id) ||
                !object->TryGetStringField(TEXT("kind"), kind) || (kind != TEXT("model") && kind != TEXT("sprite")) ||
                !object->TryGetStringField(TEXT("assetPath"), asset))
                return Fail(TEXT("Invalid or duplicate render node."));
            ids.Add(draw.Id);
            draw.bSprite = kind == TEXT("sprite");
            object->TryGetBoolField(TEXT("billboard"), draw.bBillboard);
            if (draw.bSprite && !ReadSpriteAnimation(object, draw.SpriteAnimation, draw.bBillboard)) return false;
            draw.Mesh.Reset(Resolve<UStaticMesh>(draw.bSprite ? TEXT("/Engine/BasicShapes/Plane.Plane") : asset,
                EKind::Model, !draw.bSprite));
            if (!draw.Mesh.IsValid() || draw.Mesh->GetStaticMaterials().Num() < 1 ||
                draw.Mesh->GetStaticMaterials().Num() > 256)
                return Fail(TEXT("Missing StaticMesh binding: ") + asset);
            for (const auto& slot : draw.Mesh->GetStaticMaterials()) draw.Materials.Emplace(slot.MaterialInterface.Get());
            const TArray<TSharedPtr<FJsonValue>>* materials = nullptr;
            if (!object->TryGetArrayField(TEXT("materials"), materials) || materials->Num() > 256)
                return Fail(TEXT("Invalid material overrides."));
            auto slots = TSet<int32>{};
            for (const auto& value : *materials)
            {
                auto material = value.IsValid() && value->Type == EJson::Object ? value->AsObject() : nullptr;
                auto slot = int32{};
                auto identity = FString{};
                if (!material || !Integer(material, TEXT("slot"), slot) || !draw.Materials.IsValidIndex(slot) ||
                    slots.Contains(slot) || !material->TryGetStringField(TEXT("path"), identity))
                    return Fail(TEXT("Invalid material slot or identity."));
                slots.Add(slot);
                draw.Materials[slot].Reset(Resolve<UMaterialInterface>(identity, EKind::Material));
                if (!draw.Materials[slot].IsValid()) return Fail(TEXT("Missing Material binding: ") + identity);
            }
            if (draw.bSprite)
            {
                if (!slots.Contains(0)) draw.Materials[0].Reset(
                    Resolve<UMaterialInterface>(TEXT("builtin:sprite"), EKind::Material));
                draw.bGeneratedMain = asset == TEXT("builtin:generated-texture");
                auto texture = draw.bGeneratedMain ? nullptr : Resolve<UTexture>(asset, EKind::Texture);
                if (!texture && !draw.bGeneratedMain) return Fail(TEXT("Missing sprite Texture binding: ") + asset);
                if (!draw.Materials[0].IsValid() ||
                    !HasParameter(draw.Materials[0].Get(), FMaterialParameterInfo(TEXT("Texture")), TEXT("texture")) ||
                    !HasParameter(draw.Materials[0].Get(), FMaterialParameterInfo(TEXT("AtlasRect")), TEXT("vector")))
                    return Fail(TEXT("Sprites require a Texture binding and a material exposing Texture and AtlasRect."));
                draw.Textures.Add({0, FMaterialParameterInfo(TEXT("Texture")), TStrongObjectPtr<UTexture>(texture)});
            }
            for (const auto& material : draw.Materials)
                if (!material.IsValid()) return Fail(TEXT("A mesh material slot is unavailable."));
            const TArray<TSharedPtr<FJsonValue>>* inputs = nullptr;
            if (!object->TryGetArrayField(TEXT("inputs"), inputs) || inputs->Num() > 256)
                return Fail(TEXT("Invalid render inputs."));
            auto inputIds = TSet<FString>{};
            for (const auto& value : *inputs)
            {
                auto inputObject = value.IsValid() && value->Type == EJson::Object ? value->AsObject() : nullptr;
                auto input = FInput{};
                const TArray<TSharedPtr<FJsonValue>>* defaults = nullptr;
                if (!inputObject || !inputObject->TryGetStringField(TEXT("id"), input.Id) ||
                    inputIds.Contains(input.Id) || !Integer(inputObject, TEXT("width"), input.Width) ||
                    input.Width < 1 || input.Width > 4 || !Integer(inputObject, TEXT("sourceComponent"), input.Component) ||
                    input.Component < 0 || input.Component > 4 - input.Width ||
                    !inputObject->TryGetArrayField(TEXT("defaults"), defaults) || defaults->Num() != input.Width)
                    return Fail(TEXT("Invalid render input shape."));
                inputIds.Add(input.Id);
                for (auto lane = 0; lane < input.Width; ++lane)
                    if (!(*defaults)[lane].IsValid() || !(*defaults)[lane]->TryGetNumber(input.Defaults[lane]))
                        return Fail(TEXT("Render defaults must be numeric."));
                if (!Finite(input.Defaults)) return Fail(TEXT("Non-finite render defaults."));
                auto source = FString{};
                if (inputObject->TryGetStringField(TEXT("sourceNodeId"), source) &&
                    (!FGuid::Parse(source, input.Source) || !input.Source.IsValid()))
                    return Fail(TEXT("Invalid render source identity."));
                const TSharedPtr<FJsonObject>* parameter = nullptr;
                if (inputObject->TryGetObjectField(TEXT("materialParameter"), parameter))
                {
                    input.bMaterial = true;
                    if (!ReadParameter(*parameter, input.Parameter, input.Slot) ||
                        !(*parameter)->TryGetStringField(TEXT("kind"), input.Kind) ||
                        (input.Kind != TEXT("scalar") && input.Kind != TEXT("vector")) ||
                        !draw.Materials.IsValidIndex(input.Slot) ||
                        !HasParameter(draw.Materials[input.Slot].Get(), input.Parameter, input.Kind))
                        return Fail(TEXT("Requested material parameter is unavailable."));
                }
                else if (input.Id != TEXT("position") && input.Id != TEXT("rotation") &&
                    input.Id != TEXT("scale") && input.Id != TEXT("tint") && input.Id != TEXT("frame") &&
                    input.Id != TEXT("columns") && input.Id != TEXT("rows") &&
                    input.Id != TEXT("fps") && input.Id != TEXT("playing"))
                    return Fail(TEXT("Unsupported render input: ") + input.Id);
                if (input.Id == TEXT("tint") && (input.Source.IsValid() || input.Defaults != FVector4(1, 1, 1, 1)) &&
                    !HasParameter(draw.Materials[0].Get(), FMaterialParameterInfo(TEXT("Tint")), TEXT("vector")) &&
                    !HasParameter(draw.Materials[0].Get(), FMaterialParameterInfo(TEXT("Color")), TEXT("vector")))
                    return Fail(TEXT("Tint requires a material exposing Tint or Color."));
                draw.Values.Add(input.Id, input.Defaults);
                draw.Inputs.Add(MoveTemp(input));
            }
            for (auto slot = 0; slot < draw.Materials.Num(); ++slot)
                for (const auto name : {TEXT("Tint"), TEXT("Color")})
                    if (HasParameter(draw.Materials[slot].Get(), FMaterialParameterInfo(name), TEXT("vector")) &&
                        !draw.Inputs.ContainsByPredicate([&](const auto& input)
                            { return input.bMaterial && input.Slot == slot && input.Parameter.Name == FName(name); }))
                        draw.TintTargets.Emplace(slot, FName(name));
            const TArray<TSharedPtr<FJsonValue>>* textures = nullptr;
            if (!object->TryGetArrayField(TEXT("textures"), textures) || textures->Num() > 256)
                return Fail(TEXT("Invalid texture overrides."));
            for (const auto& value : *textures)
            {
                auto textureObject = value.IsValid() && value->Type == EJson::Object ? value->AsObject() : nullptr;
                auto texture = FDraw::FTexture{};
                auto path = FString{};
                if (!textureObject || !ReadParameter(textureObject, texture.Parameter, texture.Slot) ||
                    !textureObject->TryGetStringField(TEXT("path"), path) || !draw.Materials.IsValidIndex(texture.Slot) ||
                    !HasParameter(draw.Materials[texture.Slot].Get(), texture.Parameter, TEXT("texture")))
                    return Fail(TEXT("Requested material texture parameter is unavailable."));
                if (path.IsEmpty())
                {
                    continue;
                }
                texture.Texture.Reset(Resolve<UTexture>(path, EKind::Texture));
                if (!texture.Texture.IsValid()) return Fail(TEXT("Missing Texture binding: ") + path);
                draw.Textures.Add(MoveTemp(texture));
            }
            draw.bOpaqueBatchCandidate = !draw.bSprite && !draw.bBillboard;
            for (const auto& material : draw.Materials)
            {
                const auto blendMode = material->GetBlendMode();
                if ((blendMode != BLEND_Opaque && blendMode != BLEND_Masked) ||
                    !material->CheckMaterialUsage(MATUSAGE_InstancedStaticMeshes))
                {
                    draw.bOpaqueBatchCandidate = false;
                    break;
                }
            }
            definition.Draws.Add(MoveTemp(draw));
        }
        if (cpuParticles)
        {
            for (const auto& value : *cpuParticles)
            {
                auto object = value.IsValid() && value->Type == EJson::Object ? value->AsObject() : nullptr;
                auto draw = FCpuParticleDraw{};
                auto id = FString{}, asset = FString{}, blendMode = FString{};
                auto drawScale = double{};
                auto billboard = false;
                if (!object || !object->TryGetStringField(TEXT("nodeId"), id) ||
                    !FGuid::Parse(id, draw.Id) || !draw.Id.IsValid() || ids.Contains(draw.Id) ||
                    !object->TryGetStringField(TEXT("assetPath"), asset) || asset.IsEmpty() ||
                    asset.Len() > 512 || !object->TryGetStringField(TEXT("blendMode"), blendMode) ||
                    (blendMode != TEXT("Alpha") && blendMode != TEXT("Additive")) ||
                    !object->TryGetNumberField(TEXT("drawScale"), drawScale) ||
                    !FMath::IsFinite(drawScale) || drawScale <= 0 || drawScale > MAX_flt ||
                    !object->TryGetBoolField(TEXT("billboard"), billboard) || !billboard)
                {
                    return Fail(TEXT("CPU particle Sprite descriptor is invalid or unsupported."));
                }
                ids.Add(draw.Id);
                draw.DrawScale = static_cast<float>(drawScale);
                draw.bAdditive = blendMode == TEXT("Additive");
                if (!LibraCpuParticleRendering::ReadAtlas(object,draw.Atlas)) return Fail(TEXT("Invalid CPU Flipbook settings."));
                if (!LibraParticleSpriteData::Read(object, draw.HasSpriteAnimation, draw.SpriteAnimation) ||
                    (draw.HasSpriteAnimation && (cpuVersion < 4 || draw.Atlas.enabled)))
                {
                    return Fail(TEXT("Invalid CPU Sprite editor animation; metadata version 4 is required."));
                }
                const auto rowValue = object->TryGetField(TEXT("drawMatrixRowNodeIds"));
                if (rowValue.IsValid() && rowValue->Type != EJson::Null)
                {
                    const TArray<TSharedPtr<FJsonValue>>* rows = nullptr;
                    if (cpuVersion < 2 || !object->TryGetArrayField(TEXT("drawMatrixRowNodeIds"), rows) || rows->Num() != 4)
                        return Fail(TEXT("CPU Draw Matrix requires four row IDs."));
                    for (const auto& matrixRowValue : *rows)
                    {
                        FString text;
                        FGuid row;
                        if (!matrixRowValue.IsValid() || !matrixRowValue->TryGetString(text) || !FGuid::Parse(text, row) || !row.IsValid())
                            return Fail(TEXT("Invalid CPU Draw Matrix row identity."));
                        draw.DrawRows.Add(row);
                    }
                }
                FString procTex, procMat;
                if (object->TryGetStringField(TEXT("proceduralTexture"), procTex))
                {
                    FGuid parsed;
                    draw.ProceduralTexture = FGuid::Parse(procTex, parsed)
                        ? parsed.ToString(EGuidFormats::DigitsWithHyphens).ToLower() : procTex;
                }
                if (object->TryGetStringField(TEXT("proceduralMaterial"), procMat))
                {
                    FGuid parsed;
                    draw.ProceduralMaterial = FGuid::Parse(procMat, parsed)
                        ? parsed.ToString(EGuidFormats::DigitsWithHyphens).ToLower() : procMat;
                }
                auto texture = (asset == TEXT("builtin:white") || asset == TEXT("builtin:radial-glow") ||
                                asset == TEXT("builtin:procedural-material") || asset == TEXT("builtin:procedural-texture"))
                    ? MakeWhiteTexture(asset == TEXT("builtin:radial-glow")) : Resolve<UTexture>(asset, EKind::Texture);
                if (!texture)
                {
                    return Fail(TEXT("Missing CPU particle Texture binding: ") + asset);
                }
                draw.Texture.Reset(texture);
                if (draw.HasSpriteAnimation)
                {
                    const auto *image = Cast<UTexture2D>(texture);
                    if (!image || !draw.SpriteAnimation.textureMatches(image->GetSizeX(), image->GetSizeY()))
                    {
                        return Fail(TEXT("CPU particle sprite source dimensions do not match its authored cells."));
                    }
                }
                FString customMaterialPath;
                const TArray<TSharedPtr<FJsonValue>>* matArray = nullptr;
                if (object->TryGetArrayField(TEXT("materials"), matArray) && matArray && !matArray->IsEmpty())
                {
                    for (const auto& matVal : *matArray)
                    {
                        auto matObj = matVal.IsValid() && matVal->Type == EJson::Object ? matVal->AsObject() : nullptr;
                        int32 matSlot = -1;
                        FString matPath;
                        if (matObj && matObj->TryGetNumberField(TEXT("slot"), matSlot) && matSlot == 0 &&
                            matObj->TryGetStringField(TEXT("path"), matPath) && !matPath.IsEmpty())
                        {
                            customMaterialPath = matPath;
                            break;
                        }
                    }
                }
                if (!customMaterialPath.IsEmpty())
                {
                    auto* customMaterial = Resolve<UMaterialInterface>(customMaterialPath, EKind::Material);
                    if (!customMaterial)
                    {
                        return Fail(TEXT("Missing CPU particle Material binding: ") + customMaterialPath);
                    }
                    customMaterial->CheckMaterialUsage(MATUSAGE_InstancedStaticMeshes);
                    draw.Material.Reset(customMaterial);
                    draw.bCustomMaterial = true;
                }
                else
                {
                    const auto* materialIdentity = draw.bAdditive
                        ? TEXT("builtin:cpu-particle-additive") : TEXT("builtin:cpu-particle-alpha");
                    auto* material = Resolve<UMaterialInterface>(materialIdentity, EKind::Material);
                    if (!material)
                    {
                        return Fail(TEXT("Missing CPU particle Sprite material binding: ") + FString(materialIdentity));
                    }
                    const auto expectedBlend = draw.bAdditive ? BLEND_Additive : BLEND_Translucent;
                    if (material->GetBlendMode() != expectedBlend || !material->IsTwoSided() ||
                        !material->CheckMaterialUsage(MATUSAGE_InstancedStaticMeshes) ||
                        !HasParameter(material, FMaterialParameterInfo(TEXT("Texture")), TEXT("texture")))
                    {
                        return Fail(TEXT("CPU particle material must be two-sided, support instanced static meshes, ")
                            TEXT("use the requested blend mode and expose a Texture parameter."));
                    }
                    draw.Material.Reset(material);
                    if ((draw.Atlas.enabled || draw.HasSpriteAnimation) &&
                        !HasParameter(material, FMaterialParameterInfo(TEXT("AtlasRect")), TEXT("vector")))
                    {
                        return Fail(TEXT(
                            "CPU Flipbook material must expose AtlasRect (top-left UV offset in XY, cell size in ZW)."));
                    }
                }
                auto* plane = Resolve<UStaticMesh>(TEXT("/Engine/BasicShapes/Plane.Plane"),
                    EKind::Model, false);
                if (!plane)
                {
                    return Fail(TEXT("Unreal engine Plane mesh is unavailable for CPU particle billboards."));
                }
                draw.Mesh.Reset(plane);
                definition.CpuParticles.Add(MoveTemp(draw));
            }
        }
        return true;
    }

    bool ReadGpu(const std::vector<std::uint8_t>& snapshots, const std::vector<std::uint8_t>& bytes,
        FDefinition& definition)
    {
        auto expectedParticles = TSet<FGuid>{};
        auto expectedFluids = TSet<FGuid>{};
        auto allExpected = TSet<FGuid>{};
        if (!snapshots.empty())
        {
            auto nodes = TArray<TSharedPtr<FJsonValue>>{};
            auto reader = TJsonReaderFactory<>::Create(Text(snapshots));
            if (!FJsonSerializer::Deserialize(reader, nodes) || nodes.Num() > 16)
                return Fail(TEXT("Invalid GPU snapshots or more than 16 emitters per scope."));
            for (const auto& node : nodes)
            {
                auto id = FString{};
                auto kind = FString{};
                auto guid = FGuid{};
                if (!node || node->Type != EJson::Object || !node->AsObject()->TryGetStringField(TEXT("id"), id) ||
                    !node->AsObject()->TryGetStringField(TEXT("kind"), kind) || kind != TEXT("GpuParticle") ||
                    !FGuid::Parse(id, guid) || !guid.IsValid() || allExpected.Contains(guid))
                    return Fail(TEXT("Invalid GPU container identity."));
                allExpected.Add(guid);
                auto isFluid = false;
                const TSharedPtr<FJsonObject>* gpuState = nullptr;
                if (node->AsObject()->HasField(TEXT("gpuState")))
                {
                    if (!node->AsObject()->TryGetObjectField(TEXT("gpuState"), gpuState) ||
                        !gpuState->IsValid())
                        return Fail(TEXT("Invalid GPU container state metadata."));
                    if ((*gpuState)->HasField(TEXT("fluid2D")))
                    {
                        const auto* marker = (*gpuState)->Values.Find(TEXT("fluid2D"));
                        if (!marker || !marker->IsValid() ||
                            !((*marker)->Type == EJson::Object ||
                                ((*marker)->Type == EJson::Boolean && (*marker)->AsBool())))
                            return Fail(TEXT("gpuState.fluid2D must be true or a fluid-state object."));
                        isFluid = true;
                    }
                }
                (isFluid ? expectedFluids : expectedParticles).Add(guid);
            }
        }
        const auto markedFluids = expectedFluids;
        if (bytes.empty())
            return (allExpected.IsEmpty() && definition.GpuDrawTransforms.IsEmpty() && definition.GpuEmitterTransforms.IsEmpty()) ||
                Fail(TEXT("GPU containers or draw Matrix bindings have no executable program manifest; re-export the source graph."));
        auto root = TSharedPtr<FJsonObject>{};
        auto reader = TJsonReaderFactory<>::Create(Text(bytes));
        auto version = int32{};
        const TArray<TSharedPtr<FJsonValue>>* programs = nullptr;
        const TArray<TSharedPtr<FJsonValue>>* fluids = nullptr;
        if (!FJsonSerializer::Deserialize(reader, root) || !root || !Integer(root, TEXT("version"), version) ||
            version != 4 || !root->TryGetArrayField(TEXT("programs"), programs))
        {
            return Fail(TEXT("GPU program manifest must use current version 4 and match its containers."));
        }
        auto emptyFluids = TArray<TSharedPtr<FJsonValue>>{};
        if (root->HasField(TEXT("fluidPrograms")))
        {
            if (!root->TryGetArrayField(TEXT("fluidPrograms"), fluids))
                return Fail(TEXT("fluidPrograms must be an array when present."));
        }
        else
        {
            if (!expectedFluids.IsEmpty())
                return Fail(TEXT("GPU snapshots contain Fluid2D nodes but the manifest has no fluidPrograms."));
            fluids = &emptyFluids;
        }
        if (programs->Num() + fluids->Num() > 16)
            return Fail(TEXT("GPU manifest exceeds the 16-program scope limit."));
        {
            auto manifestFluidIds = TSet<FGuid>{};
            for (const auto& item : *fluids)
            {
                auto object = item.IsValid() && item->Type == EJson::Object ? item->AsObject() : nullptr;
                auto id = FString{};
                auto guid = FGuid{};
                if (!object || !object->TryGetStringField(TEXT("nodeId"), id) ||
                    !FGuid::Parse(id, guid) || !allExpected.Contains(guid) || manifestFluidIds.Contains(guid))
                    return Fail(TEXT("FluidPrograms contains an invalid, duplicate, or unmatched nodeId."));
                manifestFluidIds.Add(guid);
            }
            if (markedFluids.IsEmpty())
            {
                for (const auto& guid : manifestFluidIds)
                {
                    if (!expectedParticles.Remove(guid))
                        return Fail(TEXT("FluidPrograms nodeId is already classified as a particle."));
                    expectedFluids.Add(guid);
                }
            }
            else
            {
                if (markedFluids.Num() != manifestFluidIds.Num())
                    return Fail(TEXT("gpuState.fluid2D markers do not match FluidPrograms entries."));
                for (const auto& guid : manifestFluidIds)
                    if (!markedFluids.Contains(guid))
                        return Fail(TEXT("gpuState.fluid2D markers do not match FluidPrograms entries."));
            }
            if (fluids->Num() != expectedFluids.Num() || programs->Num() != expectedParticles.Num())
                return Fail(TEXT("GPU manifest does not match its container snapshots."));
        }
        for (const auto& item : *programs)
        {
            auto object = item.IsValid() && item->Type == EJson::Object ? item->AsObject() : nullptr;
            auto id = FString{};
            auto guid = FGuid{};
            const TSharedPtr<FJsonObject>* delivery = nullptr;
            if (!object || !object->TryGetStringField(TEXT("nodeId"), id) || !FGuid::Parse(id, guid) ||
                !expectedParticles.Remove(guid) || !object->TryGetObjectField(TEXT("unreal"), delivery))
                return Fail(TEXT("Invalid executable GPU identity."));
            if (GUsingNullRHI) return Fail(TEXT("GPU execution requires a real SM5 graphics device, not NullRHI."));
            const TSharedPtr<FJsonObject>* paths = nullptr;
            if (!(*delivery)->TryGetObjectField(TEXT("resourceAssetPaths"), paths))
                return Fail(TEXT("GPU resource bindings are missing."));
            for (auto& entry : (*paths)->Values)
            {
                auto identity = FString{};
                if (!entry.Value || !entry.Value->TryGetString(identity))
                    return Fail(TEXT("Invalid GPU resource identity."));
                if (identity == TEXT("builtin:white")) continue;
                auto texture = Resolve<UTexture2D>(identity, ELibraPackageResourceKind::Texture);
                if (!texture) return Fail(TEXT("Missing GPU Texture2D binding: ") + identity);
                entry.Value = MakeShared<FJsonValueString>(texture->GetPathName());
            }
            auto text = FString{};
            auto writer = TJsonWriterFactory<>::Create(&text);
            FJsonSerializer::Serialize(delivery->ToSharedRef(), writer);
            writer->Close();
            auto error = FString{};
            auto asset = TStrongObjectPtr<ULibraGpuEffectAsset>(
                ULibraGpuEffectAsset::CreateInMemory(&Owner, text, error));
            if (!asset.IsValid()) return Fail(TEXT("GPU program rejected: ") + error);
            auto registers = uint64{};
            auto packetBytes = uint64{};
            for (const auto& kernel : asset->GetKernels())
            {
                if (kernel.Bytes.Num() < 20) return Fail(TEXT("Truncated GPU kernel."));
                auto count = uint32{};
                for (auto lane = 0; lane < 4; ++lane) count |= uint32(kernel.Bytes[16 + lane]) << (8 * lane);
                registers = FMath::Max(registers, uint64(count));
                packetBytes += kernel.Bytes.Num();
            }
            const auto requiredBytes = uint64(asset->GetCapacity()) * (80 + registers * 16) + packetBytes;
            if (requiredBytes > ULibraGpuEffectAsset::MaximumGpuBytes - definition.GpuBytes)
                return Fail(TEXT("GPU scope exceeds the 256 MiB budget."));
            definition.GpuBytes += requiredBytes;
            definition.GpuNodeIds.Add(guid);
            definition.Gpu.Add(MoveTemp(asset));
        }
        if (fluids)
        {
            for (const auto& item : *fluids)
            {
                auto object = item.IsValid() && item->Type == EJson::Object ? item->AsObject() : nullptr;
                auto program = FFluidProgram{};
                if (!ReadFluidProgram(object, program) || !expectedFluids.Remove(program.NodeId))
                    return Fail(TEXT("Invalid, duplicate, or unmatched FluidPrograms entry."));
                const auto fluidBytes = uint64(program.Settings.width) * program.Settings.height * 144;
                if (fluidBytes > ULibraGpuEffectAsset::MaximumGpuBytes - definition.GpuBytes)
                    return Fail(TEXT("GPU fluid scope exceeds the 256 MiB buffer and texture budget."));
                definition.GpuBytes += fluidBytes;
                definition.FluidPrograms.Add(MoveTemp(program));
            }
        }
        if (!expectedParticles.IsEmpty() || !expectedFluids.IsEmpty())
            return Fail(TEXT("GPU manifest omits one or more validated container programs."));
        for (const auto& asset : definition.Gpu)
        {
            for (const auto& texture : asset->GetTextures())
            {
                if (!texture.IsGenerated())
                {
                    continue;
                }
                FGuid producer;
                if (!FGuid::Parse(texture.GeneratedProducerNodeId, producer) ||
                    !definition.FluidPrograms.ContainsByPredicate([&](const auto& fluid) { return fluid.NodeId == producer; }))
                {
                    return Fail(TEXT("Generated texture producer must be in the same GPU execution scope."));
                }
            }
        }
        if (root->HasField(TEXT("generatedDrawBindings")))
        {
            const TArray<TSharedPtr<FJsonValue>>* entries = nullptr;
            if (!root->TryGetArrayField(TEXT("generatedDrawBindings"), entries) || entries->Num() > 64)
            {
                return Fail(TEXT("Generated draw bindings must be an array with at most 64 entries."));
            }
            for (const auto& value : *entries)
            {
                const auto object = value && value->Type == EJson::Object ? value->AsObject() : nullptr;
                auto binding = FGeneratedDrawTexture{};
                auto target = FString{}, producer = FString{}, name = FString{};
                auto association = int32{}, index = int32{};
                if (!object || object->Values.Num() != 8 ||
                    !object->TryGetStringField(TEXT("nodeId"), target) || !FGuid::Parse(target, binding.NodeId) ||
                    !object->TryGetStringField(TEXT("producerNodeId"), producer) || !FGuid::Parse(producer, binding.ProducerNodeId) ||
                    !object->TryGetStringField(TEXT("outputId"), binding.OutputId) ||
                    !object->TryGetStringField(TEXT("name"), name) || name.Len() > 256 ||
                    !Integer(object, TEXT("slot"), binding.Slot) || binding.Slot < 0 || binding.Slot > 255 ||
                    !Integer(object, TEXT("association"), association) || association < 0 || association > 2 ||
                    !Integer(object, TEXT("index"), index) || index < -1 ||
                    !object->TryGetBoolField(TEXT("mainTexture"), binding.bMainTexture) ||
                    !definition.FluidPrograms.ContainsByPredicate([&](const auto& fluid) { return fluid.NodeId == binding.ProducerNodeId; }) ||
                    (binding.OutputId != TEXT("ColorOutput") && binding.OutputId != TEXT("DyeOutput") && binding.OutputId != TEXT("FieldOutput")) ||
                    (binding.bMainTexture && binding.OutputId != TEXT("ColorOutput")))
                {
                    return Fail(TEXT("Invalid or cross-scope generated draw source."));
                }
                binding.Parameter = FMaterialParameterInfo(*name, EMaterialParameterAssociation(association), index);
                auto* draw = definition.Draws.FindByPredicate([&](const auto& item) { return item.Id == binding.NodeId; });
                const auto* plane = definition.FlexiblePlanes.FindByPredicate([&](const auto& item) { return item.Definition.nodeId == binding.NodeId; });
                if (binding.bMainTexture)
                {
                    if (!((draw && draw->bSprite && draw->bGeneratedMain) ||
                        (plane && plane->Definition.assetPath == TEXT("builtin:generated-texture"))))
                    {
                        return Fail(TEXT("Generated main texture requires a Sprite or Flexible Plane marker."));
                    }
                    binding.Slot = 0;
                    binding.Parameter = FMaterialParameterInfo(TEXT("Texture"));
                }
                else if (!draw || draw->bSprite || name.IsEmpty() || !draw->Materials.IsValidIndex(binding.Slot) ||
                    !HasParameter(draw->Materials[binding.Slot].Get(), binding.Parameter, TEXT("texture")))
                {
                    return Fail(TEXT("Generated material texture parameter is unavailable."));
                }
                if (definition.GeneratedDrawTextures.ContainsByPredicate([&](const auto& other) {
                    return other.NodeId == binding.NodeId && other.Slot == binding.Slot && other.Parameter == binding.Parameter;
                }))
                {
                    return Fail(TEXT("Duplicate generated material texture binding."));
                }
                if (draw)
                {
                    draw->bOpaqueBatchCandidate = false;
                }
                definition.GeneratedDrawTextures.Add(MoveTemp(binding));
            }
        }
        for (const auto& draw : definition.Draws)
        {
            if (draw.bGeneratedMain && !definition.GeneratedDrawTextures.ContainsByPredicate(
                [&](const auto& binding) { return binding.NodeId == draw.Id && binding.bMainTexture; }))
            {
                return Fail(TEXT("Generated sprite marker has no executable source binding."));
            }
        }
        for (const auto& plane : definition.FlexiblePlanes)
        {
            if (plane.Definition.assetPath == TEXT("builtin:generated-texture") &&
                !definition.GeneratedDrawTextures.ContainsByPredicate([&](const auto& binding) {
                    return binding.NodeId == plane.Definition.nodeId && binding.bMainTexture;
                }))
            {
                return Fail(TEXT("Generated Flexible Plane marker has no executable source binding."));
            }
        }
        for (const auto& transform : definition.GpuDrawTransforms)
        {
            auto isParticle = definition.GpuNodeIds.Contains(transform.Key);
            auto isFluid = definition.FluidPrograms.ContainsByPredicate(
                [&transform](const auto& program) { return program.NodeId == transform.Key; });
            if (!isParticle && !isFluid)
                return Fail(TEXT("GPU draw Matrix binding does not match an executable GPU container."));
        }
        for (const auto& transform : definition.GpuEmitterTransforms)
            if (!definition.GpuNodeIds.Contains(transform.Key))
                return Fail(TEXT("Emitter Matrix requires an executable particle container."));
        return true;
    }

    bool ReadGpuDrawTransform(const FDefinition& definition, FGuid nodeId,
        FGuid container, int64 instance,
        FMatrix44f& matrix, bool& connected, bool emitterBinding = false)
    {
        const auto* rows = (emitterBinding ? definition.GpuEmitterTransforms : definition.GpuDrawTransforms).Find(nodeId);
        if (!rows)
        {
            matrix = FMatrix44f::Identity;
            connected = false;
            return true;
        }
        if (!rows || rows->Num() != 4)
            return Fail(TEXT("GPU draw Matrix binding is incomplete."));
        connected = true;
        matrix = FMatrix44f::Identity;
        for (auto row = 0; row < 4; ++row)
        {
            auto value = FVector4{};
            auto width = int32{};
            auto result = container.IsValid()
                ? Owner.ReadChildValue(container, instance, (*rows)[row], value, width)
                : Owner.ReadRootValue((*rows)[row], value, width);
            if (result != ELibraRuntimeResult::Ok || width != 4 || !Finite(value))
                return Fail(TEXT("GPU draw Matrix row could not be read as a finite FloatX4 from its matching scope."));
            for (auto column = 0; column < 4; ++column)
                matrix.M[row][column] = static_cast<float>(value[column]);
        }
        if (!emitterBinding) matrix = FMatrix44f(Owner.EffectToHostMatrix().GetTransposed()) * matrix;
        return true;
    }

    bool Spawn(FGuid container, int64 id, double time, uint64 seed)
    {
        auto definition = Definitions.Find(container);
        if (!definition) return Fail(TEXT("Spawn references an unknown rendering template."));
        auto count = 0;
        for (const auto& instance : Instances)
        {
            if (instance->Container == container && instance->Id == id)
                return Fail(TEXT("Duplicate engine instance identity."));
            count += instance->Draws.Num() + instance->CpuDraws.Num() + instance->FlexiblePlanes.Num() +
                (instance->Debug ? instance->Debug->Count() : 0);
        }
        if (count + (*definition)->Draws.Num() + (*definition)->CpuParticles.Num() +
            (*definition)->FlexiblePlanes.Num() +
            (Owner.IsPreviewDebugDrawSuppressed() ? 0 : (*definition)->DebugCount) > 4096 ||
            (*definition)->GpuBytes > ULibraGpuEffectAsset::MaximumGpuBytes - GpuBytes)
            return Fail(TEXT("Active renderer or aggregate GPU memory budget exceeded."));
        auto actor = Owner.GetOwner();
        if (!actor || !actor->GetWorld()) return Fail(TEXT("Rendering requires a registered world owner."));
        auto instance = MakeUnique<FInstance>();
        instance->Container = container; instance->Id = id;
        instance->Started = instance->Previous = time;
        instance->Definition = definition->Get();
        auto* root = actor->GetRootComponent();
        const auto parentTransform = root ? root->GetComponentTransform() : actor->GetActorTransform();
        instance->PresentationRootTransform = parentTransform;
        instance->bHasPresentationRootTransform = true;
        const auto visible = !actor->IsHidden() && (!root || root->IsVisible());
        const auto ownerMatrix = FMatrix44f((Owner.EffectToHostMatrix() * parentTransform.ToMatrixWithScale()).GetTransposed());
        instance->Procedural = MakeUnique<FLibraProceduralScope>();
        auto proceduralError = FString{};
        if (!instance->Procedural->Create(Owner, instance->Definition->Procedural, proceduralError))
        {
            return Fail(proceduralError);
        }
        for (const auto& fluid : instance->Definition->FluidPrograms)
        {
            auto runtime = TStrongObjectPtr<ULibraGpuEffectRuntime>(
                NewObject<ULibraGpuEffectRuntime>(actor, NAME_None, RF_Transient));
            runtime->Initialize(actor);
            instance->Gpu.Add(MoveTemp(runtime));
            instance->GpuRuntimeNodeIds.Add(fluid.NodeId);
            instance->GpuRuntimeIsFluid.Add(true);
            auto gpu = instance->Gpu.Last().Get();
            auto drawTransform = FMatrix44f::Identity;
            auto connected = false;
            if (container.IsValid() &&
                !ReadGpuDrawTransform(*instance->Definition, fluid.NodeId, container, id,
                    drawTransform, connected))
            {
                return Fail(TEXT("GPU fluid Matrix binding is invalid."));
            }
            const auto localToWorld = connected ? drawTransform : ownerMatrix;
            if (!gpu->SetFluid2D(fluid.Settings, fluid.SizeScale, localToWorld, visible, fluid.bAutomaticPlane) ||
                !gpu->Play())
                return Fail(TEXT("GPU fluid instance creation failed: ") + gpu->LastError);
        }
        const auto resolveGenerated = [&](const FGeneratedDrawTexture& binding) -> UTexture*
        {
            const auto sourceIndex = instance->GpuRuntimeNodeIds.IndexOfByKey(binding.ProducerNodeId);
            return instance->Gpu.IsValidIndex(sourceIndex)
                ? instance->Gpu[sourceIndex]->GetFluidOutputTexture(binding.OutputId) : nullptr;
        };
        for (const auto& plane : instance->Definition->FlexiblePlanes)
        {
            if (!root)
            {
                return Fail(TEXT("Flexible Plane rendering requires an actor root component."));
            }
            auto renderer = TUniquePtr<FLibraFlexiblePlaneRenderer>{};
            auto error = FString{};
            const auto transform = root->GetComponentTransform();
            auto* texture = plane.Texture.Get();
            if (instance->Procedural &&
                (!plane.Definition.proceduralMaterial.IsEmpty() || !plane.Definition.proceduralTexture.IsEmpty()))
            {
                const auto& procId = !plane.Definition.proceduralMaterial.IsEmpty()
                    ? plane.Definition.proceduralMaterial
                    : plane.Definition.proceduralTexture;
                if (auto* procTexture = instance->Procedural->GetResourceTexture(procId))
                {
                    texture = procTexture;
                }
            }
            for (const auto& binding : instance->Definition->GeneratedDrawTextures)
            {
                if (binding.NodeId == plane.Definition.nodeId && binding.bMainTexture)
                {
                    texture = resolveGenerated(binding);
                }
            }
            if (!texture && plane.Definition.hostMaterialPath.IsEmpty())
            {
                return Fail(TEXT("Generated Flexible Plane texture is unavailable."));
            }
            if (!FLibraFlexiblePlaneRenderer::Create(Owner, plane.Definition,
                texture, plane.Material.Get(), transform,
                FName(TEXT("Texture")), renderer, error))
            {
                return Fail(error);
            }
            for (const auto& hostTex : plane.HostTextures)
            {
                renderer->SetTexture(hostTex.Value.Get(), hostTex.Key);
            }
            instance->FlexiblePlanes.Add(MoveTemp(renderer));
        }
        for (const auto& system : instance->Definition->CpuParticles)
        {
            if (!root)
            {
                return Fail(TEXT("CPU particle rendering requires an actor root component."));
            }
            auto& batch = instance->CpuDraws.AddDefaulted_GetRef();
            auto* material = UMaterialInstanceDynamic::Create(system.Material.Get(), &Owner);
            if (!material)
            {
                return Fail(TEXT("Could not create a CPU particle batch material."));
            }
            material->SetTextureParameterValue(TEXT("Texture"), system.Texture.Get());
            batch.Material.Reset(material);
        }
        if (instance->Definition->DebugConfiguration && !Owner.IsPreviewDebugDrawSuppressed())
        {
            instance->Debug = MakeUnique<FLibraDebugDrawScope>();
            auto error = FString{};
            if (!instance->Debug->Configure(*instance->Definition->DebugConfiguration, *actor,
                [this, container, id](FGuid source, FVector4& value, int32& width)
                {
                    return container.IsValid() ? Owner.ReadChildValue(container, id, source, value, width) :
                        Owner.ReadRootValue(source, value, width);
                }, [this](const FString& identity)
                { return Resolve<UStaticMesh>(identity, ELibraPackageResourceKind::Model); }, 1, error))
                return Fail(error);
        }
        for (const auto& draw : instance->Definition->Draws)
        {
            auto materialInstances = TArray<TStrongObjectPtr<UMaterialInstanceDynamic>>{};
            auto materialOverrides = TArray<TObjectPtr<UMaterialInterface>>{};
            materialInstances.Reserve(draw.Materials.Num());
            materialOverrides.Reserve(draw.Materials.Num());
            for (auto slot = 0; slot < draw.Materials.Num(); ++slot)
            {
                auto* material = UMaterialInstanceDynamic::Create(draw.Materials[slot].Get(), &Owner);
                if (!material)
                {
                    return Fail(TEXT("Could not create a native Draw material instance."));
                }
                materialInstances.Emplace(material);
                materialOverrides.Add(material);
            }
            for (const auto& texture : draw.Textures)
                materialInstances[texture.Slot].Get()
                    ->SetTextureParameterValueByInfo(texture.Parameter, texture.Texture.Get());
            for (const auto& binding : instance->Definition->GeneratedDrawTextures)
            {
                if (binding.NodeId != draw.Id)
                {
                    continue;
                }
                auto* texture = resolveGenerated(binding);
                if (!texture || !materialInstances.IsValidIndex(binding.Slot))
                {
                    return Fail(TEXT("Generated material texture source is unavailable."));
                }
                materialInstances[binding.Slot]->SetTextureParameterValueByInfo(binding.Parameter, texture);
            }
            auto primitive = MakeUnique<FLibraNativeMeshPrimitive>();
            if (!primitive->Create(Owner, draw.Mesh.Get(), MakeArrayView(materialOverrides),
                parentTransform,
                draw.SpriteAnimation.Version == 2 ? draw.SpriteAnimation.Composition.Order : 0,
                draw.Id, container, id))
            {
                return Fail(TEXT("Could not register a native StaticMesh scene primitive."));
            }
            instance->DrawMaterials.Add(MoveTemp(materialInstances));
            instance->Draws.Add(MoveTemp(primitive));
            instance->DrawLocalTransforms.Add(FTransform::Identity);
        }
        for (auto gpuIndex = 0; gpuIndex < instance->Definition->Gpu.Num(); ++gpuIndex)
        {
            const auto& asset = instance->Definition->Gpu[gpuIndex];
            auto runtime = TStrongObjectPtr<ULibraGpuEffectRuntime>(
                NewObject<ULibraGpuEffectRuntime>(actor, NAME_None, RF_Transient));
            runtime->Initialize(actor);
            instance->Gpu.Add(MoveTemp(runtime));
            instance->GpuRuntimeNodeIds.Add(instance->Definition->GpuNodeIds[gpuIndex]);
            instance->GpuRuntimeIsFluid.Add(false);
            auto gpu = instance->Gpu.Last().Get();
            if (!gpu->SetEffect(asset.Get()) ||
                !gpu->ConfigureEmitter(Owner.GpuSpawnPerSecond, static_cast<int32>(seed), true))
                return Fail(TEXT("GPU instance creation failed: ") + gpu->LastError);
            for (const auto& texture : asset->GetTextures())
            {
                if (!texture.IsGenerated())
                {
                    continue;
                }
                FGuid producer;
                if (!FGuid::Parse(texture.GeneratedProducerNodeId, producer))
                {
                    return Fail(TEXT("Generated texture producer identity is invalid."));
                }
                const auto producerIndex = instance->GpuRuntimeNodeIds.IndexOfByKey(producer);
                if (!instance->Gpu.IsValidIndex(producerIndex) || !instance->GpuRuntimeIsFluid[producerIndex] ||
                    !gpu->BindGeneratedTexture(texture.Id, instance->Gpu[producerIndex].Get(), texture.GeneratedOutputId))
                {
                    return Fail(TEXT("Generated texture producer is not active in the same scope."));
                }
            }
            auto drawTransform = FMatrix44f::Identity;
            auto connected = false;
            // Root values become available during the initial native advance.
            if (container.IsValid() &&
                !ReadGpuDrawTransform(*instance->Definition, instance->Definition->GpuNodeIds[gpuIndex],
                    container, id, drawTransform, connected))
            {
                return Fail(TEXT("GPU draw Matrix binding is invalid."));
            }
            if (!gpu->SetDrawTransform(connected ? drawTransform : ownerMatrix, true))
            {
                return Fail(gpu->LastError.IsEmpty()
                    ? TEXT("GPU draw Matrix binding is invalid.") : gpu->LastError);
            }
            if (container.IsValid())
            {
                auto emitterMatrix = FMatrix44f::Identity;
                auto emitterConnected = false;
                if (!ReadGpuDrawTransform(*instance->Definition, instance->Definition->GpuNodeIds[gpuIndex],
                    container, id, emitterMatrix, emitterConnected, true) || !gpu->SetEmitterTransform(emitterMatrix))
                    return Fail(TEXT("Emitter Matrix binding is invalid."));
            }
            if (!gpu->Play())
                return Fail(TEXT("GPU instance creation failed: ") + gpu->LastError);
        }
        GpuBytes += instance->Definition->GpuBytes;
        Instances.Add(MoveTemp(instance));
        return true;
    }

    bool UpdateOpaqueDrawBatches(FInstance* instance)
    {
        auto* actor = Owner.GetOwner();
        auto* root = actor ? actor->GetRootComponent() : nullptr;
        if (!actor)
        {
            return Fail(TEXT("Opaque Draw batching lost its actor owner."));
        }
        const auto rootTransform = root ? root->GetComponentTransform() : actor->GetActorTransform();
        const auto visible = !actor->IsHidden() && (!root || root->IsVisible());
        auto groups = TMap<FString, TArray<int32>>{};
        for (auto drawIndex = 0; drawIndex < instance->Definition->Draws.Num(); ++drawIndex)
        {
            const auto& draw = instance->Definition->Draws[drawIndex];
            if (draw.bOpaqueBatchCandidate)
            {
                const auto worldTransform = instance->DrawLocalTransforms[drawIndex] * rootTransform;
                if (LibraOpaqueBatching::HasSafePositiveInstanceTransform(worldTransform, rootTransform))
                {
                    groups.FindOrAdd(DrawBatchKey(draw)).Add(drawIndex);
                }
            }
        }

        auto previousBatches = MoveTemp(instance->DrawBatches);
        auto nextBatches = TArray<FDrawBatch>{};
        auto batchedDraws = TSet<int32>{};
        for (const auto& entry : groups)
        {
            if (entry.Value.Num() < 2)
            {
                continue;
            }
            auto batch = FDrawBatch{};
            auto existingIndex = previousBatches.IndexOfByPredicate(
                [&](const auto& item) { return item.Key == entry.Key; });
            const auto isNewBatch = existingIndex == INDEX_NONE;
            if (existingIndex != INDEX_NONE)
            {
                batch = MoveTemp(previousBatches[existingIndex]);
                previousBatches[existingIndex].Key.Reset();
            }
            else
            {
                batch.Primitive = MakeUnique<FLibraNativeInstancedMeshPrimitive>();
            }
            batch.Key = entry.Key;
            batch.DrawIndices = entry.Value;
            batch.Transforms.Reset();
            batch.CustomData.Reset();
            batch.Transforms.Reserve(entry.Value.Num());
            batch.CustomData.Reserve(entry.Value.Num());
            for (const auto drawIndex : entry.Value)
            {
                batch.Transforms.Add(instance->DrawLocalTransforms[drawIndex] * rootTransform);
                batch.CustomData.Add(FLinearColor::Transparent);
                batchedDraws.Add(drawIndex);
                instance->Draws[drawIndex].Reset();
            }
            auto submitted = false;
            if (isNewBatch)
            {
                const auto representative = entry.Value[0];
                auto materials = TArray<TObjectPtr<UMaterialInterface>>{};
                materials.Reserve(instance->DrawMaterials[representative].Num());
                for (const auto& material : instance->DrawMaterials[representative])
                {
                    materials.Add(material.Get());
                }
                submitted = batch.Primitive->CreateWithInstances(Owner,
                    instance->Definition->Draws[representative].Mesh.Get(),
                    MakeArrayView(materials), MakeArrayView(batch.Transforms),
                    MakeArrayView(batch.CustomData), rootTransform);
            }
            else
            {
                submitted = batch.Primitive.IsValid() &&
                    batch.Primitive->Update(batch.Transforms, batch.CustomData, rootTransform);
            }
            if (!submitted)
            {
                return Fail(TEXT("Could not update a compatible opaque StaticMesh batch."));
            }
            batch.Primitive->SetVisible(visible);
            nextBatches.Add(MoveTemp(batch));
        }

        for (auto drawIndex = 0; drawIndex < instance->Definition->Draws.Num(); ++drawIndex)
        {
            if (batchedDraws.Contains(drawIndex))
            {
                continue;
            }
            const auto& draw = instance->Definition->Draws[drawIndex];
            if (!instance->Draws[drawIndex])
            {
                auto materials = TArray<TObjectPtr<UMaterialInterface>>{};
                materials.Reserve(instance->DrawMaterials[drawIndex].Num());
                for (const auto& material : instance->DrawMaterials[drawIndex])
                {
                    materials.Add(material.Get());
                }
                instance->Draws[drawIndex] = MakeUnique<FLibraNativeMeshPrimitive>();
                if (!instance->Draws[drawIndex]->Create(Owner, draw.Mesh.Get(),
                    MakeArrayView(materials), instance->DrawLocalTransforms[drawIndex] * rootTransform,
                    draw.SpriteAnimation.Version == 2 ? draw.SpriteAnimation.Composition.Order : 0,
                    draw.Id, instance->Container, instance->Id))
                {
                    return Fail(TEXT("Could not restore a native primitive for an unbatched Draw."));
                }
            }
            else
            {
                instance->Draws[drawIndex]->SetTransform(instance->DrawLocalTransforms[drawIndex] * rootTransform);
            }
            instance->Draws[drawIndex]->SetVisible(visible);
        }
        instance->DrawBatches = MoveTemp(nextBatches);
        return true;
    }

    bool Update(double time)
    {
        for (auto& instance : Instances)
        {
            if (instance->Procedural)
            {
                auto* actor = Owner.GetOwner();
                auto* root = actor ? actor->GetRootComponent() : nullptr;
                const auto transform = FTransform(Owner.EffectToHostMatrix() *
                    (root ? root->GetComponentTransform().ToMatrixWithScale() : FMatrix::Identity));
                auto error = FString{};
                if (!instance->Procedural->Update([this, &instance](const liberafx::flexible_adapter::Id& id,
                    double* output, uint8& width) -> LibraResult
                    {
                        auto value = FVector4{}; auto count = int32{};
                        const auto result = instance->Container.IsValid()
                            ? Owner.ReadChildValue(instance->Container, instance->Id, Guid(id), value, count)
                            : Owner.ReadRootValue(Guid(id), value, count);
                        if (result != ELibraRuntimeResult::Ok || count < 1 || count > 4 || !Finite(value))
                        {
                            return result == ELibraRuntimeResult::Ok ? LIBRA_INVALID_VALUE_TYPE : static_cast<LibraResult>(result);
                        }
                        width = static_cast<uint8>(count);
                        for (auto i = 0; i < count; ++i)
                        {
                            output[i] = value[i];
                        }
                        return LIBRA_OK;
                    }, time - instance->Started, transform, actor && !actor->IsHidden() && (!root || root->IsVisible()), error))
                {
                    return Fail(error);
                }
            }
            instance->FlexibleHistory.beginUpdate();
            for (auto planeIndex = 0; planeIndex < instance->FlexiblePlanes.Num(); ++planeIndex)
            {
                auto& plane = instance->FlexiblePlanes[planeIndex];
                auto cameraPosition = FVector::ZeroVector;
                auto* actor = Owner.GetOwner();
                auto* root = actor ? actor->GetRootComponent() : nullptr;
                if (!root)
                {
                    return Fail(TEXT("Flexible Plane actor root was destroyed externally."));
                }
                const auto transform = FTransform(Owner.EffectToHostMatrix() * root->GetComponentTransform().ToMatrixWithScale());
                auto* overrideCamera = Owner.EffectCameraOverride.Get();
                auto* world = Owner.GetWorld();
                auto* controller = world ? world->GetFirstPlayerController() : nullptr;
                auto* camera = controller ? controller->PlayerCameraManager.Get() : nullptr;
                if (IsValid(overrideCamera))
                {
                    cameraPosition = overrideCamera->GetComponentLocation();
                }
                else if (IsValid(camera))
                {
                    cameraPosition = camera->GetCameraLocation();
                }
#if WITH_EDITOR
                else if (GCurrentLevelEditingViewportClient &&
                    (!world || world->WorldType == EWorldType::Editor || world->WorldType == EWorldType::EditorPreview))
                {
                    cameraPosition = GCurrentLevelEditingViewportClient->GetViewLocation();
                }
#endif
                else if (instance->Definition->FlexiblePlanes[planeIndex].Definition.native.options.facing == 1)
                {
                    return Fail(TEXT("Flexible Plane rendering requires a current effect or player camera."));
                }
                auto error = FString{};
                const auto& planeDef = instance->Definition->FlexiblePlanes[planeIndex].Definition;
                if (instance->Procedural &&
                    (!planeDef.proceduralMaterial.IsEmpty() || !planeDef.proceduralTexture.IsEmpty()))
                {
                    const auto& procId = !planeDef.proceduralMaterial.IsEmpty()
                        ? planeDef.proceduralMaterial
                        : planeDef.proceduralTexture;
                    if (auto* procTexture = instance->Procedural->GetResourceTexture(procId))
                    {
                        plane->SetTexture(procTexture, FName(TEXT("Texture")));
                    }
                }
                if (!plane->Update(time - instance->Started, transform, cameraPosition,
                    [this, &instance](const uint8* source, double* output, uint8& width) -> LibraResult
                    {
                        auto id = std::array<uint8, 16>{};
                        std::copy_n(source, id.size(), id.begin());
                        auto value = FVector4{};
                        auto count = int32{};
                        const auto result = instance->Container.IsValid()
                            ? Owner.ReadChildValue(instance->Container, instance->Id, Guid(id), value, count)
                            : Owner.ReadRootValue(Guid(id), value, count);
                        if (result != ELibraRuntimeResult::Ok)
                        {
                            return static_cast<LibraResult>(result);
                        }
                        if (count < 1 || count > 4 || !Finite(value))
                        {
                            return LIBRA_INVALID_VALUE_TYPE;
                        }
                        width = static_cast<uint8>(count);
                        for (auto lane = 0; lane < count; ++lane)
                        {
                            output[lane] = value[lane];
                        }
                        return LIBRA_OK;
                    }, instance->FlexibleHistory, error))
                {
                    return Fail(error);
                }
            }
            if (instance->Debug)
            {
                auto error = FString{};
                if (!instance->Debug->Update(1, error)) return Fail(error);
            }
            for (auto index = 0; index < instance->Draws.Num(); ++index)
            {
                auto& draw = instance->Definition->Draws[index];
                auto* primitive = instance->Draws[index].Get();
                if ((primitive && !primitive->HasSceneProxy()) ||
                    (!primitive && !draw.bOpaqueBatchCandidate))
                    return Fail(TEXT("An owned native scene primitive was removed externally."));
                auto& materials = instance->DrawMaterials[index];
                auto& values = draw.Values;
                for (const auto& input : draw.Inputs)
                {
                    auto value = input.Defaults;
                    if (input.Source.IsValid())
                    {
                        auto source = FVector4{};
                        auto width = int32{};
                        auto result = instance->Container.IsValid()
                            ? Owner.ReadChildValue(instance->Container, instance->Id, input.Source, source, width)
                            : Owner.ReadRootValue(input.Source, source, width);
                        if (result != ELibraRuntimeResult::Ok || input.Component + input.Width > width)
                            return Fail(TEXT("Renderer cannot read its own instance's source value."));
                        for (auto lane = 0; lane < input.Width; ++lane) value[lane] = source[input.Component + lane];
                    }
                    if (!Finite(value)) return Fail(TEXT("Renderer received a non-finite value."));
                    values.FindChecked(input.Id) = value;
                    if (input.bMaterial)
                    {
                        auto* material = materials[input.Slot].Get();
                        if (input.Kind == TEXT("scalar")) material->SetScalarParameterValueByInfo(input.Parameter, value.X);
                        else material->SetVectorParameterValueByInfo(input.Parameter, FLinearColor(value.X, value.Y, value.Z, value.W));
                    }
                }
                static const auto positionId = FString(TEXT("position")), rotationId = FString(TEXT("rotation")),
                    scaleId = FString(TEXT("scale")), frameId = FString(TEXT("frame")), playingId = FString(TEXT("playing")),
                    fpsId = FString(TEXT("fps")), columnsId = FString(TEXT("columns")), rowsId = FString(TEXT("rows")),
                    tintId = FString(TEXT("tint"));
                auto position = values.FindRef(positionId);
                auto rotation = values.FindRef(rotationId);
                auto scale = values.Contains(scaleId) ? values[scaleId] : FVector4(1, 1, 1, 0);
                auto pose = FTransform(FRotator(rotation.Y, rotation.Z, rotation.X),
                    FVector(position.X, position.Y, position.Z), FVector(scale.X, scale.Y, scale.Z));
                if (draw.bSprite)
                {
                    pose.SetScale3D(pose.GetScale3D() / 100);
                    if (draw.bBillboard)
                        if (auto player = Owner.GetWorld()->GetFirstPlayerController(); player && player->PlayerCameraManager)
                        {
                            auto direction = player->PlayerCameraManager->GetCameraLocation() -
                                (Owner.GetOwner()->GetActorTransform().TransformPosition(pose.GetLocation()));
                            if (!direction.IsNearlyZero())
                                pose.SetRotation(Owner.GetOwner()->GetActorQuat().Inverse() *
                                    FQuat::FindBetweenNormals(FVector::UpVector, direction.GetSafeNormal()));
                        }
                    auto frame = values.FindRef(frameId).X;
                    auto frameConnected = draw.Inputs.ContainsByPredicate(
                        [](const auto& input) { return input.Id == TEXT("frame") && input.Source.IsValid(); });
                    auto atlas = liberafx::AtlasFrame{};
                    if (draw.SpriteAnimation.bPresent)
                    {
                        if (frameConnected && frame < 0)
                            return Fail(TEXT("Connected Sprite Frame input must be a non-negative sequence index."));
                        auto sequenceFrame = uint32{};
                        auto elapsed = time - instance->Started;
                        auto keyedPose = liberafx::SpritePose{};
                        const auto sampled = draw.SpriteAnimation.Version == 2
                            ? draw.SpriteAnimation.Composition.Sample(elapsed, draw.SpriteAnimation.Fps,
                                draw.SpriteAnimation.bLoop, draw.SpriteAnimation.Durations, sequenceFrame, keyedPose)
                            : liberafx::sampleSpriteSequence(frame, elapsed, draw.SpriteAnimation.Fps,
                            draw.SpriteAnimation.Durations.GetData(),
                            static_cast<uint32>(draw.SpriteAnimation.Durations.Num()),
                            static_cast<uint32>(draw.SpriteAnimation.StartFrame),
                            static_cast<uint32>(draw.SpriteAnimation.EndFrame),
                            draw.SpriteAnimation.bLoop, values.FindRef(playingId).X >= 0.5,
                            sequenceFrame);
                        if (!sampled || !draw.SpriteAnimation.Frames.IsValidIndex(sequenceFrame))
                            return Fail(TEXT("Invalid sprite animation sequence or playback time."));
                        const auto& cell = draw.SpriteAnimation.Cells[
                            draw.SpriteAnimation.Frames[sequenceFrame].CellIndex];
                        if (draw.SpriteAnimation.Version == 2)
                            pose = FLibraSpriteCompositionRender::Transform(keyedPose, cell.PixelWidth, cell.PixelHeight);
                        if (!liberafx::sampleSpriteRect(sequenceFrame, cell.U, cell.V,
                                cell.Width, cell.Height, atlas))
                            return Fail(TEXT("Invalid arbitrary sprite UV rectangle."));
                    }
                    else
                    {
                        if (frame < 0 && !frameConnected && values.FindRef(playingId).X >= 0.5)
                            frame = (time - instance->Started) * values.FindRef(fpsId).X;
                        auto columns = values.FindRef(columnsId).X, rows = values.FindRef(rowsId).X;
                        if (columns < 1 || columns > MAX_uint32 || rows < 1 || rows > MAX_uint32 ||
                            FMath::FloorToDouble(columns) != columns || FMath::FloorToDouble(rows) != rows ||
                            !liberafx::sampleSpriteAtlas(frame, uint32(columns), uint32(rows), atlas))
                            return Fail(TEXT("Invalid legacy sprite atlas settings."));
                    }
                    materials[0].Get()->SetVectorParameterValue(
                        TEXT("AtlasRect"), FLinearColor(atlas.uvRect[0], atlas.uvRect[1], atlas.uvRect[2], atlas.uvRect[3]));
                }
                if (auto tint = values.Find(tintId))
                    for (const auto& target : draw.TintTargets)
                    {
                        auto* material = materials[target.Key].Get();
                        material->SetVectorParameterValue(target.Value, FLinearColor(tint->X, tint->Y, tint->Z, tint->W));
                    }
                auto* actor = Owner.GetOwner();
                auto* root = actor ? actor->GetRootComponent() : nullptr;
                if (!actor)
                {
                    return Fail(TEXT("Native mesh rendering lost its actor owner."));
                }
                const auto rootTransform = root ? root->GetComponentTransform() : actor->GetActorTransform();
                const auto coordinates = Owner.EffectToHostMatrix();
                if (draw.bSprite && draw.bBillboard)
                {
                    pose.SetTranslation(coordinates.TransformPosition(pose.GetTranslation()));
                    pose.SetScale3D(pose.GetScale3D() * coordinates.GetScaledAxis(EAxis::X).Size());
                }
                else
                    pose = FTransform((draw.bSprite ? FMatrix::Identity : coordinates.Inverse()) *
                        pose.ToMatrixWithScale() * coordinates);
                instance->DrawLocalTransforms[index] = pose;
                if (primitive)
                {
                    primitive->SetTransform(pose * rootTransform);
                }
            }
            if (!UpdateOpaqueDrawBatches(instance.Get()))
            {
                return false;
            }
            if (instance->CpuDraws.Num() != instance->Definition->CpuParticles.Num())
            {
                return Fail(TEXT("CPU particle batches do not match their definitions."));
            }
            for (auto systemIndex = 0; systemIndex < instance->Definition->CpuParticles.Num(); ++systemIndex)
            {
                const auto& system = instance->Definition->CpuParticles[systemIndex];
                auto particleCount = uint32{};
                auto result = Owner.ReadCpuParticleCount(instance->Container, instance->Id,
                    system.Id, particleCount);
                if (result != ELibraRuntimeResult::Ok)
                {
                    return Fail(TEXT("CPU particle count read failed with runtime result ") +
                        FString::FromInt(static_cast<int32>(result)) + TEXT("."));
                }
                if (particleCount > 1000000)
                {
                    return Fail(TEXT("CPU particle count exceeds the native system capacity limit."));
                }
                auto& batch = instance->CpuDraws[systemIndex];
                auto* actor = Owner.GetOwner();
                auto* root = actor ? actor->GetRootComponent() : nullptr;
                if (!batch.Material.IsValid() || !root)
                {
                    return Fail(TEXT("An owned CPU particle primitive or its actor root was destroyed externally."));
                }
                if (instance->Procedural && !system.bCustomMaterial &&
                    (!system.ProceduralMaterial.IsEmpty() || !system.ProceduralTexture.IsEmpty()))
                {
                    const auto& procId = !system.ProceduralMaterial.IsEmpty()
                        ? system.ProceduralMaterial : system.ProceduralTexture;
                    if (auto* procTexture = instance->Procedural->GetResourceTexture(procId))
                    {
                        batch.Material->SetTextureParameterValue(TEXT("Texture"), procTexture);
                    }
                }
                auto* world = Owner.GetWorld();
                auto* controller = world ? world->GetFirstPlayerController() : nullptr;
                auto* camera = controller ? controller->PlayerCameraManager.Get() : nullptr;
                auto* overrideCamera = Owner.EffectCameraOverride.Get();
                auto hasEditorViewport = false;
                auto editorViewQuat = FQuat::Identity;
#if WITH_EDITOR
                if (!IsValid(overrideCamera) && !IsValid(camera) && GCurrentLevelEditingViewportClient &&
                    (!world || world->WorldType == EWorldType::Editor || world->WorldType == EWorldType::EditorPreview))
                {
                    hasEditorViewport = true;
                    editorViewQuat = GCurrentLevelEditingViewportClient->GetViewRotation().Quaternion();
                }
#endif
                if (particleCount != 0 && !IsValid(overrideCamera) && !IsValid(camera) && !hasEditorViewport)
                {
                    return Fail(TEXT("CPU particle billboard rendering requires a player camera."));
                }
                auto rotation = FQuat::Identity;
                if (IsValid(overrideCamera) || IsValid(camera) || hasEditorViewport)
                {
                    const auto view = IsValid(overrideCamera) ? overrideCamera->GetComponentQuat()
                        : IsValid(camera) ? camera->GetCameraRotation().Quaternion()
                        : editorViewQuat;
                    rotation = FRotationMatrix::MakeFromXY(view.GetRightVector(), view.GetUpVector()).ToQuat();
                }
                const auto ownerTransform = root->GetComponentTransform();
                auto drawWorld = Owner.EffectToHostMatrix() * ownerTransform.ToMatrixWithScale();
                if (!system.DrawRows.IsEmpty())
                {
                    for (auto row = 0; row < 4; ++row)
                    {
                        FVector4 value;
                        auto width = int32{};
                        const auto read = instance->Container.IsValid()
                            ? Owner.ReadChildValue(instance->Container, instance->Id, system.DrawRows[row], value, width)
                            : Owner.ReadRootValue(system.DrawRows[row], value, width);
                        if (read != ELibraRuntimeResult::Ok || width != 4 || !Finite(value))
                            return Fail(TEXT("Could not read CPU Draw Matrix."));
                        for (auto column = 0; column < 4; ++column) drawWorld.M[column][row] = value[column];
                    }
                    drawWorld = drawWorld * Owner.EffectToHostMatrix();
                }
                const auto ownerScale = FVector(drawWorld.GetScaledAxis(EAxis::X).Size(),
                    drawWorld.GetScaledAxis(EAxis::Y).Size(), drawWorld.GetScaledAxis(EAxis::Z).Size());
                const auto count = static_cast<int32>(particleCount);
                batch.Transforms.Reset();
                batch.Colors.Reset();
                for (auto& entry : batch.Frames) { entry.Value->Transforms.Reset(); entry.Value->Colors.Reset(); }
                std::array<liberafx::CpuParticleSnapshot,256> particleBatch;
                std::array<liberafx::CpuParticleTiming,256> timingBatch;
                for (auto part = std::size_t{};
                     part < (system.HasSpriteAnimation ? system.SpriteAnimation.partCount() : 1); ++part)
                {
                    for (auto particleIndex = 0; particleIndex < count; ++particleIndex)
                    {
                        if (particleIndex % 256 == 0)
                        {
                            SIZE_T written = 0;
                            result = Owner.ReadCpuParticleBatch(
                                instance->Container, instance->Id, system.Id, particleIndex, particleBatch.data(),
                                system.Atlas.enabled || system.HasSpriteAnimation ? timingBatch.data() : nullptr,
                                particleBatch.size(), written);
                            if (result != ELibraRuntimeResult::Ok ||
                                written != static_cast<SIZE_T>(FMath::Min(256, count - particleIndex)))
                            {
                                return Fail(TEXT("CPU particle batch read failed."));
                            }
                        }
                        const auto &particle = particleBatch[particleIndex % 256];
                        if (particle.id == 0)
                        {
                            return Fail(TEXT("CPU particle snapshot read failed."));
                        }
                        const auto positionX = particle.position[0], positionY = particle.position[1],
                                   positionZ = particle.position[2];
                        const auto colorR = particle.color[0], colorG = particle.color[1], colorB = particle.color[2],
                                   colorA = particle.color[3];
                        const auto sizeX = particle.size[0], sizeY = particle.size[1];
                        auto worldPosition = drawWorld.TransformPosition(FVector(
                            positionX * system.DrawScale, positionY * system.DrawScale, positionZ * system.DrawScale));
                        auto scaledX = sizeX * system.DrawScale * ownerScale.X;
                        auto scaledY = sizeY * system.DrawScale * ownerScale.Y;
                        auto particleRotation = rotation;
                        auto sprite = liberafx::ParticleSpriteSample{};
                        if (system.HasSpriteAnimation)
                        {
                            if (system.SpriteAnimation.sample(part, timingBatch[particleIndex % 256].age, sprite) !=
                                liberafx::Result::Ok)
                            {
                                return Fail(TEXT("Invalid CPU sprite animation time."));
                            }
                            worldPosition +=
                                rotation.GetAxisX() * (scaledX * sprite.x) + rotation.GetAxisY() * (scaledY * sprite.y);
                            scaledX *= sprite.scaleX;
                            scaledY *= sprite.scaleY;
                            particleRotation = rotation * FQuat(FVector::UpVector, sprite.rotation);
                        }
                        if (!Finite(FVector4(worldPosition.X, worldPosition.Y, worldPosition.Z, scaledX)) ||
                            !Finite(FVector4(scaledY, 0, 0, 0)) || !Finite(FVector4(colorR, colorG, colorB, colorA)))
                        {
                            return Fail(TEXT("CPU particle snapshot exceeds Unreal's finite float range."));
                        }
                        auto *target = &batch;
                        if (system.Atlas.enabled || system.HasSpriteAnimation)
                        {
                            const auto &timing = timingBatch[particleIndex % 256];
                            const auto frame = system.HasSpriteAnimation ? static_cast<uint32>(part * 512) + sprite.cell
                                                                         : system.Atlas.frame(timing.age, timing.life);
                            auto &entry = batch.Frames.FindOrAdd(frame);
                            if (!entry)
                            {
                                entry = MakeUnique<LibraCpuParticleRendering::FBatch>();
                                auto *atlasMaterial = UMaterialInstanceDynamic::Create(system.Material.Get(), &Owner);
                                atlasMaterial->SetTextureParameterValue(TEXT("Texture"), system.Texture.Get());
                                if (system.HasSpriteAnimation)
                                {
                                    atlasMaterial->SetVectorParameterValue(
                                        TEXT("AtlasRect"),
                                        FLinearColor(sprite.uv[0], sprite.uv[1], sprite.uv[2], sprite.uv[3]));
                                }
                                else
                                {
                                    atlasMaterial->SetVectorParameterValue(
                                        TEXT("AtlasRect"),
                                        FLinearColor(
                                            static_cast<float>(frame % system.Atlas.columns) / system.Atlas.columns,
                                            static_cast<float>(frame / system.Atlas.columns) / system.Atlas.rows,
                                            1.0f / system.Atlas.columns, 1.0f / system.Atlas.rows));
                                }
                                entry->Material.Reset(atlasMaterial);
                            }
                            target = entry.Get();
                        }
                        target->Transforms.Emplace(particleRotation, worldPosition,
                                                   FVector(scaledX / 100, scaledY / 100, 1));
                        target->Colors.Emplace(static_cast<float>(colorR), static_cast<float>(colorG),
                                               static_cast<float>(colorB), static_cast<float>(colorA));
                    }
                }
                auto submitted = true;
                if (system.HasSpriteAnimation)
                {
                    batch.Frames.KeySort([](uint32 a, uint32 b) { return a < b; });
                }
                LibraCpuParticleRendering::ForEach(batch,[&](auto& frameBatch) {
                if (!frameBatch.Primitive.IsValid() && !frameBatch.Transforms.IsEmpty())
                {
                    frameBatch.Primitive = MakeUnique<FLibraNativeInstancedMeshPrimitive>();
                    auto materialOverrides = TArray<TObjectPtr<UMaterialInterface>>{
                        frameBatch.Material.Get()};
                    if (!frameBatch.Primitive->CreateWithInstances(Owner, system.Mesh.Get(),
                        MakeArrayView(materialOverrides), MakeArrayView(frameBatch.Transforms),
                        MakeArrayView(frameBatch.Colors), ownerTransform))
                    {
                        submitted = false;
                    }
                }
                else if (frameBatch.Primitive.IsValid() &&
                    !frameBatch.Primitive->Update(frameBatch.Transforms, frameBatch.Colors, ownerTransform))
                {
                    submitted = false;
                }
                });
                if (!submitted) return Fail(TEXT("Could not submit CPU particle atlas batches."));
            }
            auto delta = time - instance->Previous;
            if (!FMath::IsFinite(delta) || delta < 0 || delta > MAX_flt) return Fail(TEXT("Invalid GPU local time."));
            auto* ownerActor = Owner.GetOwner();
            auto* ownerRoot = ownerActor ? ownerActor->GetRootComponent() : nullptr;
            if (!ownerActor) return Fail(TEXT("GPU presentation lost its actor owner."));
            const auto ownerTransform = ownerRoot ? ownerRoot->GetComponentTransform() : ownerActor->GetActorTransform();
            const auto ownerMatrix = FMatrix44f((Owner.EffectToHostMatrix() * ownerTransform.ToMatrixWithScale()).GetTransposed());
            const auto visible = !ownerActor->IsHidden() && (!ownerRoot || ownerRoot->IsVisible());
            if (instance->GpuRuntimeNodeIds.Num() != instance->Gpu.Num() ||
                instance->GpuRuntimeIsFluid.Num() != instance->Gpu.Num())
                return Fail(TEXT("GPU runtime identities do not match their prepared programs."));
            for (auto gpuIndex = 0; gpuIndex < instance->Gpu.Num(); ++gpuIndex)
            {
                auto& gpu = instance->Gpu[gpuIndex];
                if (!gpu.IsValid())
                {
                    return Fail(TEXT("An owned GPU runtime was destroyed externally."));
                }
                auto drawTransform = FMatrix44f::Identity;
                auto connected = false;
                auto emitterMatrix = FMatrix44f::Identity;
                auto emitterConnected = false;
                if (!ReadGpuDrawTransform(*instance->Definition, instance->GpuRuntimeNodeIds[gpuIndex],
                        instance->Container, instance->Id, emitterMatrix, emitterConnected, true) ||
                    !gpu->SetEmitterTransform(emitterMatrix))
                    return Fail(TEXT("Emitter Matrix binding is invalid."));
                if (!ReadGpuDrawTransform(*instance->Definition, instance->GpuRuntimeNodeIds[gpuIndex],
                        instance->Container, instance->Id, drawTransform, connected))
                    return Fail(TEXT("GPU draw Matrix binding is invalid."));
                if (instance->GpuRuntimeIsFluid[gpuIndex])
                {
                    const auto localToWorld = connected ? drawTransform : ownerMatrix;
                    if (!gpu->SetFluidPresentation(localToWorld, visible))
                        return Fail(gpu->LastError.IsEmpty()
                            ? TEXT("GPU fluid presentation transform is invalid.") : gpu->LastError);
                }
                else if (!gpu->SetDrawTransform(connected ? drawTransform : ownerMatrix, true))
                {
                    return Fail(gpu->LastError.IsEmpty()
                        ? TEXT("GPU draw Matrix binding is invalid.") : gpu->LastError);
                }
                if (delta > 0) gpu->Tick(static_cast<float>(delta));
            }
            instance->PresentationRootTransform = ownerTransform;
            instance->bHasPresentationRootTransform = true;
            instance->Previous = time;
        }
        return true;
    }
};

FLibraSpawnableRenderState::FLibraSpawnableRenderState(ULibraSpawnableComponent& owner) : Impl(MakeUnique<FImpl>(owner)) {}
FLibraSpawnableRenderState::~FLibraSpawnableRenderState() = default;

bool FLibraSpawnableRenderState::Prepare(const liberafx::SpawnablePackage& package)
{
    if (!FMath::IsFinite(Impl->Owner.GpuSpawnPerSecond) || Impl->Owner.GpuSpawnPerSecond < 0)
        return Impl->Fail(TEXT("GPU spawn rate must be finite and nonnegative."));
    auto identities = TSet<FString>{};
    for (const auto& binding : Impl->Owner.AssetBindings)
    {
        if (binding.Identity.IsEmpty() || !binding.Asset || identities.Contains(binding.Identity))
            return Impl->Fail(TEXT("Invalid or duplicate engine asset binding."));
        identities.Add(binding.Identity);
    }
    if (package.templates.size() > 128) return Impl->Fail(TEXT("The engine adapter supports at most 128 templates."));
    auto root = MakeUnique<FDefinition>();
    if (!Impl->ReadDraws(package.rootRenderConfiguration, *root) ||
        !Impl->ReadGpu(package.rootGpuContainersJson, package.rootGpuProgramsJson, *root)) return false;
    if ((!root->Gpu.IsEmpty() || !root->FluidPrograms.IsEmpty()) && package.autoDestroyWhenIdle)
        return Impl->Fail(TEXT("Root GPU auto-idle draining is unsupported. Disable AutoDestroyWhenIdle and use RequestNaturalEnd or Stop."));
    Impl->Definitions.Add(FGuid{}, MoveTemp(root));
    for (const auto& entry : package.templates)
    {
        auto definition = MakeUnique<FDefinition>();
        if (!Impl->ReadDraws(entry.renderConfiguration, *definition) ||
            !Impl->ReadGpu(entry.gpuContainersJson, entry.gpuProgramsJson, *definition)) return false;
        auto id = Guid(entry.containerId);
        if (!id.IsValid() || Impl->Definitions.Contains(id)) return Impl->Fail(TEXT("Invalid rendering template identity."));
        Impl->Definitions.Add(id, MoveTemp(definition));
    }
    return true;
}

bool FLibraSpawnableRenderState::Spawn(FGuid container, int64 instance, double time, uint64 seed)
{
    return Impl->Spawn(container, instance, time, seed);
}

void FLibraSpawnableRenderState::Stop(FGuid container, int64 instance)
{
    for (auto index = Impl->Instances.Num() - 1; index >= 0; --index)
        if (Impl->Instances[index]->Container == container && Impl->Instances[index]->Id == instance)
        {
            Impl->GpuBytes -= Impl->Instances[index]->Definition->GpuBytes;
            Impl->Instances.RemoveAt(index);
        }
}

bool FLibraSpawnableRenderState::Update(double time, bool rootFinished)
{
    if (rootFinished) Stop(FGuid{}, 0);
    return Impl->Update(time);
}

void FLibraSpawnableRenderState::UpdateOwnerPresentation()
{
    check(IsInGameThread());
    auto* actor = Impl->Owner.GetOwner();
    if (!actor)
    {
        return;
    }
    auto* root = actor->GetRootComponent();
    const auto rootTransform = root ? root->GetComponentTransform() : actor->GetActorTransform();
    const auto visible = !actor->IsHidden() && (!root || root->IsVisible());
    for (auto& instance : Impl->Instances)
    {
        const auto canonicalRoot = FTransform(Impl->Owner.EffectToHostMatrix() * rootTransform.ToMatrixWithScale());
        if (instance->Procedural) instance->Procedural->SetPresentation(canonicalRoot, visible);
        const auto transformChanged = !instance->bHasPresentationRootTransform ||
            !instance->PresentationRootTransform.Equals(rootTransform);
        const auto ownerMatrix = FMatrix44f((Impl->Owner.EffectToHostMatrix() * rootTransform.ToMatrixWithScale()).GetTransposed());
        if (instance->GpuRuntimeNodeIds.Num() != instance->Gpu.Num() ||
            instance->GpuRuntimeIsFluid.Num() != instance->Gpu.Num())
        {
            Impl->Fail(TEXT("GPU runtime identities do not match their prepared programs."));
            return;
        }
        for (auto gpuIndex = 0; gpuIndex < instance->Gpu.Num(); ++gpuIndex)
        {
            if (!instance->GpuRuntimeIsFluid.IsValidIndex(gpuIndex) || !instance->Gpu[gpuIndex].IsValid())
                continue;
            auto drawTransform = FMatrix44f::Identity;
            auto connected = false;
            if (!Impl->ReadGpuDrawTransform(*instance->Definition, instance->GpuRuntimeNodeIds[gpuIndex],
                    instance->Container, instance->Id, drawTransform, connected))
                return;
            const auto localToWorld = connected ? drawTransform : ownerMatrix;
            const auto updated = instance->GpuRuntimeIsFluid[gpuIndex]
                ? instance->Gpu[gpuIndex]->SetFluidPresentation(localToWorld, visible)
                : instance->Gpu[gpuIndex]->SetDrawTransform(localToWorld, true);
            if (!updated)
            {
                Impl->Fail(instance->Gpu[gpuIndex]->LastError);
                return;
            }
        }
        for (auto drawIndex = 0; drawIndex < instance->Draws.Num(); ++drawIndex)
        {
            auto& primitive = instance->Draws[drawIndex];
            if (primitive)
            {
                if (transformChanged)
                {
                    primitive->SetTransform(instance->DrawLocalTransforms[drawIndex] * rootTransform);
                }
                primitive->SetVisible(visible);
            }
        }
        for (auto& batch : instance->DrawBatches)
        {
            if (!batch.Primitive.IsValid())
            {
                continue;
            }
            if (transformChanged)
            {
                batch.Transforms.Reset();
                batch.CustomData.Reset();
                batch.Transforms.Reserve(batch.DrawIndices.Num());
                batch.CustomData.Reserve(batch.DrawIndices.Num());
                for (const auto drawIndex : batch.DrawIndices)
                {
                    batch.Transforms.Add(instance->DrawLocalTransforms[drawIndex] * rootTransform);
                    batch.CustomData.Add(FLinearColor::Transparent);
                }
                if (!batch.Primitive->Update(batch.Transforms, batch.CustomData, rootTransform))
                {
                    Impl->Fail(TEXT("Could not update cached opaque batch after owner transform change."));
                    return;
                }
            }
            batch.Primitive->SetVisible(visible);
        }
        if (transformChanged)
        {
            for (auto cpuIndex = 0; cpuIndex < instance->CpuDraws.Num(); ++cpuIndex)
            {
                auto& batch = instance->CpuDraws[cpuIndex];
                bool updated = true;
                LibraCpuParticleRendering::ForEach(batch, [&](auto& frameBatch) {
                    if (!frameBatch.Primitive.IsValid()) return;
                    for (auto& transform : frameBatch.Transforms)
                        if (instance->Definition->CpuParticles[cpuIndex].DrawRows.IsEmpty())
                            transform = transform.GetRelativeTransform(instance->PresentationRootTransform) * rootTransform;
                    updated = frameBatch.Primitive->Update(frameBatch.Transforms, frameBatch.Colors, rootTransform) && updated;
                    frameBatch.Primitive->SetVisible(visible);
                });
                if (!updated) {
                    Impl->Fail(TEXT("Could not update cached CPU particle presentation after owner transform change."));
                    return;
                }
            }
            for (auto& plane : instance->FlexiblePlanes)
            {
                plane->SetOwnerPresentation(canonicalRoot, visible);
            }
            instance->PresentationRootTransform = rootTransform;
            instance->bHasPresentationRootTransform = true;
        }
        else
        {
            for (auto& batch : instance->CpuDraws)
            {
                LibraCpuParticleRendering::ForEach(batch, [&](auto& frameBatch) {
                    if (frameBatch.Primitive.IsValid()) frameBatch.Primitive->SetVisible(visible);
                });
            }
            for (auto& plane : instance->FlexiblePlanes)
            {
                plane->SetOwnerPresentation(canonicalRoot, visible);
            }
        }
    }
}

int32 FLibraSpawnableRenderState::DrawCount() const
{
    auto count = 0;
    for (const auto& instance : Impl->Instances)
    {
        count += (instance->Procedural ? instance->Procedural->DrawCount() : 0) + instance->Draws.Num() + instance->CpuDraws.Num() + instance->FlexiblePlanes.Num() +
            (instance->Debug ? instance->Debug->Count() : 0);
    }
    return count;
}

void FLibraSpawnableRenderState::ClearPreviewDebugDraw()
{
    check(IsInGameThread());
    for (auto& instance : Impl->Instances) instance->Debug.Reset();
}

int32 FLibraSpawnableRenderState::NativeDrawPrimitiveCount() const
{
    auto count = 0;
    for (const auto& instance : Impl->Instances)
    {
        count += instance->DrawBatches.Num();
        for (const auto& draw : instance->Draws)
        {
            count += draw.IsValid() ? 1 : 0;
        }
    }
    return count;
}

void FLibraSpawnableRenderState::GetNativeRenderSubmissionStats(int32& logicalRenderObjects,
    int32& nativePrimitives, int32& opaqueBatches, int32& cpuParticleBatches,
    int32& liveCpuParticles, int32& allocatedInstanceCapacity, uint64& submittedBytes) const
{
    logicalRenderObjects = 0;
    nativePrimitives = 0;
    opaqueBatches = 0;
    cpuParticleBatches = 0;
    liveCpuParticles = 0;
    allocatedInstanceCapacity = 0;
    submittedBytes = 0;
    for (const auto& instance : Impl->Instances)
    {
        logicalRenderObjects += instance->Definition->Draws.Num() +
            instance->Definition->CpuParticles.Num() + instance->Definition->FlexiblePlanes.Num();
        const auto proceduralDraws = instance->Procedural ? instance->Procedural->DrawCount() : 0;
        logicalRenderObjects += proceduralDraws;
        nativePrimitives += proceduralDraws;
        opaqueBatches += instance->DrawBatches.Num();
        nativePrimitives += instance->DrawBatches.Num();
        for (const auto& draw : instance->Draws)
        {
            if (draw)
            {
                ++nativePrimitives;
                submittedBytes += draw->SubmittedBytes;
            }
        }
        for (const auto& batch : instance->DrawBatches)
        {
            if (batch.Primitive.IsValid())
            {
                allocatedInstanceCapacity += batch.Primitive->GetAllocatedInstanceCapacity();
                submittedBytes += batch.Primitive->SubmittedBytes;
            }
        }
        for (const auto& batch : instance->CpuDraws)
        {
            LibraCpuParticleRendering::ForEach(batch, [&](const auto& frameBatch) {
                if (!frameBatch.Primitive.IsValid()) return;
                ++cpuParticleBatches;
                ++nativePrimitives;
                liveCpuParticles += frameBatch.Primitive->LiveInstanceCount;
                allocatedInstanceCapacity += frameBatch.Primitive->GetAllocatedInstanceCapacity();
                submittedBytes += frameBatch.Primitive->SubmittedBytes;
            });
        }
        nativePrimitives += instance->FlexiblePlanes.Num();
        for (const auto& plane : instance->FlexiblePlanes)
        {
            submittedBytes += plane->GetSubmittedBytes();
        }
    }
}

void FLibraSpawnableRenderState::GetDrawSnapshots(TArray<FTransform>& transforms,
    TArray<FBoxSphereBounds>& bounds, TArray<FGuid>& drawIds, TArray<FGuid>& containerIds,
    TArray<int64>& instanceIds) const
{
    check(IsInGameThread());
    transforms.Reset();
    bounds.Reset();
    drawIds.Reset();
    containerIds.Reset();
    instanceIds.Reset();
    for (const auto& instance : Impl->Instances)
    {
        for (auto drawIndex = 0; drawIndex < instance->Definition->Draws.Num(); ++drawIndex)
        {
            const auto& draw = instance->Definition->Draws[drawIndex];
            const auto worldTransform = instance->DrawLocalTransforms[drawIndex] *
                instance->PresentationRootTransform;
            transforms.Add(worldTransform);
            bounds.Add(draw.Mesh->GetBounds().TransformBy(worldTransform));
            drawIds.Add(draw.Id);
            containerIds.Add(instance->Container);
            instanceIds.Add(instance->Id);
        }
    }
}

int32 FLibraSpawnableRenderState::GpuCount() const
{
    auto count = 0;
    for (const auto& instance : Impl->Instances) count += instance->Gpu.Num();
    return count;
}
