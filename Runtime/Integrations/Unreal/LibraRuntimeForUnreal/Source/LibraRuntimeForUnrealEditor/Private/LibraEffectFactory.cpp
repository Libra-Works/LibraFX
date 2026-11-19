#include "LibraEffectFactory.h"

#include "EditorFramework/AssetImportData.h"
#include "HAL/FileManager.h"
#include "LibraEffectAsset.h"
#include "Misc/Paths.h"
#include "Serialization/Archive.h"

DEFINE_LOG_CATEGORY_STATIC(LogLibraImport, Log, All);

auto ULibraEffectFactory::ReadBytecode(const FString& filename, TArray<uint8>& bytes) -> bool
{
    if (!FPaths::GetExtension(filename).Equals(TEXT("lfxb"), ESearchCase::IgnoreCase))
    {
        return false;
    }
    auto input = TUniquePtr<FArchive>(IFileManager::Get().CreateFileReader(*filename));
    if (!input)
    {
        return false;
    }
    auto size = input->TotalSize();
    if (size <= 0 || size > ULibraEffectAsset::MaximumBytecodeBytes)
    {
        return false;
    }
    bytes.SetNumUninitialized(static_cast<int32>(size));
    input->Serialize(bytes.GetData(), size);
    if (input->IsError())
    {
        return false;
    }
    return ULibraEffectAsset::ValidateBytecodeBuffer(bytes) == ELibraRuntimeResult::Ok;
}

void ULibraEffectFactory::UpdateImportData(ULibraEffectAsset& asset, const FString& filename)
{
    if (!asset.AssetImportData)
    {
        asset.AssetImportData = NewObject<UAssetImportData>(&asset, TEXT("AssetImportData"));
    }
    asset.AssetImportData->Update(filename);
}

ULibraEffectFactory::ULibraEffectFactory()
{
    SupportedClass = ULibraEffectAsset::StaticClass();
    bEditorImport = true;
    bCreateNew = false;
    Formats.Add(TEXT("lfxb;LIBERA FX legacy raw program"));
}

auto ULibraEffectFactory::FactoryCanImport(const FString& filename) -> bool
{
    auto bytes = TArray<uint8>{};
    return ReadBytecode(filename, bytes);
}

auto ULibraEffectFactory::FactoryCreateFile(UClass* inClass, UObject* inParent, FName inName,
    EObjectFlags flags, const FString& filename, const TCHAR* parms, FFeedbackContext* warn,
    bool& outOperationCanceled) -> UObject*
{
    outOperationCanceled = false;
    auto bytes = TArray<uint8>{};
    if (!inClass || !inClass->IsChildOf(ULibraEffectAsset::StaticClass()) ||
        !ReadBytecode(filename, bytes))
    {
        UE_LOG(LogLibraImport, Error, TEXT("Cannot import invalid or unsupported LIBERA FX bytecode: %s"), *filename);
        return nullptr;
    }
    auto asset = NewObject<ULibraEffectAsset>(inParent, inClass, inName, flags);
    if (asset->SetBytecode(bytes) != ELibraRuntimeResult::Ok)
    {
        asset->ClearFlags(RF_Public | RF_Standalone);
        asset->MarkAsGarbage();
        return nullptr;
    }
    UpdateImportData(*asset, filename);
    return asset;
}

auto ULibraEffectFactory::CanReimport(UObject* object, TArray<FString>& outFilenames) -> bool
{
    auto asset = Cast<ULibraEffectAsset>(object);
    if (!asset || !asset->AssetImportData)
    {
        return false;
    }
    asset->AssetImportData->ExtractFilenames(outFilenames);
    return outFilenames.Num() == 1;
}

void ULibraEffectFactory::SetReimportPaths(UObject* object, const TArray<FString>& newPaths)
{
    auto asset = Cast<ULibraEffectAsset>(object);
    if (asset && asset->AssetImportData && newPaths.Num() == 1)
    {
        asset->Modify();
        asset->AssetImportData->Modify();
        asset->AssetImportData->UpdateFilenameOnly(newPaths[0]);
    }
}

auto ULibraEffectFactory::Reimport(UObject* object) -> EReimportResult::Type
{
    auto asset = Cast<ULibraEffectAsset>(object);
    if (!asset || !asset->AssetImportData)
    {
        return EReimportResult::Failed;
    }
    auto filename = asset->AssetImportData->GetFirstFilename();
    auto bytes = TArray<uint8>{};
    if (!ReadBytecode(filename, bytes))
    {
        return EReimportResult::Failed;
    }
    asset->Modify();
    if (asset->SetBytecode(bytes) != ELibraRuntimeResult::Ok)
    {
        return EReimportResult::Failed;
    }
    asset->AssetImportData->Modify();
    UpdateImportData(*asset, filename);
    asset->MarkPackageDirty();
    return EReimportResult::Succeeded;
}
