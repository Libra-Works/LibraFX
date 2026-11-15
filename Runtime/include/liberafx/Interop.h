#ifndef LIBERAFX_INTEROP_H
#define LIBERAFX_INTEROP_H

#include <stdint.h>

#if defined(_WIN32)
#define LIBRA_INTEROP_CALL __cdecl
#if defined(LIBRA_INTEROP_BUILD)
#define LIBRA_INTEROP_API __declspec(dllexport)
#else
#define LIBRA_INTEROP_API __declspec(dllimport)
#endif
#else
#define LIBRA_INTEROP_CALL
#define LIBRA_INTEROP_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef uint64_t LibraProgram;
typedef uint64_t LibraInstance;
typedef uint32_t LibraBinding;
typedef int32_t LibraResult;
enum {
    LIBRA_OK = 0, LIBRA_INVALID_ARGUMENT = 1, LIBRA_INVALID_BINARY = 2,
    LIBRA_UNSUPPORTED_BINARY = 3, LIBRA_INSUFFICIENT_WORK = 4,
    LIBRA_BUSY = 5, LIBRA_QUEUE_REJECTED = 6, LIBRA_NOT_READY = 7,
    LIBRA_NODE_NOT_FOUND = 8, LIBRA_DIVIDE_BY_ZERO = 9, LIBRA_NON_FINITE = 10,
    LIBRA_INVALID_VALUE_TYPE = 11, LIBRA_UNKNOWN_CUSTOM_NODE = 12,
    LIBRA_INVALID_HANDLE = 13, LIBRA_INSTRUCTION_BUDGET_EXCEEDED = 14,
    LIBRA_OUT_OF_MEMORY = 15, LIBRA_INTERNAL_ERROR = 16
};

typedef struct LibraValue {
    double x, y, z, w;
    uint32_t componentCount;
    uint32_t reserved;
} LibraValue;

typedef struct LibraAtlasFrame {
    uint32_t frameIndex;
    float u, v, width, height;
} LibraAtlasFrame;

typedef struct LibraSpriteVertex {
    float x, y, z;
    float u, v;
    float r, g, b, a;
} LibraSpriteVertex;

/* All lengths are element counts except byteCount. Pointers are borrowed only
   during the call, must address valid storage, and must not alias output arrays.
   IDs are 16 bytes in compiler/Guid big-endian order, not Windows GUID layout.
   Calls serialize internally; evaluation executes synchronously on the caller.
   Buffers are allocated only during create. Bind tokens once before frame loops.
   Tokens are instance-local; never use them with another instance. */
LIBRA_INTEROP_API uint32_t LIBRA_INTEROP_CALL libra_abi_version(void);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_program_create(
    const uint8_t* bytes, uint64_t byteCount, LibraProgram* outProgram);
/* Instances retain program ownership even after its public handle is released. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_program_release(LibraProgram program);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_instance_create(
    LibraProgram program, LibraInstance* outInstance);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_instance_release(LibraInstance instance);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_bind_value(
    LibraInstance instance, const uint8_t nodeId[16], LibraBinding* outBinding);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_bind_parameter(
    LibraInstance instance, const uint8_t nodeId[16], uint32_t inputSlot,
    uint32_t component, LibraBinding* outBinding);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_parameter_get(
    LibraInstance instance, LibraBinding binding, double* outValue);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_parameter_set(
    LibraInstance instance, LibraBinding binding, double value);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_values_read(
    LibraInstance instance, const LibraBinding* bindings, uint32_t count, LibraValue* outValues);
/* Validates all tokens before updating. On failure output is unspecified; do not
   render stale/partial data. Zero count permits update without reading outputs. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_evaluate(
    LibraInstance instance, double time, const LibraBinding* bindings,
    uint32_t count, LibraValue* outValues);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_sample_sprite_atlas(
    double frame, uint32_t columns, uint32_t rows, LibraAtlasFrame* outFrame);
/* Top-left-origin UVs; four vertices in TL, TR, BR, BL order. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_make_sprite_quad(
    const LibraAtlasFrame* frame, float width, float height, LibraSpriteVertex outVertices[4]);

#ifdef __cplusplus
}
#endif
#endif
