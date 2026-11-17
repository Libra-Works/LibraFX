#include "liberafx/VfxRuntime.h"
#include "liberafx/math/MatrixMath.h"
#include "liberafx/math/VectorMath.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cctype>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <iomanip>
#include <iostream>
#include <new>
#include <string>
#include <utility>
#include <vector>

#if defined(_MSC_VER) && !defined(__clang__)
#include <intrin.h>
#endif

namespace {
using liberafx::math::Arithmetic;
using liberafx::math::ArithmeticResult;
using liberafx::math::Matrix4;
using liberafx::math::Vector3;
using liberafx::math::arithmetic;
using liberafx::math::decomposeTransform;
using liberafx::math::invertMatrix;
using liberafx::math::makeTransform;

struct Options
{
    std::size_t sampleCount = 9;
    std::size_t vectorOperations = 131072;
    std::size_t matrixOperations = 16384;
    std::size_t vmInstructions = 1048576;
};

struct Vector4
{
    double lane[4];
};

struct VectorInput
{
    Vector3 position;
    Vector3 rotation;
    Vector3 scale;
    Matrix4 matrix;
};

struct Timing
{
    std::vector<double> nanosecondsPerOperation;
    double checksum = 0;
    double relativeError = 0;
};

auto fail(const std::string& message) -> void
{
    std::cerr << "benchmark error: " << message << '\n';
    std::exit(EXIT_FAILURE);
}

auto parseSize(const char* text, const char* name) -> std::size_t
{
    auto parsed = std::strtoull(text, nullptr, 10);
    if (parsed == 0) fail(std::string(name) + " must be positive");
    return static_cast<std::size_t>(parsed);
}

auto parseOptions(int argc, char** argv) -> Options
{
    auto options = Options{};
    for (auto index = 1; index < argc; ++index)
    {
        if (index + 1 >= argc) fail(std::string("missing value for ") + argv[index]);
        auto value = argv[++index];
        auto name = std::string(argv[index - 1]);
        if (name == "--samples") options.sampleCount = parseSize(value, "--samples");
        else if (name == "--vector-ops") options.vectorOperations = parseSize(value, "--vector-ops");
        else if (name == "--matrix-ops") options.matrixOperations = parseSize(value, "--matrix-ops");
        else if (name == "--vm-instructions") options.vmInstructions = parseSize(value, "--vm-instructions");
        else fail("unknown argument " + name);
    }
    if (options.sampleCount < 3) fail("--samples must be at least 3");
    return options;
}

auto operationName(Arithmetic operation) -> const char*
{
    switch (operation)
    {
    case Arithmetic::Add: return "vector.add";
    case Arithmetic::Subtract: return "vector.subtract";
    case Arithmetic::Multiply: return "vector.multiply";
    case Arithmetic::Divide: return "vector.divide";
    }
    return "vector.unknown";
}

auto reference(double left, double right, Arithmetic operation) -> double
{
    switch (operation)
    {
    case Arithmetic::Add: return left + right;
    case Arithmetic::Subtract: return left - right;
    case Arithmetic::Multiply: return left * right;
    case Arithmetic::Divide: return left / right;
    }
    return 0;
}

auto scalarReference(Vector4& output, const Vector4& left, const Vector4& right,
                     std::size_t width, Arithmetic operation) -> bool
{
    for (auto lane = std::size_t{0}; lane < width; ++lane)
    {
        if (operation == Arithmetic::Divide && right.lane[lane] == 0) return false;
        output.lane[lane] = reference(left.lane[lane], right.lane[lane], operation);
        if (!std::isfinite(output.lane[lane])) return false;
    }
    return true;
}

auto vectorChecksum(const std::vector<Vector4>& values, std::size_t width) -> double
{
    auto checksum = 0.0;
    for (auto index = std::size_t{0}; index < values.size(); ++index)
        for (auto lane = std::size_t{0}; lane < width; ++lane)
            checksum += values[index].lane[lane] * (1.0 + static_cast<double>(index + lane) * 1e-7);
    return checksum;
}

auto maximumRelativeError(const std::vector<Vector4>& actual,
                          const std::vector<Vector4>& expected, std::size_t width) -> double
{
    auto maximumError = 0.0;
    for (auto index = std::size_t{0}; index < actual.size(); ++index)
        for (auto lane = std::size_t{0}; lane < width; ++lane)
        {
            if (!std::isfinite(actual[index].lane[lane]) ||
                !std::isfinite(expected[index].lane[lane]))
                fail("non-finite comparison input");
            auto difference = std::abs(actual[index].lane[lane] - expected[index].lane[lane]);
            auto scale = std::max(1.0, std::abs(expected[index].lane[lane]));
            maximumError = std::max(maximumError, difference / scale);
        }
    return maximumError;
}

auto median(std::vector<double> values) -> double
{
    auto middle = values.begin() + values.size() / 2;
    std::nth_element(values.begin(), middle, values.end());
    if (values.size() % 2 != 0) return *middle;
    auto lower = std::max_element(values.begin(), middle);
    return (*lower + *middle) / 2;
}

auto report(const char* name, const char* workload, std::size_t width,
            std::size_t operationCount, const Timing& timing) -> void
{
    auto sorted = timing.nanosecondsPerOperation;
    std::sort(sorted.begin(), sorted.end());
    std::cout << name << ',' << workload << ',' << width << ',' << operationCount << ','
              << sorted.size() << ',' << std::fixed << std::setprecision(3)
              << median(timing.nanosecondsPerOperation) << ',' << sorted.front() << ','
              << sorted.back() << ',' << std::setprecision(12) << timing.checksum << ','
              << std::scientific << timing.relativeError << std::fixed << '\n';
}

auto runVectorBenchmark(const Options& options, std::size_t width, Arithmetic operation,
                        std::size_t itemCount, const char* workload) -> void
{
    auto initial = std::vector<Vector4>(itemCount);
    auto right = std::vector<Vector4>(itemCount);
    auto actual = std::vector<Vector4>(itemCount);
    auto expected = std::vector<Vector4>(itemCount);
    for (auto index = std::size_t{0}; index < itemCount; ++index)
        for (auto lane = std::size_t{0}; lane < 4; ++lane)
        {
            initial[index].lane[lane] = 0.75 + static_cast<double>((index + lane * 3) % 17) * 0.002;
            auto variation = static_cast<double>((index + lane * 5) % 13) * 1e-9;
            if (operation == Arithmetic::Multiply || operation == Arithmetic::Divide)
                right[index].lane[lane] = 1.0 + 1e-7 + variation;
            else
                right[index].lane[lane] = 1e-6 + variation;
        }

    expected = initial;
    for (auto index = std::size_t{0}; index < options.vectorOperations; ++index)
    {
        auto item = index % itemCount;
        if (!scalarReference(expected[item], expected[item], right[item], width, operation))
            fail("scalar vector reference failed");
    }

    auto timing = Timing{};
    timing.nanosecondsPerOperation.reserve(options.sampleCount);
    actual = initial;
    for (auto index = std::size_t{0}; index < options.vectorOperations; ++index)
    {
        auto item = index % itemCount;
        auto result = arithmetic(actual[item].lane, actual[item].lane, right[item].lane,
                                 width, operation);
        if (result != ArithmeticResult::Ok) fail("vector warmup operation returned an error");
    }
    if (maximumRelativeError(actual, expected, width) > 1e-12)
        fail("vector warmup correctness comparison exceeded tolerance");
    for (auto sample = std::size_t{0}; sample < options.sampleCount; ++sample)
    {
        actual = initial;
        auto start = std::chrono::steady_clock::now();
        for (auto index = std::size_t{0}; index < options.vectorOperations; ++index)
        {
            auto item = index % itemCount;
            auto result = arithmetic(actual[item].lane, actual[item].lane, right[item].lane,
                                     width, operation);
            if (result != ArithmeticResult::Ok) fail("vector operation returned an error");
        }
        auto finish = std::chrono::steady_clock::now();
        auto elapsed = std::chrono::duration<double, std::nano>(finish - start).count();
        if (!std::isfinite(elapsed) || elapsed <= 0) fail("non-finite vector timing");
        timing.nanosecondsPerOperation.push_back(elapsed / options.vectorOperations);
        auto checksum = vectorChecksum(actual, width);
        if (!std::isfinite(checksum) || checksum == 0) fail("invalid vector checksum");
        if (sample == 0) timing.checksum = checksum;
        else if (std::abs(checksum - timing.checksum) > 1e-12 * std::max(1.0, std::abs(checksum)))
            fail("vector checksum changed between samples");
    }
    timing.relativeError = maximumRelativeError(actual, expected, width);
    if (!std::isfinite(timing.relativeError) || timing.relativeError > 1e-12)
        fail("native/scalar vector correctness comparison exceeded tolerance");
    report(operationName(operation), workload, width, options.vectorOperations, timing);
}

auto matrixChecksum(const std::vector<Matrix4>& values) -> double
{
    auto checksum = 0.0;
    for (auto index = std::size_t{0}; index < values.size(); ++index)
        for (auto lane = std::size_t{0}; lane < values[index].size(); ++lane)
            checksum += values[index][lane] * (1.0 + static_cast<double>(index + lane) * 1e-8);
    return checksum;
}

auto makeInputs(std::size_t count) -> std::vector<VectorInput>
{
    auto inputs = std::vector<VectorInput>(count);
    for (auto index = std::size_t{0}; index < count; ++index)
    {
        auto offset = static_cast<double>(index % 97) * 1e-3;
        inputs[index].position = {offset, -offset * 2, offset * 3};
        inputs[index].rotation = {7.0 + offset, -13.0 + offset, 21.0 + offset};
        inputs[index].scale = {1.1, 0.9, 1.3};
        inputs[index].matrix = makeTransform(inputs[index].position, inputs[index].rotation,
                                             inputs[index].scale);
    }
    return inputs;
}

auto verifyInverse(const std::vector<Matrix4>& matrices) -> double
{
    auto maximumError = 0.0;
    for (const auto& matrix : matrices)
    {
        auto inverse = Matrix4{};
        if (!invertMatrix(matrix, inverse)) fail("matrix inverse correctness check failed");
        for (auto row = 0u; row < 4; ++row)
            for (auto column = 0u; column < 4; ++column)
            {
                auto product = 0.0;
                for (auto inner = 0u; inner < 4; ++inner)
                    product += matrix[row * 4 + inner] * inverse[inner * 4 + column];
                auto target = row == column ? 1.0 : 0.0;
                maximumError = std::max(maximumError, std::abs(product - target));
            }
    }
    return maximumError;
}

auto verifyDecomposition(const std::vector<Matrix4>& matrices) -> double
{
    auto maximumError = 0.0;
    for (const auto& matrix : matrices)
    {
        auto position = Vector3{};
        auto rotation = Vector3{};
        auto scale = Vector3{};
        if (!decomposeTransform(matrix, position, rotation, scale))
            fail("matrix decomposition correctness check failed");
        auto reconstructed = makeTransform(position, rotation, scale);
        for (auto lane = std::size_t{0}; lane < matrix.size(); ++lane)
        {
            auto error = std::abs(matrix[lane] - reconstructed[lane]) /
                         std::max(1.0, std::abs(matrix[lane]));
            maximumError = std::max(maximumError, error);
        }
    }
    return maximumError;
}

template<typename Prepare, typename Work, typename Check>
auto runMatrixBenchmark(const char* name, const char* workload,
                        std::size_t operationCount, std::size_t sampleCount,
                        Prepare prepare, Work work, Check check, std::size_t width = 0) -> void
{
    auto timing = Timing{};
    timing.nanosecondsPerOperation.reserve(sampleCount);
    auto checksum = 0.0;
    auto warmupCheck = std::pair<double, double>{};
    prepare();
    work();
    warmupCheck = check();
    if (!std::isfinite(warmupCheck.first) || !std::isfinite(warmupCheck.second) ||
        warmupCheck.second > 1e-9)
        fail("warmup correctness check failed");
    for (auto sample = std::size_t{0}; sample < sampleCount; ++sample)
    {
        prepare();
        auto start = std::chrono::steady_clock::now();
        work();
        auto finish = std::chrono::steady_clock::now();
        auto elapsed = std::chrono::duration<double, std::nano>(finish - start).count();
        if (!std::isfinite(elapsed) || elapsed <= 0) fail("non-finite timing");
        timing.nanosecondsPerOperation.push_back(elapsed / operationCount);
        auto [sampleChecksum, sampleError] = check();
        if (!std::isfinite(sampleChecksum) || sampleChecksum == 0 ||
            !std::isfinite(sampleError) || sampleError > 1e-9)
            fail("invalid checksum or numerical error");
        if (sample == 0) checksum = sampleChecksum;
        else if (std::abs(sampleChecksum - checksum) > 1e-11 * std::max(1.0, std::abs(checksum)))
            fail("checksum changed between samples");
        timing.relativeError = std::max(timing.relativeError, sampleError);
    }
    timing.checksum = checksum;
    report(name, workload, width, operationCount, timing);
}

auto writeUnsigned(std::vector<std::uint8_t>& bytes, std::size_t offset,
                   std::uint64_t value, std::size_t count) -> void
{
    for (auto index = std::size_t{0}; index < count; ++index)
        bytes[offset + index] = static_cast<std::uint8_t>(value >> (index * 8));
}

auto writeDouble(std::vector<std::uint8_t>& bytes, std::size_t offset, double value) -> void
{
    auto bits = std::uint64_t{};
    std::memcpy(&bits, &value, sizeof(value));
    writeUnsigned(bytes, offset, bits, 8);
}

// Each independent effect-like chain is SourceA, SourceB, Add, Multiply,
// Subtract, Divide, Source(copy of the connected result). All values are live.
auto makeVmBytes(std::size_t chainCount, std::size_t width,
                 const std::vector<Vector4>& sources) -> std::vector<std::uint8_t>
{
    auto nodeCount = chainCount * 7;
    auto bytes = std::vector<std::uint8_t>(16 + nodeCount * 96);
    std::memcpy(bytes.data(), "LFXB", 4);
    writeUnsigned(bytes, 4, 2, 2);
    bytes[6] = 1;
    writeUnsigned(bytes, 8, nodeCount, 4);
    writeUnsigned(bytes, 12, 96, 2);
    auto noInput = std::uint32_t{0xffffffffu};
    for (auto chain = std::size_t{0}; chain < chainCount; ++chain)
        for (auto step = std::size_t{0}; step < 7; ++step)
        {
            auto node = chain * 7 + step;
            auto offset = 16 + node * 96;
            writeUnsigned(bytes, offset, node + 1, 8); // unique, stable fixture ID
            auto sourceOpcode = width == 3 ? 8 : 9;
            constexpr auto arithmeticOffsets = std::array{0, 2, 1, 3};
            bytes[offset + 16] = static_cast<std::uint8_t>(
                step < 2 || step == 6 ? sourceOpcode :
                (width == 3 ? 15 : 19) + arithmeticOffsets[step - 2]);
            bytes[offset + 17] = static_cast<std::uint8_t>(width);
            bytes[offset + 18] = step >= 2 ? static_cast<std::uint8_t>(width) : 0;
            bytes[offset + 19] = step >= 2 && step < 6 ? static_cast<std::uint8_t>(width) : 0;
            writeUnsigned(bytes, offset + 24, step >= 2 ? node - (step == 2 ? 2 : 1) : noInput, 4);
            writeUnsigned(bytes, offset + 28, step >= 2 && step < 6 ? chain * 7 + 1 : noInput, 4);
            if (step < 2)
                for (auto lane = std::size_t{0}; lane < width; ++lane)
                    writeDouble(bytes, offset + 32 + lane * 8, sources[chain * 2 + step].lane[lane]);
        }
    return bytes;
}

struct AlignedWork
{
    double* values;
    std::size_t alignment;

    explicit AlignedWork(const liberafx::Program& program)
        : values(static_cast<double*>(::operator new(program.workBytes(),
                  std::align_val_t{program.workAlignment()}))), alignment(program.workAlignment()) {}
    ~AlignedWork() { ::operator delete(values, std::align_val_t{alignment}); }
    AlignedWork(const AlignedWork&) = delete;
    auto operator=(const AlignedWork&) -> AlignedWork& = delete;
};

auto enqueueInline(void*, liberafx::Job job, void* context) noexcept -> bool
{
    job(context);
    return true;
}

// Make the entire output storage observable on every update, not merely the
// last repeated graph evaluation. This is a compiler barrier, not timed I/O.
auto consumeMemory(const void* output) -> void
{
#if defined(__clang__) || defined(__GNUC__)
    asm volatile("" : : "g"(output) : "memory");
#elif defined(_MSC_VER)
    static const void* volatile escapedOutput = nullptr;
    escapedOutput = output;
    _ReadWriteBarrier();
#else
    // Unknown compilers must supply a compiler barrier before trusting direct
    // graph measurements; never silently publish potentially eliminated work.
    (void)output;
    fail("no output compiler barrier for this compiler");
#endif
}

auto runVmBenchmarks(const Options& options, std::size_t chainCount,
                     std::size_t width, const char* workload) -> void
{
    auto nodeCount = chainCount * 7;
    auto updates = std::max(std::size_t{1}, (options.vmInstructions + nodeCount - 1) / nodeCount);
    auto sources = std::vector<Vector4>(chainCount * 2);
    for (auto chain = std::size_t{0}; chain < chainCount; ++chain)
        for (auto lane = std::size_t{0}; lane < width; ++lane)
        {
            sources[chain * 2].lane[lane] = 0.75 + static_cast<double>((chain + lane) % 31) * 0.01;
            sources[chain * 2 + 1].lane[lane] = 1.1 + static_cast<double>((chain + lane) % 17) * 0.01;
        }
    auto bytes = makeVmBytes(chainCount, width, sources);
    auto program = liberafx::Program{};
    if (liberafx::Program::load(bytes.data(), bytes.size(), program) != liberafx::Result::Ok)
        fail("production VM fixture load failed");
    auto storage = AlignedWork{program};
    auto instance = liberafx::Instance{};
    if (instance.initialize(program, storage.values, program.workBytes() / sizeof(double)) !=
        liberafx::Result::Ok) fail("production VM initialize failed");
    auto expected = std::vector<Vector4>(nodeCount);
    auto observed = std::vector<Vector4>(nodeCount);
    auto direct = std::vector<Vector4>(nodeCount);
    auto operations = std::array{Arithmetic::Add, Arithmetic::Multiply,
                                 Arithmetic::Subtract, Arithmetic::Divide};
    for (auto chain = std::size_t{0}; chain < chainCount; ++chain)
    {
        auto base = chain * 7;
        expected[base] = sources[chain * 2];
        expected[base + 1] = sources[chain * 2 + 1];
        for (auto step = std::size_t{0}; step < operations.size(); ++step)
            if (!scalarReference(expected[base + 2 + step], expected[base + (step == 0 ? 0 : step + 1)],
                                 expected[base + 1], width, operations[step]))
                fail("VM independent scalar reference failed");
        expected[base + 6] = expected[base + 5];
    }
    std::cout << "# vm workload=" << workload << ",width=" << width
              << ",instructions_per_update=" << nodeCount << ",updates_per_sample=" << updates
              << ",bytecode_bytes=" << bytes.size() << ",work_bytes=" << program.workBytes()
              << ",unit=ns_per_instruction_including_sources_and_sink\n";
    runMatrixBenchmark("vm.source_arithmetic_sink", workload, nodeCount * updates,
        options.sampleCount, [] {},
        [&] {
            for (auto update = std::size_t{0}; update < updates; ++update)
            {
                if (instance.updateAsync(static_cast<double>(update), enqueueInline, nullptr) !=
                    liberafx::Result::Ok || instance.result() != liberafx::Result::Ok)
                    fail("production VM update failed");
                consumeMemory(storage.values);
            }
        },
        [&] {
            for (auto node = std::size_t{0}; node < nodeCount; ++node)
                for (auto lane = std::size_t{0}; lane < width; ++lane)
                    observed[node].lane[lane] = storage.values[node * 4 + lane];
            return std::pair{vectorChecksum(observed, width),
                             maximumRelativeError(observed, expected, width)};
        }, width);
    // Same live slots and checked math API, but no bytecode decode, general VM
    // routing, scheduler or instance phase management. Not an interpreter speedup.
    runMatrixBenchmark("direct.source_arithmetic_sink", workload, nodeCount * updates,
        options.sampleCount, [] {},
        [&] {
            for (auto update = std::size_t{0}; update < updates; ++update)
            {
                for (auto chain = std::size_t{0}; chain < chainCount; ++chain)
                {
                    auto base = chain * 7;
                    direct[base] = sources[chain * 2];
                    direct[base + 1] = sources[chain * 2 + 1];
                    for (auto step = std::size_t{0}; step < operations.size(); ++step)
                        if (arithmetic(direct[base + 2 + step].lane,
                            direct[base + (step == 0 ? 0 : step + 1)].lane,
                            direct[base + 1].lane, width, operations[step]) != ArithmeticResult::Ok)
                            fail("direct graph arithmetic failed");
                    direct[base + 6] = direct[base + 5];
                }
                consumeMemory(direct.data());
            }
        },
        [&] {
            return std::pair{vectorChecksum(direct, width),
                             maximumRelativeError(direct, expected, width)};
        }, width);
}

auto runMatrixBenchmarks(const Options& options, std::size_t itemCount,
                         const char* workload) -> void
{
    auto inputs = makeInputs(itemCount);
    auto positions = std::vector<Vector3>(itemCount);
    auto rotations = std::vector<Vector3>(itemCount);
    auto scales = std::vector<Vector3>(itemCount);
    auto matrices = std::vector<Matrix4>(itemCount);

    runMatrixBenchmark("matrix.make_transform", workload, options.matrixOperations,
        options.sampleCount,
        [&] {
            for (auto index = std::size_t{0}; index < itemCount; ++index)
            {
                rotations[index] = inputs[index].rotation;
                matrices[index] = inputs[index].matrix;
            }
        },
        [&] {
            for (auto index = std::size_t{0}; index < options.matrixOperations; ++index)
            {
                auto item = index % itemCount;
                rotations[item][2] += 1e-5;
                matrices[item] = makeTransform(inputs[item].position, rotations[item], inputs[item].scale);
            }
        },
        [&] {
            for (const auto& matrix : matrices)
                for (auto value : matrix)
                    if (!std::isfinite(value)) fail("non-finite transform output");
            auto checksum = matrixChecksum(matrices);
            return std::pair{std::abs(checksum), verifyDecomposition(matrices)};
        });

    runMatrixBenchmark("matrix.invert", workload, options.matrixOperations,
        options.sampleCount,
        [&] {
            for (auto index = std::size_t{0}; index < itemCount; ++index)
                matrices[index] = inputs[index].matrix;
        },
        [&] {
            for (auto index = std::size_t{0}; index < options.matrixOperations; ++index)
            {
                auto item = index % itemCount;
                if (!invertMatrix(matrices[item], matrices[item])) fail("matrix inverse failed");
            }
        },
        [&] {
            auto checksum = matrixChecksum(matrices);
            return std::pair{std::abs(checksum), verifyInverse(matrices)};
        });

    runMatrixBenchmark("matrix.decompose_transform", workload,
        options.matrixOperations, options.sampleCount,
        [&] {
            for (auto index = std::size_t{0}; index < itemCount; ++index)
            {
                matrices[index] = inputs[index].matrix;
                positions[index] = {};
                rotations[index] = {};
                scales[index] = {};
            }
        },
        [&] {
            for (auto index = std::size_t{0}; index < options.matrixOperations; ++index)
            {
                auto item = index % itemCount;
                matrices[item][3] += 1e-5;
                if (!decomposeTransform(matrices[item], positions[item], rotations[item], scales[item]))
                    fail("matrix decomposition failed");
            }
        },
        [&] {
            auto reconstructed = std::vector<Matrix4>(itemCount);
            for (auto index = std::size_t{0}; index < itemCount; ++index)
                reconstructed[index] = makeTransform(positions[index], rotations[index], scales[index]);
            return std::pair{std::abs(matrixChecksum(reconstructed)), verifyDecomposition(matrices)};
        });
}

auto multiplyReference(const Matrix4& left, const Matrix4& right) -> Matrix4
{
    auto result = Matrix4{};
    for (auto row = 0u; row < 4; ++row)
        for (auto column = 0u; column < 4; ++column)
            for (auto inner = 0u; inner < 4; ++inner)
                result[row * 4 + column] += left[row * 4 + inner] * right[inner * 4 + column];
    return result;
}

auto orderedReference(const Vector3& position, const Vector3& rotation, const Vector3& scale,
                      std::size_t order) -> Matrix4
{
    auto translation = Matrix4{1, 0, 0, position[0], 0, 1, 0, position[1],
                               0, 0, 1, position[2], 0, 0, 0, 1};
    auto scaling = Matrix4{scale[0], 0, 0, 0, 0, scale[1], 0, 0,
                           0, 0, scale[2], 0, 0, 0, 0, 1};
    auto rotationMatrix = makeTransform({}, rotation, {1, 1, 1});
    auto factors = std::array{translation, rotationMatrix, scaling};
    constexpr auto permutations = std::array<std::array<std::size_t, 3>, 6>{
        {{0, 1, 2}, {0, 2, 1}, {1, 0, 2}, {1, 2, 0}, {2, 0, 1}, {2, 1, 0}}};
    auto permutation = permutations[order];
    return multiplyReference(multiplyReference(factors[permutation[0]], factors[permutation[1]]),
                             factors[permutation[2]]);
}

auto runOrderedBenchmarks(const Options& options, std::size_t itemCount,
                          const char* workload) -> void
{
    auto inputs = makeInputs(itemCount);
    auto rotations = std::vector<Vector3>(itemCount);
    auto matrices = std::vector<Matrix4>(itemCount);
    constexpr auto names = std::array{"matrix.ordered_TRS", "matrix.ordered_TSR",
        "matrix.ordered_RTS", "matrix.ordered_RST", "matrix.ordered_STR", "matrix.ordered_SRT"};
    for (auto order = std::size_t{0}; order < names.size(); ++order)
        runMatrixBenchmark(names[order], workload, options.matrixOperations, options.sampleCount,
            [&] {
                for (auto item = std::size_t{0}; item < itemCount; ++item)
                {
                    rotations[item] = inputs[item].rotation;
                    matrices[item] = orderedReference(inputs[item].position, rotations[item],
                                                       inputs[item].scale, order);
                }
            },
            [&] {
                for (auto index = std::size_t{0}; index < options.matrixOperations; ++index)
                {
                    auto item = index % itemCount;
                    rotations[item][2] += 1e-5;
                    matrices[item] = liberafx::math::makeOrderedTransform(inputs[item].position,
                        rotations[item], inputs[item].scale, static_cast<liberafx::math::TransformOrder>(order));
                }
            },
            [&] {
                auto maximumError = 0.0;
                for (auto item = std::size_t{0}; item < itemCount; ++item)
                {
                    auto expected = orderedReference(inputs[item].position, rotations[item],
                                                       inputs[item].scale, order);
                    for (auto lane = std::size_t{0}; lane < expected.size(); ++lane)
                    {
                        if (!std::isfinite(matrices[item][lane])) fail("non-finite ordered transform");
                        auto error = std::abs(matrices[item][lane] - expected[lane]) /
                                     std::max(1.0, std::abs(expected[lane]));
                        maximumError = std::max(maximumError, error);
                    }
                }
                return std::pair{matrixChecksum(matrices), maximumError};
            });
}

auto architectureName() -> const char*
{
#if defined(_M_ARM64) || defined(__aarch64__) || defined(__arm64__)
    return "arm64";
#elif defined(_M_X64) || defined(__x86_64__)
    return "x86_64";
#elif defined(_M_IX86) || defined(__i386__)
    return "x86";
#else
    return "unknown";
#endif
}

auto backendName() -> const char*
{
#if defined(LIBERAFX_MATH_FORCE_SCALAR)
    return "portable-scalar";
#elif defined(_M_ARM64) || defined(__aarch64__) || defined(__arm64__)
    return "native-neon-when-supported";
#elif defined(__SSE2__) || defined(_M_X64) || (defined(_M_IX86_FP) && _M_IX86_FP >= 2)
    return "native-sse2";
#else
    return "portable-scalar-fallback";
#endif
}

auto compilerName() -> std::string
{
#if defined(__clang__)
    auto name = std::string("Clang ") + __clang_version__;
    while (!name.empty() && std::isspace(static_cast<unsigned char>(name.back())))
        name.pop_back();
    return name;
#elif defined(_MSC_VER)
    return "MSVC";
#elif defined(__GNUC__)
    return __VERSION__;
#else
    return "unknown";
#endif
}
}

auto main(int argc, char** argv) -> int
{
    auto options = parseOptions(argc, argv);
#if defined(LIBERAFX_MATH_BENCHMARK_CONFIGURATION)
    auto configuration = LIBERAFX_MATH_BENCHMARK_CONFIGURATION;
#else
    auto configuration = "unknown";
#endif
    std::cout << "# compiler=" << compilerName() << ",architecture=" << architectureName()
              << ",configuration=" << configuration << ",backend=" << backendName() << '\n';
    std::cout << "# ns/op is median across samples; range columns are min/max; checksum and "
                 "relative_error are validated\n";
    std::cout << "operation,workload,width,operations,samples,median_ns_op,min_ns_op,max_ns_op,"
                 "checksum,relative_error\n";

    auto operations = std::array{Arithmetic::Add, Arithmetic::Subtract,
                                 Arithmetic::Multiply, Arithmetic::Divide};
    for (auto width : {std::size_t{3}, std::size_t{4}})
        for (auto operation : operations)
        {
            runVectorBenchmark(options, width, operation, 64, "small-64");
            runVectorBenchmark(options, width, operation, 4096, "large-4096");
        }
    runMatrixBenchmarks(options, 64, "small-64");
    runMatrixBenchmarks(options, 4096, "large-4096");
    runOrderedBenchmarks(options, 64, "small-64");
    runOrderedBenchmarks(options, 4096, "large-4096");
    for (auto width : {std::size_t{3}, std::size_t{4}})
    {
        runVmBenchmarks(options, 8, width, "small-56-instructions");
        runVmBenchmarks(options, 1024, width, "large-7168-instructions");
    }
    std::cout << "# correctness=passed\n";
    return 0;
}
