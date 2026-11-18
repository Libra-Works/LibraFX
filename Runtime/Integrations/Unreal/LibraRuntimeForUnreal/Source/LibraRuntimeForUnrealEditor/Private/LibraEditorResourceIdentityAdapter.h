#pragma once

#include "CoreMinimal.h"

// Editor-side exact project identity and asset/material-parameter probe. The live
// preview request dispatcher must explicitly route resource_identity_* requests here.
class FLibraEditorResourceIdentityAdapter final
{
public:
    static FString ProcessRequest(const FString& RequestText);
};
