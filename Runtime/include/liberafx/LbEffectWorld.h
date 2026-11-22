#pragma once
#include "liberafx/LbSpawnableHost.h"
#include "liberafx/platform/LbJobScheduler.h"

namespace liberafx
{
struct EffectWorldOptions final
{
    std::size_t maximumEffects = 1024;
    // Total executing lanes, INCLUDING the caller. Never one pool per effect.
    std::size_t workers = 4;
    // Zero forces jobs for scheduler acceptance tests. Otherwise avoid wake/join
    // overhead on small collections using this conservative work estimate.
    std::size_t minimumParallelWork = 2048;
    // Optional engine-owned scheduler. Accepted jobs are joined before advance
    // returns; enqueue rejection is executed by the caller, never dropped.
    JobScheduler scheduler{};
};
struct EffectFrame final
{
    SpawnableHost *host = nullptr;
    double time = 0, delta = 0;
    Result result = Result::NotReady;
};
struct EffectWorldStats final
{
    std::size_t maximumEffects = 0, workers = 0, lastEffects = 0, lastExecutingLanes = 0;
};
// One bounded simulation executor shared by all instances in an engine world.
// Gather inputs first on the engine thread, advance, then publish render data.
// Synchronous join is the publication/Stop boundary; no worker survives a frame.
// Independent hosts run on workers only when all used callbacks opt in. Unknown
// or owner-affine callbacks execute on the owner after joining the worker phase.
// Debug-hooked/busy hosts are rejected before any entry executes.
class LIBRA_RUNTIME_CORE_API EffectWorld final
{
  public:
    static Result create(const EffectWorldOptions &, std::unique_ptr<EffectWorld> &out) noexcept;
    ~EffectWorld();
    EffectWorld(const EffectWorld &) = delete;
    EffectWorld &operator=(const EffectWorld &) = delete;
    // Preflight all entries before executing any; reject duplicates and overflow.
    // Individual execution failures are reported in each frame's result.
    Result advance(EffectFrame *frames, std::size_t count) noexcept;
    EffectWorldStats stats() const noexcept;

  private:
    struct Impl;
    explicit EffectWorld(std::unique_ptr<Impl>) noexcept;
    std::unique_ptr<Impl> impl_;
};
} // namespace liberafx
