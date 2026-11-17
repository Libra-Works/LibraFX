#include "liberafx/GeneratorPool.h"

#include <cmath>
#include <array>
#include <cstring>
#include <limits>
#include <mutex>
#include <new>
#include <optional>
#include <vector>

namespace liberafx {
namespace {
auto inlineEnqueue(void*, Job job, void* context) noexcept -> bool
{
    job(context);
    return true;
}

struct AlignedBuffer final {
    void* data = nullptr;
    std::size_t alignment = 16;
    ~AlignedBuffer() { if (data) ::operator delete(data, std::align_val_t{alignment}); }
    void allocate(std::size_t bytes, std::size_t requested)
    {
        alignment = requested < 16 ? 16 : requested;
        if (bytes) data = ::operator new(bytes, std::align_val_t{alignment});
    }
};

struct Slot final {
    AlignedBuffer work;
    AlignedBuffer defaults;
    std::array<DeferredSignal, 128> deferred{};
    // Must be destroyed before its storage.
    std::optional<Instance> instance;
    GeneratorInstanceId id = 0;
    double start = 0;
    Lifetime lifetime = Lifetime::infinite();
    std::uint64_t seed = 0;
    bool started = false;
    bool expiring = false;
    bool expired = false;
    bool destroyPublished = false;
    bool destroyDelivered = false;
    bool sampleReady = false;
    bool ownerDestroySent = false;
    double sampledAge = 0;
    double signalTime = 0;
    double signalEffectTime = 0;
    void* observerOwner = nullptr;
    Result startResult = Result::Ok;
    Result ownerDestroyResult = Result::Ok;
};
struct SpawnCommand final {
    double time = 0;
    std::size_t count = 0;
};
}

struct GeneratorPool::Impl final {
    const Program& program;
    std::uint8_t containerId[16];
    std::size_t maxActive, budget, committed, workBytes, defaultBytes, slotCost, maxCaptureLanes;
    std::vector<std::unique_ptr<Slot>> slots;
    std::vector<GeneratorEvent> events;
    std::vector<GeneratorSignalEvent> signalEvents;
    std::vector<std::array<std::uint8_t, 16>> signalOutputs;
    std::vector<SpawnCommand> commands;
    std::vector<GeneratorCapture> commandCaptures;
    std::size_t eventHead = 0, eventCount = 0, signalEventHead = 0, signalEventCount = 0;
    std::size_t signalOutputCount = 0, active = 0;
    std::size_t commandHead = 0, commandCount = 0;
    GeneratorInstanceId nextId = 1;
    GeneratorLifecycle lifecycle{};
    bool lifecycleConfigured = false;
    bool executing = false;
    bool rejectEnqueues = false;
    mutable std::mutex mutex;

    Impl(const Program& p, const std::uint8_t* id, std::size_t maximum,
         std::size_t limit, std::size_t capacity, std::size_t base,
         std::size_t work, std::size_t defaults, std::size_t cost, std::size_t maxLanes)
        : program(p), maxActive(maximum), budget(limit), committed(base),
          workBytes(work), defaultBytes(defaults), slotCost(cost), maxCaptureLanes(maxLanes),
          events(capacity), signalEvents(capacity), signalOutputs(p.nodeCount()),
          commands(capacity), commandCaptures(capacity * maxLanes)
    {
        std::memcpy(containerId, id, 16);
        slots.reserve(maximum);
    }
    auto find(GeneratorInstanceId id) noexcept -> Slot*
    {
        if (!id) return nullptr;
        for (auto& slot : slots) if (slot->id == id) return slot.get();
        return nullptr;
    }
    void emit(GeneratorEventKind kind, const Slot& slot) noexcept
    {
        auto& event = events[(eventHead + eventCount) % events.size()];
        event = {};
        event.kind = kind;
        std::memcpy(event.containerId, containerId, 16);
        event.instanceId = slot.id;
        event.startTime = slot.start;
        event.seed = slot.seed;
        ++eventCount;
    }
    static auto observeSignal(void* context, const std::uint8_t nodeId[16]) noexcept -> Result
    {
        auto slot = static_cast<Slot*>(context);
        auto impl = static_cast<Impl*>(slot->observerOwner);
        if (!impl) return Result::InvalidHandle;
        auto lock = std::lock_guard<std::mutex>(impl->mutex);
        for (auto index = std::size_t{}; index < impl->signalOutputCount; ++index) {
            if (std::memcmp(impl->signalOutputs[index].data(), nodeId, 16) != 0) continue;
            if (impl->signalEventCount == impl->signalEvents.size()) return Result::QueueRejected;
            auto& event = impl->signalEvents[(impl->signalEventHead + impl->signalEventCount) %
                                             impl->signalEvents.size()];
            event = {};
            std::memcpy(event.containerId, impl->containerId, 16);
            event.instanceId = slot->id;
            std::memcpy(event.nodeId, nodeId, 16);
            event.time = slot->signalEffectTime;
            if (!std::isfinite(event.time)) return Result::InvalidArgument;
            event.seed = slot->seed;
            ++impl->signalEventCount;
            break;
        }
        return Result::Ok;
    }
    auto publishDestroy(Slot& slot) noexcept -> Result
    {
        if (slot.destroyPublished) return Result::NotReady;
        if (eventCount == events.size()) return Result::QueueRejected;
        emit(GeneratorEventKind::Destroy, slot);
        slot.destroyPublished = true;
        return Result::Ok;
    }
};

GeneratorPool::GeneratorPool(std::unique_ptr<Impl> impl) noexcept : impl_(std::move(impl)) {}
GeneratorPool::~GeneratorPool()
{
    if (!impl_) return;
    {
        auto lock = std::lock_guard<std::mutex>(impl_->mutex);
        impl_->executing = true;
        impl_->rejectEnqueues = true;
    }
    for (auto& slot : impl_->slots) slot->instance.reset();
}

auto GeneratorPool::create(const Program& program, const std::uint8_t containerId[16],
    std::size_t maxActive, std::size_t memoryBudgetBytes, std::size_t eventCapacity,
    std::unique_ptr<GeneratorPool>& out) noexcept -> Result
{
    return createWithCaptureLimit(program, containerId, maxActive, memoryBudgetBytes,
                                  eventCapacity, 16, out);
}

auto GeneratorPool::createWithCaptureLimit(const Program& program, const std::uint8_t containerId[16],
    std::size_t maxActive, std::size_t memoryBudgetBytes, std::size_t eventCapacity,
    std::size_t maxCaptureLanes, std::unique_ptr<GeneratorPool>& out) noexcept -> Result
{
    out.reset();
    if (!containerId || !maxActive || maxActive > 4096 || !eventCapacity ||
        eventCapacity > 8192 || !memoryBudgetBytes || !maxCaptureLanes || maxCaptureLanes > 256)
        return Result::InvalidArgument;
    auto work = program.workBytes();
    auto defaultsCount = program.parameterValueCount();
    if (defaultsCount > std::numeric_limits<std::size_t>::max() / sizeof(double) ||
        work > std::numeric_limits<std::size_t>::max() - 7) return Result::InsufficientWork;
    work = (work + 7) & ~std::size_t{7};
    auto defaults = defaultsCount * sizeof(double);
    auto base = sizeof(Impl) + sizeof(GeneratorPool) + 128 +
        eventCapacity * (sizeof(GeneratorEvent) + sizeof(GeneratorSignalEvent) + sizeof(SpawnCommand) +
                         maxCaptureLanes * sizeof(GeneratorCapture)) +
        program.nodeCount() * sizeof(std::array<std::uint8_t, 16>) +
        maxActive * sizeof(std::unique_ptr<Slot>);
    if (defaults > std::numeric_limits<std::size_t>::max() - sizeof(Slot) - 128 ||
        work > std::numeric_limits<std::size_t>::max() - defaults - sizeof(Slot) - 128)
        return Result::InsufficientWork;
    auto cost = sizeof(Slot) + work + defaults + 128;
    if (base > memoryBudgetBytes || cost > memoryBudgetBytes - base)
        return Result::InsufficientWork;
    try {
        auto impl = std::make_unique<Impl>(program, containerId, maxActive, memoryBudgetBytes,
            eventCapacity, base, work, defaults, cost, maxCaptureLanes);
        out.reset(new GeneratorPool(std::move(impl)));
        return Result::Ok;
    } catch (const std::bad_alloc&) { return Result::InsufficientWork; }
    catch (...) { return Result::InvalidArgument; }
}

auto GeneratorPool::spawn(double time, const GeneratorCapture* captures, std::size_t count,
                          GeneratorInstanceId& out) noexcept -> Result
{
    out = 0;
    if (!std::isfinite(time) || count > 256 || (count && !captures)) return Result::InvalidArgument;
    try {
        auto lock = std::unique_lock<std::mutex>(impl_->mutex);
        if (impl_->executing) return Result::Busy;
        if (impl_->eventCount == impl_->events.size()) return Result::QueueRejected;
        if (impl_->active == impl_->maxActive || !impl_->nextId) return Result::InsufficientWork;
        auto slot = static_cast<Slot*>(nullptr);
        for (auto& candidate : impl_->slots) if (!candidate->id) { slot = candidate.get(); break; }
        if (!slot) {
            if (impl_->slotCost > impl_->budget - impl_->committed) return Result::InsufficientWork;
            auto candidate = std::make_unique<Slot>();
            candidate->work.allocate(impl_->workBytes, impl_->program.workAlignment());
            candidate->defaults.allocate(impl_->defaultBytes, impl_->program.defaultsAlignment());
            slot = candidate.get();
            impl_->slots.push_back(std::move(candidate));
            impl_->committed += impl_->slotCost;
        }
        slot->instance.emplace();
        impl_->executing = true;
        impl_->rejectEnqueues = true;
        lock.unlock();
        auto result = slot->instance->initialize(impl_->program,
            static_cast<double*>(slot->work.data), impl_->workBytes / sizeof(double),
            static_cast<double*>(slot->defaults.data), impl_->defaultBytes / sizeof(double));
        if (result == Result::Ok) {
            slot->observerOwner = impl_.get();
            result = slot->instance->setSignalObserver(&Impl::observeSignal, slot);
        }
        if (result == Result::Ok && impl_->lifecycle.enableDeferredSignals)
        {
            result = slot->instance->setDeferredSignalStorage(slot->deferred.data(), slot->deferred.size());
        }
        if (result == Result::Ok)
            for (auto i = std::size_t{}; i < count; ++i) {
                result = slot->instance->setUnconnectedParameter(captures[i].nodeId, captures[i].inputSlot,
                    captures[i].component, captures[i].value);
                if (result != Result::Ok) break;
            }
        if (result != Result::Ok) slot->instance.reset();
        lock.lock();
        impl_->rejectEnqueues = false;
        impl_->executing = false;
        if (result != Result::Ok) return result;
        slot->id = impl_->nextId++;
        slot->start = time;
        slot->lifetime = Lifetime::infinite();
        slot->started = !impl_->lifecycleConfigured;
        slot->expiring = false;
        slot->expired = false;
        slot->destroyPublished = false;
        slot->destroyDelivered = false;
        slot->sampleReady = false;
        slot->ownerDestroySent = false;
        slot->sampledAge = 0;
        slot->signalTime = 0;
        slot->signalEffectTime = slot->start;
        slot->observerOwner = impl_.get();
        slot->startResult = Result::Ok;
        slot->ownerDestroyResult = Result::Ok;
        auto seed = slot->id;
        for (auto byte : impl_->containerId) seed = (seed ^ byte) * UINT64_C(1099511628211);
        seed += UINT64_C(0x9e3779b97f4a7c15);
        seed = (seed ^ (seed >> 30)) * UINT64_C(0xbf58476d1ce4e5b9);
        seed = (seed ^ (seed >> 27)) * UINT64_C(0x94d049bb133111eb);
        slot->seed = seed ^ (seed >> 31);
        ++impl_->active;
        out = slot->id;
        impl_->emit(GeneratorEventKind::Spawn, *slot);
        return Result::Ok;
    } catch (const std::bad_alloc&) { return Result::InsufficientWork; }
    catch (...) { return Result::InvalidArgument; }
}

auto GeneratorPool::stop(GeneratorInstanceId id) noexcept -> Result
{
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing) return Result::Busy;
    auto slot = impl_->find(id);
    if (!slot) return Result::InvalidHandle;
    if (slot->expiring) return Result::Busy;
    if (impl_->eventCount == impl_->events.size()) return Result::QueueRejected;
    impl_->emit(GeneratorEventKind::Stop, *slot);
    slot->id = 0;
    --impl_->active;
    impl_->executing = true;
    impl_->rejectEnqueues = true;
    lock.unlock();
    slot->instance.reset();
    lock.lock();
    impl_->rejectEnqueues = false;
    impl_->executing = false;
    return Result::Ok;
}

auto GeneratorPool::enqueueSpawn(double time, const GeneratorCapture* captures, std::size_t count) noexcept -> Result
{
    if (!std::isfinite(time) || (count && !captures)) return Result::InvalidArgument;
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->rejectEnqueues) return Result::Busy;
    if (count > impl_->maxCaptureLanes) return Result::InvalidArgument;
    if (impl_->commandCount == impl_->commands.size()) return Result::QueueRejected;
    auto index = (impl_->commandHead + impl_->commandCount) % impl_->commands.size();
    auto& command = impl_->commands[index];
    command.time = time;
    command.count = count;
    for (auto i = std::size_t{}; i < count; ++i)
        impl_->commandCaptures[index * impl_->maxCaptureLanes + i] = captures[i];
    ++impl_->commandCount;
    return Result::Ok;
}

auto GeneratorPool::processSpawns(std::size_t maxCommands, std::size_t& processed) noexcept -> Result
{
    processed = 0;
    while (processed < maxCommands) {
        auto command = SpawnCommand{};
        auto captures = std::array<GeneratorCapture, 256>{};
        {
            auto lock = std::lock_guard<std::mutex>(impl_->mutex);
            if (impl_->executing) return Result::Busy;
            if (!impl_->commandCount) return Result::Ok;
            command = impl_->commands[impl_->commandHead];
            for (auto i = std::size_t{}; i < command.count; ++i)
                captures[i] = impl_->commandCaptures[impl_->commandHead * impl_->maxCaptureLanes + i];
        }
        auto id = GeneratorInstanceId{};
        auto result = spawn(command.time, captures.data(), command.count, id);
        if (result != Result::Ok) return result;
        {
            auto lock = std::lock_guard<std::mutex>(impl_->mutex);
            impl_->commandHead = (impl_->commandHead + 1) % impl_->commands.size();
            --impl_->commandCount;
        }
        ++processed;
    }
    return Result::Ok;
}

auto GeneratorPool::configureLifecycle(const GeneratorLifecycle& lifecycle) noexcept -> Result
{
    auto nonzero = [](const std::uint8_t* id) noexcept {
        for (auto i = 0u; i < 16; ++i) if (id[i]) return true;
        return false;
    };
    if ((lifecycle.hasOnStart && (!nonzero(lifecycle.onStartNodeId) ||
                                  !lifecycle.instructionBudget || lifecycle.instructionBudget > 10000)) ||
        (lifecycle.hasLife && !nonzero(lifecycle.lifeNodeId)))
        return Result::InvalidArgument;
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing || impl_->active || impl_->commandCount || impl_->eventCount ||
        impl_->signalEventCount) return Result::Busy;
    impl_->lifecycle = lifecycle;
    impl_->lifecycleConfigured = true;
    return Result::Ok;
}

auto GeneratorPool::start(GeneratorInstanceId id) noexcept -> Result
{
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing) return Result::Busy;
    if (!impl_->lifecycleConfigured) return Result::InvalidArgument;
    auto slot = impl_->find(id);
    if (!slot) return Result::InvalidHandle;
    if (slot->expiring) return impl_->publishDestroy(*slot);
    if (slot->started) return slot->startResult;
    auto signal = SignalHandle{};
    auto life = ValueHandle{};
    auto status = Result::Ok;
    if (impl_->lifecycle.hasOnStart) {
        status = slot->instance->bindSignal(impl_->lifecycle.onStartNodeId, signal);
        if (status != Result::Ok) return status;
    }
    if (impl_->lifecycle.hasLife) {
        status = slot->instance->bindValue(impl_->lifecycle.lifeNodeId, life);
        if (status != Result::Ok) return status;
        if (life.componentCount != 1) return Result::InvalidValueType;
    }
    impl_->executing = true;
    slot->signalTime = 0;
    slot->signalEffectTime = slot->start;
    lock.unlock();
    status = slot->instance->updateAsync(0, inlineEnqueue, nullptr);
    auto evaluated = status == Result::Ok;
    if (status == Result::Ok) status = slot->instance->result();
    auto duration = std::array<double, 4>{};
    if (status == Result::Ok && impl_->lifecycle.hasLife) {
        status = slot->instance->value(life, duration.data());
        if (status == Result::Ok && (!std::isfinite(duration[0]) || duration[0] < 0))
            status = Result::InvalidValueType;
    }
    if (status == Result::Ok)
        status = slot->instance->dispatchGeneratedSignals(
            impl_->lifecycle.instructionBudget ? impl_->lifecycle.instructionBudget : 10000);
    // Once signal traversal starts it is never replayed, even if a callback fails.
    auto dispatched = status == Result::Ok;
    if (dispatched && impl_->lifecycle.hasOnStart)
        status = slot->instance->dispatchSignal(signal, 0, impl_->lifecycle.instructionBudget);
    lock.lock();
    impl_->executing = false;
    if (evaluated) {
        slot->started = true;
        slot->startResult = status;
        if (dispatched && impl_->lifecycle.hasLife)
            (void)Lifetime::finite(duration[0], slot->lifetime);
    }
    if (status != Result::Ok) return status;
    slot->sampleReady = true;
    slot->sampledAge = 0;
    if (impl_->lifecycle.hasLife && duration[0] == 0)
    {
        slot->expired = true;
        auto due = double{};
        auto hasSignal = false;
        status = slot->instance->nextDeferredSignal(due, hasSignal);
        if (status != Result::Ok || hasSignal)
        {
            return status;
        }
        slot->expiring = true;
        return impl_->publishDestroy(*slot);
    }
    return Result::Ok;
}

auto GeneratorPool::finalize(GeneratorInstanceId id) noexcept -> Result
{
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing) return Result::Busy;
    auto slot = impl_->find(id);
    if (!slot) return Result::InvalidHandle;
    if (!slot->expiring || !slot->destroyDelivered) return Result::NotReady;
    if (impl_->eventCount == impl_->events.size()) return Result::QueueRejected;
    impl_->emit(GeneratorEventKind::Stop, *slot);
    slot->id = 0;
    slot->expiring = false;
    --impl_->active;
    impl_->executing = true;
    impl_->rejectEnqueues = true;
    lock.unlock();
    slot->instance.reset();
    lock.lock();
    impl_->rejectEnqueues = false;
    impl_->executing = false;
    return Result::Ok;
}

auto GeneratorPool::reset() noexcept -> Result
{
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing) return Result::Busy;
    for (const auto& slot : impl_->slots)
        if (slot->id && slot->expiring) return Result::Busy;
    if (impl_->active > impl_->events.size() - impl_->eventCount) return Result::QueueRejected;
    for (auto& slot : impl_->slots) if (slot->id) {
        impl_->emit(GeneratorEventKind::Stop, *slot);
        slot->id = 0;
    }
    impl_->active = 0;
    impl_->commandCount = 0;
    impl_->commandHead = 0;
    impl_->executing = true;
    impl_->rejectEnqueues = true;
    lock.unlock();
    for (auto& slot : impl_->slots) slot->instance.reset();
    lock.lock();
    impl_->rejectEnqueues = false;
    impl_->executing = false;
    return Result::Ok;
}

auto GeneratorPool::setLive(GeneratorInstanceId id, const GeneratorCapture* values,
                            std::size_t count) noexcept -> Result
{
    if (count > 256 || (count && !values)) return Result::InvalidArgument;
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing) return Result::Busy;
    auto slot = impl_->find(id);
    if (!slot) return Result::InvalidHandle;
    if (slot->expiring) return Result::Busy;
    auto handles = std::array<ParameterHandle, 256>{};
    auto previous = std::array<double, 256>{};
    for (auto i = std::size_t{}; i < count; ++i) {
        if (!std::isfinite(values[i].value)) return Result::NonFinite;
        auto result = slot->instance->bindParameter(values[i].nodeId, values[i].inputSlot,
                                                    values[i].component, handles[i]);
        if (result != Result::Ok) return result;
        result = slot->instance->parameter(handles[i], previous[i]);
        if (result != Result::Ok) return result;
        result = slot->instance->setUnconnectedParameter(values[i].nodeId, values[i].inputSlot,
                                                          values[i].component, previous[i]);
        if (result != Result::Ok) return result;
    }
    for (auto i = std::size_t{}; i < count; ++i) {
        auto result = slot->instance->setParameter(handles[i], values[i].value);
        if (result != Result::Ok) {
            while (i) {
                --i;
                (void)slot->instance->setParameter(handles[i], previous[i]);
            }
            return result;
        }
    }
    return Result::Ok;
}

auto GeneratorPool::setCaptured(GeneratorInstanceId id, const GeneratorCapture* values,
                                std::size_t count) noexcept -> Result
{
    if (count > 256 || (count && !values)) return Result::InvalidArgument;
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing) return Result::Busy;
    auto slot = impl_->find(id);
    if (!slot) return Result::InvalidHandle;
    if (slot->started || slot->sampleReady || slot->expiring) return Result::Busy;
    auto handles = std::array<ParameterHandle, 256>{};
    auto previous = std::array<double, 256>{};
    for (auto i = std::size_t{}; i < count; ++i) {
        if (!std::isfinite(values[i].value)) return Result::NonFinite;
        auto result = slot->instance->bindParameter(values[i].nodeId, values[i].inputSlot,
                                                    values[i].component, handles[i]);
        if (result != Result::Ok) return result;
        result = slot->instance->parameter(handles[i], previous[i]);
        if (result != Result::Ok) return result;
        result = slot->instance->setUnconnectedParameter(values[i].nodeId, values[i].inputSlot,
                                                          values[i].component, previous[i]);
        if (result != Result::Ok) return result;
    }
    for (auto i = std::size_t{}; i < count; ++i) {
        auto result = slot->instance->setParameter(handles[i], values[i].value);
        if (result != Result::Ok) {
            while (i) {
                --i;
                (void)slot->instance->setParameter(handles[i], previous[i]);
            }
            return result;
        }
    }
    return Result::Ok;
}

auto GeneratorPool::setSignalOutputs(const std::uint8_t* nodeIds, std::size_t count) noexcept -> Result
{
    if (count && !nodeIds) return Result::InvalidArgument;
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (count > impl_->signalOutputs.size()) return Result::InvalidArgument;
    if (impl_->executing || impl_->active || impl_->commandCount || impl_->eventCount ||
        impl_->signalEventCount)
        return Result::Busy;
    for (auto index = std::size_t{}; index < count; ++index) {
        auto id = nodeIds + index * 16;
        for (auto previous = std::size_t{}; previous < index; ++previous)
            if (std::memcmp(impl_->signalOutputs[previous].data(), id, 16) == 0)
                return Result::InvalidArgument;
    }
    for (auto index = std::size_t{}; index < count; ++index)
        std::memcpy(impl_->signalOutputs[index].data(), nodeIds + index * 16, 16);
    impl_->signalOutputCount = count;
    return Result::Ok;
}

auto GeneratorPool::dispatchSignal(const std::uint8_t nodeId[16], double time,
                                   std::size_t instructionBudget) noexcept -> Result
{
    if (!nodeId || !std::isfinite(time) || !instructionBudget || instructionBudget > 10000)
        return Result::InvalidArgument;
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing) return Result::Busy;
    impl_->executing = true;
    auto result = Result::Ok;
    for (auto& slot : impl_->slots) {
        if (!slot->id || !slot->started || !slot->sampleReady || slot->expiring || time < slot->start)
            continue;
        slot->signalTime = time - slot->start;
        slot->signalEffectTime = time;
        lock.unlock();
        auto current = slot->instance->dispatchSignal(nodeId, slot->signalTime, instructionBudget);
        lock.lock();
        if (result == Result::Ok) result = current;
    }
    impl_->executing = false;
    return result;
}

auto GeneratorPool::setTimeContext(GeneratorInstanceId id, double effectTime, double frameDelta) noexcept -> Result
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing) return Result::Busy;
    auto slot = impl_->find(id);
    if (!slot) return Result::InvalidHandle;
    if (slot->expiring) return Result::NotReady;
    return slot->instance->setTimeContext(effectTime, frameDelta);
}

auto GeneratorPool::evaluate(GeneratorInstanceId id, double time) noexcept -> Result
{
    if (!std::isfinite(time)) return Result::InvalidArgument;
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing) return Result::Busy;
    auto slot = impl_->find(id);
    if (!slot) return Result::InvalidHandle;
    if (slot->expiring) return impl_->publishDestroy(*slot);
    if (!slot->started) return Result::NotReady;
    if (slot->startResult != Result::Ok) return slot->startResult;
    auto elapsed = time - slot->start;
    if (impl_->lifecycle.enableDeferredSignals && slot->sampleReady &&
        time < slot->start + slot->sampledAge)
    {
        return Result::InvalidArgument;
    }
    if (slot->sampleReady && time == slot->start + slot->sampledAge)
    {
        elapsed = slot->sampledAge;
    }
    auto sample = LifetimeSample{};
    if (slot->lifetime.sample(elapsed, sample) != Result::Ok) return Result::InvalidArgument;
    auto age = slot->expired ? elapsed : sample.age;
    auto due = double{};
    auto hasSignal = false;
    auto pendingResult = slot->instance->nextDeferredSignal(due, hasSignal);
    if (pendingResult != Result::Ok)
    {
        return pendingResult;
    }
    if (hasSignal)
    {
        if (time == slot->start + due && (slot->expired || due <= sample.age))
        {
            age = due;
        }
        if (age > due)
        {
            return Result::NotReady;
        }
    }
    // Life is captured once by start, never inferred from Busy/FCurve or
    // refreshed by Live inputs. Clamp an overshooting frame to the expiry
    // boundary so Destroy consumers receive the actual final value.
    impl_->executing = true;
    slot->signalTime = age;
    slot->signalEffectTime = slot->start + age;
    lock.unlock();
    auto result = slot->instance->updateAsync(age, inlineEnqueue, nullptr);
    if (result == Result::Ok) result = slot->instance->result();
    if (result == Result::Ok)
        result = slot->instance->dispatchGeneratedSignals(
            impl_->lifecycle.instructionBudget ? impl_->lifecycle.instructionBudget : 10000);
    lock.lock();
    impl_->executing = false;
    if (result == Result::Ok) {
        slot->sampledAge = age;
        slot->sampleReady = true;
    }
    if (result == Result::Ok && impl_->lifecycleConfigured && impl_->lifecycle.hasLife &&
        sample.expired) {
        slot->expired = true;
        if (!hasSignal)
        {
            slot->expiring = true;
            return impl_->publishDestroy(*slot);
        }
    }
    return result;
}

auto GeneratorPool::nextDeferredSignal(GeneratorInstanceId id, double& time, bool& hasSignal) noexcept -> Result
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    auto due = double{};
    auto result = slot->instance->nextDeferredSignal(due, hasSignal);
    if (result == Result::Ok && hasSignal)
    {
        time = slot->start + due;
        if (!std::isfinite(time))
        {
            return Result::InvalidArgument;
        }
    }
    return result;
}

auto GeneratorPool::dispatchDeferredSignal(GeneratorInstanceId id, double time,
                                           std::size_t instructionBudget) noexcept -> Result
{
    if (!std::isfinite(time) || !instructionBudget || instructionBudget > 10000)
    {
        return Result::InvalidArgument;
    }
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    if (!slot->started || !slot->sampleReady || slot->expiring)
    {
        return Result::NotReady;
    }
    if (slot->startResult != Result::Ok)
    {
        return slot->startResult;
    }
    auto due = double{};
    auto hasSignal = false;
    auto result = slot->instance->nextDeferredSignal(due, hasSignal);
    if (result != Result::Ok)
    {
        return result;
    }
    if (!hasSignal || slot->start + due != time || due != slot->sampledAge)
    {
        return Result::NotReady;
    }
    impl_->executing = true;
    slot->signalTime = due;
    slot->signalEffectTime = time;
    lock.unlock();
    result = slot->instance->dispatchDeferredSignal(due, instructionBudget);
    lock.lock();
    impl_->executing = false;
    if (result != Result::Ok)
    {
        slot->startResult = result;
        return result;
    }
    result = slot->instance->nextDeferredSignal(due, hasSignal);
    if (result == Result::Ok && slot->expired && !hasSignal)
    {
        slot->expiring = true;
        return impl_->publishDestroy(*slot);
    }
    return result;
}

auto GeneratorPool::elapsed(GeneratorInstanceId id, double& out) noexcept -> Result
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    if (!slot->sampleReady)
    {
        return Result::NotReady;
    }
    out = slot->sampledAge;
    return Result::Ok;
}

auto GeneratorPool::ownerDestroy(GeneratorInstanceId id, const std::uint8_t nodeId[16],
                                 double time, std::size_t instructionBudget) noexcept -> Result
{
    if (!nodeId || !std::isfinite(time) || !instructionBudget || instructionBudget > 10000)
        return Result::InvalidArgument;
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing) return Result::Busy;
    auto slot = impl_->find(id);
    if (!slot) return Result::InvalidHandle;
    if (slot->ownerDestroySent) return slot->ownerDestroyResult;
    if (!slot->started || !slot->sampleReady || slot->expiring) return Result::NotReady;
    if (slot->startResult != Result::Ok) return slot->startResult;
    if (time != slot->start + slot->sampledAge) return Result::InvalidArgument;
    auto signal = SignalHandle{};
    auto result = slot->instance->bindSignal(nodeId, signal);
    if (result != Result::Ok) return result;
    slot->ownerDestroySent = true;
    impl_->executing = true;
    slot->signalTime = slot->sampledAge;
    slot->signalEffectTime = time;
    lock.unlock();
    result = slot->instance->dispatchSignal(signal, slot->sampledAge, instructionBudget);
    lock.lock();
    slot->ownerDestroyResult = result;
    impl_->executing = false;
    return result;
}

auto GeneratorPool::lifetime(GeneratorInstanceId id, LifetimeSample& out) noexcept -> Result
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing) return Result::Busy;
    auto slot = impl_->find(id);
    if (!slot) return Result::InvalidHandle;
    if (!slot->sampleReady) return Result::NotReady;
    return slot->lifetime.sample(slot->sampledAge, out);
}

auto GeneratorPool::value(GeneratorInstanceId id, const std::uint8_t nodeId[16],
                          double out[4], std::uint8_t& width) noexcept -> Result
{
    if (!nodeId || !out) return Result::InvalidArgument;
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing) return Result::Busy;
    auto slot = impl_->find(id);
    return slot ? slot->instance->valueComponents(nodeId, out, width) : Result::InvalidHandle;
}

auto GeneratorPool::popEvent(GeneratorEvent& out) noexcept -> bool
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing || !impl_->eventCount) return false;
    out = impl_->events[impl_->eventHead];
    if (out.kind == GeneratorEventKind::Destroy) {
        auto slot = impl_->find(out.instanceId);
        if (slot && slot->expiring) slot->destroyDelivered = true;
    }
    impl_->eventHead = (impl_->eventHead + 1) % impl_->events.size();
    --impl_->eventCount;
    return true;
}

auto GeneratorPool::popSignalEvent(GeneratorSignalEvent& out) noexcept -> bool
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing || !impl_->signalEventCount) return false;
    out = impl_->signalEvents[impl_->signalEventHead];
    impl_->signalEventHead = (impl_->signalEventHead + 1) % impl_->signalEvents.size();
    --impl_->signalEventCount;
    return true;
}

auto GeneratorPool::pendingEventCount() const noexcept -> std::size_t
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    return impl_->eventCount;
}

auto GeneratorPool::pendingSignalEventCount() const noexcept -> std::size_t
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    return impl_->signalEventCount;
}

auto GeneratorPool::activeCount() const noexcept -> std::size_t
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    return impl_->active;
}
}
