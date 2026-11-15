#pragma once

#include <cmath>
#include <cstddef>
#include <cstdint>

#if !defined(LIBERAFX_MATH_FORCE_SCALAR) && \
    (defined(_M_ARM64) || defined(__aarch64__) || defined(__arm64__))
#define LIBERAFX_VECTOR_MATH_NEON 1
#if defined(_MSC_VER) && !defined(__clang__)
#include <arm64_neon.h>
#else
#include <arm_neon.h>
#endif
#elif !defined(LIBERAFX_MATH_FORCE_SCALAR) && \
    (defined(__SSE2__) || defined(_M_X64) || (defined(_M_IX86_FP) && _M_IX86_FP >= 2))
#define LIBERAFX_VECTOR_MATH_SSE2 1
#include <emmintrin.h>
#endif

namespace liberafx::math {

enum class Arithmetic : std::uint8_t { Add, Subtract, Multiply, Divide };
enum class ArithmeticResult : std::uint8_t { Ok, DivideByZero, NonFinite, InvalidArgument };

// Width is 1..4. Only active lanes are read or written; buffers may overlap.
// On failure, earlier successful lanes are written, just as in the interpreter.
inline auto arithmetic(double* output, const double* left, const double* right,
                       std::size_t width, Arithmetic operation) noexcept -> ArithmeticResult
{
    if (width == 0 || width > 4 || output == nullptr || left == nullptr || right == nullptr ||
        operation > Arithmetic::Divide)
        return ArithmeticResult::InvalidArgument;

    auto zeroMask = std::uint8_t{0};
    double values[4]{};
    if (operation == Arithmetic::Divide)
        for (auto lane = std::size_t{0}; lane < width; ++lane)
            if (right[lane] == 0) zeroMask |= static_cast<std::uint8_t>(1u << lane);

#if defined(LIBERAFX_VECTOR_MATH_SSE2)
    for (auto lane = std::size_t{0}; lane + 1 < width; lane += 2)
    {
        auto av = _mm_loadu_pd(left + lane);
        auto bv = _mm_loadu_pd(right + lane);
        if ((zeroMask & (3u << lane)) != 0)
        {
            auto zero = _mm_cmpeq_pd(bv, _mm_setzero_pd());
            bv = _mm_or_pd(_mm_andnot_pd(zero, bv), _mm_and_pd(zero, _mm_set1_pd(1)));
        }
        auto result = av;
        switch (operation)
        {
        case Arithmetic::Add: result = _mm_add_pd(av, bv); break;
        case Arithmetic::Subtract: result = _mm_sub_pd(av, bv); break;
        case Arithmetic::Multiply: result = _mm_mul_pd(av, bv); break;
        case Arithmetic::Divide: result = _mm_div_pd(av, bv); break;
        }
        _mm_storeu_pd(values + lane, result);
    }
#elif defined(LIBERAFX_VECTOR_MATH_NEON)
    for (auto lane = std::size_t{0}; lane + 1 < width; lane += 2)
    {
        auto av = vld1q_f64(left + lane);
        auto bv = vld1q_f64(right + lane);
        if ((zeroMask & (3u << lane)) != 0)
            bv = vbslq_f64(vceqq_f64(bv, vdupq_n_f64(0)), vdupq_n_f64(1), bv);
        auto result = av;
        switch (operation)
        {
        case Arithmetic::Add: result = vaddq_f64(av, bv); break;
        case Arithmetic::Subtract: result = vsubq_f64(av, bv); break;
        case Arithmetic::Multiply: result = vmulq_f64(av, bv); break;
        case Arithmetic::Divide: result = vdivq_f64(av, bv); break;
        }
        vst1q_f64(values + lane, result);
    }
#endif
#if defined(LIBERAFX_VECTOR_MATH_SSE2) || defined(LIBERAFX_VECTOR_MATH_NEON)
    auto scalarStart = width - width % 2;
#else
    auto scalarStart = std::size_t{0};
#endif
    for (auto lane = scalarStart; lane < width; ++lane)
    {
        auto a = left[lane];
        auto b = (zeroMask & (1u << lane)) != 0 ? 1.0 : right[lane];
        switch (operation)
        {
        case Arithmetic::Add: values[lane] = a + b; break;
        case Arithmetic::Subtract: values[lane] = a - b; break;
        case Arithmetic::Multiply: values[lane] = a * b; break;
        case Arithmetic::Divide: values[lane] = a / b; break;
        }
    }
    for (auto lane = std::size_t{0}; lane < width; ++lane)
    {
        if ((zeroMask & (1u << lane)) != 0) return ArithmeticResult::DivideByZero;
        if (!std::isfinite(values[lane])) return ArithmeticResult::NonFinite;
        output[lane] = values[lane];
    }
    return ArithmeticResult::Ok;
}

inline auto allFinite(const double* values, std::size_t width) noexcept -> bool
{
    if (values == nullptr || width == 0 || width > 4) return false;
    for (auto lane = std::size_t{0}; lane < width; ++lane)
        if (!std::isfinite(values[lane])) return false;
    return true;
}

}

#undef LIBERAFX_VECTOR_MATH_SSE2
#undef LIBERAFX_VECTOR_MATH_NEON
