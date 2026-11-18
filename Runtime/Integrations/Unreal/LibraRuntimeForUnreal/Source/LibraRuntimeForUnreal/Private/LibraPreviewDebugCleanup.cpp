#include "LibraEffectComponent.h"
#include "LibraSpawnableComponent.h"
#include "LibraSpawnableRenderState.h"

void ULibraSpawnableComponent::ClearPreviewDebugDraw()
{
    check(IsInGameThread());
    bSuppressPreviewDebugDraw = true;
    if (RenderState) RenderState->ClearPreviewDebugDraw();
}

void ULibraEffectComponent::ClearPreviewDebugDraw()
{
    check(IsInGameThread());
    bSuppressPreviewDebugDraw = true;
    if (PackagePlayback) PackagePlayback->ClearPreviewDebugDraw();
}
