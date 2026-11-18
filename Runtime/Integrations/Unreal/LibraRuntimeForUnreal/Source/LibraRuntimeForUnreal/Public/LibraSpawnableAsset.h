#pragma once

#include "CoreMinimal.h"
#include "Engine/DataAsset.h"
#include "LibraRuntimeTypes.h"
#include "UObject/SoftObjectPath.h"
#include "LibraSpawnableAsset.generated.h"

class UAssetImportData;

UENUM()
enum class ELibraPackageResourceKind : uint8 { Model, Texture, Material };

// Identity is the exact key emitted into this package's engine delivery, not
// a portable Libera resource ID. Kind is part of the key.
USTRUCT()
struct FLibraPackageResourceOverride
{
    GENERATED_BODY()

    UPROPERTY()
    FString Identity;

    UPROPERTY()
    ELibraPackageResourceKind Kind = ELibraPackageResourceKind::Model;

    UPROPERTY()
    TObjectPtr<UObject> Asset;
};

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
    UObject* FindResourceOverride(const FString& identity, ELibraPackageResourceKind kind) const;
    virtual void PostLoad() override;
#if WITH_EDITOR
    bool GetPackageResourceKeys(TArray<FLibraPackageResourceOverride>& keys, FString& error) const;
    TConstArrayView<FLibraPackageResourceOverride> GetResourceOverrides() const { return ResourceOverrides; }
    static UClass* GetResourceClass(ELibraPackageResourceKind kind);
    // Validate before the transactional mutation. Null clears an override,
    // including an orphan left by reimport. Never modifies the embedded bytes.
    bool SetResourceOverride(const FString& identity, ELibraPackageResourceKind kind, UObject* asset, FString& error);
    virtual void GetAssetRegistryTags(FAssetRegistryTagsContext context) const override;
#endif

#if WITH_EDITORONLY_DATA
    UPROPERTY(VisibleAnywhere, Instanced, Category = "Import Settings")
    TObjectPtr<UAssetImportData> AssetImportData;

    UPROPERTY(EditAnywhere, Category = "LIBERA FX Source", meta = (FilePathFilter = "lfxproj"))
    FFilePath SourceProjectPath;
#endif

private:
    UPROPERTY()
    TArray<uint8> PackageBytes;

    // Hard references keep editor-selected assets in the cook and follow UE renames.
    UPROPERTY()
    TArray<FLibraPackageResourceOverride> ResourceOverrides;
};
