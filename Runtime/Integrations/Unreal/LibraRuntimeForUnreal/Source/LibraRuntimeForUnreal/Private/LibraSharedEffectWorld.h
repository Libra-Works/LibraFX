#pragma once
#include "CoreMinimal.h"
#include "liberafx/host/LbPreparedPlayback.h"
class ULibraSpawnableComponent;
class UWorld;

namespace LibraSharedEffects
{
liberafx::Result Prepare(UWorld *world, const uint8 *bytes, std::size_t size,
                         const liberafx::SpawnableHostPoolOptions &options);
liberafx::Result Acquire(UWorld *world, const uint8 *bytes, std::size_t size,
                         const liberafx::SpawnableHostPoolOptions &options, liberafx::host::PlaybackHost &out);
bool Queue(ULibraSpawnableComponent &component, liberafx::SpawnableHost &host, uint64 generation, double time,
           double delta);
} // namespace LibraSharedEffects
