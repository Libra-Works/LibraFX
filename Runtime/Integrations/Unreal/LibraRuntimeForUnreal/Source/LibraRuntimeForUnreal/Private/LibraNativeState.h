#pragma once

#include "CoreMinimal.h"
#include "liberafx/LbRuntimeResult.h"
#include <array>
#include "LibraRuntimeTypes.h"

inline auto ToLibraResult(liberafx::Result result) -> ELibraRuntimeResult
{
    switch (result)
    {
    case liberafx::Result::Ok: return ELibraRuntimeResult::Ok;
    case liberafx::Result::InvalidArgument: return ELibraRuntimeResult::InvalidArgument;
    case liberafx::Result::InvalidBinary: return ELibraRuntimeResult::InvalidBinary;
    case liberafx::Result::UnsupportedBinary: return ELibraRuntimeResult::UnsupportedBinary;
    case liberafx::Result::InsufficientWork: return ELibraRuntimeResult::InsufficientWork;
    case liberafx::Result::Busy: return ELibraRuntimeResult::Busy;
    case liberafx::Result::QueueRejected: return ELibraRuntimeResult::QueueRejected;
    case liberafx::Result::NotReady: return ELibraRuntimeResult::NotReady;
    case liberafx::Result::NodeNotFound: return ELibraRuntimeResult::NodeNotFound;
    case liberafx::Result::DivideByZero: return ELibraRuntimeResult::DivideByZero;
    case liberafx::Result::NonFinite: return ELibraRuntimeResult::NonFinite;
    case liberafx::Result::InvalidValueType: return ELibraRuntimeResult::InvalidValueType;
    case liberafx::Result::UnknownCustomNode: return ELibraRuntimeResult::UnknownCustomNode;
    case liberafx::Result::InvalidHandle: return ELibraRuntimeResult::InvalidHandle;
    case liberafx::Result::InstructionBudgetExceeded: return ELibraRuntimeResult::InstructionBudgetExceeded;
    case liberafx::Result::CounterOverflow: return ELibraRuntimeResult::CounterOverflow;
    case liberafx::Result::ArithmeticOverflow: return ELibraRuntimeResult::ArithmeticOverflow;
    }
    return ELibraRuntimeResult::UnsupportedBinary;
}

inline constexpr auto ToNativeResult(ELibraRuntimeResult result) -> liberafx::Result
{
    switch (result)
    {
    case ELibraRuntimeResult::Ok: return liberafx::Result::Ok;
    case ELibraRuntimeResult::InvalidArgument: return liberafx::Result::InvalidArgument;
    case ELibraRuntimeResult::InvalidBinary: return liberafx::Result::InvalidBinary;
    case ELibraRuntimeResult::UnsupportedBinary: return liberafx::Result::UnsupportedBinary;
    case ELibraRuntimeResult::InsufficientWork: return liberafx::Result::InsufficientWork;
    case ELibraRuntimeResult::Busy: return liberafx::Result::Busy;
    case ELibraRuntimeResult::QueueRejected: return liberafx::Result::QueueRejected;
    case ELibraRuntimeResult::NotReady: return liberafx::Result::NotReady;
    case ELibraRuntimeResult::NodeNotFound: return liberafx::Result::NodeNotFound;
    case ELibraRuntimeResult::DivideByZero: return liberafx::Result::DivideByZero;
    case ELibraRuntimeResult::NonFinite: return liberafx::Result::NonFinite;
    case ELibraRuntimeResult::InvalidValueType: return liberafx::Result::InvalidValueType;
    case ELibraRuntimeResult::UnknownCustomNode: return liberafx::Result::UnknownCustomNode;
    case ELibraRuntimeResult::InvalidHandle: return liberafx::Result::InvalidHandle;
    case ELibraRuntimeResult::InstructionBudgetExceeded: return liberafx::Result::InstructionBudgetExceeded;
    case ELibraRuntimeResult::ArithmeticOverflow: return liberafx::Result::ArithmeticOverflow;
    case ELibraRuntimeResult::CounterOverflow: return liberafx::Result::CounterOverflow;
    }
    return liberafx::Result::UnsupportedBinary;
}

static_assert(ToNativeResult(ELibraRuntimeResult::ArithmeticOverflow) == liberafx::Result::ArithmeticOverflow);
static_assert(ToNativeResult(ELibraRuntimeResult::CounterOverflow) == liberafx::Result::CounterOverflow);

inline void LibraNodeIdBytes(const FGuid& id, uint8 (&bytes)[16])
{
    auto words = std::array<uint32, 4>{ id.A, id.B, id.C, id.D };
    for (auto index = 0; index < 4; ++index)
        for (auto byte = 0; byte < 4; ++byte)
            bytes[index * 4 + byte] = static_cast<uint8>(words[index] >> (24 - byte * 8));
}

inline void LibraNodeIdBytes(const FGuid& id, std::array<uint8, 16>& bytes)
{
    auto words = std::array<uint32, 4>{ id.A, id.B, id.C, id.D };
    for (auto index = 0; index < 4; ++index)
        for (auto byte = 0; byte < 4; ++byte)
            bytes[index * 4 + byte] = static_cast<uint8>(words[index] >> (24 - byte * 8));
}
