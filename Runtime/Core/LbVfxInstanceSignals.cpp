#include "LbVfxRuntimeValueInternal.h"

namespace liberafx {
using namespace vfx_runtime_detail;
auto Instance::dispatchSignal(const std::uint8_t entryNodeId[16], double time,
                              std::size_t maxInstructions) noexcept -> Result {
  auto entry = SignalHandle{};
  auto status = bindSignal(entryNodeId, entry);
  if (status != Result::Ok) {
    return status;
  }
  return dispatchSignal(entry, time, maxInstructions);
}

auto Instance::dispatchSignal(const SignalHandle &entry, double time,
                              std::size_t maxInstructions) noexcept -> Result {
  return dispatchSignalInternal(entry, time, maxInstructions, -1);
}

auto Instance::setDeferredSignalStorage(DeferredSignal *storage,
                                        std::size_t capacity) noexcept
    -> Result {
  if (dispatching_ ||
      phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  if (!program_ || (capacity != 0 && !storage) || (capacity == 0 && storage) ||
      capacity >
          std::numeric_limits<std::size_t>::max() / sizeof(DeferredSignal)) {
    return Result::InvalidArgument;
  }
  if (deferredCount_) {
    return Result::Busy;
  }
  auto bytes = capacity * sizeof(DeferredSignal);
  if (capacity &&
      (!isAligned(storage, alignof(DeferredSignal)) ||
       rangesOverlap(storage, bytes, work_, program_->workBytes()) ||
       (defaults_ &&
        rangesOverlap(storage, bytes, defaults_,
                      program_->parameterValueCount() * sizeof(double))))) {
    return Result::InvalidArgument;
  }
  deferredSignals_ = storage;
  deferredCapacity_ = capacity;
  return Result::Ok;
}

auto Instance::nextDeferredSignal(double &time, bool &hasSignal) const noexcept
    -> Result {
  if (dispatching_ ||
      phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  if (!program_) {
    return Result::InvalidArgument;
  }
  hasSignal = false;
  auto earliest = std::numeric_limits<double>::infinity();
  for (auto index = std::size_t{0}; index < deferredCount_; ++index) {
    const auto &item = deferredSignals_[index];
    auto due = item.time;
    if (item.usesBranchTime) {
      if (item.branchGuardId >= program_->branchGuardCount_) {
        return Result::InvalidBinary;
      }
      if (!isBranchActive(item.branchGuardId)) {
        due = std::numeric_limits<double>::max();
      } else {
        auto clock = reinterpret_cast<const std::uint8_t *>(work_) +
                     program_->branchClockOffset_ +
                     std::size_t{item.branchGuardId} * 24;
        auto localTime = readDouble(clock);
        due = time_ + std::max(0.0, item.time - localTime);
      }
    }
    if (due < earliest) {
      earliest = due;
      hasSignal = true;
    }
  }
  if (hasSignal) {
    time = earliest;
  }
  return Result::Ok;
}

auto Instance::clearDeferredSignals() noexcept -> Result {
  if (dispatching_ ||
      phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  if (!program_) {
    return Result::InvalidArgument;
  }
  deferredCount_ = 0;
  return Result::Ok;
}

auto Instance::dispatchDeferredSignal(double time,
                                      std::size_t maxInstructions) noexcept
    -> Result {
  if (dispatching_ ||
      phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  if (!program_ || !std::isfinite(time) || !maxInstructions) {
    return Result::InvalidArgument;
  }
  if (!deferredCount_ || time_ != time || result() != Result::Ok) {
    return Result::NotReady;
  }
  auto selected = deferredCount_;
  for (auto index = std::size_t{0}; index < deferredCount_; ++index) {
    const auto &candidate = deferredSignals_[index];
    if (!candidate.usesBranchTime) {
      if (candidate.time == time) {
        selected = index;
        break;
      }
      continue;
    }
    if (candidate.branchGuardId >= program_->branchGuardCount_ ||
        !isBranchActive(candidate.branchGuardId)) {
      continue;
    }
    auto clock = reinterpret_cast<const std::uint8_t *>(work_) +
                 program_->branchClockOffset_ +
                 std::size_t{candidate.branchGuardId} * 24;
    if (readDouble(clock) >= candidate.time) {
      selected = index;
      break;
    }
  }
  if (selected == deferredCount_) {
    return Result::NotReady;
  }
  auto item = deferredSignals_[selected];
  for (auto index = selected + 1; index < deferredCount_; ++index) {
    deferredSignals_[index - 1] = deferredSignals_[index];
  }
  --deferredCount_;
  auto entry = SignalHandle{program_, generation_, program_->generation_,
                            item.nodeIndex};
  return dispatchSignalInternal(entry, time, maxInstructions, item.branch);
}

auto Instance::dispatchGeneratedSignals(std::size_t maxInstructions) noexcept
    -> Result {
  if (dispatching_ ||
      phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  if (program_ == nullptr || maxInstructions == 0) {
    return Result::InvalidArgument;
  }
  if (program_->version_ < 9) {
    return Result::Ok;
  }
  auto status = result();
  if (status != Result::Ok) {
    return status;
  }
  if (!generatedSignalsPending_ && logicalQueueCount_ == 0) {
    return refreshDirtyPure();
  }
  return drainLogicalSignals(maxInstructions, true);
}

auto Instance::dispatchSignalV9(const SignalHandle &entry, double time,
                                std::size_t maxInstructions,
                                int resumeBranch) noexcept -> Result {
  if (logicalQueueCount_ != 0 || generatedSignalsPending_ ||
      generatedQueueSeeded_) {
    return Result::NotReady;
  }
  if (program_->logicalQueueCapacity_ == 0) {
    return Result::InvalidBinary;
  }
  auto queue = reinterpret_cast<LogicalQueuedSignal *>(
      reinterpret_cast<std::uint8_t *>(work_) + program_->logicalQueueOffset_);
  auto payloadQueue = program_->signalPayloadQueueOffset_ == 0
                          ? nullptr
                          : reinterpret_cast<SignalPayload *>(
                                reinterpret_cast<std::uint8_t *>(work_) +
                                program_->signalPayloadQueueOffset_);
  if (resumeBranch >= 0) {
    queue[0] = {entry.nodeIndex, static_cast<std::uint8_t>(resumeBranch), 2, 0};
  } else {
    queue[0] = {entry.nodeIndex, 0, 1, 0};
  }
  if (payloadQueue != nullptr) {
    payloadQueue[0] = {};
  }
  logicalQueueHead_ = 0;
  logicalQueueCount_ = 1;
  time_ = time;
  return drainLogicalSignals(maxInstructions, false);
}

auto Instance::drainLogicalSignals(std::size_t maxInstructions,
                                   bool seedGenerated) noexcept -> Result {
  if (dispatching_) {
    return Result::Busy;
  }
  if (program_ == nullptr || program_->version_ < 9 || maxInstructions == 0) {
    return Result::InvalidArgument;
  }
  if (phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  auto status = result();
  if (status != Result::Ok) {
    return status;
  }
  auto queue = reinterpret_cast<LogicalQueuedSignal *>(
      reinterpret_cast<std::uint8_t *>(work_) + program_->logicalQueueOffset_);
  auto payloadQueue = program_->signalPayloadQueueOffset_ == 0
                          ? nullptr
                          : reinterpret_cast<SignalPayload *>(
                                reinterpret_cast<std::uint8_t *>(work_) +
                                program_->signalPayloadQueueOffset_);
  auto activePayload = SignalPayload{};
  auto enqueue = [&](std::uint32_t nodeIndex, std::uint8_t inputSlot,
                     std::uint8_t isRoot,
                     std::uint32_t sourceIndex) noexcept -> Result {
    if (logicalQueueCount_ >= program_->logicalQueueCapacity_) {
      return Result::InstructionBudgetExceeded;
    }
    auto tail = (logicalQueueHead_ + logicalQueueCount_) %
                program_->logicalQueueCapacity_;
    queue[tail] = {nodeIndex, inputSlot, isRoot,
                   static_cast<std::uint16_t>(sourceIndex + 1)};
    if (payloadQueue != nullptr) {
      payloadQueue[tail] = activePayload;
    }
    ++logicalQueueCount_;
    return Result::Ok;
  };
  auto routeLowerBound = [&](std::uint32_t sourceIndex,
                             std::uint8_t branch) noexcept {
    auto first = std::size_t{0};
    auto last = std::size_t{program_->logicalSignalRouteCount_};
    while (first < last) {
      auto middle = first + (last - first) / 2;
      auto record = program_->bytes_ + program_->logicalSignalRoutesOffset_ +
                    middle * logicalSignalRouteRecordSize;
      auto currentSource = read32(record);
      auto currentBranch = record[4];
      if (currentSource < sourceIndex ||
          (currentSource == sourceIndex && currentBranch < branch)) {
        first = middle + 1;
      } else {
        last = middle;
      }
    }
    return first;
  };
  auto routeCount = [&](std::uint32_t sourceIndex,
                        std::uint8_t branch) noexcept {
    auto count = std::size_t{0};
    auto routeIndex = routeLowerBound(sourceIndex, branch);
    while (routeIndex < program_->logicalSignalRouteCount_) {
      auto record = program_->bytes_ + program_->logicalSignalRoutesOffset_ +
                    routeIndex * logicalSignalRouteRecordSize;
      if (read32(record) != sourceIndex || record[4] != branch) {
        break;
      }
      ++routeIndex;
      ++count;
    }
    return count;
  };
  auto appendRoutes = [&](std::uint32_t sourceIndex,
                          std::uint8_t branch) noexcept -> Result {
    auto routeIndex = routeLowerBound(sourceIndex, branch);
    while (routeIndex < program_->logicalSignalRouteCount_) {
      auto record = program_->bytes_ + program_->logicalSignalRoutesOffset_ +
                    routeIndex * logicalSignalRouteRecordSize;
      if (read32(record) != sourceIndex || record[4] != branch) {
        break;
      }
      auto result = enqueue(read32(record + 8), record[5], 0, sourceIndex);
      if (result != Result::Ok) {
        return result;
      }
      ++routeIndex;
    }
    return Result::Ok;
  };

  if (seedGenerated && generatedSignalsPending_ && !generatedQueueSeeded_) {
    std::size_t requiredEvents = 0;
    auto eventMasks = reinterpret_cast<const std::uint8_t *>(work_) +
                      program_->logicalEventMaskOffset_;
    for (auto logicalIndex = 0u;
         logicalIndex < program_->logicalInstructionCount_; ++logicalIndex) {
      auto record = program_->bytes_ +
                    program_->logicalInstructionRecordsOffset_ +
                    std::size_t{logicalIndex} * logicalInstructionRecordSize;
      auto nodeIndex = read32(record);
      auto branches =
          read64(eventMasks + std::size_t{nodeIndex} * sizeof(std::uint64_t));
      for (auto branch = 0u; branch < record[31]; ++branch) {
        if ((branches & (std::uint64_t{1} << branch)) != 0) {
          auto routes =
              routeCount(nodeIndex, static_cast<std::uint8_t>(branch));
          if (routes > program_->logicalQueueCapacity_ - requiredEvents) {
            return Result::InstructionBudgetExceeded;
          }
          requiredEvents += routes;
        }
      }
    }
    if (requiredEvents > program_->logicalQueueCapacity_ - logicalQueueCount_) {
      return Result::InstructionBudgetExceeded;
    }
    for (auto logicalIndex = 0u;
         logicalIndex < program_->logicalInstructionCount_; ++logicalIndex) {
      auto record = program_->bytes_ +
                    program_->logicalInstructionRecordsOffset_ +
                    std::size_t{logicalIndex} * logicalInstructionRecordSize;
      auto nodeIndex = read32(record);
      auto branches =
          read64(eventMasks + std::size_t{nodeIndex} * sizeof(std::uint64_t));
      for (auto branch = 0u; branch < record[31]; ++branch) {
        if ((branches & (std::uint64_t{1} << branch)) != 0) {
          auto enqueueStatus =
              appendRoutes(nodeIndex, static_cast<std::uint8_t>(branch));
          if (enqueueStatus != Result::Ok) {
            return enqueueStatus;
          }
        }
      }
    }
    std::memset(const_cast<std::uint8_t *>(eventMasks), 0,
                std::size_t{program_->nodeCount_} * sizeof(std::uint64_t));
    generatedQueueSeeded_ = true;
  }

  struct DispatchScope final {
    bool &active;
    ~DispatchScope() { active = false; }
  };
  auto scope = DispatchScope{dispatching_};
  dispatching_ = true;
  auto visited = std::size_t{0};
  while (logicalQueueCount_ != 0) {
    if (visited >= maxInstructions) {
      return Result::InstructionBudgetExceeded;
    }
    auto event = queue[logicalQueueHead_];
    activePayload = payloadQueue == nullptr ? SignalPayload{}
                                            : payloadQueue[logicalQueueHead_];
    for (auto fieldIndex = std::size_t{}; fieldIndex < activePayload.size();
         ++fieldIndex) {
      auto field = activePayload.field(fieldIndex);
      auto sourceIndex = 0u;
      for (; sourceIndex < program_->nodeCount_; ++sourceIndex) {
        if (std::memcmp(instruction(program_->bytes_, 3, sourceIndex),
                        field->id.data(), 16) == 0) {
          break;
        }
      }
      if (sourceIndex == program_->nodeCount_ ||
          field->kind != SignalValueKind::Vector4) {
        return Result::InvalidBinary;
      }
      std::copy_n(field->components, 4, work_ + sourceIndex * 4);
      (reinterpret_cast<std::uint8_t *>(work_) +
       program_->logicalDirtyOffset_)[sourceIndex] = 1;
    }
    status = refreshDirtyPure();
    if (status != Result::Ok) {
      return status;
    }
    auto current = instruction(program_->bytes_, 3, event.nodeIndex);
    if (event.isRoot == 2) {
      auto branch = event.inputSlot;
      auto fanout = routeCount(event.nodeIndex, branch);
      if (logicalQueueCount_ - 1 + fanout > program_->logicalQueueCapacity_) {
        return Result::InstructionBudgetExceeded;
      }
      --logicalQueueCount_;
      logicalQueueHead_ =
          (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
      status = appendRoutes(event.nodeIndex, branch);
      if (status != Result::Ok) {
        return status;
      }
      continue;
    }
    ++visited;
    auto guardId = branchGuard(current, program_->version_);
    if (guardId != noInput && !isBranchActive(guardId)) {
      --logicalQueueCount_;
      logicalQueueHead_ =
          (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
      continue;
    }
    if (event.isRoot == 0 && current[16] != customSignalOpcode &&
        current[16] != logicalEventSignalOpcode) {
      return Result::InvalidBinary;
    }
    if (event.isRoot == 1 && current[16] != customSignalOpcode) {
      return Result::InvalidValueType;
    }
    auto maxFanout = std::size_t{0};
    auto logicalDescriptor =
        program_->bindings_[event.nodeIndex].logicalDescriptor;
    auto customDescriptor = program_->bindings_[event.nodeIndex].descriptor;
    if (current[16] == logicalEventSignalOpcode) {
      if (logicalDescriptor == nullptr ||
          logicalDescriptor->onSignal == nullptr ||
          event.inputSlot >= logicalDescriptor->signalInputCount) {
        return Result::InvalidValueType;
      }
      for (auto branch = 0u; branch < logicalDescriptor->signalBranchCount;
           ++branch) {
        maxFanout +=
            routeCount(event.nodeIndex, static_cast<std::uint8_t>(branch));
      }
    } else {
      if (customDescriptor == nullptr ||
          customDescriptor->execution != CustomExecution::Signal) {
        return Result::UnknownCustomNode;
      }
      for (auto branch = 0u; branch < customDescriptor->branchCount; ++branch) {
        maxFanout =
            std::max(maxFanout, routeCount(event.nodeIndex,
                                           static_cast<std::uint8_t>(branch)));
      }
    }
    if (logicalQueueCount_ - 1 + maxFanout > program_->logicalQueueCapacity_) {
      return Result::InstructionBudgetExceeded;
    }

    alignas(customWorkAlignment) auto inputs =
        std::array<double, customMaxPorts * 4>{};
    auto inputWidths = std::array<std::uint8_t, customMaxPorts>{};
    alignas(customWorkAlignment) auto constants =
        std::array<double, customMaxPorts * 4>{};
    auto inputCount = current[18];
    auto outputWidth = current[17];
    auto state = reinterpret_cast<std::uint8_t *>(work_) +
                 stateOffset(program_->bindings_, event.nodeIndex);
    if (current[16] == logicalEventSignalOpcode) {
      for (auto input = 0u; input < inputCount; ++input) {
        auto inputRecord = current + 48 + input * 8;
        auto sourceIndex = read32(inputRecord);
        inputWidths[input] = logicalDescriptor->inputWidths[input];
        for (auto lane = 0u; lane < inputWidths[input]; ++lane) {
          constants[input * 4 + lane] =
              defaults_ == nullptr
                  ? readDouble(current + 80 + input * 32 + lane * 8)
                  : defaults_[event.nodeIndex * 16 + input * 4 + lane];
          inputs[input * 4 + lane] =
              sourceIndex == noInput
                  ? constants[input * 4 + lane]
                  : work_[sourceIndex * 4 + inputRecord[5] + lane];
        }
      }
      auto invocation = LogicalNodeInvocation{inputs.data(),
                                              inputCount,
                                              inputWidths.data(),
                                              work_ + event.nodeIndex * 4,
                                              outputWidth,
                                              state,
                                              logicalDescriptor->stateBytes,
                                              0,
                                              evaluationSerial_,
                                              event.inputSlot,
                                              0,
                                              time_,
                                              &quality_};
      auto record = static_cast<const std::uint8_t *>(nullptr);
      for (auto logicalIndex = 0u;
           logicalIndex < program_->logicalInstructionCount_; ++logicalIndex) {
        auto candidate =
            program_->bytes_ + program_->logicalInstructionRecordsOffset_ +
            std::size_t{logicalIndex} * logicalInstructionRecordSize;
        if (read32(candidate) == event.nodeIndex) {
          record = candidate;
          break;
        }
      }
      if (record == nullptr) {
        return Result::InvalidBinary;
      }
      invocation.configuration = read32(record + 24);
      status = debugResolvedInputs(
          event.nodeIndex, time_, inputs.data(), inputWidths.data(), nullptr,
          event.reserved == 0
              ? nullptr
              : instruction(program_->bytes_, 3, event.reserved - 1));
      if (status != Result::Ok) {
        return status;
      }
      if (signalObserver_ != nullptr) {
        status = signalObserver_(signalObserverContext_, current);
        if (status != Result::Ok) {
          return status;
        }
      }
      status = logicalDescriptor->onSignal(invocation);
      if (status != Result::Ok) {
        return status;
      }
      auto validBranches =
          logicalDescriptor->signalBranchCount == 0
              ? std::uint64_t{0}
              : (std::uint64_t{1} << logicalDescriptor->signalBranchCount) - 1;
      if ((invocation.emittedBranches & ~validBranches) != 0) {
        return Result::InvalidValueType;
      }
      for (auto lane = 0u; lane < outputWidth; ++lane) {
        if (!std::isfinite(work_[event.nodeIndex * 4 + lane])) {
          return Result::NonFinite;
        }
      }
      if (invocation.captureSignalValue) {
        if (payloadQueue == nullptr || outputWidth != 4) {
          return Result::UnsupportedBinary;
        }
        auto field = SignalFieldValue{};
        std::copy_n(current, 16, field.id.data());
        field.kind = SignalValueKind::Vector4;
        std::copy_n(work_ + event.nodeIndex * 4, 4, field.components);
        status = activePayload.set(field);
        if (status != Result::Ok) {
          return status;
        }
      }
      auto dirty = reinterpret_cast<std::uint8_t *>(work_) +
                   program_->logicalDirtyOffset_;
      dirty[event.nodeIndex] = 1;
      --logicalQueueCount_;
      logicalQueueHead_ =
          (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
      for (auto branch = 0u; branch < logicalDescriptor->signalBranchCount;
           ++branch) {
        if ((invocation.emittedBranches & (std::uint64_t{1} << branch)) != 0) {
          status =
              appendRoutes(event.nodeIndex, static_cast<std::uint8_t>(branch));
          if (status != Result::Ok) {
            return status;
          }
        }
      }
    } else {
      for (auto input = 0u; input < inputCount; ++input) {
        auto inputRecord = current + 48 + input * 8;
        auto sourceIndex = read32(inputRecord);
        inputWidths[input] = customDescriptor->inputWidths[input];
        for (auto lane = 0u; lane < inputWidths[input]; ++lane) {
          constants[input * 4 + lane] =
              defaults_ == nullptr
                  ? readDouble(current + 80 + input * 32 + lane * 8)
                  : defaults_[event.nodeIndex * 16 + input * 4 + lane];
          inputs[input * 4 + lane] =
              sourceIndex == noInput
                  ? constants[input * 4 + lane]
                  : work_[sourceIndex * 4 + inputRecord[5] + lane];
        }
      }
      auto invocation = CustomNodeInvocation{inputs.data(),
                                             inputCount,
                                             inputWidths.data(),
                                             nullptr,
                                             0,
                                             constants.data(),
                                             std::size_t{inputCount} * 4,
                                             state,
                                             customDescriptor->stateBytes,
                                             time_,
                                             0xff,
                                             customDescriptor->context,
                                             effectTime_,
                                             frameDelta_,
                                             evaluatedTimeContext_};
      invocation.signalPayload = &activePayload;
      invocation.realEffectTime = realEffectTime_;
      invocation.realInstanceAge = realInstanceAge_;
      invocation.realFrameDelta = realFrameDelta_;
      status = debugResolvedInputs(
          event.nodeIndex, time_, inputs.data(), inputWidths.data(),
          customDescriptor->inputIsColor,
          event.reserved == 0
              ? nullptr
              : instruction(program_->bytes_, 3, event.reserved - 1));
      if (status != Result::Ok) {
        return status;
      }
      if (signalObserver_ != nullptr) {
        status = signalObserver_(signalObserverContext_, current);
        if (status != Result::Ok) {
          return status;
        }
      }
      status = customDescriptor->function(invocation);
      if (status != Result::Ok) {
        return status;
      }
      if (invocation.deferSignal && activePayload.size() != 0) {
        return Result::UnsupportedBinary;
      }
      if (invocation.enterPhase != 0) {
        if (invocation.enterPhase > 3) {
          return Result::InvalidArgument;
        }
        pendingPhase_ = invocation.enterPhase;
        pendingPhaseTime_ = time_;
      }
      if (invocation.forceScope != 0) {
        if (!forceRequestsEnabled_) {
          return Result::UnsupportedBinary;
        }
        if (invocation.forceScope > 2) {
          return Result::InvalidArgument;
        }
        forceScope_ = invocation.forceScope;
        deferredCount_ = logicalQueueCount_ = 0;
        generatedQueueSeeded_ = generatedSignalsPending_ = false;
        return Result::Ok;
      }
      if (current[19] == 0) {
        --logicalQueueCount_;
        logicalQueueHead_ =
            (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
        continue;
      }
      if (invocation.deferSignal) {
        auto usesBranchTime = guardId != noInput;
        auto baseTime = time_;
        if (usesBranchTime) {
          if (guardId >= program_->branchGuardCount_ || guardId >= 0xff) {
            return Result::UnsupportedBinary;
          }
          auto clock = reinterpret_cast<const std::uint8_t *>(work_) +
                       program_->branchClockOffset_ + std::size_t{guardId} * 24;
          baseTime = readDouble(clock);
        }
        auto due = baseTime + invocation.delaySeconds;
        if (!std::isfinite(invocation.delaySeconds) ||
            invocation.delaySeconds < 0 || !std::isfinite(due)) {
          return Result::InvalidArgument;
        }
        if (!deferredSignals_) {
          return Result::NotReady;
        }
        if (deferredCount_ == deferredCapacity_) {
          return Result::QueueRejected;
        }
        if (invocation.selectedBranch >= current[19]) {
          return Result::InvalidValueType;
        }
        deferredSignals_[deferredCount_++] = {
            due, event.nodeIndex, invocation.selectedBranch,
            usesBranchTime ? static_cast<std::uint8_t>(guardId)
                           : std::uint8_t{0xff},
            usesBranchTime};
        --logicalQueueCount_;
        logicalQueueHead_ =
            (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
        continue;
      }
      if (invocation.selectedBranch >= current[19]) {
        return Result::InvalidValueType;
      }
      --logicalQueueCount_;
      logicalQueueHead_ =
          (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
      status = appendRoutes(event.nodeIndex, invocation.selectedBranch);
      if (status != Result::Ok) {
        return status;
      }
    }
  }
  status = refreshDirtyPure();
  if (status != Result::Ok) {
    return status;
  }
  if (generatedQueueSeeded_) {
    generatedQueueSeeded_ = false;
    generatedSignalsPending_ = false;
  }
  return Result::Ok;
}

auto Instance::refreshDirtyPure() noexcept -> Result {
  if (program_ == nullptr || program_->version_ < 9) {
    return Result::Ok;
  }
  auto dirtyBytes =
      reinterpret_cast<std::uint8_t *>(work_) + program_->logicalDirtyOffset_;
  auto dirty = std::array<bool, maxNodes>{};
  auto anyDirty = false;
  for (auto index = 0u; index < program_->nodeCount_; ++index) {
    dirty[index] = dirtyBytes[index] != 0;
    anyDirty = anyDirty || dirty[index];
  }
  if (!anyDirty) {
    return Result::Ok;
  }

  auto sourceIsDirty = [&](std::uint32_t sourceIndex) noexcept {
    return sourceIndex != noInput && sourceIndex < program_->nodeCount_ &&
           dirty[sourceIndex];
  };
  for (auto index = 0u; index < program_->nodeCount_; ++index) {
    auto current = instruction(program_->bytes_, 3, index);
    auto opcode = current[16];
    if (dirty[index] || opcode == customSignalOpcode) {
      continue;
    }
    auto guardId = branchGuard(current, program_->version_);
    if (guardId != noInput && !isBranchActive(guardId)) {
      continue;
    }
    auto depends = false;
    if (opcode == customParameterOpcode || opcode == logicalEventSignalOpcode) {
      for (auto input = 0u; input < current[18]; ++input) {
        depends = depends || sourceIsDirty(read32(current + 48 + input * 8));
      }
      if (depends && opcode == logicalEventSignalOpcode) {
        if (std::memcmp(current + 24, logicalSignalCaptureTypeId.data(), 16) !=
            0) {
          return Result::UnsupportedBinary;
        }
        continue;
      }
      if (depends &&
          !isLatePureBuiltin(program_->bindings_[index].descriptor)) {
        return Result::UnsupportedBinary;
      }
    } else if (opcode == enumValueOpcode) {
      depends = sourceIsDirty(read32(current + 24));
    } else if (opcode == enumSelectOpcode) {
      for (auto selectIndex = 0u; selectIndex < program_->enumSelectCount_;
           ++selectIndex) {
        auto record = program_->bytes_ + program_->enumSelectRecordsOffset_ +
                      std::size_t{selectIndex} * enumSelectRecordSize;
        if (read32(record) != index) {
          continue;
        }
        depends = sourceIsDirty(read32(record + 4)) ||
                  sourceIsDirty(read32(record + 8));
        auto caseCount = record[29];
        auto caseOffset = read32(record + 32);
        for (auto caseIndex = 0u; caseIndex < caseCount; ++caseIndex) {
          depends =
              depends || sourceIsDirty(read32(
                             program_->bytes_ +
                             program_->enumBranchMetadataOffset_ + caseOffset +
                             std::size_t{caseIndex} * enumCaseRecordSize + 4));
        }
        break;
      }
    } else if ((opcode >= 1 && opcode <= 39) || opcode == curveOpcode ||
               opcode == branchTimeOpcode) {
      depends = sourceIsDirty(read32(current + 24)) ||
                sourceIsDirty(read32(current + 28));
    }
    if (depends) {
      dirty[index] = true;
    }
  }
  for (auto index = 0u; index < program_->nodeCount_; ++index) {
    if (dirty[index]) {
      dirtyBytes[index] = 1;
    }
  }
  auto status = evaluate(true, dirty.data());
  if (status != Result::Ok) {
    return status;
  }
  std::memset(dirtyBytes, 0, program_->nodeCount_);
  return Result::Ok;
}

auto Instance::dispatchSignalInternal(const SignalHandle &entry, double time,
                                      std::size_t maxInstructions,
                                      int resumeBranch) noexcept -> Result {
  if (dispatching_) {
    return Result::Busy;
  }
  if (program_ == nullptr || program_->version_ < 3 || !std::isfinite(time) ||
      maxInstructions == 0) {
    return Result::InvalidArgument;
  }
  if (entry.programIdentity != program_ || entry.generation != generation_ ||
      entry.programGeneration != program_->generation_ ||
      entry.nodeIndex >= program_->nodeCount_) {
    return Result::InvalidHandle;
  }
  if (phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  auto status = result();
  if (status != Result::Ok) {
    return status;
  }
  auto index = entry.nodeIndex;
  auto entryInstruction = instruction(program_->bytes_, 3, index);
  if (entryInstruction[16] != customSignalOpcode) {
    return Result::InvalidValueType;
  }
  if (program_->version_ >= 8 &&
      !isBranchActive(branchGuard(entryInstruction, program_->version_))) {
    return resumeBranch >= 0 ? Result::NotReady : Result::Ok;
  }
  if (program_->version_ >= 9) {
    return dispatchSignalV9(entry, time, maxInstructions, resumeBranch);
  }
  auto profileScope = ProfileScope(profiling_, ProfileEvent::Signal);

  struct DispatchScope final {
    bool &active;
    ~DispatchScope() { active = false; }
  };
  auto scope = DispatchScope{dispatching_};
  dispatching_ = true;
  auto visited = std::size_t{0};
  auto pending = std::size_t{0};
  auto predecessor = noInput;
  auto stack =
      reinterpret_cast<std::uint8_t *>(work_) + program_->signalStackOffset_;
  auto selectBranch = [&](const std::uint8_t *current,
                          unsigned branch) noexcept -> Result {
    if (branch >= current[19]) {
      return Result::InvalidValueType;
    }
    if (program_->version_ >= 5) {
      auto offset = read32(current + 224 + branch * 8);
      auto extraCount = read32(current + 228 + branch * 8);
      if (extraCount > program_->signalStackCapacity_ - pending) {
        return Result::InvalidBinary;
      }
      for (auto extra = extraCount; extra > 0; --extra) {
        auto target = read32(program_->bytes_ + program_->signalRoutesOffset_ +
                             offset + std::size_t{extra - 1} * 4);
        auto packed = target | ((index + 1) << 16);
        std::memcpy(stack + pending++ * sizeof(packed), &packed,
                    sizeof(packed));
      }
    }
    predecessor = index;
    index = read32(current + 208 + branch * 4);
    return Result::Ok;
  };
  if (resumeBranch >= 0) {
    status = selectBranch(instruction(program_->bytes_, 3, index),
                          static_cast<unsigned>(resumeBranch));
    if (status != Result::Ok) {
      return status;
    }
  }
  while (index != noInput || pending != 0) {
    if (index == noInput) {
      auto packed = std::uint32_t{};
      std::memcpy(&packed, stack + --pending * sizeof(packed), sizeof(packed));
      index = packed & 0xffff;
      predecessor = (packed >> 16) - 1;
      continue;
    }
    if (++visited > maxInstructions) {
      return Result::InstructionBudgetExceeded;
    }
    auto current = instruction(program_->bytes_, 3, index);
    if (program_->version_ >= 8 &&
        !isBranchActive(branchGuard(current, program_->version_))) {
      index = noInput;
      continue;
    }
    auto nodeScope =
        ProfileScope(profiling_, ProfileEvent::Node, index, profiling_.nodes);
    auto descriptor = program_->bindings_[index].descriptor;
    if (descriptor == nullptr ||
        descriptor->execution != CustomExecution::Signal) {
      return Result::UnknownCustomNode;
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
    status = debugResolvedInputs(
        index, time, inputs.data(), inputWidths.data(),
        descriptor->inputIsColor,
        predecessor == noInput ? nullptr
                               : instruction(program_->bytes_, 3, predecessor));
    if (status != Result::Ok) {
      return status;
    }
    if (signalObserver_ != nullptr) {
      status = signalObserver_(signalObserverContext_, current);
      if (status != Result::Ok) {
        return status;
      }
    }
    for (auto lane = 0u; lane < current[17]; ++lane) {
      work_[index * 4 + lane] = 0;
    }
    auto invocation = CustomNodeInvocation{inputs.data(),
                                           current[18],
                                           inputWidths.data(),
                                           nullptr,
                                           0,
                                           constants.data(),
                                           std::size_t{current[18]} * 4,
                                           state,
                                           descriptor->stateBytes,
                                           time,
                                           0xff,
                                           descriptor->context,
                                           effectTime_,
                                           frameDelta_,
                                           evaluatedTimeContext_};
    invocation.realEffectTime = realEffectTime_;
    invocation.realInstanceAge = realInstanceAge_;
    invocation.realFrameDelta = realFrameDelta_;
    status = descriptor->function(invocation);
    if (status != Result::Ok) {
      return status;
    }
    if (invocation.enterPhase != 0) {
      if (invocation.enterPhase > 3) {
        return Result::InvalidArgument;
      }
      pendingPhase_ = invocation.enterPhase;
      pendingPhaseTime_ = time;
    }
    if (invocation.forceScope != 0) {
      if (!forceRequestsEnabled_) {
        return Result::UnsupportedBinary;
      }
      if (invocation.forceScope > 2) {
        return Result::InvalidArgument;
      }
      forceScope_ = invocation.forceScope;
      deferredCount_ = 0;
      return Result::Ok;
    }
    if (current[19] == 0) {
      index = noInput;
      continue;
    }
    if (invocation.selectedBranch >= current[19]) {
      return Result::InvalidValueType;
    }
    if (invocation.deferSignal) {
      auto guardId = program_->version_ >= 8
                         ? branchGuard(current, program_->version_)
                         : noInput;
      auto usesBranchTime = guardId != noInput;
      auto baseTime = time;
      if (usesBranchTime) {
        if (guardId >= program_->branchGuardCount_ || guardId >= 0xff) {
          return Result::UnsupportedBinary;
        }
        auto clock = reinterpret_cast<const std::uint8_t *>(work_) +
                     program_->branchClockOffset_ + std::size_t{guardId} * 24;
        baseTime = readDouble(clock);
      }
      auto due = baseTime + invocation.delaySeconds;
      if (!std::isfinite(invocation.delaySeconds) ||
          invocation.delaySeconds < 0 || !std::isfinite(due)) {
        return Result::InvalidArgument;
      }
      if (!deferredSignals_) {
        return Result::NotReady;
      }
      if (deferredCount_ == deferredCapacity_) {
        return Result::QueueRejected;
      }
      deferredSignals_[deferredCount_++] = {
          due, index, invocation.selectedBranch,
          usesBranchTime ? static_cast<std::uint8_t>(guardId)
                         : std::uint8_t{0xff},
          usesBranchTime};
      index = noInput;
      continue;
    }
    status = selectBranch(current, invocation.selectedBranch);
    if (status != Result::Ok) {
      return status;
    }
  }
  return Result::Ok;
}

void Instance::run(void *context) noexcept {
  auto instance = static_cast<Instance *>(context);
  if (instance->pendingPhase_ != 0) {
    instance->currentPhase_ = instance->pendingPhase_ - 1;
    instance->phaseStartTime_ = instance->pendingPhaseTime_;
    instance->pendingPhase_ = 0;
  }
  instance->result_ = instance->evaluate();
  if (instance->program_->version_ >= 9) {
    if (instance->result_ == Result::Ok) {
      for (auto logicalIndex = 0u;
           logicalIndex < instance->program_->logicalInstructionCount_;
           ++logicalIndex) {
        auto record = instance->program_->bytes_ +
                      instance->program_->logicalInstructionRecordsOffset_ +
                      std::size_t{logicalIndex} * logicalInstructionRecordSize;
        auto nodeIndex = read32(record);
        auto current = instruction(instance->program_->bytes_, 3, nodeIndex);
        if (!instance->isBranchActive(
                branchGuard(current, instance->program_->version_))) {
          continue;
        }
        auto binding = instance->program_->bindings_[nodeIndex];
        if (binding.logicalDescriptor == nullptr ||
            binding.logicalDescriptor->commit == nullptr) {
          continue;
        }
        auto state = reinterpret_cast<std::uint8_t *>(instance->work_) +
                     stateOffset(instance->program_->bindings_, nodeIndex);
        auto invocation =
            LogicalNodeInvocation{nullptr,
                                  0,
                                  nullptr,
                                  instance->work_ + nodeIndex * 4,
                                  binding.logicalDescriptor->outputWidth,
                                  state,
                                  binding.logicalDescriptor->stateBytes,
                                  read32(record + 24),
                                  instance->pendingEvaluationSerial_,
                                  0xff,
                                  0,
                                  instance->time_};
        binding.logicalDescriptor->commit(invocation);
      }
      instance->evaluationSerial_ = instance->pendingEvaluationSerial_;
      instance->generatedSignalsPending_ =
          instance->program_->logicalInstructionCount_ != 0;
    } else {
      auto eventMasks = reinterpret_cast<std::uint8_t *>(instance->work_) +
                        instance->program_->logicalEventMaskOffset_;
      std::memset(eventMasks, 0,
                  std::size_t{instance->program_->nodeCount_} *
                      sizeof(std::uint64_t));
    }
  }
  instance->phase_.store(Phase::Complete, std::memory_order_release);
}
} // namespace liberafx
