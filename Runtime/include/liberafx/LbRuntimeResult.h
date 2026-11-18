#pragma once

#include <cstdint>

namespace liberafx {

enum class Result : std::uint8_t {
    Ok,
    InvalidArgument,
    InvalidBinary,
    UnsupportedBinary,
    InsufficientWork,
    Busy,
    QueueRejected,
    NotReady,
    NodeNotFound,
    DivideByZero,
    NonFinite,
    InvalidValueType,
    UnknownCustomNode,
    InvalidHandle,
    InstructionBudgetExceeded,
    ArithmeticOverflow = 15,
    CounterOverflow = 17
};

}
