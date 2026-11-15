#include "LibraGpuEffectFactory.h"

#include "EditorFramework/AssetImportData.h"
#include "HAL/FileManager.h"
#include "LibraGpuEffectAsset.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"

DEFINE_LOG_CATEGORY_STATIC(LogLibraGpuImport, Log, All);

namespace
{
bool ReadBundle(const FString& filename, FString& text)
{
    auto size = IFileManager::Get().FileSize(*filename);
    return size > 0 && size <= ULibraGpuEffectAsset::MaximumBundleBytes &&
        FFileHelper::LoadFileToString(text, *filename);
}

void UpdateImportData(ULibraGpuEffectAsset& asset, const FString& filename)
{
    if (!asset.AssetImportData) asset.AssetImportData = NewObject<UAssetImportData>(&asset, TEXT("AssetImportData"));
    asset.AssetImportData->Update(filename);
}
}

ULibraGpuEffectFactory::ULibraGpuEffectFactory()
{
    SupportedClass = ULibraGpuEffectAsset::StaticClass();
    bEditorImport = true;
    bCreateNew = false;
    Formats.Add(TEXT("lfxgpu;LIBERA FX GPU source bundle"));
}

bool ULibraGpuEffectFactory::FactoryCanImport(const FString& filename)
{
    return FPaths::GetExtension(filename).Equals(TEXT("lfxgpu"), ESearchCase::IgnoreCase);
}

UObject* ULibraGpuEffectFactory::FactoryCreateFile(UClass* inClass, UObject* inParent, FName inName,
    EObjectFlags flags, const FString& filename, const TCHAR* parms, FFeedbackContext* warn,
    bool& outOperationCanceled)
{
    outOperationCanceled = false;
    FString text, error;
    if (!inClass || !inClass->IsChildOf(ULibraGpuEffectAsset::StaticClass()) ||
        !FactoryCanImport(filename) || !ReadBundle(filename, text))
    {
        UE_LOG(LogLibraGpuImport, Error, TEXT("Cannot read GPU bundle: %s"), *filename);
        return nullptr;
    }
    auto asset = NewObject<ULibraGpuEffectAsset>(inParent, inClass, inName, flags);
    if (!asset->ImportBundle(text, error))
    {
        UE_LOG(LogLibraGpuImport, Error, TEXT("Invalid GPU bundle %s: %s"), *filename, *error);
        asset->ClearFlags(RF_Public | RF_Standalone);
        asset->MarkAsGarbage();
        return nullptr;
    }
    UpdateImportData(*asset, filename);
    return asset;
}

bool ULibraGpuEffectFactory::CanReimport(UObject* object, TArray<FString>& outFilenames)
{
    auto asset = Cast<ULibraGpuEffectAsset>(object);
    if (!asset || !asset->AssetImportData) return false;
    asset->AssetImportData->ExtractFilenames(outFilenames);
    return outFilenames.Num() == 1;
}

void ULibraGpuEffectFactory::SetReimportPaths(UObject* object, const TArray<FString>& newPaths)
{
    auto asset = Cast<ULibraGpuEffectAsset>(object);
    if (asset && asset->AssetImportData && newPaths.Num() == 1)
    {
        asset->Modify();
        asset->AssetImportData->Modify();
        asset->AssetImportData->UpdateFilenameOnly(newPaths[0]);
    }
}

EReimportResult::Type ULibraGpuEffectFactory::Reimport(UObject* object)
{
    auto asset = Cast<ULibraGpuEffectAsset>(object);
    if (!asset || !asset->AssetImportData) return EReimportResult::Failed;
    auto filename = asset->AssetImportData->GetFirstFilename();
    FString text, error;
    if (!ReadBundle(filename, text)) return EReimportResult::Failed;
    auto candidate = NewObject<ULibraGpuEffectAsset>();
    if (!candidate->ImportBundle(text, error))
    {
        UE_LOG(LogLibraGpuImport, Error, TEXT("GPU reimport rejected: %s"), *error);
        return EReimportResult::Failed;
    }
    asset->Modify();
    if (!asset->ImportBundle(text, error))
    {
        UE_LOG(LogLibraGpuImport, Error, TEXT("GPU reimport rejected: %s"), *error);
        return EReimportResult::Failed;
    }
    asset->AssetImportData->Modify();
    UpdateImportData(*asset, filename);
    asset->MarkPackageDirty();
    return EReimportResult::Succeeded;
}
