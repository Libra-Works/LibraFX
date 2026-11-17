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

UENUM(BlueprintType)
enum class ELibraSpawnableHostEventKind : uint8
{
    None = 0,
    Spawn = 1,
    Stop = 2,
    Destroy = 3,
    Signal = 4
};

UENUM(BlueprintType)
enum class ELibraGameIoEventKind : uint8
{
    InputProperty,
    InputSignal,
    OutputProperty,
    OutputSignal,
    OwnerMatrix
};

USTRUCT(BlueprintType)
struct FLibraSpawnableHostEvent
{
    GENERATED_BODY()

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    ELibraSpawnableHostEventKind Kind = ELibraSpawnableHostEventKind::None;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    FGuid ContainerId;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    int64 InstanceId = 0;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    double TimeSeconds = 0;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    int64 Seed = 0;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    FGuid SignalNodeId;
};

USTRUCT(BlueprintType)
struct FLibraGameIoEvent
{
    GENERATED_BODY()

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    FGuid ContainerId;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    int64 InstanceId = 0;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    int32 PortIndex = INDEX_NONE;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    FString PortName;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    ELibraGameIoEventKind Kind = ELibraGameIoEventKind::InputProperty;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    bool bIsColor = false;

    UPROPERTY(BlueprintReadOnly, Category = "Libra Runtime")
    TArray<double> Values;
};

DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(FOnLibraEvaluationCompleted,
    ELibraRuntimeResult, Result, double, TimeSeconds);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnLibraSpawnableHostEvent,
    const FLibraSpawnableHostEvent&, Event);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnLibraGameIoEvent,
    const FLibraGameIoEvent&, Event);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnLibraSpawnableRuntimeError,
    ELibraRuntimeResult, Result);
