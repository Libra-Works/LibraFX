#pragma once

#include "liberafx/LbProceduralModel.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <utility>
#include <vector>

namespace liberafx::procedural_model {

constexpr float pi = 3.14159265358979323846f;
constexpr float degrees = pi / 180.0f;
constexpr float maximumCoordinate = 1000000.0f;

inline auto sameBaseValues(const LibraProceduralModelDescriptor& a, const LibraProceduralModelDescriptor& b) -> bool
{
    return std::memcmp(a.size, b.size, sizeof(a.size)) == 0 && a.innerRadius == b.innerRadius &&
        a.sweepDegrees == b.sweepDegrees && a.turns == b.turns && a.tubeRadius == b.tubeRadius;
}

struct Vec3 {
    float x = 0, y = 0, z = 0;
    float& operator[](std::size_t index) { return index == 0 ? x : index == 1 ? y : z; }
    float operator[](std::size_t index) const { return index == 0 ? x : index == 1 ? y : z; }
};

inline auto operator+(Vec3 a, Vec3 b) -> Vec3 { return {a.x + b.x, a.y + b.y, a.z + b.z}; }
inline auto operator-(Vec3 a, Vec3 b) -> Vec3 { return {a.x - b.x, a.y - b.y, a.z - b.z}; }
inline auto operator*(Vec3 a, float b) -> Vec3 { return {a.x * b, a.y * b, a.z * b}; }
inline auto operator/(Vec3 a, float b) -> Vec3 { return a * (1.0f / b); }
inline auto dot(Vec3 a, Vec3 b) -> float { return a.x * b.x + a.y * b.y + a.z * b.z; }
inline auto oddParity(std::uint32_t value) -> bool
{
    value ^= value >> 16;
    value ^= value >> 8;
    value ^= value >> 4;
    return ((0x6996u >> (value & 15u)) & 1u) != 0;
}
inline auto cross(Vec3 a, Vec3 b) -> Vec3
{
    return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x};
}
inline auto normalized(Vec3 value, Vec3 fallback = {0, 1, 0}) -> Vec3
{
    const auto square = dot(value, value);
    if (!std::isfinite(square) || square <= 1.e-20f)
    {
        return fallback;
    }
    return value / std::sqrt(square);
}
inline auto finite(Vec3 value) -> bool
{
    return std::isfinite(value.x) && std::isfinite(value.y) && std::isfinite(value.z) &&
        std::abs(value.x) <= maximumCoordinate && std::abs(value.y) <= maximumCoordinate &&
        std::abs(value.z) <= maximumCoordinate;
}
inline auto position(const LibraProceduralModelVertex& vertex) -> Vec3
{
    return {vertex.position[0], vertex.position[1], vertex.position[2]};
}
inline void setPosition(LibraProceduralModelVertex& vertex, Vec3 value)
{
    vertex.position[0] = value.x;
    vertex.position[1] = value.y;
    vertex.position[2] = value.z;
}
inline auto normal(const LibraProceduralModelVertex& vertex) -> Vec3
{
    return {vertex.normal[0], vertex.normal[1], vertex.normal[2]};
}

struct Bounds {
    Vec3 minimum{std::numeric_limits<float>::max(), std::numeric_limits<float>::max(), std::numeric_limits<float>::max()};
    Vec3 maximum{-std::numeric_limits<float>::max(), -std::numeric_limits<float>::max(), -std::numeric_limits<float>::max()};
    void add(Vec3 value)
    {
        for (auto axis = 0u; axis < 3; ++axis)
        {
            minimum[axis] = std::min(minimum[axis], value[axis]);
            maximum[axis] = std::max(maximum[axis], value[axis]);
        }
    }
    auto center() const -> Vec3 { return (minimum + maximum) * .5f; }
};

// Connectivity and smoothing identity are separate: a UV seam must not become
// an open solidify boundary, and a box/cap seam must retain its hard normal.
struct Topology {
    std::uint32_t vertexCount = 0, weldCount = 0, normalGroupCount = 0;
    std::vector<std::uint32_t> indices, welds, normalGroups, triangleParity;
};

struct SourceSample {
    float u = 0, v = 0;
    std::uint32_t patch = 0;
};

struct Stage {
    std::uint32_t modifierIndex = 0;
    std::uint32_t inputParity = 0, outputParity = 0;
    std::shared_ptr<const Topology> input, output;
    // Subdivide appends midpoint vertices in this order. Solidify appends four
    // side-wall vertices per physical boundary edge; other stages use copy maps.
    std::vector<std::array<std::uint32_t, 2>> edges;
    std::vector<std::uint32_t> edgeParity;
};

struct Plan {
    std::shared_ptr<const Topology> base;
    std::vector<SourceSample> samples;
    std::vector<Stage> stages;
    std::uint32_t maximumVertices = 0, maximumIndices = 0;
    std::uint32_t finalParity = 0;
    std::uint64_t retainedBytes = 0;
};

struct Geometry {
    std::vector<LibraProceduralModelVertex> vertices;
    std::vector<std::uint32_t> indices;
    Bounds bounds;
    Vec3 pivot;
    const Topology* indexTopology = nullptr;
    std::uint32_t indexSignature = 0;
};

struct Definition {
    std::uint64_t id = 0;
    LibraProceduralModelDescriptor descriptor{};
    std::vector<LibraProceduralModelModifier> modifiers;
    std::shared_ptr<const Plan> plan;
    std::shared_ptr<const Geometry> baseGeometry, prefix;
    std::shared_ptr<const Geometry> initial;
    std::size_t staticPrefix = 0;
    bool usesTime = false;
};

struct EvaluationWork {
    std::array<std::vector<LibraProceduralModelVertex>, 2> vertices;
    std::vector<Vec3> normalSums, tangentSums, bitangentSums;
};

auto validate(const LibraProceduralModelDescriptor& descriptor,
    const LibraProceduralModelModifier* modifiers, std::uint32_t count) -> LibraResult;
auto predict(const LibraProceduralModelDescriptor& descriptor,
    const LibraProceduralModelModifier* modifiers, std::uint32_t count,
    LibraProceduralModelCounts& counts) -> LibraResult;
auto sameTopology(const LibraProceduralModelDescriptor& left,
    const LibraProceduralModelDescriptor& right,
    const std::vector<LibraProceduralModelModifier>& original,
    const LibraProceduralModelModifier* modifiers, std::uint32_t count) -> bool;
auto buildBase(const LibraProceduralModelDescriptor& descriptor, Plan& plan) -> LibraResult;
auto basePosition(const LibraProceduralModelDescriptor& descriptor, const SourceSample& sample) -> Vec3;
auto compileStages(const LibraProceduralModelModifier* modifiers, std::uint32_t count, Plan& plan) -> LibraResult;
auto resolvePivot(const LibraProceduralModelDescriptor& descriptor,
    const std::vector<LibraProceduralModelVertex>& vertices, const Bounds& bounds, Vec3& pivot) -> LibraResult;
auto evaluate(const Definition& definition, const LibraProceduralModelDescriptor& descriptor,
    const LibraProceduralModelModifier* modifiers, double time,
    EvaluationWork& work, Geometry& result,
    std::size_t stageLimit = std::numeric_limits<std::size_t>::max()) -> LibraResult;
auto generateBase(const LibraProceduralModelDescriptor& descriptor, const Plan& plan,
    EvaluationWork& work, Geometry& result) -> LibraResult;
void rebuildNormals(const Topology& topology, std::uint32_t activeSigns, std::uint32_t globalParity,
    std::vector<LibraProceduralModelVertex>& vertices, EvaluationWork& work, bool tangents);
auto stageParity(const LibraProceduralModelModifier* modifiers, std::uint32_t count) -> std::uint32_t;
auto allocateWork(const Plan& plan, EvaluationWork& work, Geometry& result) -> LibraResult;

}

struct LibraProceduralModelInstance {
    std::shared_ptr<const liberafx::procedural_model::Definition> definition;
    std::shared_ptr<const liberafx::procedural_model::Geometry> sharedGeometry;
    liberafx::procedural_model::Geometry published, staging;
    liberafx::procedural_model::EvaluationWork work;
    LibraProceduralModelDescriptor previousDescriptor{};
    std::vector<LibraProceduralModelModifier> previousModifiers;
    double previousTime = 0;
    std::uint64_t vertexRevision = 1, indexRevision = 1;
    LibraProceduralModelStats stats{};
    liberafx::procedural_model::Vec3 resolvedPivot;
    bool ownsGeometry = false;
};
