#pragma once

#include "CoreMinimal.h"
#include "Engine/DataAsset.h"
#include "LibraRuntimeTypes.h"
#include "LibraSpawnableAsset.generated.h"

class UAssetImportData;

UCLASS(BlueprintType)
class LIBRARUNTIMEFORUNREAL_API ULibraSpawnableAsset : public UDataAsset
{
    GENERATED_BODY()

public:
    static constexpr int32 MaximumPackageBytes = 128 * 1024 * 1024;

    UFUNCTION(BlueprintPure, Category = "Libra Runtime")
    ELibraRuntimeResult ValidatePackage() const;

    ELibraRuntimeResult SetPackageBytes(TConstArrayView<uint8> bytes);
    static ELibraRuntimeResult ValidatePackageBuffer(TConstArrayView<uint8> bytes);
    TConstArrayView<uint8> GetPackageBytes() const { return PackageBytes; }
    virtual void PostLoad() override;
#if WITH_EDITOR
    virtual void GetAssetRegistryTags(FAssetRegistryTagsContext context) const override;
#endif

#if WITH_EDITORONLY_DATA
    UPROPERTY(VisibleAnywhere, Instanced, Category = "Import Settings")
    TObjectPtr<UAssetImportData> AssetImportData;
#endif

private:
    UPROPERTY()
    TArray<uint8> PackageBytes;
};
