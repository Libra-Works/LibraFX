#include "LibraFxGodotGpu.h"
#include "LibraFxGodotCoordinates.h"
#include "LibraFxGodotFluid2D.h"
#include "LibraFxGodotRenderTaskQueue.h"
#include <godot_cpp/classes/image.hpp>
#include <godot_cpp/classes/json.hpp>
#include <godot_cpp/classes/marshalls.hpp>
#include <godot_cpp/classes/multi_mesh.hpp>
#include <godot_cpp/classes/multi_mesh_instance3d.hpp>
#include <godot_cpp/classes/quad_mesh.hpp>
#include <godot_cpp/classes/rd_pipeline_specialization_constant.hpp>
#include <godot_cpp/classes/rd_shader_source.hpp>
#include <godot_cpp/classes/rd_shader_spirv.hpp>
#include <godot_cpp/classes/rd_texture_format.hpp>
#include <godot_cpp/classes/rd_texture_view.hpp>
#include <godot_cpp/classes/rd_uniform.hpp>
#include <godot_cpp/classes/rd_sampler_state.hpp>
#include <godot_cpp/classes/rendering_device.hpp>
#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/classes/shader.hpp>
#include <godot_cpp/classes/shader_material.hpp>
#include <godot_cpp/classes/thread.hpp>
#include <godot_cpp/classes/texture2drd.hpp>
#include <godot_cpp/core/memory.hpp>
#include <godot_cpp/core/object.hpp>
#include <godot_cpp/variant/aabb.hpp>
#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/packed_byte_array.hpp>
#include <godot_cpp/variant/packed_float32_array.hpp>
#include <godot_cpp/variant/typed_array.hpp>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <unordered_set>
#include <utility>
#include <vector>

namespace liberafx::godot_adapter {
namespace {

using namespace godot;
using Textures = std::unordered_map<std::string, Ref<Texture2D>>;
using Bytes = std::vector<std::uint8_t>;
using liberafx::gpu::Fluid2DSettings;

constexpr auto particleStride = 80u;
constexpr auto blockBytes = 64u;
constexpr auto maxRegisters = 128u; // Must match the embedded interpreter.
constexpr auto maxParticles = 1'000'000u; // Aggregate per player; tiled presentation stays below 16K texture height.
constexpr auto maxFlowResources = 64u;
constexpr auto maxFlowBytes = 32u * 1024 * 1024;
constexpr auto flowHeaderBytes = maxFlowResources * 16;
constexpr auto missing = std::numeric_limits<std::uint32_t>::max();
static_assert(sizeof(float) == sizeof(std::uint32_t) && std::numeric_limits<float>::is_iec559);
constexpr auto computeSource =
#include "Shaders/LgpkCompute.glsl.inc"
;
constexpr auto billboardSource =
#include "Shaders/LgpkBillboard.gdshader.inc"
;
constexpr auto publishSource =
#include "Shaders/LgpkPublish.glsl.inc"
;

struct Kernel {
    String id;
    PackedByteArray packet;
    RID storage;
    RID uniforms;
};

using NodeId = std::array<std::uint8_t, 16>;
using MatrixRows = std::array<NodeId, 4>;

struct Emitter {
    std::array<Kernel, 2> kernels;
    std::uint32_t capacity = 0;
    std::uint32_t seed = 0;
    float sizeScale = 1.0f;
    bool additive = false;
    Ref<Texture2D> texture;
    std::array<Ref<Texture2D>, maxFlowResources> flowTextures;
    std::array<NodeId, maxFlowResources> flowProducers{};
    std::array<std::uint32_t, maxFlowResources> flowEncodings{};
    std::array<bool, maxFlowResources> generatedPlaneCoordinates{};
    std::array<std::uint32_t, maxFlowResources> generatedFieldIndices{};
    std::array<NodeId, 16> generatedFieldProducers{};
    std::array<Ref<Texture2DRD>, 16> generatedFieldTextures;
    std::uint32_t generatedFieldCount = 0;
    RID flowStorage;
    RID fieldMappingStorage;
    PackedByteArray fieldMappingData;
    RID particles;
    RID output;
    RID publishUniforms;
    Ref<Texture2DRD> outputTexture;
    Ref<ShaderMaterial> material;
    Ref<MultiMesh> multiMesh;
    std::uint64_t visualId = 0;
    NodeId containerId{};
    GeneratorInstanceId instanceId = 0;
    double startTime = 0.0;
    MatrixRows matrixRows{};
    MatrixRows emitterRows{};
    std::array<float, 16> emitterTransform{1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1};
    bool hasEmitterMatrix = false;
    bool hasMatrix = false;
    bool generatedTexture = false;
    NodeId textureProducer{};
    String textureOutput;
    double remainder = 0.0;
    double spawnRate = -1.0;
    std::uint32_t cursor = 0;
    std::uint32_t frame = 0;
};

struct EmitterTemplate {
    NodeId containerId{};
    std::vector<Emitter> emitters;
    std::vector<Fluid2DProgram> fluids;
    std::vector<GeneratedGpuDrawTexture> drawBindings;
};

auto fail(String& error, const String& detail, Result result = Result::InvalidBinary) -> Result
{
    error = detail;
    return result;
}

auto text(const Dictionary& dictionary, const char* key) -> String
{
    const auto value = dictionary.get(key, Variant{});
    if (value.get_type() != Variant::STRING) {
        return {};
    }
    return value.operator String();
}

auto number(const Variant& value) -> double
{
    if (value.get_type() != Variant::FLOAT && value.get_type() != Variant::INT) {
        return std::numeric_limits<double>::quiet_NaN();
    }
    return static_cast<double>(value);
}

auto number(const Dictionary& dictionary, const char* key) -> double
{
    return number(dictionary.get(key, Variant{}));
}

auto textKey(const String& name) -> std::string
{
    const auto utf8 = name.utf8();
    return {utf8.get_data(), static_cast<std::size_t>(utf8.length())};
}

auto noNul(const String& value) -> bool
{
    for (auto i = std::int64_t{}; i < value.length(); ++i) {
        if (value[i] == 0) {
            return false;
        }
    }
    return true;
}

auto token(const String& name) -> bool
{
    return !name.is_empty() && name.length() <= 128 && noNul(name);
}

auto object(const Variant& value, Dictionary& out) -> bool
{
    if (value.get_type() != Variant::DICTIONARY) {
        return false;
    }
    out = value;
    return true;
}

auto array(const Dictionary& dictionary, const char* key, Array& out) -> bool
{
    const auto value = dictionary.get(key, Variant{});
    if (value.get_type() != Variant::ARRAY) {
        return false;
    }
    out = value;
    return true;
}

auto parseJson(const Bytes& bytes) -> Variant
{
    return JSON::parse_string(String::utf8(reinterpret_cast<const char*>(bytes.data()),
        static_cast<std::int64_t>(bytes.size())));
}

auto hexDigit(char32_t c) -> int
{
    if (c >= '0' && c <= '9') {
        return static_cast<int>(c - '0');
    }
    if (c >= 'a' && c <= 'f') {
        return static_cast<int>(c - 'a') + 10;
    }
    if (c >= 'A' && c <= 'F') {
        return static_cast<int>(c - 'A') + 10;
    }
    return -1;
}

auto guid(const String& name, std::array<std::uint8_t, 16>& out) -> bool
{
    // Exported node UUIDs are canonical D strings. The kernel ID may also be
    // a .NET N GUID, or an arbitrary named ID (encoded as SHA-256 prefix).
    const auto dashed = name.length() == 36;
    if (!dashed && name.length() != 32) {
        return false;
    }
    auto index = 0;
    for (auto lane = 0; lane < 16; ++lane) {
        if (dashed && (lane == 4 || lane == 6 || lane == 8 || lane == 10)) {
            if (name[index++] != '-') {
                return false;
            }
        }
        const auto high = hexDigit(name[index++]);
        const auto low = hexDigit(name[index++]);
        if (high < 0 || low < 0) {
            return false;
        }
        out[lane] = static_cast<std::uint8_t>((high << 4) | low);
    }
    return true;
}

auto nodeId(const String& value, std::array<std::uint8_t, 16>& out) -> bool
{
    return value.length() == 36 && guid(value, out) &&
        std::any_of(out.begin(), out.end(), [](auto byte) { return byte != 0; });
}

auto word(const std::uint8_t* bytes) -> std::uint32_t
{
    return std::uint32_t(bytes[0]) | (std::uint32_t(bytes[1]) << 8) |
        (std::uint32_t(bytes[2]) << 16) | (std::uint32_t(bytes[3]) << 24);
}

auto floatWord(const std::uint8_t* bytes) -> float
{
    const auto bits = word(bytes);
    auto value = 0.0f;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

void putWord(std::uint8_t* bytes, std::uint32_t value)
{
    for (auto i = 0u; i < 4u; ++i) {
        bytes[i] = static_cast<std::uint8_t>(value >> (i * 8));
    }
}

void putFloat(std::uint8_t* bytes, float value)
{
    auto bits = std::uint32_t{};
    std::memcpy(&bits, &value, sizeof(value));
    putWord(bytes, bits);
}

auto isFloat(std::uint32_t type) -> bool { return type == 0 || (type >= 4 && type <= 6); }
auto lanes(std::uint32_t type) -> std::uint32_t { return type >= 4 ? type - 2 : 1; }

auto validatePacket(const Kernel& kernel, std::uint32_t stage,
    std::array<bool, maxFlowResources>& flowUsed, String& error) -> Result
{
    const auto& packet = kernel.packet;
    const auto* bytes = packet.ptr();
    if (packet.size() < blockBytes || std::memcmp(bytes, "LGPK", 4) != 0) {
        return fail(error, "Missing LGPK packet header");
    }
    if (word(bytes + 4) != 1) {
        return fail(error, "Godot GPU requires LGPK version 1", Result::UnsupportedBinary);
    }
    const auto count = word(bytes + 12);
    const auto registers = word(bytes + 16);
    if (word(bytes + 8) != blockBytes || word(bytes + 20) != stage ||
        word(bytes + 24) != particleStride || word(bytes + 28) != 48 ||
        word(bytes + 48) > 1 || word(bytes + 52) || word(bytes + 56) || word(bytes + 60) ||
        count < registers || count - registers > 9 ||
        (std::uint64_t(count) + 1) * blockBytes != static_cast<std::uint64_t>(packet.size())) {
        return fail(error, "Invalid LGPK layout or stage");
    }
    if (registers > maxRegisters) {
        return fail(error, "Godot GPU supports at most 128 registers per LGPK kernel",
            Result::UnsupportedBinary);
    }
    auto key = std::array<std::uint8_t, 16>{};
    if (!guid(kernel.id, key)) {
        const auto digest = kernel.id.sha256_buffer();
        if (digest.size() != 32) {
            return fail(error, "Cannot verify LGPK kernel identity");
        }
        std::copy_n(digest.ptr(), key.size(), key.begin());
    }
    if (std::memcmp(key.data(), bytes + 32, key.size()) != 0) {
        return fail(error, "LGPK kernel identity does not match its declared ID");
    }

    constexpr auto attributeTypes = std::array{5u, 5u, 6u, 4u, 0u, 0u, 0u, 3u, 2u};
    constexpr auto uniformTypes = std::array{0u, 0u, 5u, 6u, 6u, 6u, 6u};
    auto types = std::array<std::uint32_t, maxRegisters>{};
    auto writes = 0u;
    for (auto i = 0u; i < count; ++i) {
        const auto* instruction = bytes + blockBytes * (i + 1);
        const auto op = word(instruction);
        const auto dst = word(instruction + 4);
        const auto args = std::array{word(instruction + 8), word(instruction + 12),
            word(instruction + 16), word(instruction + 28)};
        const auto type = word(instruction + 20);
        const auto aux = word(instruction + 24);
        const auto invalid = [&](const String& detail, Result code = Result::InvalidBinary) {
            return fail(error, "LGPK instruction " + String::num_uint64(i) + ": " + detail, code);
        };
        if (op > 43 || type > 6) {
            return invalid("unsupported opcode " + String::num_uint64(op) +
                " or type " + String::num_uint64(type), Result::UnsupportedBinary);
        }
        const auto basicMath = (op >= 3 && op <= 6) || (op >= 9 && op <= 11);
        auto arity = 0u;
        if (op == 14) {
            arity = lanes(type);
        } else if (op == 9 || op == 15) {
            arity = 3;
        } else if (basicMath) {
            arity = 2;
        } else if (op == 24 || op == 26 || (op >= 33 && op <= 35) || op == 37 || op == 42 || op == 43) {
            arity = 2;
        } else if (op >= 30 && op <= 32) {
            arity = 3;
        } else if (op >= 38 && op <= 40) {
            arity = 4;
        } else if (op >= 7) {
            arity = 1;
        }
        for (auto j = 0u; j < args.size(); ++j) {
            if (op == 41 && type == 5 && j == 3) {
                continue;
            }
            if ((j < arity && args[j] >= std::min(i, registers)) ||
                (j >= arity && args[j] != missing)) {
                return invalid("invalid or forward register reference");
            }
        }
        if ((i < registers && (op == 18 || dst != i)) ||
            (i >= registers && (op != 18 || dst != missing))) {
            return invalid("expected SSA instructions followed by stores");
        }
        const auto argType = [&](auto j) { return types[args[j]]; };
        auto valid = false;
        if (op == 0) {
            valid = aux == 0 && (type != 3 || word(instruction + 48) <= 1);
        } else if (op == 1 || op == 18) {
            valid = aux < attributeTypes.size() && type == attributeTypes[aux];
            if (valid && op == 18) {
                const auto mask = 1u << aux;
                valid = argType(0) == type && (writes & mask) == 0;
                writes |= mask;
            }
        } else if (op == 2) {
            valid = aux < uniformTypes.size() && type == uniformTypes[aux];
        } else if (basicMath || op == 24 || op == 26 || op == 30 || op == 31) {
            valid = aux == 0 && ((op == 26 || op == 30 || op == 31) ? isFloat(type) : type != 3);
            auto exact = false;
            for (auto j = 0u; j < arity; ++j) {
                exact |= argType(j) == type;
                valid &= argType(j) == type || (argType(j) == 0 && isFloat(type));
            }
            valid &= exact;
        } else if (op == 7 || op == 8) {
            valid = aux == 0 && isFloat(type) && argType(0) == type;
        } else if (op == 12) {
            valid = aux == 0 && type >= 4 && argType(0) == type;
        } else if (op == 13) {
            valid = aux == 0 && type == 0 && argType(0) >= 4;
        } else if (op == 14) {
            valid = aux == 0 && type >= 4;
            for (auto j = 0u; j < arity; ++j) {
                valid &= argType(j) == 0;
            }
        } else if (op == 15) {
            valid = aux == 0 && argType(0) == 3 && argType(1) == type && argType(2) == type;
        } else if (op == 16) {
            valid = aux == 0 && type == 0 && argType(0) == 2;
        } else if (op == 17) {
            valid = type == 0 && argType(0) >= 4 && aux < lanes(argType(0));
        } else if (op == 19 || op == 25) {
            valid = aux == 0 && type != 3 && argType(0) == type;
        } else if ((op >= 20 && op <= 23) || (op >= 27 && op <= 29) || op == 36) {
            valid = aux == 0 && isFloat(type) && argType(0) == type;
        } else if (op == 32) {
            valid = aux == 0 && isFloat(type) && argType(0) == type &&
                argType(1) == 4 && argType(2) == 4;
        } else if (op == 33) {
            valid = aux == 0 && type == 0 && argType(0) >= 4 && argType(0) == argType(1);
        } else if (op == 34) {
            valid = aux == 0 && type == 5 && argType(0) == 5 && argType(1) == 5;
        } else if (op == 35) {
            valid = aux == 0 && type == 0 && isFloat(argType(0)) && argType(0) == argType(1);
        } else if (op == 37) {
            valid = aux == 0 && type == 0 && argType(0) == 5 && argType(1) == 2;
        } else if (op == 41) {
            valid = (type == 4 || type == 5) && argType(0) == type && aux < flowUsed.size();
            if (valid && type == 5) {
                auto axes = args[3];
                auto u = axes & 3u;
                auto v = (axes >> 2) & 3u;
                valid = (axes & ~0x10fu) == 0 && u < 3 && v < 3 && u != v;
            }
            if (valid) {
                flowUsed[aux] = true;
            }
        } else if (op == 42 || op == 43) {
            valid = aux == 0 && type == 3 && argType(0) == 0 && argType(1) == 0;
        } else if (op >= 38 && op <= 40) {
            valid = aux == 0 && type == (op == 40 ? 5u : 0u) && argType(0) == 5 &&
                argType(1) == 2 && argType(2) == 1 && argType(3) == 0;
            if (valid) {
                const auto* octaves = bytes + blockBytes * (args[2] + 1);
                const auto* gain = bytes + blockBytes * (args[3] + 1);
                if (word(octaves) == 0) {
                    const auto value = word(octaves + 48);
                    valid &= value >= 1 && value <= 8;
                }
                if (word(gain) == 0) {
                    const auto value = floatWord(gain + 32);
                    valid &= value >= 0.0f && value <= 1.0f;
                }
            }
        }
        if (!valid) {
            return invalid("invalid operand type, attribute/uniform slot, or store");
        }
        for (auto j = 0u; j < 4u; ++j) {
            const auto floatBits = word(instruction + 32 + j * 4);
            const auto integerBits = word(instruction + 48 + j * 4);
            if (op == 0 && isFloat(type) && j < lanes(type)) {
                if (integerBits || (floatBits & 0x7f800000u) == 0x7f800000u) {
                    return invalid("invalid float immediate");
                }
            } else if (op == 41 && type == 5) {
                auto value = floatWord(instruction + 32 + j * 4);
                if ((j == 0 ? integerBits > 2 : integerBits != 0) || !std::isfinite(value) || (j >= 2 && value <= 0.0f)) {
                    return invalid("invalid FlowMap world bounds");
                }
            } else if (floatBits || (integerBits && !((op == 0 && !isFloat(type) && j == 0) || (op == 41 && j == 0 && integerBits <= 2)))) {
                return invalid("nonzero inactive immediate fields");
            }
        }
        if (i < registers) {
            types[i] = type;
        }
    }
    if (word(bytes + 48) != std::uint32_t(stage == 1 && (writes & (1u << 5)) == 0)) {
        return fail(error, "LGPK default age flag disagrees with stores");
    }
    return Result::Ok;
}

auto readContainerManifest(const Bytes& containers, const Bytes& manifest,
    std::vector<std::array<std::uint8_t, 16>>& ids, Array& programs,
    Array& fluidPrograms, bool& version2, String& error) -> Result
{
    if (!containers.empty()) {
        const auto value = parseJson(containers);
        if (value.get_type() != Variant::ARRAY) {
            return fail(error, "Invalid GPU container snapshots");
        }
        const Array snapshots = value;
        if (snapshots.size() > 16) {
            return fail(error, "More than 16 GPU containers in one scope");
        }
        for (auto i = 0; i < snapshots.size(); ++i) {
            auto snapshot = Dictionary{};
            auto id = std::array<std::uint8_t, 16>{};
            if (!object(snapshots[i], snapshot) || !nodeId(text(snapshot, "id"), id) ||
                std::find(ids.begin(), ids.end(), id) != ids.end()) {
                return fail(error, "Invalid or duplicate GPU container ID");
            }
            ids.push_back(id);
        }
    }
    if (!manifest.empty()) {
        auto outer = Dictionary{};
        if (!object(parseJson(manifest), outer) || !array(outer, "programs", programs)) {
            return fail(error, "Invalid GPU program manifest");
        }
        const auto version = number(outer, "version");
        if (version == 1) {
            version2 = false;
            if (outer.has("fluidPrograms")) {
                return fail(error, "Version 1 GPU manifests cannot contain Fluid2D programs",
                    Result::UnsupportedBinary);
            }
        } else if (version == 2 || version == 3) {
            version2 = true;
            if (!array(outer, "fluidPrograms", fluidPrograms)) {
                return fail(error, "Version 2 GPU manifests require a fluidPrograms array");
            }
        } else {
            return fail(error, "Only GPU program manifest versions 1 and 2 are supported",
                Result::UnsupportedBinary);
        }
        if (version < 3)
        {
            for (auto index = 0; index < fluidPrograms.size(); ++index)
            {
                auto fluid = Dictionary{};
                if (object(fluidPrograms[index], fluid) && fluid.has("automaticPlane"))
                {
                    return fail(error, "Fluid output selection requires GPU payload version 3", Result::UnsupportedBinary);
                }
            }
            for (auto index = 0; index < programs.size(); ++index)
            {
                auto program = Dictionary{};
                auto delivery = Dictionary{};
                if (object(programs[index], program) && object(program.get("unreal", Variant{}), delivery) &&
                    delivery.has("generatedResources"))
                {
                    return fail(error, "Generated resources require GPU payload version 3", Result::UnsupportedBinary);
                }
            }
        }
    }
    if (static_cast<std::size_t>(programs.size() + fluidPrograms.size()) != ids.size()) {
        return fail(error, "GPU program manifest does not match the container snapshots");
    }
    return Result::Ok;
}

auto readDrawMatrices(const Bytes& renderConfig, const std::vector<NodeId>& ids,
    std::vector<std::pair<NodeId, MatrixRows>>& bindings, String& error, const char* field = "gpuDrawTransforms") -> Result
{
    if (renderConfig.empty()) {
        return Result::Ok;
    }
    auto configuration = Dictionary{};
    if (!object(parseJson(renderConfig), configuration)) {
        return fail(error, "Invalid render configuration");
    }
    if (configuration.has(field)) {
        auto transforms = Array{};
        if (!array(configuration, field, transforms) ||
            static_cast<std::size_t>(transforms.size()) > ids.size()) {
            return fail(error, "Invalid GPU draw Matrix bindings");
        }
        for (auto i = 0; i < transforms.size(); ++i) {
            auto binding = Dictionary{};
            auto id = NodeId{};
            auto rows = Array{};
            if (!object(transforms[i], binding) || !nodeId(text(binding, "nodeId"), id) ||
                std::find(ids.begin(), ids.end(), id) == ids.end() ||
                std::any_of(bindings.begin(), bindings.end(), [&](const auto& entry) { return entry.first == id; }) ||
                !array(binding, "rowNodeIds", rows) || rows.size() != 4) {
                return fail(error, "Invalid GPU draw Matrix emitter or rows");
            }
            auto rowIds = MatrixRows{};
            for (auto row = 0; row < 4; ++row) {
                if (rows[row].get_type() != Variant::STRING || !nodeId(String(rows[row]), rowIds[row])) {
                    return fail(error, "Invalid GPU draw Matrix row identity");
                }
            }
            bindings.emplace_back(id, rowIds);
        }
    }
    return Result::Ok;
}

auto unsignedSetting(const Dictionary& settings, const char* key, std::uint32_t& output) -> bool
{
    const auto numeric = number(settings.get(key, Variant{}));
    if (!std::isfinite(numeric) || numeric < 0 ||
        numeric > std::numeric_limits<std::uint32_t>::max() || std::floor(numeric) != numeric) {
        return false;
    }
    output = static_cast<std::uint32_t>(numeric);
    return true;
}

auto floatSetting(const Dictionary& settings, const char* key, float& output) -> bool
{
    const auto value = number(settings, key);
    if (!std::isfinite(value) || std::abs(value) > std::numeric_limits<float>::max()) {
        return false;
    }
    output = static_cast<float>(value);
    return true;
}

auto readFluid2DSettings(const Dictionary& values, Fluid2DSettings& settings,
    String& detail) -> bool
{
    auto optionalCount = values.has("vorticity") ? 1 : 0;
    for (const auto* key : {"model", "gravity", "initialWaterDepth", "maximumWaterSpeed", "fuelBurnRate",
        "ignitionTemperature", "heatYield", "coolingRate", "sourceTemperature", "forces"})
    {
        optionalCount += values.has(key) ? 1 : 0;
    }
    if (values.size() != 22 + optionalCount) {
        detail = "Fluid2D settings have missing or unknown properties";
        return false;
    }
    const auto invalid = [&](const char* key) {
        detail = "Invalid Fluid2D setting: " + String::utf8(key);
        return false;
    };
    auto sourceEnabled = values.get("sourceEnabled", Variant{});
    if (!unsignedSetting(values, "version", settings.version)) return invalid("version");
    if (!unsignedSetting(values, "width", settings.width)) return invalid("width");
    if (!unsignedSetting(values, "height", settings.height)) return invalid("height");
    if (!unsignedSetting(values, "pressureIterations", settings.pressureIterations))
        return invalid("pressureIterations");
    if (!unsignedSetting(values, "maximumSubsteps", settings.maximumSubsteps))
        return invalid("maximumSubsteps");
    if (!floatSetting(values, "domainWidth", settings.domainWidth)) return invalid("domainWidth");
    if (!floatSetting(values, "domainHeight", settings.domainHeight)) return invalid("domainHeight");
    if (!floatSetting(values, "maximumStep", settings.maximumStep)) return invalid("maximumStep");
    if (!floatSetting(values, "velocityDissipation", settings.velocityDissipation))
        return invalid("velocityDissipation");
    if (!floatSetting(values, "densityDissipation", settings.densityDissipation))
        return invalid("densityDissipation");
    if (sourceEnabled.get_type() != Variant::BOOL) return invalid("sourceEnabled");
    if (!floatSetting(values, "sourceX", settings.sourceX)) return invalid("sourceX");
    if (!floatSetting(values, "sourceY", settings.sourceY)) return invalid("sourceY");
    if (!floatSetting(values, "sourceRadius", settings.sourceRadius)) return invalid("sourceRadius");
    if (!floatSetting(values, "sourceDensity", settings.sourceDensity)) return invalid("sourceDensity");
    if (!floatSetting(values, "sourceVelocityX", settings.sourceVelocityX))
        return invalid("sourceVelocityX");
    if (!floatSetting(values, "sourceVelocityY", settings.sourceVelocityY))
        return invalid("sourceVelocityY");
    if (!floatSetting(values, "buoyancy", settings.buoyancy)) return invalid("buoyancy");
    if (!floatSetting(values, "colorR", settings.colorR)) return invalid("colorR");
    if (!floatSetting(values, "colorG", settings.colorG)) return invalid("colorG");
    if (!floatSetting(values, "colorB", settings.colorB)) return invalid("colorB");
    if (!floatSetting(values, "colorA", settings.colorA)) return invalid("colorA");
    settings.vorticity = 0;
    if (values.has("vorticity") && !floatSetting(values, "vorticity", settings.vorticity))
        return invalid("vorticity");
    if (values.has("model"))
    {
        const auto model = text(values, "model");
        if (model == "Smoke") settings.model = 0;
        else if (model == "FreeSurface") settings.model = 1;
        else if (model == "ShallowWater") settings.model = 2;
        else if (model == "Combustion") settings.model = 3;
        else return invalid("model");
    }
    for (auto [key, target] : {std::pair{"gravity", &settings.gravity},
        {"initialWaterDepth", &settings.initialWaterDepth}, {"maximumWaterSpeed", &settings.maximumWaterSpeed},
        {"fuelBurnRate", &settings.fuelBurnRate}, {"ignitionTemperature", &settings.ignitionTemperature},
        {"heatYield", &settings.heatYield}, {"coolingRate", &settings.coolingRate}, {"sourceTemperature", &settings.sourceTemperature}})
    {
        if (values.has(key) && !floatSetting(values, key, *target))
        {
            return invalid(key);
        }
    }
    settings.forceCount = 0;
    if (values.has("forces"))
    {
        auto forces = Array{};
        if (!array(values, "forces", forces) || forces.size() > 8)
        {
            return invalid("forces");
        }
        auto forceIds = std::vector<NodeId>{};
        for (auto index = 0; index < forces.size(); ++index)
        {
            auto force = Dictionary{};
            auto id = NodeId{};
            auto& node = settings.forces[index];
            if (!object(forces[index], force) || force.size() != 10 + (force.has("inputA") ? 1 : 0) + (force.has("inputB") ? 1 : 0) || !nodeId(text(force, "id"), id) ||
                std::find(forceIds.begin(), forceIds.end(), id) != forceIds.end())
            {
                return invalid("forces.id");
            }
            const auto kind = text(force, "kind");
            if (kind == "Acceleration") node.kind = 0;
            else if (kind == "Noise") node.kind = 1;
            else if (kind == "CurlNoise") node.kind = 2;
            else if (kind == "CellPosition") node.kind = 3;
            else if (kind == "Time") node.kind = 4;
            else if (kind == "Density") node.kind = 5;
            else if (kind == "Temperature") node.kind = 6;
            else if (kind == "Add") node.kind = 7;
            else if (kind == "Multiply") node.kind = 8;
            else if (kind == "Sine") node.kind = 9;
            else if (kind == "Smoothstep") node.kind = 10;
            else return invalid("forces.kind");
            for (auto [key, target] : {std::pair{"inputA", &node.inputA}, {"inputB", &node.inputB}})
            {
                if (!force.has(key))
                {
                    continue;
                }
                auto reference = NodeId{};
                if (!nodeId(text(force, key), reference))
                {
                    return invalid("forces.input");
                }
                const auto found = std::find(forceIds.begin(), forceIds.end(), reference);
                if (found == forceIds.end() || std::distance(forceIds.begin(), found) >= index)
                {
                    return invalid("forces.input must reference an earlier node");
                }
                *target = static_cast<std::uint32_t>(std::distance(forceIds.begin(), found) + 1);
            }
            forceIds.push_back(id);
            if (!floatSetting(force, "x", node.x) || !floatSetting(force, "y", node.y) ||
                !floatSetting(force, "strength", node.strength) || !floatSetting(force, "frequency", node.frequency) ||
                !floatSetting(force, "roughness", node.roughness) || !floatSetting(force, "animationSpeed", node.animationSpeed) ||
                !unsignedSetting(force, "seed", node.seed) || !unsignedSetting(force, "octaves", node.octaves))
            {
                return invalid("forces.parameters");
            }
        }
        settings.forceCount = static_cast<std::uint32_t>(forces.size());
    }
    if (!liberafx::gpu::validFluid2DSettings(settings)) {
        detail = "Fluid2D settings are outside supported limits";
        return false;
    }
    settings.sourceEnabled = static_cast<bool>(sourceEnabled);
    return true;
}

auto readFluid2DPrograms(const Array& entries, std::vector<NodeId>& ids,
    const std::vector<std::pair<NodeId, MatrixRows>>& bindings,
    const bool version2, std::vector<Fluid2DProgram>& programs,
    String& error) -> Result
{
    if (!entries.is_empty() && !version2) {
        return fail(error, "Fluid2D requires GPU payload version 2", Result::UnsupportedBinary);
    }
    for (auto index = 0; index < entries.size(); ++index) {
        auto entry = Dictionary{};
        auto settingsValue = Dictionary{};
        auto renderer = Dictionary{};
        auto node = NodeId{};
        if (!object(entries[index], entry) || !nodeId(text(entry, "nodeId"), node) ||
            !object(entry.get("settings", Variant{}), settingsValue) ||
            !object(entry.get("renderer", Variant{}), renderer)) {
            return fail(error, "Invalid Fluid2D program entry");
        }
        const auto snapshot = std::find(ids.begin(), ids.end(), node);
        if (snapshot == ids.end()) {
            return fail(error, "Duplicate, overlapping or unmatched Fluid2D GPU container ID");
        }
        const auto material = text(renderer, "material");
        const auto size = number(renderer, "sizeScale");
        auto program = Fluid2DProgram{};
        if (entry.has("automaticPlane"))
        {
            const auto automatic = entry.get("automaticPlane", Variant{});
            if (automatic.get_type() != Variant::BOOL)
            {
                return fail(error, "Fluid automaticPlane must be a boolean");
            }
            program.automaticPlane = static_cast<bool>(automatic);
        }
        if (renderer.size() != 2) {
            return fail(error, "Fluid2D renderer must contain exactly material and sizeScale",
                Result::UnsupportedBinary);
        }
        auto settingsError = String{};
        if (!readFluid2DSettings(settingsValue, program.settings, settingsError)) {
            return fail(error, settingsError, Result::UnsupportedBinary);
        }
        const auto expectedMaterial = program.settings.model == 3 ? "builtin:fluid-fire"
            : program.settings.model == 1 || program.settings.model == 2 ? "builtin:fluid-water"
            : "builtin:fluid-alpha";
        if (material != expectedMaterial) {
            return fail(error, "Unsupported Fluid2D renderer material", Result::UnsupportedBinary);
        }
        if (!std::isfinite(size) || size <= 0 || size > 10000 ||
            size > std::numeric_limits<float>::max()) {
            return fail(error, "Fluid2D renderer sizeScale is invalid", Result::UnsupportedBinary);
        }
        program.nodeId = node;
        program.sizeScale = static_cast<float>(size);
        const auto binding = std::find_if(bindings.begin(), bindings.end(),
            [&](const auto& candidate) { return candidate.first == node; });
        if (binding != bindings.end()) {
            program.hasMatrix = true;
            program.matrixRows = binding->second;
        }
        ids.erase(snapshot);
        programs.push_back(std::move(program));
    }
    return Result::Ok;
}

auto stageIndex(const String& stage) -> int
{
    return stage == "Spawn" ? 0 : stage == "Update" ? 1 : -1;
}

auto readEmitter(const Dictionary& delivery, const Textures& textures, Emitter& emitter,
    String& error) -> Result
{
    auto metadata = Dictionary{};
    auto paths = Dictionary{};
    auto generated = Dictionary{};
    if (delivery.has("generatedResources") && !object(delivery.get("generatedResources", Variant{}), generated))
    {
        return fail(error, "Generated GPU resources must be an object");
    }
    auto packetEntries = Array{};
    auto kernelEntries = Array{};
    auto resources = Array{};
    auto renderers = Array{};
    auto flow = Array{};
    if (!object(delivery.get("metadata", Variant{}), metadata) ||
        !object(delivery.get("resourceAssetPaths", Variant{}), paths) ||
        !array(delivery, "registerKernels", packetEntries) ||
        !array(delivery, "rendererBindings", renderers) ||
        !array(metadata, "kernels", kernelEntries) || !array(metadata, "flow", flow) ||
        !array(metadata, "resources", resources)) {
        return fail(error, "Missing packaged LGPK delivery fields");
    }
    const auto runtimeExecuted = metadata.get("runtimeExecuted", Variant{});
    const auto metadataVersion = number(metadata, "version");
    if ((metadataVersion != 1 && metadataVersion != 2 && metadataVersion != 3) || text(metadata, "purpose") != "SourceExport" ||
        runtimeExecuted.get_type() != Variant::BOOL || static_cast<bool>(runtimeExecuted) ||
        text(metadata, "format") != "Usf" || number(metadata, "particleStride") != particleStride ||
        number(metadata, "uniformBufferBytes") != 48 || number(delivery, "rendererBindingsVersion") != 1) {
        return fail(error, "Unsupported packaged LGPK metadata or renderer ABI", Result::UnsupportedBinary);
    }
    const auto canonical = text(delivery, "canonicalJson");
    if (metadataVersion >= 3) {
        emitter.spawnRate = number(metadata, "spawnRate");
        if (!std::isfinite(emitter.spawnRate) || emitter.spawnRate < 0 || emitter.spawnRate > 1000000)
            return fail(error, "Invalid authored spawn rate");
    }
    const auto source = text(delivery, "shaderSource");
    if (canonical.is_empty() || source.is_empty() ||
        canonical.sha256_text() != text(metadata, "contentHash") ||
        source.sha256_text() != text(metadata, "sourceHash")) {
        return fail(error, "Packaged LGPK canonical or source hash mismatch");
    }
    const auto capacity = number(metadata, "capacity");
    if (!std::isfinite(capacity) || capacity < 1 || capacity != std::floor(capacity)) {
        return fail(error, "Invalid LGPK capacity");
    }
    if (capacity > maxParticles || packetEntries.size() != 2 || kernelEntries.size() != 2 ||
        flow.size() != 3 || renderers.size() != 1 || resources.is_empty() || resources.size() > maxFlowResources) {
        return fail(error, "Godot GPU requires one Spawn, Update and RenderBillboard, "
            "two kernels and at most 1000000 particles", Result::UnsupportedBinary);
    }
    emitter.capacity = static_cast<std::uint32_t>(capacity);
    auto used = std::array<std::array<bool, maxFlowResources>, 2>{};
    for (auto i = 0; i < 2; ++i) {
        auto packet = Dictionary{};
        if (!object(packetEntries[i], packet)) {
            return fail(error, "Invalid LGPK kernel entry");
        }
        const auto stage = stageIndex(text(packet, "stage"));
        const auto id = text(packet, "id");
        const auto encoded = text(packet, "base64");
        // Decode only bounded, canonical Base64; malformed text may otherwise
        // be partially decoded by platform parsers.
        constexpr auto maxEncodedBytes = 4 * (((maxRegisters + 10) * blockBytes + 2) / 3);
        if (stage < 0 || !token(id) || !emitter.kernels[stage].id.is_empty() || encoded.is_empty()) {
            return fail(error, "Invalid or duplicate LGPK kernel ID or stage");
        }
        if (encoded.length() > maxEncodedBytes) {
            return fail(error, "LGPK packet exceeds the Godot GPU kernel size limit",
                Result::UnsupportedBinary);
        }
        auto& kernel = emitter.kernels[stage];
        auto* marshalls = Marshalls::get_singleton();
        if (!marshalls) {
            return fail(error, "Godot Base64 decoder unavailable", Result::NotReady);
        }
        kernel.id = id;
        kernel.packet = marshalls->base64_to_raw(encoded);
        if (marshalls->raw_to_base64(kernel.packet) != encoded) {
            return fail(error, "Invalid Base64 LGPK packet");
        }
        const auto result = validatePacket(kernel, static_cast<std::uint32_t>(stage), used[stage], error);
        if (result != Result::Ok) {
            error = "Kernel " + id + ": " + error;
            return result;
        }
    }
    if (emitter.kernels[0].id == emitter.kernels[1].id) {
        return fail(error, "Duplicate LGPK kernel ID");
    }
    auto seen = std::array<bool, 2>{};
    auto expectedFlowIds = std::array<Array, 2>{};
    for (auto i = 0; i < 2; ++i) {
        auto info = Dictionary{};
        if (!object(kernelEntries[i], info)) {
            return fail(error, "Invalid LGPK kernel metadata");
        }
        const auto stage = stageIndex(text(info, "stage"));
        auto flowMaps = Array{};
        if (stage < 0 || seen[stage] || text(info, "id") != emitter.kernels[stage].id ||
            number(info, "threadsX") != 64 || number(info, "dispatchGroupsX") != (emitter.capacity + 63) / 64 ||
            (info.has("flowMapResourceIds") && !array(info, "flowMapResourceIds", flowMaps))) {
            return fail(error, "LGPK kernels disagree with their metadata");
        }
        expectedFlowIds[stage] = flowMaps;
        seen[stage] = true;
    }

    auto flowIds = std::unordered_set<std::string>{};
    auto renderId = String{};
    auto textureId = String{};
    for (auto i = 0; i < 3; ++i) {
        auto step = Dictionary{};
        if (!object(flow[i], step)) {
            return fail(error, "Invalid LGPK flow entry");
        }
        const auto id = text(step, "id");
        const auto kind = text(step, "kind");
        if (!token(id) || !flowIds.insert(textKey(id)).second) {
            return fail(error, "Invalid or duplicate LGPK flow ID");
        }
        if (i < 2 && stageIndex(kind) == i && text(step, "kernelId") == emitter.kernels[i].id &&
            step.get("resourceId", Variant{}).get_type() == Variant::NIL) {
            continue;
        }
        if (i == 2 && kind == "RenderBillboard" &&
            step.get("kernelId", Variant{}).get_type() == Variant::NIL) {
            renderId = id;
            textureId = text(step, "resourceId");
            if (token(textureId)) {
                continue;
            }
        }
        return fail(error, "Unsupported or invalid LGPK Spawn → Update → RenderBillboard flow",
            Result::UnsupportedBinary);
    }
    auto resourceIds = std::unordered_set<std::string>{};
    auto actualFlowIds = std::array<std::unordered_set<std::string>, 2>{};
    if (paths.size() + generated.size() != resources.size()) {
        return fail(error, "Missing or unexpected GPU texture paths");
    }
    for (auto i = 0u; i < maxFlowResources; ++i) {
        auto sampled = used[0][i] || used[1][i];
        if (i >= resources.size()) {
            if (sampled) {
                return fail(error, "FlowMap instruction references an unknown texture slot");
            }
            continue;
        }
        auto resource = Dictionary{};
        if (!object(resources[i], resource) || number(resource, "register") != i) {
            return fail(error, "Invalid LGPK resource register");
        }
        auto id = text(resource, "id");
        auto kind = text(resource, "kind");
        auto billboard = id == textureId;
        if (!token(id) || !resourceIds.insert(textKey(id)).second ||
            (billboard ? kind != "Texture2D" || sampled : kind != "FlowMap2D")) {
            return fail(error, "Invalid LGPK billboard or FlowMap resource");
        }
        auto pathValue = paths.get(id, Variant{});
        auto path = pathValue.get_type() == Variant::STRING ? pathValue.operator String() : String{};
        auto encoding = 0u;
        if (kind == "FlowMap2D")
        {
            auto mapping = Dictionary{};
            if (!object(resource.get("flowMap", Variant{}), mapping)) return fail(error, "Missing field sampling settings");
            const auto encoded = text(mapping, "encoding");
            if (encoded == "FloatVelocity") encoding = 1;
            else if (encoded == "FloatDensity") encoding = 2;
            else if (!encoded.is_empty() && encoded != "SignedNormalized") return fail(error, "Unknown field encoding");
            emitter.flowEncodings[i] = encoding;
            const auto coordinates = text(mapping, "coordinates");
            if (!coordinates.is_empty() && coordinates != "WorldBounds" && coordinates != "GeneratedPlane")
                return fail(error, "Unknown field coordinate mode");
            emitter.generatedPlaneCoordinates[i] = coordinates == "GeneratedPlane";
            if (emitter.generatedPlaneCoordinates[i] && !generated.has(id))
                return fail(error, "Generated-plane sampling requires a producer");
        }
        if (generated.has(id))
        {
            auto reference = Dictionary{};
            auto producer = NodeId{};
            if (paths.has(id) || !object(generated[id], reference) || reference.size() != 2 ||
                !nodeId(text(reference, "producerNodeId"), producer))
            {
                return fail(error, "Invalid generated GPU texture binding");
            }
            const auto output = text(reference, "outputId");
            if (billboard)
            {
                if (output != "DyeOutput" && output != "ColorOutput" && output != "FieldOutput") return fail(error, "Invalid generated billboard output");
                emitter.generatedTexture = true; emitter.textureProducer = producer; emitter.textureOutput = output;
            }
            else
            {
                if (!((output == "VelocityOutput" && encoding == 1) || (output == "DensityOutput" && encoding == 2)))
                    return fail(error, "Generated field output does not match its sampling encoding");
                auto slot = 0u;
                while (slot < emitter.generatedFieldCount && emitter.generatedFieldProducers[slot] != producer) ++slot;
                if (slot >= 16) return fail(error, "Too many generated field producers");
                if (slot == emitter.generatedFieldCount) emitter.generatedFieldProducers[emitter.generatedFieldCount++] = producer;
                emitter.generatedFieldIndices[i] = slot + 1;
                emitter.flowProducers[i] = producer;
            }
        }
        else if (path.is_empty() || path.length() > 4096 || !noNul(path)) {
            return fail(error, "Missing explicit GPU texture binding for " + id);
        }
        if (!generated.has(id) && (!billboard || path != "builtin:white")) {
            auto binding = textures.find(textKey(path));
            if (binding == textures.end() || binding->second.is_null()) {
                return fail(error, "Missing GPU Texture2D binding for " + path, Result::InvalidArgument);
            }
            if (billboard) {
                emitter.texture = binding->second;
            } else if (sampled) {
                emitter.flowTextures[i] = binding->second;
            }
        }
        for (auto stage = 0u; stage < used.size(); ++stage) {
            if (used[stage][i]) {
                actualFlowIds[stage].insert(textKey(id));
            }
        }
    }
    if (generated.size() != (emitter.generatedTexture ? 1 : 0) +
        static_cast<int>(std::count_if(emitter.generatedFieldIndices.begin(), emitter.generatedFieldIndices.end(), [](auto value) { return value != 0; })))
    {
        return fail(error, "Generated GPU texture binding is undeclared or duplicated");
    }
    if (resourceIds.count(textKey(textureId)) == 0) {
        return fail(error, "LGPK billboard texture is undeclared");
    }
    for (const auto& kernel : emitter.kernels)
    {
        const auto* bytes = kernel.packet.ptr();
        const auto count = word(bytes + 12);
        for (auto index = 0u; index < count; ++index)
        {
            const auto* instruction = bytes + blockBytes * (index + 1);
            if (word(instruction) != 41) continue;
            const auto resource = word(instruction + 24);
            if (word(instruction + 48) != emitter.flowEncodings[resource] ||
                (word(instruction + 20) == 5 && ((word(instruction + 28) & 0x100u) != 0) != emitter.generatedPlaneCoordinates[resource]))
                return fail(error, "Field packet and resource metadata disagree");
        }
    }
    for (auto stage = 0u; stage < used.size(); ++stage) {
        auto& actual = actualFlowIds[stage];
        for (auto i = 0; i < expectedFlowIds[stage].size(); ++i) {
            auto value = expectedFlowIds[stage][i];
            if (value.get_type() != Variant::STRING || actual.erase(textKey(value.operator String())) != 1) {
                return fail(error, "FlowMap packets disagree with kernel resource metadata");
            }
        }
        if (!actual.empty()) {
            return fail(error, "FlowMap kernel resource metadata is incomplete");
        }
    }

    auto renderer = Dictionary{};
    if (!object(renderers[0], renderer) || text(renderer, "flowId") != renderId) {
        return fail(error, "LGPK renderer binding does not match the billboard flow");
    }
    const auto material = text(renderer, "material");
    const auto size = number(renderer, "sizeScale");
    if (text(renderer, "renderer") != "builtin:billboard" ||
        (material != "builtin:unlit-alpha" && material != "builtin:unlit-additive") ||
        !std::isfinite(size) || size <= 0 || size > 10000) {
        return fail(error, "Unsupported LGPK billboard renderer, material or sizeScale",
            Result::UnsupportedBinary);
    }
    emitter.additive = material == "builtin:unlit-additive";
    emitter.sizeScale = static_cast<float>(size);
    if (emitter.sizeScale == 0) {
        return fail(error, "LGPK billboard sizeScale is below the Godot GPU float range",
            Result::UnsupportedBinary);
    }
    return Result::Ok;
}

auto storageUniform(std::int32_t binding, const RID& resource) -> Ref<RDUniform>
{
    auto uniform = Ref<RDUniform>{};
    uniform.instantiate();
    uniform->set_uniform_type(RenderingDevice::UNIFORM_TYPE_STORAGE_BUFFER);
    uniform->set_binding(binding);
    uniform->add_id(resource);
    return uniform;
}

auto uploadFlowMaps(RenderingDevice& device, Emitter& emitter, const RID& empty,
    std::vector<RID>& owned, std::uint32_t& bytesUsed, String& error) -> Result
{
    emitter.flowStorage = empty;
    if (std::none_of(emitter.flowTextures.begin(), emitter.flowTextures.end(),
        [](const auto& texture) { return texture.is_valid(); }) && emitter.generatedFieldCount == 0) {
        return Result::Ok;
    }
    if (bytesUsed > maxFlowBytes - flowHeaderBytes) {
        return fail(error, "Godot GPU FlowMap storage budget exceeded", Result::InsufficientWork);
    }
    auto packed = PackedByteArray{};
    if (packed.resize(flowHeaderBytes) != OK) {
        return fail(error, "Could not allocate Godot GPU FlowMap headers", Result::InsufficientWork);
    }
    packed.fill(0);
    for (auto slot = 0u; slot < maxFlowResources; ++slot) {
        if (emitter.generatedFieldIndices[slot] != 0)
        {
            putWord(packed.ptrw() + slot * 16 + 12, emitter.generatedFieldIndices[slot]);
            continue;
        }
        auto& texture = emitter.flowTextures[slot];
        if (texture.is_null()) {
            continue;
        }
        auto width = texture->get_width();
        auto height = texture->get_height();
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096) {
            return fail(error, "Invalid Godot GPU FlowMap dimensions");
        }
        auto bytes = static_cast<std::int64_t>(width) * height * 8;
        auto offset = packed.size();
        if (bytes + offset + bytesUsed > maxFlowBytes) {
            return fail(error, "Godot GPU FlowMap storage budget exceeded", Result::InsufficientWork);
        }
        auto image = texture->get_image();
        if (image.is_null() || image->is_empty() || image->get_width() != width || image->get_height() != height) {
            return fail(error, "Godot GPU FlowMap requires a readable Texture2D image", Result::InvalidArgument);
        }
        auto copy = image->duplicate();
        auto* linear = Object::cast_to<Image>(copy.ptr());
        if (!linear || (linear->is_compressed() && linear->decompress() != OK)) {
            return fail(error, "Could not decompress Godot GPU FlowMap", Result::UnsupportedBinary);
        }
        linear->clear_mipmaps();
        linear->convert(Image::FORMAT_RGBAF);
        auto samples = linear->get_data();
        if (samples.size() != bytes * 2 || packed.resize(offset + bytes) != OK) {
            return fail(error, "Could not stage Godot GPU FlowMap texels", Result::InsufficientWork);
        }
        auto* output = packed.ptrw();
        auto* header = output + slot * 16;
        putWord(header, static_cast<std::uint32_t>(offset / 4));
        putWord(header + 4, static_cast<std::uint32_t>(width));
        putWord(header + 8, static_cast<std::uint32_t>(height));
        auto* source = samples.ptr();
        for (auto pixel = std::int64_t{}; pixel < bytes / 8; ++pixel) {
            auto* input = source + pixel * 16 + (emitter.flowEncodings[slot] == 2 ? 8 : 0);
            if (!std::isfinite(floatWord(input)) || !std::isfinite(floatWord(input + 4))) {
                return fail(error, "Godot GPU FlowMap contains nonfinite RG data", Result::NonFinite);
            }
            std::memcpy(output + offset + pixel * 8, input, 8);
        }
        texture.unref();
    }
    auto buffer = device.storage_buffer_create(static_cast<std::uint32_t>(packed.size()), packed);
    if (!buffer.is_valid()) {
        return fail(error, "Could not upload Godot GPU FlowMap data", Result::NotReady);
    }
    bytesUsed += static_cast<std::uint32_t>(packed.size());
    owned.push_back(buffer);
    emitter.flowStorage = buffer;
    return Result::Ok;
}

auto publishParticleTexture(RenderingDevice& device, const RID& pipeline, const Emitter& emitter) -> bool
{
    const auto list = device.compute_list_begin();
    if (list < 0)
    {
        return false;
    }
    device.compute_list_bind_compute_pipeline(list, pipeline);
    device.compute_list_bind_uniform_set(list, emitter.publishUniforms, 0);
    device.compute_list_dispatch(list, (emitter.capacity + 63) / 64, 1, 1);
    device.compute_list_end();
    return true;
}

auto setupEmitter(RenderingDevice& device, const RID& shader, const RID& publishShader,
    const RID& publishPipeline, const RID& fieldSampler, const RID& emptyField, Emitter& emitter, String& error) -> Result
{
    auto initial = PackedByteArray{};
    const auto bytes = emitter.capacity * particleStride;
    if (initial.resize(bytes) != OK) {
        return fail(error, "Could not allocate Godot GPU particle staging storage", Result::InsufficientWork);
    }
    initial.fill(0);
    emitter.particles = device.storage_buffer_create(bytes, initial);
    if (!emitter.particles.is_valid()) {
        return fail(error, "Could not allocate Godot particle storage buffer", Result::NotReady);
    }
    if (emitter.fieldMappingData.resize(4096) != OK) return fail(error, "Could not stage field mappings", Result::InsufficientWork);
    emitter.fieldMappingData.fill(0);
    emitter.fieldMappingStorage = device.storage_buffer_create(4096, emitter.fieldMappingData);
    if (!emitter.fieldMappingStorage.is_valid()) return fail(error, "Could not allocate field mappings", Result::NotReady);
    for (auto& kernel : emitter.kernels) {
        kernel.storage = device.storage_buffer_create(static_cast<std::uint32_t>(kernel.packet.size()), kernel.packet);
        if (!kernel.storage.is_valid()) {
            return fail(error, "Could not upload LGPK packet " + kernel.id, Result::NotReady);
        }
        auto bindings = TypedArray<RDUniform>{};
        bindings.append(storageUniform(0, emitter.particles));
        bindings.append(storageUniform(1, kernel.storage));
        bindings.append(storageUniform(2, emitter.flowStorage));
        auto fields = Ref<RDUniform>{};
        fields.instantiate(); fields->set_binding(3);
        fields->set_uniform_type(RenderingDevice::UNIFORM_TYPE_SAMPLER_WITH_TEXTURE);
        for (auto index = 0u; index < 16; ++index)
        {
            fields->add_id(fieldSampler);
            fields->add_id(emitter.generatedFieldTextures[index].is_valid()
                ? emitter.generatedFieldTextures[index]->get_texture_rd_rid() : emptyField);
        }
        bindings.append(fields);
        bindings.append(storageUniform(4, emitter.fieldMappingStorage));
        kernel.uniforms = device.uniform_set_create(bindings, shader, 0);
        if (!kernel.uniforms.is_valid()) {
            return fail(error, "Could not bind LGPK packet " + kernel.id, Result::NotReady);
        }
        kernel.packet.clear();
    }
    const auto usage = RenderingDevice::TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice::TEXTURE_USAGE_STORAGE_BIT;
    if (!device.texture_is_format_supported_for_usage(RenderingDevice::DATA_FORMAT_R32G32B32A32_SFLOAT, usage))
    {
        return fail(error, "GPU particle presentation requires RGBA32Float storage and sampling", Result::UnsupportedBinary);
    }
    auto format = Ref<RDTextureFormat>{};
    format.instantiate();
    format->set_texture_type(RenderingDevice::TEXTURE_TYPE_2D);
    format->set_format(RenderingDevice::DATA_FORMAT_R32G32B32A32_SFLOAT);
    format->set_width(5 * std::min(emitter.capacity, 64u));
    format->set_height((emitter.capacity + 63u) / 64u);
    format->set_depth(1);
    format->set_array_layers(1);
    format->set_mipmaps(1);
    format->set_usage_bits(usage);
    auto view = Ref<RDTextureView>{};
    view.instantiate();
    emitter.output = device.texture_create(format, view);
    if (!emitter.output.is_valid())
    {
        return fail(error, "Could not allocate GPU particle presentation texture", Result::InsufficientWork);
    }
    auto image = Ref<RDUniform>{};
    image.instantiate();
    image->set_uniform_type(RenderingDevice::UNIFORM_TYPE_IMAGE);
    image->set_binding(1);
    image->add_id(emitter.output);
    auto bindings = TypedArray<RDUniform>{};
    bindings.append(storageUniform(0, emitter.particles));
    bindings.append(image);
    emitter.publishUniforms = device.uniform_set_create(bindings, publishShader, 0);
    if (!emitter.publishUniforms.is_valid())
    {
        return fail(error, "Could not bind GPU particle presentation texture", Result::NotReady);
    }
    if (!publishParticleTexture(device, publishPipeline, emitter))
    {
        return fail(error, "Could not initialize GPU particle presentation", Result::NotReady);
    }
    return Result::Ok;
}

auto resolveGeneratedEmitterTextures(Emitter& emitter, PackageFluid2DRenderer* fluids, String& error) -> Result
{
    if (emitter.generatedTexture)
    {
        emitter.texture = fluids ? fluids->outputTexture(emitter.textureProducer, emitter.containerId,
            emitter.instanceId, emitter.textureOutput != "DyeOutput" && emitter.textureOutput != "ColorOutput",
            emitter.textureOutput == "ColorOutput") : Ref<Texture2D>{};
        if (emitter.texture.is_null()) return fail(error, "Generated billboard producer is unavailable in this scope", Result::NotReady);
    }
    for (auto index = 0u; index < emitter.generatedFieldCount; ++index)
    {
        auto texture = fluids ? fluids->outputTexture(emitter.generatedFieldProducers[index], emitter.containerId,
            emitter.instanceId, true) : Ref<Texture2D>{};
        auto* native = Object::cast_to<Texture2DRD>(texture.ptr());
        if (!native) return fail(error, "Generated sampling field is unavailable in this scope", Result::NotReady);
        emitter.generatedFieldTextures[index] = Ref<Texture2DRD>(native);
    }
    return Result::Ok;
}

auto publishParticles(Emitter& emitter, SpawnableHost& host, String& error, Transform3D* drawTransform = nullptr) -> Result
{
    auto matrix = std::array<float, 12>{1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0};
    if (emitter.hasMatrix) {
        for (auto row = 0u; row < 4; ++row) {
            auto values = std::array<double, 4>{};
            auto width = std::uint8_t{};
            auto id = emitter.matrixRows[row].data();
            auto result = emitter.instanceId == 0 ? host.rootValue(id, values.data(), width)
                : host.childValue(emitter.containerId.data(), emitter.instanceId, id, values.data(), width);
            if (result != Result::Ok || width != 4) {
                return fail(error, "Could not read GPU draw Matrix from its execution scope", Result::InvalidValueType);
            }
            for (auto column = 0u; column < 4; ++column) {
                auto value = values[column];
                if (!std::isfinite(value) || std::abs(value) > std::numeric_limits<float>::max()) {
                    return fail(error, "GPU draw Matrix exceeds the render coordinate range", Result::NonFinite);
                }
                if (row < 3) {
                    matrix[row * 4 + column] = static_cast<float>(value);
                } else if (std::abs(value - (column == 3 ? 1.0 : 0.0)) > 1e-5) {
                    return fail(error, "GPU draw Matrix must be affine", Result::InvalidArgument);
                }
            }
        }
    }
    const auto basis = Basis(Vector3(matrix[0], matrix[4], matrix[8]),
        Vector3(matrix[1], matrix[5], matrix[9]), Vector3(matrix[2], matrix[6], matrix[10]));
    auto applied = Transform3D{};
    const auto converted = effectToHost(host.coordinateBasis(), Transform3D(basis, Vector3(matrix[3], matrix[7], matrix[11])), applied);
    if (converted != Result::Ok) return fail(error, "GPU coordinate conversion failed", converted);
    if (drawTransform) *drawTransform = applied;
    emitter.material->set_shader_parameter("draw_matrix", applied);
    emitter.material->set_shader_parameter("draw_is_world", emitter.hasMatrix);
    emitter.multiMesh->set_visible_instance_count(emitter.capacity);
    return Result::Ok;
}

}

struct PackageGpuRenderer::Impl {
    Ref<detail::GpuRenderTaskQueue> queue;
    godot::RenderingDevice* device = nullptr;
    godot::RID shader;
    godot::RID pipeline;
    godot::RID publishShader;
    godot::RID publishPipeline;
    godot::RID fieldSampler;
    godot::RID emptyField;
    godot::PackedByteArray parameters;
    std::vector<Emitter> emitters;
    std::vector<EmitterTemplate> templates;
    std::vector<GeneratedGpuDrawTexture> drawBindings;
    std::unique_ptr<PackageFluid2DRenderer> fluids;
    std::vector<RID> flowBuffers;
    std::uint32_t activeCapacity = 0;
    std::uint64_t rootId = 0;

    Impl()
    {
        queue.instantiate();
    }

    auto runOnRenderThread(std::function<Result()> task, String& error) -> Result
    {
        auto result = Result::NotReady;
        if (!queue->enqueue([&result, &task]() { result = task(); }))
        {
            return fail(error, "Godot RenderingServer is unavailable", Result::NotReady);
        }
        if (auto* server = RenderingServer::get_singleton())
        {
            server->force_sync();
        }
        return result;
    }

    void release(Emitter& emitter)
    {
        if (auto* node = godot::Object::cast_to<godot::Node3D>(godot::ObjectDB::get_instance(emitter.visualId))) {
            node->hide();
            node->queue_free();
        }
        if (emitter.outputTexture.is_valid())
        {
            emitter.outputTexture->set_texture_rd_rid(RID{});
        }
        auto error = String{};
        runOnRenderThread([&]() {
            for (auto& kernel : emitter.kernels) {
                if (kernel.uniforms.is_valid()) {
                    device->free_rid(kernel.uniforms);
                }
                if (kernel.storage.is_valid()) {
                    device->free_rid(kernel.storage);
                }
            }
            if (emitter.publishUniforms.is_valid())
            {
                device->free_rid(emitter.publishUniforms);
            }
            if (emitter.particles.is_valid())
            {
                device->free_rid(emitter.particles);
            }
            if (emitter.fieldMappingStorage.is_valid()) device->free_rid(emitter.fieldMappingStorage);
            if (emitter.output.is_valid())
            {
                device->free_rid(emitter.output);
            }
            return Result::Ok;
        }, error);
    }

    static void attachEmitter(godot::Node3D& root, Emitter& emitter);

    ~Impl()
    {
        fluids.reset();
        if (rootId != 0) {
            if (auto* root = godot::Object::cast_to<godot::Node3D>(godot::ObjectDB::get_instance(rootId))) {
                // Playback can end during its owner's tree-exit notification.
                // Hide immediately without changing the tree in that callback.
                root->hide();
                root->queue_free();
            }
        }
        if (!device) {
            return;
        }
        const auto release = [this](const godot::RID& resource) {
            if (resource.is_valid()) {
                device->free_rid(resource);
            }
        };
        for (auto& emitter : emitters) {
            this->release(emitter);
        }
        auto error = String{};
        runOnRenderThread([&]() {
            for (const auto& buffer : flowBuffers)
            {
                release(buffer);
            }
            release(publishPipeline);
            release(publishShader);
            release(fieldSampler);
            release(emptyField);
            release(pipeline);
            release(shader);
            return Result::Ok;
        }, error);
        // The RenderingServer owns the global device; never delete it.
    }
};

PackageGpuRenderer::PackageGpuRenderer(std::unique_ptr<Impl> impl) noexcept
    : impl_(std::move(impl))
{
}

PackageGpuRenderer::~PackageGpuRenderer() = default;

auto readGeneratedDrawBindings(const Bytes& manifest, const Bytes& renderConfig,
    const std::vector<Fluid2DProgram>& fluids, std::vector<GeneratedGpuDrawTexture>& out,
    String& error) -> Result
{
    if (manifest.empty())
    {
        return Result::Ok;
    }
    auto document = Dictionary{};
    if (!object(JSON::parse_string(String::utf8(reinterpret_cast<const char*>(manifest.data()), manifest.size())), document))
    {
        return fail(error, "Invalid generated draw manifest");
    }
    const auto value = document.get("generatedDrawBindings", Variant{});
    if (value.get_type() == Variant::NIL)
    {
        return Result::Ok;
    }
    if (value.get_type() != Variant::ARRAY || number(document, "version") != 3)
    {
        return fail(error, "Generated draw bindings require GPU payload version 3");
    }
    const auto entries = Array(value);
    auto render = Dictionary{};
    if (entries.size() > 64 || renderConfig.empty() ||
        !object(JSON::parse_string(String::utf8(reinterpret_cast<const char*>(renderConfig.data()), renderConfig.size())), render))
    {
        return fail(error, "Invalid generated draw binding configuration");
    }
    const auto nodesValue = render.get("nodes", Variant{});
    if (nodesValue.get_type() != Variant::ARRAY)
    {
        return fail(error, "Missing generated draw targets");
    }
    const auto nodes = Array(nodesValue);
    for (auto i = 0; i < entries.size(); ++i)
    {
        auto entry = Dictionary{};
        auto binding = GeneratedGpuDrawTexture{};
        if (!object(entries[i], entry) || entry.size() != 8 ||
            !nodeId(text(entry, "nodeId"), binding.nodeId) ||
            !nodeId(text(entry, "producerNodeId"), binding.producerNodeId))
        {
            return fail(error, "Invalid generated draw identity");
        }
        const auto slot = entry.get("slot", Variant{});
        const auto association = entry.get("association", Variant{});
        const auto index = entry.get("index", Variant{});
        const auto main = entry.get("mainTexture", Variant{});
        if (slot.get_type() != Variant::FLOAT && slot.get_type() != Variant::INT)
        {
            return fail(error, "Invalid generated draw slot");
        }
        const auto slotNumber = static_cast<double>(slot);
        const auto associationNumber = static_cast<double>(association);
        const auto indexNumber = static_cast<double>(index);
        if (!std::isfinite(slotNumber) || slotNumber < 0 || slotNumber > 127 || std::floor(slotNumber) != slotNumber ||
            (association.get_type() != Variant::FLOAT && association.get_type() != Variant::INT) || associationNumber != 2 ||
            (index.get_type() != Variant::FLOAT && index.get_type() != Variant::INT) || indexNumber != -1 ||
            main.get_type() != Variant::BOOL)
        {
            return fail(error, "Unsupported generated draw parameter");
        }
        binding.slot = static_cast<std::int32_t>(slotNumber);
        binding.mainTexture = static_cast<bool>(main);
        binding.name = text(entry, "name");
        binding.outputId = text(entry, "outputId");
        if ((!binding.mainTexture && binding.name.is_empty()) || binding.name.length() > 256 ||
            (binding.outputId != "ColorOutput" && binding.outputId != "DyeOutput" && binding.outputId != "FieldOutput") ||
            (binding.mainTexture && binding.outputId != "ColorOutput") ||
            std::none_of(fluids.begin(), fluids.end(), [&](const auto& fluid) { return fluid.nodeId == binding.producerNodeId; }))
        {
            return fail(error, "Invalid or cross-scope generated draw source");
        }
        auto found = false;
        for (auto n = 0; n < nodes.size(); ++n)
        {
            auto target = Dictionary{};
            auto id = NodeId{};
            if (object(nodes[n], target) && nodeId(text(target, "nodeId"), id) && id == binding.nodeId)
            {
                const auto kind = text(target, "kind");
                found = binding.mainTexture ? kind == "sprite" || kind == "flexiblePlane" : kind == "model";
            }
        }
        if (!found || std::any_of(out.begin(), out.end(), [&](const auto& other) {
            return other.nodeId == binding.nodeId && (other.mainTexture || binding.mainTexture
                ? other.mainTexture == binding.mainTexture : other.slot == binding.slot && other.name == binding.name);
        }))
        {
            return fail(error, "Invalid or duplicate generated draw target");
        }
        out.push_back(std::move(binding));
    }
    return Result::Ok;
}

auto readEmitters(const Bytes& containers, const Bytes& manifest, const Bytes& renderConfig,
    const Textures& textureBindings, std::vector<Emitter>& emitters,
    std::vector<Fluid2DProgram>& fluids, String& error) -> Result
{
    auto ids = std::vector<NodeId>{};
    auto programs = godot::Array{};
    auto fluidPrograms = godot::Array{};
    auto version2 = false;
    auto result = readContainerManifest(containers, manifest, ids, programs, fluidPrograms,
        version2, error);
    if (result != Result::Ok) {
        return result;
    }
    auto bindings = std::vector<std::pair<NodeId, MatrixRows>>{};
    result = readDrawMatrices(renderConfig, ids, bindings, error);
    if (result != Result::Ok) {
        return result;
    }
    auto particleTotal = 0u;
    auto emitterBindings = std::vector<std::pair<NodeId, MatrixRows>>{};
    result = readDrawMatrices(renderConfig, ids, emitterBindings, error, "gpuEmitterTransforms");
    if (result != Result::Ok) return result;
    for (auto i = 0; i < programs.size(); ++i) {
        auto program = godot::Dictionary{};
        auto delivery = godot::Dictionary{};
        auto id = std::array<std::uint8_t, 16>{};
        auto deliveryId = std::array<std::uint8_t, 16>{};
        if (!object(programs[i], program) || !nodeId(text(program, "nodeId"), id) ||
            !object(program.get("unreal", godot::Variant{}), delivery) ||
            !nodeId(text(delivery, "nodeId"), deliveryId) || deliveryId != id) {
            return fail(error, "Invalid packaged GPU emitter identity");
        }
        const auto matching = std::find(ids.begin(), ids.end(), id);
        if (matching == ids.end()) {
            return fail(error, "GPU program does not match a unique emitter snapshot");
        }
        ids.erase(matching);
        auto emitter = Emitter{};
        result = readEmitter(delivery, textureBindings, emitter, error);
        if (result != Result::Ok) {
            error = "GPU emitter " + text(program, "nodeId") + ": " + error;
            return result;
        }
        if (emitter.capacity > maxParticles - particleTotal) {
            return fail(error, "Godot GPU scope capacity exceeds 1000000 particles",
                Result::UnsupportedBinary);
        }
        particleTotal += emitter.capacity;
        emitter.seed = word(id.data()) ^ word(id.data() + 4) ^ word(id.data() + 8) ^ word(id.data() + 12);
        const auto emitterBinding = std::find_if(emitterBindings.begin(), emitterBindings.end(), [&](const auto& item) { return item.first == id; });
        if (emitterBinding != emitterBindings.end()) {
            emitter.hasEmitterMatrix = true;
            emitter.emitterRows = emitterBinding->second;
        }
        auto binding = std::find_if(bindings.begin(), bindings.end(), [&](const auto& item) { return item.first == id; });
        if (binding != bindings.end()) {
            emitter.hasMatrix = true;
            emitter.matrixRows = binding->second;
        }
        emitters.push_back(std::move(emitter));
    }
    result = readFluid2DPrograms(fluidPrograms, ids, bindings, version2, fluids, error);
    if (result != Result::Ok) {
        return result;
    }
    for (const auto& emitter : emitters)
    {
        if (emitter.generatedTexture && std::none_of(fluids.begin(), fluids.end(),
            [&](const auto& fluid) { return fluid.nodeId == emitter.textureProducer; }))
        {
            return fail(error, "Generated texture producer is not in the same scope", Result::UnsupportedBinary);
        }
        for (auto index = 0u; index < emitter.generatedFieldCount; ++index)
        {
            if (std::none_of(fluids.begin(), fluids.end(), [&](const auto& fluid) { return fluid.nodeId == emitter.generatedFieldProducers[index]; }))
                return fail(error, "Generated sampling producer is not in the same scope", Result::UnsupportedBinary);
        }
    }
    if (!ids.empty()) {
        return fail(error, "GPU container snapshots contain unsupported or unclaimed node IDs",
            Result::UnsupportedBinary);
    }
    return Result::Ok;
}

auto PackageGpuRenderer::drawBindings(const std::array<std::uint8_t, 16>& container) const
    -> const std::vector<GeneratedGpuDrawTexture>&
{
    if (container == NodeId{})
    {
        return impl_->drawBindings;
    }
    for (const auto& definition : impl_->templates)
    {
        if (definition.containerId == container)
        {
            return definition.drawBindings;
        }
    }
    static const auto empty = std::vector<GeneratedGpuDrawTexture>{};
    return empty;
}

auto PackageGpuRenderer::outputTexture(const std::array<std::uint8_t, 16>& producer,
    const std::array<std::uint8_t, 16>& container, const std::uint64_t instance,
    const godot::String& output) const -> godot::Ref<godot::Texture2D>
{
    if (!impl_->fluids || (output != "DyeOutput" && output != "FieldOutput" && output != "ColorOutput"))
    {
        return {};
    }
    return impl_->fluids->outputTexture(producer, container, instance,
        output == "FieldOutput", output == "ColorOutput");
}

auto PackageGpuRenderer::create(const SpawnablePackage& package, const Textures& textureBindings,
    std::unique_ptr<PackageGpuRenderer>& out, godot::String& error) -> Result
{
    error = godot::String{};
    if (!godot::Thread::is_main_thread() || out) {
        return fail(error, "GPU playback must be created on Godot's main thread with an empty result",
            Result::Busy);
    }
    auto impl = std::make_unique<Impl>();
    auto rootFluids = std::vector<Fluid2DProgram>{};
    auto result = readEmitters(package.rootGpuContainersJson, package.rootGpuProgramsJson,
        package.rootRenderConfiguration, textureBindings, impl->emitters, rootFluids, error);
    if (result == Result::Ok)
    {
        result = readGeneratedDrawBindings(package.rootGpuProgramsJson, package.rootRenderConfiguration, rootFluids, impl->drawBindings, error);
    }
    if (result != Result::Ok) {
        return result;
    }
    if (package.autoDestroyWhenIdle && (!impl->emitters.empty() || !rootFluids.empty())) {
        return fail(error, "Root GPU playback requires package AutoDestroyWhenIdle to be disabled",
            Result::UnsupportedBinary);
    }
    for (const auto& entry : package.templates) {
        auto definition = EmitterTemplate{};
        definition.containerId = entry.containerId;
        result = readEmitters(entry.gpuContainersJson, entry.gpuProgramsJson, entry.renderConfiguration,
            textureBindings, definition.emitters, definition.fluids, error);
        if (result == Result::Ok)
        {
            result = readGeneratedDrawBindings(entry.gpuProgramsJson, entry.renderConfiguration, definition.fluids, definition.drawBindings, error);
        }
        if (result != Result::Ok) {
            return result;
        }
        if (!definition.emitters.empty() || !definition.fluids.empty()) {
            impl->templates.push_back(std::move(definition));
        }
    }
    auto fluidTemplates = std::vector<Fluid2DTemplate>{};
    for (const auto& definition : impl->templates) {
        if (!definition.fluids.empty()) {
            fluidTemplates.push_back({definition.containerId, definition.fluids});
        }
    }
    if (!rootFluids.empty() || !fluidTemplates.empty()) {
        result = PackageFluid2DRenderer::create(std::move(rootFluids), std::move(fluidTemplates),
            impl->fluids, error);
        if (result != Result::Ok) {
            return result;
        }
    }
    if (impl->emitters.empty() && impl->templates.empty() && !impl->fluids) {
        return Result::Ok;
    }

    auto* server = godot::RenderingServer::get_singleton();
    const auto hasParticles = !impl->emitters.empty() ||
        std::any_of(impl->templates.begin(), impl->templates.end(),
            [](const auto& definition) { return !definition.emitters.empty(); });
    for (auto& emitter : impl->emitters)
    {
        result = resolveGeneratedEmitterTextures(emitter, impl->fluids.get(), error);
        if (result != Result::Ok) return result;
    }
    if (hasParticles) {
        result = impl->runOnRenderThread([&]() -> Result {
            impl->device = server ? server->get_rendering_device() : nullptr;
            if (!impl->device) {
                return fail(error, "Godot GPU particles require the global RenderingDevice; "
                    "Compatibility and headless/null devices are not supported", Result::NotReady);
            }
            auto sampler = Ref<RDSamplerState>{};
            sampler.instantiate(); sampler->set_min_filter(RenderingDevice::SAMPLER_FILTER_LINEAR);
            sampler->set_mag_filter(RenderingDevice::SAMPLER_FILTER_LINEAR);
            sampler->set_repeat_u(RenderingDevice::SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE);
            sampler->set_repeat_v(RenderingDevice::SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE);
            impl->fieldSampler = impl->device->sampler_create(sampler);
            auto format = Ref<RDTextureFormat>{}; format.instantiate();
            format->set_format(RenderingDevice::DATA_FORMAT_R32G32B32A32_SFLOAT);
            format->set_width(1); format->set_height(1); format->set_depth(1); format->set_array_layers(1);
            format->set_mipmaps(1); format->set_texture_type(RenderingDevice::TEXTURE_TYPE_2D);
            format->set_usage_bits(RenderingDevice::TEXTURE_USAGE_SAMPLING_BIT);
            auto view = Ref<RDTextureView>{}; view.instantiate();
            auto blank = PackedByteArray{}; blank.resize(16); blank.fill(0);
            auto images = TypedArray<PackedByteArray>{}; images.append(blank);
            impl->emptyField = impl->device->texture_create(format, view, images);
            if (!impl->fieldSampler.is_valid() || !impl->emptyField.is_valid())
                return fail(error, "Could not create native generated-field sampling bindings", Result::NotReady);
            auto source = godot::Ref<godot::RDShaderSource>{};
            source.instantiate();
            source->set_stage_source(godot::RenderingDevice::SHADER_STAGE_COMPUTE, computeSource);
            const auto spirv = impl->device->shader_compile_spirv_from_source(source);
            if (spirv.is_null()) {
                return fail(error, "Godot could not compile the embedded LGPK compute shader", Result::NotReady);
            }
            const auto compileError = spirv->get_stage_compile_error(godot::RenderingDevice::SHADER_STAGE_COMPUTE);
            if (!compileError.is_empty()) {
                return fail(error, "LGPK compute shader: " + compileError, Result::NotReady);
            }
            impl->shader = impl->device->shader_create_from_spirv(spirv, "LibraFX LGPK v1");
            if (!impl->shader.is_valid()) {
                return fail(error, "Godot could not create the LGPK compute shader", Result::NotReady);
            }
            impl->pipeline = impl->device->compute_pipeline_create(impl->shader);
            if (!impl->pipeline.is_valid()) {
                return fail(error, "Godot could not create the LGPK compute pipeline", Result::NotReady);
            }
            auto publishSourceObject = Ref<RDShaderSource>{};
            publishSourceObject.instantiate();
            publishSourceObject->set_stage_source(RenderingDevice::SHADER_STAGE_COMPUTE, publishSource);
            const auto publishSpirv = impl->device->shader_compile_spirv_from_source(publishSourceObject);
            if (publishSpirv.is_null() || !publishSpirv->get_stage_compile_error(RenderingDevice::SHADER_STAGE_COMPUTE).is_empty())
            {
                return fail(error, "Godot could not compile GPU particle presentation", Result::NotReady);
            }
            impl->publishShader = impl->device->shader_create_from_spirv(publishSpirv, "LibraFX GPU particle presentation");
            if (!impl->publishShader.is_valid())
            {
                return fail(error, "Godot could not create GPU particle presentation shader", Result::NotReady);
            }
            impl->publishPipeline = impl->device->compute_pipeline_create(impl->publishShader);
            if (!impl->publishPipeline.is_valid())
            {
                return fail(error, "Godot could not create GPU particle presentation pipeline", Result::NotReady);
            }
            if (impl->parameters.resize(112) != godot::OK) {
                return fail(error, "Could not allocate LGPK dispatch uniforms", Result::InsufficientWork);
            }
            auto emptyFlow = PackedByteArray{};
            if (emptyFlow.resize(flowHeaderBytes) != godot::OK) {
                return fail(error, "Could not allocate empty FlowMap headers", Result::InsufficientWork);
            }
            emptyFlow.fill(0);
            auto empty = impl->device->storage_buffer_create(flowHeaderBytes, emptyFlow);
            if (!empty.is_valid()) {
                return fail(error, "Could not create empty FlowMap buffer", Result::NotReady);
            }
            impl->flowBuffers.push_back(empty);
            auto flowBytes = flowHeaderBytes;
            for (auto& definition : impl->templates) {
                for (auto& emitter : definition.emitters) {
                    result = uploadFlowMaps(*impl->device, emitter, empty, impl->flowBuffers, flowBytes, error);
                    if (result != Result::Ok) {
                        return result;
                    }
                }
            }
            for (auto& emitter : impl->emitters) {
                impl->activeCapacity += emitter.capacity;
                result = uploadFlowMaps(*impl->device, emitter, empty, impl->flowBuffers, flowBytes, error);
                if (result == Result::Ok) {
                    result = setupEmitter(*impl->device, impl->shader, impl->publishShader, impl->publishPipeline,
                        impl->fieldSampler, impl->emptyField, emitter, error);
                }
                if (result != Result::Ok) {
                    return result;
                }
            }
            return Result::Ok;
        }, error);
        if (result != Result::Ok)
        {
            return result;
        }
    }
    out = std::unique_ptr<PackageGpuRenderer>(new PackageGpuRenderer(std::move(impl)));
    return Result::Ok;
}

void PackageGpuRenderer::Impl::attachEmitter(godot::Node3D& root, Emitter& emitter)
{
        auto shader = godot::Ref<godot::Shader>{};
        shader.instantiate();
        shader->set_code(godot::String("shader_type spatial;\n"
            "render_mode unshaded, cull_disabled, shadows_disabled, depth_draw_never, "
            "skip_vertex_transform, ") + (emitter.additive ? "blend_add;\n" : "blend_mix;\n") + billboardSource);
        auto material = godot::Ref<godot::ShaderMaterial>{};
        material.instantiate();
        material->set_shader(shader);
        emitter.material = material;
        emitter.outputTexture.instantiate();
        emitter.outputTexture->set_texture_rd_rid(emitter.output);
        material->set_shader_parameter("particle_state", emitter.outputTexture);
        material->set_shader_parameter("particle_size_scale", emitter.sizeScale);
        material->set_shader_parameter("use_draw_matrix", emitter.hasMatrix);
        material->set_shader_parameter("texture_premultiplied", emitter.generatedTexture && emitter.textureOutput == "DyeOutput");
        material->set_shader_parameter("texture_flip_y", emitter.generatedTexture && emitter.textureOutput != "ColorOutput");
        if (emitter.texture.is_valid()) {
            material->set_shader_parameter("particle_texture", emitter.texture);
            material->set_shader_parameter("use_particle_texture", true);
        }
        auto quad = godot::Ref<godot::QuadMesh>{};
        quad.instantiate();
        quad->set_size(godot::Vector2(2, 2));
        emitter.multiMesh.instantiate();
        emitter.multiMesh->set_mesh(quad);
        emitter.multiMesh->set_transform_format(godot::MultiMesh::TRANSFORM_3D);
        emitter.multiMesh->set_use_colors(false);
        emitter.multiMesh->set_use_custom_data(false);
        emitter.multiMesh->set_instance_count(emitter.capacity);
        for (auto index = 0u; index < emitter.capacity; ++index)
        {
            emitter.multiMesh->set_instance_transform(index, Transform3D{});
        }
        emitter.multiMesh->set_visible_instance_count(emitter.capacity);
        auto* node = memnew(godot::MultiMeshInstance3D);
        node->set_as_top_level(emitter.hasMatrix);
        node->set_multimesh(emitter.multiMesh);
        node->set_material_override(material);
        // The GPU owns particle positions; no CPU readback is used to recompute bounds.
        // Keep the single emitter draw eligible for rendering; dead slots are discarded in the shader.
        if (auto* server = godot::RenderingServer::get_singleton())
        {
            server->instance_set_ignore_culling(node->get_instance(), true);
        }
        emitter.visualId = node->get_instance_id();
        root.add_child(node);
}

auto PackageGpuRenderer::attach(godot::Node& owner, godot::String& error) -> Result
{
    error = godot::String{};
    if (!godot::Thread::is_main_thread() || !owner.is_inside_tree() ||
        impl_->rootId != 0) {
        return fail(error, "GPU playback requires an active scene owner on Godot's main thread",
            Result::NotReady);
    }
    if (impl_->device != nullptr) {
        auto* root = memnew(godot::Node3D);
        root->set_name("LibraFxGpuRoot");
        for (auto& emitter : impl_->emitters) {
            Impl::attachEmitter(*root, emitter);
        }
        const auto rootId = root->get_instance_id();
        owner.add_child(root, true);
        auto* attached = godot::Object::cast_to<godot::Node3D>(godot::ObjectDB::get_instance(rootId));
        if (!attached || attached->get_parent() != &owner) {
            if (attached) {
                attached->hide();
                attached->queue_free();
            }
            return fail(error, "Godot could not attach the GPU billboard nodes", Result::NotReady);
        }
        impl_->rootId = rootId;
    }
    if (impl_->fluids && !impl_->fluids->isEmpty()) {
        const auto result = impl_->fluids->attach(owner, error);
        if (result != Result::Ok) {
            return result;
        }
    }
    return Result::Ok;
}

auto PackageGpuRenderer::handleEvent(const SpawnableHostEvent& event, godot::String& error) -> Result
{
    error = godot::String{};
    auto containerId = NodeId{};
    std::copy_n(event.containerId, containerId.size(), containerId.data());
    auto matches = [&](const Emitter& emitter) {
        return emitter.instanceId == event.instanceId && emitter.containerId == containerId;
    };
    if (event.kind == SpawnableHostEventKind::Spawn) {
        if (impl_->fluids)
        {
            const auto result = impl_->fluids->handleEvent(event, error);
            if (result != Result::Ok)
            {
                return result;
            }
        }
        auto definition = std::find_if(impl_->templates.begin(), impl_->templates.end(),
            [&](const auto& item) { return item.containerId == containerId; });
        if (definition != impl_->templates.end() && !definition->emitters.empty()) {
            auto* root = godot::Object::cast_to<godot::Node3D>(godot::ObjectDB::get_instance(impl_->rootId));
            if (!root || event.instanceId == 0 || !std::isfinite(event.time) ||
                std::any_of(impl_->emitters.begin(), impl_->emitters.end(), matches)) {
                return fail(error, "Invalid GPU child scope");
            }
            auto capacity = impl_->activeCapacity;
            for (const auto& source : definition->emitters) {
                if (source.capacity > maxParticles - capacity) {
                    return fail(error, "Active Godot GPU particle capacity exceeds 1000000",
                        Result::InsufficientWork);
                }
                capacity += source.capacity;
            }
            for (const auto& source : definition->emitters) {
                auto& emitter = impl_->emitters.emplace_back(source);
                emitter.containerId = containerId;
                emitter.instanceId = event.instanceId;
                emitter.startTime = event.time;
                auto resolved = resolveGeneratedEmitterTextures(emitter, impl_->fluids.get(), error);
                if (resolved != Result::Ok) return resolved;
                emitter.seed ^= static_cast<std::uint32_t>(event.seed ^ (event.seed >> 32));
                auto result = impl_->runOnRenderThread([&]() {
                    return setupEmitter(*impl_->device, impl_->shader, impl_->publishShader,
                        impl_->publishPipeline, impl_->fieldSampler, impl_->emptyField, emitter, error);
                }, error);
                if (result != Result::Ok) {
                    return result;
                }
                Impl::attachEmitter(*root, emitter);
            }
            impl_->activeCapacity = capacity;
        }
    } else if (event.kind == SpawnableHostEventKind::Stop ||
        event.kind == SpawnableHostEventKind::Destroy ||
        event.kind == SpawnableHostEventKind::ForceTerminated) {
        auto all = event.kind == SpawnableHostEventKind::ForceTerminated && event.instanceId == 0;
        auto retired = std::remove_if(impl_->emitters.begin(), impl_->emitters.end(), [&](auto& emitter) {
            if (!all && !matches(emitter)) {
                return false;
            }
            impl_->activeCapacity -= emitter.capacity;
            impl_->release(emitter);
            return true;
        });
        impl_->emitters.erase(retired, impl_->emitters.end());
    }
    return event.kind != SpawnableHostEventKind::Spawn && impl_->fluids
        ? impl_->fluids->handleEvent(event, error) : Result::Ok;
}

auto PackageGpuRenderer::advance(SpawnableHost& host, double elapsed, double delta,
    godot::String& error, double spawnPerSecond) -> Result
{
    error = godot::String{};
    if (!godot::Thread::is_main_thread() ||
        (!impl_->emitters.empty() &&
            (impl_->rootId == 0 || !godot::ObjectDB::get_instance(impl_->rootId)))) {
        return fail(error, "GPU renderer is not attached on Godot's main thread", Result::NotReady);
    }
    if (!std::isfinite(elapsed) || elapsed < 0 || elapsed > std::numeric_limits<float>::max() ||
        !std::isfinite(delta) || delta < 0 || delta > std::numeric_limits<float>::max() ||
        !std::isfinite(spawnPerSecond) || spawnPerSecond < 0) {
        return fail(error, "Invalid GPU playback time, delta or spawn rate", Result::InvalidArgument);
    }
    if (host.rootFinished()) {
        return Result::Ok;
    }
    if (impl_->fluids)
    {
        const auto result = impl_->fluids->advance(host, elapsed, delta, error);
        if (result != Result::Ok)
        {
            return result;
        }
    }
    if (impl_->emitters.empty()) {
        return Result::Ok;
    }
    auto* root = Object::cast_to<Node3D>(ObjectDB::get_instance(impl_->rootId));
    for (auto& emitter : impl_->emitters)
    {
        auto consumer = Transform3D{};
        const auto presented = publishParticles(emitter, host, error, &consumer);
        if (presented != Result::Ok) return presented;
        if (!emitter.hasMatrix) consumer = (root ? root->get_global_transform() : Transform3D{}) * consumer;
        for (auto resource = 0u; resource < maxFlowResources; ++resource)
        {
            if (!emitter.generatedPlaneCoordinates[resource]) continue;
            auto mapping = std::array<Vector4, 4>{};
            const auto mapped = impl_->fluids ? impl_->fluids->fieldMapping(emitter.flowProducers[resource],
                emitter.containerId, emitter.instanceId, consumer, mapping, error) : Result::NotReady;
            if (mapped != Result::Ok) return mapped;
            for (auto row = 0u; row < 4; ++row)
                for (auto lane = 0u; lane < 4; ++lane)
                {
                    putFloat(emitter.fieldMappingData.ptrw() + (resource * 16 + row * 4 + lane) * 4, static_cast<float>(mapping[row][lane]));
                }
        }
    }
    if (delta == 0)
    {
        return Result::Ok;
    }
    for (auto& emitter : impl_->emitters) {
        if (!emitter.hasEmitterMatrix) continue;
        for (auto row = 0u; row < 4; ++row) {
            auto values = std::array<double, 4>{};
            auto width = std::uint8_t{};
            const auto result = emitter.instanceId == 0 ? host.rootValue(emitter.emitterRows[row].data(), values.data(), width)
                : host.childValue(emitter.containerId.data(), emitter.instanceId, emitter.emitterRows[row].data(), values.data(), width);
            if (result != Result::Ok || width != 4) return fail(error, "Could not read Emitter Matrix", Result::InvalidValueType);
            for (auto column = 0u; column < 4; ++column) {
                if (!std::isfinite(values[column]) || std::abs(values[column]) > std::numeric_limits<float>::max())
                    return fail(error, "Emitter Matrix is not finite", Result::NonFinite);
                if (row == 3 && std::abs(values[column] - (column == 3 ? 1.0 : 0.0)) > 1e-5)
                    return fail(error, "Emitter Matrix must be affine", Result::InvalidArgument);
                emitter.emitterTransform[row * 4 + column] = static_cast<float>(values[column]);
            }
        }
    }
    const auto dispatchResult = impl_->runOnRenderThread([&]() -> Result {
        auto& device = *impl_->device;
        for (const auto& emitter : impl_->emitters)
        {
            if (device.buffer_update(emitter.fieldMappingStorage, 0, 4096, emitter.fieldMappingData) != OK)
                return fail(error, "Could not upload generated field coordinates", Result::NotReady);
        }
        const auto list = device.compute_list_begin();
        if (list < 0) {
            return fail(error, "Godot could not begin LGPK compute", Result::NotReady);
        }
        device.compute_list_bind_compute_pipeline(list, impl_->pipeline);
        for (auto& emitter : impl_->emitters) {
            auto localTime = std::max(0.0, elapsed - emitter.startTime);
            auto localDelta = std::min(delta, localTime);
            if (localDelta == 0) {
                continue;
            }
            emitter.remainder = std::min(emitter.remainder + (emitter.spawnRate >= 0 ? emitter.spawnRate : spawnPerSecond) * localDelta, double(emitter.capacity));
            const auto count = static_cast<std::uint32_t>(emitter.remainder);
            emitter.remainder -= count;
            auto* parameters = impl_->parameters.ptrw();
            putWord(parameters, emitter.capacity);
            putWord(parameters + 4, emitter.capacity);
            putWord(parameters + 8, emitter.cursor);
            putWord(parameters + 12, count);
            putFloat(parameters + 16, static_cast<float>(localDelta));
            putFloat(parameters + 20, static_cast<float>(localTime));
            putWord(parameters + 24, emitter.seed);
            putWord(parameters + 28, emitter.frame++);
            // Simulation is emitter-local; authored Matrix applies only to presentation.
            std::fill_n(parameters + 32, 16, std::uint8_t{0});
            emitter.cursor = (emitter.cursor + count) % emitter.capacity;
            for (auto i = 0u; i < 16; ++i) putFloat(parameters + 48 + i * 4, emitter.emitterTransform[i]);
            device.compute_list_set_push_constant(list, impl_->parameters, 112);
            const auto groups = (emitter.capacity + 63) / 64;
            if (count != 0) {
                device.compute_list_bind_uniform_set(list, emitter.kernels[0].uniforms, 0);
                device.compute_list_dispatch(list, groups, 1, 1);
                device.compute_list_add_barrier(list);
            }
            device.compute_list_bind_uniform_set(list, emitter.kernels[1].uniforms, 0);
            device.compute_list_dispatch(list, groups, 1, 1);
            device.compute_list_add_barrier(list);
        }
        device.compute_list_end();
        for (const auto& emitter : impl_->emitters)
        {
            if (!publishParticleTexture(device, impl_->publishPipeline, emitter))
            {
                return fail(error, "Could not publish GPU particle presentation", Result::NotReady);
            }
        }
        return Result::Ok;
    }, error);
    if (dispatchResult != Result::Ok)
    {
        return dispatchResult;
    }
    return Result::Ok;
}

void PackageGpuRenderer::setPreviewOffset(const godot::Vector3& offset)
{
    if (auto* root = godot::Object::cast_to<godot::Node3D>(godot::ObjectDB::get_instance(impl_->rootId)))
        root->set_position(offset);
    if (impl_->fluids) impl_->fluids->setPreviewOffset(offset);
}

auto PackageGpuRenderer::emitterCount() const noexcept -> std::size_t
{
    return impl_->emitters.size();
}

#ifdef LIBERAFX_GODOT_TEST_HOOKS
auto PackageGpuRenderer::testReadParticleState(std::size_t emitterIndex,
    godot::Dictionary& fields, godot::String& error) -> Result
{
    if (!Thread::is_main_thread() || !impl_ || emitterIndex >= impl_->emitters.size())
    {
        return fail(error, "Particle diagnostic readback requires an active emitter", Result::InvalidArgument);
    }
    const auto& emitter = impl_->emitters[emitterIndex];
    auto bytes = PackedByteArray{};
    const auto result = impl_->runOnRenderThread([&]() {
        bytes = impl_->device->buffer_get_data(emitter.particles);
        return bytes.size() == emitter.capacity * particleStride ? Result::Ok : Result::NotReady;
    }, error);
    if (result != Result::Ok)
    {
        return result;
    }
    auto values = PackedFloat32Array{};
    if (values.resize(emitter.capacity * 20) != OK)
    {
        return fail(error, "Could not allocate particle diagnostic data", Result::InsufficientWork);
    }
    std::memcpy(values.ptrw(), bytes.ptr(), bytes.size());
    fields["capacity"] = static_cast<std::int64_t>(emitter.capacity);
    fields["state"] = values;
    return Result::Ok;
}

auto PackageGpuRenderer::testReadFluidFields(godot::Dictionary& fields,
    godot::String& error) -> Result
{
    if (!impl_ || !impl_->fluids) {
        return fail(error, "Test package has no Fluid2D renderer", Result::NotReady);
    }
    return impl_->fluids->testReadFields(fields, error);
}

auto PackageGpuRenderer::testDisableFluidSourceAndZeroForces(godot::String& error) -> Result
{
    if (!impl_ || !impl_->fluids) {
        return fail(error, "Test package has no Fluid2D renderer", Result::NotReady);
    }
    return impl_->fluids->testDisableSourceAndZeroForces(error);
}

auto PackageGpuRenderer::testSetFluidPressureIterations(std::uint32_t iterations,
    godot::String& error) -> Result
{
    if (!impl_ || !impl_->fluids) {
        return fail(error, "Test package has no Fluid2D renderer", Result::NotReady);
    }
    return impl_->fluids->testSetPressureIterations(iterations, error);
}

auto PackageGpuRenderer::testSetFluidDrawMatrix(const godot::PackedFloat64Array& values,
    godot::String& error) -> Result
{
    if (!impl_ || !impl_->fluids) {
        return fail(error, "Test package has no Fluid2D renderer", Result::NotReady);
    }
    return impl_->fluids->testSetDrawMatrix(values, error);
}
#endif

}
