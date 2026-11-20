#include "ProceduralModelInternal.h"
#include "liberafx/procedural/LbProceduralNoise.h"

namespace liberafx::procedural_model {

static auto rotateAxis(Vec3 p, std::uint32_t axis, float angle) -> Vec3
{
    const auto b = (axis + 1) % 3, c = (axis + 2) % 3;
    const auto sine = std::sin(angle), cosine = std::cos(angle);
    const auto x = p[b], y = p[c];
    p[b] = x * cosine - y * sine;
    p[c] = x * sine + y * cosine;
    return p;
}

static auto rotateEuler(Vec3 p, Vec3 angles) -> Vec3
{
    for (auto axis = 0u; axis < 3; ++axis)
    {
        p = rotateAxis(p, axis, angles[axis] * degrees);
    }
    return p;
}

struct Affine {
    Vec3 x{1, 0, 0}, y{0, 1, 0}, z{0, 0, 1}, translation;
    auto apply(Vec3 p) const -> Vec3 { return x * p.x + y * p.y + z * p.z + translation; }
};

static auto copyTransform(const LibraProceduralModelModifier& m, std::uint32_t copy, Vec3 pivot) -> Affine
{
    const auto factor = static_cast<float>(copy);
    const auto angles = Vec3{m.values[3], m.values[4], m.values[5]} * factor;
    const auto scale = Vec3{std::pow(m.values[6], factor), std::pow(m.values[7], factor), std::pow(m.values[8], factor)};
    auto out = Affine{};
    out.x = rotateEuler({scale.x, 0, 0}, angles);
    out.y = rotateEuler({0, scale.y, 0}, angles);
    out.z = rotateEuler({0, 0, scale.z}, angles);
    auto offset = Vec3{m.values[0], m.values[1], m.values[2]} * factor;
    if ((m.flags & LIBRA_MOD_RADIAL_ARRAY) != 0)
    {
        const auto divisor = m.count > 1 && std::abs(m.values[10]) < 359.999f ? m.count - 1 : m.count;
        const auto angle = m.values[10] * degrees * factor / static_cast<float>(divisor);
        out.x = rotateAxis(out.x, m.axis, angle);
        out.y = rotateAxis(out.y, m.axis, angle);
        out.z = rotateAxis(out.z, m.axis, angle);
        auto radius = Vec3{};
        radius[(m.axis + 1) % 3] = m.values[9];
        offset = offset + rotateAxis(radius, m.axis, angle);
    }
    out.translation = pivot + offset - out.x * pivot.x - out.y * pivot.y - out.z * pivot.z;
    return out;
}

static auto deform(const LibraProceduralModelModifier& m, const LibraProceduralModelVertex& vertex,
    const Bounds& bounds, Vec3 pivot, double time) -> Vec3
{
    auto p = position(vertex);
    auto relative = p - pivot;
    const auto extent = std::max(bounds.maximum[m.axis] - bounds.minimum[m.axis], 1.e-6f);
    switch (m.kind)
    {
    case LIBRA_MOD_TWIST:
        return pivot + rotateAxis(relative, m.axis, m.values[0] * degrees * relative[m.axis] / extent);
    case LIBRA_MOD_BEND:
    {
        const auto curvature = m.values[0] * degrees / extent;
        if (std::abs(curvature) <= 1.e-7f)
        {
            return p;
        }
        const auto side = (m.axis + 2) % 3;
        const auto angle = relative[m.axis] * curvature;
        const auto sine = std::sin(angle), cosine = std::cos(angle);
        const auto across = relative[side];
        relative[side] = (1 - cosine) / curvature + across * cosine;
        relative[m.axis] = sine / curvature - across * sine;
        return pivot + relative;
    }
    case LIBRA_MOD_TAPER:
    {
        const auto t = std::clamp((p[m.axis] - bounds.minimum[m.axis]) / extent, 0.0f, 1.0f);
        const auto scale = 1 + (m.values[0] - 1) * t;
        relative[(m.axis + 1) % 3] *= scale;
        relative[(m.axis + 2) % 3] *= scale;
        return pivot + relative;
    }
    case LIBRA_MOD_WAVE:
    {
        const auto phase = std::fmod(static_cast<double>(m.values[2]) + time * m.values[3], 1.0);
        p[m.axis] += m.values[0] * std::sin(2 * pi * (m.values[1] * vertex.uv[0] + static_cast<float>(phase)));
        return p;
    }
    case LIBRA_MOD_SWIRL:
    {
        const auto b = (m.axis + 1) % 3, c = (m.axis + 2) % 3;
        const auto radius = m.values[1] > 0 ? m.values[1] :
            .5f * std::max(bounds.maximum[b] - bounds.minimum[b], bounds.maximum[c] - bounds.minimum[c]);
        const auto distance = std::sqrt(relative[b] * relative[b] + relative[c] * relative[c]);
        const auto weight = std::clamp(1 - distance / std::max(radius, 1.e-6f), 0.0f, 1.0f);
        return pivot + rotateAxis(relative, m.axis, m.values[0] * degrees * weight * weight);
    }
    case LIBRA_MOD_NOISE:
    {
        const auto phase = static_cast<float>(std::fmod(static_cast<double>(m.values[2]) + time * m.values[3], 4096.0));
        const auto coordinate = relative * m.values[1] + Vec3{phase, phase * .73f, phase * .37f};
        for (auto axis = 0u; axis < 3; ++axis)
        {
            p[axis] += m.values[0] * (procedural_noise::fractal3(coordinate.x, coordinate.y, coordinate.z,
                m.seed + axis * 0x68bc21ebu, m.count, m.values[4]) * 2 - 1);
        }
        return p;
    }
    case LIBRA_MOD_ROUND:
    {
        const auto radius = m.values[1] > 0 ? m.values[1] : .5f *
            std::max({bounds.maximum.x - bounds.minimum.x, bounds.maximum.y - bounds.minimum.y, bounds.maximum.z - bounds.minimum.z});
        const auto target = dot(relative, relative) > 1.e-20f ? normalized(relative) * radius : Vec3{};
        return pivot + relative * (1 - m.values[0]) + target * m.values[0];
    }
    case LIBRA_MOD_INFLATE:
        return p + normal(vertex) * m.values[0];
    case LIBRA_MOD_TRANSFORM:
        relative = {relative.x * m.values[6], relative.y * m.values[7], relative.z * m.values[8]};
        return pivot + rotateEuler(relative, {m.values[3], m.values[4], m.values[5]}) +
            Vec3{m.values[0], m.values[1], m.values[2]};
    case LIBRA_MOD_MIRROR:
        p[m.axis] = 2 * pivot[m.axis] - p[m.axis];
        return p;
    default:
        return p;
    }
}

static auto midpoint(const LibraProceduralModelVertex& a, const LibraProceduralModelVertex& b) -> LibraProceduralModelVertex
{
    auto out = LibraProceduralModelVertex{};
    for (auto i = 0u; i < 3; ++i)
    {
        out.position[i] = (a.position[i] + b.position[i]) * .5f;
        out.normal[i] = (a.normal[i] + b.normal[i]) * .5f;
    }
    for (auto i = 0u; i < 4; ++i)
    {
        out.tangent[i] = (a.tangent[i] + b.tangent[i]) * .5f;
        out.color[i] = (a.color[i] + b.color[i]) * .5f;
    }
    for (auto i = 0u; i < 2; ++i)
    {
        out.uv[i] = (a.uv[i] + b.uv[i]) * .5f;
        out.uv2[i] = (a.uv2[i] + b.uv2[i]) * .5f;
    }
    return out;
}

static void weldDisplacements(const Topology& topology,
    const std::vector<LibraProceduralModelVertex>& vertices, EvaluationWork& work)
{
    std::fill_n(work.normalSums.begin(), topology.weldCount, Vec3{});
    std::fill_n(work.tangentSums.begin(), topology.weldCount, Vec3{1, 0, 0});
    for (auto i = 0u; i < topology.vertexCount; ++i)
    {
        const auto weld = topology.welds[i];
        work.normalSums[weld] = work.normalSums[weld] + normal(vertices[i]);
    }
    for (auto weld = 0u; weld < topology.weldCount; ++weld)
    {
        work.normalSums[weld] = normalized(work.normalSums[weld]);
    }
    for (auto i = 0u; i < topology.vertexCount; ++i)
    {
        const auto weld = topology.welds[i];
        work.tangentSums[weld].x = std::min(work.tangentSums[weld].x,
            dot(work.normalSums[weld], normal(vertices[i])));
    }
    for (auto weld = 0u; weld < topology.weldCount; ++weld)
    {
        // Shared displacement seals UV/hard-normal duplicates. A bounded miter
        // preserves box face thickness without exploding at nearly folded edges.
        work.normalSums[weld] = work.normalSums[weld] / std::max(.25f, work.tangentSums[weld].x);
    }
}

auto evaluate(const Definition& definition, const LibraProceduralModelDescriptor& d,
    const LibraProceduralModelModifier* modifiers, double time,
    EvaluationWork& work, Geometry& result, std::size_t stageLimit) -> LibraResult
{
    const auto& plan = *definition.plan;
    const auto limit = std::min(stageLimit, plan.stages.size());
    const auto activeSigns = stageParity(modifiers, static_cast<std::uint32_t>(definition.modifiers.size()));
    auto current = 0u;
    auto first = std::size_t{};
    auto topology = plan.base;
    auto globalParity = std::uint32_t{};
    auto bounds = Bounds{};
    auto pivot = Vec3{};
    auto dirtyNormals = false;
    if (definition.prefix && definition.staticPrefix <= limit && (d.flags & LIBRA_PROCEDURAL_ANIMATED_BASE) == 0)
    {
        const auto& prefix = *definition.prefix;
        std::copy(prefix.vertices.begin(), prefix.vertices.end(), work.vertices[0].begin());
        first = definition.staticPrefix;
        topology = first == 0 ? plan.base : plan.stages[first - 1].output;
        globalParity = first == 0 ? 0 : plan.stages[first - 1].outputParity;
        bounds = prefix.bounds;
        const auto& base = *definition.baseGeometry;
        const auto resolved = resolvePivot(d, base.vertices, base.bounds, pivot);
        if (resolved != LIBRA_OK)
        {
            return resolved;
        }
    }
    else if (definition.baseGeometry && sameBaseValues(definition.descriptor, d))
    {
        const auto& base = *definition.baseGeometry;
        std::copy(base.vertices.begin(), base.vertices.end(), work.vertices[0].begin());
        bounds = base.bounds;
        const auto resolved = resolvePivot(d, base.vertices, base.bounds, pivot);
        if (resolved != LIBRA_OK)
        {
            return resolved;
        }
    }
    else
    {
        const auto generated = generateBase(d, plan, work, result);
        if (generated != LIBRA_OK)
        {
            return generated;
        }
        std::copy(result.vertices.begin(), result.vertices.end(), work.vertices[0].begin());
        bounds = result.bounds;
        pivot = result.pivot;
    }
    for (auto step = first; step < limit; ++step)
    {
        const auto& stage = plan.stages[step];
        const auto& m = modifiers[stage.modifierIndex];
        auto& input = work.vertices[current];
        auto& output = work.vertices[current ^ 1u];
        if (dirtyNormals && (m.kind == LIBRA_MOD_INFLATE || m.kind == LIBRA_MOD_SOLIDIFY))
        {
            rebuildNormals(*topology, activeSigns, globalParity, input, work, false);
            dirtyNormals = false;
        }
        const auto center = (m.flags & LIBRA_MOD_CUSTOM_PIVOT) != 0 ?
            Vec3{m.values[12], m.values[13], m.values[14]} : pivot;
        const auto inputCount = stage.input->vertexCount;
        if (m.kind == LIBRA_MOD_SOLIDIFY || m.kind == LIBRA_MOD_INFLATE)
        {
            weldDisplacements(*stage.input, input, work);
        }
        if (m.kind == LIBRA_MOD_SUBDIVIDE)
        {
            std::copy_n(input.begin(), inputCount, output.begin());
            for (auto i = std::size_t{}; i < stage.edges.size(); ++i)
            {
                output[inputCount + i] = midpoint(input[stage.edges[i][0]], input[stage.edges[i][1]]);
            }
        }
        else if (m.kind == LIBRA_MOD_SOLIDIFY)
        {
            const auto outerDistance = m.values[0] * .5f * (1 + m.values[1]);
            const auto innerDistance = m.values[0] * .5f * (m.values[1] - 1);
            for (auto i = 0u; i < inputCount; ++i)
            {
                output[i] = input[i];
                output[i + inputCount] = input[i];
                const auto displacement = work.normalSums[stage.input->welds[i]];
                setPosition(output[i], position(input[i]) + displacement * outerDistance);
                setPosition(output[i + inputCount], position(input[i]) + displacement * innerDistance);
            }
            for (auto i = std::size_t{}; i < stage.edges.size(); ++i)
            {
                const auto a = stage.edges[i][0], b = stage.edges[i][1];
                const auto start = inputCount * 2 + i * 4;
                output[start] = output[a];
                output[start + 1] = output[a + inputCount];
                output[start + 2] = output[b + inputCount];
                output[start + 3] = output[b];
                for (auto corner = 0u; corner < 4; ++corner)
                {
                    auto& vertex = output[start + corner];
                    vertex.uv[0] = vertex.uv2[0] = corner >= 2 ? 1.0f : 0.0f;
                    vertex.uv[1] = vertex.uv2[1] = corner == 1 || corner == 2 ? 1.0f : 0.0f;
                }
            }
        }
        else if (m.kind == LIBRA_MOD_INFLATE)
        {
            for (auto i = 0u; i < inputCount; ++i)
            {
                output[i] = input[i];
                setPosition(output[i], position(input[i]) + work.normalSums[stage.input->welds[i]] * m.values[0]);
            }
        }
        else if (m.kind == LIBRA_MOD_ARRAY)
        {
            auto matrices = std::array<Affine, LIBRA_PROCEDURAL_MAX_COPIES>{};
            for (auto copy = 0u; copy < m.count; ++copy)
            {
                matrices[copy] = copyTransform(m, copy, center);
            }
            for (auto i = 0u; i < stage.output->vertexCount; ++i)
            {
                output[i] = input[i % inputCount];
                setPosition(output[i], matrices[i / inputCount].apply(position(output[i])));
            }
        }
        else if (m.kind == LIBRA_MOD_MIRROR && (m.flags & LIBRA_MOD_MIRROR_KEEP_ORIGINAL) != 0)
        {
            std::copy_n(input.begin(), inputCount, output.begin());
            for (auto i = 0u; i < inputCount; ++i)
            {
                output[inputCount + i] = input[i];
                setPosition(output[inputCount + i], deform(m, input[i], bounds, center, time));
            }
        }
        else
        {
            for (auto i = 0u; i < inputCount; ++i)
            {
                output[i] = input[i];
                setPosition(output[i], deform(m, input[i], bounds, center, time));
            }
        }
        bounds = Bounds{};
        for (auto i = 0u; i < stage.output->vertexCount; ++i)
        {
            const auto p = position(output[i]);
            if (!finite(p))
            {
                return LIBRA_NON_FINITE;
            }
            bounds.add(p);
        }
        current ^= 1u;
        topology = stage.output;
        globalParity = stage.outputParity;
        dirtyNormals = true;
    }
    if (dirtyNormals)
    {
        rebuildNormals(*topology, activeSigns, globalParity, work.vertices[current], work,
            (d.flags & LIBRA_PROCEDURAL_TANGENTS) != 0);
    }
    result.vertices.resize(topology->vertexCount);
    std::copy_n(work.vertices[current].begin(), topology->vertexCount, result.vertices.begin());
    auto usedParity = globalParity;
    for (const auto mask : topology->triangleParity)
    {
        usedParity |= mask;
    }
    const auto signature = activeSigns & usedParity;
    if (result.indexTopology != topology.get() || result.indexSignature != signature)
    {
        result.indices.resize(topology->indices.size());
        for (auto triangle = std::size_t{}; triangle < topology->triangleParity.size(); ++triangle)
        {
            const auto reverse = oddParity(activeSigns & (topology->triangleParity[triangle] ^ globalParity));
            result.indices[triangle * 3] = topology->indices[triangle * 3];
            result.indices[triangle * 3 + 1] = topology->indices[triangle * 3 + (reverse ? 2 : 1)];
            result.indices[triangle * 3 + 2] = topology->indices[triangle * 3 + (reverse ? 1 : 2)];
        }
        result.indexTopology = topology.get();
        result.indexSignature = signature;
    }
    result.bounds = bounds;
    result.pivot = pivot;
    return LIBRA_OK;
}

}
