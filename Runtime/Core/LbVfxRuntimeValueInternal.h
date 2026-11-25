#pragma once

#include "LbVfxRuntimeBinaryInternal.h"

namespace liberafx::vfx_runtime_detail {
inline auto alignUp(std::size_t value, std::size_t alignment,
                    std::size_t &out) noexcept -> bool {
  auto padding = alignment - 1;
  if (value > std::numeric_limits<std::size_t>::max() - padding) {
    return false;
  }
  out = (value + padding) & ~padding;
  return true;
}

inline auto isAligned(const void *pointer, std::size_t alignment) noexcept
    -> bool {
  return pointer != nullptr &&
         reinterpret_cast<std::uintptr_t>(pointer) % alignment == 0;
}

inline auto stateOffset(const CustomNodeBinding *bindings,
                        std::size_t nodeIndex) noexcept -> std::size_t {
  return bindings[nodeIndex].stateOffset;
}

// Only the Core-owned, explicitly enumerated arithmetic and matrix primitives
// may be re-evaluated after Signal dispatch. A host registry can override a
// builtin UUID, so matching the UUID alone must never establish purity.
inline auto isLatePureBuiltin(const CustomNodeDescriptor *descriptor) noexcept
    -> bool {
  if (descriptor == nullptr || descriptor->abiVersion != 1 ||
      descriptor->execution != CustomExecution::Parameter ||
      descriptor->stateBytes != 0 || descriptor->context != nullptr) {
    return false;
  }
  auto count = std::size_t{0};
  auto builtins = builtinNodes(count);
  auto isBuiltin = false;
  for (auto index = std::size_t{0}; index < count; ++index) {
    if (descriptor == &builtins[index]) {
      isBuiltin = true;
      break;
    }
  }
  if (!isBuiltin) {
    return false;
  }
  constexpr auto mathPrefix = std::array<std::uint8_t, 15>{
      0x74, 0x30, 0x5a, 0xc1, 0x91, 0xb2, 0x4c, 0x33,
      0x8d, 0x44, 0,    0,    0,    0,    0};
  constexpr auto matrixPrefix = std::array<std::uint8_t, 15>{
      0xb0, 0xaf, 0x61, 0xd2, 0x29, 0xf6, 0x4c, 0xc9,
      0xa7, 0x1e, 0,    0,    0,    0,    0};
  auto id = descriptor->typeId[15];
  constexpr auto timePrefix = std::array<std::uint8_t, 15>{
      0xc9, 0x82, 0xe1, 0xbe, 0x8f, 0x78, 0x4d, 0x28,
      0x9a, 0x65, 0x4f, 0x4a, 0xa4, 0x52, 0x49};
  if (std::memcmp(descriptor->typeId, timePrefix.data(), timePrefix.size()) == 0 && id >= 0xe6 && id <= 0xea)
    return true;
  return (std::memcmp(descriptor->typeId, mathPrefix.data(),
                      mathPrefix.size()) == 0 &&
          id >= 1 && id <= 47) ||
         (std::memcmp(descriptor->typeId, matrixPrefix.data(),
                      matrixPrefix.size()) == 0 &&
          ((id >= 1 && id <= 13) || (id >= 15 && id <= 18)));
}

inline auto validIndex(std::uint32_t input, std::uint32_t index) noexcept
    -> bool {
  return input == noInput || input < index;
}

inline auto typedInputWidth(std::uint32_t input, std::uint8_t width) noexcept
    -> bool {
  return input == noInput ? width == 0 : width >= 1 && width <= 4;
}

inline auto validInt32(double value) noexcept -> bool {
  return std::isfinite(value) && std::trunc(value) == value &&
         value >= std::numeric_limits<std::int32_t>::min() &&
         value <= std::numeric_limits<std::int32_t>::max();
}

inline auto int32FromDouble(double value, std::int32_t &result) noexcept
    -> Result {
  if (!std::isfinite(value)) {
    return Result::NonFinite;
  }
  if (!validInt32(value)) {
    return Result::InvalidValueType;
  }
  result = static_cast<std::int32_t>(value);
  return Result::Ok;
}

inline auto requiredInputWidths(std::uint8_t opcode, std::uint8_t outputWidth,
                                std::uint8_t &inputAWidth,
                                std::uint8_t &inputBWidth, bool &requiresA,
                                bool &requiresB) noexcept -> bool {
  requiresA = false;
  requiresB = false;
  if (opcode == int32Opcode) {
    return outputWidth == 1 && inputAWidth <= 1 && inputBWidth == 0;
  }
  if (opcode >= int32AddOpcode && opcode <= int32DivideOpcode) {
    return outputWidth == 1 && inputAWidth == 1 && inputBWidth == 1;
  }
  if (opcode == floatToInt32Opcode || opcode == int32ToFloatOpcode) {
    requiresA = true;
    return outputWidth == 1 && inputAWidth == 1 && inputBWidth == 0;
  }
  if (opcode == 1) {
    return outputWidth == 1 && inputAWidth <= 1 && inputBWidth == 0;
  }
  if (opcode == 2) {
    return outputWidth == 1 && inputAWidth == 0 && inputBWidth == 0;
  }
  if (opcode >= 3 && opcode <= 6) {
    return outputWidth == 1 && inputAWidth == 1 && inputBWidth == 1;
  }
  if (opcode >= 7 && opcode <= 10) {
    auto expected = opcode == 7 ? 2 : opcode == 8 ? 3 : 4;
    return outputWidth == expected &&
           (inputAWidth == 0 || inputAWidth == expected) && inputBWidth == 0;
  }
  if (opcode >= 11 && opcode <= 22) {
    auto expected = opcode <= 14 ? 2 : opcode <= 18 ? 3 : 4;
    return outputWidth == expected && inputAWidth == expected &&
           inputBWidth == expected;
  }
  if (opcode >= 23 && opcode <= 25) {
    requiresA = true;
    auto expected = opcode == 23 ? 2 : opcode == 24 ? 3 : 4;
    return outputWidth == expected && inputAWidth == 1 && inputBWidth == 0;
  }
  if (opcode >= 26 && opcode <= 28) {
    requiresA = true;
    auto expected = opcode == 26 ? 2 : opcode == 27 ? 3 : 4;
    return outputWidth == expected && inputAWidth == expected &&
           inputBWidth == 0;
  }
  if (opcode >= 29 && opcode <= 34) {
    requiresA = true;
    auto expectedInput = opcode <= 30 ? 2 : opcode <= 32 ? 3 : 4;
    auto expectedOutput = opcode == 29 || opcode == 34   ? 3
                          : opcode == 30 || opcode == 32 ? 4
                                                         : 2;
    return outputWidth == expectedOutput && inputAWidth == expectedInput &&
           inputBWidth == 0;
  }
  if (opcode >= 35 && opcode <= 37) {
    requiresA = true;
    auto expected = opcode == 35 ? 2 : opcode == 36 ? 3 : 4;
    return outputWidth == expected && inputAWidth == 4 && inputBWidth == 0;
  }
  if (opcode == 38 || opcode == 39) {
    requiresA = true;
    return outputWidth == 4 && inputAWidth == 4 && inputBWidth == 0;
  }
  return false;
}

inline auto validTypedInstruction(const std::uint8_t *current,
                                  std::uint32_t index) noexcept -> bool {
  auto opcode = current[16];
  auto outputWidth = current[17];
  auto inputAWidth = current[18];
  auto inputBWidth = current[19];
  auto inputA = read32(current + 24);
  auto inputB = read32(current + 28);
  auto typeFlags = current[20];
  auto inputASourceComponent = current[21];
  auto inputBSourceComponent = current[22];
  auto shapeAWidth = inputA == noInput ? std::uint8_t{0} : inputAWidth;
  auto shapeBWidth = inputB == noInput ? std::uint8_t{0} : inputBWidth;
  if (opcode >= 3 && opcode <= 6) {
    if (inputA == noInput) {
      shapeAWidth = 1;
    }
    if (inputB == noInput) {
      shapeBWidth = 1;
    }
  }
  if (opcode >= 11 && opcode <= 22) {
    if (inputA == noInput) {
      shapeAWidth = outputWidth;
    }
    if (inputB == noInput) {
      shapeBWidth = outputWidth;
    }
  }
  auto requiresA = false;
  auto requiresB = false;
  if (outputWidth < 1 || outputWidth > 4 ||
      !typedInputWidth(inputA, inputAWidth) ||
      !typedInputWidth(inputB, inputBWidth) || !validIndex(inputA, index) ||
      !validIndex(inputB, index) ||
      !requiredInputWidths(opcode, outputWidth, shapeAWidth, shapeBWidth,
                           requiresA, requiresB) ||
      (requiresA && inputA == noInput) || (requiresB && inputB == noInput) ||
      (typeFlags & 0xf8) != 0 || current[23] != 0 ||
      inputASourceComponent > 3 || inputBSourceComponent > 3 ||
      (inputA == noInput && inputASourceComponent != 0) ||
      (inputB == noInput && inputBSourceComponent != 0)) {
    return false;
  }
  auto outputIsColor = (typeFlags & 1) != 0;
  auto inputAIsColor = (typeFlags & 2) != 0;
  auto inputBIsColor = (typeFlags & 4) != 0;
  auto expectedInputAColor =
      opcode == 38 || (opcode == 10 && inputA != noInput);
  if (outputIsColor != (opcode == 10 || opcode == 39) ||
      inputAIsColor != expectedInputAColor || inputBIsColor ||
      (inputA == noInput && inputAIsColor) ||
      (inputB == noInput && inputBIsColor)) {
    return false;
  }
  for (auto lane = 0u; lane < 4; ++lane) {
    if (!std::isfinite(readDouble(current + 32 + lane * 8)) ||
        !std::isfinite(readDouble(current + 64 + lane * 8))) {
      return false;
    }
  }
  if (opcode >= 35 && opcode <= 37) {
    for (auto lane = 0u; lane < outputWidth; ++lane) {
      auto channel = readDouble(current + 32 + lane * 8);
      if (channel < 0 || channel >= 4 || std::floor(channel) != channel) {
        return false;
      }
    }
  }
  if (opcode == 10) {
    for (auto lane = 0u; lane < 4; ++lane) {
      auto channel = readDouble(current + 32 + lane * 8);
      if (channel < 0 || (lane == 3 && channel > 1)) {
        return false;
      }
    }
  }
  return true;
}

inline auto valueTypeWidth(std::uint8_t type) noexcept -> std::uint8_t {
  switch (type) {
  case valueTypeFloat:
  case valueTypeInt32:
  case valueTypeEnum:
    return 1;
  case valueTypeVector2:
    return 2;
  case valueTypeVector3:
    return 3;
  case valueTypeVector4:
  case valueTypeColor:
    return 4;
  default:
    return 0;
  }
}

inline auto typeForShape(std::uint8_t width, bool color) noexcept
    -> std::uint8_t {
  if (color) {
    return width == 4 ? valueTypeColor : 0;
  }
  switch (width) {
  case 1:
    return valueTypeFloat;
  case 2:
    return valueTypeVector2;
  case 3:
    return valueTypeVector3;
  case 4:
    return valueTypeVector4;
  default:
    return 0;
  }
}

inline auto valueTypeRecord(const std::uint8_t *data, std::size_t offset,
                            std::uint32_t index) noexcept
    -> const std::uint8_t * {
  return data + offset + valueTypeHeaderSize +
         std::size_t{index} * valueTypeRecordSize;
}

inline auto validValueTypeMetadata(const std::uint8_t *data,
                                   std::size_t binarySize, std::size_t offset,
                                   std::uint32_t instructionCount) noexcept
    -> bool {
  if (offset > binarySize || binarySize - offset < valueTypeHeaderSize ||
      std::memcmp(data + offset, "LFTY", 4) != 0 ||
      read16(data + offset + 4) != 1 ||
      read16(data + offset + 6) != valueTypeRecordSize ||
      std::size_t{instructionCount} >
          (binarySize - offset - valueTypeHeaderSize) / valueTypeRecordSize ||
      offset + valueTypeHeaderSize +
              std::size_t{instructionCount} * valueTypeRecordSize !=
          binarySize) {
    return false;
  }

  auto expectedInputs =
      [&](std::uint8_t opcode, std::uint8_t outputType,
          std::uint8_t outputWidth, std::uint8_t &count,
          std::array<std::uint8_t, 4> &types) noexcept -> bool {
    count = 0;
    types = {};
    auto set = [&](std::uint8_t input, std::uint8_t type) noexcept {
      if (input >= types.size() || type == 0) {
        return false;
      }
      types[input] = type;
      count = static_cast<std::uint8_t>(input + 1);
      return true;
    };
    if (opcode == 1 || (opcode >= 7 && opcode <= 10) || opcode == int32Opcode) {
      return set(0, outputType);
    }
    if (opcode == 2 || opcode == curveOpcode || opcode == branchTimeOpcode ||
        opcode == currentPhaseOpcode || opcode == phaseTimeOpcode) {
      return true;
    }
    if ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22) ||
        (opcode >= int32AddOpcode && opcode <= int32DivideOpcode)) {
      return set(0, outputType) && set(1, outputType);
    }
    if (opcode >= 23 && opcode <= 25) {
      return set(0, valueTypeFloat);
    }
    if (opcode >= 26 && opcode <= 28) {
      return set(0, outputType);
    }
    if (opcode >= 29 && opcode <= 34) {
      auto inputType = opcode <= 30   ? valueTypeVector2
                       : opcode <= 32 ? valueTypeVector3
                                      : valueTypeVector4;
      return set(0, inputType);
    }
    if (opcode >= 35 && opcode <= 37) {
      return set(0, valueTypeVector4);
    }
    if (opcode == 38) {
      return set(0, valueTypeColor);
    }
    if (opcode == 39) {
      return set(0, valueTypeVector4);
    }
    if (opcode == enumValueOpcode) {
      return set(0, valueTypeEnum);
    }
    if (opcode == enumSelectOpcode) {
      return set(0, valueTypeEnum);
    }
    if (opcode == floatToInt32Opcode) {
      return set(0, valueTypeFloat);
    }
    if (opcode == int32ToFloatOpcode) {
      return set(0, valueTypeInt32);
    }
    return opcode == customParameterOpcode || opcode == customSignalOpcode ||
           opcode == logicalEventSignalOpcode;
  };

  for (auto index = 0u; index < instructionCount; ++index) {
    auto current = instruction(data, 10, index);
    auto record = valueTypeRecord(data, offset, index);
    auto opcode = current[16];
    auto outputType = record[0];
    auto inputCount = record[1];
    auto outputWidth = current[17];
    if (inputCount > 4 || record[6] != 0 || record[7] != 0) {
      return false;
    }
    auto isCustom =
        opcode == customParameterOpcode || opcode == customSignalOpcode;
    auto isLogical = opcode == logicalEventSignalOpcode;
    if (isCustom) {
      auto expectedOutput =
          opcode == customSignalOpcode
              ? 0
              : typeForShape(outputWidth, (current[20] & 1) != 0);
      if (outputType != expectedOutput || inputCount != current[18]) {
        return false;
      }
    } else if (isLogical) {
      if (outputType != typeForShape(outputWidth, false) ||
          inputCount != current[18]) {
        return false;
      }
    } else if (opcode == enumSelectOpcode) {
      if (valueTypeWidth(outputType) != outputWidth ||
          outputType == valueTypeEnum || outputType == 0 || inputCount != 1 ||
          record[2] != valueTypeEnum) {
        return false;
      }
    } else {
      std::uint8_t requiredCount = 0;
      auto requiredTypes = std::array<std::uint8_t, 4>{};
      if (!expectedInputs(opcode, outputType, outputWidth, requiredCount,
                          requiredTypes) ||
          inputCount != requiredCount) {
        return false;
      }
      auto requiredOutput =
          opcode == 1 || opcode == 2 || (opcode >= 3 && opcode <= 6) ||
                  opcode == curveOpcode || opcode == branchTimeOpcode ||
                  opcode == phaseTimeOpcode || opcode == int32ToFloatOpcode
              ? valueTypeFloat
          : opcode == int32Opcode ||
                  (opcode >= int32AddOpcode && opcode <= int32DivideOpcode) ||
                  opcode == floatToInt32Opcode
              ? valueTypeInt32
          : isEnumSourceOpcode(opcode) ? valueTypeEnum
          : opcode == 10               ? valueTypeColor
          : opcode == 7 || opcode == 11 || opcode == 12 || opcode == 13 ||
                  opcode == 14 || opcode == 26 || opcode == 33 || opcode == 35
              ? valueTypeVector2
          : opcode == 8 || (opcode >= 15 && opcode <= 18) || opcode == 27 ||
                  opcode == 29 || opcode == 34 || opcode == 36
              ? valueTypeVector3
          : opcode == 9 || (opcode >= 19 && opcode <= 22) || opcode == 28 ||
                  opcode == 30 || opcode == 32 || opcode == 37 || opcode == 38
              ? valueTypeVector4
          : opcode == 39 ? valueTypeColor
          : opcode == 23 ? valueTypeVector2
          : opcode == 24 ? valueTypeVector3
          : opcode == 25 ? valueTypeVector4
          : opcode == 31 ? valueTypeVector2
          : opcode == 40 ? outputType
                         : 0;
      if (outputType != requiredOutput) {
        return false;
      }
      if (valueTypeWidth(outputType) != outputWidth ||
          (((current[20] & 1) != 0) != (outputType == valueTypeColor))) {
        return false;
      }
      for (auto input = 0u; input < requiredCount; ++input) {
        if (record[2 + input] != requiredTypes[input]) {
          return false;
        }
      }
    }

    for (auto input = 0u; input < inputCount; ++input) {
      auto type = record[2 + input];
      auto width = valueTypeWidth(type);
      if (width == 0) {
        return false;
      }
      if (isCustom || isLogical) {
        auto inputRecord = current + 48 + input * 8;
        auto sourceIndex = read32(inputRecord);
        auto sourceWidth = inputRecord[4];
        auto color = inputRecord[6] != 0;
        if (type !=
                typeForShape(sourceWidth == 0 ? width : sourceWidth, color) ||
            (isLogical && (type == valueTypeColor || type == valueTypeEnum))) {
          return false;
        }
        if (sourceIndex != noInput) {
          if (sourceIndex >= index) {
            return false;
          }
          auto sourceOpcode = instruction(data, 10, sourceIndex)[16];
          auto sourceType = sourceOpcode >= 26 && sourceOpcode <= 28
                                ? valueTypeFloat
                                : valueTypeRecord(data, offset, sourceIndex)[0];
          if (sourceType != type) {
            return false;
          }
        }
        auto defaults = current + 80 + input * 32;
        for (auto lane = 0u; lane < width; ++lane) {
          auto value = readDouble(defaults + lane * 8);
          if (!std::isfinite(value) ||
              (type == valueTypeInt32 && !validInt32(value))) {
            return false;
          }
        }
      } else {
        auto sourceIndex = input == 0   ? read32(current + 24)
                           : input == 1 ? read32(current + 28)
                                        : noInput;
        auto sourceComponent = input == 0   ? current[21]
                               : input == 1 ? current[22]
                                            : 0;
        if (sourceIndex != noInput) {
          if (sourceIndex >= index) {
            return false;
          }
          auto source = instruction(data, 10, sourceIndex);
          auto sourceType = valueTypeRecord(data, offset, sourceIndex)[0];
          if (source[16] >= 26 && source[16] <= 28) {
            sourceType = valueTypeFloat;
          }
          if (sourceType != type) {
            return false;
          }
        }
        auto defaults = current + (input == 0 ? 32 : 64);
        for (auto lane = 0u; lane < width; ++lane) {
          auto value = readDouble(defaults + lane * 8);
          if (!std::isfinite(value) ||
              (type == valueTypeInt32 && !validInt32(value))) {
            return false;
          }
        }
        if (type == valueTypeEnum && sourceIndex != noInput) {
          auto source = instruction(data, 10, sourceIndex);
          if (!isEnumSourceOpcode(source[16]) ||
              std::memcmp(current + 96, source + 96, 16) != 0) {
            return false;
          }
        }
        if (sourceIndex == noInput && input == 0 &&
            current[16] == floatToInt32Opcode) {
          // This conversion is intentionally connection-only; it must not
          // turn an implicit Float default into an Int32 value.
          return false;
        }
        (void)sourceComponent;
      }
    }
    for (auto input = inputCount; input < 4; ++input) {
      if (record[2 + input] != 0) {
        return false;
      }
    }
  }
  return true;
}

inline auto validEnumSelectValueTypes(const std::uint8_t *data,
                                      std::size_t valueTypeOffset,
                                      std::size_t enumMetadataOffset,
                                      std::size_t selectRecordsOffset,
                                      std::uint16_t selectCount,
                                      std::uint32_t instructionCount) noexcept
    -> bool {
  for (auto index = 0u; index < selectCount; ++index) {
    auto record =
        data + selectRecordsOffset + std::size_t{index} * enumSelectRecordSize;
    auto nodeIndex = read32(record);
    if (nodeIndex >= instructionCount) {
      return false;
    }
    auto outputType = valueTypeRecord(data, valueTypeOffset, nodeIndex)[0];
    auto defaultSource = read32(record + 8);
    if (defaultSource != noInput &&
        (defaultSource >= nodeIndex ||
         valueTypeRecord(data, valueTypeOffset, defaultSource)[0] !=
             outputType)) {
      return false;
    }
    auto outputWidth = record[28];
    if (outputType == valueTypeInt32) {
      for (auto lane = 0u; lane < outputWidth; ++lane) {
        if (!validInt32(readDouble(record + 36 + lane * 8))) {
          return false;
        }
      }
    }

    auto caseCount = record[29];
    auto casesOffset = read32(record + 32);
    for (auto caseIndex = 0u; caseIndex < caseCount; ++caseIndex) {
      auto branch = data + enumMetadataOffset + casesOffset +
                    std::size_t{caseIndex} * enumCaseRecordSize;
      auto source = read32(branch + 4);
      if (source != noInput &&
          (source >= nodeIndex ||
           valueTypeRecord(data, valueTypeOffset, source)[0] != outputType)) {
        return false;
      }
      if (outputType == valueTypeInt32) {
        for (auto lane = 0u; lane < outputWidth; ++lane) {
          if (!validInt32(readDouble(branch + 8 + lane * 8))) {
            return false;
          }
        }
      }
    }
  }
  return true;
}

inline auto validCurveInstruction(const std::uint8_t *current,
                                  std::uint16_t /*version*/) noexcept -> bool {
  if (current[16] != curveOpcode || current[17] != 1 || current[18] != 0 ||
      current[19] != 0 || current[20] != 0 || current[21] != 0 ||
      current[22] != 0 || current[23] != 0 || read32(current + 24) != noInput ||
      read32(current + 28) != noInput) {
    return false;
  }
  for (auto lane = 0u; lane < 4; ++lane) {
    if (readDouble(current + 32 + lane * 8) != 0 ||
        readDouble(current + 64 + lane * 8) != 0) {
      return false;
    }
  }
  for (auto byte = typedInstructionSize; byte < 224; ++byte) {
    if (!(byte >= 112 && byte < 116) && current[byte] != 0) {
      return false;
    }
  }
  for (auto byte = 232u; byte < customInstructionSize; ++byte) {
    if (current[byte] != 0) {
      return false;
    }
  }
  return true;
}

inline auto validCurveKey(const std::uint8_t *key) noexcept -> bool {
  auto time = readDouble(key);
  auto value = readDouble(key + 8);
  auto inSlope = readDouble(key + 16);
  auto outSlope = readDouble(key + 24);
  if (!std::isfinite(time) || std::abs(time) > 1'000'000 ||
      !std::isfinite(value) || std::abs(value) > 1'000'000 ||
      !std::isfinite(inSlope) || std::abs(inSlope) > 1'000'000 ||
      !std::isfinite(outSlope) || std::abs(outSlope) > 1'000'000 ||
      key[32] > 2) {
    return false;
  }
  for (auto byte = 33u; byte < curveKeySize; ++byte) {
    if (key[byte] != 0) {
      return false;
    }
  }
  return true;
}

inline auto evaluateCurve(const std::uint8_t *keys, std::uint32_t count,
                          double time) noexcept -> double {
  if (count == 0) {
    return 0;
  }
  auto keyAt = [&](std::uint32_t index) noexcept {
    return keys + std::size_t{index} * curveKeySize;
  };
  auto first = keyAt(0);
  auto last = keyAt(count - 1);
  if (time <= readDouble(first)) {
    return readDouble(first + 8);
  }
  if (time >= readDouble(last)) {
    return readDouble(last + 8);
  }
  auto low = 0u;
  auto high = count - 1;
  while (high - low > 1) {
    auto middle = low + (high - low) / 2;
    if (time <= readDouble(keyAt(middle))) {
      high = middle;
    } else {
      low = middle;
    }
  }
  auto left = keyAt(low);
  auto right = keyAt(high);
  auto leftTime = readDouble(left);
  auto rightTime = readDouble(right);
  auto leftValue = readDouble(left + 8);
  auto rightValue = readDouble(right + 8);
  auto span = rightTime - leftTime;
  auto t = (time - leftTime) / span;
  switch (left[32]) {
  case 0:
    return leftValue;
  case 1:
    return leftValue + (rightValue - leftValue) * t;
  case 2:
    return (2 * t * t * t - 3 * t * t + 1) * leftValue +
           (t * t * t - 2 * t * t + t) * span * readDouble(left + 24) +
           (-2 * t * t * t + 3 * t * t) * rightValue +
           (t * t * t - t * t) * span * readDouble(right + 16);
  default:
    return 0;
  }
}

inline auto evaluateCurve(const CurveKey *keys, std::size_t count,
                          double time) noexcept -> double {
  if (count == 0) {
    return 0;
  }
  if (time <= keys[0].time) {
    return keys[0].value;
  }
  if (time >= keys[count - 1].time) {
    return keys[count - 1].value;
  }
  auto low = std::size_t{};
  auto high = count - 1;
  while (high - low > 1) {
    auto middle = low + (high - low) / 2;
    if (time <= keys[middle].time) {
      high = middle;
    } else {
      low = middle;
    }
  }
  const auto &left = keys[low];
  const auto &right = keys[high];
  auto span = right.time - left.time;
  auto t = (time - left.time) / span;
  switch (left.interpolation) {
  case 0:
    return left.value;
  case 1:
    return left.value + (right.value - left.value) * t;
  case 2:
    return (2 * t * t * t - 3 * t * t + 1) * left.value +
           (t * t * t - 2 * t * t + t) * span * left.outSlope +
           (-2 * t * t * t + 3 * t * t) * right.value +
           (t * t * t - t * t) * span * right.inSlope;
  default:
    return 0;
  }
}

inline auto rangesOverlap(const void *first, std::size_t firstSize,
                          const void *second, std::size_t secondSize) noexcept
    -> bool {
  if (firstSize == 0 || secondSize == 0) {
    return false;
  }
  auto firstStart = reinterpret_cast<std::uintptr_t>(first);
  auto secondStart = reinterpret_cast<std::uintptr_t>(second);
  if (firstSize > std::numeric_limits<std::uintptr_t>::max() - firstStart ||
      secondSize > std::numeric_limits<std::uintptr_t>::max() - secondStart) {
    return true;
  }
  return firstStart < secondStart + secondSize &&
         secondStart < firstStart + firstSize;
}
} // namespace liberafx::vfx_runtime_detail
