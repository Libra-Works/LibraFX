#pragma once

#include "CoreMinimal.h"
#include "Factories/Factory.h"
#include "EditorReimportHandler.h"
#include "LibraSpawnableFactory.generated.h"

class ULibraSpawnableAsset;

UCLASS()
class ULibraSpawnableFactory : public UFactory, public FReimportHandler
{
    GENERATED_BODY()

public:
    ULibraSpawnableFactory();
    virtual bool FactoryCanImport(const FString& filename) override;
    virtual UObject* FactoryCreateFile(UClass* inClass, UObject* inParent, FName inName,
        EObjectFlags flags, const FString& filename, const TCHAR* parms,
        FFeedbackContext* warn, bool& outOperationCanceled) override;
    virtual bool CanReimport(UObject* object, TArray<FString>& outFilenames) override;
    virtual void SetReimportPaths(UObject* object, const TArray<FString>& newPaths) override;
    virtual EReimportResult::Type Reimport(UObject* object) override;

private:
    static bool ReadPackage(const FString& filename, TArray<uint8>& bytes);
    static void UpdateImportData(ULibraSpawnableAsset& asset, const FString& filename);
};
