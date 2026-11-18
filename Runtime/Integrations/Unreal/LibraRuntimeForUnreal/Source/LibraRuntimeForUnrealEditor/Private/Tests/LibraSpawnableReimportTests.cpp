#if WITH_DEV_AUTOMATION_TESTS

#include "LibraSpawnableAsset.h"
#include "LibraSpawnableFactory.h"
#include "LibraEffectAsset.h"
#include "../LibraSourceProjectDetails.h"
#include "AssetToolsModule.h"
#include "IAssetTypeActions.h"
#include "EditorFramework/AssetImportData.h"
#include "Engine/StaticMesh.h"
#include "HAL/FileManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraSourceProjectPathTest,
    "Libra.Editor.Resources.SourceProjectPath",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraSourceProjectPathTest::RunTest(const FString&)
{
    const auto directory = FPaths::ConvertRelativePathToFull(FPaths::ProjectSavedDir());
    IFileManager::Get().MakeDirectory(*directory, true);
    const auto source = FPaths::CreateTempFilename(*directory, TEXT("Libra source "), TEXT(".lfxproj"));
    ON_SCOPE_EXIT { IFileManager::Get().Delete(*source); };
    auto path = FString{};
    auto error = FText{};
    TestFalse(TEXT("Reject a relative path"), LibraSourceProject::ValidatePath(TEXT("relative.lfxproj"), path, error));
    TestFalse(TEXT("Reject a compiled path"), LibraSourceProject::ValidatePath(
        FPaths::ChangeExtension(source, TEXT("lfxp")), path, error));
    TestFalse(TEXT("Reject a quoted path"), LibraSourceProject::ValidatePath(
        FString::Printf(TEXT("\"%s\""), *source), path, error));
    TestFalse(TEXT("Reject a network path without opening it"), LibraSourceProject::ValidatePath(
        TEXT("//host/share/project.lfxproj"), path, error));
    TestFalse(TEXT("Reject a missing local file"), LibraSourceProject::ValidatePath(source, path, error));
    if (!TestTrue(TEXT("Write an isolated source placeholder"), FFileHelper::SaveStringToFile(TEXT("{}"), *source)))
        return false;
    TestTrue(TEXT("Accept an existing local .lfxproj, including spaces"),
        LibraSourceProject::ValidatePath(source, path, error));
    TestTrue(TEXT("The actual source path is returned"), FPaths::IsSamePath(source, path));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraAssetFilterTest,
    "Libra.Editor.Spawnable.AssetFilters",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraAssetFilterTest::RunTest(const FString&)
{
    auto& assetTools = FModuleManager::LoadModuleChecked<FAssetToolsModule>("AssetTools").Get();
    auto category = assetTools.FindAdvancedAssetCategory("LibraFX");
    if (!TestTrue(TEXT("LIBERA FX has a dedicated asset category"), category != EAssetTypeCategories::Misc))
        return false;
    for (auto* assetClass : { ULibraEffectAsset::StaticClass(), ULibraSpawnableAsset::StaticClass() })
    {
        auto actions = assetTools.GetAssetTypeActionsForClass(assetClass).Pin();
        if (!TestTrue(FString::Printf(TEXT("%s has registered type actions"), *assetClass->GetName()),
            actions.IsValid()))
            return false;
        TestTrue(TEXT("Type is available as a Content Browser filter"), actions->CanFilter());
        TestTrue(TEXT("Type is in the LIBERA FX category"), (actions->GetCategories() & category) != 0);
    }
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraSpawnableReimportTest,
    "Libra.Editor.Spawnable.ReimportFixture",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraSpawnableReimportTest::RunTest(const FString&)
{
    auto fixture = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraSpawnableFixture="), fixture) || fixture.IsEmpty())
    {
        AddInfo(TEXT("ReimportFixture requires an explicit isolated LFXP fixture path."));
        return true;
    }

    auto bytes = TArray<uint8>{};
    if (!TestTrue(TEXT("LFXP fixture loads"), FFileHelper::LoadFileToArray(bytes, *fixture)) ||
        !TestEqual(TEXT("Fixture is valid"), ULibraSpawnableAsset::ValidatePackageBuffer(bytes),
            ELibraRuntimeResult::Ok))
        return false;

    auto directory = FPaths::ProjectSavedDir();
    IFileManager::Get().MakeDirectory(*directory, true);
    auto sourcePath = FPaths::CreateTempFilename(*directory, TEXT("LibraReimport"), TEXT(".lfxp"));
    ON_SCOPE_EXIT { IFileManager::Get().Delete(*sourcePath); };
    if (!TestTrue(TEXT("Isolated source is written"), FFileHelper::SaveArrayToFile(bytes, *sourcePath)))
        return false;

    auto* factory = NewObject<ULibraSpawnableFactory>();
    auto canceled = false;
    auto* asset = Cast<ULibraSpawnableAsset>(factory->FactoryCreateFile(
        ULibraSpawnableAsset::StaticClass(), GetTransientPackage(), NAME_None, RF_Transient,
        sourcePath, nullptr, GWarn, canceled));
    if (!TestNotNull(TEXT("Factory imports a package"), asset)) return false;
    TestTrue(TEXT("Imported binary is embedded"), TArray<uint8>(asset->GetPackageBytes()) == bytes);
    if (!TestNotNull(TEXT("Import metadata exists"), asset->AssetImportData.Get())) return false;
    const auto projectPath = FPaths::ChangeExtension(sourcePath, TEXT("lfxproj"));
    asset->SourceProjectPath.FilePath = projectPath;
    auto keys = TArray<FLibraPackageResourceOverride>{};
    auto detail = FString{};
    if (!TestTrue(TEXT("Read embedded renderer resource keys"), asset->GetPackageResourceKeys(keys, detail)))
    {
        AddError(detail);
        return false;
    }
    const auto* model = keys.FindByPredicate([](const auto& key) { return key.Kind == ELibraPackageResourceKind::Model; });
    UStaticMesh* replacement = nullptr;
    if (model)
    {
        replacement = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Sphere.Sphere"));
        if (!TestNotNull(TEXT("Persistent test mesh"), replacement) ||
            !TestTrue(TEXT("Accept matching model override"),
                asset->SetResourceOverride(model->Identity, model->Kind, replacement, detail)))
            return false;
    }

    auto updated = bytes;
    // LFXP v1 stores AutoDestroyWhenIdle immediately after its 24-byte header.
    updated[24] = updated[24] ? 0 : 1;
    if (!TestEqual(TEXT("Updated fixture is valid"), ULibraSpawnableAsset::ValidatePackageBuffer(updated),
        ELibraRuntimeResult::Ok) ||
        !TestTrue(TEXT("Source update is written"), FFileHelper::SaveArrayToFile(updated, *sourcePath)))
        return false;

    TestTrue(TEXT("Editor reimport manager reloads from source"),
        FReimportManager::Instance()->Reimport(asset, false, false));
    TestTrue(TEXT("Reimport replaces the embedded binary"), TArray<uint8>(asset->GetPackageBytes()) == updated);
    TestEqual(TEXT("Reimport preserves the independent .lfxproj link"), asset->SourceProjectPath.FilePath, projectPath);
    TestTrue(TEXT("The binary is still the import source"),
        FPaths::IsSamePath(asset->AssetImportData->GetFirstFilename(), sourcePath));
    if (model)
        TestTrue(TEXT("Reimport preserves the override"), asset->FindResourceOverride(model->Identity, model->Kind) == replacement);

    auto invalid = updated;
    invalid[0] = 0;
    if (!TestTrue(TEXT("Malformed source is written"), FFileHelper::SaveArrayToFile(invalid, *sourcePath)))
        return false;
    TestTrue(TEXT("Malformed source fails reimport"), factory->Reimport(asset) == EReimportResult::Failed);
    TestTrue(TEXT("Failed reimport preserves binary"), TArray<uint8>(asset->GetPackageBytes()) == updated);
    if (model)
    {
        TestTrue(TEXT("Failed reimport preserves the override"), asset->FindResourceOverride(model->Identity, model->Kind) == replacement);
        TestTrue(TEXT("Clearing a model override restores source lookup"),
            asset->SetResourceOverride(model->Identity, model->Kind, nullptr, detail) &&
            !asset->FindResourceOverride(model->Identity, model->Kind));
    }

    IFileManager::Get().Delete(*sourcePath);
    TestTrue(TEXT("Missing source fails reimport"), factory->Reimport(asset) == EReimportResult::Failed);
    TestTrue(TEXT("Missing source preserves binary"), TArray<uint8>(asset->GetPackageBytes()) == updated);
    TestEqual(TEXT("Embedded asset is valid without the source"), asset->ValidatePackage(), ELibraRuntimeResult::Ok);
    return true;
}

#endif
