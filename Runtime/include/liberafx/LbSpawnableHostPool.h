#pragma once
#include "liberafx/LbSpawnableHost.h"

namespace liberafx {
struct SpawnableHostLease final {
    std::uint32_t slot = UINT32_MAX;
    std::uint64_t generation = 0;
};
struct SpawnableHostPoolOptions final {
    std::size_t capacity = 8;
    // Bounded workers used only during preparation, including the caller.
    std::size_t preparationWorkers = 4;
    SpawnableHostOptions host;
};
struct SpawnableHostPoolStats final {
    std::size_t capacity = 0, active = 0, available = 0, preparationWorkers = 0;
};
// Prepares complete Core instances and their memory before gameplay. Acquire
// never grows the pool. Release resets state while retaining programs, buffers
// and queues. Host pointers are valid only for the current lease. Owner-thread
// operations; preparation hands quiescent worker-built instances to that owner.
// Initial support: root-only packages with Core builtins and no procedural mesh
// emitter. Templates, project callbacks and engine resources use ordinary hosts.
class LIBRA_RUNTIME_CORE_API SpawnableHostPool final {
public:
    static Result create(const SpawnablePackage& package, const SpawnableHostPoolOptions& options,
        std::unique_ptr<SpawnableHostPool>& out) noexcept;
    ~SpawnableHostPool();
    SpawnableHostPool(const SpawnableHostPool&) = delete;
    SpawnableHostPool& operator=(const SpawnableHostPool&) = delete;
    Result acquire(SpawnableHostLease& lease, SpawnableHost*& host) noexcept;
    Result lookup(const SpawnableHostLease& lease, SpawnableHost*& host) const noexcept;
    // Discards this playback's events and inputs; does not emit lifecycle signals.
    // Busy leaves the lease checked out. Failed resets quarantine the slot.
    Result release(const SpawnableHostLease& lease) noexcept;
    SpawnableHostPoolStats stats() const noexcept;
private:
    struct Impl;
    explicit SpawnableHostPool(std::unique_ptr<Impl> impl) noexcept;
    std::unique_ptr<Impl> impl_;
};
}
