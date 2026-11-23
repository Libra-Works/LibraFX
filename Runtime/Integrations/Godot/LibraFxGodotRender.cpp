#include "LibraFxGodotRender.h"
#include "LibraFxParticleSprite.h"
#include "LibraFxGodotAtlas.h"
#include "LibraFxGodotGpu.h"
#include "LibraFxGodotCoordinates.h"
#include "liberafx/LbCpuParticles.h"
#include "liberafx/LbSceneBindings.h"
#include <godot_cpp/classes/base_material3d.hpp>
#include <godot_cpp/classes/image.hpp>
#include <godot_cpp/classes/image_texture.hpp>
#include <godot_cpp/classes/json.hpp>
#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/classes/shader.hpp>
#include <godot_cpp/classes/viewport.hpp>
#include <godot_cpp/classes/world3d.hpp>
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

namespace render_detail {

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

auto makeWhiteTexture(bool radial = false) -> godot::Ref<godot::Texture2D>
{
    const auto size = radial ? 64 : 1;
    auto image = godot::Image::create_empty(size, size, false, godot::Image::FORMAT_RGBA8);
    image->fill(godot::Color(1, 1, 1, 1));
    if (radial) for (auto y = 0; y < size; ++y) for (auto x = 0; x < size; ++x) {
        const auto u = (x + .5) / size * 2 - 1, v = (y + .5) / size * 2 - 1;
        const auto alpha = std::clamp(1 - std::sqrt(u * u + v * v), 0.0, 1.0);
        image->set_pixel(x, y, godot::Color(1, 1, 1, alpha));
    }
    return godot::ImageTexture::create_from_image(image);
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

using render_detail::makeWhiteTexture;
using render_detail::readAnimation;
using render_detail::readArray;
using render_detail::readInteger;
using render_detail::readNumber;
using render_detail::readString;
using render_detail::sameId;
using render_detail::toReal;

PackageRenderRenderer::PackageRenderRenderer(const MeshBindings& meshBindings,
    const TextureBindings& textureBindings)
    : procedural_(std::make_unique<ProceduralPackageRenderer>()), meshBindings_(meshBindings), textureBindings_(textureBindings)
{
}

auto PackageRenderRenderer::create(const SpawnablePackage& package,
    const MeshBindings& meshBindings, const TextureBindings& textureBindings,
    const MaterialBindings& materialBindings,
    std::unique_ptr<PackageRenderRenderer>& out, PackageGpuRenderer* gpu) -> Result
{
    if (out) {
        return Result::Busy;
    }
    auto candidate = std::unique_ptr<PackageRenderRenderer>(
        new PackageRenderRenderer(meshBindings, textureBindings));
    candidate->gpu_ = gpu;
    const auto proceduralResult = candidate->procedural_->prepare(package);
    if (proceduralResult != Result::Ok)
    {
        return proceduralResult;
    }
    auto result = readDraws(package.rootRenderConfiguration, meshBindings,
        textureBindings, materialBindings, candidate->rootDraws_,
        &candidate->rootCpuParticles_, gpu ? &gpu->drawBindings({}) : nullptr);
    if (result != Result::Ok) {
        return result;
    }
    candidate->templates_.reserve(package.templates.size());
    for (const auto& item : package.templates) {
        auto definition = TemplateDraws{};
        definition.containerId = item.containerId;
        result = readDraws(item.renderConfiguration, meshBindings, textureBindings,
            materialBindings, definition.draws, &definition.cpuParticles,
            gpu ? &gpu->drawBindings(item.containerId) : nullptr);
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
    std::vector<Draw>& out, std::vector<CpuParticleDraw>* cpuParticles,
    const std::vector<GeneratedGpuDrawTexture>* generated) -> Result
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
        (!readNumber(config.get("cpuParticleVersion", godot::Variant{}), cpuVersion) ||
         (cpuVersion != 1 && cpuVersion != 2 && cpuVersion != 3 && cpuVersion != 4)))
    {
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
        if (kind == "proceduralModel")
        {
            continue; // The procedural renderer validates and owns these draws.
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
        if (generated)
        {
            for (const auto& binding : *generated)
            {
                if (binding.nodeId == draw.nodeId)
                {
                    draw.generatedTextures.push_back(binding);
                }
            }
        }
        auto resolvedBindings = textureBindings;
        const auto generatedMain = std::any_of(draw.generatedTextures.begin(), draw.generatedTextures.end(),
            [](const auto& binding) { return binding.mainTexture; });
        const auto isProceduralPlane = draw.flexiblePlane &&
            (draw.assetIdentity == "builtin:procedural-texture" || draw.assetIdentity == "builtin:procedural-material");
        if (draw.assetIdentity == "builtin:generated-texture" || isProceduralPlane)
        {
            if (draw.assetIdentity == "builtin:generated-texture" && (!generatedMain || (!draw.sprite && !draw.flexiblePlane)))
            {
                return Result::InvalidBinary;
            }
            auto image = godot::Image::create(1, 1, false, godot::Image::FORMAT_RGBA8);
            image->fill(godot::Color(1, 1, 1, 1));
            resolvedBindings[draw.assetIdentity] = godot::ImageTexture::create_from_image(image);
        }
        else if (generatedMain)
        {
            return Result::InvalidBinary;
        }
        if (draw.flexiblePlane)
        {
            const auto procTex = node.get("proceduralTexture", godot::Variant{});
            if (procTex.get_type() == godot::Variant::STRING)
            {
                draw.proceduralTexture = static_cast<godot::String>(procTex).utf8().get_data();
            }
            const auto procMat = node.get("proceduralMaterial", godot::Variant{});
            if (procMat.get_type() == godot::Variant::STRING)
            {
                draw.proceduralMaterial = static_cast<godot::String>(procMat).utf8().get_data();
            }
            auto texture = resolvedBindings.find(draw.assetIdentity);
            if (texture == resolvedBindings.end() || !texture->second.is_valid() ||
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
            const auto materialsValue = node.get("materials", godot::Variant{});
            if (materialsValue.get_type() == godot::Variant::ARRAY)
            {
                auto materials = godot::Array{};
                materials = materialsValue;
                if (materials.size() > 1)
                {
                    return Result::InvalidBinary;
                }
                for (auto mIndex = 0; mIndex < materials.size(); ++mIndex)
                {
                    if (materials[mIndex].get_type() != godot::Variant::DICTIONARY) return Result::InvalidBinary;
                    auto mItem = godot::Dictionary{};
                    mItem = materials[mIndex];
                    auto slot = std::int64_t{};
                    auto identity = godot::String{};
                    if (!readInteger(mItem, "slot", slot) || slot != 0 ||
                        !readString(mItem, "path", identity) || identity.is_empty())
                    {
                        return Result::InvalidBinary;
                    }
                    auto foundMat = materialBindings.find(identity.utf8().get_data());
                    if (foundMat == materialBindings.end() || !foundMat->second.is_valid())
                    {
                        return Result::InvalidArgument;
                    }
                    draw.materials.emplace(0, foundMat->second);
                }
            }
            const auto hostMatIt = draw.materials.find(0);
            const auto shaderMat = hostMatIt != draw.materials.end()
                ? godot::Ref<godot::ShaderMaterial>(hostMatIt->second)
                : godot::Ref<godot::ShaderMaterial>{};
            const auto texturesValue = node.get("textures", godot::Variant{});
            if (texturesValue.get_type() == godot::Variant::ARRAY)
            {
                auto textures = godot::Array{};
                textures = texturesValue;
                for (auto tIndex = 0; tIndex < textures.size(); ++tIndex)
                {
                    if (textures[tIndex].get_type() != godot::Variant::DICTIONARY) return Result::InvalidBinary;
                    auto tItem = godot::Dictionary{};
                    tItem = textures[tIndex];
                    auto tSlot = std::int64_t{};
                    auto tName = godot::String{};
                    auto tPath = godot::String{};
                    if (!readInteger(tItem, "slot", tSlot) || tSlot != 0 ||
                        !readString(tItem, "name", tName) || tName.is_empty() ||
                        !readString(tItem, "path", tPath))
                    {
                        return Result::InvalidBinary;
                    }
                    if (tPath.is_empty()) continue;
                    auto foundTex = textureBindings.find(tPath.utf8().get_data());
                    if (foundTex == textureBindings.end() || !foundTex->second.is_valid()) return Result::InvalidArgument;
                    if (shaderMat.is_null()) return Result::InvalidArgument;
                    Draw::Parameter param{};
                    param.slot = 0;
                    param.name = godot::StringName(tName);
                    param.texture = foundTex->second;
                    param.type = godot::Variant::OBJECT;
                    draw.parameters.push_back(std::move(param));
                }
            }
            const auto inputsValue = node.get("inputs", godot::Variant{});
            if (inputsValue.get_type() == godot::Variant::ARRAY)
            {
                auto inputs = godot::Array{};
                inputs = inputsValue;
                for (auto iIndex = 0; iIndex < inputs.size(); ++iIndex)
                {
                    if (inputs[iIndex].get_type() != godot::Variant::DICTIONARY) return Result::InvalidBinary;
                    auto iItem = godot::Dictionary{};
                    iItem = inputs[iIndex];
                    const auto matParamVar = iItem.get("materialParameter", godot::Variant{});
                    if (matParamVar.get_type() != godot::Variant::DICTIONARY) continue;
                    if (shaderMat.is_null()) return Result::InvalidArgument;
                    auto mp = godot::Dictionary{};
                    mp = matParamVar;
                    auto mpSlot = std::int64_t{};
                    auto mpName = godot::String{};
                    if (!readInteger(mp, "slot", mpSlot) || mpSlot != 0 ||
                        !readString(mp, "name", mpName) || mpName.is_empty())
                    {
                        return Result::InvalidBinary;
                    }
                    auto width = std::int64_t{};
                    auto component = std::int64_t{};
                    if (!readInteger(iItem, "width", width) || (width != 1 && width != 4) ||
                        !readInteger(iItem, "sourceComponent", component))
                    {
                        return Result::InvalidBinary;
                    }
                    Draw::Parameter param{};
                    param.slot = 0;
                    param.name = godot::StringName(mpName);
                    param.input.width = static_cast<std::uint8_t>(width);
                    param.input.component = static_cast<std::uint8_t>(component);
                    if (!readArray(iItem.get("defaults", godot::Variant{}), param.input.width, param.input.defaults.data()))
                    {
                        return Result::InvalidBinary;
                    }
                    const auto srcVar = iItem.get("sourceNodeId", godot::Variant{});
                    if (srcVar.get_type() == godot::Variant::STRING)
                    {
                        if (!parseGuid(static_cast<godot::String>(srcVar), param.input.source))
                        {
                            return Result::InvalidBinary;
                        }
                        param.input.connected = true;
                    }
                    param.type = width == 1 ? godot::Variant::FLOAT : godot::Variant::VECTOR4;
                    draw.parameters.push_back(std::move(param));
                }
            }
            draws.push_back(std::move(draw));
            continue;
        }
        if (draw.sprite) {
            auto texture = resolvedBindings.find(draw.assetIdentity);
            if (texture == resolvedBindings.end() || !texture->second.is_valid() ||
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

        for (const auto& binding : draw.generatedTextures)
        {
            if (binding.mainTexture)
            {
                continue;
            }
            auto item = godot::Dictionary{};
            item["slot"] = binding.slot;
            item["name"] = binding.name;
            item["association"] = binding.association;
            item["index"] = binding.index;
            auto image = godot::Image::create(1, 1, false, godot::Image::FORMAT_RGBA8);
            image->fill(godot::Color(1, 1, 1, 1));
            const auto result = addParameter(item, {}, godot::ImageTexture::create_from_image(image));
            if (result != Result::Ok)
            {
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
            auto spriteResult = readParticleSprite(item.get("spriteAnimation", godot::Variant{}),
                                                   draw.hasSpriteAnimation, draw.spriteAnimation);
            if (spriteResult != Result::Ok || (draw.hasSpriteAnimation && cpuVersion != 4))
            {
                return Result::InvalidBinary;
            }
            if (!readParticleAtlas(item.get("flipbook",godot::Variant{}),draw.atlas)) return Result::InvalidBinary;
            if (draw.hasSpriteAnimation && draw.atlas.enabled)
            {
                return Result::InvalidBinary;
            }
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
            const auto rowValues = item.get("drawMatrixRowNodeIds", godot::Variant{});
            if (rowValues.get_type() != godot::Variant::NIL) {
                if (cpuVersion < 2 || rowValues.get_type() != godot::Variant::ARRAY) return Result::InvalidBinary;
                const auto rows = godot::Array(rowValues);
                if (rows.size() != 4) return Result::InvalidBinary;
                for (auto row = 0; row < 4; ++row) {
                    auto id = std::array<std::uint8_t, 16>{};
                    if (rows[row].get_type() != godot::Variant::STRING || !parseId(godot::String(rows[row]), id)) return Result::InvalidBinary;
                    draw.drawRows.push_back(id);
                }
            }
            if (draw.assetIdentity == "builtin:white" || draw.assetIdentity == "builtin:radial-glow") {
                draw.texture = makeWhiteTexture(draw.assetIdentity == "builtin:radial-glow");
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
            if (draw.hasSpriteAnimation &&
                !draw.spriteAnimation.textureMatches(draw.texture->get_width(), draw.texture->get_height()))
            {
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

auto PackageRenderRenderer::makeRenderable(godot::Node3D& parent, const Draw& definition,
    RenderInstance& out, const std::array<std::uint8_t, 16>& container,
    const std::uint64_t instance) -> Result
{
    auto draw = definition;
    auto mainTexture = godot::Ref<godot::Texture2D>{};
    for (const auto& binding : draw.generatedTextures)
    {
        auto texture = gpu_ ? gpu_->outputTexture(binding.producerNodeId, container, instance, binding.outputId)
            : godot::Ref<godot::Texture2D>{};
        if (texture.is_null())
        {
            return Result::InvalidArgument;
        }
        if (binding.mainTexture)
        {
            mainTexture = texture;
            draw.flexibleTexture = texture;
        }
        else
        {
            for (auto& parameter : draw.parameters)
            {
                if (parameter.slot == binding.slot && parameter.name == godot::StringName(binding.name))
                {
                    parameter.texture = texture;
                }
            }
        }
    }
    if (draw.flexiblePlane) {
        auto error = godot::String{};
        const auto hostMatIt = draw.materials.find(0);
        const auto hostMaterial = hostMatIt != draw.materials.end()
            ? hostMatIt->second
            : godot::Ref<godot::Material>{};
        if (!FlexiblePlaneRenderer::create(parent, draw.flexibleDefinition,
                draw.flexibleTexture, out.flexiblePlane, error, hostMaterial))
        {
            godot::UtilityFunctions::push_error(error.is_empty()
                ? godot::String("Flexible Plane scope is unavailable.") : error);
            return Result::InvalidArgument;
        }
        for (const auto& parameter : draw.parameters)
        {
            if (parameter.texture.is_valid())
            {
                out.flexiblePlane->setShaderParameter(parameter.name, parameter.texture);
            }
        }
        return Result::Ok;
    }
    if (draw.sprite) {
        auto texture = textureBindings_.find(draw.assetIdentity);
        if (mainTexture.is_null() && (texture == textureBindings_.end() || !texture->second.is_valid())) {
            return Result::InvalidArgument;
        }
        auto quad = godot::Ref<godot::QuadMesh>{};
        quad.instantiate();
        quad->set_size(godot::Vector2(1, 1));
        auto material = godot::Ref<godot::StandardMaterial3D>{};
        material.instantiate();
        material->set_texture(godot::BaseMaterial3D::TEXTURE_ALBEDO,
            mainTexture.is_valid() ? mainTexture : texture->second);
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
        out.quad = quad;
        out.material = material;
        out.mesh = quad;
    } else {
        auto mesh = meshBindings_.find(draw.assetIdentity);
        if (mesh == meshBindings_.end() || !mesh->second.is_valid()) {
            return Result::InvalidArgument;
        }
        out.mesh = mesh->second;
        out.modelBatchCandidate = draw.materials.empty() && draw.parameters.empty();
        auto result = applyMaterials(out.mesh, draw, out);
        if (result != Result::Ok) {
            return result;
        }
    }
    auto* server = godot::RenderingServer::get_singleton();
    if (!server || out.mesh.is_null()) {
        return Result::InvalidArgument;
    }
    out.instance = OwnedRenderingRid(server->instance_create());
    if (!out.instance.isValid()) {
        return Result::InsufficientWork;
    }
    server->instance_set_base(out.instance.get(), out.mesh->get_rid());
    auto world = parent.get_world_3d();
    server->instance_set_scenario(out.instance.get(),
        world.is_valid() ? world->get_scenario() : godot::RID{});
    server->instance_set_transform(out.instance.get(), parent.get_global_transform());
    server->instance_set_visible(out.instance.get(), false);
    return Result::Ok;
}

auto PackageRenderRenderer::makeCpuRenderable(godot::Node3D& parent,
    const CpuParticleDraw& draw, CpuParticleRenderInstance& out) -> Result
{
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
    material->set_flag(godot::BaseMaterial3D::FLAG_ALBEDO_FROM_VERTEX_COLOR, true);
    material->set_cull_mode(godot::BaseMaterial3D::CULL_DISABLED);
    material->set_billboard_mode(godot::BaseMaterial3D::BILLBOARD_ENABLED);
    material->set_flag(godot::BaseMaterial3D::FLAG_BILLBOARD_KEEP_SCALE, true);
    quad->set_material(material);
    if (draw.atlas.enabled) {
        auto shader = godot::Ref<godot::Shader>{}; shader.instantiate();
        shader->set_code(godot::String("shader_type spatial; render_mode unshaded,cull_disabled,depth_draw_never,") +
            (draw.additive ? "blend_add;" : "blend_mix;") +
            "uniform sampler2D particle_texture : source_color,filter_linear,repeat_disable;"
            "void vertex(){UV=INSTANCE_CUSTOM.xy+UV*INSTANCE_CUSTOM.zw;"
            "MODELVIEW_MATRIX=VIEW_MATRIX*mat4(INV_VIEW_MATRIX[0]*length(MODEL_MATRIX[0].xyz),"
            "INV_VIEW_MATRIX[1]*length(MODEL_MATRIX[1].xyz),INV_VIEW_MATRIX[2]*length(MODEL_MATRIX[2].xyz),MODEL_MATRIX[3]);}"
            "void fragment(){vec4 tint=texture(particle_texture,UV)*COLOR;ALBEDO=tint.rgb;ALPHA=tint.a;}");
        auto atlasMaterial=godot::Ref<godot::ShaderMaterial>{}; atlasMaterial.instantiate();
        atlasMaterial->set_shader(shader); atlasMaterial->set_shader_parameter("particle_texture",draw.texture);
        quad->set_material(atlasMaterial);
    }
    if (draw.hasSpriteAnimation)
    {
        auto shader = godot::Ref<godot::Shader>{};
        shader.instantiate();
        shader->set_code(
            godot::String(
                "shader_type spatial; render_mode unshaded,cull_disabled,depth_draw_never,skip_vertex_transform,") +
            (draw.additive ? "blend_add;" : "blend_mix;") +
            "uniform sampler2D particle_texture : source_color,filter_linear,repeat_disable;"
            "uniform mat4 particle_owner=mat4(1.0);"
            "uniform mat4 particle_owner_inverse=mat4(1.0);"
            "void vertex(){UV=INSTANCE_CUSTOM.xy+UV*INSTANCE_CUSTOM.zw;"
            "mat4 local=particle_owner_inverse*MODEL_MATRIX;"
            "vec2 offset=local[0].xy*VERTEX.x+local[1].xy*VERTEX.y+local[2].xy;"
            "offset*=vec2(length(particle_owner[0].xyz),length(particle_owner[1].xyz));"
            "VERTEX=(VIEW_MATRIX*vec4(MODEL_MATRIX[3].xyz,1.0)).xyz+vec3(offset,0.0);}"
            "void fragment(){vec4 tint=texture(particle_texture,UV)*COLOR;ALBEDO=tint.rgb;ALPHA=tint.a;}");
        out.spriteMaterial.instantiate();
        out.spriteMaterial->set_shader(shader);
        out.spriteMaterial->set_shader_parameter("particle_texture", draw.texture);
        quad->set_material(out.spriteMaterial);
    }
    out.instanceStride = draw.atlas.enabled || draw.hasSpriteAnimation ? 20 : 16;
    auto* server = godot::RenderingServer::get_singleton();
    if (!server || quad.is_null()) {
        return Result::InvalidArgument;
    }
    out.quad = quad;
    out.material = material;
    out.multiMesh = OwnedRenderingRid(server->multimesh_create());
    if (!out.multiMesh.isValid()) {
        return Result::InsufficientWork;
    }
    server->multimesh_allocate_data(out.multiMesh.get(), 1, godot::RenderingServer::MULTIMESH_TRANSFORM_3D, true,
                                    draw.atlas.enabled || draw.hasSpriteAnimation);
    server->multimesh_set_mesh(out.multiMesh.get(), quad->get_rid());
    server->multimesh_set_visible_instances(out.multiMesh.get(), 0);
    out.instance = OwnedRenderingRid(server->instance_create());
    if (!out.instance.isValid()) {
        return Result::InsufficientWork;
    }
    server->instance_set_base(out.instance.get(), out.multiMesh.get());
    auto world = parent.get_world_3d();
    server->instance_set_scenario(out.instance.get(),
        world.is_valid() ? world->get_scenario() : godot::RID{});
    server->instance_set_transform(out.instance.get(), parent.get_global_transform());
    server->instance_geometry_set_cast_shadows_setting(out.instance.get(),
        godot::RenderingServer::SHADOW_CASTING_SETTING_OFF);
    server->instance_set_visible(out.instance.get(), false);
    out.buffer.resize(out.instanceStride);
    if (out.buffer.size() != static_cast<std::int64_t>(out.instanceStride)) {
        return Result::InsufficientWork;
    }
    out.capacity = 1;
    return Result::Ok;
}

auto PackageRenderRenderer::applyMaterials(godot::Ref<godot::Mesh>& mesh, const Draw& draw,
    RenderInstance& out) -> Result
{
    if (draw.materials.empty()) {
        return Result::Ok;
    }
    auto duplicate = godot::Ref<godot::Mesh>(mesh->duplicate());
    if (duplicate.is_null()) {
        return Result::InvalidArgument;
    }
    for (const auto& [slot, material] : draw.materials) {
        if (slot < 0 || slot >= duplicate->get_surface_count()) {
            return Result::InvalidArgument;
        }
        auto needsInstance = std::any_of(draw.parameters.begin(), draw.parameters.end(),
            [slot](const auto& parameter) { return parameter.slot == slot; });
        if (!needsInstance) {
            duplicate->surface_set_material(slot, material);
            continue;
        }
        auto instance = godot::Ref<godot::ShaderMaterial>(material->duplicate());
        if (instance.is_null()) {
            return Result::InvalidArgument;
        }
        duplicate->surface_set_material(slot, instance);
        out.shaderMaterials.emplace(slot, instance);
    }
    for (const auto& parameter : draw.parameters) {
        if (parameter.texture.is_valid()) {
            out.shaderMaterials.at(parameter.slot)->set_shader_parameter(parameter.name, parameter.texture);
        }
    }
    mesh = duplicate;
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

auto PackageRenderRenderer::attach(godot::Node& owner, const CoordinateBasis& coordinates) -> Result
{
    auto frame = godot::Transform3D{};
    const auto converted = effectToHost(coordinates, {}, frame);
    if (converted != Result::Ok) return converted;
    coordinateFrame_ = frame;
    auto anyChildDraws = std::any_of(templates_.begin(), templates_.end(),
        [](const auto& item) { return !item.draws.empty() || !item.cpuParticles.empty(); });
    if (rootDraws_.empty() && rootCpuParticles_.empty() && !anyChildDraws && !procedural_->hasDraws()) {
        return Result::Ok;
    }
    root_ = memnew(LibraRenderScopeNode);
    auto rootName = godot::String("LibraFxRootScope");
    auto suffix = std::uint64_t{2};
    while (owner.has_node(godot::NodePath(rootName))) {
        rootName = "LibraFxRootScope_" + godot::String::num_uint64(suffix++);
    }
    root_->set_name(rootName);
    owner.add_child(root_);
    root_->set_transform(frame);
    root_->setRenderer(this);
    const auto proceduralResult = procedural_->attach(*root_);
    if (proceduralResult != Result::Ok)
    {
        return proceduralResult;
    }
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
    auto result = procedural_->update(host, effectTime);
    if (result != Result::Ok)
    {
        return result;
    }
    if (!rootCpuParticles_.empty())
    {
        if (!root_)
        {
            return Result::InvalidBinary;
        }
        result = updateCpuParticles(host, {}, rootCpuParticles_, rootCpuRenderables_, *root_);
        if (result != Result::Ok)
        {
            return result;
        }
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
        if (!child.node)
        {
            return Result::InvalidBinary;
        }
        auto scope = SpawnableScope{};
        scope.kind = SpawnableScopeKind::Instance;
        std::memcpy(scope.containerId, child.containerId.data(), child.containerId.size());
        scope.instanceId = child.instanceId;
        result = updateCpuParticles(host, scope, definition->cpuParticles,
            child.cpuRenderables, *child.node);
        if (result != Result::Ok)
        {
            return result;
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
    synchronizePresentation();
    return Result::Ok;
}

auto PackageRenderRenderer::renderStats() const -> godot::Dictionary
{
    auto renderInstances = static_cast<std::uint64_t>(procedural_ ? procedural_->drawCount() : 0);
    auto flexibleUpdates = std::uint64_t{};
    auto allocatedDrawInstances = std::uint64_t{};
    auto allocatedFlexibleInstances = std::uint64_t{};
    for (const auto& renderable : rootRenderables_) {
        if (renderable.flexiblePlane || renderable.instance.isValid()) {
            ++renderInstances;
        }
        if (renderable.instance.isValid()) {
            ++allocatedDrawInstances;
        }
        if (renderable.flexiblePlane) {
            flexibleUpdates += renderable.flexiblePlane->surfaceUpdates();
            if (renderable.flexiblePlane->hasAllocatedInstance()) {
                ++allocatedFlexibleInstances;
            }
        }
    }
    for (const auto& child : children_) {
        for (const auto& renderable : child.renderables) {
            if (renderable.flexiblePlane || renderable.instance.isValid()) {
                ++renderInstances;
            }
            if (renderable.instance.isValid()) {
                ++allocatedDrawInstances;
            }
            if (renderable.flexiblePlane) {
                flexibleUpdates += renderable.flexiblePlane->surfaceUpdates();
                if (renderable.flexiblePlane->hasAllocatedInstance()) {
                    ++allocatedFlexibleInstances;
                }
            }
        }
    }
    auto cpuBatchCount = rootCpuRenderables_.size();
    auto allocatedCpuInstances = std::uint64_t{};
    auto allocatedMultiMeshes = std::uint64_t{};
    const auto countCpuInstances = [&](const auto& renderables)
    {
        for (const auto& renderable : renderables)
        {
            if (renderable.instance.isValid())
            {
                ++allocatedCpuInstances;
            }
            if (renderable.multiMesh.isValid())
            {
                ++allocatedMultiMeshes;
            }
        }
    };
    countCpuInstances(rootCpuRenderables_);
    for (const auto& child : children_)
    {
        cpuBatchCount += child.cpuRenderables.size();
        countCpuInstances(child.cpuRenderables);
    }
    renderInstances += cpuBatchCount;
    auto modelBatchCount = std::uint64_t{};
    auto modelBatchMembers = std::uint64_t{};
    for (const auto& batch : modelBatches_) {
        if (batch.instance.isValid()) {
            ++renderInstances;
            ++modelBatchCount;
            modelBatchMembers += batch.members.size();
        }
        if (batch.multiMesh.isValid()) {
            ++allocatedMultiMeshes;
        }
    }
    const auto allocatedInstances = allocatedDrawInstances + allocatedFlexibleInstances +
        allocatedCpuInstances + modelBatchCount;
    auto scopeNodes = static_cast<std::uint64_t>(root_ ? 1 : 0) + children_.size();
    auto stats = godot::Dictionary{};
    stats["render_server_instances"] = static_cast<std::int64_t>(renderInstances);
    stats["allocated_render_server_instances"] = static_cast<std::int64_t>(allocatedInstances);
    stats["allocated_draw_instances"] = static_cast<std::int64_t>(allocatedDrawInstances);
    stats["allocated_flexible_plane_instances"] = static_cast<std::int64_t>(allocatedFlexibleInstances);
    stats["allocated_cpu_particle_instances"] = static_cast<std::int64_t>(allocatedCpuInstances);
    stats["allocated_opaque_model_batch_instances"] = static_cast<std::int64_t>(modelBatchCount);
    stats["allocated_render_server_multimeshes"] = static_cast<std::int64_t>(allocatedMultiMeshes);
    stats["cpu_multimesh_batches"] = static_cast<std::int64_t>(cpuBatchCount);
    stats["opaque_model_multimesh_batches"] = static_cast<std::int64_t>(modelBatchCount);
    stats["opaque_model_multimesh_members"] = static_cast<std::int64_t>(modelBatchMembers);
    stats["flexible_plane_surface_updates"] = static_cast<std::int64_t>(flexibleUpdates);
    stats["effect_scope_nodes"] = static_cast<std::int64_t>(scopeNodes);
    stats["cpu_multimesh_buffer_uploads"] = static_cast<std::int64_t>(cpuBufferUploads_);
    stats["cpu_particle_active_records"] = static_cast<std::int64_t>(cpuActiveRecords_);
    stats["cpu_particle_records_transferred"] = static_cast<std::int64_t>(cpuRecordsTransferred_);
    stats["cpu_particle_bytes_transferred"] = static_cast<std::int64_t>(cpuBytesTransferred_);
    return stats;
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

auto PackageRenderRenderer::updateCpuBounds(CpuParticleRenderInstance& renderable,
    const godot::Basis& basis) -> bool
{
    const auto determinant = basis.determinant();
    if (!std::isfinite(determinant) || determinant == 0) {
        return false;
    }
    const auto inverse = basis.inverse();
    const auto ownerWidth = basis.get_column(0).length();
    const auto ownerHeight = basis.get_column(1).length();
    const auto localRadiusFactor = inverse.get_column(0).length() +
        inverse.get_column(1).length() + inverse.get_column(2).length();
    auto bounds = godot::AABB{};
    const auto* values = renderable.buffer.ptr();
    for (auto index = std::uint32_t{}; index < renderable.activeCount; ++index) {
        const auto* data = values + static_cast<std::size_t>(index) * renderable.instanceStride;
        const auto center = godot::Vector3(data[3], data[7], data[11]);
        auto radius = godot::real_t{};
        const auto sprite = renderable.spriteMaterial.is_valid();
        const auto width = sprite ? std::hypot(static_cast<double>(data[0]), static_cast<double>(data[4]))
                                  : std::abs(static_cast<double>(data[0]));
        const auto height = sprite ? std::hypot(static_cast<double>(data[1]), static_cast<double>(data[5]))
                                   : std::abs(static_cast<double>(data[5]));
        const auto offset =
            sprite ? std::hypot(static_cast<double>(data[2]) * ownerWidth, static_cast<double>(data[6]) * ownerHeight)
                   : 0.0;
        if (!toReal((offset + 0.5 * std::hypot(width * ownerWidth, height * ownerHeight)) * localRadiusFactor, radius))
        {
            return false;
        }
        const auto padding = godot::Vector3(radius, radius, radius);
        const auto particleBounds = godot::AABB(center - padding, padding * 2);
        bounds = index == 0 ? particleBounds : bounds.merge(particleBounds);
    }
    if (renderable.activeCount > 0) {
        auto* server = godot::RenderingServer::get_singleton();
        if (!server) {
            return false;
        }
        server->multimesh_set_custom_aabb(renderable.multiMesh.get(), bounds);
    }
    renderable.boundsBasis = basis;
    return true;
}

auto PackageRenderRenderer::updateCpuParticles(SpawnableHost& host, const SpawnableScope& scope,
    const std::vector<CpuParticleDraw>& draws,
    std::vector<CpuParticleRenderInstance>& renderables, godot::Node3D& node) -> Result
{
    constexpr auto maximumParticles = std::uint32_t{1000000}; // Native system capacity limit.
    if (draws.size() != renderables.size()) {
        return Result::InvalidBinary;
    }
    for (auto index = std::size_t{}; index < draws.size(); ++index) {
        const auto& draw = draws[index];
        auto& renderable = renderables[index];
        const auto instanceStride = renderable.instanceStride;
        if (!renderable.instance.isValid() || !renderable.multiMesh.isValid()) {
            return Result::InvalidBinary;
        }
        auto drawTransform = node.get_global_transform();
        if (!draw.drawRows.empty()) {
            auto rows = std::array<std::array<double, 4>, 4>{};
            for (auto row = 0u; row < 4; ++row) {
                auto width = std::uint8_t{};
                const auto result = scope.kind == SpawnableScopeKind::Effect
                    ? host.rootValue(draw.drawRows[row].data(), rows[row].data(), width)
                    : host.childValue(scope.containerId, scope.instanceId, draw.drawRows[row].data(), rows[row].data(), width);
                if (result != Result::Ok || width != 4) return Result::InvalidValueType;
                for (const auto value : rows[row]) if (!std::isfinite(value)) return Result::NonFinite;
            }
            const auto basis = godot::Basis(godot::Vector3(rows[0][0], rows[1][0], rows[2][0]),
                godot::Vector3(rows[0][1], rows[1][1], rows[2][1]), godot::Vector3(rows[0][2], rows[1][2], rows[2][2]));
            drawTransform = godot::Transform3D(basis, godot::Vector3(rows[0][3], rows[1][3], rows[2][3]));
            const auto converted = effectToHost(host.coordinateBasis(), drawTransform, drawTransform);
            if (converted != Result::Ok) return converted;
        }
        godot::RenderingServer::get_singleton()->instance_set_transform(renderable.instance.get(), drawTransform);
        renderable.drawWorld = drawTransform;
        renderable.hasDrawWorld = !draw.drawRows.empty();
        auto count = std::size_t{};
        auto result = host.cpuParticleCount(scope, draw.nodeId.data(), count);
        if (result != Result::Ok) {
            return result;
        }
        if (count > maximumParticles) {
            return Result::InvalidArgument;
        }
        const auto sourceCount = count;
        const auto parts = draw.hasSpriteAnimation ? draw.spriteAnimation.partCount() : 1;
        count *= parts;
        if (renderable.spriteMaterial.is_valid())
        {
            renderable.spriteMaterial->set_shader_parameter("particle_owner", drawTransform);
        }
        if (count > renderable.capacity) {
            auto capacity = std::max(std::uint32_t{1}, renderable.capacity);
            while (capacity < count) {
                capacity = std::min(static_cast<std::uint32_t>(maximumParticles * std::max(std::size_t{1}, parts)),
                                    capacity * 2);
            }
            const auto bufferSize = static_cast<std::int64_t>(capacity * instanceStride);
            renderable.buffer.resize(bufferSize);
            if (renderable.buffer.size() != bufferSize) {
                return Result::InsufficientWork;
            }
            auto* server = godot::RenderingServer::get_singleton();
            if (!server) {
                return Result::InvalidArgument;
            }
            server->multimesh_allocate_data(renderable.multiMesh.get(), static_cast<std::int32_t>(capacity),
                                            godot::RenderingServer::MULTIMESH_TRANSFORM_3D, true,
                                            draw.atlas.enabled || draw.hasSpriteAnimation);
            renderable.capacity = capacity;
        }
        if (count == 0) {
            godot::RenderingServer::get_singleton()->multimesh_set_visible_instances(
                renderable.multiMesh.get(), 0);
            renderable.activeCount = 0;
            renderable.boundsValid = false;
            continue;
        }
        auto* buffer = renderable.buffer.ptrw();
        const auto basis = drawTransform.basis;
        if (!std::isfinite(basis.determinant()) || basis.determinant() == 0) {
            return Result::InvalidArgument;
        }
        if (renderable.spriteMaterial.is_valid())
        {
            renderable.spriteMaterial->set_shader_parameter("particle_owner_inverse", drawTransform.affine_inverse());
        }
        std::array<CpuParticleSnapshot,256> particleBatch;
        std::array<CpuParticleTiming,256> timingBatch;
        for (auto part = std::size_t{}; part < parts; ++part)
        {
            for (auto particleIndex = std::size_t{}; particleIndex < sourceCount; ++particleIndex)
            {
                if (particleIndex % 256 == 0)
                {
                    std::size_t written = 0;
                    result = host.cpuParticleRead(scope, draw.nodeId.data(), particleIndex, particleBatch.data(),
                                                  draw.atlas.enabled || draw.hasSpriteAnimation ? timingBatch.data()
                                                                                                : nullptr,
                                                  particleBatch.size(), written);
                    if (result != Result::Ok || written != std::min(std::size_t{256}, sourceCount - particleIndex))
                    {
                        return result == Result::Ok ? Result::InvalidBinary : result;
                    }
                }
                const auto &snapshot = particleBatch[particleIndex % 256];
                if (snapshot.id == 0)
                {
                    return result == Result::Ok ? Result::InvalidBinary : result;
                }
                auto *data = buffer + (part * sourceCount + particleIndex) * instanceStride;
                const auto values = std::array<double, 16>{snapshot.size[0] * draw.drawScale,
                                                           0,
                                                           0,
                                                           snapshot.position[0] * draw.drawScale,
                                                           0,
                                                           snapshot.size[1] * draw.drawScale,
                                                           0,
                                                           snapshot.position[1] * draw.drawScale,
                                                           0,
                                                           0,
                                                           1,
                                                           snapshot.position[2] * draw.drawScale,
                                                           snapshot.color[0],
                                                           snapshot.color[1],
                                                           snapshot.color[2],
                                                           snapshot.color[3]};
                for (auto field = std::size_t{}; field < values.size(); ++field)
                {
                    if (!std::isfinite(values[field]) || std::abs(values[field]) > std::numeric_limits<float>::max())
                    {
                        return Result::NonFinite;
                    }
                    data[field] = static_cast<float>(values[field]);
                }
                if (draw.hasSpriteAnimation)
                {
                    auto sprite = ParticleSpriteSample{};
                    const auto status = draw.spriteAnimation.sample(part, timingBatch[particleIndex % 256].age, sprite);
                    if (status != Result::Ok)
                    {
                        return status;
                    }
                    const auto sx = snapshot.size[0] * draw.drawScale, sy = snapshot.size[1] * draw.drawScale;
                    const auto c = std::cos(sprite.rotation), s = std::sin(sprite.rotation);
                    data[0] = static_cast<float>(c * sx * sprite.scaleX);
                    data[1] = static_cast<float>(-s * sy * sprite.scaleY);
                    data[4] = static_cast<float>(s * sx * sprite.scaleX);
                    data[5] = static_cast<float>(c * sy * sprite.scaleY);
                    data[2] = static_cast<float>(sx * sprite.x);
                    data[6] = static_cast<float>(sy * sprite.y);
                    for (auto lane = 0u; lane < 4; ++lane)
                    {
                        data[16 + lane] = static_cast<float>(sprite.uv[lane]);
                    }
                }
                else if (draw.atlas.enabled)
                {
                    const auto &timing = timingBatch[particleIndex % 256];
                    const auto frame = draw.atlas.frame(timing.age, timing.life);
                    data[16] = static_cast<float>(frame % draw.atlas.columns) / draw.atlas.columns;
                    data[17] = static_cast<float>(frame / draw.atlas.columns) / draw.atlas.rows;
                    data[18] = 1.0f / draw.atlas.columns;
                    data[19] = 1.0f / draw.atlas.rows;
                }
            }
        }
        auto* server = godot::RenderingServer::get_singleton();
        if (!server) {
            return Result::InvalidArgument;
        }
        renderable.activeCount = static_cast<std::uint32_t>(count);
        renderable.boundsValid = updateCpuBounds(renderable, basis);
        if (!renderable.boundsValid) {
            return Result::NonFinite;
        }
        server->multimesh_set_buffer(renderable.multiMesh.get(), renderable.buffer);
        server->multimesh_set_visible_instances(renderable.multiMesh.get(),
            static_cast<std::int32_t>(count));
        ++cpuBufferUploads_;
        cpuActiveRecords_ += count;
        cpuRecordsTransferred_ += static_cast<std::uint64_t>(renderable.buffer.size()) / instanceStride;
        cpuBytesTransferred_ += static_cast<std::uint64_t>(renderable.buffer.size()) * sizeof(float);
    }
    return Result::Ok;
}

auto PackageRenderRenderer::makeScope(godot::Node& parent,
    const std::vector<Draw>& draws, godot::Node3D*& node,
    std::vector<RenderInstance>& renderables,
    const std::array<std::uint8_t, 16>& container, const std::uint64_t instance) -> Result
{
    node = memnew(godot::Node3D);
    parent.add_child(node);
    renderables.reserve(draws.size());
    for (const auto& draw : draws) {
        auto renderable = RenderInstance{};
        auto result = makeRenderable(*node, draw, renderable, container, instance);
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
    const std::vector<Draw>& draws, std::vector<RenderInstance>& renderables,
    const VisualScope* child, double localTime,
    flexible_adapter::HistoryScope& flexibleHistory, bool& flexibleHidden,
    bool& visibleBeforeFlexibleFailure) -> Result
{
    if (draws.size() != renderables.size()) {
        return Result::InvalidBinary;
    }
    auto* scopeNode = child ? child->node : root_;
    if (flexibleHidden && scopeNode) {
        scopeNode->set_visible(visibleBeforeFlexibleFailure);
        flexibleHidden = false;
    }
    flexibleHistory.beginUpdate();
    for (auto index = std::size_t{}; index < draws.size(); ++index) {
        const auto& draw = draws[index];
        auto& renderable = renderables[index];
        if (draw.flexiblePlane)
        {
            auto* camera = currentFlexibleCamera();
            auto error = godot::String{};
            if (renderable.flexiblePlane && procedural_ &&
                (!draw.proceduralMaterial.empty() || !draw.proceduralTexture.empty()))
            {
                const auto& resourceId = !draw.proceduralMaterial.empty()
                    ? draw.proceduralMaterial
                    : draw.proceduralTexture;
                auto procTexture = godot::Ref<godot::Texture2D>{};
                auto blend = 0;
                auto doubleSided = true;
                auto emission = 1.0f;
                if (procedural_->resolveResourceTexture(
                        child ? child->containerId.data() : nullptr,
                        child ? child->instanceId : 0,
                        resourceId, procTexture, blend, doubleSided, emission))
                {
                    renderable.flexiblePlane->setTexture(procTexture);
                    if (!draw.proceduralMaterial.empty())
                    {
                        renderable.flexiblePlane->applyProceduralSettings(blend, doubleSided, emission);
                    }
                }
            }
            if (renderable.flexiblePlane)
            {
                for (const auto& parameter : draw.parameters)
                {
                    if (parameter.texture.is_valid()) continue;
                    auto values = std::array<double, 4>{};
                    const auto paramResult = updateInput(host, parameter.input, child, values);
                    if (paramResult != Result::Ok)
                    {
                        return paramResult;
                    }
                    if (parameter.input.width == 1)
                    {
                        renderable.flexiblePlane->setShaderParameter(parameter.name, static_cast<float>(values[0]));
                    }
                    else
                    {
                        renderable.flexiblePlane->setShaderParameter(parameter.name,
                            godot::Vector4(static_cast<float>(values[0]), static_cast<float>(values[1]),
                                static_cast<float>(values[2]), static_cast<float>(values[3])));
                    }
                }
            }
            if (!scopeNode || !renderable.flexiblePlane ||
                !renderable.flexiblePlane->update(*scopeNode, host,
                    child ? child->containerId.data() : nullptr,
                    child ? child->instanceId : 0, localTime, camera, flexibleHistory, error))
            {
                if (scopeNode && !flexibleHidden)
                {
                    visibleBeforeFlexibleFailure = scopeNode->is_visible();
                    scopeNode->set_visible(false);
                    flexibleHidden = true;
                }
                if (scopeNode) {
                    synchronizePresentation();
                }
                godot::UtilityFunctions::push_error(error.is_empty()
                    ? godot::String("Flexible Plane render scope is unavailable.") : error);
                return Result::InvalidArgument;
            }
            continue;
        }
        if (!scopeNode || renderable.mesh.is_null()) {
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
            auto localTransform = godot::Transform3D{};
            localTransform.basis.set_euler_scale(
                godot::Vector3(r.x, r.y, r.z) * static_cast<godot::real_t>(0.017453292519943295769),
                s);
            localTransform.origin = p;
            auto coordinates = godot::Transform3D{};
            const auto converted = effectToHost(host.coordinateBasis(), {}, coordinates);
            if (converted != Result::Ok) return converted;
            localTransform = localTransform * coordinates.affine_inverse();
            renderable.localTransform = localTransform;
            renderable.submitted = true;
            updateInstance(renderable.instance, *scopeNode, localTransform);
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
        if (result == Result::Ok) {
            result = updateInput(host, draw.frame, child, frameValue);
        }
        if (result == Result::Ok) {
            result = updateInput(host, draw.columns, child, columnsValue);
        }
        if (result == Result::Ok) {
            result = updateInput(host, draw.rows, child, rowsValue);
        }
        if (result == Result::Ok) {
            result = updateInput(host, draw.fps, child, fpsValue);
        }
        if (result == Result::Ok) {
            result = updateInput(host, draw.playing, child, playingValue);
        }
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
            auto localTransform = godot::Transform3D{};
            localTransform.basis.set_euler_scale(
                godot::Vector3(r.x, r.y, r.z) * static_cast<godot::real_t>(0.017453292519943295769),
                s);
            localTransform.origin = p;
            renderable.localTransform = localTransform;
            renderable.submitted = true;
            updateInstance(renderable.instance, *scopeNode, localTransform);
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
            auto localTransform = godot::Transform3D{};
            localTransform.basis.set_euler_scale(
                godot::Vector3(r.x, r.y, r.z) * static_cast<godot::real_t>(0.017453292519943295769),
                s);
            localTransform.origin = p;
            renderable.localTransform = localTransform;
            renderable.submitted = true;
            updateInstance(renderable.instance, *scopeNode, localTransform);
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
    const auto proceduralResult = procedural_->handleEvent(event);
    if (proceduralResult != Result::Ok)
    {
        return proceduralResult;
    }
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
        if (definition->draws.empty() && definition->cpuParticles.empty()) {
            return Result::Ok;
        }
        if (findChild(event.containerId, event.instanceId) != children_.end() || !root_) {
            return Result::InvalidBinary;
        }
        auto child = VisualScope{};
        std::memcpy(child.containerId.data(), event.containerId, child.containerId.size());
        child.instanceId = event.instanceId;
        child.startTime = event.time;
        auto result = makeScope(*root_, definition->draws, child.node, child.renderables, child.containerId, child.instanceId);
        if (result != Result::Ok) {
            return result;
        }
        child.node->set_name(uuidString(event.containerId) + "_" +
            godot::String::num_uint64(event.instanceId));
        for (const auto& draw : definition->cpuParticles)
        {
            auto renderable = CpuParticleRenderInstance{};
            result = makeCpuRenderable(*child.node, draw, renderable);
            if (result != Result::Ok)
            {
                if (auto* parent = child.node->get_parent())
                {
                    parent->remove_child(child.node);
                }
                child.node->queue_free();
                return result;
            }
            child.cpuRenderables.push_back(std::move(renderable));
        }
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
            modelBatches_.clear();
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
    procedural_.reset();
    if (root_) {
        root_->setRenderer(nullptr);
    }
    resetFlexibleHistories();
    modelBatches_.clear();
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
