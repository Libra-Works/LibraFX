#include "liberafx/LbMeshEmission.h"
#include <algorithm>
#include <cmath>
#include <new>

namespace liberafx {
namespace {
auto unit(std::array<double, 3>& value) noexcept -> bool {
    const auto length = std::hypot(value[0], value[1], value[2]);
    if (!(length > 0) || !std::isfinite(length)) return false;
    for (auto& lane : value) lane /= length;
    return true;
}
auto cross(const MeshEmissionVertex& a, const MeshEmissionVertex& b,
    const MeshEmissionVertex& c) noexcept -> std::array<double, 3> {
    auto ab = std::array<double, 3>{}, ac = ab;
    for (auto i = 0u; i < 3; ++i) { ab[i] = double(b.position[i]) - a.position[i]; ac[i] = double(c.position[i]) - a.position[i]; }
    return {ab[1]*ac[2]-ab[2]*ac[1], ab[2]*ac[0]-ab[0]*ac[2], ab[0]*ac[1]-ab[1]*ac[0]};
}
}
auto MeshEmissionSurface::configure(const MeshEmissionVertex* vertices, std::size_t vertexCount,
    const std::uint32_t* indices, std::size_t indexCount) noexcept -> Result {
    if (!vertices || !indices || vertexCount == 0 || vertexCount > 1000000 ||
        indexCount == 0 || indexCount % 3 != 0 || indexCount > 6000000) return Result::InvalidArgument;
    for (auto i = std::size_t{}; i < vertexCount; ++i) {
        const auto& vertex = vertices[i];
        for (const auto lane : vertex.position) if (!std::isfinite(lane)) return Result::NonFinite;
        for (const auto lane : vertex.normal) if (!std::isfinite(lane)) return Result::NonFinite;
        for (const auto lane : vertex.uv) if (!std::isfinite(lane)) return Result::NonFinite;
        for (const auto lane : vertex.color) if (!std::isfinite(lane)) return Result::NonFinite;
    }
    try {
        auto candidate = MeshEmissionSurface{};
        candidate.triangles_.reserve(indexCount / 3);
        for (auto i = std::size_t{}; i < indexCount; i += 3) {
            if (indices[i] >= vertexCount || indices[i+1] >= vertexCount || indices[i+2] >= vertexCount) return Result::InvalidArgument;
            const auto normal = cross(vertices[indices[i]], vertices[indices[i+1]], vertices[indices[i+2]]);
            const auto area = .5 * std::hypot(normal[0], normal[1], normal[2]);
            if (area == 0) continue;
            candidate.area_ += area;
            if (!std::isfinite(candidate.area_)) return Result::NonFinite;
            candidate.triangles_.push_back({{indices[i], indices[i+1], indices[i+2]}, static_cast<std::uint32_t>(i / 3), candidate.area_});
        }
        if (candidate.triangles_.empty()) return Result::InvalidArgument;
        candidate.vertices_.assign(vertices, vertices + vertexCount);
        *this = std::move(candidate);
        return Result::Ok;
    } catch (const std::bad_alloc&) { return Result::InsufficientWork; }
}
auto MeshEmissionSurface::sample(double face, double u, double v, MeshEmissionSample& output) const noexcept -> Result {
    for (const auto value : {face, u, v})
        if (!std::isfinite(value) || value < 0 || value >= 1) return Result::InvalidArgument;
    if (triangles_.empty()) return Result::NotReady;
    const auto target = std::min(face * area_, std::nextafter(area_, 0.0));
    const auto triangle = std::upper_bound(triangles_.begin(), triangles_.end(), target,
        [](double value, const Triangle& item) { return value < item.end; });
    auto result = MeshEmissionSample{};
    const auto root = std::sqrt(u);
    result.barycentric = {1 - root, root * (1 - v), root * v};
    result.triangle = triangle->source;
    for (auto corner = 0u; corner < 3; ++corner) {
        const auto& vertex = vertices_[triangle->vertices[corner]];
        const auto weight = result.barycentric[corner];
        for (auto lane = 0u; lane < 3; ++lane) {
            result.position[lane] += vertex.position[lane] * weight;
            result.normal[lane] += vertex.normal[lane] * weight;
        }
        for (auto lane = 0u; lane < 2; ++lane) result.uv[lane] += vertex.uv[lane] * weight;
        for (auto lane = 0u; lane < 4; ++lane) result.color[lane] += vertex.color[lane] * weight;
    }
    if (!unit(result.normal)) {
        result.normal = cross(vertices_[triangle->vertices[0]], vertices_[triangle->vertices[1]], vertices_[triangle->vertices[2]]);
        if (!unit(result.normal)) return Result::InvalidArgument;
    }
    output = result;
    return Result::Ok;
}
}
