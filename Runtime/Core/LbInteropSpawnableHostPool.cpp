#include "LbInteropInternal.h"
#include "liberafx/host/LbProjectNodes.h"

namespace liberafx::interop_detail {
using HostMap=std::unordered_map<LibraSpawnableHostHandle,NativeSpawnableHost>;
struct NativeSpawnableHostPool final {
    std::thread::id owner=std::this_thread::get_id();
    std::unique_ptr<liberafx::SpawnableHostPool> pool;
    std::vector<HostMap::node_type> nodes;
    std::uint64_t packageIdentity=0;
};
static std::unordered_map<LibraSpawnableHostPoolHandle,std::shared_ptr<NativeSpawnableHostPool>> hostPools;
static std::size_t reservedPoolCapacity=0;
void reserveSpawnableHostHandles(std::size_t additionalPoolCapacity) {
    if (additionalPoolCapacity>spawnableHosts.max_size()-reservedPoolCapacity ||
        spawnableHosts.size()>=spawnableHosts.max_size()-reservedPoolCapacity-additionalPoolCapacity)
        throw std::bad_alloc{};
    const auto required=spawnableHosts.size()+reservedPoolCapacity+additionalPoolCapacity+1;
    if (static_cast<double>(required)>static_cast<double>(spawnableHosts.bucket_count())*spawnableHosts.max_load_factor())
        spawnableHosts.reserve(required);
}
auto releasePooledSpawnableHost(LibraSpawnableHostHandle handle) -> LibraResult {
    auto found=spawnableHosts.find(handle);
    if (found==spawnableHosts.end() || !found->second.poolOwner) return LIBRA_INVALID_HANDLE;
    auto& owner=found->second;
#if defined(_WIN32)
    if (owner.debug && owner.debug->job.pending()) return LIBRA_BUSY;
    owner.debug.reset();
#endif
    auto pool=owner.poolOwner;
    const auto lease=owner.lease;
    const auto result=pool->pool->release(lease);
    if (result==liberafx::Result::Busy) return LIBRA_BUSY;
    owner.pooledHost=nullptr; owner.poolOwner=nullptr; owner.lease={};
    pool->nodes[lease.slot]=spawnableHosts.extract(found);
    return toLibraResult(result);
}
}
using namespace liberafx::interop_detail;
LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_pool_create(const uint8_t* bytes,uint64_t byteCount,
    const LibraSpawnableHostPoolOptions* configuration,LibraSpawnableHostPoolHandle* outPool) {
    if (!outPool) return LIBRA_INVALID_ARGUMENT;
    *outPool=0;
    if (!bytes || !byteCount || byteCount>std::numeric_limits<std::size_t>::max()) return LIBRA_INVALID_ARGUMENT;
    return guarded(
        [&]() -> LibraResult
        {
            const auto registry = liberafx::host::projectNodes();
            if (registry.policyCount && !registry.policies)
            {
                return LIBRA_INVALID_ARGUMENT;
            }
            auto package = liberafx::SpawnablePackage{};
            auto result = liberafx::loadSpawnablePackage(bytes, static_cast<std::size_t>(byteCount), package);
            if (result != liberafx::Result::Ok)
                return toLibraResult(result);
            auto options = liberafx::SpawnableHostPoolOptions{};
            options.registry = registry.descriptors;
            options.registryCount = registry.count;
            if (registry.policyCount)
            {
                options.host.customNodePolicies.assign(registry.policies, registry.policies + registry.policyCount);
            }
            if (configuration)
            {
                options.capacity = configuration->capacity;
                options.preparationWorkers = configuration->preparationWorkers;
                options.host.eventCapacity = configuration->eventCapacity;
                options.host.instructionBudget = configuration->instructionBudget;
            }
            auto owner = std::make_shared<NativeSpawnableHostPool>();
            result = liberafx::SpawnableHostPool::create(package, options, owner->pool);
            if (result != liberafx::Result::Ok)
                return toLibraResult(result);
            if (!nextHandle)
                return LIBRA_INSUFFICIENT_WORK;
            owner->nodes.resize(options.capacity);
            // Map nodes and buckets are prepared too: acquire rekeys an extracted
            // node instead of allocating a new handle wrapper during playback.
            HostMap spare;
            for (auto index = 0u; index < options.capacity; ++index)
            {
                spare.emplace(0, NativeSpawnableHost{});
                owner->nodes[index] = spare.extract(0);
            }
            reserveSpawnableHostHandles(options.capacity);
#if defined(_WIN32)
            owner->packageIdentity =
                liberafx::host::ExecutionDebugProtocol::programIdentity(bytes, static_cast<std::size_t>(byteCount));
#endif
            const auto handle = nextHandle++;
            hostPools.emplace(handle, std::move(owner));
            reservedPoolCapacity += options.capacity;
            *outPool = handle;
            return LIBRA_OK;
        });
}
LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_pool_acquire(LibraSpawnableHostPoolHandle poolHandle,
    LibraSpawnableHostHandle* outHost) {
    if (!outHost) return LIBRA_INVALID_ARGUMENT;
    *outHost=0;
    return guarded([&]() -> LibraResult {
        const auto found=hostPools.find(poolHandle);
        if (found==hostPools.end()) return LIBRA_INVALID_HANDLE;
        auto& owner=*found->second;
        if (owner.owner!=std::this_thread::get_id()) return LIBRA_BUSY;
        if (!nextHandle) return LIBRA_INSUFFICIENT_WORK;
        if (static_cast<double>(spawnableHosts.size()+1)>
            static_cast<double>(spawnableHosts.bucket_count())*spawnableHosts.max_load_factor())
            return LIBRA_INSUFFICIENT_WORK; // Never grow handle buckets on acquire.
        auto lease=liberafx::SpawnableHostLease{}; liberafx::SpawnableHost* host=nullptr;
        const auto status=owner.pool->acquire(lease,host);
        if (status!=liberafx::Result::Ok) return toLibraResult(status);
        auto& node=owner.nodes[lease.slot];
        const auto handle=nextHandle++;
        node.key()=handle;
        auto& wrapper=node.mapped();
        wrapper.ownerThread=owner.owner; wrapper.pooledHost=host; wrapper.poolOwner=found->second;
        wrapper.lease=lease; wrapper.packageIdentity=owner.packageIdentity;
        spawnableHosts.insert(std::move(node));
        *outHost=handle;
        return LIBRA_OK;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_pool_stats(LibraSpawnableHostPoolHandle handle,
    LibraSpawnableHostPoolStats* outStats) {
    if (!outStats) return LIBRA_INVALID_ARGUMENT;
    *outStats={};
    return guarded([&]() -> LibraResult {
        const auto found=hostPools.find(handle);
        if (found==hostPools.end()) return LIBRA_INVALID_HANDLE;
        if (found->second->owner!=std::this_thread::get_id()) return LIBRA_BUSY;
        const auto stats=found->second->pool->stats();
        *outStats={static_cast<uint32_t>(stats.capacity),static_cast<uint32_t>(stats.active),
            static_cast<uint32_t>(stats.available),static_cast<uint32_t>(stats.preparationWorkers)};
        return LIBRA_OK;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_pool_release(LibraSpawnableHostPoolHandle handle) {
    return guarded([&]() -> LibraResult {
        const auto found=hostPools.find(handle);
        if (found==hostPools.end()) return LIBRA_INVALID_HANDLE;
        if (found->second->owner!=std::this_thread::get_id()) return LIBRA_BUSY;
        if (found->second->pool->stats().active) return LIBRA_BUSY;
        reservedPoolCapacity-=found->second->nodes.size();
        hostPools.erase(found); return LIBRA_OK;
    });
}
