#include "BuiltinNodesInternal.h"
#include "liberafx/VfxRuntime.h"
#include "liberafx/math/MatrixMath.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>

namespace liberafx::builtin_matrix_detail
{
enum class MatrixOp
{
    TransformRow,
    OrderedTransformRow,
    InverseRow,
    InverseValid,
    DecomposePosition,
    DecomposeRotation,
    DecomposeScale,
    DecomposeValid
};

template <MatrixOp op, unsigned selectedRow = 0> auto evaluateMatrix(CustomNodeInvocation& call) noexcept -> Result
{
    for (auto lane = 0u; lane < call.outputWidth; ++lane)
    {
        call.outputs[lane] = 0;
    }
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

    if constexpr (op == MatrixOp::TransformRow || op == MatrixOp::OrderedTransformRow)
    {
        auto position = math::LbVector{call.inputs[0], call.inputs[1], call.inputs[2]};
        auto rotation = math::LbVector{call.inputs[4], call.inputs[5], call.inputs[6]};
        auto scale = math::LbVector{call.inputs[8], call.inputs[9], call.inputs[10]};
        auto matrix = math::makeTransform(position, rotation, scale);
        if constexpr (op == MatrixOp::OrderedTransformRow)
        {
            auto order = call.inputs[12];
            if (order < 0 || order > 5 || order != std::floor(order))
            {
                return Result::InvalidArgument;
            }
            matrix = math::makeOrderedTransform(position, rotation, scale, static_cast<math::TransformOrder>(order));
        }
        for (auto lane = 0u; lane < 4; ++lane)
        {
            auto value = matrix[selectedRow * 4 + lane];
            if (!std::isfinite(value))
            {
                return Result::NonFinite;
            }
            call.outputs[lane] = value;
        }
    }
    else
    {
        auto matrix = math::LbMatrix{};
        for (auto row = 0u; row < 4; ++row)
        {
            for (auto column = 0u; column < 4; ++column)
            {
                matrix[row * 4 + column] = call.inputs[row * 4 + column];
            }
        }

        if constexpr (op == MatrixOp::InverseRow || op == MatrixOp::InverseValid)
        {
            auto inverse = math::LbMatrix{};
            if (!math::invertMatrix(matrix, inverse))
            {
                return Result::Ok;
            }
            if constexpr (op == MatrixOp::InverseValid)
            {
                call.outputs[0] = 1;
            }
            else
            {
                for (auto lane = 0u; lane < 4; ++lane)
                {
                    call.outputs[lane] = inverse[selectedRow * 4 + lane];
                }
            }
        }
        else
        {
            auto position = math::LbVector{};
            auto rotation = math::LbVector{};
            auto scale = math::LbVector{};
            if (!math::decomposeTransform(matrix, position, rotation, scale))
            {
                return Result::Ok;
            }
            if constexpr (op == MatrixOp::DecomposeValid)
            {
                call.outputs[0] = 1;
            }
            if constexpr (op == MatrixOp::DecomposePosition)
            {
                for (auto lane = 0u; lane < 3; ++lane)
                {
                    call.outputs[lane] = position[lane];
                }
            }
            if constexpr (op == MatrixOp::DecomposeRotation)
            {
                for (auto lane = 0u; lane < 3; ++lane)
                {
                    call.outputs[lane] = rotation[lane];
                }
            }
            if constexpr (op == MatrixOp::DecomposeScale)
            {
                for (auto lane = 0u; lane < 3; ++lane)
                {
                    call.outputs[lane] = scale[lane];
                }
            }
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

template <MatrixOp op, unsigned outputWidth, unsigned selectedRow = 0>
auto makeMatrixDescriptor(unsigned id, unsigned inputCount, unsigned inputWidth) noexcept -> CustomNodeDescriptor
{
    auto descriptor = CustomNodeDescriptor{};
    const std::uint8_t prefix[] = {0xb0, 0xaf, 0x61, 0xd2, 0x29, 0xf6, 0x4c, 0xc9, 0xa7, 0x1e};
    for (auto index = 0u; index < 10; ++index)
    {
        descriptor.typeId[index] = prefix[index];
    }
    descriptor.typeId[15] = static_cast<std::uint8_t>(id);
    descriptor.abiVersion = 1;
    descriptor.execution = CustomExecution::Parameter;
    descriptor.inputCount = static_cast<std::uint8_t>(inputCount);
    for (auto input = 0u; input < inputCount; ++input)
    {
        descriptor.inputWidths[input] = static_cast<std::uint8_t>(inputWidth);
    }
    if constexpr (op == MatrixOp::OrderedTransformRow)
    {
        descriptor.inputWidths[3] = 1;
    }
    descriptor.outputWidth = static_cast<std::uint8_t>(outputWidth);
    descriptor.function = evaluateMatrix<op, selectedRow>;
    return descriptor;
}

auto matrixNodeDescriptors() noexcept -> const builtin_node_detail::MatrixNodeDescriptors&
{
    static const auto descriptors =
        builtin_node_detail::MatrixNodeDescriptors{makeMatrixDescriptor<MatrixOp::TransformRow, 4, 0>(1, 3, 3),
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
                                                   makeMatrixDescriptor<MatrixOp::DecomposeValid, 1>(13, 4, 4)};
    return descriptors;
}
auto orderedMatrixNodeDescriptors() noexcept -> const builtin_node_detail::OrderedMatrixNodeDescriptors&
{
    static const auto descriptors = builtin_node_detail::OrderedMatrixNodeDescriptors{
        makeMatrixDescriptor<MatrixOp::OrderedTransformRow, 4, 0>(15, 4, 3),
        makeMatrixDescriptor<MatrixOp::OrderedTransformRow, 4, 1>(16, 4, 3),
        makeMatrixDescriptor<MatrixOp::OrderedTransformRow, 4, 2>(17, 4, 3),
        makeMatrixDescriptor<MatrixOp::OrderedTransformRow, 4, 3>(18, 4, 3)};
    return descriptors;
}
} // namespace liberafx::builtin_matrix_detail

namespace liberafx::builtin_node_detail
{
auto matrixNodeDescriptors() noexcept -> const MatrixNodeDescriptors&
{
    return builtin_matrix_detail::matrixNodeDescriptors();
}

auto orderedMatrixNodeDescriptors() noexcept -> const OrderedMatrixNodeDescriptors&
{
    return builtin_matrix_detail::orderedMatrixNodeDescriptors();
}
} // namespace liberafx::builtin_node_detail
