#include "liberafx/LbSpawnablePackage.h"
#include "ProgramFixture.h"
#include "liberafx/LbVfxRuntime.h"
#include "liberafx/platform/LbProfileRecorder.h"

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iterator>
#include <limits>
#include <thread>
#include <vector>
#ifdef _MSC_VER
#include <crtdbg.h>
#endif

namespace {

struct Queue {
    liberafx::Job job = nullptr;
    void* data = nullptr;
};

auto enqueue(void* context, liberafx::Job job, void* data) noexcept -> bool
{
    auto queue = static_cast<Queue*>(context);
    if (queue->job != nullptr) return false;
    queue->job = job;
    queue->data = data;
    return true;
}

void run(Queue& queue)
{
    auto job = queue.job;
    auto data = queue.data;
    queue = {};
    assert(job != nullptr);
    job(data);
}

void write32(std::vector<std::uint8_t>& bytes, std::size_t offset, std::uint32_t value)
{
    for (auto index = 0u; index < 4; ++index)
        bytes[offset + index] = static_cast<std::uint8_t>(value >> (index * 8));
}

void write16(std::vector<std::uint8_t>& bytes, std::size_t offset, std::uint16_t value)
{
    bytes[offset] = static_cast<std::uint8_t>(value);
    bytes[offset + 1] = static_cast<std::uint8_t>(value >> 8);
}

void writeDouble(std::vector<std::uint8_t>& bytes, std::size_t offset, double value)
{
    auto bits = std::uint64_t{};
    std::memcpy(&bits, &value, sizeof(value));
    for (auto index = 0u; index < 8; ++index)
        bytes[offset + index] = static_cast<std::uint8_t>(bits >> (index * 8));
}

auto curveFixture(std::uint8_t interpolation) -> std::vector<std::uint8_t>
{
    auto bytes = std::vector<std::uint8_t>(16 + 256 + 2 * 40);
    std::memcpy(bytes.data(), "LFXB", 4);
    write16(bytes, 4, 4);
    bytes[6] = 1;
    write32(bytes, 8, 1);
    write16(bytes, 12, 256);
    auto instruction = std::size_t{16};
    bytes[instruction + 15] = 1;
    bytes[instruction + 16] = 42;
    bytes[instruction + 17] = 1;
    write32(bytes, instruction + 24, 0xffffffffu);
    write32(bytes, instruction + 28, 0xffffffffu);
    write32(bytes, instruction + 224, 0);
    write32(bytes, instruction + 228, 2);
    auto first = std::size_t{272};
    writeDouble(bytes, first, 0);
    writeDouble(bytes, first + 8, 0);
    writeDouble(bytes, first + 24, 2);
    bytes[first + 32] = interpolation;
    auto second = first + 40;
    writeDouble(bytes, second, 1);
    writeDouble(bytes, second + 8, 1);
    return runtime_test_fixture::promoteSimpleProgram(bytes);
}

void verifyCurve(std::uint8_t interpolation, double time, double expected)
{
    auto bytes = curveFixture(interpolation);
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), program) == liberafx::Result::Ok);
    assert(program.version() == 11 && program.nodeCount() == 1 &&
           program.workBytes() >= 4 * sizeof(double) && program.workAlignment() == 16);
    alignas(16) auto work = std::array<double, 128>{};
    auto defaults = std::array<double, 16>{};
    auto instance = liberafx::Instance{};
    auto queue = Queue{};
    assert(instance.initialize(program, work.data(), work.size(), defaults.data(), defaults.size()) ==
           liberafx::Result::Ok);
    assert(instance.updateAsync(time, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    auto value = double{};
    assert(instance.value(bytes.data() + 16, value) == liberafx::Result::Ok);
    assert(std::abs(value - expected) < 1e-12);
}

void verifyCurveOverrides()
{
    auto bytes = curveFixture(1);
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), program) == liberafx::Result::Ok);
    auto first = liberafx::Instance{};
    auto second = liberafx::Instance{};
    alignas(16) auto firstWork = std::array<double, 128>{};
    alignas(16) auto secondWork = std::array<double, 128>{};
    assert(first.initialize(program, firstWork.data(), firstWork.size()) == liberafx::Result::Ok);
    assert(second.initialize(program, secondWork.data(), secondWork.size()) == liberafx::Result::Ok);
    auto index = std::uint32_t{};
    assert(liberafx::Instance::findCurve(program, bytes.data() + 16, index) == liberafx::Result::Ok);
    auto keys = std::array<liberafx::CurveKey, 2>{{{0, 10, 0, 0, 1}, {1, 20, 0, 0, 1}}};
    auto view = liberafx::CurveOverrideView{index, keys.data(), keys.size()};
    assert(first.setCurveOverrides(&view, 1) == liberafx::Result::Ok);
    auto queue = Queue{};
    assert(first.updateAsync(0.5, enqueue, &queue) == liberafx::Result::Ok);
    assert(first.setCurveOverrides(nullptr, 0) == liberafx::Result::Busy);
    run(queue);
    auto result = double{};
    assert(first.value(bytes.data() + 16, result) == liberafx::Result::Ok && result == 15);
    assert(second.updateAsync(0.5, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    assert(second.value(bytes.data() + 16, result) == liberafx::Result::Ok && result == 0.5);
    auto invalid = keys;
    invalid[1].time = 0;
    auto bad = liberafx::CurveOverrideView{index, invalid.data(), invalid.size()};
    assert(first.setCurveOverrides(&bad, 1) == liberafx::Result::InvalidArgument);
    assert(first.setCurveOverrides(nullptr, 0) == liberafx::Result::Ok);
    assert(first.updateAsync(0.5, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    assert(first.value(bytes.data() + 16, result) == liberafx::Result::Ok && result == 0.5);
}

void verifyCurveBytecode()
{
    verifyCurveOverrides();
    verifyCurve(0, 0.5, 0);
    verifyCurve(1, 0.25, 0.25);
    verifyCurve(2, 0.5, 0.75);
    verifyCurve(1, -1, 0);
    verifyCurve(1, 2, 1);

    auto bytes = curveFixture(1);
    auto program = liberafx::Program{};
    auto malformed = bytes;
    write32(malformed, 16 + 224, 81);
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) ==
           liberafx::Result::InvalidBinary);
    malformed = bytes;
    writeDouble(malformed, 312, -1);
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) ==
           liberafx::Result::InvalidBinary);
    malformed = bytes;
    malformed[272 + 32] = 3;
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) ==
           liberafx::Result::InvalidBinary);
}

auto fixture() -> std::vector<std::uint8_t>
{
    auto bytes = std::vector<std::uint8_t>(12 + 44 * 3);
    std::memcpy(bytes.data(), "LFXB", 4);
    bytes[4] = 1;
    bytes[6] = 1;
    write32(bytes, 8, 3);
    for (auto index = 0u; index < 3; ++index)
    {
        auto offset = 12 + index * 44;
        bytes[offset + 15] = static_cast<std::uint8_t>(index + 1);
        bytes[offset + 16] = static_cast<std::uint8_t>(index + 1);
        write32(bytes, offset + 20, 0xffffffffu);
        write32(bytes, offset + 24, 0xffffffffu);
    }
    writeDouble(bytes, 12 + 28, 3);
    write32(bytes, 12 + 2 * 44 + 20, 0);
    write32(bytes, 12 + 2 * 44 + 24, 1);
    return runtime_test_fixture::promoteSimpleProgram(bytes);
}

void verify(const std::vector<std::uint8_t>& bytes)
{
    auto shiftedBytes = std::vector<std::uint8_t>(bytes.size() + 1);
    std::memcpy(shiftedBytes.data() + 1, bytes.data(), bytes.size());
    auto shiftedProgram = liberafx::Program{};
    assert(liberafx::Program::load(shiftedBytes.data() + 1, bytes.size(), shiftedProgram) ==
           liberafx::Result::Ok);
    auto required = (shiftedProgram.workBytes() + sizeof(double) - 1) / sizeof(double);
    assert(required <= 256 && shiftedProgram.workAlignment() <= 16);

    alignas(16) auto misalignedWorkStorage = std::array<std::uint8_t, 2049>{};
    misalignedWorkStorage.fill(0xa5);
    auto misalignedWork = reinterpret_cast<double*>(misalignedWorkStorage.data() + 1);
    auto shiftedInstance = liberafx::Instance{};
    assert(shiftedInstance.initialize(shiftedProgram, misalignedWork, required) ==
           liberafx::Result::InvalidArgument);
    for (auto byte : misalignedWorkStorage) assert(byte == 0xa5);
    alignas(16) auto alignedWork = std::array<double, 256>{};
    alignas(16) auto misalignedDefaultsStorage = std::array<std::uint8_t, 385>{};
    misalignedDefaultsStorage.fill(0x5a);
    auto misalignedDefaults = reinterpret_cast<double*>(misalignedDefaultsStorage.data() + 1);
    assert(shiftedInstance.initialize(shiftedProgram, alignedWork.data(), alignedWork.size(),
                                      misalignedDefaults, 48) == liberafx::Result::InvalidArgument);
    for (auto byte : misalignedDefaultsStorage) assert(byte == 0x5a);

    auto profileCounters = std::array<liberafx::ProfileCounter, 16>{};
    auto recorder = liberafx::ProfileRecorder{};
    recorder.initialize(profileCounters.data(), profileCounters.size());
    auto profiledProgram = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), profiledProgram, recorder.hooks()) == liberafx::Result::Ok);
    alignas(16) auto profiledWork = std::array<double, 256>{};
    auto profiled = liberafx::Instance{};
    assert(profiled.setProfiling(recorder.hooks(true, true)) == liberafx::Result::Ok);
    assert(profiled.initialize(profiledProgram, profiledWork.data(), profiledWork.size()) == liberafx::Result::Ok);
    auto profiledQueue = Queue{};
    assert(profiled.updateAsync(7, liberafx::JobScheduler{enqueue, &profiledQueue}) == liberafx::Result::Ok);
    assert(profiled.setProfiling({}) == liberafx::Result::Busy);
    run(profiledQueue);
    auto profile = liberafx::ProfileMeasurement{};
    assert(profiled.result() == liberafx::Result::Ok);
    assert(recorder.read(static_cast<size_t>(liberafx::ProfileEvent::Evaluate), profile) && profile.calls == 1);
    assert(profile.exclusiveNs <= profile.inclusiveNs);
    assert(recorder.read(static_cast<size_t>(liberafx::ProfileEvent::Count) + 2, profile) && profile.calls == 1);
    auto profiledValue = double{};
    auto profiledId = bytes.data() + 16 + 2 * 256;
    assert(profiled.value(profiledId, profiledValue) == liberafx::Result::Ok && profiledValue == 10);
    assert(profiled.setProfiling({}) == liberafx::Result::Ok);
    assert(profiled.updateAsync(8, liberafx::JobScheduler{enqueue, &profiledQueue}) == liberafx::Result::Ok);
    run(profiledQueue);
    assert(profiled.result() == liberafx::Result::Ok);
    assert(profiled.value(profiledId, profiledValue) == liberafx::Result::Ok && profiledValue == 11);
    assert(recorder.read(static_cast<size_t>(liberafx::ProfileEvent::Evaluate), profile) && profile.calls == 1);
    assert(recorder.read(static_cast<size_t>(liberafx::ProfileEvent::Count) + 2, profile) && profile.calls == 1);
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), program) == liberafx::Result::Ok);
    assert(program.nodeCount() == 3 && program.workBytes() >= 12 * sizeof(double));
    alignas(16) auto work = std::array<double, 256>{};
    alignas(16) auto workOther = std::array<double, 256>{};
    auto instance = liberafx::Instance{};
    auto other = liberafx::Instance{};
    assert(instance.initialize(program, work.data(), required - 1) == liberafx::Result::InsufficientWork);
    assert(instance.initialize(program, work.data(), work.size()) == liberafx::Result::Ok);
    assert(other.initialize(program, workOther.data(), workOther.size()) == liberafx::Result::Ok);
    auto first = Queue{};
    auto second = Queue{};
    assert(instance.updateAsync(7, enqueue, &first) == liberafx::Result::Ok);
    assert(instance.updateAsync(8, enqueue, &first) == liberafx::Result::Busy);
    assert(instance.result() == liberafx::Result::NotReady);
    assert(other.updateAsync(2, enqueue, &second) == liberafx::Result::Ok);
    auto firstWorker = std::thread([&first] { run(first); });
    auto secondWorker = std::thread([&second] { run(second); });
    firstWorker.join();
    secondWorker.join();
    auto value = double{};
    auto id = bytes.data() + 16 + 2 * 256;
    assert(instance.value(id, value) == liberafx::Result::Ok && value == 10);
    assert(other.value(id, value) == liberafx::Result::Ok && value == 5);
    assert(instance.capacityGrowthCount() == 0);

    alignas(16) auto parameterWork = std::array<double, 256>{};
    auto parameterDefaults = std::array<double, 48>{};
    auto parameterInstance = liberafx::Instance{};
    auto parameterQueue = Queue{};
    assert(parameterInstance.initialize(program, parameterWork.data(), parameterWork.size(),
                                        parameterDefaults.data(), parameterDefaults.size()) == liberafx::Result::Ok);
    assert(parameterInstance.updateAsync(7, enqueue, &parameterQueue) == liberafx::Result::Ok);
    run(parameterQueue);
    auto scalarId = bytes.data() + 16;
    assert(parameterInstance.setParameter(scalarId, 0, 0, 4) == liberafx::Result::Ok);
    assert(parameterInstance.updateAsync(7, enqueue, &parameterQueue) == liberafx::Result::Ok);
    run(parameterQueue);
    assert(parameterInstance.value(id, value) == liberafx::Result::Ok && value == 11);
}

auto typedFixture() -> std::vector<std::uint8_t>
{
    auto bytes = std::vector<std::uint8_t>(16 + 11 * 96);
    std::memcpy(bytes.data(), "LFXB", 4);
    write16(bytes, 4, 2);
    bytes[6] = 1;
    write32(bytes, 8, 11);
    write16(bytes, 12, 96);
    auto writeNode = [&](auto index, auto opcode, auto outputWidth, auto inputAWidth, auto inputBWidth,
                         auto inputA, auto inputB, std::uint8_t typeFlags = 0) {
        auto offset = 16 + index * 96;
        bytes[offset + 15] = static_cast<std::uint8_t>(index + 1);
        bytes[offset + 16] = static_cast<std::uint8_t>(opcode);
        bytes[offset + 17] = static_cast<std::uint8_t>(outputWidth);
        bytes[offset + 18] = static_cast<std::uint8_t>(inputAWidth);
        bytes[offset + 19] = static_cast<std::uint8_t>(inputBWidth);
        bytes[offset + 20] = static_cast<std::uint8_t>(typeFlags);
        write32(bytes, offset + 24, inputA);
        write32(bytes, offset + 28, inputB);
    };
    auto writeLane = [&](auto index, auto base, auto lane, auto value) {
        writeDouble(bytes, 16 + index * 96 + base + lane * 8, value);
    };
    writeNode(0, 9, 4, 0, 0, 0xffffffffu, 0xffffffffu);
    writeLane(0, 32, 0, 2);
    writeLane(0, 32, 1, -3);
    writeLane(0, 32, 2, 4.5);
    writeLane(0, 32, 3, 2.5);
    writeNode(1, 35, 2, 4, 0, 0, 0xffffffffu);
    writeLane(1, 32, 0, 3);
    writeLane(1, 32, 1, 0);
    writeNode(2, 28, 4, 4, 0, 0, 0xffffffffu);
    writeNode(3, 39, 4, 4, 0, 0, 0xffffffffu, 1);
    writeNode(4, 38, 4, 4, 0, 3, 0xffffffffu, 2);
    writeNode(5, 21, 4, 4, 4, 0, 0);
    writeNode(6, 30, 4, 2, 0, 1, 0xffffffffu);
    writeLane(6, 32, 0, 7);
    for (auto component = 0u; component < 4; ++component)
    {
        writeNode(7 + component, 23, 2, 1, 0, 2, 0xffffffffu);
        bytes[16 + (7 + component) * 96 + 21] = static_cast<std::uint8_t>(component);
    }
    return runtime_test_fixture::promoteSimpleProgram(bytes);
}

struct CustomCounters {
    std::uint32_t calls = 0;
    double observed = 0;
};

struct StateCounters {
    std::uint32_t initializations = 0;
    std::uint32_t cleanups = 0;
    bool fail = false;
    std::size_t requiredAlignment = 16;
};

auto initializeCustomState(void* state, std::size_t stateBytes, void* context) noexcept -> liberafx::Result
{
    auto counters = static_cast<StateCounters*>(context);
    ++counters->initializations;
    if (reinterpret_cast<std::uintptr_t>(state) % counters->requiredAlignment != 0)
        return liberafx::Result::InvalidArgument;
    if (stateBytes >= sizeof(std::uint64_t)) *static_cast<std::uint64_t*>(state) = 1;
    return counters->fail ? liberafx::Result::InvalidArgument : liberafx::Result::Ok;
}

void cleanupCustomState(void*, std::size_t, void* context) noexcept
{
    ++static_cast<StateCounters*>(context)->cleanups;
}

auto customCopy(liberafx::CustomNodeInvocation& invocation) noexcept -> liberafx::Result
{
    auto counters = static_cast<CustomCounters*>(invocation.context);
    ++counters->calls;
    invocation.outputs[0] = invocation.inputs[0];
    return liberafx::Result::Ok;
}

auto customIncrement(liberafx::CustomNodeInvocation& invocation) noexcept -> liberafx::Result
{
    auto counters = static_cast<CustomCounters*>(invocation.context);
    ++counters->calls;
    invocation.outputs[0] = invocation.inputs[0] + 1;
    return liberafx::Result::Ok;
}

auto customBranch(liberafx::CustomNodeInvocation& invocation) noexcept -> liberafx::Result
{
    auto counters = static_cast<CustomCounters*>(invocation.context);
    ++counters->calls;
    counters->observed = invocation.inputs[0];
    invocation.selectedBranch = invocation.inputs[0] == 0 ? 0 : 1;
    return liberafx::Result::Ok;
}

auto customEnd(liberafx::CustomNodeInvocation& invocation) noexcept -> liberafx::Result
{
    auto counters = static_cast<CustomCounters*>(invocation.context);
    ++counters->calls;
    counters->observed = invocation.inputs[0];
    invocation.selectedBranch = 0;
    return liberafx::Result::Ok;
}

struct ReentrantSignalContext {
    liberafx::Instance* instance = nullptr;
    liberafx::SignalHandle entry{};
    liberafx::ParameterHandle parameter{};
    bool visited = false;
};

auto reentrantSignal(liberafx::CustomNodeInvocation& invocation) noexcept -> liberafx::Result
{
    auto context = static_cast<ReentrantSignalContext*>(invocation.context);
    auto queue = Queue{};
    assert(context->instance->dispatchSignal(context->entry, 0, 10) == liberafx::Result::Busy);
    assert(context->instance->updateAsync(0, enqueue, &queue) == liberafx::Result::Busy);
    assert(context->instance->setParameter(context->parameter, 2) == liberafx::Result::Busy);
    context->visited = true;
    invocation.selectedBranch = 0;
    return liberafx::Result::Ok;
}

auto customFixture() -> std::vector<std::uint8_t>
{
    auto bytes = std::vector<std::uint8_t>(16 + 3 * 256);
    std::memcpy(bytes.data(), "LFXB", 4);
    write16(bytes, 4, 3);
    bytes[6] = 1;
    write32(bytes, 8, 3);
    write16(bytes, 12, 256);
    const auto typeIds = std::array<std::array<std::uint8_t, 16>, 3>{
        std::array<std::uint8_t, 16>{0xa1}, std::array<std::uint8_t, 16>{0xa2},
        std::array<std::uint8_t, 16>{0xa3}};
    auto writeCustom = [&](std::size_t index, std::uint8_t opcode, std::uint8_t width,
                           std::uint8_t inputCount, std::uint8_t branchCount, std::size_t type) {
        auto offset = 16 + index * 256;
        bytes[offset + 15] = static_cast<std::uint8_t>(index + 1);
        bytes[offset + 16] = opcode;
        bytes[offset + 17] = width;
        bytes[offset + 18] = inputCount;
        bytes[offset + 19] = branchCount;
        std::memcpy(bytes.data() + offset + 24, typeIds[type].data(), 16);
        write32(bytes, offset + 40, 1);
        for (auto input = 0u; input < 4; ++input)
            write32(bytes, offset + 48 + input * 8, 0xffffffffu);
        for (auto branch = 0u; branch < 4; ++branch)
            write32(bytes, offset + 208 + branch * 4, 0xffffffffu);
    };
    writeCustom(0, 40, 1, 1, 0, 0);
    writeDouble(bytes, 16 + 80, 5);
    writeCustom(1, 40, 1, 1, 0, 1);
    write32(bytes, 16 + 256 + 48, 0);
    bytes[16 + 256 + 48 + 4] = 1;
    writeCustom(2, 40, 1, 1, 0, 2);
    write32(bytes, 16 + 2 * 256 + 48, 0);
    bytes[16 + 2 * 256 + 48 + 4] = 1;
    return bytes;
}

struct FanoutLog {
    std::array<unsigned, 16> nodes{};
    std::size_t count = 0;
    unsigned failNode = 99;
};

struct FanoutContext {
    FanoutLog* log = nullptr;
    unsigned node = 0;
};

auto recordFanout(liberafx::CustomNodeInvocation& invocation) noexcept -> liberafx::Result
{
    auto context = static_cast<FanoutContext*>(invocation.context);
    context->log->nodes[context->log->count++] = context->node;
    if (context->node == context->log->failNode) return liberafx::Result::InvalidArgument;
    invocation.selectedBranch = 0;
    return liberafx::Result::Ok;
}

struct DeferredTestContext {
    FanoutLog* log = nullptr;
    double delay = 0;
};

auto recordDeferred(liberafx::CustomNodeInvocation& invocation) noexcept -> liberafx::Result
{
    auto context = static_cast<DeferredTestContext*>(invocation.context);
    context->log->nodes[context->log->count++] = 0;
    invocation.selectedBranch = 0;
    invocation.deferSignal = true;
    invocation.delaySeconds = context->delay;
    return liberafx::Result::Ok;
}

void verifySignalFanout()
{
    auto bytes = std::vector<std::uint8_t>(16 + 5 * 256 + 8);
    std::memcpy(bytes.data(), "LFXB", 4);
    write16(bytes, 4, 5);
    bytes[6] = 1;
    write32(bytes, 8, 5);
    write16(bytes, 12, 256);
    auto log = FanoutLog{};
    auto contexts = std::array<FanoutContext, 5>{};
    auto descriptors = std::array<liberafx::CustomNodeDescriptor, 5>{};
    for (auto index = 0u; index < 5; ++index)
    {
        auto offset = 16 + index * 256;
        bytes[offset + 15] = static_cast<std::uint8_t>(index + 1);
        bytes[offset + 16] = 41;
        bytes[offset + 19] = 1;
        bytes[offset + 24] = static_cast<std::uint8_t>(0x80 + index);
        write32(bytes, offset + 40, 1);
        for (auto input = 0u; input < 4; ++input)
            write32(bytes, offset + 48 + input * 8, 0xffffffffu);
        for (auto branch = 0u; branch < 4; ++branch)
            write32(bytes, offset + 208 + branch * 4, 0xffffffffu);
        contexts[index] = {&log, index};
        descriptors[index].typeId[0] = static_cast<std::uint8_t>(0x80 + index);
        descriptors[index].abiVersion = 1;
        descriptors[index].execution = liberafx::CustomExecution::Signal;
        descriptors[index].branchCount = 1;
        descriptors[index].function = recordFanout;
        descriptors[index].context = &contexts[index];
    }
    write32(bytes, 16 + 208, 1);
    write32(bytes, 16 + 228, 2);
    write32(bytes, 16 + 256 + 208, 2);
    write32(bytes, 16 + 3 * 256 + 208, 4);
    write32(bytes, 16 + 5 * 256, 3);
    write32(bytes, 16 + 5 * 256 + 4, 4);
    auto legacyFixture = bytes;
    bytes = runtime_test_fixture::promoteSimpleProgram(bytes, descriptors.data(), descriptors.size());
    auto bindings = std::array<liberafx::CustomNodeBinding, 5>{};
    auto program = liberafx::Program{};
    auto load = [&](const auto& data) {
        return liberafx::Program::load(data.data(), data.size(), descriptors.data(),
            descriptors.size(), bindings.data(), bindings.size(), program);
    };
    assert(load(bytes) == liberafx::Result::Ok && program.workBytes() >= 160);
    alignas(16) auto work = std::array<double, 512>{};
    auto instance = liberafx::Instance{};
    auto queue = Queue{};
    assert(instance.initialize(program, work.data(), work.size()) == liberafx::Result::Ok);
    assert(instance.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    assert(instance.dispatchSignal(bytes.data() + 16, 0, 6) == liberafx::Result::Ok);
    assert(log.count == 6 && (log.nodes == std::array<unsigned, 16>{0, 1, 3, 4, 2, 4}));
    log.count = 0;
    assert(instance.dispatchSignal(bytes.data() + 16, 0, 3) == liberafx::Result::InstructionBudgetExceeded);
    assert(log.count == 3 && log.nodes[0] == 0 && log.nodes[1] == 1 && log.nodes[2] == 3);
    assert(instance.dispatchGeneratedSignals(10) == liberafx::Result::Ok);
    assert(log.count == 6);
    log.count = 0;
    log.failNode = 2;
    assert(instance.dispatchSignal(bytes.data() + 16, 0, 10) == liberafx::Result::InvalidArgument);
    assert(log.count == 5);
    log.failNode = 99;
    assert(instance.dispatchGeneratedSignals(10) == liberafx::Result::Ok);

    auto delayed = DeferredTestContext{&log, 1};
    descriptors[0].function = recordDeferred;
    descriptors[0].context = &delayed;
    log = {};
    assert(instance.dispatchSignal(bytes.data() + 16, 0, 10) == liberafx::Result::NotReady);
    auto storage = std::array<liberafx::DeferredSignal, 2>{};
    assert(instance.setDeferredSignalStorage(storage.data(), storage.size()) == liberafx::Result::Ok);
    assert(instance.dispatchGeneratedSignals(10) == liberafx::Result::Ok);
    delayed.delay = 0.5;
    assert(instance.dispatchSignal(bytes.data() + 16, 0, 10) == liberafx::Result::Ok);
    assert(instance.dispatchSignal(bytes.data() + 16, 0, 10) == liberafx::Result::QueueRejected);
    assert(instance.initialize(program, work.data(), work.size()) == liberafx::Result::Ok);
    assert(instance.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    assert(instance.setDeferredSignalStorage(storage.data(), storage.size()) == liberafx::Result::Ok);
    delayed.delay = 1;
    assert(instance.dispatchSignal(bytes.data() + 16, 0, 10) == liberafx::Result::Ok);
    delayed.delay = 0.5;
    assert(instance.dispatchSignal(bytes.data() + 16, 0, 10) == liberafx::Result::Ok);
    auto due = double{};
    auto has = false;
    assert(instance.nextDeferredSignal(due, has) == liberafx::Result::Ok && has && due == 0.5);
    assert(instance.setDeferredSignalStorage(nullptr, 0) == liberafx::Result::Busy);
    assert(instance.dispatchDeferredSignal(0.5, 10) == liberafx::Result::NotReady);
    assert(instance.updateAsync(0.5, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    log.count = 0;
    assert(instance.dispatchDeferredSignal(0.5, 10) == liberafx::Result::Ok);
    assert(log.count == 5 && (log.nodes[0] == 1 && log.nodes[1] == 3 &&
                              log.nodes[2] == 4 && log.nodes[3] == 2 && log.nodes[4] == 4));
    assert(instance.nextDeferredSignal(due, has) == liberafx::Result::Ok && has && due == 1);
    assert(instance.updateAsync(1, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    log.count = 0;
    log.failNode = 2;
    assert(instance.dispatchDeferredSignal(1, 10) == liberafx::Result::InvalidArgument);
    assert(instance.nextDeferredSignal(due, has) == liberafx::Result::Ok && !has);
    log = {};
    assert(instance.dispatchGeneratedSignals(10) == liberafx::Result::Ok);
    log = {};
    delayed.delay = -1;
    assert(instance.dispatchSignal(bytes.data() + 16, 1, 10) == liberafx::Result::InvalidArgument);
    delayed.delay = std::numeric_limits<double>::infinity();
    assert(instance.dispatchGeneratedSignals(10) == liberafx::Result::InvalidArgument);
    delayed.delay = std::numeric_limits<double>::quiet_NaN();
    assert(instance.dispatchGeneratedSignals(10) == liberafx::Result::InvalidArgument);
    delayed.delay = 0;
    assert(instance.dispatchGeneratedSignals(10) == liberafx::Result::Ok);
    assert(instance.clearDeferredSignals() == liberafx::Result::Ok);
    assert(instance.nextDeferredSignal(due, has) == liberafx::Result::Ok && !has);
    log = {};
    delayed.delay = 1;
    descriptors[1].function = recordDeferred;
    descriptors[1].context = &delayed;
    assert(instance.dispatchSignal(bytes.data() + 16, 1, 10) == liberafx::Result::Ok);
    assert(instance.dispatchSignal(bytes.data() + 16 + 256, 1, 10) == liberafx::Result::Ok);
    assert(storage[0].nodeIndex == 0 && storage[1].nodeIndex == 1);
    descriptors[1].function = recordFanout;
    descriptors[1].context = &contexts[1];
    assert(instance.updateAsync(2, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    log = {};
    assert(instance.dispatchDeferredSignal(2, 10) == liberafx::Result::Ok);
    assert(instance.nextDeferredSignal(due, has) == liberafx::Result::Ok && has && due == 2);
    assert(instance.dispatchDeferredSignal(2, 10) == liberafx::Result::Ok);
    assert(log.count == 6 && log.nodes[0] == 1 && log.nodes[1] == 3 &&
           log.nodes[2] == 4 && log.nodes[3] == 2 && log.nodes[4] == 4 && log.nodes[5] == 2);
    descriptors[0].function = recordFanout;
    descriptors[0].context = &contexts[0];

    auto oneExtra = legacyFixture;
    write32(oneExtra, 16 + 228, 1);
    oneExtra.resize(oneExtra.size() - 4);
    oneExtra = runtime_test_fixture::promoteSimpleProgram(oneExtra, descriptors.data(), descriptors.size());
    auto alignedProgram = liberafx::Program{};
    auto alignedBindings = std::array<liberafx::CustomNodeBinding, 5>{};
    assert(liberafx::Program::load(oneExtra.data(), oneExtra.size(), descriptors.data(),
        descriptors.size(), alignedBindings.data(), alignedBindings.size(), alignedProgram) == liberafx::Result::Ok);
    assert(alignedProgram.workBytes() >= 160);

    auto malformed = bytes;
    write32(malformed, 16 + 224, 4);
    assert(load(malformed) == liberafx::Result::InvalidBinary);
    malformed = bytes;
    write32(malformed, 16 + 5 * 256, 0);
    assert(load(malformed) == liberafx::Result::InvalidBinary);
    malformed = bytes;
    malformed.pop_back();
    assert(load(malformed) == liberafx::Result::InvalidBinary);
    malformed = bytes;
    write16(malformed, 4, 4);
    assert(load(malformed) == liberafx::Result::UnsupportedBinary);
}

void verifyCustom()
{
    auto bytes = customFixture();
    auto sourceCounters = CustomCounters{};
    auto firstCounters = CustomCounters{};
    auto secondCounters = CustomCounters{};
    auto descriptors = std::array<liberafx::CustomNodeDescriptor, 3>{};
    const auto ids = std::array<std::array<std::uint8_t, 16>, 3>{
        std::array<std::uint8_t, 16>{0xa1}, std::array<std::uint8_t, 16>{0xa2},
        std::array<std::uint8_t, 16>{0xa3}};
    for (auto index = 0u; index < descriptors.size(); ++index)
    {
        std::memcpy(descriptors[index].typeId, ids[index].data(), 16);
        descriptors[index].abiVersion = 1;
        descriptors[index].execution = liberafx::CustomExecution::Parameter;
        descriptors[index].inputCount = 1;
        descriptors[index].inputWidths[0] = 1;
        descriptors[index].outputWidth = 1;
        descriptors[index].function = index == 0 ? customCopy : customIncrement;
        descriptors[index].context = index == 0 ? static_cast<void*>(&sourceCounters) :
            index == 1 ? static_cast<void*>(&firstCounters) : static_cast<void*>(&secondCounters);
    }
    bytes = runtime_test_fixture::promoteSimpleProgram(bytes, descriptors.data(), descriptors.size());
    auto program = liberafx::Program{};
    auto bindings = std::array<liberafx::CustomNodeBinding, 3>{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), descriptors.data(), descriptors.size(),
                                   bindings.data(), bindings.size(), program) == liberafx::Result::Ok);
    assert(program.version() == 11 && program.workBytes() >= 12 * sizeof(double));
    assert(program.workAlignment() == 16 && program.defaultsAlignment() == alignof(double));
    alignas(16) auto work = std::array<double, 128>{};
    alignas(16) auto otherWork = std::array<double, 128>{};
    auto defaults = std::array<double, 48>{};
    auto otherDefaults = std::array<double, 48>{};
    {
    auto instance = liberafx::Instance{};
    auto other = liberafx::Instance{};
    assert(instance.initialize(program, work.data(), work.size(), defaults.data(), defaults.size()) ==
           liberafx::Result::Ok);
    assert(other.initialize(program, otherWork.data(), otherWork.size(),
                            otherDefaults.data(), otherDefaults.size()) == liberafx::Result::Ok);
    auto sourceId = bytes.data() + 16;
    auto handle = liberafx::ValueHandle{};
    auto parameter = liberafx::ParameterHandle{};
    assert(instance.bindValue(sourceId, handle) == liberafx::Result::Ok);
    assert(instance.bindParameter(sourceId, 0, 0, parameter) == liberafx::Result::Ok);
    auto queue = Queue{};
    assert(instance.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    assert(sourceCounters.calls == 1 && firstCounters.calls == 1 && secondCounters.calls == 1);
    auto values = std::array<double, 4>{};
    assert(instance.value(handle, values.data()) == liberafx::Result::Ok && values[0] == 5);
    assert(instance.setParameter(parameter, 8) == liberafx::Result::Ok);
    auto parameterValue = double{};
    assert(instance.parameter(parameter, parameterValue) == liberafx::Result::Ok && parameterValue == 8);
    assert(instance.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    assert(instance.value(handle, values.data()) == liberafx::Result::Ok && values[0] == 8);
    assert(other.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    assert(other.value(sourceId, values[0]) == liberafx::Result::Ok && values[0] == 5);
    assert(other.value(handle, values.data()) == liberafx::Result::InvalidHandle);
    assert(other.setParameter(parameter, 99) == liberafx::Result::InvalidHandle);
    assert(instance.initialize(program, work.data(), work.size(), defaults.data(), defaults.size()) ==
           liberafx::Result::Ok);
    assert(instance.value(handle, values.data()) == liberafx::Result::InvalidHandle);
    assert(instance.parameter(parameter, parameterValue) == liberafx::Result::InvalidHandle);
    }

    auto unknown = descriptors;
    unknown[1].abiVersion = 2;
    assert(liberafx::Program::load(bytes.data(), bytes.size(), unknown.data(), unknown.size(),
                                   bindings.data(), bindings.size(), program) ==
           liberafx::Result::UnsupportedBinary);
    auto unknownType = bytes;
    unknownType[16 + 256 + 24] ^= 0x7f;
    assert(liberafx::Program::load(unknownType.data(), unknownType.size(), descriptors.data(), descriptors.size(),
                                   bindings.data(), bindings.size(), program) ==
           liberafx::Result::UnknownCustomNode);
    auto malformed = bytes;
    malformed[16 + 256 + 23] = 1;
    assert(liberafx::Program::load(malformed.data(), malformed.size(), descriptors.data(), descriptors.size(),
                                   bindings.data(), bindings.size(), program) ==
           liberafx::Result::InvalidBinary);

    auto mixed = customFixture();
    auto customVersionFour = mixed;
    write16(customVersionFour, 4, 4);
    auto customVersionFourBindings = std::array<liberafx::CustomNodeBinding, 3>{};
    auto customVersionFourProgram = liberafx::Program{};
    assert(liberafx::Program::load(customVersionFour.data(), customVersionFour.size(),
        descriptors.data(), descriptors.size(), customVersionFourBindings.data(),
        customVersionFourBindings.size(), customVersionFourProgram) == liberafx::Result::UnsupportedBinary);
    mixed.resize(16 + 4 * 256 + 2 * 40);
    write16(mixed, 4, 4);
    write32(mixed, 8, 4);
    auto curveInstruction = std::size_t{16 + 3 * 256};
    mixed[curveInstruction + 15] = 4;
    mixed[curveInstruction + 16] = 42;
    mixed[curveInstruction + 17] = 1;
    write32(mixed, curveInstruction + 24, 0xffffffffu);
    write32(mixed, curveInstruction + 28, 0xffffffffu);
    write32(mixed, curveInstruction + 224, 0);
    write32(mixed, curveInstruction + 228, 2);
    auto curveKeys = 16 + 4 * 256;
    writeDouble(mixed, curveKeys, 0);
    writeDouble(mixed, curveKeys + 8, 3);
    mixed[curveKeys + 32] = 1;
    writeDouble(mixed, curveKeys + 40, 1);
    writeDouble(mixed, curveKeys + 48, 5);
    mixed = runtime_test_fixture::promoteSimpleProgram(mixed, descriptors.data(), descriptors.size());
    auto mixedProgram = liberafx::Program{};
    auto mixedBindings = std::array<liberafx::CustomNodeBinding, 4>{};
    assert(liberafx::Program::load(mixed.data(), mixed.size(), descriptors.data(), descriptors.size(),
                                   mixedBindings.data(), mixedBindings.size(), mixedProgram) ==
           liberafx::Result::Ok);
    alignas(16) auto mixedWork = std::array<double, 128>{};
    auto mixedDefaults = std::array<double, 64>{};
    auto mixedInstance = liberafx::Instance{};
    auto mixedQueue = Queue{};
    assert(mixedInstance.initialize(mixedProgram, mixedWork.data(), mixedWork.size(),
                                    mixedDefaults.data(), mixedDefaults.size()) == liberafx::Result::Ok);
    assert(mixedInstance.updateAsync(0.5, enqueue, &mixedQueue) == liberafx::Result::Ok);
    run(mixedQueue);
    auto mixedCurveValue = double{};
    assert(mixedInstance.value(mixed.data() + curveInstruction, mixedCurveValue) == liberafx::Result::Ok &&
           mixedCurveValue == 4);
    alignas(16) auto misalignedBindingsStorage =
        std::array<std::uint8_t, sizeof(liberafx::CustomNodeBinding) * 3 + 1>{};
    auto misalignedBindings = reinterpret_cast<liberafx::CustomNodeBinding*>(
        misalignedBindingsStorage.data() + 1);
    assert(liberafx::Program::load(bytes.data(), bytes.size(), descriptors.data(), descriptors.size(),
                                   misalignedBindings, bindings.size(), program) ==
           liberafx::Result::InvalidArgument);

    auto stateCounters = std::array<StateCounters, 3>{};
    stateCounters[1].fail = true;
    auto stateDescriptors = descriptors;
    for (auto index = 0u; index < stateDescriptors.size(); ++index)
    {
        stateDescriptors[index].stateBytes = sizeof(std::uint64_t);
        stateDescriptors[index].stateAlignment = 64;
        stateCounters[index].requiredAlignment = 64;
        stateDescriptors[index].initialize = initializeCustomState;
        stateDescriptors[index].cleanup = cleanupCustomState;
        stateDescriptors[index].context = &stateCounters[index];
    }
    assert(liberafx::Program::load(bytes.data(), bytes.size(), stateDescriptors.data(), stateDescriptors.size(),
                                   bindings.data(), bindings.size(), program) == liberafx::Result::Ok);
    assert(program.workBytes() >= 264 && program.workAlignment() == 64);
    alignas(64) auto stateWork = std::array<double, 128>{};
    auto stateDefaults = std::array<double, 48>{};
    auto failedInstance = liberafx::Instance{};
    assert(failedInstance.initialize(program, stateWork.data(), stateWork.size(),
                                     stateDefaults.data(), stateDefaults.size()) ==
           liberafx::Result::InvalidArgument);
    assert(stateCounters[0].initializations == 1 && stateCounters[0].cleanups == 1 &&
           stateCounters[1].initializations == 1 && stateCounters[1].cleanups == 1 &&
           stateCounters[2].initializations == 0);
    auto invalidAlignment = stateDescriptors;
    invalidAlignment[0].stateAlignment = 24;
    assert(liberafx::Program::load(bytes.data(), bytes.size(), invalidAlignment.data(),
                                   invalidAlignment.size(), bindings.data(), bindings.size(), program) ==
           liberafx::Result::InvalidArgument);
}

auto signalFixture() -> std::vector<std::uint8_t>
{
    auto bytes = std::vector<std::uint8_t>(16 + 3 * 256);
    std::memcpy(bytes.data(), "LFXB", 4);
    write16(bytes, 4, 3);
    bytes[6] = 1;
    write32(bytes, 8, 3);
    write16(bytes, 12, 256);
    const auto ids = std::array<std::array<std::uint8_t, 16>, 3>{
        std::array<std::uint8_t, 16>{0xb1}, std::array<std::uint8_t, 16>{0xb2},
        std::array<std::uint8_t, 16>{0xb3}};
    auto initNode = [&](std::size_t index, std::uint8_t opcode, std::uint8_t width,
                        std::uint8_t inputCount, std::uint8_t branchCount, std::size_t type) {
        auto offset = 16 + index * 256;
        bytes[offset + 15] = static_cast<std::uint8_t>(index + 1);
        bytes[offset + 16] = opcode;
        bytes[offset + 17] = width;
        bytes[offset + 18] = inputCount;
        bytes[offset + 19] = branchCount;
        std::memcpy(bytes.data() + offset + 24, ids[type].data(), 16);
        write32(bytes, offset + 40, 1);
        for (auto input = 0u; input < 4; ++input)
            write32(bytes, offset + 48 + input * 8, 0xffffffffu);
        for (auto branch = 0u; branch < 4; ++branch)
            write32(bytes, offset + 208 + branch * 4, 0xffffffffu);
    };
    initNode(0, 40, 1, 1, 0, 0);
    writeDouble(bytes, 16 + 80, 1);
    initNode(1, 41, 0, 1, 2, 1);
    write32(bytes, 16 + 256 + 48, 0);
    bytes[16 + 256 + 48 + 4] = 1;
    write32(bytes, 16 + 256 + 208 + 4, 2);
    initNode(2, 41, 0, 1, 1, 2);
    write32(bytes, 16 + 2 * 256 + 48, 0);
    bytes[16 + 2 * 256 + 48 + 4] = 1;
    return bytes;
}

void verifyCustomSignal()
{
    auto bytes = signalFixture();
    auto sourceCounters = CustomCounters{};
    auto branchCounters = CustomCounters{};
    auto endCounters = CustomCounters{};
    auto descriptors = std::array<liberafx::CustomNodeDescriptor, 3>{};
    const auto ids = std::array<std::array<std::uint8_t, 16>, 3>{
        std::array<std::uint8_t, 16>{0xb1}, std::array<std::uint8_t, 16>{0xb2},
        std::array<std::uint8_t, 16>{0xb3}};
    for (auto index = 0u; index < 3; ++index)
    {
        std::memcpy(descriptors[index].typeId, ids[index].data(), 16);
        descriptors[index].abiVersion = 1;
        descriptors[index].inputCount = 1;
        descriptors[index].inputWidths[0] = 1;
        descriptors[index].execution = index == 0 ? liberafx::CustomExecution::Parameter :
            liberafx::CustomExecution::Signal;
        descriptors[index].outputWidth = index == 0 ? 1 : 0;
        descriptors[index].branchCount = index == 1 ? 2 : index == 2 ? 1 : 0;
        descriptors[index].function = index == 0 ? customCopy : index == 1 ? customBranch : customEnd;
        descriptors[index].context = index == 0 ? static_cast<void*>(&sourceCounters) :
            index == 1 ? static_cast<void*>(&branchCounters) : static_cast<void*>(&endCounters);
    }
    bytes = runtime_test_fixture::promoteSimpleProgram(bytes, descriptors.data(), descriptors.size());
    auto program = liberafx::Program{};
    auto bindings = std::array<liberafx::CustomNodeBinding, 3>{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), descriptors.data(), descriptors.size(),
                                   bindings.data(), bindings.size(), program) == liberafx::Result::Ok);
    alignas(16) auto work = std::array<double, 128>{};
    auto defaults = std::array<double, 48>{};
    auto instance = liberafx::Instance{};
    assert(instance.initialize(program, work.data(), work.size(), defaults.data(), defaults.size()) ==
           liberafx::Result::Ok);
    auto queue = Queue{};
    assert(instance.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    auto entryId = bytes.data() + 16 + 256;
    auto entryHandle = liberafx::SignalHandle{};
    assert(instance.bindSignal(entryId, entryHandle) == liberafx::Result::Ok);
    assert(instance.dispatchSignal(entryHandle, 0, 1) == liberafx::Result::InstructionBudgetExceeded);
    assert(branchCounters.calls == 1 && endCounters.calls == 0);
    assert(instance.dispatchGeneratedSignals(2) == liberafx::Result::Ok);
    assert(branchCounters.calls == 1 && endCounters.calls == 1);
    assert(instance.dispatchSignal(entryHandle, 0, 2) == liberafx::Result::Ok);
    assert(branchCounters.calls == 2 && endCounters.calls == 2 && endCounters.observed == 1);
    auto context = ReentrantSignalContext{};
    auto reentrantDescriptors = descriptors;
    reentrantDescriptors[1].function = reentrantSignal;
    reentrantDescriptors[1].context = &context;
    auto reentrantBindings = std::array<liberafx::CustomNodeBinding, 3>{};
    auto reentrantProgram = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), reentrantDescriptors.data(),
        reentrantDescriptors.size(), reentrantBindings.data(), reentrantBindings.size(), reentrantProgram) == liberafx::Result::Ok);
    alignas(16) auto reentrantWork = std::array<double, 128>{};
    auto reentrantDefaults = std::array<double, 48>{};
    auto reentrantInstance = liberafx::Instance{};
    context.instance = &reentrantInstance;
    assert(reentrantInstance.initialize(reentrantProgram, reentrantWork.data(), reentrantWork.size(),
        reentrantDefaults.data(), reentrantDefaults.size()) == liberafx::Result::Ok);
    assert(reentrantInstance.bindSignal(entryId, context.entry) == liberafx::Result::Ok);
    assert(reentrantInstance.bindParameter(bytes.data() + 16, 0, 0, context.parameter) == liberafx::Result::Ok);
    assert(reentrantInstance.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    assert(reentrantInstance.dispatchSignal(context.entry, 0, 2) == liberafx::Result::Ok && context.visited);
}

void verifyTyped(const std::vector<std::uint8_t>& bytes)
{
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), program) == liberafx::Result::Ok);
    assert(program.version() == 11 && program.nodeCount() == 11);
    assert(program.workBytes() >= 11 * 4 * sizeof(double));
    alignas(16) auto work = std::array<double, 128>{};
    auto defaults = std::array<double, 176>{};
    assert(program.parameterValueCount() == defaults.size());
    auto instance = liberafx::Instance{};
    auto queue = Queue{};
    auto required = (program.workBytes() + sizeof(double) - 1) / sizeof(double);
    assert(instance.initialize(program, work.data(), required - 1) == liberafx::Result::InsufficientWork);
    assert(instance.initialize(program, work.data(), work.size(), defaults.data(), defaults.size()) == liberafx::Result::Ok);
    assert(instance.capacityGrowthCount() == 0);
    assert(instance.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    auto vectorId = bytes.data() + 16;
    assert(instance.setParameter(vectorId, 0, 0, 4) == liberafx::Result::Busy);
    run(queue);
    assert(instance.result() == liberafx::Result::Ok);
    auto values = std::array<double, 4>{};
    auto count = std::uint8_t{};
    auto maskId = bytes.data() + 16 + 256;
    assert(instance.valueComponents(maskId, values.data(), count) == liberafx::Result::Ok);
    assert(count == 2 && values[0] == 2.5 && values[1] == 2);
    auto breakId = bytes.data() + 16 + 2 * 256;
    assert(instance.valueComponents(breakId, values.data(), count) == liberafx::Result::Ok);
    assert(count == 4 && values[0] == 2 && values[1] == -3 && values[2] == 4.5 && values[3] == 2.5);
    auto colorId = bytes.data() + 16 + 4 * 256;
    assert(instance.valueComponents(colorId, values.data(), count) == liberafx::Result::Ok);
    assert(count == 4 && values[0] == 2 && values[1] == -3 && values[2] == 4.5 && values[3] == 2.5);
    auto multiplyId = bytes.data() + 16 + 5 * 256;
    assert(instance.valueComponents(multiplyId, values.data(), count) == liberafx::Result::Ok);
    assert(count == 4 && values[0] == 4 && values[1] == 9 && values[2] == 20.25 && values[3] == 6.25);
    auto extendedId = bytes.data() + 16 + 6 * 256;
    assert(instance.valueComponents(extendedId, values.data(), count) == liberafx::Result::Ok);
    assert(count == 4 && values[0] == 2.5 && values[1] == 2 && values[2] == 7 && values[3] == 7);
    auto breakOutputs = std::array<double, 4>{2, -3, 4.5, 2.5};
    for (auto component = 0u; component < 4; ++component)
    {
        auto outputId = bytes.data() + 16 + (7 + component) * 256;
        assert(instance.valueComponents(outputId, values.data(), count) == liberafx::Result::Ok);
        assert(count == 2 && values[0] == breakOutputs[component] && values[1] == breakOutputs[component]);
    }
    auto scalar = double{};
    assert(instance.value(colorId, scalar) == liberafx::Result::InvalidValueType);
    auto maskNodeId = bytes.data() + 16 + 256;
    assert(instance.setParameter(maskNodeId, 0, 0, 4) == liberafx::Result::InvalidValueType);
    assert(instance.setParameter(maskNodeId, 0, 0, std::numeric_limits<double>::quiet_NaN()) ==
           liberafx::Result::NonFinite);
    assert(instance.setParameter(vectorId, 0, 0, 4) == liberafx::Result::Ok);
    assert(instance.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    assert(instance.valueComponents(multiplyId, values.data(), count) == liberafx::Result::Ok);
    assert(values[0] == 16 && values[1] == 9 && values[2] == 20.25 && values[3] == 6.25);
    assert(instance.valueComponents(extendedId, values.data(), count) == liberafx::Result::Ok);
    assert(count == 4 && values[0] == 2.5 && values[1] == 4 && values[2] == 7 && values[3] == 7);

    auto malformed = bytes;
    malformed[16 + 256 + 32] = 4;
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) == liberafx::Result::InvalidBinary);
    malformed = bytes;
    malformed[6] = 2;
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) == liberafx::Result::UnsupportedBinary);
    malformed = bytes;
    write32(malformed, 16 + 256 + 24, 99);
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) == liberafx::Result::InvalidBinary);
    malformed = bytes;
    malformed[16 + 4 * 256 + 20] = 0;
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) == liberafx::Result::InvalidBinary);
    malformed = bytes;
    malformed[16 + 2 * 256 + 17] = 2;
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) == liberafx::Result::InvalidBinary);
    malformed = bytes;
    malformed[16 + 5 * 256 + 16] = 250;
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) == liberafx::Result::UnsupportedBinary);
    malformed = bytes;
    malformed[6] = 2;
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) == liberafx::Result::UnsupportedBinary);

    auto divideByZero = bytes;
    divideByZero[16 + 5 * 256 + 16] = 22;
    divideByZero[16 + 5 * 256 + 19] = 0;
    write32(divideByZero, 16 + 5 * 256 + 28, 0xffffffffu);
    writeDouble(divideByZero, 16 + 5 * 256 + 64, 1);
    writeDouble(divideByZero, 16 + 5 * 256 + 72, 0);
    writeDouble(divideByZero, 16 + 5 * 256 + 80, 1);
    writeDouble(divideByZero, 16 + 5 * 256 + 88, 1);
    assert(liberafx::Program::load(divideByZero.data(), divideByZero.size(), program) == liberafx::Result::Ok);
    alignas(16) auto divideWork = std::array<double, 128>{};
    auto divideInstance = liberafx::Instance{};
    auto divideQueue = Queue{};
    assert(divideInstance.initialize(program, divideWork.data(), divideWork.size()) == liberafx::Result::Ok);
    assert(divideInstance.updateAsync(0, enqueue, &divideQueue) == liberafx::Result::Ok);
    run(divideQueue);
    assert(divideInstance.result() == liberafx::Result::DivideByZero);
}

}

auto loadPackageProgram(const char* path) -> std::vector<std::uint8_t>
{
    auto input = std::ifstream(path, std::ios::binary);
    assert(input);
    auto bytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(input), {});
    auto package = liberafx::SpawnablePackage{};
    assert(liberafx::loadSpawnablePackage(bytes.data(), bytes.size(), package) == liberafx::Result::Ok);
    return std::move(package.parentBytecode);
}

void verifyGeneratedBreak(const std::vector<std::uint8_t>& bytes)
{
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), program) == liberafx::Result::Ok);
    assert(program.version() == 11 && program.nodeCount() == 3);
    auto count = (program.workBytes() + sizeof(std::max_align_t) - 1) / sizeof(std::max_align_t);
    auto work = std::vector<std::max_align_t>(count);
    auto otherWork = std::vector<std::max_align_t>(count);
    auto defaults = std::vector<double>(program.parameterValueCount());
    auto instance = liberafx::Instance{};
    auto other = liberafx::Instance{};
    assert(instance.initialize(program, reinterpret_cast<double*>(work.data()),
        work.size() * sizeof(std::max_align_t) / sizeof(double), defaults.data(), defaults.size()) == liberafx::Result::Ok);
    assert(other.initialize(program, reinterpret_cast<double*>(otherWork.data()),
        otherWork.size() * sizeof(std::max_align_t) / sizeof(double)) == liberafx::Result::Ok);
    auto queue = Queue{};
    assert(instance.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    auto id = std::array<std::uint8_t, 16>{};
    id.back() = 3;
    auto values = std::array<double, 4>{};
    auto width = std::uint8_t{};
    assert(instance.valueComponents(id.data(), values.data(), width) == liberafx::Result::Ok);
    assert(width == 2 && values[0] == 4 && values[1] == 4);
    auto sourceId = std::array<std::uint8_t, 16>{};
    sourceId.back() = 1;
    assert(instance.setParameter(sourceId.data(), 0, 3, 9) == liberafx::Result::Ok);
    assert(instance.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    assert(instance.valueComponents(id.data(), values.data(), width) == liberafx::Result::Ok);
    assert(width == 2 && values[0] == 9 && values[1] == 9);
    assert(other.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    assert(other.valueComponents(id.data(), values.data(), width) == liberafx::Result::Ok);
    assert(width == 2 && values[0] == 4 && values[1] == 4);
}

void verifyGeneratedCurve(const std::vector<std::uint8_t>& bytes)
{
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), program) == liberafx::Result::Ok);
    assert(program.version() == 11 && program.nodeCount() == 1);
    auto count = (program.workBytes() + sizeof(std::max_align_t) - 1) / sizeof(std::max_align_t);
    auto work = std::vector<std::max_align_t>(count);
    auto defaults = std::vector<double>(program.parameterValueCount());
    auto instance = liberafx::Instance{};
    auto queue = Queue{};
    assert(instance.initialize(program, reinterpret_cast<double*>(work.data()),
        work.size() * sizeof(std::max_align_t) / sizeof(double), defaults.data(), defaults.size()) ==
           liberafx::Result::Ok);
    assert(instance.updateAsync(0.5, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    auto value = double{};
    assert(instance.value(bytes.data() + 16, value) == liberafx::Result::Ok);
    assert(std::abs(value - 0.50125) < 1e-12);
}

auto main(int argc, char** argv) -> int
{
#ifdef _MSC_VER
    _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE);
    _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
#endif
    if (argc == 3 && std::strcmp(argv[1], "--curve") == 0)
    {
        auto generated = loadPackageProgram(argv[2]);
        verifyGeneratedCurve(generated);
        return 0;
    }
    auto bytes = fixture();
    verify(bytes);
    verifyCurveBytecode();
    verifyTyped(typedFixture());
    verifyCustom();
    verifyCustomSignal();
    verifySignalFanout();
    auto corrupt = bytes;
    corrupt[6] = 2;
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(corrupt.data(), corrupt.size(), program) == liberafx::Result::UnsupportedBinary);
    corrupt = bytes;
    write32(corrupt, 16 + 24, 0);
    assert(liberafx::Program::load(corrupt.data(), corrupt.size(), program) == liberafx::Result::InvalidBinary);
    corrupt = bytes;
    std::memcpy(corrupt.data() + 16 + 256, corrupt.data() + 16, 16);
    assert(liberafx::Program::load(corrupt.data(), corrupt.size(), program) == liberafx::Result::InvalidBinary);
    corrupt = bytes;
    corrupt[16 + 2 * 256 + 16] = 6;
    corrupt[16 + 2 * 256 + 19] = 0;
    write32(corrupt, 16 + 2 * 256 + 28, 0xffffffffu);
    assert(liberafx::Program::load(corrupt.data(), corrupt.size(), program) == liberafx::Result::Ok);
    alignas(16) auto work = std::array<double, 128>{};
    auto instance = liberafx::Instance{};
    auto queue = Queue{};
    assert(instance.initialize(program, work.data(), work.size()) == liberafx::Result::Ok);
    assert(instance.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    assert(instance.result() == liberafx::Result::DivideByZero);
    if (argc > 1)
    {
        auto generated = loadPackageProgram(argv[1]);
        verify(generated);
    }
    if (argc > 2)
    {
        auto generated = loadPackageProgram(argv[2]);
        verifyGeneratedBreak(generated);
    }
    if (argc > 3)
    {
        auto generated = loadPackageProgram(argv[3]);
        verifyGeneratedCurve(generated);
    }
    return 0;
}
