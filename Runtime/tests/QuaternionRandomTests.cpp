#include "liberafx/LbSpawnablePackage.h"
#include "ProgramFixture.h"
#include "../Core/QuaternionRandom/LbQuaternionRandomNodes.h"
#include "liberafx/math/LbQuaternionMath.h"
#include "liberafx/math/LbRandomMath.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <cmath>
#include <cstdlib>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <iterator>
#include <limits>
#include <vector>
#ifdef _WIN32
#include <crtdbg.h>
#endif

namespace liberafx::quaternion_random_tests
{
using Inputs = std::array<double, 16>;
using Outputs = std::array<double, 4>;

auto close(double a, double b, double tolerance = 2e-12) noexcept -> bool
{
    return std::abs(a - b) <= tolerance;
}

auto descriptor(unsigned id) -> const CustomNodeDescriptor&
{
    for (const auto& entry : quaternion_random_node_detail::quaternionRandomNodeDescriptors())
    {
        if (entry.typeId[15] == id)
        {
            return entry;
        }
    }
    std::abort();
}

auto evaluate(unsigned id, const Inputs& inputs, Outputs& outputs) -> Result
{
    const auto& entry = descriptor(id);
    auto invocation = CustomNodeInvocation{inputs.data(), entry.inputCount, entry.inputWidths, outputs.data(), entry.outputWidth};
    return entry.function(invocation);
}

void checkVector(const math::LbVector& actual, const math::LbVector& expected, double tolerance = 2e-12)
{
    for (auto lane = 0u; lane < 3; ++lane)
    {
        if (!close(actual[lane], expected[lane], tolerance))
        {
            std::fprintf(stderr, "lane %u: actual=%.17g expected=%.17g tolerance=%.17g\n",
                lane, actual[lane], expected[lane], tolerance);
        }
        assert(close(actual[lane], expected[lane], tolerance));
    }
}

void runQuaternionTests()
{
    auto inputs = Inputs{};
    auto outputs = Outputs{};
    assert(evaluate(1, inputs, outputs) == Result::Ok && outputs == Outputs({0, 0, 0, 1}));
    inputs[0] = 1; inputs[4] = 2; inputs[8] = 3; inputs[12] = 4;
    assert(evaluate(2, inputs, outputs) == Result::Ok && outputs == Outputs({1, 2, 3, 4}));
    inputs = {1, 2, 3, 4};
    assert(evaluate(4, inputs, outputs) == Result::Ok && outputs == Outputs({-1, -2, -3, 4}));
    assert(evaluate(5, inputs, outputs) == Result::Ok);
    auto product = math::multiplyQuaternion({1, 2, 3, 4}, outputs);
    assert(close(product[0], 0) && close(product[1], 0) && close(product[2], 0) && close(product[3], 1));
    inputs = {};
    for (auto id : {3u, 5u, 8u, 9u, 10u, 11u})
    {
        outputs.fill(123);
        assert(evaluate(id, inputs, outputs) == Result::InvalidArgument);
        for (auto lane = 0u; lane < descriptor(id).outputWidth; ++lane)
        {
            assert(outputs[lane] == 0);
        }
    }
    inputs = {std::numeric_limits<double>::max(), std::numeric_limits<double>::max(), 0, 0};
    assert(evaluate(3, inputs, outputs) == Result::Ok && close(outputs[0], std::sqrt(0.5)));
    inputs = {std::numeric_limits<double>::denorm_min(), 0, 0, 0};
    assert(evaluate(3, inputs, outputs) == Result::Ok && outputs[0] == 1);
    assert(evaluate(5, inputs, outputs) == Result::NonFinite);
    inputs = {std::numeric_limits<double>::infinity(), 0, 0, 1};
    assert(evaluate(3, inputs, outputs) == Result::NonFinite);

    // Exact convention checks against the existing matrix implementation, including gimbal lock.
    const auto angles = std::array<math::LbVector, 9>{{
        {0, 0, 90}, {90, 0, 0}, {0, 90, 0}, {20, -35, 70}, {-120, 45, 170},
        {12, 90, 33}, {-50, -90, 120}, {0, 89.99999, 180}, {360, -720, 1080}}};
    for (const auto& degrees : angles)
    {
        auto quaternion = math::quaternionFromEuler(degrees);
        auto matrix = math::makeTransform({}, degrees, {1, 1, 1});
        for (auto column = 0u; column < 3; ++column)
        {
            auto vector = math::LbVector{};
            vector[column] = 1;
            auto rotated = math::LbVector{};
            assert(math::rotateByQuaternion(quaternion, vector, rotated));
            checkVector(rotated, {matrix[column], matrix[4 + column], matrix[8 + column]});
        }
        auto restored = math::LbVector{};
        assert(math::quaternionToEuler(quaternion, restored));
        auto roundtrip = math::makeTransform({}, restored, {1, 1, 1});
        for (auto lane = 0u; lane < 16; ++lane)
        {
            assert(close(matrix[lane], roundtrip[lane], 2e-8));
        }
    }
    auto rotated = math::LbVector{};
    assert(math::rotateByQuaternion(math::quaternionFromEuler({90, 0, 0}), {0, 1, 0}, rotated));
    checkVector(rotated, {0, 0, -1});
    assert(math::rotateByQuaternion(math::quaternionFromEuler({0, 90, 0}), {1, 0, 0}, rotated));
    checkVector(rotated, {0, 0, 1});
    auto axis = math::LbQuaternion{};
    assert(math::quaternionFromAxisAngle({0, 0, 7}, 90, axis));
    assert(math::rotateByQuaternion(axis, {1, 0, 0}, rotated));
    checkVector(rotated, {0, 1, 0});

    auto a = math::quaternionFromEuler({23, 45, -10});
    auto b = math::quaternionFromEuler({-70, 5, 120});
    auto once = math::LbVector{}, twice = math::LbVector{}, combined = math::LbVector{};
    assert(math::rotateByQuaternion(b, {1, 2, 3}, once));
    assert(math::rotateByQuaternion(a, once, twice));
    assert(math::rotateByQuaternion(math::multiplyQuaternion(a, b), {1, 2, 3}, combined));
    checkVector(combined, twice);
    auto antipode = a;
    for (auto& component : antipode)
    {
        component = -component;
    }
    for (auto t : {0.0, 0.1, 0.5, 0.9, 1.0})
    {
        auto interpolated = math::LbQuaternion{};
        assert(math::slerpQuaternion(a, antipode, t, interpolated));
        assert(math::rotateByQuaternion(interpolated, {1, 2, 3}, once));
        assert(math::rotateByQuaternion(a, {1, 2, 3}, twice));
        checkVector(once, twice);
    }
    auto halfway = math::LbQuaternion{};
    assert(math::slerpQuaternion({0, 0, 0, 1}, math::quaternionFromEuler({0, 0, 270}), 0.5, halfway));
    assert(math::rotateByQuaternion(halfway, {1, 0, 0}, rotated));
    checkVector(rotated, {std::sqrt(0.5), -std::sqrt(0.5), 0});
    auto near = math::quaternionFromEuler({0, 0, 0.000001});
    assert(math::slerpQuaternion({0, 0, 0, 1}, near, 0.5, halfway));
    assert(close(halfway[3], 1));
    assert(math::slerpQuaternion(a, b, -5, halfway));
    assert(math::rotateByQuaternion(halfway, {1, 2, 3}, once));
    assert(math::rotateByQuaternion(a, {1, 2, 3}, twice));
    checkVector(once, twice);
}

void runRandomTests()
{
    auto inputs = Inputs{};
    inputs[0] = 12345; inputs[4] = 17; inputs[8] = -7; inputs[12] = 11;
    auto outputs = Outputs{};
    assert(evaluate(0x21, inputs, outputs) == Result::Ok);
    auto first = outputs[0];
    assert(close(first, -7 * (1 - math::randomUnit(12345, 17)) + 11 * math::randomUnit(12345, 17)));
    inputs[4] = 999;
    assert(evaluate(0x21, inputs, outputs) == Result::Ok && outputs[0] != first);
    inputs[4] = 17;
    assert(evaluate(0x21, inputs, outputs) == Result::Ok && outputs[0] == first);
    inputs[8] = 11; inputs[12] = -7;
    assert(evaluate(0x21, inputs, outputs) == Result::Ok && outputs[0] == first);
    for (auto id : {0x21u, 0x22u, 0x23u, 0x24u, 0x25u})
    {
        inputs[0] = 0.5;
        assert(evaluate(id, inputs, outputs) == Result::InvalidArgument);
        inputs[0] = 12345; inputs[4] = 4294967296.0;
        assert(evaluate(id, inputs, outputs) == Result::InvalidArgument);
        inputs[4] = -1;
        assert(evaluate(id, inputs, outputs) == Result::InvalidArgument);
        inputs[4] = 17;
    }
    inputs[8] = -2147483648.0; inputs[12] = 2147483647.0;
    assert(evaluate(0x22, inputs, outputs) == Result::Ok);
    assert(outputs[0] == static_cast<double>(static_cast<std::int64_t>(-2147483648LL) + math::randomWord(12345, 17)));
    inputs[8] = 0.5;
    assert(evaluate(0x22, inputs, outputs) == Result::InvalidArgument);
    inputs[8] = 2147483648.0;
    assert(evaluate(0x22, inputs, outputs) == Result::InvalidArgument);
    inputs[8] = -9; inputs[12] = -9;
    assert(evaluate(0x22, inputs, outputs) == Result::Ok && outputs[0] == -9);
    inputs[8] = 10; inputs[12] = -10;
    assert(evaluate(0x22, inputs, outputs) == Result::Ok && outputs[0] >= -10 && outputs[0] <= 10);
    auto exercisedRejection = false;
    for (auto index = 0u; index < 256; ++index)
    {
        // More than half the uint32 domain: almost half of initial words need rejection.
        constexpr auto count = std::uint64_t{2147483649};
        constexpr auto threshold = std::uint64_t{2147483647};
        auto stream = 0u;
        auto word = math::randomWord(42, index, stream);
        while (word < threshold)
        {
            exercisedRejection = true;
            word = math::randomWord(42, index, ++stream);
        }
        auto expected = std::int64_t{-2147483648LL} + static_cast<std::int64_t>(word % count);
        assert(math::randomInteger(42, index, std::numeric_limits<std::int32_t>::min(), 0) == expected);
    }
    assert(exercisedRejection);
    inputs = {12345, 0, 0, 0, 17, 0, 0, 0, -2, 10, 0.25, 0, 3, -10, 0.25, 0};
    assert(evaluate(0x23, inputs, outputs) == Result::Ok && outputs[0] >= -2 && outputs[0] <= 3 &&
           outputs[1] >= -10 && outputs[1] <= 10 && outputs[2] == 0.25);
    auto mean = math::LbVector{};
    auto meanRadiusCubed = 0.0;
    auto buckets = std::array<unsigned, 7>{};
    for (auto index = 0u; index < 4096; ++index)
    {
        auto direction = math::randomSphereDirection(42, index);
        auto point = math::randomSpherePoint(42, index);
        auto lengthSquared = 0.0, radiusSquared = 0.0;
        for (auto lane = 0u; lane < 3; ++lane)
        {
            lengthSquared += direction[lane] * direction[lane];
            radiusSquared += point[lane] * point[lane];
            mean[lane] += direction[lane];
        }
        assert(close(lengthSquared, 1) && radiusSquared <= 1 + 1e-14);
        meanRadiusCubed += std::pow(radiusSquared, 1.5);
        auto integer = math::randomInteger(42, index, 0, 6);
        assert(integer >= 0 && integer <= 6);
        ++buckets[static_cast<std::size_t>(integer)];
        assert(math::randomInteger(42, index, 6, 0) == integer);
    }
    for (auto component : mean)
    {
        assert(std::abs(component / 4096) < 0.06);
    }
    assert(std::abs(meanRadiusCubed / 4096 - 0.5) < 0.06);
    for (auto count : buckets)
    {
        assert(count > 450 && count < 750);
    }
}

auto enqueue(void*, Job job, void* context) noexcept -> bool
{
    job(context);
    return true;
}

// Handcrafted v3 records independently check UUID network order / little-endian numeric
// fields and native registry dispatch for every new descriptor, without a converter binary.
void runRegistryTests()
{
    for (const auto& entry : quaternion_random_node_detail::quaternionRandomNodeDescriptors())
    {
        auto bytes = std::array<std::uint8_t, 272>{};
        std::memcpy(bytes.data(), "LFXB", 4);
        bytes[4] = 3; bytes[6] = 1; bytes[8] = 1; bytes[13] = 1;
        bytes[16] = 1;
        bytes[32] = 40; bytes[33] = entry.outputWidth; bytes[34] = entry.inputCount;
        std::memcpy(bytes.data() + 40, entry.typeId, 16);
        bytes[56] = 1;
        for (auto input = 0u; input < 4; ++input)
        {
            for (auto byte = 0u; byte < 4; ++byte)
            {
                bytes[64 + input * 8 + byte] = 0xff;
                bytes[224 + input * 4 + byte] = 0xff;
            }
            for (auto lane = 0u; lane < 4; ++lane)
            {
                // IEEE-754 binary64 1.0, encoded explicitly little-endian.
                bytes[96 + input * 32 + lane * 8 + 6] = 0xf0;
                bytes[96 + input * 32 + lane * 8 + 7] = 0x3f;
            }
        }
        auto current = runtime_test_fixture::promoteSimpleProgram(
            std::vector<std::uint8_t>(bytes.begin(), bytes.end()), &entry, 1);
        auto binding = CustomNodeBinding{};
        auto program = Program{};
        assert(Program::load(current.data(), current.size(), nullptr, 0, &binding, 1, program) == Result::Ok);
        assert(std::memcmp(binding.descriptor->typeId, entry.typeId, 16) == 0);
        alignas(16) auto work = std::array<double, 128>{};
        auto instance = Instance{};
        assert(instance.initialize(program, work.data(), work.size()) == Result::Ok);
        assert(instance.updateAsync(0, enqueue, nullptr) == Result::Ok && instance.result() == Result::Ok);
        for (auto lane = 0u; lane < entry.outputWidth; ++lane)
        {
            assert(std::isfinite(work[lane]));
        }
    }
}

// Compiler-generated LFXP fixture from WriteNativeFixtures. This path does not
// invoke the standalone converter and is not executable-converter integration coverage.
void runCompiledFixture(const char* path, bool random)
{
    auto file = std::ifstream(path, std::ios::binary);
    assert(file.good());
    auto bytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>{});
    auto package = SpawnablePackage{};
    assert(loadSpawnablePackage(bytes.data(), bytes.size(), package) == Result::Ok);
    const auto& programBytes = package.parentBytecode;
    auto program = Program{};
    auto bindings = std::array<CustomNodeBinding, 2>{};
    assert(Program::load(programBytes.data(), programBytes.size(), nullptr, 0, bindings.data(), bindings.size(), program) == Result::Ok);
    auto wordCount = (program.workBytes() + sizeof(std::max_align_t) - 1) / sizeof(std::max_align_t);
    auto work = std::vector<std::max_align_t>(wordCount);
    auto instance = Instance{};
    assert(instance.initialize(program, reinterpret_cast<double*>(work.data()),
        work.size() * sizeof(std::max_align_t) / sizeof(double)) == Result::Ok);
    assert(instance.updateAsync(7, enqueue, nullptr) == Result::Ok && instance.result() == Result::Ok);
    auto expected = math::LbVector{};
    if (random)
    {
        auto x = math::randomUnit(12345, 7, 0), y = math::randomUnit(12345, 7, 1);
        expected = {-2 * (1 - x) + 3 * x, -10 * (1 - y) + 10 * y, 0.25};
    }
    else
    {
        auto matrix = math::makeTransform({}, {20, -35, 70}, {1, 1, 1});
        expected = {matrix[0] + 2 * matrix[1] - 3 * matrix[2],
                    matrix[4] + 2 * matrix[5] - 3 * matrix[6],
                    matrix[8] + 2 * matrix[9] - 3 * matrix[10]};
    }
    auto values = reinterpret_cast<const double*>(work.data());
    checkVector({values[4], values[5], values[6]}, expected);
}
}

int main(int argc, char** argv)
{
#ifdef _WIN32
    _set_error_mode(_OUT_TO_STDERR);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
    _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE);
    _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
#endif
    using namespace liberafx::quaternion_random_tests;
    runQuaternionTests();
    runRandomTests();
    runRegistryTests();
    if (argc == 3)
    {
        runCompiledFixture(argv[1], false);
        runCompiledFixture(argv[2], true);
    }
    else
    {
        assert(argc == 1);
    }
}
