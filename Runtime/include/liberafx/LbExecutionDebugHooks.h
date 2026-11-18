#pragma once

#include <cstddef>
#include <cstdint>

namespace liberafx {
enum class Result : std::uint8_t;
enum class DebugValueKind : std::uint8_t { Numeric, Color, Int32, Unavailable };
struct DebugInputValue final {
    std::uint8_t slot = 0, width = 0;
    DebugValueKind kind = DebugValueKind::Unavailable;
    bool connected = false;
    double components[4]{};
    // RFC 4122 bytes; zero for a default or when the source is unknown.
    std::uint8_t sourceNodeId[16]{};
};
struct DebugNodeVisit final {
    const std::uint8_t* nodeId = nullptr;
    const DebugInputValue* inputs = nullptr;
    std::size_t inputCount = 0;
    double time = 0;
    // The actually selected signal edge, if this visit is a signal dispatch.
    const std::uint8_t* signalSourceNodeId = nullptr;
};
using BeforeDebugNode = Result (*)(void* context, const DebugNodeVisit& visit) noexcept;
struct ExecutionDebugHooks final {
    void* context = nullptr;
    BeforeDebugNode beforeNode = nullptr;
};
}
