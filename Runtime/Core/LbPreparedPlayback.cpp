#include "liberafx/host/LbPreparedPlayback.h"
#include <algorithm>
#include <thread>
#include <vector>

namespace liberafx::host
{
struct PreparedPlaybackCache::Impl final
{
    struct Entry final
    {
        std::vector<std::uint8_t> bytes;
        SpawnableHostPoolOptions options;
        std::shared_ptr<SpawnableHostPool> pool;
        std::vector<CustomNodeDescriptor> registry;
    };
    std::vector<Entry> entries;
    std::thread::id owner = std::this_thread::get_id();
    static bool sameHost(const SpawnableHostOptions &a, const SpawnableHostOptions &b) noexcept
    {
        if (a.eventCapacity != b.eventCapacity || a.instructionBudget != b.instructionBudget ||
            a.cpuParticleBatching != b.cpuParticleBatching || a.maxActivePerTemplate != b.maxActivePerTemplate ||
            a.memoryBudgetBytesPerTemplate != b.memoryBudgetBytesPerTemplate ||
            a.customNodePolicies.size() != b.customNodePolicies.size())
        {
            return false;
        }
        for (auto i = std::size_t{}; i < a.customNodePolicies.size(); ++i)
        {
            const auto &x = a.customNodePolicies[i];
            const auto &y = b.customNodePolicies[i];
            if (!sameCustomNodeDescriptor(x.descriptor, y.descriptor) || x.reusable != y.reusable ||
                x.workerSafe != y.workerSafe || x.parallelPreparation != y.parallelPreparation)
            {
                return false;
            }
        }
        return true;
    }
    static bool sameRegistry(const Entry &entry, const CustomNodeDescriptor *registry, std::size_t count) noexcept
    {
        if (entry.registry.size() != count || (count && !registry))
        {
            return false;
        }
        for (auto i = std::size_t{}; i < count; ++i)
        {
            if (!sameCustomNodeDescriptor(entry.registry[i], registry[i]))
            {
                return false;
            }
        }
        return true;
    }
    static bool matches(const Entry &entry, const std::uint8_t *bytes, std::size_t size,
                        const SpawnableHostPoolOptions &options) noexcept
    {
        return entry.bytes.size() == size && std::equal(entry.bytes.begin(), entry.bytes.end(), bytes) &&
               entry.options.capacity == options.capacity &&
               entry.options.preparationWorkers == options.preparationWorkers &&
               sameHost(entry.options.host, options.host) &&
               sameRegistry(entry, options.registry, options.registryCount);
    }
};
PreparedPlaybackCache::PreparedPlaybackCache() : impl_(std::make_unique<Impl>())
{
}
PreparedPlaybackCache::~PreparedPlaybackCache() = default;
Result PreparedPlaybackCache::prepare(const std::uint8_t *bytes, std::size_t size,
                                      const SpawnableHostPoolOptions &options) noexcept
{
    if (impl_->owner != std::this_thread::get_id())
    {
        return Result::Busy;
    }
    if (!bytes || !size)
    {
        return Result::InvalidArgument;
    }
    for (const auto &entry : impl_->entries)
    {
        if (Impl::matches(entry, bytes, size, options))
        {
            return Result::Ok;
        }
    }
    if (impl_->entries.size() >= 64)
    {
        return Result::InsufficientWork;
    }
    try
    {
        auto package = SpawnablePackage{};
        auto result = loadSpawnablePackage(bytes, size, package);
        if (result != Result::Ok)
        {
            return result;
        }
        auto pool = std::unique_ptr<SpawnableHostPool>{};
        result = SpawnableHostPool::create(package, options, pool);
        if (result != Result::Ok)
        {
            return result;
        }
        auto next = Impl::Entry{};
        next.bytes.assign(bytes, bytes + size);
        next.options = options;
        if (options.registryCount)
        {
            next.registry.assign(options.registry, options.registry + options.registryCount);
        }
        next.options.registry = next.registry.data();
        next.pool = std::move(pool);
        impl_->entries.push_back(std::move(next));
        return Result::Ok;
    }
    catch (...)
    {
        return Result::InsufficientWork;
    }
}
Result PreparedPlaybackCache::acquire(const std::uint8_t *bytes, std::size_t size,
                                      const SpawnableHostPoolOptions &options, PlaybackHost &out) noexcept
{
    if (impl_->owner != std::this_thread::get_id())
    {
        return Result::Busy;
    }
    if (!bytes || !size || out)
    {
        return Result::InvalidArgument;
    }
    for (const auto &entry : impl_->entries)
    {
        if (!Impl::matches(entry, bytes, size, options))
        {
            continue;
        }
        auto lease = SpawnableHostLease{};
        SpawnableHost *value = nullptr;
        const auto result = entry.pool->acquire(lease, value);
        if (result == Result::Ok)
        {
            out = PlaybackHost(value, PreparedHostDeleter{entry.pool, lease});
        }
        return result;
    }
    return Result::NotReady;
}
void PreparedPlaybackCache::clear() noexcept
{
    if (impl_->owner == std::this_thread::get_id())
    {
        impl_->entries.clear();
    }
}
Result PreparedPlaybackCache::acquireCompatible(const std::uint8_t *bytes, std::size_t size,
                                                const SpawnableHostOptions &host, PlaybackHost &out,
                                                const CustomNodeDescriptor *registry,
                                                std::size_t registryCount) noexcept
{
    if (impl_->owner != std::this_thread::get_id())
    {
        return Result::Busy;
    }
    if (!bytes || !size || out)
    {
        return Result::InvalidArgument;
    }
    auto matched = false;
    for (const auto &entry : impl_->entries)
    {
        if (entry.bytes.size() != size || !std::equal(entry.bytes.begin(), entry.bytes.end(), bytes) ||
            !Impl::sameHost(entry.options.host, host) || !Impl::sameRegistry(entry, registry, registryCount))
        {
            continue;
        }
        matched = true;
        SpawnableHost *value = nullptr;
        auto lease = SpawnableHostLease{};
        const auto result = entry.pool->acquire(lease, value);
        if (result == Result::InsufficientWork)
        {
            continue;
        }
        if (result == Result::Ok)
        {
            out = PlaybackHost(value, PreparedHostDeleter{entry.pool, lease});
        }
        return result;
    }
    return matched ? Result::InsufficientWork : Result::NotReady;
}
} // namespace liberafx::host
