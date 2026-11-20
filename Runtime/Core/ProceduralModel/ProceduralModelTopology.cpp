#include "ProceduralModelInternal.h"
#include <unordered_map>

namespace liberafx::procedural_model {

static auto edgeKey(std::uint32_t a, std::uint32_t b) -> std::uint64_t
{
    return (static_cast<std::uint64_t>(std::min(a, b)) << 32) | std::max(a, b);
}

static auto copiedTopology(const Topology& source, std::uint32_t copies, bool mirror,
    std::uint32_t parityBit) -> std::shared_ptr<const Topology>
{
    auto out = std::make_shared<Topology>();
    out->vertexCount = source.vertexCount * copies;
    out->weldCount = source.weldCount * copies;
    out->normalGroupCount = source.normalGroupCount * copies;
    out->welds.reserve(out->vertexCount);
    out->normalGroups.reserve(out->vertexCount);
    out->indices.reserve(source.indices.size() * copies);
    out->triangleParity.reserve(source.triangleParity.size() * copies);
    for (auto copy = 0u; copy < copies; ++copy)
    {
        for (auto vertex = 0u; vertex < source.vertexCount; ++vertex)
        {
            out->welds.push_back(source.welds[vertex] + copy * source.weldCount);
            out->normalGroups.push_back(source.normalGroups[vertex] + copy * source.normalGroupCount);
        }
        for (auto triangle = std::size_t{}; triangle < source.triangleParity.size(); ++triangle)
        {
            const auto offset = triangle * 3;
            const auto reverse = mirror && copy != 0;
            out->indices.push_back(source.indices[offset] + copy * source.vertexCount);
            out->indices.push_back(source.indices[offset + (reverse ? 2 : 1)] + copy * source.vertexCount);
            out->indices.push_back(source.indices[offset + (reverse ? 1 : 2)] + copy * source.vertexCount);
            out->triangleParity.push_back(source.triangleParity[triangle] ^
                (!mirror && (copy & 1u) != 0 ? parityBit : 0u));
        }
    }
    return out;
}

static auto subdividedTopology(const Topology& source, Stage& stage) -> std::shared_ptr<const Topology>
{
    auto out = std::make_shared<Topology>();
    out->welds = source.welds;
    out->normalGroups = source.normalGroups;
    out->weldCount = source.weldCount;
    out->normalGroupCount = source.normalGroupCount;
    out->indices.reserve(source.indices.size() * 4);
    out->triangleParity.reserve(source.triangleParity.size() * 4);
    auto edges = std::unordered_map<std::uint64_t, std::uint32_t>{};
    auto weldEdges = std::unordered_map<std::uint64_t, std::uint32_t>{};
    auto normalEdges = std::unordered_map<std::uint64_t, std::uint32_t>{};
    const auto midpoint = [&](std::uint32_t a, std::uint32_t b)
    {
        const auto key = edgeKey(a, b);
        const auto found = edges.find(key);
        if (found != edges.end())
        {
            return found->second;
        }
        const auto index = source.vertexCount + static_cast<std::uint32_t>(stage.edges.size());
        edges.emplace(key, index);
        stage.edges.push_back({a, b});
        const auto wa = source.welds[a], wb = source.welds[b];
        if (wa == wb)
        {
            out->welds.push_back(wa);
        }
        else
        {
            const auto inserted = weldEdges.emplace(edgeKey(wa, wb), out->weldCount);
            out->welds.push_back(inserted.first->second);
            if (inserted.second)
            {
                ++out->weldCount;
            }
        }
        const auto ga = source.normalGroups[a], gb = source.normalGroups[b];
        if (ga == gb)
        {
            out->normalGroups.push_back(ga);
        }
        else
        {
            const auto inserted = normalEdges.emplace(edgeKey(ga, gb), out->normalGroupCount);
            out->normalGroups.push_back(inserted.first->second);
            if (inserted.second)
            {
                ++out->normalGroupCount;
            }
        }
        return index;
    };
    const auto triangle = [&](std::uint32_t a, std::uint32_t b, std::uint32_t c, std::uint32_t mask)
    {
        out->indices.push_back(a);
        out->indices.push_back(b);
        out->indices.push_back(c);
        out->triangleParity.push_back(mask);
    };
    for (auto i = std::size_t{}; i < source.triangleParity.size(); ++i)
    {
        const auto a = source.indices[i * 3], b = source.indices[i * 3 + 1], c = source.indices[i * 3 + 2];
        const auto ab = midpoint(a, b), bc = midpoint(b, c), ca = midpoint(c, a);
        const auto mask = source.triangleParity[i];
        triangle(a, ab, ca, mask);
        triangle(ab, b, bc, mask);
        triangle(ca, bc, c, mask);
        triangle(ab, bc, ca, mask);
    }
    out->vertexCount = static_cast<std::uint32_t>(out->welds.size());
    return out;
}

struct BoundaryEdge {
    std::uint32_t a = 0, b = 0, parity = 0, uses = 0;
};

static auto solidifiedTopology(const Topology& source, Stage& stage) -> std::shared_ptr<const Topology>
{
    auto boundary = std::unordered_map<std::uint64_t, BoundaryEdge>{};
    for (auto i = std::size_t{}; i < source.triangleParity.size(); ++i)
    {
        const auto* triangle = source.indices.data() + i * 3;
        if (source.welds[triangle[0]] == source.welds[triangle[1]] ||
            source.welds[triangle[1]] == source.welds[triangle[2]] ||
            source.welds[triangle[2]] == source.welds[triangle[0]])
        {
            continue;
        }
        for (auto edge = 0u; edge < 3; ++edge)
        {
            const auto a = triangle[edge], b = triangle[(edge + 1) % 3];
            const auto key = edgeKey(source.welds[a], source.welds[b]);
            auto& item = boundary[key];
            if (item.uses == 0)
            {
                item.a = a;
                item.b = b;
                item.parity = source.triangleParity[i];
            }
            ++item.uses;
        }
    }
    // Stable ordering makes construction and exported diagnostic indices
    // independent of unordered-map iteration order.
    auto ordered = std::vector<std::pair<std::uint64_t, BoundaryEdge>>{};
    for (const auto& edge : boundary)
    {
        if (edge.second.uses == 1)
        {
            ordered.push_back(edge);
        }
    }
    std::sort(ordered.begin(), ordered.end(), [](const auto& a, const auto& b) { return a.first < b.first; });
    auto out = std::make_shared<Topology>();
    out->vertexCount = source.vertexCount * 2 + static_cast<std::uint32_t>(ordered.size()) * 4;
    out->weldCount = source.weldCount * 2;
    out->normalGroupCount = source.normalGroupCount * 2 + static_cast<std::uint32_t>(ordered.size());
    out->welds.reserve(out->vertexCount);
    out->normalGroups.reserve(out->vertexCount);
    out->indices.reserve(source.indices.size() * 2 + ordered.size() * 6);
    out->triangleParity.reserve(source.triangleParity.size() * 2 + ordered.size() * 2);
    for (auto shell = 0u; shell < 2; ++shell)
    {
        for (auto vertex = 0u; vertex < source.vertexCount; ++vertex)
        {
            out->welds.push_back(source.welds[vertex] + shell * source.weldCount);
            out->normalGroups.push_back(source.normalGroups[vertex] + shell * source.normalGroupCount);
        }
        for (auto i = std::size_t{}; i < source.triangleParity.size(); ++i)
        {
            out->indices.push_back(source.indices[i * 3] + shell * source.vertexCount);
            out->indices.push_back(source.indices[i * 3 + (shell == 0 ? 1 : 2)] + shell * source.vertexCount);
            out->indices.push_back(source.indices[i * 3 + (shell == 0 ? 2 : 1)] + shell * source.vertexCount);
            out->triangleParity.push_back(source.triangleParity[i]);
        }
    }
    for (auto i = std::size_t{}; i < ordered.size(); ++i)
    {
        const auto& edge = ordered[i].second;
        stage.edges.push_back({edge.a, edge.b});
        stage.edgeParity.push_back(edge.parity);
        const auto a = source.welds[edge.a], b = source.welds[edge.b];
        out->welds.insert(out->welds.end(), {a, a + source.weldCount, b + source.weldCount, b});
        out->normalGroups.insert(out->normalGroups.end(), 4, source.normalGroupCount * 2 + static_cast<std::uint32_t>(i));
        const auto start = source.vertexCount * 2 + static_cast<std::uint32_t>(i) * 4;
        out->indices.insert(out->indices.end(), {start, start + 1, start + 2, start, start + 2, start + 3});
        out->triangleParity.insert(out->triangleParity.end(), 2, edge.parity);
    }
    return out;
}

auto compileStages(const LibraProceduralModelModifier* modifiers, std::uint32_t count, Plan& plan) -> LibraResult
{
    auto current = plan.base;
    auto globalParity = std::uint32_t{};
    for (auto i = 0u; i < count; ++i)
    {
        const auto& modifier = modifiers[i];
        if ((modifier.flags & LIBRA_MOD_ENABLED) == 0)
        {
            continue;
        }
        const auto levels = modifier.kind == LIBRA_MOD_SUBDIVIDE ? modifier.count : 1u;
        for (auto level = 0u; level < levels; ++level)
        {
            auto stage = Stage{};
            stage.modifierIndex = i;
            stage.input = current;
            stage.inputParity = globalParity;
            if (modifier.kind == LIBRA_MOD_MIRROR && (modifier.flags & LIBRA_MOD_MIRROR_KEEP_ORIGINAL) != 0)
            {
                current = copiedTopology(*current, 2, true, 0);
            }
            else if (modifier.kind == LIBRA_MOD_ARRAY && modifier.count > 1)
            {
                current = copiedTopology(*current, modifier.count, false, 1u << i);
            }
            else if (modifier.kind == LIBRA_MOD_SUBDIVIDE)
            {
                current = subdividedTopology(*current, stage);
            }
            else if (modifier.kind == LIBRA_MOD_SOLIDIFY)
            {
                current = solidifiedTopology(*current, stage);
            }
            else if (modifier.kind == LIBRA_MOD_TRANSFORM ||
                (modifier.kind == LIBRA_MOD_MIRROR && (modifier.flags & LIBRA_MOD_MIRROR_KEEP_ORIGINAL) == 0))
            {
                globalParity ^= 1u << i;
            }
            stage.output = current;
            stage.outputParity = globalParity;
            plan.maximumVertices = std::max(plan.maximumVertices, current->vertexCount);
            plan.maximumIndices = std::max(plan.maximumIndices, static_cast<std::uint32_t>(current->indices.size()));
            if (current != stage.input)
            {
                plan.retainedBytes += current->indices.size() * 4 + current->triangleParity.size() * 4 +
                    current->vertexCount * 8;
            }
            plan.retainedBytes += stage.edges.size() * sizeof(stage.edges[0]) + stage.edgeParity.size() * 4;
            if (plan.retainedBytes > LIBRA_PROCEDURAL_MAX_WORK_BYTES || current->vertexCount > LIBRA_PROCEDURAL_MAX_VERTICES ||
                current->indices.size() > LIBRA_PROCEDURAL_MAX_TRIANGLES * 3u)
            {
                return LIBRA_INSUFFICIENT_WORK;
            }
            plan.stages.push_back(std::move(stage));
        }
    }
    plan.finalParity = globalParity;
    return LIBRA_OK;
}

auto stageParity(const LibraProceduralModelModifier* modifiers, std::uint32_t count) -> std::uint32_t
{
    auto mask = std::uint32_t{};
    for (auto i = 0u; i < count; ++i)
    {
        const auto& m = modifiers[i];
        if ((m.flags & LIBRA_MOD_ENABLED) == 0)
        {
            continue;
        }
        if ((m.kind == LIBRA_MOD_TRANSFORM || m.kind == LIBRA_MOD_ARRAY) &&
            m.values[6] * m.values[7] * m.values[8] < 0)
        {
            mask |= 1u << i;
        }
        else if (m.kind == LIBRA_MOD_MIRROR && (m.flags & LIBRA_MOD_MIRROR_KEEP_ORIGINAL) == 0)
        {
            mask |= 1u << i;
        }
    }
    return mask;
}

}
