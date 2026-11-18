#include "LbVfxRuntimeValueInternal.h"

namespace liberafx {
using namespace vfx_runtime_detail;
auto Instance::isBranchActive(std::uint32_t guardId) const noexcept -> bool {
  if (program_ == nullptr || program_->version_ < 8 || guardId == noInput) {
    return true;
  }
  auto chain = std::array<std::uint32_t, 64>{};
  auto depth = std::size_t{0};
  auto currentGuard = guardId;
  while (currentGuard != noInput) {
    if (depth == chain.size() || currentGuard >= program_->branchGuardCount_) {
      return false;
    }
    chain[depth++] = currentGuard;
    auto record = program_->bytes_ + program_->enumGuardRecordsOffset_ +
                  std::size_t{currentGuard} * enumGuardRecordSize;
    currentGuard = read32(record + 20);
  }
  while (depth != 0) {
    auto record = program_->bytes_ + program_->enumGuardRecordsOffset_ +
                  std::size_t{chain[--depth]} * enumGuardRecordSize;
    auto selector = read32(record);
    auto selected = work_[selector * 4];
    auto isInteger = std::isfinite(selected) &&
                     std::trunc(selected) == selected &&
                     selected >= std::numeric_limits<std::int32_t>::min() &&
                     selected <= std::numeric_limits<std::int32_t>::max();
    auto selectedValue = isInteger ? static_cast<std::int32_t>(selected) : 0;
    auto matched = false;
    auto caseCount = read16(record + 28);
    auto valuesOffset = read32(record + 32);
    for (auto index = 0u; index < caseCount; ++index) {
      matched =
          matched ||
          (isInteger &&
           readI32(program_->bytes_ + program_->enumBranchMetadataOffset_ +
                   valuesOffset + std::size_t{index} * 4) == selectedValue);
    }
    if ((read32(record + 24) & 1u) != 0) {
      auto excluded = false;
      auto excludedCount = read16(record + 30);
      auto excludedOffset = read32(record + 36);
      for (auto index = 0u; index < excludedCount; ++index) {
        excluded =
            excluded ||
            (isInteger &&
             readI32(program_->bytes_ + program_->enumBranchMetadataOffset_ +
                     excludedOffset + std::size_t{index} * 4) == selectedValue);
      }
      matched = matched || !excluded;
    }
    if (!matched) {
      return false;
    }
  }
  return true;
}

auto Instance::updateBranchClocks(std::uint32_t guardId, bool &active) noexcept
    -> Result {
  active = true;
  if (program_ == nullptr || program_->version_ < 8 || guardId == noInput) {
    return Result::Ok;
  }
  auto chain = std::array<std::uint32_t, 64>{};
  auto depth = std::size_t{0};
  auto currentGuard = guardId;
  while (currentGuard != noInput) {
    if (depth == chain.size() || currentGuard >= program_->branchGuardCount_) {
      return Result::InvalidBinary;
    }
    chain[depth++] = currentGuard;
    auto record = program_->bytes_ + program_->enumGuardRecordsOffset_ +
                  std::size_t{currentGuard} * enumGuardRecordSize;
    currentGuard = read32(record + 20);
  }
  auto globalTime = evaluatedTimeContext_ ? effectTime_ : time_;
  while (depth != 0) {
    auto guardIndex = chain[--depth];
    auto record = program_->bytes_ + program_->enumGuardRecordsOffset_ +
                  std::size_t{guardIndex} * enumGuardRecordSize;
    auto selector = read32(record);
    auto selected = work_[selector * 4];
    auto isInteger = std::isfinite(selected) &&
                     std::trunc(selected) == selected &&
                     selected >= std::numeric_limits<std::int32_t>::min() &&
                     selected <= std::numeric_limits<std::int32_t>::max();
    auto selectedValue = isInteger ? static_cast<std::int32_t>(selected) : 0;
    auto matched = false;
    auto caseCount = read16(record + 28);
    auto valuesOffset = read32(record + 32);
    for (auto index = 0u; index < caseCount; ++index) {
      matched =
          matched ||
          (isInteger &&
           readI32(program_->bytes_ + program_->enumBranchMetadataOffset_ +
                   valuesOffset + std::size_t{index} * 4) == selectedValue);
    }
    if ((read32(record + 24) & 1u) != 0) {
      auto excluded = false;
      auto excludedCount = read16(record + 30);
      auto excludedOffset = read32(record + 36);
      for (auto index = 0u; index < excludedCount; ++index) {
        excluded =
            excluded ||
            (isInteger &&
             readI32(program_->bytes_ + program_->enumBranchMetadataOffset_ +
                     excludedOffset + std::size_t{index} * 4) == selectedValue);
      }
      matched = matched || !excluded;
    }
    active = active && matched;
    auto clock = reinterpret_cast<std::uint8_t *>(work_) +
                 program_->branchClockOffset_ + std::size_t{guardIndex} * 24;
    auto localTime = readDouble(clock);
    auto lastGlobalTime = readDouble(clock + 8);
    auto lastSerial = read64(clock + 16);
    if (lastSerial != updateSerial_) {
      auto delta = evaluatedTimeContext_ ? frameDelta_
                                         : (globalTime >= lastGlobalTime
                                                ? globalTime - lastGlobalTime
                                                : 0.0);
      if (lastSerial == 0) {
        delta = 0;
      }
      if (active && delta > 0) {
        localTime += delta;
      }
      if (!std::isfinite(localTime)) {
        return Result::NonFinite;
      }
      writeDouble(clock, localTime);
      writeDouble(clock + 8, globalTime);
      write64(clock + 16, updateSerial_);
    }
  }
  return Result::Ok;
}

auto Instance::evaluate(bool pureRefresh, const bool *dirty) noexcept
    -> Result {
  auto profileScope = ProfileScope(profiling_, ProfileEvent::Evaluate);
  if (pureRefresh && dirty == nullptr) {
    return Result::InvalidArgument;
  }
  if (program_->version_ == 1) {
    if (pureRefresh) {
      return Result::InvalidArgument;
    }
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index) {
      auto current = instruction(program_->bytes_, 1, index);
      auto debugStatus = beforeDebugNode(index);
      if (debugStatus != Result::Ok) {
        return debugStatus;
      }
      auto nodeScope =
          ProfileScope(profiling_, ProfileEvent::Node, index, profiling_.nodes);
      auto aIndex = read32(current + 20);
      auto bIndex = read32(current + 24);
      auto a = aIndex == noInput
                   ? (defaults_ == nullptr ? readDouble(current + 28)
                                           : defaults_[index * 2])
                   : work_[aIndex];
      auto b = bIndex == noInput
                   ? (defaults_ == nullptr ? readDouble(current + 36)
                                           : defaults_[index * 2 + 1])
                   : work_[bIndex];
      auto value = double{};
      switch (current[16]) {
      case 1:
        value = a;
        break;
      case 2:
        value = time_;
        break;
      case 3:
        value = a + b;
        break;
      case 4:
        value = a - b;
        break;
      case 5:
        value = a * b;
        break;
      case 6:
        if (b == 0) {
          return Result::DivideByZero;
        }
        value = a / b;
        break;
      default:
        return Result::UnsupportedBinary;
      }
      if (!std::isfinite(value)) {
        return Result::NonFinite;
      }
      work_[index] = value;
    }
    return Result::Ok;
  }
  if (!pureRefresh && ++updateSerial_ == 0) {
    updateSerial_ = 1;
    for (auto index = 0u; index < program_->branchGuardCount_; ++index) {
      auto clock = reinterpret_cast<std::uint8_t *>(work_) +
                   program_->branchClockOffset_ + std::size_t{index} * 24;
      write64(clock + 16, 0);
    }
  }
  if (!pureRefresh && program_->version_ >= 9) {
    pendingEvaluationSerial_ = evaluationSerial_ + 1;
    auto eventMasks = reinterpret_cast<std::uint8_t *>(work_) +
                      program_->logicalEventMaskOffset_;
    std::memset(eventMasks, 0,
                std::size_t{program_->nodeCount_} * sizeof(std::uint64_t));
  }
  for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index) {
    auto current = instruction(program_->bytes_, program_->version_, index);
    auto opcode = current[16];
    if (pureRefresh) {
      if (!dirty[index] || opcode == customSignalOpcode ||
          opcode == logicalEventSignalOpcode) {
        continue;
      }
    }
    auto guardId = program_->version_ >= 8
                       ? branchGuard(current, program_->version_)
                       : noInput;
    if (guardId != noInput) {
      if (pureRefresh) {
        if (!isBranchActive(guardId)) {
          continue;
        }
      } else {
        auto active = false;
        auto clockStatus = updateBranchClocks(guardId, active);
        if (clockStatus != Result::Ok) {
          return clockStatus;
        }
        if (!active) {
          continue;
        }
      }
    }
    if (opcode != customParameterOpcode && opcode != customSignalOpcode &&
        opcode != logicalEventSignalOpcode && opcode != enumSelectOpcode) {
      auto debugStatus = beforeDebugNode(index);
      if (debugStatus != Result::Ok) {
        return debugStatus;
      }
    }
    auto nodeScope =
        ProfileScope(profiling_, ProfileEvent::Node, index,
                     profiling_.nodes && opcode != customSignalOpcode);
    if (program_->version_ >= 3 &&
        (opcode == customParameterOpcode || opcode == customSignalOpcode)) {
      if (opcode == customSignalOpcode) {
        continue;
      }
      auto descriptor = program_->bindings_[index].descriptor;
      if (descriptor == nullptr) {
        return Result::UnknownCustomNode;
      }
      if (pureRefresh && !isLatePureBuiltin(descriptor)) {
        return Result::UnsupportedBinary;
      }
      alignas(customWorkAlignment) auto inputs =
          std::array<double, customMaxPorts * 4>{};
      auto inputWidths = std::array<std::uint8_t, customMaxPorts>{};
      alignas(customWorkAlignment) auto constants =
          std::array<double, customMaxPorts * 4>{};
      for (auto input = 0u; input < current[18]; ++input) {
        auto inputRecord = current + 48 + input * 8;
        auto sourceIndex = read32(inputRecord);
        inputWidths[input] = descriptor->inputWidths[input];
        for (auto lane = 0u; lane < descriptor->inputWidths[input]; ++lane) {
          constants[input * 4 + lane] =
              defaults_ == nullptr
                  ? readDouble(current + 80 + input * 32 + lane * 8)
                  : defaults_[index * 16 + input * 4 + lane];
          inputs[input * 4 + lane] =
              sourceIndex == noInput
                  ? constants[input * 4 + lane]
                  : work_[sourceIndex * 4 + inputRecord[5] + lane];
        }
      }
      auto state = reinterpret_cast<std::uint8_t *>(work_) +
                   stateOffset(program_->bindings_, index);
      auto invocation = CustomNodeInvocation{inputs.data(),
                                             current[18],
                                             inputWidths.data(),
                                             work_ + index * 4,
                                             current[17],
                                             constants.data(),
                                             std::size_t{current[18]} * 4,
                                             state,
                                             descriptor->stateBytes,
                                             time_,
                                             0xff,
                                             descriptor->context,
                                             effectTime_,
                                             frameDelta_,
                                             evaluatedTimeContext_};
      auto debugStatus =
          debugResolvedInputs(index, time_, inputs.data(), inputWidths.data(),
                              descriptor->inputIsColor);
      if (debugStatus != Result::Ok) {
        return debugStatus;
      }
      auto status = descriptor->function(invocation);
      if (status != Result::Ok) {
        return status;
      }
      for (auto lane = 0u; lane < current[17]; ++lane) {
        if (!std::isfinite(work_[index * 4 + lane])) {
          return Result::NonFinite;
        }
      }
      continue;
    }
    if (opcode == logicalEventSignalOpcode) {
      if (pureRefresh) {
        continue;
      }
      auto descriptor = program_->bindings_[index].logicalDescriptor;
      if (descriptor == nullptr || descriptor->update == nullptr) {
        return Result::UnknownCustomNode;
      }
      auto record = static_cast<const std::uint8_t *>(nullptr);
      for (auto logicalIndex = 0u;
           logicalIndex < program_->logicalInstructionCount_; ++logicalIndex) {
        auto candidate =
            program_->bytes_ + program_->logicalInstructionRecordsOffset_ +
            std::size_t{logicalIndex} * logicalInstructionRecordSize;
        if (read32(candidate) == index) {
          record = candidate;
          break;
        }
      }
      if (record == nullptr) {
        return Result::InvalidBinary;
      }
      alignas(customWorkAlignment) auto inputs =
          std::array<double, customMaxPorts * 4>{};
      auto inputWidths = std::array<std::uint8_t, customMaxPorts>{};
      for (auto input = 0u; input < current[18]; ++input) {
        auto inputRecord = current + 48 + input * 8;
        auto sourceIndex = read32(inputRecord);
        inputWidths[input] = descriptor->inputWidths[input];
        for (auto lane = 0u; lane < descriptor->inputWidths[input]; ++lane) {
          auto defaultValue =
              defaults_ == nullptr
                  ? readDouble(current + 80 + input * 32 + lane * 8)
                  : defaults_[index * 16 + input * 4 + lane];
          inputs[input * 4 + lane] =
              sourceIndex == noInput
                  ? defaultValue
                  : work_[sourceIndex * 4 + inputRecord[5] + lane];
        }
      }
      auto state = reinterpret_cast<std::uint8_t *>(work_) +
                   stateOffset(program_->bindings_, index);
      auto invocation = LogicalNodeInvocation{inputs.data(),
                                              current[18],
                                              inputWidths.data(),
                                              work_ + index * 4,
                                              current[17],
                                              state,
                                              descriptor->stateBytes,
                                              read32(record + 24),
                                              pendingEvaluationSerial_,
                                              0xff,
                                              0,
                                              time_};
      auto debugStatus =
          debugResolvedInputs(index, time_, inputs.data(), inputWidths.data());
      if (debugStatus != Result::Ok) {
        return debugStatus;
      }
      auto status = descriptor->update(invocation);
      if (status != Result::Ok) {
        return status;
      }
      auto validBranches =
          descriptor->signalBranchCount == 0
              ? std::uint64_t{0}
              : (std::uint64_t{1} << descriptor->signalBranchCount) - 1;
      if ((invocation.emittedBranches & ~validBranches) != 0) {
        return Result::InvalidValueType;
      }
      for (auto lane = 0u; lane < descriptor->outputWidth; ++lane) {
        if (!std::isfinite(work_[index * 4 + lane])) {
          return Result::NonFinite;
        }
      }
      auto eventMasks = reinterpret_cast<std::uint8_t *>(work_) +
                        program_->logicalEventMaskOffset_;
      write64(eventMasks + std::size_t{index} * sizeof(std::uint64_t),
              invocation.emittedBranches);
      continue;
    }
    if (opcode == currentPhaseOpcode || opcode == phaseTimeOpcode) {
      if (time_ < phaseStartTime_) {
        return Result::InvalidArgument;
      }
      work_[index * 4] = opcode == currentPhaseOpcode
                             ? static_cast<double>(currentPhase_)
                             : time_ - phaseStartTime_;
      continue;
    }
    if (opcode == enumValueOpcode) {
      auto sourceIndex = read32(current + 24);
      auto enumValue = sourceIndex == noInput
                           ? (defaults_ == nullptr ? readDouble(current + 32)
                                                   : defaults_[index * 16])
                           : work_[sourceIndex * 4 + current[21]];
      if (!std::isfinite(enumValue) || std::trunc(enumValue) != enumValue ||
          enumValue < std::numeric_limits<std::int32_t>::min() ||
          enumValue > std::numeric_limits<std::int32_t>::max()) {
        return Result::InvalidValueType;
      }
      work_[index * 4] = enumValue;
      continue;
    }
    if (opcode == enumSelectOpcode) {
      auto selectRecord = static_cast<const std::uint8_t *>(nullptr);
      for (auto selectIndex = 0u; selectIndex < program_->enumSelectCount_;
           ++selectIndex) {
        auto candidate = program_->bytes_ + program_->enumSelectRecordsOffset_ +
                         std::size_t{selectIndex} * enumSelectRecordSize;
        if (read32(candidate) == index) {
          selectRecord = candidate;
          break;
        }
      }
      if (selectRecord == nullptr) {
        return Result::InvalidBinary;
      }
      auto selector = read32(selectRecord + 4);
      auto selected = work_[selector * 4];
      auto isInteger = std::isfinite(selected) &&
                       std::trunc(selected) == selected &&
                       selected >= std::numeric_limits<std::int32_t>::min() &&
                       selected <= std::numeric_limits<std::int32_t>::max();
      auto selectedValue = isInteger ? static_cast<std::int32_t>(selected) : 0;
      auto caseCount = selectRecord[29];
      auto casesOffset = read32(selectRecord + 32);
      auto selectedCase = static_cast<const std::uint8_t *>(nullptr);
      for (auto caseIndex = 0u; caseIndex < caseCount; ++caseIndex) {
        auto candidate = program_->bytes_ +
                         program_->enumBranchMetadataOffset_ + casesOffset +
                         std::size_t{caseIndex} * enumCaseRecordSize;
        if (isInteger && readI32(candidate) == selectedValue) {
          selectedCase = candidate;
          break;
        }
      }
      auto sourceIndex = noInput;
      auto defaultValues = static_cast<const std::uint8_t *>(nullptr);
      if (selectedCase != nullptr) {
        sourceIndex = read32(selectedCase + 4);
        defaultValues = selectedCase + 8;
        if (sourceIndex == noInput && read32(selectRecord + 8) != noInput) {
          sourceIndex = read32(selectRecord + 8);
        }
      } else {
        sourceIndex = read32(selectRecord + 8);
        defaultValues = selectRecord + 36;
      }
      auto debugStatus =
          debugEnumSelect(index, selector, sourceIndex, defaultValues);
      if (debugStatus != Result::Ok) {
        return debugStatus;
      }
      for (auto lane = 0u; lane < current[17]; ++lane) {
        work_[index * 4 + lane] = sourceIndex == noInput
                                      ? readDouble(defaultValues + lane * 8)
                                      : work_[sourceIndex * 4 + lane];
      }
      continue;
    }
    if (opcode == branchTimeOpcode) {
      auto clockIndex = read32(current + 112);
      if (clockIndex >= program_->branchGuardCount_) {
        return Result::InvalidBinary;
      }
      auto clock = reinterpret_cast<std::uint8_t *>(work_) +
                   program_->branchClockOffset_ + std::size_t{clockIndex} * 24;
      work_[index * 4] = readDouble(clock);
      continue;
    }
    if (opcode == curveOpcode) {
      auto instructionBytes =
          typedHeaderSize +
          std::size_t{program_->nodeCount_} * customInstructionSize;
      auto curveKeys =
          program_->bytes_ + instructionBytes + read32(current + 224);
      work_[index * 4] = evaluateCurve(curveKeys, read32(current + 228), time_);
      if (!std::isfinite(work_[index * 4])) {
        return Result::NonFinite;
      }
      continue;
    }
    auto outputWidth = current[17];
    auto aIndex = read32(current + 24);
    auto bIndex = read32(current + 28);
    auto base = index * 4;
    auto valueA = [&](auto lane) noexcept {
      return aIndex == noInput
                 ? (defaults_ == nullptr
                        ? readDouble(current + 32 + lane * 8)
                        : defaults_[index * (program_->version_ >= 3 ? 16 : 8) +
                                    lane])
                 : work_[aIndex * 4 + current[21] + lane];
    };
    auto valueB = [&](auto lane) noexcept {
      return bIndex == noInput
                 ? (defaults_ == nullptr
                        ? readDouble(current + 64 + lane * 8)
                        : defaults_[index * (program_->version_ >= 3 ? 16 : 8) +
                                    4 + lane])
                 : work_[bIndex * 4 + current[22] + lane];
    };
    if (opcode >= int32Opcode && opcode <= int32ToFloatOpcode) {
      auto a = valueA(0);
      if (opcode == floatToInt32Opcode) {
        if (!std::isfinite(a)) {
          return Result::NonFinite;
        }
        auto truncated = std::trunc(a);
        if (truncated < std::numeric_limits<std::int32_t>::min() ||
            truncated > std::numeric_limits<std::int32_t>::max()) {
          return Result::ArithmeticOverflow;
        }
        work_[base] = truncated;
        continue;
      }
      auto integerA = std::int32_t{};
      auto status = int32FromDouble(a, integerA);
      if (status != Result::Ok) {
        return status;
      }
      if (opcode == int32Opcode) {
        work_[base] = integerA;
        continue;
      }
      if (opcode == int32ToFloatOpcode) {
        work_[base] = static_cast<double>(integerA);
        continue;
      }
      auto integerB = std::int32_t{};
      status = int32FromDouble(valueB(0), integerB);
      if (status != Result::Ok) {
        return status;
      }
      if (opcode == int32DivideOpcode) {
        if (integerB == 0) {
          return Result::DivideByZero;
        }
        if (integerA == std::numeric_limits<std::int32_t>::min() &&
            integerB == -1) {
          return Result::ArithmeticOverflow;
        }
        work_[base] = integerA / integerB;
        continue;
      }
      auto result = std::int64_t{};
      if (opcode == int32AddOpcode) {
        result = std::int64_t{integerA} + integerB;
      } else if (opcode == int32SubtractOpcode) {
        result = std::int64_t{integerA} - integerB;
      } else if (opcode == int32MultiplyOpcode) {
        result = std::int64_t{integerA} * integerB;
      } else {
        return Result::UnsupportedBinary;
      }
      if (result < std::numeric_limits<std::int32_t>::min() ||
          result > std::numeric_limits<std::int32_t>::max()) {
        return Result::ArithmeticOverflow;
      }
      work_[base] = static_cast<double>(result);
      continue;
    }
    if (opcode == 1 || opcode == 7 || opcode == 8 || opcode == 9 ||
        opcode == 10) {
      for (auto lane = 0u; lane < outputWidth; ++lane) {
        work_[base + lane] = valueA(lane);
      }
    } else if (opcode == 2) {
      work_[base] = time_;
    } else if (opcode >= 3 && opcode <= 22) {
      if (outputWidth > 1) {
        auto aValues = std::array<double, 4>{};
        auto bValues = std::array<double, 4>{};
        auto parameterStride = program_->version_ >= 3 ? 16u : 8u;
        auto a = aIndex != noInput      ? work_ + aIndex * 4 + current[21]
                 : defaults_ != nullptr ? defaults_ + index * parameterStride
                                        : nullptr;
        auto b = bIndex != noInput ? work_ + bIndex * 4 + current[22]
                 : defaults_ != nullptr
                     ? defaults_ + index * parameterStride + 4
                     : nullptr;
        if (a == nullptr) {
          for (auto lane = 0u; lane < outputWidth; ++lane) {
            aValues[lane] = valueA(lane);
          }
          a = aValues.data();
        }
        if (b == nullptr) {
          for (auto lane = 0u; lane < outputWidth; ++lane) {
            bValues[lane] = valueB(lane);
          }
          b = bValues.data();
        }
        auto status =
            math::arithmetic(work_ + base, a, b, outputWidth,
                             static_cast<math::Arithmetic>((opcode - 3) % 4));
        if (status == math::ArithmeticResult::DivideByZero) {
          return Result::DivideByZero;
        }
        if (status == math::ArithmeticResult::NonFinite) {
          return Result::NonFinite;
        }
        if (status != math::ArithmeticResult::Ok) {
          return Result::InvalidArgument;
        }
        continue;
      }
      for (auto lane = 0u; lane < outputWidth; ++lane) {
        auto a = valueA(lane);
        auto b = valueB(lane);
        auto value = double{};
        switch ((opcode - 3) % 4) {
        case 0:
          value = a + b;
          break;
        case 1:
          value = a - b;
          break;
        case 2:
          value = a * b;
          break;
        case 3:
          if (b == 0) {
            return Result::DivideByZero;
          }
          value = a / b;
          break;
        }
        if (!std::isfinite(value)) {
          return Result::NonFinite;
        }
        work_[base + lane] = value;
      }
    } else if (opcode >= 23 && opcode <= 25) {
      auto value = valueA(0);
      for (auto lane = 0u; lane < outputWidth; ++lane) {
        work_[base + lane] = value;
      }
    } else if (opcode >= 26 && opcode <= 28) {
      for (auto lane = 0u; lane < outputWidth; ++lane) {
        work_[base + lane] = valueA(lane);
      }
    } else if (opcode >= 29 && opcode <= 34) {
      auto copied = opcode == 29 || opcode == 30   ? 2u
                    : opcode == 31 || opcode == 32 ? 3u
                                                   : 4u;
      for (auto lane = 0u; lane < outputWidth; ++lane) {
        work_[base + lane] =
            lane < copied
                ? valueA(lane)
                : (defaults_ == nullptr
                       ? readDouble(current + 32)
                       : defaults_[index * (program_->version_ >= 3 ? 16 : 8)]);
      }
    } else if (opcode >= 35 && opcode <= 37) {
      for (auto lane = 0u; lane < outputWidth; ++lane) {
        work_[base + lane] = valueA(static_cast<std::uint32_t>(
            defaults_ == nullptr
                ? readDouble(current + 32 + lane * 8)
                : defaults_[index * (program_->version_ >= 3 ? 16 : 8) +
                            lane]));
      }
    } else if (opcode == 38 || opcode == 39) {
      for (auto lane = 0u; lane < 4; ++lane) {
        work_[base + lane] = valueA(lane);
      }
    } else {
      return Result::UnsupportedBinary;
    }
    for (auto lane = 0u; lane < outputWidth; ++lane) {
      if (!std::isfinite(work_[base + lane])) {
        return Result::NonFinite;
      }
    }
  }
  return Result::Ok;
}
} // namespace liberafx
