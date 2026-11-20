namespace LibraGpu.Core;

public static class Fluid2DPresets
{
    public static string RendererMaterial(Fluid2DModel model) => model switch
    {
        Fluid2DModel.FreeSurface or Fluid2DModel.ShallowWater => "builtin:fluid-water",
        Fluid2DModel.Combustion => "builtin:fluid-fire",
        _ => "builtin:fluid-alpha"
    };

    public static Fluid2DSettings Create(Fluid2DModel model, uint width = 128, uint height = 128)
    {
        var settings = new Fluid2DSettings { Width = width, Height = height, Model = model };
        return model switch
        {
            Fluid2DModel.FreeSurface => settings with
            {
                InitialWaterDepth = .35f, Gravity = 9.81f, MaximumWaterSpeed = 4, PressureIterations = 128,
                MaximumSubsteps = 32, SourceEnabled = false, SourceY = .75f, SourceDensity = 2,
                SourceVelocityY = 0, Buoyancy = 0, VelocityDissipation = 0, DensityDissipation = 0,
                ColorR = .1f, ColorG = .4f, ColorB = 1
            },
            Fluid2DModel.ShallowWater => settings with
            {
                DomainWidth = 10, DomainHeight = 10, InitialWaterDepth = .25f, Gravity = 9.81f,
                MaximumWaterSpeed = 4, MaximumSubsteps = 32, SourceEnabled = true,
                SourceX = .5f, SourceY = .5f, SourceRadius = 1, SourceDensity = .2f,
                SourceVelocityY = 0, Buoyancy = 0, VelocityDissipation = .05f, DensityDissipation = 0,
                ColorR = .1f, ColorG = .4f, ColorB = 1
            },
            Fluid2DModel.Combustion => settings with
            {
                SourceTemperature = 2, FuelBurnRate = 4, IgnitionTemperature = .5f, HeatYield = 2,
                CoolingRate = .25f, SourceRadius = .12f, SourceVelocityY = .5f, Buoyancy = .25f,
                DensityDissipation = .1f, ColorR = 1, ColorG = .3f, ColorB = .05f
            },
            _ => settings
        };
    }
}
