// Package-only validation retained for Unity's transactional LFXP restart path.
#if defined(LIBRA_INTEROP_BUILD)
#include "liberafx/LbInteropSession.h"
#include "liberafx/host/LbLivePackage.h"
#include <cstddef>

LibraResult LIBRA_INTEROP_CALL libra_session_validate_live_restart(
    const uint8_t* bytes, uint64_t byteCount)
{
    if (!bytes || byteCount > 16 * 1024 * 1024 || byteCount > SIZE_MAX) {
        return LIBRA_INVALID_ARGUMENT;
    }
    return static_cast<LibraResult>(liberafx::host::validateLiveRestart(
        bytes, static_cast<std::size_t>(byteCount)));
}
#endif
