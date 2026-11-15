#include "liberafx/VfxRuntime.h"
#include "liberafx/platform/ProfileRecorder.h"

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
    return bytes;
}

void verify(const std::vector<std::uint8_t>& bytes)
{
    auto profileCounters = std::array<liberafx::ProfileCounter, 16>{};
    auto recorder = liberafx::ProfileRecorder{};
    recorder.initialize(profileCounters.data(), profileCounters.size());
    auto profiledProgram = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), profiledProgram, recorder.hooks()) == liberafx::Result::Ok);
    auto profiledWork = std::array<double, 3>{};
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
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), program) == liberafx::Result::Ok);
    assert(program.nodeCount() == 3 && program.workBytes() == 3 * sizeof(double));
    auto work = std::array<double, 3>{};
    auto workOther = std::array<double, 3>{};
    auto instance = liberafx::Instance{};
    auto other = liberafx::Instance{};
    assert(instance.initialize(program, work.data(), 2) == liberafx::Result::InsufficientWork);
    assert(instance.initialize(program, work.data(), 3) == liberafx::Result::Ok);
    assert(other.initialize(program, workOther.data(), 3) == liberafx::Result::Ok);
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
    auto id = bytes.data() + 12 + 2 * 44;
    assert(instance.value(id, value) == liberafx::Result::Ok && value == 10);
    assert(other.value(id, value) == liberafx::Result::Ok && value == 5);
    assert(instance.capacityGrowthCount() == 0);

    auto parameterWork = std::array<double, 3>{};
    auto parameterDefaults = std::array<double, 6>{};
    auto parameterInstance = liberafx::Instance{};
    auto parameterQueue = Queue{};
    assert(parameterInstance.initialize(program, parameterWork.data(), parameterWork.size(),
                                        parameterDefaults.data(), parameterDefaults.size()) == liberafx::Result::Ok);
    assert(parameterInstance.updateAsync(7, enqueue, &parameterQueue) == liberafx::Result::Ok);
    run(parameterQueue);
    auto scalarId = bytes.data() + 12;
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
    return bytes;
}

struct CustomCounters {
    std::uint32_t calls = 0;
    double observed = 0;
};

struct StateCounters {
    std::uint32_t initializations = 0;
    std::uint32_t cleanups = 0;
    bool fail = false;
};

auto initializeCustomState(void* state, std::size_t stateBytes, void* context) noexcept -> liberafx::Result
{
    auto counters = static_cast<StateCounters*>(context);
    ++counters->initializations;
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
    auto program = liberafx::Program{};
    auto bindings = std::array<liberafx::CustomNodeBinding, 3>{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), descriptors.data(), descriptors.size(),
                                   bindings.data(), bindings.size(), program) == liberafx::Result::Ok);
    assert(program.version() == 3 && program.workBytes() == 12 * sizeof(double));
    auto work = std::array<double, 12>{};
    auto otherWork = std::array<double, 12>{};
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

    auto stateCounters = std::array<StateCounters, 3>{};
    stateCounters[1].fail = true;
    auto stateDescriptors = descriptors;
    for (auto index = 0u; index < stateDescriptors.size(); ++index)
    {
        stateDescriptors[index].stateBytes = sizeof(std::uint64_t);
        stateDescriptors[index].initialize = initializeCustomState;
        stateDescriptors[index].cleanup = cleanupCustomState;
        stateDescriptors[index].context = &stateCounters[index];
    }
    assert(liberafx::Program::load(bytes.data(), bytes.size(), stateDescriptors.data(), stateDescriptors.size(),
                                   bindings.data(), bindings.size(), program) == liberafx::Result::Ok);
    auto stateWork = std::array<double, 15>{};
    auto stateDefaults = std::array<double, 48>{};
    auto failedInstance = liberafx::Instance{};
    assert(failedInstance.initialize(program, stateWork.data(), stateWork.size(),
                                     stateDefaults.data(), stateDefaults.size()) ==
           liberafx::Result::InvalidArgument);
    assert(stateCounters[0].initializations == 1 && stateCounters[0].cleanups == 1 &&
           stateCounters[1].initializations == 1 && stateCounters[1].cleanups == 1 &&
           stateCounters[2].initializations == 0);
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
    auto program = liberafx::Program{};
    auto bindings = std::array<liberafx::CustomNodeBinding, 3>{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), descriptors.data(), descriptors.size(),
                                   bindings.data(), bindings.size(), program) == liberafx::Result::Ok);
    auto work = std::array<double, 12>{};
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
    assert(instance.dispatchSignal(entryHandle, 0, 2) == liberafx::Result::Ok);
    assert(branchCounters.calls == 2 && endCounters.calls == 1 && endCounters.observed == 1);
    auto context = ReentrantSignalContext{};
    auto reentrantDescriptors = descriptors;
    reentrantDescriptors[1].function = reentrantSignal;
    reentrantDescriptors[1].context = &context;
    auto reentrantBindings = std::array<liberafx::CustomNodeBinding, 3>{};
    auto reentrantProgram = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), reentrantDescriptors.data(),
        reentrantDescriptors.size(), reentrantBindings.data(), reentrantBindings.size(), reentrantProgram) == liberafx::Result::Ok);
    auto reentrantWork = std::array<double, 12>{};
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
    assert(program.version() == 2 && program.nodeCount() == 11);
    assert(program.workBytes() == 11 * 4 * sizeof(double));
    auto work = std::array<double, 44>{};
    auto defaults = std::array<double, 88>{};
    assert(program.parameterValueCount() == defaults.size());
    auto instance = liberafx::Instance{};
    auto queue = Queue{};
    assert(instance.initialize(program, work.data(), 43) == liberafx::Result::InsufficientWork);
    assert(instance.initialize(program, work.data(), 44, defaults.data(), defaults.size()) == liberafx::Result::Ok);
    assert(instance.capacityGrowthCount() == 0);
    assert(instance.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    auto vectorId = bytes.data() + 16;
    assert(instance.setParameter(vectorId, 0, 0, 4) == liberafx::Result::Busy);
    run(queue);
    assert(instance.result() == liberafx::Result::Ok);
    auto values = std::array<double, 4>{};
    auto count = std::uint8_t{};
    auto maskId = bytes.data() + 16 + 96;
    assert(instance.valueComponents(maskId, values.data(), count) == liberafx::Result::Ok);
    assert(count == 2 && values[0] == 2.5 && values[1] == 2);
    auto breakId = bytes.data() + 16 + 2 * 96;
    assert(instance.valueComponents(breakId, values.data(), count) == liberafx::Result::Ok);
    assert(count == 4 && values[0] == 2 && values[1] == -3 && values[2] == 4.5 && values[3] == 2.5);
    auto colorId = bytes.data() + 16 + 4 * 96;
    assert(instance.valueComponents(colorId, values.data(), count) == liberafx::Result::Ok);
    assert(count == 4 && values[0] == 2 && values[1] == -3 && values[2] == 4.5 && values[3] == 2.5);
    auto multiplyId = bytes.data() + 16 + 5 * 96;
    assert(instance.valueComponents(multiplyId, values.data(), count) == liberafx::Result::Ok);
    assert(count == 4 && values[0] == 4 && values[1] == 9 && values[2] == 20.25 && values[3] == 6.25);
    auto extendedId = bytes.data() + 16 + 6 * 96;
    assert(instance.valueComponents(extendedId, values.data(), count) == liberafx::Result::Ok);
    assert(count == 4 && values[0] == 2.5 && values[1] == 2 && values[2] == 7 && values[3] == 7);
    auto breakOutputs = std::array<double, 4>{2, -3, 4.5, 2.5};
    for (auto component = 0u; component < 4; ++component)
    {
        auto outputId = bytes.data() + 16 + (7 + component) * 96;
        assert(instance.valueComponents(outputId, values.data(), count) == liberafx::Result::Ok);
        assert(count == 2 && values[0] == breakOutputs[component] && values[1] == breakOutputs[component]);
    }
    auto scalar = double{};
    assert(instance.value(colorId, scalar) == liberafx::Result::InvalidValueType);
    auto maskNodeId = bytes.data() + 16 + 96;
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
    malformed[16 + 96 + 32] = 4;
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) == liberafx::Result::InvalidBinary);
    malformed = bytes;
    malformed[6] = 2;
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) == liberafx::Result::UnsupportedBinary);
    malformed = bytes;
    write32(malformed, 16 + 96 + 24, 99);
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) == liberafx::Result::InvalidBinary);
    malformed = bytes;
    malformed[16 + 4 * 96 + 20] = 0;
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) == liberafx::Result::InvalidBinary);
    malformed = bytes;
    malformed[16 + 2 * 96 + 17] = 2;
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) == liberafx::Result::InvalidBinary);
    malformed = bytes;
    malformed[16 + 5 * 96 + 16] = 40;
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) == liberafx::Result::UnsupportedBinary);
    malformed = bytes;
    malformed[6] = 2;
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) == liberafx::Result::UnsupportedBinary);

    auto divideByZero = bytes;
    divideByZero[16 + 5 * 96 + 16] = 22;
    divideByZero[16 + 5 * 96 + 19] = 0;
    write32(divideByZero, 16 + 5 * 96 + 28, 0xffffffffu);
    writeDouble(divideByZero, 16 + 5 * 96 + 64, 1);
    writeDouble(divideByZero, 16 + 5 * 96 + 72, 0);
    writeDouble(divideByZero, 16 + 5 * 96 + 80, 1);
    writeDouble(divideByZero, 16 + 5 * 96 + 88, 1);
    assert(liberafx::Program::load(divideByZero.data(), divideByZero.size(), program) == liberafx::Result::Ok);
    auto divideWork = std::array<double, 44>{};
    auto divideInstance = liberafx::Instance{};
    auto divideQueue = Queue{};
    assert(divideInstance.initialize(program, divideWork.data(), divideWork.size()) == liberafx::Result::Ok);
    assert(divideInstance.updateAsync(0, enqueue, &divideQueue) == liberafx::Result::Ok);
    run(divideQueue);
    assert(divideInstance.result() == liberafx::Result::DivideByZero);
}

}

void verifyGeneratedBreak(const std::vector<std::uint8_t>& bytes)
{
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), program) == liberafx::Result::Ok);
    assert(program.version() == 2 && program.nodeCount() == 3);
    auto work = std::array<double, 12>{};
    auto otherWork = std::array<double, 12>{};
    auto defaults = std::array<double, 24>{};
    auto instance = liberafx::Instance{};
    auto other = liberafx::Instance{};
    assert(instance.initialize(program, work.data(), work.size(), defaults.data(), defaults.size()) == liberafx::Result::Ok);
    assert(other.initialize(program, otherWork.data(), otherWork.size()) == liberafx::Result::Ok);
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

auto main(int argc, char** argv) -> int
{
    auto bytes = fixture();
    verify(bytes);
    verifyTyped(typedFixture());
    verifyCustom();
    verifyCustomSignal();
    auto corrupt = bytes;
    corrupt[6] = 2;
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(corrupt.data(), corrupt.size(), program) == liberafx::Result::UnsupportedBinary);
    corrupt = bytes;
    write32(corrupt, 12 + 20, 0);
    assert(liberafx::Program::load(corrupt.data(), corrupt.size(), program) == liberafx::Result::InvalidBinary);
    corrupt = bytes;
    std::memcpy(corrupt.data() + 12 + 44, corrupt.data() + 12, 16);
    assert(liberafx::Program::load(corrupt.data(), corrupt.size(), program) == liberafx::Result::InvalidBinary);
    corrupt = bytes;
    corrupt[12 + 2 * 44 + 16] = 6;
    write32(corrupt, 12 + 2 * 44 + 24, 0xffffffffu);
    assert(liberafx::Program::load(corrupt.data(), corrupt.size(), program) == liberafx::Result::Ok);
    auto work = std::array<double, 3>{};
    auto instance = liberafx::Instance{};
    auto queue = Queue{};
    assert(instance.initialize(program, work.data(), 3) == liberafx::Result::Ok);
    assert(instance.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    assert(instance.result() == liberafx::Result::DivideByZero);
    if (argc > 1)
    {
        auto input = std::ifstream(argv[1], std::ios::binary);
        assert(input);
        auto generated = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(input), {});
        verify(generated);
    }
    if (argc > 2)
    {
        auto input = std::ifstream(argv[2], std::ios::binary);
        assert(input);
        auto generated = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(input), {});
        verifyGeneratedBreak(generated);
    }
    return 0;
}
