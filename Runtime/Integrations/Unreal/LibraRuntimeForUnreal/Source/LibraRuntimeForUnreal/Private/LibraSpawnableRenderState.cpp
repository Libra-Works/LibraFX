#include "LibraSpawnableRenderState.h"
#include "LibraDebugDrawScope.h"
#include "LibraFlexiblePlaneRenderer.h"

#include "LibraSpawnableComponent.h"
#include "LibraSpawnableAsset.h"
#include "LibraGpuEffectAsset.h"
#include "LibraGpuEffectRuntime.h"
#include "Camera/CameraComponent.h"
#include "Camera/PlayerCameraManager.h"
#include "Components/InstancedStaticMeshComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Dom/JsonObject.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture.h"
#include "Engine/Texture2D.h"
#include "GameFramework/Actor.h"
#include "GameFramework/PlayerController.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Materials/MaterialInterface.h"
#include "Math/RotationMatrix.h"
#include "Misc/PackageName.h"
#include "RHI.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UObject/StrongObjectPtr.h"
#include "liberafx/LbSceneBindings.h"
#include "LibraSpriteCompositionRender.h"
#include "liberafx/LbSpawnablePackage.h"
#include <algorithm>

namespace LibraCpuParticleRendering
{
struct FBatch
{
    TStrongObjectPtr<UInstancedStaticMeshComponent> Component;
    TArray<FTransform> Transforms;
    TArray<FTransform> Growth;
    TArray<int32> Removed;
    TArray<FLinearColor> Colors;
};
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
    bool bSprite = false, bBillboard = false;
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
    FGuid Id;
    float DrawScale = 1;
    bool bAdditive = false;
    TStrongObjectPtr<UStaticMesh> Mesh;
    TStrongObjectPtr<UTexture> Texture;
    TStrongObjectPtr<UMaterialInterface> Material;
};

struct FFlexiblePlaneDraw
{
    FLibraFlexiblePlaneDefinition Definition;
    TStrongObjectPtr<UTexture> Texture;
    TStrongObjectPtr<UMaterialInterface> Material;
};

struct FDefinition
{
    TSharedPtr<FJsonObject> DebugConfiguration;
    int32 DebugCount = 0;
    TArray<FDraw> Draws;
    TArray<FCpuParticleDraw> CpuParticles;
    TArray<FFlexiblePlaneDraw> FlexiblePlanes;
    TArray<TStrongObjectPtr<ULibraGpuEffectAsset>> Gpu;
    TArray<FGuid> GpuNodeIds;
    TMap<FGuid, TArray<FGuid>> GpuDrawTransforms;
    uint64 GpuBytes = 0;
};

struct FInstance
{
    TUniquePtr<FLibraDebugDrawScope> Debug;
    FGuid Container;
    int64 Id = 0;
    double Started = 0, Previous = 0;
    FDefinition* Definition = nullptr;
    TArray<TStrongObjectPtr<UStaticMeshComponent>> Draws;
    TArray<LibraCpuParticleRendering::FBatch> CpuDraws;
    TArray<TUniquePtr<FLibraFlexiblePlaneRenderer>> FlexiblePlanes;
    FLibraFlexiblePlaneHistoryScope FlexibleHistory;
    TArray<TStrongObjectPtr<ULibraGpuEffectRuntime>> Gpu;
    ~FInstance()
    {
        for (auto& runtime : Gpu)
        {
            if (runtime.IsValid())
            {
                runtime->Stop();
            }
        }
        for (auto& component : Draws)
        {
            if (component.IsValid())
            {
                component->DestroyComponent();
            }
        }
        for (auto& system : CpuDraws)
        {
            if (system.Component.IsValid())
            {
                system.Component->DestroyComponent();
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

    UTexture2D* MakeWhiteTexture()
    {
        auto* texture = UTexture2D::CreateTransient(1, 1, PF_B8G8R8A8);
        if (!texture || !texture->GetPlatformData() || texture->GetPlatformData()->Mips.IsEmpty())
            return nullptr;
        auto& mip = texture->GetPlatformData()->Mips[0];
        auto* pixel = static_cast<FColor*>(mip.BulkData.Lock(LOCK_READ_WRITE));
        if (!pixel)
        {
            return nullptr;
        }
        *pixel = FColor::White;
        mip.BulkData.Unlock();
        texture->UpdateResource();
        return texture;
    }

    bool ReadDraws(const std::vector<std::uint8_t>& bytes, FDefinition& definition,
        bool bAllowCpuParticles = false)
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
            (!root->TryGetNumberField(TEXT("cpuParticleVersion"), cpuVersion) || cpuVersion != 1))
        {
            return Fail(TEXT("Unsupported CPU particle scene metadata version."));
        }
        if (!bAllowCpuParticles && cpuParticles && cpuParticles->Num() != 0)
        {
            return Fail(TEXT("CPU particle Sprite rendering inside SpawnableGroup templates is not supported."));
        }
        const TArray<TSharedPtr<FJsonValue>>* gpuDrawTransforms = nullptr;
        if (root->TryGetArrayField(TEXT("gpuDrawTransforms"), gpuDrawTransforms))
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
                    definition.GpuDrawTransforms.Contains(nodeId) ||
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
                definition.GpuDrawTransforms.Add(nodeId, MoveTemp(rows));
            }
        }
        auto ids = TSet<FGuid>{};
        using EKind = ELibraPackageResourceKind;
        for (const auto& item : *nodes)
        {
            auto object = item.IsValid() && item->Type == EJson::Object ? item->AsObject() : nullptr;
            auto draw = FDraw{};
            auto id = FString{}, kind = FString{}, asset = FString{};
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
                plane.Texture.Reset(Resolve<UTexture>(plane.Definition.assetPath, EKind::Texture));
                plane.Material.Reset(Resolve<UMaterialInterface>(TEXT("builtin:flexible-plane"), EKind::Material));
                if (!plane.Texture.IsValid() || !plane.Material.IsValid() ||
                    !HasParameter(plane.Material.Get(), FMaterialParameterInfo(TEXT("Texture")), TEXT("texture")))
                {
                    return Fail(TEXT("Flexible Plane requires its Texture binding and a builtin:flexible-plane Material with a Texture parameter and vertex colors."));
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
                auto texture = Resolve<UTexture>(asset, EKind::Texture);
                if (!texture) return Fail(TEXT("Missing sprite Texture binding: ") + asset);
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
                texture.Texture.Reset(Resolve<UTexture>(path, EKind::Texture));
                if (!texture.Texture.IsValid()) return Fail(TEXT("Missing Texture binding: ") + path);
                draw.Textures.Add(MoveTemp(texture));
            }
            definition.Draws.Add(MoveTemp(draw));
        }
        if (bAllowCpuParticles && cpuParticles)
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
                auto texture = asset == TEXT("builtin:white")
                    ? MakeWhiteTexture() : Resolve<UTexture>(asset, EKind::Texture);
                if (!texture)
                {
                    return Fail(TEXT("Missing CPU particle Texture binding: ") + asset);
                }
                draw.Texture.Reset(texture);
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
        auto expected = TSet<FGuid>{};
        if (!snapshots.empty())
        {
            auto nodes = TArray<TSharedPtr<FJsonValue>>{};
            auto reader = TJsonReaderFactory<>::Create(Text(snapshots));
            if (!FJsonSerializer::Deserialize(reader, nodes) || nodes.Num() > 16)
                return Fail(TEXT("Invalid GPU snapshots or more than 16 emitters per scope."));
            for (const auto& node : nodes)
            {
                auto id = FString{};
                auto guid = FGuid{};
                if (!node || node->Type != EJson::Object || !node->AsObject()->TryGetStringField(TEXT("id"), id) ||
                    !FGuid::Parse(id, guid) || !guid.IsValid() || expected.Contains(guid))
                    return Fail(TEXT("Invalid GPU container identity."));
                expected.Add(guid);
            }
        }
        if (bytes.empty())
            return (expected.IsEmpty() && definition.GpuDrawTransforms.IsEmpty()) ||
                Fail(TEXT("GPU containers or draw Matrix bindings have no executable program manifest; re-export the source graph."));
        auto root = TSharedPtr<FJsonObject>{};
        auto reader = TJsonReaderFactory<>::Create(Text(bytes));
        auto version = int32{};
        const TArray<TSharedPtr<FJsonValue>>* programs = nullptr;
        if (!FJsonSerializer::Deserialize(reader, root) || !root || !Integer(root, TEXT("version"), version) ||
            version != 1 || !root->TryGetArrayField(TEXT("programs"), programs) || programs->Num() != expected.Num())
            return Fail(TEXT("GPU program manifest does not match its containers."));
        for (const auto& item : *programs)
        {
            auto object = item.IsValid() && item->Type == EJson::Object ? item->AsObject() : nullptr;
            auto id = FString{};
            auto guid = FGuid{};
            const TSharedPtr<FJsonObject>* delivery = nullptr;
            if (!object || !object->TryGetStringField(TEXT("nodeId"), id) || !FGuid::Parse(id, guid) ||
                !expected.Remove(guid) || !object->TryGetObjectField(TEXT("unreal"), delivery))
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
            definition.GpuBytes += uint64(asset->GetCapacity()) * (80 + registers * 16) + packetBytes;
            if (definition.GpuBytes > ULibraGpuEffectAsset::MaximumGpuBytes)
                return Fail(TEXT("GPU scope exceeds the 256 MiB budget."));
            definition.GpuNodeIds.Add(guid);
            definition.Gpu.Add(MoveTemp(asset));
        }
        for (const auto& transform : definition.GpuDrawTransforms)
            if (!definition.GpuNodeIds.Contains(transform.Key))
                return Fail(TEXT("GPU draw Matrix binding does not match an executable GPU container."));
        return true;
    }

    bool ReadGpuDrawTransform(const FDefinition& definition, FGuid nodeId,
        FGuid container, int64 instance,
        FMatrix44f& matrix, bool& connected)
    {
        const auto* rows = definition.GpuDrawTransforms.Find(nodeId);
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
        for (const auto& plane : instance->Definition->FlexiblePlanes)
        {
            auto* root = actor->GetRootComponent();
            if (!root)
            {
                return Fail(TEXT("Flexible Plane rendering requires an actor root component."));
            }
            auto renderer = TUniquePtr<FLibraFlexiblePlaneRenderer>{};
            auto error = FString{};
            if (!FLibraFlexiblePlaneRenderer::Create(*actor, *root, plane.Definition,
                plane.Texture.Get(), plane.Material.Get(), FName(TEXT("Texture")), renderer, error))
            {
                return Fail(error);
            }
            instance->FlexiblePlanes.Add(MoveTemp(renderer));
        }
        for (const auto& system : instance->Definition->CpuParticles)
        {
            auto* root = actor->GetRootComponent();
            if (!root)
            {
                return Fail(TEXT("CPU particle rendering requires an actor root component."));
            }
            auto& batch = instance->CpuDraws.AddDefaulted_GetRef();
            batch.Component.Reset(NewObject<UInstancedStaticMeshComponent>(actor, NAME_None, RF_Transient));
            auto* component = batch.Component.Get();
            component->SetMobility(EComponentMobility::Movable);
            component->SetCollisionEnabled(ECollisionEnabled::NoCollision);
            component->SetCanEverAffectNavigation(false);
            component->SetCastShadow(false);
            component->SetupAttachment(root);
            component->SetAbsolute(false, true, true);
            component->SetStaticMesh(system.Mesh.Get());
            component->NumCustomDataFloats = 4;
            auto* material = UMaterialInstanceDynamic::Create(system.Material.Get(), component);
            if (!material)
            {
                return Fail(TEXT("Could not create a CPU particle batch material."));
            }
            material->SetTextureParameterValue(TEXT("Texture"), system.Texture.Get());
            component->SetMaterial(0, material);
            component->ComponentTags.Add(TEXT("LibraCpuParticleRenderer"));
            component->ComponentTags.Add(FName(*system.Id.ToString()));
            actor->AddInstanceComponent(component);
            component->RegisterComponent();
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
            auto component = TStrongObjectPtr<UStaticMeshComponent>(
                NewObject<UStaticMeshComponent>(actor, NAME_None, RF_Transient));
            component->SetMobility(EComponentMobility::Movable);
            component->SetCollisionEnabled(ECollisionEnabled::NoCollision);
            component->SetupAttachment(actor->GetRootComponent());
            component->SetStaticMesh(draw.Mesh.Get());
            if (draw.SpriteAnimation.Version == 2)
                component->SetTranslucentSortPriority(draw.SpriteAnimation.Composition.Order);
            component->SetVisibility(false);
            component->ComponentTags.Add(TEXT("LibraSpawnableRenderer"));
            component->ComponentTags.Add(FName(*draw.Id.ToString()));
            for (auto slot = 0; slot < draw.Materials.Num(); ++slot)
                component->SetMaterial(slot, UMaterialInstanceDynamic::Create(draw.Materials[slot].Get(), component.Get()));
            for (const auto& texture : draw.Textures)
                CastChecked<UMaterialInstanceDynamic>(component->GetMaterial(texture.Slot))
                    ->SetTextureParameterValueByInfo(texture.Parameter, texture.Texture.Get());
            actor->AddInstanceComponent(component.Get());
            component->RegisterComponent();
            instance->Draws.Add(MoveTemp(component));
        }
        for (auto gpuIndex = 0; gpuIndex < instance->Definition->Gpu.Num(); ++gpuIndex)
        {
            const auto& asset = instance->Definition->Gpu[gpuIndex];
            auto runtime = TStrongObjectPtr<ULibraGpuEffectRuntime>(
                NewObject<ULibraGpuEffectRuntime>(actor, NAME_None, RF_Transient));
            runtime->Initialize(actor);
            instance->Gpu.Add(MoveTemp(runtime));
            auto gpu = instance->Gpu.Last().Get();
            if (!gpu->SetEffect(asset.Get()) ||
                !gpu->ConfigureEmitter(Owner.GpuSpawnPerSecond, static_cast<int32>(seed)))
                return Fail(TEXT("GPU instance creation failed: ") + gpu->LastError);
            auto drawTransform = FMatrix44f::Identity;
            auto connected = false;
            if (!ReadGpuDrawTransform(*instance->Definition, instance->Definition->GpuNodeIds[gpuIndex],
                    container, id, drawTransform, connected) ||
                !gpu->SetDrawTransform(drawTransform, connected))
                return Fail(gpu->LastError.IsEmpty()
                    ? TEXT("GPU draw Matrix binding is invalid.") : gpu->LastError);
            if (!gpu->Play())
                return Fail(TEXT("GPU instance creation failed: ") + gpu->LastError);
        }
        GpuBytes += instance->Definition->GpuBytes;
        Instances.Add(MoveTemp(instance));
        return true;
    }

    bool Update(double time)
    {
        for (auto& instance : Instances)
        {
            instance->FlexibleHistory.beginUpdate();
            for (auto planeIndex = 0; planeIndex < instance->FlexiblePlanes.Num(); ++planeIndex)
            {
                auto& plane = instance->FlexiblePlanes[planeIndex];
                auto cameraPosition = FVector::ZeroVector;
                auto* overrideCamera = Owner.EffectCameraOverride.Get();
                auto* controller = Owner.GetWorld() ? Owner.GetWorld()->GetFirstPlayerController() : nullptr;
                auto* camera = controller ? controller->PlayerCameraManager : nullptr;
                if (IsValid(overrideCamera))
                {
                    cameraPosition = overrideCamera->GetComponentLocation();
                }
                else if (IsValid(camera))
                {
                    cameraPosition = camera->GetCameraLocation();
                }
                else if (instance->Definition->FlexiblePlanes[planeIndex].Definition.native.options.facing == 1)
                {
                    return Fail(TEXT("Flexible Plane rendering requires a current effect or player camera."));
                }
                auto error = FString{};
                if (!plane->Update(time - instance->Started, cameraPosition,
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
                auto component = instance->Draws[index].Get();
                if (!IsValid(component) || !component->IsRegistered())
                    return Fail(TEXT("An owned renderer was destroyed externally."));
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
                        auto material = CastChecked<UMaterialInstanceDynamic>(component->GetMaterial(input.Slot));
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
                    CastChecked<UMaterialInstanceDynamic>(component->GetMaterial(0))->SetVectorParameterValue(
                        TEXT("AtlasRect"), FLinearColor(atlas.uvRect[0], atlas.uvRect[1], atlas.uvRect[2], atlas.uvRect[3]));
                }
                component->SetRelativeTransform(pose);
                if (auto tint = values.Find(tintId))
                    for (const auto& target : draw.TintTargets)
                    {
                        auto material = CastChecked<UMaterialInstanceDynamic>(component->GetMaterial(target.Key));
                        material->SetVectorParameterValue(target.Value, FLinearColor(tint->X, tint->Y, tint->Z, tint->W));
                    }
                component->SetVisibility(true);
            }
            if (instance->CpuDraws.Num() != instance->Definition->CpuParticles.Num())
            {
                return Fail(TEXT("CPU particle batches do not match their definitions."));
            }
            for (auto systemIndex = 0; systemIndex < instance->Definition->CpuParticles.Num(); ++systemIndex)
            {
                if (instance->Container.IsValid())
                {
                    return Fail(TEXT("CPU particle systems are supported only in the root render scope."));
                }
                const auto& system = instance->Definition->CpuParticles[systemIndex];
                auto particleCount = uint32{};
                auto result = Owner.ReadCpuParticleCount(system.Id, particleCount);
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
                auto* component = batch.Component.Get();
                auto* actor = Owner.GetOwner();
                auto* root = actor ? actor->GetRootComponent() : nullptr;
                if (!IsValid(component) || !component->IsRegistered() || !root)
                {
                    return Fail(TEXT("An owned CPU particle batch or its actor root was destroyed externally."));
                }
                auto* world = Owner.GetWorld();
                auto* controller = world ? world->GetFirstPlayerController() : nullptr;
                auto* camera = controller ? controller->PlayerCameraManager : nullptr;
                auto* overrideCamera = Owner.EffectCameraOverride.Get();
                if (particleCount != 0 && !IsValid(overrideCamera) && !IsValid(camera))
                {
                    return Fail(TEXT("CPU particle billboard rendering requires a player camera."));
                }
                auto rotation = FQuat::Identity;
                if (IsValid(overrideCamera) || IsValid(camera))
                {
                    const auto view = IsValid(overrideCamera) ? overrideCamera->GetComponentQuat()
                        : camera->GetCameraRotation().Quaternion();
                    rotation = FRotationMatrix::MakeFromXY(view.GetRightVector(), view.GetUpVector()).ToQuat();
                }
                const auto ownerTransform = root->GetComponentTransform();
                const auto ownerScale = ownerTransform.GetScale3D().GetAbs();
                const auto count = static_cast<int32>(particleCount);
                batch.Transforms.Reset();
                batch.Colors.Reset();
                for (auto particleIndex = 0; particleIndex < count; ++particleIndex)
                {
                    auto particleId = uint64{};
                    auto positionX = double{}, positionY = double{}, positionZ = double{};
                    auto colorR = double{}, colorG = double{}, colorB = double{}, colorA = double{};
                    auto sizeX = double{}, sizeY = double{};
                    result = Owner.ReadCpuParticleAt(system.Id, static_cast<SIZE_T>(particleIndex),
                        particleId, positionX, positionY, positionZ, colorR, colorG,
                        colorB, colorA, sizeX, sizeY);
                    if (result != ELibraRuntimeResult::Ok || particleId == 0)
                    {
                        return Fail(TEXT("CPU particle snapshot read failed."));
                    }
                    const auto worldPosition = ownerTransform.TransformPosition(FVector(
                        positionX * system.DrawScale,
                        positionY * system.DrawScale,
                        positionZ * system.DrawScale));
                    const auto scaledX = sizeX * system.DrawScale * ownerScale.X;
                    const auto scaledY = sizeY * system.DrawScale * ownerScale.Y;
                    if (!Finite(FVector4(worldPosition.X, worldPosition.Y, worldPosition.Z, scaledX)) ||
                        !Finite(FVector4(scaledY, 0, 0, 0)) ||
                        !Finite(FVector4(colorR, colorG, colorB, colorA)))
                    {
                        return Fail(TEXT("CPU particle snapshot exceeds Unreal's finite float range."));
                    }
                    batch.Transforms.Emplace(rotation, worldPosition, FVector(scaledX / 100, scaledY / 100, 1));
                    batch.Colors.Emplace(static_cast<float>(colorR), static_cast<float>(colorG),
                        static_cast<float>(colorB), static_cast<float>(colorA));
                }
                const auto previousCount = component->GetInstanceCount();
                if (count < previousCount)
                {
                    batch.Removed.Reset(previousCount - count);
                    for (auto index = previousCount - 1; index >= count; --index)
                    {
                        batch.Removed.Add(index);
                    }
                    if (!component->RemoveInstances(batch.Removed, true))
                    {
                        return Fail(TEXT("Could not shrink a CPU particle batch."));
                    }
                }
                else if (count > previousCount)
                {
                    batch.Growth.Reset(count - previousCount);
                    batch.Growth.Append(batch.Transforms.GetData() + previousCount, count - previousCount);
                    component->AddInstances(batch.Growth, false, true, false);
                    if (component->GetInstanceCount() != count)
                    {
                        return Fail(TEXT("Could not grow a CPU particle batch."));
                    }
                }
                if (count != 0)
                {
                    if (!component->BatchUpdateInstancesTransforms(0, batch.Transforms, true, false, true))
                    {
                        return Fail(TEXT("Could not update CPU particle batch transforms."));
                    }
                    for (auto index = 0; index < count; ++index)
                    {
                        const auto& color = batch.Colors[index];
                        const float channels[]{color.R, color.G, color.B, color.A};
                        if (!component->SetCustomData(index, MakeArrayView(channels), false))
                        {
                            return Fail(TEXT("Could not update CPU particle batch RGBA."));
                        }
                    }
                    component->MarkRenderInstancesDirty();
                }
            }
            auto delta = time - instance->Previous;
            if (!FMath::IsFinite(delta) || delta < 0 || delta > MAX_flt) return Fail(TEXT("Invalid GPU local time."));
            for (auto gpuIndex = 0; gpuIndex < instance->Gpu.Num(); ++gpuIndex)
            {
                auto& gpu = instance->Gpu[gpuIndex];
                if (!gpu.IsValid())
                {
                    return Fail(TEXT("An owned GPU runtime was destroyed externally."));
                }
                auto drawTransform = FMatrix44f::Identity;
                auto connected = false;
                if (!ReadGpuDrawTransform(*instance->Definition, instance->Definition->GpuNodeIds[gpuIndex],
                        instance->Container, instance->Id, drawTransform, connected) ||
                    !gpu->SetDrawTransform(drawTransform, connected))
                    return Fail(gpu->LastError.IsEmpty()
                        ? TEXT("GPU draw Matrix binding is invalid.") : gpu->LastError);
                if (delta > 0) gpu->Tick(static_cast<float>(delta));
            }
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
    if (!Impl->ReadDraws(package.rootRenderConfiguration, *root, true) ||
        !Impl->ReadGpu(package.rootGpuContainersJson, package.rootGpuProgramsJson, *root)) return false;
    if (!root->Gpu.IsEmpty() && package.autoDestroyWhenIdle)
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

int32 FLibraSpawnableRenderState::DrawCount() const
{
    auto count = 0;
    for (const auto& instance : Impl->Instances)
    {
        count += instance->Draws.Num() + instance->CpuDraws.Num() + instance->FlexiblePlanes.Num() +
            (instance->Debug ? instance->Debug->Count() : 0);
    }
    return count;
}

void FLibraSpawnableRenderState::ClearPreviewDebugDraw()
{
    check(IsInGameThread());
    for (auto& instance : Impl->Instances) instance->Debug.Reset();
}

int32 FLibraSpawnableRenderState::GpuCount() const
{
    auto count = 0;
    for (const auto& instance : Impl->Instances) count += instance->Gpu.Num();
    return count;
}
