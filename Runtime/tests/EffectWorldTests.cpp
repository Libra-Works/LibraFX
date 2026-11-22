#include "liberafx/LbEffectWorld.h"
#include "liberafx/host/LbPreparedPlayback.h"
#include <array>
#include <atomic>
#include <cstring>
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>
#include <thread>

namespace liberafx::effect_world_tests
{
void require(bool value, const char *message)
{
    if (!value)
    {
        throw std::runtime_error(message);
    }
}
void check(Result value)
{
    require(value == Result::Ok, "Unexpected effect-world status.");
}
bool reject(void *, Job, void *) noexcept
{
    return false;
}
bool inlineJob(void *, Job job, void *data) noexcept
{
    job(data);
    return true;
}
Result debug(void *, const DebugNodeVisit &) noexcept
{
    return Result::Ok;
}
struct EngineJobs
{
    std::array<std::thread, 15> jobs;
    std::size_t count = 0;
    std::atomic<std::size_t> running{0}, peak{0};
    static bool enqueue(void *context, Job job, void *data) noexcept
    {
        auto &self = *static_cast<EngineJobs *>(context);
        if (self.count == self.jobs.size())
        {
            return false;
        }
        try
        {
            self.jobs[self.count++] = std::thread(
                [&self, job, data]
                {
                    const auto running = self.running.fetch_add(1) + 1;
                    auto peak = self.peak.load();
                    while (peak < running && !self.peak.compare_exchange_weak(peak, running))
                    {
                    }
                    job(data);
                    self.running.fetch_sub(1);
                });
            return true;
        }
        catch (...)
        {
            return false;
        }
    }
    void join()
    {
        for (auto i = std::size_t{}; i < count; ++i)
        {
            if (jobs[i].joinable())
            {
                jobs[i].join();
            }
        }
        count = 0;
    }
    ~EngineJobs()
    {
        join();
    }
};
void fixture(const char *path, JobScheduler scheduler = {})
{
    auto stream = std::ifstream(path, std::ios::binary);
    require(stream.good(), "Missing effect-world fixture.");
    const auto bytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(stream), {});
    auto package = SpawnablePackage{};
    check(loadSpawnablePackage(bytes.data(), bytes.size(), package));
    require(package.cpuPrograms.size() > 24, "Expected CPU particle fixture.");
    std::array<std::uint8_t, 16> system{};
    std::copy_n(package.cpuPrograms.data() + 8, 16, system.data());
    auto options = SpawnableHostPoolOptions{};
    options.capacity = 8;
    options.preparationWorkers = 3;
    auto cache = host::PreparedPlaybackCache{};
    check(cache.prepare(bytes.data(), bytes.size(), options));
    auto compatible = host::PlaybackHost{};
    check(cache.acquireCompatible(bytes.data(), bytes.size(), options.host, compatible));
    compatible.reset();
    auto incompatibleHost = options.host;
    ++incompatibleHost.maxActivePerTemplate;
    require(cache.acquireCompatible(bytes.data(), bytes.size(), incompatibleHost, compatible) == Result::NotReady,
            "Changed child capacity acquired an incompatible prepared host.");
    auto changed = bytes;
    changed.back() ^= 1;
    require(cache.acquireCompatible(changed.data(), changed.size(), options.host, compatible) == Result::NotReady,
            "Changed asset contents reused a stale prepared package.");
    std::array<host::PlaybackHost, 8> hosts;
    std::array<std::unique_ptr<SpawnableHost>, 8> references;
    std::array<EffectFrame, 8> frames{};
    auto world = std::unique_ptr<EffectWorld>{};
    auto worldOptions = EffectWorldOptions{};
    worldOptions.maximumEffects = 8;
    worldOptions.workers = 4;
    worldOptions.minimumParallelWork = 0;
    worldOptions.scheduler = scheduler;
    check(EffectWorld::create(worldOptions, world));
    auto wrongOwner = Result::Ok;
    std::thread other([&] { wrongOwner = world->advance(nullptr, 0); });
    other.join();
    require(wrongOwner == Result::Busy, "Effect world accepted a different owner thread.");
    for (auto cycle = 0; cycle < 3; ++cycle)
    {
        for (auto index = 0u; index < hosts.size(); ++index)
        {
            check(cache.acquire(bytes.data(), bytes.size(), options, hosts[index]));
            auto referenceOptions = SpawnableHostOptions{};
            referenceOptions.cpuParticleBatching = false;
            check(SpawnableHost::create(package, referenceOptions, references[index]));
            check(hosts[index]->setTimeScale(index == 0 ? .25 : 1));
            check(references[index]->setTimeScale(index == 0 ? .25 : 1));
        }
        auto excess = host::PlaybackHost{};
        require(cache.acquire(bytes.data(), bytes.size(), options, excess) == Result::InsufficientWork,
                "Prepared cache grew on exhaustion.");
        frames[0] = {hosts[0].get(), 0, 0};
        frames[1] = frames[0];
        require(world->advance(frames.data(), 2) == Result::InvalidArgument && hosts[0]->timeSnapshot().realTime == 0,
                "Duplicate hosts were not rejected before work.");
        auto target = SpawnableDebugTarget{};
        check(hosts[1]->debugTarget({}, target));
        check(hosts[1]->setDebugHooks(target, {nullptr, debug}));
        frames[1] = {hosts[1].get(), 0, 0};
        require(world->advance(frames.data(), 2) == Result::Busy, "Debug callbacks ran on the shared workers.");
        check(hosts[1]->setDebugHooks(target, {}));
        auto time = 0.0;
        for (auto frame = 0; frame < 120; ++frame)
        {
            const auto delta = frame == 0 ? 0.0 : frame % 7 == 0 ? .1 : 1.0 / 60;
            time += delta;
            for (auto i = 0u; i < hosts.size(); ++i)
            {
                frames[i] = {hosts[i].get(), time, delta};
            }
            check(world->advance(frames.data(), frames.size()));
            if (scheduler.context)
            {
                static_cast<EngineJobs *>(scheduler.context)->join();
            }
            for (auto i = 0u; i < hosts.size(); ++i)
            {
                const auto expected = references[i]->advance(time, delta);
                require(frames[i].result == expected, "World changed host execution result.");
                check(expected);
                std::size_t left = 0, right = 0;
                check(hosts[i]->cpuParticleCount(system.data(), left));
                check(references[i]->cpuParticleCount(system.data(), right));
                require(left == right, "World changed live count.");
                for (auto p = std::size_t{}; p < left; ++p)
                {
                    CpuParticleSnapshot a{}, b{};
                    CpuParticleTiming at{}, bt{};
                    check(hosts[i]->cpuParticleAt(system.data(), p, a));
                    check(references[i]->cpuParticleAt(system.data(), p, b));
                    check(hosts[i]->cpuParticleTiming({}, system.data(), p, at));
                    check(references[i]->cpuParticleTiming({}, system.data(), p, bt));
                    require(std::memcmp(&a, &b, sizeof(a)) == 0 && std::memcmp(&at, &bt, sizeof(at)) == 0,
                            "World/SoA changed particle state or order.");
                }
            }
            if (frame == 75)
            {
                for (auto i = 0u; i < hosts.size(); ++i)
                {
                    check(hosts[i]->requestNaturalEnd());
                    check(references[i]->requestNaturalEnd());
                }
            }
        }
        check(hosts[0]->forceTerminate({}));
        for (auto &value : hosts)
        {
            value.reset();
        }
    }
    // Cache eviction must not destroy storage held by an outstanding playback.
    check(cache.acquire(bytes.data(), bytes.size(), options, hosts[0]));
    cache.clear();
    check(hosts[0]->advance(0, 0));
    hosts[0].reset();
    require(world->stats().workers <= 4 && world->stats().lastEffects == 8, "World worker bound/stats changed.");
}
} // namespace liberafx::effect_world_tests
int main(int argc, char **argv)
{
    using namespace liberafx;
    using namespace effect_world_tests;
    try
    {
        auto world = std::unique_ptr<EffectWorld>{};
        auto invalid = EffectWorldOptions{};
        invalid.workers = 0;
        require(EffectWorld::create(invalid, world) == Result::InvalidArgument, "Zero worker bound accepted.");
        check(EffectWorld::create({}, world));
        check(world->advance(nullptr, 0));
        if (argc < 2)
        {
            std::cout << "Effect world option/empty-frame checks passed; compiler fixtures not supplied.\n";
            return 0;
        }
        for (auto i = 1; i < argc; ++i)
        {
            fixture(argv[i]);
            fixture(argv[i], {reject, nullptr});
            fixture(argv[i], {inlineJob, nullptr});
            auto engine = EngineJobs{};
            fixture(argv[i], {EngineJobs::enqueue, &engine});
            require(engine.peak.load() <= 3, "Shared executor exceeded its external worker allowance.");
            require(engine.peak.load() > 0 || std::thread::hardware_concurrency() <= 1,
                    "External scheduler jobs were never exercised.");
        }
        std::cout << "Shared effects: pool reuse, SoA/interpreter parity, owner/debug/duplicate guards and scheduler "
                     "rejection passed.\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
