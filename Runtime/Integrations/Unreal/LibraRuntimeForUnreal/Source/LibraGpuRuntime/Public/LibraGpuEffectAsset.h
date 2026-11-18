#pragma once

#include "CoreMinimal.h"
#include "Engine/DataAsset.h"
#include "LibraGpuEffectAsset.generated.h"

class UAssetImportData;
class UTexture2D;

UENUM(BlueprintType)
enum class ELibraGpuExecutionMode : uint8
{
    GpuRegisterInterpreter
};

UENUM(BlueprintType)
enum class ELibraGpuBillboardBlend : uint8
{
    Alpha,
    Additive
};

USTRUCT()
struct FLibraGpuKernelPacket
{
    GENERATED_BODY()

    UPROPERTY()
    FString Id;

    UPROPERTY()
    bool bUpdate = false;

    UPROPERTY()
    TArray<uint8> Bytes;
};

USTRUCT()
struct FLibraGpuTextureBinding
{
    GENERATED_BODY()

    UPROPERTY()
    FString Id;

    UPROPERTY()
    TObjectPtr<UTexture2D> Texture;

    UPROPERTY()
    bool bBuiltinWhite = false;

    UPROPERTY()
    bool bFlowMap = false;
};

USTRUCT()
struct FLibraGpuFlowStep
{
    GENERATED_BODY()

    UPROPERTY()
    FString Kind;

    UPROPERTY()
    int32 Index = INDEX_NONE;

    UPROPERTY()
    FString Id;

    UPROPERTY()
    ELibraGpuBillboardBlend BillboardBlend = ELibraGpuBillboardBlend::Alpha;

    UPROPERTY()
    float SizeScale = 1;
};

UCLASS(BlueprintType)
class LIBRAGPURUNTIME_API ULibraGpuEffectAsset : public UDataAsset
{
    GENERATED_BODY()

public:
    static constexpr int32 MaximumBundleBytes = 16 * 1024 * 1024;
    static constexpr int32 MaximumRegisters = 256;
    static constexpr uint64 MaximumGpuBytes = 256ull * 1024 * 1024;

    // Import is transactional: no field changes unless the whole bundle validates.
    bool ImportBundle(const FString& json, FString& error);
    bool ValidateProgram(FString& error) const;
    static ULibraGpuEffectAsset* CreateInMemory(UObject* outer, const FString& bundleJson, FString& error);
#if WITH_EDITOR
    virtual EDataValidationResult IsDataValid(FDataValidationContext& context) const override;
#endif

    UFUNCTION(BlueprintPure, Category = "Libra GPU")
    int32 GetCapacity() const { return Capacity; }

    UFUNCTION(BlueprintPure, Category = "Libra GPU")
    ELibraGpuExecutionMode GetExecutionMode() const { return ELibraGpuExecutionMode::GpuRegisterInterpreter; }

    const TArray<FLibraGpuKernelPacket>& GetKernels() const { return Kernels; }
    const TArray<FLibraGpuFlowStep>& GetFlow() const { return Flow; }
    const TArray<FLibraGpuTextureBinding>& GetTextures() const { return Textures; }
    const FString& GetContentHash() const { return ContentHash; }
    const FString& GetSourceHash() const { return SourceHash; }

#if WITH_EDITORONLY_DATA
    UPROPERTY(VisibleAnywhere, Instanced, Category = "Import Settings")
    TObjectPtr<UAssetImportData> AssetImportData;
#endif

private:
    UPROPERTY()
    int32 Capacity = 0;

    UPROPERTY()
    FString CanonicalJson;

    UPROPERTY()
    FString OptimizedSourceExport;

    UPROPERTY()
    FString ContentHash;

    UPROPERTY()
    FString SourceHash;

    UPROPERTY()
    TArray<FLibraGpuKernelPacket> Kernels;

    UPROPERTY()
    TArray<FLibraGpuFlowStep> Flow;

    UPROPERTY()
    TArray<FLibraGpuTextureBinding> Textures;
};
