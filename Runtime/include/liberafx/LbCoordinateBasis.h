#pragma once

#include "liberafx/LbVfxRuntime.h"
#include "liberafx/LbRuntimeResult.h"
#include <array>

namespace liberafx {

// Effects use metres, X right, Y up, Z forward. Each signed axis says where
// the corresponding effect axis points in the host. Colours and arbitrary
// numeric vectors are never implicitly treated as spatial values.
struct CoordinateBasis final {
    std::array<int, 3> axes{1, 2, 3};
    double unitsPerMetre = 1;
    static auto unity() noexcept -> CoordinateBasis { return {}; }
    static auto unreal() noexcept -> CoordinateBasis { return {{2, 3, 1}, 100}; }
    static auto godot() noexcept -> CoordinateBasis { return {{1, 2, -3}, 1}; }
};
enum class SpatialValueKind : unsigned {
    Position = 0, Vector = 1, Direction = 2, Normal = 3,
    AngularVector = 4, Transform = 5, CanonicalDrawTransform = 6
};
LIBRA_RUNTIME_CORE_API auto validCoordinateBasis(const CoordinateBasis& basis) noexcept -> bool;
// Row-major, column-vector matrices. Transform converts by conjugation;
// CanonicalDrawTransform converts a draw of canonical vertices by left multiplication.
// Vector includes units (velocity/acceleration/displacement); Direction does not.
// Failure leaves out unchanged. Source and destination may alias.
LIBRA_RUNTIME_CORE_API auto convertCoordinates(const CoordinateBasis& basis,
    SpatialValueKind kind, bool toHost, const double* source, std::size_t width,
    double* out) noexcept -> Result;
}
