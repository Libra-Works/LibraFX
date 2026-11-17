#include "LibraSpawnableAsset.h"

#include "LibraNativeState.h"
#if WITH_EDITORONLY_DATA
#include "EditorFramework/AssetImportData.h"
#endif
#if WITH_EDITOR
#include "UObject/AssetRegistryTagsContext.h"
#endif
#include "liberafx/SpawnablePackage.h"

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

#if WITH_EDITOR
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
