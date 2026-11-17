#include "LibraSpawnableRenderState.h"

#include "LibraSpawnableComponent.h"
#include "LibraGpuEffectAsset.h"
#include "LibraGpuEffectComponent.h"
#include "Camera/PlayerCameraManager.h"
#include "Components/StaticMeshComponent.h"
#include "Dom/JsonObject.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture.h"
#include "Engine/Texture2D.h"
#include "GameFramework/Actor.h"
#include "GameFramework/PlayerController.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Materials/MaterialInterface.h"
#include "Misc/PackageName.h"
#include "RHI.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UObject/StrongObjectPtr.h"
#include "liberafx/SceneBindings.h"
#include "liberafx/SpawnablePackage.h"

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

struct FDraw
{
    FGuid Id;
    bool bSprite = false, bBillboard = false;
    TStrongObjectPtr<UStaticMesh> Mesh;
    TArray<TStrongObjectPtr<UMaterialInterface>> Materials;
    struct FTexture { int32 Slot; FMaterialParameterInfo Parameter; TStrongObjectPtr<UTexture> Texture; };
    TArray<FTexture> Textures;
    TArray<FInput> Inputs;
};

struct FDefinition
{
    TArray<FDraw> Draws;
    TArray<TStrongObjectPtr<ULibraGpuEffectAsset>> Gpu;
    uint64 GpuBytes = 0;
};

struct FInstance
{
    FGuid Container;
    int64 Id = 0;
    double Started = 0, Previous = 0;
    FDefinition* Definition = nullptr;
    TArray<TStrongObjectPtr<UStaticMeshComponent>> Draws;
    TArray<TStrongObjectPtr<ULibraGpuEffectComponent>> Gpu;
    ~FInstance()
    {
        for (auto& component : Gpu)
            if (component.IsValid()) { component->Stop(); component->DestroyComponent(); }
        for (auto& component : Draws)
            if (component.IsValid()) component->DestroyComponent();
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

    template<class T> T* Resolve(const FString& identity)
    {
        for (const auto& binding : Owner.AssetBindings)
            if (binding.Identity == identity) return Cast<T>(binding.Asset);
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
        auto ids = TSet<FGuid>{};
        for (const auto& item : *nodes)
        {
            auto object = item.IsValid() && item->Type == EJson::Object ? item->AsObject() : nullptr;
            auto draw = FDraw{};
            auto id = FString{}, kind = FString{}, asset = FString{};
            if (!object || !object->TryGetStringField(TEXT("nodeId"), id) || !FGuid::Parse(id, draw.Id) ||
                !draw.Id.IsValid() || ids.Contains(draw.Id) ||
                !object->TryGetStringField(TEXT("kind"), kind) || (kind != TEXT("model") && kind != TEXT("sprite")) ||
                !object->TryGetStringField(TEXT("assetPath"), asset))
                return Fail(TEXT("Invalid or duplicate render node."));
            ids.Add(draw.Id);
            draw.bSprite = kind == TEXT("sprite");
            object->TryGetBoolField(TEXT("billboard"), draw.bBillboard);
            draw.Mesh.Reset(Resolve<UStaticMesh>(draw.bSprite ? TEXT("/Engine/BasicShapes/Plane.Plane") : asset));
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
                draw.Materials[slot].Reset(Resolve<UMaterialInterface>(identity));
                if (!draw.Materials[slot].IsValid()) return Fail(TEXT("Missing Material binding: ") + identity);
            }
            if (draw.bSprite)
            {
                if (!slots.Contains(0)) draw.Materials[0].Reset(Resolve<UMaterialInterface>(TEXT("builtin:sprite")));
                auto texture = Resolve<UTexture>(asset);
                if (!texture || !draw.Materials[0].IsValid() ||
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
                draw.Inputs.Add(MoveTemp(input));
            }
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
                texture.Texture.Reset(Resolve<UTexture>(path));
                if (!texture.Texture.IsValid()) return Fail(TEXT("Missing Texture binding: ") + path);
                draw.Textures.Add(MoveTemp(texture));
            }
            definition.Draws.Add(MoveTemp(draw));
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
            return expected.IsEmpty() || Fail(TEXT("Legacy GPU snapshots are not executable; re-export the source graph."));
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
                auto texture = Resolve<UTexture2D>(identity);
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
            definition.Gpu.Add(MoveTemp(asset));
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
            count += instance->Draws.Num();
        }
        if (count + (*definition)->Draws.Num() > 4096 ||
            (*definition)->GpuBytes > ULibraGpuEffectAsset::MaximumGpuBytes - GpuBytes)
            return Fail(TEXT("Active renderer or aggregate GPU memory budget exceeded."));
        auto actor = Owner.GetOwner();
        if (!actor || !actor->GetWorld()) return Fail(TEXT("Rendering requires a registered world owner."));
        auto instance = MakeUnique<FInstance>();
        instance->Container = container; instance->Id = id;
        instance->Started = instance->Previous = time;
        instance->Definition = definition->Get();
        for (const auto& draw : instance->Definition->Draws)
        {
            auto component = TStrongObjectPtr<UStaticMeshComponent>(
                NewObject<UStaticMeshComponent>(actor, NAME_None, RF_Transient));
            component->SetMobility(EComponentMobility::Movable);
            component->SetCollisionEnabled(ECollisionEnabled::NoCollision);
            component->SetupAttachment(actor->GetRootComponent());
            component->SetStaticMesh(draw.Mesh.Get());
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
        for (const auto& asset : instance->Definition->Gpu)
        {
            auto component = TStrongObjectPtr<ULibraGpuEffectComponent>(
                NewObject<ULibraGpuEffectComponent>(actor, NAME_None, RF_Transient));
            component->bAutoPlay = false;
            component->SetupAttachment(actor->GetRootComponent());
            actor->AddInstanceComponent(component.Get());
            component->RegisterComponent();
            component->SetComponentTickEnabled(false);
            instance->Gpu.Add(MoveTemp(component));
            auto gpu = instance->Gpu.Last().Get();
            if (!gpu->SetEffect(asset.Get()) ||
                !gpu->ConfigureEmitter(Owner.GpuSpawnPerSecond, static_cast<int32>(seed)) || !gpu->Play())
                return Fail(TEXT("GPU instance creation failed: ") + gpu->LastError);
        }
        GpuBytes += instance->Definition->GpuBytes;
        Instances.Add(MoveTemp(instance));
        return true;
    }

    bool Update(double time)
    {
        for (const auto& instance : Instances)
        {
            for (auto index = 0; index < instance->Draws.Num(); ++index)
            {
                const auto& draw = instance->Definition->Draws[index];
                auto component = instance->Draws[index].Get();
                if (!IsValid(component) || !component->IsRegistered())
                    return Fail(TEXT("An owned renderer was destroyed externally."));
                auto values = TMap<FString, FVector4>{};
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
                    values.Add(input.Id, value);
                    if (input.bMaterial)
                    {
                        auto material = CastChecked<UMaterialInstanceDynamic>(component->GetMaterial(input.Slot));
                        if (input.Kind == TEXT("scalar")) material->SetScalarParameterValueByInfo(input.Parameter, value.X);
                        else material->SetVectorParameterValueByInfo(input.Parameter, FLinearColor(value.X, value.Y, value.Z, value.W));
                    }
                }
                auto position = values.FindRef(TEXT("position"));
                auto rotation = values.FindRef(TEXT("rotation"));
                auto scale = values.Contains(TEXT("scale")) ? values[TEXT("scale")] : FVector4(1, 1, 1, 0);
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
                    auto frame = values.FindRef(TEXT("frame")).X;
                    if (frame < 0 && values.FindRef(TEXT("playing")).X != 0)
                        frame = (time - instance->Started) * values.FindRef(TEXT("fps")).X;
                    auto columns = values.FindRef(TEXT("columns")).X, rows = values.FindRef(TEXT("rows")).X;
                    auto atlas = liberafx::AtlasFrame{};
                    if (columns < 1 || columns > MAX_uint32 || rows < 1 || rows > MAX_uint32 ||
                        FMath::FloorToDouble(columns) != columns || FMath::FloorToDouble(rows) != rows ||
                        !liberafx::sampleSpriteAtlas(frame, uint32(columns), uint32(rows), atlas))
                        return Fail(TEXT("Invalid sprite atlas settings."));
                    CastChecked<UMaterialInstanceDynamic>(component->GetMaterial(0))->SetVectorParameterValue(
                        TEXT("AtlasRect"), FLinearColor(atlas.uvRect[0], atlas.uvRect[1], atlas.uvRect[2], atlas.uvRect[3]));
                }
                component->SetRelativeTransform(pose);
                if (auto tint = values.Find(TEXT("tint")))
                    for (auto slot = 0; slot < draw.Materials.Num(); ++slot)
                    {
                        auto material = CastChecked<UMaterialInstanceDynamic>(component->GetMaterial(slot));
                        for (const auto name : {TEXT("Tint"), TEXT("Color")})
                            if (HasParameter(draw.Materials[slot].Get(), FMaterialParameterInfo(name), TEXT("vector")) &&
                                !draw.Inputs.ContainsByPredicate([&](const auto& input)
                                    { return input.bMaterial && input.Slot == slot && input.Parameter.Name == FName(name); }))
                                material->SetVectorParameterValue(name, FLinearColor(tint->X, tint->Y, tint->Z, tint->W));
                    }
                component->SetVisibility(true);
            }
            auto delta = time - instance->Previous;
            if (!FMath::IsFinite(delta) || delta < 0 || delta > MAX_flt) return Fail(TEXT("Invalid GPU local time."));
            if (delta > 0)
                for (auto& gpu : instance->Gpu)
                {
                    if (!gpu.IsValid() || !gpu->IsRegistered()) return Fail(TEXT("An owned GPU emitter was destroyed externally."));
                    gpu->TickComponent(static_cast<float>(delta), LEVELTICK_All, nullptr);
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
    if (!Impl->ReadDraws(package.rootRenderConfiguration, *root) ||
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
    for (const auto& instance : Impl->Instances) count += instance->Draws.Num();
    return count;
}

int32 FLibraSpawnableRenderState::GpuCount() const
{
    auto count = 0;
    for (const auto& instance : Impl->Instances) count += instance->Gpu.Num();
    return count;
}
