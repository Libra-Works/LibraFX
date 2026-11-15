#include "liberafx/math/VectorMath.h"

#include <array>
#include <cmath>
#include <cstddef>
#include <cstdlib>
#include <iostream>
#include <limits>

namespace {
using liberafx::math::Arithmetic;
using liberafx::math::ArithmeticResult;
using liberafx::math::arithmetic;
using liberafx::math::allFinite;

void require(bool condition)
{
    if (!condition) std::abort();
}

auto reference(double a, double b, Arithmetic operation) -> double
{
    switch (operation)
    {
    case Arithmetic::Add: return a + b;
    case Arithmetic::Subtract: return a - b;
    case Arithmetic::Multiply: return a * b;
    case Arithmetic::Divide: return a / b;
    }
    std::abort();
}

void checkArithmetic()
{
    alignas(16) double leftStorage[6]{0, -3.25, 0.0, 7.5, -12.0, 0};
    alignas(16) double rightStorage[6]{0, 2.0, -4.0, 3.0, -2.0, 0};
    alignas(16) double outputStorage[6]{};
    auto left = leftStorage + 1;
    auto right = rightStorage + 1;
    auto output = outputStorage + 1;
    require(reinterpret_cast<std::uintptr_t>(left) % 16 != 0);
    require(reinterpret_cast<std::uintptr_t>(right) % 16 != 0);
    require(reinterpret_cast<std::uintptr_t>(output) % 16 != 0);
    auto operations = std::array{Arithmetic::Add, Arithmetic::Subtract,
                                 Arithmetic::Multiply, Arithmetic::Divide};
    for (auto operation : operations)
    {
        for (auto width = std::size_t{1}; width <= 4; ++width)
        {
            for (auto lane = std::size_t{0}; lane < 4; ++lane) output[lane] = 12345;
            require(arithmetic(output, left, right, width, operation) == ArithmeticResult::Ok);
            for (auto lane = std::size_t{0}; lane < width; ++lane)
            {
                auto expected = reference(left[lane], right[lane], operation);
                require(output[lane] == expected);
                if (expected == 0) require(std::signbit(output[lane]) == std::signbit(expected));
            }
            for (auto lane = width; lane < 4; ++lane) require(output[lane] == 12345);

            auto inplaceLeft = std::array{left[0], left[1], left[2], left[3]};
            require(arithmetic(inplaceLeft.data(), inplaceLeft.data(), right, width, operation) ==
                    ArithmeticResult::Ok);
            for (auto lane = std::size_t{0}; lane < width; ++lane)
                require(inplaceLeft[lane] == reference(left[lane], right[lane], operation));
            auto inplaceRight = std::array{right[0], right[1], right[2], right[3]};
            require(arithmetic(inplaceRight.data(), left, inplaceRight.data(), width, operation) ==
                    ArithmeticResult::Ok);
            for (auto lane = std::size_t{0}; lane < width; ++lane)
                require(inplaceRight[lane] == reference(left[lane], right[lane], operation));
        }
    }
    auto overlap = std::array{1.0, 2.0, 3.0, 4.0, 5.0};
    require(arithmetic(overlap.data() + 1, overlap.data(), overlap.data() + 2, 3,
                       Arithmetic::Add) == ArithmeticResult::Ok);
    require(overlap == std::array{1.0, 4.0, 6.0, 8.0, 5.0});
}

void checkExceptionalLanes()
{
    auto infinity = std::numeric_limits<double>::infinity();
    auto nan = std::numeric_limits<double>::quiet_NaN();
    auto left = std::array{8.0, 6.0, infinity, nan};
    auto right = std::array{2.0, 3.0, 0.0, 0.0};
    auto output = std::array{99.0, 99.0, 99.0, 99.0};
    require(arithmetic(output.data(), left.data(), right.data(), 2, Arithmetic::Divide) ==
            ArithmeticResult::Ok);
    require(output == std::array{4.0, 2.0, 99.0, 99.0});
    require(allFinite(left.data(), 2));
    require(!allFinite(left.data(), 3));
    require(!allFinite(left.data(), 4));

    left = {8.0, 6.0, 9.0, nan};
    right = {2.0, 3.0, 3.0, 0.0};
    output = {99.0, 99.0, 99.0, 99.0};
    require(arithmetic(output.data(), left.data(), right.data(), 3, Arithmetic::Divide) ==
            ArithmeticResult::Ok);
    require(output == std::array{4.0, 2.0, 3.0, 99.0});
    auto shortLeft = std::array{8.0, 6.0, 9.0};
    auto shortRight = std::array{2.0, 3.0, 3.0};
    auto shortOutput = std::array{0.0, 0.0, 0.0};
    require(arithmetic(shortOutput.data(), shortLeft.data(), shortRight.data(), 3,
                       Arithmetic::Divide) == ArithmeticResult::Ok);
    require(shortOutput == std::array{4.0, 2.0, 3.0});

    left = {8.0, infinity, 12.0, 9.0};
    right = {2.0, 1.0, -0.0, 3.0};
    output = {99.0, 99.0, 99.0, 99.0};
    require(arithmetic(output.data(), left.data(), right.data(), 4, Arithmetic::Divide) ==
            ArithmeticResult::NonFinite);
    require(output == std::array{4.0, 99.0, 99.0, 99.0});

    left = {8.0, 6.0, nan, 9.0};
    right = {2.0, 3.0, -0.0, 3.0};
    output = {99.0, 99.0, 99.0, 99.0};
    require(arithmetic(output.data(), left.data(), right.data(), 4, Arithmetic::Divide) ==
            ArithmeticResult::DivideByZero);
    require(output == std::array{4.0, 2.0, 99.0, 99.0});

    left = {8.0, 6.0, std::numeric_limits<double>::max(), 9.0};
    right = {2.0, 3.0, 2.0, 3.0};
    output = {99.0, 99.0, 99.0, 99.0};
    require(arithmetic(output.data(), left.data(), right.data(), 3, Arithmetic::Multiply) ==
            ArithmeticResult::NonFinite);
    require(output == std::array{16.0, 18.0, 99.0, 99.0});

    require(arithmetic(output.data(), left.data(), right.data(), 0, Arithmetic::Add) ==
            ArithmeticResult::InvalidArgument);
    require(arithmetic(output.data(), left.data(), right.data(), 5, Arithmetic::Add) ==
            ArithmeticResult::InvalidArgument);
    require(arithmetic(nullptr, left.data(), right.data(), 2, Arithmetic::Add) ==
            ArithmeticResult::InvalidArgument);
    require(arithmetic(output.data(), left.data(), right.data(), 2, static_cast<Arithmetic>(99)) ==
            ArithmeticResult::InvalidArgument);
    require(!allFinite(nullptr, 2) && !allFinite(left.data(), 0) && !allFinite(left.data(), 5));
}

void checkScalarAlignment()
{
    alignas(16) auto storage = std::array<std::uint8_t, 64>{};
    auto misaligned = reinterpret_cast<double*>(storage.data() + 1);
    auto left = std::array<double, 2>{2, 3};
    auto right = std::array<double, 2>{4, 5};
    auto output = std::array<double, 2>{17, 19};
    require(arithmetic(misaligned, left.data(), right.data(), 2, Arithmetic::Add) ==
            ArithmeticResult::InvalidArgument);
    require(arithmetic(output.data(), misaligned, right.data(), 2, Arithmetic::Add) ==
            ArithmeticResult::InvalidArgument);
    require(arithmetic(output.data(), left.data(), misaligned, 2, Arithmetic::Add) ==
            ArithmeticResult::InvalidArgument);
    require(output == std::array<double, 2>{17, 19});
    require(!allFinite(misaligned, 2));
}
}

int main()
{
    checkArithmetic();
    checkExceptionalLanes();
    checkScalarAlignment();
    std::cout << "Portable vector math checks passed.\n";
}
