#ifndef LIBERAFX_PROCEDURAL_MODEL_H
#define LIBERAFX_PROCEDURAL_MODEL_H

#include "liberafx/LbInterop.h"

#if !defined(LIBRA_PROCEDURAL_API) && defined(LIBRA_PROCEDURAL_STATIC)
#define LIBRA_PROCEDURAL_API
#elif !defined(LIBRA_PROCEDURAL_API)
#define LIBRA_PROCEDURAL_API LIBRA_INTEROP_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum LibraProceduralShape {
    LIBRA_SHAPE_RIBBON, LIBRA_SHAPE_SLASH, LIBRA_SHAPE_SPIRAL, LIBRA_SHAPE_WAVE,
    LIBRA_SHAPE_RING, LIBRA_SHAPE_ARC, LIBRA_SHAPE_DISK, LIBRA_SHAPE_TORUS,
    LIBRA_SHAPE_CYLINDER, LIBRA_SHAPE_CONE, LIBRA_SHAPE_FLARE, LIBRA_SHAPE_SPHERE,
    LIBRA_SHAPE_HEMISPHERE, LIBRA_SHAPE_CAPSULE, LIBRA_SHAPE_PLANE, LIBRA_SHAPE_BOX,
    LIBRA_SHAPE_CROSS, LIBRA_SHAPE_SPIKE, LIBRA_SHAPE_LEAF, LIBRA_SHAPE_SWIRL,
    LIBRA_SHAPE_FLAME_RING, LIBRA_SHAPE_DOUBLE_HELIX, LIBRA_SHAPE_COIL, LIBRA_SHAPE_KNOT,
    LIBRA_SHAPE_COUNT
};

enum LibraProceduralModifierKind {
    LIBRA_MOD_TWIST, LIBRA_MOD_BEND, LIBRA_MOD_TAPER, LIBRA_MOD_WAVE,
    LIBRA_MOD_SWIRL, LIBRA_MOD_NOISE, LIBRA_MOD_ROUND, LIBRA_MOD_INFLATE,
    LIBRA_MOD_TRANSFORM, LIBRA_MOD_MIRROR, LIBRA_MOD_ARRAY, LIBRA_MOD_SUBDIVIDE,
    LIBRA_MOD_SOLIDIFY, LIBRA_MOD_COUNT
};

enum LibraProceduralPivot {
    LIBRA_PIVOT_CENTER, LIBRA_PIVOT_BASE, LIBRA_PIVOT_TIP, LIBRA_PIVOT_ORIGIN,
    LIBRA_PIVOT_CUSTOM, LIBRA_PIVOT_START, LIBRA_PIVOT_END, LIBRA_PIVOT_VERTEX
};

enum {
    LIBRA_PROCEDURAL_VERSION = 1,
    LIBRA_PROCEDURAL_MAX_MODIFIERS = 16,
    LIBRA_PROCEDURAL_MAX_VERTICES = 65536,
    LIBRA_PROCEDURAL_MAX_TRIANGLES = 131072,
    LIBRA_PROCEDURAL_MAX_ANIMATED_VERTICES = 16384,
    LIBRA_PROCEDURAL_MAX_ANIMATED_TRIANGLES = 32768,
    LIBRA_PROCEDURAL_MAX_COPIES = 64,
    LIBRA_PROCEDURAL_MAX_SUBDIVISION = 3,
    LIBRA_PROCEDURAL_MAX_WORK_BYTES = 32 * 1024 * 1024,
    LIBRA_PROCEDURAL_CAPPED = 1,
    LIBRA_PROCEDURAL_TANGENTS = 2,
    LIBRA_PROCEDURAL_ANIMATED = 4,
    LIBRA_PROCEDURAL_ANIMATED_BASE = 8,
    LIBRA_MOD_ENABLED = 1,
    LIBRA_MOD_CUSTOM_PIVOT = 2,
    LIBRA_MOD_RADIAL_ARRAY = 4,
    LIBRA_MOD_MIRROR_KEEP_ORIGINAL = 8,
    LIBRA_MOD_ANIMATED = 16,
    LIBRA_PROCEDURAL_CHANGED_VERTICES = 1,
    LIBRA_PROCEDURAL_CHANGED_INDICES = 2,
    LIBRA_PROCEDURAL_CHANGED_BOUNDS = 4,
    LIBRA_PROCEDURAL_CHANGED_PIVOT = 8
};

#pragma pack(push, 8)
/* Canonical local coordinates are Y-up. Angles in recipes are degrees.
   Topology fields (shape, segment counts, flags, modifier kind/count/flags)
   cannot change through evaluate: create a new definition for those edits.
   ANIMATED_BASE and per-modifier ANIMATED mark continuous input owners.
   Other recipe values are immutable until a new create. Pure draw-pivot
   changes may be evaluated without rewriting unaffected geometry. */
typedef struct LibraProceduralModelDescriptor {
    uint32_t version, shape, segmentsU, segmentsV;
    float size[3];
    float innerRadius, sweepDegrees, turns, tubeRadius;
    uint32_t pivotMode, flags;
    float pivot[3];
    uint32_t maxVertices, maxTriangles, pivotVertex, reserved;
} LibraProceduralModelDescriptor;

/* values[0..11] are kind-specific, values[12..14] are the optional local
   modifier pivot, and values[15] is reserved (zero). axis: 0=X, 1=Y, 2=Z.
   count is the fixed copy/subdivision/octave count. seed is an exact UInt32.
   Twist/Bend: angle. Taper: end scale. Wave: amplitude/frequency/phase/speed.
   Swirl: angle/radius. Noise: amplitude/frequency/phase/speed/roughness.
   Round: blend/radius. Inflate: distance.
   Transform/Array: translation.xyz, rotation.xyz, scale.xyz.
   Solidify: thickness/offset. Disabled modifiers retain their authored order. */
typedef struct LibraProceduralModelModifier {
    uint32_t kind, axis, flags, count;
    uint32_t seed, reserved0, reserved1, reserved2;
    float values[16];
} LibraProceduralModelModifier;

typedef struct LibraProceduralModelVertex {
    float position[3], normal[3], tangent[4], uv[2], uv2[2], color[4];
} LibraProceduralModelVertex;

typedef struct LibraProceduralModelCounts {
    uint32_t vertices, indices;
    uint64_t retainedBytes;
} LibraProceduralModelCounts;

typedef struct LibraProceduralModelView {
    const LibraProceduralModelVertex* vertices;
    const uint32_t* indices;
    uint32_t vertexCount, indexCount;
    uint64_t definitionId, vertexRevision, indexRevision;
    float boundsMin[3], boundsMax[3], pivot[3];
} LibraProceduralModelView;

typedef struct LibraProceduralModelStats {
    uint64_t topologyBuilds, vertexEvaluations, skippedEvaluations;
    uint64_t capacityGrowths, retainedBytes, sharedDefinitionBytes;
    double lastEvaluationMicroseconds, maximumEvaluationMicroseconds;
} LibraProceduralModelStats;
#pragma pack(pop)

typedef struct LibraProceduralModelInstance LibraProceduralModelInstance;

/* Validation/prediction has no engine dependency and never allocates geometry.
   Counts are safe upper bounds (UV seams can reduce physical solidify borders).
   It rejects excessive intermediate growth as well as the final result. */
LIBRA_PROCEDURAL_API LibraResult LIBRA_INTEROP_CALL libra_procedural_model_predict(
    const LibraProceduralModelDescriptor* descriptor,
    const LibraProceduralModelModifier* modifiers, uint32_t modifierCount,
    LibraProceduralModelCounts* counts);

LIBRA_PROCEDURAL_API LibraResult LIBRA_INTEROP_CALL libra_procedural_model_create(
    const LibraProceduralModelDescriptor* descriptor,
    const LibraProceduralModelModifier* modifiers, uint32_t modifierCount,
    LibraProceduralModelInstance** instance);

/* Clones share immutable definitions/static data. Mutable evaluation state is
   private to the clone, so stopping one scope cannot invalidate another. */
LIBRA_PROCEDURAL_API LibraResult LIBRA_INTEROP_CALL libra_procedural_model_clone(
    const LibraProceduralModelInstance* source, LibraProceduralModelInstance** instance);

/* No topology/buffer growth on successful warm evaluation. Time is finite
   effect-local seconds, preserving pause/seek behavior. changed is a bitmask.
   Failure is atomic: previously published geometry remains valid. */
LIBRA_PROCEDURAL_API LibraResult LIBRA_INTEROP_CALL libra_procedural_model_evaluate(
    LibraProceduralModelInstance* instance,
    const LibraProceduralModelDescriptor* descriptor,
    const LibraProceduralModelModifier* modifiers, uint32_t modifierCount,
    double time, uint32_t* changed);

/* Borrowed arrays are valid until this instance's next evaluate/release.
   The host must consume/copy them before submitting asynchronous engine work.
   Positions remain in source coordinates; apply -view.pivot exactly once in
   the draw transform. Do not subtract it again from the uploaded vertices. */
LIBRA_PROCEDURAL_API LibraResult LIBRA_INTEROP_CALL libra_procedural_model_view(
    const LibraProceduralModelInstance* instance, LibraProceduralModelView* view);

LIBRA_PROCEDURAL_API LibraResult LIBRA_INTEROP_CALL libra_procedural_model_stats(
    const LibraProceduralModelInstance* instance, LibraProceduralModelStats* stats);

/* Blittable-array copies for managed adapters that cannot borrow native arrays.
   No recomputation/allocation occurs. Insufficient capacity changes no output. */
LIBRA_PROCEDURAL_API LibraResult LIBRA_INTEROP_CALL libra_procedural_model_copy_vertices(
    const LibraProceduralModelInstance* instance,
    LibraProceduralModelVertex* vertices, uint32_t vertexCapacity);
LIBRA_PROCEDURAL_API LibraResult LIBRA_INTEROP_CALL libra_procedural_model_copy_indices(
    const LibraProceduralModelInstance* instance, uint32_t* indices, uint32_t indexCapacity);

LIBRA_PROCEDURAL_API void LIBRA_INTEROP_CALL libra_procedural_model_release(
    LibraProceduralModelInstance* instance);

#ifdef __cplusplus
}
static_assert(sizeof(LibraProceduralModelDescriptor) == 80, "Procedural model descriptor ABI");
static_assert(sizeof(LibraProceduralModelModifier) == 96, "Procedural modifier ABI");
static_assert(sizeof(LibraProceduralModelVertex) == 72, "Procedural vertex ABI");
#endif
#endif
