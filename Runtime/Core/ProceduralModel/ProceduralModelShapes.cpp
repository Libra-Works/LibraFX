#include "ProceduralModelInternal.h"

namespace liberafx::procedural_model {

static auto pathCenter(const LibraProceduralModelDescriptor& d, float u, std::uint32_t patch) -> Vec3
{
    const auto angle = 2 * pi * d.turns * u + (patch == 1 ? pi : 0);
    if (d.shape == LIBRA_SHAPE_KNOT)
    {
        const auto t = 2 * pi * u;
        const auto radius = (2 + std::cos(3 * t)) / 6;
        return {radius * std::cos(2 * t), std::sin(3 * t) / 3, radius * std::sin(2 * t)};
    }
    return {.35f * std::cos(angle), u - .5f, .35f * std::sin(angle)};
}

static auto tubePosition(const LibraProceduralModelDescriptor& d, float u, float v, std::uint32_t patch) -> Vec3
{
    const auto center = pathCenter(d, u, patch);
    const auto tangent = normalized(pathCenter(d, u + .0001f, patch) - pathCenter(d, u - .0001f, patch));
    const auto angleAlongPath = d.shape == LIBRA_SHAPE_KNOT ? 4 * pi * u :
        2 * pi * d.turns * u + (patch == 1 ? pi : 0);
    const auto radial = Vec3{std::cos(angleAlongPath), 0, std::sin(angleAlongPath)};
    const auto side = normalized(radial - tangent * dot(radial, tangent), {1, 0, 0});
    const auto up = normalized(cross(side, tangent));
    const auto angle = 2 * pi * v;
    return center + (side * std::cos(angle) + up * std::sin(angle)) * d.tubeRadius;
}

static auto unscaledPosition(const LibraProceduralModelDescriptor& d, float u, float v, std::uint32_t patch) -> Vec3
{
    const auto t = 2 * pi * u;
    const auto radial = [&](float radius, float height) -> Vec3
    {
        return {radius * std::cos(t), height, radius * std::sin(t)};
    };
    switch (d.shape)
    {
    case LIBRA_SHAPE_RIBBON:
        return {v - .5f, u - .5f, 0};
    case LIBRA_SHAPE_SLASH:
    {
        const auto profile = u <= 0 || u >= 1 ? 0.0f : std::pow(std::sin(pi * u), .65f);
        return {(v - .5f) * profile + .25f * std::sin(pi * u), u - .5f, 0};
    }
    case LIBRA_SHAPE_SPIRAL:
    {
        const auto angle = t * d.turns;
        const auto radius = .35f + (v - .5f) * .25f;
        return {radius * std::cos(angle), u - .5f, radius * std::sin(angle)};
    }
    case LIBRA_SHAPE_WAVE:
        return {v - .5f, u - .5f, .1f * std::sin(t)};
    case LIBRA_SHAPE_RING:
    case LIBRA_SHAPE_ARC:
    case LIBRA_SHAPE_DISK:
    case LIBRA_SHAPE_FLAME_RING:
    {
        const auto inner = d.shape == LIBRA_SHAPE_DISK ? 0 : d.innerRadius;
        const auto radius = .5f * (inner + (1 - inner) * v);
        const auto angle = d.shape == LIBRA_SHAPE_ARC ? d.sweepDegrees * degrees * u : t;
        const auto flame = d.shape == LIBRA_SHAPE_FLAME_RING ? .12f * v * std::pow(.5f + .5f * std::sin(9 * t), 3.0f) : 0;
        return {(radius + flame) * std::cos(angle), 0, (radius + flame) * std::sin(angle)};
    }
    case LIBRA_SHAPE_TORUS:
    {
        const auto tube = std::min(d.tubeRadius, .49f);
        const auto radius = .5f - tube + tube * std::cos(2 * pi * v);
        return radial(radius, tube * std::sin(2 * pi * v));
    }
    case LIBRA_SHAPE_CYLINDER:
        return radial(.5f, v - .5f);
    case LIBRA_SHAPE_CONE:
        return radial(.5f * (1 - v), v - .5f);
    case LIBRA_SHAPE_FLARE:
        return radial(.15f + .35f * v * v, v - .5f);
    case LIBRA_SHAPE_SPHERE:
    {
        const auto latitude = pi * v;
        return radial(.5f * std::sin(latitude), -.5f * std::cos(latitude));
    }
    case LIBRA_SHAPE_HEMISPHERE:
    {
        const auto latitude = .5f * pi * v;
        return radial(.5f * std::cos(latitude), std::sin(latitude));
    }
    case LIBRA_SHAPE_CAPSULE:
    {
        const auto height = std::max(d.size[1], .000001f);
        const auto radiusY = std::min(.5f, .5f * std::min(d.size[0], d.size[2]) / height);
        const auto halfBody = .5f - radiusY;
        if (v < .25f)
        {
            const auto angle = (v * 4 - 1) * .5f * pi;
            return radial(.5f * std::cos(angle), -halfBody + radiusY * std::sin(angle));
        }
        if (v > .75f)
        {
            const auto angle = (v - .75f) * 2 * pi;
            return radial(.5f * std::cos(angle), halfBody + radiusY * std::sin(angle));
        }
        return radial(.5f, halfBody * (4 * v - 2));
    }
    case LIBRA_SHAPE_PLANE:
        return {u - .5f, v - .5f, 0};
    case LIBRA_SHAPE_BOX:
        switch (patch)
        {
        case 0: return {.5f, v - .5f, u - .5f};
        case 1: return {-.5f, v - .5f, u - .5f};
        case 2: return {u - .5f, .5f, v - .5f};
        case 3: return {u - .5f, -.5f, v - .5f};
        case 4: return {u - .5f, v - .5f, .5f};
        default: return {u - .5f, v - .5f, -.5f};
        }
    case LIBRA_SHAPE_CROSS:
        return patch == 0 ? Vec3{v - .5f, u - .5f, 0} : Vec3{0, u - .5f, v - .5f};
    case LIBRA_SHAPE_SPIKE:
    {
        auto p = radial(.5f * (1 - v), v - .5f);
        const auto divisor = std::max(std::abs(std::cos(t)), std::abs(std::sin(t)));
        p.x /= divisor;
        p.z /= divisor;
        return p;
    }
    case LIBRA_SHAPE_LEAF:
    {
        const auto profile = u <= 0 || u >= 1 ? 0.0f : std::sin(pi * u);
        return {(v - .5f) * profile, u - .5f, .08f * profile * (1 - 4 * (v - .5f) * (v - .5f))};
    }
    case LIBRA_SHAPE_SWIRL:
    {
        const auto width = u <= 0 || u >= 1 ? 0.0f : .15f * std::sin(pi * u);
        const auto radius = .5f * u + (v - .5f) * width;
        return {radius * std::cos(t * d.turns), radius * std::sin(t * d.turns), 0};
    }
    case LIBRA_SHAPE_DOUBLE_HELIX:
    case LIBRA_SHAPE_COIL:
    case LIBRA_SHAPE_KNOT:
        return tubePosition(d, u, v, patch);
    default:
        return {};
    }
}

auto basePosition(const LibraProceduralModelDescriptor& d, const SourceSample& sample) -> Vec3
{
    constexpr auto capFlag = 0x10000000u;
    constexpr auto endFlag = 0x20000000u;
    auto p = Vec3{};
    if ((sample.patch & capFlag) != 0)
    {
        const auto end = (sample.patch & endFlag) != 0;
        const auto patch = sample.patch & 0xffffu;
        if (d.shape == LIBRA_SHAPE_COIL || d.shape == LIBRA_SHAPE_DOUBLE_HELIX)
        {
            const auto center = pathCenter(d, end ? 1.0f : 0.0f, patch);
            p = center + (tubePosition(d, end ? 1.0f : 0.0f, sample.u, patch) - center) * sample.v;
        }
        else
        {
            const auto rim = unscaledPosition(d, sample.u, end ? 1.0f : 0.0f, patch);
            p = {rim.x * sample.v, rim.y, rim.z * sample.v};
        }
    }
    else
    {
        p = unscaledPosition(d, sample.u, sample.v, sample.patch);
    }
    return {p.x * d.size[0], p.y * d.size[1], p.z * d.size[2]};
}

auto resolvePivot(const LibraProceduralModelDescriptor& d,
    const std::vector<LibraProceduralModelVertex>& vertices, const Bounds& bounds, Vec3& pivot) -> LibraResult
{
    pivot = bounds.center();
    switch (d.pivotMode)
    {
    case LIBRA_PIVOT_CENTER: break;
    case LIBRA_PIVOT_BASE: pivot.y = bounds.minimum.y; break;
    case LIBRA_PIVOT_TIP: pivot.y = bounds.maximum.y; break;
    case LIBRA_PIVOT_ORIGIN: pivot = {}; break;
    case LIBRA_PIVOT_CUSTOM: pivot = {d.pivot[0], d.pivot[1], d.pivot[2]}; break;
    case LIBRA_PIVOT_START:
    case LIBRA_PIVOT_END:
        pivot = basePosition(d, {d.pivotMode == LIBRA_PIVOT_END ? 1.0f : 0.0f, .5f, 0});
        break;
    case LIBRA_PIVOT_VERTEX:
        if (d.pivotVertex >= vertices.size())
        {
            return LIBRA_INVALID_ARGUMENT;
        }
        pivot = position(vertices[d.pivotVertex]);
        break;
    default: return LIBRA_INVALID_ARGUMENT;
    }
    return finite(pivot) ? LIBRA_OK : LIBRA_NON_FINITE;
}

}
