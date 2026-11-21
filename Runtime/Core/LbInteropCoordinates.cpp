#include "LbInteropInternal.h"
#include "liberafx/LbCoordinateBasis.h"

using namespace liberafx::interop_detail;

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_set_time_scale(LibraSpawnableHostHandle host, double ratio) {
    return guardedSpawnable(host, [&]() -> LibraResult {
        const auto native = findSpawnableHost(host);
        return native ? toLibraResult(native->setTimeScale(ratio)) : LIBRA_INVALID_HANDLE;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_time_snapshot(LibraSpawnableHostHandle host, LibraEffectTimeSnapshot* snapshot) {
    if (!snapshot) return LIBRA_INVALID_ARGUMENT;
    return guardedSpawnable(host, [&]() -> LibraResult {
        const auto native = findSpawnableHost(host);
        if (!native) return LIBRA_INVALID_HANDLE;
        const auto value = native->timeSnapshot();
        *snapshot = {value.realTime, value.effectTime, value.realDelta, value.effectDelta, value.timeScale};
        return LIBRA_OK;
    });
}

LibraResult LIBRA_INTEROP_CALL libra_coordinate_convert(const LibraCoordinateBasis* basis,
    uint32_t kind, uint32_t toHost, const double* source, uint32_t width, double* destination) {
    if (!basis || kind > 6 || toHost > 1) return LIBRA_INVALID_ARGUMENT;
    const auto convention = liberafx::CoordinateBasis{{basis->axisX, basis->axisY, basis->axisZ}, basis->unitsPerMetre};
    return toLibraResult(liberafx::convertCoordinates(convention,
        static_cast<liberafx::SpatialValueKind>(kind), toHost != 0, source, width, destination));
}
LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_set_coordinate_basis(
    LibraSpawnableHostHandle host, const LibraCoordinateBasis* basis) {
    if (!basis) return LIBRA_INVALID_ARGUMENT;
    return guardedSpawnable(host, [&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        if (!native) return LIBRA_INVALID_HANDLE;
        return toLibraResult(native->setCoordinateBasis(
            {{basis->axisX, basis->axisY, basis->axisZ}, basis->unitsPerMetre}));
    });
}
LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_set_game_spatial_property(
    LibraSpawnableHostHandle host, const char* name, uint32_t kind, const double* values, uint32_t width) {
    if (!name || kind > 6) return LIBRA_INVALID_ARGUMENT;
    return guardedSpawnable(host, [&]() -> LibraResult {
        auto native = findSpawnableHost(host);
        return native ? toLibraResult(native->setGameSpatialProperty(name,
            static_cast<liberafx::SpatialValueKind>(kind), values, width)) : LIBRA_INVALID_HANDLE;
    });
}
