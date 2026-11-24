#include "LibraSpawnableAsset.h"

#include "LibraNativeState.h"
#include "LibraFluid2DJson.h"
#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "liberafx/gpu/LbFluid2D.h"
#if WITH_EDITORONLY_DATA
#include "EditorFramework/AssetImportData.h"
#endif
#if WITH_EDITOR
#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "Materials/MaterialInterface.h"
#include "Misc/PackageName.h"
#include "UObject/AssetRegistryTagsContext.h"
#include "UObject/Package.h"
#endif
#include "liberafx/LbSpawnablePackage.h"

namespace
{
FString LibraPackageJsonText(const std::vector<std::uint8_t>& bytes)
{
    if (bytes.empty()) return {};
    auto converted = FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(bytes.data()), bytes.size());
    return FString(converted.Length(), converted.Get());
}

bool LibraReadFluidNumber(const TSharedPtr<FJsonObject>& object, const TCHAR* name, float& value)
{
    auto number = double{};
    if (!object->TryGetNumberField(name, number) || !FMath::IsFinite(number) ||
        number < -double(MAX_flt) || number > double(MAX_flt)) return false;
    value = static_cast<float>(number);
    return FMath::IsFinite(value);
}

bool LibraReadFluidInteger(const TSharedPtr<FJsonObject>& object, const TCHAR* name,
    uint32 minimum, uint32 maximum, uint32& value)
{
    auto number = double{};
    if (!object->TryGetNumberField(name, number) || !FMath::IsFinite(number) ||
        number < minimum || number > maximum || FMath::FloorToDouble(number) != number) return false;
    value = static_cast<uint32>(number);
    return true;
}

bool LibraValidateFluidSettings(const TSharedPtr<FJsonObject>& object)
{
    using namespace liberafx::gpu;
    auto settings = Fluid2DSettings{};
    if (!LibraReadFluidModelProperties(object, settings))
    {
        return false;
    }
    return LibraReadFluidInteger(object, TEXT("version"), 1, 1, settings.version) &&
        LibraReadFluidInteger(object, TEXT("width"), 8, 1024, settings.width) &&
        LibraReadFluidInteger(object, TEXT("height"), 8, 1024, settings.height) &&
        LibraReadFluidNumber(object, TEXT("domainWidth"), settings.domainWidth) &&
        LibraReadFluidNumber(object, TEXT("domainHeight"), settings.domainHeight) &&
        LibraReadFluidInteger(object, TEXT("pressureIterations"), 1, 128, settings.pressureIterations) &&
        LibraReadFluidNumber(object, TEXT("maximumStep"), settings.maximumStep) &&
        LibraReadFluidInteger(object, TEXT("maximumSubsteps"), 1, 64, settings.maximumSubsteps) &&
        LibraReadFluidNumber(object, TEXT("velocityDissipation"), settings.velocityDissipation) &&
        LibraReadFluidNumber(object, TEXT("densityDissipation"), settings.densityDissipation) &&
        object->TryGetBoolField(TEXT("sourceEnabled"), settings.sourceEnabled) &&
        LibraReadFluidNumber(object, TEXT("sourceX"), settings.sourceX) &&
        LibraReadFluidNumber(object, TEXT("sourceY"), settings.sourceY) &&
        LibraReadFluidNumber(object, TEXT("sourceRadius"), settings.sourceRadius) &&
        LibraReadFluidNumber(object, TEXT("sourceDensity"), settings.sourceDensity) &&
        LibraReadFluidNumber(object, TEXT("sourceVelocityX"), settings.sourceVelocityX) &&
        LibraReadFluidNumber(object, TEXT("sourceVelocityY"), settings.sourceVelocityY) &&
        LibraReadFluidNumber(object, TEXT("buoyancy"), settings.buoyancy) &&
        LibraReadFluidNumber(object, TEXT("colorR"), settings.colorR) &&
        LibraReadFluidNumber(object, TEXT("colorG"), settings.colorG) &&
        LibraReadFluidNumber(object, TEXT("colorB"), settings.colorB) &&
        LibraReadFluidNumber(object, TEXT("colorA"), settings.colorA) &&
        (!object->HasField(TEXT("vorticity")) || LibraReadFluidNumber(object, TEXT("vorticity"), settings.vorticity)) &&
        validFluid2DSettings(settings);
}

bool LibraValidateFluidScope(const std::vector<std::uint8_t>& snapshotBytes,
    const std::vector<std::uint8_t>& manifestBytes, FString& failure)
{
    const auto reject = [&failure](const TCHAR* message)
    {
        failure = message;
        return false;
    };
    if (manifestBytes.empty())
    {
        if (snapshotBytes.empty()) return true;
        auto legacySnapshots = TArray<TSharedPtr<FJsonValue>>{};
        auto legacyReader = TJsonReaderFactory<>::Create(LibraPackageJsonText(snapshotBytes));
        if (!FJsonSerializer::Deserialize(legacyReader, legacySnapshots))
            return reject(TEXT("legacy GPU container snapshot JSON is invalid"));
        for (const auto& value : legacySnapshots)
        {
            auto node = value && value->Type == EJson::Object ? value->AsObject() : nullptr;
            const TSharedPtr<FJsonObject>* gpuState = nullptr;
            if (node && node->TryGetObjectField(TEXT("gpuState"), gpuState) && gpuState->IsValid() &&
                (*gpuState)->HasField(TEXT("fluid2D")))
                return reject(TEXT("legacy package contains Fluid2D without a version-2 manifest"));
        }
        return true;
    }
    auto manifest = TSharedPtr<FJsonObject>{};
    auto manifestReader = TJsonReaderFactory<>::Create(LibraPackageJsonText(manifestBytes));
    if (!FJsonSerializer::Deserialize(manifestReader, manifest) || !manifest)
        return reject(TEXT("GPU executable manifest JSON is invalid"));
    auto version = double{};
    if (!manifest->TryGetNumberField(TEXT("version"), version) || (version != 1 && version != 2 && version != 3))
        return reject(TEXT("GPU executable manifest has an unsupported version"));
    TArray<TSharedPtr<FJsonValue>> snapshots;
    if (!snapshotBytes.empty())
    {
        auto snapshotReader = TJsonReaderFactory<>::Create(LibraPackageJsonText(snapshotBytes));
        if (!FJsonSerializer::Deserialize(snapshotReader, snapshots) || snapshots.Num() > 16)
            return reject(TEXT("GPU container snapshot JSON is invalid or exceeds 16 entries"));
    }
    auto expectedParticles = TSet<FGuid>{};
    auto expectedFluids = TSet<FGuid>{};
    auto allIds = TSet<FGuid>{};
    for (const auto& value : snapshots)
    {
        auto node = value && value->Type == EJson::Object ? value->AsObject() : nullptr;
        auto id = FString{}, kind = FString{};
        auto guid = FGuid{};
        if (!node || !node->TryGetStringField(TEXT("id"), id) ||
            !node->TryGetStringField(TEXT("kind"), kind) || kind != TEXT("GpuParticle") ||
            !FGuid::Parse(id, guid) || !guid.IsValid() || allIds.Contains(guid))
            return reject(TEXT("GPU container snapshot has an invalid, duplicate, or non-GpuParticle node ID"));
        allIds.Add(guid);
        auto isFluid = false;
        const TSharedPtr<FJsonObject>* gpuState = nullptr;
        if (node->HasField(TEXT("gpuState")))
        {
            if (!node->TryGetObjectField(TEXT("gpuState"), gpuState) || !gpuState->IsValid())
                return reject(TEXT("GPU container snapshot gpuState is not an object"));
            if ((*gpuState)->HasField(TEXT("fluid2D")))
            {
                const auto* marker = (*gpuState)->Values.Find(TEXT("fluid2D"));
                if (!marker || !marker->IsValid() ||
                    !((*marker)->Type == EJson::Object ||
                        ((*marker)->Type == EJson::Boolean && (*marker)->AsBool())))
                    return reject(TEXT("GPU container snapshot has an invalid fluid2D marker"));
                isFluid = true;
            }
        }
        (isFluid ? expectedFluids : expectedParticles).Add(guid);
    }
    const auto markedFluids = expectedFluids;
    if (version == 1)
        return expectedFluids.IsEmpty() && !manifest->HasField(TEXT("fluidPrograms"))
            ? true : reject(TEXT("version-1 manifest cannot contain Fluid2D snapshots or fluidPrograms"));
    const TArray<TSharedPtr<FJsonValue>>* programs = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* fluids = nullptr;
    if (!manifest->TryGetArrayField(TEXT("programs"), programs) ||
        !manifest->TryGetArrayField(TEXT("fluidPrograms"), fluids))
        return reject(TEXT("version-2 manifest is missing programs or fluidPrograms array"));
    auto manifestFluidIds = TSet<FGuid>{};
    for (const auto& value : *fluids)
    {
        auto fluid = value && value->Type == EJson::Object ? value->AsObject() : nullptr;
        auto id = FString{};
        auto guid = FGuid{};
        if (!fluid || !fluid->TryGetStringField(TEXT("nodeId"), id) ||
            !FGuid::Parse(id, guid) || !allIds.Contains(guid) || manifestFluidIds.Contains(guid))
            return reject(TEXT("Fluid2D program nodeId is invalid, duplicated, or absent from GPU snapshots"));
        manifestFluidIds.Add(guid);
    }
    if (markedFluids.IsEmpty())
    {
        for (const auto& guid : manifestFluidIds)
        {
            if (!expectedParticles.Remove(guid))
                return reject(TEXT("Fluid2D manifest nodeId is already classified as a particle"));
            expectedFluids.Add(guid);
        }
    }
    else
    {
        if (markedFluids.Num() != manifestFluidIds.Num())
            return reject(TEXT("snapshot gpuState.fluid2D markers do not match Fluid2D manifest entries"));
        for (const auto& guid : manifestFluidIds)
            if (!markedFluids.Contains(guid))
                return reject(TEXT("snapshot gpuState.fluid2D markers do not match Fluid2D manifest entries"));
    }
    if (expectedFluids.IsEmpty())
        return reject(TEXT("version-2 manifest contains no Fluid2D programs"));
    if (programs->Num() != expectedParticles.Num() || fluids->Num() != expectedFluids.Num())
    {
        failure = FString::Printf(
            TEXT("version-2 manifest/snapshot count mismatch (particles %d/%d, fluids %d/%d)"),
            programs->Num(), expectedParticles.Num(), fluids->Num(), expectedFluids.Num());
        return false;
    }
    if (programs->Num() + fluids->Num() > 16)
        return reject(TEXT("version-2 manifest exceeds the 16-program scope limit"));
    for (const auto& value : *programs)
    {
        auto program = value && value->Type == EJson::Object ? value->AsObject() : nullptr;
        auto id = FString{};
        auto guid = FGuid{};
        if (!program || !program->TryGetStringField(TEXT("nodeId"), id) ||
            !FGuid::Parse(id, guid) || !expectedParticles.Remove(guid))
            return reject(TEXT("particle program nodeId is invalid, duplicated, or absent from GPU snapshots"));
    }
    for (const auto& value : *fluids)
    {
        auto fluid = value && value->Type == EJson::Object ? value->AsObject() : nullptr;
        auto id = FString{}, material = FString{};
        auto guid = FGuid{};
        const TSharedPtr<FJsonObject>* settings = nullptr;
        const TSharedPtr<FJsonObject>* renderer = nullptr;
        auto sizeScale = double{};
        if (!fluid || !fluid->TryGetStringField(TEXT("nodeId"), id) ||
            !FGuid::Parse(id, guid) || !expectedFluids.Remove(guid) ||
            !fluid->TryGetObjectField(TEXT("settings"), settings) ||
            !LibraValidateFluidSettings(*settings) ||
            !fluid->TryGetObjectField(TEXT("renderer"), renderer) || !renderer->IsValid() ||
            !(*renderer)->TryGetStringField(TEXT("material"), material) ||
            !LibraFluidMaterialMatches(*settings, material) ||
            !(*renderer)->TryGetNumberField(TEXT("sizeScale"), sizeScale) ||
            !FMath::IsFinite(sizeScale) || sizeScale <= 0 || sizeScale > 10000)
            return reject(TEXT("Fluid2D program has an invalid ID, settings, or renderer"));
        if (fluid->HasField(TEXT("automaticPlane")))
        {
            bool automaticPlane;
            if (version < 3 || !fluid->TryGetBoolField(TEXT("automaticPlane"), automaticPlane))
                return reject(TEXT("Fluid output selection requires version 3 and a boolean"));
        }
    }
    for (const auto& value : *programs)
    {
        const auto program = value->AsObject();
        const TSharedPtr<FJsonObject>* delivery = nullptr;
        const TSharedPtr<FJsonObject>* generated = nullptr;
        if (!program || !program->TryGetObjectField(TEXT("unreal"), delivery))
            return reject(TEXT("GPU particle delivery is missing"));
        if (!(*delivery)->HasField(TEXT("generatedResources")))
            continue;
        if (version < 3 || !(*delivery)->TryGetObjectField(TEXT("generatedResources"), generated) || (*generated)->Values.Num() > 64)
            return reject(TEXT("Generated resources require version 3 and bounded bindings"));
        for (const auto& binding : (*generated)->Values)
        {
            const auto reference = binding.Value->AsObject();
            FString producerId, output;
            FGuid producer;
            if (!reference || reference->Values.Num() != 2 ||
                !reference->TryGetStringField(TEXT("producerNodeId"), producerId) || !FGuid::Parse(producerId, producer) ||
                !manifestFluidIds.Contains(producer) || !reference->TryGetStringField(TEXT("outputId"), output) ||
                (output != TEXT("DyeOutput") && output != TEXT("ColorOutput") && output != TEXT("FieldOutput") && output != TEXT("VelocityOutput") && output != TEXT("DensityOutput")))
                return reject(TEXT("Generated texture producer/output does not match this execution scope"));
        }
    }
    return expectedParticles.IsEmpty() && expectedFluids.IsEmpty()
        ? true : reject(TEXT("GPU container snapshots contain programs missing from the manifest"));
}
}

auto ULibraSpawnableAsset::ValidatePackage() const -> ELibraRuntimeResult
{
    return ValidatePackageBuffer(PackageBytes);
}

auto ULibraSpawnableAsset::SetPackageBytes(TConstArrayView<uint8> bytes) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (bytes.Num() <= 0 || bytes.Num() > MaximumPackageBytes)
        return ELibraRuntimeResult::InvalidBinary;
    auto result = ValidatePackageBuffer(bytes);
    if (result != ELibraRuntimeResult::Ok) return result;
    PackageBytes.Reset(bytes.Num());
    PackageBytes.Append(bytes.GetData(), bytes.Num());
    return ELibraRuntimeResult::Ok;
}

auto ULibraSpawnableAsset::ValidatePackageBuffer(TConstArrayView<uint8> bytes) -> ELibraRuntimeResult
{
    if (bytes.Num() <= 0 || bytes.Num() > MaximumPackageBytes)
        return ELibraRuntimeResult::InvalidBinary;
    auto package = liberafx::SpawnablePackage{};
    auto result = liberafx::loadSpawnablePackage(bytes.GetData(), bytes.Num(), package);
    if (result != liberafx::Result::Ok) return ToLibraResult(result);
    auto failure = FString{};
    if (!LibraValidateFluidScope(package.rootGpuContainersJson, package.rootGpuProgramsJson, failure))
    {
        UE_LOG(LogTemp, Error, TEXT("Libra GPU root scope rejected: %s"), *failure);
        return ELibraRuntimeResult::InvalidBinary;
    }
    for (const auto& entry : package.templates)
    {
        failure.Reset();
        if (!LibraValidateFluidScope(entry.gpuContainersJson, entry.gpuProgramsJson, failure))
        {
            UE_LOG(LogTemp, Error, TEXT("Libra GPU template scope rejected: %s"), *failure);
            return ELibraRuntimeResult::InvalidBinary;
        }
    }
    return ELibraRuntimeResult::Ok;
}

UObject* ULibraSpawnableAsset::FindResourceOverride(const FString& identity, ELibraPackageResourceKind kind) const
{
    check(IsInGameThread());
    const auto* binding = ResourceOverrides.FindByPredicate([&](const auto& entry) {
        return entry.Kind == kind && entry.Identity.Equals(identity, ESearchCase::CaseSensitive);
    });
    return binding ? binding->Asset.Get() : nullptr;
}

#if WITH_EDITOR
namespace {
using FLibraPackageResourceJsonArray = TArray<TSharedPtr<FJsonValue>>;

TSharedPtr<FJsonObject> ReadLibraPackageResourceObject(const std::vector<std::uint8_t>& bytes)
{
    auto text = FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(bytes.data()), bytes.size());
    auto root = TSharedPtr<FJsonObject>{};
    return FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(FString(text.Length(), text.Get())), root)
        ? root : nullptr;
}

bool AddLibraPackageResourceKey(TArray<FLibraPackageResourceOverride>& keys, const FString& identity,
    ELibraPackageResourceKind kind)
{
    if (identity.IsEmpty()) return true; // An unassigned source is not an identity.
    if (identity.Len() > 4096) return false;
    if (!keys.ContainsByPredicate([&](const auto& key) {
        return key.Kind == kind && key.Identity.Equals(identity, ESearchCase::CaseSensitive);
    }))
    {
        if (keys.Num() >= 4096) return false;
        auto& key = keys.AddDefaulted_GetRef();
        key.Identity = identity;
        key.Kind = kind;
    }
    return true;
}

bool ReadLibraPackageResourceDrawKeys(const std::vector<std::uint8_t>& bytes,
    TArray<FLibraPackageResourceOverride>& keys)
{
    if (bytes.empty()) return true;
    const auto root = ReadLibraPackageResourceObject(bytes);
    const FLibraPackageResourceJsonArray* nodes = nullptr;
    auto type = FString{};
    if (!root || !root->TryGetStringField(TEXT("type"), type) || type != TEXT("render_config") ||
        !root->TryGetArrayField(TEXT("nodes"), nodes) || nodes->Num() > 128) return false;
    for (const auto& value : *nodes)
    {
        auto node = value && value->Type == EJson::Object ? value->AsObject() : nullptr;
        auto kind = FString{}, identity = FString{};
        if (!node || !node->TryGetStringField(TEXT("kind"), kind) ||
            (kind != TEXT("model") && kind != TEXT("sprite")) ||
            !node->TryGetStringField(TEXT("assetPath"), identity) ||
            !AddLibraPackageResourceKey(keys, identity, kind == TEXT("model")
                ? ELibraPackageResourceKind::Model : ELibraPackageResourceKind::Texture))
            return false;
        auto hasMaterialZero = false;
        for (auto field : { TEXT("materials"), TEXT("textures") })
        {
            const FLibraPackageResourceJsonArray* entries = nullptr;
            if (!node->TryGetArrayField(field, entries) || entries->Num() > 256) return false;
            const auto isMaterial = FCString::Strcmp(field, TEXT("materials")) == 0;
            for (const auto& entry : *entries)
            {
                auto object = entry && entry->Type == EJson::Object ? entry->AsObject() : nullptr;
                auto slot = double{};
                if (!object || !object->TryGetStringField(TEXT("path"), identity) ||
                    !AddLibraPackageResourceKey(keys, identity, isMaterial
                        ? ELibraPackageResourceKind::Material : ELibraPackageResourceKind::Texture))
                    return false;
                if (isMaterial && object->TryGetNumberField(TEXT("slot"), slot) && slot == 0)
                    hasMaterialZero = true;
            }
        }
        if (kind == TEXT("sprite") && !hasMaterialZero &&
            !AddLibraPackageResourceKey(keys, TEXT("builtin:sprite"), ELibraPackageResourceKind::Material))
            return false;
    }
    const FLibraPackageResourceJsonArray* cpuParticles = nullptr;
    if (root->HasField(TEXT("cpuParticles")) &&
        !root->TryGetArrayField(TEXT("cpuParticles"), cpuParticles))
        return false;
    if (cpuParticles)
    {
        auto cpuVersion = double{};
        if (cpuParticles->Num() > 128 ||
            (!cpuParticles->IsEmpty() && (!root->TryGetNumberField(TEXT("cpuParticleVersion"), cpuVersion) ||
                (cpuVersion != 1 && cpuVersion != 2 && cpuVersion != 3 && cpuVersion != 4 && cpuVersion != 5))))
        {
            return false;
        }
        for (const auto& value : *cpuParticles)
        {
            auto particle = value && value->Type == EJson::Object ? value->AsObject() : nullptr;
            auto identity = FString{}, blendMode = FString{};
            if (!particle || !particle->TryGetStringField(TEXT("assetPath"), identity) ||
                !particle->TryGetStringField(TEXT("blendMode"), blendMode) ||
                (blendMode != TEXT("Alpha") && blendMode != TEXT("Additive")) ||
                (identity != TEXT("builtin:white") && identity != TEXT("builtin:radial-glow") &&
                 identity != TEXT("builtin:procedural-material") && identity != TEXT("builtin:procedural-texture") &&
                    !AddLibraPackageResourceKey(keys, identity, ELibraPackageResourceKind::Texture)))
            {
                return false;
            }
            const TArray<TSharedPtr<FJsonValue>>* materials = nullptr;
            auto hasCustomMaterial = false;
            if (particle->TryGetArrayField(TEXT("materials"), materials) && materials && !materials->IsEmpty())
            {
                for (const auto& matVal : *materials)
                {
                    auto matObj = matVal.IsValid() && matVal->Type == EJson::Object ? matVal->AsObject() : nullptr;
                    auto matPath = FString{};
                    if (matObj && matObj->TryGetStringField(TEXT("path"), matPath) && !matPath.IsEmpty())
                    {
                        if (!AddLibraPackageResourceKey(keys, matPath, ELibraPackageResourceKind::Material))
                            return false;
                        hasCustomMaterial = true;
                    }
                }
            }
            if (!hasCustomMaterial)
            {
                const auto* materialIdentity = blendMode == TEXT("Additive")
                    ? TEXT("builtin:cpu-particle-additive") : TEXT("builtin:cpu-particle-alpha");
                if (!AddLibraPackageResourceKey(keys, materialIdentity, ELibraPackageResourceKind::Material))
                {
                    return false;
                }
            }
        }
    }
    return true;
}

bool ReadLibraPackageResourceGpuKeys(const std::vector<std::uint8_t>& bytes,
    TArray<FLibraPackageResourceOverride>& keys)
{
    if (bytes.empty()) return true;
    const auto root = ReadLibraPackageResourceObject(bytes);
    const FLibraPackageResourceJsonArray* programs = nullptr;
    const FLibraPackageResourceJsonArray* fluids = nullptr;
    auto version = double{};
    if (!root || !root->TryGetNumberField(TEXT("version"), version) ||
        (version != 1 && version != 2 && version != 3) ||
        !root->TryGetArrayField(TEXT("programs"), programs) || programs->Num() > 16)
        return false;
    if ((version == 1 && root->HasField(TEXT("fluidPrograms"))) ||
        (version >= 2 && (!root->TryGetArrayField(TEXT("fluidPrograms"), fluids) ||
            fluids->IsEmpty() || programs->Num() + fluids->Num() > 16)))
        return false;
    for (const auto& value : *programs)
    {
        auto program = value && value->Type == EJson::Object ? value->AsObject() : nullptr;
        const TSharedPtr<FJsonObject>* delivery = nullptr;
        const TSharedPtr<FJsonObject>* paths = nullptr;
        if (!program || !program->TryGetObjectField(TEXT("unreal"), delivery) ||
            !(*delivery)->TryGetObjectField(TEXT("resourceAssetPaths"), paths))
            return false;
        for (const auto& entry : (*paths)->Values)
        {
            auto identity = FString{};
            if (!entry.Value || !entry.Value->TryGetString(identity) ||
                (identity != TEXT("builtin:white") &&
                    !AddLibraPackageResourceKey(keys, identity, ELibraPackageResourceKind::Texture)))
                return false;
        }
    }
    if (fluids)
    {
        for (const auto& value : *fluids)
        {
            auto fluid = value && value->Type == EJson::Object ? value->AsObject() : nullptr;
            const TSharedPtr<FJsonObject>* settings = nullptr;
            const TSharedPtr<FJsonObject>* renderer = nullptr;
            FString nodeId, material;
            double sizeScale = 0;
            if (!fluid || !fluid->TryGetStringField(TEXT("nodeId"), nodeId) ||
                !fluid->TryGetObjectField(TEXT("settings"), settings) ||
                !fluid->TryGetObjectField(TEXT("renderer"), renderer) ||
                !(*renderer)->TryGetStringField(TEXT("material"), material) ||
                !LibraFluidMaterialMatches(*settings, material) ||
                !(*renderer)->TryGetNumberField(TEXT("sizeScale"), sizeScale) ||
                !FMath::IsFinite(sizeScale) || sizeScale <= 0 || sizeScale > 10000)
                return false;
        }
    }
    return true;
}
}

bool ULibraSpawnableAsset::GetPackageResourceKeys(TArray<FLibraPackageResourceOverride>& keys, FString& error) const
{
    check(IsInGameThread());
    error.Reset();
    auto package = liberafx::SpawnablePackage{};
    auto candidate = TArray<FLibraPackageResourceOverride>{};
    auto valid = !PackageBytes.IsEmpty() && liberafx::loadSpawnablePackage(
        PackageBytes.GetData(), PackageBytes.Num(), package) == liberafx::Result::Ok &&
        ReadLibraPackageResourceDrawKeys(package.rootRenderConfiguration, candidate) &&
        ReadLibraPackageResourceGpuKeys(package.rootGpuProgramsJson, candidate);
    if (valid)
        for (const auto& entry : package.templates)
            if (!ReadLibraPackageResourceDrawKeys(entry.renderConfiguration, candidate) ||
                !ReadLibraPackageResourceGpuKeys(entry.gpuProgramsJson, candidate))
            {
                valid = false;
                break;
            }
    if (!valid)
    {
        error = TEXT("Cannot read the embedded renderer resource list. Export a valid .lfxp and reimport it.");
        return false;
    }
    keys = MoveTemp(candidate);
    return true;
}

UClass* ULibraSpawnableAsset::GetResourceClass(ELibraPackageResourceKind kind)
{
    switch (kind)
    {
        case ELibraPackageResourceKind::Model: return UStaticMesh::StaticClass();
        case ELibraPackageResourceKind::Texture: return UTexture2D::StaticClass();
        case ELibraPackageResourceKind::Material: return UMaterialInterface::StaticClass();
        default: return nullptr;
    }
}

bool ULibraSpawnableAsset::SetResourceOverride(const FString& identity, ELibraPackageResourceKind kind,
    UObject* asset, FString& error)
{
    check(IsInGameThread());
    error.Reset();
    auto matches = [&](const auto& key) {
        return key.Kind == kind && key.Identity.Equals(identity, ESearchCase::CaseSensitive);
    };
    auto index = ResourceOverrides.IndexOfByPredicate(matches);
    if (asset)
    {
        auto* expectedClass = GetResourceClass(kind);
        if (!IsValid(asset) || !expectedClass || !asset->IsA(expectedClass) || !asset->IsAsset() ||
            asset->GetOutermost()->HasAnyPackageFlags(PKG_EditorOnly) ||
            !FPackageName::IsValidObjectPath(asset->GetPathName()))
        {
            error = TEXT("Select a persistent Unreal asset of the required type (Static Mesh, Texture2D or Material).");
            return false;
        }
        auto keys = TArray<FLibraPackageResourceOverride>{};
        if (!GetPackageResourceKeys(keys, error)) return false;
        if (!keys.ContainsByPredicate(matches))
        {
            error = TEXT("This resource is no longer in the package. Clear the override or refresh the inspector after reimport.");
            return false;
        }
        if (index != INDEX_NONE && ResourceOverrides[index].Asset == asset) return true;
        SetFlags(RF_Transactional);
        Modify();
        if (index == INDEX_NONE)
        {
            index = ResourceOverrides.AddDefaulted();
            ResourceOverrides[index].Identity = identity;
            ResourceOverrides[index].Kind = kind;
        }
        ResourceOverrides[index].Asset = asset;
    }
    else if (index != INDEX_NONE)
    {
        SetFlags(RF_Transactional);
        Modify();
        ResourceOverrides.RemoveAt(index);
    }
    return true;
}

void ULibraSpawnableAsset::GetAssetRegistryTags(FAssetRegistryTagsContext context) const
{
    Super::GetAssetRegistryTags(context);
#if WITH_EDITORONLY_DATA
    if (AssetImportData)
        context.AddTag(FAssetRegistryTag(SourceFileTagName(), AssetImportData->GetSourceData().ToJson(),
            FAssetRegistryTag::TT_Hidden));
#endif
}
#endif

void ULibraSpawnableAsset::PostLoad()
{
    Super::PostLoad();
#if WITH_EDITORONLY_DATA
    if (!AssetImportData) AssetImportData = NewObject<UAssetImportData>(this, TEXT("AssetImportData"));
#endif
}
