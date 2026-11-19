#pragma once

#include "LbMatrixMath.h"
#include <array>

namespace liberafx::math
{
// XYZW Hamilton quaternion. Rotation APIs normalize inputs and reject zero.
using LbQuaternion = std::array<double, 4>;
auto normalizeQuaternion(const LbQuaternion& value, LbQuaternion& result) noexcept -> bool;
auto conjugateQuaternion(const LbQuaternion& value) noexcept -> LbQuaternion;
auto inverseQuaternion(const LbQuaternion& value, LbQuaternion& result) noexcept -> bool;
// a*b applies b first, then a, to column vectors.
auto multiplyQuaternion(const LbQuaternion& a, const LbQuaternion& b) noexcept -> LbQuaternion;
// Matches makeTransform: Rz(yaw) * Ry(-pitch) * Rx(-roll), degrees.
auto quaternionFromEuler(const LbVector& degrees) noexcept -> LbQuaternion;
auto quaternionToEuler(const LbQuaternion& value, LbVector& degrees) noexcept -> bool;
// Standard right-handed positive angle about the supplied axis, in degrees.
auto quaternionFromAxisAngle(const LbVector& axis, double degrees, LbQuaternion& result) noexcept -> bool;
auto slerpQuaternion(const LbQuaternion& a, const LbQuaternion& b, double t, LbQuaternion& result) noexcept -> bool;
auto rotateByQuaternion(const LbQuaternion& value, const LbVector& vector, LbVector& result) noexcept -> bool;
}
