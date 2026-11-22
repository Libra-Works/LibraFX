#include "LbInteropInternal.h"
#include "liberafx/LbEffectWorld.h"

namespace liberafx::interop_detail
{
struct NativeEffectWorld final
{
    std::thread::id owner = std::this_thread::get_id();
    std::unique_ptr<liberafx::EffectWorld> world;
    std::vector<liberafx::EffectFrame> frames;
};
static std::unordered_map<LibraEffectWorldHandle, NativeEffectWorld> effectWorlds;
} // namespace liberafx::interop_detail
using namespace liberafx::interop_detail;
static_assert(sizeof(LibraEffectFrame) == 32 && offsetof(LibraEffectFrame, result) == 24);
int32_t LIBRA_INTEROP_CALL libra_effect_world_create(uint32_t maximumEffects, uint32_t workers,
                                                     LibraEffectWorldHandle *outWorld)
{
    if (!outWorld)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    *outWorld = 0;
    return guarded(
        [&]() -> LibraResult
        {
            auto owner = NativeEffectWorld{};
            auto options = liberafx::EffectWorldOptions{};
            options.maximumEffects = maximumEffects;
            options.workers = workers;
            const auto result = liberafx::EffectWorld::create(options, owner.world);
            if (result != liberafx::Result::Ok)
            {
                return toLibraResult(result);
            }
            if (!nextHandle)
            {
                return LIBRA_INSUFFICIENT_WORK;
            }
            owner.frames.resize(maximumEffects);
            const auto handle = nextHandle++;
            effectWorlds.emplace(handle, std::move(owner));
            *outWorld = handle;
            return LIBRA_OK;
        });
}
int32_t LIBRA_INTEROP_CALL libra_spawnable_host_worker_safe(uint64_t handle, uint32_t *outSafe)
{
    if (!outSafe)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    *outSafe = 0;
    return guardedSpawnable(
        handle,
        [&]() -> LibraResult
        {
            *outSafe = findSpawnableHost(handle)->supportsWorkerExecution() ? 1u : 0u;
            return LIBRA_OK;
        },
        true);
}
int32_t LIBRA_INTEROP_CALL libra_spawnable_host_execution_capabilities(uint64_t handle, uint32_t *flags)
{
    if (!flags)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    *flags = 0;
    return guardedSpawnable(handle,
                            [&]() -> LibraResult
                            {
                                const auto *host = findSpawnableHost(handle);
                                *flags = (host->supportsSharedExecution() ? 1u : 0u) |
                                         (host->supportsWorkerExecution() ? 2u : 0u) |
                                         (host->supportsPoolReuse() ? 4u : 0u) |
                                         (host->supportsParallelPreparation() ? 8u : 0u);
                                return LIBRA_OK;
                            });
}
int32_t LIBRA_INTEROP_CALL libra_effect_world_advance(LibraEffectWorldHandle handle, LibraEffectFrame *frames,
                                                      uint32_t count)
{
    if (!frames && count)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    return guarded(
        [&]() -> LibraResult
        {
            const auto found = effectWorlds.find(handle);
            if (found == effectWorlds.end())
            {
                return LIBRA_INVALID_HANDLE;
            }
            auto &owner = found->second;
            if (owner.owner != std::this_thread::get_id())
            {
                return LIBRA_BUSY;
            }
            if (count > owner.frames.size())
            {
                return LIBRA_INSUFFICIENT_WORK;
            }
            for (auto index = 0u; index < count; ++index)
            {
                auto *host = static_cast<liberafx::SpawnableHost *>(nullptr);
                const auto result = ownedSpawnableHost(frames[index].host, host);
                if (result != LIBRA_OK)
                {
                    return result;
                }
                auto &wrapper = spawnableHosts.at(frames[index].host);
#if defined(_WIN32)
                if (wrapper.debug)
                {
                    return LIBRA_BUSY;
                }
#else
                (void)wrapper;
#endif
                if (frames[index].reserved)
                {
                    return LIBRA_INVALID_ARGUMENT;
                }
                owner.frames[index] = {host, frames[index].time, frames[index].delta};
            }
            const auto result = owner.world->advance(owner.frames.data(), count);
            if (result != liberafx::Result::Ok)
            {
                return toLibraResult(result);
            }
            for (auto index = 0u; index < count; ++index)
            {
                frames[index].result = toLibraResult(owner.frames[index].result);
            }
            return LIBRA_OK;
        });
}
int32_t LIBRA_INTEROP_CALL libra_effect_world_release(LibraEffectWorldHandle handle)
{
    return guarded(
        [&]() -> LibraResult
        {
            const auto found = effectWorlds.find(handle);
            if (found == effectWorlds.end())
            {
                return LIBRA_INVALID_HANDLE;
            }
            if (found->second.owner != std::this_thread::get_id())
            {
                return LIBRA_BUSY;
            }
            effectWorlds.erase(found);
            return LIBRA_OK;
        });
}
