#include "../LbInteropInternal.h"

using namespace liberafx::interop_detail;

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_cpu_particle_count(
    LibraSpawnableHostHandle host, const uint8_t* id, uint32_t* outCount)
{
    if (!id || !outCount)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    return guardedSpawnable(host, [&]() -> LibraResult
    {
        auto count = std::size_t{};
        auto result = findSpawnableHost(host)->cpuParticleCount(id, count);
        if (result == liberafx::Result::Ok)
        {
            *outCount = static_cast<std::uint32_t>(count);
        }
        return toLibraResult(result);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_cpu_particle_at(
    LibraSpawnableHostHandle host, const uint8_t* id, uint32_t index, LibraCpuParticleSnapshot* out)
{
    if (!id || !out)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    return guardedSpawnable(host, [&]() -> LibraResult
    {
        auto snapshot = liberafx::CpuParticleSnapshot{};
        auto result = findSpawnableHost(host)->cpuParticleAt(id, index, snapshot);
        if (result == liberafx::Result::Ok)
        {
            static_assert(sizeof(snapshot) == sizeof(*out));
            std::memcpy(out, &snapshot, sizeof(snapshot));
        }
        return toLibraResult(result);
    });
}
LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_cpu_particle_pop_event(
    LibraSpawnableHostHandle host, LibraCpuParticleEvent* out, uint32_t* hasEvent)
{
    if (!out || !hasEvent)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    return guardedSpawnable(host, [&]() -> LibraResult
    {
        auto event = liberafx::CpuParticleEvent{};
        *hasEvent = findSpawnableHost(host)->popCpuParticleEvent(event) ? 1u : 0u;
        if (*hasEvent != 0)
        {
            static_assert(sizeof(event) == sizeof(*out));
            std::memcpy(out, &event, sizeof(event));
        }
        return LIBRA_OK;
    });
}
