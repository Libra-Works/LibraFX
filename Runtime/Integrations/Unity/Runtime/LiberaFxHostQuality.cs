#if UNITY_5_3_OR_NEWER
using UnityEngine;
#endif

namespace LiberaFx.Unity
{
    public enum HostQualityAvailability : byte { Unprovided, Known, Unknown, Unsupported }
    public enum HostQualityTier : byte { Low, Medium, High, Ultra, Cinematic }
    public enum HostQualitySource : byte { HostPolicy, EngineEffects, PlatformPolicy }

    public readonly struct HostQualitySnapshot
    {
        public readonly HostQualityAvailability Availability;
        public readonly HostQualityTier Tier;
        public readonly HostQualitySource Source;
        public readonly ulong FrameSerial;

        public HostQualitySnapshot(HostQualityAvailability availability, HostQualityTier tier,
            HostQualitySource source, ulong frameSerial)
        {
            Availability = availability;
            Tier = tier;
            Source = source;
            FrameSerial = frameSerial;
        }
    }

#if UNITY_5_3_OR_NEWER
    public static class UnityHostQuality
    {
        // Unity quality names/counts are project-defined, not a portable tier ordering.
        public static HostQualitySnapshot Read(HostQualityTier[] explicitMapping, ulong frameSerial)
        {
            var level = QualitySettings.GetQualityLevel();
            if (explicitMapping == null || level < 0 || level >= explicitMapping.Length)
                return new HostQualitySnapshot(HostQualityAvailability.Unknown, default,
                    HostQualitySource.EngineEffects, frameSerial);
            var tier = explicitMapping[level];
            return new HostQualitySnapshot(tier <= HostQualityTier.Cinematic
                ? HostQualityAvailability.Known : HostQualityAvailability.Unsupported,
                tier, HostQualitySource.EngineEffects, frameSerial);
        }
    }
#endif
}
