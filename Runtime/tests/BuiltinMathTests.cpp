#include "liberafx/VfxRuntime.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <array>
#include <cassert>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iterator>
#include <limits>
#include <vector>

namespace {
using namespace liberafx;
using Bytes = std::array<std::uint8_t, 272>;

void write32(Bytes& bytes, std::size_t offset, std::uint32_t value)
{
    for (auto i = 0u; i < 4; ++i) bytes[offset + i] = static_cast<std::uint8_t>(value >> (8 * i));
}
void writeDouble(Bytes& bytes, std::size_t offset, double value)
{
    auto bits = std::uint64_t{};
    std::memcpy(&bits, &value, 8);
    for (auto i = 0u; i < 8; ++i) bytes[offset + i] = static_cast<std::uint8_t>(bits >> (8 * i));
}
auto fixture(const CustomNodeDescriptor& descriptor) -> Bytes
{
    auto bytes = Bytes{};
    std::memcpy(bytes.data(), "LFXB", 4);
    bytes[4] = 3; bytes[6] = 1; bytes[8] = 1; bytes[13] = 1;
    bytes[16] = 1;
    bytes[32] = 40; bytes[33] = descriptor.outputWidth; bytes[34] = descriptor.inputCount;
    std::memcpy(bytes.data() + 40, descriptor.typeId, 16);
    write32(bytes, 56, descriptor.abiVersion);
    for (auto i = 0u; i < 4; ++i)
    {
        write32(bytes, 64 + i * 8, 0xffffffffu);
        write32(bytes, 224 + i * 4, 0xffffffffu);
        for (auto lane = 0u; lane < 4; ++lane) writeDouble(bytes, 96 + i * 32 + lane * 8, 1);
    }
    return bytes;
}
auto enqueue(void*, Job job, void* context) noexcept -> bool { job(context); return true; }
auto overrideNode(CustomNodeInvocation& call) noexcept -> Result { call.outputs[0] = 42; return Result::Ok; }

auto evaluate(const CustomNodeDescriptor& descriptor, const std::array<double, 16>& inputs,
              std::array<double, 4>& output) -> Result
{
    auto invocation = CustomNodeInvocation{inputs.data(), descriptor.inputCount, descriptor.inputWidths,
        output.data(), descriptor.outputWidth};
    return descriptor.function(invocation);
}
}

int main(int argc, char** argv)
{
    auto count = std::size_t{};
    auto registry = builtinNodes(count);
    assert(registry != nullptr && count == 47);
    const std::uint8_t expectedPrefix[] = {0x74, 0x30, 0x5a, 0xc1, 0x91, 0xb2, 0x4c, 0x33, 0x8d, 0x44};
    assert(std::memcmp(registry[0].typeId, expectedPrefix, sizeof(expectedPrefix)) == 0);
    if (argc >= 2)
    {
        auto file = std::ifstream(argv[1], std::ios::binary);
        auto compiled = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(file), {});
        auto compiledBinding = CustomNodeBinding{};
        auto compiledProgram = Program{};
        assert(Program::load(compiled.data(), compiled.size(), nullptr, 0, &compiledBinding, 1, compiledProgram) == Result::Ok);
        auto compiledWork = std::array<double, 4>{};
        auto compiledInstance = Instance{};
        assert(compiledInstance.initialize(compiledProgram, compiledWork.data(), compiledWork.size()) == Result::Ok);
        assert(compiledInstance.updateAsync(0, enqueue, nullptr) == Result::Ok);
        assert(compiledInstance.result() == Result::Ok && compiledWork[0] == 1);
    }
    if (argc >= 3)
    {
        auto file = std::ifstream(argv[2], std::ios::binary);
        auto compiled = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(file), {});
        auto bindings = std::array<CustomNodeBinding, 3>{};
        auto program = Program{};
        assert(Program::load(compiled.data(), compiled.size(), nullptr, 0, bindings.data(), bindings.size(), program) == Result::Ok);
        auto work = std::array<double, 12>{};
        auto instance = Instance{};
        assert(instance.initialize(program, work.data(), work.size()) == Result::Ok);
        assert(instance.updateAsync(1.5707963267948966, enqueue, nullptr) == Result::Ok);
        assert(instance.result() == Result::Ok && std::abs(work[8] - 1) < 1e-15 &&
               work[9] == 0.25 && work[10] == 0 && work[11] == 1);
    }
    for (auto i = std::size_t{}; i < count; ++i)
    {
        auto bytes = fixture(registry[i]);
        auto binding = CustomNodeBinding{};
        auto program = Program{};
        assert(Program::load(bytes.data(), bytes.size(), nullptr, 0, &binding, 1, program) == Result::Ok);
        assert(binding.descriptor == &registry[i]);
        auto work = std::array<double, 4>{};
        auto instance = Instance{};
        assert(instance.initialize(program, work.data(), work.size()) == Result::Ok);
        assert(instance.updateAsync(0, enqueue, nullptr) == Result::Ok);
        assert(instance.result() == Result::Ok);
    }

    auto bytes = fixture(registry[0]);
    writeDouble(bytes, 96, -8);
    auto binding = CustomNodeBinding{};
    auto program = Program{};
    assert(Program::load(bytes.data(), bytes.size(), nullptr, 0, &binding, 1, program) == Result::Ok);
    auto work = std::array<double, 4>{};
    auto defaults = std::array<double, 16>{};
    auto instance = Instance{};
    assert(instance.initialize(program, work.data(), work.size(), defaults.data(), defaults.size()) == Result::Ok);
    assert(instance.updateAsync(0, enqueue, nullptr) == Result::Ok && instance.result() == Result::Ok);
    assert(work[0] == 8);
    assert(instance.setParameter(bytes.data() + 16, 0, 0, -3) == Result::Ok);
    assert(instance.updateAsync(0, enqueue, nullptr) == Result::Ok && work[0] == 3);
    auto overrideDescriptor = registry[0];
    overrideDescriptor.function = overrideNode;
    auto overrideProgram = Program{};
    assert(Program::load(bytes.data(), bytes.size(), &overrideDescriptor, 1, &binding, 1, overrideProgram) == Result::Ok);
    assert(binding.descriptor == &overrideDescriptor);
    overrideDescriptor.abiVersion = 2;
    assert(Program::load(bytes.data(), bytes.size(), &overrideDescriptor, 1, &binding, 1, overrideProgram) == Result::UnsupportedBinary);
    bytes[40] ^= 0xff;
    assert(Program::load(bytes.data(), bytes.size(), nullptr, 0, &binding, 1, overrideProgram) == Result::UnknownCustomNode);

    auto input = std::array<double, 16>{};
    auto output = std::array<double, 4>{};
    input[0] = -1.5;
    assert(evaluate(registry[2], input, output) == Result::Ok && output[0] == -2);
    input[0] = -2.1; input[4] = 2;
    assert(evaluate(registry[1], input, output) == Result::Ok && output[0] == -4);
    input[4] = 0;
    assert(evaluate(registry[1], input, output) == Result::DivideByZero);
    assert(evaluate(registry[4], input, output) == Result::NonFinite);
    input[0] = 180;
    assert(evaluate(registry[16], input, output) == Result::Ok && output[0] == -180);
    input = {}; input[0] = std::numeric_limits<double>::max(); input[1] = input[0];
    assert(evaluate(registry[24], input, output) == Result::NonFinite);
    assert(evaluate(registry[26], input, output) == Result::Ok &&
           std::abs(output[0] - std::sqrt(0.5)) < 1e-15 && output[1] == output[0]);
    input = {};
    assert(evaluate(registry[26], input, output) == Result::Ok && output[0] == 0 && output[1] == 0);
    input[0] = 1; input[5] = 1;
    assert(evaluate(registry[33], input, output) == Result::Ok && output[2] == 1);
    input = {}; input[0] = 4; input[4] = 2; input[8] = 1;
    assert(evaluate(registry[19], input, output) == Result::InvalidArgument);
    input = {}; input[0] = 2; input[1] = 4; input[4] = 6; input[5] = 8; input[8] = 0.25;
    assert(evaluate(registry[37], input, output) == Result::Ok && output[0] == 3 && output[1] == 5);
    input = {}; input[0] = 0.25; input[4] = 0.5; input[8] = 0.75; input[12] = 1;
    assert(evaluate(registry[46], input, output) == Result::Ok &&
           output[0] == 0.25 && output[1] == 0.5 && output[2] == 0.75 && output[3] == 1);
}
