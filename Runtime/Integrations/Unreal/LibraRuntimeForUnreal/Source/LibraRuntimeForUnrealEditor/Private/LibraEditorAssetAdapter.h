#pragma once

#include "CoreMinimal.h"

class FLibraEditorAssetAdapter final
{
public:
    static FString ProcessRequest(const FString& request);
    static void Shutdown();
};
