#pragma once

#include "liberafx/LbNamedIo.h"
#include "liberafx/LbGeneratorPool.h"
#include "liberafx/LbSpawnablePackage.h"
#include "liberafx/LbCpuParticles.h"
#include "liberafx/LbCoordinateBasis.h"
#include <cstddef>
#include <cstdint>
#include <memory>

namespace liberafx {

enum class SpawnableHostEventKind : std::uint8_t {
    Spawn = 1, Stop = 2, Destroy = 3, Signal = 4, ForceTerminated = 5
};

enum class SpawnableScopeKind : std::uint8_t { Effect, Instance };
struct SpawnableScope final {
    SpawnableScopeKind kind = SpawnableScopeKind::Effect;
    std::uint8_t containerId[16]{};
    GeneratorInstanceId instanceId = 0;
};
struct SpawnableDebugTarget final {
    std::uint64_t hostGeneration = 0;
    SpawnableScope scope{};
};

struct SpawnableHostEvent final {
    SpawnableHostEventKind kind{};
    std::uint8_t containerId[16]{};
    GeneratorInstanceId instanceId = 0;
    double time = 0;
    std::uint64_t seed = 0;
    std::uint8_t signalNodeId[16]{};
};

struct SpawnableHostOptions final {
    std::size_t maxActivePerTemplate = 32;
    std::size_t memoryBudgetBytesPerTemplate = 4 * 1024 * 1024;
    std::size_t eventCapacity = 128;
    std::size_t instructionBudget = 10000;
};
struct EffectTimeSnapshot final {
    double realTime = 0, effectTime = 0, realDelta = 0, effectDelta = 0, timeScale = 1;
};

// Owner-thread lifecycle and routing host for one engine-neutral package.
// Rendering and engine object operations remain the adapter's responsibility.
// All calls are owner-thread only, never concurrent. Evaluation jobs are inline;
// reentrant lifecycle calls return Busy without freeing work. Submit force before
// advance/notifications for same-tick priority. Already executed side effects
// cannot be undone. Force discards queued target events before publishing its
// own cleanup notice; the adapter must invalidate matching render snapshots.
class LIBRA_RUNTIME_CORE_API SpawnableHost final {
public:
    static Result create(SpawnablePackage package, const SpawnableHostOptions& options,
                         std::unique_ptr<SpawnableHost>& out);
    // Descriptors are copied. Callback code and contexts must outlive the host.
    static Result create(SpawnablePackage package, const SpawnableHostOptions& options,
                         const CustomNodeDescriptor* registry, std::size_t registryCount,
                         std::unique_ptr<SpawnableHost>& out);
    ~SpawnableHost();
    SpawnableHost(const SpawnableHost&) = delete;
    SpawnableHost& operator=(const SpawnableHost&) = delete;

    Result advance(double time, double frameDelta);
    // advance receives monotonic unscaled elapsed time and unscaled frame delta.
    // Changing the ratio affects only future intervals, never elapsed history.
    Result setTimeScale(double ratio);
    EffectTimeSnapshot timeSnapshot() const noexcept;
    Result dispatchSignal(const std::uint8_t nodeId[16]);
    Result fireGameSignal(const char* name);
    Result setGameProperty(const char* name, const double* values, std::size_t width);
    Result setInstanceProperty(const SpawnableDebugTarget& target, const char* name,
                               const double* values, std::size_t width);
    Result instanceProperty(const SpawnableDebugTarget& target, const char* name,
                            double values[16], std::uint8_t& width);
    Result fireInstanceSignal(const SpawnableDebugTarget& target, const char* name);
    Result instancePortName(const std::uint8_t containerId[16], std::size_t index,
                            const char*& name) const noexcept;
    Result setOwnerMatrix(const double* values, std::size_t width);
    Result setCoordinateBasis(const CoordinateBasis& basis);
    CoordinateBasis coordinateBasis() const noexcept;
    Result setGameSpatialProperty(const char* name, SpatialValueKind kind, const double* values, std::size_t width);
    Result setHostQuality(const HostQualitySnapshot& snapshot);
    Result requestNaturalEnd();
    Result stop();
    // Notification only: nodeId is an explicit user-authored signal entry.
    // Every call is a separate arrival; no fixed immediate/boundary End policy,
    // rootEnding, lifetime change or automatic Destroy is attached to it.
    Result notifyEndRequest(const SpawnableScope& scope, const std::uint8_t nodeId[16]);
    // Effect covers root, all child pools, pending spawns and continuations.
    // Instance covers (containerId,instanceId) and its dynamically owned
    // descendant subtree, including deferred signals, CPU worlds and queued
    // spawns. Sibling instances and Destroy-route targets remain unaffected.
    // Per-descendant cleanup notices are retained beyond eventCapacity.
    // Issued, retired targets and duplicate requests succeed without replay.
    Result forceTerminate(const SpawnableScope& scope);
    // Only at an owner/job boundary. Scope Effect means root only here.
    Result debugTarget(const SpawnableScope& scope, SpawnableDebugTarget& out) noexcept;
    Result setDebugHooks(const SpawnableDebugTarget& target, const ExecutionDebugHooks& hooks) noexcept;
    std::uint64_t debugGeneration() const noexcept;

    Result rootValue(const std::uint8_t nodeId[16], double out[4],
                     std::uint8_t& width) const;
    Result childValue(const std::uint8_t containerId[16], GeneratorInstanceId instanceId,
                      const std::uint8_t nodeId[16], double out[4], std::uint8_t& width) const;
    bool popEvent(SpawnableHostEvent& out) noexcept;
    bool popGameIoEvent(NamedIoEvent& out) noexcept;
    std::size_t activeCount() const noexcept;
    bool rootFinished() const noexcept;
    bool hasCpuParticleEvents() const noexcept;
    const SpawnablePackage& package() const noexcept;
    Result cpuParticleCount(const std::uint8_t systemId[16], std::size_t& out) const noexcept;
    Result cpuParticleAt(const std::uint8_t systemId[16], std::size_t index, CpuParticleSnapshot& out) const noexcept;
    bool popCpuParticleEvent(CpuParticleEvent& out) noexcept;
    Result cpuParticleCount(const SpawnableScope& scope,
                            const std::uint8_t systemId[16],
                            std::size_t& out) const noexcept;
    Result cpuParticleAt(const SpawnableScope& scope,
                         const std::uint8_t systemId[16], std::size_t index,
                         CpuParticleSnapshot& out) const noexcept;
    bool popCpuParticleEvent(const SpawnableScope& scope,
                             CpuParticleEvent& out) noexcept;

private:
    struct Impl;
    explicit SpawnableHost(std::unique_ptr<Impl> impl) noexcept;
    std::unique_ptr<Impl> impl_;
};

}
