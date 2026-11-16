#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include "liberafx/platform/JobScheduler.h"
#include "liberafx/platform/Profiler.h"

#ifndef LIBRA_RUNTIME_CORE_API
#define LIBRA_RUNTIME_CORE_API
#endif

namespace liberafx {

enum class Result : std::uint8_t {
    Ok,
    InvalidArgument,
    InvalidBinary,
    UnsupportedBinary,
    InsufficientWork,
    Busy,
    QueueRejected,
    NotReady,
    NodeNotFound,
    DivideByZero,
    NonFinite,
    InvalidValueType,
    UnknownCustomNode,
    InvalidHandle,
    InstructionBudgetExceeded
};

enum class CustomExecution : std::uint8_t { Parameter = 1, Signal = 2 };

struct CustomNodeInvocation;
// Parameter callbacks run on scheduler workers; signal callbacks run only from explicit
// owner-thread dispatch. Callbacks may not traverse the graph or allocate during execution.
using CustomNodeFunction = Result (*)(CustomNodeInvocation&) noexcept;
using CustomNodeInitialize = Result (*)(void* state, std::size_t stateBytes, void* context) noexcept;
using CustomNodeCleanup = void (*)(void* state, std::size_t stateBytes, void* context) noexcept;
using SignalObserver = Result (*)(void* context, const std::uint8_t nodeId[16]) noexcept;

struct LIBRA_RUNTIME_CORE_API CustomNodeDescriptor final {
    std::uint8_t typeId[16]{};
    std::uint32_t abiVersion = 0;
    CustomExecution execution = CustomExecution::Parameter;
    std::uint8_t inputCount = 0;
    std::uint8_t inputWidths[4]{};
    std::uint8_t inputIsColor[4]{};
    std::uint8_t outputWidth = 0;
    bool outputIsColor = false;
    std::uint8_t branchCount = 0;
    std::size_t stateBytes = 0;
    CustomNodeFunction function = nullptr;
    CustomNodeInitialize initialize = nullptr;
    CustomNodeCleanup cleanup = nullptr;
    void* context = nullptr;
    // Power-of-two alignment required for state (16 through 64). State storage is
    // placed at this alignment; workAlignment() reports the Program-wide requirement.
    std::size_t stateAlignment = 16;
};

struct CustomNodeInvocation final {
    // inputs/constants are 16-byte aligned; outputs and state meet the Program and
    // descriptor alignment requirements respectively. All double pointers are at
    // least alignof(double)-aligned.
    const double* inputs = nullptr;
    std::uint8_t inputCount = 0;
    const std::uint8_t* inputWidths = nullptr;
    double* outputs = nullptr;
    std::uint8_t outputWidth = 0;
    const double* constants = nullptr;
    std::size_t constantCount = 0;
    void* state = nullptr;
    std::size_t stateBytes = 0;
    double time = 0;
    std::uint8_t selectedBranch = 0xff;
    void* context = nullptr;
    double effectTime = 0;
    double frameDelta = 0;
    bool hasTimeContext = false;
};

struct CustomNodeBinding final {
    const CustomNodeDescriptor* descriptor = nullptr;
    std::size_t stateOffset = 0;
};

struct ProgramGameIoPort final {
    const char* name = nullptr;
    std::uint8_t nodeId[16]{};
    std::uint8_t additionalNodeIds[3][16]{};
    std::uint8_t kind = 0, width = 0, isColor = 0;
    std::uint8_t nodeCount = 0;
};

// Immutable process-lifetime descriptors. IDs use Guid.TryWriteBytes(bigEndian: true).
LIBRA_RUNTIME_CORE_API const CustomNodeDescriptor* builtinNodes(std::size_t& count) noexcept;

struct ValueHandle final {
    const void* programIdentity = nullptr;
    std::uint64_t generation = 0;
    std::uint64_t programGeneration = 0;
    std::uint32_t nodeIndex = 0;
    std::uint8_t componentCount = 0;
};

struct ParameterHandle final {
    const void* programIdentity = nullptr;
    std::uint64_t generation = 0;
    std::uint64_t programGeneration = 0;
    std::uint32_t nodeIndex = 0;
    std::uint8_t inputSlot = 0;
    std::uint8_t component = 0;
};

struct SignalHandle final {
    const void* programIdentity = nullptr;
    std::uint64_t generation = 0;
    std::uint64_t programGeneration = 0;
    std::uint32_t nodeIndex = 0;
};

class LIBRA_RUNTIME_CORE_API Program final {
public:
    // The program and its bytecode must remain immutable and alive while instances use them.
    static Result load(const void* bytes, std::size_t size, Program& out,
                       const ProfilingHooks& profiling = {}) noexcept;
    // V3 requires caller-owned bindings (one entry per instruction). Built-ins are
    // resolved automatically if their type is absent from the caller's registry.
    // The bytecode, descriptor array and binding storage must outlive this Program and its Instances.
    static Result load(const void* bytes, std::size_t size,
                       const CustomNodeDescriptor* registry, std::size_t registryCount,
                       CustomNodeBinding* bindings, std::size_t bindingCount, Program& out,
                       const ProfilingHooks& profiling = {}) noexcept;
    std::size_t nodeCount() const noexcept { return nodeCount_; }
    std::size_t workBytes() const noexcept;
    // Caller-owned work storage must be aligned to this value and contain workBytes().
    std::size_t workAlignment() const noexcept;
    std::size_t parameterValueCount() const noexcept;
    // Defaults contain doubles and need at least defaultsAlignment() alignment.
    std::size_t defaultsAlignment() const noexcept;
    std::size_t bindingCount() const noexcept { return version_ >= 3 && bindings_ != nullptr ? nodeCount_ : 0; }
    std::uint16_t version() const noexcept { return version_; }
    std::size_t gameIoPortCount() const noexcept { return gameIoPortCount_; }
    bool gameIoPort(std::size_t index, ProgramGameIoPort& out) const noexcept;

private:
    friend class Instance;
    const std::uint8_t* bytes_ = nullptr;
    std::uint32_t nodeCount_ = 0;
    std::uint16_t version_ = 0;
    std::uint64_t generation_ = 0;
    CustomNodeBinding* bindings_ = nullptr;
    std::size_t stateBytes_ = 0;
    std::size_t signalRoutesOffset_ = 0;
    std::size_t signalStackOffset_ = 0;
    std::size_t signalStackCapacity_ = 0;
    std::size_t gameIoMetadataOffset_ = 0;
    std::uint16_t gameIoPortCount_ = 0;
};

class LIBRA_RUNTIME_CORE_API Instance final {
public:
    ~Instance();
    // Owner thread only, not during evaluation/signal dispatch. Context and
    // callback code must outlive this Instance, including native state cleanup.
    Result setProfiling(const ProfilingHooks& profiling) noexcept;
    // Call control/read methods from one owner thread; the scheduler runs parameter callbacks on a worker.
    // Work is owned by the caller, must satisfy Program::workAlignment(), and must
    // remain alive until all jobs complete. Optional defaults must satisfy
    // Program::defaultsAlignment().
    Result initialize(const Program& program, double* work, std::size_t workCount) noexcept;
    // The optional defaults buffer is caller-owned, separate from work, and has parameterValueCount() lanes.
    Result initialize(const Program& program, double* work, std::size_t workCount,
                      double* defaults, std::size_t defaultsCount) noexcept;
    Result bindValue(const std::uint8_t nodeId[16], ValueHandle& out) const noexcept;
    Result bindParameter(const std::uint8_t nodeId[16], std::uint8_t inputSlot,
                         std::uint8_t component, ParameterHandle& out) const noexcept;
    Result bindSignal(const std::uint8_t nodeId[16], SignalHandle& out) const noexcept;
    Result value(const ValueHandle& handle, double out[4]) const noexcept;
    Result parameter(const ParameterHandle& handle, double& out) const noexcept;
    Result setParameter(const ParameterHandle& handle, double value) noexcept;
    // Set before each evaluation. Values are supplied by the effect host;
    // frameDelta is the game's frame delta, not an internal scheduler substep.
    Result setTimeContext(double effectTime, double frameDelta) noexcept;
    Result clearTimeContext() noexcept;
    Result updateAsync(double time, Enqueue enqueue, void* queueContext) noexcept;
    Result updateAsync(double time, const JobScheduler& scheduler) noexcept
    {
        return updateAsync(time, scheduler.enqueue, scheduler.context);
    }
    // Legacy slots are A/B (0/1); v3 custom slots index declared numeric input ports (0..3).
    Result setParameter(const std::uint8_t nodeId[16], std::uint8_t inputSlot,
                        std::uint8_t component, double value) noexcept;
    // Capture targets must not have a connected producer, which would mask
    // their default. This is separate from the legacy setParameter behavior.
    Result setUnconnectedParameter(const std::uint8_t nodeId[16], std::uint8_t inputSlot,
                                   std::uint8_t component, double value) noexcept;
    Result result() const noexcept;
    Result setSignalObserver(SignalObserver observer, void* context) noexcept;
    Result value(const std::uint8_t nodeId[16], double& out) const noexcept;
    Result valueComponents(const std::uint8_t nodeId[16], double out[4], std::uint8_t& componentCount) const noexcept;
    // Synchronous, owner-thread-only execution after a successful update.
    // maxInstructions bounds all callbacks across the depth-first traversal. An error
    // stops the remaining branches without rolling back callbacks already executed.
    Result dispatchSignal(const std::uint8_t entryNodeId[16], double time,
                         std::size_t maxInstructions) noexcept;
    Result dispatchSignal(const SignalHandle& entry, double time,
                          std::size_t maxInstructions) noexcept;
    std::size_t capacityGrowthCount() const noexcept { return 0; }

private:
    enum class Phase : std::uint8_t { Idle, Running, Complete };
    static void run(void* context) noexcept;
    Result evaluate() noexcept;
    void cleanupStates() noexcept;

    const Program* program_ = nullptr;
    double* work_ = nullptr;
    double* defaults_ = nullptr;
    double time_ = 0;
    double effectTime_ = 0, frameDelta_ = 0;
    double pendingEffectTime_ = 0, pendingFrameDelta_ = 0;
    bool pendingTimeContext_ = false, evaluatedTimeContext_ = false;
    std::uint64_t generation_ = 0;
    double* stateWork_ = nullptr;
    std::size_t initializedStateCount_ = 0;
    bool dispatching_ = false;
    SignalObserver signalObserver_ = nullptr;
    void* signalObserverContext_ = nullptr;
    Result result_ = Result::NotReady;
    ProfilingHooks profiling_;
    std::atomic<Phase> phase_{Phase::Idle};
};

}
