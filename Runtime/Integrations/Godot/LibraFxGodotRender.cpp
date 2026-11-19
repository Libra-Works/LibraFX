#include "LibraFxGodotRender.h"
#include "LibraFxGodotGpu.h"
#include "liberafx/LbCpuParticles.h"
#include "liberafx/LbSceneBindings.h"
#include <godot_cpp/classes/base_material3d.hpp>
#include <godot_cpp/classes/image.hpp>
#include <godot_cpp/classes/image_texture.hpp>
#include <godot_cpp/classes/json.hpp>
#include <godot_cpp/classes/shader.hpp>
#include <godot_cpp/classes/viewport.hpp>
#include <godot_cpp/core/memory.hpp>
#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/color.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/node_path.hpp>
#include <godot_cpp/variant/utility_functions.hpp>
#include <godot_cpp/variant/vector2.hpp>
#include <godot_cpp/variant/vector3.hpp>
#include <godot_cpp/variant/variant.hpp>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>

namespace liberafx::godot_adapter {

namespace {

auto readString(const godot::Dictionary& object, const char* key,
                godot::String& out, bool required = true) -> bool
{
    auto value = object.get(key, godot::Variant{});
    if (value.get_type() == godot::Variant::NIL && !required) {
        out = godot::String{};
        return true;
    }
    if (value.get_type() != godot::Variant::STRING) {
        return false;
    }
    out = value;
    return true;
}

auto readNumber(const godot::Variant& value, double& out) -> bool
{
    if (value.get_type() != godot::Variant::INT &&
        value.get_type() != godot::Variant::FLOAT) {
        return false;
    }
    out = value;
    return std::isfinite(out);
}

auto readInteger(const godot::Dictionary& object, const char* key,
                 std::int64_t& out) -> bool
{
    auto value = object.get(key, godot::Variant{});
    if (value.get_type() == godot::Variant::INT) {
        out = value;
        return true;
    }
    if (value.get_type() != godot::Variant::FLOAT) {
        return false;
    }
    auto number = 0.0;
    if (!readNumber(value, number) || std::trunc(number) != number ||
        number < static_cast<double>(std::numeric_limits<std::int64_t>::min()) ||
        number >= -static_cast<double>(std::numeric_limits<std::int64_t>::min())) {
        return false;
    }
    out = static_cast<std::int64_t>(number);
    return true;
}

auto readArray(const godot::Dictionary& object, const char* key,
               godot::Array& out) -> bool
{
    auto value = object.get(key, godot::Variant{});
    if (value.get_type() != godot::Variant::ARRAY) {
        return false;
    }
    out = value;
    return true;
}

auto sameId(const std::array<std::uint8_t, 16>& left,
            const std::uint8_t* right) -> bool
{
    return std::memcmp(left.data(), right, left.size()) == 0;
}

auto toReal(double value, godot::real_t& out) -> bool
{
    if (!std::isfinite(value) ||
        std::abs(value) > static_cast<double>(std::numeric_limits<godot::real_t>::max())) {
        return false;
    }
    out = static_cast<godot::real_t>(value);
    return std::isfinite(static_cast<double>(out));
}

auto makeWhiteTexture() -> godot::Ref<godot::Texture2D>
{
    auto image = godot::Ref<godot::Image>{};
    image.instantiate();
    image->create(1, 1, false, godot::Image::FORMAT_RGBA8);
    image->fill(godot::Color(1, 1, 1, 1));
    auto texture = godot::Ref<godot::ImageTexture>{};
    texture.instantiate();
    texture->set_image(image);
    return texture;
}

template<class Animation>
auto readAnimation(const godot::Variant& value,
                   const godot::Ref<godot::Texture2D>& texture,
                   Animation& out) -> Result
{
    if (value.get_type() == godot::Variant::NIL) {
        return Result::Ok;
    }
    if (value.get_type() != godot::Variant::DICTIONARY) {
        return Result::InvalidBinary;
    }
    auto object = godot::Dictionary{};
    object = value;
    auto versionValue = std::int64_t{};
    if (!readInteger(object, "version", versionValue) ||
        (versionValue != 1 && versionValue != 2)) {
        return Result::UnsupportedBinary;
    }
    out.version = static_cast<std::uint8_t>(versionValue);
    auto textureWidth = std::int64_t{};
    auto textureHeight = std::int64_t{};
    auto startFrame = std::int64_t{};
    auto endFrame = std::int64_t{};
    if (!readInteger(object, "textureWidth", textureWidth) ||
        !readInteger(object, "textureHeight", textureHeight) ||
        !readInteger(object, "startFrame", startFrame) ||
        !readInteger(object, "endFrame", endFrame) ||
        textureWidth < 1 || textureHeight < 1 ||
        textureWidth != texture->get_width() || textureHeight != texture->get_height() ||
        startFrame < 0 || endFrame < startFrame || endFrame >= 4096) {
        return Result::InvalidArgument;
    }
    out.textureWidth = static_cast<std::int32_t>(textureWidth);
    out.textureHeight = static_cast<std::int32_t>(textureHeight);
    out.startFrame = static_cast<std::uint32_t>(startFrame);
    out.endFrame = static_cast<std::uint32_t>(endFrame);

    auto fpsValue = object.get("fps", godot::Variant{});
    auto loopValue = object.get("loop", godot::Variant{});
    if (!readNumber(fpsValue, out.fps) || out.fps <= 0 ||
        loopValue.get_type() != godot::Variant::BOOL) {
        return Result::InvalidBinary;
    }
    out.loop = loopValue;
    auto cells = godot::Array{};
    auto frames = godot::Array{};
    if (!readArray(object, "cells", cells) || !readArray(object, "frames", frames) ||
        cells.is_empty() || cells.size() > 4096 ||
        frames.is_empty() || frames.size() > 4096 ||
        out.endFrame >= static_cast<std::uint32_t>(frames.size())) {
        return Result::InvalidBinary;
    }
    out.cells.reserve(static_cast<std::size_t>(cells.size()));
    for (auto index = 0; index < cells.size(); ++index) {
        if (cells[index].get_type() != godot::Variant::DICTIONARY) {
            return Result::InvalidBinary;
        }
        auto cellObject = godot::Dictionary{};
        cellObject = cells[index];
        auto x = std::int64_t{};
        auto y = std::int64_t{};
        auto width = std::int64_t{};
        auto height = std::int64_t{};
        if (!readInteger(cellObject, "x", x) || !readInteger(cellObject, "y", y) ||
            !readInteger(cellObject, "width", width) || !readInteger(cellObject, "height", height) ||
            x < 0 || y < 0 || width < 1 || height < 1 ||
            x + width > textureWidth || y + height > textureHeight) {
            return Result::InvalidBinary;
        }
        out.cells.emplace_back();
        auto& cell = out.cells.back();
        cell.width = static_cast<std::int32_t>(width);
        cell.height = static_cast<std::int32_t>(height);
        const auto readUv = [&cellObject](const char* key, double& target) {
            return readNumber(cellObject.get(key, godot::Variant{}), target);
        };
        if (!readUv("u", cell.uv[0]) || !readUv("v", cell.uv[1]) ||
            !readUv("uvWidth", cell.uv[2]) || !readUv("uvHeight", cell.uv[3]) ||
            cell.uv[0] < 0 || cell.uv[1] < 0 || cell.uv[2] <= 0 || cell.uv[3] <= 0 ||
            cell.uv[0] + cell.uv[2] > 1.000001 ||
            cell.uv[1] + cell.uv[3] > 1.000001) {
            return Result::InvalidBinary;
        }
    }

    out.frames.reserve(static_cast<std::size_t>(frames.size()));
    out.durations.reserve(static_cast<std::size_t>(frames.size()));
    for (auto index = 0; index < frames.size(); ++index) {
        if (frames[index].get_type() != godot::Variant::DICTIONARY) {
            return Result::InvalidBinary;
        }
        auto frameObject = godot::Dictionary{};
        frameObject = frames[index];
        auto cellIndex = std::int64_t{};
        auto duration = 0.0;
        if (!readInteger(frameObject, "cellIndex", cellIndex) ||
            !readNumber(frameObject.get("durationFrames", godot::Variant{}), duration) ||
            cellIndex < 0 || cellIndex >= cells.size() ||
            duration <= 0 || duration > 1'000'000) {
            return Result::InvalidBinary;
        }
        out.frames.emplace_back();
        out.frames.back().cellIndex = static_cast<std::uint32_t>(cellIndex);
        out.frames.back().durationFrames = duration;
        out.durations.push_back(duration);
    }
    if (out.version == 1) {
        return Result::Ok;
    }

    auto durationValue = object.get("durationFrames", godot::Variant{});
    auto order = std::int64_t{};
    if (!readNumber(durationValue, out.durationFrames) || out.durationFrames < 1 ||
        out.durationFrames > 4'096'000'000.0 ||
        !readInteger(object, "drawOrder", order) || order < 0 || order > 127) {
        return Result::InvalidBinary;
    }
    out.drawOrder = static_cast<std::int32_t>(order);
    auto keys = godot::Array{};
    if (!readArray(object, "keys", keys) || keys.size() > 256) {
        return Result::InvalidBinary;
    }
    out.keys.reserve(static_cast<std::size_t>(keys.size()));
    for (auto index = 0; index < keys.size(); ++index) {
        if (keys[index].get_type() != godot::Variant::DICTIONARY) {
            return Result::InvalidBinary;
        }
        auto keyObject = godot::Dictionary{};
        keyObject = keys[index];
        out.keys.emplace_back();
        auto& key = out.keys.back();
        if (!readNumber(keyObject.get("frame", godot::Variant{}), key.frame) ||
            !readNumber(keyObject.get("x", godot::Variant{}), key.x) ||
            !readNumber(keyObject.get("y", godot::Variant{}), key.y) ||
            !readNumber(keyObject.get("rotationDegrees", godot::Variant{}), key.rotationDegrees) ||
            !readNumber(keyObject.get("scaleX", godot::Variant{}), key.scaleX) ||
            !readNumber(keyObject.get("scaleY", godot::Variant{}), key.scaleY)) {
            return Result::InvalidBinary;
        }
        out.nativeKeys.push_back({key.frame, {key.x, key.y, key.rotationDegrees,
            key.scaleX, key.scaleY}});
    }
    return Result::Ok;
}

}

PackageRenderRenderer::PackageRenderRenderer(const MeshBindings& meshBindings,
    const TextureBindings& textureBindings)
    : meshBindings_(meshBindings), textureBindings_(textureBindings)
{
}

auto PackageRenderRenderer::create(const SpawnablePackage& package,
    const MeshBindings& meshBindings, const TextureBindings& textureBindings,
    const MaterialBindings& materialBindings,
    std::unique_ptr<PackageRenderRenderer>& out) -> Result
{
    if (out) {
        return Result::Busy;
    }
    auto candidate = std::unique_ptr<PackageRenderRenderer>(
        new PackageRenderRenderer(meshBindings, textureBindings));
    auto result = readDraws(package.rootRenderConfiguration, meshBindings,
        textureBindings, materialBindings, candidate->rootDraws_,
        &candidate->rootCpuParticles_);
    if (result != Result::Ok) {
        return result;
    }
    candidate->templates_.reserve(package.templates.size());
    for (const auto& item : package.templates) {
        auto definition = TemplateDraws{};
        definition.containerId = item.containerId;
        result = readDraws(item.renderConfiguration, meshBindings, textureBindings,
            materialBindings, definition.draws);
        if (result != Result::Ok) {
            return result;
        }
        candidate->templates_.push_back(std::move(definition));
    }
    out = std::move(candidate);
    return Result::Ok;
}

PackageRenderRenderer::~PackageRenderRenderer()
{
    clear();
}

auto PackageRenderRenderer::readDraws(const std::vector<std::uint8_t>& bytes,
    const MeshBindings& meshBindings, const TextureBindings& textureBindings,
    const MaterialBindings& materialBindings,
    std::vector<Draw>& out, std::vector<CpuParticleDraw>* cpuParticles) -> Result
{
    if (bytes.empty()) {
        return Result::Ok;
    }
    auto text = godot::String::utf8(reinterpret_cast<const char*>(bytes.data()),
        static_cast<std::int64_t>(bytes.size()));
    auto parsed = godot::JSON::parse_string(text);
    if (parsed.get_type() != godot::Variant::DICTIONARY) {
        return Result::InvalidBinary;
    }
    auto config = godot::Dictionary{};
    config = parsed;
    auto type = godot::String{};
    if (!readString(config, "type", type) || type != "render_config") {
        return Result::InvalidBinary;
    }
    auto nodes = godot::Array{};
    if (!readArray(config, "nodes", nodes) || nodes.size() > 128) {
        return Result::InvalidBinary;
    }
    auto cpuValue = config.get("cpuParticles", godot::Variant{});
    auto cpuNodes = godot::Array{};
    if (cpuValue.get_type() != godot::Variant::NIL) {
        if (cpuValue.get_type() != godot::Variant::ARRAY) {
            return Result::InvalidBinary;
        }
        cpuNodes = cpuValue;
    }
    if (cpuNodes.size() > 128) {
        return Result::InvalidBinary;
    }
    auto cpuVersion = 0.0;
    if ((config.has("cpuParticleVersion") || !cpuNodes.is_empty()) &&
        (!readNumber(config.get("cpuParticleVersion", godot::Variant{}), cpuVersion) || cpuVersion != 1)) {
        return Result::UnsupportedBinary;
    }
    if (cpuParticles == nullptr && !cpuNodes.is_empty()) {
        return Result::UnsupportedBinary;
    }
    auto draws = std::vector<Draw>{};
    draws.reserve(static_cast<std::size_t>(nodes.size()));
    for (auto index = 0; index < nodes.size(); ++index) {
        if (nodes[index].get_type() != godot::Variant::DICTIONARY) {
            return Result::InvalidBinary;
        }
        auto node = godot::Dictionary{};
        node = nodes[index];
        auto kind = godot::String{};
        auto idText = godot::String{};
        auto assetIdentity = godot::String{};
        if (!readString(node, "kind", kind) || !readString(node, "nodeId", idText) ||
            !readString(node, "assetPath", assetIdentity)) {
            return Result::InvalidBinary;
        }
        if (kind != "model" && kind != "sprite" && kind != "flexiblePlane") {
            return Result::UnsupportedBinary;
        }

        auto draw = Draw{};
        draw.sprite = kind == "sprite";
        draw.flexiblePlane = kind == "flexiblePlane";
        if (!parseId(idText, draw.nodeId)) {
            return Result::InvalidBinary;
        }
        if (std::any_of(draws.begin(), draws.end(), [&draw](const auto& existing) {
            return existing.nodeId == draw.nodeId &&
                (existing.flexiblePlane || draw.flexiblePlane);
        }))
        {
            return Result::InvalidBinary;
        }
        auto idBytes = idText.utf8();
        draw.nodeName = idBytes.get_data();
        auto assetBytes = assetIdentity.utf8();
        draw.assetIdentity = assetBytes.get_data();
        if (draw.assetIdentity.empty() || draw.assetIdentity.size() > 512) {
            return Result::InvalidBinary;
        }
        if (draw.flexiblePlane)
        {
            auto texture = textureBindings.find(draw.assetIdentity);
            if (texture == textureBindings.end() || !texture->second.is_valid() ||
                texture->second->get_width() <= 0 || texture->second->get_height() <= 0)
            {
                godot::UtilityFunctions::push_error(
                    godot::String("Flexible Plane texture is unbound or invalid: ") + assetIdentity);
                return Result::InvalidArgument;
            }
            auto error = godot::String{};
            if (!FlexiblePlaneRenderer::prepare(node, texture->second, draw.flexibleDefinition, error))
            {
                godot::UtilityFunctions::push_error(error);
                return Result::InvalidBinary;
            }
            draw.flexibleTexture = texture->second;
            draws.push_back(std::move(draw));
            continue;
        }
        if (draw.sprite) {
            auto texture = textureBindings.find(draw.assetIdentity);
            if (texture == textureBindings.end() || !texture->second.is_valid() ||
                texture->second->get_width() <= 0 || texture->second->get_height() <= 0) {
                return Result::InvalidArgument;
            }
            auto animation = node.get("spriteAnimation", godot::Variant{});
            auto result = readAnimation(animation, texture->second, draw.animation);
            if (result != Result::Ok) {
                return result;
            }
        } else {
            auto mesh = meshBindings.find(draw.assetIdentity);
            if (mesh == meshBindings.end() || !mesh->second.is_valid()) {
                return Result::InvalidArgument;
            }
            if (node.get("spriteAnimation", godot::Variant{}).get_type() !=
                godot::Variant::NIL) {
                return Result::UnsupportedBinary;
            }
        }

        auto billboard = node.get("billboard", false);
        if (billboard.get_type() != godot::Variant::BOOL) {
            return Result::InvalidBinary;
        }
        draw.billboard = billboard;
        if (draw.billboard && !draw.sprite) {
            return Result::UnsupportedBinary;
        }
        auto rotationConvention = godot::String{};
        if (!readString(node, "rotationConvention", rotationConvention, false)) {
            return Result::InvalidBinary;
        }
        if (!rotationConvention.is_empty() && rotationConvention != "liberafx-world") {
            return Result::UnsupportedBinary;
        }

        auto materials = godot::Array{};
        auto textures = godot::Array{};
        if (!readArray(node, "materials", materials) ||
            !readArray(node, "textures", textures)) {
            return Result::InvalidBinary;
        }
        if (draw.sprite && (!materials.is_empty() || !textures.is_empty())) {
            return Result::UnsupportedBinary;
        }
        if (materials.size() > 128 || textures.size() > 256) {
            return Result::InvalidBinary;
        }
        auto mesh = draw.sprite ? godot::Ref<godot::Mesh>{} : meshBindings.at(draw.assetIdentity);
        auto hasSlot = [&mesh](std::int64_t slot) {
            return mesh.is_valid() && slot >= 0 && slot < mesh->get_surface_count();
        };
        for (auto materialIndex = 0; materialIndex < materials.size(); ++materialIndex) {
            if (materials[materialIndex].get_type() != godot::Variant::DICTIONARY) {
                return Result::InvalidBinary;
            }
            auto item = godot::Dictionary{};
            item = materials[materialIndex];
            auto slot = std::int64_t{};
            auto identity = godot::String{};
            if (!readInteger(item, "slot", slot) || !hasSlot(slot) ||
                !readString(item, "path", identity) || identity.is_empty()) {
                return Result::InvalidBinary;
            }
            auto material = materialBindings.find(identity.utf8().get_data());
            if (material == materialBindings.end() || !material->second.is_valid()) {
                return Result::InvalidArgument;
            }
            if (!draw.materials.emplace(static_cast<std::int32_t>(slot), material->second).second) {
                return Result::InvalidBinary;
            }
        }
        const auto addParameter = [&](const godot::Dictionary& item, Input input,
                                      const godot::Ref<godot::Texture2D>& texture) -> Result {
            auto slot = std::int64_t{};
            auto association = std::int64_t{};
            auto index = std::int64_t{};
            auto name = godot::String{};
            if (!readInteger(item, "slot", slot) || !hasSlot(slot) ||
                !readInteger(item, "association", association) || !readInteger(item, "index", index) ||
                !readString(item, "name", name) || name.is_empty()) {
                return Result::InvalidBinary;
            }
            if (association != 2 || index != -1) {
                return Result::UnsupportedBinary;
            }
            auto surface = static_cast<std::int32_t>(slot);
            auto override = draw.materials.find(surface);
            auto material = override != draw.materials.end()
                ? override->second : mesh->surface_get_material(surface);
            auto shaderMaterial = godot::Ref<godot::ShaderMaterial>(material);
            if (shaderMaterial.is_null()) {
                return Result::InvalidArgument;
            }
            auto shader = shaderMaterial->get_shader();
            if (shader.is_null() || shader->get_mode() != godot::Shader::MODE_SPATIAL) {
                return Result::InvalidArgument;
            }
            auto type = godot::Variant::NIL;
            auto uniforms = shader->get_shader_uniform_list();
            for (auto i = 0; i < uniforms.size(); ++i) {
                auto uniform = godot::Dictionary{};
                uniform = uniforms[i];
                auto uniformName = uniform.get("name", godot::Variant{});
                auto uniformType = uniform.get("type", godot::Variant{});
                if ((uniformName.get_type() == godot::Variant::STRING ||
                        uniformName.get_type() == godot::Variant::STRING_NAME) &&
                    uniformType.get_type() == godot::Variant::INT &&
                    static_cast<godot::String>(uniformName) == name) {
                    type = static_cast<godot::Variant::Type>(static_cast<std::int64_t>(uniformType));
                    break;
                }
            }
            if ((texture.is_valid() && type != godot::Variant::OBJECT) ||
                (!texture.is_valid() && input.width == 1 && type != godot::Variant::FLOAT) ||
                (!texture.is_valid() && input.width == 4 && type != godot::Variant::VECTOR4 &&
                    type != godot::Variant::COLOR)) {
                return Result::InvalidArgument;
            }
            auto uniformName = godot::StringName(name);
            if (std::any_of(draw.parameters.begin(), draw.parameters.end(), [&](const auto& parameter) {
                return parameter.slot == surface && parameter.name == uniformName;
            })) {
                return Result::InvalidBinary;
            }
            draw.materials.try_emplace(surface, material);
            draw.parameters.push_back({surface, uniformName, input, texture, type});
            return Result::Ok;
        };
        for (auto textureIndex = 0; textureIndex < textures.size(); ++textureIndex) {
            if (textures[textureIndex].get_type() != godot::Variant::DICTIONARY) {
                return Result::InvalidBinary;
            }
            auto item = godot::Dictionary{};
            item = textures[textureIndex];
            auto identity = godot::String{};
            if (!readString(item, "path", identity)) {
                return Result::InvalidBinary;
            }
            if (identity.is_empty()) {
                continue;
            }
            auto texture = textureBindings.find(identity.utf8().get_data());
            if (texture == textureBindings.end() || !texture->second.is_valid()) {
                return Result::InvalidArgument;
            }
            auto result = addParameter(item, {}, texture->second);
            if (result != Result::Ok) {
                return result;
            }
        }

        auto inputs = godot::Array{};
        if (!readArray(node, "inputs", inputs) || inputs.size() > 256) {
            return Result::InvalidBinary;
        }
        auto hasPosition = false;
        auto hasRotation = false;
        auto hasScale = false;
        auto hasTint = false;
        auto hasFrame = false;
        auto hasColumns = false;
        auto hasRows = false;
        auto hasFps = false;
        auto hasPlaying = false;
        for (auto inputIndex = 0; inputIndex < inputs.size(); ++inputIndex) {
            if (inputs[inputIndex].get_type() != godot::Variant::DICTIONARY) {
                return Result::InvalidBinary;
            }
            auto inputObject = godot::Dictionary{};
            inputObject = inputs[inputIndex];
            auto inputName = godot::String{};
            if (!readString(inputObject, "id", inputName)) {
                return Result::InvalidBinary;
            }
            auto widthValue = std::int64_t{};
            auto componentValue = std::int64_t{};
            if (!readInteger(inputObject, "width", widthValue) ||
                !readInteger(inputObject, "sourceComponent", componentValue) ||
                widthValue < 1 || widthValue > 4 || componentValue < 0 ||
                componentValue > 4 - widthValue) {
                return Result::InvalidBinary;
            }
            auto defaults = godot::Array{};
            if (!readArray(inputObject, "defaults", defaults) ||
                defaults.size() != widthValue) {
                return Result::InvalidBinary;
            }
            auto input = Input{};
            input.width = static_cast<std::uint8_t>(widthValue);
            input.sourceComponent = static_cast<std::uint8_t>(componentValue);
            for (auto lane = 0; lane < defaults.size(); ++lane) {
                if (!readNumber(defaults[lane], input.defaults[lane])) {
                    return Result::InvalidBinary;
                }
            }
            auto sourceValue = inputObject.get("sourceNodeId", godot::Variant{});
            if (sourceValue.get_type() == godot::Variant::STRING) {
                auto sourceText = godot::String{};
                sourceText = sourceValue;
                if (!parseId(sourceText, input.sourceNodeId)) {
                    return Result::InvalidBinary;
                }
                input.connected = true;
            } else if (sourceValue.get_type() != godot::Variant::NIL) {
                return Result::InvalidBinary;
            }
            auto parameter = inputObject.get("materialParameter", godot::Variant{});
            if (parameter.get_type() != godot::Variant::NIL) {
                if (draw.sprite) {
                    return Result::UnsupportedBinary;
                }
                if (parameter.get_type() != godot::Variant::DICTIONARY) {
                    return Result::InvalidBinary;
                }
                auto item = godot::Dictionary{};
                item = parameter;
                auto kind = godot::String{};
                if (!readString(item, "kind", kind) ||
                    !((kind == "scalar" && input.width == 1) || (kind == "vector" && input.width == 4))) {
                    return Result::InvalidBinary;
                }
                auto result = addParameter(item, input, {});
                if (result != Result::Ok) {
                    return result;
                }
                continue;
            }

            auto target = static_cast<Input*>(nullptr);
            auto expectedWidth = 0;
            if (inputName == "position") {
                target = &draw.position; expectedWidth = 3; hasPosition = true;
            } else if (inputName == "rotation") {
                target = &draw.rotation; expectedWidth = 3; hasRotation = true;
            } else if (inputName == "scale") {
                target = &draw.scale; expectedWidth = 3; hasScale = true;
            } else if (inputName == "tint") {
                if (!draw.sprite) {
                    if (input.width != 4 || input.connected ||
                        input.defaults != std::array<double, 4>{1, 1, 1, 1}) {
                        return Result::UnsupportedBinary;
                    }
                    continue;
                }
                target = &draw.tint; expectedWidth = 4; hasTint = true;
            } else if (inputName == "frame" && draw.sprite) {
                target = &draw.frame; expectedWidth = 1; hasFrame = true;
            } else if (inputName == "columns" && draw.sprite) {
                target = &draw.columns; expectedWidth = 1; hasColumns = true;
            } else if (inputName == "rows" && draw.sprite) {
                target = &draw.rows; expectedWidth = 1; hasRows = true;
            } else if (inputName == "fps" && draw.sprite) {
                target = &draw.fps; expectedWidth = 1; hasFps = true;
            } else if (inputName == "playing" && draw.sprite) {
                target = &draw.playing; expectedWidth = 1; hasPlaying = true;
            } else {
                return Result::UnsupportedBinary;
            }
            if (target == nullptr || input.width != expectedWidth ||
                target->width != 0) {
                return Result::InvalidBinary;
            }
            *target = input;
        }
        if (!hasPosition || !hasRotation || !hasScale ||
            (draw.sprite && (!hasTint || !hasFrame || !hasColumns || !hasRows ||
                !hasFps || !hasPlaying))) {
            return Result::InvalidBinary;
        }
        if (!draw.scale.width) {
            draw.scale.width = 3;
            draw.scale.defaults = {1, 1, 1, 0};
        }
        if (draw.sprite && (draw.animation.version == 2 && draw.billboard)) {
            return Result::UnsupportedBinary;
        }
        draws.push_back(std::move(draw));
    }
    auto particleDraws = std::vector<CpuParticleDraw>{};
    if (cpuParticles != nullptr) {
        particleDraws.reserve(static_cast<std::size_t>(cpuNodes.size()));
        for (auto index = 0; index < cpuNodes.size(); ++index) {
            if (cpuNodes[index].get_type() != godot::Variant::DICTIONARY) {
                return Result::InvalidBinary;
            }
            auto item = godot::Dictionary{};
            item = cpuNodes[index];
            auto idText = godot::String{};
            auto assetIdentity = godot::String{};
            auto blendMode = godot::String{};
            auto scale = 0.0;
            auto billboard = item.get("billboard", false);
            if (!readString(item, "nodeId", idText) ||
                !readString(item, "assetPath", assetIdentity) ||
                !readString(item, "blendMode", blendMode) ||
                !readNumber(item.get("drawScale", godot::Variant{}), scale) ||
                billboard.get_type() != godot::Variant::BOOL || !static_cast<bool>(billboard) ||
                (blendMode != "Alpha" && blendMode != "Additive") ||
                assetIdentity.is_empty() || assetIdentity.length() > 512 || scale <= 0 ||
                scale > static_cast<double>(std::numeric_limits<godot::real_t>::max())) {
                return Result::InvalidBinary;
            }
            auto draw = CpuParticleDraw{};
            if (!parseId(idText, draw.nodeId) ||
                std::any_of(draws.begin(), draws.end(), [&draw](const auto& existing) {
                    return existing.nodeId == draw.nodeId;
                }) ||
                std::any_of(particleDraws.begin(), particleDraws.end(), [&draw](const auto& existing) {
                    return existing.nodeId == draw.nodeId;
                })) {
                return Result::InvalidBinary;
            }
            draw.nodeName = idText.utf8().get_data();
            draw.assetIdentity = assetIdentity.utf8().get_data();
            draw.additive = blendMode == "Additive";
            draw.drawScale = scale;
            if (draw.assetIdentity == "builtin:white") {
                draw.texture = makeWhiteTexture();
            } else {
                auto texture = textureBindings.find(draw.assetIdentity);
                if (texture != textureBindings.end()) {
                    draw.texture = texture->second;
                }
            }
            if (draw.texture.is_null() || draw.texture->get_width() <= 0 ||
                draw.texture->get_height() <= 0) {
                return Result::InvalidArgument;
            }
            particleDraws.push_back(std::move(draw));
        }
    }
    out = std::move(draws);
    if (cpuParticles != nullptr) {
        *cpuParticles = std::move(particleDraws);
    }
    return Result::Ok;
}

auto PackageRenderRenderer::parseId(const godot::String& text,
    std::array<std::uint8_t, 16>& id) -> bool
{
    if (text.length() != 36) {
        return false;
    }
    auto digit = 0u;
    for (auto index = 0; index < 36; ++index) {
        auto character = text[index];
        if (index == 8 || index == 13 || index == 18 || index == 23) {
            if (character != '-') {
                return false;
            }
            continue;
        }
        auto value = character >= '0' && character <= '9' ? character - '0' :
            character >= 'a' && character <= 'f' ? character - 'a' + 10 :
            character >= 'A' && character <= 'F' ? character - 'A' + 10 : 16;
        if (value >= 16) {
            return false;
        }
        id[digit / 2] |= static_cast<std::uint8_t>(value << (digit % 2 == 0 ? 4 : 0));
        ++digit;
    }
    return digit == 32;
}

auto PackageRenderRenderer::uuidString(const std::uint8_t* bytes) -> godot::String
{
    static constexpr char digits[] = "0123456789abcdef";
    auto text = std::array<char, 37>{};
    auto output = std::size_t{};
    for (auto index = 0u; index < 16; ++index) {
        if (index == 4 || index == 6 || index == 8 || index == 10) {
            text[output++] = '-';
        }
        text[output++] = digits[bytes[index] >> 4];
        text[output++] = digits[bytes[index] & 0x0f];
    }
    return godot::String::utf8(text.data());
}

auto PackageRenderRenderer::makeRenderable(godot::Node& parent, const Draw& draw,
    RenderInstance& out) -> Result
{
    if (draw.flexiblePlane)
    {
        auto* scope = godot::Object::cast_to<godot::Node3D>(&parent);
        auto error = godot::String{};
        if (!scope || !FlexiblePlaneRenderer::create(*scope, draw.flexibleDefinition,
                draw.flexibleTexture, out.flexiblePlane, error))
        {
            godot::UtilityFunctions::push_error(error.is_empty()
                ? godot::String("Flexible Plane scope is not a Node3D.") : error);
            return Result::InvalidArgument;
        }
        return Result::Ok;
    }
    auto node = memnew(godot::MeshInstance3D);
    node->set_name(godot::String::utf8(draw.nodeName.c_str()));
    if (draw.sprite) {
        auto texture = textureBindings_.find(draw.assetIdentity);
        if (texture == textureBindings_.end() || !texture->second.is_valid()) {
            node->queue_free();
            return Result::InvalidArgument;
        }
        auto quad = godot::Ref<godot::QuadMesh>{};
        quad.instantiate();
        quad->set_size(godot::Vector2(1, 1));
        auto material = godot::Ref<godot::StandardMaterial3D>{};
        material.instantiate();
        material->set_texture(godot::BaseMaterial3D::TEXTURE_ALBEDO, texture->second);
        material->set_shading_mode(godot::BaseMaterial3D::SHADING_MODE_UNSHADED);
        material->set_transparency(godot::BaseMaterial3D::TRANSPARENCY_ALPHA);
        material->set_cull_mode(godot::BaseMaterial3D::CULL_DISABLED);
        material->set_billboard_mode(draw.billboard
            ? godot::BaseMaterial3D::BILLBOARD_ENABLED
            : godot::BaseMaterial3D::BILLBOARD_DISABLED);
        if (draw.animation.version == 2) {
            material->set_render_priority(draw.animation.drawOrder);
        }
        quad->set_material(material);
        node->set_mesh(quad);
        out.quad = quad;
        out.material = material;
    } else {
        auto mesh = meshBindings_.find(draw.assetIdentity);
        if (mesh == meshBindings_.end() || !mesh->second.is_valid()) {
            node->queue_free();
            return Result::InvalidArgument;
        }
        node->set_mesh(mesh->second);
        auto result = applyMaterials(*node, draw, out);
        if (result != Result::Ok) {
            node->queue_free();
            return result;
        }
    }
    parent.add_child(node);
    out.node = node;
    return Result::Ok;
}

auto PackageRenderRenderer::makeCpuRenderable(godot::Node& parent,
    const CpuParticleDraw& draw, CpuParticleRenderInstance& out) -> Result
{
    auto node = memnew(godot::MultiMeshInstance3D);
    node->set_name(godot::String("LibraFxCpuParticles_") + draw.nodeName);
    auto quad = godot::Ref<godot::QuadMesh>{};
    quad.instantiate();
    quad->set_size(godot::Vector2(1, 1));
    auto material = godot::Ref<godot::StandardMaterial3D>{};
    material.instantiate();
    material->set_texture(godot::BaseMaterial3D::TEXTURE_ALBEDO, draw.texture);
    material->set_shading_mode(godot::BaseMaterial3D::SHADING_MODE_UNSHADED);
    material->set_transparency(godot::BaseMaterial3D::TRANSPARENCY_ALPHA);
    material->set_blend_mode(draw.additive
        ? godot::BaseMaterial3D::BLEND_MODE_ADD
        : godot::BaseMaterial3D::BLEND_MODE_MIX);
    material->set_vertex_color_use_as_albedo(true);
    material->set_cull_mode(godot::BaseMaterial3D::CULL_DISABLED);
    material->set_billboard_mode(godot::BaseMaterial3D::BILLBOARD_ENABLED);
    material->set_flag(godot::BaseMaterial3D::FLAG_BILLBOARD_KEEP_SCALE, true);
    quad->set_material(material);
    auto multiMesh = godot::Ref<godot::MultiMesh>{};
    multiMesh.instantiate();
    multiMesh->set_transform_format(godot::MultiMesh::TRANSFORM_3D);
    multiMesh->set_use_colors(true);
    multiMesh->set_mesh(quad);
    auto result = multiMesh.is_valid() ? Result::Ok : Result::InvalidArgument;
    if (result != Result::Ok) {
        node->queue_free();
        return result;
    }
    node->set_multimesh(multiMesh);
    node->set_cast_shadows_setting(godot::GeometryInstance3D::SHADOW_CASTING_SETTING_OFF);
    parent.add_child(node);
    out.node = node;
    out.multiMesh = multiMesh;
    return Result::Ok;
}

auto PackageRenderRenderer::applyMaterials(godot::MeshInstance3D& node, const Draw& draw,
    RenderInstance& out) -> Result
{
    for (const auto& [slot, material] : draw.materials) {
        auto needsInstance = std::any_of(draw.parameters.begin(), draw.parameters.end(),
            [slot](const auto& parameter) { return parameter.slot == slot; });
        if (!needsInstance) {
            node.set_surface_override_material(slot, material);
            continue;
        }
        auto instance = godot::Ref<godot::ShaderMaterial>(material->duplicate());
        if (instance.is_null()) {
            return Result::InvalidArgument;
        }
        node.set_surface_override_material(slot, instance);
        out.shaderMaterials.emplace(slot, instance);
    }
    for (const auto& parameter : draw.parameters) {
        if (parameter.texture.is_valid()) {
            out.shaderMaterials.at(parameter.slot)->set_shader_parameter(parameter.name, parameter.texture);
        }
    }
    return Result::Ok;
}

auto PackageRenderRenderer::updateMaterials(SpawnableHost& host, const Draw& draw,
    const RenderInstance& renderable, const VisualScope* child) const -> Result
{
    for (const auto& parameter : draw.parameters) {
        if (parameter.texture.is_valid()) {
            continue;
        }
        auto value = std::array<double, 4>{};
        auto result = updateInput(host, parameter.input, child, value);
        if (result != Result::Ok) {
            return result;
        }
        auto formatted = godot::Variant{};
        if (parameter.type == godot::Variant::FLOAT) {
            if (std::abs(value[0]) > std::numeric_limits<float>::max()) {
                return Result::NonFinite;
            }
            formatted = value[0];
        } else if (parameter.type == godot::Variant::COLOR) {
            if (std::any_of(value.begin(), value.end(), [](auto lane) {
                return std::abs(lane) > std::numeric_limits<float>::max();
            })) {
                return Result::NonFinite;
            }
            formatted = godot::Color(static_cast<float>(value[0]), static_cast<float>(value[1]),
                static_cast<float>(value[2]), static_cast<float>(value[3]));
        } else {
            auto vector = godot::Vector4{};
            if (!toReal(value[0], vector.x) || !toReal(value[1], vector.y) ||
                !toReal(value[2], vector.z) || !toReal(value[3], vector.w)) {
                return Result::NonFinite;
            }
            formatted = vector;
        }
        renderable.shaderMaterials.at(parameter.slot)->set_shader_parameter(parameter.name, formatted);
    }
    return Result::Ok;
}

auto PackageRenderRenderer::attach(godot::Node& owner) -> Result
{
    auto anyChildDraws = std::any_of(templates_.begin(), templates_.end(),
        [](const auto& item) { return !item.draws.empty(); });
    if (rootDraws_.empty() && rootCpuParticles_.empty() && !anyChildDraws) {
        return Result::Ok;
    }
    root_ = memnew(godot::Node3D);
    auto rootName = godot::String("LibraFxRootScope");
    auto suffix = std::uint64_t{2};
    while (owner.has_node(godot::NodePath(rootName))) {
        rootName = "LibraFxRootScope_" + godot::String::num_uint64(suffix++);
    }
    root_->set_name(rootName);
    owner.add_child(root_);
    for (const auto& draw : rootDraws_) {
        auto renderable = RenderInstance{};
        auto result = makeRenderable(*root_, draw, renderable);
        if (result != Result::Ok) {
            return result;
        }
        rootRenderables_.push_back(std::move(renderable));
    }
    for (const auto& draw : rootCpuParticles_) {
        auto renderable = CpuParticleRenderInstance{};
        auto result = makeCpuRenderable(*root_, draw, renderable);
        if (result != Result::Ok) {
            return result;
        }
        rootCpuRenderables_.push_back(std::move(renderable));
    }
    return Result::Ok;
}

auto PackageRenderRenderer::advance(SpawnableHost& host, double effectTime, PackageGpuRenderer* gpu,
    godot::String& gpuError) -> Result
{
    if (!std::isfinite(effectTime) || effectTime < 0) {
        return Result::InvalidArgument;
    }
    auto result = handleEvents(host, gpu, gpuError);
    if (result != Result::Ok) {
        return result;
    }
    return updateValues(host, effectTime);
}

auto PackageRenderRenderer::updateValues(SpawnableHost& host, double effectTime) -> Result
{
    if (!std::isfinite(effectTime) || effectTime < 0) {
        return Result::InvalidArgument;
    }
    auto result = Result::Ok;
    result = updateCpuParticles(host);
    if (result != Result::Ok) {
        return result;
    }
    if (!rootDraws_.empty()) {
        if (rootHasFlexibleTime_ && effectTime < rootLastFlexibleTime_)
        {
            rootFlexibleHistory_.reset();
        }
        result = updateScope(host, rootDraws_, rootRenderables_, nullptr, effectTime,
            rootFlexibleHistory_, rootFlexibleHidden_, rootVisibleBeforeFlexibleFailure_);
        if (result != Result::Ok) {
            return result;
        }
        rootLastFlexibleTime_ = effectTime;
        rootHasFlexibleTime_ = true;
    }
    for (auto& child : children_) {
        auto definition = findTemplate(child.containerId.data());
        if (!definition) {
            return Result::InvalidBinary;
        }
        auto localTime = effectTime - child.startTime;
        if (!std::isfinite(localTime) || localTime < -1e-8) {
            return Result::InvalidArgument;
        }
        localTime = std::max(0.0, localTime);
        if (child.hasFlexibleTime && localTime < child.lastFlexibleTime)
        {
            child.flexibleHistory.reset();
        }
        result = updateScope(host, definition->draws, child.renderables, &child,
            localTime, child.flexibleHistory, child.flexibleHidden,
            child.visibleBeforeFlexibleFailure);
        if (result != Result::Ok) {
            return result;
        }
        child.lastFlexibleTime = localTime;
        child.hasFlexibleTime = true;
    }
    return Result::Ok;
}

void PackageRenderRenderer::resetFlexibleHistories()
{
    rootFlexibleHistory_.reset();
    rootHasFlexibleTime_ = false;
    for (auto& child : children_)
    {
        child.flexibleHistory.reset();
        child.hasFlexibleTime = false;
    }
}

auto PackageRenderRenderer::currentFlexibleCamera() const -> godot::Camera3D*
{
    auto* owner = root_ ? root_->get_parent() : nullptr;
    auto* camera = static_cast<godot::Camera3D*>(nullptr);
    // LibraEffectPlayer exposes camera_path; the debug player uses the viewport.
    if (owner && owner->has_method("get_camera_path"))
    {
        auto value = owner->call("get_camera_path");
        if (value.get_type() == godot::Variant::NODE_PATH)
        {
            auto path = godot::NodePath{};
            path = value;
            if (!path.is_empty())
            {
                camera = godot::Object::cast_to<godot::Camera3D>(owner->get_node_or_null(path));
            }
        }
    }
    if (!camera)
    {
        auto* viewport = root_ ? root_->get_viewport() : nullptr;
        camera = viewport ? viewport->get_camera_3d() : nullptr;
    }
    return camera;
}

auto PackageRenderRenderer::updateCpuParticles(SpawnableHost& host) -> Result
{
    constexpr auto maximumParticles = std::uint32_t{1000000}; // Native system capacity limit.
    constexpr auto instanceStride = std::size_t{16}; // 3x4 transform rows followed by RGBA.
    if (rootCpuParticles_.size() != rootCpuRenderables_.size()) {
        return Result::InvalidBinary;
    }
    for (auto index = std::size_t{}; index < rootCpuParticles_.size(); ++index) {
        const auto& draw = rootCpuParticles_[index];
        auto& renderable = rootCpuRenderables_[index];
        if (!renderable.node || renderable.multiMesh.is_null()) {
            return Result::InvalidBinary;
        }
        auto count = std::size_t{};
        auto result = host.cpuParticleCount(draw.nodeId.data(), count);
        if (result != Result::Ok) {
            return result;
        }
        if (count > maximumParticles) {
            return Result::InvalidArgument;
        }
        if (count > renderable.capacity) {
            auto capacity = std::max(std::uint32_t{1}, renderable.capacity);
            while (capacity < count) {
                capacity = std::min(maximumParticles, capacity * 2);
            }
            const auto bufferSize = static_cast<std::int64_t>(capacity * instanceStride);
            renderable.buffer.resize(bufferSize);
            if (renderable.buffer.size() != bufferSize) {
                return Result::InsufficientWork;
            }
            renderable.multiMesh->set_instance_count(static_cast<std::int32_t>(capacity));
            renderable.capacity = capacity;
        }
        if (count == 0) {
            renderable.multiMesh->set_visible_instance_count(0);
            continue;
        }
        auto* buffer = renderable.buffer.ptrw();
        const auto basis = renderable.node->get_global_transform().basis;
        const auto ownerWidth = basis.get_column(0).length();
        const auto ownerHeight = basis.get_column(1).length();
        if (!std::isfinite(basis.determinant()) || basis.determinant() == 0) {
            return Result::InvalidArgument;
        }
        const auto inverse = basis.inverse();
        const auto localRadiusFactor = inverse.get_column(0).length() +
            inverse.get_column(1).length() + inverse.get_column(2).length();
        auto bounds = godot::AABB{};
        for (auto particleIndex = std::size_t{}; particleIndex < count; ++particleIndex) {
            auto snapshot = CpuParticleSnapshot{};
            result = host.cpuParticleAt(draw.nodeId.data(), particleIndex, snapshot);
            if (result != Result::Ok || snapshot.id == 0) {
                return result == Result::Ok ? Result::InvalidBinary : result;
            }
            auto* data = buffer + particleIndex * instanceStride;
            const auto values = std::array<double, instanceStride>{
                snapshot.size[0] * draw.drawScale, 0, 0, snapshot.position[0] * draw.drawScale,
                0, snapshot.size[1] * draw.drawScale, 0, snapshot.position[1] * draw.drawScale,
                0, 0, 1, snapshot.position[2] * draw.drawScale,
                snapshot.color[0], snapshot.color[1], snapshot.color[2], snapshot.color[3]};
            for (auto field = std::size_t{}; field < values.size(); ++field) {
                if (!std::isfinite(values[field]) || std::abs(values[field]) > std::numeric_limits<float>::max()) {
                    return Result::NonFinite;
                }
                data[field] = static_cast<float>(values[field]);
            }
            const auto center = godot::Vector3(data[3], data[7], data[11]);
            auto radius = godot::real_t{};
            if (!toReal(0.5 * std::hypot(static_cast<double>(data[0]) * ownerWidth,
                    static_cast<double>(data[5]) * ownerHeight) * localRadiusFactor, radius)) {
                return Result::NonFinite;
            }
            const auto padding = godot::Vector3(radius, radius, radius);
            const auto particleBounds = godot::AABB(center - padding, padding * 2);
            bounds = particleIndex == 0 ? particleBounds : bounds.merge(particleBounds);
        }
        renderable.node->set_custom_aabb(bounds);
        renderable.multiMesh->set_buffer(renderable.buffer);
        renderable.multiMesh->set_visible_instance_count(static_cast<std::int32_t>(count));
    }
    return Result::Ok;
}

auto PackageRenderRenderer::makeScope(godot::Node& parent,
    const std::vector<Draw>& draws, godot::Node3D*& node,
    std::vector<RenderInstance>& renderables) -> Result
{
    node = memnew(godot::Node3D);
    parent.add_child(node);
    renderables.reserve(draws.size());
    for (const auto& draw : draws) {
        auto renderable = RenderInstance{};
        auto result = makeRenderable(*node, draw, renderable);
        if (result != Result::Ok) {
            if (auto* scopeParent = node->get_parent()) {
                scopeParent->remove_child(node);
            }
            node->queue_free();
            node = nullptr;
            renderables.clear();
            return result;
        }
        renderables.push_back(std::move(renderable));
    }
    return Result::Ok;
}

auto PackageRenderRenderer::updateScope(SpawnableHost& host,
    const std::vector<Draw>& draws, const std::vector<RenderInstance>& renderables,
    const VisualScope* child, double localTime,
    flexible_adapter::HistoryScope& flexibleHistory, bool& flexibleHidden,
    bool& visibleBeforeFlexibleFailure) -> Result
{
    if (draws.size() != renderables.size()) {
        return Result::InvalidBinary;
    }
    flexibleHistory.beginUpdate();
    for (auto index = std::size_t{}; index < draws.size(); ++index) {
        const auto& draw = draws[index];
        const auto& renderable = renderables[index];
        if (draw.flexiblePlane)
        {
            auto* scope = child ? child->node : root_;
            auto* camera = currentFlexibleCamera();
            auto error = godot::String{};
            if (!scope || !renderable.flexiblePlane ||
                !renderable.flexiblePlane->update(host,
                    child ? child->containerId.data() : nullptr,
                    child ? child->instanceId : 0, localTime, camera, flexibleHistory, error))
            {
                if (scope && !flexibleHidden)
                {
                    visibleBeforeFlexibleFailure = scope->is_visible();
                    scope->set_visible(false);
                    flexibleHidden = true;
                }
                godot::UtilityFunctions::push_error(error.is_empty()
                    ? godot::String("Flexible Plane render scope is unavailable.") : error);
                return Result::InvalidArgument;
            }
            continue;
        }
        if (!renderable.node) {
            return Result::InvalidBinary;
        }
        auto position = std::array<double, 4>{};
        auto rotation = std::array<double, 4>{};
        auto scale = std::array<double, 4>{};
        auto result = updateInput(host, draw.position, child, position);
        if (result == Result::Ok) {
            result = updateInput(host, draw.rotation, child, rotation);
        }
        if (result == Result::Ok) {
            result = updateInput(host, draw.scale, child, scale);
        }
        if (result != Result::Ok) {
            return result;
        }
        if (!draw.sprite) {
            auto p = godot::Vector3{};
            auto r = godot::Vector3{};
            auto s = godot::Vector3{};
            if (!toReal(position[0], p.x) || !toReal(position[1], p.y) ||
                !toReal(position[2], p.z) || !toReal(rotation[0], r.x) ||
                !toReal(rotation[1], r.y) || !toReal(rotation[2], r.z) ||
                !toReal(scale[0], s.x) || !toReal(scale[1], s.y) ||
                !toReal(scale[2], s.z)) {
                return Result::NonFinite;
            }
            renderable.node->set_position(p);
            renderable.node->set_rotation_degrees(r);
            renderable.node->set_scale(s);
            result = updateMaterials(host, draw, renderable, child);
            if (result != Result::Ok) {
                return result;
            }
            continue;
        }

        auto tint = std::array<double, 4>{};
        auto frameValue = std::array<double, 4>{};
        auto columnsValue = std::array<double, 4>{};
        auto rowsValue = std::array<double, 4>{};
        auto fpsValue = std::array<double, 4>{};
        auto playingValue = std::array<double, 4>{};
        result = updateInput(host, draw.tint, child, tint);
        if (result == Result::Ok) result = updateInput(host, draw.frame, child, frameValue);
        if (result == Result::Ok) result = updateInput(host, draw.columns, child, columnsValue);
        if (result == Result::Ok) result = updateInput(host, draw.rows, child, rowsValue);
        if (result == Result::Ok) result = updateInput(host, draw.fps, child, fpsValue);
        if (result == Result::Ok) result = updateInput(host, draw.playing, child, playingValue);
        if (result != Result::Ok) {
            return result;
        }

        auto uv = std::array<double, 4>{0, 0, 1, 1};
        auto compositionWidth = 1.0;
        auto compositionHeight = 1.0;
        if (draw.animation.version == 2) {
            auto frame = std::uint32_t{};
            auto pose = liberafx::SpritePose{};
            if (!liberafx::sampleSpriteComposition(
                    localTime, draw.animation.fps, draw.animation.durationFrames,
                    draw.animation.loop, draw.animation.durations.data(),
                    static_cast<std::uint32_t>(draw.animation.durations.size()),
                    draw.animation.nativeKeys.data(),
                    static_cast<std::uint32_t>(draw.animation.nativeKeys.size()),
                    frame, pose)) {
                return Result::InvalidBinary;
            }
            const auto& selected = draw.animation.frames[frame];
            const auto& cell = draw.animation.cells[selected.cellIndex];
            uv = cell.uv;
            compositionWidth = cell.width;
            compositionHeight = cell.height;
            auto p = godot::Vector3{};
            auto r = godot::Vector3{};
            auto s = godot::Vector3{};
            if (!toReal(pose.x, p.x) || !toReal(pose.y, p.y) ||
                !toReal(pose.rotationDegrees, r.z) ||
                !toReal(pose.scaleX * compositionWidth, s.x) ||
                !toReal(pose.scaleY * compositionHeight, s.y) ||
                !toReal(1, s.z)) {
                return Result::NonFinite;
            }
            renderable.node->set_position(p);
            renderable.node->set_rotation_degrees(r);
            renderable.node->set_scale(s);
        } else {
            auto frame = frameValue[0];
            auto playing = playingValue[0] >= 0.5;
            if (draw.animation.version == 1) {
                if (draw.frame.connected && frame < 0) {
                    return Result::InvalidArgument;
                }
                auto sequenceFrame = std::uint32_t{};
                if (!liberafx::sampleSpriteSequence(frame, localTime, draw.animation.fps,
                        draw.animation.durations.data(),
                        static_cast<std::uint32_t>(draw.animation.durations.size()),
                        draw.animation.startFrame, draw.animation.endFrame,
                        draw.animation.loop, playing, sequenceFrame)) {
                    return Result::InvalidArgument;
                }
                uv = draw.animation.cells[
                    draw.animation.frames[sequenceFrame].cellIndex].uv;
            } else {
                const auto readGridCount = [](double value, std::uint32_t& out) {
                    if (!std::isfinite(value) || value < 1 || value > 65536 ||
                        std::floor(value) != value) {
                        return false;
                    }
                    out = static_cast<std::uint32_t>(value);
                    return true;
                };
                auto columns = std::uint32_t{};
                auto rows = std::uint32_t{};
                if (!readGridCount(columnsValue[0], columns) ||
                    !readGridCount(rowsValue[0], rows) ||
                    !std::isfinite(fpsValue[0]) || fpsValue[0] < 0) {
                    return Result::InvalidArgument;
                }
                if (frame == -1 && !draw.frame.connected) {
                    frame = playing ? std::floor(localTime * fpsValue[0]) : 0;
                }
                auto atlas = liberafx::AtlasFrame{};
                if (!liberafx::sampleSpriteAtlas(frame, columns, rows, atlas)) {
                    return Result::InvalidArgument;
                }
                std::copy(std::begin(atlas.uvRect), std::end(atlas.uvRect), uv.begin());
            }

            auto p = godot::Vector3{};
            auto r = godot::Vector3{};
            auto s = godot::Vector3{};
            if (!toReal(position[0], p.x) || !toReal(position[1], p.y) ||
                !toReal(position[2], p.z) || !toReal(rotation[0], r.x) ||
                !toReal(rotation[1], r.y) || !toReal(rotation[2], r.z) ||
                !toReal(scale[0], s.x) || !toReal(scale[1], s.y) ||
                !toReal(scale[2], s.z)) {
                return Result::NonFinite;
            }
            renderable.node->set_position(p);
            renderable.node->set_rotation_degrees(r);
            renderable.node->set_scale(s);
        }

        auto materialTint = godot::Color{};
        if (!toReal(tint[0], materialTint.r) || !toReal(tint[1], materialTint.g) ||
            !toReal(tint[2], materialTint.b) || !toReal(tint[3], materialTint.a)) {
            return Result::NonFinite;
        }
        auto uvScale = godot::Vector3{};
        auto uvOffset = godot::Vector3{};
        if (!toReal(uv[2], uvScale.x) || !toReal(uv[3], uvScale.y) ||
            !toReal(1, uvScale.z) || !toReal(uv[0], uvOffset.x) ||
            !toReal(uv[1], uvOffset.y) || !toReal(0, uvOffset.z)) {
            return Result::NonFinite;
        }
        renderable.material->set_albedo(materialTint);
        renderable.material->set_uv1_scale(uvScale);
        renderable.material->set_uv1_offset(uvOffset);
    }
    if (flexibleHidden)
    {
        auto* scope = child ? child->node : root_;
        if (scope)
        {
            scope->set_visible(visibleBeforeFlexibleFailure);
        }
        flexibleHidden = false;
    }
    return Result::Ok;
}

auto PackageRenderRenderer::updateInput(SpawnableHost& host, const Input& input,
    const VisualScope* child, std::array<double, 4>& values) const -> Result
{
    values = input.defaults;
    if (!input.connected) {
        return Result::Ok;
    }
    auto source = std::array<double, 4>{};
    auto width = std::uint8_t{};
    auto result = child
        ? host.childValue(child->containerId.data(), child->instanceId,
            input.sourceNodeId.data(), source.data(), width)
        : host.rootValue(input.sourceNodeId.data(), source.data(), width);
    if (result != Result::Ok) {
        return result;
    }
    if (static_cast<std::size_t>(input.sourceComponent) + input.width > width) {
        return Result::InvalidBinary;
    }
    values = {};
    for (auto lane = std::size_t{}; lane < input.width; ++lane) {
        values[lane] = source[input.sourceComponent + lane];
        if (!std::isfinite(values[lane])) {
            return Result::NonFinite;
        }
    }
    return Result::Ok;
}

auto PackageRenderRenderer::handleEvents(SpawnableHost& host, PackageGpuRenderer* gpu,
    godot::String& gpuError) -> Result
{
    auto event = SpawnableHostEvent{};
    while (host.popEvent(event)) {
        auto result = handleEvent(event, gpu, gpuError);
        if (result != Result::Ok) {
            return result;
        }
    }
    return Result::Ok;
}

auto PackageRenderRenderer::handleEvent(const SpawnableHostEvent& event, PackageGpuRenderer* gpu,
    godot::String& gpuError) -> Result
{
    if (gpu) {
        auto result = gpu->handleEvent(event, gpuError);
        if (result != Result::Ok) {
            return result;
        }
    }
    if (event.kind == SpawnableHostEventKind::Spawn) {
        auto definition = findTemplate(event.containerId);
        if (!definition) {
            return Result::InvalidBinary;
        }
        if (definition->draws.empty()) {
            return Result::Ok;
        }
        if (findChild(event.containerId, event.instanceId) != children_.end() || !root_) {
            return Result::InvalidBinary;
        }
        auto child = VisualScope{};
        std::memcpy(child.containerId.data(), event.containerId, child.containerId.size());
        child.instanceId = event.instanceId;
        child.startTime = event.time;
        auto result = makeScope(*root_, definition->draws, child.node, child.renderables);
        if (result != Result::Ok) {
            return result;
        }
        child.node->set_name(uuidString(event.containerId) + "_" +
            godot::String::num_uint64(event.instanceId));
        children_.push_back(std::move(child));
    } else if (event.kind == SpawnableHostEventKind::Stop ||
               event.kind == SpawnableHostEventKind::Destroy) {
        auto child = findChild(event.containerId, event.instanceId);
        if (child != children_.end()) {
            removeChild(child);
        }
    } else if (event.kind == SpawnableHostEventKind::ForceTerminated) {
        if (event.instanceId == 0) {
            for (auto& child : children_) {
                if (child.node) {
                    if (auto* parent = child.node->get_parent()) {
                        parent->remove_child(child.node);
                    }
                    child.node->queue_free();
                }
            }
            children_.clear();
        } else {
            auto child = findChild(event.containerId, event.instanceId);
            if (child != children_.end()) {
                removeChild(child);
            }
        }
    }
    return Result::Ok;
}

auto PackageRenderRenderer::findTemplate(const std::uint8_t containerId[16]) const
    -> const TemplateDraws*
{
    auto found = std::find_if(templates_.begin(), templates_.end(),
        [containerId](const auto& item) { return sameId(item.containerId, containerId); });
    return found == templates_.end() ? nullptr : &*found;
}

auto PackageRenderRenderer::findChild(const std::uint8_t* containerId,
    std::uint64_t instanceId) -> std::vector<VisualScope>::iterator
{
    return std::find_if(children_.begin(), children_.end(),
        [containerId, instanceId](const auto& item) {
            return item.instanceId == instanceId && sameId(item.containerId, containerId);
        });
}

void PackageRenderRenderer::removeChild(std::vector<VisualScope>::iterator child)
{
    if (child->node) {
        if (auto* parent = child->node->get_parent()) {
            parent->remove_child(child->node);
        }
        child->node->queue_free();
    }
    children_.erase(child);
}

void PackageRenderRenderer::clear() noexcept
{
    resetFlexibleHistories();
    children_.clear();
    rootRenderables_.clear();
    rootCpuRenderables_.clear();
    if (!root_) {
        return;
    }
    if (auto* parent = root_->get_parent()) {
        parent->remove_child(root_);
    }
    root_->queue_free();
    root_ = nullptr;
}

}
