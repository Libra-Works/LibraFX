#ifndef LIBERAFX_PROCEDURAL_PIXEL_H
#define LIBERAFX_PROCEDURAL_PIXEL_H

#include "liberafx/LbProceduralModel.h"

#ifdef __cplusplus
extern "C" {
#endif

enum LibraProceduralPixelKind {
    LIBRA_PIXEL_UV, LIBRA_PIXEL_CONSTANT, LIBRA_PIXEL_TIME, LIBRA_PIXEL_TRANSFORM_UV,
    LIBRA_PIXEL_LINEAR, LIBRA_PIXEL_RADIAL, LIBRA_PIXEL_RING, LIBRA_PIXEL_STRIPES,
    LIBRA_PIXEL_CHECKER, LIBRA_PIXEL_VALUE_NOISE, LIBRA_PIXEL_GRADIENT_NOISE,
    LIBRA_PIXEL_CELLULAR, LIBRA_PIXEL_CELL_EDGES, LIBRA_PIXEL_FRACTAL,
    LIBRA_PIXEL_RIDGED, LIBRA_PIXEL_CURL, LIBRA_PIXEL_WARP_UV, LIBRA_PIXEL_ADD,
    LIBRA_PIXEL_MULTIPLY, LIBRA_PIXEL_SUBTRACT, LIBRA_PIXEL_MIN, LIBRA_PIXEL_MAX,
    LIBRA_PIXEL_LERP, LIBRA_PIXEL_POWER, LIBRA_PIXEL_SATURATE, LIBRA_PIXEL_INVERT,
    LIBRA_PIXEL_SMOOTHSTEP, LIBRA_PIXEL_COLOR_RAMP, LIBRA_PIXEL_CHANNEL,
    LIBRA_PIXEL_ALPHA_MASK, LIBRA_PIXEL_TEXTURE_SAMPLE, LIBRA_PIXEL_OUTPUT,
    LIBRA_PIXEL_NORMAL_MAP,
    LIBRA_PIXEL_KIND_COUNT
};

enum {
    LIBRA_PIXEL_VERSION = 1,
    LIBRA_PIXEL_MAX_OPERATIONS = 32,
    LIBRA_PIXEL_MAX_OCTAVES = 8,
    LIBRA_PIXEL_MAX_TEXTURES = 4,
    LIBRA_PIXEL_MAX_RESOLUTION = 1024,
    LIBRA_PIXEL_DOMAIN_TEXTURE = 0,
    LIBRA_PIXEL_DOMAIN_MATERIAL = 1
};

#pragma pack(push, 8)
/* A/B are zero (the incoming UV register) or one-based earlier op registers.
   Bounded opcodes select trusted shader behavior, never delivered shader text.
   values are three Float4 vectors, shared by HLSL, GLSL and the CPU oracle. */
typedef struct LibraProceduralPixelOperation {
    uint32_t kind, inputA, inputB, seed;
    float values[12];
} LibraProceduralPixelOperation;

typedef struct LibraProceduralPixelProgram {
    uint32_t version, operationCount, outputRegister, domain;
} LibraProceduralPixelProgram;
#pragma pack(pop)

/* CPU sampling is a numerical reference/explicit preview path, not normal
   animated texture playback. Animated Texture2D evaluation belongs on GPU.
   Material-only sample ops use a caller-supplied image sampler. */
typedef LibraResult (LIBRA_INTEROP_CALL *LibraProceduralPixelSampleTexture)(
    void* context, uint32_t slot, float u, float v, float rgba[4]);

LIBRA_PROCEDURAL_API LibraResult LIBRA_INTEROP_CALL libra_procedural_pixel_validate(
    const LibraProceduralPixelProgram* program,
    const LibraProceduralPixelOperation* operations, uint32_t operationCount,
    uint32_t* usesTime);

LIBRA_PROCEDURAL_API LibraResult LIBRA_INTEROP_CALL libra_procedural_pixel_sample(
    const LibraProceduralPixelProgram* program,
    const LibraProceduralPixelOperation* operations, uint32_t operationCount,
    float u, float v, double time, LibraProceduralPixelSampleTexture sampleTexture,
    void* context, float rgba[4]);

#ifdef __cplusplus
}
static_assert(sizeof(LibraProceduralPixelOperation) == 64, "Procedural pixel operation ABI");
static_assert(sizeof(LibraProceduralPixelProgram) == 16, "Procedural pixel program ABI");
#endif
#endif
