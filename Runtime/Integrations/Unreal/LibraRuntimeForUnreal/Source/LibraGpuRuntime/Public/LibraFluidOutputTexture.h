#pragma once

#include "CoreMinimal.h"
#include "Engine/Texture.h"
#include "LibraFluidOutputTexture.generated.h"

struct FLibraGpuState;

// A borrowed GPU allocation exposed to ordinary engine materials. No pixel copy.
UCLASS(Transient)
class LIBRAGPURUNTIME_API ULibraFluidOutputTexture final : public UTexture
{
    GENERATED_BODY()
public:
    void Initialize(const TSharedPtr<FLibraGpuState, ESPMode::ThreadSafe>& state, int32 output);
    void Invalidate();
    virtual FTextureResource* CreateResource() override;
    virtual ETextureClass GetTextureClass() const override { return ETextureClass::TwoD; }
    virtual EMaterialValueType GetMaterialType() const override { return MCT_Texture2D; }
    virtual float GetSurfaceWidth() const override { return Width; }
    virtual float GetSurfaceHeight() const override { return Height; }
    virtual float GetSurfaceDepth() const override { return 0; }
    virtual uint32 GetSurfaceArraySize() const override { return 0; }
    virtual TextureAddress GetTextureAddressX() const override { return TA_Clamp; }
    virtual TextureAddress GetTextureAddressY() const override { return TA_Clamp; }
private:
    TSharedPtr<FLibraGpuState, ESPMode::ThreadSafe> Source;
    int32 Output = 0;
    uint32 Width = 0, Height = 0;
};
