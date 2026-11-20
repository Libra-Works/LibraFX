#include "LibraProceduralScope.h"
#include "LibraProceduralScopeInternal.h"
#include "Dom/JsonObject.h"
#include "Misc/Base64.h"

namespace LibraProcedural
{
static bool Id(const FString& text, liberafx::flexible_adapter::Id& output)
{
    auto id = FGuid{};
    if (!FGuid::ParseExact(text, EGuidFormats::DigitsWithHyphens, id) || !id.IsValid())
    {
        return false;
    }
    const uint32 words[] = {id.A, id.B, id.C, id.D};
    for (auto i = 0u; i < 16; ++i)
    {
        output[i] = static_cast<uint8>(words[i / 4] >> (24 - i % 4 * 8));
    }
    return true;
}
static bool Inputs(const TSharedPtr<FJsonObject>& object, std::vector<liberafx::procedural_adapter::Input>& output)
{
    const TArray<TSharedPtr<FJsonValue>>* inputs = nullptr;
    if (!object->TryGetArrayField(TEXT("inputs"), inputs) || inputs->Num() > 128)
    {
        return false;
    }
    for (const auto& value : *inputs)
    {
        const auto input = value.IsValid() && value->Type == EJson::Object ? value->AsObject() : nullptr;
        auto parsed = liberafx::procedural_adapter::Input{};
        auto id = FString{}, source = FString{}; auto width = 0.0, component = 0.0;
        const TArray<TSharedPtr<FJsonValue>>* defaults = nullptr;
        if (!input || !input->TryGetStringField(TEXT("id"), id) || id.IsEmpty() || id.Len() > 128 ||
            !input->TryGetNumberField(TEXT("width"), width) || width < 1 || width > 4 || FMath::FloorToDouble(width) != width ||
            !input->TryGetNumberField(TEXT("sourceComponent"), component) || component < 0 ||
            component + width > 4 || FMath::FloorToDouble(component) != component ||
            !input->TryGetArrayField(TEXT("defaults"), defaults) || defaults->Num() != width)
        {
            return false;
        }
        parsed.id = TCHAR_TO_UTF8(*id); parsed.width = static_cast<uint32>(width); parsed.component = static_cast<uint32>(component);
        for (auto i = 0; i < defaults->Num(); ++i)
        {
            if (!(*defaults)[i]->TryGetNumber(parsed.defaults[i]) || !FMath::IsFinite(parsed.defaults[i]) || FMath::Abs(parsed.defaults[i]) > 1000000)
            {
                return false;
            }
        }
        if (input->TryGetStringField(TEXT("sourceNodeId"), source))
        {
            if (!Id(source, parsed.source))
            {
                return false;
            }
            parsed.connected = true;
        }
        if (std::any_of(output.begin(), output.end(), [&](const auto& previous) { return previous.id == parsed.id; }))
        {
            return false;
        }
        output.push_back(std::move(parsed));
    }
    return true;
}
bool Prepare(const TSharedPtr<FJsonObject>& config, TSharedPtr<FLibraProceduralDefinition>& output, FString& error)
{
    auto definition = MakeShared<FLibraProceduralDefinition>();
    if (!config->HasField(TEXT("proceduralResources")))
    {
        output = definition;
        return true;
    }
    const auto fail = [&]() { error = TEXT("Invalid or unsupported procedural resource metadata."); return false; };
    auto version = 0.0;
    const TArray<TSharedPtr<FJsonValue>>* resources = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* nodes = nullptr;
    if (!config->TryGetNumberField(TEXT("proceduralVersion"), version) || version != 1 ||
        !config->TryGetArrayField(TEXT("proceduralResources"), resources) || resources->Num() > 64 ||
        !config->TryGetArrayField(TEXT("nodes"), nodes) || nodes->Num() > 128)
    {
        return fail();
    }
    for (const auto& value : *resources)
    {
        const auto object = value.IsValid() && value->Type == EJson::Object ? value->AsObject() : nullptr;
        auto id = FString{}, encoded = FString{}, kind = FString{}; auto guid = liberafx::flexible_adapter::Id{};
        auto bytes = TArray<uint8>{}; auto resource = FLibraProceduralResourceDefinition{};
        if (!object || !object->TryGetStringField(TEXT("nodeId"), id) || !Id(id, guid) ||
            definition->Resources.contains(id) || !object->TryGetStringField(TEXT("definition"), encoded) || encoded.Len() > 5500 ||
            !FBase64::Decode(encoded, bytes) || liberafx::procedural_adapter::decode(bytes.GetData(), bytes.Num(), resource.Native) != LIBRA_OK ||
            !Inputs(object, resource.Native.inputs) || !object->TryGetStringField(TEXT("kind"), kind) ||
            kind != (resource.Native.kind == 0 ? TEXT("ProceduralModel") : resource.Native.kind == 1 ? TEXT("ProceduralTexture") : TEXT("ProceduralMaterial")))
        {
            return fail();
        }
        const TArray<TSharedPtr<FJsonValue>>* textures = nullptr;
        if (!object->TryGetArrayField(TEXT("textures"), textures) || textures->Num() > 4)
        {
            return fail();
        }
        for (const auto& item : *textures)
        {
            const auto texture = item.IsValid() && item->Type == EJson::Object ? item->AsObject() : nullptr;
            auto slot = 0.0; auto source = FString{};
            if (!texture || resource.Native.kind != 2 || !texture->TryGetNumberField(TEXT("slot"), slot) || slot < 0 || slot > 3 ||
                FMath::FloorToDouble(slot) != slot || !texture->TryGetStringField(TEXT("producerNodeId"), source) ||
                !resource.Textures[static_cast<int32>(slot)].IsEmpty())
            {
                return fail();
            }
            resource.Textures[static_cast<int32>(slot)] = source;
            resource.Native.textureMask |= 1u << static_cast<uint32>(slot);
        }
        if (resource.Native.kind == 0)
        {
            auto counts = LibraProceduralModelCounts{};
            if (libra_procedural_model_predict(&resource.Native.model, resource.Native.modifiers.data(), resource.Native.modifiers.size(), &counts) != LIBRA_OK)
            {
                return fail();
            }
            definition->Bytes += counts.retainedBytes;
        }
        else
        {
            definition->Bytes += uint64(resource.Native.resolution) * resource.Native.resolution * 8 + 16384;
        }
        if (definition->Bytes > 64 * 1024 * 1024)
        {
            return fail();
        }
        definition->Resources.emplace(id, std::move(resource));
    }
    for (const auto& [id, resource] : definition->Resources)
    {
        for (const auto& texture : resource.Textures)
        {
            if (!texture.IsEmpty() && (!definition->Resources.contains(texture) || definition->Resources.at(texture).Native.kind != 1))
            {
                return fail();
            }
        }
    }
    for (const auto& value : *nodes)
    {
        const auto object = value.IsValid() && value->Type == EJson::Object ? value->AsObject() : nullptr;
        auto kind = FString{};
        if (!object || !object->TryGetStringField(TEXT("kind"), kind) || kind != TEXT("proceduralModel"))
        {
            continue;
        }
        auto draw = FLibraProceduralDrawDefinition{}; auto inputs = std::vector<liberafx::procedural_adapter::Input>{};
        if (!object->TryGetStringField(TEXT("proceduralModel"), draw.Model) || !definition->Resources.contains(draw.Model) ||
            definition->Resources.at(draw.Model).Native.kind != 0 || !Inputs(object, inputs) || inputs.size() != 4)
        {
            return fail();
        }
        if (object->TryGetStringField(TEXT("proceduralMaterial"), draw.Material) &&
            (!definition->Resources.contains(draw.Material) || definition->Resources.at(draw.Material).Native.kind != 2))
        {
            return fail();
        }
        const char* names[] = {"position", "rotation", "scale", "tint"};
        for (auto i = 0u; i < 4; ++i)
        {
            const auto found = std::find_if(inputs.begin(), inputs.end(), [&](const auto& input) { return input.id == names[i]; });
            if (found == inputs.end() || found->width != (i == 3 ? 4u : 3u))
            {
                return fail();
            }
            draw.Inputs[i] = *found;
        }
        definition->Draws.push_back(std::move(draw));
    }
    output = std::move(definition);
    return true;
}
uint64 Bytes(const TSharedPtr<FLibraProceduralDefinition>& definition) { return definition ? definition->Bytes : 0; }
int32 DrawCount(const TSharedPtr<FLibraProceduralDefinition>& definition) { return definition ? definition->Draws.size() : 0; }
}
