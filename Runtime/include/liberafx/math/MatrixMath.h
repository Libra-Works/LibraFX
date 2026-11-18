#pragma once

#include <array>
#include <cstdint>

namespace liberafx::math {

// Matrices are row-major; vectors are column vectors and transforms compose T * R * S.
using LbMatrix = std::array<double, 16>;
using LbVector = std::array<double, 3>;
enum class TransformOrder : std::uint8_t { TRS = 0, TSR = 1, RTS = 2, RST = 3, STR = 4, SRT = 5 };

// rotationDegrees is (RollX, PitchY, YawZ). The convention matches the Unreal renderer:
// Rz(yaw) * Ry(-pitch) * Rx(-roll).
auto makeTransform(const LbVector& position, const LbVector& rotationDegrees,
                  const LbVector& scale) noexcept -> LbMatrix;

auto makeOrderedTransform(const LbVector& position, const LbVector& rotationDegrees,
                          const LbVector& scale, TransformOrder order) noexcept -> LbMatrix;

// Returns false for singular, relatively near-singular, non-finite, or unrepresentable
// results. On failure, inverse is all zeroes.
auto invertMatrix(const LbMatrix& matrix, LbMatrix& inverse) noexcept -> bool;

// Decomposes affine TRS matrices only. On failure, all output vectors are zeroed.
auto decomposeTransform(const LbMatrix& matrix, LbVector& position, LbVector& rotationDegrees,
                        LbVector& scale) noexcept -> bool;

}
