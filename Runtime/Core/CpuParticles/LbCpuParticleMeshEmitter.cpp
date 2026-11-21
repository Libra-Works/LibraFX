#include "LbCpuParticlesInternal.h"

namespace liberafx::cpu_particle_detail {
auto System::refreshEmitter(double time) -> Result {
    if (!emitterModel) return Result::Ok;
    auto changed = std::uint32_t{};
    auto status = libra_procedural_model_evaluate(emitterModel, &emitterDefinition.model,
        emitterDefinition.modifiers.data(), static_cast<std::uint32_t>(emitterDefinition.modifiers.size()), time, &changed);
    if (status != LIBRA_OK) return static_cast<Result>(status);
    auto view = LibraProceduralModelView{};
    status = libra_procedural_model_view(emitterModel, &view);
    if (status != LIBRA_OK) return static_cast<Result>(status);
    if (emitterSurface.triangleCount() && view.vertexRevision == emitterVertexRevision &&
        std::equal(emitterPivot.begin(), emitterPivot.end(), view.pivot)) return Result::Ok;
    auto vertices = std::vector<MeshEmissionVertex>(view.vertexCount);
    for (auto i = 0u; i < view.vertexCount; ++i) {
        const auto& source = view.vertices[i];
        for (auto lane = 0u; lane < 3; ++lane) {
            vertices[i].position[lane] = source.position[lane] - view.pivot[lane];
            vertices[i].normal[lane] = source.normal[lane];
        }
        std::copy_n(source.uv, 2, vertices[i].uv.begin());
        std::copy_n(source.color, 4, vertices[i].color.begin());
    }
    const auto result = emitterSurface.configure(vertices.data(), vertices.size(), view.indices, view.indexCount);
    if (result == Result::Ok) {
        emitterVertexRevision = view.vertexRevision;
        std::copy_n(view.pivot, 3, emitterPivot.begin());
    }
    return result;
}
}
