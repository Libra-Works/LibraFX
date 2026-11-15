#include "liberafx/VfxRuntime.h"

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
