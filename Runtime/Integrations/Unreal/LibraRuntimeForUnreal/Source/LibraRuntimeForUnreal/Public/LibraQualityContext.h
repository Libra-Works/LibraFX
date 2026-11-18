#pragma once

#include "CoreMinimal.h"
#include "liberafx/LbHostSignalContext.h"

// Read on the game thread before scheduling a tick; pass the copied result to workers.
LIBRARUNTIMEFORUNREAL_API liberafx::Result ReadLibraQualitySnapshot(
    uint64 frameSerial, liberafx::HostQualitySnapshot& snapshot);
