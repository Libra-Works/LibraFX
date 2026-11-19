#pragma once

#include "CoreMinimal.h"

class ULibraSpawnableComponent;
namespace liberafx { struct SpawnablePackage; }

class FLibraSpawnableRenderState final
{
public:
    explicit FLibraSpawnableRenderState(ULibraSpawnableComponent& owner);
    ~FLibraSpawnableRenderState();
    bool Prepare(const liberafx::SpawnablePackage& package);
    bool Spawn(FGuid container, int64 instance, double time, uint64 seed);
    void Stop(FGuid container, int64 instance);
    bool Update(double time, bool rootFinished);
    void UpdateOwnerPresentation();
    int32 DrawCount() const;
    int32 NativeDrawPrimitiveCount() const;
    void GetNativeRenderSubmissionStats(int32& logicalRenderObjects, int32& nativePrimitives,
        int32& opaqueBatches, int32& cpuParticleBatches, int32& liveCpuParticles,
        int32& allocatedInstanceCapacity, uint64& submittedBytes) const;
    void GetDrawSnapshots(TArray<FTransform>& transforms, TArray<FBoxSphereBounds>& bounds,
        TArray<FGuid>& drawIds, TArray<FGuid>& containerIds, TArray<int64>& instanceIds) const;
    int32 GpuCount() const;
    void ClearPreviewDebugDraw();

private:
    struct FImpl;
    TUniquePtr<FImpl> Impl;
};
