#include "../LbInteropInternal.h"

using namespace liberafx::interop_detail;

auto readCpuParticleScope(const uint8_t containerId[16], uint64_t instanceId,
                          liberafx::SpawnableScope& out) -> bool
{
    if (!containerId)
    {
        return false;
    }
    auto anyContainerByte = false;
    for (auto index = 0u; index < 16; ++index)
    {
        anyContainerByte = anyContainerByte || containerId[index] != 0;
    }
    if (!instanceId && !anyContainerByte)
    {
        out = {};
        return true;
    }
    if (!instanceId || !anyContainerByte)
    {
        return false;
    }
    out = {};
    out.kind = liberafx::SpawnableScopeKind::Instance;
    std::memcpy(out.containerId, containerId, 16);
    out.instanceId = instanceId;
    return true;
}

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

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_scoped_cpu_particle_timing(
    LibraSpawnableHostHandle host, const uint8_t* containerId, uint64_t instanceId,
    const uint8_t* systemId, uint32_t index, double* age, double* life)
{
    if (!containerId || !systemId || !age || !life) return LIBRA_INVALID_ARGUMENT;
    *age = 0; *life = 0;
    auto scope = liberafx::SpawnableScope{};
    if (!readCpuParticleScope(containerId, instanceId, scope)) return LIBRA_INVALID_ARGUMENT;
    return guardedSpawnable(host, [&]() -> LibraResult {
        auto timing = liberafx::CpuParticleTiming{};
        const auto result = findSpawnableHost(host)->cpuParticleTiming(scope,systemId,index,timing);
        if (result == liberafx::Result::Ok) { *age = timing.age; *life = timing.life; }
        return toLibraResult(result);
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_scoped_cpu_particle_count(
    LibraSpawnableHostHandle host, const uint8_t* containerId,
    uint64_t instanceId, const uint8_t* systemId, uint32_t* outCount)
{
    if (!containerId || !systemId || !outCount)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    *outCount = 0;
    auto scope = liberafx::SpawnableScope{};
    if (!readCpuParticleScope(containerId, instanceId, scope))
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    return guardedSpawnable(host, [&]() -> LibraResult
    {
        auto count = std::size_t{};
        auto result =
            findSpawnableHost(host)->cpuParticleCount(scope, systemId, count);
        if (result == liberafx::Result::Ok)
        {
            if (count > UINT32_MAX)
            {
                return LIBRA_ARITHMETIC_OVERFLOW;
            }
            *outCount = static_cast<std::uint32_t>(count);
        }
        return toLibraResult(result);
    });
}

LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_scoped_cpu_particle_at(
    LibraSpawnableHostHandle host, const uint8_t* containerId,
    uint64_t instanceId, const uint8_t* systemId, uint32_t index,
    LibraCpuParticleSnapshot* out)
{
    if (!containerId || !systemId || !out)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    std::memset(out, 0, sizeof(*out));
    auto scope = liberafx::SpawnableScope{};
    if (!readCpuParticleScope(containerId, instanceId, scope))
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    return guardedSpawnable(host, [&]() -> LibraResult
    {
        auto snapshot = liberafx::CpuParticleSnapshot{};
        auto result = findSpawnableHost(host)->cpuParticleAt(
            scope, systemId, index, snapshot);
        if (result == liberafx::Result::Ok)
        {
            static_assert(sizeof(snapshot) == sizeof(*out));
            std::memcpy(out, &snapshot, sizeof(snapshot));
        }
        return toLibraResult(result);
    });
}

LibraResult LIBRA_INTEROP_CALL
libra_spawnable_host_scoped_cpu_particle_pop_event(
    LibraSpawnableHostHandle host, const uint8_t* containerId,
    uint64_t instanceId, LibraCpuParticleEvent* out, uint32_t* hasEvent)
{
    if (!containerId || !out || !hasEvent)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    std::memset(out, 0, sizeof(*out));
    *hasEvent = 0;
    auto scope = liberafx::SpawnableScope{};
    if (!readCpuParticleScope(containerId, instanceId, scope))
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    return guardedSpawnable(host, [&]() -> LibraResult
    {
        auto event = liberafx::CpuParticleEvent{};
        *hasEvent =
            findSpawnableHost(host)->popCpuParticleEvent(scope, event) ? 1u : 0u;
        if (*hasEvent)
        {
            static_assert(sizeof(event) == sizeof(*out));
            std::memcpy(out, &event, sizeof(event));
        }
        return LIBRA_OK;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_spawnable_host_scoped_cpu_particle_read(
    LibraSpawnableHostHandle host, const uint8_t containerId[16], uint64_t instanceId,
    const uint8_t systemId[16], uint32_t first, LibraCpuParticleSnapshot* snapshots,
    LibraCpuParticleTiming* timings, uint32_t capacity, uint32_t* written)
{
    if (written) *written=0;
    auto scope=liberafx::SpawnableScope{};
    if (!systemId || !snapshots || !written || capacity==0 || capacity>256 ||
        !readCpuParticleScope(containerId,instanceId,scope)) return LIBRA_INVALID_ARGUMENT;
    return guardedSpawnable(host,[&]() -> LibraResult {
        std::array<liberafx::CpuParticleSnapshot,256> particles;
        std::array<liberafx::CpuParticleTiming,256> clocks;
        std::size_t count=0;
        const auto status=findSpawnableHost(host)->cpuParticleRead(scope,systemId,first,particles.data(),
            timings ? clocks.data() : nullptr,capacity,count);
        if (status==liberafx::Result::Ok) {
            static_assert(sizeof(particles[0])==sizeof(*snapshots));
            static_assert(sizeof(clocks[0])==sizeof(*timings));
            std::memcpy(snapshots,particles.data(),count*sizeof(*snapshots));
            if (timings) std::memcpy(timings,clocks.data(),count*sizeof(*timings));
            *written=static_cast<uint32_t>(count);
        }
        return toLibraResult(status);
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
