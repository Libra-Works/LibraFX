#include "liberafx/GeneratorPool.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
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
}

int main()
{
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
