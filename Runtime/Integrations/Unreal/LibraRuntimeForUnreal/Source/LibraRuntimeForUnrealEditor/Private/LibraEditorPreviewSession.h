#pragma once

#include "CoreMinimal.h"
#include "Containers/Ticker.h"
#include "UObject/StrongObjectPtr.h"

class UWorld;
class ULibraEffectComponent;
class ULibraEditorConnectionComponent;

// Explicit opt-in automation preview, isolated from the user's edited level.
class FLibraEditorPreviewSession final
{
public:
    void Start();
    void Stop();

private:
    bool Tick(float deltaTime);
    FTSTicker::FDelegateHandle ticker;
    FDelegateHandle preExit;
    TStrongObjectPtr<UWorld> world;
    TStrongObjectPtr<ULibraEffectComponent> effect;
    TStrongObjectPtr<ULibraEditorConnectionComponent> connection;
};
