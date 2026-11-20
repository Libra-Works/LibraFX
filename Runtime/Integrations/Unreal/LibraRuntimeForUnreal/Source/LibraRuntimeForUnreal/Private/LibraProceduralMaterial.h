#pragma once
#include "CoreMinimal.h"
class UMaterialInterface;
class UTexture2D;
namespace LibraProceduralMaterial
{
    UTexture2D* WhiteTexture();
UMaterialInterface* Resolve(bool additive, bool doubleSided, FString& error);
}
