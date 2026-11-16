#include "liberafx/VfxRuntime.h"
#include "liberafx/BuiltinSignals.h"
#include "liberafx/math/MatrixMath.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>

namespace liberafx {
namespace {

constexpr auto pi = 3.141592653589793238462643383279502884;
enum class Op {
    Abs, Floor, Round, Sign, Sqrt, Pow, Log, Log10, Sin, Cos, Tan, ATan, ATan2,
    DegToRad, RadToDeg, NormalizeRad, NormalizeDeg, Min, Max, Clamp, Lerp, Mad,
    Pi, TwoPi, Length, Dot, Normalize, Cross, Compose
};

// Domain errors and non-finite inputs/results report NonFinite. Round ties go away
// from zero; angles wrap to [-pi, pi) or [-180, 180). Zero vectors normalize to zero.
enum class MatrixOp { TransformRow, InverseRow, InverseValid, DecomposePosition,
                      DecomposeRotation, DecomposeScale, DecomposeValid };

template<MatrixOp op, unsigned selectedRow = 0>
auto evaluateMatrix(CustomNodeInvocation& call) noexcept -> Result
{
    for (auto lane = 0u; lane < call.outputWidth; ++lane) call.outputs[lane] = 0;
    for (auto input = 0u; input < call.inputCount; ++input)
        for (auto lane = 0u; lane < call.inputWidths[input]; ++lane)
            if (!std::isfinite(call.inputs[input * 4 + lane])) return Result::NonFinite;

    if constexpr (op == MatrixOp::TransformRow)
    {
        auto position = math::Vector3{call.inputs[0], call.inputs[1], call.inputs[2]};
        auto rotation = math::Vector3{call.inputs[4], call.inputs[5], call.inputs[6]};
        auto scale = math::Vector3{call.inputs[8], call.inputs[9], call.inputs[10]};
        auto matrix = math::makeTransform(position, rotation, scale);
        for (auto lane = 0u; lane < 4; ++lane)
        {
            auto value = matrix[selectedRow * 4 + lane];
            if (!std::isfinite(value)) return Result::NonFinite;
            call.outputs[lane] = value;
        }
    }
    else
    {
        auto matrix = math::Matrix4{};
        for (auto row = 0u; row < 4; ++row)
            for (auto column = 0u; column < 4; ++column)
                matrix[row * 4 + column] = call.inputs[row * 4 + column];

        if constexpr (op == MatrixOp::InverseRow || op == MatrixOp::InverseValid)
        {
            auto inverse = math::Matrix4{};
            if (!math::invertMatrix(matrix, inverse)) return Result::Ok;
            if constexpr (op == MatrixOp::InverseValid) call.outputs[0] = 1;
            else
                for (auto lane = 0u; lane < 4; ++lane)
                    call.outputs[lane] = inverse[selectedRow * 4 + lane];
        }
        else
        {
            auto position = math::Vector3{};
            auto rotation = math::Vector3{};
            auto scale = math::Vector3{};
            if (!math::decomposeTransform(matrix, position, rotation, scale)) return Result::Ok;
            if constexpr (op == MatrixOp::DecomposeValid) call.outputs[0] = 1;
            if constexpr (op == MatrixOp::DecomposePosition)
                for (auto lane = 0u; lane < 3; ++lane) call.outputs[lane] = position[lane];
            if constexpr (op == MatrixOp::DecomposeRotation)
                for (auto lane = 0u; lane < 3; ++lane) call.outputs[lane] = rotation[lane];
            if constexpr (op == MatrixOp::DecomposeScale)
                for (auto lane = 0u; lane < 3; ++lane) call.outputs[lane] = scale[lane];
        }
    }
    for (auto lane = 0u; lane < call.outputWidth; ++lane)
        if (!std::isfinite(call.outputs[lane])) return Result::NonFinite;
    return Result::Ok;
}

template<Op op, unsigned width>
auto evaluate(CustomNodeInvocation& call) noexcept -> Result
{
    for (auto input = 0u; input < call.inputCount; ++input)
        for (auto lane = 0u; lane < call.inputWidths[input]; ++lane)
            if (!std::isfinite(call.inputs[input * 4 + lane])) return Result::NonFinite;
    auto a = call.inputs;
    auto b = call.inputs + 4;
    auto c = call.inputs + 8;
    if constexpr (op == Op::Pi || op == Op::TwoPi)
        call.outputs[0] = op == Op::Pi ? pi : 2 * pi;
    else if constexpr (op == Op::Compose)
        for (auto lane = 0u; lane < 4; ++lane) call.outputs[lane] = call.inputs[lane * 4];
    else if constexpr (op == Op::Length || op == Op::Normalize)
    {
        auto scale = 0.0;
        for (auto lane = 0u; lane < width; ++lane) scale = std::max(scale, std::abs(a[lane]));
        auto sum = 0.0;
        if (scale != 0)
            for (auto lane = 0u; lane < width; ++lane) sum += (a[lane] / scale) * (a[lane] / scale);
        auto factor = std::sqrt(sum);
        if constexpr (op == Op::Length) call.outputs[0] = scale * factor;
        else
            for (auto lane = 0u; lane < width; ++lane)
                call.outputs[lane] = scale == 0 ? 0 : (a[lane] / scale) / factor;
    }
    else if constexpr (op == Op::Dot)
    {
        auto sum = 0.0;
        for (auto lane = 0u; lane < width; ++lane) sum += a[lane] * b[lane];
        call.outputs[0] = sum;
    }
    else if constexpr (op == Op::Cross)
    {
        call.outputs[0] = a[1] * b[2] - a[2] * b[1];
        call.outputs[1] = a[2] * b[0] - a[0] * b[2];
        call.outputs[2] = a[0] * b[1] - a[1] * b[0];
    }
    else
    {
        for (auto lane = 0u; lane < width; ++lane)
        {
            auto x = a[lane];
            auto y = b[lane];
            auto z = c[lane];
            auto value = 0.0;
            if constexpr (op == Op::Abs) value = std::abs(x);
            if constexpr (op == Op::Floor)
            {
                if (y == 0) return Result::DivideByZero;
                value = std::floor(x / y) * y;
            }
            if constexpr (op == Op::Round) value = std::round(x);
            if constexpr (op == Op::Sign) value = (x > 0) - (x < 0);
            if constexpr (op == Op::Sqrt) value = std::sqrt(x);
            if constexpr (op == Op::Pow) value = std::pow(x, y);
            if constexpr (op == Op::Log) value = std::log(x);
            if constexpr (op == Op::Log10) value = std::log10(x);
            if constexpr (op == Op::Sin) value = std::sin(x);
            if constexpr (op == Op::Cos) value = std::cos(x);
            if constexpr (op == Op::Tan) value = std::tan(x);
            if constexpr (op == Op::ATan) value = std::atan(x);
            if constexpr (op == Op::ATan2) value = std::atan2(x, y);
            if constexpr (op == Op::DegToRad) value = x * (pi / 180);
            if constexpr (op == Op::RadToDeg) value = x * (180 / pi);
            if constexpr (op == Op::NormalizeRad || op == Op::NormalizeDeg)
            {
                auto half = op == Op::NormalizeRad ? pi : 180.0;
                value = std::fmod(x, 2 * half);
                if (value >= half) value -= 2 * half;
                if (value < -half) value += 2 * half;
            }
            if constexpr (op == Op::Min) value = std::min(x, y);
            if constexpr (op == Op::Max) value = std::max(x, y);
            if constexpr (op == Op::Clamp)
            {
                if (y > z) return Result::InvalidArgument;
                value = std::max(y, std::min(x, z));
            }
            if constexpr (op == Op::Lerp) value = x * (1 - c[0]) + y * c[0];
            if constexpr (op == Op::Mad) value = x * y + z;
            call.outputs[lane] = value;
        }
    }
    for (auto lane = 0u; lane < call.outputWidth; ++lane)
        if (!std::isfinite(call.outputs[lane])) return Result::NonFinite;
    return Result::Ok;
}

template<Op op, unsigned width>
auto makeDescriptor(unsigned id, unsigned inputCount, unsigned outputWidth,
                    unsigned secondWidth = width, unsigned thirdWidth = width) noexcept -> CustomNodeDescriptor
{
    auto descriptor = CustomNodeDescriptor{};
    const std::uint8_t prefix[] = {0x74, 0x30, 0x5a, 0xc1, 0x91, 0xb2, 0x4c, 0x33, 0x8d, 0x44};
    for (auto index = 0u; index < 10; ++index) descriptor.typeId[index] = prefix[index];
    descriptor.typeId[15] = static_cast<std::uint8_t>(id);
    descriptor.abiVersion = 1;
    descriptor.inputCount = static_cast<std::uint8_t>(inputCount);
    descriptor.inputWidths[0] = static_cast<std::uint8_t>(width);
    descriptor.inputWidths[1] = static_cast<std::uint8_t>(secondWidth);
    descriptor.inputWidths[2] = static_cast<std::uint8_t>(thirdWidth);
    descriptor.inputWidths[3] = static_cast<std::uint8_t>(width);
    descriptor.outputWidth = static_cast<std::uint8_t>(outputWidth);
    descriptor.function = evaluate<op, width>;
    return descriptor;
}

template<MatrixOp op, unsigned outputWidth, unsigned selectedRow = 0>
auto makeMatrixDescriptor(unsigned id, unsigned inputCount, unsigned inputWidth) noexcept -> CustomNodeDescriptor
{
    auto descriptor = CustomNodeDescriptor{};
    const std::uint8_t prefix[] = {0xb0, 0xaf, 0x61, 0xd2, 0x29, 0xf6, 0x4c, 0xc9, 0xa7, 0x1e};
    for (auto index = 0u; index < 10; ++index) descriptor.typeId[index] = prefix[index];
    descriptor.typeId[15] = static_cast<std::uint8_t>(id);
    descriptor.abiVersion = 1;
    descriptor.execution = CustomExecution::Parameter;
    descriptor.inputCount = static_cast<std::uint8_t>(inputCount);
    for (auto input = 0u; input < inputCount; ++input)
        descriptor.inputWidths[input] = static_cast<std::uint8_t>(inputWidth);
    descriptor.outputWidth = static_cast<std::uint8_t>(outputWidth);
    descriptor.function = evaluateMatrix<op, selectedRow>;
    return descriptor;
}

auto makeSignalPassthroughDescriptor() noexcept -> CustomNodeDescriptor
{
    auto descriptor = CustomNodeDescriptor{};
    std::copy(signalPassthroughTypeId.begin(), signalPassthroughTypeId.end(), descriptor.typeId);
    descriptor.abiVersion = 1;
    descriptor.execution = CustomExecution::Signal;
    descriptor.branchCount = 1;
    descriptor.function = signalPassthrough;
    return descriptor;
}

auto makeGameIoOutputSignalDescriptor() noexcept -> CustomNodeDescriptor
{
    auto descriptor = CustomNodeDescriptor{};
    const auto typeId = std::array<std::uint8_t, 16>{
        0x7f, 0xf0, 0x78, 0xd2, 0xdc, 0xe1, 0x42, 0xb0,
        0x93, 0xfb, 0xb4, 0x02, 0x00, 0x00, 0x00, 0x01};
    std::copy(typeId.begin(), typeId.end(), descriptor.typeId);
    descriptor.abiVersion = 1;
    descriptor.execution = CustomExecution::Signal;
    descriptor.branchCount = 1;
    descriptor.function = signalPassthrough;
    return descriptor;
}

const std::array<CustomNodeDescriptor, 62> descriptors = {
    makeDescriptor<Op::Abs, 1>(1, 1, 1), makeDescriptor<Op::Floor, 1>(2, 2, 1),
    makeDescriptor<Op::Round, 1>(3, 1, 1), makeDescriptor<Op::Sign, 1>(4, 1, 1),
    makeDescriptor<Op::Sqrt, 1>(5, 1, 1), makeDescriptor<Op::Pow, 1>(6, 2, 1),
    makeDescriptor<Op::Log, 1>(7, 1, 1), makeDescriptor<Op::Log10, 1>(8, 1, 1),
    makeDescriptor<Op::Sin, 1>(9, 1, 1), makeDescriptor<Op::Cos, 1>(10, 1, 1),
    makeDescriptor<Op::Tan, 1>(11, 1, 1), makeDescriptor<Op::ATan, 1>(12, 1, 1),
    makeDescriptor<Op::ATan2, 1>(13, 2, 1), makeDescriptor<Op::DegToRad, 1>(14, 1, 1),
    makeDescriptor<Op::RadToDeg, 1>(15, 1, 1), makeDescriptor<Op::NormalizeRad, 1>(16, 1, 1),
    makeDescriptor<Op::NormalizeDeg, 1>(17, 1, 1), makeDescriptor<Op::Min, 1>(18, 2, 1),
    makeDescriptor<Op::Max, 1>(19, 2, 1), makeDescriptor<Op::Clamp, 1>(20, 3, 1),
    makeDescriptor<Op::Lerp, 1>(21, 3, 1, 1, 1), makeDescriptor<Op::Mad, 1>(22, 3, 1),
    makeDescriptor<Op::Pi, 1>(23, 0, 1), makeDescriptor<Op::TwoPi, 1>(24, 0, 1),
    makeDescriptor<Op::Length, 2>(25, 1, 1), makeDescriptor<Op::Dot, 2>(26, 2, 1),
    makeDescriptor<Op::Normalize, 2>(27, 1, 2),
    makeDescriptor<Op::Length, 3>(28, 1, 1), makeDescriptor<Op::Dot, 3>(29, 2, 1),
    makeDescriptor<Op::Normalize, 3>(30, 1, 3),
    makeDescriptor<Op::Length, 4>(31, 1, 1), makeDescriptor<Op::Dot, 4>(32, 2, 1),
    makeDescriptor<Op::Normalize, 4>(33, 1, 4), makeDescriptor<Op::Cross, 3>(34, 2, 3),
    makeDescriptor<Op::Abs, 2>(35, 1, 2), makeDescriptor<Op::Min, 2>(36, 2, 2),
    makeDescriptor<Op::Max, 2>(37, 2, 2), makeDescriptor<Op::Lerp, 2>(38, 3, 2, 2, 1),
    makeDescriptor<Op::Abs, 3>(39, 1, 3), makeDescriptor<Op::Min, 3>(40, 2, 3),
    makeDescriptor<Op::Max, 3>(41, 2, 3), makeDescriptor<Op::Lerp, 3>(42, 3, 3, 3, 1),
    makeDescriptor<Op::Abs, 4>(43, 1, 4), makeDescriptor<Op::Min, 4>(44, 2, 4),
    makeDescriptor<Op::Max, 4>(45, 2, 4), makeDescriptor<Op::Lerp, 4>(46, 3, 4, 4, 1),
    makeDescriptor<Op::Compose, 1>(47, 4, 4),
    makeMatrixDescriptor<MatrixOp::TransformRow, 4, 0>(1, 3, 3),
    makeMatrixDescriptor<MatrixOp::TransformRow, 4, 1>(2, 3, 3),
    makeMatrixDescriptor<MatrixOp::TransformRow, 4, 2>(3, 3, 3),
    makeMatrixDescriptor<MatrixOp::TransformRow, 4, 3>(4, 3, 3),
    makeMatrixDescriptor<MatrixOp::InverseRow, 4, 0>(5, 4, 4),
    makeMatrixDescriptor<MatrixOp::InverseRow, 4, 1>(6, 4, 4),
    makeMatrixDescriptor<MatrixOp::InverseRow, 4, 2>(7, 4, 4),
    makeMatrixDescriptor<MatrixOp::InverseRow, 4, 3>(8, 4, 4),
    makeMatrixDescriptor<MatrixOp::InverseValid, 1>(9, 4, 4),
    makeMatrixDescriptor<MatrixOp::DecomposePosition, 3>(10, 4, 4),
    makeMatrixDescriptor<MatrixOp::DecomposeRotation, 3>(11, 4, 4),
    makeMatrixDescriptor<MatrixOp::DecomposeScale, 3>(12, 4, 4),
    makeMatrixDescriptor<MatrixOp::DecomposeValid, 1>(13, 4, 4),
    makeSignalPassthroughDescriptor(), makeGameIoOutputSignalDescriptor()
};
}

auto builtinNodes(std::size_t& count) noexcept -> const CustomNodeDescriptor*
{
    count = descriptors.size();
    return descriptors.data();
}
}

namespace liberafx::math {
namespace {
constexpr auto pi = 3.141592653589793238462643383279502884;
constexpr auto radiansPerDegree = pi / 180.0;
constexpr auto degreesPerRadian = 180.0 / pi;
constexpr auto relativePivotTolerance = 1e-12;
constexpr auto affineTolerance = 1e-10;

template<std::size_t width>
auto invertSquare(const std::array<double, width * width>& input,
                  std::array<double, width * width>& output) noexcept -> bool
{
    output.fill(0);
    auto inputScale = 0.0;
    for (auto value : input)
    {
        if (!std::isfinite(value)) return false;
        inputScale = std::max(inputScale, std::abs(value));
    }
    if (inputScale == 0) return false;

    auto augmented = std::array<double, width * width * 2>{};
    for (auto row = 0u; row < width; ++row)
    {
        for (auto column = 0u; column < width; ++column)
            augmented[row * (2 * width) + column] = input[row * width + column] / inputScale;
        augmented[row * (2 * width) + width + row] = 1;
    }
    for (auto column = 0u; column < width; ++column)
    {
        auto pivotRow = column;
        for (auto row = column + 1; row < width; ++row)
            if (std::abs(augmented[row * (2 * width) + column]) >
                std::abs(augmented[pivotRow * (2 * width) + column])) pivotRow = row;
        auto pivot = augmented[pivotRow * (2 * width) + column];
        if (!std::isfinite(pivot) || std::abs(pivot) <= relativePivotTolerance) return false;
        if (pivotRow != column)
            for (auto entry = 0u; entry < 2 * width; ++entry)
                std::swap(augmented[column * (2 * width) + entry],
                          augmented[pivotRow * (2 * width) + entry]);

        auto rowOffset = column * (2 * width);
        pivot = augmented[rowOffset + column];
        for (auto entry = 0u; entry < 2 * width; ++entry)
            augmented[rowOffset + entry] /= pivot;
        for (auto row = 0u; row < width; ++row)
        {
            if (row == column) continue;
            rowOffset = row * (2 * width);
            auto factor = augmented[rowOffset + column];
            for (auto entry = 0u; entry < 2 * width; ++entry)
                augmented[rowOffset + entry] -= factor * augmented[column * (2 * width) + entry];
        }
    }
    for (auto row = 0u; row < width; ++row)
        for (auto column = 0u; column < width; ++column)
        {
            auto value = augmented[row * (2 * width) + width + column] / inputScale;
            if (!std::isfinite(value)) { output.fill(0); return false; }
            output[row * width + column] = value;
        }
    return true;
}

auto finite(const Matrix4& matrix) noexcept -> bool
{
    for (auto value : matrix) if (!std::isfinite(value)) return false;
    return true;
}

auto normalizedDegrees(double angle) noexcept -> double
{
    angle = std::fmod(angle, 360.0);
    if (angle >= 180.0) angle -= 360.0;
    if (angle < -180.0) angle += 360.0;
    return angle;
}
}

auto makeTransform(const Vector3& position, const Vector3& rotationDegrees,
                  const Vector3& scale) noexcept -> Matrix4
{
    auto roll = rotationDegrees[0] * radiansPerDegree;
    auto pitch = rotationDegrees[1] * radiansPerDegree;
    auto yaw = rotationDegrees[2] * radiansPerDegree;
    auto cr = std::cos(roll), sr = std::sin(roll);
    auto cp = std::cos(pitch), sp = std::sin(pitch);
    auto cy = std::cos(yaw), sy = std::sin(yaw);

    return Matrix4{
        cy * cp * scale[0], (cy * sp * sr - sy * cr) * scale[1],
            (-cy * sp * cr - sy * sr) * scale[2], position[0],
        sy * cp * scale[0], (sy * sp * sr + cy * cr) * scale[1],
            (-sy * sp * cr + cy * sr) * scale[2], position[1],
        sp * scale[0], -cp * sr * scale[1], cp * cr * scale[2], position[2],
        0, 0, 0, 1
    };
}

auto invertMatrix(const Matrix4& matrix, Matrix4& inverse) noexcept -> bool
{
    auto source = matrix;
    inverse.fill(0);
    if (!finite(source)) return false;

    // Keep translation out of the linear pivot scale for affine matrices.
    if (source[12] == 0 && source[13] == 0 && source[14] == 0 && source[15] == 1)
    {
        auto linear = std::array<double, 9>{};
        for (auto row = 0u; row < 3; ++row)
            for (auto column = 0u; column < 3; ++column)
                linear[row * 3 + column] = source[row * 4 + column];
        auto linearInverse = std::array<double, 9>{};
        if (!invertSquare<3>(linear, linearInverse)) return false;
        for (auto row = 0u; row < 3; ++row)
        {
            for (auto column = 0u; column < 3; ++column)
                inverse[row * 4 + column] = linearInverse[row * 3 + column];
            auto translated = 0.0;
            for (auto column = 0u; column < 3; ++column)
                translated -= linearInverse[row * 3 + column] * source[column * 4 + 3];
            if (!std::isfinite(translated)) { inverse.fill(0); return false; }
            inverse[row * 4 + 3] = translated;
        }
        inverse[15] = 1;
        return true;
    }

    auto result = std::array<double, 16>{};
    if (!invertSquare<4>(source, result)) return false;
    for (auto index = 0u; index < 16; ++index) inverse[index] = result[index];
    return true;
}

auto decomposeTransform(const Matrix4& matrix, Vector3& position, Vector3& rotationDegrees,
                        Vector3& scale) noexcept -> bool
{
    position.fill(0);
    rotationDegrees.fill(0);
    scale.fill(0);
    if (!finite(matrix) || std::abs(matrix[12]) > affineTolerance ||
        std::abs(matrix[13]) > affineTolerance || std::abs(matrix[14]) > affineTolerance ||
        std::abs(matrix[15] - 1) > affineTolerance) return false;

    auto maximumScale = 0.0;
    auto magnitudes = Vector3{};
    for (auto column = 0u; column < 3; ++column)
    {
        auto componentScale = 0.0;
        for (auto row = 0u; row < 3; ++row)
            componentScale = std::max(componentScale, std::abs(matrix[row * 4 + column]));
        if (componentScale == 0) return false;
        auto sum = 0.0;
        for (auto row = 0u; row < 3; ++row)
        {
            auto normalized = matrix[row * 4 + column] / componentScale;
            sum += normalized * normalized;
        }
        magnitudes[column] = componentScale * std::sqrt(sum);
        if (!std::isfinite(magnitudes[column])) return false;
        maximumScale = std::max(maximumScale, magnitudes[column]);
    }
    if (maximumScale == 0) return false;
    for (auto column = 0u; column < 3; ++column)
        if (magnitudes[column] / maximumScale <= relativePivotTolerance) return false;

    auto axes = std::array<double, 9>{};
    for (auto column = 0u; column < 3; ++column)
        for (auto row = 0u; row < 3; ++row)
            axes[row * 3 + column] = matrix[row * 4 + column] / magnitudes[column];
    auto dot = [&](unsigned first, unsigned second) noexcept -> double {
        auto value = 0.0;
        for (auto row = 0u; row < 3; ++row)
            value += axes[row * 3 + first] * axes[row * 3 + second];
        return value;
    };
    if (std::abs(dot(0, 1)) > affineTolerance || std::abs(dot(0, 2)) > affineTolerance ||
        std::abs(dot(1, 2)) > affineTolerance) return false;

    auto determinant =
        axes[0] * (axes[4] * axes[8] - axes[5] * axes[7]) -
        axes[1] * (axes[3] * axes[8] - axes[5] * axes[6]) +
        axes[2] * (axes[3] * axes[7] - axes[4] * axes[6]);
    if (!std::isfinite(determinant) || std::abs(std::abs(determinant) - 1) > 4 * affineTolerance)
        return false;
    if (determinant < 0)
    {
        magnitudes[0] = -magnitudes[0];
        for (auto row = 0u; row < 3; ++row) axes[row * 3] = -axes[row * 3];
    }

    auto sinPitch = std::max(-1.0, std::min(1.0, axes[6]));
    auto pitch = std::asin(sinPitch);
    auto cosPitch = std::hypot(axes[0], axes[3]);
    auto roll = 0.0;
    auto yaw = 0.0;
    if (cosPitch <= affineTolerance)
    {
        pitch = std::copysign(pi / 2, sinPitch);
        yaw = std::atan2(-axes[1], axes[4]);
    }
    else
    {
        roll = std::atan2(-axes[7], axes[8]);
        yaw = std::atan2(axes[3], axes[0]);
    }
    position = {matrix[3], matrix[7], matrix[11]};
    rotationDegrees = {normalizedDegrees(roll * degreesPerRadian),
                       pitch * degreesPerRadian, normalizedDegrees(yaw * degreesPerRadian)};
    scale = magnitudes;

    auto reconstructed = makeTransform(position, rotationDegrees, scale);
    for (auto row = 0u; row < 3; ++row)
        for (auto column = 0u; column < 4; ++column)
        {
            auto expected = matrix[row * 4 + column];
            auto actual = reconstructed[row * 4 + column];
            if (!std::isfinite(actual) ||
                std::abs(actual - expected) > affineTolerance * std::max(1.0, std::abs(expected)))
            {
                position.fill(0); rotationDegrees.fill(0); scale.fill(0);
                return false;
            }
        }
    return true;
}
}
