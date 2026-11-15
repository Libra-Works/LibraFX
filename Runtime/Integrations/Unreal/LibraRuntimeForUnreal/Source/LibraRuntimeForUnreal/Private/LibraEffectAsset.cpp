#include "LibraEffectAsset.h"

#include "LibraNativeState.h"
#if WITH_EDITORONLY_DATA
#include "EditorFramework/AssetImportData.h"
#endif
#if WITH_EDITOR
#include "UObject/AssetRegistryTagsContext.h"
#endif

ULibraEffectAsset::~ULibraEffectAsset() = default;

auto ULibraEffectAsset::GetCompiledProgram() const -> TSharedPtr<const FCompiledLibraProgram, ESPMode::ThreadSafe>
{
    check(IsInGameThread());
    if (!CompiledProgram || CompiledProgram->registry != LibraNativeNodeSnapshot())
        CompiledProgram = MakeShared<FCompiledLibraProgram, ESPMode::ThreadSafe>(TConstArrayView<uint8>(Bytecode));
    return CompiledProgram;
}

auto ULibraEffectAsset::ValidateBytecode() const -> ELibraRuntimeResult
{
    return ToLibraResult(GetCompiledProgram()->result);
}

auto ULibraEffectAsset::GetNodeCount() const -> int32
{
    auto compiled = GetCompiledProgram();
    return compiled->result == liberafx::Result::Ok ? static_cast<int32>(compiled->program.nodeCount()) : 0;
}

auto ULibraEffectAsset::SetBytecode(TConstArrayView<uint8> bytes) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    if (bytes.Num() <= 0 || bytes.Num() > MaximumBytecodeBytes) return ELibraRuntimeResult::InvalidBinary;
    auto candidate = MakeShared<FCompiledLibraProgram, ESPMode::ThreadSafe>(bytes);
    if (candidate->result != liberafx::Result::Ok) return ToLibraResult(candidate->result);
    Bytecode = candidate->bytecode;
    CompiledProgram = MoveTemp(candidate);
    return ELibraRuntimeResult::Ok;
}

auto ULibraEffectAsset::ValidateBytecodeBuffer(TConstArrayView<uint8> bytes) -> ELibraRuntimeResult
{
    check(IsInGameThread());
    auto candidate = MakeShared<FCompiledLibraProgram, ESPMode::ThreadSafe>(bytes);
    return ToLibraResult(candidate->result);
}

#if WITH_EDITOR
void ULibraEffectAsset::PostEditUndo()
{
    Super::PostEditUndo();
    CompiledProgram.Reset();
}

void ULibraEffectAsset::GetAssetRegistryTags(FAssetRegistryTagsContext context) const
{
    Super::GetAssetRegistryTags(context);
#if WITH_EDITORONLY_DATA
    if (AssetImportData)
        context.AddTag(FAssetRegistryTag(SourceFileTagName(), AssetImportData->GetSourceData().ToJson(),
            FAssetRegistryTag::TT_Hidden));
#endif
}
#endif

void ULibraEffectAsset::PostLoad()
{
    Super::PostLoad();
    CompiledProgram.Reset();
#if WITH_EDITORONLY_DATA
    if (!AssetImportData) AssetImportData = NewObject<UAssetImportData>(this, TEXT("AssetImportData"));
#endif
}
