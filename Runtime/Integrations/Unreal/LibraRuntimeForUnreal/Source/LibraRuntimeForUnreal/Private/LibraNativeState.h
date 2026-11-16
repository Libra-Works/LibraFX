#pragma once

#include "CoreMinimal.h"
#include <array>
#include "LibraEffectAsset.h"
#include "LibraRuntimeTypes.h"
#include "LibraNativeNodesInternal.h"
#include "liberafx/VfxRuntime.h"
#include "LibraCpuProfiler.h"

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
    }
    return ELibraRuntimeResult::UnsupportedBinary;
}

class FCompiledLibraProgram final
{
public:
    explicit FCompiledLibraProgram(TConstArrayView<uint8> bytes)
        : registry(LibraNativeNodeSnapshot())
    {
        auto loadProfiler = FLibraCpuProfiler{};
        if (bytes.Num() <= 0 || bytes.Num() > ULibraEffectAsset::MaximumBytecodeBytes)
        {
            result = liberafx::Result::InvalidBinary;
            return;
        }
        bytecode.Append(bytes.GetData(), bytes.Num());
        result = liberafx::Program::load(bytecode.GetData(), bytecode.Num(), program, loadProfiler.GetHooks());
        if (bytecode.Num() >= 16 &&
            (bytecode[4] == 3 || bytecode[4] == 4 || bytecode[4] == 5) && bytecode[5] == 0)
        {
            auto count = uint32{bytecode[8]} | (uint32{bytecode[9]} << 8) |
                (uint32{bytecode[10]} << 16) | (uint32{bytecode[11]} << 24);
            auto instructionBytes = 16 + static_cast<uint64>(count) * 256;
            if (count > 10000 || static_cast<uint64>(bytecode.Num()) < instructionBytes ||
                (bytecode[4] == 3 && static_cast<uint64>(bytecode.Num()) != instructionBytes))
            {
                result = liberafx::Result::InvalidBinary;
                return;
            }
            bindings.SetNum(static_cast<int32>(count));
            result = liberafx::Program::load(bytecode.GetData(), bytecode.Num(),
                registry->descriptors.GetData(), registry->descriptors.Num(),
                bindings.GetData(), bindings.Num(), program, loadProfiler.GetHooks());
        }
    }

    TSharedRef<const FLibraNodeRegistrySnapshot, ESPMode::ThreadSafe> registry;
    TArray<uint8> bytecode;
    TArray<liberafx::CustomNodeBinding> bindings;
    liberafx::Program program;
    liberafx::Result result = liberafx::Result::NotReady;
};

inline void LibraNodeIdBytes(const FGuid& id, uint8 (&bytes)[16])
{
    auto words = std::array<uint32, 4>{ id.A, id.B, id.C, id.D };
    for (auto index = 0; index < 4; ++index)
        for (auto byte = 0; byte < 4; ++byte)
            bytes[index * 4 + byte] = static_cast<uint8>(words[index] >> (24 - byte * 8));
}
