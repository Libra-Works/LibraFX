using System;

namespace LiberaFx.Unity
{
    /// <summary>Statistics for the most recent simulation call.</summary>
    public struct LibraFluid2DFrameStats
    {
        public int Substeps { get; internal set; }
        public double DiscardedDelta { get; internal set; }
    }

    [Serializable]
    internal sealed class LibraFluid2DSettings
    {
        public int version, width, height, pressureIterations, maximumSubsteps;
        public float domainWidth, domainHeight, maximumStep, velocityDissipation, densityDissipation;
        public bool sourceEnabled;
        public float sourceX, sourceY, sourceRadius, sourceDensity, sourceVelocityX, sourceVelocityY, buoyancy;
        public float colorR, colorG, colorB, colorA;

        internal void Validate()
        {
            if (version != 1 || width < 8 || width > 1024 || height < 8 || height > 1024 ||
                pressureIterations < 1 || pressureIterations > 128 || maximumSubsteps < 1 || maximumSubsteps > 16 ||
                !Range(maximumStep, .0001f, .05f) || !Range(domainWidth, .001f, 10000) ||
                !Range(domainHeight, .001f, 10000) || !Range(velocityDissipation, 0, 100) ||
                !Range(densityDissipation, 0, 100) || !Range(sourceX, 0, 1) || !Range(sourceY, 0, 1) ||
                !Range(sourceRadius, .0001f, 10000) || !Range(sourceDensity, 0, 1000) ||
                !Range(sourceVelocityX, -1000, 1000) || !Range(sourceVelocityY, -1000, 1000) ||
                !Range(buoyancy, -100, 100) || !Range(colorR, 0, 1) || !Range(colorG, 0, 1) ||
                !Range(colorB, 0, 1) || !Range(colorA, 0, 1))
                throw new InvalidOperationException("Invalid or unsupported Fluid2D settings.");
        }

        private static bool Range(float value, float min, float max) =>
            !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max;
    }
}
