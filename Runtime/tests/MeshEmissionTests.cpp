#include "liberafx/LbMeshEmission.h"
#include "liberafx/LbProceduralModel.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <cmath>
#include <limits>

using namespace liberafx;
int main() {
    // Two disconnected faces with a 1:4 area ratio and one degenerate face.
    auto vertices = std::array<MeshEmissionVertex, 6>{};
    vertices[1].position = {1,0,0}; vertices[2].position = {0,0,1};
    vertices[3].position = {10,0,0}; vertices[4].position = {12,0,0}; vertices[5].position = {10,0,2};
    vertices[1].uv = {1,0}; vertices[2].uv = {0,1};
    vertices[0].color = {1,0,0,1}; vertices[1].color = {0,1,0,1}; vertices[2].color = {0,0,1,1};
    auto indices = std::array<std::uint32_t, 9>{0,0,0, 0,1,2, 3,4,5};
    auto surface = MeshEmissionSurface{};
    assert(surface.configure(vertices.data(), vertices.size(), indices.data(), indices.size()) == Result::Ok);
    assert(surface.triangleCount() == 2 && surface.area() == 2.5);
    auto sample = MeshEmissionSample{};
    assert(surface.sample(0,.25,.5,sample) == Result::Ok);
    assert(sample.triangle == 1 && sample.position[0] == .25 && sample.position[2] == .25);
    assert(sample.uv[0] == .25 && sample.uv[1] == .25);
    assert(sample.color[0] == .5 && sample.color[1] == .25 && sample.color[2] == .25);
    assert(sample.normal[1] == -1); // Geometric normal fallback, preserving winding.
    auto counts = std::array<int, 3>{};
    auto mean = std::array<double, 3>{};
    for (auto i = 0; i < 10000; ++i) {
        assert(surface.sample((i+.5)/10000, (i+.5)/10000, ((i*7919)%10000+.5)/10000, sample) == Result::Ok);
        ++counts[sample.triangle];
        for (auto lane = 0u; lane < 3; ++lane) mean[lane] += sample.barycentric[lane] / 10000;
    }
    assert(counts[0] == 0 && counts[1] == 2000 && counts[2] == 8000);
    for (const auto value : mean) assert(std::abs(value - 1./3) < .002);
    const auto saved = sample.position;
    assert(surface.sample(1,0,0,sample) == Result::InvalidArgument && sample.position == saved);
    indices[1] = 100;
    assert(surface.configure(vertices.data(), vertices.size(), indices.data(), indices.size()) == Result::InvalidArgument);
    assert(surface.area() == 2.5 && surface.triangleCount() == 2); // Failed replacement is atomic.
    vertices[0].position[0] = std::numeric_limits<float>::quiet_NaN();
    assert(surface.configure(vertices.data(), vertices.size(), indices.data(), indices.size()) == Result::NonFinite);

    auto descriptor = LibraProceduralModelDescriptor{};
    descriptor.version = 1; descriptor.shape = LIBRA_SHAPE_SPHERE;
    descriptor.segmentsU = 24; descriptor.segmentsV = 12;
    descriptor.size[0] = descriptor.size[1] = descriptor.size[2] = 2;
    descriptor.sweepDegrees = 360; descriptor.turns = 1;
    auto model = static_cast<LibraProceduralModelInstance*>(nullptr);
    assert(libra_procedural_model_create(&descriptor,nullptr,0,&model) == LIBRA_OK);
    auto view = LibraProceduralModelView{};
    assert(libra_procedural_model_view(model,&view) == LIBRA_OK);
    auto sphere = std::vector<MeshEmissionVertex>(view.vertexCount);
    for (auto i = 0u; i < view.vertexCount; ++i) {
        for (auto lane = 0u; lane < 3; ++lane) {
            sphere[i].position[lane] = view.vertices[i].position[lane] - view.pivot[lane];
            sphere[i].normal[lane] = view.vertices[i].normal[lane];
        }
        for (auto lane = 0u; lane < 2; ++lane) sphere[i].uv[lane] = view.vertices[i].uv[lane];
        for (auto lane = 0u; lane < 4; ++lane) sphere[i].color[lane] = view.vertices[i].color[lane];
    }
    assert(surface.configure(sphere.data(),sphere.size(),view.indices,view.indexCount) == Result::Ok);
    libra_procedural_model_release(model); // Surface owns its data.
    assert(surface.sample(.42,.37,.61,sample) == Result::Ok);
    const auto length = std::hypot(sample.normal[0],sample.normal[1],sample.normal[2]);
    assert(std::abs(length-1) < 1e-12);
    assert(sample.position[0]*sample.normal[0]+sample.position[1]*sample.normal[1]+sample.position[2]*sample.normal[2] > .8);
}
