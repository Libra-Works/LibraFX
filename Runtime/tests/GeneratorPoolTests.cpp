#include "liberafx/GeneratorPool.h"
#include "liberafx/NamedIo.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <algorithm>
#include <array>
#include <cassert>
#include <cstring>
#include <vector>
#if defined(_MSC_VER) && defined(_DEBUG)
#include <crtdbg.h>
#endif

namespace {
#if defined(_MSC_VER) && defined(_DEBUG)
auto frameAllocations = 0u;
auto __cdecl allocationHook(int kind, void*, size_t, int, long, const unsigned char*, int) -> int
{
    if (kind == _HOOK_ALLOC || kind == _HOOK_REALLOC) ++frameAllocations;
    return 1;
}
#endif
void write32(std::vector<std::uint8_t>& bytes, std::size_t offset, std::uint32_t value)
{
    for (auto i = 0u; i < 4; ++i) bytes[offset + i] = static_cast<std::uint8_t>(value >> (i * 8));
}
auto inlineJob(void*, liberafx::Job job, void* context) noexcept -> bool { job(context); return true; }
struct Context { liberafx::GeneratorPool* pool; std::uint8_t target[16]; };
auto signal(liberafx::CustomNodeInvocation& call) noexcept -> liberafx::Result
{
    auto context = static_cast<Context*>(call.context);
    auto capture = liberafx::GeneratorCapture{};
    std::memcpy(capture.nodeId, context->target, 16);
    capture.value = call.inputs[0];
    auto result = context->pool->enqueueSpawn(call.time, &capture, 1);
    call.selectedBranch = 0;
    return result;
}
struct IoSignalContext { liberafx::NamedIo* io = nullptr; unsigned calls = 0; };
auto ioSignal(liberafx::CustomNodeInvocation& call) noexcept -> liberafx::Result
{
    auto& context = *static_cast<IoSignalContext*>(call.context);
    ++context.calls;
    auto event = liberafx::NamedIoEvent{};
    if (context.io->popEvent(event)) return liberafx::Result::InvalidArgument;
    call.selectedBranch = 0;
    return context.io->emitSignal("Finished");
}
auto stateInitialize(void* state, std::size_t, void*) noexcept -> liberafx::Result
{
    assert(reinterpret_cast<std::uintptr_t>(state) % 64 == 0);
    *static_cast<double*>(state) = 0;
    return liberafx::Result::Ok;
}
auto stateEvaluate(liberafx::CustomNodeInvocation& call) noexcept -> liberafx::Result
{
    assert(reinterpret_cast<std::uintptr_t>(call.state) % 64 == 0);
    auto& count = *static_cast<double*>(call.state);
    call.outputs[0] = ++count + call.time;
    return liberafx::Result::Ok;
}
auto failStartEvaluation(liberafx::CustomNodeInvocation& call) noexcept -> liberafx::Result
{
    ++*static_cast<unsigned*>(call.context);
    return liberafx::Result::QueueRejected;
}

struct LifeContext {
    liberafx::GeneratorPool* pool = nullptr;
    liberafx::GeneratorInstanceId instance = 0;
    unsigned calls = 0;
    double observed[8]{};
    bool enqueue = false;
    bool fail = false;
    bool defer = false;
    double delay = 0;
    liberafx::Result reentrantStop = liberafx::Result::Ok;
    liberafx::Result reentrantStart = liberafx::Result::Ok;
    liberafx::Result enqueueResult = liberafx::Result::Ok;
    unsigned cleanupCalls = 0;
    liberafx::Result cleanupEnqueue = liberafx::Result::Ok;
};
auto onStart(liberafx::CustomNodeInvocation& call) noexcept -> liberafx::Result
{
    auto& context = *static_cast<LifeContext*>(call.context);
    if (context.calls >= 8) return liberafx::Result::InvalidArgument;
    context.observed[context.calls++] = call.inputs[0];
    context.reentrantStop = context.pool->stop(context.instance);
    context.reentrantStart = context.pool->start(context.instance);
    if (context.enqueue) context.enqueueResult = context.pool->enqueueSpawn(20, nullptr, 0);
    call.selectedBranch = 0;
    call.deferSignal = context.defer;
    call.delaySeconds = context.delay;
    return context.fail ? liberafx::Result::QueueRejected : liberafx::Result::Ok;
}
void cleanupLife(void*, std::size_t, void* data) noexcept
{
    auto& context = *static_cast<LifeContext*>(data);
    ++context.cleanupCalls;
    context.cleanupEnqueue = context.pool->enqueueSpawn(0, nullptr, 0);
}

void lifecycleTests()
{
    using liberafx::Result;
    auto bytes = std::vector<std::uint8_t>(16 + 3 * 256);
    std::memcpy(bytes.data(), "LFXB", 4);
    bytes[4] = 3; bytes[6] = 1; bytes[8] = 3; bytes[13] = 1;
    for (auto index = 0u; index < 2; ++index) {
        auto offset = 16 + index * 256;
        bytes[offset + 15] = static_cast<std::uint8_t>(index + 1);
        bytes[offset + 16] = 1; bytes[offset + 17] = 1;
        write32(bytes, offset + 24, UINT32_MAX);
        write32(bytes, offset + 28, UINT32_MAX);
        auto value = index == 0 ? 7.0 : 3.0;
        std::memcpy(bytes.data() + offset + 32, &value, sizeof(value));
    }
    auto signalOffset = std::size_t{16 + 2 * 256};
    bytes[signalOffset + 15] = 3;
    bytes[signalOffset + 16] = 41;
    bytes[signalOffset + 18] = 1;
    bytes[signalOffset + 19] = 1;
    bytes[signalOffset + 39] = 77;
    write32(bytes, signalOffset + 40, 1);
    bytes[signalOffset + 52] = 1;
    for (auto i = 1u; i < 4; ++i) write32(bytes, signalOffset + 48 + i * 8, UINT32_MAX);
    for (auto i = 0u; i < 4; ++i) write32(bytes, signalOffset + 208 + i * 4, UINT32_MAX);
    auto context = LifeContext{};
    auto descriptor = liberafx::CustomNodeDescriptor{};
    descriptor.typeId[15] = 77;
    descriptor.abiVersion = 1;
    descriptor.execution = liberafx::CustomExecution::Signal;
    descriptor.inputCount = 1;
    descriptor.inputWidths[0] = 1;
    descriptor.branchCount = 1;
    descriptor.function = onStart;
    descriptor.cleanup = cleanupLife;
    descriptor.context = &context;
    auto bindings = std::array<liberafx::CustomNodeBinding, 3>{};
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), &descriptor, 1,
                                   bindings.data(), bindings.size(), program) == Result::Ok);
    auto container = std::array<std::uint8_t, 16>{}; container[15] = 42;
    auto pool = std::unique_ptr<liberafx::GeneratorPool>{};
    assert(liberafx::GeneratorPool::create(program, container.data(), 3, 65536, 8, pool) == Result::Ok);
    context.pool = pool.get();
    auto signalOutput = std::array<std::uint8_t, 16>{}; signalOutput[15] = 3;
    assert(pool->setSignalOutputs(signalOutput.data(), 1) == Result::Ok);
    auto config = liberafx::GeneratorLifecycle{};
    config.hasOnStart = true; config.onStartNodeId[15] = 3; config.instructionBudget = 4;
    config.hasLife = true; config.lifeNodeId[15] = 2;
    assert(pool->configureLifecycle(config) == Result::Ok);
    auto first = liberafx::GeneratorInstanceId{};
    auto second = liberafx::GeneratorInstanceId{};
    assert(pool->spawn(10, nullptr, 0, first) == Result::Ok);
    assert(pool->spawn(12, nullptr, 0, second) == Result::Ok);
    assert(pool->evaluate(first, 10) == Result::NotReady);
    auto captured = liberafx::GeneratorCapture{};
    captured.nodeId[15] = 1; captured.value = 9;
    assert(pool->setCaptured(first, &captured, 1) == Result::Ok);
    auto live = liberafx::GeneratorCapture{};
    live.nodeId[15] = 1; live.value = 11;
    assert(pool->setLive(first, &live, 1) == Result::Ok);
    context.instance = first; context.enqueue = true;
    assert(pool->start(first) == Result::Ok);
    assert(context.calls == 1 && context.observed[0] == 11 &&
           context.reentrantStop == Result::Busy && context.reentrantStart == Result::Busy &&
           context.enqueueResult == Result::Ok);
    auto signalEvent = liberafx::GeneratorSignalEvent{};
    assert(pool->popSignalEvent(signalEvent) && signalEvent.instanceId == first &&
           signalEvent.time == 10 && signalEvent.nodeId[15] == 3);
    assert(pool->setCaptured(first, &captured, 1) == Result::Busy);
    assert(pool->start(first) == Result::Ok && context.calls == 1);
    context.instance = second; context.enqueue = false;
    captured.value = 13;
    assert(pool->setCaptured(second, &captured, 1) == Result::Ok);
    assert(pool->start(second) == Result::Ok && context.calls == 2 && context.observed[1] == 13);
    assert(pool->popSignalEvent(signalEvent) && signalEvent.instanceId == second &&
           signalEvent.time == 12 && signalEvent.nodeId[15] == 3);
    context.instance = first; context.enqueue = false;
    assert(pool->dispatchSignal(signalOutput.data(), 13, 4) == Result::Ok &&
           context.calls == 4 && context.observed[2] == 11 && context.observed[3] == 13);
    assert(pool->popSignalEvent(signalEvent) && signalEvent.instanceId == first && signalEvent.time == 13);
    assert(pool->popSignalEvent(signalEvent) && signalEvent.instanceId == second && signalEvent.time == 13);
    auto lifetimeSample = liberafx::LifetimeSample{};
    assert(pool->lifetime(second, lifetimeSample) == Result::Ok &&
           lifetimeSample.age == 0 && lifetimeSample.remaining == 3 && !lifetimeSample.expired);
    auto event = liberafx::GeneratorEvent{};
    assert(pool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Spawn && event.instanceId == first);
    assert(pool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Spawn && event.instanceId == second);
    assert(pool->evaluate(first, 13) == Result::Ok);
    assert(pool->evaluate(first, 13) == Result::NotReady);
    assert(pool->finalize(first) == Result::NotReady);
    assert(pool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Destroy && event.instanceId == first);
    auto finalValue = std::array<double, 4>{}; auto finalWidth = std::uint8_t{};
    assert(pool->value(first, bytes.data() + 16, finalValue.data(), finalWidth) == Result::Ok && finalValue[0] == 11);
    assert(pool->finalize(first) == Result::Ok);
    assert(context.cleanupCalls == 1 && context.cleanupEnqueue == Result::Busy);
    assert(pool->evaluate(first, 13) == Result::InvalidHandle);
    assert(pool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Stop && event.instanceId == first);
    auto liveLife = liberafx::GeneratorCapture{}; liveLife.nodeId[15] = 2; liveLife.value = 4;
    assert(pool->setLive(second, &liveLife, 1) == Result::Ok);
    assert(pool->evaluate(second, 14) == Result::Ok);
    assert(pool->lifetime(second, lifetimeSample) == Result::Ok &&
           lifetimeSample.age == 2 && lifetimeSample.remaining == 1 &&
           lifetimeSample.normalizedAge == 2.0 / 3 && !lifetimeSample.expired);
    assert(pool->evaluate(second, 15) == Result::Ok);
    // Changing the Life lane after start must not extend captured duration 3.
    assert(pool->stop(second) == Result::Busy);
    assert(pool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Destroy && event.instanceId == second);
    assert(pool->finalize(second) == Result::Ok);
    assert(pool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Stop && event.instanceId == second);
    assert(!pool->popEvent(event));
    auto processed = std::size_t{};
    assert(pool->processSpawns(1, processed) == Result::Ok && processed == 1);
    assert(pool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Spawn);
    assert(pool->reset() == Result::Ok);
    assert(pool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Stop);
    assert(!pool->popEvent(event));

    auto pressure = std::unique_ptr<liberafx::GeneratorPool>{};
    assert(liberafx::GeneratorPool::create(program, container.data(), 1, 65536, 1, pressure) == Result::Ok);
    assert(pressure->configureLifecycle(config) == Result::Ok);
    auto life = liberafx::GeneratorCapture{}; life.nodeId[15] = 2; life.value = 0;
    auto zero = liberafx::GeneratorInstanceId{};
    assert(pressure->spawn(0, &life, 1, zero) == Result::Ok);
    context.pool = pressure.get(); context.instance = zero;
    auto callsBeforePressure = context.calls;
    assert(pressure->start(zero) == Result::QueueRejected && context.calls == callsBeforePressure + 1);
    assert(pressure->lifetime(zero, lifetimeSample) == Result::Ok &&
           lifetimeSample.age == 0 && lifetimeSample.remaining == 0 &&
           lifetimeSample.normalizedAge == 1 && lifetimeSample.expired);
    assert(pressure->start(zero) == Result::QueueRejected && context.calls == callsBeforePressure + 1);
    assert(pressure->evaluate(zero, 1) == Result::QueueRejected);
    assert(pressure->stop(zero) == Result::Busy && pressure->reset() == Result::Busy);
    assert(pressure->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Spawn);
    assert(pressure->start(zero) == Result::Ok && context.calls == callsBeforePressure + 1);
    assert(pressure->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Destroy && event.instanceId == zero);
    assert(pressure->finalize(zero) == Result::Ok);
    assert(pressure->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Stop && event.instanceId == zero);
    assert(!pressure->popEvent(event) && pressure->start(zero) == Result::InvalidHandle);

    auto failed = std::unique_ptr<liberafx::GeneratorPool>{};
    assert(liberafx::GeneratorPool::create(program, container.data(), 1, 65536, 2, failed) == Result::Ok);
    assert(failed->configureLifecycle(config) == Result::Ok);
    auto failedId = liberafx::GeneratorInstanceId{};
    assert(failed->spawn(0, nullptr, 0, failedId) == Result::Ok);
    context.pool = failed.get(); context.instance = failedId; context.fail = true;
    auto callsBeforeFailure = context.calls;
    assert(failed->start(failedId) == Result::QueueRejected && context.calls == callsBeforeFailure + 1);
    assert(failed->start(failedId) == Result::QueueRejected && context.calls == callsBeforeFailure + 1);
    assert(failed->evaluate(failedId, 5) == Result::QueueRejected);
    assert(failed->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Spawn);
    assert(failed->stop(failedId) == Result::Ok);
    assert(failed->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Stop && !failed->popEvent(event));

    auto ownerPool = std::unique_ptr<liberafx::GeneratorPool>{};
    assert(liberafx::GeneratorPool::create(program, container.data(), 1, 65536, 4, ownerPool) == Result::Ok);
    auto ownerSignalOutput = std::array<std::uint8_t, 16>{}; ownerSignalOutput[15] = 3;
    assert(ownerPool->setSignalOutputs(ownerSignalOutput.data(), 1) == Result::Ok);
    auto ownerConfig = config;
    ownerConfig.hasOnStart = false;
    assert(ownerPool->configureLifecycle(ownerConfig) == Result::Ok);
    auto ownerChild = liberafx::GeneratorInstanceId{};
    assert(ownerPool->spawn(0.1, nullptr, 0, ownerChild) == Result::Ok);
    context.pool = ownerPool.get(); context.instance = ownerChild; context.fail = false;
    assert(ownerPool->ownerDestroy(ownerChild, bytes.data() + signalOffset, 0.1, 4) == Result::NotReady);
    assert(ownerPool->start(ownerChild) == Result::Ok);
    assert(ownerPool->evaluate(ownerChild, 0.3) == Result::Ok);
    assert(ownerPool->ownerDestroy(ownerChild, bytes.data() + signalOffset, 0.4, 4) == Result::InvalidArgument);
    auto callsBeforeOwner = context.calls;
    assert(ownerPool->ownerDestroy(ownerChild, bytes.data() + signalOffset, 0.3, 4) == Result::Ok);
    assert(context.calls == callsBeforeOwner + 1 && context.reentrantStop == Result::Busy &&
           context.reentrantStart == Result::Busy);
    auto ownerSignalEvent = liberafx::GeneratorSignalEvent{};
    assert(ownerPool->popSignalEvent(ownerSignalEvent) && ownerSignalEvent.time == 0.3 &&
           ownerSignalEvent.instanceId == ownerChild);
    assert(ownerPool->ownerDestroy(ownerChild, bytes.data() + signalOffset, 0.3, 4) == Result::Ok &&
           context.calls == callsBeforeOwner + 1);
    assert(ownerPool->stop(ownerChild) == Result::Ok);
    assert(ownerPool->ownerDestroy(ownerChild, bytes.data() + signalOffset, 0.3, 4) == Result::InvalidHandle);
    assert(ownerPool->spawn(20, nullptr, 0, ownerChild) == Result::Ok);
    context.instance = ownerChild; context.fail = true;
    assert(ownerPool->start(ownerChild) == Result::Ok);
    assert(ownerPool->ownerDestroy(ownerChild, bytes.data() + signalOffset, 20, 4) == Result::QueueRejected);
    assert(ownerPool->ownerDestroy(ownerChild, bytes.data() + signalOffset, 20, 4) == Result::QueueRejected &&
           context.calls == callsBeforeOwner + 2);
    assert(ownerPool->stop(ownerChild) == Result::Ok);
    context.fail = false;

    {
        auto delayedContext = LifeContext{};
        delayedContext.defer = true;
        delayedContext.delay = 5;
        descriptor.context = &delayedContext;
        auto delayedPool = std::unique_ptr<liberafx::GeneratorPool>{};
        assert(liberafx::GeneratorPool::create(program, container.data(), 3, 65536, 8, delayedPool) == Result::Ok);
        delayedContext.pool = delayedPool.get();
        auto delayedConfig = config;
        delayedConfig.enableDeferredSignals = true;
        assert(delayedPool->configureLifecycle(delayedConfig) == Result::Ok);
        auto delayedFirst = liberafx::GeneratorInstanceId{};
        auto delayedSecond = liberafx::GeneratorInstanceId{};
        assert(delayedPool->spawn(10, nullptr, 0, delayedFirst) == Result::Ok);
        assert(delayedPool->spawn(12, nullptr, 0, delayedSecond) == Result::Ok);
        assert(delayedPool->start(delayedFirst) == Result::Ok);
        assert(delayedPool->start(delayedSecond) == Result::Ok);
        assert(delayedPool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Spawn);
        assert(delayedPool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Spawn);
        auto due = double{};
        auto pending = false;
        assert(delayedPool->nextDeferredSignal(delayedFirst, due, pending) == Result::Ok && pending && due == 15);
        assert(delayedPool->nextDeferredSignal(delayedSecond, due, pending) == Result::Ok && pending && due == 17);
        assert(delayedPool->evaluate(delayedFirst, 13) == Result::Ok);
        assert(delayedPool->evaluate(delayedFirst, 14) == Result::Ok);
        assert(delayedPool->elapsed(delayedFirst, due) == Result::Ok && due == 4);
        assert(delayedPool->lifetime(delayedFirst, lifetimeSample) == Result::Ok &&
               lifetimeSample.age == 3 && lifetimeSample.expired);
        assert(!delayedPool->popEvent(event));
        assert(delayedPool->evaluate(delayedFirst, 16) == Result::NotReady);
        assert(delayedPool->dispatchDeferredSignal(delayedFirst, 15, 4) == Result::NotReady);
        assert(delayedPool->evaluate(delayedFirst, 15) == Result::Ok);
        assert(delayedPool->dispatchDeferredSignal(delayedFirst, 15, 4) == Result::Ok);
        assert(delayedPool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Destroy &&
               event.instanceId == delayedFirst);
        assert(delayedPool->elapsed(delayedFirst, due) == Result::Ok && due == 5);
        assert(delayedPool->finalize(delayedFirst) == Result::Ok);
        assert(delayedPool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Stop);
        assert(delayedPool->evaluate(delayedSecond, 15) == Result::Ok);
        assert(delayedPool->stop(delayedSecond) == Result::Ok);
        assert(delayedPool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Stop);
        assert(!delayedPool->popEvent(event));
        assert(delayedPool->spawn(20, &life, 1, delayedFirst) == Result::Ok);
        assert(delayedPool->start(delayedFirst) == Result::Ok);
        assert(delayedPool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Spawn);
        assert(!delayedPool->popEvent(event));
        assert(delayedPool->evaluate(delayedFirst, 25) == Result::Ok);
        assert(delayedPool->dispatchDeferredSignal(delayedFirst, 25, 4) == Result::Ok);
        assert(delayedPool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Destroy);
        assert(delayedPool->finalize(delayedFirst) == Result::Ok);
        assert(delayedPool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Stop);
        delayedContext.calls = 0;
        delayedContext.delay = 0.3;
        assert(delayedPool->spawn(0.1, nullptr, 0, delayedFirst) == Result::Ok);
        assert(delayedPool->start(delayedFirst) == Result::Ok);
        assert(delayedPool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Spawn);
        assert(delayedPool->nextDeferredSignal(delayedFirst, due, pending) == Result::Ok &&
               pending && due == 0.1 + 0.3);
        assert(delayedPool->evaluate(delayedFirst, due) == Result::Ok);
        assert(delayedPool->dispatchDeferredSignal(delayedFirst, due, 4) == Result::Ok);
        assert(delayedPool->evaluate(delayedFirst, due) == Result::Ok);
        assert(delayedPool->evaluate(delayedFirst, 0.2) == Result::InvalidArgument);
        delayedContext.defer = false;
        delayedContext.delay = 0;
        assert(delayedPool->ownerDestroy(delayedFirst, bytes.data() + signalOffset, due, 4) == Result::Ok);
        assert(delayedPool->stop(delayedFirst) == Result::Ok);
        assert(delayedPool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Stop);
        delayedPool.reset();
        descriptor.context = &context;
    }

    auto failureBytes = bytes;
    std::fill(failureBytes.begin() + 16, failureBytes.begin() + 16 + 256, 0);
    failureBytes[16 + 15] = 1;
    failureBytes[16 + 16] = 40;
    failureBytes[16 + 17] = 1;
    failureBytes[16 + 39] = 78;
    write32(failureBytes, 16 + 40, 1);
    for (auto i = 0u; i < 4; ++i) write32(failureBytes, 16 + 48 + i * 8, UINT32_MAX);
    for (auto i = 0u; i < 4; ++i) write32(failureBytes, 16 + 208 + i * 4, UINT32_MAX);
    auto failedEvaluations = 0u;
    auto failureDescriptors = std::array<liberafx::CustomNodeDescriptor, 2>{descriptor, {}};
    failureDescriptors[1].typeId[15] = 78;
    failureDescriptors[1].abiVersion = 1;
    failureDescriptors[1].execution = liberafx::CustomExecution::Parameter;
    failureDescriptors[1].outputWidth = 1;
    failureDescriptors[1].function = failStartEvaluation;
    failureDescriptors[1].context = &failedEvaluations;
    auto failureBindings = std::array<liberafx::CustomNodeBinding, 3>{};
    auto failureProgram = liberafx::Program{};
    assert(liberafx::Program::load(failureBytes.data(), failureBytes.size(), failureDescriptors.data(),
        failureDescriptors.size(), failureBindings.data(), failureBindings.size(), failureProgram) == Result::Ok);
    auto failurePool = std::unique_ptr<liberafx::GeneratorPool>{};
    assert(liberafx::GeneratorPool::create(failureProgram, container.data(), 1, 65536, 2, failurePool) == Result::Ok);
    assert(failurePool->configureLifecycle({}) == Result::Ok);
    auto evaluationFailure = liberafx::GeneratorInstanceId{};
    assert(failurePool->spawn(0, nullptr, 0, evaluationFailure) == Result::Ok);
    context.pool = failurePool.get();
    assert(failurePool->start(evaluationFailure) == Result::QueueRejected);
    assert(failurePool->start(evaluationFailure) == Result::QueueRejected && failedEvaluations == 1);
    assert(failurePool->evaluate(evaluationFailure, 1) == Result::QueueRejected);
    assert(failurePool->stop(evaluationFailure) == Result::Ok);

    auto extended = std::unique_ptr<liberafx::GeneratorPool>{};
    assert(liberafx::GeneratorPool::createWithCaptureLimit(program, container.data(), 1, 65536, 2, 256, extended) == Result::Ok);
    auto captures = std::array<liberafx::GeneratorCapture, 257>{};
    for (auto i = 0u; i < captures.size(); ++i) { captures[i].nodeId[15] = 1; captures[i].value = i; }
    assert(extended->enqueueSpawn(0, captures.data(), 257) == Result::InvalidArgument);
    assert(extended->enqueueSpawn(0, captures.data(), 256) == Result::Ok);
    assert(extended->processSpawns(1, processed) == Result::Ok && processed == 1);
    assert(extended->popEvent(event));
    assert(extended->evaluate(event.instanceId, 0) == Result::Ok);
    auto value = std::array<double, 4>{}; auto width = std::uint8_t{};
    assert(extended->value(event.instanceId, bytes.data() + 16, value.data(), width) == Result::Ok && value[0] == 255);
    context.pool = extended.get();
    auto beforeCleanup = context.cleanupCalls;
    extended.reset();
    assert(context.cleanupCalls == beforeCleanup + 1 && context.cleanupEnqueue == Result::Busy);
}
}

int main()
{
#if defined(_MSC_VER) && defined(_DEBUG)
    // A failing isolated test must report to CTest, not wait on a CRT dialog.
    _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE);
    _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
#endif
    lifecycleTests();
    // Template: scalar + local Time. The same template node GUIDs are used in each instance.
    auto childBytes = std::vector<std::uint8_t>(12 + 44 * 3);
    std::memcpy(childBytes.data(), "LFXB", 4);
    childBytes[4] = 1; childBytes[6] = 1; childBytes[8] = 3;
    for (auto i = 0u; i < 3; ++i) {
        auto offset = 12 + i * 44;
        childBytes[offset + 15] = static_cast<std::uint8_t>(i + 1);
        childBytes[offset + 16] = static_cast<std::uint8_t>(i + 1);
        write32(childBytes, offset + 20, UINT32_MAX);
        write32(childBytes, offset + 24, UINT32_MAX);
    }
    write32(childBytes, 12 + 2 * 44 + 20, 0);
    write32(childBytes, 12 + 2 * 44 + 24, 1);
    auto child = liberafx::Program{};
    assert(liberafx::Program::load(childBytes.data(), childBytes.size(), child) == liberafx::Result::Ok);
    {
        alignas(16) auto ioWork = std::array<double, 3>{};
        alignas(16) auto ioDefaults = std::array<double, 6>{};
        auto instance = liberafx::Instance{};
        assert(instance.initialize(child, ioWork.data(), ioWork.size(), ioDefaults.data(), ioDefaults.size()) == liberafx::Result::Ok);
        auto ports = std::array<liberafx::NamedIoPort, 2>{};
        ports[0].name = "Amount"; ports[0].kind = liberafx::NamedIoKind::InputProperty;
        ports[0].nodeId[15] = 1; ports[0].width = 1;
        ports[1].name = "Result"; ports[1].kind = liberafx::NamedIoKind::OutputProperty;
        ports[1].nodeId[15] = 3; ports[1].width = 1;
        auto io = std::unique_ptr<liberafx::NamedIo>{};
        assert(liberafx::NamedIo::create(instance, ports.data(), ports.size(), 2, io) == liberafx::Result::Ok);
        auto input = 7.0;
        assert(io->setProperty("Amount", &input, 1) == liberafx::Result::Ok);
        assert(io->start(0, nullptr, 0) == liberafx::Result::Ok);
        auto event = liberafx::NamedIoEvent{};
        assert(io->popEvent(event) && event.kind == liberafx::NamedIoKind::OutputProperty &&
               event.values[0] == 7 && std::strcmp(io->portName(event.portIndex), "Result") == 0);
        assert(io->start(0, nullptr, 0) == liberafx::Result::Ok);
        assert(io->update(0) == liberafx::Result::Ok && !io->popEvent(event));
        assert(io->update(1) == liberafx::Result::Ok);
        assert(io->popEvent(event) && event.values[0] == 8);
        ports[1].name = ports[0].name;
        auto duplicate = std::unique_ptr<liberafx::NamedIo>{};
        assert(liberafx::NamedIo::create(instance, ports.data(), ports.size(), 2, duplicate) == liberafx::Result::InvalidArgument);
    }
    auto container = std::array<std::uint8_t, 16>{}; container[15] = 20;
    auto pool = std::unique_ptr<liberafx::GeneratorPool>{};
    assert(liberafx::GeneratorPool::create(child, container.data(), 2, 65536, 8, pool) == liberafx::Result::Ok);

    // Actual v3 native signal dispatch invokes the host-neutral pool.
    auto signalBytes = std::vector<std::uint8_t>(16 + 256);
    std::memcpy(signalBytes.data(), "LFXB", 4);
    signalBytes[4] = 3; signalBytes[6] = 1; signalBytes[8] = 1; signalBytes[13] = 1;
    auto record = std::size_t{16};
    signalBytes[record + 15] = 10;
    signalBytes[record + 16] = 41;
    signalBytes[record + 18] = 1;
    signalBytes[record + 19] = 1;
    signalBytes[record + 39] = 42;
    write32(signalBytes, record + 40, 1);
    for (auto i = 0u; i < 4; ++i) {
        write32(signalBytes, record + 48 + i * 8, UINT32_MAX);
        write32(signalBytes, record + 208 + i * 4, UINT32_MAX);
    }
    double captureValue = 7;
    std::memcpy(signalBytes.data() + record + 80, &captureValue, sizeof(captureValue));
    auto context = Context{pool.get(), {}};
    context.target[15] = 1;
    auto descriptor = liberafx::CustomNodeDescriptor{};
    descriptor.typeId[15] = 42;
    descriptor.abiVersion = 1;
    descriptor.execution = liberafx::CustomExecution::Signal;
    descriptor.inputCount = 1;
    descriptor.inputWidths[0] = 1;
    descriptor.branchCount = 1;
    descriptor.function = signal;
    descriptor.context = &context;
    {
        auto ioContext = IoSignalContext{};
        auto ioDescriptor = descriptor;
        ioDescriptor.function = ioSignal;
        ioDescriptor.context = &ioContext;
        auto ioBinding = liberafx::CustomNodeBinding{};
        auto ioProgram = liberafx::Program{};
        assert(liberafx::Program::load(signalBytes.data(), signalBytes.size(), &ioDescriptor, 1,
                                       &ioBinding, 1, ioProgram) == liberafx::Result::Ok);
        alignas(16) auto ioWork = std::array<double, 4>{};
        auto instance = liberafx::Instance{};
        assert(instance.initialize(ioProgram, ioWork.data(), ioWork.size()) == liberafx::Result::Ok);
        auto ports = std::array<liberafx::NamedIoPort, 2>{};
        ports[0].name = "Fire"; ports[0].kind = liberafx::NamedIoKind::InputSignal; ports[0].nodeId[15] = 10;
        ports[1].name = "Finished"; ports[1].kind = liberafx::NamedIoKind::OutputSignal; ports[1].nodeId[15] = 10;
        auto io = std::unique_ptr<liberafx::NamedIo>{};
        auto identity = liberafx::NamedIoIdentity{};
        identity.containerId[15] = 20;
        identity.instanceId = 123;
        assert(liberafx::NamedIo::create(instance, ports.data(), ports.size(), 4, io, identity) == liberafx::Result::Ok);
        ioContext.io = io.get();
        assert(io->start(0, ports[0].nodeId, 4) == liberafx::Result::Ok);
        assert(io->start(1, ports[0].nodeId, 4) == liberafx::Result::Ok && ioContext.calls == 1);
        assert(io->fireSignal("Fire", 1, 4) == liberafx::Result::Ok && ioContext.calls == 2);
        auto event = liberafx::NamedIoEvent{};
        assert(io->popEvent(event) && event.kind == liberafx::NamedIoKind::OutputSignal &&
               event.identity.containerId[15] == 20 && event.identity.instanceId == 123 &&
               std::strcmp(io->portName(event.portIndex), "Finished") == 0);
        assert(io->popEvent(event) && !io->popEvent(event));
    }
    auto binding = liberafx::CustomNodeBinding{};
    auto parent = liberafx::Program{};
    assert(liberafx::Program::load(signalBytes.data(), signalBytes.size(), &descriptor, 1, &binding, 1, parent) == liberafx::Result::Ok);
    alignas(16) auto work = std::array<double, 4>{};
    auto parentInstance = liberafx::Instance{};
    assert(parentInstance.initialize(parent, work.data(), work.size()) == liberafx::Result::Ok);
    assert(parentInstance.updateAsync(0, inlineJob, nullptr) == liberafx::Result::Ok);
    assert(parentInstance.dispatchSignal(signalBytes.data() + record, 10, 4) == liberafx::Result::Ok);
    assert(parentInstance.dispatchSignal(signalBytes.data() + record, 12, 4) == liberafx::Result::Ok);
    auto processed = std::size_t{};
    assert(pool->processSpawns(2, processed) == liberafx::Result::Ok && processed == 2);
    assert(parentInstance.dispatchSignal(signalBytes.data() + record, 12, 4) == liberafx::Result::Ok);
    assert(pool->processSpawns(1, processed) == liberafx::Result::InsufficientWork && processed == 0);
    auto first = liberafx::GeneratorEvent{};
    auto second = liberafx::GeneratorEvent{};
    assert(pool->popEvent(first) && pool->popEvent(second));
    assert(first.instanceId != second.instanceId && first.startTime == 10 && second.startTime == 12);
    assert(std::memcmp(first.containerId, container.data(), 16) == 0);
    auto resultNode = childBytes.data() + 12 + 2 * 44;
    auto values = std::array<double, 4>{};
    auto width = std::uint8_t{};
    assert(pool->evaluate(first.instanceId, 15) == liberafx::Result::Ok);
    assert(pool->evaluate(second.instanceId, 15) == liberafx::Result::Ok);
    assert(pool->value(first.instanceId, resultNode, values.data(), width) == liberafx::Result::Ok && values[0] == 12);
    assert(pool->value(second.instanceId, resultNode, values.data(), width) == liberafx::Result::Ok && values[0] == 10);
    assert(pool->evaluate(second.instanceId, 14) == liberafx::Result::Ok);
    assert(pool->value(second.instanceId, resultNode, values.data(), width) == liberafx::Result::Ok && values[0] == 9);
    assert(pool->evaluate(second.instanceId, 15) == liberafx::Result::Ok);
    auto live = liberafx::GeneratorCapture{};
    std::memcpy(live.nodeId, childBytes.data() + 12, 16);
    live.value = 20;
    assert(pool->setLive(first.instanceId, &live, 1) == liberafx::Result::Ok);
    assert(pool->evaluate(first.instanceId, 15) == liberafx::Result::Ok);
    assert(pool->value(first.instanceId, resultNode, values.data(), width) == liberafx::Result::Ok && values[0] == 25);
    assert(pool->value(second.instanceId, resultNode, values.data(), width) == liberafx::Result::Ok && values[0] == 10);
    auto invalidLive = std::array<liberafx::GeneratorCapture, 2>{live, live};
    invalidLive[0].value = 30;
    std::memcpy(invalidLive[1].nodeId, resultNode, 16);
    assert(pool->setLive(first.instanceId, invalidLive.data(), invalidLive.size()) == liberafx::Result::InvalidValueType);
    assert(pool->evaluate(first.instanceId, 15) == liberafx::Result::Ok);
    assert(pool->value(first.instanceId, resultNode, values.data(), width) == liberafx::Result::Ok && values[0] == 25);
    auto invalidCapture = liberafx::GeneratorCapture{};
    std::memcpy(invalidCapture.nodeId, resultNode, 16);
    invalidCapture.value = 3;
    auto unused = liberafx::GeneratorInstanceId{};
    assert(pool->stop(first.instanceId) == liberafx::Result::Ok);
    assert(pool->spawn(15, &invalidCapture, 1, unused) == liberafx::Result::InvalidValueType && unused == 0);
    assert(pool->evaluate(first.instanceId, 16) == liberafx::Result::InvalidHandle);
    assert(pool->evaluate(second.instanceId, 16) == liberafx::Result::Ok);
    assert(pool->reset() == liberafx::Result::Ok && pool->activeCount() == 0);
    assert(pool->popEvent(first) && first.kind == liberafx::GeneratorEventKind::Stop);
    assert(pool->popEvent(second) && second.kind == liberafx::GeneratorEventKind::Stop);
    auto small = std::unique_ptr<liberafx::GeneratorPool>{};
    assert(liberafx::GeneratorPool::create(child, container.data(), 2, 1, 1, small) == liberafx::Result::InsufficientWork);
    assert(liberafx::GeneratorPool::create(child, container.data(), 2, 65536, 1, small) == liberafx::Result::Ok);
    auto smallId = liberafx::GeneratorInstanceId{};
    assert(small->spawn(1, nullptr, 0, smallId) == liberafx::Result::Ok);
    assert(small->stop(smallId) == liberafx::Result::QueueRejected);
    assert(small->enqueueSpawn(2, nullptr, 0) == liberafx::Result::Ok);
    assert(small->enqueueSpawn(2, nullptr, 0) == liberafx::Result::QueueRejected);
    assert(small->popEvent(first));
    assert(small->reset() == liberafx::Result::Ok);
    assert(small->processSpawns(1, processed) == liberafx::Result::Ok && processed == 0);

    auto stateBytes = std::vector<std::uint8_t>(16 + 256);
    std::memcpy(stateBytes.data(), "LFXB", 4);
    stateBytes[4] = 3; stateBytes[6] = 1; stateBytes[8] = 1; stateBytes[13] = 1;
    stateBytes[31] = 30; stateBytes[32] = 40; stateBytes[33] = 1;
    stateBytes[55] = 99; write32(stateBytes, 56, 1);
    for (auto i = 0u; i < 4; ++i) {
        write32(stateBytes, 64 + i * 8, UINT32_MAX);
        write32(stateBytes, 224 + i * 4, UINT32_MAX);
    }
    auto stateDescriptor = liberafx::CustomNodeDescriptor{};
    stateDescriptor.typeId[15] = 99;
    stateDescriptor.abiVersion = 1;
    stateDescriptor.outputWidth = 1;
    stateDescriptor.stateBytes = sizeof(double);
    stateDescriptor.stateAlignment = 64;
    stateDescriptor.initialize = stateInitialize;
    stateDescriptor.function = stateEvaluate;
    auto stateBinding = liberafx::CustomNodeBinding{};
    auto stateProgram = liberafx::Program{};
    assert(liberafx::Program::load(stateBytes.data(), stateBytes.size(), &stateDescriptor, 1,
        &stateBinding, 1, stateProgram) == liberafx::Result::Ok);
    assert(stateProgram.workAlignment() == 64);
    auto statePool = std::unique_ptr<liberafx::GeneratorPool>{};
    assert(liberafx::GeneratorPool::create(stateProgram, container.data(), 2, 65536, 4, statePool) == liberafx::Result::Ok);
    auto a = liberafx::GeneratorInstanceId{}, b = liberafx::GeneratorInstanceId{};
    assert(statePool->spawn(1, nullptr, 0, a) == liberafx::Result::Ok);
    assert(statePool->spawn(2, nullptr, 0, b) == liberafx::Result::Ok);
    assert(statePool->evaluate(a, 3) == liberafx::Result::Ok);
    assert(statePool->evaluate(a, 3) == liberafx::Result::Ok);
    assert(statePool->evaluate(b, 3) == liberafx::Result::Ok);
    assert(statePool->value(a, stateBytes.data() + 16, values.data(), width) == liberafx::Result::Ok && values[0] == 4);
    assert(statePool->value(b, stateBytes.data() + 16, values.data(), width) == liberafx::Result::Ok && values[0] == 2);
    assert(statePool->popEvent(first) && statePool->popEvent(second));
    assert(statePool->stop(a) == liberafx::Result::Ok);
    assert(statePool->stop(b) == liberafx::Result::Ok);
    assert(statePool->popEvent(first) && statePool->popEvent(second));
#if defined(_MSC_VER) && defined(_DEBUG)
    auto previousHook = _CrtSetAllocHook(allocationHook);
#endif
    for (auto i = 0; i < 1000; ++i) {
        assert(statePool->enqueueSpawn(2, nullptr, 0) == liberafx::Result::Ok);
        assert(statePool->processSpawns(1, processed) == liberafx::Result::Ok && processed == 1);
        assert(statePool->popEvent(first));
        assert(first.instanceId != a && first.instanceId != b);
        assert(statePool->evaluate(first.instanceId, 3) == liberafx::Result::Ok);
        assert(statePool->stop(first.instanceId) == liberafx::Result::Ok);
        assert(statePool->popEvent(second));
    }
#if defined(_MSC_VER) && defined(_DEBUG)
    _CrtSetAllocHook(previousHook);
    assert(frameAllocations == 0);
#endif

    auto vectorBytes = std::vector<std::uint8_t>(16 + 96);
    std::memcpy(vectorBytes.data(), "LFXB", 4);
    vectorBytes[4] = 2; vectorBytes[6] = 1; vectorBytes[8] = 1; vectorBytes[12] = 96;
    vectorBytes[31] = 50; vectorBytes[32] = 8; vectorBytes[33] = 3;
    write32(vectorBytes, 40, UINT32_MAX); write32(vectorBytes, 44, UINT32_MAX);
    auto vectorProgram = liberafx::Program{};
    assert(liberafx::Program::load(vectorBytes.data(), vectorBytes.size(), vectorProgram) == liberafx::Result::Ok);
    auto vectorPool = std::unique_ptr<liberafx::GeneratorPool>{};
    assert(liberafx::GeneratorPool::create(vectorProgram, container.data(), 2, 65536, 4, vectorPool) == liberafx::Result::Ok);
    auto captures = std::array<liberafx::GeneratorCapture, 3>{};
    for (auto i = 0u; i < captures.size(); ++i) {
        captures[i].nodeId[15] = 50;
        captures[i].component = static_cast<std::uint8_t>(i);
        captures[i].value = i + 1;
    }
    assert(vectorPool->spawn(0, captures.data(), captures.size(), a) == liberafx::Result::Ok);
    captures[0].value = 9;
    assert(vectorPool->spawn(0, captures.data(), captures.size(), b) == liberafx::Result::Ok);
    assert(vectorPool->evaluate(a, 0) == liberafx::Result::Ok);
    assert(vectorPool->evaluate(b, 0) == liberafx::Result::Ok);
    assert(vectorPool->value(a, vectorBytes.data() + 16, values.data(), width) == liberafx::Result::Ok &&
           width == 3 && values[0] == 1 && values[1] == 2 && values[2] == 3);
    assert(vectorPool->value(b, vectorBytes.data() + 16, values.data(), width) == liberafx::Result::Ok && values[0] == 9);
}
