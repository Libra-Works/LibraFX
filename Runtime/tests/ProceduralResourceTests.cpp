#include "liberafx/host/LbProceduralAdapter.h"
#include "liberafx/host/LbProceduralCache.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <atomic>
#include <cstdlib>
#include <new>
#include <iostream>
#include <limits>
#include <map>

static std::atomic<std::size_t> allocations{0};
void* operator new(std::size_t bytes)
{
    ++allocations;
    if (auto* data = std::malloc(bytes == 0 ? 1 : bytes))
    {
        return data;
    }
    throw std::bad_alloc();
}
void operator delete(void* data) noexcept { std::free(data); }
void operator delete(void* data, std::size_t) noexcept { std::free(data); }

namespace procedural_tests {
static auto descriptor(std::uint32_t shape) -> LibraProceduralModelDescriptor
{
    auto d = LibraProceduralModelDescriptor{};
    d.version = 1; d.shape = shape; d.segmentsU = 16; d.segmentsV = 8;
    d.size[0] = d.size[1] = d.size[2] = 1;
    d.innerRadius = .5f; d.sweepDegrees = 360; d.turns = 2; d.tubeRadius = .08f;
    d.flags = LIBRA_PROCEDURAL_CAPPED | LIBRA_PROCEDURAL_TANGENTS;
    return d;
}
struct Model
{
    LibraProceduralModelInstance* instance = nullptr;
    ~Model() { libra_procedural_model_release(instance); }
    auto view() const -> LibraProceduralModelView
    {
        auto v = LibraProceduralModelView{};
        assert(libra_procedural_model_view(instance, &v) == LIBRA_OK);
        return v;
    }
};
static void validGeometry(const LibraProceduralModelView& view)
{
    assert(view.vertexCount > 0 && view.indexCount > 0 && view.indexCount % 3 == 0);
    for (auto i = 0u; i < view.vertexCount; ++i)
    {
        const auto& vertex = view.vertices[i];
        auto length = 0.0f, tangent = 0.0f, dot = 0.0f;
        for (auto c = 0u; c < 3; ++c)
        {
            assert(std::isfinite(vertex.position[c]) && vertex.position[c] >= view.boundsMin[c] - 1e-4 && vertex.position[c] <= view.boundsMax[c] + 1e-4);
            length += vertex.normal[c] * vertex.normal[c]; tangent += vertex.tangent[c] * vertex.tangent[c];
            dot += vertex.normal[c] * vertex.tangent[c];
        }
        assert(std::abs(length - 1) < 1e-3 && std::abs(tangent - 1) < 1e-3 && std::abs(dot) < 1e-3);
    }
    for (auto i = 0u; i < view.indexCount; ++i)
    {
        assert(view.indices[i] < view.vertexCount);
    }
}
static void shapesAndModifiers()
{
    for (auto shape = 0u; shape < LIBRA_SHAPE_COUNT; ++shape)
    {
        auto d = descriptor(shape); auto model = Model{}; auto counts = LibraProceduralModelCounts{};
        assert(libra_procedural_model_predict(&d, nullptr, 0, &counts) == LIBRA_OK);
        assert(libra_procedural_model_create(&d, nullptr, 0, &model.instance) == LIBRA_OK);
        validGeometry(model.view());
        assert(model.view().vertexCount <= counts.vertices && model.view().indexCount <= counts.indices);
        for (auto kind = 0u; kind < LIBRA_MOD_COUNT; ++kind)
        {
            auto modifier = LibraProceduralModelModifier{}; modifier.kind = kind; modifier.axis = 1;
            modifier.flags = LIBRA_MOD_ENABLED; modifier.count = kind == LIBRA_MOD_ARRAY ? 3 : 1;
            modifier.values[0] = .15f;
            if (kind == LIBRA_MOD_NOISE) { modifier.values[1] = 2; modifier.values[4] = .5f; }
            if (kind == LIBRA_MOD_TRANSFORM || kind == LIBRA_MOD_ARRAY) { modifier.values[6] = modifier.values[7] = modifier.values[8] = 1; }
            auto modified = Model{};
            assert(libra_procedural_model_predict(&d, &modifier, 1, &counts) == LIBRA_OK);
            assert(libra_procedural_model_create(&d, &modifier, 1, &modified.instance) == LIBRA_OK);
            validGeometry(modified.view());
            assert(modified.view().vertexCount <= counts.vertices && modified.view().indexCount <= counts.indices);
        }
    }
}
static void animationAndIsolation()
{
    auto d = descriptor(LIBRA_SHAPE_PLANE); d.flags |= LIBRA_PROCEDURAL_ANIMATED;
    auto modifier = LibraProceduralModelModifier{};
    modifier.kind = LIBRA_MOD_WAVE; modifier.axis = 2; modifier.flags = LIBRA_MOD_ENABLED | LIBRA_MOD_ANIMATED;
    modifier.count = 1; modifier.values[0] = .2f; modifier.values[1] = 2; modifier.values[3] = 1;
    auto model = Model{}, clone = Model{};
    assert(libra_procedural_model_create(&d, &modifier, 1, &model.instance) == LIBRA_OK);
    assert(libra_procedural_model_clone(model.instance, &clone.instance) == LIBRA_OK);
    auto initial = clone.view(); const auto first = initial.vertices[0];
    auto changed = std::uint32_t{};
    assert(libra_procedural_model_evaluate(model.instance, &d, &modifier, 1, .1, &changed) == LIBRA_OK);
    const auto indexRevision = model.view().indexRevision;
    const auto before = allocations.load();
    for (auto frame = 1; frame < 61; ++frame)
    {
        assert(libra_procedural_model_evaluate(model.instance, &d, &modifier, 1, frame / 60.0, &changed) == LIBRA_OK);
        assert(model.view().indexRevision == indexRevision);
    }
    assert(allocations.load() == before);
    assert(std::memcmp(&clone.view().vertices[0], &first, sizeof(first)) == 0);
    auto stats = LibraProceduralModelStats{};
    assert(libra_procedural_model_stats(model.instance, &stats) == LIBRA_OK && stats.capacityGrowths == 0);
    assert(libra_procedural_model_evaluate(model.instance, &d, &modifier, 1, 1, &changed) == LIBRA_OK && changed == 0);
    const auto revision = model.view().vertexRevision;
    auto bad = d; bad.segmentsU *= 2;
    assert(libra_procedural_model_evaluate(model.instance, &bad, &modifier, 1, 2, &changed) != LIBRA_OK);
    assert(model.view().vertexRevision == revision);
    std::cout << "animated plane max us=" << stats.maximumEvaluationMicroseconds << " retained=" << stats.retainedBytes << '\n';
}
static void pivotsAndBounds()
{
    auto d = descriptor(LIBRA_SHAPE_CYLINDER); d.pivotMode = LIBRA_PIVOT_BASE;
    auto model = Model{};
    assert(libra_procedural_model_create(&d, nullptr, 0, &model.instance) == LIBRA_OK);
    const auto initial = model.view(); assert(std::abs(initial.pivot[1] - initial.boundsMin[1]) < 1e-6);
    d.pivotMode = LIBRA_PIVOT_CUSTOM; d.pivot[0] = 3; d.pivot[1] = -2; d.pivot[2] = 1;
    auto changed = std::uint32_t{};
    assert(libra_procedural_model_evaluate(model.instance, &d, nullptr, 0, 0, &changed) == LIBRA_OK);
    assert(changed == LIBRA_PROCEDURAL_CHANGED_PIVOT && model.view().vertexRevision == initial.vertexRevision);
    assert(model.view().pivot[0] == 3 && model.view().vertices[0].position[0] == initial.vertices[0].position[0]);
    auto copy = Model{};
    assert(libra_procedural_model_clone(model.instance, &copy.instance) == LIBRA_OK);
    assert(copy.view().definitionId == initial.definitionId && copy.view().pivot[0] != 3);
    d.segmentsU = 256; d.segmentsV = 128;
    auto mod = LibraProceduralModelModifier{}; mod.kind = LIBRA_MOD_ARRAY; mod.count = 64; mod.flags = LIBRA_MOD_ENABLED;
    mod.values[6] = mod.values[7] = mod.values[8] = 1;
    auto counts = LibraProceduralModelCounts{};
    assert(libra_procedural_model_predict(&d, &mod, 1, &counts) != LIBRA_OK);
}
static void pixels()
{
    auto program = LibraProceduralPixelProgram{1, 2, 2, 0};
    auto ops = std::array<LibraProceduralPixelOperation, 2>{};
    ops[0].kind = LIBRA_PIXEL_CONSTANT; ops[0].values[0] = .25f; ops[0].values[1] = .5f; ops[0].values[2] = .75f; ops[0].values[3] = 1;
    ops[1].kind = LIBRA_PIXEL_INVERT; ops[1].inputA = 1;
    float rgba[4]{}; auto usesTime = std::uint32_t{};
    assert(libra_procedural_pixel_validate(&program, ops.data(), 2, &usesTime) == LIBRA_OK && usesTime == 0);
    assert(libra_procedural_pixel_sample(&program, ops.data(), 2, .1f, .2f, 0, nullptr, nullptr, rgba) == LIBRA_OK);
    assert(rgba[0] == .75f && rgba[1] == .5f && rgba[2] == .25f && rgba[3] == 0);
    ops[0].kind = LIBRA_PIXEL_VALUE_NOISE; ops[0].values[0] = ops[0].values[1] = 1; ops[0].values[2] = ops[0].values[3] = 0;
    float repeated[4]{};
    assert(libra_procedural_pixel_sample(&program, ops.data(), 2, .25f, .5f, 0, nullptr, nullptr, rgba) == LIBRA_OK);
    assert(libra_procedural_pixel_sample(&program, ops.data(), 2, 4096.25f, 4096.5f, 0, nullptr, nullptr, repeated) == LIBRA_OK);
    assert(std::memcmp(rgba, repeated, sizeof(rgba)) == 0);
    ops[1].inputA = 2;
    assert(libra_procedural_pixel_validate(&program, ops.data(), 2, &usesTime) == LIBRA_INVALID_ARGUMENT);
}
static void contentSharing()
{
    using namespace liberafx::procedural_adapter;
    auto definition = Definition{}; definition.kind = 1;
    definition.pixel = {1, 1, 1, 0};
    definition.operations.resize(1); definition.operations[0].kind = 1;
    definition.inputs.resize(3);
    for (auto& input : definition.inputs) input.width = 4;
    definition.inputs[0].defaults = {1, .5, .25, 1};
    const auto key = staticIdentity(definition); assert(!key.empty());
    auto renamed = definition; renamed.inputs[0].id = "different-node-identity";
    assert(staticIdentity(renamed) == key);
    auto changed = definition; changed.inputs[0].defaults[0] = .75;
    assert(staticIdentity(changed) != key);
    changed = definition; changed.inputs[0].connected = true;
    assert(staticIdentity(changed).empty());
    changed = definition; changed.operations[0].kind = 2;
    assert(staticIdentity(changed).empty());
    auto cache = StaticResourceCache<int>{};
    auto first = std::make_shared<int>(7); cache.insert(key, first);
    auto second = cache.find(key); assert(second == first);
    first.reset(); assert(*second == 7);
    second.reset(); assert(!cache.find(key));
}
static void solidifyClosesSeams()
{
    for (const auto shape : {LIBRA_SHAPE_PLANE, LIBRA_SHAPE_BOX, LIBRA_SHAPE_SPHERE, LIBRA_SHAPE_CYLINDER, LIBRA_SHAPE_TORUS})
    {
        auto d = descriptor(shape); d.segmentsU = 8; d.segmentsV = 4;
        auto modifier = LibraProceduralModelModifier{}; modifier.kind = LIBRA_MOD_SOLIDIFY;
        modifier.count = 1; modifier.flags = LIBRA_MOD_ENABLED; modifier.values[0] = .05f;
        auto model = Model{};
        assert(libra_procedural_model_create(&d, &modifier, 1, &model.instance) == LIBRA_OK);
        const auto view = model.view();
        auto positions = std::map<std::array<int, 3>, uint32_t>{};
        auto ids = std::vector<uint32_t>(view.vertexCount);
        for (auto i = 0u; i < view.vertexCount; ++i)
        {
            auto key = std::array<int, 3>{};
            for (auto c = 0u; c < 3; ++c)
            {
                key[c] = static_cast<int>(std::round(view.vertices[i].position[c] * 100000));
            }
            const auto [entry, inserted] = positions.emplace(key, static_cast<uint32_t>(positions.size()));
            ids[i] = entry->second;
        }
        auto edges = std::map<std::array<uint32_t, 2>, std::array<int, 2>>{};
        for (auto triangle = 0u; triangle < view.indexCount; triangle += 3)
        {
            const uint32_t face[] = {ids[view.indices[triangle]], ids[view.indices[triangle + 1]], ids[view.indices[triangle + 2]]};
            if (face[0] == face[1] || face[1] == face[2] || face[2] == face[0])
            {
                continue;
            }
            for (auto edge = 0u; edge < 3; ++edge)
            {
                const auto a = face[edge], b = face[(edge + 1) % 3];
                auto& counts = edges[{std::min(a, b), std::max(a, b)}];
                ++counts[0]; counts[1] += a < b ? 1 : -1;
            }
        }
        for (const auto& [edge, counts] : edges)
        {
            if (counts[0] != 2 || counts[1] != 0)
            {
                std::cerr << "open solidify seam shape=" << shape << " edge count=" << counts[0] << " winding=" << counts[1] << '\n';
                assert(false);
            }
        }
    }
}
}
int main()
{
#if defined(_WIN32)
    _set_error_mode(_OUT_TO_STDERR);
#endif
    procedural_tests::shapesAndModifiers(); procedural_tests::animationAndIsolation();
    procedural_tests::pivotsAndBounds(); procedural_tests::pixels();
    procedural_tests::solidifyClosesSeams(); procedural_tests::contentSharing();
    std::cout << "Procedural resource contracts passed\n";
}
