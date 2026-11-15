#pragma once

#include "CoreMinimal.h"

struct FLibraPreviewDiscoveryCandidate
{
    FString pipeName;
    FString invitation;
};

class FLibraPreviewDiscovery final
{
public:
    FLibraPreviewDiscovery();
    ~FLibraPreviewDiscovery();
    void Begin(const FString& overridePipeName = FString{});
    void Cancel();
    bool Poll(TArray<FLibraPreviewDiscoveryCandidate>& candidates, FString& error);
    bool IsRunning() const;

private:
    friend class FLibraPreviewDiscoveryTest;
    struct FState;
    TSharedPtr<FState, ESPMode::ThreadSafe> state;
};
