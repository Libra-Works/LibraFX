#pragma once

#include "CoreMinimal.h"
#include "liberafx/VfxRuntime.h"

struct FLibraNodeRegistrySnapshot final
{
    TArray<liberafx::CustomNodeDescriptor> descriptors;
};

TSharedRef<const FLibraNodeRegistrySnapshot, ESPMode::ThreadSafe> LibraNativeNodeSnapshot();
