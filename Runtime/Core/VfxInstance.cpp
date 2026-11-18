#include "VfxRuntimeInternal.h"

namespace liberafx {
using namespace vfx_runtime_detail;

auto Instance::initialize(const Program& program, double* work, std::size_t workCount) noexcept
    -> Result
{
    return initialize(program, work, workCount, nullptr, 0);
}

auto Instance::initialize(const Program& program, double* work, std::size_t workCount,
                          double* defaults, std::size_t defaultsCount) noexcept -> Result
{
    if (dispatching_ || debugging_.beforeNode != nullptr)
    {
        return Result::Busy;
    }
    if (phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Initialize);
    auto workBytes = program.workBytes();
    auto workRequiredCount = workBytes / sizeof(double) + (workBytes % sizeof(double) == 0 ? 0 : 1);
    auto defaultsRequiredCount = program.parameterValueCount();
    if (program.bytes_ == nullptr || (workRequiredCount > 0 && work == nullptr))
    {
        return Result::InvalidArgument;
    }
    if (workCount < workRequiredCount ||
        (defaults != nullptr && defaultsCount < defaultsRequiredCount))
    {
        return Result::InsufficientWork;
    }
    if (defaults == nullptr && defaultsCount != 0)
    {
        return Result::InvalidArgument;
    }
    if (workRequiredCount != 0 && !isAligned(work, program.workAlignment()))
    {
        return Result::InvalidArgument;
    }
    if (defaults != nullptr && !isAligned(defaults, program.defaultsAlignment()))
    {
        return Result::InvalidArgument;
    }
    if (defaults != nullptr && rangesOverlap(work, workRequiredCount * sizeof(double), defaults,
                                             defaultsRequiredCount * sizeof(double)))
    {
        return Result::InvalidArgument;
    }
    if (defaults != nullptr)
    {
        for (auto index = std::uint32_t{0}; index < program.nodeCount_; ++index)
        {
            auto current = instruction(program.bytes_, program.version_, index);
            if (program.version_ == 1)
            {
                defaults[index * 2] = readDouble(current + 28);
                defaults[index * 2 + 1] = readDouble(current + 36);
            } else if (program.version_ == 2 || current[16] <= 39 || current[16] == curveOpcode ||
                       (current[16] >= int32Opcode && current[16] <= int32ToFloatOpcode))
            {
                for (auto lane = 0u; lane < 8; ++lane)
                {
                    defaults[index * (program.version_ >= 3 ? 16 : 8) + lane] =
                        readDouble(current + (lane < 4 ? 32 + lane * 8 : 64 + (lane - 4) * 8));
                }
            } else if (current[16] == enumValueOpcode || current[16] == enumSelectOpcode)
            {
                for (auto lane = 0u; lane < 4; ++lane)
                {
                    defaults[index * 16 + lane] = readDouble(current + 32 + lane * 8);
                    defaults[index * 16 + 4 + lane] = readDouble(current + 64 + lane * 8);
                }
            } else
            {
                for (auto input = 0u; input < customMaxPorts; ++input)
                {
                    for (auto lane = 0u; lane < 4; ++lane)
                    {
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
                     : reinterpret_cast<double*>(
                           reinterpret_cast<std::uint8_t*>(work) +
                           std::size_t{program.nodeCount_} *
                               (program.version_ == 1 ? sizeof(double) : 4 * sizeof(double)));
    if (program.version_ >= 3 && program.stateBytes_ != 0)
    {
        std::memset(stateWork_, 0, program.stateBytes_);
    }
    initializedStateCount_ = 0;
    updateSerial_ = 0;
    evaluationSerial_ = 0;
    pendingEvaluationSerial_ = 0;
    if (program.version_ >= 3 && program.bindings_ != nullptr)
    {
        for (auto index = std::size_t{0}; index < program.nodeCount_; ++index)
        {
            auto descriptor = program.bindings_[index].descriptor;
            if (descriptor == nullptr)
            {
                continue;
            }
            auto state =
                reinterpret_cast<std::uint8_t*>(work) + stateOffset(program.bindings_, index);
            if (descriptor->initialize != nullptr)
            {
                auto status =
                    descriptor->initialize(state, descriptor->stateBytes, descriptor->context);
                if (status != Result::Ok)
                {
                    if (descriptor->cleanup != nullptr)
                    {
                        descriptor->cleanup(state, descriptor->stateBytes, descriptor->context);
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

auto Instance::setProfiling(const ProfilingHooks& profiling) noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    if (!profiling.valid())
    {
        return Result::InvalidArgument;
    }
    profiling_ = profiling;
    return Result::Ok;
}

auto Instance::setDebugHooks(const ExecutionDebugHooks& hooks) noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    if (hooks.beforeNode != nullptr && program_ == nullptr)
    {
        return Result::NotReady;
    }
    debugging_ = hooks;
    return Result::Ok;
}

auto Instance::beforeDebugNode(std::uint32_t index) noexcept -> Result
{
    if (debugging_.beforeNode == nullptr)
    {
        return Result::Ok;
    }
    auto current = instruction(program_->bytes_, program_->version_, index);
    auto opcode = current[16];
    auto values = std::array<DebugInputValue, 2>{};
    auto count = opcode == 2 || opcode == curveOpcode || opcode == branchTimeOpcode ||
                         opcode == currentPhaseOpcode || opcode == phaseTimeOpcode
                     ? std::size_t{}
                 : ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22) ||
                    (opcode >= int32AddOpcode && opcode <= int32DivideOpcode))
                     ? std::size_t{2}
                     : std::size_t{1};
    for (auto slot = std::size_t{}; slot < count; ++slot)
    {
        auto& value = values[slot];
        value.slot = static_cast<std::uint8_t>(slot);
        value.kind = DebugValueKind::Numeric;
        if (program_->version_ == 1)
        {
            auto source = read32(current + 20 + slot * 4);
            value.width = 1;
            value.connected = source != noInput;
            if (value.connected)
            {
                std::memcpy(value.sourceNodeId, instruction(program_->bytes_, 1, source), 16);
            }
            value.components[0] = value.connected        ? work_[source]
                                  : defaults_ != nullptr ? defaults_[index * 2 + slot]
                                                         : readDouble(current + 28 + slot * 8);
            continue;
        }
        auto source = read32(current + 24 + slot * 4);
        value.connected = source != noInput;
        if (value.connected)
        {
            std::memcpy(value.sourceNodeId,
                        instruction(program_->bytes_, program_->version_, source), 16);
        }
        value.width = current[17];
        if (opcode == enumValueOpcode)
        {
            value.kind = DebugValueKind::Int32;
        }
        if (opcode >= 23 && opcode <= 25)
        {
            value.width = 1;
        }
        if (opcode >= 29 && opcode <= 34)
        {
            value.width = opcode <= 30 ? 2 : opcode <= 32 ? 3 : 4;
        }
        if (opcode >= 35 && opcode <= 39)
        {
            value.width = 4;
        }
        if (value.width == 4 && (opcode == 10 || opcode == 38 || (current[20] & (2u << slot)) != 0))
        {
            value.kind = DebugValueKind::Color;
        }
        if (program_->version_ >= 10)
        {
            auto type = valueTypeRecord(program_->bytes_, program_->valueTypeMetadataOffset_,
                                        index)[2 + slot];
            value.width = valueTypeWidth(type);
            value.kind = type == valueTypeInt32 || type == valueTypeEnum ? DebugValueKind::Int32
                         : type == valueTypeColor                        ? DebugValueKind::Color
                                                                         : DebugValueKind::Numeric;
        }
        for (auto lane = 0u; lane < value.width; ++lane)
        {
            value.components[lane] =
                value.connected ? work_[source * 4 + current[21 + slot] + lane]
                : defaults_ != nullptr
                    ? defaults_[index * (program_->version_ >= 3 ? 16 : 8) + slot * 4 + lane]
                    : readDouble(current + 32 + slot * 32 + lane * 8);
        }
    }
    return debugging_.beforeNode(debugging_.context, {current, values.data(), count, time_});
}

#include "ExecutionDebugCapture.inl"

void Instance::cleanupStates() noexcept
{
    if (program_ == nullptr || program_->version_ < 3 || work_ == nullptr)
    {
        return;
    }
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Cleanup);
    for (auto index = initializedStateCount_; index > 0; --index)
    {
        auto nodeIndex = index - 1;
        auto descriptor = program_->bindings_[nodeIndex].descriptor;
        if (descriptor == nullptr || descriptor->cleanup == nullptr)
        {
            continue;
        }
        auto state =
            reinterpret_cast<std::uint8_t*>(work_) + stateOffset(program_->bindings_, nodeIndex);
        descriptor->cleanup(state, descriptor->stateBytes, descriptor->context);
    }
    initializedStateCount_ = 0;
}

auto Instance::bindValue(const std::uint8_t nodeId[16], ValueHandle& out) const noexcept -> Result
{
    auto profileScope =
        ProfileScope(profiling_, ProfileEvent::Bind, noProfileNode, profiling_.accessors);
    if (program_ == nullptr || nodeId == nullptr)
    {
        return Result::InvalidArgument;
    }
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
    {
        if (std::memcmp(instruction(program_->bytes_, program_->version_, index), nodeId, 16) == 0)
        {
            auto node = instruction(program_->bytes_, program_->version_, index);
            auto width = program_->version_ == 1 ? 1 : node[17];
            if (program_->version_ >= 3 && node[16] == customSignalOpcode)
            {
                return Result::InvalidValueType;
            }
            out = {program_, generation_, program_->generation_, index,
                   static_cast<std::uint8_t>(width)};
            return Result::Ok;
        }
    }
    return Result::NodeNotFound;
}

auto Instance::bindParameter(const std::uint8_t nodeId[16], std::uint8_t inputSlot,
                             std::uint8_t component, ParameterHandle& out) const noexcept -> Result
{
    auto profileScope =
        ProfileScope(profiling_, ProfileEvent::Bind, noProfileNode, profiling_.accessors);
    if (program_ == nullptr || nodeId == nullptr)
    {
        return Result::InvalidArgument;
    }
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
    {
        if (std::memcmp(instruction(program_->bytes_, program_->version_, index), nodeId, 16) == 0)
        {
            auto current = instruction(program_->bytes_, program_->version_, index);
            auto laneCount = std::uint8_t{0};
            if (program_->version_ >= 3 &&
                (current[16] == customParameterOpcode || current[16] == customSignalOpcode))
            {
                laneCount = inputSlot < current[18]
                                ? program_->bindings_[index].descriptor->inputWidths[inputSlot]
                                : 0;
            } else
            {
                auto opcode = current[16];
                auto width = program_->version_ == 1 ? 1u : current[17];
                if (program_->version_ == 1)
                {
                    laneCount = (inputSlot == 0 && (opcode == 1 || (opcode >= 3 && opcode <= 6))) ||
                                        (inputSlot == 1 && opcode >= 3 && opcode <= 6)
                                    ? 1
                                    : 0;
                } else if (inputSlot == 0)
                {
                    if (opcode == enumValueOpcode)
                    {
                        laneCount = 1;
                    }
                    if (opcode == 1 || (opcode >= 7 && opcode <= 10) ||
                        (opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22))
                    {
                        laneCount = width;
                    } else if (opcode == 29 || opcode == 30 || opcode == 32)
                    {
                        laneCount = 1;
                    } else if (opcode >= 35 && opcode <= 37)
                    {
                        laneCount = width;
                    } else if (opcode == int32Opcode ||
                               (opcode >= int32AddOpcode && opcode <= int32DivideOpcode) ||
                               opcode == floatToInt32Opcode || opcode == int32ToFloatOpcode)
                    {
                        laneCount = 1;
                    }
                } else if ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22))
                {
                    laneCount = width;
                } else if (inputSlot == 1 && opcode >= int32AddOpcode &&
                           opcode <= int32DivideOpcode)
                {
                    laneCount = 1;
                }
            }
            if (component >= laneCount)
            {
                return Result::InvalidValueType;
            }
            out = {program_, generation_, program_->generation_, index, inputSlot, component};
            return Result::Ok;
        }
    }
    return Result::NodeNotFound;
}

auto Instance::bindSignal(const std::uint8_t nodeId[16], SignalHandle& out) const noexcept -> Result
{
    auto profileScope =
        ProfileScope(profiling_, ProfileEvent::Bind, noProfileNode, profiling_.accessors);
    if (program_ == nullptr || program_->version_ < 3 || nodeId == nullptr)
    {
        return Result::InvalidArgument;
    }
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
    {
        if (std::memcmp(instruction(program_->bytes_, program_->version_, index), nodeId, 16) == 0)
        {
            if (instruction(program_->bytes_, program_->version_, index)[16] != customSignalOpcode)
            {
                return Result::InvalidValueType;
            }
            out = {program_, generation_, program_->generation_, index};
            return Result::Ok;
        }
    }
    return Result::NodeNotFound;
}

auto Instance::value(const ValueHandle& handle, double out[4]) const noexcept -> Result
{
    auto profileScope =
        ProfileScope(profiling_, ProfileEvent::ReadValue, handle.nodeIndex, profiling_.accessors);
    if (out == nullptr)
    {
        return Result::InvalidArgument;
    }
    if (program_ == nullptr || handle.programIdentity != program_ ||
        handle.generation != generation_ || handle.programGeneration != program_->generation_ ||
        handle.nodeIndex >= program_->nodeCount_)
    {
        return Result::InvalidHandle;
    }
    if (handle.componentCount == 0 || handle.componentCount > 4)
    {
        return Result::InvalidHandle;
    }
    auto node = instruction(program_->bytes_, program_->version_, handle.nodeIndex);
    if ((program_->version_ >= 3 && node[16] == customSignalOpcode) ||
        handle.componentCount != (program_->version_ == 1 ? 1 : node[17]))
    {
        return Result::InvalidHandle;
    }
    auto status = result();
    if (status != Result::Ok)
    {
        return status;
    }
    auto base = handle.nodeIndex * (program_->version_ == 1 ? 1 : 4);
    for (auto lane = 0u; lane < handle.componentCount; ++lane)
    {
        out[lane] = work_[base + lane];
    }
    for (auto lane = handle.componentCount; lane < 4; ++lane)
    {
        out[lane] = 0;
    }
    return Result::Ok;
}

auto Instance::valueInt32(const ValueHandle& handle, std::int32_t& out) const noexcept -> Result
{
    auto values = std::array<double, 4>{};
    auto status = value(handle, values.data());
    if (status != Result::Ok)
    {
        return status;
    }
    auto type = ValueType::None;
    status = program_->valueType(handle.nodeIndex, type);
    if (status != Result::Ok)
    {
        return status;
    }
    if (type != ValueType::Int32 || handle.componentCount != 1)
    {
        return Result::InvalidValueType;
    }
    return int32FromDouble(values[0], out);
}

auto Instance::parameter(const ParameterHandle& handle, double& out) const noexcept -> Result
{
    auto profileScope = ProfileScope(profiling_, ProfileEvent::ReadParameter, handle.nodeIndex,
                                     profiling_.accessors);
    if (phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    if (program_ == nullptr || handle.programIdentity != program_ ||
        handle.generation != generation_ || handle.programGeneration != program_->generation_ ||
        handle.nodeIndex >= program_->nodeCount_)
    {
        return Result::InvalidHandle;
    }
    auto node = instruction(program_->bytes_, program_->version_, handle.nodeIndex);
    auto valid = false;
    if (program_->version_ >= 3 &&
        (node[16] == customParameterOpcode || node[16] == customSignalOpcode))
    {
        auto descriptor = program_->bindings_[handle.nodeIndex].descriptor;
        valid = descriptor != nullptr && handle.inputSlot < node[18] &&
                handle.component < descriptor->inputWidths[handle.inputSlot];
    } else
    {
        auto opcode = node[16];
        auto width = program_->version_ == 1 ? 1u : node[17];
        auto laneCount = 0u;
        if (program_->version_ == 1)
        {
            if ((handle.inputSlot == 0 && (opcode == 1 || (opcode >= 3 && opcode <= 6))) ||
                (handle.inputSlot == 1 && opcode >= 3 && opcode <= 6))
            {
                laneCount = 1;
            }
        } else if (handle.inputSlot == 0)
        {
            if (opcode == enumValueOpcode)
            {
                laneCount = 1;
            }
            if (opcode == 1 || (opcode >= 7 && opcode <= 10) || (opcode >= 3 && opcode <= 6) ||
                (opcode >= 11 && opcode <= 22))
            {
                laneCount = width;
            } else if (opcode == 29 || opcode == 30 || opcode == 32)
            {
                laneCount = 1;
            } else if (opcode >= 35 && opcode <= 37)
            {
                laneCount = width;
            } else if (opcode == int32Opcode ||
                       (opcode >= int32AddOpcode && opcode <= int32DivideOpcode) ||
                       opcode == floatToInt32Opcode || opcode == int32ToFloatOpcode)
            {
                laneCount = 1;
            }
        } else if (handle.inputSlot == 1 &&
                   ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22) ||
                    (opcode >= int32AddOpcode && opcode <= int32DivideOpcode)))
        {
            laneCount = width;
        }
        valid = handle.inputSlot <= 1 && handle.component < laneCount;
    }
    if (!valid)
    {
        return Result::InvalidHandle;
    }
    if (program_->version_ == 1)
    {
        out = defaults_ == nullptr ? readDouble(node + (handle.inputSlot == 0 ? 28 : 36))
                                   : defaults_[handle.nodeIndex * 2 + handle.inputSlot];
    } else if (program_->version_ >= 3 &&
               (node[16] == customParameterOpcode || node[16] == customSignalOpcode))
    {
        out = defaults_ == nullptr
                  ? readDouble(node + 80 + handle.inputSlot * 32 + handle.component * 8)
                  : defaults_[handle.nodeIndex * 16 + handle.inputSlot * 4 + handle.component];
    } else
    {
        out = defaults_ == nullptr
                  ? readDouble(node + (handle.inputSlot == 0 ? 32 : 64) + handle.component * 8)
                  : defaults_[handle.nodeIndex * (program_->version_ >= 3 ? 16 : 8) +
                              handle.inputSlot * 4 + handle.component];
    }
    return Result::Ok;
}

auto Instance::setParameter(const ParameterHandle& handle, double value) noexcept -> Result
{
    auto profileScope = ProfileScope(profiling_, ProfileEvent::WriteParameter, handle.nodeIndex,
                                     profiling_.accessors);
    if (dispatching_)
    {
        return Result::Busy;
    }
    if (phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    if (program_ == nullptr || defaults_ == nullptr || handle.programIdentity != program_ ||
        handle.generation != generation_ || handle.programGeneration != program_->generation_ ||
        handle.nodeIndex >= program_->nodeCount_)
    {
        return Result::InvalidHandle;
    }
    if (!std::isfinite(value))
    {
        return Result::NonFinite;
    }
    auto node = instruction(program_->bytes_, program_->version_, handle.nodeIndex);
    if (program_->version_ >= 10 && handle.inputSlot < 4 &&
        valueTypeRecord(program_->bytes_, program_->valueTypeMetadataOffset_,
                        handle.nodeIndex)[2 + handle.inputSlot] == valueTypeInt32 &&
        !validInt32(value))
    {
        return Result::InvalidValueType;
    }
    if (node[16] == enumValueOpcode &&
        (handle.inputSlot != 0 || handle.component != 0 || std::trunc(value) != value ||
         value < std::numeric_limits<std::int32_t>::min() ||
         value > std::numeric_limits<std::int32_t>::max()))
    {
        return Result::InvalidValueType;
    }
    if (program_->version_ >= 3 &&
        (node[16] == customParameterOpcode || node[16] == customSignalOpcode))
    {
        auto descriptor = program_->bindings_[handle.nodeIndex].descriptor;
        if (descriptor == nullptr || handle.inputSlot >= node[18] ||
            handle.component >= descriptor->inputWidths[handle.inputSlot])
        {
            return Result::InvalidHandle;
        }
        if (descriptor->inputIsColor[handle.inputSlot] != 0 &&
            (value < 0 || (handle.component == 3 && value > 1)))
        {
            return Result::InvalidValueType;
        }
        defaults_[handle.nodeIndex * 16 + handle.inputSlot * 4 + handle.component] = value;
    } else
    {
        auto opcode = node[16];
        auto width = program_->version_ == 1 ? 1u : node[17];
        auto laneCount = 0u;
        if (program_->version_ == 1)
        {
            if ((handle.inputSlot == 0 && (opcode == 1 || (opcode >= 3 && opcode <= 6))) ||
                (handle.inputSlot == 1 && opcode >= 3 && opcode <= 6))
            {
                laneCount = 1;
            }
        } else if (handle.inputSlot == 0)
        {
            if (opcode == enumValueOpcode)
            {
                laneCount = 1;
            }
            if (opcode == 1 || (opcode >= 7 && opcode <= 10) || (opcode >= 3 && opcode <= 6) ||
                (opcode >= 11 && opcode <= 22))
            {
                laneCount = width;
            } else if (opcode == 29 || opcode == 30 || opcode == 32)
            {
                laneCount = 1;
            } else if (opcode >= 35 && opcode <= 37)
            {
                laneCount = width;
            } else if (opcode == int32Opcode ||
                       (opcode >= int32AddOpcode && opcode <= int32DivideOpcode) ||
                       opcode == floatToInt32Opcode || opcode == int32ToFloatOpcode)
            {
                laneCount = 1;
            }
        } else if (handle.inputSlot == 1 &&
                   ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22) ||
                    (opcode >= int32AddOpcode && opcode <= int32DivideOpcode)))
        {
            laneCount = width;
        }
        if (handle.inputSlot > 1 || handle.component >= laneCount)
        {
            return Result::InvalidHandle;
        }
        if (program_->version_ == 1)
        {
            defaults_[handle.nodeIndex * 2 + handle.inputSlot] = value;
        } else
        {
            defaults_[handle.nodeIndex * (program_->version_ >= 3 ? 16 : 8) + handle.inputSlot * 4 +
                      handle.component] = value;
        }
    }
    return Result::Ok;
}

auto Instance::setParameterInt32(const ParameterHandle& handle, std::int32_t value) noexcept
    -> Result
{
    auto currentValue = 0.0;
    auto status = parameter(handle, currentValue);
    if (status != Result::Ok)
    {
        return status;
    }
    if (program_ == nullptr || program_->version_ < 10 || handle.inputSlot >= 4 ||
        valueTypeRecord(program_->bytes_, program_->valueTypeMetadataOffset_,
                        handle.nodeIndex)[2 + handle.inputSlot] != valueTypeInt32)
    {
        return Result::InvalidValueType;
    }
    return setParameter(handle, static_cast<double>(value));
}

auto Instance::setHostQuality(const HostQualitySnapshot& snapshot) noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running ||
        generatedSignalsPending_ || logicalQueueCount_ != 0)
    {
        return Result::Busy;
    }
    if (!program_)
    {
        return Result::InvalidArgument;
    }
    if (static_cast<unsigned>(snapshot.availability) >
            static_cast<unsigned>(HostQualityAvailability::Unsupported) ||
        (snapshot.availability == HostQualityAvailability::Known &&
         (static_cast<unsigned>(snapshot.tier) >
              static_cast<unsigned>(HostQualityTier::Cinematic) ||
          static_cast<unsigned>(snapshot.source) >
              static_cast<unsigned>(HostQualitySource::PlatformPolicy))))
    {
        return Result::InvalidValueType;
    }
    quality_ = snapshot;
    return Result::Ok;
}

auto Instance::setTimeContext(double effectTime, double frameDelta) noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    if (!program_ || !std::isfinite(effectTime) || !std::isfinite(frameDelta) || effectTime < 0 ||
        frameDelta < 0)
    {
        return Result::InvalidArgument;
    }
    pendingEffectTime_ = effectTime;
    pendingFrameDelta_ = frameDelta;
    pendingTimeContext_ = true;
    return Result::Ok;
}

auto Instance::clearTimeContext() noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    if (!program_)
    {
        return Result::InvalidArgument;
    }
    pendingTimeContext_ = false;
    return Result::Ok;
}

auto Instance::updateAsync(double time, Enqueue enqueue, void* queueContext) noexcept -> Result
{
    if (dispatching_)
    {
        return Result::Busy;
    }
    if (program_ == nullptr || enqueue == nullptr || !std::isfinite(time))
    {
        return Result::InvalidArgument;
    }
    if (program_->version_ >= 9 && generatedSignalsPending_)
    {
        return Result::Busy;
    }
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Schedule);
    auto phase = phase_.load(std::memory_order_acquire);
    for (;;)
    {
        if (phase == Phase::Running)
        {
            return Result::Busy;
        }
        if (phase_.compare_exchange_weak(phase, Phase::Running, std::memory_order_acq_rel))
        {
            break;
        }
    }
    time_ = time;
    evaluatedTimeContext_ = pendingTimeContext_;
    effectTime_ = pendingEffectTime_;
    frameDelta_ = pendingFrameDelta_;
    pendingTimeContext_ = false;
    if (!enqueue(queueContext, &Instance::run, this))
    {
        pendingTimeContext_ = evaluatedTimeContext_;
        phase_.store(Phase::Idle, std::memory_order_release);
        return Result::QueueRejected;
    }
    return Result::Ok;
}

auto Instance::setParameter(const std::uint8_t nodeId[16], std::uint8_t inputSlot,
                            std::uint8_t component, double value) noexcept -> Result
{
    auto profileScope =
        ProfileScope(profiling_, ProfileEvent::WriteParameter, noProfileNode, profiling_.accessors);
    if (dispatching_)
    {
        return Result::Busy;
    }
    if (phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    if (program_ == nullptr || defaults_ == nullptr || nodeId == nullptr ||
        inputSlot > (program_->version_ >= 3 ? 3 : 1))
    {
        return Result::InvalidArgument;
    }
    if (!std::isfinite(value))
    {
        return Result::NonFinite;
    }
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
    {
        auto current = instruction(program_->bytes_, program_->version_, index);
        if (std::memcmp(current, nodeId, 16) != 0)
        {
            continue;
        }
        auto opcode = current[16];
        if (program_->version_ >= 10 &&
            valueTypeRecord(program_->bytes_, program_->valueTypeMetadataOffset_,
                            index)[2 + inputSlot] == valueTypeInt32 &&
            !validInt32(value))
        {
            return Result::InvalidValueType;
        }
        if (program_->version_ >= 3 &&
            (opcode == customParameterOpcode || opcode == customSignalOpcode))
        {
            if (inputSlot >= current[18] || component >= 4)
            {
                return Result::InvalidValueType;
            }
            auto descriptor = program_->bindings_[index].descriptor;
            if (descriptor == nullptr || component >= descriptor->inputWidths[inputSlot])
            {
                return Result::InvalidValueType;
            }
            if (descriptor->inputIsColor[inputSlot] != 0 &&
                (value < 0 || (component == 3 && value > 1)))
            {
                return Result::InvalidValueType;
            }
            defaults_[index * 16 + inputSlot * 4 + component] = value;
            return Result::Ok;
        }
        auto width = program_->version_ == 1 ? 1u : current[17];
        auto laneCount = 0u;
        if (program_->version_ == 1)
        {
            if ((inputSlot == 0 && (opcode == 1 || (opcode >= 3 && opcode <= 6))) ||
                (inputSlot == 1 && opcode >= 3 && opcode <= 6))
            {
                laneCount = 1;
            }
        } else if (inputSlot == 0)
        {
            if (opcode == 1 || (opcode >= 7 && opcode <= 10))
            {
                laneCount = width;
            } else if ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22))
            {
                laneCount = width;
            } else if (opcode == 29 || opcode == 30 || opcode == 32)
            {
                laneCount = 1;
            } else if (opcode >= 35 && opcode <= 37)
            {
                laneCount = width;
            } else if (opcode == int32Opcode ||
                       (opcode >= int32AddOpcode && opcode <= int32DivideOpcode) ||
                       opcode == floatToInt32Opcode || opcode == int32ToFloatOpcode)
            {
                laneCount = 1;
            }
        } else if ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22))
        {
            laneCount = width;
        } else if (inputSlot == 1 && opcode >= int32AddOpcode && opcode <= int32DivideOpcode)
        {
            laneCount = 1;
        }
        if (component >= laneCount)
        {
            return Result::InvalidValueType;
        }
        if (opcode >= 26 && opcode <= 28)
        {
            auto inputWidth = current[18];
            if (value < 0 || value >= inputWidth || std::floor(value) != value)
            {
                return Result::InvalidValueType;
            }
        }
        if (opcode >= 35 && opcode <= 37 && (value < 0 || value >= 4 || std::floor(value) != value))
        {
            return Result::InvalidValueType;
        }
        if (opcode == 10 && (value < 0 || (component == 3 && value > 1)))
        {
            return Result::InvalidValueType;
        }
        if (program_->version_ == 1)
        {
            defaults_[index * 2 + inputSlot] = value;
        } else if (program_->version_ >= 3)
        {
            defaults_[index * 16 + inputSlot * 4 + component] = value;
        } else
        {
            defaults_[index * 8 + inputSlot * 4 + component] = value;
        }
        return Result::Ok;
    }
    return Result::NodeNotFound;
}

auto Instance::result() const noexcept -> Result
{
    return phase_.load(std::memory_order_acquire) == Phase::Complete ? result_ : Result::NotReady;
}

auto Instance::setSignalObserver(SignalObserver observer, void* context) noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    if (observer != nullptr && context == nullptr)
    {
        return Result::InvalidArgument;
    }
    signalObserver_ = observer;
    signalObserverContext_ = context;
    return Result::Ok;
}

auto Instance::enableForceRequests() noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    if (!program_)
    {
        return Result::NotReady;
    }
    forceRequestsEnabled_ = true;
    return Result::Ok;
}

auto Instance::pendingForceRequest(std::uint8_t& scope) const noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    scope = forceScope_;
    return Result::Ok;
}

auto Instance::value(const std::uint8_t nodeId[16], double& out) const noexcept -> Result
{
    auto components = std::array<double, 4>{};
    auto count = std::uint8_t{};
    auto status = valueComponents(nodeId, components.data(), count);
    if (status == Result::Ok)
    {
        if (count != 1)
        {
            return Result::InvalidValueType;
        }
        out = components[0];
    }
    return status;
}

auto Instance::valueComponents(const std::uint8_t nodeId[16], double out[4],
                               std::uint8_t& componentCount) const noexcept -> Result
{
    auto profileScope =
        ProfileScope(profiling_, ProfileEvent::ReadValue, noProfileNode, profiling_.accessors);
    if (nodeId == nullptr || out == nullptr)
    {
        return Result::InvalidArgument;
    }
    auto status = result();
    if (status != Result::Ok)
    {
        return status;
    }
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
    {
        auto current = instruction(program_->bytes_, program_->version_, index);
        if (std::memcmp(current, nodeId, 16) == 0)
        {
            auto width = program_->version_ == 1 ? 1u : current[17];
            if (program_->version_ >= 3 && current[16] == customSignalOpcode)
            {
                return Result::InvalidValueType;
            }
            for (auto lane = 0u; lane < width; ++lane)
            {
                out[lane] = work_[index * (program_->version_ == 1 ? 1u : 4u) + lane];
            }
            for (auto lane = width; lane < 4; ++lane)
            {
                out[lane] = 0;
            }
            componentCount = static_cast<std::uint8_t>(width);
            return Result::Ok;
        }
    }
    return Result::NodeNotFound;
}

auto Instance::setUnconnectedParameter(const std::uint8_t nodeId[16], std::uint8_t inputSlot,
                                       std::uint8_t component, double value) noexcept -> Result
{
    if (program_ == nullptr || inputSlot > (program_->version_ >= 3 ? 3 : 1))
    {
        return Result::InvalidArgument;
    }
    auto handle = ParameterHandle{};
    auto status = bindParameter(nodeId, inputSlot, component, handle);
    if (status != Result::Ok)
    {
        return status;
    }
    auto current = instruction(program_->bytes_, program_->version_, handle.nodeIndex);
    auto offset = program_->version_ == 1 ? 20 + inputSlot * 4
                  : program_->version_ >= 3 && (current[16] == customParameterOpcode ||
                                                current[16] == customSignalOpcode)
                      ? 48 + inputSlot * 8
                      : 24 + inputSlot * 4;
    if (read32(current + offset) != noInput)
    {
        return Result::InvalidValueType;
    }
    return setParameter(handle, value);
}

auto Instance::dispatchSignal(const std::uint8_t entryNodeId[16], double time,
                              std::size_t maxInstructions) noexcept -> Result
{
    auto entry = SignalHandle{};
    auto status = bindSignal(entryNodeId, entry);
    if (status != Result::Ok)
    {
        return status;
    }
    return dispatchSignal(entry, time, maxInstructions);
}

auto Instance::dispatchSignal(const SignalHandle& entry, double time,
                              std::size_t maxInstructions) noexcept -> Result
{
    return dispatchSignalInternal(entry, time, maxInstructions, -1);
}

auto Instance::setDeferredSignalStorage(DeferredSignal* storage, std::size_t capacity) noexcept
    -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    if (!program_ || (capacity != 0 && !storage) || (capacity == 0 && storage) ||
        capacity > std::numeric_limits<std::size_t>::max() / sizeof(DeferredSignal))
    {
        return Result::InvalidArgument;
    }
    if (deferredCount_)
    {
        return Result::Busy;
    }
    auto bytes = capacity * sizeof(DeferredSignal);
    if (capacity &&
        (!isAligned(storage, alignof(DeferredSignal)) ||
         rangesOverlap(storage, bytes, work_, program_->workBytes()) ||
         (defaults_ && rangesOverlap(storage, bytes, defaults_,
                                     program_->parameterValueCount() * sizeof(double)))))
    {
        return Result::InvalidArgument;
    }
    deferredSignals_ = storage;
    deferredCapacity_ = capacity;
    return Result::Ok;
}

auto Instance::nextDeferredSignal(double& time, bool& hasSignal) const noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    if (!program_)
    {
        return Result::InvalidArgument;
    }
    hasSignal = false;
    auto earliest = std::numeric_limits<double>::infinity();
    for (auto index = std::size_t{0}; index < deferredCount_; ++index)
    {
        const auto& item = deferredSignals_[index];
        auto due = item.time;
        if (item.usesBranchTime)
        {
            if (item.branchGuardId >= program_->branchGuardCount_)
            {
                return Result::InvalidBinary;
            }
            if (!isBranchActive(item.branchGuardId))
            {
                due = std::numeric_limits<double>::max();
            } else
            {
                auto clock = reinterpret_cast<const std::uint8_t*>(work_) +
                             program_->branchClockOffset_ + std::size_t{item.branchGuardId} * 24;
                auto localTime = readDouble(clock);
                due = time_ + std::max(0.0, item.time - localTime);
            }
        }
        if (due < earliest)
        {
            earliest = due;
            hasSignal = true;
        }
    }
    if (hasSignal)
    {
        time = earliest;
    }
    return Result::Ok;
}

auto Instance::clearDeferredSignals() noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    if (!program_)
    {
        return Result::InvalidArgument;
    }
    deferredCount_ = 0;
    return Result::Ok;
}

auto Instance::dispatchDeferredSignal(double time, std::size_t maxInstructions) noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    if (!program_ || !std::isfinite(time) || !maxInstructions)
    {
        return Result::InvalidArgument;
    }
    if (!deferredCount_ || time_ != time || result() != Result::Ok)
    {
        return Result::NotReady;
    }
    auto selected = deferredCount_;
    for (auto index = std::size_t{0}; index < deferredCount_; ++index)
    {
        const auto& candidate = deferredSignals_[index];
        if (!candidate.usesBranchTime)
        {
            if (candidate.time == time)
            {
                selected = index;
                break;
            }
            continue;
        }
        if (candidate.branchGuardId >= program_->branchGuardCount_ ||
            !isBranchActive(candidate.branchGuardId))
        {
            continue;
        }
        auto clock = reinterpret_cast<const std::uint8_t*>(work_) + program_->branchClockOffset_ +
                     std::size_t{candidate.branchGuardId} * 24;
        if (readDouble(clock) >= candidate.time)
        {
            selected = index;
            break;
        }
    }
    if (selected == deferredCount_)
    {
        return Result::NotReady;
    }
    auto item = deferredSignals_[selected];
    for (auto index = selected + 1; index < deferredCount_; ++index)
    {
        deferredSignals_[index - 1] = deferredSignals_[index];
    }
    --deferredCount_;
    auto entry = SignalHandle{program_, generation_, program_->generation_, item.nodeIndex};
    return dispatchSignalInternal(entry, time, maxInstructions, item.branch);
}

auto Instance::dispatchGeneratedSignals(std::size_t maxInstructions) noexcept -> Result
{
    if (dispatching_ || phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    if (program_ == nullptr || maxInstructions == 0)
    {
        return Result::InvalidArgument;
    }
    if (program_->version_ < 9)
    {
        return Result::Ok;
    }
    auto status = result();
    if (status != Result::Ok)
    {
        return status;
    }
    if (!generatedSignalsPending_ && logicalQueueCount_ == 0)
    {
        return refreshDirtyPure();
    }
    return drainLogicalSignals(maxInstructions, true);
}

auto Instance::dispatchSignalV9(const SignalHandle& entry, double time, std::size_t maxInstructions,
                                int resumeBranch) noexcept -> Result
{
    if (logicalQueueCount_ != 0 || generatedSignalsPending_ || generatedQueueSeeded_)
    {
        return Result::NotReady;
    }
    if (program_->logicalQueueCapacity_ == 0)
    {
        return Result::InvalidBinary;
    }
    auto queue = reinterpret_cast<LogicalQueuedSignal*>(reinterpret_cast<std::uint8_t*>(work_) +
                                                        program_->logicalQueueOffset_);
    auto payloadQueue =
        program_->signalPayloadQueueOffset_ == 0
            ? nullptr
            : reinterpret_cast<SignalPayload*>(reinterpret_cast<std::uint8_t*>(work_) +
                                               program_->signalPayloadQueueOffset_);
    if (resumeBranch >= 0)
    {
        queue[0] = {entry.nodeIndex, static_cast<std::uint8_t>(resumeBranch), 2, 0};
    } else
    {
        queue[0] = {entry.nodeIndex, 0, 1, 0};
    }
    if (payloadQueue != nullptr)
    {
        payloadQueue[0] = {};
    }
    logicalQueueHead_ = 0;
    logicalQueueCount_ = 1;
    time_ = time;
    return drainLogicalSignals(maxInstructions, false);
}

auto Instance::drainLogicalSignals(std::size_t maxInstructions, bool seedGenerated) noexcept
    -> Result
{
    if (dispatching_)
    {
        return Result::Busy;
    }
    if (program_ == nullptr || program_->version_ < 9 || maxInstructions == 0)
    {
        return Result::InvalidArgument;
    }
    if (phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    auto status = result();
    if (status != Result::Ok)
    {
        return status;
    }
    auto queue = reinterpret_cast<LogicalQueuedSignal*>(reinterpret_cast<std::uint8_t*>(work_) +
                                                        program_->logicalQueueOffset_);
    auto payloadQueue =
        program_->signalPayloadQueueOffset_ == 0
            ? nullptr
            : reinterpret_cast<SignalPayload*>(reinterpret_cast<std::uint8_t*>(work_) +
                                               program_->signalPayloadQueueOffset_);
    auto activePayload = SignalPayload{};
    auto enqueue = [&](std::uint32_t nodeIndex, std::uint8_t inputSlot, std::uint8_t isRoot,
                       std::uint32_t sourceIndex) noexcept -> Result {
        if (logicalQueueCount_ >= program_->logicalQueueCapacity_)
        {
            return Result::InstructionBudgetExceeded;
        }
        auto tail = (logicalQueueHead_ + logicalQueueCount_) % program_->logicalQueueCapacity_;
        queue[tail] = {nodeIndex, inputSlot, isRoot, static_cast<std::uint16_t>(sourceIndex + 1)};
        if (payloadQueue != nullptr)
        {
            payloadQueue[tail] = activePayload;
        }
        ++logicalQueueCount_;
        return Result::Ok;
    };
    auto routeLowerBound = [&](std::uint32_t sourceIndex, std::uint8_t branch) noexcept {
        auto first = std::size_t{0};
        auto last = std::size_t{program_->logicalSignalRouteCount_};
        while (first < last)
        {
            auto middle = first + (last - first) / 2;
            auto record = program_->bytes_ + program_->logicalSignalRoutesOffset_ +
                          middle * logicalSignalRouteRecordSize;
            auto currentSource = read32(record);
            auto currentBranch = record[4];
            if (currentSource < sourceIndex ||
                (currentSource == sourceIndex && currentBranch < branch))
            {
                first = middle + 1;
            } else
            {
                last = middle;
            }
        }
        return first;
    };
    auto routeCount = [&](std::uint32_t sourceIndex, std::uint8_t branch) noexcept {
        auto count = std::size_t{0};
        auto routeIndex = routeLowerBound(sourceIndex, branch);
        while (routeIndex < program_->logicalSignalRouteCount_)
        {
            auto record = program_->bytes_ + program_->logicalSignalRoutesOffset_ +
                          routeIndex * logicalSignalRouteRecordSize;
            if (read32(record) != sourceIndex || record[4] != branch)
            {
                break;
            }
            ++routeIndex;
            ++count;
        }
        return count;
    };
    auto appendRoutes = [&](std::uint32_t sourceIndex, std::uint8_t branch) noexcept -> Result {
        auto routeIndex = routeLowerBound(sourceIndex, branch);
        while (routeIndex < program_->logicalSignalRouteCount_)
        {
            auto record = program_->bytes_ + program_->logicalSignalRoutesOffset_ +
                          routeIndex * logicalSignalRouteRecordSize;
            if (read32(record) != sourceIndex || record[4] != branch)
            {
                break;
            }
            auto result = enqueue(read32(record + 8), record[5], 0, sourceIndex);
            if (result != Result::Ok)
            {
                return result;
            }
            ++routeIndex;
        }
        return Result::Ok;
    };

    if (seedGenerated && generatedSignalsPending_ && !generatedQueueSeeded_)
    {
        std::size_t requiredEvents = 0;
        auto eventMasks =
            reinterpret_cast<const std::uint8_t*>(work_) + program_->logicalEventMaskOffset_;
        for (auto logicalIndex = 0u; logicalIndex < program_->logicalInstructionCount_;
             ++logicalIndex)
        {
            auto record = program_->bytes_ + program_->logicalInstructionRecordsOffset_ +
                          std::size_t{logicalIndex} * logicalInstructionRecordSize;
            auto nodeIndex = read32(record);
            auto branches = read64(eventMasks + std::size_t{nodeIndex} * sizeof(std::uint64_t));
            for (auto branch = 0u; branch < record[31]; ++branch)
            {
                if ((branches & (std::uint64_t{1} << branch)) != 0)
                {
                    auto routes = routeCount(nodeIndex, static_cast<std::uint8_t>(branch));
                    if (routes > program_->logicalQueueCapacity_ - requiredEvents)
                    {
                        return Result::InstructionBudgetExceeded;
                    }
                    requiredEvents += routes;
                }
            }
        }
        if (requiredEvents > program_->logicalQueueCapacity_ - logicalQueueCount_)
        {
            return Result::InstructionBudgetExceeded;
        }
        for (auto logicalIndex = 0u; logicalIndex < program_->logicalInstructionCount_;
             ++logicalIndex)
        {
            auto record = program_->bytes_ + program_->logicalInstructionRecordsOffset_ +
                          std::size_t{logicalIndex} * logicalInstructionRecordSize;
            auto nodeIndex = read32(record);
            auto branches = read64(eventMasks + std::size_t{nodeIndex} * sizeof(std::uint64_t));
            for (auto branch = 0u; branch < record[31]; ++branch)
            {
                if ((branches & (std::uint64_t{1} << branch)) != 0)
                {
                    auto enqueueStatus = appendRoutes(nodeIndex, static_cast<std::uint8_t>(branch));
                    if (enqueueStatus != Result::Ok)
                    {
                        return enqueueStatus;
                    }
                }
            }
        }
        std::memset(const_cast<std::uint8_t*>(eventMasks), 0,
                    std::size_t{program_->nodeCount_} * sizeof(std::uint64_t));
        generatedQueueSeeded_ = true;
    }

    struct DispatchScope final
    {
        bool& active;
        ~DispatchScope() { active = false; }
    };
    auto scope = DispatchScope{dispatching_};
    dispatching_ = true;
    auto visited = std::size_t{0};
    while (logicalQueueCount_ != 0)
    {
        if (visited >= maxInstructions)
        {
            return Result::InstructionBudgetExceeded;
        }
        auto event = queue[logicalQueueHead_];
        activePayload = payloadQueue == nullptr ? SignalPayload{} : payloadQueue[logicalQueueHead_];
        for (auto fieldIndex = std::size_t{}; fieldIndex < activePayload.size(); ++fieldIndex)
        {
            auto field = activePayload.field(fieldIndex);
            auto sourceIndex = 0u;
            for (; sourceIndex < program_->nodeCount_; ++sourceIndex)
            {
                if (std::memcmp(instruction(program_->bytes_, 3, sourceIndex), field->id.data(),
                                16) == 0)
                {
                    break;
                }
            }
            if (sourceIndex == program_->nodeCount_ || field->kind != SignalValueKind::Vector4)
            {
                return Result::InvalidBinary;
            }
            std::copy_n(field->components, 4, work_ + sourceIndex * 4);
            (reinterpret_cast<std::uint8_t*>(work_) + program_->logicalDirtyOffset_)[sourceIndex] =
                1;
        }
        status = refreshDirtyPure();
        if (status != Result::Ok)
        {
            return status;
        }
        auto current = instruction(program_->bytes_, 3, event.nodeIndex);
        if (event.isRoot == 2)
        {
            auto branch = event.inputSlot;
            auto fanout = routeCount(event.nodeIndex, branch);
            if (logicalQueueCount_ - 1 + fanout > program_->logicalQueueCapacity_)
            {
                return Result::InstructionBudgetExceeded;
            }
            --logicalQueueCount_;
            logicalQueueHead_ = (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
            status = appendRoutes(event.nodeIndex, branch);
            if (status != Result::Ok)
            {
                return status;
            }
            continue;
        }
        ++visited;
        auto guardId = branchGuard(current, program_->version_);
        if (guardId != noInput && !isBranchActive(guardId))
        {
            --logicalQueueCount_;
            logicalQueueHead_ = (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
            continue;
        }
        if (event.isRoot == 0 && current[16] != customSignalOpcode &&
            current[16] != logicalEventSignalOpcode)
        {
            return Result::InvalidBinary;
        }
        if (event.isRoot == 1 && current[16] != customSignalOpcode)
        {
            return Result::InvalidValueType;
        }
        auto maxFanout = std::size_t{0};
        auto logicalDescriptor = program_->bindings_[event.nodeIndex].logicalDescriptor;
        auto customDescriptor = program_->bindings_[event.nodeIndex].descriptor;
        if (current[16] == logicalEventSignalOpcode)
        {
            if (logicalDescriptor == nullptr || logicalDescriptor->onSignal == nullptr ||
                event.inputSlot >= logicalDescriptor->signalInputCount)
            {
                return Result::InvalidValueType;
            }
            for (auto branch = 0u; branch < logicalDescriptor->signalBranchCount; ++branch)
            {
                maxFanout += routeCount(event.nodeIndex, static_cast<std::uint8_t>(branch));
            }
        } else
        {
            if (customDescriptor == nullptr ||
                customDescriptor->execution != CustomExecution::Signal)
            {
                return Result::UnknownCustomNode;
            }
            for (auto branch = 0u; branch < customDescriptor->branchCount; ++branch)
            {
                maxFanout = std::max(
                    maxFanout, routeCount(event.nodeIndex, static_cast<std::uint8_t>(branch)));
            }
        }
        if (logicalQueueCount_ - 1 + maxFanout > program_->logicalQueueCapacity_)
        {
            return Result::InstructionBudgetExceeded;
        }

        alignas(customWorkAlignment) auto inputs = std::array<double, customMaxPorts * 4>{};
        auto inputWidths = std::array<std::uint8_t, customMaxPorts>{};
        alignas(customWorkAlignment) auto constants = std::array<double, customMaxPorts * 4>{};
        auto inputCount = current[18];
        auto outputWidth = current[17];
        auto state = reinterpret_cast<std::uint8_t*>(work_) +
                     stateOffset(program_->bindings_, event.nodeIndex);
        if (current[16] == logicalEventSignalOpcode)
        {
            for (auto input = 0u; input < inputCount; ++input)
            {
                auto inputRecord = current + 48 + input * 8;
                auto sourceIndex = read32(inputRecord);
                inputWidths[input] = logicalDescriptor->inputWidths[input];
                for (auto lane = 0u; lane < inputWidths[input]; ++lane)
                {
                    constants[input * 4 + lane] =
                        defaults_ == nullptr ? readDouble(current + 80 + input * 32 + lane * 8)
                                             : defaults_[event.nodeIndex * 16 + input * 4 + lane];
                    inputs[input * 4 + lane] = sourceIndex == noInput
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
            auto record = static_cast<const std::uint8_t*>(nullptr);
            for (auto logicalIndex = 0u; logicalIndex < program_->logicalInstructionCount_;
                 ++logicalIndex)
            {
                auto candidate = program_->bytes_ + program_->logicalInstructionRecordsOffset_ +
                                 std::size_t{logicalIndex} * logicalInstructionRecordSize;
                if (read32(candidate) == event.nodeIndex)
                {
                    record = candidate;
                    break;
                }
            }
            if (record == nullptr)
            {
                return Result::InvalidBinary;
            }
            invocation.configuration = read32(record + 24);
            status = debugResolvedInputs(
                event.nodeIndex, time_, inputs.data(), inputWidths.data(), nullptr,
                event.reserved == 0 ? nullptr
                                    : instruction(program_->bytes_, 3, event.reserved - 1));
            if (status != Result::Ok)
            {
                return status;
            }
            if (signalObserver_ != nullptr)
            {
                status = signalObserver_(signalObserverContext_, current);
                if (status != Result::Ok)
                {
                    return status;
                }
            }
            status = logicalDescriptor->onSignal(invocation);
            if (status != Result::Ok)
            {
                return status;
            }
            auto validBranches =
                logicalDescriptor->signalBranchCount == 0
                    ? std::uint64_t{0}
                    : (std::uint64_t{1} << logicalDescriptor->signalBranchCount) - 1;
            if ((invocation.emittedBranches & ~validBranches) != 0)
            {
                return Result::InvalidValueType;
            }
            for (auto lane = 0u; lane < outputWidth; ++lane)
            {
                if (!std::isfinite(work_[event.nodeIndex * 4 + lane]))
                {
                    return Result::NonFinite;
                }
            }
            if (invocation.captureSignalValue)
            {
                if (payloadQueue == nullptr || outputWidth != 4)
                {
                    return Result::UnsupportedBinary;
                }
                auto field = SignalFieldValue{};
                std::copy_n(current, 16, field.id.data());
                field.kind = SignalValueKind::Vector4;
                std::copy_n(work_ + event.nodeIndex * 4, 4, field.components);
                status = activePayload.set(field);
                if (status != Result::Ok)
                {
                    return status;
                }
            }
            auto dirty = reinterpret_cast<std::uint8_t*>(work_) + program_->logicalDirtyOffset_;
            dirty[event.nodeIndex] = 1;
            --logicalQueueCount_;
            logicalQueueHead_ = (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
            for (auto branch = 0u; branch < logicalDescriptor->signalBranchCount; ++branch)
            {
                if ((invocation.emittedBranches & (std::uint64_t{1} << branch)) != 0)
                {
                    status = appendRoutes(event.nodeIndex, static_cast<std::uint8_t>(branch));
                    if (status != Result::Ok)
                    {
                        return status;
                    }
                }
            }
        } else
        {
            for (auto input = 0u; input < inputCount; ++input)
            {
                auto inputRecord = current + 48 + input * 8;
                auto sourceIndex = read32(inputRecord);
                inputWidths[input] = customDescriptor->inputWidths[input];
                for (auto lane = 0u; lane < inputWidths[input]; ++lane)
                {
                    constants[input * 4 + lane] =
                        defaults_ == nullptr ? readDouble(current + 80 + input * 32 + lane * 8)
                                             : defaults_[event.nodeIndex * 16 + input * 4 + lane];
                    inputs[input * 4 + lane] = sourceIndex == noInput
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
            status = debugResolvedInputs(
                event.nodeIndex, time_, inputs.data(), inputWidths.data(),
                customDescriptor->inputIsColor,
                event.reserved == 0 ? nullptr
                                    : instruction(program_->bytes_, 3, event.reserved - 1));
            if (status != Result::Ok)
            {
                return status;
            }
            if (signalObserver_ != nullptr)
            {
                status = signalObserver_(signalObserverContext_, current);
                if (status != Result::Ok)
                {
                    return status;
                }
            }
            status = customDescriptor->function(invocation);
            if (status != Result::Ok)
            {
                return status;
            }
            if (invocation.deferSignal && activePayload.size() != 0)
            {
                return Result::UnsupportedBinary;
            }
            if (invocation.enterPhase != 0)
            {
                if (invocation.enterPhase > 3)
                {
                    return Result::InvalidArgument;
                }
                pendingPhase_ = invocation.enterPhase;
                pendingPhaseTime_ = time_;
            }
            if (invocation.forceScope != 0)
            {
                if (!forceRequestsEnabled_)
                {
                    return Result::UnsupportedBinary;
                }
                if (invocation.forceScope > 2)
                {
                    return Result::InvalidArgument;
                }
                forceScope_ = invocation.forceScope;
                deferredCount_ = logicalQueueCount_ = 0;
                generatedQueueSeeded_ = generatedSignalsPending_ = false;
                return Result::Ok;
            }
            if (current[19] == 0)
            {
                --logicalQueueCount_;
                logicalQueueHead_ = (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
                continue;
            }
            if (invocation.deferSignal)
            {
                auto usesBranchTime = guardId != noInput;
                auto baseTime = time_;
                if (usesBranchTime)
                {
                    if (guardId >= program_->branchGuardCount_ || guardId >= 0xff)
                    {
                        return Result::UnsupportedBinary;
                    }
                    auto clock = reinterpret_cast<const std::uint8_t*>(work_) +
                                 program_->branchClockOffset_ + std::size_t{guardId} * 24;
                    baseTime = readDouble(clock);
                }
                auto due = baseTime + invocation.delaySeconds;
                if (!std::isfinite(invocation.delaySeconds) || invocation.delaySeconds < 0 ||
                    !std::isfinite(due))
                {
                    return Result::InvalidArgument;
                }
                if (!deferredSignals_)
                {
                    return Result::NotReady;
                }
                if (deferredCount_ == deferredCapacity_)
                {
                    return Result::QueueRejected;
                }
                if (invocation.selectedBranch >= current[19])
                {
                    return Result::InvalidValueType;
                }
                deferredSignals_[deferredCount_++] = {
                    due, event.nodeIndex, invocation.selectedBranch,
                    usesBranchTime ? static_cast<std::uint8_t>(guardId) : std::uint8_t{0xff},
                    usesBranchTime};
                --logicalQueueCount_;
                logicalQueueHead_ = (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
                continue;
            }
            if (invocation.selectedBranch >= current[19])
            {
                return Result::InvalidValueType;
            }
            --logicalQueueCount_;
            logicalQueueHead_ = (logicalQueueHead_ + 1) % program_->logicalQueueCapacity_;
            status = appendRoutes(event.nodeIndex, invocation.selectedBranch);
            if (status != Result::Ok)
            {
                return status;
            }
        }
    }
    status = refreshDirtyPure();
    if (status != Result::Ok)
    {
        return status;
    }
    if (generatedQueueSeeded_)
    {
        generatedQueueSeeded_ = false;
        generatedSignalsPending_ = false;
    }
    return Result::Ok;
}

auto Instance::refreshDirtyPure() noexcept -> Result
{
    if (program_ == nullptr || program_->version_ < 9)
    {
        return Result::Ok;
    }
    auto dirtyBytes = reinterpret_cast<std::uint8_t*>(work_) + program_->logicalDirtyOffset_;
    auto dirty = std::array<bool, maxNodes>{};
    auto anyDirty = false;
    for (auto index = 0u; index < program_->nodeCount_; ++index)
    {
        dirty[index] = dirtyBytes[index] != 0;
        anyDirty = anyDirty || dirty[index];
    }
    if (!anyDirty)
    {
        return Result::Ok;
    }

    auto sourceIsDirty = [&](std::uint32_t sourceIndex) noexcept {
        return sourceIndex != noInput && sourceIndex < program_->nodeCount_ && dirty[sourceIndex];
    };
    for (auto index = 0u; index < program_->nodeCount_; ++index)
    {
        auto current = instruction(program_->bytes_, 3, index);
        auto opcode = current[16];
        if (dirty[index] || opcode == customSignalOpcode)
        {
            continue;
        }
        auto guardId = branchGuard(current, program_->version_);
        if (guardId != noInput && !isBranchActive(guardId))
        {
            continue;
        }
        auto depends = false;
        if (opcode == customParameterOpcode || opcode == logicalEventSignalOpcode)
        {
            for (auto input = 0u; input < current[18]; ++input)
            {
                depends = depends || sourceIsDirty(read32(current + 48 + input * 8));
            }
            if (depends && opcode == logicalEventSignalOpcode)
            {
                if (std::memcmp(current + 24, logicalSignalCaptureTypeId.data(), 16) != 0)
                {
                    return Result::UnsupportedBinary;
                }
                continue;
            }
            if (depends && !isLatePureBuiltin(program_->bindings_[index].descriptor))
            {
                return Result::UnsupportedBinary;
            }
        } else if (opcode == enumValueOpcode)
        {
            depends = sourceIsDirty(read32(current + 24));
        } else if (opcode == enumSelectOpcode)
        {
            for (auto selectIndex = 0u; selectIndex < program_->enumSelectCount_; ++selectIndex)
            {
                auto record = program_->bytes_ + program_->enumSelectRecordsOffset_ +
                              std::size_t{selectIndex} * enumSelectRecordSize;
                if (read32(record) != index)
                {
                    continue;
                }
                depends = sourceIsDirty(read32(record + 4)) || sourceIsDirty(read32(record + 8));
                auto caseCount = record[29];
                auto caseOffset = read32(record + 32);
                for (auto caseIndex = 0u; caseIndex < caseCount; ++caseIndex)
                {
                    depends = depends ||
                              sourceIsDirty(read32(
                                  program_->bytes_ + program_->enumBranchMetadataOffset_ +
                                  caseOffset + std::size_t{caseIndex} * enumCaseRecordSize + 4));
                }
                break;
            }
        } else if ((opcode >= 1 && opcode <= 39) || opcode == curveOpcode ||
                   opcode == branchTimeOpcode)
        {
            depends = sourceIsDirty(read32(current + 24)) || sourceIsDirty(read32(current + 28));
        }
        if (depends)
        {
            dirty[index] = true;
        }
    }
    for (auto index = 0u; index < program_->nodeCount_; ++index)
    {
        if (dirty[index])
        {
            dirtyBytes[index] = 1;
        }
    }
    auto status = evaluate(true, dirty.data());
    if (status != Result::Ok)
    {
        return status;
    }
    std::memset(dirtyBytes, 0, program_->nodeCount_);
    return Result::Ok;
}

auto Instance::dispatchSignalInternal(const SignalHandle& entry, double time,
                                      std::size_t maxInstructions, int resumeBranch) noexcept
    -> Result
{
    if (dispatching_)
    {
        return Result::Busy;
    }
    if (program_ == nullptr || program_->version_ < 3 || !std::isfinite(time) ||
        maxInstructions == 0)
    {
        return Result::InvalidArgument;
    }
    if (entry.programIdentity != program_ || entry.generation != generation_ ||
        entry.programGeneration != program_->generation_ || entry.nodeIndex >= program_->nodeCount_)
    {
        return Result::InvalidHandle;
    }
    if (phase_.load(std::memory_order_acquire) == Phase::Running)
    {
        return Result::Busy;
    }
    auto status = result();
    if (status != Result::Ok)
    {
        return status;
    }
    auto index = entry.nodeIndex;
    auto entryInstruction = instruction(program_->bytes_, 3, index);
    if (entryInstruction[16] != customSignalOpcode)
    {
        return Result::InvalidValueType;
    }
    if (program_->version_ >= 8 &&
        !isBranchActive(branchGuard(entryInstruction, program_->version_)))
    {
        return resumeBranch >= 0 ? Result::NotReady : Result::Ok;
    }
    if (program_->version_ >= 9)
    {
        return dispatchSignalV9(entry, time, maxInstructions, resumeBranch);
    }
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Signal);

    struct DispatchScope final
    {
        bool& active;
        ~DispatchScope() { active = false; }
    };
    auto scope = DispatchScope{dispatching_};
    dispatching_ = true;
    auto visited = std::size_t{0};
    auto pending = std::size_t{0};
    auto predecessor = noInput;
    auto stack = reinterpret_cast<std::uint8_t*>(work_) + program_->signalStackOffset_;
    auto selectBranch = [&](const std::uint8_t* current, unsigned branch) noexcept -> Result {
        if (branch >= current[19])
        {
            return Result::InvalidValueType;
        }
        if (program_->version_ >= 5)
        {
            auto offset = read32(current + 224 + branch * 8);
            auto extraCount = read32(current + 228 + branch * 8);
            if (extraCount > program_->signalStackCapacity_ - pending)
            {
                return Result::InvalidBinary;
            }
            for (auto extra = extraCount; extra > 0; --extra)
            {
                auto target = read32(program_->bytes_ + program_->signalRoutesOffset_ + offset +
                                     std::size_t{extra - 1} * 4);
                auto packed = target | ((index + 1) << 16);
                std::memcpy(stack + pending++ * sizeof(packed), &packed, sizeof(packed));
            }
        }
        predecessor = index;
        index = read32(current + 208 + branch * 4);
        return Result::Ok;
    };
    if (resumeBranch >= 0)
    {
        status = selectBranch(instruction(program_->bytes_, 3, index),
                              static_cast<unsigned>(resumeBranch));
        if (status != Result::Ok)
        {
            return status;
        }
    }
    while (index != noInput || pending != 0)
    {
        if (index == noInput)
        {
            auto packed = std::uint32_t{};
            std::memcpy(&packed, stack + --pending * sizeof(packed), sizeof(packed));
            index = packed & 0xffff;
            predecessor = (packed >> 16) - 1;
            continue;
        }
        if (++visited > maxInstructions)
        {
            return Result::InstructionBudgetExceeded;
        }
        auto current = instruction(program_->bytes_, 3, index);
        if (program_->version_ >= 8 && !isBranchActive(branchGuard(current, program_->version_)))
        {
            index = noInput;
            continue;
        }
        auto nodeScope = ProfileScope(profiling_, ProfileEvent::Node, index, profiling_.nodes);
        auto descriptor = program_->bindings_[index].descriptor;
        if (descriptor == nullptr || descriptor->execution != CustomExecution::Signal)
        {
            return Result::UnknownCustomNode;
        }
        alignas(customWorkAlignment) auto inputs = std::array<double, customMaxPorts * 4>{};
        auto inputWidths = std::array<std::uint8_t, customMaxPorts>{};
        alignas(customWorkAlignment) auto constants = std::array<double, customMaxPorts * 4>{};
        for (auto input = 0u; input < current[18]; ++input)
        {
            auto inputRecord = current + 48 + input * 8;
            auto sourceIndex = read32(inputRecord);
            inputWidths[input] = descriptor->inputWidths[input];
            for (auto lane = 0u; lane < descriptor->inputWidths[input]; ++lane)
            {
                constants[input * 4 + lane] = defaults_ == nullptr
                                                  ? readDouble(current + 80 + input * 32 + lane * 8)
                                                  : defaults_[index * 16 + input * 4 + lane];
                inputs[input * 4 + lane] = sourceIndex == noInput
                                               ? constants[input * 4 + lane]
                                               : work_[sourceIndex * 4 + inputRecord[5] + lane];
            }
        }
        auto state =
            reinterpret_cast<std::uint8_t*>(work_) + stateOffset(program_->bindings_, index);
        status = debugResolvedInputs(
            index, time, inputs.data(), inputWidths.data(), descriptor->inputIsColor,
            predecessor == noInput ? nullptr : instruction(program_->bytes_, 3, predecessor));
        if (status != Result::Ok)
        {
            return status;
        }
        if (signalObserver_ != nullptr)
        {
            status = signalObserver_(signalObserverContext_, current);
            if (status != Result::Ok)
            {
                return status;
            }
        }
        for (auto lane = 0u; lane < current[17]; ++lane)
        {
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
        status = descriptor->function(invocation);
        if (status != Result::Ok)
        {
            return status;
        }
        if (invocation.enterPhase != 0)
        {
            if (invocation.enterPhase > 3)
            {
                return Result::InvalidArgument;
            }
            pendingPhase_ = invocation.enterPhase;
            pendingPhaseTime_ = time;
        }
        if (invocation.forceScope != 0)
        {
            if (!forceRequestsEnabled_)
            {
                return Result::UnsupportedBinary;
            }
            if (invocation.forceScope > 2)
            {
                return Result::InvalidArgument;
            }
            forceScope_ = invocation.forceScope;
            deferredCount_ = 0;
            return Result::Ok;
        }
        if (current[19] == 0)
        {
            index = noInput;
            continue;
        }
        if (invocation.selectedBranch >= current[19])
        {
            return Result::InvalidValueType;
        }
        if (invocation.deferSignal)
        {
            auto guardId =
                program_->version_ >= 8 ? branchGuard(current, program_->version_) : noInput;
            auto usesBranchTime = guardId != noInput;
            auto baseTime = time;
            if (usesBranchTime)
            {
                if (guardId >= program_->branchGuardCount_ || guardId >= 0xff)
                {
                    return Result::UnsupportedBinary;
                }
                auto clock = reinterpret_cast<const std::uint8_t*>(work_) +
                             program_->branchClockOffset_ + std::size_t{guardId} * 24;
                baseTime = readDouble(clock);
            }
            auto due = baseTime + invocation.delaySeconds;
            if (!std::isfinite(invocation.delaySeconds) || invocation.delaySeconds < 0 ||
                !std::isfinite(due))
            {
                return Result::InvalidArgument;
            }
            if (!deferredSignals_)
            {
                return Result::NotReady;
            }
            if (deferredCount_ == deferredCapacity_)
            {
                return Result::QueueRejected;
            }
            deferredSignals_[deferredCount_++] = {
                due, index, invocation.selectedBranch,
                usesBranchTime ? static_cast<std::uint8_t>(guardId) : std::uint8_t{0xff},
                usesBranchTime};
            index = noInput;
            continue;
        }
        status = selectBranch(current, invocation.selectedBranch);
        if (status != Result::Ok)
        {
            return status;
        }
    }
    return Result::Ok;
}

void Instance::run(void* context) noexcept
{
    auto instance = static_cast<Instance*>(context);
    if (instance->pendingPhase_ != 0)
    {
        instance->currentPhase_ = instance->pendingPhase_ - 1;
        instance->phaseStartTime_ = instance->pendingPhaseTime_;
        instance->pendingPhase_ = 0;
    }
    instance->result_ = instance->evaluate();
    if (instance->program_->version_ >= 9)
    {
        if (instance->result_ == Result::Ok)
        {
            for (auto logicalIndex = 0u;
                 logicalIndex < instance->program_->logicalInstructionCount_; ++logicalIndex)
            {
                auto record = instance->program_->bytes_ +
                              instance->program_->logicalInstructionRecordsOffset_ +
                              std::size_t{logicalIndex} * logicalInstructionRecordSize;
                auto nodeIndex = read32(record);
                auto current = instruction(instance->program_->bytes_, 3, nodeIndex);
                if (!instance->isBranchActive(branchGuard(current, instance->program_->version_)))
                {
                    continue;
                }
                auto binding = instance->program_->bindings_[nodeIndex];
                if (binding.logicalDescriptor == nullptr ||
                    binding.logicalDescriptor->commit == nullptr)
                {
                    continue;
                }
                auto state = reinterpret_cast<std::uint8_t*>(instance->work_) +
                             stateOffset(instance->program_->bindings_, nodeIndex);
                auto invocation = LogicalNodeInvocation{nullptr,
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
            instance->generatedSignalsPending_ = instance->program_->logicalInstructionCount_ != 0;
        } else
        {
            auto eventMasks = reinterpret_cast<std::uint8_t*>(instance->work_) +
                              instance->program_->logicalEventMaskOffset_;
            std::memset(eventMasks, 0,
                        std::size_t{instance->program_->nodeCount_} * sizeof(std::uint64_t));
        }
    }
    instance->phase_.store(Phase::Complete, std::memory_order_release);
}

auto Instance::isBranchActive(std::uint32_t guardId) const noexcept -> bool
{
    if (program_ == nullptr || program_->version_ < 8 || guardId == noInput)
    {
        return true;
    }
    auto chain = std::array<std::uint32_t, 64>{};
    auto depth = std::size_t{0};
    auto currentGuard = guardId;
    while (currentGuard != noInput)
    {
        if (depth == chain.size() || currentGuard >= program_->branchGuardCount_)
        {
            return false;
        }
        chain[depth++] = currentGuard;
        auto record = program_->bytes_ + program_->enumGuardRecordsOffset_ +
                      std::size_t{currentGuard} * enumGuardRecordSize;
        currentGuard = read32(record + 20);
    }
    while (depth != 0)
    {
        auto record = program_->bytes_ + program_->enumGuardRecordsOffset_ +
                      std::size_t{chain[--depth]} * enumGuardRecordSize;
        auto selector = read32(record);
        auto selected = work_[selector * 4];
        auto isInteger = std::isfinite(selected) && std::trunc(selected) == selected &&
                         selected >= std::numeric_limits<std::int32_t>::min() &&
                         selected <= std::numeric_limits<std::int32_t>::max();
        auto selectedValue = isInteger ? static_cast<std::int32_t>(selected) : 0;
        auto matched = false;
        auto caseCount = read16(record + 28);
        auto valuesOffset = read32(record + 32);
        for (auto index = 0u; index < caseCount; ++index)
        {
            matched = matched || (isInteger &&
                                  readI32(program_->bytes_ + program_->enumBranchMetadataOffset_ +
                                          valuesOffset + std::size_t{index} * 4) == selectedValue);
        }
        if ((read32(record + 24) & 1u) != 0)
        {
            auto excluded = false;
            auto excludedCount = read16(record + 30);
            auto excludedOffset = read32(record + 36);
            for (auto index = 0u; index < excludedCount; ++index)
            {
                excluded =
                    excluded || (isInteger &&
                                 readI32(program_->bytes_ + program_->enumBranchMetadataOffset_ +
                                         excludedOffset + std::size_t{index} * 4) == selectedValue);
            }
            matched = matched || !excluded;
        }
        if (!matched)
        {
            return false;
        }
    }
    return true;
}

auto Instance::updateBranchClocks(std::uint32_t guardId, bool& active) noexcept -> Result
{
    active = true;
    if (program_ == nullptr || program_->version_ < 8 || guardId == noInput)
    {
        return Result::Ok;
    }
    auto chain = std::array<std::uint32_t, 64>{};
    auto depth = std::size_t{0};
    auto currentGuard = guardId;
    while (currentGuard != noInput)
    {
        if (depth == chain.size() || currentGuard >= program_->branchGuardCount_)
        {
            return Result::InvalidBinary;
        }
        chain[depth++] = currentGuard;
        auto record = program_->bytes_ + program_->enumGuardRecordsOffset_ +
                      std::size_t{currentGuard} * enumGuardRecordSize;
        currentGuard = read32(record + 20);
    }
    auto globalTime = evaluatedTimeContext_ ? effectTime_ : time_;
    while (depth != 0)
    {
        auto guardIndex = chain[--depth];
        auto record = program_->bytes_ + program_->enumGuardRecordsOffset_ +
                      std::size_t{guardIndex} * enumGuardRecordSize;
        auto selector = read32(record);
        auto selected = work_[selector * 4];
        auto isInteger = std::isfinite(selected) && std::trunc(selected) == selected &&
                         selected >= std::numeric_limits<std::int32_t>::min() &&
                         selected <= std::numeric_limits<std::int32_t>::max();
        auto selectedValue = isInteger ? static_cast<std::int32_t>(selected) : 0;
        auto matched = false;
        auto caseCount = read16(record + 28);
        auto valuesOffset = read32(record + 32);
        for (auto index = 0u; index < caseCount; ++index)
        {
            matched = matched || (isInteger &&
                                  readI32(program_->bytes_ + program_->enumBranchMetadataOffset_ +
                                          valuesOffset + std::size_t{index} * 4) == selectedValue);
        }
        if ((read32(record + 24) & 1u) != 0)
        {
            auto excluded = false;
            auto excludedCount = read16(record + 30);
            auto excludedOffset = read32(record + 36);
            for (auto index = 0u; index < excludedCount; ++index)
            {
                excluded =
                    excluded || (isInteger &&
                                 readI32(program_->bytes_ + program_->enumBranchMetadataOffset_ +
                                         excludedOffset + std::size_t{index} * 4) == selectedValue);
            }
            matched = matched || !excluded;
        }
        active = active && matched;
        auto clock = reinterpret_cast<std::uint8_t*>(work_) + program_->branchClockOffset_ +
                     std::size_t{guardIndex} * 24;
        auto localTime = readDouble(clock);
        auto lastGlobalTime = readDouble(clock + 8);
        auto lastSerial = read64(clock + 16);
        if (lastSerial != updateSerial_)
        {
            auto delta = evaluatedTimeContext_
                             ? frameDelta_
                             : (globalTime >= lastGlobalTime ? globalTime - lastGlobalTime : 0.0);
            if (lastSerial == 0)
            {
                delta = 0;
            }
            if (active && delta > 0)
            {
                localTime += delta;
            }
            if (!std::isfinite(localTime))
            {
                return Result::NonFinite;
            }
            writeDouble(clock, localTime);
            writeDouble(clock + 8, globalTime);
            write64(clock + 16, updateSerial_);
        }
    }
    return Result::Ok;
}

auto Instance::evaluate(bool pureRefresh, const bool* dirty) noexcept -> Result
{
    auto profileScope = ProfileScope(profiling_, ProfileEvent::Evaluate);
    if (pureRefresh && dirty == nullptr)
    {
        return Result::InvalidArgument;
    }
    if (program_->version_ == 1)
    {
        if (pureRefresh)
        {
            return Result::InvalidArgument;
        }
        for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
        {
            auto current = instruction(program_->bytes_, 1, index);
            auto debugStatus = beforeDebugNode(index);
            if (debugStatus != Result::Ok)
            {
                return debugStatus;
            }
            auto nodeScope = ProfileScope(profiling_, ProfileEvent::Node, index, profiling_.nodes);
            auto aIndex = read32(current + 20);
            auto bIndex = read32(current + 24);
            auto a = aIndex == noInput
                         ? (defaults_ == nullptr ? readDouble(current + 28) : defaults_[index * 2])
                         : work_[aIndex];
            auto b = bIndex == noInput ? (defaults_ == nullptr ? readDouble(current + 36)
                                                               : defaults_[index * 2 + 1])
                                       : work_[bIndex];
            auto value = double{};
            switch (current[16])
            {
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
                if (b == 0)
                {
                    return Result::DivideByZero;
                }
                value = a / b;
                break;
            default:
                return Result::UnsupportedBinary;
            }
            if (!std::isfinite(value))
            {
                return Result::NonFinite;
            }
            work_[index] = value;
        }
        return Result::Ok;
    }
    if (!pureRefresh && ++updateSerial_ == 0)
    {
        updateSerial_ = 1;
        for (auto index = 0u; index < program_->branchGuardCount_; ++index)
        {
            auto clock = reinterpret_cast<std::uint8_t*>(work_) + program_->branchClockOffset_ +
                         std::size_t{index} * 24;
            write64(clock + 16, 0);
        }
    }
    if (!pureRefresh && program_->version_ >= 9)
    {
        pendingEvaluationSerial_ = evaluationSerial_ + 1;
        auto eventMasks =
            reinterpret_cast<std::uint8_t*>(work_) + program_->logicalEventMaskOffset_;
        std::memset(eventMasks, 0, std::size_t{program_->nodeCount_} * sizeof(std::uint64_t));
    }
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
    {
        auto current = instruction(program_->bytes_, program_->version_, index);
        auto opcode = current[16];
        if (pureRefresh)
        {
            if (!dirty[index] || opcode == customSignalOpcode || opcode == logicalEventSignalOpcode)
            {
                continue;
            }
        }
        auto guardId = program_->version_ >= 8 ? branchGuard(current, program_->version_) : noInput;
        if (guardId != noInput)
        {
            if (pureRefresh)
            {
                if (!isBranchActive(guardId))
                {
                    continue;
                }
            } else
            {
                auto active = false;
                auto clockStatus = updateBranchClocks(guardId, active);
                if (clockStatus != Result::Ok)
                {
                    return clockStatus;
                }
                if (!active)
                {
                    continue;
                }
            }
        }
        if (opcode != customParameterOpcode && opcode != customSignalOpcode &&
            opcode != logicalEventSignalOpcode && opcode != enumSelectOpcode)
        {
            auto debugStatus = beforeDebugNode(index);
            if (debugStatus != Result::Ok)
            {
                return debugStatus;
            }
        }
        auto nodeScope = ProfileScope(profiling_, ProfileEvent::Node, index,
                                      profiling_.nodes && opcode != customSignalOpcode);
        if (program_->version_ >= 3 &&
            (opcode == customParameterOpcode || opcode == customSignalOpcode))
        {
            if (opcode == customSignalOpcode)
            {
                continue;
            }
            auto descriptor = program_->bindings_[index].descriptor;
            if (descriptor == nullptr)
            {
                return Result::UnknownCustomNode;
            }
            if (pureRefresh && !isLatePureBuiltin(descriptor))
            {
                return Result::UnsupportedBinary;
            }
            alignas(customWorkAlignment) auto inputs = std::array<double, customMaxPorts * 4>{};
            auto inputWidths = std::array<std::uint8_t, customMaxPorts>{};
            alignas(customWorkAlignment) auto constants = std::array<double, customMaxPorts * 4>{};
            for (auto input = 0u; input < current[18]; ++input)
            {
                auto inputRecord = current + 48 + input * 8;
                auto sourceIndex = read32(inputRecord);
                inputWidths[input] = descriptor->inputWidths[input];
                for (auto lane = 0u; lane < descriptor->inputWidths[input]; ++lane)
                {
                    constants[input * 4 + lane] =
                        defaults_ == nullptr ? readDouble(current + 80 + input * 32 + lane * 8)
                                             : defaults_[index * 16 + input * 4 + lane];
                    inputs[input * 4 + lane] = sourceIndex == noInput
                                                   ? constants[input * 4 + lane]
                                                   : work_[sourceIndex * 4 + inputRecord[5] + lane];
                }
            }
            auto state =
                reinterpret_cast<std::uint8_t*>(work_) + stateOffset(program_->bindings_, index);
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
            auto debugStatus = debugResolvedInputs(index, time_, inputs.data(), inputWidths.data(),
                                                   descriptor->inputIsColor);
            if (debugStatus != Result::Ok)
            {
                return debugStatus;
            }
            auto status = descriptor->function(invocation);
            if (status != Result::Ok)
            {
                return status;
            }
            for (auto lane = 0u; lane < current[17]; ++lane)
            {
                if (!std::isfinite(work_[index * 4 + lane]))
                {
                    return Result::NonFinite;
                }
            }
            continue;
        }
        if (opcode == logicalEventSignalOpcode)
        {
            if (pureRefresh)
            {
                continue;
            }
            auto descriptor = program_->bindings_[index].logicalDescriptor;
            if (descriptor == nullptr || descriptor->update == nullptr)
            {
                return Result::UnknownCustomNode;
            }
            auto record = static_cast<const std::uint8_t*>(nullptr);
            for (auto logicalIndex = 0u; logicalIndex < program_->logicalInstructionCount_;
                 ++logicalIndex)
            {
                auto candidate = program_->bytes_ + program_->logicalInstructionRecordsOffset_ +
                                 std::size_t{logicalIndex} * logicalInstructionRecordSize;
                if (read32(candidate) == index)
                {
                    record = candidate;
                    break;
                }
            }
            if (record == nullptr)
            {
                return Result::InvalidBinary;
            }
            alignas(customWorkAlignment) auto inputs = std::array<double, customMaxPorts * 4>{};
            auto inputWidths = std::array<std::uint8_t, customMaxPorts>{};
            for (auto input = 0u; input < current[18]; ++input)
            {
                auto inputRecord = current + 48 + input * 8;
                auto sourceIndex = read32(inputRecord);
                inputWidths[input] = descriptor->inputWidths[input];
                for (auto lane = 0u; lane < descriptor->inputWidths[input]; ++lane)
                {
                    auto defaultValue = defaults_ == nullptr
                                            ? readDouble(current + 80 + input * 32 + lane * 8)
                                            : defaults_[index * 16 + input * 4 + lane];
                    inputs[input * 4 + lane] = sourceIndex == noInput
                                                   ? defaultValue
                                                   : work_[sourceIndex * 4 + inputRecord[5] + lane];
                }
            }
            auto state =
                reinterpret_cast<std::uint8_t*>(work_) + stateOffset(program_->bindings_, index);
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
            auto debugStatus = debugResolvedInputs(index, time_, inputs.data(), inputWidths.data());
            if (debugStatus != Result::Ok)
            {
                return debugStatus;
            }
            auto status = descriptor->update(invocation);
            if (status != Result::Ok)
            {
                return status;
            }
            auto validBranches = descriptor->signalBranchCount == 0
                                     ? std::uint64_t{0}
                                     : (std::uint64_t{1} << descriptor->signalBranchCount) - 1;
            if ((invocation.emittedBranches & ~validBranches) != 0)
            {
                return Result::InvalidValueType;
            }
            for (auto lane = 0u; lane < descriptor->outputWidth; ++lane)
            {
                if (!std::isfinite(work_[index * 4 + lane]))
                {
                    return Result::NonFinite;
                }
            }
            auto eventMasks =
                reinterpret_cast<std::uint8_t*>(work_) + program_->logicalEventMaskOffset_;
            write64(eventMasks + std::size_t{index} * sizeof(std::uint64_t),
                    invocation.emittedBranches);
            continue;
        }
        if (opcode == currentPhaseOpcode || opcode == phaseTimeOpcode)
        {
            if (time_ < phaseStartTime_)
            {
                return Result::InvalidArgument;
            }
            work_[index * 4] = opcode == currentPhaseOpcode ? static_cast<double>(currentPhase_)
                                                            : time_ - phaseStartTime_;
            continue;
        }
        if (opcode == enumValueOpcode)
        {
            auto sourceIndex = read32(current + 24);
            auto enumValue =
                sourceIndex == noInput
                    ? (defaults_ == nullptr ? readDouble(current + 32) : defaults_[index * 16])
                    : work_[sourceIndex * 4 + current[21]];
            if (!std::isfinite(enumValue) || std::trunc(enumValue) != enumValue ||
                enumValue < std::numeric_limits<std::int32_t>::min() ||
                enumValue > std::numeric_limits<std::int32_t>::max())
            {
                return Result::InvalidValueType;
            }
            work_[index * 4] = enumValue;
            continue;
        }
        if (opcode == enumSelectOpcode)
        {
            auto selectRecord = static_cast<const std::uint8_t*>(nullptr);
            for (auto selectIndex = 0u; selectIndex < program_->enumSelectCount_; ++selectIndex)
            {
                auto candidate = program_->bytes_ + program_->enumSelectRecordsOffset_ +
                                 std::size_t{selectIndex} * enumSelectRecordSize;
                if (read32(candidate) == index)
                {
                    selectRecord = candidate;
                    break;
                }
            }
            if (selectRecord == nullptr)
            {
                return Result::InvalidBinary;
            }
            auto selector = read32(selectRecord + 4);
            auto selected = work_[selector * 4];
            auto isInteger = std::isfinite(selected) && std::trunc(selected) == selected &&
                             selected >= std::numeric_limits<std::int32_t>::min() &&
                             selected <= std::numeric_limits<std::int32_t>::max();
            auto selectedValue = isInteger ? static_cast<std::int32_t>(selected) : 0;
            auto caseCount = selectRecord[29];
            auto casesOffset = read32(selectRecord + 32);
            auto selectedCase = static_cast<const std::uint8_t*>(nullptr);
            for (auto caseIndex = 0u; caseIndex < caseCount; ++caseIndex)
            {
                auto candidate = program_->bytes_ + program_->enumBranchMetadataOffset_ +
                                 casesOffset + std::size_t{caseIndex} * enumCaseRecordSize;
                if (isInteger && readI32(candidate) == selectedValue)
                {
                    selectedCase = candidate;
                    break;
                }
            }
            auto sourceIndex = noInput;
            auto defaultValues = static_cast<const std::uint8_t*>(nullptr);
            if (selectedCase != nullptr)
            {
                sourceIndex = read32(selectedCase + 4);
                defaultValues = selectedCase + 8;
                if (sourceIndex == noInput && read32(selectRecord + 8) != noInput)
                {
                    sourceIndex = read32(selectRecord + 8);
                }
            } else
            {
                sourceIndex = read32(selectRecord + 8);
                defaultValues = selectRecord + 36;
            }
            auto debugStatus = debugEnumSelect(index, selector, sourceIndex, defaultValues);
            if (debugStatus != Result::Ok)
            {
                return debugStatus;
            }
            for (auto lane = 0u; lane < current[17]; ++lane)
            {
                work_[index * 4 + lane] = sourceIndex == noInput
                                              ? readDouble(defaultValues + lane * 8)
                                              : work_[sourceIndex * 4 + lane];
            }
            continue;
        }
        if (opcode == branchTimeOpcode)
        {
            auto clockIndex = read32(current + 112);
            if (clockIndex >= program_->branchGuardCount_)
            {
                return Result::InvalidBinary;
            }
            auto clock = reinterpret_cast<std::uint8_t*>(work_) + program_->branchClockOffset_ +
                         std::size_t{clockIndex} * 24;
            work_[index * 4] = readDouble(clock);
            continue;
        }
        if (opcode == curveOpcode)
        {
            auto instructionBytes =
                typedHeaderSize + std::size_t{program_->nodeCount_} * customInstructionSize;
            auto curveKeys = program_->bytes_ + instructionBytes + read32(current + 224);
            work_[index * 4] = evaluateCurve(curveKeys, read32(current + 228), time_);
            if (!std::isfinite(work_[index * 4]))
            {
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
                              : defaults_[index * (program_->version_ >= 3 ? 16 : 8) + lane])
                       : work_[aIndex * 4 + current[21] + lane];
        };
        auto valueB = [&](auto lane) noexcept {
            return bIndex == noInput
                       ? (defaults_ == nullptr
                              ? readDouble(current + 64 + lane * 8)
                              : defaults_[index * (program_->version_ >= 3 ? 16 : 8) + 4 + lane])
                       : work_[bIndex * 4 + current[22] + lane];
        };
        if (opcode >= int32Opcode && opcode <= int32ToFloatOpcode)
        {
            auto a = valueA(0);
            if (opcode == floatToInt32Opcode)
            {
                if (!std::isfinite(a))
                {
                    return Result::NonFinite;
                }
                auto truncated = std::trunc(a);
                if (truncated < std::numeric_limits<std::int32_t>::min() ||
                    truncated > std::numeric_limits<std::int32_t>::max())
                {
                    return Result::ArithmeticOverflow;
                }
                work_[base] = truncated;
                continue;
            }
            auto integerA = std::int32_t{};
            auto status = int32FromDouble(a, integerA);
            if (status != Result::Ok)
            {
                return status;
            }
            if (opcode == int32Opcode)
            {
                work_[base] = integerA;
                continue;
            }
            if (opcode == int32ToFloatOpcode)
            {
                work_[base] = static_cast<double>(integerA);
                continue;
            }
            auto integerB = std::int32_t{};
            status = int32FromDouble(valueB(0), integerB);
            if (status != Result::Ok)
            {
                return status;
            }
            if (opcode == int32DivideOpcode)
            {
                if (integerB == 0)
                {
                    return Result::DivideByZero;
                }
                if (integerA == std::numeric_limits<std::int32_t>::min() && integerB == -1)
                {
                    return Result::ArithmeticOverflow;
                }
                work_[base] = integerA / integerB;
                continue;
            }
            auto result = std::int64_t{};
            if (opcode == int32AddOpcode)
            {
                result = std::int64_t{integerA} + integerB;
            } else if (opcode == int32SubtractOpcode)
            {
                result = std::int64_t{integerA} - integerB;
            } else if (opcode == int32MultiplyOpcode)
            {
                result = std::int64_t{integerA} * integerB;
            } else
            {
                return Result::UnsupportedBinary;
            }
            if (result < std::numeric_limits<std::int32_t>::min() ||
                result > std::numeric_limits<std::int32_t>::max())
            {
                return Result::ArithmeticOverflow;
            }
            work_[base] = static_cast<double>(result);
            continue;
        }
        if (opcode == 1 || opcode == 7 || opcode == 8 || opcode == 9 || opcode == 10)
        {
            for (auto lane = 0u; lane < outputWidth; ++lane)
            {
                work_[base + lane] = valueA(lane);
            }
        } else if (opcode == 2)
        {
            work_[base] = time_;
        } else if (opcode >= 3 && opcode <= 22)
        {
            if (outputWidth > 1)
            {
                auto aValues = std::array<double, 4>{};
                auto bValues = std::array<double, 4>{};
                auto parameterStride = program_->version_ >= 3 ? 16u : 8u;
                auto a = aIndex != noInput      ? work_ + aIndex * 4 + current[21]
                         : defaults_ != nullptr ? defaults_ + index * parameterStride
                                                : nullptr;
                auto b = bIndex != noInput      ? work_ + bIndex * 4 + current[22]
                         : defaults_ != nullptr ? defaults_ + index * parameterStride + 4
                                                : nullptr;
                if (a == nullptr)
                {
                    for (auto lane = 0u; lane < outputWidth; ++lane)
                    {
                        aValues[lane] = valueA(lane);
                    }
                    a = aValues.data();
                }
                if (b == nullptr)
                {
                    for (auto lane = 0u; lane < outputWidth; ++lane)
                    {
                        bValues[lane] = valueB(lane);
                    }
                    b = bValues.data();
                }
                auto status = math::arithmetic(work_ + base, a, b, outputWidth,
                                               static_cast<math::Arithmetic>((opcode - 3) % 4));
                if (status == math::ArithmeticResult::DivideByZero)
                {
                    return Result::DivideByZero;
                }
                if (status == math::ArithmeticResult::NonFinite)
                {
                    return Result::NonFinite;
                }
                if (status != math::ArithmeticResult::Ok)
                {
                    return Result::InvalidArgument;
                }
                continue;
            }
            for (auto lane = 0u; lane < outputWidth; ++lane)
            {
                auto a = valueA(lane);
                auto b = valueB(lane);
                auto value = double{};
                switch ((opcode - 3) % 4)
                {
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
                    if (b == 0)
                    {
                        return Result::DivideByZero;
                    }
                    value = a / b;
                    break;
                }
                if (!std::isfinite(value))
                {
                    return Result::NonFinite;
                }
                work_[base + lane] = value;
            }
        } else if (opcode >= 23 && opcode <= 25)
        {
            auto value = valueA(0);
            for (auto lane = 0u; lane < outputWidth; ++lane)
            {
                work_[base + lane] = value;
            }
        } else if (opcode >= 26 && opcode <= 28)
        {
            for (auto lane = 0u; lane < outputWidth; ++lane)
            {
                work_[base + lane] = valueA(lane);
            }
        } else if (opcode >= 29 && opcode <= 34)
        {
            auto copied = opcode == 29 || opcode == 30   ? 2u
                          : opcode == 31 || opcode == 32 ? 3u
                                                         : 4u;
            for (auto lane = 0u; lane < outputWidth; ++lane)
            {
                work_[base + lane] =
                    lane < copied ? valueA(lane)
                                  : (defaults_ == nullptr
                                         ? readDouble(current + 32)
                                         : defaults_[index * (program_->version_ >= 3 ? 16 : 8)]);
            }
        } else if (opcode >= 35 && opcode <= 37)
        {
            for (auto lane = 0u; lane < outputWidth; ++lane)
            {
                work_[base + lane] = valueA(static_cast<std::uint32_t>(
                    defaults_ == nullptr
                        ? readDouble(current + 32 + lane * 8)
                        : defaults_[index * (program_->version_ >= 3 ? 16 : 8) + lane]));
            }
        } else if (opcode == 38 || opcode == 39)
        {
            for (auto lane = 0u; lane < 4; ++lane)
            {
                work_[base + lane] = valueA(lane);
            }
        } else
        {
            return Result::UnsupportedBinary;
        }
        for (auto lane = 0u; lane < outputWidth; ++lane)
        {
            if (!std::isfinite(work_[base + lane]))
            {
                return Result::NonFinite;
            }
        }
    }
    return Result::Ok;
}

} // namespace liberafx
