#if WITH_DEV_AUTOMATION_TESTS

#include "LibraSpawnableAsset.h"
#include "LibraSpawnableFactory.h"
#include "LibraEffectAsset.h"
#include "../LibraSourceProjectDetails.h"
#include "AssetToolsModule.h"
#include "IAssetTypeActions.h"
#include "EditorFramework/AssetImportData.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "HAL/FileManager.h"
#include "Materials/MaterialInterface.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "PackageTools.h"
#include "Misc/ScopeExit.h"
#include "UObject/SavePackage.h"

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
    {
        return false;
    }
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
    {
        return false;
    }
    for (auto* assetClass : { ULibraEffectAsset::StaticClass(), ULibraSpawnableAsset::StaticClass() })
    {
        auto actions = assetTools.GetAssetTypeActionsForClass(assetClass).Pin();
        if (!TestTrue(FString::Printf(TEXT("%s has registered type actions"), *assetClass->GetName()),
            actions.IsValid()))
        {
            return false;
        }
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
    auto requiredKindsText = FString{};
    FParse::Value(FCommandLine::Get(), TEXT("LibraSpawnableRequiredKinds="), requiredKindsText);
    auto requestedKinds = TArray<ELibraPackageResourceKind>{};
    if (!requiredKindsText.IsEmpty())
    {
        auto requiredKindNames = TArray<FString>{};
        requiredKindsText.ParseIntoArray(requiredKindNames, TEXT(","), true);
        for (const auto& requiredKindName : requiredKindNames)
        {
            if (requiredKindName.Equals(TEXT("Model"), ESearchCase::IgnoreCase))
            {
                requestedKinds.Add(ELibraPackageResourceKind::Model);
            }
            else if (requiredKindName.Equals(TEXT("Texture"), ESearchCase::IgnoreCase))
            {
                requestedKinds.Add(ELibraPackageResourceKind::Texture);
            }
            else if (requiredKindName.Equals(TEXT("Material"), ESearchCase::IgnoreCase))
            {
                requestedKinds.Add(ELibraPackageResourceKind::Material);
            }
            else
            {
                AddError(FString::Printf(TEXT("Unknown required resource kind '%s'."), *requiredKindName));
                return false;
            }
        }
        if (requestedKinds.IsEmpty())
        {
            AddError(TEXT("LibraSpawnableRequiredKinds must list at least one resource kind."));
            return false;
        }
    }

    auto* modelAsset = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Sphere.Sphere"));
    auto* textureAsset = LoadObject<UTexture2D>(nullptr, TEXT("/Engine/EngineResources/DefaultTexture.DefaultTexture"));
    auto* materialAsset = LoadObject<UMaterialInterface>(nullptr,
        TEXT("/Engine/BasicShapes/BasicShapeMaterial.BasicShapeMaterial"));
    if (!TestNotNull(TEXT("Persistent engine model override"), modelAsset) ||
        !TestNotNull(TEXT("Persistent engine texture override"), textureAsset) ||
        !TestNotNull(TEXT("Persistent engine material override"), materialAsset))
    {
        return false;
    }

    auto* factory = NewObject<ULibraSpawnableFactory>();
    auto kindName = [](ELibraPackageResourceKind kind) -> const TCHAR*
    {
        switch (kind)
        {
        case ELibraPackageResourceKind::Model: return TEXT("Model");
        case ELibraPackageResourceKind::Texture: return TEXT("Texture");
        case ELibraPackageResourceKind::Material: return TEXT("Material");
        default: return TEXT("Unknown");
        }
    };
    auto assetForKind = [&](ELibraPackageResourceKind kind) -> UObject*
    {
        switch (kind)
        {
        case ELibraPackageResourceKind::Model: return modelAsset;
        case ELibraPackageResourceKind::Texture: return textureAsset;
        case ELibraPackageResourceKind::Material: return materialAsset;
        default: return nullptr;
        }
    };
    auto verifyFixture = [&](const FString& fixturePath,
        const TArray<ELibraPackageResourceKind>& requiredKinds) -> bool
    {
        auto bytes = TArray<uint8>{};
        if (!TestTrue(TEXT("LFXP fixture loads"), FFileHelper::LoadFileToArray(bytes, *fixturePath)) ||
            !TestEqual(TEXT("Fixture is valid"), ULibraSpawnableAsset::ValidatePackageBuffer(bytes),
                ELibraRuntimeResult::Ok))
        {
            return false;
        }

        auto directory = FPaths::ProjectSavedDir();
        IFileManager::Get().MakeDirectory(*directory, true);
        auto sourcePath = FPaths::CreateTempFilename(*directory, TEXT("LibraReimport"), TEXT(".lfxp"));
        ON_SCOPE_EXIT { IFileManager::Get().Delete(*sourcePath); };
        if (!TestTrue(TEXT("Isolated source is written"), FFileHelper::SaveArrayToFile(bytes, *sourcePath)))
        {
            return false;
        }

        auto projectPath = FPaths::ChangeExtension(fixturePath, TEXT("lfxproj"));
        const auto hasSourceProject = IFileManager::Get().FileExists(*projectPath);
        if (hasSourceProject)
        {
            TestTrue(TEXT("Compiler-authored .lfxproj source link exists"), hasSourceProject);
        }
        else
        {
            AddInfo(TEXT("Fixture has no sibling .lfxproj; preserving its isolated source-link value only."));
            projectPath = FPaths::ChangeExtension(sourcePath, TEXT("lfxproj"));
        }

        const auto packageToken = FGuid::NewGuid().ToString(EGuidFormats::Digits);
        const auto packageName = FString::Printf(TEXT("/Game/LibraAcceptanceReimport_%s/Spawnable"), *packageToken);
        const auto assetName = FName(TEXT("ReimportedSpawnable"));
        const auto packageFilename = FPackageName::LongPackageNameToFilename(
            packageName, FPackageName::GetAssetPackageExtension());
        const auto packageDirectory = FPaths::GetPath(packageFilename);
        if (IFileManager::Get().FileExists(*packageFilename) ||
            IFileManager::Get().DirectoryExists(*packageDirectory))
        {
            AddError(TEXT("Refusing to overwrite an existing isolated persistence package."));
            return false;
        }
        IFileManager::Get().MakeDirectory(*packageDirectory, true);

        auto* asset = static_cast<ULibraSpawnableAsset*>(nullptr);
        ON_SCOPE_EXIT
        {
            if (asset)
            {
                auto packages = TArray<UPackage*>{Cast<UPackage>(asset->GetOutermost())};
                auto unloadError = FText{};
                UPackageTools::UnloadPackages(packages, unloadError, false);
            }
            IFileManager::Get().DeleteDirectory(*packageDirectory, false, true);
        };

        auto* package = CreatePackage(*packageName);
        if (!TestNotNull(TEXT("Isolated persistent asset package"), package))
        {
            return false;
        }
        auto canceled = false;
        asset = Cast<ULibraSpawnableAsset>(factory->FactoryCreateFile(
            ULibraSpawnableAsset::StaticClass(), package, assetName, RF_Public | RF_Standalone,
            sourcePath, nullptr, GWarn, canceled));
        if (!TestNotNull(TEXT("Factory imports into the isolated persistent package"), asset))
        {
            return false;
        }
        if (!TestTrue(TEXT("Imported binary is embedded"), TArray<uint8>(asset->GetPackageBytes()) == bytes) ||
            !TestNotNull(TEXT("Import metadata exists"), asset->AssetImportData.Get()))
        {
            return false;
        }
        asset->SourceProjectPath.FilePath = projectPath;

        auto keys = TArray<FLibraPackageResourceOverride>{};
        auto detail = FString{};
        if (!TestTrue(TEXT("Read embedded renderer resource keys"), asset->GetPackageResourceKeys(keys, detail)))
        {
            AddError(detail);
            return false;
        }
        for (auto requiredKind : requiredKinds)
        {
            if (!TestTrue(FString::Printf(TEXT("Compiler fixture contains a real %s resource key"), kindName(requiredKind)),
                keys.ContainsByPredicate([&](const auto& key) { return key.Kind == requiredKind; })))
            {
                return false;
            }
        }

        auto expectedOverrides = TArray<FLibraPackageResourceOverride>{};
        for (const auto& key : keys)
        {
            auto* replacement = assetForKind(key.Kind);
            if (!replacement)
            {
                continue;
            }
            if (!TestTrue(FString::Printf(TEXT("Accept authentic %s package key"), kindName(key.Kind)),
                asset->SetResourceOverride(key.Identity, key.Kind, replacement, detail)))
            {
                AddError(detail);
                return false;
            }
            auto expected = key;
            expected.Asset = replacement;
            expectedOverrides.Add(MoveTemp(expected));
        }
        if (!requiredKinds.IsEmpty() && !TestTrue(TEXT("Fixture accepts at least one typed asset override"),
            !expectedOverrides.IsEmpty()))
        {
            return false;
        }

        auto verifyOverrides = [&](const TCHAR* stage)
        {
            auto passed = true;
            for (const auto& expected : expectedOverrides)
            {
                passed &= TestTrue(FString::Printf(TEXT("%s preserves its %s asset reference"),
                    stage, kindName(expected.Kind)),
                    asset->FindResourceOverride(expected.Identity, expected.Kind) == expected.Asset.Get());
            }
            return passed;
        };
        for (const auto& expected : expectedOverrides)
        {
            const auto invalidIdentity = expected.Identity + TEXT(".__invalid_acceptance_key");
            if (!TestFalse(TEXT("Reject an unexported resource key"),
                asset->SetResourceOverride(invalidIdentity, expected.Kind, expected.Asset.Get(), detail)) ||
                !TestTrue(TEXT("Invalid-key rejection preserves the existing binding"),
                    asset->FindResourceOverride(expected.Identity, expected.Kind) == expected.Asset.Get()) ||
                !TestFalse(TEXT("Invalid-key rejection creates no binding"),
                    asset->FindResourceOverride(invalidIdentity, expected.Kind) != nullptr))
            {
                return false;
            }

            auto wrongKind = expected.Kind;
            auto* wrongAsset = static_cast<UObject*>(nullptr);
            for (auto candidate : { ELibraPackageResourceKind::Model, ELibraPackageResourceKind::Texture,
                                    ELibraPackageResourceKind::Material })
            {
                if (candidate == expected.Kind || keys.ContainsByPredicate([&](const auto& key)
                    { return key.Kind == candidate && key.Identity == expected.Identity; }))
                {
                    continue;
                }
                wrongKind = candidate;
                wrongAsset = assetForKind(candidate);
                break;
            }
            if (wrongAsset)
            {
                if (!TestFalse(TEXT("Reject a valid asset under the wrong resource kind"),
                    asset->SetResourceOverride(expected.Identity, wrongKind, wrongAsset, detail)) ||
                    !TestTrue(TEXT("Wrong-kind rejection preserves the previous binding"),
                        asset->FindResourceOverride(expected.Identity, expected.Kind) == expected.Asset.Get()) ||
                    !TestFalse(TEXT("Wrong-kind rejection creates no alternate binding"),
                        asset->FindResourceOverride(expected.Identity, wrongKind) != nullptr))
                {
                    return false;
                }
            }
        }

        auto invalid = bytes;
        invalid[0] = 0;
        if (!TestTrue(TEXT("Malformed source is written"), FFileHelper::SaveArrayToFile(invalid, *sourcePath)) ||
            !TestTrue(TEXT("Malformed source fails reimport"),
                factory->Reimport(asset) == EReimportResult::Failed) ||
            !TestTrue(TEXT("Failed reimport preserves binary"),
                TArray<uint8>(asset->GetPackageBytes()) == bytes) ||
            !verifyOverrides(TEXT("Failed reimport")))
        {
            return false;
        }

        IFileManager::Get().Delete(*sourcePath);
        if (!TestTrue(TEXT("Missing source fails reimport"),
                factory->Reimport(asset) == EReimportResult::Failed) ||
            !TestTrue(TEXT("Missing source preserves binary"),
                TArray<uint8>(asset->GetPackageBytes()) == bytes) ||
            !TestEqual(TEXT("Embedded asset is valid without the source"),
                asset->ValidatePackage(), ELibraRuntimeResult::Ok) ||
            !verifyOverrides(TEXT("Missing-source reimport")) ||
            !TestTrue(TEXT("Original source is restored"), FFileHelper::SaveArrayToFile(bytes, *sourcePath)))
        {
            return false;
        }

        auto saveAndReload = [&](const TCHAR* stage) -> bool
        {
            auto* currentPackage = Cast<UPackage>(asset->GetOutermost());
            if (!TestNotNull(TEXT("Asset has a persistent package outer"), currentPackage))
            {
                return false;
            }
            currentPackage->SetDirtyFlag(true);
            auto saveArgs = FSavePackageArgs{};
            saveArgs.TopLevelFlags = RF_Public | RF_Standalone;
            saveArgs.SaveFlags = SAVE_NoError;
            if (!TestTrue(FString::Printf(TEXT("Save isolated asset (%s)"), stage),
                UPackage::SavePackage(currentPackage, asset, *packageFilename, saveArgs)))
            {
                return false;
            }
            currentPackage->SetDirtyFlag(false);

            auto packagesToUnload = TArray<UPackage*>{currentPackage};
            auto unloadError = FText{};
            if (!TestTrue(FString::Printf(TEXT("Unload isolated package for a real reload (%s)"), stage),
                UPackageTools::UnloadPackages(packagesToUnload, unloadError, false)))
            {
                AddError(unloadError.ToString());
                return false;
            }
            asset = nullptr;
            auto* reloadedPackage = LoadPackage(nullptr, *packageName, LOAD_None);
            if (!TestNotNull(FString::Printf(TEXT("Reload isolated package (%s)"), stage), reloadedPackage))
            {
                return false;
            }
            asset = FindObject<ULibraSpawnableAsset>(reloadedPackage, TEXT("ReimportedSpawnable"));
            return TestNotNull(FString::Printf(TEXT("Reloaded asset is available (%s)"), stage), asset);
        };

        if (!saveAndReload(TEXT("initial")) ||
            !TestEqual(TEXT("Save/reload preserves package bytes"),
                TArray<uint8>(asset->GetPackageBytes()), bytes) ||
            !TestEqual(TEXT("Save/reload preserves the independent source project link"),
                asset->SourceProjectPath.FilePath, projectPath) ||
            !TestTrue(TEXT("Save/reload preserves the import source path"),
                asset->AssetImportData && FPaths::IsSamePath(asset->AssetImportData->GetFirstFilename(), sourcePath)) ||
            !verifyOverrides(TEXT("Save/reload")))
        {
            return false;
        }

        auto updated = bytes;
        // LFXP stores AutoDestroyWhenIdle immediately after its 24-byte header.
        updated[24] = updated[24] ? 0 : 1;
        if (!TestEqual(TEXT("Updated fixture is valid"), ULibraSpawnableAsset::ValidatePackageBuffer(updated),
            ELibraRuntimeResult::Ok) ||
            !TestTrue(TEXT("Source update is written"), FFileHelper::SaveArrayToFile(updated, *sourcePath)))
        {
            return false;
        }

        if (!TestTrue(TEXT("Editor reimport manager reloads from source"),
            FReimportManager::Instance()->Reimport(asset, false, false)) ||
            !TestTrue(TEXT("Reimport replaces the embedded binary"), TArray<uint8>(asset->GetPackageBytes()) == updated) ||
            !TestEqual(TEXT("Reimport preserves the independent .lfxproj link"),
                asset->SourceProjectPath.FilePath, projectPath) ||
            !TestTrue(TEXT("The binary is still the import source"),
                asset->AssetImportData && FPaths::IsSamePath(asset->AssetImportData->GetFirstFilename(), sourcePath)) ||
            !verifyOverrides(TEXT("Successful reimport")))
        {
            return false;
        }

        if (!saveAndReload(TEXT("after reimport")) ||
            !TestEqual(TEXT("Post-reimport reload preserves updated package bytes"),
                TArray<uint8>(asset->GetPackageBytes()), updated) ||
            !TestEqual(TEXT("Post-reimport reload preserves the source project link"),
                asset->SourceProjectPath.FilePath, projectPath) ||
            !TestTrue(TEXT("Post-reimport reload preserves the import source path"),
                asset->AssetImportData && FPaths::IsSamePath(asset->AssetImportData->GetFirstFilename(), sourcePath)) ||
            !verifyOverrides(TEXT("Post-reimport reload")))
        {
            return false;
        }

        const auto* model = keys.FindByPredicate([](const auto& key)
            { return key.Kind == ELibraPackageResourceKind::Model; });
        if (model)
        {
            if (!TestTrue(TEXT("Clearing a model override restores source lookup"),
                asset->SetResourceOverride(model->Identity, model->Kind, nullptr, detail) &&
                !asset->FindResourceOverride(model->Identity, model->Kind)))
            {
                return false;
            }
            for (const auto& expected : expectedOverrides)
            {
                if (expected.Kind != ELibraPackageResourceKind::Model &&
                    !TestTrue(TEXT("Clearing the model leaves other typed bindings intact"),
                        asset->FindResourceOverride(expected.Identity, expected.Kind) == expected.Asset.Get()))
                {
                    return false;
                }
            }
        }
        if (asset)
        {
            auto* persistedPackage = Cast<UPackage>(asset->GetOutermost());
            persistedPackage->SetDirtyFlag(false);
            auto packagesToUnload = TArray<UPackage*>{persistedPackage};
            auto unloadError = FText{};
            if (!TestTrue(TEXT("Isolated persisted asset is unloaded at test end"),
                UPackageTools::UnloadPackages(packagesToUnload, unloadError, false)))
            {
                AddError(unloadError.ToString());
                return false;
            }
            asset = nullptr;
        }
        return true;
    };

    if (!verifyFixture(fixture, requestedKinds))
    {
        return false;
    }
    return true;
}

#endif
