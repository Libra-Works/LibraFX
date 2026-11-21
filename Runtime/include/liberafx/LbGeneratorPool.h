#pragma once

#include "liberafx/LbVfxRuntime.h"
#include "liberafx/LbLifetime.h"
#include <cstddef>
#include <cstdint>
#include <memory>

namespace liberafx {

using GeneratorInstanceId = std::uint64_t;
using GeneratorInstanceAccess = Result (*)(void *, Instance &) noexcept;

struct GeneratorCapture final {
    std::uint8_t nodeId[16]{};
    std::uint8_t inputSlot = 0;
    std::uint8_t component = 0;
    double value = 0;
};

// Destroy is a local group output, never a game-facing callback.
enum class GeneratorEventKind : std::uint8_t { Spawn = 1, Stop = 2, Destroy = 3 };
struct GeneratorEvent final {
    GeneratorEventKind kind{};
    std::uint8_t containerId[16]{};
    GeneratorInstanceId instanceId = 0;
    double startTime = 0;
    std::uint64_t seed = 0;
};
struct GeneratorSignalEvent final {
    std::uint8_t containerId[16]{};
    GeneratorInstanceId instanceId = 0;
    std::uint8_t nodeId[16]{};
    double time = 0;
    std::uint64_t seed = 0;
};

struct GeneratorLifecycle final {
    std::uint8_t onStartNodeId[16]{};
    std::uint8_t lifeNodeId[16]{};
    std::size_t instructionBudget = 0;
    bool hasOnStart = false;
    bool hasLife = false;
    bool enableDeferredSignals = false;
};

// enqueueSpawn may be called concurrently; all other operations belong to one
// owner thread. The Program, bytecode, registry and bindings must outlive the pool.
// Evaluation is synchronous; events are host data, never engine object operations.
class LIBRA_RUNTIME_CORE_API GeneratorPool final {
public:
    static Result create(const Program& program, const std::uint8_t containerId[16],
        std::size_t maxActive, std::size_t memoryBudgetBytes, std::size_t eventCapacity,
        std::unique_ptr<GeneratorPool>& out) noexcept;
    static Result createWithCaptureLimit(const Program& program, const std::uint8_t containerId[16],
        std::size_t maxActive, std::size_t memoryBudgetBytes, std::size_t eventCapacity,
        std::size_t maxCaptureLanes, std::unique_ptr<GeneratorPool>& out) noexcept;
    ~GeneratorPool();
    GeneratorPool(const GeneratorPool&) = delete;
    GeneratorPool& operator=(const GeneratorPool&) = delete;

    // CaptureAtSpawn: values address unconnected template parameter defaults.
    Result spawn(double time, const GeneratorCapture* captures, std::size_t count,
                 GeneratorInstanceId& out) noexcept;
    // Host-internal ownership tag is copied from a deferred command to the
    // materialized slot. It does not alter the public GeneratorEvent ABI.
    Result spawnTagged(double time, const GeneratorCapture* captures, std::size_t count,
                       std::uint64_t ownerTag, GeneratorInstanceId& out) noexcept;
    // Allocation-free signal callback path. The default limit is 16 lanes;
    // createWithCaptureLimit can increase it to at most 256.
    // Owner processes commands outside callback execution. A rejected command
    // remains queued until resources are available or reset clears it.
    Result enqueueSpawn(double time, const GeneratorCapture* captures, std::size_t count) noexcept;
    Result enqueueSpawnTagged(double time, const GeneratorCapture* captures, std::size_t count,
                              std::uint64_t ownerTag) noexcept;
    Result ownerTag(GeneratorInstanceId id, std::uint64_t& out) const noexcept;
    Result cancelSpawnsOwned(std::uint64_t ownerTag) noexcept;
    Result processSpawns(std::size_t maxCommands, std::size_t& processed) noexcept;
    // Configure only while empty. Once configured, spawn creates an initialized
    // instance; the owner applies initial Live values, then explicitly starts it.
    Result configureLifecycle(const GeneratorLifecycle& lifecycle) noexcept;
    Result start(GeneratorInstanceId id) noexcept;
    // After draining and routing a natural Destroy event, retire the frozen
    // instance. Until then its values are readable but it cannot evaluate.
    Result finalize(GeneratorInstanceId id) noexcept;
    Result stop(GeneratorInstanceId id) noexcept;
    Result reset() noexcept;
    // Force is distinct from Stop/Destroy. Owner-thread, job boundary only;
    // Busy never frees running work. No lifecycle callback or Stop event runs.
    // Purges the target's queued lifecycle/signal events, including unpublished
    // natural Destroy, and releases deferred signals with its instance storage.
    // Already retired issued IDs succeed; unknown/zero IDs are rejected.
    Result forceTerminate(GeneratorInstanceId id) noexcept;
    // Also discards queued spawn commands. Other pools are not affected.
    Result forceTerminateAll() noexcept;
    Result pendingForceRequest(GeneratorInstanceId id, std::uint8_t& scope) const noexcept;
    // Apply host-selected Live lanes to one instance before evaluate. CaptureAtSpawn
    // lanes must not be passed here; their values stay fixed from spawn.
    Result setLive(GeneratorInstanceId id, const GeneratorCapture* values,
                   std::size_t count) noexcept;
    // Apply CaptureAtSpawn values to a materialized instance before start.
    Result setCaptured(GeneratorInstanceId id, const GeneratorCapture* values,
                       std::size_t count) noexcept;
    // Configure the template's local signal outputs before spawning any instances.
    Result setSignalOutputs(const std::uint8_t* nodeIds, std::size_t count) noexcept;
    // Broadcast a parent signal to every started, non-expiring instance.
    Result dispatchSignal(const std::uint8_t nodeId[16], double time,
                         std::size_t instructionBudget) noexcept;
    // Targeted notification: every call is one arrival; never sets lifetime or
    // invokes ownerDestroy. time must equal the last evaluated effect time.
    Result dispatchSignal(GeneratorInstanceId id, const std::uint8_t nodeId[16],
                          double time, std::size_t instructionBudget) noexcept;
    Result dispatchSignalOwned(const std::uint8_t nodeId[16], double time,
                               std::size_t instructionBudget,
                               std::uint64_t ownerTag) noexcept;
    Result setTimeContext(GeneratorInstanceId id, double effectTime, double frameDelta) noexcept;
    Result setTimeContext(GeneratorInstanceId id, double effectTime, double frameDelta,
                          double realEffectTime, double realInstanceAge, double realFrameDelta) noexcept;
    Result withInstance(GeneratorInstanceId id, GeneratorInstanceAccess access,
                        void *context) noexcept;
    Result setHostQuality(GeneratorInstanceId id, const HostQualitySnapshot& snapshot) noexcept;
    Result setDebugHooks(GeneratorInstanceId id, const ExecutionDebugHooks& hooks) noexcept;
    // Deferred-signal lifecycles require monotonic evaluation without skipping
    // their next due time. Other lifecycles retain random-access evaluation.
    Result evaluate(GeneratorInstanceId id, double time) noexcept;
    // Due time is in the effect clock (instance start + local deadline).
    Result nextDeferredSignal(GeneratorInstanceId id, double& time, bool& hasSignal) noexcept;
    Result dispatchDeferredSignal(GeneratorInstanceId id, double time, std::size_t instructionBudget) noexcept;
    // Actual evaluated local age, including work draining past Life Time.
    Result elapsed(GeneratorInstanceId id, double& out) noexcept;
    // Send the owner's natural end request at the last evaluated global time.
    // Each instance receives it at most once, even if a callback fails.
    Result ownerDestroy(GeneratorInstanceId id, const std::uint8_t nodeId[16],
                        double time, std::size_t instructionBudget) noexcept;
    // Last successful update, including the frozen expiry boundary.
    Result lifetime(GeneratorInstanceId id, LifetimeSample& out) noexcept;
    Result value(GeneratorInstanceId id, const std::uint8_t nodeId[16],
                 double out[4], std::uint8_t& width) noexcept;
    bool popEvent(GeneratorEvent& out) noexcept;
    bool popSignalEvent(GeneratorSignalEvent& out) noexcept;
    std::size_t pendingEventCount() const noexcept;
    std::size_t pendingSignalEventCount() const noexcept;
    std::size_t activeCount() const noexcept;

private:
    struct Impl;
    explicit GeneratorPool(std::unique_ptr<Impl> impl) noexcept;
    std::unique_ptr<Impl> impl_;
};

}
