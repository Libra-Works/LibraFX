#include "LbGeneratorPoolInternal.h"

namespace liberafx
{

GeneratorPool::GeneratorPool(std::unique_ptr<Impl> impl) noexcept
    : impl_(std::move(impl))
{
}

GeneratorPool::~GeneratorPool()
{
    if (!impl_)
    {
        return;
    }
    {
        auto lock = std::lock_guard<std::mutex>(impl_->mutex);
        impl_->executing = true;
        impl_->rejectEnqueues = true;
    }
    for (auto &slot : impl_->slots)
    {
        slot->instance.reset();
    }
}

auto GeneratorPool::create(const Program &program,
                           const std::uint8_t containerId[16],
                           std::size_t maxActive, std::size_t memoryBudgetBytes,
                           std::size_t eventCapacity,
                           std::unique_ptr<GeneratorPool> &out) noexcept
    -> Result
{
    return createWithCaptureLimit(program, containerId, maxActive,
                                  memoryBudgetBytes, eventCapacity, 16, out);
}

auto GeneratorPool::createWithCaptureLimit(
    const Program &program, const std::uint8_t containerId[16],
    std::size_t maxActive, std::size_t memoryBudgetBytes,
    std::size_t eventCapacity, std::size_t maxCaptureLanes,
    std::unique_ptr<GeneratorPool> &out) noexcept -> Result
{
    out.reset();
    if (!containerId || !maxActive || maxActive > 4096 || !eventCapacity ||
        eventCapacity > 8192 || !memoryBudgetBytes || !maxCaptureLanes ||
        maxCaptureLanes > 256)
    {
        return Result::InvalidArgument;
    }
    auto work = program.workBytes();
    auto defaultsCount = program.parameterValueCount();
    if (defaultsCount >
            std::numeric_limits<std::size_t>::max() / sizeof(double) ||
        work > std::numeric_limits<std::size_t>::max() - 7)
    {
        return Result::InsufficientWork;
    }
    work = (work + 7) & ~std::size_t{7};
    auto defaults = defaultsCount * sizeof(double);
    auto base =
        sizeof(Impl) + sizeof(GeneratorPool) + 128 +
        eventCapacity * (sizeof(GeneratorEvent) + sizeof(GeneratorSignalEvent) +
                         sizeof(SpawnCommand) +
                         maxCaptureLanes * sizeof(GeneratorCapture)) +
        program.nodeCount() * sizeof(std::array<std::uint8_t, 16>) +
        maxActive * sizeof(std::unique_ptr<Slot>);
    if (defaults >
            std::numeric_limits<std::size_t>::max() - sizeof(Slot) - 128 ||
        work > std::numeric_limits<std::size_t>::max() - defaults -
                   sizeof(Slot) - 128)
    {
        return Result::InsufficientWork;
    }
    auto cost = sizeof(Slot) + work + defaults + 128;
    if (base > memoryBudgetBytes || cost > memoryBudgetBytes - base)
    {
        return Result::InsufficientWork;
    }
    try
    {
        auto impl = std::make_unique<Impl>(
            program, containerId, maxActive, memoryBudgetBytes, eventCapacity,
            base, work, defaults, cost, maxCaptureLanes);
        out.reset(new GeneratorPool(std::move(impl)));
        return Result::Ok;
    }
    catch (const std::bad_alloc &)
    {
        return Result::InsufficientWork;
    }
    catch (...)
    {
        return Result::InvalidArgument;
    }
}

auto GeneratorPool::spawn(double time, const GeneratorCapture *captures,
                          std::size_t count, GeneratorInstanceId &out) noexcept
    -> Result
{
    return spawnTagged(time, captures, count, 0, out);
}

auto GeneratorPool::spawnTagged(double time, const GeneratorCapture *captures,
                                std::size_t count, std::uint64_t ownerTag,
                                GeneratorInstanceId &out) noexcept -> Result
{
    out = 0;
    if (!std::isfinite(time) || count > 256 || (count && !captures))
    {
        return Result::InvalidArgument;
    }
    try
    {
        auto lock = std::unique_lock<std::mutex>(impl_->mutex);
        if (impl_->executing)
        {
            return Result::Busy;
        }
        if (impl_->eventCount == impl_->events.size())
        {
            return Result::QueueRejected;
        }
        if (impl_->active == impl_->maxActive || !impl_->nextId)
        {
            return Result::InsufficientWork;
        }
        auto slot = static_cast<Slot *>(nullptr);
        for (auto &candidate : impl_->slots)
        {
            if (!candidate->id)
            {
                slot = candidate.get();
                break;
            }
        }
        if (!slot)
        {
            if (impl_->preparedStorage)
            {
                return Result::InsufficientWork;
            }
            if (impl_->slotCost > impl_->budget - impl_->committed)
            {
                return Result::InsufficientWork;
            }
            auto candidate = std::make_unique<Slot>();
            candidate->work.allocate(impl_->workBytes,
                                     impl_->program.workAlignment());
            candidate->defaults.allocate(impl_->defaultBytes,
                                         impl_->program.defaultsAlignment());
            slot = candidate.get();
            impl_->slots.push_back(std::move(candidate));
            impl_->committed += impl_->slotCost;
        }
        slot->instance.emplace();
        impl_->executing = true;
        impl_->rejectEnqueues = true;
        lock.unlock();
        auto result = slot->instance->initialize(
            impl_->program, static_cast<double *>(slot->work.data),
            impl_->workBytes / sizeof(double),
            static_cast<double *>(slot->defaults.data),
            impl_->defaultBytes / sizeof(double));
        if (result == Result::Ok)
        {
            slot->observerOwner = impl_.get();
            result =
                slot->instance->setSignalObserver(&Impl::observeSignal, slot);
        }
        if (result == Result::Ok)
        {
            result = slot->instance->enableForceRequests();
        }
        if (result == Result::Ok && impl_->lifecycle.enableDeferredSignals)
        {
            result = slot->instance->setDeferredSignalStorage(
                slot->deferred.data(), slot->deferred.size());
        }
        if (result == Result::Ok)
        {
            for (auto i = std::size_t{}; i < count; ++i)
            {
                result = slot->instance->setUnconnectedParameter(
                    captures[i].nodeId, captures[i].inputSlot,
                    captures[i].component, captures[i].value);
                if (result != Result::Ok)
                {
                    break;
                }
            }
        }
        if (result != Result::Ok)
        {
            slot->instance.reset();
        }
        lock.lock();
        impl_->rejectEnqueues = false;
        impl_->executing = false;
        if (result != Result::Ok)
        {
            return result;
        }
        slot->id = impl_->nextId++;
        slot->ownerTag = ownerTag;
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
        for (auto byte : impl_->containerId)
        {
            seed = (seed ^ byte) * UINT64_C(1099511628211);
        }
        seed += UINT64_C(0x9e3779b97f4a7c15);
        seed = (seed ^ (seed >> 30)) * UINT64_C(0xbf58476d1ce4e5b9);
        seed = (seed ^ (seed >> 27)) * UINT64_C(0x94d049bb133111eb);
        slot->seed = seed ^ (seed >> 31);
        ++impl_->active;
        out = slot->id;
        impl_->emit(GeneratorEventKind::Spawn, *slot);
        return Result::Ok;
    }
    catch (const std::bad_alloc &)
    {
        return Result::InsufficientWork;
    }
    catch (...)
    {
        return Result::InvalidArgument;
    }
}

auto GeneratorPool::stop(GeneratorInstanceId id) noexcept -> Result
{
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
    if (slot->expiring)
    {
        return Result::Busy;
    }
    if (impl_->eventCount == impl_->events.size())
    {
        return Result::QueueRejected;
    }
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

auto GeneratorPool::forceTerminate(GeneratorInstanceId id) noexcept -> Result
{
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    if (!id || (impl_->nextId && id >= impl_->nextId))
    {
        return Result::InvalidHandle;
    }
    auto slot = impl_->find(id);
    // Purge even after natural retirement: host may not have routed the notice.
    removeQueued(impl_->events, impl_->eventHead, impl_->eventCount,
                 [id](const auto &event) { return event.instanceId == id; });
    removeQueued(impl_->signalEvents, impl_->signalEventHead,
                 impl_->signalEventCount,
                 [id](const auto &event) { return event.instanceId == id; });
    if (!slot)
    {
        return Result::Ok;
    }
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

auto GeneratorPool::forceTerminateAll() noexcept -> Result
{
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    impl_->executing = true;
    impl_->rejectEnqueues = true;
    impl_->active = 0;
    impl_->eventCount = impl_->signalEventCount = 0;
    impl_->commandHead = impl_->commandCount = 0;
    for (auto &slot : impl_->slots)
    {
        slot->id = 0;
    }
    lock.unlock();
    for (auto &slot : impl_->slots)
    {
        slot->instance.reset();
    }
    lock.lock();
    impl_->rejectEnqueues = false;
    impl_->executing = false;
    return Result::Ok;
}

auto GeneratorPool::enqueueSpawn(double time, const GeneratorCapture *captures,
                                 std::size_t count) noexcept -> Result
{
    return enqueueSpawnTagged(time, captures, count, 0);
}

auto GeneratorPool::enqueueSpawnTagged(double time, const GeneratorCapture *captures,
                                       std::size_t count,
                                       std::uint64_t ownerTag) noexcept -> Result
{
    if (!std::isfinite(time) || (count && !captures))
    {
        return Result::InvalidArgument;
    }
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->rejectEnqueues)
    {
        return Result::Busy;
    }
    if (count > impl_->maxCaptureLanes)
    {
        return Result::InvalidArgument;
    }
    if (impl_->commandCount == impl_->commands.size())
    {
        return Result::QueueRejected;
    }
    auto index =
        (impl_->commandHead + impl_->commandCount) % impl_->commands.size();
    auto &command = impl_->commands[index];
    command.time = time;
    command.count = count;
    command.ownerTag = ownerTag;
    for (auto i = std::size_t{}; i < count; ++i)
    {
        impl_->commandCaptures[index * impl_->maxCaptureLanes + i] =
            captures[i];
    }
    ++impl_->commandCount;
    return Result::Ok;
}

auto GeneratorPool::ownerTag(GeneratorInstanceId id, std::uint64_t& out) const noexcept -> Result
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
    out = slot->ownerTag;
    return Result::Ok;
}

auto GeneratorPool::cancelSpawnsOwned(std::uint64_t ownerTag) noexcept -> Result
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    removeQueued(impl_->commands, impl_->commandHead, impl_->commandCount,
                 [ownerTag](const auto& command) {
                     return command.ownerTag == ownerTag;
                 });
    return Result::Ok;
}

auto GeneratorPool::processSpawns(std::size_t maxCommands,
                                  std::size_t &processed) noexcept -> Result
{
    processed = 0;
    while (processed < maxCommands)
    {
        auto command = SpawnCommand{};
        auto captures = std::array<GeneratorCapture, 256>{};
        {
            auto lock = std::lock_guard<std::mutex>(impl_->mutex);
            if (impl_->executing)
            {
                return Result::Busy;
            }
            if (!impl_->commandCount)
            {
                return Result::Ok;
            }
            command = impl_->commands[impl_->commandHead];
            for (auto i = std::size_t{}; i < command.count; ++i)
            {
                captures[i] =
                    impl_->commandCaptures[impl_->commandHead *
                                               impl_->maxCaptureLanes +
                                           i];
            }
        }
        auto id = GeneratorInstanceId{};
        auto result = spawnTagged(command.time, captures.data(), command.count,
                                  command.ownerTag, id);
        if (result != Result::Ok)
        {
            return result;
        }
        {
            auto lock = std::lock_guard<std::mutex>(impl_->mutex);
            impl_->commandHead =
                (impl_->commandHead + 1) % impl_->commands.size();
            --impl_->commandCount;
        }
        ++processed;
    }
    return Result::Ok;
}

auto GeneratorPool::prepareStorage() noexcept -> Result
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing || impl_->active)
    {
        return Result::Busy;
    }
    try
    {
        while (impl_->slots.size() < impl_->maxActive)
        {
            if (impl_->slotCost > impl_->budget - impl_->committed)
            {
                return Result::InsufficientWork;
            }
            auto slot = std::make_unique<Slot>();
            slot->work.allocate(impl_->workBytes, impl_->program.workAlignment());
            slot->defaults.allocate(impl_->defaultBytes, impl_->program.defaultsAlignment());
            impl_->slots.push_back(std::move(slot));
            impl_->committed += impl_->slotCost;
        }
        impl_->preparedStorage = true;
        return Result::Ok;
    }
    catch (...)
    {
        return Result::InsufficientWork;
    }
}
auto GeneratorPool::resetForPoolReuse() noexcept -> Result
{
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    impl_->executing = impl_->rejectEnqueues = true;
    impl_->active = 0;
    impl_->eventCount = impl_->signalEventCount = impl_->commandCount = 0;
    for (auto &slot : impl_->slots)
    {
        slot->id = 0;
    }
    lock.unlock();
    for (auto &slot : impl_->slots)
    {
        slot->instance.reset();
    }
    lock.lock();
    impl_->eventHead = impl_->signalEventHead = impl_->commandHead = 0;
    impl_->nextId = 1;
    impl_->executing = impl_->rejectEnqueues = false;
    return Result::Ok;
}
auto GeneratorPool::reset() noexcept -> Result
{
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    for (const auto &slot : impl_->slots)
    {
        if (slot->id && slot->expiring)
        {
            return Result::Busy;
        }
    }
    if (impl_->active > impl_->events.size() - impl_->eventCount)
    {
        return Result::QueueRejected;
    }
    for (auto &slot : impl_->slots)
    {
        if (slot->id)
        {
            impl_->emit(GeneratorEventKind::Stop, *slot);
            slot->id = 0;
        }
    }
    impl_->active = 0;
    impl_->commandCount = 0;
    impl_->commandHead = 0;
    impl_->executing = true;
    impl_->rejectEnqueues = true;
    lock.unlock();
    for (auto &slot : impl_->slots)
    {
        slot->instance.reset();
    }
    lock.lock();
    impl_->rejectEnqueues = false;
    impl_->executing = false;
    return Result::Ok;
}

auto GeneratorPool::popEvent(GeneratorEvent &out) noexcept -> bool
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing || !impl_->eventCount)
    {
        return false;
    }
    out = impl_->events[impl_->eventHead];
    if (out.kind == GeneratorEventKind::Destroy)
    {
        auto slot = impl_->find(out.instanceId);
        if (slot && slot->expiring)
        {
            slot->destroyDelivered = true;
        }
    }
    impl_->eventHead = (impl_->eventHead + 1) % impl_->events.size();
    --impl_->eventCount;
    return true;
}

auto GeneratorPool::popSignalEvent(GeneratorSignalEvent &out) noexcept -> bool
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing || !impl_->signalEventCount)
    {
        return false;
    }
    out = impl_->signalEvents[impl_->signalEventHead];
    impl_->signalEventHead =
        (impl_->signalEventHead + 1) % impl_->signalEvents.size();
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

} // namespace liberafx
