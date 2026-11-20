using System;

namespace LiberaFx.Unity
{
    [Serializable]
    internal sealed class LibraFluid2DForceNode
    {
        public string id, kind, inputA, inputB;
        public float x, y, strength, frequency, roughness, animationSpeed;
        public int seed, octaves;
        internal static readonly string[] Kinds = { "Acceleration", "Noise", "CurlNoise", "CellPosition", "Time", "Density", "Temperature", "Add", "Multiply", "Sine", "Smoothstep" };
        internal int KindId => Array.IndexOf(Kinds, kind);
    }
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
        public float vorticity;
        public string model;
        public float gravity, initialWaterDepth, maximumWaterSpeed, fuelBurnRate;
        public float ignitionTemperature, heatYield, coolingRate, sourceTemperature;
        public LibraFluid2DForceNode[] forces;
        internal int ModelId => model == "FreeSurface" ? 1 : model == "ShallowWater" ? 2 : model == "Combustion" ? 3 : 0;
        internal static string MaterialFor(int modelId) => modelId == 1 || modelId == 2 ? "builtin:fluid-water" : modelId == 3 ? "builtin:fluid-fire" : "builtin:fluid-alpha";

        internal void ApplyModel(LibraFluid2DModelParameters parameters)
        {
            model = parameters.model; gravity = parameters.gravity;
            initialWaterDepth = parameters.initialWaterDepth; maximumWaterSpeed = parameters.maximumWaterSpeed;
            fuelBurnRate = parameters.fuelBurnRate; ignitionTemperature = parameters.ignitionTemperature;
            heatYield = parameters.heatYield; coolingRate = parameters.coolingRate; sourceTemperature = parameters.sourceTemperature;
            forces = parameters.forces;
        }

        internal double MaximumStableStep()
        {
            if (ModelId != 1 && ModelId != 2) return maximumStep;
            var speed = (double)maximumWaterSpeed;
            if (ModelId == 2) speed += Math.Sqrt((double)gravity * initialWaterDepth * 16);
            return Math.Min(maximumStep, .4 / (speed * (width / (double)domainWidth + height / (double)domainHeight)));
        }

        internal void Validate()
        {
            if (model == null) ApplyModel(new LibraFluid2DModelParameters());
            var ids = new System.Collections.Generic.HashSet<string>();
            if ((forces?.Length ?? 0) > 8) throw new InvalidOperationException("At most eight cell-force nodes are supported.");
            foreach (var node in forces ?? Array.Empty<LibraFluid2DForceNode>())
            {
                if (node == null || !Guid.TryParse(node.id, out var id) || id == Guid.Empty || !ids.Add(id.ToString("D")) ||
                    node.KindId < 0 ||
                    node.inputA != null && !ids.Contains(node.inputA) || node.inputB != null && !ids.Contains(node.inputB) ||
                    node.inputA == node.id || node.inputB == node.id ||
                    !Range(node.x, -1000, 1000) || !Range(node.y, -1000, 1000) || !Range(node.strength, 0, 100) ||
                    !Range(node.frequency, .001f, 1000) || node.seed < 0 || node.seed > 16777215 || node.octaves < 1 || node.octaves > 8 ||
                    !Range(node.roughness, 0, 1) || !Range(node.animationSpeed, -100, 100))
                    throw new InvalidOperationException("Invalid cell-force node.");
            }
            if (version != 1 || width < 8 || width > 1024 || height < 8 || height > 1024 ||
                pressureIterations < 1 || pressureIterations > 128 || maximumSubsteps < 1 || maximumSubsteps > 64 ||
                model != "Smoke" && model != "FreeSurface" && model != "ShallowWater" && model != "Combustion" ||
                !Range(gravity, .001f, 100) || !Range(initialWaterDepth, .0001f, 1000) ||
                !Range(maximumWaterSpeed, .001f, 1000) || !Range(fuelBurnRate, 0, 100) ||
                !Range(ignitionTemperature, .001f, 100) || !Range(heatYield, 0, 100) ||
                !Range(coolingRate, 0, 100) || !Range(sourceTemperature, 0, 100) ||
                !Range(maximumStep, .0001f, .05f) || !Range(domainWidth, .001f, 10000) ||
                !Range(domainHeight, .001f, 10000) || !Range(velocityDissipation, 0, 100) ||
                !Range(densityDissipation, 0, 100) || !Range(sourceX, 0, 1) || !Range(sourceY, 0, 1) ||
                !Range(sourceRadius, .0001f, 10000) || !Range(sourceDensity, 0, 1000) ||
                !Range(sourceVelocityX, -1000, 1000) || !Range(sourceVelocityY, -1000, 1000) ||
                !Range(buoyancy, -100, 100) || !Range(vorticity, 0, 100) || !Range(colorR, 0, 1) || !Range(colorG, 0, 1) ||
                !Range(colorB, 0, 1) || !Range(colorA, 0, 1))
                throw new InvalidOperationException("Invalid or unsupported Fluid2D settings.");
        }

        private static bool Range(float value, float min, float max) =>
            !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max;
    }
}
