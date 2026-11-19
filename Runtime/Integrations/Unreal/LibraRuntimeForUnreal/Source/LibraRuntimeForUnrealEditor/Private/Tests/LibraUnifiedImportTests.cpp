#if WITH_DEV_AUTOMATION_TESTS

#include "LibraEffectAsset.h"
#include "LibraEffectFactory.h"
#include "LibraSpawnableAsset.h"
#include "LibraSpawnableFactory.h"
#include "HAL/FileManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraUnifiedImportTest,
    "Libra.Editor.Import.LegacyRawRouting",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraUnifiedImportTest::RunTest(const FString&)
{
    // Same scalar v1 layout used by the existing immutable-bytecode asset test.
    auto bytes = TArray<uint8>{};
    bytes.SetNumZeroed(56);
    FMemory::Memcpy(bytes.GetData(), "LFXB", 4);
    bytes[4] = 1;
    bytes[6] = 1;
    bytes[8] = 1;
    bytes[27] = 1;
    bytes[28] = 1;
    for (auto index = 32; index < 40; ++index)
    {
        bytes[index] = 255;
    }

    auto directory = FPaths::ProjectSavedDir();
    IFileManager::Get().MakeDirectory(*directory, true);
    auto source = FPaths::CreateTempFilename(*directory, TEXT("LibraRawImport"), TEXT(".lfxb"));
    ON_SCOPE_EXIT { IFileManager::Get().Delete(*source); };
    if (!TestTrue(TEXT("Write isolated raw source"), FFileHelper::SaveArrayToFile(bytes, *source)))
    {
        return false;
    }
    auto rawFactory = NewObject<ULibraEffectFactory>();
    auto packageFactory = NewObject<ULibraSpawnableFactory>();
    TestTrue(TEXT("Raw factory accepts validated legacy bytecode"), rawFactory->FactoryCanImport(source));
    TestFalse(TEXT("Package factory does not compete for raw .lfxb"), packageFactory->FactoryCanImport(source));
    auto canceled = false;
    auto asset = Cast<ULibraEffectAsset>(rawFactory->FactoryCreateFile(
        ULibraEffectAsset::StaticClass(), GetTransientPackage(), NAME_None, RF_Transient,
        source, nullptr, GWarn, canceled));
    if (!TestNotNull(TEXT("Raw source creates the legacy asset type"), asset))
    {
        return false;
    }
    TestEqual(TEXT("Raw import embeds the exact bytes"), TArray<uint8>(asset->GetBytecode()), bytes);
    AddExpectedError(TEXT("Cannot import invalid or unsupported LIBERA FX package"),
        EAutomationExpectedErrorFlags::Contains, 1);
    TestNull(TEXT("Direct package factory call rejects raw .lfxb"), packageFactory->FactoryCreateFile(
        ULibraSpawnableAsset::StaticClass(), GetTransientPackage(), NAME_None, RF_Transient,
        source, nullptr, GWarn, canceled));

    // Correct magic alone is not validation.
    bytes.SetNum(12);
    if (!TestTrue(TEXT("Write truncated raw source"), FFileHelper::SaveArrayToFile(bytes, *source)))
    {
        return false;
    }
    TestFalse(TEXT("Raw factory rejects a truncated LFXB"), rawFactory->FactoryCanImport(source));
    TestFalse(TEXT("Package factory rejects a truncated LFXB"), packageFactory->FactoryCanImport(source));
    AddExpectedError(TEXT("Cannot import invalid or unsupported LIBERA FX bytecode"),
        EAutomationExpectedErrorFlags::Contains, 1);
    TestNull(TEXT("Direct raw factory call cannot bypass validation"), rawFactory->FactoryCreateFile(
        ULibraEffectAsset::StaticClass(), GetTransientPackage(), NAME_None, RF_Transient,
        source, nullptr, GWarn, canceled));
    TestTrue(TEXT("Failed raw reimport leaves the existing asset intact"),
        rawFactory->Reimport(asset) == EReimportResult::Failed &&
        asset->ValidateBytecode() == ELibraRuntimeResult::Ok);
    return true;
}

#endif
