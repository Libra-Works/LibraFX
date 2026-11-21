#pragma once
#include "liberafx/LbVfxRuntime.h"
#include "liberafx/LbRuntimeResult.h"
#include <array>
#include <vector>

namespace liberafx {
// Engine-independent, canonical effect-local geometry. Colours are linear.
// Static, procedural and skinned sources all submit the same evaluated data.
struct MeshEmissionVertex final {
    std::array<float, 3> position{}, normal{};
    std::array<float, 2> uv{};
    std::array<float, 4> color{1, 1, 1, 1};
};
struct MeshEmissionSample final {
    std::array<double, 3> position{}, normal{}, barycentric{};
    std::array<double, 2> uv{};
    std::array<double, 4> color{};
    std::uint32_t triangle = 0;
};

// Configure once per geometry revision, then sample without allocations.
// Selection weights are triangle areas in source space, never vertex counts.
// Degenerate faces are skipped; an entirely empty surface is rejected.
class LIBRA_RUNTIME_CORE_API MeshEmissionSurface final {
public:
    Result configure(const MeshEmissionVertex* vertices, std::size_t vertexCount,
        const std::uint32_t* indices, std::size_t indexCount) noexcept;
    // Three independent uniform values in [0,1). Failure leaves output intact.
    Result sample(double face, double u, double v, MeshEmissionSample& output) const noexcept;
    double area() const noexcept { return area_; }
    std::size_t triangleCount() const noexcept { return triangles_.size(); }
private:
    struct Triangle { std::array<std::uint32_t, 3> vertices; std::uint32_t source; double end; };
    std::vector<MeshEmissionVertex> vertices_;
    std::vector<Triangle> triangles_;
    double area_ = 0;
};
}
