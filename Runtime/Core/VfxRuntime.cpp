#include "liberafx/VfxRuntime.h"

#include <cmath>
#include <cstring>
#include <limits>

namespace liberafx {
namespace {

constexpr auto headerSize = std::size_t{12};
constexpr auto instructionSize = std::size_t{44};
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

auto instruction(const std::uint8_t* bytes, std::size_t index) noexcept -> const std::uint8_t*
{
    return bytes + headerSize + index * instructionSize;
}

auto validIndex(std::uint32_t input, std::uint32_t index) noexcept -> bool
{
    return input == noInput || input < index;
}

}

auto Program::load(const void* bytes, std::size_t size, Program& out) noexcept -> Result
{
    if (bytes == nullptr || size < headerSize) return Result::InvalidArgument;
    auto data = static_cast<const std::uint8_t*>(bytes);
    if (std::memcmp(data, "LFXB", 4) != 0) return Result::InvalidBinary;
    if (read16(data + 4) != 1 || data[6] != 1) return Result::UnsupportedBinary;
    if (data[7] != 0) return Result::InvalidBinary;
    auto count = read32(data + 8);
    if (count > maxNodes || size != headerSize + std::size_t{count} * instructionSize)
        return Result::InvalidBinary;
    for (auto index = std::uint32_t{0}; index < count; ++index)
    {
        auto current = instruction(data, index);
        auto emptyId = true;
        for (auto byte = 0u; byte < 16; ++byte) emptyId = emptyId && current[byte] == 0;
        if (emptyId) return Result::InvalidBinary;
        for (auto other = std::uint32_t{0}; other < index; ++other)
            if (std::memcmp(current, instruction(data, other), 16) == 0) return Result::InvalidBinary;
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
    out.bytes_ = data;
    out.nodeCount_ = count;
    return Result::Ok;
}

auto Instance::initialize(const Program& program, double* work, std::size_t workCount) noexcept -> Result
{
    if (phase_.load(std::memory_order_acquire) == Phase::Running) return Result::Busy;
    if (program.bytes_ == nullptr || (program.nodeCount_ > 0 && work == nullptr))
        return Result::InvalidArgument;
    if (workCount < program.nodeCount_) return Result::InsufficientWork;
    program_ = &program;
    work_ = work;
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

auto Instance::result() const noexcept -> Result
{
    return phase_.load(std::memory_order_acquire) == Phase::Complete ? result_ : Result::NotReady;
}

auto Instance::value(const std::uint8_t nodeId[16], double& out) const noexcept -> Result
{
    if (nodeId == nullptr) return Result::InvalidArgument;
    auto status = result();
    if (status != Result::Ok) return status;
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
    {
        if (std::memcmp(instruction(program_->bytes_, index), nodeId, 16) == 0)
        {
            out = work_[index];
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
    for (auto index = std::uint32_t{0}; index < program_->nodeCount_; ++index)
    {
        auto current = instruction(program_->bytes_, index);
        auto aIndex = read32(current + 20);
        auto bIndex = read32(current + 24);
        auto a = aIndex == noInput ? readDouble(current + 28) : work_[aIndex];
        auto b = bIndex == noInput ? readDouble(current + 36) : work_[bIndex];
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

}
