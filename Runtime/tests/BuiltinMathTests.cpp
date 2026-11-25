#include "ProgramFixture.h"
#include "liberafx/LbVfxRuntime.h"
#include "liberafx/LbSpawnablePackage.h"
#include "liberafx/math/LbMatrixMath.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <array>
#include <cassert>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <cstdlib>
#include <fstream>
#include <iterator>
#include <limits>
#include <vector>
#if defined(_MSC_VER) && defined(_DEBUG)
#include <crtdbg.h>
#endif

namespace {
using namespace liberafx;
using Bytes = std::array<std::uint8_t, 272>;

void write32(Bytes& bytes, std::size_t offset, std::uint32_t value)
{
    for (auto i = 0u; i < 4; ++i) bytes[offset + i] = static_cast<std::uint8_t>(value >> (8 * i));
}
template <typename Buffer>
void writeDouble(Buffer& bytes, std::size_t offset, double value)
{
    auto bits = std::uint64_t{};
    std::memcpy(&bits, &value, 8);
    for (auto i = 0u; i < 8; ++i) bytes[offset + i] = static_cast<std::uint8_t>(bits >> (8 * i));
}
auto fixture(const CustomNodeDescriptor& descriptor) -> std::vector<std::uint8_t>
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
    if (descriptor.typeId[15] == 53 || descriptor.typeId[15] == 54)
    {
        writeDouble(bytes, 96 + 2 * 32, 2);
    }
    return runtime_test_fixture::promoteSimpleProgram(
        std::vector<std::uint8_t>(bytes.begin(), bytes.end()), &descriptor, 1);
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
#if defined(_WIN32)
    _set_error_mode(_OUT_TO_STDERR);
#endif
#if defined(_MSC_VER) && defined(_DEBUG)
    _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE);
    _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif
    auto count = std::size_t{};
    auto registry = builtinNodes(count);
    assert(registry != nullptr && count == 107); // Includes the three real-time clock primitives.
    const std::uint8_t expectedPrefix[] = {0x74, 0x30, 0x5a, 0xc1, 0x91, 0xb2, 0x4c, 0x33, 0x8d, 0x44};
    assert(std::memcmp(registry[0].typeId, expectedPrefix, sizeof(expectedPrefix)) == 0);
    if (argc >= 2)
    {
        auto file = std::ifstream(argv[1], std::ios::binary);
        auto packageBytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(file), {});
        auto package = SpawnablePackage{};
        assert(loadSpawnablePackage(packageBytes.data(), packageBytes.size(), package) == Result::Ok);
        auto& compiled = package.parentBytecode;
        auto compiledBinding = CustomNodeBinding{};
        auto compiledProgram = Program{};
        assert(Program::load(compiled.data(), compiled.size(), nullptr, 0, &compiledBinding, 1, compiledProgram) == Result::Ok);
        alignas(16) auto compiledWork = std::array<double, 128>{};
        auto compiledInstance = Instance{};
        assert(compiledInstance.initialize(compiledProgram, compiledWork.data(), compiledWork.size()) == Result::Ok);
        assert(compiledInstance.updateAsync(0, enqueue, nullptr) == Result::Ok);
        assert(compiledInstance.result() == Result::Ok && compiledWork[0] == 1);
    }
    if (argc >= 3)
    {
        auto file = std::ifstream(argv[2], std::ios::binary);
        auto packageBytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(file), {});
        auto package = SpawnablePackage{};
        assert(loadSpawnablePackage(packageBytes.data(), packageBytes.size(), package) == Result::Ok);
        auto& compiled = package.parentBytecode;
        auto bindings = std::array<CustomNodeBinding, 3>{};
        auto program = Program{};
        assert(Program::load(compiled.data(), compiled.size(), nullptr, 0, bindings.data(), bindings.size(), program) == Result::Ok);
        alignas(16) auto work = std::array<double, 128>{};
        auto instance = Instance{};
        assert(instance.initialize(program, work.data(), work.size()) == Result::Ok);
        assert(instance.updateAsync(1.5707963267948966, enqueue, nullptr) == Result::Ok);
        assert(instance.result() == Result::Ok && std::abs(work[8] - 1) < 1e-15 &&
               work[9] == 0.25 && work[10] == 0 && work[11] == 1);
    }
    for (auto i = std::size_t{}; i < count; ++i)
    {
        if (registry[i].execution != CustomExecution::Parameter) continue;
        auto bytes = fixture(registry[i]);
        auto binding = CustomNodeBinding{};
        auto program = Program{};
        assert(Program::load(bytes.data(), bytes.size(), nullptr, 0, &binding, 1, program) == Result::Ok);
        assert(binding.descriptor == &registry[i]);
        alignas(16) auto work = std::array<double, 128>{};
        auto instance = Instance{};
        assert(instance.initialize(program, work.data(), work.size()) == Result::Ok);
        assert(instance.setTimeContext(0, 0) == Result::Ok);
        assert(instance.updateAsync(0, enqueue, nullptr) == Result::Ok);
        assert(instance.result() == Result::Ok);
    }

    for (auto index = std::size_t{64}; index < 66; ++index)
    {
        auto bytes = fixture(registry[index]);
        auto binding = CustomNodeBinding{};
        auto program = Program{};
        assert(Program::load(bytes.data(), bytes.size(), nullptr, 0, &binding, 1, program) == Result::Ok);
        alignas(16) auto work = std::array<double, 128>{};
        auto instance = Instance{};
        assert(instance.initialize(program, work.data(), work.size()) == Result::Ok);
        assert(instance.updateAsync(0, enqueue, nullptr) == Result::Ok);
        assert(instance.result() == Result::NotReady);
        assert(instance.setTimeContext(-1, 0) == Result::InvalidArgument);
        assert(instance.setTimeContext(3, -1) == Result::InvalidArgument);
        assert(instance.setTimeContext(3, 0.125) == Result::Ok);
        assert(instance.updateAsync(1, enqueue, nullptr) == Result::Ok);
        assert(instance.result() == Result::Ok && work[0] == (index == 64 ? 3 : 0.125));
        assert(instance.updateAsync(2, enqueue, nullptr) == Result::Ok);
        assert(instance.result() == Result::NotReady);
        assert(instance.setTimeContext(4, 0.125) == Result::Ok);
        assert(instance.clearTimeContext() == Result::Ok);
        assert(instance.updateAsync(2, enqueue, nullptr) == Result::Ok);
        assert(instance.result() == Result::NotReady);
    }

    for (auto index = std::size_t{62}; index < 64; ++index)
    {
        auto bytes = fixture(registry[index]);
        writeDouble(bytes, 96, 2);
        auto binding = CustomNodeBinding{};
        auto program = Program{};
        assert(Program::load(bytes.data(), bytes.size(), nullptr, 0, &binding, 1, program) == Result::Ok);
        alignas(16) auto work = std::array<double, 128>{};
        auto defaults = std::array<double, 16>{};
        auto instance = Instance{};
        assert(instance.initialize(program, work.data(), work.size(), defaults.data(), defaults.size()) == Result::Ok);
        assert(instance.updateAsync(0, enqueue, nullptr) == Result::Ok && instance.result() == Result::Ok);
        assert(work[0] == (index == 62 ? 2 : 0));
        assert(instance.setParameter(bytes.data() + 16, 0, 0, 10) == Result::Ok);
        assert(instance.updateAsync(1, enqueue, nullptr) == Result::Ok && instance.result() == Result::Ok);
        assert(work[0] == (index == 62 ? 1 : 0.5));
        assert(instance.updateAsync(3, enqueue, nullptr) == Result::Ok && instance.result() == Result::Ok);
        assert(work[0] == (index == 62 ? 0 : 1));
        auto zeroBytes = bytes;
        writeDouble(zeroBytes, 96, 0);
        auto zeroProgram = Program{};
        auto zeroBinding = CustomNodeBinding{};
        assert(Program::load(zeroBytes.data(), zeroBytes.size(), nullptr, 0, &zeroBinding, 1, zeroProgram) == Result::Ok);
        alignas(16) auto zeroWork = std::array<double, 128>{};
        auto zero = Instance{};
        assert(zero.initialize(zeroProgram, zeroWork.data(), zeroWork.size()) == Result::Ok);
        assert(zero.updateAsync(0, enqueue, nullptr) == Result::Ok && zero.result() == Result::Ok);
        assert(zeroWork[0] == (index == 62 ? 0 : 1));
    }

    auto bytes = fixture(registry[0]);
    writeDouble(bytes, 96, -8);
    auto binding = CustomNodeBinding{};
    auto program = Program{};
    assert(Program::load(bytes.data(), bytes.size(), nullptr, 0, &binding, 1, program) == Result::Ok);
    alignas(16) auto work = std::array<double, 128>{};
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

    for (auto i = 0u; i < 10; ++i)
    {
        auto& descriptor = registry[79 + i];
        assert(std::memcmp(descriptor.typeId, expectedPrefix, sizeof(expectedPrefix)) == 0);
        assert(descriptor.typeId[15] == 48 + i && descriptor.abiVersion == 1 &&
               descriptor.outputWidth == 1);
        input = {};
        input[0] = std::numeric_limits<double>::infinity();
        assert(evaluate(descriptor, input, output) == Result::NonFinite);
        input[0] = std::numeric_limits<double>::quiet_NaN();
        assert(evaluate(descriptor, input, output) == Result::NonFinite);
    }
    input = {}; input[0] = -5.5; input[4] = 2;
    assert(evaluate(registry[79], input, output) == Result::Ok && output[0] == -1.5);
    input[0] = 5.5; input[4] = -2;
    assert(evaluate(registry[79], input, output) == Result::Ok && output[0] == 1.5);
    input[4] = 0;
    assert(evaluate(registry[79], input, output) == Result::DivideByZero);
    input = {}; input[0] = -1.25;
    assert(evaluate(registry[80], input, output) == Result::Ok && output[0] == 0.75);
    assert(evaluate(registry[81], input, output) == Result::Ok && output[0] == -1);
    assert(evaluate(registry[88], input, output) == Result::Ok && output[0] == -1);
    input[0] = 0;
    assert(evaluate(registry[82], input, output) == Result::Ok && output[0] == 1);
    input[0] = 3;
    assert(evaluate(registry[83], input, output) == Result::Ok && output[0] == 8);
    input[0] = 10000;
    assert(evaluate(registry[82], input, output) == Result::NonFinite);
    assert(evaluate(registry[83], input, output) == Result::NonFinite);
    input = {}; input[0] = 0.25; input[8] = 1;
    assert(evaluate(registry[84], input, output) == Result::Ok && output[0] == 0.15625);
    input[0] = -1;
    assert(evaluate(registry[84], input, output) == Result::Ok && output[0] == 0);
    input[0] = 2;
    assert(evaluate(registry[84], input, output) == Result::Ok && output[0] == 1);
    input[8] = 0;
    assert(evaluate(registry[84], input, output) == Result::InvalidArgument);
    input[8] = -1;
    assert(evaluate(registry[84], input, output) == Result::InvalidArgument);
    input[0] = 0; input[4] = -std::numeric_limits<double>::max();
    input[8] = std::numeric_limits<double>::max();
    assert(evaluate(registry[84], input, output) == Result::NonFinite);
    input = {}; input[0] = 2; input[4] = 4; input[8] = 0;
    assert(evaluate(registry[85], input, output) == Result::Ok && output[0] == 0.5);
    input[0] = 6;
    assert(evaluate(registry[85], input, output) == Result::Ok && output[0] == -0.5);
    input[8] = 4;
    assert(evaluate(registry[85], input, output) == Result::DivideByZero);
    input[0] = 0; input[4] = -std::numeric_limits<double>::max();
    input[8] = std::numeric_limits<double>::max();
    assert(evaluate(registry[85], input, output) == Result::NonFinite);
    input = {}; input[0] = -2;
    assert(evaluate(registry[86], input, output) == Result::Ok && output[0] == 0);
    input[0] = 2;
    assert(evaluate(registry[86], input, output) == Result::Ok && output[0] == 1);
    input[4] = 2;
    assert(evaluate(registry[87], input, output) == Result::Ok && output[0] == 1);
    input[0] = 1;
    assert(evaluate(registry[87], input, output) == Result::Ok && output[0] == 0);

    const std::uint8_t matrixPrefix[] = {0xb0, 0xaf, 0x61, 0xd2, 0x29, 0xf6, 0x4c, 0xc9, 0xa7, 0x1e};
    for (auto id = 1u; id <= 13; ++id)
    {
        auto descriptor = &registry[46 + id];
        assert(std::memcmp(descriptor->typeId, matrixPrefix, sizeof(matrixPrefix)) == 0);
        assert(descriptor->typeId[10] == 0 && descriptor->typeId[11] == 0 &&
               descriptor->typeId[12] == 0 && descriptor->typeId[13] == 0 &&
               descriptor->typeId[14] == 0 &&
               descriptor->typeId[15] == id && descriptor->abiVersion == 1 &&
               descriptor->execution == CustomExecution::Parameter && !descriptor->outputIsColor);
        assert(descriptor->inputCount == (id <= 4 ? 3 : 4));
        for (auto inputIndex = 0u; inputIndex < descriptor->inputCount; ++inputIndex)
            assert(descriptor->inputIsColor[inputIndex] == 0 &&
                   descriptor->inputWidths[inputIndex] == (id <= 4 ? 3 : 4));
        assert(descriptor->outputWidth == (id <= 8 ? 4 : id == 9 || id == 13 ? 1 :
                                             id <= 12 ? 3 : 0));
    }

    auto transformInput = std::array<double, 16>{};
    transformInput[0] = 5; transformInput[1] = 6; transformInput[2] = 7;
    transformInput[4] = 90; transformInput[5] = 30; transformInput[6] = 90;
    transformInput[8] = 2; transformInput[9] = 3; transformInput[10] = 4;
    auto transformMatrix = std::array<double, 16>{};
    for (auto row = 0u; row < 4; ++row)
    {
        auto rowOutput = std::array<double, 4>{};
        assert(evaluate(registry[47 + row], transformInput, rowOutput) == Result::Ok);
        for (auto column = 0u; column < 4; ++column)
            transformMatrix[row * 4 + column] = rowOutput[column];
    }
    assert(std::abs(transformMatrix[0]) < 1e-14 && std::abs(transformMatrix[1]) < 1e-14 &&
           transformMatrix[2] == -4 && transformMatrix[3] == 5);
    assert(std::abs(transformMatrix[4] - 1.7320508075688772) < 1e-14 && std::abs(transformMatrix[5] - 1.5) < 1e-14 &&
           std::abs(transformMatrix[6]) < 1e-14 && transformMatrix[7] == 6);
    assert(std::abs(transformMatrix[8] - 1) < 1e-14 && std::abs(transformMatrix[9] + 2.598076211353316) < 1e-14 &&
           std::abs(transformMatrix[10]) < 1e-14 && transformMatrix[11] == 7);
    assert(transformMatrix[12] == 0 && transformMatrix[13] == 0 &&
           transformMatrix[14] == 0 && transformMatrix[15] == 1);

    auto matrixInput = transformMatrix;
    auto decompositionValid = std::array<double, 4>{};
    assert(evaluate(registry[59], matrixInput, decompositionValid) == Result::Ok &&
           decompositionValid[0] == 1);
    auto position = std::array<double, 4>{};
    auto rotation = std::array<double, 4>{};
    auto scale = std::array<double, 4>{};
    assert(evaluate(registry[56], matrixInput, position) == Result::Ok);
    assert(evaluate(registry[57], matrixInput, rotation) == Result::Ok);
    assert(evaluate(registry[58], matrixInput, scale) == Result::Ok);
    assert(position[0] == 5 && position[1] == 6 && position[2] == 7);
    assert(std::abs(rotation[0] - 90) < 1e-10 && std::abs(rotation[1] - 30) < 1e-10 &&
           std::abs(rotation[2] - 90) < 1e-10);
    assert(std::abs(scale[0] - 2) < 1e-14 && std::abs(scale[1] - 3) < 1e-14 &&
           std::abs(scale[2] - 4) < 1e-14);

    auto reflectedTransformInput = transformInput;
    reflectedTransformInput[8] = -2;
    auto reflectedMatrix = math::makeTransform(
        {transformInput[0], transformInput[1], transformInput[2]},
        {transformInput[4], transformInput[5], transformInput[6]},
        {reflectedTransformInput[8], reflectedTransformInput[9], reflectedTransformInput[10]});
    assert(evaluate(registry[59], reflectedMatrix, decompositionValid) == Result::Ok &&
           decompositionValid[0] == 1);
    assert(evaluate(registry[58], reflectedMatrix, scale) == Result::Ok &&
           std::abs(scale[0] + 2) < 1e-14 && std::abs(scale[1] - 3) < 1e-14 &&
           std::abs(scale[2] - 4) < 1e-14);
    assert(evaluate(registry[57], reflectedMatrix, rotation) == Result::Ok &&
           std::abs(rotation[0] - 90) < 1e-10 && std::abs(rotation[1] - 30) < 1e-10 &&
           std::abs(rotation[2] - 90) < 1e-10);

    auto generalMatrix = std::array<double, 16>{
        2, 1, 0, 3,
        0, 3, 1, 2,
        0, 0, 4, 1,
        0, 0, 0, 2
    };
    auto expectedInverse = std::array<double, 16>{
        0.5, -1.0 / 6, 1.0 / 24, -29.0 / 48,
        0, 1.0 / 3, -1.0 / 12, -7.0 / 24,
        0, 0, 0.25, -0.125,
        0, 0, 0, 0.5
    };
    auto inverseMatrix = std::array<double, 16>{};
    for (auto row = 0u; row < 4; ++row)
    {
        auto rowOutput = std::array<double, 4>{};
        assert(evaluate(registry[51 + row], generalMatrix, rowOutput) == Result::Ok);
        for (auto column = 0u; column < 4; ++column)
            inverseMatrix[row * 4 + column] = rowOutput[column];
    }
    auto inverseValid = std::array<double, 4>{};
    assert(evaluate(registry[55], generalMatrix, inverseValid) == Result::Ok && inverseValid[0] == 1);
    for (auto index = 0u; index < 16; ++index)
        assert(std::abs(inverseMatrix[index] - expectedInverse[index]) < 1e-14);
    for (auto row = 0u; row < 4; ++row)
        for (auto column = 0u; column < 4; ++column)
        {
            auto product = 0.0;
            for (auto inner = 0u; inner < 4; ++inner)
                product += generalMatrix[row * 4 + inner] * inverseMatrix[inner * 4 + column];
            assert(std::abs(product - (row == column ? 1.0 : 0.0)) < 1e-14);
        }

    auto largeTranslation = std::array<double, 16>{
        1, 0, 0, 1e15, 0, 1, 0, -2e15, 0, 0, 1, 3e15, 0, 0, 0, 1
    };
    for (auto row = 0u; row < 4; ++row)
    {
        auto rowOutput = std::array<double, 4>{};
        assert(evaluate(registry[51 + row], largeTranslation, rowOutput) == Result::Ok);
        assert(rowOutput[row] == 1);
        for (auto column = 0u; column < 4; ++column)
            if (column != row)
                assert(rowOutput[column] == ((row == 0 && column == 3) ? -1e15 :
                    (row == 1 && column == 3) ? 2e15 : (row == 2 && column == 3) ? -3e15 : 0));
    }
    assert(evaluate(registry[55], largeTranslation, inverseValid) == Result::Ok && inverseValid[0] == 1);

    auto singular = std::array<double, 16>{};
    singular[0] = singular[5] = singular[10] = 1;
    auto zeroRow = std::array<double, 4>{};
    assert(evaluate(registry[55], singular, inverseValid) == Result::Ok && inverseValid[0] == 0);
    for (auto row = 0u; row < 4; ++row)
    {
        assert(evaluate(registry[51 + row], singular, zeroRow) == Result::Ok);
        for (auto value : zeroRow) assert(value == 0);
    }
    auto nearlySingular = singular;
    nearlySingular[15] = 1e-13;
    assert(evaluate(registry[55], nearlySingular, inverseValid) == Result::Ok && inverseValid[0] == 0);

    auto sheared = transformMatrix;
    sheared[1] += 0.25;
    assert(evaluate(registry[59], sheared, decompositionValid) == Result::Ok &&
           decompositionValid[0] == 0);
    assert(evaluate(registry[56], sheared, position) == Result::Ok &&
           evaluate(registry[57], sheared, rotation) == Result::Ok &&
           evaluate(registry[58], sheared, scale) == Result::Ok);
    for (auto value : position) assert(value == 0);
    for (auto value : rotation) assert(value == 0);
    for (auto value : scale) assert(value == 0);
    auto perspective = transformMatrix;
    perspective[12] = 0.01;
    assert(evaluate(registry[59], perspective, decompositionValid) == Result::Ok &&
           decompositionValid[0] == 0);
    auto zeroScale = std::array<double, 16>{};
    zeroScale[3] = 1; zeroScale[7] = 2; zeroScale[11] = 3; zeroScale[15] = 1;
    assert(evaluate(registry[59], zeroScale, decompositionValid) == Result::Ok &&
           decompositionValid[0] == 0);

    auto gimbalInput = transformInput;
    gimbalInput[4] = 23; gimbalInput[5] = 90; gimbalInput[6] = -49;
    auto gimbalMatrix = math::makeTransform(
        {gimbalInput[0], gimbalInput[1], gimbalInput[2]},
        {gimbalInput[4], gimbalInput[5], gimbalInput[6]},
        {gimbalInput[8], gimbalInput[9], gimbalInput[10]});
    assert(evaluate(registry[59], gimbalMatrix, decompositionValid) == Result::Ok &&
           decompositionValid[0] == 1);
    assert(evaluate(registry[57], gimbalMatrix, rotation) == Result::Ok && rotation[0] == 0 &&
           std::abs(rotation[1] - 90) < 1e-10);
    auto nearGimbalInput = gimbalInput;
    nearGimbalInput[5] = 89.999999999;
    auto nearGimbalMatrix = math::makeTransform(
        {nearGimbalInput[0], nearGimbalInput[1], nearGimbalInput[2]},
        {nearGimbalInput[4], nearGimbalInput[5], nearGimbalInput[6]},
        {nearGimbalInput[8], nearGimbalInput[9], nearGimbalInput[10]});
    assert(evaluate(registry[59], nearGimbalMatrix, decompositionValid) == Result::Ok &&
           decompositionValid[0] == 1);
    assert(evaluate(registry[57], nearGimbalMatrix, rotation) == Result::Ok && rotation[0] == 0);

    auto nonfiniteMatrix = generalMatrix;
    nonfiniteMatrix[0] = std::numeric_limits<double>::infinity();
    assert(evaluate(registry[55], nonfiniteMatrix, inverseValid) == Result::NonFinite &&
           inverseValid[0] == 0);
}
