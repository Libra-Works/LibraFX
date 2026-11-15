#pragma once

#include "CoreMinimal.h"
#include "EditorReimportHandler.h"
#include "Factories/Factory.h"
#include "LibraGpuEffectFactory.generated.h"

UCLASS()
class ULibraGpuEffectFactory : public UFactory, public FReimportHandler
{
    GENERATED_BODY()

public:
    ULibraGpuEffectFactory();
    bool FactoryCanImport(const FString& filename) override;
    UObject* FactoryCreateFile(UClass* inClass, UObject* inParent, FName inName,
        EObjectFlags flags, const FString& filename, const TCHAR* parms,
        FFeedbackContext* warn, bool& outOperationCanceled) override;
    bool CanReimport(UObject* object, TArray<FString>& outFilenames) override;
    void SetReimportPaths(UObject* object, const TArray<FString>& newPaths) override;
    EReimportResult::Type Reimport(UObject* object) override;
};
