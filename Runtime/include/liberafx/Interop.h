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
enum { LIBRA_GENERATOR_SPAWN = 1, LIBRA_GENERATOR_STOP = 2, LIBRA_GENERATOR_DESTROY = 3 };

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

typedef struct LIBRA_INTEROP_ALIGN(8) LibraGeneratorSignalEvent {
    uint8_t containerId[16];
    LibraGeneratorInstance instanceId;
    uint8_t nodeId[16];
    double time;
    uint64_t seed;
} LibraGeneratorSignalEvent;

typedef struct LIBRA_INTEROP_ALIGN(4) LibraGeneratorLifecycle {
    uint8_t onStartNodeId[16], lifeNodeId[16];
    uint32_t flags, instructionBudget;
} LibraGeneratorLifecycle;

typedef struct LIBRA_INTEROP_ALIGN(8) LibraLifetimeSample {
    double age, remaining, normalizedAge;
    uint32_t expired, infinite;
} LibraLifetimeSample;

typedef struct LIBRA_INTEROP_ALIGN(8) LibraScheduledEvent {
    double time;
    uint64_t token;
} LibraScheduledEvent;

typedef struct LIBRA_INTEROP_ALIGN(8) LibraSpawnableBinding {
    uint8_t containerId[16];
    uint8_t signalTypeId[16];
    LibraGeneratorPool pool;
    uint32_t inputCount, reserved;
    uint8_t widths[4], colors[4];
    uint8_t templateNodeIds[4][16];
} LibraSpawnableBinding;

typedef struct LIBRA_INTEROP_ALIGN(8) LibraSpawnableSignalInputBinding {
    uint8_t containerId[16];
    uint8_t signalTypeId[16];
    LibraGeneratorPool pool;
    uint8_t proxyNodeId[16];
    uint8_t signalNodeId[16];
} LibraSpawnableSignalInputBinding;

typedef struct LIBRA_INTEROP_ALIGN(8) LibraGameIoEvent {
    uint8_t containerId[16];
    uint64_t instanceId;
    uint32_t portIndex;
    uint8_t kind, width, isColor, reserved;
    double values[4];
} LibraGameIoEvent;

typedef struct LIBRA_INTEROP_ALIGN(8) LibraGameIoEventV2 {
    uint8_t containerId[16];
    uint64_t instanceId;
    uint32_t portIndex;
    uint8_t kind, width, isColor, reserved;
    double values[16];
} LibraGameIoEventV2;
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
LIBRA_INTEROP_ASSERT(sizeof(LibraGeneratorSignalEvent) == 56 &&
    LIBRA_INTEROP_ALIGNOF(LibraGeneratorSignalEvent) == 8);
LIBRA_INTEROP_ASSERT(offsetof(LibraGeneratorSignalEvent, instanceId) == 16 &&
    offsetof(LibraGeneratorSignalEvent, nodeId) == 24 &&
    offsetof(LibraGeneratorSignalEvent, time) == 40 &&
    offsetof(LibraGeneratorSignalEvent, seed) == 48);
LIBRA_INTEROP_ASSERT(sizeof(LibraGeneratorLifecycle) == 40 && LIBRA_INTEROP_ALIGNOF(LibraGeneratorLifecycle) == 4);
LIBRA_INTEROP_ASSERT(offsetof(LibraGeneratorLifecycle, flags) == 32 &&
    offsetof(LibraGeneratorLifecycle, instructionBudget) == 36);
LIBRA_INTEROP_ASSERT(sizeof(LibraLifetimeSample) == 32 && LIBRA_INTEROP_ALIGNOF(LibraLifetimeSample) == 8);
LIBRA_INTEROP_ASSERT(offsetof(LibraLifetimeSample, age) == 0 &&
    offsetof(LibraLifetimeSample, remaining) == 8 && offsetof(LibraLifetimeSample, normalizedAge) == 16 &&
    offsetof(LibraLifetimeSample, expired) == 24 && offsetof(LibraLifetimeSample, infinite) == 28);
LIBRA_INTEROP_ASSERT(sizeof(LibraSpawnableBinding) == 120 && LIBRA_INTEROP_ALIGNOF(LibraSpawnableBinding) == 8);
LIBRA_INTEROP_ASSERT(offsetof(LibraSpawnableBinding, containerId) == 0 &&
    offsetof(LibraSpawnableBinding, signalTypeId) == 16 && offsetof(LibraSpawnableBinding, pool) == 32 &&
    offsetof(LibraSpawnableBinding, inputCount) == 40 && offsetof(LibraSpawnableBinding, widths) == 48 &&
    offsetof(LibraSpawnableBinding, colors) == 52 && offsetof(LibraSpawnableBinding, templateNodeIds) == 56);
LIBRA_INTEROP_ASSERT(sizeof(LibraSpawnableSignalInputBinding) == 72 &&
    LIBRA_INTEROP_ALIGNOF(LibraSpawnableSignalInputBinding) == 8);
LIBRA_INTEROP_ASSERT(offsetof(LibraSpawnableSignalInputBinding, signalTypeId) == 16 &&
    offsetof(LibraSpawnableSignalInputBinding, pool) == 32 &&
    offsetof(LibraSpawnableSignalInputBinding, proxyNodeId) == 40 &&
    offsetof(LibraSpawnableSignalInputBinding, signalNodeId) == 56);
LIBRA_INTEROP_ASSERT(sizeof(LibraGameIoEvent) == 64 && sizeof(LibraGameIoEventV2) == 160 &&
    LIBRA_INTEROP_ALIGNOF(LibraGameIoEventV2) == 8);
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
/* Set before each evaluation for Effect Age and Delta Time nodes. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_instance_set_time_context(
    LibraInstance instance, double effectTime, double frameDelta);
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
/* Opt-in root lifetime. infinite must be 0 or 1; duration is ignored if 1.
   elapsed is scaled seconds since start. OnStart is optional. Output is valid
   only on success; the deadline is reported once after the clamped evaluation.
   Stop never reports expiry. Restart requires a new instance.
   A host must keep the parent alive until children and queued continuations
   complete; use drain for subsequent updates and finish only when quiescent.
   These primitives do not manage child pools or dispatch Destroy automatically.
   Configure game IO before this call, if needed. A previous raw evaluation or
   game_io_start prevents managed playback. Once start is attempted, use these
   lifetime calls instead of raw evaluate, game_io_start/update, or raw signal
   dispatch. Managed game input signals use the last successfully evaluated age.
   values_read and game IO event draining remain available. A failed evaluation,
   property publication or OnStart is terminal; create a fresh instance. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_effect_start(
    LibraInstance instance, double duration, uint32_t infinite,
    const uint8_t onStartNodeId[16], uint32_t instructionBudget, LibraLifetimeSample* outSample);
/* Capture a finite scalar duration from the initial evaluation, before OnStart.
   The captured value is not affected by subsequent property changes. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_effect_start_captured(
    LibraInstance instance, const uint8_t lifeNodeId[16],
    const uint8_t onStartNodeId[16], uint32_t instructionBudget, LibraLifetimeSample* outSample);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_effect_advance(
    LibraInstance instance, double elapsed, LibraLifetimeSample* outSample);
/* After deadline, keep the parent active while owned work completes. Do not
   report final destruction until all children and continuations have finished. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_effect_drain(LibraInstance instance, double elapsed);
/* Bounded owner-thread continuation queue. Advance/drain must not skip the
   next scheduled time; take consumes a due event before the host delivers it. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_effect_schedule(
    LibraInstance instance, double time, uint64_t token);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_effect_next_scheduled(
    LibraInstance instance, LibraScheduledEvent* outEvent, uint32_t* hasEvent);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_effect_take_scheduled(
    LibraInstance instance, LibraScheduledEvent* outEvent, uint32_t* hasEvent);
/* Deferred signal continuations use the instance's local effect time. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_effect_next_deferred_signal(
    LibraInstance instance, double* outTime, uint32_t* hasSignal);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_effect_dispatch_deferred_signal(
    LibraInstance instance, double time, uint32_t instructionBudget);
/* Mark the owner for natural termination; scheduled work still drains before Destroy. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_effect_request_natural_end(LibraInstance instance);
/* Dispatch natural root Destroy at most once after owned work is quiescent. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_effect_destroy(
    LibraInstance instance, const uint8_t nodeId[16], uint32_t instructionBudget);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_effect_finish(LibraInstance instance);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_effect_stop(LibraInstance instance);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_sample_sprite_atlas(
    double frame, uint32_t columns, uint32_t rows, LibraAtlasFrame* outFrame);
/* Top-left-origin UVs; four vertices in TL, TR, BR, BL order. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_make_sprite_quad(
    const LibraAtlasFrame* frame, float width, float height, LibraSpriteVertex outVertices[4]);

/* Manual, synchronous runtime-only instance pool. The program is retained until
   pool release. Captures override template defaults at spawn, not live bindings.
   The event queue must be drained; full queues reject spawn/stop/reset. Event kind
   1 is spawn and 2 is stop. Configured lifecycle pools additionally emit 3
   (local Destroy) only on natural lifetime expiry. Drain and route Destroy
   before finalize; the frozen instance remains readable until then.
   All IDs are scoped to this pool and never reused.
   Evaluations use time minus spawn time, independent for each instance. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_pool_create(
    LibraProgram program, const uint8_t containerId[16], uint32_t maxActive,
    uint64_t memoryBudgetBytes, uint32_t eventCapacity, LibraGeneratorPool* outPool);
/* Extended queue capture limit is 1..256 scalar lanes; legacy create uses 16. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_pool_create_ex(
    LibraProgram program, const uint8_t containerId[16], uint32_t maxActive,
    uint64_t memoryBudgetBytes, uint32_t eventCapacity, uint32_t maxCaptureLanes,
    LibraGeneratorPool* outPool);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_pool_release(LibraGeneratorPool pool);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_spawn(
    LibraGeneratorPool pool, double time, const LibraGeneratorCapture* captures,
    uint32_t count, LibraGeneratorInstance* outInstance);
/* enqueue is allocation-free and snapshots at most 16 scalar components.
   process runs on the owner thread; failed commands remain queued for retry. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_enqueue_spawn(
    LibraGeneratorPool pool, double time, const LibraGeneratorCapture* captures, uint32_t count);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_enqueue_spawn_ex(
    LibraGeneratorPool pool, double time, const LibraGeneratorCapture* captures, uint32_t count);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_process_spawns(
    LibraGeneratorPool pool, uint32_t maxCommands, uint32_t* processed);
enum { LIBRA_LIFECYCLE_ON_START = 1, LIBRA_LIFECYCLE_LIFE = 2, LIBRA_LIFECYCLE_DEFERRED_SIGNALS = 4 };
/* Configure an empty pool. After spawn, apply Live inputs, then start exactly
   once. Life is a scalar duration in seconds; zero expires after OnStart. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_configure_lifecycle(
    LibraGeneratorPool pool, const LibraGeneratorLifecycle* lifecycle);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_start(
    LibraGeneratorPool pool, LibraGeneratorInstance instance);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_finalize(
    LibraGeneratorPool pool, LibraGeneratorInstance instance);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_stop(
    LibraGeneratorPool pool, LibraGeneratorInstance instance);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_reset(LibraGeneratorPool pool);
/* Host supplies only lanes configured Live; captured lanes are not changed. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_set_live(
    LibraGeneratorPool pool, LibraGeneratorInstance instance,
    const LibraGeneratorCapture* values, uint32_t count);
/* Apply CaptureAtSpawn lanes to a materialized instance before start. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_set_captured(
    LibraGeneratorPool pool, LibraGeneratorInstance instance,
    const LibraGeneratorCapture* values, uint32_t count);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_set_signal_outputs(
    LibraGeneratorPool pool, const uint8_t* nodeIds, uint32_t count);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_dispatch_signal(
    LibraGeneratorPool pool, const uint8_t nodeId[16], double time, uint32_t instructionBudget);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_next_signal_event(
    LibraGeneratorPool pool, LibraGeneratorSignalEvent* outEvent, uint32_t* hasEvent);
/* Supply effect elapsed time and the game frame's delta before each evaluation. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_set_time_context(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, double effectTime, double frameDelta);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_evaluate(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, double time);
/* Due times use the containing effect clock; elapsed returns the actual local
   evaluation age, including draining past the lifetime deadline. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_next_deferred_signal(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, double* outTime, uint32_t* hasSignal);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_dispatch_deferred_signal(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, double time, uint32_t instructionBudget);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_elapsed(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, double* outElapsed);
/* At-most-once natural owner end request at the instance's last evaluated global time. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_owner_destroy(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, const uint8_t nodeId[16],
    double time, uint32_t instructionBudget);
/* Last successful sample; infinite lifetimes report infinite remaining and normalizedAge 0. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_generator_lifetime(
    LibraGeneratorPool pool, LibraGeneratorInstance instance, LibraLifetimeSample* outSample);
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
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_program_create_spawnable_v2(
    const uint8_t* bytes, uint64_t byteCount, const LibraSpawnableBinding* bindings,
    uint32_t bindingCount, const LibraSpawnableSignalInputBinding* signalInputs,
    uint32_t signalInputCount, LibraProgram* outProgram);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_dispatch_signal(
    LibraInstance instance, const uint8_t nodeId[16], double time, uint32_t instructionBudget);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_effect_dispatch_signal(
    LibraInstance instance, const uint8_t nodeId[16], double time, uint32_t instructionBudget);
/* Configure Game I/O once after instance creation; port descriptors are read
   from the validated bytecode and copied into the owner-thread facade. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_game_io_configure(
    LibraInstance instance, uint32_t eventCapacity,
    const uint8_t containerId[16], uint64_t instanceId);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_game_io_set_property(
    LibraInstance instance, const char* name, const double* values, uint32_t width);
/* Matrix values are 16 row-major doubles, matching Matrix Compose row order.
   Set Owner Matrix after configure and before start; it may be updated between frames. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_game_io_set_owner_matrix(
    LibraInstance instance, const double values[16]);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_game_io_start(
    LibraInstance instance, double time, const uint8_t onStartNodeId[16], uint32_t instructionBudget);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_game_io_update(
    LibraInstance instance, double time);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_game_io_fire_signal(
    LibraInstance instance, const char* name, double time, uint32_t instructionBudget);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_game_io_publish_properties(LibraInstance instance);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_game_io_next_event(
    LibraInstance instance, LibraGameIoEvent* outEvent, uint32_t* hasEvent);
/* Matrix output events require this larger record. The legacy event function
   reports LIBRA_INVALID_VALUE_TYPE and leaves a matrix event queued. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_game_io_next_event_v2(
    LibraInstance instance, LibraGameIoEventV2* outEvent, uint32_t* hasEvent);

#ifdef __cplusplus
}
#endif
#endif
