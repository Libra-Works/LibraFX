#include "LibraSpawnableAsset.h"

#include "LibraNativeState.h"
#if WITH_EDITORONLY_DATA
#include "EditorFramework/AssetImportData.h"
#endif
#if WITH_EDITOR
#include "Dom/JsonObject.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "Materials/MaterialInterface.h"
#include "Misc/PackageName.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UObject/AssetRegistryTagsContext.h"
#include "UObject/Package.h"
#endif
#include "liberafx/LbSpawnablePackage.h"

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
    return ToLibraResult(liberafx::loadSpawnablePackage(bytes.GetData(), bytes.Num(), package));
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
    return true;
}

bool ReadLibraPackageResourceGpuKeys(const std::vector<std::uint8_t>& bytes,
    TArray<FLibraPackageResourceOverride>& keys)
{
    if (bytes.empty()) return true;
    const auto root = ReadLibraPackageResourceObject(bytes);
    const FLibraPackageResourceJsonArray* programs = nullptr;
    if (!root || !root->TryGetArrayField(TEXT("programs"), programs) || programs->Num() > 16) return false;
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
