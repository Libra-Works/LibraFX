#pragma once

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>

namespace liberafx::gpu {

struct Fluid2DForceNode {
    std::uint32_t kind = 0;
    float x = 0, y = 0, strength = 1, frequency = 1;
    std::uint32_t seed = 0, octaves = 3;
    std::uint32_t inputA = 0, inputB = 0;
    float roughness = 0.5f, animationSpeed = 0;
};

struct Fluid2DSettings {
    std::uint32_t version = 1;
    std::uint32_t width = 128;
    std::uint32_t height = 128;
    float domainWidth = 1;
    float domainHeight = 1;
    std::uint32_t pressureIterations = 40;
    float maximumStep = 1.0f / 60.0f;
    std::uint32_t maximumSubsteps = 4;
    float velocityDissipation = 0.1f;
    float densityDissipation = 0.2f;
    bool sourceEnabled = true;
    float sourceX = 0.5f;
    float sourceY = 0.12f;
    float sourceRadius = 0.08f;
    float sourceDensity = 10;
    float sourceVelocityX = 0;
    float sourceVelocityY = 2;
    float buoyancy = 1;
    float vorticity = 0;
    std::uint32_t model = 0; // Smoke, FreeSurface, ShallowWater, Combustion.
    float gravity = 9.81f;
    float initialWaterDepth = 0.25f;
    float maximumWaterSpeed = 4;
    float fuelBurnRate = 4;
    float ignitionTemperature = 0.5f;
    float heatYield = 2;
    float coolingRate = 1;
    float sourceTemperature = 1;
    std::uint32_t forceCount = 0;
    std::array<Fluid2DForceNode, 8> forces{};
    float colorR = 1;
    float colorG = 1;
    float colorB = 1;
    float colorA = 1;
};

enum class Fluid2DField : std::int32_t {
    None = -1, Velocity, VelocityScratch, Dye, DyeScratch, Divergence, Pressure, PressureScratch
};
enum class Fluid2DPass {
    Clear, AdvectVelocity, ApplyForces, Divergence, Jacobi, ProjectVelocity, AdvectDye, Publish, RenderDye,
    InitializeWater, ShallowWater, RenderFields, RenderColor
};
struct Fluid2DDispatch {
    Fluid2DPass pass = Fluid2DPass::Clear;
    Fluid2DField read0 = Fluid2DField::None;
    Fluid2DField read1 = Fluid2DField::None;
    Fluid2DField write0 = Fluid2DField::None;
    Fluid2DField write1 = Fluid2DField::None;
};
struct Fluid2DPlan {
    std::array<Fluid2DDispatch, 144> passes{};
    std::uint32_t count = 0;
};
struct Fluid2DFrame {
    std::uint32_t substeps = 0;
    float stepDelta = 0;
    double discardedDelta = 0;
};
struct alignas(16) Fluid2DUniforms {
    std::array<float, 4> grid{};
    std::array<float, 4> domain{};
    std::array<float, 4> source{};
    std::array<float, 4> force{};
    std::array<float, 4> color{};
    std::array<float, 4> model{};
    std::array<float, 4> reaction{};
    std::array<std::array<float, 4>, 25> forceNodes{};
};
static_assert(sizeof(Fluid2DUniforms) == 512, "Fluid model and force uniforms require 32 float4 slots.");

inline auto validFluid2DSettings(const Fluid2DSettings& settings) noexcept -> bool
{
    const auto range = [](float value, float minimum, float maximum) {
        return std::isfinite(value) && value >= minimum && value <= maximum;
    };
    if (settings.forceCount > settings.forces.size())
    {
        return false;
    }
    for (auto index = 0u; index < settings.forceCount; ++index)
    {
        const auto& node = settings.forces[index];
        if (node.kind > 10 || node.inputA > index || node.inputB > index || !range(node.x, -1000, 1000) || !range(node.y, -1000, 1000) ||
            !range(node.strength, 0, 100) || !range(node.frequency, .001f, 1000) || node.seed > 16777215 ||
            node.octaves < 1 || node.octaves > 8 || !range(node.roughness, 0, 1) || !range(node.animationSpeed, -100, 100))
        {
            return false;
        }
    }
    return settings.version == 1 && settings.width >= 8 && settings.width <= 1024 &&
        settings.height >= 8 && settings.height <= 1024 &&
        settings.pressureIterations >= 1 && settings.pressureIterations <= 128 &&
        settings.maximumSubsteps >= 1 && settings.maximumSubsteps <= 64 && settings.model <= 3 &&
        range(settings.gravity, 0.001f, 100) && range(settings.initialWaterDepth, 0.0001f, 1000) &&
        range(settings.maximumWaterSpeed, 0.001f, 1000) && range(settings.fuelBurnRate, 0, 100) &&
        range(settings.ignitionTemperature, 0.001f, 100) && range(settings.heatYield, 0, 100) &&
        range(settings.coolingRate, 0, 100) && range(settings.sourceTemperature, 0, 100) &&
        range(settings.maximumStep, 0.0001f, 0.05f) &&
        range(settings.domainWidth, 0.001f, 10000) && range(settings.domainHeight, 0.001f, 10000) &&
        range(settings.velocityDissipation, 0, 100) && range(settings.densityDissipation, 0, 100) &&
        range(settings.sourceX, 0, 1) && range(settings.sourceY, 0, 1) &&
        range(settings.sourceRadius, 0.0001f, 10000) && range(settings.sourceDensity, 0, 1000) &&
        range(settings.sourceVelocityX, -1000, 1000) && range(settings.sourceVelocityY, -1000, 1000) &&
        range(settings.buoyancy, -100, 100) && range(settings.vorticity, 0, 100) && range(settings.colorR, 0, 1) &&
        range(settings.colorG, 0, 1) && range(settings.colorB, 0, 1) && range(settings.colorA, 0, 1);
}

inline auto makeFluid2DPlan(const Fluid2DSettings& settings, Fluid2DPlan& out) noexcept -> bool
{
    if (!validFluid2DSettings(settings))
    {
        return false;
    }
    auto plan = Fluid2DPlan{};
    using Field = Fluid2DField;
    using Pass = Fluid2DPass;
    const auto add = [&](Pass pass, Field read0 = Field::None, Field read1 = Field::None,
                         Field write0 = Field::None, Field write1 = Field::None) {
        plan.passes[plan.count++] = {pass, read0, read1, write0, write1};
    };
    if (settings.model == 2)
    {
        add(Pass::ShallowWater, Field::Dye, Field::None, Field::DyeScratch);
        add(Pass::Publish, Field::DyeScratch, Field::DyeScratch, Field::Velocity, Field::Dye);
        add(Pass::RenderDye, Field::Dye);
        add(Pass::RenderFields, Field::Velocity, Field::Dye);
        add(Pass::RenderColor, Field::Dye);
        out = plan;
        return true;
    }
    add(Pass::AdvectVelocity, Field::Velocity, Field::None, Field::VelocityScratch);
    add(Pass::ApplyForces, Field::VelocityScratch, Field::Dye, Field::Velocity);
    add(Pass::Divergence, Field::Velocity, Field::Dye, Field::Divergence);
    if (settings.model != 1)
    {
        add(Pass::Clear, Field::None, Field::None, Field::Pressure);
    }
    auto pressure = Field::Pressure;
    auto scratch = Field::PressureScratch;
    for (auto index = std::uint32_t{0}; index < settings.pressureIterations; index++)
    {
        add(Pass::Jacobi, pressure, Field::Divergence, scratch);
        std::swap(pressure, scratch);
    }
    add(Pass::ProjectVelocity, Field::Velocity, pressure, Field::VelocityScratch);
    add(Pass::AdvectDye, Field::Dye, Field::VelocityScratch, Field::DyeScratch);
    add(Pass::Publish, Field::VelocityScratch, Field::DyeScratch, Field::Velocity, Field::Dye);
    add(Pass::RenderDye, Field::Dye);
    add(Pass::RenderFields, Field::Velocity, Field::Dye);
    add(Pass::RenderColor, Field::Dye);
    out = plan;
    return true;
}

inline auto maximumStableFluid2DStep(const Fluid2DSettings& settings) noexcept -> double
{
    auto step = static_cast<double>(settings.maximumStep);
    if (settings.model != 1 && settings.model != 2)
    {
        return step;
    }
    auto speed = static_cast<double>(settings.maximumWaterSpeed);
    if (settings.model == 2)
    {
        speed += std::sqrt(static_cast<double>(settings.gravity) * settings.initialWaterDepth * 16);
    }
    return std::min(step, 0.4 / (speed * (settings.width / static_cast<double>(settings.domainWidth) +
        settings.height / static_cast<double>(settings.domainHeight))));
}

inline auto makeFluid2DFrame(const Fluid2DSettings& settings, double delta, Fluid2DFrame& out) noexcept -> bool
{
    if (!validFluid2DSettings(settings) || !std::isfinite(delta) || delta < 0)
    {
        return false;
    }
    auto frame = Fluid2DFrame{};
    if (delta > 0)
    {
        const auto step = maximumStableFluid2DStep(settings);
        const auto duration = std::min(delta, step * settings.maximumSubsteps);
        frame.substeps = static_cast<std::uint32_t>(std::clamp(std::ceil(duration / step),
            1.0, static_cast<double>(settings.maximumSubsteps)));
        frame.stepDelta = static_cast<float>(duration / frame.substeps);
        frame.discardedDelta = delta - duration;
    }
    out = frame;
    return true;
}

inline auto makeFluid2DUniforms(const Fluid2DSettings& settings, float delta, double time = 0) noexcept -> Fluid2DUniforms
{
    auto uniforms = Fluid2DUniforms{
        {static_cast<float>(settings.width), static_cast<float>(settings.height), delta, settings.vorticity},
        {settings.domainWidth, settings.domainHeight, settings.velocityDissipation, settings.densityDissipation},
        {settings.sourceX, settings.sourceY, settings.sourceRadius, settings.sourceDensity},
        {settings.sourceVelocityX, settings.sourceVelocityY, settings.buoyancy, settings.sourceEnabled ? 1.0f : 0.0f},
        {settings.colorR, settings.colorG, settings.colorB, settings.colorA},
        {static_cast<float>(settings.model), settings.gravity, settings.initialWaterDepth,
            settings.model == 3 ? settings.sourceTemperature : settings.maximumWaterSpeed},
        {settings.fuelBurnRate, settings.ignitionTemperature, settings.heatYield, settings.coolingRate}
    };
    uniforms.forceNodes[0] = {static_cast<float>(settings.forceCount), static_cast<float>(std::fmod(time, 4096)), 0, 0};
    for (auto index = 0u; index < std::min<std::uint32_t>(settings.forceCount, 8); ++index)
    {
        const auto& node = settings.forces[index];
        uniforms.forceNodes[1 + index * 3] = {static_cast<float>(node.kind), node.x, node.y, node.strength};
        uniforms.forceNodes[2 + index * 3] = {node.frequency, static_cast<float>(node.seed), static_cast<float>(node.octaves), node.roughness};
        auto output = node.kind < 3 || node.kind >= 7;
        for (auto other = 0u; other < settings.forceCount; ++other)
        {
            if (settings.forces[other].inputA == index + 1 || settings.forces[other].inputB == index + 1)
            {
                output = false;
            }
        }
        uniforms.forceNodes[3 + index * 3] = {node.animationSpeed, static_cast<float>(node.inputA),
            static_cast<float>(node.inputB), output ? 1.0f : 0.0f};
    }
    return uniforms;
}

}
