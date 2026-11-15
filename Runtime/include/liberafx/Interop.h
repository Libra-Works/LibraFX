#ifndef LIBERAFX_INTEROP_H
#define LIBERAFX_INTEROP_H

#include <stdint.h>
#include <stddef.h>

#if defined(_MSC_VER)
#define LIBRA_INTEROP_ALIGN(bytes) __declspec(align(bytes))
#else
#define LIBRA_INTEROP_ALIGN(bytes) __attribute__((aligned(bytes)))
#endif

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
typedef uint64_t LibraGeneratorPool;
typedef uint64_t LibraGeneratorInstance;
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

/* These are host ABI records, not packed bytecode/wire overlays. Preserve the
   layout even when an includer's current packing differs. They are not SIMD
   vectors: values require 8-byte alignment, atlas/vertices require 4, not 16. */
#pragma pack(push, 8)
typedef struct LIBRA_INTEROP_ALIGN(8) LibraValue {
    double x, y, z, w;
    uint32_t componentCount;
    uint32_t reserved;
} LibraValue;

typedef struct LIBRA_INTEROP_ALIGN(4) LibraAtlasFrame {
    uint32_t frameIndex;
    float u, v, width, height;
} LibraAtlasFrame;

typedef struct LIBRA_INTEROP_ALIGN(4) LibraSpriteVertex {
    float x, y, z;
    float u, v;
    float r, g, b, a;
} LibraSpriteVertex;

typedef struct LIBRA_INTEROP_ALIGN(8) LibraGeneratorCapture {
    uint8_t nodeId[16];
    uint32_t inputSlot, component;
    double value;
} LibraGeneratorCapture;

typedef struct LIBRA_INTEROP_ALIGN(8) LibraGeneratorEvent {
    uint32_t kind, reserved;
    uint8_t containerId[16];
    LibraGeneratorInstance instanceId;
    double startTime;
    uint64_t seed;
} LibraGeneratorEvent;

typedef struct LIBRA_INTEROP_ALIGN(8) LibraSpawnableBinding {
    uint8_t containerId[16];
    uint8_t signalTypeId[16];
    LibraGeneratorPool pool;
    uint32_t inputCount, reserved;
    uint8_t widths[4], colors[4];
    uint8_t templateNodeIds[4][16];
} LibraSpawnableBinding;
#pragma pack(pop)

#if defined(__cplusplus)
#define LIBRA_INTEROP_ASSERT(expression) static_assert(expression, "LiberaFx ABI layout")
#define LIBRA_INTEROP_ALIGNOF(type) alignof(type)
#elif defined(__STDC_VERSION__) && __STDC_VERSION__ >= 201112L
#define LIBRA_INTEROP_ASSERT(expression) _Static_assert(expression, "LiberaFx ABI layout")
#define LIBRA_INTEROP_ALIGNOF(type) _Alignof(type)
#endif
#ifdef LIBRA_INTEROP_ASSERT
LIBRA_INTEROP_ASSERT(sizeof(LibraValue) == 40 && LIBRA_INTEROP_ALIGNOF(LibraValue) == 8);
LIBRA_INTEROP_ASSERT(offsetof(LibraValue, x) == 0 && offsetof(LibraValue, y) == 8 &&
    offsetof(LibraValue, z) == 16 && offsetof(LibraValue, w) == 24 &&
    offsetof(LibraValue, componentCount) == 32 && offsetof(LibraValue, reserved) == 36);
LIBRA_INTEROP_ASSERT(sizeof(LibraAtlasFrame) == 20 && LIBRA_INTEROP_ALIGNOF(LibraAtlasFrame) == 4);
LIBRA_INTEROP_ASSERT(offsetof(LibraAtlasFrame, frameIndex) == 0 && offsetof(LibraAtlasFrame, u) == 4 &&
    offsetof(LibraAtlasFrame, v) == 8 && offsetof(LibraAtlasFrame, width) == 12 &&
    offsetof(LibraAtlasFrame, height) == 16);
LIBRA_INTEROP_ASSERT(sizeof(LibraSpriteVertex) == 36 && LIBRA_INTEROP_ALIGNOF(LibraSpriteVertex) == 4);
LIBRA_INTEROP_ASSERT(offsetof(LibraSpriteVertex, x) == 0 && offsetof(LibraSpriteVertex, y) == 4 &&
    offsetof(LibraSpriteVertex, z) == 8 && offsetof(LibraSpriteVertex, u) == 12 &&
    offsetof(LibraSpriteVertex, v) == 16 && offsetof(LibraSpriteVertex, r) == 20 &&
    offsetof(LibraSpriteVertex, g) == 24 && offsetof(LibraSpriteVertex, b) == 28 &&
    offsetof(LibraSpriteVertex, a) == 32);
LIBRA_INTEROP_ASSERT(sizeof(LibraGeneratorCapture) == 32 && LIBRA_INTEROP_ALIGNOF(LibraGeneratorCapture) == 8);
LIBRA_INTEROP_ASSERT(offsetof(LibraGeneratorCapture, nodeId) == 0 &&
    offsetof(LibraGeneratorCapture, inputSlot) == 16 && offsetof(LibraGeneratorCapture, component) == 20 &&
    offsetof(LibraGeneratorCapture, value) == 24);
LIBRA_INTEROP_ASSERT(sizeof(LibraGeneratorEvent) == 48 && LIBRA_INTEROP_ALIGNOF(LibraGeneratorEvent) == 8);
LIBRA_INTEROP_ASSERT(offsetof(LibraGeneratorEvent, containerId) == 8 &&
    offsetof(LibraGeneratorEvent, instanceId) == 24 && offsetof(LibraGeneratorEvent, startTime) == 32 &&
    offsetof(LibraGeneratorEvent, seed) == 40);
LIBRA_INTEROP_ASSERT(sizeof(LibraSpawnableBinding) == 120 && LIBRA_INTEROP_ALIGNOF(LibraSpawnableBinding) == 8);
LIBRA_INTEROP_ASSERT(offsetof(LibraSpawnableBinding, containerId) == 0 &&
    offsetof(LibraSpawnableBinding, signalTypeId) == 16 && offsetof(LibraSpawnableBinding, pool) == 32 &&
    offsetof(LibraSpawnableBinding, inputCount) == 40 && offsetof(LibraSpawnableBinding, widths) == 48 &&
    offsetof(LibraSpawnableBinding, colors) == 52 && offsetof(LibraSpawnableBinding, templateNodeIds) == 56);
#undef LIBRA_INTEROP_ASSERT
#undef LIBRA_INTEROP_ALIGNOF
#endif

/* All lengths are element counts except byteCount. Pointers are borrowed only
   during the call, must address valid storage, and must not alias output arrays.
   IDs are 16 bytes in compiler/Guid big-endian order, not Windows GUID layout.
   Calls serialize internally; evaluation executes synchronously on the caller.
   Native work/default allocations are explicitly aligned to at least 16 bytes.
   No borrowed work/GPU buffer API is exposed. Output POD arrays need only their
   declared type alignment; never cast their lanes to aligned SIMD vectors.
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

/* Manual, synchronous runtime-only instance pool. The program is retained until
   pool release. Captures override template defaults at spawn, not live bindings.
   The event queue must be drained; full queues reject spawn/stop/reset. Event kind
   1 is spawn and 2 is stop. All IDs are scoped to this pool and never reused.
   Evaluations use time minus spawn time, independent for each instance. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_pool_create(
    LibraProgram program, const uint8_t containerId[16], uint32_t maxActive,
    uint64_t memoryBudgetBytes, uint32_t eventCapacity, LibraGeneratorPool* outPool);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_pool_release(LibraGeneratorPool pool);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_spawn(
    LibraGeneratorPool pool, double time, const LibraGeneratorCapture* captures,
    uint32_t count, LibraGeneratorInstance* outInstance);
/* enqueue is allocation-free and snapshots at most 16 scalar components.
   process runs on the owner thread; failed commands remain queued for retry. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_enqueue_spawn(
    LibraGeneratorPool pool, double time, const LibraGeneratorCapture* captures, uint32_t count);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_process_spawns(
    LibraGeneratorPool pool, uint32_t maxCommands, uint32_t* processed);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_stop(
    LibraGeneratorPool pool, LibraGeneratorInstance instance);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_reset(LibraGeneratorPool pool);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_evaluate(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, double time);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_value(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, const uint8_t nodeId[16],
    LibraValue* outValue);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_next_event(
    LibraGeneratorPool pool, LibraGeneratorEvent* outEvent, uint32_t* hasEvent);
/* Load a v3 parent using package-provided per-container descriptors. The pools
   are retained by the program; release handles independently. Dispatch only
   enqueues captures; call generator_process_spawns outside the callback. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_program_create_spawnable(
    const uint8_t* bytes, uint64_t byteCount, const LibraSpawnableBinding* bindings,
    uint32_t bindingCount, LibraProgram* outProgram);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_dispatch_signal(
    LibraInstance instance, const uint8_t nodeId[16], double time, uint32_t instructionBudget);

#ifdef __cplusplus
}
#endif
#endif
