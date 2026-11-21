#include "LbCpuParticlesInternal.h"

namespace liberafx {
using namespace cpu_particle_detail;

CpuParticleWorld::CpuParticleWorld(std::unique_ptr<Impl> impl) noexcept : impl_(std::move(impl)) {}
CpuParticleWorld::~CpuParticleWorld() = default;

auto CpuParticleWorld::create(const std::uint8_t* payload, std::size_t bytes,
    const CustomNodeDescriptor* registry, std::size_t registryCount,
    std::unique_ptr<CpuParticleWorld>& out, bool enableBatching) noexcept -> Result
{
    if ((!payload && bytes != 0) || (!registry && registryCount != 0))
    {
        return Result::InvalidArgument;
    }
    try
    {
        auto impl = std::make_unique<Impl>();
        if (bytes != 0)
        {
            auto result = parse(payload, bytes, impl->systems);
            if (result != Result::Ok)
            {
                return result;
            }
        }
        for (auto& system : impl->systems)
        {
            auto result = system->initialize(registry, registryCount, enableBatching);
            if (result != Result::Ok)
            {
                return result;
            }
            for (auto& callback : system->callbacks)
            {
                if (callback.operation != 0x36)
                {
                    continue;
                }
                auto descriptor = CustomNodeDescriptor{};
                std::copy(callback.node.begin(), callback.node.end(), descriptor.typeId);
                descriptor.abiVersion = 1;
                descriptor.execution = CustomExecution::Signal;
                descriptor.branchCount = 1;
                descriptor.function = System::invoke;
                descriptor.context = &callback;
                impl->signals.push_back(descriptor);
            }
        }
        out.reset(new CpuParticleWorld(std::move(impl)));
        return Result::Ok;
    }
    catch (const std::bad_alloc&)
    {
        return Result::InsufficientWork;
    }
}
auto CpuParticleWorld::signalDescriptors(std::size_t& count) const noexcept -> const CustomNodeDescriptor*
{
    count = impl_->signals.size();
    return impl_->signals.data();
}
auto CpuParticleWorld::prepare(Instance& root) noexcept -> Result
{
    for (auto& system : impl_->systems)
    {
        system->pending.swap(system->delivery);
        system->deliveryCount = system->pendingCount;
        system->pendingCount = 0;
        for (auto index = std::size_t{}; index < system->deliveryCount; ++index)
        {
            const auto& message = system->delivery[index];
            if (message.event.kind != 1 || !nonzero(message.target))
            {
                continue;
            }
            // Explicit LastById only. A later/higher ID wins, never slot order.
            auto selected = &message;
            for (auto candidate = std::size_t{}; candidate < system->deliveryCount; ++candidate)
            {
                const auto& other = system->delivery[candidate];
                if (other.event.kind == 1 && other.target == message.target &&
                    other.event.particleId > selected->event.particleId)
                {
                    selected = &other;
                }
            }
            if (selected != &message)
            {
                continue;
            }
            for (auto lane = 0u; lane < message.event.width; ++lane)
            {
                auto result = root.setUnconnectedParameter(message.target.data(), 0, static_cast<std::uint8_t>(lane),
                    message.event.values[lane]);
                if (result != Result::Ok)
                {
                    return result;
                }
            }
        }
    }
    return Result::Ok;
}
auto CpuParticleWorld::deliverSignals(Instance& root, double time, std::size_t budget) noexcept -> Result
{
    for (auto& system : impl_->systems)
    {
        for (auto index = std::size_t{}; index < system->deliveryCount; ++index)
        {
            const auto& message = system->delivery[index];
            if (message.event.kind == 2 && nonzero(message.target))
            {
                auto result = root.dispatchSignal(message.target.data(), time, budget);
                if (result != Result::Ok)
                {
                    return result;
                }
            }
        }
        system->deliveryCount = 0;
    }
    return Result::Ok;
}
auto CpuParticleWorld::advance(Instance& root, double time, bool emitting, const HostQualitySnapshot& quality,
    double frameDelta) noexcept -> Result
{
    if (!std::isfinite(frameDelta) || frameDelta < 0)
    {
        return Result::InvalidArgument;
    }
    for (auto& system : impl_->systems)
    {
        system->frameDelta = frameDelta;
        system->realTime = root.realEffectTime();
        system->realDelta = root.realFrameDelta();
        auto result = system->tick(root, time, emitting, quality);
        if (result != Result::Ok)
        {
            system->failure = result;
            return result;
        }
    }
    return Result::Ok;
}
auto CpuParticleWorld::queueSignal(const std::uint8_t nodeId[16]) noexcept -> Result
{
    if (!nodeId)
    {
        return Result::InvalidArgument;
    }
    for (auto& system : impl_->systems)
    {
        for (const auto& callback : system->callbacks)
        {
            if (callback.operation != 0x36 ||
                std::memcmp(callback.node.data(), nodeId, 16) != 0)
            {
                continue;
            }
            if (system->incomingCount == system->incoming.size())
            {
                return Result::QueueRejected;
            }
            system->incoming[system->incomingCount++] =
                {callback.node, callback.stage};
        }
    }
    return Result::Ok;
}
auto CpuParticleWorld::count(const std::uint8_t systemId[16], std::size_t& out) const noexcept -> Result
{
    if (!systemId)
    {
        return Result::InvalidArgument;
    }
    for (auto& system : impl_->systems)
    {
        if (std::memcmp(systemId, system->id.data(), 16) == 0)
        {
            out = system->live;
            return Result::Ok;
        }
    }
    return Result::InvalidHandle;
}
auto CpuParticleWorld::at(const std::uint8_t systemId[16], std::size_t index, CpuParticleSnapshot& out) const noexcept -> Result
{
    if (!systemId)
    {
        return Result::InvalidArgument;
    }
    for (auto& system : impl_->systems)
    {
        if (std::memcmp(systemId, system->id.data(), 16) != 0)
        {
            continue;
        }
        if (index >= system->live)
        {
            return Result::InvalidArgument;
        }
        out = system->slots[system->activeIndices[index]].draw;
        return Result::Ok;
    }
    return Result::InvalidHandle;
}
auto CpuParticleWorld::timing(const std::uint8_t systemId[16], std::size_t index, CpuParticleTiming& out) const noexcept -> Result
{
    if (!systemId) return Result::InvalidArgument;
    for (const auto& system : impl_->systems) {
        if (std::memcmp(systemId, system->id.data(), 16) != 0) continue;
        if (index >= system->live) return Result::InvalidArgument;
        const auto& slot = system->slots[system->activeIndices[index]];
        out = {slot.age, slot.life};
        return Result::Ok;
    }
    return Result::InvalidHandle;
}

auto CpuParticleWorld::read(const std::uint8_t systemId[16], std::size_t first,
    CpuParticleSnapshot* snapshots, CpuParticleTiming* timings, std::size_t capacity, std::size_t& written) const noexcept -> Result
{
    written = 0;
    if (!systemId || !snapshots || capacity == 0) return Result::InvalidArgument;
    for (const auto& system : impl_->systems) {
        if (std::memcmp(systemId,system->id.data(),16)!=0) continue;
        if (first > system->live) return Result::InvalidArgument;
        const auto count = std::min(capacity,system->live-first);
        for (auto index=std::size_t{};index<count;++index) {
            const auto& slot=system->slots[system->activeIndices[first+index]];
            snapshots[index]=slot.draw;
            if (timings) timings[index]={slot.age,slot.life};
        }
        written=count;
        return Result::Ok;
    }
    return Result::InvalidHandle;
}

auto CpuParticleWorld::popEvent(CpuParticleEvent& out) noexcept -> bool
{
    for (auto& system : impl_->systems)
    {
        if (system->eventCount != 0)
        {
            out = system->events[system->eventHead];
            system->eventHead = (system->eventHead + 1) % system->events.size();
            --system->eventCount;
            return true;
        }
    }
    return false;
}
auto CpuParticleWorld::pendingEventCount() const noexcept -> std::size_t
{
    auto count = std::size_t{};
    for (const auto& system : impl_->systems)
    {
        count += system->eventCount;
    }
    return count;
}
auto CpuParticleWorld::activeCount() const noexcept -> std::size_t
{
    auto count = std::size_t{};
    for (auto& system : impl_->systems)
    {
        count += system->live;
    }
    return count;
}
auto CpuParticleWorld::canEmit() const noexcept -> bool
{
    for (auto& system : impl_->systems)
    {
        if (system->burst != 0 || system->rate > 0 || nonzero(system->emissionSource))
        {
            return true;
        }
    }
    return false;
}
auto CpuParticleWorld::hasPendingRoutes() const noexcept -> bool
{
    for (auto& system : impl_->systems)
    {
        if (system->pendingCount != 0 || system->deliveryCount != 0 || system->incomingCount != 0)
        {
            return true;
        }
    }
    return false;
}
void CpuParticleWorld::clear() noexcept
{
    for (auto& system : impl_->systems)
    {
        for (auto index = 0u; index < system->capacity; ++index)
        {
            system->slots[index].active = false;
        }
        system->live = 0;
        system->incomingCount = 0;
        system->pendingCount = 0;
        system->deliveryCount = 0;
        system->eventHead = 0;
        system->eventCount = 0;
    }
}
bool CpuParticleWorld::supportsPoolReuse() const noexcept
{
    for (const auto& system : impl_->systems) if (system->emitterModel) return false;
    return true;
}
Result CpuParticleWorld::resetForPoolReuse() noexcept
{
    if (!supportsPoolReuse()) return Result::UnsupportedBinary;
    clear();
    for (auto& pointer : impl_->systems) {
        auto& system=*pointer;
        if (system.instances) for (auto i=0u;i<system.capacity;++i) for (auto stage=0u;stage<2;++stage) {
            auto& instance=system.instances[i].stages[stage];
            const auto status=instance.deinitialize();
            if (status!=Result::Ok) return status;
        }
        for (auto stage=0u;stage<2;++stage) {
            auto& shared=stage==0 ? system.sharedSpawn : system.sharedUpdate;
            if (!shared) continue;
            auto& work=stage==0 ? system.sharedSpawnWork : system.sharedUpdateWork;
            auto& handle=stage==0 ? system.sharedSpawnEntry : system.sharedUpdateEntry;
            const auto& program=system.stages[stage].program;
            auto status=shared->initialize(program,static_cast<double*>(work.data),program.workBytes()/sizeof(double));
            if (status==Result::Ok) status=shared->bindSignal(system.stages[stage].entry.data(),handle);
            if (status!=Result::Ok) return status;
        }
        system.burst=system.initialBurst;
        system.remainder=system.lastTotal=0; system.lastTime=-1;
        system.stepDelta=system.frameDelta=system.realTime=system.realDelta=0;
        system.nextId=1; system.current=nullptr; system.failure=Result::Ok;
        system.spawnBatch.enabled=static_cast<bool>(system.sharedSpawn);
        system.updateBatch.enabled=static_cast<bool>(system.sharedUpdate);
    }
    return Result::Ok;
}

}
