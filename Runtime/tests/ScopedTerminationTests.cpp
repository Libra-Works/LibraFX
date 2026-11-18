#include "liberafx/LbSpawnableHost.h"
#include "liberafx/LbBuiltinSignals.h"
#include "liberafx/LbEffectLifecycle.h"
#include "liberafx/LbPhasePrimitives.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <array>
#include <cassert>
#include <cstring>
#include <utility>
#include <vector>
#include <cstdlib>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iterator>
#if defined(_MSC_VER) && defined(_DEBUG)
#include <crtdbg.h>
#endif

namespace {
using liberafx::Result;
using Id = std::array<std::uint8_t, 16>;

auto id(std::uint8_t value) -> Id { auto result = Id{}; result[15] = value; return result; }
void write32(std::vector<std::uint8_t>& bytes, std::size_t offset, std::uint32_t value)
{
    for (auto index = 0u; index < 4; ++index)
        bytes[offset + index] = static_cast<std::uint8_t>(value >> (index * 8));
}
auto binary(std::size_t count) -> std::vector<std::uint8_t>
{
    auto bytes = std::vector<std::uint8_t>(16 + count * 256);
    std::memcpy(bytes.data(), "LFXB", 4);
    bytes[4] = 3; bytes[6] = 1; bytes[13] = 1;
    write32(bytes, 8, static_cast<std::uint32_t>(count));
    return bytes;
}
void constant(std::vector<std::uint8_t>& bytes, std::size_t index, const Id& node, double value)
{
    auto offset = 16 + index * 256;
    std::memcpy(bytes.data() + offset, node.data(), 16);
    bytes[offset + 16] = 1; bytes[offset + 17] = 1;
    write32(bytes, offset + 24, UINT32_MAX);
    write32(bytes, offset + 28, UINT32_MAX);
    std::memcpy(bytes.data() + offset + 32, &value, sizeof(value));
}
void signal(std::vector<std::uint8_t>& bytes, std::size_t index, const Id& node, const Id& type)
{
    auto offset = 16 + index * 256;
    std::memcpy(bytes.data() + offset, node.data(), 16);
    bytes[offset + 16] = 41; bytes[offset + 19] = 1;
    std::memcpy(bytes.data() + offset + 24, type.data(), 16);
    write32(bytes, offset + 40, 1);
    for (auto input = 0u; input < 4; ++input) {
        write32(bytes, offset + 48 + input * 8, UINT32_MAX);
        write32(bytes, offset + 208 + input * 4, UINT32_MAX);
    }
}
auto childBinary() -> std::vector<std::uint8_t>
{
    auto bytes = binary(2);
    constant(bytes, 0, id(1), 2);
    signal(bytes, 1, id(2), liberafx::signalPassthroughTypeId);
    return bytes;
}
auto package(bool delayedRoute = false) -> liberafx::SpawnablePackage
{
    auto result = liberafx::SpawnablePackage{};
    result.packageVersion = 11;
    result.autoDestroyWhenIdle = false;
    result.parentBytecode = binary(delayedRoute ? 2 : 1);
    signal(result.parentBytecode, 0, id(42), id(77));
    if (delayedRoute) constant(result.parentBytecode, 1, id(5), 10);
    result.onStartNodeId = id(42);
    auto child = liberafx::SpawnablePackageTemplate{};
    child.containerId = id(42);
    child.signalTypeId = id(77);
    child.bytecode = childBinary();
    auto lifecycle = liberafx::SpawnablePackageLifecycle{};
    lifecycle.lifeNodeId = id(1);
    child.lifecycle = lifecycle;
    auto output = liberafx::SpawnablePackageSignalPort{};
    output.portId = "request-observed";
    output.templateNodeId = id(2);
    child.signalOutputs = std::vector<liberafx::SpawnablePackageSignalPort>{output};
    result.templates.push_back(std::move(child));
    if (delayedRoute) {
        auto route = liberafx::SpawnablePackageDestroyRoute{};
        route.sourceContainerId = id(42);
        route.targetContainerId = id(42);
        route.delayNodeId = id(5);
        result.destroyRoutes.push_back(route);
    }
    return result;
}
auto target(liberafx::GeneratorInstanceId instance) -> liberafx::SpawnableScope
{
    auto scope = liberafx::SpawnableScope{};
    scope.kind = liberafx::SpawnableScopeKind::Instance;
    std::memcpy(scope.containerId, id(42).data(), 16);
    scope.instanceId = instance;
    return scope;
}
auto next(liberafx::SpawnableHost& host) -> liberafx::SpawnableHostEvent
{
    auto event = liberafx::SpawnableHostEvent{};
    assert(host.popEvent(event));
    return event;
}
void scopedHost()
{
    auto host = std::unique_ptr<liberafx::SpawnableHost>{};
    assert(liberafx::SpawnableHost::create(package(), {}, host) == Result::Ok);
    assert(host->advance(0, 0) == Result::Ok);
    auto first = next(*host).instanceId;
    auto effect = liberafx::SpawnableScope{};
    // EndRequest is actual user signal dispatch, not natural end: here the user
    // wires it to spawn another instance. Repeated arrivals must remain distinct.
    assert(host->notifyEndRequest(effect, id(42).data()) == Result::Ok);
    auto second = next(*host).instanceId;
    assert(host->notifyEndRequest(effect, id(42).data()) == Result::Ok);
    auto third = next(*host).instanceId;
    assert(host->activeCount() == 3 && !host->rootFinished());
    assert(host->notifyEndRequest(target(first), id(2).data()) == Result::Ok);
    assert(host->notifyEndRequest(target(first), id(2).data()) == Result::Ok);
    assert(host->activeCount() == 3); // no hidden Lifetime/Destroy policy
    assert(host->forceTerminate(target(first)) == Result::Ok);
    auto forced = next(*host); // prior queued Signal arrivals were purged
    assert(forced.kind == liberafx::SpawnableHostEventKind::ForceTerminated &&
        forced.instanceId == first);
    assert(host->forceTerminate(target(first)) == Result::Ok && host->activeCount() == 2);
    auto unexpected = liberafx::SpawnableHostEvent{};
    assert(!host->popEvent(unexpected));
    assert(host->notifyEndRequest(target(first), id(2).data()) == Result::InvalidHandle);
    assert(host->advance(2, 2) == Result::Ok && host->activeCount() == 0);
    auto destroyed = 0u;
    while (host->popEvent(unexpected)) {
        assert(unexpected.instanceId == second || unexpected.instanceId == third);
        if (unexpected.kind == liberafx::SpawnableHostEventKind::Destroy) ++destroyed;
    }
    assert(destroyed == 2); // siblings retain natural lifetime
    assert(host->forceTerminate(effect) == Result::Ok);
    assert(next(*host).kind == liberafx::SpawnableHostEventKind::ForceTerminated);
    assert(host->forceTerminate(effect) == Result::Ok && !host->popEvent(unexpected));
    assert(host->advance(3, 1) == Result::NotReady);
}

void pendingContinuationAndSameTick()
{
    auto host = std::unique_ptr<liberafx::SpawnableHost>{};
    assert(liberafx::SpawnableHost::create(package(true), {}, host) == Result::Ok);
    assert(host->advance(0, 0) == Result::Ok);
    auto first = next(*host).instanceId;
    assert(host->advance(2, 2) == Result::Ok && host->activeCount() == 0);
    // A natural Destroy was captured but not delivered to the adapter; cancel
    // both queued notice and its future Spawn without touching other siblings.
    assert(host->forceTerminate(target(first)) == Result::Ok);
    assert(next(*host).kind == liberafx::SpawnableHostEventKind::ForceTerminated);
    assert(host->advance(20, 18) == Result::Ok && host->activeCount() == 0);
    auto event = liberafx::SpawnableHostEvent{};
    assert(!host->popEvent(event));

    assert(liberafx::SpawnableHost::create(package(true), {}, host) == Result::Ok);
    assert(host->advance(0, 0) == Result::Ok);
    first = next(*host).instanceId;
    // Same-tick precedence is owner operation order: force before advancing to
    // the expiry means no natural Destroy or Destroy-route spawn ever executes.
    assert(host->forceTerminate(target(first)) == Result::Ok);
    assert(host->advance(2, 2) == Result::Ok && host->activeCount() == 0);
    assert(next(*host).kind == liberafx::SpawnableHostEventKind::ForceTerminated);
    assert(!host->popEvent(event));

    // Cancel one retired source's future route, not its same-template sibling.
    assert(liberafx::SpawnableHost::create(package(true), {}, host) == Result::Ok);
    assert(host->advance(0, 0) == Result::Ok);
    first = next(*host).instanceId;
    assert(host->dispatchSignal(id(42).data()) == Result::Ok);
    auto sibling = next(*host).instanceId;
    assert(host->advance(2, 2) == Result::Ok);
    assert(host->forceTerminate(target(first)) == Result::Ok);
    auto siblingDestroys = 0u;
    while (host->popEvent(event)) {
        if (event.kind == liberafx::SpawnableHostEventKind::Destroy) {
            assert(event.instanceId == sibling);
            ++siblingDestroys;
        }
    }
    assert(siblingDestroys == 1);
    assert(host->advance(12, 10) == Result::Ok && host->activeCount() == 1);
    assert(next(*host).kind == liberafx::SpawnableHostEventKind::Spawn);
    assert(!host->popEvent(event));
    assert(host->forceTerminate({}) == Result::Ok);

    // A natural-end request already in progress is still overridden by force.
    auto naturalEndPackage = package();
    naturalEndPackage.onDestroyNodeId = id(42); // would spawn on natural Destroy
    assert(liberafx::SpawnableHost::create(std::move(naturalEndPackage), {}, host) == Result::Ok);
    assert(host->advance(0, 0) == Result::Ok);
    (void)next(*host);
    assert(host->requestNaturalEnd() == Result::Ok && !host->rootFinished());
    assert(host->forceTerminate({}) == Result::Ok && host->activeCount() == 0);
    assert(next(*host).kind == liberafx::SpawnableHostEventKind::ForceTerminated);
    assert(!host->popEvent(event)); // no root Destroy spawn/End processing
}

void backpressureAndEffect()
{
    auto host = std::unique_ptr<liberafx::SpawnableHost>{};
    auto options = liberafx::SpawnableHostOptions{};
    options.eventCapacity = 1;
    assert(liberafx::SpawnableHost::create(package(), options, host) == Result::Ok);
    assert(host->advance(0, 0) == Result::Ok);
    auto first = next(*host).instanceId;
    assert(host->dispatchSignal(id(42).data()) == Result::Ok); // fills queue with sibling Spawn
    assert(host->forceTerminate(target(first)) == Result::QueueRejected && host->activeCount() == 2);
    auto sibling = next(*host).instanceId;
    assert(sibling != first);
    assert(host->forceTerminate(target(first)) == Result::Ok && host->activeCount() == 1);
    // Effect force replaces pending notices with one whole-scope invalidation.
    assert(host->forceTerminate({}) == Result::Ok && host->activeCount() == 0);
    auto forced = next(*host);
    assert(forced.kind == liberafx::SpawnableHostEventKind::ForceTerminated && forced.instanceId == 0);
    auto event = liberafx::SpawnableHostEvent{};
    assert(!host->popEvent(event));
}

struct JobProbe final {
    liberafx::GeneratorPool* pool = nullptr;
    liberafx::GeneratorInstanceId instance = 0;
    Result attemptedForce = Result::Ok;
    Result attemptedAll = Result::Ok;
    unsigned cleanups = 0;
};
auto probe(liberafx::CustomNodeInvocation& call) noexcept -> Result
{
    auto& context = *static_cast<JobProbe*>(call.context);
    context.attemptedForce = context.pool->forceTerminate(context.instance);
    context.attemptedAll = context.pool->forceTerminateAll();
    assert(!context.cleanups); // job work must remain alive
    call.outputs[0] = 1;
    return Result::Ok;
}
void cleanup(void*, std::size_t, void* data) noexcept
{
    ++static_cast<JobProbe*>(data)->cleanups;
}
void poolBoundaryAndExpiry()
{
    auto bytes = childBinary();
    auto bindings = std::array<liberafx::CustomNodeBinding, 2>{};
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), nullptr, 0,
        bindings.data(), bindings.size(), program) == Result::Ok);
    auto pool = std::unique_ptr<liberafx::GeneratorPool>{};
    assert(liberafx::GeneratorPool::create(program, id(42).data(), 4, 1024 * 1024, 8, pool) == Result::Ok);
    auto config = liberafx::GeneratorLifecycle{};
    config.hasLife = true;
    std::memcpy(config.lifeNodeId, id(1).data(), 16);
    assert(pool->configureLifecycle(config) == Result::Ok);
    auto first = liberafx::GeneratorInstanceId{};
    assert(pool->spawn(0, nullptr, 0, first) == Result::Ok);
    assert(pool->start(first) == Result::Ok);
    assert(pool->evaluate(first, 2) == Result::Ok); // expiring, Destroy still queued
    assert(pool->forceTerminate(first) == Result::Ok && pool->activeCount() == 0);
    auto event = liberafx::GeneratorEvent{};
    assert(!pool->popEvent(event)); // purge even Spawn + unpublished Destroy
    assert(pool->forceTerminate(first) == Result::Ok);
    assert(pool->forceTerminate(first + 1) == Result::InvalidHandle);
    assert(pool->enqueueSpawn(10, nullptr, 0) == Result::Ok);
    assert(pool->forceTerminateAll() == Result::Ok);
    auto processed = std::size_t{99};
    assert(pool->processSpawns(8, processed) == Result::Ok && processed == 0);

    // Reentrant force during a parameter job is Busy, never premature cleanup.
    auto context = JobProbe{};
    bytes = binary(1);
    signal(bytes, 0, id(9), id(99));
    bytes[16 + 16] = 40; // custom parameter
    bytes[16 + 17] = 1;
    bytes[16 + 19] = 0;
    auto descriptor = liberafx::CustomNodeDescriptor{};
    std::memcpy(descriptor.typeId, id(99).data(), 16);
    descriptor.abiVersion = 1;
    descriptor.outputWidth = 1;
    descriptor.function = probe;
    descriptor.cleanup = cleanup;
    descriptor.context = &context;
    assert(liberafx::Program::load(bytes.data(), bytes.size(), &descriptor, 1,
        bindings.data(), bindings.size(), program) == Result::Ok);
    assert(liberafx::GeneratorPool::create(program, id(42).data(), 2, 1024 * 1024, 8, pool) == Result::Ok);
    context.pool = pool.get();
    assert(pool->spawn(0, nullptr, 0, context.instance) == Result::Ok);
    assert(pool->evaluate(context.instance, 0) == Result::Ok);
    assert(context.attemptedForce == Result::Busy && context.attemptedAll == Result::Busy);
    assert(pool->forceTerminate(context.instance) == Result::Ok && context.cleanups == 1);
}

void poolDeferredAndSiblingSignals()
{
    auto bytes = binary(4);
    constant(bytes, 0, id(1), 2);
    constant(bytes, 1, id(3), 10);
    const auto delayType = Id{
        0xc9, 0x82, 0xe1, 0xbe, 0x8f, 0x78, 0x4d, 0x28,
        0x9a, 0x65, 0x4f, 0x4a, 0xa4, 0x52, 0x49, 0xe4};
    signal(bytes, 2, id(4), delayType);
    auto offset = std::size_t{16 + 2 * 256};
    bytes[offset + 18] = 1;
    write32(bytes, offset + 48, 1); // delay input from constant #1
    bytes[offset + 52] = 1;
    write32(bytes, offset + 208, 3); // future continuation -> output
    signal(bytes, 3, id(2), liberafx::signalPassthroughTypeId);
    auto bindings = std::array<liberafx::CustomNodeBinding, 4>{};
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), nullptr, 0,
        bindings.data(), bindings.size(), program) == Result::Ok);
    auto pool = std::unique_ptr<liberafx::GeneratorPool>{};
    assert(liberafx::GeneratorPool::create(program, id(42).data(), 4, 1024 * 1024, 8, pool) == Result::Ok);
    assert(pool->setSignalOutputs(id(2).data(), 1) == Result::Ok);
    auto config = liberafx::GeneratorLifecycle{};
    config.hasLife = config.hasOnStart = config.enableDeferredSignals = true;
    config.instructionBudget = 100;
    std::memcpy(config.lifeNodeId, id(1).data(), 16);
    std::memcpy(config.onStartNodeId, id(4).data(), 16);
    assert(pool->configureLifecycle(config) == Result::Ok);
    auto first = liberafx::GeneratorInstanceId{};
    auto second = liberafx::GeneratorInstanceId{};
    assert(pool->spawn(0, nullptr, 0, first) == Result::Ok && pool->start(first) == Result::Ok);
    assert(pool->spawn(0, nullptr, 0, second) == Result::Ok && pool->start(second) == Result::Ok);
    // Queue output arrivals for both instances, preserving multiplicity.
    assert(pool->dispatchSignal(first, id(2).data(), 0, 100) == Result::Ok);
    assert(pool->dispatchSignal(second, id(2).data(), 0, 100) == Result::Ok);
    assert(pool->dispatchSignal(second, id(2).data(), 0, 100) == Result::Ok);
    assert(pool->forceTerminate(first) == Result::Ok);
    auto output = liberafx::GeneratorSignalEvent{};
    assert(pool->popSignalEvent(output) && output.instanceId == second);
    assert(pool->popSignalEvent(output) && output.instanceId == second);
    assert(!pool->popSignalEvent(output));
    auto due = double{};
    auto pending = false;
    assert(pool->nextDeferredSignal(first, due, pending) == Result::InvalidHandle);
    assert(pool->nextDeferredSignal(second, due, pending) == Result::Ok && pending && due == 10);
    assert(pool->forceTerminateAll() == Result::Ok && pool->activeCount() == 0);
    auto event = liberafx::GeneratorEvent{};
    assert(!pool->popEvent(event));
}

void singleInstancePhaseDispatch()
{
    // Actual native signal graph entries, not three Spawnable instances.
    // The host/caller selects entries; this is not authored phase-node support.
    auto bytes = binary(4);
    constant(bytes, 0, id(1), 2); // a curve/phase length is not a Lifetime
    signal(bytes, 1, id(10), liberafx::signalPassthroughTypeId);
    signal(bytes, 2, id(20), liberafx::signalPassthroughTypeId);
    signal(bytes, 3, id(30), liberafx::signalPassthroughTypeId);
    auto bindings = std::array<liberafx::CustomNodeBinding, 4>{};
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), nullptr, 0,
        bindings.data(), bindings.size(), program) == Result::Ok);
    auto pool = std::unique_ptr<liberafx::GeneratorPool>{};
    assert(liberafx::GeneratorPool::create(program, id(42).data(), 1, 1024 * 1024, 16, pool) == Result::Ok);
    auto outputs = std::array<Id, 3>{id(10), id(20), id(30)};
    assert(pool->setSignalOutputs(reinterpret_cast<const std::uint8_t*>(outputs.data()), 3) == Result::Ok);
    assert(pool->configureLifecycle({}) == Result::Ok); // explicitly no lifetime
    auto instance = liberafx::GeneratorInstanceId{};
    assert(pool->spawn(0, nullptr, 0, instance) == Result::Ok && pool->start(instance) == Result::Ok);
    auto spawn = liberafx::GeneratorEvent{};
    assert(pool->popEvent(spawn) && spawn.kind == liberafx::GeneratorEventKind::Spawn);
    auto phase = liberafx::PhaseClock{};
    auto loop = liberafx::LoopClock{};
    auto arm = liberafx::FutureBoundaryArm{};
    auto sample = liberafx::LoopSample{};
    assert(phase.reset(0, 10) == Result::Ok);
    assert(pool->dispatchSignal(instance, id(10).data(), 0, 100) == Result::Ok);
    assert(pool->evaluate(instance, 1) == Result::Ok);
    assert(phase.enter(1, 20) == Result::Ok && loop.reset(1, 2, true, 3) == Result::Ok);
    assert(pool->dispatchSignal(instance, id(20).data(), 1, 100) == Result::Ok);
    assert(pool->evaluate(instance, 5) == Result::Ok && loop.advance(5, sample) == Result::Ok);
    assert(sample.boundariesCrossed == 2);
    for (auto index = std::uint64_t{}; index < sample.boundariesCrossed; ++index)
        assert(pool->dispatchSignal(instance, id(20).data(), 5, 100) == Result::Ok);
    arm.arm(sample); // request after the current boundary, not a stored AND
    assert(!arm.consume(sample) && phase.sample().phase == 20);
    assert(pool->evaluate(instance, 7) == Result::Ok && loop.advance(7, sample) == Result::Ok);
    assert(arm.consume(sample) && phase.enter(7, 30) == Result::Ok);
    assert(pool->dispatchSignal(instance, id(30).data(), 7, 100) == Result::Ok);
    auto output = liberafx::GeneratorSignalEvent{};
    for (auto expected : {10, 20, 20, 20, 30}) {
        assert(pool->popSignalEvent(output) && output.instanceId == instance &&
            output.nodeId[15] == expected);
    }
    assert(!pool->popSignalEvent(output)); // no inactive phase signal executed
    assert(pool->evaluate(instance, 100) == Result::Ok && pool->activeCount() == 1);
    assert(!pool->popEvent(spawn)); // End phase/length did not cause Destroy
    assert(pool->forceTerminate(instance) == Result::Ok && pool->activeCount() == 0);
    assert(!pool->popEvent(spawn)); // explicit force still is not Destroy
}

void authoredPhases()
{
    auto directory = std::getenv("LIBERAFX_LIFECYCLE_FIXTURE_DIR");
    if (!directory) return;
    auto file = std::ifstream(std::filesystem::path(directory) / "AuthoredPhases.lfxb", std::ios::binary);
    assert(file.good());
    auto bytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
    auto identities = std::ifstream(std::filesystem::path(directory) / "AuthoredPhases.ids", std::ios::binary);
    auto ids = std::array<Id, 10>{};
    for (auto& value : ids) identities.read(reinterpret_cast<char*>(value.data()), value.size());
    assert(identities.good());
    auto bindings = std::array<liberafx::CustomNodeBinding, 128>{};
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), nullptr, 0,
        bindings.data(), bindings.size(), program) == Result::Ok);
    alignas(64) double work[32768]{}, defaults[32768]{};
    auto instance = liberafx::Instance{};
    assert(instance.initialize(program, work, 32768, defaults, 32768) == Result::Ok);
    struct Observed final {
        Id end{};
        unsigned arrivals = 0;
    } observed{ids[5]};
    assert(instance.setSignalObserver([](void* context, const std::uint8_t node[16]) noexcept {
        auto state = static_cast<Observed*>(context);
        if (std::memcmp(state->end.data(), node, 16) == 0) ++state->arrivals;
        return Result::Ok;
    }, &observed) == Result::Ok);
    auto evaluate = [&](double time) {
        assert(instance.updateAsync(time, [](void*, liberafx::Job job, void* context) noexcept {
            job(context); return true;
        }, nullptr) == Result::Ok);
        assert(instance.result() == Result::Ok);
        assert(instance.dispatchGeneratedSignals(10000) == Result::Ok);
    };
    auto value = [&](std::size_t node) {
        auto result = std::array<double, 4>{};
        auto width = std::uint8_t{};
        assert(instance.valueComponents(ids[node].data(), result.data(), width) == Result::Ok);
        return result;
    };
    auto dispatch = [&](std::size_t node, double time) {
        assert(instance.dispatchSignal(ids[node].data(), time, 10000) == Result::Ok);
    };
    evaluate(0);
    evaluate(.5);
    assert(value(0)[0] == 0 && value(1)[0] == .5 && observed.arrivals == 0);
    dispatch(4, .5);
    assert(value(0)[0] == 0); // immutable phase selection during the current evaluation
    evaluate(.5);
    assert(value(0)[0] == 1 && value(1)[0] == 0);
    evaluate(2.75);
    assert(value(2)[0] == .25 && value(2)[1] == 2 && value(2)[2] == 2);
    dispatch(7, 2.75); // the two earlier boundaries do not satisfy this request
    evaluate(2.75);
    assert(observed.arrivals == 0);
    evaluate(3.5);
    assert(observed.arrivals == 1 && value(0)[0] == 1 && value(1)[0] == 3);
    evaluate(3.5);
    assert(value(0)[0] == 2 && value(1)[0] == 0);
    evaluate(100);
    assert(value(0)[0] == 2 && value(1)[0] == 96.5 && observed.arrivals == 1);
    assert(value(3)[2] == 3); // inactive Loop clock did not execute again
    dispatch(9, 100);
    evaluate(100);
    assert(value(0)[0] == 2 && value(1)[0] == 0); // same-phase reentry restarts only its clock
    dispatch(6, 100);
    evaluate(100);
    assert(value(0)[0] == 0 && value(1)[0] == 0);
    dispatch(4, 100);
    evaluate(100);
    assert(value(0)[0] == 1 && value(2)[2] == 0); // phase-local rewind disarms and resets Loop
    dispatch(7, 100);
    dispatch(8, 100);
    evaluate(101);
    assert(observed.arrivals == 1); // explicit reset also invalidates the previously armed request
    dispatch(7, 101);
    evaluate(102);
    assert(observed.arrivals == 2);
}

void authoredPreset()
{
    auto directory = std::getenv("LIBERAFX_LIFECYCLE_FIXTURE_DIR");
    if (!directory) return;
    auto file = std::ifstream(std::filesystem::path(directory) / "PhasePreset.lfxp", std::ios::binary);
    assert(file.good());
    auto bytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
    auto package = liberafx::SpawnablePackage{};
    assert(liberafx::loadSpawnablePackage(bytes.data(), bytes.size(), package) == Result::Ok);
    assert(package.templates.empty());
    auto phase = Id{}, time = Id{};
    auto found = 0u;
    auto count = package.parentBytecode.size() >= 16
        ? std::uint32_t{package.parentBytecode[8]} | (std::uint32_t{package.parentBytecode[9]} << 8) : 0;
    for (auto index = 0u; index < count; ++index)
    {
        auto current = package.parentBytecode.data() + 16 + index * 256;
        if (current[16] == 55) { std::memcpy(phase.data(), current, 16); ++found; }
        if (current[16] == 56) { std::memcpy(time.data(), current, 16); ++found; }
    }
    assert(found == 2);
    auto host = std::unique_ptr<liberafx::SpawnableHost>{};
    assert(liberafx::SpawnableHost::create(std::move(package), {}, host) == Result::Ok);
    auto currentPhase = [&] {
        auto value = std::array<double, 4>{};
        auto width = std::uint8_t{};
        assert(host->rootValue(phase.data(), value.data(), width) == Result::Ok && width == 1);
        return value[0];
    };
    assert(host->advance(0, 0) == Result::Ok && currentPhase() == 0);
    assert(host->advance(1, 1) == Result::Ok);
    assert(host->advance(1.5, .5) == Result::Ok && currentPhase() == 1);
    assert(host->advance(4, 3) == Result::Ok);
    assert(host->advance(4.5, .5) == Result::Ok && currentPhase() == 2);
    assert(host->advance(5, .5) == Result::Ok);
    assert(host->advance(100, 95) == Result::Ok && currentPhase() == 2 && !host->rootFinished());
    auto value = std::array<double, 4>{};
    auto width = std::uint8_t{};
    assert(host->rootValue(time.data(), value.data(), width) == Result::Ok && value[0] == 96);
}

void authoredForce()
{
    auto directory = std::getenv("LIBERAFX_LIFECYCLE_FIXTURE_DIR");
    if (!directory) return;
    for (auto name : {"ForceCurrent", "ForceEffect"})
    {
        auto base = std::filesystem::path(directory) / name;
        auto packageFile = std::ifstream(base.string() + ".lfxp", std::ios::binary);
        assert(packageFile.good());
        auto bytes = std::vector<std::uint8_t>(
            std::istreambuf_iterator<char>(packageFile), std::istreambuf_iterator<char>());
        auto identities = std::ifstream(base.string() + ".ids", std::ios::binary);
        auto spawn = Id{}, childForce = Id{}, rootForce = Id{};
        identities.read(reinterpret_cast<char*>(spawn.data()), 16);
        identities.read(reinterpret_cast<char*>(childForce.data()), 16);
        identities.read(reinterpret_cast<char*>(rootForce.data()), 16);
        assert(identities.good());
        auto data = liberafx::SpawnablePackage{};
        assert(liberafx::loadSpawnablePackage(bytes.data(), bytes.size(), data) == Result::Ok);
        auto host = std::unique_ptr<liberafx::SpawnableHost>{};
        assert(liberafx::SpawnableHost::create(std::move(data), {}, host) == Result::Ok);
        assert(host->advance(0, 0) == Result::Ok);
        auto first = next(*host).instanceId;
        assert(host->dispatchSignal(spawn.data()) == Result::Ok);
        auto sibling = next(*host).instanceId;
        auto scope = liberafx::SpawnableScope{};
        scope.kind = liberafx::SpawnableScopeKind::Instance;
        std::memcpy(scope.containerId, spawn.data(), 16);
        scope.instanceId = first;
        assert(host->notifyEndRequest(scope, childForce.data()) == Result::Ok);
        auto event = next(*host);
        assert(event.kind == liberafx::SpawnableHostEventKind::ForceTerminated);
        if (std::strcmp(name, "ForceCurrent") == 0)
        {
            assert(event.instanceId == first && host->activeCount() == 1 && !host->rootFinished());
            auto values = std::array<double, 4>{};
            auto width = std::uint8_t{};
            assert(host->childValue(spawn.data(), first, childForce.data(), values.data(), width) == Result::InvalidHandle);
            assert(host->dispatchSignal(rootForce.data()) == Result::Ok && host->rootFinished());
            assert(next(*host).instanceId == 0);
        }
        else assert(event.instanceId == 0 && host->rootFinished());
        assert(first != sibling && host->activeCount() == 0);
        assert(!host->popEvent(event));
        assert(host->advance(100, 100) == Result::NotReady);
    }
}
}

int main()
{
#if defined(_MSC_VER) && defined(_DEBUG)
    _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE);
    _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif
    std::setvbuf(stderr, nullptr, _IONBF, 0);
    scopedHost();
    pendingContinuationAndSameTick();
    backpressureAndEffect();
    poolBoundaryAndExpiry();
    poolDeferredAndSiblingSignals();
    singleInstancePhaseDispatch();
    authoredPhases();
    authoredPreset();
    authoredForce();
}
