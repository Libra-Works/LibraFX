#pragma once

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>

namespace liberafx::gpu {

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
    float colorR = 1;
    float colorG = 1;
    float colorB = 1;
    float colorA = 1;
};

enum class Fluid2DField : std::int32_t {
    None = -1, Velocity, VelocityScratch, Dye, DyeScratch, Divergence, Pressure, PressureScratch
};
enum class Fluid2DPass {
    Clear, AdvectVelocity, ApplyForces, Divergence, Jacobi, ProjectVelocity, AdvectDye, Publish, RenderDye
};
struct Fluid2DDispatch {
    Fluid2DPass pass = Fluid2DPass::Clear;
    Fluid2DField read0 = Fluid2DField::None;
    Fluid2DField read1 = Fluid2DField::None;
    Fluid2DField write0 = Fluid2DField::None;
    Fluid2DField write1 = Fluid2DField::None;
};
struct Fluid2DPlan {
    std::array<Fluid2DDispatch, 136> passes{};
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
};
static_assert(sizeof(Fluid2DUniforms) == 80, "Fluid uniforms require five float4 slots.");

inline auto validFluid2DSettings(const Fluid2DSettings& settings) noexcept -> bool
{
    const auto range = [](float value, float minimum, float maximum) {
        return std::isfinite(value) && value >= minimum && value <= maximum;
    };
    return settings.version == 1 && settings.width >= 8 && settings.width <= 1024 &&
        settings.height >= 8 && settings.height <= 1024 &&
        settings.pressureIterations >= 1 && settings.pressureIterations <= 128 &&
        settings.maximumSubsteps >= 1 && settings.maximumSubsteps <= 16 &&
        range(settings.maximumStep, 0.0001f, 0.05f) &&
        range(settings.domainWidth, 0.001f, 10000) && range(settings.domainHeight, 0.001f, 10000) &&
        range(settings.velocityDissipation, 0, 100) && range(settings.densityDissipation, 0, 100) &&
        range(settings.sourceX, 0, 1) && range(settings.sourceY, 0, 1) &&
        range(settings.sourceRadius, 0.0001f, 10000) && range(settings.sourceDensity, 0, 1000) &&
        range(settings.sourceVelocityX, -1000, 1000) && range(settings.sourceVelocityY, -1000, 1000) &&
        range(settings.buoyancy, -100, 100) && range(settings.colorR, 0, 1) &&
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
    add(Pass::AdvectVelocity, Field::Velocity, Field::None, Field::VelocityScratch);
    add(Pass::ApplyForces, Field::VelocityScratch, Field::Dye, Field::Velocity);
    add(Pass::Divergence, Field::Velocity, Field::None, Field::Divergence);
    add(Pass::Clear, Field::None, Field::None, Field::Pressure);
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
    out = plan;
    return true;
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
        const auto duration = std::min(delta, static_cast<double>(settings.maximumStep) * settings.maximumSubsteps);
        frame.substeps = static_cast<std::uint32_t>(std::clamp(std::ceil(duration / settings.maximumStep),
            1.0, static_cast<double>(settings.maximumSubsteps)));
        frame.stepDelta = static_cast<float>(duration / frame.substeps);
        frame.discardedDelta = delta - duration;
    }
    out = frame;
    return true;
}

inline auto makeFluid2DUniforms(const Fluid2DSettings& settings, float delta) noexcept -> Fluid2DUniforms
{
    return {
        {static_cast<float>(settings.width), static_cast<float>(settings.height), delta, 0},
        {settings.domainWidth, settings.domainHeight, settings.velocityDissipation, settings.densityDissipation},
        {settings.sourceX, settings.sourceY, settings.sourceRadius, settings.sourceDensity},
        {settings.sourceVelocityX, settings.sourceVelocityY, settings.buoyancy, settings.sourceEnabled ? 1.0f : 0.0f},
        {settings.colorR, settings.colorG, settings.colorB, settings.colorA}
    };
}

}
