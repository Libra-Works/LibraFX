#include "ProceduralModelInternal.h"
#include <unordered_map>

namespace liberafx::procedural_model {

static auto periodicU(std::uint32_t shape) -> bool
{
    return shape == LIBRA_SHAPE_RING || shape == LIBRA_SHAPE_DISK || shape == LIBRA_SHAPE_TORUS ||
        shape == LIBRA_SHAPE_CYLINDER || shape == LIBRA_SHAPE_CONE || shape == LIBRA_SHAPE_FLARE ||
        shape == LIBRA_SHAPE_SPHERE || shape == LIBRA_SHAPE_HEMISPHERE || shape == LIBRA_SHAPE_CAPSULE ||
        shape == LIBRA_SHAPE_SPIKE || shape == LIBRA_SHAPE_FLAME_RING || shape == LIBRA_SHAPE_KNOT;
}

static auto periodicV(std::uint32_t shape) -> bool
{
    return shape == LIBRA_SHAPE_TORUS || shape == LIBRA_SHAPE_COIL ||
        shape == LIBRA_SHAPE_DOUBLE_HELIX || shape == LIBRA_SHAPE_KNOT;
}

static auto gridWeld(const LibraProceduralModelDescriptor& d, std::uint32_t patch,
    std::uint32_t x, std::uint32_t y, std::uint32_t v) -> std::uint64_t
{
    if (d.shape == LIBRA_SHAPE_BOX)
    {
        std::uint64_t px = 0, py = 0, pz = 0;
        switch (patch)
        {
        case 0: px = d.segmentsU; py = y; pz = x; break;
        case 1: px = 0; py = y; pz = x; break;
        case 2: px = x; py = d.segmentsU; pz = y; break;
        case 3: px = x; py = 0; pz = y; break;
        case 4: px = x; py = y; pz = d.segmentsU; break;
        default: px = x; py = y; pz = 0; break;
        }
        const auto stride = static_cast<std::uint64_t>(d.segmentsU + 1);
        return px + stride * (py + stride * pz);
    }
    if (periodicU(d.shape) && x == d.segmentsU)
    {
        x = 0;
    }
    if (periodicV(d.shape) && y == v)
    {
        y = 0;
    }
    if ((d.shape == LIBRA_SHAPE_SPHERE || d.shape == LIBRA_SHAPE_CAPSULE) && (y == 0 || y == v))
    {
        x = 0;
    }
    if ((d.shape == LIBRA_SHAPE_CONE || d.shape == LIBRA_SHAPE_SPIKE || d.shape == LIBRA_SHAPE_HEMISPHERE) && y == v)
    {
        x = 0;
    }
    if (d.shape == LIBRA_SHAPE_DISK && y == 0)
    {
        x = 0;
    }
    if ((d.shape == LIBRA_SHAPE_LEAF || d.shape == LIBRA_SHAPE_SLASH || d.shape == LIBRA_SHAPE_SWIRL) &&
        (x == 0 || x == d.segmentsU))
    {
        y = 0;
    }
    return (static_cast<std::uint64_t>(patch + 1) << 40) |
        (static_cast<std::uint64_t>(y) * (d.segmentsU + 1) + x);
}

static auto reverseGrid(std::uint32_t shape, std::uint32_t patch) -> bool
{
    if (shape == LIBRA_SHAPE_BOX)
    {
        return patch == 0 || patch == 2 || patch == 5;
    }
    if (shape == LIBRA_SHAPE_CROSS)
    {
        return patch == 0;
    }
    return shape == LIBRA_SHAPE_RIBBON || shape == LIBRA_SHAPE_SLASH || shape == LIBRA_SHAPE_WAVE ||
        shape == LIBRA_SHAPE_LEAF || shape == LIBRA_SHAPE_CYLINDER || shape == LIBRA_SHAPE_CONE ||
        shape == LIBRA_SHAPE_FLARE || shape == LIBRA_SHAPE_SPHERE || shape == LIBRA_SHAPE_HEMISPHERE ||
        shape == LIBRA_SHAPE_CAPSULE || shape == LIBRA_SHAPE_SPIKE || shape == LIBRA_SHAPE_TORUS;
}

auto buildBase(const LibraProceduralModelDescriptor& d, Plan& plan) -> LibraResult
{
    auto topology = std::make_shared<Topology>();
    auto welds = std::unordered_map<std::uint64_t, std::uint32_t>{};
    auto normalGroups = std::unordered_map<std::uint64_t, std::uint32_t>{};
    const auto u = d.segmentsU;
    const auto v = d.shape == LIBRA_SHAPE_BOX ? u : d.segmentsV;
    const auto patches = d.shape == LIBRA_SHAPE_BOX ? 6u :
        d.shape == LIBRA_SHAPE_CROSS || d.shape == LIBRA_SHAPE_DOUBLE_HELIX ? 2u : 1u;
    auto predicted = LibraProceduralModelCounts{};
    const auto prediction = predict(d, nullptr, 0, predicted);
    if (prediction != LIBRA_OK)
    {
        return prediction;
    }
    topology->indices.reserve(predicted.indices);
    topology->triangleParity.reserve(predicted.indices / 3);
    topology->welds.reserve(predicted.vertices);
    topology->normalGroups.reserve(predicted.vertices);
    plan.samples.reserve(predicted.vertices);
    welds.reserve(predicted.vertices);
    normalGroups.reserve(predicted.vertices);
    const auto vertex = [&](SourceSample sample, std::uint64_t rawWeld, std::uint32_t smoothing)
    {
        const auto weld = welds.emplace(rawWeld, static_cast<std::uint32_t>(welds.size())).first->second;
        const auto normalKey = (static_cast<std::uint64_t>(weld) << 32) | smoothing;
        const auto group = normalGroups.emplace(normalKey, static_cast<std::uint32_t>(normalGroups.size())).first->second;
        topology->welds.push_back(weld);
        topology->normalGroups.push_back(group);
        plan.samples.push_back(sample);
        return topology->vertexCount++;
    };
    const auto triangle = [&](std::uint32_t a, std::uint32_t b, std::uint32_t c, bool reverse)
    {
        topology->indices.push_back(a);
        topology->indices.push_back(reverse ? c : b);
        topology->indices.push_back(reverse ? b : c);
        topology->triangleParity.push_back(0);
    };
    for (auto patch = 0u; patch < patches; ++patch)
    {
        const auto offset = topology->vertexCount;
        for (auto y = 0u; y <= v; ++y)
        {
            for (auto x = 0u; x <= u; ++x)
            {
                vertex({static_cast<float>(x) / u, static_cast<float>(y) / v, patch},
                    gridWeld(d, patch, x, y, v), d.shape == LIBRA_SHAPE_BOX ? patch + 1 : 0);
            }
        }
        for (auto y = 0u; y < v; ++y)
        {
            for (auto x = 0u; x < u; ++x)
            {
                const auto a = offset + y * (u + 1) + x;
                const auto b = a + 1;
                const auto c = a + u + 1;
                const auto e = c + 1;
                triangle(a, b, e, reverseGrid(d.shape, patch));
                triangle(a, e, c, reverseGrid(d.shape, patch));
            }
        }
    }
    const auto capped = (d.flags & LIBRA_PROCEDURAL_CAPPED) != 0;
    const auto roundCaps = capped && (d.shape == LIBRA_SHAPE_CYLINDER || d.shape == LIBRA_SHAPE_CONE ||
        d.shape == LIBRA_SHAPE_FLARE || d.shape == LIBRA_SHAPE_SPIKE);
    const auto tubeCaps = capped && (d.shape == LIBRA_SHAPE_COIL || d.shape == LIBRA_SHAPE_DOUBLE_HELIX);
    const auto hemisphereCap = capped && d.shape == LIBRA_SHAPE_HEMISPHERE;
    if (roundCaps || tubeCaps || hemisphereCap)
    {
        const auto segments = tubeCaps ? v : u;
        for (auto patch = 0u; patch < patches; ++patch)
        {
            for (auto end = 0u; end < (hemisphereCap ? 1u : 2u); ++end)
            {
                const auto samplePatch = 0x10000000u | (end == 0 ? 0 : 0x20000000u) | patch;
                const auto smoothing = 128 + patch * 2 + end;
                const auto pole = end != 0 && (d.shape == LIBRA_SHAPE_CONE || d.shape == LIBRA_SHAPE_SPIKE);
                const auto centerWeld = pole ? gridWeld(d, patch, 0, v, v) :
                    (1ull << 60) | (static_cast<std::uint64_t>(patch) << 32) | end;
                const auto center = vertex({0, 0, samplePatch}, centerWeld, smoothing);
                const auto ring = topology->vertexCount;
                for (auto x = 0u; x <= segments; ++x)
                {
                    const auto raw = tubeCaps ? gridWeld(d, patch, end == 0 ? 0 : u, x, v) :
                        gridWeld(d, patch, x, end == 0 ? 0 : v, v);
                    vertex({static_cast<float>(x) / segments, 1, samplePatch}, raw, smoothing);
                }
                for (auto x = 0u; x < segments; ++x)
                {
                    // Increasing ring coordinates face the negative path axis;
                    // reverse only the far cap, for both round and tube ends.
                    triangle(center, ring + x, ring + x + 1, end != 0);
                }
            }
        }
    }
    topology->weldCount = static_cast<std::uint32_t>(welds.size());
    topology->normalGroupCount = static_cast<std::uint32_t>(normalGroups.size());
    plan.maximumVertices = topology->vertexCount;
    plan.maximumIndices = static_cast<std::uint32_t>(topology->indices.size());
    plan.retainedBytes = topology->indices.size() * sizeof(std::uint32_t) +
        topology->triangleParity.size() * sizeof(std::uint32_t) +
        topology->vertexCount * 2 * sizeof(std::uint32_t) + plan.samples.size() * sizeof(SourceSample);
    plan.base = std::move(topology);
    return LIBRA_OK;
}

}
