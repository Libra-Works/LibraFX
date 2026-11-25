#ifndef LIBERAFX_INTEROP_SESSION_H
#define LIBERAFX_INTEROP_SESSION_H

#include "liberafx/LbInterop.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Bounded validation for Unity's transactional restart of current root-only LFXP packages. */
LIBRA_INTEROP_API LibraResult LIBRA_INTEROP_CALL libra_session_validate_live_restart(
    const uint8_t* bytes, uint64_t byteCount);

#ifdef __cplusplus
}
#endif
#endif
