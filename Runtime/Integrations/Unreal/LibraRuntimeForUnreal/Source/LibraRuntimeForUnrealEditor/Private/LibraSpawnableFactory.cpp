#include "LibraSpawnableFactory.h"

#include "EditorFramework/AssetImportData.h"
#include "HAL/FileManager.h"
#include "LibraSpawnableAsset.h"
#include "Misc/Paths.h"
#include "Serialization/Archive.h"

DEFINE_LOG_CATEGORY_STATIC(LogLibraSpawnableImport, Log, All);

auto ULibraSpawnableFactory::ReadPackage(const FString& filename, TArray<uint8>& bytes) -> bool
{
    auto extension = FPaths::GetExtension(filename);
    if (!extension.Equals(TEXT("lfxb"), ESearchCase::IgnoreCase) &&
        !extension.Equals(TEXT("lfxp"), ESearchCase::IgnoreCase))
    {
        return false;
    }
    auto input = TUniquePtr<FArchive>(IFileManager::Get().CreateFileReader(*filename));
    if (!input)
    {
        return false;
    }
    auto size = input->TotalSize();
    if (size <= 0 || size > ULibraSpawnableAsset::MaximumPackageBytes)
    {
        return false;
    }
    bytes.SetNumUninitialized(static_cast<int32>(size));
    input->Serialize(bytes.GetData(), size);
    if (input->IsError())
    {
        return false;
    }
    return ULibraSpawnableAsset::ValidatePackageBuffer(bytes) == ELibraRuntimeResult::Ok;
}

void ULibraSpawnableFactory::UpdateImportData(ULibraSpawnableAsset& asset, const FString& filename)
{
    if (!asset.AssetImportData)
    {
        asset.AssetImportData = NewObject<UAssetImportData>(&asset, TEXT("AssetImportData"));
    }
    asset.AssetImportData->Update(filename);
}

ULibraSpawnableFactory::ULibraSpawnableFactory()
{
    SupportedClass = ULibraSpawnableAsset::StaticClass();
    bEditorImport = true;
    bCreateNew = false;
    Formats.Add(TEXT("lfxb;LIBERA FX compiled package"));
    Formats.Add(TEXT("lfxp;LIBERA FX legacy package alias"));
}

auto ULibraSpawnableFactory::FactoryCanImport(const FString& filename) -> bool
{
    auto bytes = TArray<uint8>{};
    return ReadPackage(filename, bytes);
}

auto ULibraSpawnableFactory::FactoryCreateFile(UClass* inClass, UObject* inParent, FName inName,
    EObjectFlags flags, const FString& filename, const TCHAR* parms, FFeedbackContext* warn,
    bool& outOperationCanceled) -> UObject*
{
    outOperationCanceled = false;
    auto bytes = TArray<uint8>{};
    if (!inClass || !inClass->IsChildOf(ULibraSpawnableAsset::StaticClass()) ||
        !ReadPackage(filename, bytes))
    {
        UE_LOG(LogLibraSpawnableImport, Error, TEXT("Cannot import invalid or unsupported LIBERA FX package: %s"), *filename);
        return nullptr;
    }
    auto asset = NewObject<ULibraSpawnableAsset>(inParent, inClass, inName, flags);
    if (asset->SetPackageBytes(bytes) != ELibraRuntimeResult::Ok)
    {
        asset->ClearFlags(RF_Public | RF_Standalone);
        asset->MarkAsGarbage();
        return nullptr;
    }
    UpdateImportData(*asset, filename);
    return asset;
}

auto ULibraSpawnableFactory::CanReimport(UObject* object, TArray<FString>& outFilenames) -> bool
{
    auto asset = Cast<ULibraSpawnableAsset>(object);
    if (!asset || !asset->AssetImportData)
    {
        return false;
    }
    asset->AssetImportData->ExtractFilenames(outFilenames);
    return outFilenames.Num() == 1;
}

void ULibraSpawnableFactory::SetReimportPaths(UObject* object, const TArray<FString>& newPaths)
{
    auto asset = Cast<ULibraSpawnableAsset>(object);
    if (asset && asset->AssetImportData && newPaths.Num() == 1)
    {
        asset->Modify();
        asset->AssetImportData->Modify();
        asset->AssetImportData->UpdateFilenameOnly(newPaths[0]);
    }
}

auto ULibraSpawnableFactory::Reimport(UObject* object) -> EReimportResult::Type
{
    auto asset = Cast<ULibraSpawnableAsset>(object);
    if (!asset || !asset->AssetImportData)
    {
        return EReimportResult::Failed;
    }
    auto filename = asset->AssetImportData->GetFirstFilename();
    auto bytes = TArray<uint8>{};
    if (!ReadPackage(filename, bytes))
    {
        return EReimportResult::Failed;
    }
    asset->Modify();
    if (asset->SetPackageBytes(bytes) != ELibraRuntimeResult::Ok)
    {
        return EReimportResult::Failed;
    }
    asset->AssetImportData->Modify();
    UpdateImportData(*asset, filename);
    asset->MarkPackageDirty();
    return EReimportResult::Succeeded;
}
