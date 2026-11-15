#pragma once

#include "CoreMinimal.h"

struct FLibraPreviewConnectionInvitation
{
    FString host;
    int32 port = 0;
    FString accessToken;
    FString certificateFingerprint;
};

bool ParseLibraPreviewConnectionInvitation(const FString& text, FLibraPreviewConnectionInvitation& result, FString& error);
