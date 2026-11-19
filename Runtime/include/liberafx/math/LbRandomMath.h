#pragma once

#include "LbMatrixMath.h"
#include <cstdint>

namespace liberafx::math
{
// Version 1 stateless, non-cryptographic sampling. All arithmetic is unsigned 32-bit.
auto randomWord(std::uint32_t seed, std::uint32_t sampleIndex, std::uint32_t stream = 0) noexcept -> std::uint32_t;
auto randomUnit(std::uint32_t seed, std::uint32_t sampleIndex, std::uint32_t stream = 0) noexcept -> double;
// Inclusive endpoints; reversed endpoints are swapped. Rejection removes modulo mapping bias.
auto randomInteger(std::uint32_t seed, std::uint32_t sampleIndex, std::int32_t low, std::int32_t high) noexcept -> std::int32_t;
auto randomSphereDirection(std::uint32_t seed, std::uint32_t sampleIndex) noexcept -> LbVector;
auto randomSpherePoint(std::uint32_t seed, std::uint32_t sampleIndex) noexcept -> LbVector;
}
