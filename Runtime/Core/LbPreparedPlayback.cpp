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
    };
    std::vector<Entry> entries;
    std::thread::id owner = std::this_thread::get_id();
    static bool matches(const Entry &entry, const std::uint8_t *bytes, std::size_t size,
                        const SpawnableHostPoolOptions &options) noexcept
    {
        return entry.bytes.size() == size && std::equal(entry.bytes.begin(), entry.bytes.end(), bytes) &&
               entry.options.capacity == options.capacity &&
               entry.options.preparationWorkers == options.preparationWorkers &&
               entry.options.host.eventCapacity == options.host.eventCapacity &&
               entry.options.host.instructionBudget == options.host.instructionBudget &&
               entry.options.host.cpuParticleBatching == options.host.cpuParticleBatching;
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
                                                const SpawnableHostOptions &host, PlaybackHost &out) noexcept
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
        auto requested = entry.options;
        requested.host = host;
        if (!Impl::matches(entry, bytes, size, requested))
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
