#pragma once

#include "CoreMinimal.h"

struct FLibraProfileMeasurement final
{
    FString Event;
    FGuid NodeId;
    uint64 Calls = 0;
    uint64 InclusiveNs = 0;
    uint64 ExclusiveNs = 0;
    uint64 MinNs = 0;
    uint64 MaxNs = 0;
};
