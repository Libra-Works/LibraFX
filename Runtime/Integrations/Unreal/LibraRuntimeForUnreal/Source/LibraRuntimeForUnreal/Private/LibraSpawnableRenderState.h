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
    int32 DrawCount() const;
    int32 GpuCount() const;

private:
    struct FImpl;
    TUniquePtr<FImpl> Impl;
};
