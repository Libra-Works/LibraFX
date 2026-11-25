#include "liberafx/LbInstanceSession.h"
#include <cassert>
#include <cmath>

namespace liberafx {
namespace {
auto nextIncarnation = std::atomic<std::uint64_t>{1};
auto allocateIncarnation() noexcept -> std::uint64_t
{
    auto next = nextIncarnation.load(std::memory_order_relaxed);
    while (next && !nextIncarnation.compare_exchange_weak(next,
        next == UINT64_MAX ? 0 : next + 1, std::memory_order_relaxed)) {}
    return next;
}
auto sameSession(SessionId left, SessionId right) noexcept -> bool
{
    return left.high == right.high && left.low == right.low;
}
struct OwnerGuard final {
    std::atomic_flag& flag;
    bool acquired;
    explicit OwnerGuard(std::atomic_flag& value) noexcept
        : flag(value), acquired(!flag.test_and_set(std::memory_order_acquire)) {}
    ~OwnerGuard() { if (acquired) flag.clear(std::memory_order_release); }
};
auto overlaps(const void* left, std::size_t leftBytes,
              const void* right, std::size_t rightBytes) noexcept -> bool
{
    if (!leftBytes || !rightBytes) return false;
    auto a = reinterpret_cast<std::uintptr_t>(left);
    auto b = reinterpret_cast<std::uintptr_t>(right);
    // Difference comparison cannot overflow, including malformed ranges.
    return a <= b ? b - a < leftBytes : a - b < rightBytes;
}
auto disjoint(SessionStagingMemory left, SessionStagingMemory right) noexcept -> bool
{
    return !overlaps(left.work, left.workBytes, right.work, right.workBytes) &&
        !overlaps(left.work, left.workBytes, right.defaults, right.defaultsBytes) &&
        !overlaps(left.defaults, left.defaultsBytes, right.work, right.workBytes) &&
        !overlaps(left.defaults, left.defaultsBytes, right.defaults, right.defaultsBytes);
}
}

LifecycleSessionEffect::LifecycleSessionEffect(const Program& program, double* work,
    std::size_t workCount, double* defaults, std::size_t defaultsCount, Lifetime lifetime) noexcept
    : program_(&program), work_(work), workCount_(workCount), defaults_(defaults),
      defaultsCount_(defaultsCount), lifetime_(lifetime) {}

auto LifecycleSessionEffect::start() noexcept -> Result
{
    if (instance_) return Result::NotReady;
    if (!restartable()) return Result::UnsupportedBinary;
    if (workCount_ > SIZE_MAX / sizeof(double) || defaultsCount_ > SIZE_MAX / sizeof(double) ||
        overlaps(work_, workCount_ * sizeof(double), defaults_, defaultsCount_ * sizeof(double)))
        return Result::InvalidArgument;
    instance_.emplace();
    auto result = instance_->initialize(*program_, work_, workCount_, defaults_, defaultsCount_);
    if (result != Result::Ok) return result;
    lifecycle_.emplace(*instance_);
    auto sample = LifetimeSample{};
    result = lifecycle_->start(lifetime_, nullptr, 10000, sample);
    if (result == Result::Ok) {
        draining_ = sample.expired;
        finished_ = false;
    }
    return result;
}
auto LifecycleSessionEffect::advance(double time, double frameDelta) noexcept -> Result
{
    if (!lifecycle_ || finished_) return Result::NotReady;
    auto result = instance_->setTimeContext(time, frameDelta);
    if (result != Result::Ok) return result;
    if (!draining_) {
        auto sample = LifetimeSample{};
        result = lifecycle_->advance(time, sample);
        if (result != Result::Ok) return result;
        draining_ = sample.expired;
    } else {
        result = lifecycle_->drain(time);
        if (result != Result::Ok) return result;
    }
    if (draining_) {
        // Deferred signals must be reached/dispatched at their exact timestamp.
        // This simple root facade cannot route children or host continuations.
        auto deferred = double{};
        auto pending = false;
        result = lifecycle_->nextDeferredSignal(deferred, pending);
        if (result != Result::Ok) return result;
        if (pending) return Result::NotReady;
        result = lifecycle_->finish();
        if (result != Result::Ok) return result;
        finished_ = true;
    }
    return Result::Ok;
}
auto LifecycleSessionEffect::naturalEnd() noexcept -> Result
{
    if (!lifecycle_) return Result::NotReady;
    if (finished_) return Result::Ok;
    auto result = lifecycle_->requestNaturalEnd();
    if (result == Result::Ok) draining_ = true;
    return result;
}
auto LifecycleSessionEffect::forceStop() noexcept -> Result
{
    return !lifecycle_ ? Result::NotReady : finished_ ? Result::Ok : lifecycle_->stop();
}
auto LifecycleSessionEffect::read(const std::uint8_t nodeId[16], double out[4],
                                  std::uint8_t& width) const noexcept -> Result
{
    return instance_ ? instance_->valueComponents(nodeId, out, width) : Result::NotReady;
}
auto LifecycleSessionEffect::restartable() const noexcept -> bool
{
    return program_->isPureRestartable();
}
auto LifecycleSessionEffect::stagingMemory() const noexcept -> SessionStagingMemory
{
    return {work_, workCount_ > SIZE_MAX / sizeof(double) ? SIZE_MAX : workCount_ * sizeof(double),
        defaults_, defaultsCount_ > SIZE_MAX / sizeof(double) ? SIZE_MAX : defaultsCount_ * sizeof(double)};
}
void LifecycleSessionEffect::release() noexcept
{
    lifecycle_.reset(); // Clears deferred storage before Instance is destroyed.
    instance_.reset();  // Custom cleanup precedes return of caller buffers.
    draining_ = finished_ = false;
}

InstanceSession::InstanceSession(SessionId id, InstanceSlot* slots, std::size_t slotCount,
    InstanceRequestStorage* requests, std::size_t requestCapacity) noexcept
    : id_(id), incarnation_(allocateIncarnation()), slots_(slots), slotCount_(slotCount),
      requests_(requests), requestCapacity_(requestCapacity)
{
    valid_ = (id.high || id.low) && incarnation_ && slots && slotCount && requests && requestCapacity;
    if (!valid_) return;
    for (auto index = std::size_t{}; index < slotCount_; ++index) slots_[index] = {};
    for (auto index = std::size_t{}; index < requestCapacity_; ++index) requests_[index] = {};
}
InstanceSession::~InstanceSession()
{
    // Deliberately no implicit stop, callbacks or resource reclamation.
    assert(!valid_ || closed_);
}
auto InstanceSession::matches(InstanceId id) const noexcept -> bool
{
    return sameSession(id.session, id_) && id.incarnation == incarnation_ && id.generation;
}
auto InstanceSession::matches(RequestId id) const noexcept -> bool
{
    return sameSession(id.session, id_) && id.incarnation == incarnation_ && id.sequence;
}
auto InstanceSession::find(InstanceId id) noexcept -> InstanceSlot*
{
    if (!matches(id) || id.slot >= slotCount_) return nullptr;
    auto& slot = slots_[id.slot];
    return slot.effect && slot.generation == id.generation ? &slot : nullptr;
}
auto InstanceSession::idFor(std::size_t slot) const noexcept -> InstanceId
{
    return {id_, incarnation_, slots_[slot].generation, slot};
}
auto InstanceSession::submit(const InstanceRequest& request, RequestId& out) noexcept -> Result
{
    out = {};
    auto lock = std::lock_guard<std::mutex>(queueMutex_);
    if (!valid_) return Result::InvalidArgument;
    // Destruction must remain possible while closing.
    if (closed_ || (!accepting_ && request.kind != InstanceRequestKind::Destroy)) return Result::NotReady;
    auto needsEffect = request.kind == InstanceRequestKind::Create || request.kind == InstanceRequestKind::Replace;
    if (request.kind != InstanceRequestKind::Create && request.kind != InstanceRequestKind::Replace &&
        request.kind != InstanceRequestKind::NaturalEnd && request.kind != InstanceRequestKind::Destroy)
        return Result::InvalidArgument;
    if (needsEffect != (request.effect != nullptr)) return Result::InvalidArgument;
    if (request.kind != InstanceRequestKind::Create && !matches(request.target)) return Result::InvalidHandle;
    if (request.kind == InstanceRequestKind::Replace &&
        request.policy != ReplacementPolicy::Restart && request.policy != ReplacementPolicy::PreserveState)
        return Result::InvalidArgument;
    if (!nextSequence_) return Result::CounterOverflow;
    auto* entry = static_cast<InstanceRequestStorage*>(nullptr);
    for (auto index = std::size_t{}; index < requestCapacity_; ++index)
        if (requests_[index].state == InstanceRequestStorage::State::Empty) { entry = &requests_[index]; break; }
    if (!entry) return Result::QueueRejected;
    if (needsEffect) {
        auto expected = false;
        if (!request.effect->claimed_.compare_exchange_strong(expected, true)) return Result::Busy;
    }
    auto sequence = nextSequence_++;
    entry->request = request;
    entry->completion = {};
    entry->completion.request = {id_, incarnation_, sequence};
    entry->state = InstanceRequestStorage::State::Pending;
    out = entry->completion.request;
    return Result::Ok;
}
auto InstanceSession::takeCompletion(RequestId request, InstanceCompletion& out) noexcept -> Result
{
    auto lock = std::lock_guard<std::mutex>(queueMutex_);
    if (!valid_ || !matches(request)) return Result::InvalidHandle;
    for (auto index = std::size_t{}; index < requestCapacity_; ++index) {
        auto& entry = requests_[index];
        if (entry.state == InstanceRequestStorage::State::Empty ||
            entry.completion.request.sequence != request.sequence) continue;
        if (entry.state != InstanceRequestStorage::State::Complete) return Result::NotReady;
        out = entry.completion;
        if (out.returnedEffect) out.returnedEffect->claimed_.store(false);
        entry = {};
        return Result::Ok;
    }
    return Result::InvalidHandle; // Already consumed or never issued.
}
void InstanceSession::retire(InstanceSlot& slot) noexcept
{
    slot.effect = nullptr;
    if (slot.generation == UINT64_MAX) slot.exhausted = true;
    else ++slot.generation;
}
auto InstanceSession::execute(const InstanceRequest& request) noexcept -> InstanceCompletion
{
    auto completion = InstanceCompletion{};
    completion.kind = request.kind;
    completion.instance = request.target;
    completion.result = Result::InvalidHandle;
    if (request.kind == InstanceRequestKind::Create) {
        completion.returnedEffect = request.effect;
        completion.result = Result::InsufficientWork;
        for (auto index = std::size_t{}; index < slotCount_; ++index) {
            if (slots_[index].effect && !disjoint(slots_[index].effect->stagingMemory(),
                                                 request.effect->stagingMemory())) {
                completion.result = Result::InvalidArgument;
                return completion;
            }
        }
        for (auto index = std::size_t{}; index < slotCount_; ++index) {
            auto& slot = slots_[index];
            if (slot.effect || slot.exhausted) continue;
            completion.result = request.effect->start();
            if (completion.result != Result::Ok) { request.effect->release(); return completion; }
            slot.effect = request.effect;
            completion.instance = idFor(index);
            completion.returnedEffect = nullptr;
            return completion;
        }
        return completion;
    }
    auto* slot = find(request.target);
    if (request.kind == InstanceRequestKind::Replace) completion.returnedEffect = request.effect;
    if (!slot) return completion;
    switch (request.kind) {
    case InstanceRequestKind::NaturalEnd:
        completion.result = slot->effect->naturalEnd();
        break;
    case InstanceRequestKind::Destroy:
        // Explicit forced destroy, never invoked on natural expiry.
        completion.result = slot->effect->forceStop();
        if (completion.result == Result::Ok) {
            completion.returnedEffect = slot->effect;
            slot->effect->release();
            retire(*slot);
        }
        break;
    case InstanceRequestKind::Replace:
        completion.result = Result::UnsupportedBinary;
        if (request.policy != ReplacementPolicy::Restart ||
            !slot->effect->restartable() || !request.effect->restartable()) break;
        for (auto index = std::size_t{}; index < slotCount_; ++index) {
            if (slots_[index].effect && !disjoint(slots_[index].effect->stagingMemory(),
                                                 request.effect->stagingMemory())) {
                completion.result = Result::InvalidArgument;
                return completion;
            }
        }
        // Initialize/evaluate only a disjoint, side-effect-free candidate.
        // No operation on the previous effect can fail after commit.
        completion.result = request.effect->start();
        if (completion.result != Result::Ok) { request.effect->release(); break; }
        completion.returnedEffect = slot->effect;
        slot->effect = request.effect;
        completion.returnedEffect->release();
        break;
    default: completion.result = Result::InvalidArgument; break;
    }
    return completion;
}
auto InstanceSession::process(std::size_t limit, std::size_t& processed) noexcept -> Result
{
    processed = 0;
    auto owner = OwnerGuard(ownerBusy_);
    if (!owner.acquired) return Result::Busy;
    if (!valid_) return Result::InvalidArgument;
    while (processed < limit) {
        auto* entry = static_cast<InstanceRequestStorage*>(nullptr);
        {
            auto lock = std::lock_guard<std::mutex>(queueMutex_);
            for (auto index = std::size_t{}; index < requestCapacity_; ++index) {
                auto& candidate = requests_[index];
                if (candidate.state == InstanceRequestStorage::State::Pending &&
                    (!entry || candidate.completion.request.sequence < entry->completion.request.sequence))
                    entry = &candidate;
            }
            if (!entry) break;
            entry->state = InstanceRequestStorage::State::Processing;
        }
        auto completion = execute(entry->request);
        {
            auto lock = std::lock_guard<std::mutex>(queueMutex_);
            completion.request = entry->completion.request;
            entry->completion = completion;
            entry->state = InstanceRequestStorage::State::Complete;
        }
        ++processed;
    }
    return Result::Ok;
}
auto InstanceSession::advance(InstanceId id, double time, double frameDelta, bool& finished) noexcept -> Result
{
    auto owner = OwnerGuard(ownerBusy_);
    if (!owner.acquired) return Result::Busy;
    if (!valid_) return Result::InvalidArgument;
    auto* slot = find(id);
    if (!slot) return Result::InvalidHandle;
    if (!std::isfinite(time) || time < 0 || !std::isfinite(frameDelta) || frameDelta < 0)
        return Result::InvalidArgument;
    auto result = slot->effect->advance(time, frameDelta);
    if (result == Result::Ok) finished = slot->effect->finished();
    return result;
}
auto InstanceSession::read(InstanceId id, const std::uint8_t nodeId[16], double out[4],
                           std::uint8_t& width) noexcept -> Result
{
    auto owner = OwnerGuard(ownerBusy_);
    if (!owner.acquired) return Result::Busy;
    if (!valid_ || !nodeId || !out) return Result::InvalidArgument;
    auto* slot = find(id);
    return slot ? slot->effect->read(nodeId, out, width) : Result::InvalidHandle;
}
auto InstanceSession::close() noexcept -> Result
{
    auto owner = OwnerGuard(ownerBusy_);
    if (!owner.acquired) return Result::Busy;
    auto lock = std::lock_guard<std::mutex>(queueMutex_);
    if (!valid_) return Result::InvalidArgument;
    accepting_ = false;
    for (auto index = std::size_t{}; index < slotCount_; ++index)
        if (slots_[index].effect) return Result::NotReady;
    for (auto index = std::size_t{}; index < requestCapacity_; ++index)
        if (requests_[index].state != InstanceRequestStorage::State::Empty) return Result::NotReady;
    closed_ = true;
    return Result::Ok;
}
auto InstanceSession::access(InstanceId id, OwnerOperation operation, void* context) noexcept -> Result
{
    auto owner = OwnerGuard(ownerBusy_);
    if (!owner.acquired) return Result::Busy;
    if (!valid_ || !operation) return Result::InvalidArgument;
    auto* slot = find(id);
    return slot ? operation(context, *slot->effect) : Result::InvalidHandle;
}
}
