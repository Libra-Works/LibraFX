#pragma once

#include "liberafx/LbExecutionDebugger.h"

namespace liberafx::host {
// Fixed-width local protocol; UUIDs are RFC 4122; all numbers little endian.
// Bytecode identity is not authentication. The transport must authenticate its peer.
class ExecutionDebugProtocol final {
public:
    using Command = std::array<std::uint8_t, 32>;
    using Frame = std::array<std::uint8_t, 56 + 16 * 56 + 32>;
    static std::uint64_t programIdentity(const std::uint8_t* bytes, std::size_t length) noexcept
    {
        auto hash = UINT64_C(14695981039346656037);
        for (auto i = std::size_t{}; i < length; ++i)
            hash = (hash ^ bytes[i]) * UINT64_C(1099511628211);
        return hash;
    }
    explicit ExecutionDebugProtocol(std::uint64_t identity, std::uint64_t generation = 0,
        const std::uint8_t* container = nullptr, std::uint64_t instance = 0) noexcept
        : debugger_(breakpoints_.data(), breakpoints_.size()), identity_(identity), generation_(generation), instance_(instance)
    {
        if (container) std::memcpy(container_.data(), container, 16);
    }
    ExecutionDebugger& debugger() noexcept { return debugger_; }
    bool exchange(const Command& command, Frame& out) noexcept
    {
        if (std::memcmp(command.data(), "LFD2", 4) != 0 ||
            command[5] || command[6] || command[7] || command[4] > 6)
            return false;
        auto identity = std::uint64_t{};
        for (auto i = 0u; i < 8; ++i) identity |= std::uint64_t{command[8 + i]} << (8 * i);
        auto result = identity == identity_ ? Result::Ok : Result::InvalidBinary;
        if (result == Result::Ok)
        {
            auto id = ExecutionDebugger::NodeId{};
            std::memcpy(id.data(), command.data() + 16, 16);
            switch (command[4])
            {
                case 1: result = debugger_.attach(); break;
                case 2: case 3: result = debugger_.breakpoint(id, command[4] == 2); break;
                case 4: result = debugger_.pause(); break;
                case 5: case 6: result = debugger_.resume(command[4] == 5); break;
                default: break;
            }
        }
        out = {};
        std::memcpy(out.data(), "LFD2", 4);
        out[5] = static_cast<std::uint8_t>(result);
        if (identity != identity_) return true; // Never disclose program contents on mismatch.
        number(out.data() + 952, generation_);
        number(out.data() + 960, instance_);
        std::memcpy(out.data() + 968, container_.data(), 16);
        auto snapshot = DebugPauseSnapshot{};
        auto state = debugger_.sample(snapshot);
        out[4] = static_cast<std::uint8_t>(state);
        if (state != DebugExecutionState::Running && state != DebugExecutionState::Paused) return true;
        out[6] = static_cast<std::uint8_t>(snapshot.inputCount);
        number(out.data() + 8, snapshot.sequence);
        std::memcpy(out.data() + 16, snapshot.nodeId.data(), 16);
        real(out.data() + 32, snapshot.time);
        std::memcpy(out.data() + 40, snapshot.signalSourceNodeId.data(), 16);
        for (auto i = std::size_t{}; i < snapshot.inputCount; ++i)
        {
            const auto& input = snapshot.inputs[i];
            auto value = out.data() + 56 + i * 56;
            value[0] = input.slot;
            value[1] = state == DebugExecutionState::Paused ? input.width : 0;
            value[2] = state == DebugExecutionState::Paused ?
                static_cast<std::uint8_t>(input.kind) : static_cast<std::uint8_t>(DebugValueKind::Unavailable);
            value[3] = input.connected ? 1 : 0;
            std::memcpy(value + 4, input.sourceNodeId, 16);
            for (auto lane = 0u; lane < value[1]; ++lane) real(value + 20 + lane * 8, input.components[lane]);
        }
        return true;
    }
private:
    static void number(std::uint8_t* target, std::uint64_t value) noexcept
    {
        for (auto i = 0u; i < 8; ++i) target[i] = static_cast<std::uint8_t>(value >> (i * 8));
    }
    static void real(std::uint8_t* target, double value) noexcept
    {
        auto bits = std::uint64_t{};
        std::memcpy(&bits, &value, 8);
        number(target, bits);
    }
    std::array<ExecutionDebugger::NodeId, 128> breakpoints_{};
    ExecutionDebugger debugger_;
    std::uint64_t identity_, generation_, instance_;
    std::array<std::uint8_t, 16> container_{};
};
}
