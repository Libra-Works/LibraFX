#pragma once

#include "liberafx/NamedIo.h"
#include "liberafx/GeneratorPool.h"
#include "liberafx/SpawnablePackage.h"
#include <cstddef>
#include <cstdint>
#include <memory>

namespace liberafx {

enum class SpawnableHostEventKind : std::uint8_t { Spawn = 1, Stop = 2, Destroy = 3, Signal = 4 };

struct SpawnableHostEvent final {
    SpawnableHostEventKind kind{};
    std::uint8_t containerId[16]{};
    GeneratorInstanceId instanceId = 0;
    double time = 0;
    std::uint64_t seed = 0;
    std::uint8_t signalNodeId[16]{};
};

struct SpawnableHostOptions final {
    std::size_t maxActivePerTemplate = 32;
    std::size_t memoryBudgetBytesPerTemplate = 4 * 1024 * 1024;
    std::size_t eventCapacity = 128;
    std::size_t instructionBudget = 10000;
};

// Owner-thread lifecycle and routing host for one engine-neutral package.
// Rendering and engine object operations remain the adapter's responsibility.
class LIBRA_RUNTIME_CORE_API SpawnableHost final {
public:
    static Result create(SpawnablePackage package, const SpawnableHostOptions& options,
                         std::unique_ptr<SpawnableHost>& out);
    ~SpawnableHost();
    SpawnableHost(const SpawnableHost&) = delete;
    SpawnableHost& operator=(const SpawnableHost&) = delete;

    Result advance(double time, double frameDelta);
    Result dispatchSignal(const std::uint8_t nodeId[16]);
    Result fireGameSignal(const char* name);
    Result setGameProperty(const char* name, const double* values, std::size_t width);
    Result setOwnerMatrix(const double* values, std::size_t width);
    Result requestNaturalEnd();
    Result stop();

    Result rootValue(const std::uint8_t nodeId[16], double out[4],
                     std::uint8_t& width) const;
    Result childValue(const std::uint8_t containerId[16], GeneratorInstanceId instanceId,
                      const std::uint8_t nodeId[16], double out[4], std::uint8_t& width) const;
    bool popEvent(SpawnableHostEvent& out) noexcept;
    bool popGameIoEvent(NamedIoEvent& out) noexcept;
    std::size_t activeCount() const noexcept;
    bool rootFinished() const noexcept;
    const SpawnablePackage& package() const noexcept;

private:
    struct Impl;
    explicit SpawnableHost(std::unique_ptr<Impl> impl) noexcept;
    std::unique_ptr<Impl> impl_;
};

}
