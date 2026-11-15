#pragma once

#include "CoreMinimal.h"
#include "LibraRuntimeTypes.generated.h"

UENUM(BlueprintType)
enum class ELibraRuntimeResult : uint8
{
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
    InstructionBudgetExceeded
};

UENUM(BlueprintType)
enum class ELibraParameterInput : uint8
{
    A,
    B,
    C,
    D
};

DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(FOnLibraEvaluationCompleted,
    ELibraRuntimeResult, Result, double, TimeSeconds);
