#include "liberafx/math/LbQuaternionMath.h"
#include <algorithm>
#include <cmath>

namespace liberafx::math
{
auto normalizeQuaternion(const LbQuaternion& value, LbQuaternion& result) noexcept -> bool
{
    auto input = value;
    result = {};
    auto scale = 0.0;
    for (auto component : input)
    {
        if (!std::isfinite(component))
        {
            return false;
        }
        scale = std::max(scale, std::abs(component));
    }
    if (scale == 0)
    {
        return false;
    }
    auto lengthSquared = 0.0;
    for (auto component : input)
    {
        auto scaled = component / scale;
        lengthSquared += scaled * scaled;
    }
    auto length = std::sqrt(lengthSquared);
    for (auto lane = 0u; lane < 4; ++lane)
    {
        result[lane] = (input[lane] / scale) / length;
    }
    return true;
}

auto conjugateQuaternion(const LbQuaternion& value) noexcept -> LbQuaternion
{
    return {-value[0], -value[1], -value[2], value[3]};
}

auto inverseQuaternion(const LbQuaternion& value, LbQuaternion& result) noexcept -> bool
{
    auto input = value;
    result = {};
    auto unit = LbQuaternion{};
    if (!normalizeQuaternion(input, unit))
    {
        return false;
    }
    auto scale = 0.0;
    for (auto component : input)
    {
        scale = std::max(scale, std::abs(component));
    }
    auto scaled = LbQuaternion{};
    auto lengthSquared = 0.0;
    for (auto lane = 0u; lane < 4; ++lane)
    {
        scaled[lane] = input[lane] / scale;
        lengthSquared += scaled[lane] * scaled[lane];
    }
    auto candidate = conjugateQuaternion(scaled);
    for (auto& component : candidate)
    {
        component = (component / lengthSquared) / scale;
        if (!std::isfinite(component))
        {
            return false;
        }
    }
    result = candidate;
    return true;
}

auto multiplyQuaternion(const LbQuaternion& a, const LbQuaternion& b) noexcept -> LbQuaternion
{
    return {a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
            a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
            a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
            a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2]};
}

auto quaternionFromEuler(const LbVector& degrees) noexcept -> LbQuaternion
{
    constexpr auto halfRadiansPerDegree = 0.00872664625997164788461845384244;
    auto x = -degrees[0] * halfRadiansPerDegree;
    auto y = -degrees[1] * halfRadiansPerDegree;
    auto z = degrees[2] * halfRadiansPerDegree;
    auto qx = LbQuaternion{std::sin(x), 0, 0, std::cos(x)};
    auto qy = LbQuaternion{0, std::sin(y), 0, std::cos(y)};
    auto qz = LbQuaternion{0, 0, std::sin(z), std::cos(z)};
    return multiplyQuaternion(qz, multiplyQuaternion(qy, qx));
}

auto quaternionToEuler(const LbQuaternion& value, LbVector& degrees) noexcept -> bool
{
    degrees = {};
    auto q = LbQuaternion{};
    if (!normalizeQuaternion(value, q))
    {
        return false;
    }
    auto x = q[0], y = q[1], z = q[2], w = q[3];
    auto m00 = 1 - 2 * (y * y + z * z);
    auto m01 = 2 * (x * y - z * w);
    auto m10 = 2 * (x * y + z * w);
    auto m11 = 1 - 2 * (x * x + z * z);
    auto m20 = 2 * (x * z - y * w);
    auto m21 = 2 * (y * z + x * w);
    auto m22 = 1 - 2 * (x * x + y * y);
    auto sinPitch = std::clamp(m20, -1.0, 1.0);
    auto cosPitch = std::hypot(m00, m10);
    // Same axes/signs and gimbal policy as decomposeTransform. atan2 avoids
    // asin's loss of precision near +/-90 degrees for a known unit rotation.
    auto roll = 0.0, pitch = std::atan2(sinPitch, cosPitch), yaw = 0.0;
    constexpr auto pi = 3.1415926535897932384626433832795;
    if (cosPitch <= 1e-10)
    {
        pitch = std::copysign(pi / 2, sinPitch);
        yaw = std::atan2(-m01, m11);
    }
    else
    {
        roll = std::atan2(-m21, m22);
        yaw = std::atan2(m10, m00);
    }
    constexpr auto degreesPerRadian = 180.0 / pi;
    auto rollDegrees = roll * degreesPerRadian;
    auto yawDegrees = yaw * degreesPerRadian;
    if (rollDegrees >= 180)
    {
        rollDegrees -= 360;
    }
    if (yawDegrees >= 180)
    {
        yawDegrees -= 360;
    }
    degrees = {rollDegrees, pitch * degreesPerRadian, yawDegrees};
    return true;
}

auto quaternionFromAxisAngle(const LbVector& axis, double degrees, LbQuaternion& result) noexcept -> bool
{
    result = {};
    if (!std::isfinite(degrees))
    {
        return false;
    }
    auto unit = LbQuaternion{};
    if (!normalizeQuaternion({axis[0], axis[1], axis[2], 0}, unit))
    {
        return false;
    }
    auto halfAngle = degrees * 0.00872664625997164788461845384244;
    auto sine = std::sin(halfAngle);
    result = {unit[0] * sine, unit[1] * sine, unit[2] * sine, std::cos(halfAngle)};
    return true;
}

auto slerpQuaternion(const LbQuaternion& a, const LbQuaternion& b, double t, LbQuaternion& result) noexcept -> bool
{
    auto inputA = a, inputB = b;
    result = {};
    auto first = LbQuaternion{}, second = LbQuaternion{};
    if (!std::isfinite(t) || !normalizeQuaternion(inputA, first) || !normalizeQuaternion(inputB, second))
    {
        return false;
    }
    t = std::clamp(t, 0.0, 1.0);
    auto dot = 0.0;
    for (auto lane = 0u; lane < 4; ++lane)
    {
        dot += first[lane] * second[lane];
    }
    if (dot < 0)
    {
        dot = -dot;
        for (auto& component : second)
        {
            component = -component;
        }
    }
    dot = std::clamp(dot, 0.0, 1.0);
    auto firstWeight = 1 - t, secondWeight = t;
    if (dot < 0.9995)
    {
        auto angle = std::acos(dot);
        auto denominator = std::sin(angle);
        firstWeight = std::sin((1 - t) * angle) / denominator;
        secondWeight = std::sin(t * angle) / denominator;
    }
    auto blended = LbQuaternion{};
    for (auto lane = 0u; lane < 4; ++lane)
    {
        blended[lane] = firstWeight * first[lane] + secondWeight * second[lane];
    }
    return normalizeQuaternion(blended, result);
}

auto rotateByQuaternion(const LbQuaternion& value, const LbVector& vector, LbVector& result) noexcept -> bool
{
    auto input = vector;
    result = {};
    auto q = LbQuaternion{};
    if (!normalizeQuaternion(value, q))
    {
        return false;
    }
    // Rotation matrix form avoids overflow in the common 2*cross(q.xyz, vector) formulation.
    auto x = q[0], y = q[1], z = q[2], w = q[3];
    auto candidate = LbVector{
        (1 - 2 * (y * y + z * z)) * input[0] + 2 * (x * y - z * w) * input[1] + 2 * (x * z + y * w) * input[2],
        2 * (x * y + z * w) * input[0] + (1 - 2 * (x * x + z * z)) * input[1] + 2 * (y * z - x * w) * input[2],
        2 * (x * z - y * w) * input[0] + 2 * (y * z + x * w) * input[1] + (1 - 2 * (x * x + y * y)) * input[2]};
    for (auto component : candidate)
    {
        if (!std::isfinite(component))
        {
            return false;
        }
    }
    result = candidate;
    return true;
}
}
