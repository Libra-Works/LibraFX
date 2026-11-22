#pragma once

#include "CoreMinimal.h"
#include "liberafx/LbCustomNodePolicy.h"
#include "liberafx/LbVfxRuntime.h"

struct FLibraNodeRegistrySnapshot final
{
    TArray<liberafx::CustomNodeDescriptor> descriptors;
    TArray<liberafx::CustomNodePolicy> policies;

    TConstArrayView<liberafx::CustomNodeDescriptor> ProjectDescriptors() const
    {
        // The package host supplies its own passthrough descriptor.
        return TConstArrayView<liberafx::CustomNodeDescriptor>(descriptors).RightChop(1);
    }
};

TSharedRef<const FLibraNodeRegistrySnapshot, ESPMode::ThreadSafe> LibraNativeNodeSnapshot();
