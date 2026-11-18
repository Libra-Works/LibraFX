#pragma once
#include "liberafx/LbInterop.h"

#ifdef __cplusplus
extern "C" {
#endif
// The invitation is exactly 32 lower-case ASCII hex digits plus NUL. Never
// persist it. Only standalone instances are supported by this adapter.
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_debug_attach(LibraInstance, const char* invitation);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_debug_schedule(LibraInstance, double time);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_debug_finish(LibraInstance,
    const LibraBinding*, uint32_t count, LibraValue*);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_debug_detach(LibraInstance);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_spawnable_debug_generation(LibraSpawnableHostHandle, uint64_t*);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_spawnable_debug_attach(LibraSpawnableHostHandle,
    uint64_t generation, const uint8_t* container, LibraGeneratorInstance instance, const char* invitation);
// 0: advance, 1: signal, 2: named signal, 3: natural end, 4: notify end.
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_spawnable_debug_submit(LibraSpawnableHostHandle,
    uint8_t operation, double time, double frameDelta, const uint8_t* container,
    LibraGeneratorInstance instance, const uint8_t* node, const char* name);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_spawnable_debug_poll(LibraSpawnableHostHandle, LibraResult*);
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_spawnable_debug_detach(LibraSpawnableHostHandle);
#ifdef __cplusplus
}
#endif
