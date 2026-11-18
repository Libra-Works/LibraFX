#pragma once

#include "liberafx/LbEffectLifecycle.h"
#include "liberafx/LbSpawnableHost.h"
#include <atomic>
#include <mutex>
#include <optional>

namespace liberafx {

// Host-supplied, fresh connection/process nonce, never an address. The host must
// authenticate development connections and must not reuse a nonce after reconnect.
struct SessionId final {
    std::uint64_t high = 0, low = 0;
};
struct InstanceId final {
    SessionId session{};
    std::uint64_t incarnation = 0, generation = 0;
    std::uint64_t slot = 0;
};
struct RequestId final {
    SessionId session{};
    std::uint64_t incarnation = 0, sequence = 0;
};
enum class ReplacementPolicy : std::uint8_t { PreserveState, Restart };
enum class InstanceRequestKind : std::uint8_t { Create, NaturalEnd, Destroy, Replace };
struct SessionStagingMemory final {
    const void* work = nullptr;
    std::size_t workBytes = 0;
    const void* defaults = nullptr;
    std::size_t defaultsBytes = 0;
};

// Local memory, NOT a transport identifier. The caller owns this object and all
// backing resources. After an accepted request, do not access/reuse/destroy it
// until returned by a consumed completion. No allocation is done by the session.
class LIBRA_RUNTIME_CORE_API SessionEffect {
public:
    virtual ~SessionEffect() = default;
    SessionEffect() = default;
    SessionEffect(const SessionEffect&) = delete;
    SessionEffect& operator=(const SessionEffect&) = delete;
protected:
    // Called only by the designated owner at a job boundary. No reentry into
    // owner operations; workers may enqueue. No SDK calls on a worker.
    virtual Result start() noexcept = 0;
    virtual Result advance(double time, double frameDelta) noexcept = 0;
    virtual Result naturalEnd() noexcept = 0;
    virtual Result forceStop() noexcept = 0;
    virtual Result read(const std::uint8_t nodeId[16], double out[4],
                        std::uint8_t& width) const noexcept = 0;
    virtual bool finished() const noexcept = 0;
    // Both sides must support side-effect-free staging and release. Default
    // denies replacement, including children, queued signals and host objects.
    virtual bool restartable() const noexcept { return false; }
    virtual SessionStagingMemory stagingMemory() const noexcept { return {}; }
    virtual void release() noexcept = 0;
private:
    friend class InstanceSession;
    std::atomic<bool> claimed_{false};
};

// A real native Instance + existing EffectLifecycle, using caller-owned buffers.
// Program/bytecode/descriptors/work/defaults must outlive the return completion.
// Only pure v1/v2 programs are supported: no signal/event/child routing is
// silently omitted. Use SpawnableSessionEffect for packages.
class LIBRA_RUNTIME_CORE_API LifecycleSessionEffect final : public SessionEffect {
public:
    LifecycleSessionEffect(const Program& program, double* work, std::size_t workCount,
                           double* defaults, std::size_t defaultsCount,
                           Lifetime lifetime = Lifetime::infinite()) noexcept;
protected:
    Result start() noexcept override;
    Result advance(double time, double frameDelta) noexcept override;
    Result naturalEnd() noexcept override;
    Result forceStop() noexcept override;
    Result read(const std::uint8_t nodeId[16], double out[4],
                std::uint8_t& width) const noexcept override;
    bool finished() const noexcept override { return finished_; }
    bool restartable() const noexcept override;
    SessionStagingMemory stagingMemory() const noexcept override;
    void release() noexcept override;
private:
    const Program* program_;
    double* work_;
    std::size_t workCount_;
    double* defaults_;
    std::size_t defaultsCount_;
    Lifetime lifetime_;
    std::optional<Instance> instance_;
    std::optional<EffectLifecycle> lifecycle_;
    bool draining_ = false, finished_ = false;
};

// Adopts an already prepared, not-yet-advanced existing native package host.
// Host construction/allocation remains an owner-thread host responsibility.
// Advance preserves SpawnableHost's child/Destroy/continuation draining rules.
// Live state migration/restart is deliberately unsupported for this resource.
class LIBRA_RUNTIME_CORE_API SpawnableSessionEffect final : public SessionEffect {
public:
    explicit SpawnableSessionEffect(SpawnableHost& host) noexcept : host_(&host) {}
protected:
    Result start() noexcept override { return host_->advance(0, 0); }
    Result advance(double time, double frameDelta) noexcept override { return host_->advance(time, frameDelta); }
    Result naturalEnd() noexcept override { return host_->requestNaturalEnd(); }
    Result forceStop() noexcept override { return host_->stop(); }
    Result read(const std::uint8_t nodeId[16], double out[4],
                std::uint8_t& width) const noexcept override { return host_->rootValue(nodeId, out, width); }
    bool finished() const noexcept override { return host_->rootFinished(); }
    void release() noexcept override {} // Caller destroys host only after return.
private:
    SpawnableHost* host_;
};

struct InstanceRequest final {
    InstanceRequestKind kind = InstanceRequestKind::Create;
    InstanceId target{};
    SessionEffect* effect = nullptr; // Only Create/Replace; borrowed until returned.
    ReplacementPolicy policy = ReplacementPolicy::PreserveState;
};
struct InstanceCompletion final {
    RequestId request{};
    InstanceRequestKind kind{};
    Result result = Result::NotReady;
    InstanceId instance{};
    // Local release receipt, never serialize this pointer. Null for successful
    // Create/NaturalEnd. Returned resources are safe to free after takeCompletion.
    SessionEffect* returnedEffect = nullptr;
};
struct InstanceSlot final {
    SessionEffect* effect = nullptr;
    std::uint64_t generation = 1;
    bool exhausted = false;
};
struct InstanceRequestStorage final {
    enum class State : std::uint8_t { Empty, Pending, Processing, Complete };
    State state = State::Empty;
    InstanceRequest request{};
    InstanceCompletion completion{};
};

// Multi-producer bounded requests; one designated owner calls process/advance/
// read/close (normally game thread). Owner operations use an atomic gate and return
// Busy on overlap/reentry. Evaluation completes synchronously before unlock;
// no native job or engine resource is freed during evaluation. Workers ONLY
// submit/takeCompletion. No method is an engine-thread dispatcher.
class LIBRA_RUNTIME_CORE_API InstanceSession final {
public:
    using OwnerOperation = Result (*)(void* context, SessionEffect& effect) noexcept;
    InstanceSession(SessionId id, InstanceSlot* slots, std::size_t slotCount,
                    InstanceRequestStorage* requests, std::size_t requestCapacity) noexcept;
    ~InstanceSession();
    InstanceSession(const InstanceSession&) = delete;
    InstanceSession& operator=(const InstanceSession&) = delete;
    Result submit(const InstanceRequest& request, RequestId& out) noexcept;
    Result takeCompletion(RequestId request, InstanceCompletion& out) noexcept;
    // Per-session enqueue order, not producer invocation order. Each accepted
    // request reserves its completion cell until consumed (backpressure).
    Result process(std::size_t limit, std::size_t& processed) noexcept;
    Result advance(InstanceId id, double time, double frameDelta, bool& finished) noexcept;
    Result read(InstanceId id, const std::uint8_t nodeId[16], double out[4],
                std::uint8_t& width) noexcept;
    // Adapter escape hatch at the SAME owner/job boundary: drain package events,
    // read children or set game IO on the caller's known borrowed host here.
    // The effect reference is borrowed only during this callback, not an ID.
    // Callback may submit requests, but must not take ownership or free resources.
    Result access(InstanceId id, OwnerOperation operation, void* context) noexcept;
    // Stops accepting new requests even if NotReady. Continues servicing accepted
    // work. Caller must explicitly destroy slots and consume ALL completions;
    // no natural Lifetime is ever converted to force-stop by shutdown.
    Result close() noexcept;
    // Identity only; not a pointer and not an authorization credential.
    std::uint64_t incarnation() const noexcept { return incarnation_; }
private:
    bool matches(InstanceId id) const noexcept;
    bool matches(RequestId id) const noexcept;
    InstanceSlot* find(InstanceId id) noexcept;
    InstanceId idFor(std::size_t slot) const noexcept;
    InstanceCompletion execute(const InstanceRequest& request) noexcept;
    void retire(InstanceSlot& slot) noexcept;
    SessionId id_;
    std::uint64_t incarnation_ = 0, nextSequence_ = 1;
    InstanceSlot* slots_;
    std::size_t slotCount_;
    InstanceRequestStorage* requests_;
    std::size_t requestCapacity_;
    std::mutex queueMutex_;
    std::atomic_flag ownerBusy_ = ATOMIC_FLAG_INIT;
    bool accepting_ = true, closed_ = false, valid_ = false;
};

}
