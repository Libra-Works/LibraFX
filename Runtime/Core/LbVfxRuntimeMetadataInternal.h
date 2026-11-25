#pragma once

#include "LbVfxRuntimeBinaryInternal.h"

namespace liberafx::vfx_runtime_detail {
inline auto validUtf8(const std::uint8_t *text, std::size_t length) noexcept
    -> bool {
  for (auto index = std::size_t{}; index < length;) {
    auto first = text[index++];
    auto codePoint = std::uint32_t{};
    auto continuationCount = 0u;
    if (first < 0x80) {
      codePoint = first;
    } else if (first >= 0xc2 && first <= 0xdf) {
      codePoint = first & 0x1f;
      continuationCount = 1;
    } else if (first >= 0xe0 && first <= 0xef) {
      codePoint = first & 0x0f;
      continuationCount = 2;
    } else if (first >= 0xf0 && first <= 0xf4) {
      codePoint = first & 0x07;
      continuationCount = 3;
    } else {
      return false;
    }
    if (continuationCount > length - index) {
      return false;
    }
    for (auto count = 0u; count < continuationCount; ++count) {
      auto next = text[index++];
      if ((next & 0xc0) != 0x80) {
        return false;
      }
      codePoint = (codePoint << 6) | (next & 0x3f);
    }
    if ((continuationCount == 1 && codePoint < 0x80) ||
        (continuationCount == 2 && codePoint < 0x800) ||
        (continuationCount == 3 && codePoint < 0x10000) ||
        codePoint > 0x10ffff || (codePoint >= 0xd800 && codePoint <= 0xdfff) ||
        codePoint <= 0x1f || (codePoint >= 0x7f && codePoint <= 0x9f)) {
      return false;
    }
  }
  return true;
}

inline auto validGameIoMetadata(const std::uint8_t *data, std::size_t size,
                                std::size_t offset,
                                std::uint32_t instructionCount,
                                std::uint16_t /*version*/,
                                std::size_t &metadataEnd) noexcept -> bool {
  constexpr auto headerSize = std::size_t{8};
  constexpr auto currentRecordSize = std::size_t{72};
  if (offset > size || size - offset < headerSize ||
      std::memcmp(data + offset, "LFIO", 4) != 0 ||
      read16(data + offset + 6) != 0) {
    return false;
  }
  auto portCount = read16(data + offset + 4);
  if (portCount == 0 || portCount > 256) {
    return false;
  }
  auto cursor = offset + headerSize;
  for (auto port = std::uint16_t{}; port < portCount; ++port) {
    if (cursor > size || size - cursor < currentRecordSize) {
      return false;
    }
    auto record = data + cursor;
    auto kind = record[64];
    auto width = record[65];
    auto isColor = record[66];
    auto bindingCount = record[67];
    auto nameLength = read16(record + 68);
    auto matrix = width == 16;
    if (kind > 4 || isColor > 1 || nameLength == 0 ||
        nameLength > 128 || read16(record + 70) != 0 ||
        bindingCount != (matrix ? 4 : 1) ||
        (kind == 1 || kind == 3
             ? width != 0 || isColor != 0 || bindingCount != 1
             : (matrix ? kind == 1 || kind == 3 || isColor != 0
                       : width < 1 || width > 4 || (isColor && width != 4)) ||
                   (kind == 4 && !matrix))) {
      return false;
    }
    auto nameOffset = cursor + currentRecordSize;
    if (nameOffset > size || size - nameOffset < std::size_t{nameLength} + 1 ||
        data[nameOffset + nameLength] != 0 || data[nameOffset] == ' ' ||
        data[nameOffset + nameLength - 1] == ' ' ||
        !validUtf8(data + nameOffset, nameLength)) {
      return false;
    }
    for (auto row = 0u; row < bindingCount; ++row) {
      auto nodeId = record + row * 16;
      auto emptyId = true;
      for (auto byte = 0u; byte < 16; ++byte) {
        emptyId = emptyId && nodeId[byte] == 0;
      }
      if (emptyId) {
        return false;
      }
      auto foundNode = false;
      for (auto index = 0u; index < instructionCount; ++index) {
        auto current = instruction(data, 3, index);
        if (std::memcmp(current, nodeId, 16) != 0) {
          continue;
        }
        auto rowWidth = matrix ? std::uint8_t{4} : width;
        if ((kind == 1 || kind == 3)
                ? (current[16] != customSignalOpcode &&
                   (current[16] != logicalEventSignalOpcode ||
                    current[19] == 0))
                : current[17] != rowWidth ||
                      ((current[20] & 1) != 0) != (isColor != 0)) {
          return false;
        }
        foundNode = true;
        break;
      }
      if (!foundNode) {
        return false;
      }
      for (auto earlierRow = 0u; earlierRow < row; ++earlierRow) {
        if (std::memcmp(nodeId, record + earlierRow * 16, 16) == 0) {
          return false;
        }
      }
    }
    for (auto previous = offset + headerSize; previous < cursor;) {
      auto previousRecord = data + previous;
      auto previousKind = previousRecord[64];
      auto previousBindingCount = previousRecord[67];
      auto previousNameLength = read16(previousRecord + 68);
      auto duplicateName = previousNameLength == nameLength &&
                           std::memcmp(data + previous + currentRecordSize,
                                       data + nameOffset, nameLength) == 0;
      if (duplicateName && kind != 4 && previousKind != 4) {
        return false;
      }
      for (auto row = 0u; row < bindingCount; ++row) {
        for (auto previousRow = 0u; previousRow < previousBindingCount;
             ++previousRow) {
          if (std::memcmp(record + row * 16,
                          previousRecord + previousRow * 16, 16) == 0 &&
              kind != 2 && previousKind != 2) {
            return false;
          }
        }
      }
      previous += currentRecordSize + previousNameLength + 1;
    }
    cursor = nameOffset + nameLength + 1;
  }
  metadataEnd = cursor;
  return cursor <= size;
}

inline auto validEnumBranchMetadata(
    const std::uint8_t *data, std::size_t size, std::size_t offset,
    std::uint16_t version, std::uint32_t instructionCount,
    std::size_t &metadataEnd, std::size_t &selectRecordsOffset,
    std::size_t &guardRecordsOffset, std::size_t &guardValuesOffset,
    std::uint16_t &enumValueCount, std::uint16_t &enumSelectCount,
    std::uint16_t &guardCount) noexcept -> bool {
  if (offset > size || size - offset < enumBranchHeaderSize ||
      std::memcmp(data + offset, "LFEB", 4) != 0 ||
      read16(data + offset + 4) != 1) {
    return false;
  }
  guardCount = read16(data + offset + 6);
  enumValueCount = read16(data + offset + 8);
  enumSelectCount = read16(data + offset + 10);
  auto declaredSize = read32(data + offset + 12);
  if (declaredSize < enumBranchHeaderSize || declaredSize > size - offset ||
      guardCount > 255) {
    return false;
  }
  metadataEnd = offset + declaredSize;
  auto valuesOffset = offset + enumBranchHeaderSize;
  if (std::size_t{enumValueCount} >
      (declaredSize - enumBranchHeaderSize) / enumValueRecordSize) {
    return false;
  }
  selectRecordsOffset =
      valuesOffset + std::size_t{enumValueCount} * enumValueRecordSize;
  if (std::size_t{enumSelectCount} >
      (metadataEnd - selectRecordsOffset) / enumSelectRecordSize) {
    return false;
  }
  guardRecordsOffset =
      selectRecordsOffset + std::size_t{enumSelectCount} * enumSelectRecordSize;
  if (std::size_t{guardCount} >
      (metadataEnd - guardRecordsOffset) / enumGuardRecordSize) {
    return false;
  }
  guardValuesOffset =
      guardRecordsOffset + std::size_t{guardCount} * enumGuardRecordSize;
  auto caseCursor = guardValuesOffset;
  for (auto index = 0u; index < enumValueCount; ++index) {
    auto record =
        data + valuesOffset + std::size_t{index} * enumValueRecordSize;
    auto nodeIndex = read32(record);
    if (nodeIndex >= instructionCount) {
      return false;
    }
    auto current = instruction(data, 3, nodeIndex);
    if (!isEnumSourceOpcode(current[16]) ||
        std::memcmp(current + 96, record + 4, 16) != 0) {
      return false;
    }
    auto sourceIndex = read32(current + 24);
    if (sourceIndex != noInput) {
      if (sourceIndex >= nodeIndex) {
        return false;
      }
      auto source = instruction(data, 3, sourceIndex);
      if (!isEnumSourceOpcode(source[16]) ||
          std::memcmp(source + 96, record + 4, 16) != 0) {
        return false;
      }
    }
    for (auto prior = 0u; prior < index; ++prior) {
      if (read32(data + valuesOffset +
                 std::size_t{prior} * enumValueRecordSize) == nodeIndex) {
        return false;
      }
    }
  }
  for (auto index = 0u; index < enumSelectCount; ++index) {
    auto record =
        data + selectRecordsOffset + std::size_t{index} * enumSelectRecordSize;
    auto nodeIndex = read32(record);
    auto selectorIndex = read32(record + 4);
    auto defaultSource = read32(record + 8);
    auto outputWidth = record[28];
    auto caseCount = record[29];
    auto casesOffset = read32(record + 32);
    if (nodeIndex >= instructionCount || selectorIndex >= nodeIndex ||
        (defaultSource != noInput &&
         (defaultSource >= nodeIndex ||
          instruction(data, 3, defaultSource)[17] != outputWidth)) ||
        outputWidth < 1 || outputWidth > 4 || caseCount > 32 ||
        offset + casesOffset != caseCursor ||
        std::size_t{caseCount} >
            (metadataEnd - caseCursor) / enumCaseRecordSize) {
      return false;
    }
    auto current = instruction(data, 3, nodeIndex);
    auto selector = instruction(data, 3, selectorIndex);
    if (current[16] != enumSelectOpcode || current[17] != outputWidth ||
        read32(current + 24) != selectorIndex ||
        !isEnumSourceOpcode(selector[16]) ||
        std::memcmp(current + 96, record + 12, 16) != 0 ||
        std::memcmp(selector + 96, record + 12, 16) != 0) {
      return false;
    }
    for (auto prior = 0u; prior < index; ++prior) {
      if (read32(data + selectRecordsOffset +
                 std::size_t{prior} * enumSelectRecordSize) == nodeIndex) {
        return false;
      }
    }
    for (auto lane = 0u; lane < outputWidth; ++lane) {
      if (!std::isfinite(readDouble(record + 36 + lane * 8))) {
        return false;
      }
    }
    for (auto caseIndex = 0u; caseIndex < caseCount; ++caseIndex) {
      auto branch =
          data + caseCursor + std::size_t{caseIndex} * enumCaseRecordSize;
      auto source = read32(branch + 4);
      for (auto lane = 0u; lane < outputWidth; ++lane) {
        if (!std::isfinite(readDouble(branch + 8 + lane * 8))) {
          return false;
        }
      }
      if (source != noInput &&
          (source >= nodeIndex ||
           instruction(data, 3, source)[17] != outputWidth)) {
        return false;
      }
      for (auto prior = 0u; prior < caseIndex; ++prior) {
        if (read32(data + caseCursor +
                   std::size_t{prior} * enumCaseRecordSize) == read32(branch)) {
          return false;
        }
      }
    }
    caseCursor += std::size_t{caseCount} * enumCaseRecordSize;
  }
  auto guardDataCursor = caseCursor;
  for (auto index = 0u; index < guardCount; ++index) {
    auto record =
        data + guardRecordsOffset + std::size_t{index} * enumGuardRecordSize;
    auto selectorIndex = read32(record);
    auto parent = read32(record + 20);
    auto flags = read32(record + 24);
    auto caseCount = read16(record + 28);
    auto excludedCount = read16(record + 30);
    auto guardCaseListOffset = read32(record + 32);
    auto excludedOffset = read32(record + 36);
    if (selectorIndex >= instructionCount ||
        (parent != noInput && parent >= index) || flags > 1 ||
        offset + guardCaseListOffset != guardDataCursor ||
        std::size_t{caseCount} > (metadataEnd - guardDataCursor) / 4 ||
        offset + excludedOffset !=
            guardDataCursor + std::size_t{caseCount} * 4 ||
        std::size_t{excludedCount} >
            (metadataEnd - (offset + excludedOffset)) / 4) {
      return false;
    }
    auto selector = instruction(data, 3, selectorIndex);
    if (!isEnumSourceOpcode(selector[16]) ||
        std::memcmp(selector + 96, record + 4, 16) != 0) {
      return false;
    }
    for (auto caseIndex = 0u; caseIndex < caseCount; ++caseIndex) {
      for (auto prior = 0u; prior < caseIndex; ++prior) {
        if (read32(data + offset + guardCaseListOffset +
                   std::size_t{caseIndex} * 4) ==
            read32(data + offset + guardCaseListOffset +
                   std::size_t{prior} * 4)) {
          return false;
        }
      }
    }
    for (auto caseIndex = 0u; caseIndex < excludedCount; ++caseIndex) {
      for (auto prior = 0u; prior < caseIndex; ++prior) {
        if (read32(data + offset + excludedOffset +
                   std::size_t{caseIndex} * 4) ==
            read32(data + offset + excludedOffset + std::size_t{prior} * 4)) {
          return false;
        }
      }
    }
    guardDataCursor = offset + excludedOffset + std::size_t{excludedCount} * 4;
    auto ancestor = read32(record + 20);
    auto depth = 1u;
    while (ancestor != noInput) {
      if (++depth > 64 || ancestor >= index) {
        return false;
      }
      ancestor = read32(data + guardRecordsOffset +
                        std::size_t{ancestor} * enumGuardRecordSize + 20);
    }
  }
  if (guardDataCursor != metadataEnd) {
    return false;
  }
  auto observedEnumValues = std::uint16_t{0};
  auto observedEnumSelects = std::uint16_t{0};
  for (auto index = 0u; index < instructionCount; ++index) {
    auto current = instruction(data, 3, index);
    auto opcode = current[16];
    if (isEnumSourceOpcode(opcode)) {
      ++observedEnumValues;
    }
    if (opcode == enumSelectOpcode) {
      ++observedEnumSelects;
    }
    auto guardId = branchGuard(current, version);
    if (guardId != noInput && guardId >= guardCount) {
      return false;
    }
    if (opcode == branchTimeOpcode && guardId == noInput) {
      return false;
    }
  }
  return observedEnumValues == enumValueCount &&
         observedEnumSelects == enumSelectCount;
}

inline auto findLogicalDescriptor(const std::uint8_t *typeId,
                                  std::uint32_t abiVersion,
                                  bool &registeredType) noexcept
    -> const LogicalNodeDescriptor * {
  registeredType = false;
  auto count = std::size_t{0};
  auto descriptors = logicalNodes(count);
  for (auto index = std::size_t{0}; index < count; ++index) {
    if (std::memcmp(descriptors[index].typeId, typeId, 16) != 0) {
      continue;
    }
    registeredType = true;
    if (descriptors[index].abiVersion == abiVersion) {
      return &descriptors[index];
    }
  }
  return nullptr;
}

inline auto
validLogicalDescriptor(const LogicalNodeDescriptor &descriptor) noexcept
    -> bool {
  if (descriptor.abiVersion == 0 || descriptor.inputCount > customMaxPorts ||
      descriptor.parameterCount > customMaxPorts ||
      descriptor.signalInputCount > customMaxPorts ||
      descriptor.outputWidth < 1 || descriptor.outputWidth > 4 ||
      descriptor.signalBranchCount > customMaxPorts ||
      descriptor.update == nullptr ||
      !validStateAlignment(descriptor.stateAlignment) ||
      descriptor.stateBytes > (std::numeric_limits<std::size_t>::max() -
                               descriptor.stateAlignment + 1) ||
      (descriptor.signalInputCount != 0 && descriptor.onSignal == nullptr)) {
    return false;
  }
  for (auto input = 0u; input < descriptor.inputCount; ++input) {
    if (descriptor.inputWidths[input] < 1 ||
        descriptor.inputWidths[input] > 4) {
      return false;
    }
  }
  return true;
}

inline auto validLogicalConfiguration(const std::uint8_t *typeId,
                                      std::uint32_t configuration) noexcept
    -> bool {
  if (std::memcmp(typeId, logicalCompareTypeId.data(), 16) == 0) {
    auto operation = configuration & 0xffu;
    auto mode = (configuration >> 8u) & 0xffu;
    return (configuration & 0xffff0000u) == 0 && operation <= 5 && mode <= 1;
  }
  if (std::memcmp(typeId, logicalAndTypeId.data(), 16) == 0) {
    return (configuration & 0xffffff00u) == 0 && (configuration & 0xffu) <= 1;
  }
  if (std::memcmp(typeId, logicalOrTypeId.data(), 16) == 0) {
    return configuration == 0;
  }
  if (std::memcmp(typeId, logicalSignalToNumberTypeId.data(), 16) == 0) {
    return (configuration & 0xffffff00u) == 0 && (configuration & 0xffu) <= 2;
  }
  if (std::memcmp(typeId, logicalLoopClockTypeId.data(), 16) == 0) {
    return configuration == 0;
  }
  if (std::memcmp(typeId, logicalQualityBranchTypeId.data(), 16) == 0) {
    return (configuration & 0xffff0000u) == 0 && (configuration & 0xffu) <= 5 &&
           ((configuration >> 8u) & 0xffu) <= 4;
  }
  if (std::memcmp(typeId, logicalSignalCaptureTypeId.data(), 16) == 0) {
    return configuration == 0;
  }
  return false;
}

inline auto validLogicalEventMetadata(
    const std::uint8_t *data, std::size_t size, std::size_t offset,
    std::uint32_t instructionCount, std::size_t &metadataEnd,
    std::size_t &instructionRecordsOffset, std::size_t &signalRoutesOffset,
    std::uint16_t &logicalInstructionCount, std::uint32_t &signalRouteCount,
    std::size_t &logicalQueueCapacity) noexcept -> bool {
  if (offset > size || size - offset < logicalEventHeaderSize ||
      std::memcmp(data + offset, "LFLE", 4) != 0 ||
      read16(data + offset + 4) != 1) {
    return false;
  }
  logicalInstructionCount = read16(data + offset + 6);
  signalRouteCount = read32(data + offset + 8);
  auto declaredSize = read32(data + offset + 12);
  if (declaredSize < logicalEventHeaderSize || declaredSize > size - offset ||
      signalRouteCount > maxSignalRoutes ||
      std::size_t{logicalInstructionCount} >
          (declaredSize - logicalEventHeaderSize) /
              logicalInstructionRecordSize) {
    return false;
  }
  auto routesBytesOffset =
      logicalEventHeaderSize +
      std::size_t{logicalInstructionCount} * logicalInstructionRecordSize;
  if (std::size_t{signalRouteCount} >
          (declaredSize - routesBytesOffset) / logicalSignalRouteRecordSize ||
      routesBytesOffset +
              std::size_t{signalRouteCount} * logicalSignalRouteRecordSize !=
          declaredSize) {
    return false;
  }
  metadataEnd = offset + declaredSize;
  instructionRecordsOffset = offset + logicalEventHeaderSize;
  signalRoutesOffset =
      instructionRecordsOffset +
      std::size_t{logicalInstructionCount} * logicalInstructionRecordSize;

  auto signalInputCounts = std::array<std::uint8_t, maxNodes>{};
  signalInputCounts.fill(0xff);
  auto observedLogicalInstructions = std::uint16_t{0};
  for (auto index = std::uint32_t{0}; index < instructionCount; ++index) {
    observedLogicalInstructions +=
        instruction(data, 3, index)[16] == logicalEventSignalOpcode ? 1 : 0;
  }
  if (observedLogicalInstructions != logicalInstructionCount) {
    return false;
  }

  for (auto index = 0u; index < logicalInstructionCount; ++index) {
    auto record = data + instructionRecordsOffset +
                  std::size_t{index} * logicalInstructionRecordSize;
    auto nodeIndex = read32(record);
    auto abiVersion = read32(record + 20);
    auto configuration = read32(record + 24);
    if (nodeIndex >= instructionCount || signalInputCounts[nodeIndex] != 0xff) {
      return false;
    }
    auto current = instruction(data, 3, nodeIndex);
    if (current[16] != logicalEventSignalOpcode ||
        std::memcmp(current + 24, record + 4, 16) != 0 ||
        read32(current + 40) != abiVersion || current[18] != record[28] ||
        current[19] != record[31] || current[17] != record[30] ||
        record[28] > customMaxPorts || record[29] > customMaxPorts ||
        record[30] < 1 || record[30] > 4 || record[31] > customMaxPorts ||
        current[20] != 0) {
      return false;
    }
    auto registeredType = false;
    auto descriptor =
        findLogicalDescriptor(record + 4, abiVersion, registeredType);
    auto expectedParameterCount =
        std::memcmp(record + 4, logicalCompareTypeId.data(), 16) == 0 ||
                std::memcmp(record + 4, logicalQualityBranchTypeId.data(),
                            16) == 0
            ? 2
        : std::memcmp(record + 4, logicalOrTypeId.data(), 16) == 0 ||
                std::memcmp(record + 4, logicalLoopClockTypeId.data(), 16) ==
                    0 ||
                std::memcmp(record + 4, logicalSignalCaptureTypeId.data(),
                            16) == 0
            ? 0
        : std::memcmp(record + 4, logicalAndTypeId.data(), 16) == 0 ||
                std::memcmp(record + 4, logicalSignalToNumberTypeId.data(),
                            16) == 0
            ? 1
            : 0xff;
    if (descriptor == nullptr || !validLogicalDescriptor(*descriptor) ||
        descriptor->inputCount != record[28] ||
        descriptor->parameterCount != expectedParameterCount ||
        descriptor->signalInputCount != record[29] ||
        descriptor->outputWidth != record[30] ||
        descriptor->signalBranchCount != record[31] ||
        !validLogicalConfiguration(record + 4, configuration)) {
      return false;
    }
    auto emptyId = true;
    for (auto byte = 0u; byte < 16; ++byte) {
      emptyId = emptyId && record[4 + byte] == 0;
    }
    if (emptyId) {
      return false;
    }
    signalInputCounts[nodeIndex] = record[29];
    for (auto input = 0u; input < descriptor->inputCount; ++input) {
      auto inputRecord = current + 48 + input * 8;
      auto sourceIndex = read32(inputRecord);
      auto sourceWidth = inputRecord[4];
      auto selectedLane = inputRecord[5];
      auto isColor = inputRecord[6];
      if (sourceIndex != noInput &&
          (sourceIndex >= nodeIndex ||
           sourceWidth != descriptor->inputWidths[input] || selectedLane != 0 ||
           isColor != 0 ||
           instruction(data, 3, sourceIndex)[17] !=
               descriptor->inputWidths[input])) {
        return false;
      }
      if (sourceIndex == noInput &&
          (sourceWidth != 0 || selectedLane != 0 || isColor != 0)) {
        return false;
      }
    }
    for (auto prior = 0u; prior < index; ++prior) {
      if (read32(data + instructionRecordsOffset +
                 std::size_t{prior} * logicalInstructionRecordSize) ==
          nodeIndex) {
        return false;
      }
    }
  }

  auto previousSource = std::uint32_t{0};
  auto previousBranch = std::uint8_t{0};
  auto hasPrevious = false;
  for (auto index = 0u; index < signalRouteCount; ++index) {
    auto record = data + signalRoutesOffset +
                  std::size_t{index} * logicalSignalRouteRecordSize;
    auto sourceIndex = read32(record);
    auto branch = record[4];
    auto inputSlot = record[5];
    auto targetIndex = read32(record + 8);
    if (read16(record + 6) != 0 || sourceIndex >= instructionCount ||
        targetIndex >= instructionCount || targetIndex <= sourceIndex) {
      return false;
    }
    auto source = instruction(data, 3, sourceIndex);
    auto target = instruction(data, 3, targetIndex);
    if ((source[16] != customSignalOpcode &&
         source[16] != logicalEventSignalOpcode) ||
        branch >= source[19] ||
        (target[16] != customSignalOpcode &&
         target[16] != logicalEventSignalOpcode)) {
      return false;
    }
    if (target[16] == logicalEventSignalOpcode) {
      if (signalInputCounts[targetIndex] == 0xff ||
          inputSlot >= signalInputCounts[targetIndex]) {
        return false;
      }
    } else if (inputSlot != 0) {
      return false;
    }
    if (hasPrevious &&
        (sourceIndex < previousSource ||
         (sourceIndex == previousSource && branch < previousBranch))) {
      return false;
    }
    previousSource = sourceIndex;
    previousBranch = branch;
    hasPrevious = true;
  }
  auto eventCounts = std::array<std::uint64_t, maxNodes>{};
  auto maximumManualEvents = std::uint64_t{0};
  auto generatedEvents = std::uint64_t{0};
  auto routeCursor = std::size_t{signalRouteCount};
  for (auto sourceIndex = instructionCount; sourceIndex > 0; --sourceIndex) {
    auto nodeIndex = sourceIndex - 1;
    auto source = instruction(data, 3, nodeIndex);
    auto opcode = source[16];
    if (opcode != customSignalOpcode && opcode != logicalEventSignalOpcode) {
      continue;
    }
    auto branchCount = source[19];
    auto maximumBranchEvents = std::uint64_t{0};
    auto allBranchEvents = std::uint64_t{0};
    auto branchEventCounts = std::array<std::uint64_t, customMaxPorts>{};
    while (routeCursor != 0) {
      auto route = data + signalRoutesOffset +
                   (routeCursor - 1) * logicalSignalRouteRecordSize;
      auto routeSource = read32(route);
      if (routeSource < nodeIndex) {
        break;
      }
      if (routeSource != nodeIndex) {
        return false;
      }
      auto branch = route[4];
      auto targetIndex = read32(route + 8);
      if (branch >= branchCount ||
          eventCounts[targetIndex] >
              maxLogicalQueueEvents - branchEventCounts[branch]) {
        return false;
      }
      branchEventCounts[branch] += eventCounts[targetIndex];
      --routeCursor;
    }
    for (auto branch = 0u; branch < branchCount; ++branch) {
      auto branchEvents = branchEventCounts[branch];
      maximumBranchEvents = std::max(maximumBranchEvents, branchEvents);
      if (branchEvents > maxLogicalQueueEvents - allBranchEvents) {
        return false;
      }
      allBranchEvents += branchEvents;
    }
    auto childEvents =
        opcode == customSignalOpcode ? maximumBranchEvents : allBranchEvents;
    if (childEvents >= maxLogicalQueueEvents) {
      return false;
    }
    eventCounts[nodeIndex] = childEvents + 1;
    if (opcode == customSignalOpcode) {
      maximumManualEvents =
          std::max(maximumManualEvents, eventCounts[nodeIndex]);
    }
    if (opcode == logicalEventSignalOpcode &&
        std::memcmp(source + 24, logicalCompareTypeId.data(), 16) == 0) {
      generatedEvents += allBranchEvents;
    }
    if (generatedEvents > maxLogicalQueueEvents) {
      return false;
    }
  }
  if (routeCursor != 0) {
    return false;
  }
  auto requiredQueueCapacity = std::max(maximumManualEvents, generatedEvents);
  if (requiredQueueCapacity == 0) {
    requiredQueueCapacity = 1;
  }
  if (requiredQueueCapacity > maxLogicalQueueEvents) {
    return false;
  }
  logicalQueueCapacity = static_cast<std::size_t>(requiredQueueCapacity);
  return true;
}
} // namespace liberafx::vfx_runtime_detail
