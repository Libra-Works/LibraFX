#include "LbBuiltinNodesInternal.h"
#include "liberafx/LbVfxRuntime.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>

namespace liberafx::builtin_math_detail
{
constexpr auto pi = 3.141592653589793238462643383279502884;
enum class Op
{
    Abs,
    Floor,
    Round,
    Sign,
    Sqrt,
    Pow,
    Log,
    Log10,
    Sin,
    Cos,
    Tan,
    ATan,
    ATan2,
    DegToRad,
    RadToDeg,
    NormalizeRad,
    NormalizeDeg,
    Min,
    Max,
    Clamp,
    Lerp,
    Mad,
    Pi,
    TwoPi,
    Length,
    Dot,
    Normalize,
    Cross,
    Compose
};

// Domain errors and non-finite inputs/results report NonFinite. Round ties go away
// from zero; angles wrap to [-pi, pi) or [-180, 180). Zero vectors normalize to zero.
template <Op op, unsigned width> auto evaluate(CustomNodeInvocation& call) noexcept -> Result
{
    for (auto input = 0u; input < call.inputCount; ++input)
    {
        for (auto lane = 0u; lane < call.inputWidths[input]; ++lane)
        {
            if (!std::isfinite(call.inputs[input * 4 + lane]))
            {
                return Result::NonFinite;
            }
        }
    }
    auto a = call.inputs;
    auto b = call.inputs + 4;
    auto c = call.inputs + 8;
    if constexpr (op == Op::Pi || op == Op::TwoPi)
    {
        call.outputs[0] = op == Op::Pi ? pi : 2 * pi;
    }
    else if constexpr (op == Op::Compose)
    {
        for (auto lane = 0u; lane < 4; ++lane)
        {
            call.outputs[lane] = call.inputs[lane * 4];
        }
    }
    else if constexpr (op == Op::Length || op == Op::Normalize)
    {
        auto scale = 0.0;
        for (auto lane = 0u; lane < width; ++lane)
        {
            scale = std::max(scale, std::abs(a[lane]));
        }
        auto sum = 0.0;
        if (scale != 0)
        {
            for (auto lane = 0u; lane < width; ++lane)
            {
                sum += (a[lane] / scale) * (a[lane] / scale);
            }
        }
        auto factor = std::sqrt(sum);
        if constexpr (op == Op::Length)
        {
            call.outputs[0] = scale * factor;
        }
        else
        {
            for (auto lane = 0u; lane < width; ++lane)
            {
                call.outputs[lane] = scale == 0 ? 0 : (a[lane] / scale) / factor;
            }
        }
    }
    else if constexpr (op == Op::Dot)
    {
        auto sum = 0.0;
        for (auto lane = 0u; lane < width; ++lane)
        {
            sum += a[lane] * b[lane];
        }
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
            if constexpr (op == Op::Abs)
            {
                value = std::abs(x);
            }
            if constexpr (op == Op::Floor)
            {
                if (y == 0)
                {
                    return Result::DivideByZero;
                }
                value = std::floor(x / y) * y;
            }
            if constexpr (op == Op::Round)
            {
                value = std::round(x);
            }
            if constexpr (op == Op::Sign)
            {
                value = (x > 0) - (x < 0);
            }
            if constexpr (op == Op::Sqrt)
            {
                value = std::sqrt(x);
            }
            if constexpr (op == Op::Pow)
            {
                value = std::pow(x, y);
            }
            if constexpr (op == Op::Log)
            {
                value = std::log(x);
            }
            if constexpr (op == Op::Log10)
            {
                value = std::log10(x);
            }
            if constexpr (op == Op::Sin)
            {
                value = std::sin(x);
            }
            if constexpr (op == Op::Cos)
            {
                value = std::cos(x);
            }
            if constexpr (op == Op::Tan)
            {
                value = std::tan(x);
            }
            if constexpr (op == Op::ATan)
            {
                value = std::atan(x);
            }
            if constexpr (op == Op::ATan2)
            {
                value = std::atan2(x, y);
            }
            if constexpr (op == Op::DegToRad)
            {
                value = x * (pi / 180);
            }
            if constexpr (op == Op::RadToDeg)
            {
                value = x * (180 / pi);
            }
            if constexpr (op == Op::NormalizeRad || op == Op::NormalizeDeg)
            {
                auto half = op == Op::NormalizeRad ? pi : 180.0;
                value = std::fmod(x, 2 * half);
                if (value >= half)
                {
                    value -= 2 * half;
                }
                if (value < -half)
                {
                    value += 2 * half;
                }
            }
            if constexpr (op == Op::Min)
            {
                value = std::min(x, y);
            }
            if constexpr (op == Op::Max)
            {
                value = std::max(x, y);
            }
            if constexpr (op == Op::Clamp)
            {
                if (y > z)
                {
                    return Result::InvalidArgument;
                }
                value = std::max(y, std::min(x, z));
            }
            if constexpr (op == Op::Lerp)
            {
                value = x * (1 - c[0]) + y * c[0];
            }
            if constexpr (op == Op::Mad)
            {
                value = x * y + z;
            }
            call.outputs[lane] = value;
        }
    }
    for (auto lane = 0u; lane < call.outputWidth; ++lane)
    {
        if (!std::isfinite(call.outputs[lane]))
        {
            return Result::NonFinite;
        }
    }
    return Result::Ok;
}

template <Op op, unsigned width>
auto makeDescriptor(unsigned id, unsigned inputCount, unsigned outputWidth, unsigned secondWidth = width,
                    unsigned thirdWidth = width) noexcept -> CustomNodeDescriptor
{
    auto descriptor = CustomNodeDescriptor{};
    const std::uint8_t prefix[] = {0x74, 0x30, 0x5a, 0xc1, 0x91, 0xb2, 0x4c, 0x33, 0x8d, 0x44};
    for (auto index = 0u; index < 10; ++index)
    {
        descriptor.typeId[index] = prefix[index];
    }
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

auto arithmeticNodeDescriptors() noexcept -> const builtin_node_detail::ArithmeticNodeDescriptors&
{
    static const auto descriptors = builtin_node_detail::ArithmeticNodeDescriptors{

        makeDescriptor<Op::Abs, 1>(1, 1, 1),           makeDescriptor<Op::Floor, 1>(2, 2, 1),
        makeDescriptor<Op::Round, 1>(3, 1, 1),         makeDescriptor<Op::Sign, 1>(4, 1, 1),
        makeDescriptor<Op::Sqrt, 1>(5, 1, 1),          makeDescriptor<Op::Pow, 1>(6, 2, 1),
        makeDescriptor<Op::Log, 1>(7, 1, 1),           makeDescriptor<Op::Log10, 1>(8, 1, 1),
        makeDescriptor<Op::Sin, 1>(9, 1, 1),           makeDescriptor<Op::Cos, 1>(10, 1, 1),
        makeDescriptor<Op::Tan, 1>(11, 1, 1),          makeDescriptor<Op::ATan, 1>(12, 1, 1),
        makeDescriptor<Op::ATan2, 1>(13, 2, 1),        makeDescriptor<Op::DegToRad, 1>(14, 1, 1),
        makeDescriptor<Op::RadToDeg, 1>(15, 1, 1),     makeDescriptor<Op::NormalizeRad, 1>(16, 1, 1),
        makeDescriptor<Op::NormalizeDeg, 1>(17, 1, 1), makeDescriptor<Op::Min, 1>(18, 2, 1),
        makeDescriptor<Op::Max, 1>(19, 2, 1),          makeDescriptor<Op::Clamp, 1>(20, 3, 1),
        makeDescriptor<Op::Lerp, 1>(21, 3, 1, 1, 1),   makeDescriptor<Op::Mad, 1>(22, 3, 1),
        makeDescriptor<Op::Pi, 1>(23, 0, 1),           makeDescriptor<Op::TwoPi, 1>(24, 0, 1),
        makeDescriptor<Op::Length, 2>(25, 1, 1),       makeDescriptor<Op::Dot, 2>(26, 2, 1),
        makeDescriptor<Op::Normalize, 2>(27, 1, 2),    makeDescriptor<Op::Length, 3>(28, 1, 1),
        makeDescriptor<Op::Dot, 3>(29, 2, 1),          makeDescriptor<Op::Normalize, 3>(30, 1, 3),
        makeDescriptor<Op::Length, 4>(31, 1, 1),       makeDescriptor<Op::Dot, 4>(32, 2, 1),
        makeDescriptor<Op::Normalize, 4>(33, 1, 4),    makeDescriptor<Op::Cross, 3>(34, 2, 3),
        makeDescriptor<Op::Abs, 2>(35, 1, 2),          makeDescriptor<Op::Min, 2>(36, 2, 2),
        makeDescriptor<Op::Max, 2>(37, 2, 2),          makeDescriptor<Op::Lerp, 2>(38, 3, 2, 2, 1),
        makeDescriptor<Op::Abs, 3>(39, 1, 3),          makeDescriptor<Op::Min, 3>(40, 2, 3),
        makeDescriptor<Op::Max, 3>(41, 2, 3),          makeDescriptor<Op::Lerp, 3>(42, 3, 3, 3, 1),
        makeDescriptor<Op::Abs, 4>(43, 1, 4),          makeDescriptor<Op::Min, 4>(44, 2, 4),
        makeDescriptor<Op::Max, 4>(45, 2, 4),          makeDescriptor<Op::Lerp, 4>(46, 3, 4, 4, 1),
        makeDescriptor<Op::Compose, 1>(47, 4, 4)};
    return descriptors;
}
} // namespace liberafx::builtin_math_detail

namespace liberafx::builtin_node_detail
{
auto arithmeticNodeDescriptors() noexcept -> const ArithmeticNodeDescriptors&
{
    return builtin_math_detail::arithmeticNodeDescriptors();
}
} // namespace liberafx::builtin_node_detail
