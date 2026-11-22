#include "liberafx/LbEffectWorld.h"
#include "liberafx/LbSpawnableHostPool.h"
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstring>
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>
#include <thread>
#include <vector>

namespace liberafx::prepared_execution_tests
{
void require(bool value, const char *message)
{
    if (!value)
    {
        throw std::runtime_error(message);
    }
}
void check(Result result)
{
    if (result != Result::Ok)
    {
        throw std::runtime_error("Prepared execution status: " + std::to_string(static_cast<int>(result)));
    }
}
SpawnablePackage basic()
{
    auto package = SpawnablePackage{};
    package.parentBytecode = {'L', 'F', 'X', 'B', 2, 0, 1, 0, 1, 0, 0, 0, 96, 0, 0, 0};
    package.parentBytecode.resize(112);
    auto *record = package.parentBytecode.data() + 16;
    record[15] = 1;
    record[16] = 1;
    record[17] = 1;
    std::fill_n(record + 24, 8, 0xff);
    return package;
}
SpawnablePackage custom(const CustomNodeDescriptor &descriptor)
{
    auto package = SpawnablePackage{};
    auto &bytes = package.parentBytecode;
    bytes.resize(272);
    std::copy_n("LFXB", 4, bytes.data());
    bytes[4] = 3;
    bytes[6] = 1;
    bytes[8] = 1;
    bytes[13] = 1;
    auto *record = bytes.data() + 16;
    record[15] = 1;
    record[16] = 40;
    record[17] = 1;
    std::copy_n(descriptor.typeId, 16, record + 24);
    record[40] = 1;
    for (auto slot = 0u; slot < 4; ++slot)
    {
        std::fill_n(record + 48 + slot * 8, 4, 0xff);
        std::fill_n(record + 208 + slot * 4, 4, 0xff);
    }
    return package;
}
struct Callbacks
{
    std::thread::id owner = std::this_thread::get_id();
    std::atomic<unsigned> initialized{0}, cleaned{0}, offOwnerInit{0}, offOwnerRun{0}, runs{0};
    std::atomic<unsigned> *runningWorkers = nullptr;
    std::atomic<bool> invalid{false};
    bool ownerOnly = false;
};
CustomNodeDescriptor descriptor(Callbacks &context)
{
    auto result = CustomNodeDescriptor{};
    result.typeId[15] = 0xf2;
    result.abiVersion = 1;
    result.outputWidth = 1;
    result.stateBytes = sizeof(double);
    result.context = &context;
    result.initialize = [](void *state, std::size_t, void *data) noexcept -> Result
    {
        auto &self = *static_cast<Callbacks *>(data);
        ++self.initialized;
        if (std::this_thread::get_id() != self.owner)
        {
            ++self.offOwnerInit;
        }
        *static_cast<double *>(state) = 0;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
        return Result::Ok;
    };
    result.cleanup = [](void *, std::size_t, void *data) noexcept
    {
        auto &self = *static_cast<Callbacks *>(data);
        ++self.cleaned;
        if (self.ownerOnly && (std::this_thread::get_id() != self.owner || self.runningWorkers->load() != 0))
        {
            self.invalid = true;
        }
    };
    result.function = [](CustomNodeInvocation &call) noexcept -> Result
    {
        auto &self = *static_cast<Callbacks *>(call.context);
        ++self.runs;
        if (std::this_thread::get_id() != self.owner)
        {
            ++self.offOwnerRun;
        }
        if (self.ownerOnly)
        {
            if (std::this_thread::get_id() != self.owner || self.runningWorkers->load() != 0)
            {
                self.invalid = true;
            }
        }
        else
        {
            ++*self.runningWorkers;
        }
        call.outputs[0] = ++*static_cast<double *>(call.state);
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
        if (!self.ownerOnly)
        {
            --*self.runningWorkers;
        }
        return Result::Ok;
    };
    return result;
}
void callbackPolicies()
{
    auto running = std::atomic<unsigned>{0};
    auto owner = Callbacks{};
    owner.ownerOnly = true;
    owner.runningWorkers = &running;
    auto worker = Callbacks{};
    worker.runningWorkers = &running;
    auto d = descriptor(owner), w = descriptor(worker);
    auto ordinary = std::unique_ptr<SpawnableHost>{};
    check(SpawnableHost::create(custom(d), {}, &d, 1, ordinary));
    require(ordinary->supportsSharedExecution() && !ordinary->supportsWorkerExecution() &&
                !ordinary->supportsPoolReuse() && !ordinary->supportsParallelPreparation(),
            "Undeclared callback acquired permissions.");
    ordinary.reset();
    auto independent = SpawnableHostOptions{};
    independent.customNodePolicies = {{w, false, true, false}};
    check(SpawnableHost::create(custom(w), independent, &w, 1, ordinary));
    require(ordinary->supportsWorkerExecution() && !ordinary->supportsPoolReuse() &&
                !ordinary->supportsParallelPreparation(),
            "Worker safety implicitly granted reuse or parallel initialization.");
    ordinary.reset();
    auto options = SpawnableHostPoolOptions{};
    options.capacity = 8;
    options.preparationWorkers = 3;
    options.registry = &d;
    options.registryCount = 1;
    auto ownerPool = std::unique_ptr<SpawnableHostPool>{}, workerPool = std::unique_ptr<SpawnableHostPool>{};
    require(SpawnableHostPool::create(custom(d), options, ownerPool) == Result::UnsupportedBinary,
            "Undeclared callback was reused.");
    // An unused registry entry must not disable builtin-only packages.
    check(SpawnableHostPool::create(basic(), options, ownerPool));
    ownerPool.reset();
    options.host.customNodePolicies = {{d, true, false, false}};
    // A context mismatch cannot grant permissions to a different callback instance.
    options.host.customNodePolicies[0].descriptor.context = &worker;
    require(SpawnableHostPool::create(custom(d), options, ownerPool) == Result::UnsupportedBinary,
            "Mismatched policy granted reuse.");
    options.host.customNodePolicies[0].descriptor = d;
    check(SpawnableHostPool::create(custom(d), options, ownerPool));
    require(ownerPool->stats().preparationWorkers == 1 && owner.offOwnerInit == 0,
            "Owner-affine initialization escaped its thread.");
    options.registry = &w;
    options.host.customNodePolicies = {{w, true, true, true}};
    check(SpawnableHostPool::create(custom(w), options, workerPool));
    require(workerPool->stats().preparationWorkers <= 3, "Preparation exceeded worker allowance.");
    if (std::thread::hardware_concurrency() > 1)
    {
        require(worker.offOwnerInit > 0, "Parallel initialization was not exercised.");
    }
    auto worldOptions = EffectWorldOptions{};
    worldOptions.maximumEffects = 16;
    worldOptions.minimumParallelWork = 0;
    auto world = std::unique_ptr<EffectWorld>{};
    check(EffectWorld::create(worldOptions, world));
    for (auto cycle = 0; cycle < 3; ++cycle)
    {
        std::array<EffectFrame, 16> frames{};
        std::array<SpawnableHostLease, 16> leases{};
        for (auto i = 0u; i < frames.size(); ++i)
        {
            auto &pool = i % 2 ? workerPool : ownerPool;
            check(pool->acquire(leases[i], frames[i].host));
            require(frames[i].host->supportsWorkerExecution() == bool(i % 2),
                    "Reuse and worker permissions were conflated.");
        }
        for (auto tick = 0; tick < 3; ++tick)
        {
            for (auto &frame : frames)
            {
                frame.time = tick / 60.0;
                frame.delta = tick ? 1.0 / 60 : 0;
            }
            check(world->advance(frames.data(), frames.size()));
            for (auto &frame : frames)
            {
                check(frame.result);
                double value[4]{};
                std::uint8_t id[16]{};
                id[15] = 1;
                auto width = std::uint8_t{};
                check(frame.host->rootValue(id, value, width));
                require(value[0] == tick + 1, "Reused callback retained state from a previous playback.");
            }
        }
        for (auto i = 0u; i < frames.size(); ++i)
        {
            check((i % 2 ? workerPool : ownerPool)->release(leases[i]));
        }
    }
    require(!owner.invalid && owner.offOwnerRun == 0 && owner.runs > 0,
            "Owner callback overlapped workers or changed thread.");
    if (std::thread::hardware_concurrency() > 1)
    {
        require(worker.offOwnerRun > 0, "Declared worker callback never reached a worker.");
    }
    ownerPool.reset();
    workerPool.reset();
    require(owner.initialized == owner.cleaned && worker.initialized == worker.cleaned,
            "Callback cleanup did not run exactly once.");
}
SpawnablePackage load(const char *path)
{
    auto file = std::ifstream(path, std::ios::binary);
    require(file.good(), "Missing prepared-execution fixture.");
    const auto bytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(file), {});
    auto result = SpawnablePackage{};
    check(loadSpawnablePackage(bytes.data(), bytes.size(), result));
    return result;
}
void childCallbackPolicy(const char *path)
{
    auto package = load(path);
    const auto leaf = std::find_if(package.templates.begin(), package.templates.end(),
                                   [](const auto &t) { return t.parentContainerId.has_value(); });
    require(leaf != package.templates.end(), "Expected a nested callback fixture.");
    auto running = std::atomic<unsigned>{0};
    auto callbacks = Callbacks{};
    callbacks.ownerOnly = true;
    callbacks.runningWorkers = &running;
    const auto node = descriptor(callbacks);
    leaf->bytecode = custom(node).parentBytecode;
    leaf->cpuPrograms.clear();
    leaf->lifecycle.reset();
    leaf->capturedInputs.reset();
    leaf->liveInputs.reset();
    leaf->signalInputs.reset();
    leaf->signalOutputs.reset();
    leaf->inputs.clear();
    leaf->outputs.reset();
    auto host = std::unique_ptr<SpawnableHost>{};
    check(SpawnableHost::create(package, {}, &node, 1, host));
    require(!host->supportsWorkerExecution() && !host->supportsPoolReuse(),
            "Nested callback was omitted from safety classification.");
    host.reset();
    auto options = SpawnableHostPoolOptions{};
    options.capacity = 2;
    options.registry = &node;
    options.registryCount = 1;
    options.host.customNodePolicies = {{node, true, false, false}};
    auto pool = std::unique_ptr<SpawnableHostPool>{};
    check(SpawnableHostPool::create(package, options, pool));
    auto world = std::unique_ptr<EffectWorld>{};
    check(EffectWorld::create({}, world));
    for (auto cycle = 0; cycle < 3; ++cycle)
    {
        auto lease = SpawnableHostLease{};
        auto frame = EffectFrame{};
        check(pool->acquire(lease, frame.host));
        check(world->advance(&frame, 1));
        check(frame.result);
        auto event = SpawnableHostEvent{};
        auto instance = GeneratorInstanceId{};
        while (frame.host->popEvent(event))
        {
            if (event.kind == SpawnableHostEventKind::Spawn &&
                std::memcmp(event.containerId, leaf->containerId.data(), 16) == 0)
            {
                instance = event.instanceId;
            }
        }
        require(instance != 0, "Custom child was not spawned.");
        std::uint8_t id[16]{};
        id[15] = 1;
        double values[4]{};
        auto width = std::uint8_t{};
        check(frame.host->childValue(leaf->containerId.data(), instance, id, values, width));
        require(values[0] == 1, "Custom child state did not reset.");
        check(pool->release(lease));
    }
    pool.reset();
    require(callbacks.runs > 0 && callbacks.offOwnerInit == 0 && callbacks.offOwnerRun == 0 && !callbacks.invalid &&
                callbacks.initialized == callbacks.cleaned,
            "Child callback lifetime or affinity changed during reuse.");
}
void fixture(const char *path)
{
    std::cout << "Prepared fixture: " << path << std::endl;
    const auto package = load(path);
    auto options = SpawnableHostPoolOptions{};
    options.capacity = 4;
    auto pool = std::unique_ptr<SpawnableHostPool>{};
    check(SpawnableHostPool::create(package, options, pool));
    auto settings = EffectWorldOptions{};
    settings.maximumEffects = 4;
    settings.minimumParallelWork = 0;
    auto world = std::unique_ptr<EffectWorld>{};
    check(EffectWorld::create(settings, world));
    for (auto cycle = 0; cycle < 3; ++cycle)
    {
        std::array<EffectFrame, 4> frames{};
        std::array<SpawnableHostLease, 4> leases{};
        std::array<std::unique_ptr<SpawnableHost>, 4> fresh{};
        std::array<std::vector<SpawnableScope>, 4> scopes{};
        for (auto i = 0u; i < frames.size(); ++i)
        {
            check(pool->acquire(leases[i], frames[i].host));
            check(SpawnableHost::create(package, options.host, fresh[i]));
            scopes[i].push_back({});
            for (auto *host : {frames[i].host, fresh[i].get()})
            {
                if (cycle == 1)
                {
                    check(host->setCoordinateBasis({{2, 3, 1}, 100}));
                }
                check(host->setTimeScale(i == 0 ? .25 : 1));
            }
        }
        for (auto tick = 0; tick < 121; ++tick)
        {
            for (auto &frame : frames)
            {
                frame.time = tick / 20.0;
                frame.delta = tick ? .05 : 0;
            }
            check(world->advance(frames.data(), frames.size()));
            for (auto i = 0u; i < frames.size(); ++i)
            {
                auto &actual = *frames[i].host;
                auto &expected = *fresh[i];
                require(frames[i].result == expected.advance(frames[i].time, frames[i].delta),
                        "Shared hierarchy/mesh changed execution status.");
                if (frames[i].result == Result::NotReady && actual.rootFinished() && expected.rootFinished())
                {
                    continue;
                }
                check(frames[i].result);
                require(actual.activeCount() == expected.activeCount() &&
                            actual.rootFinished() == expected.rootFinished(),
                        "Hierarchy lifetime changed.");
                auto a = SpawnableHostEvent{}, b = SpawnableHostEvent{};
                for (;;)
                {
                    const auto left = actual.popEvent(a), right = expected.popEvent(b);
                    require(left == right, "Hierarchy event count changed.");
                    if (!left)
                    {
                        break;
                    }
                    require(a.kind == b.kind && a.instanceId == b.instanceId && a.time == b.time && a.seed == b.seed &&
                                std::memcmp(a.containerId, b.containerId, 16) == 0 &&
                                std::memcmp(a.signalNodeId, b.signalNodeId, 16) == 0,
                            "Hierarchy event order, seed or identity changed.");
                    if (a.kind == SpawnableHostEventKind::Spawn)
                    {
                        auto scope = SpawnableScope{};
                        scope.kind = SpawnableScopeKind::Instance;
                        std::copy_n(a.containerId, 16, scope.containerId);
                        scope.instanceId = a.instanceId;
                        scopes[i].push_back(scope);
                    }
                }
                for (const auto &scope : scopes[i])
                {
                    const auto *payload = &package.cpuPrograms;
                    if (scope.kind == SpawnableScopeKind::Instance)
                    {
                        const auto entry =
                            std::find_if(package.templates.begin(), package.templates.end(), [&](const auto &t)
                                         { return std::memcmp(t.containerId.data(), scope.containerId, 16) == 0; });
                        require(entry != package.templates.end(), "Spawned unknown template.");
                        payload = &entry->cpuPrograms;
                    }
                    if (payload->size() < 24)
                    {
                        continue;
                    }
                    // These compiler fixtures contain one CPU system per scope.
                    const auto *id = payload->data() + 8;
                    std::size_t ac = 0, bc = 0;
                    const auto ar = actual.cpuParticleCount(scope, id, ac),
                               br = expected.cpuParticleCount(scope, id, bc);
                    require(ar == br && ac == bc, "Scoped particle count changed.");
                    if (ar == Result::Ok)
                    {
                        for (auto p = std::size_t{}; p < ac; ++p)
                        {
                            CpuParticleSnapshot x{}, y{};
                            CpuParticleTiming xt{}, yt{};
                            check(actual.cpuParticleAt(scope, id, p, x));
                            check(expected.cpuParticleAt(scope, id, p, y));
                            check(actual.cpuParticleTiming(scope, id, p, xt));
                            check(expected.cpuParticleTiming(scope, id, p, yt));
                            require(std::memcmp(&x, &y, sizeof(x)) == 0 && std::memcmp(&xt, &yt, sizeof(xt)) == 0,
                                    "Scoped/mesh snapshot, color, UV or timing changed.");
                        }
                    }
                    auto x = CpuParticleEvent{}, y = CpuParticleEvent{};
                    for (;;)
                    {
                        const auto l = actual.popCpuParticleEvent(scope, x), r = expected.popCpuParticleEvent(scope, y);
                        require(l == r, "Scoped CPU event count changed.");
                        if (!l)
                        {
                            break;
                        }
                        require(std::memcmp(&x, &y, sizeof(x)) == 0, "Scoped CPU event ordering/data changed.");
                    }
                }
            }
            if (tick == 80 && cycle == 2)
            {
                for (auto i = 0u; i < frames.size(); ++i)
                {
                    check(frames[i].host->forceTerminate({}));
                    check(fresh[i]->forceTerminate({}));
                }
            }
        }
        for (auto i = 0u; i < frames.size(); ++i)
        {
            check(pool->release(leases[i]));
        }
        require(pool->stats().available == 4, "Hierarchy/mesh reuse quarantined a slot.");
    }
}
} // namespace liberafx::prepared_execution_tests
int main(int argc, char **argv)
{
    try
    {
        liberafx::prepared_execution_tests::callbackPolicies();
        if (argc > 1 && !liberafx::prepared_execution_tests::load(argv[1]).templates.empty())
        {
            liberafx::prepared_execution_tests::childCallbackPolicy(argv[1]);
        }
        for (auto i = 1; i < argc; ++i)
        {
            liberafx::prepared_execution_tests::fixture(argv[i]);
        }
        std::cout << "Callback policies and prepared shared hierarchy/mesh parity passed.\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
