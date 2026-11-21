using System;
using System.Runtime.InteropServices;

namespace LiberaFx.Unity
{
    [StructLayout(LayoutKind.Sequential)]
    public struct EffectTimeSnapshot
    {
        public double realTime, effectTime, realDelta, effectDelta, timeScale;
    }
    public enum SpatialValueKind : uint
    {
        Position, Vector, Direction, Normal, AngularVector, Transform, CanonicalDrawTransform
    }
    [Serializable, StructLayout(LayoutKind.Sequential)]
    public partial struct CoordinateBasis
    {
        public int axisX;
        public int axisY;
        public int axisZ;
        public double unitsPerMetre;
        public static CoordinateBasis Unity => new CoordinateBasis { axisX = 1, axisY = 2, axisZ = 3, unitsPerMetre = 1 };
        public static CoordinateBasis Unreal => new CoordinateBasis { axisX = 2, axisY = 3, axisZ = 1, unitsPerMetre = 100 };
        public static CoordinateBasis Godot => new CoordinateBasis { axisX = 1, axisY = 2, axisZ = -3, unitsPerMetre = 1 };

        public double[] Convert(SpatialValueKind kind, bool toHost, double[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var destination = new double[values.Length];
            var result = Native.libra_coordinate_convert(ref this, (uint)kind, toHost ? 1u : 0u, values, (uint)values.Length, destination);
            if (result != NativeResult.Ok) throw new ArgumentException("Invalid coordinate conversion: " + result);
            return destination;
        }
    }
}
