#include "liberafx/math/LbMatrixMath.h"
#include <algorithm>
#include <array>
#include <cmath>

namespace liberafx::math
{
namespace matrix_math_detail
{
constexpr auto pi = 3.141592653589793238462643383279502884;
constexpr auto radiansPerDegree = pi / 180.0;
constexpr auto degreesPerRadian = 180.0 / pi;
constexpr auto relativePivotTolerance = 1e-12;
constexpr auto affineTolerance = 1e-10;

template <std::size_t width>
auto invertSquare(const std::array<double, width * width>& input, std::array<double, width * width>& output) noexcept
    -> bool
{
    output.fill(0);
    auto inputScale = 0.0;
    for (auto value : input)
    {
        if (!std::isfinite(value))
        {
            return false;
        }
        inputScale = std::max(inputScale, std::abs(value));
    }
    if (inputScale == 0)
    {
        return false;
    }

    auto augmented = std::array<double, width * width * 2>{};
    for (auto row = 0u; row < width; ++row)
    {
        for (auto column = 0u; column < width; ++column)
        {
            augmented[row * (2 * width) + column] = input[row * width + column] / inputScale;
        }
        augmented[row * (2 * width) + width + row] = 1;
    }
    for (auto column = 0u; column < width; ++column)
    {
        auto pivotRow = column;
        for (auto row = column + 1; row < width; ++row)
        {
            if (std::abs(augmented[row * (2 * width) + column]) > std::abs(augmented[pivotRow * (2 * width) + column]))
            {
                pivotRow = row;
            }
        }
        auto pivot = augmented[pivotRow * (2 * width) + column];
        if (!std::isfinite(pivot) || std::abs(pivot) <= relativePivotTolerance)
        {
            return false;
        }
        if (pivotRow != column)
        {
            for (auto entry = 0u; entry < 2 * width; ++entry)
            {
                std::swap(augmented[column * (2 * width) + entry], augmented[pivotRow * (2 * width) + entry]);
            }
        }

        auto rowOffset = column * (2 * width);
        pivot = augmented[rowOffset + column];
        for (auto entry = 0u; entry < 2 * width; ++entry)
        {
            augmented[rowOffset + entry] /= pivot;
        }
        for (auto row = 0u; row < width; ++row)
        {
            if (row == column)
            {
                continue;
            }
            rowOffset = row * (2 * width);
            auto factor = augmented[rowOffset + column];
            for (auto entry = 0u; entry < 2 * width; ++entry)
            {
                augmented[rowOffset + entry] -= factor * augmented[column * (2 * width) + entry];
            }
        }
    }
    for (auto row = 0u; row < width; ++row)
    {
        for (auto column = 0u; column < width; ++column)
        {
            auto value = augmented[row * (2 * width) + width + column] / inputScale;
            if (!std::isfinite(value))
            {
                output.fill(0);
                return false;
            }
            output[row * width + column] = value;
        }
    }
    return true;
}

auto finite(const LbMatrix& matrix) noexcept -> bool
{
    for (auto value : matrix)
    {
        if (!std::isfinite(value))
        {
            return false;
        }
    }
    return true;
}

auto normalizedDegrees(double angle) noexcept -> double
{
    angle = std::fmod(angle, 360.0);
    if (angle >= 180.0)
    {
        angle -= 360.0;
    }
    if (angle < -180.0)
    {
        angle += 360.0;
    }
    return angle;
}
} // namespace matrix_math_detail
using matrix_math_detail::affineTolerance;
using matrix_math_detail::degreesPerRadian;
using matrix_math_detail::finite;
using matrix_math_detail::invertSquare;
using matrix_math_detail::normalizedDegrees;
using matrix_math_detail::pi;
using matrix_math_detail::radiansPerDegree;
using matrix_math_detail::relativePivotTolerance;

auto makeTransform(const LbVector& position, const LbVector& rotationDegrees, const LbVector& scale) noexcept
    -> LbMatrix
{
    auto roll = rotationDegrees[0] * radiansPerDegree;
    auto pitch = rotationDegrees[1] * radiansPerDegree;
    auto yaw = rotationDegrees[2] * radiansPerDegree;
    auto cr = std::cos(roll), sr = std::sin(roll);
    auto cp = std::cos(pitch), sp = std::sin(pitch);
    auto cy = std::cos(yaw), sy = std::sin(yaw);

    return LbMatrix{cy * cp * scale[0],
                    (cy * sp * sr - sy * cr) * scale[1],
                    (-cy * sp * cr - sy * sr) * scale[2],
                    position[0],
                    sy * cp * scale[0],
                    (sy * sp * sr + cy * cr) * scale[1],
                    (-sy * sp * cr + cy * sr) * scale[2],
                    position[1],
                    sp * scale[0],
                    -cp * sr * scale[1],
                    cp * cr * scale[2],
                    position[2],
                    0,
                    0,
                    0,
                    1};
}

auto makeOrderedTransform(const LbVector& position, const LbVector& rotationDegrees, const LbVector& scale,
                          TransformOrder order) noexcept -> LbMatrix
{
    if (order == TransformOrder::TRS)
    {
        return makeTransform(position, rotationDegrees, scale);
    }
    auto rotation = makeTransform({}, rotationDegrees, {1, 1, 1});
    auto result = rotation;
    auto leftScale = order == TransformOrder::TSR || order == TransformOrder::STR || order == TransformOrder::SRT;
    if (static_cast<unsigned>(order) > 5)
    {
        return {};
    }
    for (auto row = 0u; row < 3; ++row)
    {
        for (auto column = 0u; column < 3; ++column)
        {
            result[row * 4 + column] *= scale[leftScale ? row : column];
        }
    }
    auto translated = position;
    if (order == TransformOrder::STR)
    {
        for (auto lane = 0u; lane < 3; ++lane)
        {
            translated[lane] *= scale[lane];
        }
    }
    else if (order == TransformOrder::RTS || order == TransformOrder::RST || order == TransformOrder::SRT)
    {
        translated.fill(0);
        for (auto row = 0u; row < 3; ++row)
        {
            for (auto column = 0u; column < 3; ++column)
            {
                translated[row] +=
                    rotation[row * 4 + column] * position[column] * (order == TransformOrder::RST ? scale[column] : 1);
            }
        }
        if (order == TransformOrder::SRT)
        {
            for (auto lane = 0u; lane < 3; ++lane)
            {
                translated[lane] *= scale[lane];
            }
        }
    }
    for (auto lane = 0u; lane < 3; ++lane)
    {
        result[lane * 4 + 3] = translated[lane];
    }
    return result;
}

auto invertMatrix(const LbMatrix& matrix, LbMatrix& inverse) noexcept -> bool
{
    auto source = matrix;
    inverse.fill(0);
    if (!finite(source))
    {
        return false;
    }

    // Keep translation out of the linear pivot scale for affine matrices.
    if (source[12] == 0 && source[13] == 0 && source[14] == 0 && source[15] == 1)
    {
        auto linear = std::array<double, 9>{};
        for (auto row = 0u; row < 3; ++row)
        {
            for (auto column = 0u; column < 3; ++column)
            {
                linear[row * 3 + column] = source[row * 4 + column];
            }
        }
        auto linearInverse = std::array<double, 9>{};
        if (!invertSquare<3>(linear, linearInverse))
        {
            return false;
        }
        for (auto row = 0u; row < 3; ++row)
        {
            for (auto column = 0u; column < 3; ++column)
            {
                inverse[row * 4 + column] = linearInverse[row * 3 + column];
            }
            auto translated = 0.0;
            for (auto column = 0u; column < 3; ++column)
            {
                translated -= linearInverse[row * 3 + column] * source[column * 4 + 3];
            }
            if (!std::isfinite(translated))
            {
                inverse.fill(0);
                return false;
            }
            inverse[row * 4 + 3] = translated;
        }
        inverse[15] = 1;
        return true;
    }

    auto result = std::array<double, 16>{};
    if (!invertSquare<4>(source, result))
    {
        return false;
    }
    for (auto index = 0u; index < 16; ++index)
    {
        inverse[index] = result[index];
    }
    return true;
}

auto decomposeTransform(const LbMatrix& matrix, LbVector& position, LbVector& rotationDegrees, LbVector& scale) noexcept
    -> bool
{
    position.fill(0);
    rotationDegrees.fill(0);
    scale.fill(0);
    if (!finite(matrix) || std::abs(matrix[12]) > affineTolerance || std::abs(matrix[13]) > affineTolerance ||
        std::abs(matrix[14]) > affineTolerance || std::abs(matrix[15] - 1) > affineTolerance)
    {
        return false;
    }

    auto maximumScale = 0.0;
    auto magnitudes = LbVector{};
    for (auto column = 0u; column < 3; ++column)
    {
        auto componentScale = 0.0;
        for (auto row = 0u; row < 3; ++row)
        {
            componentScale = std::max(componentScale, std::abs(matrix[row * 4 + column]));
        }
        if (componentScale == 0)
        {
            return false;
        }
        auto sum = 0.0;
        for (auto row = 0u; row < 3; ++row)
        {
            auto normalized = matrix[row * 4 + column] / componentScale;
            sum += normalized * normalized;
        }
        magnitudes[column] = componentScale * std::sqrt(sum);
        if (!std::isfinite(magnitudes[column]))
        {
            return false;
        }
        maximumScale = std::max(maximumScale, magnitudes[column]);
    }
    if (maximumScale == 0)
    {
        return false;
    }
    for (auto column = 0u; column < 3; ++column)
    {
        if (magnitudes[column] / maximumScale <= relativePivotTolerance)
        {
            return false;
        }
    }

    auto axes = std::array<double, 9>{};
    for (auto column = 0u; column < 3; ++column)
    {
        for (auto row = 0u; row < 3; ++row)
        {
            axes[row * 3 + column] = matrix[row * 4 + column] / magnitudes[column];
        }
    }
    auto dot = [&](unsigned first, unsigned second) noexcept -> double
    {
        auto value = 0.0;
        for (auto row = 0u; row < 3; ++row)
        {
            value += axes[row * 3 + first] * axes[row * 3 + second];
        }
        return value;
    };
    if (std::abs(dot(0, 1)) > affineTolerance || std::abs(dot(0, 2)) > affineTolerance ||
        std::abs(dot(1, 2)) > affineTolerance)
    {
        return false;
    }

    auto determinant = axes[0] * (axes[4] * axes[8] - axes[5] * axes[7]) -
                       axes[1] * (axes[3] * axes[8] - axes[5] * axes[6]) +
                       axes[2] * (axes[3] * axes[7] - axes[4] * axes[6]);
    if (!std::isfinite(determinant) || std::abs(std::abs(determinant) - 1) > 4 * affineTolerance)
    {
        return false;
    }
    if (determinant < 0)
    {
        magnitudes[0] = -magnitudes[0];
        for (auto row = 0u; row < 3; ++row)
        {
            axes[row * 3] = -axes[row * 3];
        }
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
    rotationDegrees = {normalizedDegrees(roll * degreesPerRadian), pitch * degreesPerRadian,
                       normalizedDegrees(yaw * degreesPerRadian)};
    scale = magnitudes;

    auto reconstructed = makeTransform(position, rotationDegrees, scale);
    for (auto row = 0u; row < 3; ++row)
    {
        for (auto column = 0u; column < 4; ++column)
        {
            auto expected = matrix[row * 4 + column];
            auto actual = reconstructed[row * 4 + column];
            if (!std::isfinite(actual) ||
                std::abs(actual - expected) > affineTolerance * std::max(1.0, std::abs(expected)))
            {
                position.fill(0);
                rotationDegrees.fill(0);
                scale.fill(0);
                return false;
            }
        }
    }
    return true;
}
} // namespace liberafx::math
