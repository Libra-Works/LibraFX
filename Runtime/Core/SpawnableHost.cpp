#include "liberafx/SpawnableHost.h"
#include "liberafx/host/SpawnableDebugJob.h"
#include <atomic>
#include "liberafx/BuiltinSignals.h"
#include "liberafx/EffectLifecycle.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>
#include <numeric>
#include <optional>
#include <utility>
#include <vector>

namespace liberafx {
namespace {
constexpr auto bytecodeHeaderBytes = std::size_t{16};
constexpr auto typedInstructionBytes = std::size_t{96};
constexpr auto customInstructionBytes = std::size_t{256};

struct AlignedBuffer final {
    void* data = nullptr;
    std::size_t alignment = 16;
    ~AlignedBuffer() { release(); }
    void release() noexcept
    {
        if (data) ::operator delete(data, std::align_val_t{alignment});
        data = nullptr;
    }
    AlignedBuffer() = default;
    AlignedBuffer(const AlignedBuffer&) = delete;
    AlignedBuffer& operator=(const AlignedBuffer&) = delete;
    auto allocate(std::size_t bytes, std::size_t requested) noexcept -> Result
    {
        alignment = std::max<std::size_t>(requested, 16);
        if (!bytes) return Result::Ok;
        try {
            data = ::operator new(bytes, std::align_val_t{alignment});
            std::memset(data, 0, bytes);
            return Result::Ok;
        } catch (const std::bad_alloc&) {
            return Result::InsufficientWork;
        }
    }
};

auto isNonzero(const std::uint8_t id[16]) noexcept -> bool
{
    for (auto index = 0u; index < 16; ++index) if (id[index]) return true;
    return false;
}

auto sameId(const std::uint8_t left[16], const std::uint8_t right[16]) noexcept -> bool
{
    return std::memcmp(left, right, 16) == 0;
}

struct OwnerOperation final {
    bool& busy;
    explicit OwnerOperation(bool& value) noexcept : busy(value) { busy = true; }
    ~OwnerOperation() { busy = false; }
};

auto validScope(const SpawnableScope& scope) noexcept -> bool
{
    if (scope.kind == SpawnableScopeKind::Effect)
        return !scope.instanceId && !isNonzero(scope.containerId);
    return scope.kind == SpawnableScopeKind::Instance &&
        scope.instanceId && isNonzero(scope.containerId);
}

auto bytecodeInstructionBytes(const std::vector<std::uint8_t>& bytes,
                              std::size_t& size) noexcept -> Result
{
    if (bytes.size() < bytecodeHeaderBytes || std::memcmp(bytes.data(), "LFXB", 4) != 0 ||
        bytes[4] < 2 || bytes[4] > 11 || bytes[5] != 0 || bytes[6] != 1 || bytes[7] != 0)
        return Result::InvalidBinary;
    if (bytes[4] == 2) {
        if (bytes[12] != typedInstructionBytes || bytes[13] != 0 ||
            bytes[14] != 0 || bytes[15] != 0)
            return Result::InvalidBinary;
        size = typedInstructionBytes;
    } else {
        if (bytes[12] != 0 || bytes[13] != 1 || bytes[14] != 0 || bytes[15] != 0)
            return Result::InvalidBinary;
        size = customInstructionBytes;
    }
    return Result::Ok;
}

auto bytecodeNodeCount(const std::vector<std::uint8_t>& bytes, std::size_t& count) noexcept -> Result
{
    auto instructionBytes = std::size_t{};
    auto result = bytecodeInstructionBytes(bytes, instructionBytes);
    if (result != Result::Ok) return result;
    auto value = std::uint32_t{};
    for (auto index = 0u; index < 4; ++index)
        value |= std::uint32_t{bytes[8 + index]} << (index * 8);
    if (value > 10000 || bytes.size() < bytecodeHeaderBytes + instructionBytes * value)
        return Result::InvalidBinary;
    if (bytes[4] == 2 && bytes.size() != bytecodeHeaderBytes + instructionBytes * value)
        return Result::InvalidBinary;
    if (bytes[4] == 3 && bytes.size() != bytecodeHeaderBytes + instructionBytes * value)
        return Result::InvalidBinary;
    count = value;
    return Result::Ok;
}

auto loadProgram(const std::vector<std::uint8_t>& bytes,
                 const CustomNodeDescriptor* registry, std::size_t registryCount,
                 std::vector<CustomNodeBinding>& bindings, Program& out) -> Result
{
    auto count = std::size_t{};
    auto result = bytecodeNodeCount(bytes, count);
    if (result != Result::Ok) return result;
    bindings.resize(count);
    return Program::load(bytes.data(), bytes.size(), registry, registryCount,
                         bindings.data(), bindings.size(), out);
}

auto findSignalType(const std::vector<std::uint8_t>& bytes, const std::uint8_t nodeId[16],
                    std::array<std::uint8_t, 16>& typeId) noexcept -> Result
{
    auto count = std::size_t{};
    auto result = bytecodeNodeCount(bytes, count);
    if (result != Result::Ok) return result;
    if (bytes[4] < 3) return Result::InvalidBinary;
    auto matches = std::size_t{};
    for (auto index = std::size_t{}; index < count; ++index) {
        const auto* record = bytes.data() + bytecodeHeaderBytes + index * customInstructionBytes;
        if (record[16] != 41 || !sameId(record, nodeId)) continue;
        std::memcpy(typeId.data(), record + 24, typeId.size());
        ++matches;
    }
    return matches == 1 && isNonzero(typeId.data()) ? Result::Ok : Result::InvalidBinary;
}

struct SpawnCallbackContext final {
    GeneratorPool* pool = nullptr;
    std::array<std::array<std::uint8_t, 16>, 4> nodeIds{};
    std::array<std::uint8_t, 4> widths{};
    std::uint8_t inputCount = 0;
};

struct SignalInputCallbackContext final {
    GeneratorPool* pool = nullptr;
    std::array<std::uint8_t, 16> nodeId{};
};

auto enqueueSpawn(CustomNodeInvocation& call) noexcept -> Result
{
    auto& context = *static_cast<SpawnCallbackContext*>(call.context);
    auto captures = std::array<GeneratorCapture, 16>{};
    auto count = std::size_t{};
    for (auto input = 0u; input < context.inputCount; ++input) {
        for (auto lane = 0u; lane < context.widths[input]; ++lane) {
            auto& capture = captures[count++];
            std::memcpy(capture.nodeId, context.nodeIds[input].data(), 16);
            capture.component = static_cast<std::uint8_t>(lane);
            capture.value = call.inputs[input * 4 + lane];
        }
    }
    call.selectedBranch = 0;
    return context.pool->enqueueSpawn(call.time, captures.data(), count);
}

auto dispatchTemplateSignal(CustomNodeInvocation& call) noexcept -> Result
{
    auto& context = *static_cast<SignalInputCallbackContext*>(call.context);
    call.selectedBranch = 0;
    return context.pool->dispatchSignal(context.nodeId.data(), call.time, 10000);
}

auto passthroughDescriptor(CustomNodeDescriptor& out) noexcept -> void
{
    std::memcpy(out.typeId, signalPassthroughTypeId.data(), 16);
    out.abiVersion = 1;
    out.execution = CustomExecution::Signal;
    out.branchCount = 1;
    out.function = signalPassthrough;
}
}

void host::SpawnableDebugJob::run(void* self) noexcept
{
    auto& job = *static_cast<SpawnableDebugJob*>(self);
    const auto& request = job.request_;
    try {
        switch (request.operation) {
        case SpawnableDebugOperation::Advance: job.result_ = job.host_.advance(request.time, request.frameDelta); break;
        case SpawnableDebugOperation::Signal: job.result_ = job.host_.dispatchSignal(request.node.data()); break;
        case SpawnableDebugOperation::GameSignal: job.result_ = job.host_.fireGameSignal(request.name.data()); break;
        case SpawnableDebugOperation::NaturalEnd: job.result_ = job.host_.requestNaturalEnd(); break;
        case SpawnableDebugOperation::NotifyEnd: job.result_ = job.host_.notifyEndRequest(request.scope, request.node.data()); break;
        default: job.result_ = Result::InvalidArgument; break;
        }
    } catch (const std::bad_alloc&) {
        job.result_ = Result::InsufficientWork;
    } catch (...) {
        job.result_ = Result::NotReady;
    }
    job.done_.store(true, std::memory_order_release);
}

struct SpawnableHost::Impl final {
    struct InstanceInfo final {
        GeneratorInstanceId id = 0;
        double start = 0;
        double life = std::numeric_limits<double>::infinity();
    };
    struct TemplateState final {
        const SpawnablePackageTemplate* data = nullptr;
        Program program;
        std::vector<CustomNodeBinding> bindings;
        std::unique_ptr<GeneratorPool> pool;
        std::vector<InstanceInfo> instances;
        SpawnCallbackContext spawnContext;
        std::vector<std::array<std::uint8_t, 16>> outputIds;
    };
    struct SignalInputContext final {
        std::array<std::uint8_t, 16> typeId{};
        SignalInputCallbackContext callback;
    };
    struct SpawnContextEntry final {
        std::array<std::uint8_t, 16> typeId{};
        SpawnCallbackContext callback;
    };
    struct CapturedRoute final {
        const SpawnablePackageDestroyRoute* route = nullptr;
        std::vector<GeneratorCapture> captures;
    };
    struct ScheduledRoute final {
        std::uint64_t token = 0;
        CapturedRoute continuation;
        GeneratorInstanceId sourceInstanceId = 0;
    };
    struct OwnerNotice final {
        std::array<std::uint8_t, 16> containerId{};
        GeneratorInstanceId instanceId = 0;
    };

    SpawnablePackage packageData;
    SpawnableHostOptions options;
    std::vector<std::unique_ptr<TemplateState>> templates;
    std::vector<SpawnContextEntry> spawnContexts;
    std::vector<SignalInputContext> signalInputs;
    std::vector<CustomNodeDescriptor> parentDescriptors;
    std::vector<CustomNodeBinding> parentBindings;
    Program parentProgram;
    AlignedBuffer rootWork;
    AlignedBuffer rootDefaults;
    std::optional<Instance> rootInstance;
    std::unique_ptr<NamedIo> gameIo;
    std::optional<EffectLifecycle> lifecycle;
    std::vector<NamedIoPort> gameIoPorts;
    std::vector<SpawnableHostEvent> events;
    std::size_t eventHead = 0;
    std::size_t eventCount = 0;
    std::vector<OwnerNotice> ownerNotices;
    std::vector<ScheduledRoute> scheduledRoutes;
    double time = -1;
    double rootAge = 0;
    double frameDelta = 0;
    HostQualitySnapshot quality{};
    std::uint64_t nextToken = 1;
    std::uint64_t debugGeneration = 0;
    bool started = false;
    bool rootEnding = false;
    bool rootFinished = false;
    bool rootStopped = false;
    bool rootDestroySent = false;
    bool pendingPump = false;
    bool operationActive = false;
    bool rootForced = false;
    Result failure = Result::Ok;

    Impl(SpawnablePackage value, const SpawnableHostOptions& configuration)
        : packageData(std::move(value)), options(configuration), events(configuration.eventCapacity) {}

    auto findTemplate(const std::uint8_t id[16]) noexcept -> TemplateState*
    {
        for (auto& state : templates)
            if (sameId(state->data->containerId.data(), id)) return state.get();
        return nullptr;
    }

    auto findInstance(TemplateState& state, GeneratorInstanceId id) noexcept -> InstanceInfo*
    {
        for (auto& instance : state.instances) if (instance.id == id) return &instance;
        return nullptr;
    }

    auto activeCount() const noexcept -> std::size_t
    {
        auto count = std::size_t{};
        for (const auto& state : templates) count += state->pool->activeCount();
        return count;
    }

    auto appendEvent(SpawnableHostEventKind kind, const std::uint8_t container[16],
                     GeneratorInstanceId instance, double eventTime, std::uint64_t seed,
                     const std::uint8_t* nodeId = nullptr) noexcept -> Result
    {
        if (eventCount == events.size()) return Result::QueueRejected;
        auto& event = events[(eventHead + eventCount) % events.size()];
        event = {};
        event.kind = kind;
        if (container) std::memcpy(event.containerId, container, 16);
        event.instanceId = instance;
        event.time = eventTime;
        event.seed = seed;
        if (nodeId) std::memcpy(event.signalNodeId, nodeId, 16);
        ++eventCount;
        return Result::Ok;
    }

    auto readRoot(const std::uint8_t nodeId[16], double out[4], std::uint8_t& width) -> Result
    {
        auto handle = ValueHandle{};
        auto result = rootInstance->bindValue(nodeId, handle);
        if (result != Result::Ok) return result;
        width = handle.componentCount;
        return rootInstance->value(handle, out);
    }

    auto readChild(TemplateState& state, GeneratorInstanceId instance,
                   const std::uint8_t nodeId[16], double out[4], std::uint8_t& width) -> Result
    {
        return state.pool->value(instance, nodeId, out, width);
    }

    auto appendCapture(std::vector<GeneratorCapture>& captures, const SpawnablePackageCapture& binding,
                       const double value[4], std::uint8_t valueWidth) -> Result
    {
        if (binding.width < 1 || binding.width > 4 ||
            binding.sourceComponent > valueWidth || binding.width > valueWidth - binding.sourceComponent)
            return Result::InvalidBinary;
        if (captures.size() > 256 - binding.width) return Result::InvalidBinary;
        for (auto lane = 0u; lane < binding.width; ++lane) {
            auto capture = GeneratorCapture{};
            std::memcpy(capture.nodeId, binding.targetNodeId.data(), 16);
            capture.component = static_cast<std::uint8_t>(lane);
            capture.value = value[binding.sourceComponent + lane];
            captures.push_back(capture);
        }
        return Result::Ok;
    }

    auto appendParentCapture(std::vector<GeneratorCapture>& captures,
                             const SpawnablePackageCapture& binding) -> Result
    {
        if (binding.sourceContainerId) return Result::InvalidBinary;
        auto values = std::array<double, 4>{};
        auto width = std::uint8_t{};
        auto result = readRoot(binding.sourceNodeId.data(), values.data(), width);
        if (result != Result::Ok) return result;
        return appendCapture(captures, binding, values.data(), width);
    }

    auto initialize() -> Result;
    auto startRoot(double initialFrameDelta) -> Result;
    auto evaluateParent(double at) -> Result;
    auto evaluateFrame(double at) -> Result;
    auto pump(double at) -> Result;
    auto processPoolEvent(TemplateState& state, const GeneratorEvent& event, double at) -> Result;
    auto spawnRoute(const CapturedRoute& captured, double at) -> Result;
    auto dispatchScheduled(double at) -> Result;
    auto nextDeferredTime(double& out) -> Result;
    auto nextChildDeferredTime(double& out) -> Result;
    auto nextScheduledTime(double& out) -> Result;
    auto notifyOwnerDestroy(double at) -> Result;
    auto beginAutoEnd(double at) -> Result;
    auto finishIfQuiescent() -> Result;
    auto applyCaptures(TemplateState& state, GeneratorInstanceId id,
                       const std::optional<std::vector<SpawnablePackageCapture>>& inputs,
                       bool live) -> Result;
    auto forceTerminate(const SpawnableScope& scope) -> Result;
    auto applyAuthoredForce() -> Result;
};

auto SpawnableHost::Impl::initialize() -> Result
{
    if (!options.maxActivePerTemplate || options.maxActivePerTemplate > 4096 ||
        !options.memoryBudgetBytesPerTemplate || !options.eventCapacity ||
        options.eventCapacity > 8192 || !options.instructionBudget ||
        options.instructionBudget > 10000 || packageData.templates.size() > 4096)
        return Result::InvalidArgument;
    auto result = Result::Ok;
    templates.reserve(packageData.templates.size());
    for (const auto& data : packageData.templates) {
        if (!isNonzero(data.containerId.data()) || !isNonzero(data.signalTypeId.data()) ||
            data.inputs.size() > 256)
            return Result::InvalidBinary;
        if (findTemplate(data.containerId.data())) return Result::InvalidBinary;
        auto state = std::make_unique<TemplateState>();
        state->data = &data;
        result = loadProgram(data.bytecode, nullptr, 0, state->bindings, state->program);
        if (result != Result::Ok) return result;
        if (state->program.gameIoPortCount() != 0) return Result::InvalidBinary;

        auto maxCaptureLanes = std::size_t{16};
        const auto extended = data.inputs.size() > 4 ||
            std::accumulate(data.inputs.begin(), data.inputs.end(), std::size_t{},
                [](std::size_t total, const auto& input) { return total + input.width; }) > 16;
        if (extended || (data.capturedInputs && !data.capturedInputs->empty()) ||
            (data.liveInputs && !data.liveInputs->empty())) maxCaptureLanes = 256;
        result = GeneratorPool::createWithCaptureLimit(state->program, data.containerId.data(),
            options.maxActivePerTemplate, options.memoryBudgetBytesPerTemplate, options.eventCapacity,
            maxCaptureLanes, state->pool);
        if (result != Result::Ok) return result;

        auto lifecycleConfig = GeneratorLifecycle{};
        lifecycleConfig.instructionBudget = options.instructionBudget;
        lifecycleConfig.enableDeferredSignals = packageData.hasChildDeferredSignals;
        if (data.lifecycle) {
            if (data.lifecycle->onStartNodeId) {
                std::memcpy(lifecycleConfig.onStartNodeId, data.lifecycle->onStartNodeId->data(), 16);
                lifecycleConfig.hasOnStart = true;
            }
            if (data.lifecycle->lifeNodeId) {
                std::memcpy(lifecycleConfig.lifeNodeId, data.lifecycle->lifeNodeId->data(), 16);
                lifecycleConfig.hasLife = true;
            }
        }
        result = state->pool->configureLifecycle(lifecycleConfig);
        if (result != Result::Ok) return result;
        if (data.signalOutputs && !data.signalOutputs->empty()) {
            state->outputIds.reserve(data.signalOutputs->size());
            for (const auto& port : *data.signalOutputs)
                state->outputIds.push_back(port.templateNodeId);
            result = state->pool->setSignalOutputs(
                reinterpret_cast<const std::uint8_t*>(state->outputIds.data()), state->outputIds.size());
            if (result != Result::Ok) return result;
        }
        templates.push_back(std::move(state));
    }

    auto signalInputCount = std::size_t{};
    for (const auto& state : templates)
        if (state->data->signalInputs) signalInputCount += state->data->signalInputs->size();
    spawnContexts.resize(templates.size());
    signalInputs.resize(signalInputCount);
    parentDescriptors.resize(1 + spawnContexts.size() + signalInputs.size());
    passthroughDescriptor(parentDescriptors[0]);
    auto descriptorIndex = std::size_t{1};
    for (auto index = std::size_t{}; index < templates.size(); ++index) {
        auto& state = *templates[index];
        auto& context = spawnContexts[index];
        context.callback.pool = state.pool.get();
        const auto extended = state.data->inputs.size() > 4 ||
            std::accumulate(state.data->inputs.begin(), state.data->inputs.end(), std::size_t{},
                [](std::size_t total, const auto& input) { return total + input.width; }) > 16;
        if (!extended) {
            if (state.data->inputs.size() > 4) return Result::InvalidBinary;
            context.callback.inputCount = static_cast<std::uint8_t>(state.data->inputs.size());
            for (auto input = std::size_t{}; input < state.data->inputs.size(); ++input) {
                const auto& source = state.data->inputs[input];
                if (source.width < 1 || source.width > 4 || (source.isColor && source.width != 4))
                    return Result::InvalidBinary;
                context.callback.nodeIds[input] = source.templateNodeId;
                context.callback.widths[input] = static_cast<std::uint8_t>(source.width);
            }
        }
        auto& descriptor = parentDescriptors[descriptorIndex++];
        std::memcpy(descriptor.typeId, state.data->signalTypeId.data(), 16);
        descriptor.abiVersion = 1;
        descriptor.execution = CustomExecution::Signal;
        descriptor.inputCount = context.callback.inputCount;
        descriptor.branchCount = 1;
        descriptor.function = enqueueSpawn;
        descriptor.context = &context.callback;
        for (auto input = 0u; input < descriptor.inputCount; ++input) {
            descriptor.inputWidths[input] = context.callback.widths[input];
            descriptor.inputIsColor[input] = state.data->inputs[input].isColor ? 1 : 0;
        }
        auto recordMatches = std::size_t{};
        auto parentCount = std::size_t{};
        result = bytecodeNodeCount(packageData.parentBytecode, parentCount);
        if (result != Result::Ok) return result;
        for (auto node = std::size_t{}; node < parentCount; ++node) {
            const auto* record = packageData.parentBytecode.data() +
                bytecodeHeaderBytes + node * customInstructionBytes;
            if (record[16] != 41 || !sameId(record, state.data->containerId.data()) ||
                !sameId(record + 24, state.data->signalTypeId.data())) continue;
            if (record[18] != descriptor.inputCount) return Result::InvalidBinary;
            ++recordMatches;
        }
        if (recordMatches != 1) return Result::InvalidBinary;
    }

    auto signalContextIndex = std::size_t{};
    for (auto& statePointer : templates) {
        auto& state = *statePointer;
        if (!state.data->signalInputs) continue;
        for (const auto& input : *state.data->signalInputs) {
            auto& context = signalInputs[signalContextIndex++];
            result = findSignalType(packageData.parentBytecode, input.proxyNodeId.data(), context.typeId);
            if (result != Result::Ok) return result;
            context.callback.pool = state.pool.get();
            context.callback.nodeId = input.templateNodeId;
            auto& descriptor = parentDescriptors[descriptorIndex++];
            std::memcpy(descriptor.typeId, context.typeId.data(), 16);
            descriptor.abiVersion = 1;
            descriptor.execution = CustomExecution::Signal;
            descriptor.branchCount = 1;
            descriptor.function = dispatchTemplateSignal;
            descriptor.context = &context.callback;
            for (auto previous = std::size_t{1}; previous < descriptorIndex - 1; ++previous)
                if (sameId(parentDescriptors[previous].typeId, descriptor.typeId))
                    return Result::InvalidBinary;
        }
    }

    result = loadProgram(packageData.parentBytecode, parentDescriptors.data(),
                         parentDescriptors.size(), parentBindings, parentProgram);
    if (result != Result::Ok) return result;
    if (parentProgram.gameIoPortCount() != packageData.gameIoPorts.size())
        return Result::InvalidBinary;
    gameIoPorts.reserve(packageData.gameIoPorts.size());
    for (auto index = std::size_t{}; index < packageData.gameIoPorts.size(); ++index) {
        auto compiled = ProgramGameIoPort{};
        if (!parentProgram.gameIoPort(index, compiled) || !compiled.name)
            return Result::InvalidBinary;
        const auto& saved = packageData.gameIoPorts[index];
        if (saved.name != compiled.name || saved.kind != compiled.kind || saved.width != compiled.width ||
            saved.isColor != (compiled.isColor != 0) || !sameId(saved.nodeId.data(), compiled.nodeId))
            return Result::InvalidBinary;
        auto port = NamedIoPort{};
        port.name = compiled.name;
        port.kind = static_cast<NamedIoKind>(compiled.kind);
        std::memcpy(port.nodeId, compiled.nodeId, 16);
        std::memcpy(port.additionalNodeIds, compiled.additionalNodeIds, sizeof(port.additionalNodeIds));
        port.width = compiled.width;
        port.isColor = compiled.isColor != 0;
        port.nodeCount = compiled.nodeCount;
        if (saved.runtimeNodeIds) {
            if (saved.runtimeNodeIds->size() != compiled.nodeCount) return Result::InvalidBinary;
            for (auto node = std::size_t{}; node < saved.runtimeNodeIds->size(); ++node) {
                const auto* compiledNode = node == 0 ? compiled.nodeId : compiled.additionalNodeIds[node - 1];
                if (!sameId((*saved.runtimeNodeIds)[node].data(), compiledNode))
                    return Result::InvalidBinary;
            }
        } else if (compiled.nodeCount != 1) {
            return Result::InvalidBinary;
        }
        gameIoPorts.push_back(port);
    }

    auto workBytes = parentProgram.workBytes();
    auto defaultsCount = parentProgram.parameterValueCount();
    if (defaultsCount > std::numeric_limits<std::size_t>::max() / sizeof(double))
        return Result::InsufficientWork;
    result = rootWork.allocate(workBytes, parentProgram.workAlignment());
    if (result != Result::Ok) return result;
    result = rootDefaults.allocate(defaultsCount * sizeof(double), parentProgram.defaultsAlignment());
    if (result != Result::Ok) return result;
    rootInstance.emplace();
    result = rootInstance->initialize(parentProgram, static_cast<double*>(rootWork.data),
        (workBytes + sizeof(double) - 1) / sizeof(double),
        static_cast<double*>(rootDefaults.data), defaultsCount);
    if (result != Result::Ok) return result;
    result = rootInstance->enableForceRequests();
    if (result != Result::Ok) return result;
    if (!gameIoPorts.empty()) {
        result = NamedIo::create(*rootInstance, gameIoPorts.data(), gameIoPorts.size(),
            options.eventCapacity, gameIo);
        if (result != Result::Ok) return result;
        lifecycle.emplace(*rootInstance, *gameIo);
    } else {
        lifecycle.emplace(*rootInstance);
    }
    return Result::Ok;
}

auto SpawnableHost::Impl::applyCaptures(
    TemplateState& state, GeneratorInstanceId id,
    const std::optional<std::vector<SpawnablePackageCapture>>& inputs, bool live) -> Result
{
    if (!inputs || inputs->empty()) return Result::Ok;
    auto captures = std::vector<GeneratorCapture>{};
    for (const auto& binding : *inputs) {
        auto values = std::array<double, 4>{};
        auto width = std::uint8_t{};
        auto result = readRoot(binding.sourceNodeId.data(), values.data(), width);
        if (result != Result::Ok) return result;
        result = appendCapture(captures, binding, values.data(), width);
        if (result != Result::Ok) return result;
    }
    return live ? state.pool->setLive(id, captures.data(), captures.size()) :
        state.pool->setCaptured(id, captures.data(), captures.size());
}

auto SpawnableHost::Impl::startRoot(double initialFrameDelta) -> Result
{
    if (started) return Result::Ok;
    auto result = rootInstance->setHostQuality(quality);
    if (result != Result::Ok) return result;
    result = rootInstance->setTimeContext(0, initialFrameDelta);
    if (result != Result::Ok) return result;
    auto sample = LifetimeSample{};
    const auto* startNode = packageData.onStartNodeId
        ? packageData.onStartNodeId->data() : nullptr;
    result = lifecycle->start(Lifetime::infinite(), startNode, options.instructionBudget, sample);
    if (result != Result::Ok) return result;
    started = true;
    rootAge = 0;
    time = 0;
    rootEnding = sample.expired;
    result = pump(0);
    if (result == Result::Ok) result = dispatchScheduled(0);
    if (result == Result::Ok) result = beginAutoEnd(0);
    if (result == Result::Ok) result = notifyOwnerDestroy(0);
    if (result == Result::Ok) result = finishIfQuiescent();
    return result;
}

auto SpawnableHost::Impl::evaluateParent(double at) -> Result
{
    if (!std::isfinite(at) || at < rootAge) return Result::InvalidArgument;
    if (at == rootAge) return Result::Ok;
    auto result = rootInstance->setHostQuality(quality);
    if (result != Result::Ok) return result;
    result = rootInstance->setTimeContext(at, frameDelta);
    if (result != Result::Ok) return result;
    if (rootEnding) {
        result = lifecycle->drain(at);
        if (result == Result::Ok) rootAge = at;
        return result;
    }
    auto sample = LifetimeSample{};
    result = lifecycle->advance(at, sample);
    if (result == Result::Ok) {
        rootAge = sample.age;
        rootEnding = sample.expired;
    }
    return result;
}

auto SpawnableHost::Impl::evaluateFrame(double at) -> Result
{
    auto result = evaluateParent(at);
    if (result != Result::Ok) return result;
    result = applyAuthoredForce();
    if (result != Result::Ok || rootForced) return result;
    auto evaluated = std::vector<OwnerNotice>{};
    for (auto round = 0u; round < 128; ++round) {
        auto foundPending = false;
        for (auto& statePointer : templates) {
            auto& state = *statePointer;
            auto instanceIds = std::vector<GeneratorInstanceId>{};
            instanceIds.reserve(state.instances.size());
            for (const auto& instance : state.instances)
                instanceIds.push_back(instance.id);
            for (auto id : instanceIds) {
                const auto alreadyEvaluated = std::any_of(evaluated.begin(), evaluated.end(),
                    [&](const auto& item) {
                        return item.instanceId == id &&
                            sameId(item.containerId.data(), state.data->containerId.data());
                    });
                if (alreadyEvaluated) continue;
                foundPending = true;
                evaluated.push_back({state.data->containerId, id});
                result = state.pool->setHostQuality(id, quality);
                if (result != Result::Ok) return result;
                result = state.pool->setTimeContext(id, at, frameDelta);
                if (result == Result::Ok)
                    result = applyCaptures(state, id, state.data->liveInputs, true);
                if (result == Result::Ok) result = state.pool->evaluate(id, at);
                if (result == Result::Ok) result = pump(at);
                if (result != Result::Ok) return result;
                if (rootForced) return Result::Ok;
            }
        }
        if (!foundPending) return Result::Ok;
    }
    return Result::InstructionBudgetExceeded;
}

auto SpawnableHost::Impl::spawnRoute(const CapturedRoute& captured, double at) -> Result
{
    if (!captured.route) return Result::InvalidBinary;
    auto target = findTemplate(captured.route->targetContainerId.data());
    if (!target) return Result::InvalidBinary;
    auto captures = captured.captures;
    for (const auto& binding : captured.route->captures) {
        if (binding.sourceContainerId) continue;
        auto result = appendParentCapture(captures, binding);
        if (result != Result::Ok) return result;
    }
    auto id = GeneratorInstanceId{};
    return target->pool->spawn(at, captures.data(), captures.size(), id);
}

auto SpawnableHost::Impl::processPoolEvent(TemplateState& state, const GeneratorEvent& event,
                                            double at) -> Result
{
    if (event.kind == GeneratorEventKind::Spawn) {
        auto result = state.pool->setHostQuality(event.instanceId, quality);
        if (result != Result::Ok) return result;
        result = state.pool->setTimeContext(event.instanceId, event.startTime, frameDelta);
        if (result == Result::Ok)
            result = applyCaptures(state, event.instanceId, state.data->capturedInputs, false);
        if (result == Result::Ok)
            result = applyCaptures(state, event.instanceId, state.data->liveInputs, true);
        if (result == Result::Ok) result = state.pool->start(event.instanceId);
        if (result != Result::Ok) return result;
        auto life = std::numeric_limits<double>::infinity();
        if (state.data->lifecycle && state.data->lifecycle->lifeNodeId) {
            auto values = std::array<double, 4>{};
            auto width = std::uint8_t{};
            result = readChild(state, event.instanceId, state.data->lifecycle->lifeNodeId->data(),
                               values.data(), width);
            if (result != Result::Ok) return result;
            if (width != 1 || !std::isfinite(values[0]) || values[0] < 0 ||
                !std::isfinite(event.startTime + values[0]))
                return Result::InvalidArgument;
            life = values[0];
        }
        state.instances.push_back({event.instanceId, event.startTime, life});
        return appendEvent(SpawnableHostEventKind::Spawn, event.containerId, event.instanceId,
                           event.startTime, event.seed);
    }
    if (event.kind == GeneratorEventKind::Destroy) {
        auto instance = findInstance(state, event.instanceId);
        if (!instance) return Result::InvalidHandle;
        auto expiry = instance->start + instance->life;
        if (packageData.hasChildDeferredSignals) {
            auto elapsed = double{};
            auto result = state.pool->elapsed(event.instanceId, elapsed);
            if (result != Result::Ok) return result;
            expiry = instance->start + elapsed;
        }
        auto result = appendEvent(SpawnableHostEventKind::Destroy, event.containerId,
                                  event.instanceId, expiry, event.seed);
        if (result != Result::Ok) return result;
        result = evaluateParent(expiry);
        if (result != Result::Ok) return result;
        for (const auto& route : packageData.destroyRoutes) {
            if (!sameId(route.sourceContainerId.data(), event.containerId)) continue;
            auto frozen = CapturedRoute{};
            frozen.route = &route;
            for (const auto& binding : route.captures) {
                if (!binding.sourceContainerId) continue;
                if (!sameId(binding.sourceContainerId->data(), event.containerId))
                    return Result::InvalidBinary;
                auto values = std::array<double, 4>{};
                auto width = std::uint8_t{};
                result = readChild(state, event.instanceId, binding.sourceNodeId.data(),
                                   values.data(), width);
                if (result != Result::Ok) return result;
                result = appendCapture(frozen.captures, binding, values.data(), width);
                if (result != Result::Ok) return result;
            }
            if (route.delayNodeId) {
                auto values = std::array<double, 4>{};
                auto width = std::uint8_t{};
                result = readRoot(route.delayNodeId->data(), values.data(), width);
                if (result != Result::Ok) return result;
                if (width != 1 || !std::isfinite(values[0]) || values[0] < 0 ||
                    !std::isfinite(expiry + values[0]))
                    return Result::InvalidArgument;
                if (!nextToken) return Result::InsufficientWork;
                const auto token = nextToken++;
                const auto due = expiry + values[0];
                result = lifecycle->schedule(due, token);
                if (result != Result::Ok) return result;
                scheduledRoutes.push_back({token, std::move(frozen), event.instanceId});
            } else {
                result = spawnRoute(frozen, expiry);
                if (result != Result::Ok) return result;
            }
        }
        result = state.pool->finalize(event.instanceId);
        if (result != Result::Ok) return result;
        state.instances.erase(std::remove_if(state.instances.begin(), state.instances.end(),
            [&](const auto& item) { return item.id == event.instanceId; }), state.instances.end());
        ownerNotices.erase(std::remove_if(ownerNotices.begin(), ownerNotices.end(),
            [&](const auto& item) {
                return item.instanceId == event.instanceId &&
                    sameId(item.containerId.data(), event.containerId);
            }), ownerNotices.end());
        return evaluateParent(at);
    }
    if (event.kind == GeneratorEventKind::Stop) {
        const auto instanceId = event.instanceId;
        state.instances.erase(std::remove_if(state.instances.begin(), state.instances.end(),
            [&](const auto& item) { return item.id == instanceId; }), state.instances.end());
        ownerNotices.erase(std::remove_if(ownerNotices.begin(), ownerNotices.end(),
            [&](const auto& item) {
                return item.instanceId == instanceId &&
                    sameId(item.containerId.data(), event.containerId);
            }), ownerNotices.end());
        return appendEvent(SpawnableHostEventKind::Stop, event.containerId, instanceId,
                           at, event.seed);
    }
    return Result::InvalidBinary;
}

auto SpawnableHost::Impl::pump(double at) -> Result
{
    const auto fail = [&](Result result) {
        if (result == Result::QueueRejected) pendingPump = true;
        return result;
    };
    for (auto round = 0u; round < 128; ++round) {
        auto controlResult = applyAuthoredForce();
        if (controlResult != Result::Ok || rootForced) return fail(controlResult);
        auto progress = false;
        for (auto& statePointer : templates) {
            auto& state = *statePointer;
            auto processed = std::size_t{};
            auto result = state.pool->processSpawns(128, processed);
            if (result != Result::Ok) return fail(result);
            progress = progress || processed != 0;
            if (eventCount == events.size() &&
                (state.pool->pendingEventCount() || state.pool->pendingSignalEventCount()))
                return fail(Result::QueueRejected);
            while (eventCount < events.size()) {
                auto event = GeneratorEvent{};
                if (!state.pool->popEvent(event)) break;
                progress = true;
                result = processPoolEvent(state, event, at);
                if (result != Result::Ok) return fail(result);
                controlResult = applyAuthoredForce();
                if (controlResult != Result::Ok || rootForced) return fail(controlResult);
            }
            while (eventCount < events.size()) {
                auto signal = GeneratorSignalEvent{};
                if (!state.pool->popSignalEvent(signal)) break;
                progress = true;
                result = appendEvent(SpawnableHostEventKind::Signal, signal.containerId,
                    signal.instanceId, signal.time, signal.seed, signal.nodeId);
                if (result != Result::Ok) return fail(result);
                result = evaluateParent(signal.time);
                if (result != Result::Ok) return fail(result);
                result = rootInstance->setTimeContext(signal.time, frameDelta);
                if (result != Result::Ok) return fail(result);
                result = lifecycle->validateSignalTime(signal.time);
                if (result != Result::Ok) return fail(result);
                for (const auto& route : packageData.signalRoutes) {
                    if (!sameId(route.sourceContainerId.data(), signal.containerId) ||
                        !sameId(route.sourceNodeId.data(), signal.nodeId)) continue;
                    result = rootInstance->dispatchSignal(route.targetNodeId.data(), signal.time,
                                                         options.instructionBudget);
                    if (result != Result::Ok) return fail(result);
                }
            }
            if (eventCount == events.size() &&
                (state.pool->pendingEventCount() || state.pool->pendingSignalEventCount()))
                return fail(Result::QueueRejected);
        }
        if (!progress) {
            pendingPump = false;
            return Result::Ok;
        }
    }
    return fail(Result::InstructionBudgetExceeded);
}

auto SpawnableHost::Impl::nextDeferredTime(double& out) -> Result
{
    out = std::numeric_limits<double>::infinity();
    if (!started) return Result::Ok;
    auto pending = false;
    auto result = lifecycle->nextDeferredSignal(out, pending);
    if (result != Result::Ok) return result;
    if (!pending) out = std::numeric_limits<double>::infinity();
    return Result::Ok;
}

auto SpawnableHost::Impl::nextChildDeferredTime(double& out) -> Result
{
    out = std::numeric_limits<double>::infinity();
    for (const auto& statePointer : templates) {
        const auto& state = *statePointer;
        for (const auto& instance : state.instances) {
            auto due = double{};
            auto pending = false;
            auto result = state.pool->nextDeferredSignal(instance.id, due, pending);
            if (result != Result::Ok) return result;
            if (pending) out = std::min(out, due);
        }
    }
    return Result::Ok;
}

auto SpawnableHost::Impl::nextScheduledTime(double& out) -> Result
{
    out = std::numeric_limits<double>::infinity();
    if (started) {
        auto scheduled = ScheduledEvent{};
        auto hasScheduled = false;
        auto result = lifecycle->nextScheduled(scheduled, hasScheduled);
        if (result != Result::Ok) return result;
        if (hasScheduled) out = scheduled.time;
    }
    auto due = double{};
    auto result = nextDeferredTime(due);
    if (result != Result::Ok) return result;
    out = std::min(out, due);
    result = nextChildDeferredTime(due);
    if (result != Result::Ok) return result;
    out = std::min(out, due);
    return Result::Ok;
}

auto SpawnableHost::Impl::dispatchScheduled(double at) -> Result
{
    for (auto round = 0u; round < 128; ++round) {
        if (rootForced) return Result::Ok;
        auto scheduled = ScheduledEvent{};
        auto hasScheduled = false;
        auto result = lifecycle->takeScheduled(scheduled, hasScheduled);
        if (result != Result::Ok) return result;
        if (hasScheduled) {
            if (scheduled.time != at) return Result::InvalidArgument;
            auto found = std::find_if(scheduledRoutes.begin(), scheduledRoutes.end(),
                [&](const auto& item) { return item.token == scheduled.token; });
            if (found == scheduledRoutes.end()) return Result::InvalidBinary;
            auto continuation = std::move(found->continuation);
            scheduledRoutes.erase(found);
            result = spawnRoute(continuation, at);
            if (result != Result::Ok) return result;
        } else {
            auto due = double{};
            auto hasSignal = false;
            result = lifecycle->nextDeferredSignal(due, hasSignal);
            if (result != Result::Ok) return result;
            if (hasSignal && due == at) {
                result = lifecycle->dispatchDeferredSignal(at, options.instructionBudget);
                if (result != Result::Ok) return result;
            } else {
                auto childDue = double{};
                result = nextChildDeferredTime(childDue);
                if (result != Result::Ok) return result;
                if (childDue != at) return Result::Ok;
                for (auto& statePointer : templates) {
                    auto& state = *statePointer;
                    auto ids = std::vector<GeneratorInstanceId>{};
                    for (const auto& instance : state.instances) ids.push_back(instance.id);
                    for (auto id : ids) {
                        auto dueTime = double{};
                        auto pending = false;
                        result = state.pool->nextDeferredSignal(id, dueTime, pending);
                        if (result != Result::Ok) return result;
                        if (!pending || dueTime != at) continue;
                        result = state.pool->dispatchDeferredSignal(id, at, options.instructionBudget);
                        if (result != Result::Ok) return result;
                    }
                }
            }
        }
        result = pump(at);
        if (result != Result::Ok) return result;
    }
    auto next = double{};
    auto result = nextScheduledTime(next);
    if (result != Result::Ok) return result;
    return next == at ? Result::InstructionBudgetExceeded : Result::Ok;
}

auto SpawnableHost::Impl::notifyOwnerDestroy(double at) -> Result
{
    if (!rootEnding || rootForced) return Result::Ok;
    for (auto round = 0u; round < 128; ++round) {
        auto pending = std::vector<OwnerNotice>{};
        for (const auto& statePointer : templates) {
            const auto& state = *statePointer;
            for (const auto& instance : state.instances) {
                const auto notified = std::any_of(ownerNotices.begin(), ownerNotices.end(),
                    [&](const auto& item) {
                        return item.instanceId == instance.id &&
                            sameId(item.containerId.data(), state.data->containerId.data());
                    });
                if (!notified) pending.push_back({state.data->containerId, instance.id});
            }
        }
        if (pending.empty()) return Result::Ok;
        for (const auto& item : pending) {
            auto state = findTemplate(item.containerId.data());
            if (!state) return Result::InvalidBinary;
            auto instance = findInstance(*state, item.instanceId);
            if (!instance) continue;
            if (state->data->lifecycle && state->data->lifecycle->ownerDestroyNodeId) {
                auto result = state->pool->ownerDestroy(item.instanceId,
                    state->data->lifecycle->ownerDestroyNodeId->data(), at, options.instructionBudget);
                if (result != Result::Ok) return result;
            }
            ownerNotices.push_back(item);
        }
        auto result = pump(at);
        if (result == Result::Ok) result = dispatchScheduled(at);
        if (result != Result::Ok) return result;
    }
    return Result::InstructionBudgetExceeded;
}

auto SpawnableHost::Impl::beginAutoEnd(double at) -> Result
{
    if (rootEnding || rootFinished || !packageData.autoDestroyWhenIdle || templates.empty())
        return Result::Ok;
    for (const auto& state : templates)
        if (state->pool->activeCount() != 0) return Result::Ok;
    if (!scheduledRoutes.empty()) return Result::Ok;
    auto due = double{};
    auto result = nextDeferredTime(due);
    if (result != Result::Ok || std::isfinite(due)) return result;
    result = lifecycle->requestNaturalEnd();
    if (result != Result::Ok) return result;
    rootEnding = true;
    result = notifyOwnerDestroy(at);
    if (result == Result::Ok) result = finishIfQuiescent();
    return result;
}

auto SpawnableHost::Impl::finishIfQuiescent() -> Result
{
    if (!rootEnding || rootFinished || activeCount() != 0 || !scheduledRoutes.empty())
        return Result::Ok;
    auto due = double{};
    auto result = nextDeferredTime(due);
    if (result != Result::Ok) return result;
    if (std::isfinite(due)) return Result::Ok;
    if (!rootDestroySent && packageData.onDestroyNodeId) {
        result = lifecycle->destroy(packageData.onDestroyNodeId->data(), options.instructionBudget);
        if (result != Result::Ok) return result;
        rootDestroySent = true;
        result = pump(rootAge);
        if (result == Result::Ok) result = dispatchScheduled(rootAge);
        if (result == Result::Ok) result = notifyOwnerDestroy(rootAge);
        if (result != Result::Ok) return result;
        if (activeCount() != 0 || !scheduledRoutes.empty()) return Result::Ok;
        result = nextDeferredTime(due);
        if (result != Result::Ok || std::isfinite(due)) return result;
    }
    result = lifecycle->finish();
    if (result == Result::Ok) rootFinished = true;
    return result;
}

auto SpawnableHost::advance(double at, double delta) -> Result
{
    auto& host = *impl_;
    if (host.operationActive) return Result::Busy;
    auto operation = OwnerOperation(host.operationActive);
    if (!std::isfinite(at) || at < 0 || (host.time >= 0 && at < host.time) || at < host.rootAge ||
        !std::isfinite(delta) || delta < 0)
        return Result::InvalidArgument;
    if (host.rootForced) return Result::NotReady;
    if (host.failure != Result::Ok) return host.failure;
    if (host.rootStopped) return Result::NotReady;
    try {
        host.frameDelta = delta;
        auto result = Result::Ok;
        if (host.pendingPump) {
            result = host.pump(host.rootAge);
            if (result == Result::Ok) result = host.dispatchScheduled(host.rootAge);
            if (result == Result::Ok) result = host.pump(host.rootAge);
            if (result == Result::Ok) result = host.beginAutoEnd(host.rootAge);
            if (result == Result::Ok) result = host.notifyOwnerDestroy(host.rootAge);
            if (result == Result::Ok) result = host.finishIfQuiescent();
            if (result != Result::Ok) {
                if (result != Result::QueueRejected) host.failure = result;
                return result;
            }
        }
        result = host.startRoot(delta);
        if (result != Result::Ok) {
            if (result == Result::QueueRejected) host.pendingPump = true;
            else host.failure = result;
            return result;
        }
        for (auto round = 0u; !host.rootFinished && round < 128; ++round) {
            auto nextDue = std::numeric_limits<double>::infinity();
            result = host.nextScheduledTime(nextDue);
            if (result != Result::Ok) break;
            auto childExpiry = std::numeric_limits<double>::infinity();
            for (const auto& state : host.templates) {
                for (const auto& instance : state->instances) {
                    if (!std::isfinite(instance.life)) continue;
                    const auto expiry = instance.start + instance.life;
                    if (expiry > host.rootAge && expiry <= at)
                        childExpiry = std::min(childExpiry, expiry);
                }
            }
            const auto next = std::min(at, std::min(nextDue, childExpiry));
            if (next <= host.rootAge) break;
            result = host.evaluateFrame(next);
            if (result == Result::Ok) result = host.dispatchScheduled(next);
            if (result == Result::Ok) result = host.pump(next);
            if (result == Result::Ok) result = host.beginAutoEnd(next);
            if (result == Result::Ok) result = host.notifyOwnerDestroy(next);
            if (result == Result::Ok) result = host.finishIfQuiescent();
            if (result != Result::Ok) break;
            if (next >= at) break;
            if (round == 127) result = Result::InstructionBudgetExceeded;
        }
        if (result == Result::Ok) host.time = at;
        else if (result == Result::QueueRejected) host.pendingPump = true;
        else host.failure = result;
        return result;
    } catch (const std::bad_alloc&) {
        host.failure = Result::InsufficientWork;
        return host.failure;
    }
}

auto SpawnableHost::setHostQuality(const HostQualitySnapshot& snapshot) -> Result
{
    if (impl_->operationActive) return Result::Busy;
    if (impl_->rootForced || !impl_->rootInstance) return Result::NotReady;
    if (static_cast<unsigned>(snapshot.availability) >
            static_cast<unsigned>(HostQualityAvailability::Unsupported) ||
        (snapshot.availability == HostQualityAvailability::Known &&
            (static_cast<unsigned>(snapshot.tier) > static_cast<unsigned>(HostQualityTier::Cinematic) ||
             static_cast<unsigned>(snapshot.source) > static_cast<unsigned>(HostQualitySource::PlatformPolicy))))
        return Result::InvalidValueType;
    auto result = impl_->rootInstance->setHostQuality(snapshot);
    if (result != Result::Ok) return result;
    for (auto& state : impl_->templates)
        for (const auto& instance : state->instances)
        {
            result = state->pool->setHostQuality(instance.id, snapshot);
            if (result != Result::Ok) return result;
        }
    impl_->quality = snapshot;
    return Result::Ok;
}

auto SpawnableHost::dispatchSignal(const std::uint8_t nodeId[16]) -> Result
{
    auto& host = *impl_;
    if (host.operationActive) return Result::Busy;
    auto operation = OwnerOperation(host.operationActive);
    if (!nodeId || !isNonzero(nodeId)) return Result::InvalidArgument;
    if (!host.started || host.rootEnding || host.rootFinished || host.rootStopped)
        return Result::NotReady;
    if (host.failure != Result::Ok) return host.failure;
    auto result = host.rootInstance->setTimeContext(host.rootAge, host.frameDelta);
    if (result == Result::Ok) result = host.lifecycle->validateSignalTime(host.rootAge);
    if (result == Result::Ok)
        result = host.rootInstance->dispatchSignal(nodeId, host.rootAge, host.options.instructionBudget);
    if (result == Result::Ok) result = host.pump(host.rootAge);
    if (result == Result::Ok) result = host.dispatchScheduled(host.rootAge);
    if (result == Result::Ok) result = host.beginAutoEnd(host.rootAge);
    if (result == Result::Ok) result = host.notifyOwnerDestroy(host.rootAge);
    if (result == Result::Ok) result = host.finishIfQuiescent();
    if (result != Result::Ok) host.failure = result;
    return result;
}

auto SpawnableHost::fireGameSignal(const char* name) -> Result
{
    auto& host = *impl_;
    if (host.operationActive) return Result::Busy;
    auto operation = OwnerOperation(host.operationActive);
    if (!host.gameIo) return Result::NotReady;
    if (!host.started || host.rootEnding || host.rootFinished || host.rootStopped)
        return Result::NotReady;
    if (host.failure != Result::Ok) return host.failure;
    auto result = host.rootInstance->setTimeContext(host.rootAge, host.frameDelta);
    if (result == Result::Ok) result = host.lifecycle->validateSignalTime(host.rootAge);
    if (result == Result::Ok)
        result = host.gameIo->fireSignal(name, host.rootAge, host.options.instructionBudget);
    if (result == Result::Ok) result = host.pump(host.rootAge);
    if (result == Result::Ok) result = host.dispatchScheduled(host.rootAge);
    if (result == Result::Ok) result = host.beginAutoEnd(host.rootAge);
    if (result == Result::Ok) result = host.notifyOwnerDestroy(host.rootAge);
    if (result == Result::Ok) result = host.finishIfQuiescent();
    if (result != Result::Ok) host.failure = result;
    return result;
}

auto SpawnableHost::setGameProperty(const char* name, const double* values,
                                    std::size_t width) -> Result
{
    return impl_->gameIo ? impl_->gameIo->setProperty(name, values, width) : Result::NotReady;
}

auto SpawnableHost::setOwnerMatrix(const double* values, std::size_t width) -> Result
{
    return impl_->gameIo ? impl_->gameIo->setOwnerMatrix(values, width) : Result::NotReady;
}

auto SpawnableHost::requestNaturalEnd() -> Result
{
    auto& host = *impl_;
    if (host.operationActive) return Result::Busy;
    auto operation = OwnerOperation(host.operationActive);
    if (!host.started || host.rootStopped) return Result::NotReady;
    if (host.failure != Result::Ok) return host.failure;
    if (host.rootEnding || host.rootFinished) return Result::Ok;
    auto result = host.lifecycle->requestNaturalEnd();
    if (result == Result::Ok) {
        host.rootEnding = true;
        result = host.notifyOwnerDestroy(host.rootAge);
    }
    if (result == Result::Ok) result = host.finishIfQuiescent();
    if (result != Result::Ok) host.failure = result;
    return result;
}

auto SpawnableHost::stop() -> Result
{
    auto& host = *impl_;
    if (host.operationActive) return Result::Busy;
    auto operation = OwnerOperation(host.operationActive);
    if (host.rootStopped) return Result::NotReady;
    for (const auto& state : host.templates) {
        auto result = state->pool->reset();
        if (result != Result::Ok) return result;
    }
    auto result = host.pump(host.started ? host.rootAge : 0);
    if (result != Result::Ok) return result;
    if (host.started && !host.rootFinished) {
        result = host.lifecycle->stop();
        if (result != Result::Ok) return result;
    }
    host.scheduledRoutes.clear();
    host.ownerNotices.clear();
    host.rootStopped = true;
    return Result::Ok;
}

auto SpawnableHost::notifyEndRequest(const SpawnableScope& scope,
                                     const std::uint8_t nodeId[16]) -> Result
{
    auto& host = *impl_;
    if (!validScope(scope) || !nodeId || !isNonzero(nodeId)) return Result::InvalidArgument;
    if (host.operationActive) return Result::Busy;
    // The existing root signal API owns its own guard and routes actual work.
    if (scope.kind == SpawnableScopeKind::Effect) return dispatchSignal(nodeId);
    auto operation = OwnerOperation(host.operationActive);
    if (!host.started || host.rootStopped || host.rootFinished) return Result::NotReady;
    if (host.failure != Result::Ok) return host.failure;
    auto state = host.findTemplate(scope.containerId);
    if (!state || !host.findInstance(*state, scope.instanceId)) return Result::InvalidHandle;
    auto result = state->pool->dispatchSignal(scope.instanceId, nodeId, host.rootAge,
                                              host.options.instructionBudget);
    if (result == Result::Ok) result = host.pump(host.rootAge);
    if (result == Result::Ok) result = host.dispatchScheduled(host.rootAge);
    if (result == Result::Ok) result = host.beginAutoEnd(host.rootAge);
    if (result == Result::Ok) result = host.notifyOwnerDestroy(host.rootAge);
    if (result == Result::Ok) result = host.finishIfQuiescent();
    if (result == Result::QueueRejected) host.pendingPump = true;
    else if (result != Result::Ok && result != Result::InvalidHandle &&
             result != Result::InvalidArgument && result != Result::NotReady)
        host.failure = result;
    return result;
}

auto SpawnableHost::forceTerminate(const SpawnableScope& scope) -> Result
{
    auto& host = *impl_;
    if (!validScope(scope)) return Result::InvalidArgument;
    if (host.operationActive) return Result::Busy;
    auto operation = OwnerOperation(host.operationActive);
    return host.forceTerminate(scope);
}

auto SpawnableHost::Impl::applyAuthoredForce() -> Result
{
    if (rootForced) return Result::Ok;
    auto forceScope = std::uint8_t{};
    auto result = rootInstance ? rootInstance->pendingForceRequest(forceScope) : Result::Ok;
    if (result != Result::Ok) return result;
    if (forceScope != 0) return forceTerminate({});
    for (auto& state : templates)
    {
        for (auto index = std::size_t{}; index < state->instances.size();)
        {
            auto id = state->instances[index].id;
            result = state->pool->pendingForceRequest(id, forceScope);
            if (result != Result::Ok) return result;
            if (forceScope == 2) return forceTerminate({});
            if (forceScope == 0) { ++index; continue; }
            auto scope = SpawnableScope{};
            scope.kind = SpawnableScopeKind::Instance;
            std::memcpy(scope.containerId, state->data->containerId.data(), 16);
            scope.instanceId = id;
            result = forceTerminate(scope);
            if (result != Result::Ok) return result;
        }
    }
    return Result::Ok;
}

auto SpawnableHost::Impl::forceTerminate(const SpawnableScope& scope) -> Result
{
    auto& host = *this;
    auto at = host.started ? host.rootAge : 0;
    if (scope.kind == SpawnableScopeKind::Effect) {
        if (host.rootForced) return Result::Ok;
        // No asynchronous pool job can exist here: pools are private and all
        // evaluations complete inline inside guarded owner operations.
        if (host.started && !host.rootFinished && !host.rootStopped) {
            auto result = host.lifecycle->stop();
            if (result != Result::Ok) return result;
        }
        for (auto& state : host.templates) {
            auto result = state->pool->forceTerminateAll();
            if (result != Result::Ok) return result;
            state->instances.clear();
        }
        host.scheduledRoutes.clear();
        host.ownerNotices.clear();
        if (host.gameIo) {
            auto ignored = NamedIoEvent{};
            while (host.gameIo->popEvent(ignored)) {}
        }
        // Release facades before the instance and its caller-owned storage.
        // Even a failed first start can have initialized custom/deferred state.
        host.lifecycle.reset();
        host.gameIo.reset();
        host.rootInstance.reset();
        host.rootDefaults.release();
        host.rootWork.release();
        host.eventHead = host.eventCount = 0;
        host.pendingPump = false;
        host.rootStopped = host.rootFinished = host.rootForced = true;
        return host.appendEvent(SpawnableHostEventKind::ForceTerminated, nullptr, 0, at, 0);
    }

    auto state = host.findTemplate(scope.containerId);
    if (!state) return Result::InvalidHandle;
    auto active = host.findInstance(*state, scope.instanceId) != nullptr;
    auto ownsPending = [&](const Impl::ScheduledRoute& item) {
        return item.sourceInstanceId == scope.instanceId && item.continuation.route &&
            sameId(item.continuation.route->sourceContainerId.data(), scope.containerId);
    };
    auto hasPending = std::any_of(host.scheduledRoutes.begin(), host.scheduledRoutes.end(), ownsPending);
    auto matches = [&](const SpawnableHostEvent& event) {
        return event.instanceId == scope.instanceId && sameId(event.containerId, scope.containerId) &&
            event.kind != SpawnableHostEventKind::ForceTerminated;
    };
    auto removed = std::size_t{};
    for (auto index = std::size_t{}; index < host.eventCount; ++index)
        if (matches(host.events[(host.eventHead + index) % host.events.size()])) ++removed;
    if ((active || removed || hasPending) && host.eventCount - removed == host.events.size())
        return Result::QueueRejected;
    auto result = state->pool->forceTerminate(scope.instanceId);
    if (result != Result::Ok) return result;
    for (const auto& item : host.scheduledRoutes) {
        if (!ownsPending(item)) continue;
        // Host's guard establishes the lifecycle job boundary before any purge.
        result = host.lifecycle->cancelScheduled(item.token);
        if (result != Result::Ok) return result;
    }
    host.scheduledRoutes.erase(std::remove_if(host.scheduledRoutes.begin(), host.scheduledRoutes.end(),
        ownsPending), host.scheduledRoutes.end());
    auto kept = std::size_t{};
    for (auto index = std::size_t{}; index < host.eventCount; ++index) {
        const auto& event = host.events[(host.eventHead + index) % host.events.size()];
        if (matches(event)) continue;
        if (kept != index) host.events[(host.eventHead + kept) % host.events.size()] = event;
        ++kept;
    }
    host.eventCount = kept;
    state->instances.erase(std::remove_if(state->instances.begin(), state->instances.end(),
        [&](const auto& item) { return item.id == scope.instanceId; }), state->instances.end());
    host.ownerNotices.erase(std::remove_if(host.ownerNotices.begin(), host.ownerNotices.end(),
        [&](const auto& item) {
            return item.instanceId == scope.instanceId && sameId(item.containerId.data(), scope.containerId);
        }), host.ownerNotices.end());
    // Do not pump: that could execute unrelated natural routes while cancelling.
    // Root auto-end is deliberately left to a subsequent normal advance.
    return active || removed || hasPending
        ? host.appendEvent(SpawnableHostEventKind::ForceTerminated, scope.containerId,
                           scope.instanceId, at, 0)
        : Result::Ok;
}

auto SpawnableHost::debugGeneration() const noexcept -> std::uint64_t
{
    return impl_->debugGeneration;
}

auto SpawnableHost::debugTarget(const SpawnableScope& scope, SpawnableDebugTarget& out) noexcept -> Result
{
    out = {};
    if (!validScope(scope)) return Result::InvalidArgument;
    if (scope.kind == SpawnableScopeKind::Effect) {
        if (!impl_->rootInstance || impl_->rootFinished || impl_->rootForced) return Result::NotReady;
    } else {
        auto state = impl_->findTemplate(scope.containerId);
        if (!state || !impl_->findInstance(*state, scope.instanceId)) return Result::InvalidHandle;
    }
    out = {impl_->debugGeneration, scope};
    return Result::Ok;
}

auto SpawnableHost::setDebugHooks(const SpawnableDebugTarget& target, const ExecutionDebugHooks& hooks) noexcept -> Result
{
    if (!target.hostGeneration || target.hostGeneration != impl_->debugGeneration) return Result::InvalidHandle;
    if (!validScope(target.scope)) return Result::InvalidArgument;
    if (target.scope.kind == SpawnableScopeKind::Effect) {
        if (!impl_->rootInstance || (hooks.beforeNode && (impl_->rootFinished || impl_->rootForced))) return Result::NotReady;
        return impl_->rootInstance->setDebugHooks(hooks);
    }
    auto state = impl_->findTemplate(target.scope.containerId);
    if (!state || !impl_->findInstance(*state, target.scope.instanceId)) return Result::InvalidHandle;
    return state->pool->setDebugHooks(target.scope.instanceId, hooks);
}

auto SpawnableHost::rootValue(const std::uint8_t nodeId[16], double out[4],
                              std::uint8_t& width) const -> Result
{
    if (!nodeId || !out) return Result::InvalidArgument;
    if (impl_->rootForced) return Result::NotReady;
    return impl_->readRoot(nodeId, out, width);
}

auto SpawnableHost::childValue(const std::uint8_t containerId[16], GeneratorInstanceId instanceId,
                               const std::uint8_t nodeId[16], double out[4],
                               std::uint8_t& width) const -> Result
{
    if (!containerId || !nodeId || !out) return Result::InvalidArgument;
    auto state = impl_->findTemplate(containerId);
    if (!state || !impl_->findInstance(*state, instanceId)) return Result::InvalidHandle;
    return impl_->readChild(*state, instanceId, nodeId, out, width);
}

auto SpawnableHost::popEvent(SpawnableHostEvent& out) noexcept -> bool
{
    auto& host = *impl_;
    if (!host.eventCount) return false;
    out = host.events[host.eventHead];
    host.eventHead = (host.eventHead + 1) % host.events.size();
    --host.eventCount;
    return true;
}

auto SpawnableHost::popGameIoEvent(NamedIoEvent& out) noexcept -> bool
{
    return impl_->gameIo && impl_->gameIo->popEvent(out);
}

auto SpawnableHost::activeCount() const noexcept -> std::size_t
{
    return impl_->activeCount();
}

auto SpawnableHost::rootFinished() const noexcept -> bool
{
    return impl_->rootFinished;
}

auto SpawnableHost::package() const noexcept -> const SpawnablePackage&
{
    return impl_->packageData;
}

SpawnableHost::SpawnableHost(std::unique_ptr<Impl> impl) noexcept : impl_(std::move(impl)) {}
SpawnableHost::~SpawnableHost() = default;

auto SpawnableHost::create(SpawnablePackage package, const SpawnableHostOptions& options,
                           std::unique_ptr<SpawnableHost>& out) -> Result
{
    out.reset();
    try {
        auto impl = std::make_unique<Impl>(std::move(package), options);
        static auto generation = std::atomic<std::uint64_t>{1};
        auto next = generation.load(std::memory_order_relaxed);
        do {
            if (next == UINT64_MAX) return Result::CounterOverflow;
        } while (!generation.compare_exchange_weak(next, next + 1, std::memory_order_relaxed));
        impl->debugGeneration = next;
        auto result = impl->initialize();
        if (result != Result::Ok) return result;
        out.reset(new SpawnableHost(std::move(impl)));
        return Result::Ok;
    } catch (const std::bad_alloc&) {
        return Result::InsufficientWork;
    }
}
}
