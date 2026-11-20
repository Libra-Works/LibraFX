#include "ProceduralModelInternal.h"

namespace liberafx::procedural_model {

auto validate(const LibraProceduralModelDescriptor& d,
    const LibraProceduralModelModifier* modifiers, std::uint32_t count) -> LibraResult
{
    if (d.version != LIBRA_PROCEDURAL_VERSION || d.shape >= LIBRA_SHAPE_COUNT ||
        d.segmentsU < 1 || d.segmentsU > 256 || d.segmentsV < 1 || d.segmentsV > 128 ||
        d.pivotMode > LIBRA_PIVOT_VERTEX || (d.flags & ~15u) != 0 || d.reserved != 0 ||
        d.maxVertices > LIBRA_PROCEDURAL_MAX_VERTICES || d.maxTriangles > LIBRA_PROCEDURAL_MAX_TRIANGLES ||
        count > LIBRA_PROCEDURAL_MAX_MODIFIERS || (count != 0 && modifiers == nullptr))
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    for (auto axis = 0u; axis < 3; ++axis)
    {
        if (!std::isfinite(d.size[axis]) || d.size[axis] < 0 || d.size[axis] > 10000 ||
            !std::isfinite(d.pivot[axis]) || std::abs(d.pivot[axis]) > maximumCoordinate)
        {
            return LIBRA_INVALID_ARGUMENT;
        }
    }
    if (!std::isfinite(d.innerRadius) || d.innerRadius < 0 || d.innerRadius > 1 ||
        !std::isfinite(d.sweepDegrees) || d.sweepDegrees <= 0 || d.sweepDegrees > 360 ||
        !std::isfinite(d.turns) || d.turns <= 0 || d.turns > 64 ||
        !std::isfinite(d.tubeRadius) || d.tubeRadius < 0 || d.tubeRadius > .5f ||
        ((d.flags & LIBRA_PROCEDURAL_ANIMATED_BASE) != 0 && (d.flags & LIBRA_PROCEDURAL_ANIMATED) == 0))
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    for (auto i = 0u; i < count; ++i)
    {
        const auto& m = modifiers[i];
        if (m.kind >= LIBRA_MOD_COUNT || m.axis > 2 || (m.flags & ~31u) != 0 ||
            m.reserved0 != 0 || m.reserved1 != 0 || m.reserved2 != 0 || m.values[15] != 0 ||
            ((m.flags & LIBRA_MOD_ANIMATED) != 0 && (d.flags & LIBRA_PROCEDURAL_ANIMATED) == 0))
        {
            return LIBRA_INVALID_ARGUMENT;
        }
        for (const auto value : m.values)
        {
            if (!std::isfinite(value) || std::abs(value) > maximumCoordinate)
            {
                return LIBRA_INVALID_ARGUMENT;
            }
        }
        const auto maximumCount = m.kind == LIBRA_MOD_ARRAY ? LIBRA_PROCEDURAL_MAX_COPIES :
            m.kind == LIBRA_MOD_SUBDIVIDE ? LIBRA_PROCEDURAL_MAX_SUBDIVISION : m.kind == LIBRA_MOD_NOISE ? 8 : 1;
        if (m.count == 0 || m.count > static_cast<std::uint32_t>(maximumCount) ||
            (m.kind == LIBRA_MOD_ROUND && (m.values[0] < 0 || m.values[0] > 1 || m.values[1] < 0)) ||
            (m.kind == LIBRA_MOD_SOLIDIFY && (m.values[0] < 0 || m.values[1] < -1 || m.values[1] > 1)) ||
            (m.kind == LIBRA_MOD_NOISE && (m.values[1] < 0 || m.values[4] < 0 || m.values[4] > 1)))
        {
            return LIBRA_INVALID_ARGUMENT;
        }
        if ((m.flags & LIBRA_MOD_ENABLED) != 0 &&
            (m.kind == LIBRA_MOD_WAVE || m.kind == LIBRA_MOD_NOISE) && m.values[3] != 0 &&
            (d.flags & LIBRA_PROCEDURAL_ANIMATED) == 0)
        {
            return LIBRA_INVALID_ARGUMENT;
        }
    }
    return LIBRA_OK;
}

auto predict(const LibraProceduralModelDescriptor& d,
    const LibraProceduralModelModifier* modifiers, std::uint32_t count,
    LibraProceduralModelCounts& counts) -> LibraResult
{
    const auto valid = validate(d, modifiers, count);
    if (valid != LIBRA_OK)
    {
        return valid;
    }
    const auto u = static_cast<std::uint64_t>(d.segmentsU);
    const auto v = static_cast<std::uint64_t>(d.shape == LIBRA_SHAPE_BOX ? d.segmentsU : d.segmentsV);
    const auto patches = d.shape == LIBRA_SHAPE_BOX ? 6u :
        d.shape == LIBRA_SHAPE_CROSS || d.shape == LIBRA_SHAPE_DOUBLE_HELIX ? 2u : 1u;
    auto vertices = (u + 1) * (v + 1) * patches;
    auto triangles = 2 * u * v * patches;
    auto edges = (3 * u * v + u + v) * patches;
    auto boundary = 2 * (u + v) * patches;
    const auto capped = (d.flags & LIBRA_PROCEDURAL_CAPPED) != 0;
    const auto roundCaps = capped && (d.shape == LIBRA_SHAPE_CYLINDER || d.shape == LIBRA_SHAPE_CONE ||
        d.shape == LIBRA_SHAPE_FLARE || d.shape == LIBRA_SHAPE_SPIKE);
    const auto tubeCaps = capped && (d.shape == LIBRA_SHAPE_COIL || d.shape == LIBRA_SHAPE_DOUBLE_HELIX);
    const auto caps = roundCaps || tubeCaps ? 2u * patches :
        capped && d.shape == LIBRA_SHAPE_HEMISPHERE ? 1u : 0u;
    const auto capSegments = tubeCaps ? v : u;
    vertices += caps * (capSegments + 2);
    triangles += caps * capSegments;
    edges += caps * (2 * capSegments + 1);
    boundary += caps * (capSegments + 2);
    if (d.pivotMode == LIBRA_PIVOT_VERTEX && d.pivotVertex >= vertices)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    const auto animated = (d.flags & LIBRA_PROCEDURAL_ANIMATED) != 0;
    const auto maxVertices = std::min<std::uint64_t>(d.maxVertices == 0 ? LIBRA_PROCEDURAL_MAX_VERTICES : d.maxVertices,
        animated ? LIBRA_PROCEDURAL_MAX_ANIMATED_VERTICES : LIBRA_PROCEDURAL_MAX_VERTICES);
    const auto maxTriangles = std::min<std::uint64_t>(d.maxTriangles == 0 ? LIBRA_PROCEDURAL_MAX_TRIANGLES : d.maxTriangles,
        animated ? LIBRA_PROCEDURAL_MAX_ANIMATED_TRIANGLES : LIBRA_PROCEDURAL_MAX_TRIANGLES);
    auto retained = std::uint64_t{};
    auto maximumVertices = vertices;
    auto maximumTriangles = triangles;
    const auto account = [&]() -> bool
    {
        if (vertices > maxVertices || triangles > maxTriangles)
        {
            return false;
        }
        maximumVertices = std::max(maximumVertices, vertices);
        maximumTriangles = std::max(maximumTriangles, triangles);
        // Index connectivity/parity, weld/smoothing IDs, and worst-case edge maps.
        retained += triangles * 16 + vertices * 8 + edges * 8;
        return retained <= LIBRA_PROCEDURAL_MAX_WORK_BYTES;
    };
    if (!account())
    {
        return LIBRA_INSUFFICIENT_WORK;
    }
    for (auto i = 0u; i < count; ++i)
    {
        const auto& m = modifiers[i];
        if ((m.flags & LIBRA_MOD_ENABLED) == 0)
        {
            continue;
        }
        if (m.kind == LIBRA_MOD_MIRROR || m.kind == LIBRA_MOD_ARRAY)
        {
            const auto copies = m.kind == LIBRA_MOD_ARRAY ? m.count :
                (m.flags & LIBRA_MOD_MIRROR_KEEP_ORIGINAL) != 0 ? 2u : 1u;
            vertices *= copies;
            triangles *= copies;
            edges *= copies;
            boundary *= copies;
        }
        else if (m.kind == LIBRA_MOD_SUBDIVIDE)
        {
            for (auto level = 0u; level < m.count; ++level)
            {
                vertices += edges;
                edges = 2 * edges + 3 * triangles;
                triangles *= 4;
                boundary *= 2;
                if (!account())
                {
                    return LIBRA_INSUFFICIENT_WORK;
                }
            }
            continue;
        }
        else if (m.kind == LIBRA_MOD_SOLIDIFY)
        {
            vertices = 2 * vertices + 4 * boundary;
            triangles = 2 * triangles + 2 * boundary;
            edges = 2 * edges + 5 * boundary;
            boundary *= 6;
        }
        else
        {
            continue;
        }
        if (!account())
        {
            return LIBRA_INSUFFICIENT_WORK;
        }
    }
    // Published/staging geometry, ping-pong vertices, normal/tangent accumulators,
    // and corrected winding indices are bounded before any geometry allocation.
    retained += maximumVertices * (4 * sizeof(LibraProceduralModelVertex) + 3 * sizeof(Vec3)) +
        maximumTriangles * 3 * sizeof(std::uint32_t) * 2;
    if (retained > LIBRA_PROCEDURAL_MAX_WORK_BYTES)
    {
        return LIBRA_INSUFFICIENT_WORK;
    }
    counts.vertices = static_cast<std::uint32_t>(vertices);
    counts.indices = static_cast<std::uint32_t>(triangles * 3);
    counts.retainedBytes = retained;
    return LIBRA_OK;
}

auto sameTopology(const LibraProceduralModelDescriptor& a, const LibraProceduralModelDescriptor& b,
    const std::vector<LibraProceduralModelModifier>& original,
    const LibraProceduralModelModifier* modifiers, std::uint32_t count) -> bool
{
    if (a.version != b.version || a.shape != b.shape || a.segmentsU != b.segmentsU ||
        a.segmentsV != b.segmentsV || a.flags != b.flags || a.maxVertices != b.maxVertices ||
        a.maxTriangles != b.maxTriangles || count != original.size())
    {
        return false;
    }
    for (auto i = 0u; i < count; ++i)
    {
        const auto& x = original[i];
        const auto& y = modifiers[i];
        if (x.kind != y.kind || x.axis != y.axis || x.flags != y.flags || x.count != y.count)
        {
            return false;
        }
    }
    return true;
}

}
