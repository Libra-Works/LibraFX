#include "liberafx/Interop.h"
#include "liberafx/VfxRuntime.h"
#include "liberafx/SceneBindings.h"
#include "liberafx/GeneratorPool.h"
#include <cmath>
#include "liberafx/BuiltinSignals.h"
#include "liberafx/NamedIo.h"
#include "liberafx/EffectLifecycle.h"
#include "liberafx/SpawnableHost.h"
#include "liberafx/SpawnablePackage.h"

#include <algorithm>
#include <cstring>
#include <array>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <optional>
#include <unordered_map>
#include <vector>
#include <type_traits>

namespace {
static_assert(static_cast<int>(liberafx::Result::InstructionBudgetExceeded) == LIBRA_INSTRUCTION_BUDGET_EXCEEDED);
static_assert(sizeof(LibraValue) == 40 && sizeof(LibraAtlasFrame) == 20 && sizeof(LibraSpriteVertex) == 36);
static_assert(sizeof(LibraGameIoEvent) == 64 && sizeof(LibraGameIoEventV2) == 160);
static_assert(sizeof(LibraSpawnableHostEvent) == 64);
static_assert(std::is_standard_layout<LibraValue>::value && std::is_trivially_copyable<LibraValue>::value);
static_assert(std::is_standard_layout<LibraAtlasFrame>::value && std::is_trivially_copyable<LibraAtlasFrame>::value);
static_assert(std::is_standard_layout<LibraSpriteVertex>::value && std::is_trivially_copyable<LibraSpriteVertex>::value);
template<class T>
struct AlignedAllocator {
    using value_type = T;
    using propagate_on_container_move_assignment = std::true_type;
    using is_always_equal = std::false_type;
    static constexpr auto minimumAlignment = alignof(T) > 16 ? alignof(T) : 16;
    std::size_t alignment = minimumAlignment;
    AlignedAllocator() noexcept = default;
    explicit AlignedAllocator(std::size_t required) noexcept
        : alignment(required > minimumAlignment ? required : minimumAlignment) {}
    template<class U> AlignedAllocator(const AlignedAllocator<U>& other) noexcept
        : AlignedAllocator(other.alignment) {}
    auto allocate(std::size_t count) -> T*
    {
        if (count > std::numeric_limits<std::size_t>::max() / sizeof(T)) throw std::bad_array_new_length{};
        return static_cast<T*>(::operator new(count * sizeof(T), std::align_val_t{alignment}));
    }
    void deallocate(T* memory, std::size_t) noexcept
    {
        ::operator delete(memory, std::align_val_t{alignment});
    }
    template<class U> auto operator==(const AlignedAllocator<U>& other) const noexcept -> bool
    {
        return alignment == other.alignment;
    }
    template<class U> auto operator!=(const AlignedAllocator<U>& other) const noexcept -> bool
    {
        return !(*this == other);
    }
};
struct NativeGeneratorPool;
struct SpawnContext {
    std::shared_ptr<NativeGeneratorPool> owner;
    LibraSpawnableBinding binding{};
};
struct SpawnSignalInputContext {
    std::shared_ptr<NativeGeneratorPool> owner;
    LibraSpawnableSignalInputBinding binding{};
};
struct NativeProgram {
    std::vector<std::uint8_t> bytes;
    std::vector<liberafx::CustomNodeBinding> bindings;
    std::vector<SpawnContext> spawnContexts;
    std::vector<SpawnSignalInputContext> spawnSignalInputContexts;
    std::vector<liberafx::CustomNodeDescriptor> descriptors;
    liberafx::Program program;
};
struct NativeInstance {
    std::shared_ptr<NativeProgram> owner;
    std::vector<double, AlignedAllocator<double>> work, defaults;
    std::vector<liberafx::ValueHandle> values;
    std::vector<liberafx::ParameterHandle> parameters;
    std::size_t valueCount = 0, parameterCount = 0;
    // Destroy core state before its backing buffers and program.
    liberafx::Instance instance;
    std::unique_ptr<liberafx::NamedIo> gameIo;
    std::optional<liberafx::EffectLifecycle> lifecycle;
    bool rawStarted = false;
    bool gameIoStarted = false;
};
struct NativeGeneratorPool {
    std::shared_ptr<NativeProgram> owner;
    std::unique_ptr<liberafx::GeneratorPool> pool;
    std::array<std::uint8_t, 16> containerId{};
};
auto registryMutex = std::mutex{};
auto programs = std::unordered_map<LibraProgram, std::shared_ptr<NativeProgram>>{};
auto instances = std::unordered_map<LibraInstance, std::unique_ptr<NativeInstance>>{};
auto generatorPools = std::unordered_map<LibraGeneratorPool, std::shared_ptr<NativeGeneratorPool>>{};
auto spawnableHosts = std::unordered_map<LibraSpawnableHostHandle,
    std::unique_ptr<liberafx::SpawnableHost>>{};
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
auto findSpawnableHost(LibraSpawnableHostHandle handle) -> liberafx::SpawnableHost*
{
    auto found = spawnableHosts.find(handle);
    return found == spawnableHosts.end() ? nullptr : found->second.get();
}
auto inlineEnqueue(void*, liberafx::Job job, void* data) noexcept -> bool
{
    job(data);
    return true;
}
auto spawnSignal(liberafx::CustomNodeInvocation& call) noexcept -> liberafx::Result
{
    auto& context = *static_cast<SpawnContext*>(call.context);
    auto captures = std::array<liberafx::GeneratorCapture, 16>{};
    auto count = std::size_t{};
    for (auto input = 0u; input < context.binding.inputCount; ++input) {
        for (auto lane = 0u; lane < context.binding.widths[input]; ++lane) {
            auto& capture = captures[count++];
            std::memcpy(capture.nodeId, context.binding.templateNodeIds[input], 16);
            capture.component = static_cast<std::uint8_t>(lane);
            capture.value = call.inputs[input * 4 + lane];
        }
    }
    call.selectedBranch = 0;
    return context.owner->pool->enqueueSpawn(call.time, captures.data(), count);
}
auto dispatchSpawnSignalInput(liberafx::CustomNodeInvocation& call) noexcept -> liberafx::Result
{
    auto& context = *static_cast<SpawnSignalInputContext*>(call.context);
    call.selectedBranch = 0;
    return context.owner->pool->dispatchSignal(context.binding.signalNodeId, call.time, 10000);
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
        alignas(16) auto lanes = std::array<double, 4>{};
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
LibraResult LIBRA_INTEROP_CALL libra_program_create_spawnable(
    const uint8_t* bytes, uint64_t byteCount, const LibraSpawnableBinding* bindings,
    uint32_t bindingCount, LibraProgram* outProgram)
{
    return libra_program_create_spawnable_v2(bytes, byteCount, bindings, bindingCount, nullptr, 0, outProgram);
}
LibraResult LIBRA_INTEROP_CALL libra_program_create_spawnable_v2(
    const uint8_t* bytes, uint64_t byteCount, const LibraSpawnableBinding* bindings,
    uint32_t bindingCount, const LibraSpawnableSignalInputBinding* signalInputs,
    uint32_t signalInputCount, LibraProgram* outProgram)
{
    if (!outProgram || !bytes || (bindingCount && !bindings) ||
        (signalInputCount && !signalInputs) || bindingCount > 4096 || signalInputCount > 4096)
        return LIBRA_INVALID_ARGUMENT;
    *outProgram = 0;
    return guarded([&]() -> LibraResult {
        if (byteCount < 16 || byteCount > 256ull * 1024 * 1024 ||
            std::memcmp(bytes, "LFXB", 4) != 0 || bytes[4] < 3 || bytes[4] > 7 || bytes[5] != 0 ||
            bytes[12] != 0 || bytes[13] != 1) return LIBRA_INVALID_BINARY;
        auto count = std::uint32_t{};
        for (auto i = 0u; i < 4; ++i) count |= std::uint32_t{bytes[8 + i]} << (i * 8);
        const auto instructionBytes = 16ull + 256ull * count;
        if (count > 10000 || byteCount < instructionBytes ||
            (bytes[4] == 3 && byteCount != instructionBytes) ||
            bindingCount + signalInputCount > count)
            return LIBRA_INVALID_BINARY;
        auto native = std::make_shared<NativeProgram>();
        native->bytes.assign(bytes, bytes + static_cast<std::size_t>(byteCount));
        native->bindings.resize(count);
        native->spawnContexts.resize(bindingCount);
        native->spawnSignalInputContexts.resize(signalInputCount);
        native->descriptors.resize(static_cast<std::size_t>(bindingCount) + signalInputCount + 1);
        const auto& passthroughId = liberafx::signalPassthroughTypeId;
        auto& pass = native->descriptors.back();
        std::memcpy(pass.typeId, passthroughId.data(), 16);
        pass.abiVersion = 1;
        pass.execution = liberafx::CustomExecution::Signal;
        pass.branchCount = 1;
        pass.function = liberafx::signalPassthrough;
        for (auto i = 0u; i < bindingCount; ++i) {
            const auto& binding = bindings[i];
            if (binding.inputCount > 4 || binding.reserved ||
                std::memcmp(binding.signalTypeId, passthroughId.data(), 16) == 0)
                return LIBRA_INVALID_ARGUMENT;
            auto pool = generatorPools.find(binding.pool);
            if (pool == generatorPools.end()) return LIBRA_INVALID_HANDLE;
            if (std::memcmp(pool->second->containerId.data(), binding.containerId, 16) != 0)
                return LIBRA_INVALID_ARGUMENT;
            auto matches = 0u;
            for (auto index = 0u; index < count; ++index) {
                auto record = bytes + 16 + std::size_t{index} * 256;
                if (record[16] == 41 && std::memcmp(record + 24, binding.signalTypeId, 16) == 0) {
                    if (std::memcmp(record, binding.containerId, 16) != 0) return LIBRA_INVALID_BINARY;
                    ++matches;
                }
            }
            if (matches != 1) return LIBRA_INVALID_BINARY;
            for (auto j = 0u; j < i; ++j)
                if (std::memcmp(bindings[j].signalTypeId, binding.signalTypeId, 16) == 0 ||
                    std::memcmp(bindings[j].containerId, binding.containerId, 16) == 0)
                    return LIBRA_INVALID_ARGUMENT;
            auto& context = native->spawnContexts[i];
            context.owner = pool->second;
            context.binding = binding;
            auto& descriptor = native->descriptors[i];
            std::memcpy(descriptor.typeId, binding.signalTypeId, 16);
            descriptor.abiVersion = 1;
            descriptor.execution = liberafx::CustomExecution::Signal;
            descriptor.inputCount = static_cast<std::uint8_t>(binding.inputCount);
            descriptor.branchCount = 1;
            descriptor.function = spawnSignal;
            descriptor.context = &context;
            for (auto j = 0u; j < binding.inputCount; ++j) {
                if (binding.widths[j] < 1 || binding.widths[j] > 4 || binding.colors[j] > 1 ||
                    (binding.colors[j] && binding.widths[j] != 4))
                    return LIBRA_INVALID_ARGUMENT;
                descriptor.inputWidths[j] = binding.widths[j];
                descriptor.inputIsColor[j] = binding.colors[j];
            }
        }
        for (auto i = 0u; i < signalInputCount; ++i) {
            const auto& binding = signalInputs[i];
            if (std::memcmp(binding.signalTypeId, passthroughId.data(), 16) == 0)
                return LIBRA_INVALID_ARGUMENT;
            auto pool = generatorPools.find(binding.pool);
            if (pool == generatorPools.end()) return LIBRA_INVALID_HANDLE;
            if (std::memcmp(pool->second->containerId.data(), binding.containerId, 16) != 0)
                return LIBRA_INVALID_ARGUMENT;
            auto matches = 0u;
            for (auto index = 0u; index < count; ++index) {
                auto record = bytes + 16 + std::size_t{index} * 256;
                if (record[16] != 41 || std::memcmp(record, binding.proxyNodeId, 16) != 0 ||
                    std::memcmp(record + 24, binding.signalTypeId, 16) != 0) continue;
                ++matches;
            }
            if (matches != 1) return LIBRA_INVALID_BINARY;
            for (auto j = 0u; j < bindingCount; ++j)
                if (std::memcmp(bindings[j].signalTypeId, binding.signalTypeId, 16) == 0)
                    return LIBRA_INVALID_ARGUMENT;
            for (auto j = 0u; j < i; ++j)
                if (std::memcmp(signalInputs[j].signalTypeId, binding.signalTypeId, 16) == 0 ||
                    std::memcmp(signalInputs[j].proxyNodeId, binding.proxyNodeId, 16) == 0)
                    return LIBRA_INVALID_ARGUMENT;
            auto& context = native->spawnSignalInputContexts[i];
            context.owner = pool->second;
            context.binding = binding;
            auto& descriptor = native->descriptors[static_cast<std::size_t>(bindingCount) + i];
            std::memcpy(descriptor.typeId, binding.signalTypeId, 16);
            descriptor.abiVersion = 1;
            descriptor.execution = liberafx::CustomExecution::Signal;
            descriptor.branchCount = 1;
            descriptor.function = dispatchSpawnSignalInput;
            descriptor.context = &context;
        }
        auto result = liberafx::Program::load(native->bytes.data(), native->bytes.size(),
            native->descriptors.data(), native->descriptors.size(), native->bindings.data(),
            native->bindings.size(), native->program);
        if (result != liberafx::Result::Ok) return static_cast<LibraResult>(result);
        if (!nextHandle) return LIBRA_INSUFFICIENT_WORK;
        auto handle = nextHandle++;
        programs.emplace(handle, std::move(native));
        *outProgram = handle;
        return LIBRA_OK;
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
        native->work = decltype(native->work){AlignedAllocator<double>{core.workAlignment()}};
        native->defaults = decltype(native->defaults){AlignedAllocator<double>{core.defaultsAlignment()}};
        native->work.resize((core.workBytes() + sizeof(double) - 1) / sizeof(double));
        native->defaults.resize(core.parameterValueCount());
        if ((!native->work.empty() && reinterpret_cast<std::uintptr_t>(native->work.data()) %
                native->work.get_allocator().alignment != 0) ||
            (!native->defaults.empty() && reinterpret_cast<std::uintptr_t>(native->defaults.data()) %
                native->defaults.get_allocator().alignment != 0))
            return LIBRA_INTERNAL_ERROR;
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
LibraResult LIBRA_INTEROP_CALL libra_instance_set_time_context(
    LibraInstance instance, double effectTime, double frameDelta)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        return native ? static_cast<LibraResult>(native->instance.setTimeContext(effectTime, frameDelta)) :
            LIBRA_INVALID_HANDLE;
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
        if (native->lifecycle) return LIBRA_NOT_READY;
        auto result = checkValues(*native, bindings, count, outValues);
        if (result != LIBRA_OK) return result;
        auto update = native->instance.updateAsync(time, inlineEnqueue, nullptr);
        if (update != liberafx::Result::Ok) return static_cast<LibraResult>(update);
        native->rawStarted = true;
        update = native->instance.result();
        return update == liberafx::Result::Ok ? readValues(*native, bindings, count, outValues)
                                             : static_cast<LibraResult>(update);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_start(
    LibraInstance instance, double duration, uint32_t infinite,
    const uint8_t onStartNodeId[16], uint32_t instructionBudget, LibraLifetimeSample* outSample)
{
    if (!outSample || infinite > 1 || (onStartNodeId && !instructionBudget))
        return LIBRA_INVALID_ARGUMENT;
    auto lifetime = liberafx::Lifetime::infinite();
    if (!infinite && liberafx::Lifetime::finite(duration, lifetime) != liberafx::Result::Ok)
        return LIBRA_INVALID_ARGUMENT;
    *outSample = {};
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (native->rawStarted || native->gameIoStarted) return LIBRA_NOT_READY;
        if (!native->lifecycle) {
            if (native->gameIo) native->lifecycle.emplace(native->instance, *native->gameIo);
            else native->lifecycle.emplace(native->instance);
        }
        auto sample = liberafx::LifetimeSample{};
        auto result = native->lifecycle->start(lifetime, onStartNodeId, instructionBudget, sample);
        if (result == liberafx::Result::Ok)
            *outSample = {sample.age, sample.remaining, sample.normalizedAge,
                          sample.expired ? 1u : 0u, std::isinf(sample.remaining) ? 1u : 0u};
        return static_cast<LibraResult>(result);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_start_captured(
    LibraInstance instance, const uint8_t lifeNodeId[16],
    const uint8_t onStartNodeId[16], uint32_t instructionBudget, LibraLifetimeSample* outSample)
{
    if (!outSample || !lifeNodeId || (onStartNodeId && !instructionBudget))
        return LIBRA_INVALID_ARGUMENT;
    *outSample = {};
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (native->rawStarted || native->gameIoStarted) return LIBRA_NOT_READY;
        if (!native->lifecycle) {
            if (native->gameIo) native->lifecycle.emplace(native->instance, *native->gameIo);
            else native->lifecycle.emplace(native->instance);
        }
        auto sample = liberafx::LifetimeSample{};
        auto result = native->lifecycle->startCaptured(lifeNodeId, onStartNodeId, instructionBudget, sample);
        if (result == liberafx::Result::Ok)
            *outSample = {sample.age, sample.remaining, sample.normalizedAge,
                          sample.expired ? 1u : 0u, std::isinf(sample.remaining) ? 1u : 0u};
        return static_cast<LibraResult>(result);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_advance(
    LibraInstance instance, double elapsed, LibraLifetimeSample* outSample)
{
    if (!outSample) return LIBRA_INVALID_ARGUMENT;
    *outSample = {};
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (!native->lifecycle) return LIBRA_NOT_READY;
        auto sample = liberafx::LifetimeSample{};
        auto result = native->lifecycle->advance(elapsed, sample);
        if (result == liberafx::Result::Ok)
            *outSample = {sample.age, sample.remaining, sample.normalizedAge,
                          sample.expired ? 1u : 0u, std::isinf(sample.remaining) ? 1u : 0u};
        return static_cast<LibraResult>(result);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_drain(LibraInstance instance, double elapsed)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        return native->lifecycle ? static_cast<LibraResult>(native->lifecycle->drain(elapsed)) : LIBRA_NOT_READY;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_schedule(LibraInstance instance, double time, uint64_t token)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        return native->lifecycle ? static_cast<LibraResult>(native->lifecycle->schedule(time, token)) : LIBRA_NOT_READY;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_next_scheduled(
    LibraInstance instance, LibraScheduledEvent* outEvent, uint32_t* hasEvent)
{
    if (!outEvent || !hasEvent) return LIBRA_INVALID_ARGUMENT;
    *outEvent = {}; *hasEvent = 0;
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (!native->lifecycle) return LIBRA_NOT_READY;
        auto event = liberafx::ScheduledEvent{};
        auto has = false;
        auto result = native->lifecycle->nextScheduled(event, has);
        if (result == liberafx::Result::Ok && has) {
            *outEvent = {event.time, event.token}; *hasEvent = 1;
        }
        return static_cast<LibraResult>(result);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_take_scheduled(
    LibraInstance instance, LibraScheduledEvent* outEvent, uint32_t* hasEvent)
{
    if (!outEvent || !hasEvent) return LIBRA_INVALID_ARGUMENT;
    *outEvent = {}; *hasEvent = 0;
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (!native->lifecycle) return LIBRA_NOT_READY;
        auto event = liberafx::ScheduledEvent{};
        auto has = false;
        auto result = native->lifecycle->takeScheduled(event, has);
        if (result == liberafx::Result::Ok && has) {
            *outEvent = {event.time, event.token}; *hasEvent = 1;
        }
        return static_cast<LibraResult>(result);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_next_deferred_signal(
    LibraInstance instance, double* outTime, uint32_t* hasSignal)
{
    if (!outTime || !hasSignal) return LIBRA_INVALID_ARGUMENT;
    *outTime = 0; *hasSignal = 0;
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (!native->lifecycle) return LIBRA_NOT_READY;
        auto time = double{};
        auto has = false;
        auto result = native->lifecycle->nextDeferredSignal(time, has);
        if (result == liberafx::Result::Ok && has) {
            *outTime = time; *hasSignal = 1;
        }
        return static_cast<LibraResult>(result);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_dispatch_deferred_signal(
    LibraInstance instance, double time, uint32_t instructionBudget)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        return native->lifecycle ? static_cast<LibraResult>(
            native->lifecycle->dispatchDeferredSignal(time, instructionBudget)) : LIBRA_NOT_READY;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_request_natural_end(LibraInstance instance)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        return native->lifecycle ? static_cast<LibraResult>(native->lifecycle->requestNaturalEnd()) : LIBRA_NOT_READY;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_destroy(
    LibraInstance instance, const uint8_t nodeId[16], uint32_t instructionBudget)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        return native->lifecycle ? static_cast<LibraResult>(native->lifecycle->destroy(nodeId, instructionBudget)) : LIBRA_NOT_READY;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_finish(LibraInstance instance)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        return native->lifecycle ? static_cast<LibraResult>(native->lifecycle->finish()) : LIBRA_NOT_READY;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_stop(LibraInstance instance)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        return native->lifecycle ? static_cast<LibraResult>(native->lifecycle->stop()) : LIBRA_NOT_READY;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_dispatch_signal(
    LibraInstance instance, const uint8_t nodeId[16], double time, uint32_t instructionBudget)
{
    if (!nodeId || !instructionBudget) return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (native && native->lifecycle) return LIBRA_NOT_READY;
        return native ? static_cast<LibraResult>(
            native->instance.dispatchSignal(nodeId, time, instructionBudget)) : LIBRA_INVALID_HANDLE;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_effect_dispatch_signal(
    LibraInstance instance, const uint8_t nodeId[16], double time, uint32_t instructionBudget)
{
    if (!nodeId || !instructionBudget || instructionBudget > 10000) return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (!native->lifecycle) return LIBRA_NOT_READY;
        auto result = native->lifecycle->validateSignalTime(time);
        return result == liberafx::Result::Ok ? static_cast<LibraResult>(
            native->instance.dispatchSignal(nodeId, time, instructionBudget)) :
            static_cast<LibraResult>(result);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_configure(
    LibraInstance instance, uint32_t eventCapacity,
    const uint8_t containerId[16], uint64_t instanceId)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (native->lifecycle || native->gameIo || native->owner->program.version() < 6 ||
            native->owner->program.version() > 7)
            return LIBRA_INVALID_VALUE_TYPE;
        auto converted = std::vector<liberafx::NamedIoPort>{};
        auto count = native->owner->program.gameIoPortCount();
        converted.reserve(count);
        for (auto index = std::size_t{}; index < count; ++index) {
            auto port = liberafx::ProgramGameIoPort{};
            if (!native->owner->program.gameIoPort(index, port) || port.kind > 4)
                return LIBRA_INVALID_BINARY;
            auto item = liberafx::NamedIoPort{};
            item.name = port.name;
            item.kind = static_cast<liberafx::NamedIoKind>(port.kind);
            std::memcpy(item.nodeId, port.nodeId, 16);
            std::memcpy(item.additionalNodeIds, port.additionalNodeIds, sizeof(item.additionalNodeIds));
            item.width = port.width;
            item.isColor = port.isColor != 0;
            item.nodeCount = port.nodeCount;
            converted.push_back(item);
        }
        auto identity = liberafx::NamedIoIdentity{};
        if (containerId) std::memcpy(identity.containerId, containerId, 16);
        identity.instanceId = instanceId;
        auto result = liberafx::NamedIo::create(native->instance, converted.data(), converted.size(),
            eventCapacity, native->gameIo, identity);
        return static_cast<LibraResult>(result);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_set_property(
    LibraInstance instance, const char* name, const double* values, uint32_t width)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (!native->gameIo) return LIBRA_NOT_READY;
        return static_cast<LibraResult>(native->gameIo->setProperty(name, values, width));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_set_owner_matrix(
    LibraInstance instance, const double values[16])
{
    if (!values) return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (!native->gameIo) return LIBRA_NOT_READY;
        return static_cast<LibraResult>(native->gameIo->setOwnerMatrix(values, 16));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_start(
    LibraInstance instance, double time, const uint8_t onStartNodeId[16], uint32_t instructionBudget)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (!native->gameIo) return LIBRA_NOT_READY;
        if (native->lifecycle) return LIBRA_NOT_READY;
        native->gameIoStarted = true;
        return static_cast<LibraResult>(
            native->gameIo->start(time, onStartNodeId, instructionBudget));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_update(LibraInstance instance, double time)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (!native->gameIo) return LIBRA_NOT_READY;
        if (native->lifecycle) return LIBRA_NOT_READY;
        return static_cast<LibraResult>(native->gameIo->update(time));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_fire_signal(
    LibraInstance instance, const char* name, double time, uint32_t instructionBudget)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (!native->gameIo) return LIBRA_NOT_READY;
        if (native->lifecycle) {
            auto status = native->lifecycle->validateSignalTime(time);
            if (status != liberafx::Result::Ok) return static_cast<LibraResult>(status);
        }
        return static_cast<LibraResult>(
            native->gameIo->fireSignal(name, time, instructionBudget));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_publish_properties(LibraInstance instance)
{
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (!native->gameIo) return LIBRA_NOT_READY;
        return static_cast<LibraResult>(native->gameIo->publishProperties());
    });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_next_event(
    LibraInstance instance, LibraGameIoEvent* outEvent, uint32_t* hasEvent)
{
    if (!outEvent || !hasEvent) return LIBRA_INVALID_ARGUMENT;
    *outEvent = {};
    *hasEvent = 0;
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (!native->gameIo) return LIBRA_NOT_READY;
        auto event = liberafx::NamedIoEvent{};
        if (!native->gameIo->peekEvent(event)) return LIBRA_OK;
        if (event.width > 4) return LIBRA_INVALID_VALUE_TYPE;
        if (!native->gameIo->popEvent(event)) return LIBRA_INTERNAL_ERROR;
        std::memcpy(outEvent->containerId, event.identity.containerId, 16);
        outEvent->instanceId = event.identity.instanceId;
        outEvent->portIndex = event.portIndex;
        outEvent->kind = static_cast<uint8_t>(event.kind);
        outEvent->width = event.width;
        outEvent->isColor = event.isColor ? 1 : 0;
        std::copy_n(std::begin(event.values), event.width, outEvent->values);
        *hasEvent = 1;
        return LIBRA_OK;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_game_io_next_event_v2(
    LibraInstance instance, LibraGameIoEventV2* outEvent, uint32_t* hasEvent)
{
    if (!outEvent || !hasEvent) return LIBRA_INVALID_ARGUMENT;
    *outEvent = {};
    *hasEvent = 0;
    return guarded([&]() -> LibraResult {
        auto native = findInstance(instance);
        if (!native) return LIBRA_INVALID_HANDLE;
        if (!native->gameIo) return LIBRA_NOT_READY;
        auto event = liberafx::NamedIoEvent{};
        if (!native->gameIo->popEvent(event)) return LIBRA_OK;
        std::memcpy(outEvent->containerId, event.identity.containerId, 16);
        outEvent->instanceId = event.identity.instanceId;
        outEvent->portIndex = event.portIndex;
        outEvent->kind = static_cast<uint8_t>(event.kind);
        outEvent->width = event.width;
        outEvent->isColor = event.isColor ? 1 : 0;
        std::copy(std::begin(event.values), std::end(event.values), outEvent->values);
        *hasEvent = 1;
        return LIBRA_OK;
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

LibraResult LIBRA_INTEROP_CALL libra_generator_pool_create(
    LibraProgram program, const uint8_t containerId[16], uint32_t maxActive,
    uint64_t memoryBudgetBytes, uint32_t eventCapacity, LibraGeneratorPool* outPool)
{
    return libra_generator_pool_create_ex(program, containerId, maxActive, memoryBudgetBytes,
                                          eventCapacity, 16, outPool);
}
LibraResult LIBRA_INTEROP_CALL libra_generator_pool_create_ex(
    LibraProgram program, const uint8_t containerId[16], uint32_t maxActive,
    uint64_t memoryBudgetBytes, uint32_t eventCapacity, uint32_t maxCaptureLanes,
    LibraGeneratorPool* outPool)
{
    if (!outPool || !containerId || memoryBudgetBytes > std::numeric_limits<std::size_t>::max())
        return LIBRA_INVALID_ARGUMENT;
    *outPool = 0;
    return guarded([&]() -> LibraResult {
        auto found = programs.find(program);
        if (found == programs.end()) return LIBRA_INVALID_HANDLE;
        auto native = std::make_shared<NativeGeneratorPool>();
        native->owner = found->second;
        std::memcpy(native->containerId.data(), containerId, 16);
        auto result = liberafx::GeneratorPool::createWithCaptureLimit(native->owner->program, containerId,
            maxActive, static_cast<std::size_t>(memoryBudgetBytes), eventCapacity, maxCaptureLanes, native->pool);
        if (result != liberafx::Result::Ok) return static_cast<LibraResult>(result);
        if (!nextHandle) return LIBRA_INSUFFICIENT_WORK;
        auto handle = nextHandle++;
        generatorPools.emplace(handle, std::move(native));
        *outPool = handle;
        return LIBRA_OK;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_pool_release(LibraGeneratorPool pool)
{
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        if (found == generatorPools.end()) return LIBRA_INVALID_HANDLE;
        if (found->second.use_count() != 1) return LIBRA_BUSY;
        generatorPools.erase(found);
        return LIBRA_OK;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_spawn(LibraGeneratorPool pool, double time,
    const LibraGeneratorCapture* captures, uint32_t count, LibraGeneratorInstance* outInstance)
{
    if (!outInstance || count > 256 || (count && !captures)) return LIBRA_INVALID_ARGUMENT;
    *outInstance = 0;
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        if (found == generatorPools.end()) return LIBRA_INVALID_HANDLE;
        auto converted = std::array<liberafx::GeneratorCapture, 256>{};
        for (auto i = 0u; i < count; ++i) {
            if (captures[i].inputSlot > 3 || captures[i].component > 3) return LIBRA_INVALID_ARGUMENT;
            std::memcpy(converted[i].nodeId, captures[i].nodeId, 16);
            converted[i].inputSlot = static_cast<std::uint8_t>(captures[i].inputSlot);
            converted[i].component = static_cast<std::uint8_t>(captures[i].component);
            converted[i].value = captures[i].value;
        }
        return static_cast<LibraResult>(found->second->pool->spawn(time, converted.data(), count, *outInstance));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_stop(LibraGeneratorPool pool, LibraGeneratorInstance instance)
{
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        return found == generatorPools.end() ? LIBRA_INVALID_HANDLE :
            static_cast<LibraResult>(found->second->pool->stop(instance));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_enqueue_spawn(LibraGeneratorPool pool, double time,
    const LibraGeneratorCapture* captures, uint32_t count)
{
    if (count > 16 || (count && !captures)) return LIBRA_INVALID_ARGUMENT;
    return libra_generator_enqueue_spawn_ex(pool, time, captures, count);
}
LibraResult LIBRA_INTEROP_CALL libra_generator_enqueue_spawn_ex(LibraGeneratorPool pool, double time,
    const LibraGeneratorCapture* captures, uint32_t count)
{
    if (count > 256 || (count && !captures)) return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        if (found == generatorPools.end()) return LIBRA_INVALID_HANDLE;
        auto converted = std::array<liberafx::GeneratorCapture, 256>{};
        for (auto i = 0u; i < count; ++i) {
            if (captures[i].inputSlot > 3 || captures[i].component > 3) return LIBRA_INVALID_ARGUMENT;
            std::memcpy(converted[i].nodeId, captures[i].nodeId, 16);
            converted[i].inputSlot = static_cast<std::uint8_t>(captures[i].inputSlot);
            converted[i].component = static_cast<std::uint8_t>(captures[i].component);
            converted[i].value = captures[i].value;
        }
        return static_cast<LibraResult>(found->second->pool->enqueueSpawn(time, converted.data(), count));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_configure_lifecycle(
    LibraGeneratorPool pool, const LibraGeneratorLifecycle* lifecycle)
{
    if (!lifecycle || (lifecycle->flags & ~(LIBRA_LIFECYCLE_ON_START | LIBRA_LIFECYCLE_LIFE | LIBRA_LIFECYCLE_DEFERRED_SIGNALS)))
        return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        if (found == generatorPools.end()) return LIBRA_INVALID_HANDLE;
        auto config = liberafx::GeneratorLifecycle{};
        std::memcpy(config.onStartNodeId, lifecycle->onStartNodeId, 16);
        std::memcpy(config.lifeNodeId, lifecycle->lifeNodeId, 16);
        config.hasOnStart = (lifecycle->flags & LIBRA_LIFECYCLE_ON_START) != 0;
        config.hasLife = (lifecycle->flags & LIBRA_LIFECYCLE_LIFE) != 0;
        config.enableDeferredSignals = (lifecycle->flags & LIBRA_LIFECYCLE_DEFERRED_SIGNALS) != 0;
        config.instructionBudget = lifecycle->instructionBudget;
        return static_cast<LibraResult>(found->second->pool->configureLifecycle(config));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_start(LibraGeneratorPool pool, LibraGeneratorInstance instance)
{
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        return found == generatorPools.end() ? LIBRA_INVALID_HANDLE :
            static_cast<LibraResult>(found->second->pool->start(instance));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_finalize(LibraGeneratorPool pool, LibraGeneratorInstance instance)
{
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        return found == generatorPools.end() ? LIBRA_INVALID_HANDLE :
            static_cast<LibraResult>(found->second->pool->finalize(instance));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_process_spawns(
    LibraGeneratorPool pool, uint32_t maxCommands, uint32_t* processed)
{
    if (!processed) return LIBRA_INVALID_ARGUMENT;
    *processed = 0;
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        if (found == generatorPools.end()) return LIBRA_INVALID_HANDLE;
        auto completed = std::size_t{};
        auto result = found->second->pool->processSpawns(maxCommands, completed);
        *processed = static_cast<uint32_t>(completed);
        return static_cast<LibraResult>(result);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_reset(LibraGeneratorPool pool)
{
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        return found == generatorPools.end() ? LIBRA_INVALID_HANDLE :
            static_cast<LibraResult>(found->second->pool->reset());
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_set_live(
    LibraGeneratorPool pool, LibraGeneratorInstance instance,
    const LibraGeneratorCapture* values, uint32_t count)
{
    if (count > 256 || (count && !values)) return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        if (found == generatorPools.end()) return LIBRA_INVALID_HANDLE;
        auto converted = std::array<liberafx::GeneratorCapture, 256>{};
        for (auto i = 0u; i < count; ++i) {
            if (values[i].inputSlot > 3 || values[i].component > 3) return LIBRA_INVALID_ARGUMENT;
            std::memcpy(converted[i].nodeId, values[i].nodeId, 16);
            converted[i].inputSlot = static_cast<std::uint8_t>(values[i].inputSlot);
            converted[i].component = static_cast<std::uint8_t>(values[i].component);
            converted[i].value = values[i].value;
        }
        return static_cast<LibraResult>(found->second->pool->setLive(instance, converted.data(), count));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_set_captured(
    LibraGeneratorPool pool, LibraGeneratorInstance instance,
    const LibraGeneratorCapture* values, uint32_t count)
{
    if (count > 256 || (count && !values)) return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        if (found == generatorPools.end()) return LIBRA_INVALID_HANDLE;
        auto converted = std::array<liberafx::GeneratorCapture, 256>{};
        for (auto i = 0u; i < count; ++i) {
            if (values[i].inputSlot > 3 || values[i].component > 3) return LIBRA_INVALID_ARGUMENT;
            std::memcpy(converted[i].nodeId, values[i].nodeId, 16);
            converted[i].inputSlot = static_cast<std::uint8_t>(values[i].inputSlot);
            converted[i].component = static_cast<std::uint8_t>(values[i].component);
            converted[i].value = values[i].value;
        }
        return static_cast<LibraResult>(found->second->pool->setCaptured(instance, converted.data(), count));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_set_signal_outputs(
    LibraGeneratorPool pool, const uint8_t* nodeIds, uint32_t count)
{
    if (count && !nodeIds) return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        return found == generatorPools.end() ? LIBRA_INVALID_HANDLE :
            static_cast<LibraResult>(found->second->pool->setSignalOutputs(nodeIds, count));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_dispatch_signal(
    LibraGeneratorPool pool, const uint8_t nodeId[16], double time, uint32_t instructionBudget)
{
    if (!nodeId) return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        return found == generatorPools.end() ? LIBRA_INVALID_HANDLE :
            static_cast<LibraResult>(found->second->pool->dispatchSignal(nodeId, time, instructionBudget));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_next_signal_event(
    LibraGeneratorPool pool, LibraGeneratorSignalEvent* outEvent, uint32_t* hasEvent)
{
    if (!outEvent || !hasEvent) return LIBRA_INVALID_ARGUMENT;
    *outEvent = {};
    *hasEvent = 0;
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        if (found == generatorPools.end()) return LIBRA_INVALID_HANDLE;
        auto event = liberafx::GeneratorSignalEvent{};
        if (!found->second->pool->popSignalEvent(event)) return LIBRA_OK;
        std::memcpy(outEvent->containerId, event.containerId, 16);
        outEvent->instanceId = event.instanceId;
        std::memcpy(outEvent->nodeId, event.nodeId, 16);
        outEvent->time = event.time;
        outEvent->seed = event.seed;
        *hasEvent = 1;
        return LIBRA_OK;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_set_time_context(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, double effectTime, double frameDelta)
{
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        return found == generatorPools.end() ? LIBRA_INVALID_HANDLE :
            static_cast<LibraResult>(found->second->pool->setTimeContext(instance, effectTime, frameDelta));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_evaluate(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, double time)
{
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        return found == generatorPools.end() ? LIBRA_INVALID_HANDLE :
            static_cast<LibraResult>(found->second->pool->evaluate(instance, time));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_next_deferred_signal(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, double* outTime, uint32_t* hasSignal)
{
    if (!outTime || !hasSignal)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    *outTime = 0;
    *hasSignal = 0;
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        if (found == generatorPools.end())
        {
            return LIBRA_INVALID_HANDLE;
        }
        auto has = false;
        auto result = found->second->pool->nextDeferredSignal(instance, *outTime, has);
        if (result == liberafx::Result::Ok)
        {
            *hasSignal = has ? 1u : 0u;
        }
        return static_cast<LibraResult>(result);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_dispatch_deferred_signal(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, double time, uint32_t instructionBudget)
{
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        return found == generatorPools.end() ? LIBRA_INVALID_HANDLE :
            static_cast<LibraResult>(found->second->pool->dispatchDeferredSignal(instance, time, instructionBudget));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_elapsed(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, double* outElapsed)
{
    if (!outElapsed)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    *outElapsed = 0;
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        return found == generatorPools.end() ? LIBRA_INVALID_HANDLE :
            static_cast<LibraResult>(found->second->pool->elapsed(instance, *outElapsed));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_owner_destroy(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, const uint8_t nodeId[16],
    double time, uint32_t instructionBudget)
{
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        return found == generatorPools.end() ? LIBRA_INVALID_HANDLE :
            static_cast<LibraResult>(found->second->pool->ownerDestroy(instance, nodeId, time, instructionBudget));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_lifetime(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, LibraLifetimeSample* outSample)
{
    if (!outSample) return LIBRA_INVALID_ARGUMENT;
    *outSample = {};
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        if (found == generatorPools.end()) return LIBRA_INVALID_HANDLE;
        auto sample = liberafx::LifetimeSample{};
        auto result = found->second->pool->lifetime(instance, sample);
        if (result == liberafx::Result::Ok)
            *outSample = {sample.age, sample.remaining, sample.normalizedAge,
                          sample.expired ? 1u : 0u, std::isinf(sample.remaining) ? 1u : 0u};
        return static_cast<LibraResult>(result);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_value(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, const uint8_t nodeId[16], LibraValue* outValue)
{
    if (!nodeId || !outValue) return LIBRA_INVALID_ARGUMENT;
    *outValue = {};
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        if (found == generatorPools.end()) return LIBRA_INVALID_HANDLE;
        auto lanes = std::array<double, 4>{};
        auto width = std::uint8_t{};
        auto result = found->second->pool->value(instance, nodeId, lanes.data(), width);
        if (result == liberafx::Result::Ok)
            *outValue = {lanes[0], lanes[1], lanes[2], lanes[3], width, 0};
        return static_cast<LibraResult>(result);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_generator_next_event(
    LibraGeneratorPool pool, LibraGeneratorEvent* outEvent, uint32_t* hasEvent)
{
    if (!outEvent || !hasEvent) return LIBRA_INVALID_ARGUMENT;
    *outEvent = {};
    *hasEvent = 0;
    return guarded([&]() -> LibraResult {
        auto found = generatorPools.find(pool);
        if (found == generatorPools.end()) return LIBRA_INVALID_HANDLE;
        auto event = liberafx::GeneratorEvent{};
        if (found->second->pool->popEvent(event)) {
            outEvent->kind = static_cast<uint32_t>(event.kind);
            std::memcpy(outEvent->containerId, event.containerId, 16);
            outEvent->instanceId = event.instanceId;
            outEvent->startTime = event.startTime;
            outEvent->seed = event.seed;
            *hasEvent = 1;
        }
        return LIBRA_OK;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_create(
    const uint8_t* bytes, uint64_t byteCount, uint32_t maxActivePerTemplate,
    uint64_t memoryBudgetBytesPerTemplate, uint32_t eventCapacity,
    uint32_t instructionBudget, LibraSpawnableHostHandle* outHost)
{
    if (!outHost || !bytes || !byteCount ||
        memoryBudgetBytesPerTemplate > std::numeric_limits<std::size_t>::max())
        return LIBRA_INVALID_ARGUMENT;
    *outHost = 0;
    return guarded([&]() -> LibraResult {
        auto package = liberafx::SpawnablePackage{};
        auto result = liberafx::loadSpawnablePackage(bytes,
            static_cast<std::size_t>(byteCount), package);
        if (result != liberafx::Result::Ok) return static_cast<LibraResult>(result);
        auto options = liberafx::SpawnableHostOptions{};
        options.maxActivePerTemplate = maxActivePerTemplate;
        options.memoryBudgetBytesPerTemplate = static_cast<std::size_t>(memoryBudgetBytesPerTemplate);
        options.eventCapacity = eventCapacity;
        options.instructionBudget = instructionBudget;
        auto host = std::unique_ptr<liberafx::SpawnableHost>{};
        result = liberafx::SpawnableHost::create(std::move(package), options, host);
        if (result != liberafx::Result::Ok) return static_cast<LibraResult>(result);
        if (!nextHandle) return LIBRA_INSUFFICIENT_WORK;
        const auto handle = nextHandle++;
        spawnableHosts.emplace(handle, std::move(host));
        *outHost = handle;
        return LIBRA_OK;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_release(LibraSpawnableHostHandle host)
{
    return guarded([&]() -> LibraResult {
        return spawnableHosts.erase(host) ? LIBRA_OK : LIBRA_INVALID_HANDLE;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_advance(
    LibraSpawnableHostHandle host, double time, double frameDelta)
{
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        return native ? static_cast<LibraResult>(native->advance(time, frameDelta)) :
            LIBRA_INVALID_HANDLE;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_dispatch_signal(
    LibraSpawnableHostHandle host, const uint8_t nodeId[16])
{
    if (!nodeId) return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        return native ? static_cast<LibraResult>(native->dispatchSignal(nodeId)) :
            LIBRA_INVALID_HANDLE;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_fire_game_signal(
    LibraSpawnableHostHandle host, const char* name)
{
    if (!name) return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        return native ? static_cast<LibraResult>(native->fireGameSignal(name)) :
            LIBRA_INVALID_HANDLE;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_set_game_property(
    LibraSpawnableHostHandle host, const char* name, const double* values, uint32_t width)
{
    if (!name || (width && !values)) return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        return native ? static_cast<LibraResult>(native->setGameProperty(name, values, width)) :
            LIBRA_INVALID_HANDLE;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_set_owner_matrix(
    LibraSpawnableHostHandle host, const double values[16])
{
    if (!values) return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        return native ? static_cast<LibraResult>(native->setOwnerMatrix(values, 16)) :
            LIBRA_INVALID_HANDLE;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_request_natural_end(
    LibraSpawnableHostHandle host)
{
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        return native ? static_cast<LibraResult>(native->requestNaturalEnd()) :
            LIBRA_INVALID_HANDLE;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_stop(LibraSpawnableHostHandle host)
{
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        return native ? static_cast<LibraResult>(native->stop()) : LIBRA_INVALID_HANDLE;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_root_value(
    LibraSpawnableHostHandle host, const uint8_t nodeId[16], LibraValue* outValue)
{
    if (!nodeId || !outValue) return LIBRA_INVALID_ARGUMENT;
    *outValue = {};
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        if (!native) return LIBRA_INVALID_HANDLE;
        auto values = std::array<double, 4>{};
        auto width = std::uint8_t{};
        const auto result = native->rootValue(nodeId, values.data(), width);
        if (result == liberafx::Result::Ok)
            *outValue = {values[0], values[1], values[2], values[3], width, 0};
        return static_cast<LibraResult>(result);
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_child_value(
    LibraSpawnableHostHandle host, const uint8_t containerId[16],
    LibraGeneratorInstance instance, const uint8_t nodeId[16], LibraValue* outValue)
{
    if (!containerId || !nodeId || !outValue) return LIBRA_INVALID_ARGUMENT;
    *outValue = {};
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        if (!native) return LIBRA_INVALID_HANDLE;
        auto values = std::array<double, 4>{};
        auto width = std::uint8_t{};
        const auto result = native->childValue(containerId, instance, nodeId, values.data(), width);
        if (result == liberafx::Result::Ok)
            *outValue = {values[0], values[1], values[2], values[3], width, 0};
        return static_cast<LibraResult>(result);
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_next_event(
    LibraSpawnableHostHandle host, LibraSpawnableHostEvent* outEvent, uint32_t* hasEvent)
{
    if (!outEvent || !hasEvent) return LIBRA_INVALID_ARGUMENT;
    *outEvent = {};
    *hasEvent = 0;
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        if (!native) return LIBRA_INVALID_HANDLE;
        auto event = liberafx::SpawnableHostEvent{};
        if (!native->popEvent(event)) return LIBRA_OK;
        outEvent->kind = static_cast<uint32_t>(event.kind);
        std::memcpy(outEvent->containerId, event.containerId, 16);
        outEvent->instanceId = event.instanceId;
        outEvent->time = event.time;
        outEvent->seed = event.seed;
        std::memcpy(outEvent->signalNodeId, event.signalNodeId, 16);
        *hasEvent = 1;
        return LIBRA_OK;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_next_game_io_event(
    LibraSpawnableHostHandle host, LibraGameIoEventV2* outEvent, uint32_t* hasEvent)
{
    if (!outEvent || !hasEvent) return LIBRA_INVALID_ARGUMENT;
    *outEvent = {};
    *hasEvent = 0;
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        if (!native) return LIBRA_INVALID_HANDLE;
        auto event = liberafx::NamedIoEvent{};
        if (!native->popGameIoEvent(event)) return LIBRA_OK;
        std::memcpy(outEvent->containerId, event.identity.containerId, 16);
        outEvent->instanceId = event.identity.instanceId;
        outEvent->portIndex = event.portIndex;
        outEvent->kind = static_cast<std::uint8_t>(event.kind);
        outEvent->width = event.width;
        outEvent->isColor = event.isColor ? 1 : 0;
        std::copy(std::begin(event.values), std::end(event.values), outEvent->values);
        *hasEvent = 1;
        return LIBRA_OK;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_active_count(
    LibraSpawnableHostHandle host, uint64_t* outCount)
{
    if (!outCount) return LIBRA_INVALID_ARGUMENT;
    *outCount = 0;
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        if (!native) return LIBRA_INVALID_HANDLE;
        *outCount = native->activeCount();
        return LIBRA_OK;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_root_finished(
    LibraSpawnableHostHandle host, uint32_t* outFinished)
{
    if (!outFinished) return LIBRA_INVALID_ARGUMENT;
    *outFinished = 0;
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        if (!native) return LIBRA_INVALID_HANDLE;
        *outFinished = native->rootFinished() ? 1 : 0;
        return LIBRA_OK;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_template_count(
    LibraSpawnableHostHandle host, uint64_t* outCount)
{
    if (!outCount) return LIBRA_INVALID_ARGUMENT;
    *outCount = 0;
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        if (!native) return LIBRA_INVALID_HANDLE;
        *outCount = native->package().templates.size();
        return LIBRA_OK;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_template_id(
    LibraSpawnableHostHandle host, uint64_t index, uint8_t outContainerId[16])
{
    if (!outContainerId) return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        if (!native) return LIBRA_INVALID_HANDLE;
        const auto& templates = native->package().templates;
        if (index >= templates.size()) return LIBRA_INVALID_ARGUMENT;
        const auto& id = templates[static_cast<std::size_t>(index)].containerId;
        std::copy(id.begin(), id.end(), outContainerId);
        return LIBRA_OK;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_copy_template_payload(
    LibraSpawnableHostHandle host, const uint8_t containerId[16], uint32_t kind,
    uint8_t* destination, uint64_t capacity, uint64_t* outByteCount)
{
    if (!outByteCount) return LIBRA_INVALID_ARGUMENT;
    *outByteCount = 0;
    if (!containerId || (!destination && capacity != 0) ||
        (kind != LIBRA_SPAWNABLE_RENDER_CONFIGURATION && kind != LIBRA_SPAWNABLE_GPU_CONTAINERS))
        return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        if (!native) return LIBRA_INVALID_HANDLE;
        const auto& templates = native->package().templates;
        auto found = std::find_if(templates.begin(), templates.end(), [&](const auto& value) {
            return std::memcmp(value.containerId.data(), containerId, 16) == 0;
        });
        if (found == templates.end()) return LIBRA_NODE_NOT_FOUND;
        const auto& payload = kind == LIBRA_SPAWNABLE_RENDER_CONFIGURATION
            ? found->renderConfiguration : found->gpuContainersJson;
        *outByteCount = payload.size();
        if (!destination) return LIBRA_OK;
        if (capacity < payload.size()) return LIBRA_INSUFFICIENT_WORK;
        if (!payload.empty()) std::memcpy(destination, payload.data(), payload.size());
        return LIBRA_OK;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_copy_root_payload(
    LibraSpawnableHostHandle host, uint32_t kind, uint8_t* destination,
    uint64_t capacity, uint64_t* outByteCount)
{
    if (!outByteCount) return LIBRA_INVALID_ARGUMENT;
    *outByteCount = 0;
    if ((!destination && capacity != 0) ||
        (kind != LIBRA_SPAWNABLE_RENDER_CONFIGURATION && kind != LIBRA_SPAWNABLE_GPU_CONTAINERS))
        return LIBRA_INVALID_ARGUMENT;
    return guarded([&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        if (!native) return LIBRA_INVALID_HANDLE;
        const auto& payload = kind == LIBRA_SPAWNABLE_RENDER_CONFIGURATION
            ? native->package().rootRenderConfiguration : native->package().rootGpuContainersJson;
        *outByteCount = payload.size();
        if (!destination) return LIBRA_OK;
        if (capacity < payload.size()) return LIBRA_INSUFFICIENT_WORK;
        if (!payload.empty()) std::memcpy(destination, payload.data(), payload.size());
        return LIBRA_OK;
    });
}
