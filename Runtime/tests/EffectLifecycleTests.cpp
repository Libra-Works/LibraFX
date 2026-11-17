#include "liberafx/EffectLifecycle.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <algorithm>
#include <array>
#include <cassert>
#include <cmath>
#include <cstring>
#include <cstdlib>
#include <fstream>
#include <iterator>
#include <limits>
#include <string>
#include <vector>
#if defined(_MSC_VER) && defined(_DEBUG)
#include <crtdbg.h>
#endif

namespace {
struct StartContext {
    liberafx::EffectLifecycle* lifecycle = nullptr;
    unsigned calls = 0;
    bool fail = false;
    bool defer = false;
    double delay = 0;
    liberafx::Result reentrant = liberafx::Result::Ok;
};

auto onStart(liberafx::CustomNodeInvocation& call) noexcept -> liberafx::Result
{
    auto& context = *static_cast<StartContext*>(call.context);
    ++context.calls;
    context.reentrant = context.lifecycle->stop();
    call.selectedBranch = 0;
    call.deferSignal = context.defer;
    call.delaySeconds = context.delay;
    return context.fail ? liberafx::Result::QueueRejected : liberafx::Result::Ok;
}

struct EvaluationContext {
    unsigned calls = 0;
    unsigned failAt = 1;
};

auto failEvaluation(liberafx::CustomNodeInvocation& call) noexcept -> liberafx::Result
{
    auto& context = *static_cast<EvaluationContext*>(call.context);
    ++context.calls;
    if (context.calls == context.failAt) return liberafx::Result::QueueRejected;
    call.outputs[0] = 3;
    return liberafx::Result::Ok;
}

void write32(std::array<std::uint8_t, 16 + 2 * 256>& bytes, std::size_t offset, std::uint32_t value)
{
    for (auto i = 0u; i < 4; ++i) bytes[offset + i] = static_cast<std::uint8_t>(value >> (i * 8));
}

void compiledFixture(const char* directory)
{
    auto root = std::string(directory) + "\\";
    auto binary = std::ifstream(root + "Launch.lfxb", std::ios::binary);
    auto identity = std::ifstream(root + "Fireworks.native-fixture", std::ios::binary);
    assert(binary && identity);
    auto bytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(binary), {});
    auto ids = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(identity), {});
    assert(bytes.size() >= 12 && ids.size() == 8 + 11 * 16);
    auto count = std::uint32_t{};
    for (auto i = 0u; i < 4; ++i) count |= std::uint32_t{bytes[8 + i]} << (i * 8);
    auto bindings = std::vector<liberafx::CustomNodeBinding>(count);
    auto program = liberafx::Program{};
    using liberafx::Result;
    assert(liberafx::Program::load(bytes.data(), bytes.size(), nullptr, 0,
        bindings.data(), bindings.size(), program) == Result::Ok);
    auto words = (program.workBytes() + sizeof(std::max_align_t) - 1) / sizeof(std::max_align_t);
    auto work = std::vector<std::max_align_t>(words);
    auto instance = liberafx::Instance{};
    assert(instance.initialize(program, reinterpret_cast<double*>(work.data()),
        work.size() * sizeof(std::max_align_t) / sizeof(double)) == Result::Ok);
    auto lifecycle = liberafx::EffectLifecycle(instance);
    auto duration = liberafx::Lifetime::infinite();
    assert(liberafx::Lifetime::finite(2, duration) == Result::Ok);
    auto sample = liberafx::LifetimeSample{};
    assert(lifecycle.start(duration, nullptr, 0, sample) == Result::Ok);
    auto first = std::array<double, 4>{};
    auto last = std::array<double, 4>{};
    auto width = std::uint8_t{};
    auto nodeId = ids.data() + 8 + 5 * 16;
    assert(instance.valueComponents(nodeId, first.data(), width) == Result::Ok);
    assert(lifecycle.advance(3, sample) == Result::Ok && sample.age == 2 && sample.expired);
    assert(instance.valueComponents(nodeId, last.data(), width) == Result::Ok);
    assert(width == 3 && first[2] != last[2]);
}
}

int main(int argc, char** argv)
{
#if defined(_MSC_VER) && defined(_DEBUG)
    _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE);
    _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif
    using liberafx::Result;
    auto bytes = std::array<std::uint8_t, 16 + 2 * 256>{};
    std::memcpy(bytes.data(), "LFXB", 4);
    bytes[4] = 3; bytes[6] = 1; bytes[8] = 2; bytes[13] = 1;
    bytes[16 + 15] = 1; bytes[16 + 16] = 2; bytes[16 + 17] = 1;
    write32(bytes, 16 + 24, UINT32_MAX);
    write32(bytes, 16 + 28, UINT32_MAX);
    auto offset = std::size_t{16 + 256};
    bytes[offset + 15] = 2;
    bytes[offset + 16] = 41;
    bytes[offset + 19] = 1;
    bytes[offset + 39] = 77;
    write32(bytes, offset + 40, 1);
    for (auto i = 0u; i < 4; ++i) write32(bytes, offset + 48 + i * 8, UINT32_MAX);
    for (auto i = 0u; i < 4; ++i) write32(bytes, offset + 208 + i * 4, UINT32_MAX);
    auto context = StartContext{};
    auto descriptor = liberafx::CustomNodeDescriptor{};
    descriptor.typeId[15] = 77;
    descriptor.abiVersion = 1;
    descriptor.execution = liberafx::CustomExecution::Signal;
    descriptor.branchCount = 1;
    descriptor.function = onStart;
    descriptor.context = &context;
    auto bindings = std::array<liberafx::CustomNodeBinding, 2>{};
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), &descriptor, 1,
                                  bindings.data(), bindings.size(), program) == Result::Ok);
    alignas(16) auto work = std::array<double, 16>{};
    alignas(16) auto defaults = std::array<double, 32>{};
    auto instance = liberafx::Instance{};
    assert(instance.initialize(program, work.data(), work.size(), defaults.data(), defaults.size()) == Result::Ok);
    auto lifecycle = liberafx::EffectLifecycle(instance);
    context.lifecycle = &lifecycle;
    auto duration = liberafx::Lifetime::infinite();
    assert(liberafx::Lifetime::finite(2, duration) == Result::Ok);
    auto sample = liberafx::LifetimeSample{};
    auto signalId = std::array<std::uint8_t, 16>{}; signalId[15] = 2;
    assert(lifecycle.start(duration, signalId.data(), 3, sample) == Result::Ok);
    assert(context.calls == 1 && context.reentrant == Result::Busy && sample.age == 0 && !sample.expired);
    assert(lifecycle.advance(0, sample) == Result::Ok && context.calls == 1);
    assert(lifecycle.advance(1, sample) == Result::Ok && sample.age == 1 && sample.remaining == 1);
    assert(lifecycle.advance(0.5, sample) == Result::InvalidArgument);
    assert(lifecycle.advance(std::numeric_limits<double>::quiet_NaN(), sample) == Result::InvalidArgument);
    assert(lifecycle.advance(3, sample) == Result::Ok && sample.age == 2 && sample.expired);
    auto timeId = std::array<std::uint8_t, 16>{}; timeId[15] = 1;
    auto time = std::array<double, 4>{};
    auto width = std::uint8_t{};
    assert(instance.valueComponents(timeId.data(), time.data(), width) == Result::Ok && time[0] == 2);
    assert(lifecycle.advance(3, sample) == Result::NotReady && context.calls == 1);
    assert(lifecycle.drain(2) == Result::Ok);
    assert(lifecycle.drain(4) == Result::Ok);
    assert(instance.valueComponents(timeId.data(), time.data(), width) == Result::Ok && time[0] == 4);
    assert(lifecycle.drain(3) == Result::InvalidArgument);
    assert(lifecycle.finish() == Result::Ok);
    assert(lifecycle.drain(5) == Result::NotReady && lifecycle.finish() == Result::NotReady);

    alignas(16) auto zeroWork = std::array<double, 16>{};
    alignas(16) auto zeroDefaults = std::array<double, 32>{};
    auto zeroInstance = liberafx::Instance{};
    assert(zeroInstance.initialize(program, zeroWork.data(), zeroWork.size(), zeroDefaults.data(), zeroDefaults.size()) == Result::Ok);
    auto zero = liberafx::EffectLifecycle(zeroInstance);
    context.lifecycle = &zero;
    assert(liberafx::Lifetime::finite(0, duration) == Result::Ok);
    assert(zero.start(duration, signalId.data(), 3, sample) == Result::Ok && sample.expired && context.calls == 2);
    assert(zero.advance(0, sample) == Result::NotReady);
    assert(zero.stop() == Result::Ok);
    assert(zero.finish() == Result::NotReady && zero.drain(0) == Result::NotReady);

    alignas(16) auto stopWork = std::array<double, 16>{};
    alignas(16) auto stopDefaults = std::array<double, 32>{};
    auto stopInstance = liberafx::Instance{};
    assert(stopInstance.initialize(program, stopWork.data(), stopWork.size(), stopDefaults.data(), stopDefaults.size()) == Result::Ok);
    auto stopped = liberafx::EffectLifecycle(stopInstance);
    assert(stopped.start(liberafx::Lifetime::infinite(), nullptr, 0, sample) == Result::Ok);
    assert(stopped.advance(10, sample) == Result::Ok && sample.age == 10 &&
           std::isinf(sample.remaining) && sample.normalizedAge == 0 && !sample.expired);
    assert(stopped.stop() == Result::Ok && stopped.advance(10, sample) == Result::NotReady);

    alignas(16) auto naturalEndWork = std::array<double, 16>{};
    auto naturalEndInstance = liberafx::Instance{};
    assert(naturalEndInstance.initialize(program, naturalEndWork.data(), naturalEndWork.size()) == Result::Ok);
    auto naturalEnd = liberafx::EffectLifecycle(naturalEndInstance);
    assert(naturalEnd.start(liberafx::Lifetime::infinite(), nullptr, 0, sample) == Result::Ok);
    assert(naturalEnd.advance(2, sample) == Result::Ok && sample.age == 2 && !sample.expired);
    assert(naturalEnd.schedule(3, 1) == Result::Ok);
    assert(naturalEnd.requestNaturalEnd() == Result::Ok &&
           naturalEnd.requestNaturalEnd() == Result::Ok &&
           naturalEnd.advance(3, sample) == Result::NotReady &&
           naturalEnd.destroy(signalId.data(), 3) == Result::NotReady &&
           naturalEnd.drain(3) == Result::Ok &&
           naturalEnd.validateSignalTime(3) == Result::Ok);
    auto naturalEndEvent = liberafx::ScheduledEvent{};
    auto hasNaturalEndEvent = false;
    assert(naturalEnd.takeScheduled(naturalEndEvent, hasNaturalEndEvent) == Result::Ok &&
           hasNaturalEndEvent && naturalEndEvent.token == 1 && naturalEndEvent.time == 3);
    context.lifecycle = &naturalEnd;
    auto destroyCalls = context.calls;
    assert(naturalEnd.destroy(signalId.data(), 3) == Result::Ok && context.calls == destroyCalls + 1);
    assert(naturalEnd.finish() == Result::Ok);

    alignas(16) auto scheduledWork = std::array<double, 16>{};
    auto scheduledInstance = liberafx::Instance{};
    assert(scheduledInstance.initialize(program, scheduledWork.data(), scheduledWork.size()) == Result::Ok);
    auto scheduled = liberafx::EffectLifecycle(scheduledInstance);
    assert(liberafx::Lifetime::finite(1, duration) == Result::Ok);
    assert(scheduled.start(duration, nullptr, 0, sample) == Result::Ok);
    assert(scheduled.schedule(2, 1) == Result::Ok);
    assert(scheduled.schedule(1.5, 2) == Result::Ok);
    assert(scheduled.schedule(1.5, 3) == Result::Ok);
    assert(scheduled.schedule(1, 0) == Result::InvalidArgument);
    assert(scheduled.advance(10, sample) == Result::Ok && sample.expired && sample.age == 1);
    assert(scheduled.finish() == Result::NotReady);
    assert(scheduled.destroy(signalId.data(), 3) == Result::NotReady);
    assert(scheduled.drain(2) == Result::NotReady);
    assert(scheduled.drain(1.5) == Result::Ok);
    auto scheduledEvent = liberafx::ScheduledEvent{};
    auto hasScheduled = false;
    assert(scheduled.takeScheduled(scheduledEvent, hasScheduled) == Result::Ok &&
           hasScheduled && scheduledEvent.token == 2 && scheduledEvent.time == 1.5);
    assert(scheduled.takeScheduled(scheduledEvent, hasScheduled) == Result::Ok &&
           hasScheduled && scheduledEvent.token == 3);
    assert(scheduled.takeScheduled(scheduledEvent, hasScheduled) == Result::Ok && !hasScheduled);
    assert(scheduled.schedule(1.4, 4) == Result::InvalidArgument);
    assert(scheduled.drain(2) == Result::Ok);
    assert(scheduled.takeScheduled(scheduledEvent, hasScheduled) == Result::Ok &&
           hasScheduled && scheduledEvent.token == 1);
    context.lifecycle = &scheduled;
    assert(scheduled.destroy(signalId.data(), 3) == Result::Ok &&
           context.calls == 3 && context.reentrant == Result::Busy);
    assert(scheduled.destroy(signalId.data(), 3) == Result::Ok && context.calls == 3);
    assert(scheduled.finish() == Result::Ok);

    alignas(16) auto deferredWork = std::array<double, 16>{};
    auto deferredContext = StartContext{};
    auto deferredDescriptor = descriptor;
    deferredDescriptor.context = &deferredContext;
    auto deferredBindings = std::array<liberafx::CustomNodeBinding, 2>{};
    auto deferredProgram = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), &deferredDescriptor, 1,
        deferredBindings.data(), deferredBindings.size(), deferredProgram) == Result::Ok);
    auto deferredInstance = liberafx::Instance{};
    assert(deferredInstance.initialize(deferredProgram, deferredWork.data(), deferredWork.size()) == Result::Ok);
    auto deferred = liberafx::EffectLifecycle(deferredInstance);
    deferredContext.lifecycle = &deferred;
    deferredContext.defer = true;
    deferredContext.delay = 2;
    assert(liberafx::Lifetime::finite(1, duration) == Result::Ok);
    assert(deferred.start(duration, signalId.data(), 3, sample) == Result::Ok);
    auto deferredTime = double{};
    auto hasDeferred = false;
    assert(deferred.nextDeferredSignal(deferredTime, hasDeferred) == Result::Ok && hasDeferred && deferredTime == 2);
    assert(deferred.advance(3, sample) == Result::Ok && sample.expired && sample.age == 1);
    assert(deferred.finish() == Result::NotReady);
    assert(deferred.destroy(signalId.data(), 3) == Result::NotReady);
    assert(deferred.drain(3) == Result::NotReady);
    assert(deferred.drain(2) == Result::Ok);
    assert(deferred.dispatchDeferredSignal(2, 3) == Result::Ok);
    assert(deferred.nextDeferredSignal(deferredTime, hasDeferred) == Result::Ok && !hasDeferred);
    assert(deferred.finish() == Result::Ok);

    alignas(16) auto queueWork = std::array<double, 16>{};
    auto queueInstance = liberafx::Instance{};
    assert(queueInstance.initialize(program, queueWork.data(), queueWork.size()) == Result::Ok);
    auto queue = liberafx::EffectLifecycle(queueInstance);
    assert(queue.start(liberafx::Lifetime::infinite(), nullptr, 0, sample) == Result::Ok);
    for (auto i = 0u; i < 128; ++i) assert(queue.schedule(1, i + 1) == Result::Ok);
    assert(queue.schedule(1, 129) == Result::QueueRejected);
    assert(queue.stop() == Result::Ok && queue.nextScheduled(scheduledEvent, hasScheduled) == Result::NotReady);

    alignas(16) auto failedWork = std::array<double, 16>{};
    alignas(16) auto failedDefaults = std::array<double, 32>{};
    auto failedInstance = liberafx::Instance{};
    assert(failedInstance.initialize(program, failedWork.data(), failedWork.size(), failedDefaults.data(), failedDefaults.size()) == Result::Ok);
    auto failed = liberafx::EffectLifecycle(failedInstance);
    context.lifecycle = &failed;
    context.fail = true;
    assert(failed.start(duration, signalId.data(), 3, sample) == Result::QueueRejected);
    assert(failed.start(duration, signalId.data(), 3, sample) == Result::QueueRejected && context.calls == 4);
    assert(failed.stop() == Result::Ok);

    alignas(16) auto destroyFailureWork = std::array<double, 16>{};
    auto destroyFailureInstance = liberafx::Instance{};
    assert(destroyFailureInstance.initialize(program, destroyFailureWork.data(), destroyFailureWork.size()) == Result::Ok);
    auto destroyFailure = liberafx::EffectLifecycle(destroyFailureInstance);
    context.lifecycle = &destroyFailure;
    assert(liberafx::Lifetime::finite(0, duration) == Result::Ok);
    assert(destroyFailure.start(duration, nullptr, 0, sample) == Result::Ok && sample.expired);
    assert(destroyFailure.destroy(signalId.data(), 3) == Result::QueueRejected);
    assert(destroyFailure.destroy(signalId.data(), 3) == Result::QueueRejected && context.calls == 5);
    assert(destroyFailure.stop() == Result::Ok);

    auto failureBytes = bytes;
    std::fill(failureBytes.begin() + 16, failureBytes.begin() + 16 + 256, 0);
    failureBytes[16 + 15] = 1;
    failureBytes[16 + 16] = 40;
    failureBytes[16 + 17] = 1;
    failureBytes[16 + 39] = 78;
    write32(failureBytes, 16 + 40, 1);
    for (auto i = 0u; i < 4; ++i) write32(failureBytes, 16 + 48 + i * 8, UINT32_MAX);
    for (auto i = 0u; i < 4; ++i) write32(failureBytes, 16 + 208 + i * 4, UINT32_MAX);
    auto evaluationContext = EvaluationContext{};
    auto descriptors = std::array<liberafx::CustomNodeDescriptor, 2>{descriptor, {}};
    descriptors[1].typeId[15] = 78;
    descriptors[1].abiVersion = 1;
    descriptors[1].execution = liberafx::CustomExecution::Parameter;
    descriptors[1].outputWidth = 1;
    descriptors[1].function = failEvaluation;
    descriptors[1].context = &evaluationContext;
    auto failureBindings = std::array<liberafx::CustomNodeBinding, 2>{};
    auto failureProgram = liberafx::Program{};
    assert(liberafx::Program::load(failureBytes.data(), failureBytes.size(), descriptors.data(),
        descriptors.size(), failureBindings.data(), failureBindings.size(), failureProgram) == Result::Ok);
    alignas(16) auto evaluationWork = std::array<double, 16>{};
    auto evaluationInstance = liberafx::Instance{};
    assert(evaluationInstance.initialize(failureProgram, evaluationWork.data(), evaluationWork.size()) == Result::Ok);
    auto evaluation = liberafx::EffectLifecycle(evaluationInstance);
    assert(evaluation.start(liberafx::Lifetime::infinite(), nullptr, 0, sample) == Result::QueueRejected);
    assert(evaluation.start(liberafx::Lifetime::infinite(), nullptr, 0, sample) == Result::QueueRejected &&
           evaluationContext.calls == 1);
    assert(evaluation.stop() == Result::Ok);

    alignas(16) auto laterWork = std::array<double, 16>{};
    auto laterInstance = liberafx::Instance{};
    assert(laterInstance.initialize(failureProgram, laterWork.data(), laterWork.size()) == Result::Ok);
    auto later = liberafx::EffectLifecycle(laterInstance);
    evaluationContext = {0, 2};
    assert(later.start(liberafx::Lifetime::infinite(), nullptr, 0, sample) == Result::Ok);
    assert(later.advance(1, sample) == Result::QueueRejected);
    assert(later.advance(1, sample) == Result::NotReady && evaluationContext.calls == 2);
    assert(later.stop() == Result::Ok);

    alignas(16) auto capturedWork = std::array<double, 16>{};
    auto capturedInstance = liberafx::Instance{};
    assert(capturedInstance.initialize(failureProgram, capturedWork.data(), capturedWork.size()) == Result::Ok);
    auto captured = liberafx::EffectLifecycle(capturedInstance);
    context.lifecycle = &captured;
    context.fail = false;
    evaluationContext = {0, 0};
    assert(captured.startCaptured(timeId.data(), signalId.data(), 3, sample) == Result::Ok);
    assert(evaluationContext.calls == 1 && sample.remaining == 3 && !sample.expired);
    assert(captured.advance(5, sample) == Result::Ok && sample.age == 3 && sample.expired);
    assert(evaluationContext.calls == 2);
    if (argc == 2) compiledFixture(argv[1]);
}
