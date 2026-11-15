#include "liberafx/Interop.h"
#include "liberafx/VfxRuntime.h"
#include "liberafx/SceneBindings.h"

#include <cstring>
#include <array>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <unordered_map>
#include <vector>

namespace {
static_assert(static_cast<int>(liberafx::Result::InstructionBudgetExceeded) == LIBRA_INSTRUCTION_BUDGET_EXCEEDED);
static_assert(sizeof(LibraValue) == 40 && sizeof(LibraAtlasFrame) == 20 && sizeof(LibraSpriteVertex) == 36);
struct NativeProgram {
    std::vector<std::uint8_t> bytes;
    std::vector<liberafx::CustomNodeBinding> bindings;
    liberafx::Program program;
};
struct NativeInstance {
    std::shared_ptr<NativeProgram> owner;
    std::vector<double> work, defaults;
    std::vector<liberafx::ValueHandle> values;
    std::vector<liberafx::ParameterHandle> parameters;
    std::size_t valueCount = 0, parameterCount = 0;
    // Destroy core state before its backing buffers and program.
    liberafx::Instance instance;
};
auto registryMutex = std::mutex{};
auto programs = std::unordered_map<LibraProgram, std::shared_ptr<NativeProgram>>{};
auto instances = std::unordered_map<LibraInstance, std::unique_ptr<NativeInstance>>{};
auto nextHandle = std::uint64_t{1};

template<class Function>
auto guarded(Function function) noexcept -> LibraResult
{
    try {
        auto lock = std::lock_guard<std::mutex>(registryMutex);
        return function();
    } catch (const std::bad_alloc&) { return LIBRA_OUT_OF_MEMORY; }
    catch (...) { return LIBRA_INTERNAL_ERROR; }
}
auto findInstance(LibraInstance handle) -> NativeInstance*
{
    auto found = instances.find(handle);
    return found == instances.end() ? nullptr : found->second.get();
}
auto inlineEnqueue(void*, liberafx::Job job, void* data) noexcept -> bool
{
    job(data);
    return true;
}
auto checkValues(NativeInstance& native, const LibraBinding* bindings,
                 std::uint32_t count, LibraValue* out) -> LibraResult
{
    if (count && (!bindings || !out)) return LIBRA_INVALID_ARGUMENT;
    for (auto i = 0u; i < count; ++i)
        if (!bindings[i] || bindings[i] > native.valueCount) return LIBRA_INVALID_HANDLE;
    return LIBRA_OK;
}
auto readValues(NativeInstance& native, const LibraBinding* bindings,
                std::uint32_t count, LibraValue* out) -> LibraResult
{
    for (auto i = 0u; i < count; ++i) {
        auto lanes = std::array<double, 4>{};
        const auto& handle = native.values[bindings[i] - 1];
        auto result = native.instance.value(handle, lanes.data());
        if (result != liberafx::Result::Ok) return static_cast<LibraResult>(result);
        out[i] = {lanes[0], lanes[1], lanes[2], lanes[3], handle.componentCount, 0};
    }
    return LIBRA_OK;
}
}

uint32_t LIBRA_INTEROP_CALL libra_abi_version(void) { return 1; }

LibraResult LIBRA_INTEROP_CALL libra_program_create(
    const uint8_t* bytes, uint64_t byteCount, LibraProgram* outProgram)
{
    if (!outProgram) return LIBRA_INVALID_ARGUMENT;
    *outProgram = 0;
    return guarded([&]() -> LibraResult {
        // Bound untrusted header-driven allocation before the core validates it.
        if (!bytes || byteCount < 12) return LIBRA_INVALID_ARGUMENT;
        if (byteCount > 256ull * 1024 * 1024 ||
            std::memcmp(bytes, "LFXB", 4) != 0) return LIBRA_INVALID_BINARY;
        auto count = std::uint32_t{};
        for (auto i = 0u; i < 4; ++i) count |= std::uint32_t{bytes[8 + i]} << (i * 8);
        if (count > byteCount / 44) return LIBRA_INVALID_BINARY;
        auto native = std::make_shared<NativeProgram>();
        native->bytes.assign(bytes, bytes + static_cast<std::size_t>(byteCount));
        native->bindings.resize(count);
        auto result = liberafx::Program::load(native->bytes.data(), native->bytes.size(),
            nullptr, 0, native->bindings.data(), native->bindings.size(), native->program);
        if (result != liberafx::Result::Ok) return static_cast<LibraResult>(result);
        if (!nextHandle) return LIBRA_INSUFFICIENT_WORK;
        auto handle = nextHandle++;
        programs.emplace(handle, std::move(native));
        *outProgram = handle;
        return LIBRA_OK;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_program_release(LibraProgram program)
{
    return guarded([&]() -> LibraResult {
        return programs.erase(program) ? LIBRA_OK : LIBRA_INVALID_HANDLE;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_instance_create(LibraProgram program, LibraInstance* outInstance)
{
    if (!outInstance) return LIBRA_INVALID_ARGUMENT;
    *outInstance = 0;
    return guarded([&]() -> LibraResult {
        auto found = programs.find(program);
        if (found == programs.end()) return LIBRA_INVALID_HANDLE;
        auto native = std::make_unique<NativeInstance>();
        native->owner = found->second;
        const auto& core = native->owner->program;
        native->work.resize((core.workBytes() + sizeof(double) - 1) / sizeof(double));
        native->defaults.resize(core.parameterValueCount());
        native->values.resize(core.nodeCount());
        native->parameters.resize(core.parameterValueCount());
        auto result = native->instance.initialize(core, native->work.data(), native->work.size(),
            native->defaults.data(), native->defaults.size());
        if (result != liberafx::Result::Ok) return static_cast<LibraResult>(result);
        if (!nextHandle) return LIBRA_INSUFFICIENT_WORK;
        auto handle = nextHandle++;
        instances.emplace(handle, std::move(native));
        *outInstance = handle;
        return LIBRA_OK;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_instance_release(LibraInstance instance)
{
    return guarded([&]() -> LibraResult {
        return instances.erase(instance) ? LIBRA_OK : LIBRA_INVALID_HANDLE;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_bind_value(
    LibraInstance instance, const uint8_t nodeId[16], LibraBinding* outBinding)
{
    if (!nodeId || !outBinding) return LIBRA_INVALID_ARGUMENT;
    *outBinding = 0;
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        auto handle = liberafx::ValueHandle{};
        auto result = native->instance.bindValue(nodeId, handle);
        if (result != liberafx::Result::Ok) return static_cast<LibraResult>(result);
        for (auto i = std::size_t{}; i < native->valueCount; ++i)
            if (native->values[i].nodeIndex == handle.nodeIndex) {
                *outBinding = static_cast<LibraBinding>(i + 1); return LIBRA_OK;
            }
        if (native->valueCount == native->values.size()) return LIBRA_INSUFFICIENT_WORK;
        native->values[native->valueCount++] = handle;
        *outBinding = static_cast<LibraBinding>(native->valueCount);
        return LIBRA_OK;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_bind_parameter(
    LibraInstance instance, const uint8_t nodeId[16], uint32_t inputSlot,
    uint32_t component, LibraBinding* outBinding)
{
    if (!nodeId || !outBinding || inputSlot > 3 || component > 3) return LIBRA_INVALID_ARGUMENT;
    *outBinding = 0;
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        auto handle = liberafx::ParameterHandle{};
        auto result = native->instance.bindParameter(nodeId, static_cast<uint8_t>(inputSlot),
            static_cast<uint8_t>(component), handle);
        if (result != liberafx::Result::Ok) return static_cast<LibraResult>(result);
        for (auto i = std::size_t{}; i < native->parameterCount; ++i) {
            const auto& other = native->parameters[i];
            if (other.nodeIndex == handle.nodeIndex && other.inputSlot == inputSlot &&
                other.component == component) {
                *outBinding = static_cast<LibraBinding>(i + 1); return LIBRA_OK;
            }
        }
        if (native->parameterCount == native->parameters.size()) return LIBRA_INSUFFICIENT_WORK;
        native->parameters[native->parameterCount++] = handle;
        *outBinding = static_cast<LibraBinding>(native->parameterCount);
        return LIBRA_OK;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_parameter_get(
    LibraInstance instance, LibraBinding binding, double* outValue)
{
    if (!outValue) return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native || !binding || binding > native->parameterCount) return LIBRA_INVALID_HANDLE;
        return static_cast<LibraResult>(native->instance.parameter(native->parameters[binding - 1], *outValue));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_parameter_set(
    LibraInstance instance, LibraBinding binding, double value)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native || !binding || binding > native->parameterCount) return LIBRA_INVALID_HANDLE;
        return static_cast<LibraResult>(native->instance.setParameter(native->parameters[binding - 1], value));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_values_read(
    LibraInstance instance, const LibraBinding* bindings, uint32_t count, LibraValue* outValues)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        auto result = checkValues(*native, bindings, count, outValues);
        return result == LIBRA_OK ? readValues(*native, bindings, count, outValues) : result;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_evaluate(
    LibraInstance instance, double time, const LibraBinding* bindings, uint32_t count, LibraValue* outValues)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        auto result = checkValues(*native, bindings, count, outValues);
        if (result != LIBRA_OK) return result;
        auto update = native->instance.updateAsync(time, inlineEnqueue, nullptr);
        if (update != liberafx::Result::Ok) return static_cast<LibraResult>(update);
        update = native->instance.result();
        return update == liberafx::Result::Ok ? readValues(*native, bindings, count, outValues)
                                             : static_cast<LibraResult>(update);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_sample_sprite_atlas(
    double frame, uint32_t columns, uint32_t rows, LibraAtlasFrame* outFrame)
{
    if (!outFrame) return LIBRA_INVALID_ARGUMENT;
    *outFrame = {};
    auto atlas = liberafx::AtlasFrame{};
    if (!liberafx::sampleSpriteAtlas(frame, columns, rows, atlas)) return LIBRA_INVALID_ARGUMENT;
    *outFrame = {atlas.frameIndex, atlas.uvRect[0], atlas.uvRect[1], atlas.uvRect[2], atlas.uvRect[3]};
    return LIBRA_OK;
}

LibraResult LIBRA_INTEROP_CALL libra_make_sprite_quad(
    const LibraAtlasFrame* frame, float width, float height, LibraSpriteVertex outVertices[4])
{
    if (!frame || !outVertices) return LIBRA_INVALID_ARGUMENT;
    auto atlas = liberafx::AtlasFrame{frame->frameIndex, {frame->u, frame->v, frame->width, frame->height}};
    liberafx::RenderVertex vertices[4]{};
    if (!liberafx::makeSpriteQuad(atlas, width, height, vertices)) return LIBRA_INVALID_ARGUMENT;
    for (auto i = 0u; i < 4; ++i) {
        const auto& vertex = vertices[i];
        outVertices[i] = {vertex.position[0], vertex.position[1], vertex.position[2],
            vertex.uv[0], vertex.uv[1], vertex.color[0], vertex.color[1], vertex.color[2], vertex.color[3]};
    }
    return LIBRA_OK;
}
