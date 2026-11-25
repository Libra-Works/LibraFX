#pragma once

#include "CoreMinimal.h"
#include "IDetailCustomization.h"

namespace LibraSourceProject
{
    bool ValidatePath(const FString& input, FString& fullPath, FText& error);
    void AddDetails(IDetailLayoutBuilder& builder, TWeakObjectPtr<UObject> object);
}
