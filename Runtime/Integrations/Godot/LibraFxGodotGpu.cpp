#include "LibraFxGodotGpu.h"
#include <godot_cpp/classes/image.hpp>
#include <godot_cpp/classes/json.hpp>
#include <godot_cpp/classes/marshalls.hpp>
#include <godot_cpp/classes/multi_mesh.hpp>
#include <godot_cpp/classes/multi_mesh_instance3d.hpp>
#include <godot_cpp/classes/quad_mesh.hpp>
#include <godot_cpp/classes/rd_pipeline_specialization_constant.hpp>
#include <godot_cpp/classes/rd_shader_source.hpp>
#include <godot_cpp/classes/rd_shader_spirv.hpp>
#include <godot_cpp/classes/rd_uniform.hpp>
#include <godot_cpp/classes/rendering_device.hpp>
#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/classes/shader.hpp>
#include <godot_cpp/classes/shader_material.hpp>
#include <godot_cpp/classes/thread.hpp>
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

constexpr auto particleStride = 80u;
constexpr auto blockBytes = 64u;
constexpr auto maxRegisters = 128u; // Must match the embedded interpreter.
constexpr auto maxParticles = 16384u; // Aggregate per player, bounds render readback.
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
    RID flowStorage;
    RID particles;
    Ref<MultiMesh> multiMesh;
    std::uint64_t visualId = 0;
    NodeId containerId{};
    GeneratorInstanceId instanceId = 0;
    double startTime = 0.0;
    MatrixRows matrixRows{};
    bool hasMatrix = false;
    PackedFloat32Array drawBuffer;
    double remainder = 0.0;
    std::uint32_t cursor = 0;
    std::uint32_t frame = 0;
};

struct EmitterTemplate {
    NodeId containerId{};
    std::vector<Emitter> emitters;
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
    constexpr auto uniformTypes = std::array{0u, 0u, 5u};
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
        if (op > 41 || type > 6) {
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
        } else if (op == 24 || op == 26 || (op >= 33 && op <= 35) || op == 37) {
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
                valid = axes < 16 && u < 3 && v < 3 && u != v;
            }
            if (valid) {
                flowUsed[aux] = true;
            }
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
                if (integerBits || !std::isfinite(value) || (j >= 2 && value <= 0.0f)) {
                    return invalid("invalid FlowMap world bounds");
                }
            } else if (floatBits || (integerBits && !(op == 0 && !isFloat(type) && j == 0))) {
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
    std::vector<std::array<std::uint8_t, 16>>& ids, Array& programs, String& error) -> Result
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
        if (number(outer, "version") != 1) {
            return fail(error, "Only GPU program manifest version 1 is supported",
                Result::UnsupportedBinary);
        }
    }
    if (static_cast<std::size_t>(programs.size()) != ids.size()) {
        return fail(error, "GPU program manifest does not match the container snapshots");
    }
    return Result::Ok;
}

auto readDrawMatrices(const Bytes& renderConfig, const std::vector<NodeId>& ids,
    std::vector<std::pair<NodeId, MatrixRows>>& bindings, String& error) -> Result
{
    if (renderConfig.empty()) {
        return Result::Ok;
    }
    auto configuration = Dictionary{};
    if (!object(parseJson(renderConfig), configuration)) {
        return fail(error, "Invalid render configuration");
    }
    if (configuration.has("gpuDrawTransforms")) {
        auto transforms = Array{};
        if (!array(configuration, "gpuDrawTransforms", transforms) ||
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

auto stageIndex(const String& stage) -> int
{
    return stage == "Spawn" ? 0 : stage == "Update" ? 1 : -1;
}

auto readEmitter(const Dictionary& delivery, const Textures& textures, Emitter& emitter,
    String& error) -> Result
{
    auto metadata = Dictionary{};
    auto paths = Dictionary{};
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
    if (number(metadata, "version") != 1 || text(metadata, "purpose") != "SourceExport" ||
        runtimeExecuted.get_type() != Variant::BOOL || static_cast<bool>(runtimeExecuted) ||
        text(metadata, "format") != "Usf" || number(metadata, "particleStride") != particleStride ||
        number(metadata, "uniformBufferBytes") != 48 || number(delivery, "rendererBindingsVersion") != 1) {
        return fail(error, "Unsupported packaged LGPK metadata or renderer ABI", Result::UnsupportedBinary);
    }
    const auto canonical = text(delivery, "canonicalJson");
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
            "two kernels and at most 16384 particles", Result::UnsupportedBinary);
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
    if (paths.size() != resources.size()) {
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
        if (path.is_empty() || path.length() > 4096 || !noNul(path)) {
            return fail(error, "Missing explicit GPU texture binding for " + id);
        }
        if (!billboard || path != "builtin:white") {
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
    if (resourceIds.count(textKey(textureId)) == 0) {
        return fail(error, "LGPK billboard texture is undeclared");
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
        [](const auto& texture) { return texture.is_valid(); })) {
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
            auto* input = source + pixel * 16;
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

auto setupEmitter(RenderingDevice& device, const RID& shader, Emitter& emitter, String& error) -> Result
{
    auto initial = PackedByteArray{};
    const auto bytes = emitter.capacity * particleStride;
    if (initial.resize(bytes) != OK || emitter.drawBuffer.resize(emitter.capacity * 20) != OK) {
        return fail(error, "Could not allocate Godot GPU particle staging storage", Result::InsufficientWork);
    }
    initial.fill(0);
    emitter.drawBuffer.fill(0.0f);
    emitter.particles = device.storage_buffer_create(bytes, initial);
    if (!emitter.particles.is_valid()) {
        return fail(error, "Could not allocate Godot particle storage buffer", Result::NotReady);
    }
    for (auto& kernel : emitter.kernels) {
        kernel.storage = device.storage_buffer_create(static_cast<std::uint32_t>(kernel.packet.size()), kernel.packet);
        if (!kernel.storage.is_valid()) {
            return fail(error, "Could not upload LGPK packet " + kernel.id, Result::NotReady);
        }
        auto bindings = TypedArray<RDUniform>{};
        bindings.append(storageUniform(0, emitter.particles));
        bindings.append(storageUniform(1, kernel.storage));
        bindings.append(storageUniform(2, emitter.flowStorage));
        kernel.uniforms = device.uniform_set_create(bindings, shader, 0);
        if (!kernel.uniforms.is_valid()) {
            return fail(error, "Could not bind LGPK packet " + kernel.id, Result::NotReady);
        }
        kernel.packet.clear();
    }
    return Result::Ok;
}

auto publishParticles(RenderingDevice& device, Emitter& emitter, SpawnableHost& host, String& error) -> Result
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
    auto rowX = Vector3(matrix[0], matrix[1], matrix[2]);
    auto rowY = Vector3(matrix[4], matrix[5], matrix[6]);
    auto rowZ = Vector3(matrix[8], matrix[9], matrix[10]);
    auto scale = Vector3(rowX.length(), rowY.length(), rowZ.length());
    const auto expected = emitter.capacity * particleStride;
    const auto particles = device.buffer_get_data(emitter.particles);
    if (particles.size() != expected) {
        return fail(error, "Godot GPU particle readback did not complete", Result::NotReady);
    }
    auto* out = emitter.drawBuffer.ptrw();
    const auto* bytes = particles.ptr();
    auto visible = 0u;
    auto boundsMin = Vector3{};
    auto boundsMax = Vector3{};
    for (auto slot = 0u; slot < emitter.capacity; ++slot) {
        const auto* p = bytes + slot * particleStride;
        if (word(p + 68) == 0) {
            continue;
        }
        const auto position = Vector3(floatWord(p), floatWord(p + 4), floatWord(p + 8));
        const auto center = Vector3(rowX.dot(position) + matrix[3],
            rowY.dot(position) + matrix[7], rowZ.dot(position) + matrix[11]);
        const auto width = floatWord(p + 48) * emitter.sizeScale;
        const auto height = floatWord(p + 52) * emitter.sizeScale;
        const auto angle = floatWord(p + 56);
        const auto radius = std::max(std::abs(width), std::abs(height)) * 1.415f;
        const auto extent = scale * radius;
        const auto low = center - extent;
        const auto high = center + extent;
        if (!std::isfinite(angle) || !low.is_finite() || !high.is_finite()) {
            return fail(error, "GPU billboard exceeds the Godot render coordinate range", Result::NonFinite);
        }
        boundsMin = visible == 0 ? low : boundsMin.min(low);
        boundsMax = visible == 0 ? high : boundsMax.max(high);
        // MultiMesh format: 3 row-major affine transform rows, RGBA color,
        // then custom data (authored half-width, half-height and radians).
        auto* instance = out + visible * 20;
        const auto values = std::array<float, 20>{matrix[0], matrix[1], matrix[2], static_cast<float>(center.x),
            matrix[4], matrix[5], matrix[6], static_cast<float>(center.y),
            matrix[8], matrix[9], matrix[10], static_cast<float>(center.z),
            floatWord(p + 32), floatWord(p + 36), floatWord(p + 40), floatWord(p + 44),
            width, height, angle, 0};
        std::copy(values.begin(), values.end(), instance);
        ++visible;
    }
    const auto extent = boundsMax - boundsMin;
    if (!extent.is_finite()) {
        return fail(error, "GPU billboard bounds exceed the Godot render coordinate range", Result::NonFinite);
    }
    if (visible != 0) {
        emitter.multiMesh->set_custom_aabb(AABB(boundsMin, extent));
        emitter.multiMesh->set_buffer(emitter.drawBuffer);
    }
    emitter.multiMesh->set_visible_instance_count(visible);
    return Result::Ok;
}

}

struct PackageGpuRenderer::Impl {
    godot::RenderingDevice* device = nullptr;
    godot::RID shader;
    godot::RID pipeline;
    godot::PackedByteArray parameters;
    std::vector<Emitter> emitters;
    std::vector<EmitterTemplate> templates;
    std::vector<RID> flowBuffers;
    std::uint32_t activeCapacity = 0;
    std::uint64_t rootId = 0;

    void release(Emitter& emitter)
    {
        if (auto* node = godot::Object::cast_to<godot::Node3D>(godot::ObjectDB::get_instance(emitter.visualId))) {
            node->hide();
            node->queue_free();
        }
        for (auto& kernel : emitter.kernels) {
            if (kernel.uniforms.is_valid()) {
                device->free_rid(kernel.uniforms);
            }
            if (kernel.storage.is_valid()) {
                device->free_rid(kernel.storage);
            }
        }
        if (emitter.particles.is_valid()) {
            device->free_rid(emitter.particles);
        }
    }

    static void attachEmitter(godot::Node3D& root, Emitter& emitter);

    ~Impl()
    {
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
        for (const auto& buffer : flowBuffers) {
            release(buffer);
        }
        release(pipeline);
        release(shader);
        // create_local_rendering_device() transfers ownership to this adapter.
        memdelete(device);
    }
};

PackageGpuRenderer::PackageGpuRenderer(std::unique_ptr<Impl> impl) noexcept
    : impl_(std::move(impl))
{
}

PackageGpuRenderer::~PackageGpuRenderer() = default;

auto readEmitters(const Bytes& containers, const Bytes& manifest, const Bytes& renderConfig,
    const Textures& textureBindings, std::vector<Emitter>& emitters, String& error) -> Result
{
    auto ids = std::vector<NodeId>{};
    auto programs = godot::Array{};
    auto result = readContainerManifest(containers, manifest, ids, programs, error);
    if (result != Result::Ok) {
        return result;
    }
    auto bindings = std::vector<std::pair<NodeId, MatrixRows>>{};
    result = readDrawMatrices(renderConfig, ids, bindings, error);
    if (result != Result::Ok) {
        return result;
    }
    auto particleTotal = 0u;
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
            return fail(error, "Godot GPU scope capacity exceeds 16384 particles",
                Result::UnsupportedBinary);
        }
        particleTotal += emitter.capacity;
        emitter.seed = word(id.data()) ^ word(id.data() + 4) ^ word(id.data() + 8) ^ word(id.data() + 12);
        auto binding = std::find_if(bindings.begin(), bindings.end(), [&](const auto& item) { return item.first == id; });
        if (binding != bindings.end()) {
            emitter.hasMatrix = true;
            emitter.matrixRows = binding->second;
        }
        emitters.push_back(std::move(emitter));
    }
    return Result::Ok;
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
    auto result = readEmitters(package.rootGpuContainersJson, package.rootGpuProgramsJson,
        package.rootRenderConfiguration, textureBindings, impl->emitters, error);
    if (result != Result::Ok) {
        return result;
    }
    if (package.autoDestroyWhenIdle && !impl->emitters.empty()) {
        return fail(error, "Root GPU playback requires package AutoDestroyWhenIdle to be disabled",
            Result::UnsupportedBinary);
    }
    for (const auto& entry : package.templates) {
        auto definition = EmitterTemplate{};
        definition.containerId = entry.containerId;
        result = readEmitters(entry.gpuContainersJson, entry.gpuProgramsJson, entry.renderConfiguration,
            textureBindings, definition.emitters, error);
        if (result != Result::Ok) {
            return result;
        }
        if (!definition.emitters.empty()) {
            impl->templates.push_back(std::move(definition));
        }
    }
    if (impl->emitters.empty() && impl->templates.empty()) {
        return Result::Ok;
    }

    auto* server = godot::RenderingServer::get_singleton();
    if (server && server->get_rendering_device()) {
        impl->device = server->create_local_rendering_device();
    }
    if (!impl->device) {
        return fail(error, "Godot GPU playback requires a RenderingDevice renderer with local compute; "
            "Compatibility and headless/null devices are not supported", Result::NotReady);
    }
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
    if (impl->parameters.resize(48) != godot::OK) {
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
            result = setupEmitter(*impl->device, impl->shader, emitter, error);
        }
        if (result != Result::Ok) {
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
        material->set_shader_parameter("use_draw_matrix", emitter.hasMatrix);
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
        emitter.multiMesh->set_use_colors(true);
        emitter.multiMesh->set_use_custom_data(true);
        emitter.multiMesh->set_instance_count(emitter.capacity);
        emitter.multiMesh->set_visible_instance_count(0);
        auto* node = memnew(godot::MultiMeshInstance3D);
        node->set_multimesh(emitter.multiMesh);
        node->set_material_override(material);
        emitter.visualId = node->get_instance_id();
        root.add_child(node);
}

auto PackageGpuRenderer::attach(godot::Node& owner, godot::String& error) -> Result
{
    error = godot::String{};
    if (!godot::Thread::is_main_thread() || !owner.is_inside_tree() || impl_->rootId != 0) {
        return fail(error, "GPU playback requires an active scene owner on Godot's main thread",
            Result::NotReady);
    }
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
    return Result::Ok;
}

auto PackageGpuRenderer::handleEvent(const SpawnableHostEvent& event, godot::String& error) -> Result
{
    auto containerId = NodeId{};
    std::copy_n(event.containerId, containerId.size(), containerId.data());
    auto matches = [&](const Emitter& emitter) {
        return emitter.instanceId == event.instanceId && emitter.containerId == containerId;
    };
    if (event.kind == SpawnableHostEventKind::Spawn) {
        auto definition = std::find_if(impl_->templates.begin(), impl_->templates.end(),
            [&](const auto& item) { return item.containerId == containerId; });
        if (definition == impl_->templates.end()) {
            return Result::Ok;
        }
        auto* root = godot::Object::cast_to<godot::Node3D>(godot::ObjectDB::get_instance(impl_->rootId));
        if (!root || event.instanceId == 0 || !std::isfinite(event.time) ||
            std::any_of(impl_->emitters.begin(), impl_->emitters.end(), matches)) {
            return fail(error, "Invalid GPU child scope");
        }
        auto capacity = impl_->activeCapacity;
        for (const auto& source : definition->emitters) {
            if (source.capacity > maxParticles - capacity) {
                return fail(error, "Active Godot GPU particle capacity exceeds 16384",
                    Result::InsufficientWork);
            }
            capacity += source.capacity;
        }
        for (const auto& source : definition->emitters) {
            auto& emitter = impl_->emitters.emplace_back(source);
            emitter.containerId = containerId;
            emitter.instanceId = event.instanceId;
            emitter.startTime = event.time;
            emitter.seed ^= static_cast<std::uint32_t>(event.seed ^ (event.seed >> 32));
            auto result = setupEmitter(*impl_->device, impl_->shader, emitter, error);
            if (result != Result::Ok) {
                return result;
            }
            Impl::attachEmitter(*root, emitter);
        }
        impl_->activeCapacity = capacity;
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
    return Result::Ok;
}

auto PackageGpuRenderer::advance(SpawnableHost& host, double elapsed, double delta,
    godot::String& error, double spawnPerSecond) -> Result
{
    error = godot::String{};
    if (!godot::Thread::is_main_thread() || impl_->rootId == 0 ||
        !godot::ObjectDB::get_instance(impl_->rootId)) {
        return fail(error, "GPU renderer is not attached on Godot's main thread", Result::NotReady);
    }
    if (!std::isfinite(elapsed) || elapsed < 0 || elapsed > std::numeric_limits<float>::max() ||
        !std::isfinite(delta) || delta < 0 || delta > std::numeric_limits<float>::max() ||
        !std::isfinite(spawnPerSecond) || spawnPerSecond < 0) {
        return fail(error, "Invalid GPU playback time, delta or spawn rate", Result::InvalidArgument);
    }
    if (host.rootFinished() || impl_->emitters.empty()) {
        return Result::Ok;
    }
    if (delta == 0) {
        for (auto& emitter : impl_->emitters) {
            const auto result = publishParticles(*impl_->device, emitter, host, error);
            if (result != Result::Ok) {
                return result;
            }
        }
        return Result::Ok;
    }
    auto& device = *impl_->device;
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
        emitter.remainder = std::min(emitter.remainder + spawnPerSecond * localDelta, double(emitter.capacity));
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
        device.compute_list_set_push_constant(list, impl_->parameters, 48);
        const auto groups = (emitter.capacity + 63) / 64;
        if (count != 0) {
            device.compute_list_bind_uniform_set(list, emitter.kernels[0].uniforms, 0);
            device.compute_list_dispatch(list, groups, 1, 1);
            device.compute_list_add_barrier(list);
        }
        device.compute_list_bind_uniform_set(list, emitter.kernels[1].uniforms, 0);
        device.compute_list_dispatch(list, groups, 1, 1);
    }
    device.compute_list_end();
    device.submit();
    device.sync();
    // The local RenderingDevice cannot give its buffers to the scene
    // renderer. Read back once per emitter for presentation only. All graph
    // instructions and particle lifecycle math above ran on the GPU.
    for (auto& emitter : impl_->emitters) {
        const auto result = publishParticles(device, emitter, host, error);
        if (result != Result::Ok) {
            return result;
        }
    }
    return Result::Ok;
}

auto PackageGpuRenderer::emitterCount() const noexcept -> std::size_t
{
    return impl_->emitters.size();
}

}
