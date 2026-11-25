#pragma once

#include "liberafx/LbVfxRuntime.h"
#include "liberafx/math/LbVectorMath.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>

namespace liberafx::vfx_runtime_detail {

constexpr auto legacyHeaderSize = std::size_t{12};
constexpr auto legacyInstructionSize = std::size_t{44};
constexpr auto typedHeaderSize = std::size_t{16};
constexpr auto typedInstructionSize = std::size_t{96};
constexpr auto customInstructionSize = std::size_t{256};
constexpr auto curveKeySize = std::size_t{40};
constexpr auto maxNodes = std::uint32_t{10000};
constexpr auto maxCurveKeysPerInstruction = std::uint32_t{10000};
constexpr auto maxCurveKeysTotal = std::uint32_t{100000};
constexpr auto maxSignalRoutes = std::size_t{1000000};
constexpr auto maxLogicalQueueEvents = std::size_t{1000000};
constexpr auto noInput = std::uint32_t{0xffffffffu};
constexpr auto customParameterOpcode = std::uint8_t{40};
constexpr auto customSignalOpcode = std::uint8_t{41};
constexpr auto curveOpcode = std::uint8_t{42};
constexpr auto enumValueOpcode = std::uint8_t{43};
constexpr auto enumSelectOpcode = std::uint8_t{44};
constexpr auto branchTimeOpcode = std::uint8_t{45};
constexpr auto logicalEventSignalOpcode = std::uint8_t{47};
constexpr auto currentPhaseOpcode = std::uint8_t{55};
constexpr auto phaseTimeOpcode = std::uint8_t{56};

constexpr auto isEnumSourceOpcode(std::uint8_t opcode) noexcept -> bool {
  return opcode == enumValueOpcode || opcode == currentPhaseOpcode;
}

inline auto branchGuard(const std::uint8_t *current,
                        std::uint16_t version) noexcept -> std::uint32_t;
constexpr auto int32Opcode = std::uint8_t{48};
constexpr auto int32AddOpcode = std::uint8_t{49};
constexpr auto int32SubtractOpcode = std::uint8_t{50};
constexpr auto int32MultiplyOpcode = std::uint8_t{51};
constexpr auto int32DivideOpcode = std::uint8_t{52};
constexpr auto floatToInt32Opcode = std::uint8_t{53};
constexpr auto int32ToFloatOpcode = std::uint8_t{54};
constexpr auto customMaxPorts = std::uint8_t{4};
constexpr auto enumBranchHeaderSize = std::size_t{16};
constexpr auto enumValueRecordSize = std::size_t{24};
constexpr auto enumSelectRecordSize = std::size_t{68};
constexpr auto enumCaseRecordSize = std::size_t{40};
constexpr auto enumGuardRecordSize = std::size_t{40};
constexpr auto logicalEventHeaderSize = std::size_t{16};
constexpr auto valueTypeHeaderSize = std::size_t{8};
constexpr auto valueTypeRecordSize = std::size_t{8};
constexpr auto logicalInstructionRecordSize = std::size_t{32};
constexpr auto logicalSignalRouteRecordSize = std::size_t{12};
constexpr auto customWorkAlignment = std::size_t{16};
constexpr auto maxCustomStateAlignment = std::size_t{64};
constexpr auto logicalCompareTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x01};
constexpr auto logicalAndTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x02};
constexpr auto logicalOrTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x03};
constexpr auto logicalSignalToNumberTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x04};
constexpr auto logicalLoopClockTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x05};
constexpr auto logicalQualityBranchTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x06};
constexpr auto logicalSignalCaptureTypeId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x07};
constexpr auto valueTypeFloat = std::uint8_t{1};
constexpr auto valueTypeInt32 = std::uint8_t{2};
constexpr auto valueTypeVector2 = std::uint8_t{3};
constexpr auto valueTypeVector3 = std::uint8_t{4};
constexpr auto valueTypeVector4 = std::uint8_t{5};
constexpr auto valueTypeColor = std::uint8_t{6};
constexpr auto valueTypeEnum = std::uint8_t{7};
inline std::atomic<std::uint64_t> nextProgramGeneration{1};
inline std::atomic<std::uint64_t> nextInstanceGeneration{1};
static_assert(sizeof(double) == 8 && std::numeric_limits<double>::is_iec559,
              "LIBERA FX requires IEEE-754 binary64.");

inline auto validStateAlignment(std::size_t alignment) noexcept -> bool;
inline auto instruction(const std::uint8_t *bytes, std::uint16_t version,
                        std::size_t index) noexcept -> const std::uint8_t *;

inline auto read16(const std::uint8_t *bytes) noexcept -> std::uint16_t {
  return static_cast<std::uint16_t>(bytes[0] | (std::uint16_t{bytes[1]} << 8));
}

inline auto read32(const std::uint8_t *bytes) noexcept -> std::uint32_t {
  return std::uint32_t{bytes[0]} | (std::uint32_t{bytes[1]} << 8) |
         (std::uint32_t{bytes[2]} << 16) | (std::uint32_t{bytes[3]} << 24);
}

inline auto branchGuard(const std::uint8_t *current,
                        std::uint16_t /*version*/) noexcept -> std::uint32_t {
  auto opcode = current[16];
  auto customLayout = opcode == customParameterOpcode ||
                      opcode == customSignalOpcode ||
                      opcode == logicalEventSignalOpcode;
  return read32(current + (customLayout ? 44 : 112));
}

inline auto read64(const std::uint8_t *bytes) noexcept -> std::uint64_t {
  auto value = std::uint64_t{0};
  for (auto index = 0u; index < 8; ++index) {
    value |= std::uint64_t{bytes[index]} << (index * 8);
  }
  return value;
}

inline auto readI32(const std::uint8_t *bytes) noexcept -> std::int32_t {
  auto bits = read32(bytes);
  auto value = std::int32_t{};
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

inline auto readDouble(const std::uint8_t *bytes) noexcept -> double {
  auto bits = read64(bytes);
  auto value = double{};
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

inline void write64(std::uint8_t *bytes, std::uint64_t value) noexcept {
  for (auto index = 0u; index < 8; ++index) {
    bytes[index] = static_cast<std::uint8_t>(value >> (index * 8));
  }
}

inline void writeDouble(std::uint8_t *bytes, double value) noexcept {
  auto bits = std::uint64_t{};
  std::memcpy(&bits, &value, sizeof(bits));
  write64(bytes, bits);
}

inline auto instruction(const std::uint8_t *bytes, std::uint16_t version,
                        std::size_t index) noexcept -> const std::uint8_t *;

inline auto instruction(const std::uint8_t *bytes, std::uint16_t /*version*/,
                        std::size_t index) noexcept -> const std::uint8_t * {
  return bytes + typedHeaderSize + index * customInstructionSize;
}

inline auto validStateAlignment(std::size_t alignment) noexcept -> bool {
  return alignment >= customWorkAlignment &&
         alignment <= maxCustomStateAlignment &&
         (alignment & (alignment - 1)) == 0;
}

} // namespace liberafx::vfx_runtime_detail
