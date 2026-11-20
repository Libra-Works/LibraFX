#pragma once
#include "CoreMinimal.h"
#include "UObject/StrongObjectPtr.h"
#include "liberafx/LbProceduralPixel.h"

class UTexture;
class UTextureRenderTarget2D;
class LIBRAGPURUNTIME_API FLibraProceduralGpu final
{
public:
    FLibraProceduralGpu();
    ~FLibraProceduralGpu();
    bool Initialize(uint32 resolution, uint32 operationCount, uint32 outputRegister,
        TConstArrayView<UTexture*> textures, FString& error);
    bool Update(const LibraProceduralPixelOperation* operations, uint32 count, double time);
    UTexture* GetTexture() const;
private:
    struct FState;
    TSharedPtr<FState, ESPMode::ThreadSafe> State;
    TStrongObjectPtr<UTextureRenderTarget2D> Output;
};
