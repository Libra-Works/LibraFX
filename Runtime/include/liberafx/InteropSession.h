#ifndef LIBERAFX_INTEROP_SESSION_H
#define LIBERAFX_INTEROP_SESSION_H

#include "liberafx/Interop.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Additive host ABI v1. These records are NOT packed wire overlays. No field
   encodes a pointer. Session nonces must be fresh across processes/connections.
   The caller explicitly authorizes development use; this API opens no socket. */
#pragma pack(push, 8)
typedef struct LIBRA_INTEROP_ALIGN(8) LibraSessionId {
    uint64_t high, low, incarnation;
} LibraSessionId;
typedef struct LIBRA_INTEROP_ALIGN(8) LibraSessionRequestId {
    LibraSessionId session;
    uint64_t sequence;
} LibraSessionRequestId;
typedef struct LIBRA_INTEROP_ALIGN(8) LibraSessionInstanceId {
    LibraSessionId session;
    uint64_t generation, slot;
} LibraSessionInstanceId;
typedef struct LIBRA_INTEROP_ALIGN(8) LibraSessionOptions {
    uint32_t structSize, instanceCapacity, requestCapacity, flags;
    uint64_t nonceHigh, nonceLow, maxProgramBytes, maxOwnedBytes;
} LibraSessionOptions;
typedef struct LIBRA_INTEROP_ALIGN(8) LibraSessionCompletion {
    LibraSessionRequestId request;
    LibraSessionInstanceId instance;
    uint32_t kind;
    LibraResult result;
} LibraSessionCompletion;
#pragma pack(pop)
enum {
    LIBRA_SESSION_CREATE = 0, LIBRA_SESSION_NATURAL_END = 1,
    LIBRA_SESSION_DESTROY = 2, LIBRA_SESSION_REPLACE = 3,
    LIBRA_SESSION_PRESERVE_STATE = 0, LIBRA_SESSION_RESTART = 1,
    LIBRA_SESSION_DEVELOPMENT_OPT_IN = 1
};

LIBRA_INTEROP_API uint32_t LIBRA_INTEROP_CALL libra_session_abi_version(void);
/* Pure bounded validation for engine component transactional restart. LFXP:
   root-only numeric v1/v2, manual lifetime, no GPU/children/signals/game I/O.
   Does not create a facade instance or call an engine SDK. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_session_validate_live_restart(
    const uint8_t* bytes, uint64_t byteCount);
/* Captures calling thread as owner. flags must be DEVELOPMENT_OPT_IN.
   Explicit capacities, maxProgramBytes (12..16 MiB), maxOwnedBytes (<=1 GiB).
   No borrowed arrays are retained. The host wrapper owns Core backing memory. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_session_create(
    const LibraSessionOptions* options, LibraSessionId* outSession);
/* Worker-safe. Copies bytes during call; caller must keep input stable only
   until return. Validates/allocates bounded host storage, but never evaluates
   nodes or calls an engine SDK. Only pure LFXB v1/v2 supported.
   infinite must be 0/1; finite duration is sampled at creation/restart.
   Ok means accepted, not applied. Unsupported state preservation completes
   with UNSUPPORTED_BINARY; explicit Restart never migrates state. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_session_submit_program(
    LibraSessionId session, uint32_t kind, LibraSessionInstanceId target,
    const uint8_t* bytes, uint64_t byteCount, double duration,
    uint32_t infinite, uint32_t policy, LibraSessionRequestId* outRequest);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_session_submit_control(
    LibraSessionId session, uint32_t kind, LibraSessionInstanceId target,
    LibraSessionRequestId* outRequest);
/* Worker-safe, one successful consumption per request. A pending/unconsumed
   completion reserves queue credit and resources. No pointers are returned. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_session_take_completion(
    LibraSessionId session, LibraSessionRequestId request, LibraSessionCompletion* outCompletion);
/* Owner-only (thread captured by create). Wrong thread -> INVALID_ARGUMENT.
   These calls do not dispatch work to the game thread automatically. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_session_process(
    LibraSessionId session, uint32_t limit, uint32_t* outProcessed);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_session_advance(
    LibraSessionId session, LibraSessionInstanceId instance, double elapsed,
    double frameDelta, uint32_t* outFinished);
/* nodeId is compiler/Guid big-endian byte order, not a platform GUID overlay. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_session_read_value(
    LibraSessionId session, LibraSessionInstanceId instance,
    const uint8_t nodeId[16], LibraValue* outValue);
/* close disables new work except Destroy; NotReady requires explicit draining,
   destruction and completion consumption. Never implicitly force-stop.
   release succeeds ONLY after close/quiescence. It invalidates the session ID.
   Quiesce/join producers before final release. No finalizer/auto-stop API. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_session_close(LibraSessionId session);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_session_release(LibraSessionId session);

#ifdef __cplusplus
}
static_assert(sizeof(LibraSessionId) == 24 && alignof(LibraSessionId) == 8, "LibraSessionId ABI");
static_assert(sizeof(LibraSessionRequestId) == 32 && offsetof(LibraSessionRequestId, sequence) == 24, "LibraSessionRequestId ABI");
static_assert(sizeof(LibraSessionInstanceId) == 40 && offsetof(LibraSessionInstanceId, generation) == 24 &&
    offsetof(LibraSessionInstanceId, slot) == 32, "LibraSessionInstanceId ABI");
static_assert(sizeof(LibraSessionOptions) == 48 && offsetof(LibraSessionOptions, nonceHigh) == 16 &&
    offsetof(LibraSessionOptions, maxOwnedBytes) == 40, "LibraSessionOptions ABI");
static_assert(sizeof(LibraSessionCompletion) == 80 && offsetof(LibraSessionCompletion, instance) == 32 &&
    offsetof(LibraSessionCompletion, kind) == 72 && offsetof(LibraSessionCompletion, result) == 76, "LibraSessionCompletion ABI");
#endif
#endif
