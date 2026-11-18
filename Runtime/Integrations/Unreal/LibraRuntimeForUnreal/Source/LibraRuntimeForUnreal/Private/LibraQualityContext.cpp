#include "LibraQualityContext.h"
#include "Scalability.h"

liberafx::Result ReadLibraQualitySnapshot(uint64 frameSerial, liberafx::HostQualitySnapshot& snapshot)
{
    snapshot = {};
    if (!IsInGameThread()) return liberafx::Result::Busy;
    snapshot.source = liberafx::HostQualitySource::EngineEffects;
    snapshot.frameSerial = frameSerial;
    auto quality = Scalability::GetQualityLevels().EffectsQuality;
    if (quality < 0 || quality > 4)
    {
        snapshot.availability = liberafx::HostQualityAvailability::Unknown;
        return liberafx::Result::InvalidValueType;
    }
    snapshot.availability = liberafx::HostQualityAvailability::Known;
    snapshot.tier = static_cast<liberafx::HostQualityTier>(quality);
    return liberafx::Result::Ok;
}
