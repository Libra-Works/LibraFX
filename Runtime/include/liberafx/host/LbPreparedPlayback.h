#pragma once
#include "liberafx/LbSpawnableHostPool.h"

namespace liberafx::host
{
struct PreparedHostDeleter
{
    std::shared_ptr<SpawnableHostPool> pool;
    SpawnableHostLease lease{};
    void operator()(SpawnableHost *value) const noexcept
    {
        if (pool)
        {
            pool->release(lease);
        }
        else
        {
            delete value;
        }
    }
};
using PlaybackHost = std::unique_ptr<SpawnableHost, PreparedHostDeleter>;

// Engine-world-owned, bounded loading cache. Compare the actual package bytes,
// not a filename, asset address or collision-prone hash. Changed assets get a
// new preparation; existing leases retain the matching old pool until returned.
class LIBRA_RUNTIME_CORE_API PreparedPlaybackCache final
{
    struct Impl;
    std::unique_ptr<Impl> impl_;

  public:
    PreparedPlaybackCache();
    ~PreparedPlaybackCache();
    PreparedPlaybackCache(const PreparedPlaybackCache &) = delete;
    PreparedPlaybackCache &operator=(const PreparedPlaybackCache &) = delete;
    Result prepare(const std::uint8_t *, std::size_t, const SpawnableHostPoolOptions &) noexcept;
    Result acquire(const std::uint8_t *, std::size_t, const SpawnableHostPoolOptions &, PlaybackHost &out) noexcept;
    // Capacity/preparation workers describe reservation, not playback semantics.
    // An engine loader may prepare once for subsequently created players.
    Result acquireCompatible(const std::uint8_t *, std::size_t, const SpawnableHostOptions &, PlaybackHost &out,
                             const CustomNodeDescriptor *registry = nullptr, std::size_t registryCount = 0) noexcept;
    void clear() noexcept;
};
} // namespace liberafx::host
