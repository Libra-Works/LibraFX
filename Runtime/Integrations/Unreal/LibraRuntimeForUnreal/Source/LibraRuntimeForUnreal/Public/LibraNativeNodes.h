#pragma once

#include "CoreMinimal.h"
#include "LibraRuntimeTypes.h"
#include "liberafx/LbVfxRuntime.h"

class LIBRARUNTIMEFORUNREAL_API FLibraNativeNodes final
{
public:
    // Register on the game thread before loading effects. Callback code and context must
    // remain alive until editor shutdown; close the editor before rebuilding node modules.
    static ELibraRuntimeResult Register(TConstArrayView<liberafx::CustomNodeDescriptor> descriptors);
};
