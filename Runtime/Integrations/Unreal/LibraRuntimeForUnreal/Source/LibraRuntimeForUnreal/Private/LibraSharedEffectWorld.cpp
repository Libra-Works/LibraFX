#include "LibraSharedEffectWorld.h"
#include "Engine/World.h"
#include "LibraSpawnableComponent.h"
#include "Tasks/Task.h"
#include "liberafx/LbEffectWorld.h"
#include <array>

namespace LibraSharedEffects
{
struct FPending
{
    TWeakObjectPtr<ULibraSpawnableComponent> Component;
    liberafx::SpawnableHost *Host = nullptr;
    uint64 Generation = 0;
    double Time = 0, Delta = 0;
};
static bool Enqueue(void *, liberafx::Job job, void *data) noexcept;
struct FState
{
    liberafx::host::PreparedPlaybackCache Prepared;
    std::unique_ptr<liberafx::EffectWorld> Executor;
    TArray<FPending> Pending, Ready;
    std::array<liberafx::EffectFrame, 1024> Frames{};
    std::array<std::size_t, 1024> Indices{};
    FState()
    {
        Pending.Reserve(1024);
        Ready.Reserve(1024);
        auto options = liberafx::EffectWorldOptions{};
        options.scheduler = {Enqueue, nullptr};
        liberafx::EffectWorld::create(options, Executor);
    }
};
// The engine owns the workers. Core bounds submitted consumer tasks for the
// complete world and joins them before any UObject/render/event operation.
static bool Enqueue(void *, liberafx::Job job, void *data) noexcept
{
    UE::Tasks::Launch(UE_SOURCE_LOCATION, [job, data] { job(data); });
    return true;
}
class FWorlds
{
    TMap<TWeakObjectPtr<UWorld>, TSharedPtr<FState>> States;
    FDelegateHandle TickHandle, CleanupHandle;

  public:
    FWorlds()
    {
        TickHandle = FWorldDelegates::OnWorldPostActorTick.AddRaw(this, &FWorlds::Tick);
        CleanupHandle = FWorldDelegates::OnWorldCleanup.AddRaw(this, &FWorlds::Cleanup);
    }
    ~FWorlds()
    {
        FWorldDelegates::OnWorldPostActorTick.Remove(TickHandle);
        FWorldDelegates::OnWorldCleanup.Remove(CleanupHandle);
    }
    FState *Find(UWorld *world)
    {
        auto *found = States.Find(world);
        return found ? found->Get() : nullptr;
    }
    FState &Get(UWorld *world)
    {
        auto &value = States.FindOrAdd(world);
        if (!value)
        {
            value = MakeShared<FState>();
        }
        return *value;
    }
    void Cleanup(UWorld *world, bool, bool)
    {
        States.Remove(world);
    }
    void Tick(UWorld *world, ELevelTick, float)
    {
        auto lifetime = States.FindRef(world);
        auto *state = lifetime.Get();
        if (!state || state->Pending.IsEmpty())
        {
            return;
        }
        Swap(state->Pending, state->Ready);
        auto count = std::size_t{};
        for (auto i = 0; i < state->Ready.Num(); ++i)
        {
            const auto &next = state->Ready[i];
            state->Indices[i] = SIZE_MAX;
            auto *component = next.Component.Get();
            if (component && component->SharedFrameIsCurrent(next.Host, next.Generation))
            {
                state->Indices[i] = count;
                state->Frames[count++] = {next.Host, next.Time, next.Delta};
            }
        }
        const auto status = state->Executor->advance(state->Frames.data(), count);
        for (auto i = 0; i < state->Ready.Num(); ++i)
        {
            const auto next = state->Ready[i];
            const auto index = state->Indices[i];
            auto *component = next.Component.Get();
            if (index < count && component && component->SharedFrameIsCurrent(next.Host, next.Generation))
            {
                const auto result = status == liberafx::Result::Ok ? state->Frames[index].result
                                                                   : next.Host->advance(next.Time, next.Delta);
                component->CompleteSharedFrame(next.Host, next.Generation, next.Delta, static_cast<uint8>(result),
                                               static_cast<int32>(count));
            }
        }
        state->Ready.Reset();
    }
};
static FWorlds &Worlds()
{
    static FWorlds value;
    return value;
}
liberafx::Result Prepare(UWorld *world, const uint8 *bytes, std::size_t size,
                         const liberafx::SpawnableHostPoolOptions &options)
{
    if (!IsInGameThread() || !world || !world->IsGameWorld())
    {
        return liberafx::Result::NotReady;
    }
    return Worlds().Get(world).Prepared.prepare(bytes, size, options);
}
liberafx::Result Acquire(UWorld *world, const uint8 *bytes, std::size_t size,
                         const liberafx::SpawnableHostPoolOptions &options, liberafx::host::PlaybackHost &out)
{
    if (!IsInGameThread() || !world || !world->IsGameWorld())
    {
        return liberafx::Result::NotReady;
    }
    auto *state = Worlds().Find(world);
    return state ? state->Prepared.acquire(bytes, size, options, out) : liberafx::Result::NotReady;
}
bool Queue(ULibraSpawnableComponent &component, liberafx::SpawnableHost &host, uint64 generation, double time,
           double delta)
{
    auto *world = component.GetWorld();
    if (!world || !world->IsGameWorld() || !host.supportsSharedExecution())
    {
        return false;
    }
    auto &state = Worlds().Get(world);
    if (state.Pending.Num() == 1024)
    {
        return false;
    }
    for (const auto &entry : state.Pending)
    {
        if (entry.Host == &host)
        {
            return false;
        }
    }
    if (!state.Executor)
    {
        auto options = liberafx::EffectWorldOptions{};
        options.scheduler = {Enqueue, nullptr};
        if (liberafx::EffectWorld::create(options, state.Executor) != liberafx::Result::Ok)
        {
            return false;
        }
    }
    state.Pending.Add({&component, &host, generation, time, delta});
    return true;
}
} // namespace LibraSharedEffects
