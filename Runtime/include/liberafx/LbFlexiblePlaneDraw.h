#ifndef LIBERAFX_FLEXIBLE_PLANE_DRAW_H
#define LIBERAFX_FLEXIBLE_PLANE_DRAW_H

#include "liberafx/LbInterop.h"

#if !defined(LIBRA_FLEXIBLE_API) && defined(LIBRA_FLEXIBLE_STATIC)
#define LIBRA_FLEXIBLE_API
#elif !defined(LIBRA_FLEXIBLE_API)
#define LIBRA_FLEXIBLE_API LIBRA_INTEROP_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* All positions, widths, up and camera are in one caller-selected coordinate
   system. Width is the full width. Colors are linear, unpremultiplied RGBA. */
#pragma pack(push, 8)
typedef struct LibraFlexiblePoint {
    float x, y, z, width;
    float r, g, b, a;
} LibraFlexiblePoint;

typedef struct LibraFlexiblePlaneOptions {
    uint32_t facing; /* 0: fixed up, 1: camera position */
    uint32_t uvMode; /* 0: stretch, 1: repeat */
    float upX, upY, upZ;
    float cameraX, cameraY, cameraZ;
    float repeatLength;
} LibraFlexiblePlaneOptions;

typedef struct LibraFlexiblePlaneCounts {
    uint32_t vertices, indices;
} LibraFlexiblePlaneCounts;

typedef struct LibraFlexibleTimedPoint {
    double time;
    LibraFlexiblePoint point;
} LibraFlexibleTimedPoint;

/* Zero-initialize or reset before first use and whenever an instance is reused.
   The caller owns both state and the storage passed to every update/copy. */
typedef struct LibraFlexibleHistory {
    uint32_t count;
    uint32_t reserved;
    double lastTime;
} LibraFlexibleHistory;
#pragma pack(pop)

enum { LIBRA_FLEXIBLE_MAX_POINTS = 4096 };

/* Duplicate consecutive positions are collapsed. Fewer than two distinct
   positions is a successful empty draw. Counts are returned on insufficient
   work; vertex/index storage is never changed on any failure. No aliasing. */
LIBRA_FLEXIBLE_API LibraResult LIBRA_INTEROP_CALL libra_flexible_plane_generate(
    const LibraFlexiblePoint* points, uint32_t pointCount,
    const LibraFlexiblePlaneOptions* options, LibraSpriteVertex* vertices,
    uint32_t vertexCapacity, uint32_t* indices, uint32_t indexCapacity,
    LibraFlexiblePlaneCounts* counts);

LIBRA_FLEXIBLE_API void LIBRA_INTEROP_CALL libra_flexible_history_reset(
    LibraFlexibleHistory* history);

/* maxAge is finite and > 0 seconds. Times are finite seconds. Older than
   maxAge is removed, full storage drops oldest, equal time replaces newest,
   backward time resets then inserts. Storage is contiguous oldest first. */
LIBRA_FLEXIBLE_API LibraResult LIBRA_INTEROP_CALL libra_flexible_history_update(
    LibraFlexibleHistory* history, LibraFlexibleTimedPoint* storage,
    uint32_t capacity, double time, double maxAge, const LibraFlexiblePoint* point);

LIBRA_FLEXIBLE_API LibraResult LIBRA_INTEROP_CALL libra_flexible_history_copy(
    const LibraFlexibleHistory* history, const LibraFlexibleTimedPoint* storage,
    uint32_t capacity, LibraFlexiblePoint* points, uint32_t pointCapacity,
    uint32_t* count);

#ifdef __cplusplus
}
#endif
#endif
