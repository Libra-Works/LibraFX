#include "liberafx/VfxRuntime.h"

#include <array>
#include <cmath>
#include <cstring>
#include <limits>

namespace liberafx {
namespace {

constexpr auto legacyHeaderSize = std::size_t{12};
constexpr auto legacyInstructionSize = std::size_t{44};
constexpr auto typedHeaderSize = std::size_t{16};
constexpr auto typedInstructionSize = std::size_t{96};
constexpr auto maxNodes = std::uint32_t{10000};
constexpr auto noInput = std::uint32_t{0xffffffffu};
static_assert(sizeof(double) == 8 && std::numeric_limits<double>::is_iec559,
              "LIBERA FX requires IEEE-754 binary64.");

auto read16(const std::uint8_t* bytes) noexcept -> std::uint16_t
{
    return static_cast<std::uint16_t>(bytes[0] | (std::uint16_t{bytes[1]} << 8));
}

auto read32(const std::uint8_t* bytes) noexcept -> std::uint32_t
{
    return std::uint32_t{bytes[0]} | (std::uint32_t{bytes[1]} << 8) |
           (std::uint32_t{bytes[2]} << 16) | (std::uint32_t{bytes[3]} << 24);
}

auto readDouble(const std::uint8_t* bytes) noexcept -> double
{
    auto bits = std::uint64_t{0};
    for (auto index = 0u; index < 8; ++index)
        bits |= std::uint64_t{bytes[index]} << (index * 8);
    auto value = double{};
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

auto instruction(const std::uint8_t* bytes, std::uint16_t version, std::size_t index) noexcept -> const std::uint8_t*
{
    return bytes + (version == 1 ? legacyHeaderSize + index * legacyInstructionSize :
        typedHeaderSize + index * typedInstructionSize);
}

auto validIndex(std::uint32_t input, std::uint32_t index) noexcept -> bool
{
    return input == noInput || input < index;
}

auto typedInputWidth(std::uint32_t input, std::uint8_t width) noexcept -> bool
{
    return input == noInput ? width == 0 : width >= 1 && width <= 4;
}

auto requiredInputWidths(std::uint8_t opcode, std::uint8_t outputWidth,
                         std::uint8_t& inputAWidth, std::uint8_t& inputBWidth,
                         bool& requiresA, bool& requiresB) noexcept -> bool
{
    requiresA = false;
    requiresB = false;
    if (opcode == 1)
    {
        return outputWidth == 1 && inputAWidth <= 1 && inputBWidth == 0;
    }
    if (opcode == 2) return outputWidth == 1 && inputAWidth == 0 && inputBWidth == 0;
    if (opcode >= 3 && opcode <= 6)
    {
        return outputWidth == 1 && inputAWidth == 1 && inputBWidth == 1;
    }
    if (opcode >= 7 && opcode <= 10)
    {
        auto expected = opcode == 7 ? 2 : opcode == 8 ? 3 : 4;
        return outputWidth == expected && (inputAWidth == 0 || inputAWidth == expected) && inputBWidth == 0;
    }
    if (opcode >= 11 && opcode <= 22)
    {
        auto expected = opcode <= 14 ? 2 : opcode <= 18 ? 3 : 4;
        return outputWidth == expected && inputAWidth == expected && inputBWidth == expected;
    }
    if (opcode >= 23 && opcode <= 25)
    {
        requiresA = true;
        auto expected = opcode == 23 ? 2 : opcode == 24 ? 3 : 4;
        return outputWidth == expected && inputAWidth == 1 && inputBWidth == 0;
    }
    if (opcode >= 26 && opcode <= 28)
    {
        requiresA = true;
        auto expected = opcode == 26 ? 2 : opcode == 27 ? 3 : 4;
        return outputWidth == expected && inputAWidth == expected && inputBWidth == 0;
    }
    if (opcode >= 29 && opcode <= 34)
    {
        requiresA = true;
        auto expectedInput = opcode <= 30 ? 2 : opcode <= 32 ? 3 : 4;
        auto expectedOutput = opcode == 29 || opcode == 32 || opcode == 34 ? 3 :
                              opcode == 30 || opcode == 33 ? 4 : 2;
        return outputWidth == expectedOutput && inputAWidth == expectedInput && inputBWidth == 0;
    }
    if (opcode >= 35 && opcode <= 37)
    {
        requiresA = true;
        auto expected = opcode == 35 ? 2 : opcode == 36 ? 3 : 4;
        return outputWidth == expected && inputAWidth == 4 && inputBWidth == 0;
    }
    if (opcode == 38 || opcode == 39)
    {
        requiresA = true;
        return outputWidth == 4 && inputAWidth == 4 && inputBWidth == 0;
    }
    return false;
}

auto validTypedInstruction(const std::uint8_t* current, std::uint32_t index) noexcept -> bool
{
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
    if (opcode >= 3 && opcode <= 6)
    {
        if (inputA == noInput) shapeAWidth = 1;
        if (inputB == noInput) shapeBWidth = 1;
    }
    if (opcode >= 11 && opcode <= 22)
    {
        if (inputA == noInput) shapeAWidth = outputWidth;
        if (inputB == noInput) shapeBWidth = outputWidth;
    }
    auto requiresA = false;
    auto requiresB = false;
    if (outputWidth < 1 || outputWidth > 4 ||
        !typedInputWidth(inputA, inputAWidth) || !typedInputWidth(inputB, inputBWidth) ||
        !validIndex(inputA, index) || !validIndex(inputB, index) ||
        !requiredInputWidths(opcode, outputWidth, shapeAWidth, shapeBWidth, requiresA, requiresB) ||
        (requiresA && inputA == noInput) || (requiresB && inputB == noInput) ||
        (typeFlags & 0xf8) != 0 || current[23] != 0 ||
        inputASourceComponent > 3 || inputBSourceComponent > 3 ||
        (inputA == noInput && inputASourceComponent != 0) ||
        (inputB == noInput && inputBSourceComponent != 0))
        return false;
    auto outputIsColor = (typeFlags & 1) != 0;
    auto inputAIsColor = (typeFlags & 2) != 0;
    auto inputBIsColor = (typeFlags & 4) != 0;
    auto expectedInputAColor = opcode == 38 || (opcode == 10 && inputA != noInput);
    if (outputIsColor != (opcode == 10 || opcode == 39) ||
        inputAIsColor != expectedInputAColor || inputBIsColor ||
        (inputA == noInput && inputAIsColor) || (inputB == noInput && inputBIsColor))
        return false;
    for (auto lane = 0u; lane < 4; ++lane)
        if (!std::isfinite(readDouble(current + 32 + lane * 8)) ||
            !std::isfinite(readDouble(current + 64 + lane * 8)))
            return false;
    if (opcode >= 35 && opcode <= 37)
        for (auto lane = 0u; lane < outputWidth; ++lane)
        {
            auto channel = readDouble(current + 32 + lane * 8);
            if (channel < 0 || channel >= 4 || std::floor(channel) != channel) return false;
        }
    if (opcode == 10)
        for (auto lane = 0u; lane < 4; ++lane)
        {
            auto channel = readDouble(current + 32 + lane * 8);
            if (channel < 0 || (lane == 3 && channel > 1)) return false;
        }
    return true;
}

auto typedSourceWidth(const std::uint8_t* bytes, std::uint32_t input) noexcept -> std::uint8_t
{
    return input == noInput ? 0 : instruction(bytes, 2, input)[17];
}

auto rangesOverlap(const void* first, std::size_t firstSize, const void* second,
                   std::size_t secondSize) noexcept -> bool
{
    if (firstSize == 0 || secondSize == 0) return false;
    auto firstStart = reinterpret_cast<std::uintptr_t>(first);
    auto secondStart = reinterpret_cast<std::uintptr_t>(second);
    if (firstSize > std::numeric_limits<std::uintptr_t>::max() - firstStart ||
        secondSize > std::numeric_limits<std::uintptr_t>::max() - secondStart)
        return true;
    return firstStart < secondStart + secondSize && secondStart < firstStart + firstSize;
}

}

auto Program::load(const void* bytes, std::size_t size, Program& out) noexcept -> Result
{
    if (bytes == nullptr || size < legacyHeaderSize) return Result::InvalidArgument;
    auto data = static_cast<const std::uint8_t*>(bytes);
    if (std::memcmp(data, "LFXB", 4) != 0) return Result::InvalidBinary;
    auto version = read16(data + 4);
    if (version != 1 && version != 2) return Result::UnsupportedBinary;
    if (data[6] != 1) return Result::UnsupportedBinary;
    if (data[7] != 0) return Result::InvalidBinary;
    auto count = read32(data + 8);
    auto headerSize = version == 1 ? legacyHeaderSize : typedHeaderSize;
    auto instructionSize = version == 1 ? legacyInstructionSize : typedInstructionSize;
    if (count > maxNodes || size < headerSize) return Result::InvalidBinary;
    if (version == 2 &&
        (read16(data + 12) != typedInstructionSize || read16(data + 14) != 0))
        return Result::UnsupportedBinary;
    if (size != headerSize + std::size_t{count} * instructionSize) return Result::InvalidBinary;
    for (auto index = std::uint32_t{0}; index < count; ++index)
    {
        auto current = instruction(data, version, index);
        auto emptyId = true;
        for (auto byte = 0u; byte < 16; ++byte) emptyId = emptyId && current[byte] == 0;
        if (emptyId) return Result::InvalidBinary;
        for (auto other = std::uint32_t{0}; other < index; ++other)
            if (std::memcmp(current, instruction(data, version, other), 16) == 0) return Result::InvalidBinary;
        if (version == 1)
        {
            auto opcode = current[16];
            auto a = read32(current + 20);
            auto b = read32(current + 24);
            if (opcode < 1 || opcode > 6) return Result::UnsupportedBinary;
            if (current[17] != 0 || current[18] != 0 || current[19] != 0 ||
                !validIndex(a, index) || !validIndex(b, index) ||
                !std::isfinite(readDouble(current + 28)) || !std::isfinite(readDouble(current + 36)) ||
                (opcode == 1 && b != noInput) || (opcode == 2 && (a != noInput || b != noInput)))
                return Result::InvalidBinary;
        }
        else
        {
            if (current[16] == 0 || current[16] > 39) return Result::UnsupportedBinary;
            if (!validTypedInstruction(current, index)) return Result::InvalidBinary;
            auto inputA = read32(current + 24);
            auto inputB = read32(current + 28);
            auto validSource = [&](auto sourceIndex, auto sourceWidth, auto selectedLane, auto colorFlag) noexcept {
                if (sourceIndex == noInput) return sourceWidth == 0 && selectedLane == 0 && colorFlag == 0;
                auto source = instruction(data, 2, sourceIndex);
                auto sourceIsBreak = source[16] >= 26 && source[16] <= 28;
                if (sourceIsBreak)
                    return sourceWidth == 1 && selectedLane < source[17] && colorFlag == 0;
                return sourceWidth == source[17] && selectedLane == 0 &&
                       ((source[20] & 1) != 0) == (colorFlag != 0);
            };
            if (!validSource(inputA, current[18], current[21], current[20] & 2) ||
                !validSource(inputB, current[19], current[22], current[20] & 4))
                return Result::InvalidBinary;
        }
    }
    out.bytes_ = data;
    out.nodeCount_ = count;
    out.version_ = version;
    return Result::Ok;
}

auto Instance::initialize(const Program& program, double* work, std::size_t workCount) noexcept -> Result
{
    return initialize(program, work, workCount, nullptr, 0);
}

auto Instance::initialize(const Program& program, double* work, std::size_t workCount,
                          double* defaults, std::size_t defaultsCount) noexcept -> Result
{
    if (phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    auto workRequiredCount = program.nodeCount_ * (program.version_ == 1 ? 1u : 4u);
    auto defaultsRequiredCount = program.nodeCount_ * (program.version_ == 1 ? 2u : 8u);
    if (program.bytes_ == nullptr || (workRequiredCount > 0 && work == nullptr))
        return Result::InvalidArgument;
    if (workCount < workRequiredCount || (defaults != nullptr && defaultsCount < defaultsRequiredCount))
        return Result::InsufficientWork;
    if (defaults == nullptr && defaultsCount != 0) return Result::InvalidArgument;
    if (defaults != nullptr &&
        rangesOverlap(work, workRequiredCount * sizeof(double), defaults, defaultsRequiredCount * sizeof(double)))
        return Result::InvalidArgument;
    if (defaults != nullptr)
        for (auto index = std::uint32_t{0}; index < program.nodeCount_; ++index)
        {
            auto current = instruction(program.bytes_, program.version_, index);
            if (program.version_ == 1)
            {
                defaults[index * 2] = readDouble(current + 28);
                defaults[index * 2 + 1] = readDouble(current + 36);
            }
            else
                for (auto lane = 0u; lane < 8; ++lane)
                    defaults[index * 8 + lane] = readDouble(current + (lane < 4 ? 32 + lane * 8 : 64 + (lane - 4) * 8));
        }
    program_ = &program;
    work_ = work;
    defaults_ = defaults;
    result_ = Result::NotReady;
    phase_.store(Phase::Idle, std::memory_order_release);
    return Result::Ok;
}

auto Instance::updateAsync(double time, Enqueue enqueue, void* queueContext) noexcept -> Result
{
    if (program_ == nullptr || enqueue == nullptr || !std::isfinite(time)) return Result::InvalidArgument;
    auto phase = phase_.load(std::memory_order_acquire);
    for (;;)
    {
        if (phase == Phase::Running) return Result::Busy;
        if (phase_.compare_exchange_weak(phase, Phase::Running, std::memory_order_acq_rel)) break;
    }
    time_ = time;
    if (!enqueue(queueContext, &Instance::run, this))
    {
        phase_.store(Phase::Idle, std::memory_order_release);
        return Result::QueueRejected;
    }
    return Result::Ok;
}

auto Instance::setParameter(const std::uint8_t nodeId[16], std::uint8_t inputSlot,
                            std::uint8_t component, double value) noexcept -> Result
{
    if (phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    if (program_ == nullptr || defaults_ == nullptr || nodeId == nullptr || inputSlot > 1)
        return Result::InvalidArgument;
    if (!std::isfinite(value)) return Result::NonFinite;
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
    {
        auto current = instruction(program_->bytes_, program_->version_, index);
        if (std::memcmp(current, nodeId, 16) != 0) continue;
        auto opcode = current[16];
        auto width = program_->version_ == 1 ? 1u : current[17];
        auto laneCount = 0u;
        if (program_->version_ == 1)
        {
            if ((inputSlot == 0 && (opcode == 1 || (opcode >= 3 && opcode <= 6))) ||
                (inputSlot == 1 && opcode >= 3 && opcode <= 6))
                laneCount = 1;
        }
        else if (inputSlot == 0)
        {
            if (opcode == 1 || (opcode >= 7 && opcode <= 10)) laneCount = width;
            else if ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22)) laneCount = width;
            else if (opcode == 29 || opcode == 30 || opcode == 32) laneCount = 1;
            else if (opcode >= 35 && opcode <= 37) laneCount = width;
        }
        else if ((opcode >= 3 && opcode <= 6) || (opcode >= 11 && opcode <= 22))
            laneCount = width;
        if (component >= laneCount) return Result::InvalidValueType;
        if (opcode >= 26 && opcode <= 28)
        {
            auto inputWidth = current[18];
            if (value < 0 || value >= inputWidth || std::floor(value) != value)
                return Result::InvalidValueType;
        }
        if (opcode >= 35 && opcode <= 37 &&
            (value < 0 || value >= 4 || std::floor(value) != value))
            return Result::InvalidValueType;
        if (opcode == 10 && (value < 0 || (component == 3 && value > 1)))
            return Result::InvalidValueType;
        if (program_->version_ == 1)
            defaults_[index * 2 + inputSlot] = value;
        else
            defaults_[index * 8 + inputSlot * 4 + component] = value;
        return Result::Ok;
    }
    return Result::NodeNotFound;
}

auto Instance::result() const noexcept -> Result
{
    return phase_.load(std::memory_order_acquire) == Phase::Complete ? result_ : Result::NotReady;
}

auto Instance::value(const std::uint8_t nodeId[16], double& out) const noexcept -> Result
{
    auto components = std::array<double, 4>{};
    auto count = std::uint8_t{};
    auto status = valueComponents(nodeId, components.data(), count);
    if (status == Result::Ok)
    {
        if (count != 1) return Result::InvalidValueType;
        out = components[0];
    }
    return status;
}

auto Instance::valueComponents(const std::uint8_t nodeId[16], double out[4],
                              std::uint8_t& componentCount) const noexcept -> Result
{
    if (nodeId == nullptr || out == nullptr) return Result::InvalidArgument;
    auto status = result();
    if (status != Result::Ok) return status;
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
    {
        auto current = instruction(program_->bytes_, program_->version_, index);
        if (std::memcmp(current, nodeId, 16) == 0)
        {
            auto width = program_->version_ == 1 ? 1u : current[17];
            for (auto lane = 0u; lane < width; ++lane)
                out[lane] = work_[index * (program_->version_ == 1 ? 1u : 4u) + lane];
            for (auto lane = width; lane < 4; ++lane) out[lane] = 0;
            componentCount = static_cast<std::uint8_t>(width);
            return Result::Ok;
        }
    }
    return Result::NodeNotFound;
}

void Instance::run(void* context) noexcept
{
    auto instance = static_cast<Instance*>(context);
    instance->result_ = instance->evaluate();
    instance->phase_.store(Phase::Complete, std::memory_order_release);
}

auto Instance::evaluate() noexcept -> Result
{
    if (program_->version_ == 1)
    {
        for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
        {
            auto current = instruction(program_->bytes_, 1, index);
            auto aIndex = read32(current + 20);
            auto bIndex = read32(current + 24);
            auto a = aIndex == noInput ?
                (defaults_ == nullptr ? readDouble(current + 28) : defaults_[index * 2]) : work_[aIndex];
            auto b = bIndex == noInput ?
                (defaults_ == nullptr ? readDouble(current + 36) : defaults_[index * 2 + 1]) : work_[bIndex];
            auto value = double{};
            switch (current[16])
            {
            case 1: value = a; break;
            case 2: value = time_; break;
            case 3: value = a + b; break;
            case 4: value = a - b; break;
            case 5: value = a * b; break;
            case 6:
                if (b == 0) return Result::DivideByZero;
                value = a / b;
                break;
            default: return Result::UnsupportedBinary;
            }
            if (!std::isfinite(value)) return Result::NonFinite;
            work_[index] = value;
        }
        return Result::Ok;
    }
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
    {
        auto current = instruction(program_->bytes_, 2, index);
        auto opcode = current[16];
        auto outputWidth = current[17];
        auto aIndex = read32(current + 24);
        auto bIndex = read32(current + 28);
        auto base = index * 4;
        auto valueA = [&](auto lane) noexcept {
            return aIndex == noInput ?
                (defaults_ == nullptr ? readDouble(current + 32 + lane * 8) : defaults_[index * 8 + lane]) :
                work_[aIndex * 4 + current[21] + lane];
        };
        auto valueB = [&](auto lane) noexcept {
            return bIndex == noInput ?
                (defaults_ == nullptr ? readDouble(current + 64 + lane * 8) : defaults_[index * 8 + 4 + lane]) :
                work_[bIndex * 4 + current[22] + lane];
        };
        if (opcode == 1 || opcode == 7 || opcode == 8 || opcode == 9 || opcode == 10)
        {
            for (auto lane = 0u; lane < outputWidth; ++lane) work_[base + lane] = valueA(lane);
        }
        else if (opcode == 2)
        {
            work_[base] = time_;
        }
        else if (opcode >= 3 && opcode <= 22)
        {
            for (auto lane = 0u; lane < outputWidth; ++lane)
            {
                auto a = valueA(lane);
                auto b = valueB(lane);
                auto value = double{};
                switch ((opcode - 3) % 4)
                {
                case 0: value = a + b; break;
                case 1: value = a - b; break;
                case 2: value = a * b; break;
                case 3:
                    if (b == 0) return Result::DivideByZero;
                    value = a / b;
                    break;
                }
                if (!std::isfinite(value)) return Result::NonFinite;
                work_[base + lane] = value;
            }
        }
        else if (opcode >= 23 && opcode <= 25)
        {
            auto value = valueA(0);
            for (auto lane = 0u; lane < outputWidth; ++lane) work_[base + lane] = value;
        }
        else if (opcode >= 26 && opcode <= 28)
        {
            for (auto lane = 0u; lane < outputWidth; ++lane)
                work_[base + lane] = valueA(lane);
        }
        else if (opcode >= 29 && opcode <= 34)
        {
            auto copied = opcode == 29 || opcode == 30 ? 2u : opcode == 31 || opcode == 32 ? 3u : 4u;
            for (auto lane = 0u; lane < outputWidth; ++lane)
                work_[base + lane] = lane < copied ? valueA(lane) :
                    (defaults_ == nullptr ? readDouble(current + 32) : defaults_[index * 8]);
        }
        else if (opcode >= 35 && opcode <= 37)
        {
            for (auto lane = 0u; lane < outputWidth; ++lane)
                work_[base + lane] = valueA(static_cast<std::uint32_t>(
                    defaults_ == nullptr ? readDouble(current + 32 + lane * 8) : defaults_[index * 8 + lane]));
        }
        else if (opcode == 38 || opcode == 39)
        {
            for (auto lane = 0u; lane < 4; ++lane) work_[base + lane] = valueA(lane);
        }
        else return Result::UnsupportedBinary;
        for (auto lane = 0u; lane < outputWidth; ++lane)
            if (!std::isfinite(work_[base + lane])) return Result::NonFinite;
    }
    return Result::Ok;
}

}
