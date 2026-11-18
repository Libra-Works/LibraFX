#include "LbVfxRuntimeValueInternal.h"

namespace liberafx {
using namespace vfx_runtime_detail;

auto Instance::initialize(const Program &program, double *work,
                          std::size_t workCount) noexcept -> Result {
  return initialize(program, work, workCount, nullptr, 0);
}

auto Instance::initialize(const Program &program, double *work,
                          std::size_t workCount, double *defaults,
                          std::size_t defaultsCount) noexcept -> Result {
  if (dispatching_ || debugging_.beforeNode != nullptr) {
    return Result::Busy;
  }
  if (phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  auto profileScope = ProfileScope(profiling_, ProfileEvent::Initialize);
  auto workBytes = program.workBytes();
  auto workRequiredCount =
      workBytes / sizeof(double) + (workBytes % sizeof(double) == 0 ? 0 : 1);
  auto defaultsRequiredCount = program.parameterValueCount();
  if (program.bytes_ == nullptr || (workRequiredCount > 0 && work == nullptr)) {
    return Result::InvalidArgument;
  }
  if (workCount < workRequiredCount ||
      (defaults != nullptr && defaultsCount < defaultsRequiredCount)) {
    return Result::InsufficientWork;
  }
  if (defaults == nullptr && defaultsCount != 0) {
    return Result::InvalidArgument;
  }
  if (workRequiredCount != 0 && !isAligned(work, program.workAlignment())) {
    return Result::InvalidArgument;
  }
  if (defaults != nullptr &&
      !isAligned(defaults, program.defaultsAlignment())) {
    return Result::InvalidArgument;
  }
  if (defaults != nullptr &&
      rangesOverlap(work, workRequiredCount * sizeof(double), defaults,
                    defaultsRequiredCount * sizeof(double))) {
    return Result::InvalidArgument;
  }
  if (defaults != nullptr) {
    for (auto index = std::uint32_t{0}; index < program.nodeCount_; ++index) {
      auto current = instruction(program.bytes_, program.version_, index);
      if (program.version_ == 1) {
        defaults[index * 2] = readDouble(current + 28);
        defaults[index * 2 + 1] = readDouble(current + 36);
      } else if (program.version_ == 2 || current[16] <= 39 ||
                 current[16] == curveOpcode ||
                 (current[16] >= int32Opcode &&
                  current[16] <= int32ToFloatOpcode)) {
        for (auto lane = 0u; lane < 8; ++lane) {
          defaults[index * (program.version_ >= 3 ? 16 : 8) + lane] =
              readDouble(current +
                         (lane < 4 ? 32 + lane * 8 : 64 + (lane - 4) * 8));
        }
      } else if (current[16] == enumValueOpcode ||
                 current[16] == enumSelectOpcode) {
        for (auto lane = 0u; lane < 4; ++lane) {
          defaults[index * 16 + lane] = readDouble(current + 32 + lane * 8);
          defaults[index * 16 + 4 + lane] = readDouble(current + 64 + lane * 8);
        }
      } else {
        for (auto input = 0u; input < customMaxPorts; ++input) {
          for (auto lane = 0u; lane < 4; ++lane) {
            defaults[index * 16 + input * 4 + lane] =
                readDouble(current + 80 + input * 32 + lane * 8);
          }
        }
      }
    }
  }
  cleanupStates();
  program_ = &program;
  work_ = work;
  defaults_ = defaults;
  stateWork_ = work == nullptr
                   ? nullptr
                   : reinterpret_cast<double *>(
                         reinterpret_cast<std::uint8_t *>(work) +
                         std::size_t{program.nodeCount_} *
                             (program.version_ == 1 ? sizeof(double)
                                                    : 4 * sizeof(double)));
  if (program.version_ >= 3 && program.stateBytes_ != 0) {
    std::memset(stateWork_, 0, program.stateBytes_);
  }
  initializedStateCount_ = 0;
  updateSerial_ = 0;
  evaluationSerial_ = 0;
  pendingEvaluationSerial_ = 0;
  if (program.version_ >= 3 && program.bindings_ != nullptr) {
    for (auto index = std::size_t{0}; index < program.nodeCount_; ++index) {
      auto descriptor = program.bindings_[index].descriptor;
      if (descriptor == nullptr) {
        continue;
      }
      auto state = reinterpret_cast<std::uint8_t *>(work) +
                   stateOffset(program.bindings_, index);
      if (descriptor->initialize != nullptr) {
        auto status = descriptor->initialize(state, descriptor->stateBytes,
                                             descriptor->context);
        if (status != Result::Ok) {
          if (descriptor->cleanup != nullptr) {
            descriptor->cleanup(state, descriptor->stateBytes,
                                descriptor->context);
          }
          cleanupStates();
          program_ = nullptr;
          work_ = nullptr;
          defaults_ = nullptr;
          stateWork_ = nullptr;
          result_ = Result::NotReady;
          phase_.store(Phase::Idle, std::memory_order_release);
          return status;
        }
      }
      initializedStateCount_ = index + 1;
    }
  }
  generation_ = nextInstanceGeneration.fetch_add(1, std::memory_order_relaxed);
  deferredSignals_ = nullptr;
  deferredCapacity_ = 0;
  deferredCount_ = 0;
  logicalQueueHead_ = 0;
  logicalQueueCount_ = 0;
  generatedQueueSeeded_ = false;
  generatedSignalsPending_ = false;
  forceRequestsEnabled_ = false;
  forceScope_ = 0;
  currentPhase_ = pendingPhase_ = 0;
  phaseStartTime_ = pendingPhaseTime_ = 0;
  quality_ = {};
  pendingTimeContext_ = false;
  evaluatedTimeContext_ = false;
  result_ = Result::NotReady;
  phase_.store(Phase::Idle, std::memory_order_release);
  return Result::Ok;
}

Instance::~Instance() { cleanupStates(); }

auto Instance::setProfiling(const ProfilingHooks &profiling) noexcept
    -> Result {
  if (dispatching_ ||
      phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  if (!profiling.valid()) {
    return Result::InvalidArgument;
  }
  profiling_ = profiling;
  return Result::Ok;
}

auto Instance::setDebugHooks(const ExecutionDebugHooks &hooks) noexcept
    -> Result {
  if (dispatching_ ||
      phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  if (hooks.beforeNode != nullptr && program_ == nullptr) {
    return Result::NotReady;
  }
  debugging_ = hooks;
  return Result::Ok;
}

auto Instance::beforeDebugNode(std::uint32_t index) noexcept -> Result {
  if (debugging_.beforeNode == nullptr) {
    return Result::Ok;
  }
  auto current = instruction(program_->bytes_, program_->version_, index);
  auto opcode = current[16];
  auto values = std::array<DebugInputValue, 2>{};
  auto count =
      opcode == 2 || opcode == curveOpcode || opcode == branchTimeOpcode ||
              opcode == currentPhaseOpcode || opcode == phaseTimeOpcode
          ? std::size_t{}
      : ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22) ||
         (opcode >= int32AddOpcode && opcode <= int32DivideOpcode))
          ? std::size_t{2}
          : std::size_t{1};
  for (auto slot = std::size_t{}; slot < count; ++slot) {
    auto &value = values[slot];
    value.slot = static_cast<std::uint8_t>(slot);
    value.kind = DebugValueKind::Numeric;
    if (program_->version_ == 1) {
      auto source = read32(current + 20 + slot * 4);
      value.width = 1;
      value.connected = source != noInput;
      if (value.connected) {
        std::memcpy(value.sourceNodeId,
                    instruction(program_->bytes_, 1, source), 16);
      }
      value.components[0] = value.connected ? work_[source]
                            : defaults_ != nullptr
                                ? defaults_[index * 2 + slot]
                                : readDouble(current + 28 + slot * 8);
      continue;
    }
    auto source = read32(current + 24 + slot * 4);
    value.connected = source != noInput;
    if (value.connected) {
      std::memcpy(value.sourceNodeId,
                  instruction(program_->bytes_, program_->version_, source),
                  16);
    }
    value.width = current[17];
    if (opcode == enumValueOpcode) {
      value.kind = DebugValueKind::Int32;
    }
    if (opcode >= 23 && opcode <= 25) {
      value.width = 1;
    }
    if (opcode >= 29 && opcode <= 34) {
      value.width = opcode <= 30 ? 2 : opcode <= 32 ? 3 : 4;
    }
    if (opcode >= 35 && opcode <= 39) {
      value.width = 4;
    }
    if (value.width == 4 &&
        (opcode == 10 || opcode == 38 || (current[20] & (2u << slot)) != 0)) {
      value.kind = DebugValueKind::Color;
    }
    if (program_->version_ >= 10) {
      auto type =
          valueTypeRecord(program_->bytes_, program_->valueTypeMetadataOffset_,
                          index)[2 + slot];
      value.width = valueTypeWidth(type);
      value.kind = type == valueTypeInt32 || type == valueTypeEnum
                       ? DebugValueKind::Int32
                   : type == valueTypeColor ? DebugValueKind::Color
                                            : DebugValueKind::Numeric;
    }
    for (auto lane = 0u; lane < value.width; ++lane) {
      value.components[lane] =
          value.connected ? work_[source * 4 + current[21 + slot] + lane]
          : defaults_ != nullptr
              ? defaults_[index * (program_->version_ >= 3 ? 16 : 8) +
                          slot * 4 + lane]
              : readDouble(current + 32 + slot * 32 + lane * 8);
    }
  }
  return debugging_.beforeNode(debugging_.context,
                               {current, values.data(), count, time_});
}

#include "ExecutionDebugCapture.inl"

void Instance::cleanupStates() noexcept {
  if (program_ == nullptr || program_->version_ < 3 || work_ == nullptr) {
    return;
  }
  auto profileScope = ProfileScope(profiling_, ProfileEvent::Cleanup);
  for (auto index = initializedStateCount_; index > 0; --index) {
    auto nodeIndex = index - 1;
    auto descriptor = program_->bindings_[nodeIndex].descriptor;
    if (descriptor == nullptr || descriptor->cleanup == nullptr) {
      continue;
    }
    auto state = reinterpret_cast<std::uint8_t *>(work_) +
                 stateOffset(program_->bindings_, nodeIndex);
    descriptor->cleanup(state, descriptor->stateBytes, descriptor->context);
  }
  initializedStateCount_ = 0;
}

auto Instance::bindValue(const std::uint8_t nodeId[16],
                         ValueHandle &out) const noexcept -> Result {
  auto profileScope = ProfileScope(profiling_, ProfileEvent::Bind,
                                   noProfileNode, profiling_.accessors);
  if (program_ == nullptr || nodeId == nullptr) {
    return Result::InvalidArgument;
  }
  for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index) {
    if (std::memcmp(instruction(program_->bytes_, program_->version_, index),
                    nodeId, 16) == 0) {
      auto node = instruction(program_->bytes_, program_->version_, index);
      auto width = program_->version_ == 1 ? 1 : node[17];
      if (program_->version_ >= 3 && node[16] == customSignalOpcode) {
        return Result::InvalidValueType;
      }
      out = {program_, generation_, program_->generation_, index,
             static_cast<std::uint8_t>(width)};
      return Result::Ok;
    }
  }
  return Result::NodeNotFound;
}

auto Instance::bindParameter(const std::uint8_t nodeId[16],
                             std::uint8_t inputSlot, std::uint8_t component,
                             ParameterHandle &out) const noexcept -> Result {
  auto profileScope = ProfileScope(profiling_, ProfileEvent::Bind,
                                   noProfileNode, profiling_.accessors);
  if (program_ == nullptr || nodeId == nullptr) {
    return Result::InvalidArgument;
  }
  for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index) {
    if (std::memcmp(instruction(program_->bytes_, program_->version_, index),
                    nodeId, 16) == 0) {
      auto current = instruction(program_->bytes_, program_->version_, index);
      auto laneCount = std::uint8_t{0};
      if (program_->version_ >= 3 && (current[16] == customParameterOpcode ||
                                      current[16] == customSignalOpcode)) {
        laneCount =
            inputSlot < current[18]
                ? program_->bindings_[index].descriptor->inputWidths[inputSlot]
                : 0;
      } else {
        auto opcode = current[16];
        auto width = program_->version_ == 1 ? 1u : current[17];
        if (program_->version_ == 1) {
          laneCount = (inputSlot == 0 &&
                       (opcode == 1 || (opcode >= 3 && opcode <= 6))) ||
                              (inputSlot == 1 && opcode >= 3 && opcode <= 6)
                          ? 1
                          : 0;
        } else if (inputSlot == 0) {
          if (opcode == enumValueOpcode) {
            laneCount = 1;
          }
          if (opcode == 1 || (opcode >= 7 && opcode <= 10) ||
              (opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22)) {
            laneCount = width;
          } else if (opcode == 29 || opcode == 30 || opcode == 32) {
            laneCount = 1;
          } else if (opcode >= 35 && opcode <= 37) {
            laneCount = width;
          } else if (opcode == int32Opcode ||
                     (opcode >= int32AddOpcode &&
                      opcode <= int32DivideOpcode) ||
                     opcode == floatToInt32Opcode ||
                     opcode == int32ToFloatOpcode) {
            laneCount = 1;
          }
        } else if ((opcode >= 3 && opcode <= 6) ||
                   (opcode >= 11 && opcode <= 22)) {
          laneCount = width;
        } else if (inputSlot == 1 && opcode >= int32AddOpcode &&
                   opcode <= int32DivideOpcode) {
          laneCount = 1;
        }
      }
      if (component >= laneCount) {
        return Result::InvalidValueType;
      }
      out = {program_, generation_, program_->generation_,
             index,    inputSlot,   component};
      return Result::Ok;
    }
  }
  return Result::NodeNotFound;
}

auto Instance::bindSignal(const std::uint8_t nodeId[16],
                          SignalHandle &out) const noexcept -> Result {
  auto profileScope = ProfileScope(profiling_, ProfileEvent::Bind,
                                   noProfileNode, profiling_.accessors);
  if (program_ == nullptr || program_->version_ < 3 || nodeId == nullptr) {
    return Result::InvalidArgument;
  }
  for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index) {
    if (std::memcmp(instruction(program_->bytes_, program_->version_, index),
                    nodeId, 16) == 0) {
      if (instruction(program_->bytes_, program_->version_, index)[16] !=
          customSignalOpcode) {
        return Result::InvalidValueType;
      }
      out = {program_, generation_, program_->generation_, index};
      return Result::Ok;
    }
  }
  return Result::NodeNotFound;
}

auto Instance::value(const ValueHandle &handle, double out[4]) const noexcept
    -> Result {
  auto profileScope = ProfileScope(profiling_, ProfileEvent::ReadValue,
                                   handle.nodeIndex, profiling_.accessors);
  if (out == nullptr) {
    return Result::InvalidArgument;
  }
  if (program_ == nullptr || handle.programIdentity != program_ ||
      handle.generation != generation_ ||
      handle.programGeneration != program_->generation_ ||
      handle.nodeIndex >= program_->nodeCount_) {
    return Result::InvalidHandle;
  }
  if (handle.componentCount == 0 || handle.componentCount > 4) {
    return Result::InvalidHandle;
  }
  auto node =
      instruction(program_->bytes_, program_->version_, handle.nodeIndex);
  if ((program_->version_ >= 3 && node[16] == customSignalOpcode) ||
      handle.componentCount != (program_->version_ == 1 ? 1 : node[17])) {
    return Result::InvalidHandle;
  }
  auto status = result();
  if (status != Result::Ok) {
    return status;
  }
  auto base = handle.nodeIndex * (program_->version_ == 1 ? 1 : 4);
  for (auto lane = 0u; lane < handle.componentCount; ++lane) {
    out[lane] = work_[base + lane];
  }
  for (auto lane = handle.componentCount; lane < 4; ++lane) {
    out[lane] = 0;
  }
  return Result::Ok;
}

auto Instance::valueInt32(const ValueHandle &handle,
                          std::int32_t &out) const noexcept -> Result {
  auto values = std::array<double, 4>{};
  auto status = value(handle, values.data());
  if (status != Result::Ok) {
    return status;
  }
  auto type = ValueType::None;
  status = program_->valueType(handle.nodeIndex, type);
  if (status != Result::Ok) {
    return status;
  }
  if (type != ValueType::Int32 || handle.componentCount != 1) {
    return Result::InvalidValueType;
  }
  return int32FromDouble(values[0], out);
}

auto Instance::parameter(const ParameterHandle &handle,
                         double &out) const noexcept -> Result {
  auto profileScope = ProfileScope(profiling_, ProfileEvent::ReadParameter,
                                   handle.nodeIndex, profiling_.accessors);
  if (phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  if (program_ == nullptr || handle.programIdentity != program_ ||
      handle.generation != generation_ ||
      handle.programGeneration != program_->generation_ ||
      handle.nodeIndex >= program_->nodeCount_) {
    return Result::InvalidHandle;
  }
  auto node =
      instruction(program_->bytes_, program_->version_, handle.nodeIndex);
  auto valid = false;
  if (program_->version_ >= 3 &&
      (node[16] == customParameterOpcode || node[16] == customSignalOpcode)) {
    auto descriptor = program_->bindings_[handle.nodeIndex].descriptor;
    valid = descriptor != nullptr && handle.inputSlot < node[18] &&
            handle.component < descriptor->inputWidths[handle.inputSlot];
  } else {
    auto opcode = node[16];
    auto width = program_->version_ == 1 ? 1u : node[17];
    auto laneCount = 0u;
    if (program_->version_ == 1) {
      if ((handle.inputSlot == 0 &&
           (opcode == 1 || (opcode >= 3 && opcode <= 6))) ||
          (handle.inputSlot == 1 && opcode >= 3 && opcode <= 6)) {
        laneCount = 1;
      }
    } else if (handle.inputSlot == 0) {
      if (opcode == enumValueOpcode) {
        laneCount = 1;
      }
      if (opcode == 1 || (opcode >= 7 && opcode <= 10) ||
          (opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22)) {
        laneCount = width;
      } else if (opcode == 29 || opcode == 30 || opcode == 32) {
        laneCount = 1;
      } else if (opcode >= 35 && opcode <= 37) {
        laneCount = width;
      } else if (opcode == int32Opcode ||
                 (opcode >= int32AddOpcode && opcode <= int32DivideOpcode) ||
                 opcode == floatToInt32Opcode || opcode == int32ToFloatOpcode) {
        laneCount = 1;
      }
    } else if (handle.inputSlot == 1 &&
               ((opcode >= 3 && opcode <= 6) ||
                (opcode >= 11 && opcode <= 22) ||
                (opcode >= int32AddOpcode && opcode <= int32DivideOpcode))) {
      laneCount = width;
    }
    valid = handle.inputSlot <= 1 && handle.component < laneCount;
  }
  if (!valid) {
    return Result::InvalidHandle;
  }
  if (program_->version_ == 1) {
    out = defaults_ == nullptr
              ? readDouble(node + (handle.inputSlot == 0 ? 28 : 36))
              : defaults_[handle.nodeIndex * 2 + handle.inputSlot];
  } else if (program_->version_ >= 3 && (node[16] == customParameterOpcode ||
                                         node[16] == customSignalOpcode)) {
    out = defaults_ == nullptr
              ? readDouble(node + 80 + handle.inputSlot * 32 +
                           handle.component * 8)
              : defaults_[handle.nodeIndex * 16 + handle.inputSlot * 4 +
                          handle.component];
  } else {
    out =
        defaults_ == nullptr
            ? readDouble(node + (handle.inputSlot == 0 ? 32 : 64) +
                         handle.component * 8)
            : defaults_[handle.nodeIndex * (program_->version_ >= 3 ? 16 : 8) +
                        handle.inputSlot * 4 + handle.component];
  }
  return Result::Ok;
}

auto Instance::setParameter(const ParameterHandle &handle,
                            double value) noexcept -> Result {
  auto profileScope = ProfileScope(profiling_, ProfileEvent::WriteParameter,
                                   handle.nodeIndex, profiling_.accessors);
  if (dispatching_) {
    return Result::Busy;
  }
  if (phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  if (program_ == nullptr || defaults_ == nullptr ||
      handle.programIdentity != program_ || handle.generation != generation_ ||
      handle.programGeneration != program_->generation_ ||
      handle.nodeIndex >= program_->nodeCount_) {
    return Result::InvalidHandle;
  }
  if (!std::isfinite(value)) {
    return Result::NonFinite;
  }
  auto node =
      instruction(program_->bytes_, program_->version_, handle.nodeIndex);
  if (program_->version_ >= 10 && handle.inputSlot < 4 &&
      valueTypeRecord(program_->bytes_, program_->valueTypeMetadataOffset_,
                      handle.nodeIndex)[2 + handle.inputSlot] ==
          valueTypeInt32 &&
      !validInt32(value)) {
    return Result::InvalidValueType;
  }
  if (node[16] == enumValueOpcode &&
      (handle.inputSlot != 0 || handle.component != 0 ||
       std::trunc(value) != value ||
       value < std::numeric_limits<std::int32_t>::min() ||
       value > std::numeric_limits<std::int32_t>::max())) {
    return Result::InvalidValueType;
  }
  if (program_->version_ >= 3 &&
      (node[16] == customParameterOpcode || node[16] == customSignalOpcode)) {
    auto descriptor = program_->bindings_[handle.nodeIndex].descriptor;
    if (descriptor == nullptr || handle.inputSlot >= node[18] ||
        handle.component >= descriptor->inputWidths[handle.inputSlot]) {
      return Result::InvalidHandle;
    }
    if (descriptor->inputIsColor[handle.inputSlot] != 0 &&
        (value < 0 || (handle.component == 3 && value > 1))) {
      return Result::InvalidValueType;
    }
    defaults_[handle.nodeIndex * 16 + handle.inputSlot * 4 + handle.component] =
        value;
  } else {
    auto opcode = node[16];
    auto width = program_->version_ == 1 ? 1u : node[17];
    auto laneCount = 0u;
    if (program_->version_ == 1) {
      if ((handle.inputSlot == 0 &&
           (opcode == 1 || (opcode >= 3 && opcode <= 6))) ||
          (handle.inputSlot == 1 && opcode >= 3 && opcode <= 6)) {
        laneCount = 1;
      }
    } else if (handle.inputSlot == 0) {
      if (opcode == enumValueOpcode) {
        laneCount = 1;
      }
      if (opcode == 1 || (opcode >= 7 && opcode <= 10) ||
          (opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22)) {
        laneCount = width;
      } else if (opcode == 29 || opcode == 30 || opcode == 32) {
        laneCount = 1;
      } else if (opcode >= 35 && opcode <= 37) {
        laneCount = width;
      } else if (opcode == int32Opcode ||
                 (opcode >= int32AddOpcode && opcode <= int32DivideOpcode) ||
                 opcode == floatToInt32Opcode || opcode == int32ToFloatOpcode) {
        laneCount = 1;
      }
    } else if (handle.inputSlot == 1 &&
               ((opcode >= 3 && opcode <= 6) ||
                (opcode >= 11 && opcode <= 22) ||
                (opcode >= int32AddOpcode && opcode <= int32DivideOpcode))) {
      laneCount = width;
    }
    if (handle.inputSlot > 1 || handle.component >= laneCount) {
      return Result::InvalidHandle;
    }
    if (program_->version_ == 1) {
      defaults_[handle.nodeIndex * 2 + handle.inputSlot] = value;
    } else {
      defaults_[handle.nodeIndex * (program_->version_ >= 3 ? 16 : 8) +
                handle.inputSlot * 4 + handle.component] = value;
    }
  }
  return Result::Ok;
}

auto Instance::setParameterInt32(const ParameterHandle &handle,
                                 std::int32_t value) noexcept -> Result {
  auto currentValue = 0.0;
  auto status = parameter(handle, currentValue);
  if (status != Result::Ok) {
    return status;
  }
  if (program_ == nullptr || program_->version_ < 10 || handle.inputSlot >= 4 ||
      valueTypeRecord(program_->bytes_, program_->valueTypeMetadataOffset_,
                      handle.nodeIndex)[2 + handle.inputSlot] !=
          valueTypeInt32) {
    return Result::InvalidValueType;
  }
  return setParameter(handle, static_cast<double>(value));
}

auto Instance::setHostQuality(const HostQualitySnapshot &snapshot) noexcept
    -> Result {
  if (dispatching_ ||
      phase_.load(std::memory_order_acquire) == Phase::Running ||
      generatedSignalsPending_ || logicalQueueCount_ != 0) {
    return Result::Busy;
  }
  if (!program_) {
    return Result::InvalidArgument;
  }
  if (static_cast<unsigned>(snapshot.availability) >
          static_cast<unsigned>(HostQualityAvailability::Unsupported) ||
      (snapshot.availability == HostQualityAvailability::Known &&
       (static_cast<unsigned>(snapshot.tier) >
            static_cast<unsigned>(HostQualityTier::Cinematic) ||
        static_cast<unsigned>(snapshot.source) >
            static_cast<unsigned>(HostQualitySource::PlatformPolicy)))) {
    return Result::InvalidValueType;
  }
  quality_ = snapshot;
  return Result::Ok;
}

auto Instance::setTimeContext(double effectTime, double frameDelta) noexcept
    -> Result {
  if (dispatching_ ||
      phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  if (!program_ || !std::isfinite(effectTime) || !std::isfinite(frameDelta) ||
      effectTime < 0 || frameDelta < 0) {
    return Result::InvalidArgument;
  }
  pendingEffectTime_ = effectTime;
  pendingFrameDelta_ = frameDelta;
  pendingTimeContext_ = true;
  return Result::Ok;
}

auto Instance::clearTimeContext() noexcept -> Result {
  if (dispatching_ ||
      phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  if (!program_) {
    return Result::InvalidArgument;
  }
  pendingTimeContext_ = false;
  return Result::Ok;
}

auto Instance::updateAsync(double time, Enqueue enqueue,
                           void *queueContext) noexcept -> Result {
  if (dispatching_) {
    return Result::Busy;
  }
  if (program_ == nullptr || enqueue == nullptr || !std::isfinite(time)) {
    return Result::InvalidArgument;
  }
  if (program_->version_ >= 9 && generatedSignalsPending_) {
    return Result::Busy;
  }
  auto profileScope = ProfileScope(profiling_, ProfileEvent::Schedule);
  auto phase = phase_.load(std::memory_order_acquire);
  for (;;) {
    if (phase == Phase::Running) {
      return Result::Busy;
    }
    if (phase_.compare_exchange_weak(phase, Phase::Running,
                                     std::memory_order_acq_rel)) {
      break;
    }
  }
  time_ = time;
  evaluatedTimeContext_ = pendingTimeContext_;
  effectTime_ = pendingEffectTime_;
  frameDelta_ = pendingFrameDelta_;
  pendingTimeContext_ = false;
  if (!enqueue(queueContext, &Instance::run, this)) {
    pendingTimeContext_ = evaluatedTimeContext_;
    phase_.store(Phase::Idle, std::memory_order_release);
    return Result::QueueRejected;
  }
  return Result::Ok;
}

auto Instance::setParameter(const std::uint8_t nodeId[16],
                            std::uint8_t inputSlot, std::uint8_t component,
                            double value) noexcept -> Result {
  auto profileScope = ProfileScope(profiling_, ProfileEvent::WriteParameter,
                                   noProfileNode, profiling_.accessors);
  if (dispatching_) {
    return Result::Busy;
  }
  if (phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  if (program_ == nullptr || defaults_ == nullptr || nodeId == nullptr ||
      inputSlot > (program_->version_ >= 3 ? 3 : 1)) {
    return Result::InvalidArgument;
  }
  if (!std::isfinite(value)) {
    return Result::NonFinite;
  }
  for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index) {
    auto current = instruction(program_->bytes_, program_->version_, index);
    if (std::memcmp(current, nodeId, 16) != 0) {
      continue;
    }
    auto opcode = current[16];
    if (program_->version_ >= 10 &&
        valueTypeRecord(program_->bytes_, program_->valueTypeMetadataOffset_,
                        index)[2 + inputSlot] == valueTypeInt32 &&
        !validInt32(value)) {
      return Result::InvalidValueType;
    }
    if (program_->version_ >= 3 &&
        (opcode == customParameterOpcode || opcode == customSignalOpcode)) {
      if (inputSlot >= current[18] || component >= 4) {
        return Result::InvalidValueType;
      }
      auto descriptor = program_->bindings_[index].descriptor;
      if (descriptor == nullptr ||
          component >= descriptor->inputWidths[inputSlot]) {
        return Result::InvalidValueType;
      }
      if (descriptor->inputIsColor[inputSlot] != 0 &&
          (value < 0 || (component == 3 && value > 1))) {
        return Result::InvalidValueType;
      }
      defaults_[index * 16 + inputSlot * 4 + component] = value;
      return Result::Ok;
    }
    auto width = program_->version_ == 1 ? 1u : current[17];
    auto laneCount = 0u;
    if (program_->version_ == 1) {
      if ((inputSlot == 0 && (opcode == 1 || (opcode >= 3 && opcode <= 6))) ||
          (inputSlot == 1 && opcode >= 3 && opcode <= 6)) {
        laneCount = 1;
      }
    } else if (inputSlot == 0) {
      if (opcode == 1 || (opcode >= 7 && opcode <= 10)) {
        laneCount = width;
      } else if ((opcode >= 3 && opcode <= 6) ||
                 (opcode >= 11 && opcode <= 22)) {
        laneCount = width;
      } else if (opcode == 29 || opcode == 30 || opcode == 32) {
        laneCount = 1;
      } else if (opcode >= 35 && opcode <= 37) {
        laneCount = width;
      } else if (opcode == int32Opcode ||
                 (opcode >= int32AddOpcode && opcode <= int32DivideOpcode) ||
                 opcode == floatToInt32Opcode || opcode == int32ToFloatOpcode) {
        laneCount = 1;
      }
    } else if ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22)) {
      laneCount = width;
    } else if (inputSlot == 1 && opcode >= int32AddOpcode &&
               opcode <= int32DivideOpcode) {
      laneCount = 1;
    }
    if (component >= laneCount) {
      return Result::InvalidValueType;
    }
    if (opcode >= 26 && opcode <= 28) {
      auto inputWidth = current[18];
      if (value < 0 || value >= inputWidth || std::floor(value) != value) {
        return Result::InvalidValueType;
      }
    }
    if (opcode >= 35 && opcode <= 37 &&
        (value < 0 || value >= 4 || std::floor(value) != value)) {
      return Result::InvalidValueType;
    }
    if (opcode == 10 && (value < 0 || (component == 3 && value > 1))) {
      return Result::InvalidValueType;
    }
    if (program_->version_ == 1) {
      defaults_[index * 2 + inputSlot] = value;
    } else if (program_->version_ >= 3) {
      defaults_[index * 16 + inputSlot * 4 + component] = value;
    } else {
      defaults_[index * 8 + inputSlot * 4 + component] = value;
    }
    return Result::Ok;
  }
  return Result::NodeNotFound;
}

auto Instance::result() const noexcept -> Result {
  return phase_.load(std::memory_order_acquire) == Phase::Complete
             ? result_
             : Result::NotReady;
}

auto Instance::setSignalObserver(SignalObserver observer,
                                 void *context) noexcept -> Result {
  if (dispatching_ ||
      phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  if (observer != nullptr && context == nullptr) {
    return Result::InvalidArgument;
  }
  signalObserver_ = observer;
  signalObserverContext_ = context;
  return Result::Ok;
}

auto Instance::enableForceRequests() noexcept -> Result {
  if (dispatching_ ||
      phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  if (!program_) {
    return Result::NotReady;
  }
  forceRequestsEnabled_ = true;
  return Result::Ok;
}

auto Instance::pendingForceRequest(std::uint8_t &scope) const noexcept
    -> Result {
  if (dispatching_ ||
      phase_.load(std::memory_order_acquire) == Phase::Running) {
    return Result::Busy;
  }
  scope = forceScope_;
  return Result::Ok;
}

auto Instance::value(const std::uint8_t nodeId[16], double &out) const noexcept
    -> Result {
  auto components = std::array<double, 4>{};
  auto count = std::uint8_t{};
  auto status = valueComponents(nodeId, components.data(), count);
  if (status == Result::Ok) {
    if (count != 1) {
      return Result::InvalidValueType;
    }
    out = components[0];
  }
  return status;
}

auto Instance::valueComponents(const std::uint8_t nodeId[16], double out[4],
                               std::uint8_t &componentCount) const noexcept
    -> Result {
  auto profileScope = ProfileScope(profiling_, ProfileEvent::ReadValue,
                                   noProfileNode, profiling_.accessors);
  if (nodeId == nullptr || out == nullptr) {
    return Result::InvalidArgument;
  }
  auto status = result();
  if (status != Result::Ok) {
    return status;
  }
  for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index) {
    auto current = instruction(program_->bytes_, program_->version_, index);
    if (std::memcmp(current, nodeId, 16) == 0) {
      auto width = program_->version_ == 1 ? 1u : current[17];
      if (program_->version_ >= 3 && current[16] == customSignalOpcode) {
        return Result::InvalidValueType;
      }
      for (auto lane = 0u; lane < width; ++lane) {
        out[lane] = work_[index * (program_->version_ == 1 ? 1u : 4u) + lane];
      }
      for (auto lane = width; lane < 4; ++lane) {
        out[lane] = 0;
      }
      componentCount = static_cast<std::uint8_t>(width);
      return Result::Ok;
    }
  }
  return Result::NodeNotFound;
}

auto Instance::setUnconnectedParameter(const std::uint8_t nodeId[16],
                                       std::uint8_t inputSlot,
                                       std::uint8_t component,
                                       double value) noexcept -> Result {
  if (program_ == nullptr || inputSlot > (program_->version_ >= 3 ? 3 : 1)) {
    return Result::InvalidArgument;
  }
  auto handle = ParameterHandle{};
  auto status = bindParameter(nodeId, inputSlot, component, handle);
  if (status != Result::Ok) {
    return status;
  }
  auto current =
      instruction(program_->bytes_, program_->version_, handle.nodeIndex);
  auto offset =
      program_->version_ == 1 ? 20 + inputSlot * 4
      : program_->version_ >= 3 && (current[16] == customParameterOpcode ||
                                    current[16] == customSignalOpcode)
          ? 48 + inputSlot * 8
          : 24 + inputSlot * 4;
  if (read32(current + offset) != noInput) {
    return Result::InvalidValueType;
  }
  return setParameter(handle, value);
}
} // namespace liberafx
