#include "ProceduralModelInternal.h"

namespace liberafx::procedural_model {

void rebuildNormals(const Topology& topology, std::uint32_t activeSigns, std::uint32_t globalParity,
    std::vector<LibraProceduralModelVertex>& vertices, EvaluationWork& work, bool tangents)
{
    std::fill_n(work.normalSums.begin(), topology.normalGroupCount, Vec3{});
    if (tangents)
    {
        std::fill_n(work.tangentSums.begin(), topology.normalGroupCount, Vec3{});
        std::fill_n(work.bitangentSums.begin(), topology.normalGroupCount, Vec3{});
    }
    for (auto triangle = std::size_t{}; triangle < topology.triangleParity.size(); ++triangle)
    {
        const auto reverse = oddParity(activeSigns & (topology.triangleParity[triangle] ^ globalParity));
        const auto a = topology.indices[triangle * 3];
        const auto b = topology.indices[triangle * 3 + (reverse ? 2 : 1)];
        const auto c = topology.indices[triangle * 3 + (reverse ? 1 : 2)];
        const auto edgeA = position(vertices[b]) - position(vertices[a]);
        const auto edgeB = position(vertices[c]) - position(vertices[a]);
        const auto face = cross(edgeA, edgeB);
        for (const auto vertex : {a, b, c})
        {
            auto& sum = work.normalSums[topology.normalGroups[vertex]];
            sum = sum + face;
        }
        if (!tangents)
        {
            continue;
        }
        const auto u1 = vertices[b].uv[0] - vertices[a].uv[0];
        const auto v1 = vertices[b].uv[1] - vertices[a].uv[1];
        const auto u2 = vertices[c].uv[0] - vertices[a].uv[0];
        const auto v2 = vertices[c].uv[1] - vertices[a].uv[1];
        const auto determinant = u1 * v2 - u2 * v1;
        if (std::abs(determinant) <= 1.e-12f)
        {
            continue;
        }
        const auto tangent = (edgeA * v2 - edgeB * v1) / determinant;
        const auto bitangent = (edgeB * u1 - edgeA * u2) / determinant;
        for (const auto vertex : {a, b, c})
        {
            const auto group = topology.normalGroups[vertex];
            work.tangentSums[group] = work.tangentSums[group] + tangent;
            work.bitangentSums[group] = work.bitangentSums[group] + bitangent;
        }
    }
    for (auto vertex = 0u; vertex < topology.vertexCount; ++vertex)
    {
        auto& output = vertices[vertex];
        const auto group = topology.normalGroups[vertex];
        const auto n = normalized(work.normalSums[group]);
        output.normal[0] = n.x;
        output.normal[1] = n.y;
        output.normal[2] = n.z;
        const auto fallback = normalized(cross(std::abs(n.y) < .9f ? Vec3{0, 1, 0} : Vec3{0, 0, 1}, n), {1, 0, 0});
        const auto sum = tangents ? work.tangentSums[group] : fallback;
        const auto tangent = normalized(sum - n * dot(n, sum), fallback);
        output.tangent[0] = tangent.x;
        output.tangent[1] = tangent.y;
        output.tangent[2] = tangent.z;
        output.tangent[3] = tangents && dot(cross(n, tangent), work.bitangentSums[group]) < 0 ? -1.0f : 1.0f;
    }
}

auto allocateWork(const Plan& plan, EvaluationWork& work, Geometry& result) -> LibraResult
{
    for (auto& vertices : work.vertices)
    {
        vertices.resize(plan.maximumVertices);
    }
    work.normalSums.resize(plan.maximumVertices);
    work.tangentSums.resize(plan.maximumVertices);
    work.bitangentSums.resize(plan.maximumVertices);
    result.vertices.reserve(plan.maximumVertices);
    result.indices.reserve(plan.maximumIndices);
    return LIBRA_OK;
}

auto generateBase(const LibraProceduralModelDescriptor& d, const Plan& plan,
    EvaluationWork& work, Geometry& result) -> LibraResult
{
    result.vertices.resize(plan.samples.size());
    result.bounds = Bounds{};
    for (auto i = std::size_t{}; i < plan.samples.size(); ++i)
    {
        const auto& sample = plan.samples[i];
        const auto p = basePosition(d, sample);
        if (!finite(p))
        {
            return LIBRA_NON_FINITE;
        }
        auto& vertex = result.vertices[i];
        vertex = LibraProceduralModelVertex{};
        setPosition(vertex, p);
        if ((sample.patch & 0x10000000u) != 0)
        {
            vertex.uv[0] = .5f + .5f * sample.v * std::cos(2 * pi * sample.u);
            vertex.uv[1] = .5f + .5f * sample.v * std::sin(2 * pi * sample.u);
        }
        else
        {
            vertex.uv[0] = sample.u;
            vertex.uv[1] = sample.v;
        }
        vertex.uv2[0] = vertex.uv[0];
        vertex.uv2[1] = vertex.uv[1];
        std::fill_n(vertex.color, 4, 1.0f);
        result.bounds.add(p);
    }
    const auto pivot = resolvePivot(d, result.vertices, result.bounds, result.pivot);
    if (pivot != LIBRA_OK)
    {
        return pivot;
    }
    rebuildNormals(*plan.base, 0, 0, result.vertices, work, (d.flags & LIBRA_PROCEDURAL_TANGENTS) != 0);
    result.indices.assign(plan.base->indices.begin(), plan.base->indices.end());
    result.indexTopology = plan.base.get();
    result.indexSignature = 0;
    return LIBRA_OK;
}

}
