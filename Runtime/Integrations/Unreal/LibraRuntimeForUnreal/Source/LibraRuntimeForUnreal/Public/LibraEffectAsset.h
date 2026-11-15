#pragma once

#include "CoreMinimal.h"
#include "Engine/DataAsset.h"
#include "LibraRuntimeTypes.h"
#include "LibraEffectAsset.generated.h"

class FCompiledLibraProgram;
class UAssetImportData;

UCLASS(BlueprintType)
class LIBRARUNTIMEFORUNREAL_API ULibraEffectAsset : public UDataAsset
{
    GENERATED_BODY()

public:
    static constexpr int32 MaximumBytecodeBytes = 128 * 1024 * 1024;
    virtual ~ULibraEffectAsset() override;

    UFUNCTION(BlueprintPure, Category = "Libra Runtime")
    ELibraRuntimeResult ValidateBytecode() const;

    UFUNCTION(BlueprintPure, Category = "Libra Runtime")
    int32 GetNodeCount() const;

    ELibraRuntimeResult SetBytecode(TConstArrayView<uint8> bytes);
    static ELibraRuntimeResult ValidateBytecodeBuffer(TConstArrayView<uint8> bytes);
    TConstArrayView<uint8> GetBytecode() const { return Bytecode; }
    virtual void PostLoad() override;
#if WITH_EDITOR
    virtual void PostEditUndo() override;
    virtual void GetAssetRegistryTags(FAssetRegistryTagsContext context) const override;
#endif

#if WITH_EDITORONLY_DATA
    UPROPERTY(VisibleAnywhere, Instanced, Category = "Import Settings")
    TObjectPtr<UAssetImportData> AssetImportData;
#endif

private:
    friend class ULibraEffectComponent;
    TSharedPtr<const FCompiledLibraProgram, ESPMode::ThreadSafe> GetCompiledProgram() const;

    UPROPERTY()
    TArray<uint8> Bytecode;

    mutable TSharedPtr<const FCompiledLibraProgram, ESPMode::ThreadSafe> CompiledProgram;
};
