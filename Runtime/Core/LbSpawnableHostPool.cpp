#include "liberafx/LbSpawnableHostPool.h"
#include "LbSpawnableHostInternal.h"
#include <atomic>
#include <thread>

namespace liberafx {
using namespace spawnable_host_detail;

bool SpawnableHost::supportsPoolReuse() const noexcept
{
    return impl_->callbackReuseSafe;
}
bool SpawnableHost::supportsWorkerExecution() const noexcept
{
    return supportsSharedExecution() && impl_->callbackWorkerSafe;
}
bool SpawnableHost::supportsSharedExecution() const noexcept
{
    if (impl_->operationActive || impl_->executionDebugAttached)
    {
        return false;
    }
    for (const auto &state : impl_->templates)
    {
        for (const auto &instance : state->instances)
        {
            if (instance.executionDebugAttached)
            {
                return false;
            }
        }
    }
    return true;
}
bool SpawnableHost::supportsParallelPreparation() const noexcept
{
    return impl_->callbackPreparationSafe;
}
std::size_t SpawnableHost::simulationWorkEstimate() const noexcept
{
    auto work = std::max<std::size_t>(1, impl_->parentProgram.nodeCount()) +
                (impl_->cpuParticles
                     ? (impl_->started ? impl_->cpuParticles->activeCount() : impl_->cpuParticles->reservedCapacity())
                     : 0);
    for (const auto &state : impl_->templates)
    {
        work += state->program.nodeCount() * state->instances.size();
        for (const auto &instance : state->instances)
        {
            if (instance.cpuParticles)
            {
                work += instance.cpuParticles->activeCount();
            }
        }
    }
    return work;
}
Result SpawnableHost::resetForPoolReuse() noexcept
{
    auto& host=*impl_;
    if (host.operationActive) return Result::Busy;
    if (!supportsPoolReuse()) return Result::UnsupportedBinary;
    auto operation=OwnerOperation(host.operationActive);
    auto generation=std::uint64_t{};
    auto status=nextHostGeneration(generation);
    if (status!=Result::Ok) return status;
    for (auto &state : host.templates)
    {
        status = state->pool->resetForPoolReuse();
        if (status != Result::Ok)
        {
            return status;
        }
        for (auto &instance : state->instances)
        {
            status = host.recycleCpuWorld(*state, instance);
            if (status != Result::Ok)
            {
                return status;
            }
        }
        state->instances.clear();
        if (!state->data->cpuPrograms.empty() && state->idleCpuWorlds.size() != host.options.maxActivePerTemplate)
        {
            return Result::InsufficientWork;
        }
    }
    // The lifecycle detaches borrowed deferred storage before reinitialization.
    host.lifecycle.reset();
    status=host.rootInstance->setDebugHooks({});
    if (status==Result::Ok) status=host.rootInstance->setProfiling({});
    if (status==Result::Ok) status=host.rootInstance->setSignalObserver(nullptr,nullptr);
    if (status==Result::Ok) status=host.rootInstance->initialize(host.parentProgram,
        static_cast<double*>(host.rootWork.data),(host.parentProgram.workBytes()+7)/8,
        static_cast<double*>(host.rootDefaults.data),host.parentProgram.parameterValueCount());
    if (status!=Result::Ok) { host.failure=status; return status; }
    // Match fresh work values without overwriting initialized custom state.
    const auto numericBytes=host.parentProgram.nodeCount()*(host.parentProgram.version()==1 ? 1u : 4u)*sizeof(double);
    if (numericBytes) std::memset(host.rootWork.data,0,numericBytes);
    status=host.rootInstance->enableForceRequests();
    if (status==Result::Ok && host.cpuParticles) status=host.cpuParticles->resetForPoolReuse();
    if (status==Result::Ok && host.gameIo) status=host.gameIo->resetBindings();
    if (status!=Result::Ok) { host.failure=status; return status; }
    if (host.gameIo) host.lifecycle.emplace(*host.rootInstance,*host.gameIo);
    else host.lifecycle.emplace(*host.rootInstance);
    host.eventHead=host.eventCount=host.pendingForceEventHead=0;
    host.childGameHead=host.childGameCount=0;
    host.pendingForceEvents.clear(); host.ownerNotices.clear();
    host.retiredCpuEvents.clear(); host.scheduledRoutes.clear();
    host.time=-1; host.rootAge=host.frameDelta=0;
    host.realElapsed=host.effectElapsed=host.realFrameDelta=0; host.timeScale=1;
    host.coordinateBasis={}; host.quality={};
    host.nextToken=host.nextScopeTag=1; host.currentOwnerTag=0;
    host.activeCpuParticles=nullptr;
    host.started=host.rootEnding=host.rootFinished=host.rootStopped=false;
    host.rootDestroySent=host.pendingPump=host.rootForced=false;
    host.failure=Result::Ok; host.debugGeneration=generation;
    host.executionDebugAttached=false;
    return Result::Ok;
}

struct SpawnableHostPool::Impl final {
    struct Slot final {
        std::unique_ptr<SpawnableHost> host;
        std::uint64_t generation=0;
        bool active=false, quarantined=false;
    };
    std::vector<Slot> slots;
    std::vector<std::uint32_t> free;
    std::size_t active=0, workers=1;
    std::thread::id owner=std::this_thread::get_id();
};
SpawnableHostPool::SpawnableHostPool(std::unique_ptr<Impl> impl) noexcept : impl_(std::move(impl)) {}
SpawnableHostPool::~SpawnableHostPool()=default;

Result SpawnableHostPool::create(const SpawnablePackage& package, const SpawnableHostPoolOptions& options,
    std::unique_ptr<SpawnableHostPool>& out) noexcept
{
    out.reset();
    if (!options.capacity || options.capacity>1024 || !options.preparationWorkers || options.preparationWorkers>16)
        return Result::InvalidArgument;
    if (options.registryCount && !options.registry)
    {
        return Result::InvalidArgument;
    }
    try {
        auto impl=std::make_unique<Impl>();
        impl->slots.resize(options.capacity); impl->free.reserve(options.capacity);
        auto status =
            SpawnableHost::create(package, options.host, options.registry, options.registryCount, impl->slots[0].host);
        if (status!=Result::Ok) return status;
        if (!impl->slots[0].host->supportsPoolReuse()) return Result::UnsupportedBinary;
        status = impl->slots[0].host->prepareForPoolReuse();
        if (status != Result::Ok)
        {
            return status;
        }
        const auto hardware=std::max(1u,std::thread::hardware_concurrency());
        impl->workers=std::min({options.capacity,options.preparationWorkers,static_cast<std::size_t>(hardware)});
        if (!impl->slots[0].host->supportsParallelPreparation())
        {
            impl->workers = 1;
        }
        auto cursor=std::atomic<std::size_t>{1};
        auto failure=std::atomic<Result>{Result::Ok};
        const auto prepare=[&]() noexcept {
            while (failure.load(std::memory_order_relaxed)==Result::Ok) {
                const auto index=cursor.fetch_add(1,std::memory_order_relaxed);
                if (index>=options.capacity) break;
                auto result=Result::Ok;
                try
                {
                    result = SpawnableHost::create(package, options.host, options.registry, options.registryCount,
                                                   impl->slots[index].host);
                    if (result == Result::Ok)
                    {
                        result = impl->slots[index].host->prepareForPoolReuse();
                    }
                }
                catch (const std::bad_alloc&) { result=Result::InsufficientWork; }
                catch (...) { result=Result::NotReady; }
                if (result!=Result::Ok) {
                    auto expected=Result::Ok;
                    failure.compare_exchange_strong(expected,result,std::memory_order_relaxed);
                }
            }
        };
        std::vector<std::thread> threads;
        threads.reserve(impl->workers-1);
        try {
            for (auto worker=std::size_t{1};worker<impl->workers;++worker) threads.emplace_back(prepare);
        } catch (...) {
            failure.store(Result::InsufficientWork,std::memory_order_relaxed);
            for (auto& thread : threads) thread.join();
            return Result::InsufficientWork;
        }
        prepare();
        for (auto& thread : threads) thread.join();
        if (failure.load()!=Result::Ok) return failure.load();
        for (auto index=options.capacity;index>0;--index) {
            impl->slots[index-1].host->impl_->retainForPool=true;
            impl->free.push_back(static_cast<std::uint32_t>(index-1));
        }
        out.reset(new SpawnableHostPool(std::move(impl)));
        return Result::Ok;
    } catch (const std::bad_alloc&) { return Result::InsufficientWork; }
    catch (...) { return Result::NotReady; }
}
Result SpawnableHostPool::acquire(SpawnableHostLease& lease, SpawnableHost*& host) noexcept
{
    lease={}; host=nullptr;
    if (impl_->owner!=std::this_thread::get_id()) return Result::Busy;
    if (impl_->free.empty()) return Result::InsufficientWork;
    const auto index=impl_->free.back();
    auto& slot=impl_->slots[index];
    impl_->free.pop_back(); slot.active=true; ++impl_->active;
    slot.generation=slot.host->debugGeneration();
    lease={index,slot.generation}; host=slot.host.get();
    return Result::Ok;
}
Result SpawnableHostPool::lookup(const SpawnableHostLease& lease, SpawnableHost*& host) const noexcept
{
    host=nullptr;
    if (impl_->owner!=std::this_thread::get_id()) return Result::Busy;
    if (!lease.generation || lease.slot>=impl_->slots.size()) return Result::InvalidHandle;
    const auto& slot=impl_->slots[lease.slot];
    if (!slot.active || slot.generation!=lease.generation || slot.quarantined) return Result::InvalidHandle;
    host=slot.host.get(); return Result::Ok;
}
Result SpawnableHostPool::release(const SpawnableHostLease& lease) noexcept
{
    SpawnableHost* host=nullptr;
    auto status=lookup(lease,host);
    if (status!=Result::Ok) return status;
    status=host->resetForPoolReuse();
    if (status==Result::Busy) return status;
    auto& slot=impl_->slots[lease.slot];
    slot.active=false; --impl_->active;
    if (status==Result::Ok) impl_->free.push_back(lease.slot);
    else slot.quarantined=true;
    return status;
}
SpawnableHostPoolStats SpawnableHostPool::stats() const noexcept
{
    return {impl_->slots.size(),impl_->active,impl_->free.size(),impl_->workers};
}
}
