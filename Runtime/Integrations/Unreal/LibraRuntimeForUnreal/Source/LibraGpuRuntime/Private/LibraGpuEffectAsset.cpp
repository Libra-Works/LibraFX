#include "LibraGpuEffectAsset.h"

#include "Dom/JsonObject.h"
#include "Engine/Texture2D.h"
#include "Misc/Base64.h"
#include "Misc/DataValidation.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

THIRD_PARTY_INCLUDES_START
#include <openssl/sha.h>
THIRD_PARTY_INCLUDES_END

namespace
{
constexpr uint32 Missing = MAX_uint32;

uint32 Word(const TArray<uint8>& bytes, int32 offset)
{
    return uint32(bytes[offset]) | uint32(bytes[offset + 1]) << 8 |
        uint32(bytes[offset + 2]) << 16 | uint32(bytes[offset + 3]) << 24;
}

int32 Components(uint32 type)
{
    return type >= 4 && type <= 6 ? int32(type - 2) : 1;
}

bool IsFloat(uint32 type) { return type == 0 || (type >= 4 && type <= 6); }
bool IsNumeric(uint32 type) { return IsFloat(type) || type == 1 || type == 2; }

FString LibraGpuSha256(const FString& text)
{
    auto utf8 = FTCHARToUTF8(*text);
    uint8 digest[SHA256_DIGEST_LENGTH];
    SHA256(reinterpret_cast<const uint8*>(utf8.Get()), utf8.Length(), digest);
    return BytesToHex(digest, UE_ARRAY_COUNT(digest)).ToLower();
}

bool ValidatePacket(const FLibraGpuKernelPacket& packet, int32 capacity,
    const TArray<bool>& flowMapResources, FString& error)
{
    auto fail = [&error](const TCHAR* message) { error = message; return false; };
    const auto& bytes = packet.Bytes;
    if (bytes.Num() < 64 || FMemory::Memcmp(bytes.GetData(), "LGPK", 4) != 0 ||
        Word(bytes, 4) != 1 || Word(bytes, 8) != 64 || Word(bytes, 24) != 80 ||
        Word(bytes, 28) != 48 || Word(bytes, 20) != uint32(packet.bUpdate) ||
        Word(bytes, 48) > 1 || Word(bytes, 52) || Word(bytes, 56) || Word(bytes, 60))
        return fail(TEXT("Invalid GPU packet header."));
    auto count = Word(bytes, 12);
    auto registers = Word(bytes, 16);
    if (registers > ULibraGpuEffectAsset::MaximumRegisters || count < registers ||
        count > registers + 9 || uint64(count) * 64 + 64 != uint64(bytes.Num()) ||
        uint64(capacity) * (80 + uint64(registers) * 16) > ULibraGpuEffectAsset::MaximumGpuBytes)
        return fail(TEXT("GPU packet or scratch memory limit exceeded."));

    static constexpr uint32 attrTypes[] = {5, 5, 6, 4, 0, 0, 0, 3, 2};
    static constexpr uint32 uniformTypes[] = {0, 0, 5, 6, 6, 6, 6};
    TArray<uint32> types;
    types.Reserve(registers);
    uint32 writes = 0;
    for (uint32 i = 0; i < count; ++i)
    {
        auto base = int32(64 + i * 64);
        auto op = Word(bytes, base);
        auto dst = Word(bytes, base + 4);
        uint32 args[] = {Word(bytes, base + 8), Word(bytes, base + 12),
            Word(bytes, base + 16), Word(bytes, base + 28)};
        auto d = Word(bytes, base + 28);
        auto type = Word(bytes, base + 20);
        auto aux = Word(bytes, base + 24);
        if (op > 43 || type > 6) return fail(TEXT("Unknown GPU opcode or type."));
        auto arity = 0;
        if (op >= 3 && op <= 6) arity = 2;
        else if (op == 7 || op == 8 || op == 12 || op == 13 || op == 16 || op == 17 || op == 18) arity = 1;
        else if (op == 9 || op == 15) arity = 3;
        else if (op == 10 || op == 11) arity = 2;
        else if (op == 14) arity = Components(type);
        else if (op == 19 || (op >= 20 && op <= 23) || op == 25 ||
            (op >= 27 && op <= 29) || op == 36) arity = 1;
        else if (op == 24 || op == 26 || (op >= 33 && op <= 35) || op == 37) arity = 2;
        else if (op >= 30 && op <= 32) arity = 3;
        else if (op >= 38 && op <= 40) arity = 4;
        else if (op == 41) arity = 1;
        else if (op == 42 || op == 43) arity = 2;
        for (auto j = 0; j < 4; ++j)
            if ((j < arity && args[j] >= uint32(types.Num())) ||
                (j >= arity && args[j] != Missing && !(op == 41 && type == 5 && j == 3)))
                return fail(TEXT("GPU packet has an invalid or forward register reference."));
        if ((i < registers && (op == 18 || dst != i)) ||
            (i >= registers && (op != 18 || dst != Missing)))
            return fail(TEXT("GPU packet must contain SSA instructions followed by stores."));

        auto valid = false;
        if (op == 0)
        {
            valid = aux == 0;
            if (IsFloat(type))
            {
                for (auto j = 0; j < Components(type); ++j)
                {
                    auto bits = Word(bytes, base + 32 + j * 4);
                    valid &= (bits & 0x7f800000u) != 0x7f800000u;
                }
            }
            else if (type == 3) valid &= Word(bytes, base + 48) <= 1;
        }
        else if (op == 1 || op == 18)
        {
            valid = aux < UE_ARRAY_COUNT(attrTypes) && type == attrTypes[aux];
            if (op == 18 && valid)
            {
                valid = types[args[0]] == type && (writes & (1u << aux)) == 0;
                writes |= 1u << aux;
            }
        }
        else if (op == 2) valid = aux < UE_ARRAY_COUNT(uniformTypes) && type == uniformTypes[aux];
        else if ((op >= 3 && op <= 6) || (op >= 9 && op <= 11) || op == 24 ||
            op == 26 || op == 30 || op == 31)
        {
            valid = aux == 0 && ((op == 26 || op == 30 || op == 31) ? IsFloat(type) : IsNumeric(type));
            auto exact = false;
            for (auto j = 0; j < arity; ++j)
            {
                auto input = types[args[j]];
                exact |= input == type;
                valid &= input == type || (input == 0 && IsFloat(type));
            }
            valid &= exact;
        }
        else if (op == 7 || op == 8) valid = aux == 0 && IsFloat(type) && types[args[0]] == type;
        else if (op == 12) valid = aux == 0 && type >= 4 && type <= 6 && types[args[0]] == type;
        else if (op == 13) valid = aux == 0 && type == 0 && types[args[0]] >= 4 && types[args[0]] <= 6;
        else if (op == 14)
        {
            valid = aux == 0 && type >= 4 && type <= 6;
            for (auto j = 0; j < arity; ++j) valid &= types[args[j]] == 0;
        }
        else if (op == 15) valid = aux == 0 && types[args[0]] == 3 && types[args[1]] == type && types[args[2]] == type;
        else if (op == 16) valid = aux == 0 && type == 0 && types[args[0]] == 2;
        else if (op == 17) valid = type == 0 && types[args[0]] >= 4 && types[args[0]] <= 6 && aux < uint32(Components(types[args[0]]));
        else if (op == 19 || op == 25) valid = aux == 0 && IsNumeric(type) && types[args[0]] == type;
        else if ((op >= 20 && op <= 23) || (op >= 27 && op <= 29) || op == 36)
            valid = aux == 0 && IsFloat(type) && types[args[0]] == type;
        else if (op == 32) valid = aux == 0 && IsFloat(type) && types[args[0]] == type &&
            types[args[1]] == 4 && types[args[2]] == 4;
        else if (op == 33) valid = aux == 0 && type == 0 && types[args[0]] == types[args[1]] &&
            types[args[0]] >= 4 && types[args[0]] <= 6;
        else if (op == 34) valid = aux == 0 && type == 5 && types[args[0]] == 5 && types[args[1]] == 5;
        else if (op == 35) valid = aux == 0 && type == 0 && types[args[0]] == types[args[1]] && IsFloat(types[args[0]]);
        else if (op == 37) valid = aux == 0 && type == 0 && types[args[0]] == 5 && types[args[1]] == 2;
        else if (op == 41)
        {
            valid = (type == 4 || type == 5) && types[args[0]] == type;
            valid &= aux < uint32(flowMapResources.Num());
            if (valid) valid &= flowMapResources[int32(aux)];
            if (type == 4)
                valid &= d == Missing;
            else
            {
                auto axisU = d & 3u;
                auto axisV = (d >> 2) & 3u;
                float minimumU, minimumV, rangeU, rangeV;
                auto bitsU = Word(bytes, base + 32);
                auto bitsV = Word(bytes, base + 36);
                auto bitsRangeU = Word(bytes, base + 40);
                auto bitsRangeV = Word(bytes, base + 44);
                FMemory::Memcpy(&minimumU, &bitsU, sizeof(minimumU));
                FMemory::Memcpy(&minimumV, &bitsV, sizeof(minimumV));
                FMemory::Memcpy(&rangeU, &bitsRangeU, sizeof(rangeU));
                FMemory::Memcpy(&rangeV, &bitsRangeV, sizeof(rangeV));
                valid &= (d & ~0x10fu) == 0 && axisU <= 2 && axisV <= 2 && axisU != axisV &&
                    FMath::IsFinite(minimumU) && FMath::IsFinite(minimumV) &&
                    FMath::IsFinite(rangeU) && FMath::IsFinite(rangeV) && rangeU > 0 && rangeV > 0;
            }
        }
        else if (op == 42 || op == 43)
        {
            valid = aux == 0 && type == 3 && types[args[0]] == 0 && types[args[1]] == 0;
        }
        else if (op >= 38 && op <= 40)
        {
            valid = aux == 0 && type == (op == 40 ? 5u : 0u) && types[args[0]] == 5 &&
                types[args[1]] == 2 && types[args[2]] == 1 && types[args[3]] == 0;
            for (auto j = 2; j < 4 && valid; ++j)
            {
                auto parameter = int32(64 + args[j] * 64);
                if (Word(bytes, parameter) != 0) continue;
                if (j == 2)
                {
                    auto octaves = int32(Word(bytes, parameter + 48));
                    valid &= octaves >= 1 && octaves <= 8;
                }
                else
                {
                    auto bits = Word(bytes, parameter + 32);
                    float gain;
                    FMemory::Memcpy(&gain, &bits, sizeof(gain));
                    valid &= gain >= 0 && gain <= 1;
                }
            }
        }
        if (!valid) return fail(TEXT("GPU packet has an invalid operation, type, or payload."));
        for (auto j = 0; j < 4; ++j)
        {
            auto floatBits = Word(bytes, base + 32 + j * 4);
            auto integerBits = Word(bytes, base + 48 + j * 4);
            if ((op != 0 && op != 41 && (floatBits || integerBits)) ||
                (op == 41 && ((j == 0 ? integerBits > 2 : integerBits != 0) || (type == 4 && floatBits))) ||
                (op == 0 && IsFloat(type) && (integerBits || (j >= Components(type) && floatBits))) ||
                (op == 0 && !IsFloat(type) && (floatBits || (j > 0 && integerBits))))
                return fail(TEXT("GPU packet has nonzero inactive payload fields."));
        }
        if (i < registers) types.Add(type);
    }
    if (Word(bytes, 48) != uint32(packet.bUpdate && !(writes & (1u << 5))))
        return fail(TEXT("GPU packet default age flag disagrees with stores."));
    return true;
}

bool NameValid(const FString& value) { return !value.IsEmpty() && value.Len() <= 128; }

bool LibraGpuBufferBudget(const TArray<FLibraGpuKernelPacket>& kernels, int32 capacity, FString& error)
{
    uint32 registers = 0;
    uint64 programBytes = 0;
    for (const auto& kernel : kernels)
    {
        registers = FMath::Max(registers, Word(kernel.Bytes, 16));
        programBytes += uint64(kernel.Bytes.Num());
    }
    if (uint64(capacity) * (80 + uint64(registers) * 16) + programBytes > ULibraGpuEffectAsset::MaximumGpuBytes)
    {
        error = TEXT("Combined GPU particle, register, and instruction buffer budget exceeded.");
        return false;
    }
    return true;
}

bool BoundedJsonDepth(const FString& text)
{
    auto depth = 0;
    auto quoted = false;
    auto escaped = false;
    for (auto c : text)
    {
        if (quoted)
        {
            if (escaped) escaped = false;
            else if (c == TEXT('\\')) escaped = true;
            else if (c == TEXT('"')) quoted = false;
        }
        else if (c == TEXT('"')) quoted = true;
        else if (c == TEXT('{') || c == TEXT('[')) { if (++depth > 32) return false; }
        else if (c == TEXT('}') || c == TEXT(']')) { if (--depth < 0) return false; }
    }
    return !quoted && depth == 0;
}
}

bool ULibraGpuEffectAsset::ValidateProgram(FString& error) const
{
    if (Capacity <= 0 || uint64(Capacity) * 80 > MaximumGpuBytes || Kernels.Num() > 64 ||
        Flow.Num() > 128 || Textures.Num() > 64)
    {
        error = TEXT("Invalid GPU program limits.");
        return false;
    }
    if (ContentHash != LibraGpuSha256(CanonicalJson) || SourceHash != LibraGpuSha256(OptimizedSourceExport))
    {
        error = TEXT("GPU program source or IR hash mismatch.");
        return false;
    }
    TArray<bool> flowMapResources;
    for (const auto& texture : Textures)
    {
        FGuid producer;
        const auto generatedOutputValid = texture.bFlowMap
            ? ((texture.GeneratedOutputId == TEXT("VelocityOutput") && texture.FlowEncoding == 1) || (texture.GeneratedOutputId == TEXT("DensityOutput") && texture.FlowEncoding == 2))
            : (texture.GeneratedOutputId == TEXT("DyeOutput") || texture.GeneratedOutputId == TEXT("ColorOutput") || texture.GeneratedOutputId == TEXT("FieldOutput"));
        if ((texture.IsGenerated() && (texture.bBuiltinWhite || texture.Texture ||
                !FGuid::Parse(texture.GeneratedProducerNodeId, producer) || !producer.IsValid() || !generatedOutputValid)) ||
            (!texture.IsGenerated() && ((texture.bBuiltinWhite && texture.Texture) || (!texture.bBuiltinWhite && !texture.Texture))))
        {
            error = TEXT("Missing GPU texture dependency.");
            return false;
        }
        flowMapResources.Add(texture.bFlowMap);
    }
    for (const auto& packet : Kernels)
        if (!ValidatePacket(packet, Capacity, flowMapResources, error)) return false;
    if (!LibraGpuBufferBudget(Kernels, Capacity, error)) return false;
    auto seenSpawn = false;
    auto seenUpdate = false;
    auto seenRender = false;
    for (const auto& step : Flow)
    {
        if (step.Kind == TEXT("Spawn") || step.Kind == TEXT("Update"))
        {
            auto update = step.Kind == TEXT("Update");
            if (!Kernels.IsValidIndex(step.Index) || Kernels[step.Index].bUpdate != update ||
                seenRender || (update ? seenUpdate : seenSpawn) || (!update && seenUpdate))
            {
                error = TEXT("Invalid GPU dispatch flow.");
                return false;
            }
            if (update) seenUpdate = true; else seenSpawn = true;
        }
        else if (step.Kind == TEXT("RenderBillboard"))
        {
            if (!Textures.IsValidIndex(step.Index) ||
                (!Textures[step.Index].bBuiltinWhite && !Textures[step.Index].Texture && !Textures[step.Index].IsGenerated()) ||
                (step.BillboardBlend != ELibraGpuBillboardBlend::Alpha && step.BillboardBlend != ELibraGpuBillboardBlend::Additive) ||
                !FMath::IsFinite(step.SizeScale) || step.SizeScale <= 0 || step.SizeScale > 10000)
            {
                error = TEXT("Invalid GPU billboard texture, blend mode, or sizeScale.");
                return false;
            }
            seenRender = true;
        }
        else
        {
            error = TEXT("Unsupported GPU flow step.");
            return false;
        }
    }
    return true;
}

#if WITH_EDITOR
EDataValidationResult ULibraGpuEffectAsset::IsDataValid(FDataValidationContext& context) const
{
    FString error;
    if (!ValidateProgram(error))
    {
        context.AddError(FText::FromString(error));
        return EDataValidationResult::Invalid;
    }
    return EDataValidationResult::Valid;
}
#endif

ULibraGpuEffectAsset* ULibraGpuEffectAsset::CreateInMemory(UObject* outer, const FString& bundleJson, FString& error)
{
    if (!IsInGameThread() || (outer && !IsValid(outer)))
    {
        error = TEXT("GPU asset creation requires a valid outer on the game thread.");
        return nullptr;
    }
    auto asset = NewObject<ULibraGpuEffectAsset>(outer ? outer : GetTransientPackage());
    if (!asset->ImportBundle(bundleJson, error))
    {
        asset->MarkAsGarbage();
        return nullptr;
    }
    return asset;
}

bool ULibraGpuEffectAsset::ImportBundle(const FString& json, FString& error)
{
    if (!IsInGameThread() || json.Len() > MaximumBundleBytes || !BoundedJsonDepth(json))
    {
        error = TEXT("GPU bundle is too large or import is not on the game thread.");
        return false;
    }
    TSharedPtr<FJsonObject> root;
    auto reader = TJsonReaderFactory<>::Create(json);
    if (!FJsonSerializer::Deserialize(reader, root) || !root.IsValid())
    {
        error = TEXT("Invalid GPU bundle JSON.");
        return false;
    }
    double version = 0;
    bool executed = true;
    FString purpose, canonical, source;
    const TSharedPtr<FJsonObject>* metadata = nullptr;
    const TArray<TSharedPtr<FJsonValue>> *kernelJson = nullptr, *textureJson = nullptr, *flowJson = nullptr, *resourcesJson = nullptr;
    auto delivery = root->HasField(TEXT("programSequence"));
    TArray<TSharedPtr<FJsonValue>> deliveryTextures;
    if ((!delivery && (!root->TryGetNumberField(TEXT("version"), version) || version != 1 ||
        !root->TryGetStringField(TEXT("purpose"), purpose) || purpose != TEXT("SourceExport") ||
        !root->TryGetBoolField(TEXT("runtimeExecuted"), executed) || executed)) ||
        !root->TryGetStringField(TEXT("canonicalJson"), canonical) || canonical.IsEmpty() ||
        !root->TryGetStringField(delivery ? TEXT("shaderSource") : TEXT("usf"), source) || source.IsEmpty() ||
        !root->TryGetObjectField(TEXT("metadata"), metadata) ||
        !root->TryGetArrayField(delivery ? TEXT("registerKernels") : TEXT("kernels"), kernelJson) ||
        (!delivery && !root->TryGetArrayField(TEXT("textures"), textureJson)))
    {
        error = TEXT("Invalid GPU bundle envelope.");
        return false;
    }
    if (delivery)
    {
        const TSharedPtr<FJsonObject>* paths = nullptr;
        if (!root->TryGetObjectField(TEXT("resourceAssetPaths"), paths))
        {
            error = TEXT("Missing frontend GPU texture bindings.");
            return false;
        }
        for (const auto& pair : (*paths)->Values)
        {
            FString path;
            if (!pair.Value->TryGetString(path)) { error = TEXT("Invalid frontend GPU texture path."); return false; }
            auto binding = MakeShared<FJsonObject>();
            binding->SetStringField(TEXT("id"), pair.Key);
            binding->SetStringField(TEXT("objectPath"), path);
            deliveryTextures.Add(MakeShared<FJsonValueObject>(binding));
        }
        if (root->HasField(TEXT("generatedResources")))
        {
            const TSharedPtr<FJsonObject>* generated = nullptr;
            if (!root->TryGetObjectField(TEXT("generatedResources"), generated) || (*generated)->Values.Num() > 64)
            {
                error = TEXT("Invalid generated GPU resource bindings.");
                return false;
            }
            for (const auto& pair : (*generated)->Values)
            {
                const auto reference = pair.Value->AsObject();
                FString producer, output;
                FGuid id;
                if (!reference || reference->Values.Num() != 2 || (*paths)->HasField(pair.Key) ||
                    !reference->TryGetStringField(TEXT("producerNodeId"), producer) || !FGuid::Parse(producer, id) || !id.IsValid() ||
                    !reference->TryGetStringField(TEXT("outputId"), output) ||
                    (output != TEXT("DyeOutput") && output != TEXT("ColorOutput") && output != TEXT("FieldOutput") && output != TEXT("VelocityOutput") && output != TEXT("DensityOutput")))
                {
                    error = TEXT("Invalid or conflicting generated GPU resource reference.");
                    return false;
                }
                auto binding = MakeShared<FJsonObject>();
                binding->SetStringField(TEXT("id"), pair.Key);
                binding->SetStringField(TEXT("producerNodeId"), producer);
                binding->SetStringField(TEXT("outputId"), output);
                deliveryTextures.Add(MakeShared<FJsonValueObject>(binding));
            }
        }
        textureJson = &deliveryTextures;
    }
    double capacityNumber = 0, stride = 0, uniformSize = 0;
    FString hash, sourceHash, format, metadataPurpose;
    bool metadataExecuted = true;
    if (!(*metadata)->TryGetNumberField(TEXT("version"), version) || (version != 1 && version != 2 && version != 3) ||
        !(*metadata)->TryGetNumberField(TEXT("capacity"), capacityNumber) ||
        capacityNumber < 1 || capacityNumber > double(MAX_int32) || FMath::FloorToDouble(capacityNumber) != capacityNumber ||
        !(*metadata)->TryGetNumberField(TEXT("particleStride"), stride) || stride != 80 ||
        !(*metadata)->TryGetNumberField(TEXT("uniformBufferBytes"), uniformSize) || uniformSize != 48 ||
        !(*metadata)->TryGetStringField(TEXT("contentHash"), hash) || hash.Len() != 64 ||
        !(*metadata)->TryGetStringField(TEXT("sourceHash"), sourceHash) || sourceHash.Len() != 64 ||
        !(*metadata)->TryGetStringField(TEXT("purpose"), metadataPurpose) || metadataPurpose != TEXT("SourceExport") ||
        !(*metadata)->TryGetBoolField(TEXT("runtimeExecuted"), metadataExecuted) || metadataExecuted ||
        !(*metadata)->TryGetStringField(TEXT("format"), format) || format != TEXT("Usf") ||
        !(*metadata)->TryGetArrayField(TEXT("flow"), flowJson) ||
        !(*metadata)->TryGetArrayField(TEXT("resources"), resourcesJson) ||
        kernelJson->Num() > 64 || textureJson->Num() > 64 || flowJson->Num() > 128 ||
        resourcesJson->Num() > 64)
    {
        error = TEXT("Invalid GPU bundle metadata.");
        return false;
    }
    if (resourcesJson->Num() != textureJson->Num())
    {
        TSet<FString> declaredIds, boundIds;
        auto collectIds = [](const TArray<TSharedPtr<FJsonValue>>& values, TSet<FString>& ids)
        {
            for (const auto& value : values)
            {
                auto object = value->AsObject();
                FString id;
                if (object && object->TryGetStringField(TEXT("id"), id) && NameValid(id)) ids.Add(id);
            }
        };
        collectIds(*resourcesJson, declaredIds);
        collectIds(*textureJson, boundIds);
        for (const auto& id : declaredIds)
            if (!boundIds.Contains(id))
            {
                error = FString::Printf(TEXT("Missing Texture2D binding for GPU resource '%s'. Supply its native texture object path or explicit builtin:white in resourceAssetPaths (or textures)."), *id);
                return false;
            }
        for (const auto& id : boundIds)
            if (!declaredIds.Contains(id))
            {
                error = FString::Printf(TEXT("Extra texture binding for undeclared GPU resource '%s'. Remove it from resourceAssetPaths (or textures)."), *id);
                return false;
            }
        error = FString::Printf(TEXT("GPU texture binding count mismatch: %d resource declarations, %d bindings. Supply exactly one native Texture2D binding per resource."),
            resourcesJson->Num(), textureJson->Num());
        return false;
    }
    if (hash != LibraGpuSha256(canonical) || sourceHash != LibraGpuSha256(source))
    {
        error = TEXT("GPU bundle IR or shader source hash mismatch.");
        return false;
    }
    auto newSpawnRate = -1.0;
    if (version >= 3 && (!(*metadata)->TryGetNumberField(TEXT("spawnRate"), newSpawnRate) ||
        !FMath::IsFinite(newSpawnRate) || newSpawnRate < 0 || newSpawnRate > 1000000))
    {
        error = TEXT("Invalid authored GPU spawn rate.");
        return false;
    }
    auto newCapacity = int32(capacityNumber);
    TArray<FLibraGpuKernelPacket> newKernels;
    TArray<FLibraGpuTextureBinding> newTextures;
    TArray<FLibraGpuFlowStep> newFlow;
    TMap<FString, int32> kernelIndices, textureIndices;
    TMap<FString, ELibraGpuBillboardBlend> rendererBindings;
    TMap<FString, float> rendererSizeScales;
    auto explicitRenderers = root->HasField(TEXT("rendererBindingsVersion")) || root->HasField(TEXT("rendererBindings"));
    if (explicitRenderers)
    {
        const TArray<TSharedPtr<FJsonValue>>* renderers = nullptr;
        double rendererVersion = 0;
        if (!root->TryGetNumberField(TEXT("rendererBindingsVersion"), rendererVersion) || rendererVersion != 1 ||
            !root->TryGetArrayField(TEXT("rendererBindings"), renderers) || renderers->Num() > 128)
        {
            error = TEXT("Invalid GPU renderer bindings: rendererBindingsVersion must be 1.");
            return false;
        }
        for (const auto& value : *renderers)
        {
            auto object = value->AsObject();
            FString flowId, renderer, material;
            if (!object || !object->TryGetStringField(TEXT("flowId"), flowId) || !NameValid(flowId) ||
                rendererBindings.Contains(flowId) || !object->TryGetStringField(TEXT("renderer"), renderer) ||
                !object->TryGetStringField(TEXT("material"), material) || material.Len() > 1024)
            {
                error = TEXT("Invalid or duplicate GPU renderer binding.");
                return false;
            }
            if (renderer != TEXT("builtin:billboard"))
            {
                error = TEXT("Unsupported GPU renderer. This backend supports only builtin:billboard.");
                return false;
            }
            if (material != TEXT("builtin:unlit-alpha") && material != TEXT("builtin:unlit-additive"))
            {
                error = FString::Printf(TEXT("Unsupported GPU material '%s'. The builtin billboard shader supports only builtin:unlit-alpha or builtin:unlit-additive; native UE materials are not evaluated."), *material);
                return false;
            }
            double sizeScale = 1;
            if (object->HasField(TEXT("sizeScale")) &&
                (!object->TryGetNumberField(TEXT("sizeScale"), sizeScale) || !FMath::IsFinite(sizeScale) ||
                    sizeScale <= 0 || sizeScale > 10000 || float(sizeScale) <= 0))
            {
                error = TEXT("GPU billboard sizeScale must be finite and greater than 0, at most 10000.");
                return false;
            }
            rendererSizeScales.Add(flowId, float(sizeScale));
            rendererBindings.Add(flowId, material == TEXT("builtin:unlit-additive") ?
                ELibraGpuBillboardBlend::Additive : ELibraGpuBillboardBlend::Alpha);
        }
    }
    for (const auto& value : *kernelJson)
    {
        auto object = value->AsObject();
        FString id, stage, encoded;
        if (!object || !object->TryGetStringField(TEXT("id"), id) || !NameValid(id) || kernelIndices.Contains(id) ||
            !object->TryGetStringField(TEXT("stage"), stage) || (stage != TEXT("Spawn") && stage != TEXT("Update")) ||
            !object->TryGetStringField(delivery ? TEXT("base64") : TEXT("packet"), encoded) || encoded.Len() > 24000)
        {
            error = TEXT("Invalid GPU kernel entry.");
            return false;
        }
        FLibraGpuKernelPacket packet;
        packet.Id = id;
        packet.bUpdate = stage == TEXT("Update");
        if (!FBase64::Decode(encoded, packet.Bytes)) { error = TEXT("Invalid GPU packet encoding."); return false; }
        kernelIndices.Add(id, newKernels.Num());
        newKernels.Add(MoveTemp(packet));
    }
    for (const auto& value : *textureJson)
    {
        auto object = value->AsObject();
        FString id, path;
        if (!object || !object->TryGetStringField(TEXT("id"), id) || !NameValid(id) || textureIndices.Contains(id) ||
            (!object->HasField(TEXT("producerNodeId")) && !object->TryGetStringField(TEXT("objectPath"), path)) || path.Len() > 1024)
        {
            error = TEXT("Invalid GPU texture binding.");
            return false;
        }
        FLibraGpuTextureBinding binding;
        binding.Id = id;
        if (object->HasField(TEXT("producerNodeId")))
        {
            FGuid producer;
            if (object->HasField(TEXT("objectPath")) ||
                !object->TryGetStringField(TEXT("producerNodeId"), binding.GeneratedProducerNodeId) ||
                !FGuid::Parse(binding.GeneratedProducerNodeId, producer) || !producer.IsValid() ||
                !object->TryGetStringField(TEXT("outputId"), binding.GeneratedOutputId) ||
                (binding.GeneratedOutputId != TEXT("DyeOutput") && binding.GeneratedOutputId != TEXT("ColorOutput") && binding.GeneratedOutputId != TEXT("FieldOutput") &&
                 binding.GeneratedOutputId != TEXT("VelocityOutput") && binding.GeneratedOutputId != TEXT("DensityOutput")))
            {
                error = TEXT("Invalid generated texture binding.");
                return false;
            }
        }
        else if (path == TEXT("builtin:white")) binding.bBuiltinWhite = true;
        else
        {
            if (path.StartsWith(TEXT("builtin:")))
            {
                error = FString::Printf(TEXT("Unknown builtin GPU texture '%s'. Supported token: builtin:white."), *path);
                return false;
            }
            if (!FSoftObjectPath(path).IsValid()) { error = TEXT("Invalid GPU texture object path."); return false; }
            auto texture = LoadObject<UTexture2D>(nullptr, *path);
            if (!texture) { error = FString::Printf(TEXT("GPU texture not found or wrong type: %s"), *path); return false; }
            binding.Texture = texture;
        }
        textureIndices.Add(id, newTextures.Num());
        newTextures.Add(MoveTemp(binding));
    }
    TSet<FString> resourceIds;
    TArray<FLibraGpuTextureBinding> orderedTextures;
    TMap<FString, int32> orderedTextureIndices;
    TArray<bool> flowMapResources;
    for (int32 resourceIndex = 0; resourceIndex < resourcesJson->Num(); ++resourceIndex)
    {
        auto object = (*resourcesJson)[resourceIndex]->AsObject();
        FString id, kind;
        double declaredRegister = -1;
        if (!object || !object->TryGetStringField(TEXT("id"), id) || resourceIds.Contains(id) ||
            !textureIndices.Contains(id) || !object->TryGetStringField(TEXT("kind"), kind) ||
            (kind != TEXT("Texture2D") && kind != TEXT("FlowMap2D")) ||
            !object->TryGetNumberField(TEXT("register"), declaredRegister) || declaredRegister != resourceIndex)
        {
            error = TEXT("Unsupported or mismatched GPU resource.");
            return false;
        }
        if (kind == TEXT("FlowMap2D"))
        {
            const TSharedPtr<FJsonObject>* settings = nullptr;
            FString axisU, axisV;
            double minimumU = 0, maximumU = 0, minimumV = 0, maximumV = 0;
            auto validAxis = [](const FString& axis) { return axis == TEXT("X") || axis == TEXT("Y") || axis == TEXT("Z"); };
            if (!object->TryGetObjectField(TEXT("flowMap"), settings) || !settings->IsValid() ||
                !(*settings)->TryGetStringField(TEXT("axisU"), axisU) || !validAxis(axisU) ||
                !(*settings)->TryGetStringField(TEXT("axisV"), axisV) || !validAxis(axisV) || axisU == axisV ||
                !(*settings)->TryGetNumberField(TEXT("worldMinimumU"), minimumU) ||
                !(*settings)->TryGetNumberField(TEXT("worldMaximumU"), maximumU) ||
                !(*settings)->TryGetNumberField(TEXT("worldMinimumV"), minimumV) ||
                !(*settings)->TryGetNumberField(TEXT("worldMaximumV"), maximumV) ||
                !FMath::IsFinite(minimumU) || !FMath::IsFinite(maximumU) ||
                !FMath::IsFinite(minimumV) || !FMath::IsFinite(maximumV) ||
                !(float(minimumU) < float(maximumU)) || !(float(minimumV) < float(maximumV)))
            {
                error = TEXT("Invalid FlowMap2D metadata or world mapping bounds.");
                return false;
            }
        }
        else if (object->HasField(TEXT("flowMap")))
        {
            error = TEXT("Texture2D resources cannot contain FlowMap2D metadata.");
            return false;
        }
        resourceIds.Add(id);
        auto binding = MoveTemp(newTextures[textureIndices[id]]);
        binding.bFlowMap = kind == TEXT("FlowMap2D");
        if (binding.bFlowMap)
        {
            const TSharedPtr<FJsonObject>* sampling = nullptr;
            object->TryGetObjectField(TEXT("flowMap"), sampling);
            FString encoding, coordinates;
            if ((*sampling)->HasField(TEXT("encoding")) && !(*sampling)->TryGetStringField(TEXT("encoding"), encoding))
            {
                error = TEXT("Invalid field sampling encoding."); return false;
            }
            if (encoding == TEXT("FloatVelocity")) binding.FlowEncoding = 1;
            else if (encoding == TEXT("FloatDensity")) binding.FlowEncoding = 2;
            else if (!encoding.IsEmpty() && encoding != TEXT("SignedNormalized")) { error = TEXT("Unknown field sampling encoding."); return false; }
            if ((*sampling)->HasField(TEXT("coordinates")) && !(*sampling)->TryGetStringField(TEXT("coordinates"), coordinates))
            {
                error = TEXT("Invalid field coordinate mode."); return false;
            }
            binding.bGeneratedPlaneCoordinates = coordinates == TEXT("GeneratedPlane");
            if (!coordinates.IsEmpty() && coordinates != TEXT("WorldBounds") && !binding.bGeneratedPlaneCoordinates)
            {
                error = TEXT("Unknown field coordinate mode."); return false;
            }
        }
        if (binding.IsGenerated() && (binding.bFlowMap
            ? !((binding.GeneratedOutputId == TEXT("VelocityOutput") && binding.FlowEncoding == 1) || (binding.GeneratedOutputId == TEXT("DensityOutput") && binding.FlowEncoding == 2))
            : (binding.GeneratedOutputId != TEXT("DyeOutput") && binding.GeneratedOutputId != TEXT("ColorOutput") && binding.GeneratedOutputId != TEXT("FieldOutput"))))
        {
            error = TEXT("Generated output and declared sampling format disagree."); return false;
        }
        if (binding.bGeneratedPlaneCoordinates && !binding.IsGenerated()) { error = TEXT("Generated-plane coordinates need a producer."); return false; }
        orderedTextureIndices.Add(id, orderedTextures.Num());
        orderedTextures.Add(MoveTemp(binding));
        flowMapResources.Add(kind == TEXT("FlowMap2D"));
    }
    newTextures = MoveTemp(orderedTextures);
    textureIndices = MoveTemp(orderedTextureIndices);
    for (const auto& packet : newKernels)
    {
        if (!ValidatePacket(packet, newCapacity, flowMapResources, error)) return false;
        for (auto offset = 64; offset < packet.Bytes.Num(); offset += 64)
        {
            if (Word(packet.Bytes, offset) != 41) continue;
            const auto& texture = newTextures[Word(packet.Bytes, offset + 24)];
            if (Word(packet.Bytes, offset + 48) != texture.FlowEncoding ||
                (Word(packet.Bytes, offset + 20) == 5 && ((Word(packet.Bytes, offset + 28) & 0x100u) != 0) != texture.bGeneratedPlaneCoordinates))
            {
                error = TEXT("Field sampling packet disagrees with resource metadata."); return false;
            }
        }
    }
    if (!LibraGpuBufferBudget(newKernels, newCapacity, error)) return false;
    auto seenSpawn = false;
    auto seenUpdate = false;
    auto seenRender = false;
    TSet<FString> usedRenderers;
    for (const auto& value : *flowJson)
    {
        auto object = value->AsObject();
        FString kind, id;
        FLibraGpuFlowStep step;
        if (!object || !object->TryGetStringField(TEXT("kind"), kind)) { error = TEXT("Invalid GPU flow."); return false; }
        step.Kind = kind;
        object->TryGetStringField(TEXT("id"), step.Id);
        if (kind == TEXT("Spawn") || kind == TEXT("Update"))
        {
            auto update = kind == TEXT("Update");
            if ((update ? seenUpdate : seenSpawn) || seenRender || (!update && seenUpdate) ||
                !object->TryGetStringField(TEXT("kernelId"), id) || !kernelIndices.Contains(id))
            { error = TEXT("Invalid GPU dispatch flow."); return false; }
            step.Index = kernelIndices[id];
            if (newKernels[step.Index].bUpdate != update) { error = TEXT("Kernel stage mismatch."); return false; }
            if (update) seenUpdate = true; else seenSpawn = true;
        }
        else if (kind == TEXT("RenderBillboard"))
        {
            if (!object->TryGetStringField(TEXT("resourceId"), id) || !textureIndices.Contains(id))
            { error = TEXT("Missing billboard texture."); return false; }
            step.Index = textureIndices[id];
            if (explicitRenderers)
            {
                auto blend = rendererBindings.Find(step.Id);
                if (!blend || usedRenderers.Contains(step.Id))
                {
                    error = TEXT("Every RenderBillboard flow requires exactly one matching rendererBindings entry.");
                    return false;
                }
                step.BillboardBlend = *blend;
                step.SizeScale = rendererSizeScales[step.Id];
                usedRenderers.Add(step.Id);
            }
            seenRender = true;
        }
        else { error = TEXT("Unsupported GPU flow step."); return false; }
        newFlow.Add(MoveTemp(step));
    }
    if (usedRenderers.Num() != rendererBindings.Num())
    {
        error = TEXT("GPU renderer binding targets an unknown or non-billboard flow.");
        return false;
    }
    Capacity = newCapacity;
    AuthoredSpawnRate = static_cast<float>(newSpawnRate);
    CanonicalJson = MoveTemp(canonical);
    OptimizedSourceExport = MoveTemp(source);
    ContentHash = MoveTemp(hash);
    SourceHash = MoveTemp(sourceHash);
    Kernels = MoveTemp(newKernels);
    Textures = MoveTemp(newTextures);
    Flow = MoveTemp(newFlow);
    error.Reset();
    return true;
}
