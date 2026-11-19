#pragma once

#include "liberafx/LbVfxRuntime.h"
#include <array>
#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace liberafx {

struct SpawnablePackageCapture final {
    std::array<std::uint8_t, 16> targetNodeId{};
    std::uint32_t width = 0;
    std::optional<std::array<std::uint8_t, 16>> sourceContainerId;
    std::array<std::uint8_t, 16> sourceNodeId{};
    std::uint32_t sourceComponent = 0;
};

struct SpawnablePackageInput final {
    std::string portId;
    std::array<std::uint8_t, 16> templateNodeId{};
    std::string portType;
    std::uint32_t width = 0;
    bool isColor = false;
    bool isLive = false;
};

struct SpawnablePackageOutput final {
    std::string portId;
    std::array<std::uint8_t, 16> templateNodeId{};
    std::string portType;
};

struct SpawnablePackageSignalPort final {
    std::string portId;
    std::array<std::uint8_t, 16> templateNodeId{};
    std::array<std::uint8_t, 16> proxyNodeId{};
};

struct SpawnablePackageLifecycle final {
    std::optional<std::array<std::uint8_t, 16>> onStartNodeId;
    std::optional<std::array<std::uint8_t, 16>> lifeNodeId;
    std::optional<std::array<std::uint8_t, 16>> ownerDestroyNodeId;
};

struct SpawnablePackageTemplate final {
    std::array<std::uint8_t, 16> containerId{};
    std::array<std::uint8_t, 16> signalTypeId{};
    std::vector<std::uint8_t> bytecode;
    std::vector<std::uint8_t> renderConfiguration;
    std::vector<std::uint8_t> gpuContainersJson;
    std::vector<std::uint8_t> gpuProgramsJson;
    std::vector<SpawnablePackageInput> inputs;
    std::optional<std::vector<SpawnablePackageOutput>> outputs;
    std::optional<std::vector<SpawnablePackageCapture>> liveInputs;
    std::optional<std::vector<SpawnablePackageCapture>> capturedInputs;
    std::optional<std::vector<SpawnablePackageSignalPort>> signalInputs;
    std::optional<std::vector<SpawnablePackageSignalPort>> signalOutputs;
    std::optional<SpawnablePackageLifecycle> lifecycle;
};

struct SpawnablePackageGameIoPort final {
    std::array<std::uint8_t, 16> nodeId{};
    std::string name;
    std::uint8_t kind = 0;
    std::uint32_t width = 0;
    bool isColor = false;
    std::optional<std::vector<std::array<std::uint8_t, 16>>> runtimeNodeIds;
};

struct SpawnablePackageDestroyRoute final {
    std::array<std::uint8_t, 16> sourceContainerId{};
    std::array<std::uint8_t, 16> targetContainerId{};
    std::optional<std::array<std::uint8_t, 16>> delayNodeId;
    std::vector<SpawnablePackageCapture> captures;
};

struct SpawnablePackageSignalRoute final {
    std::array<std::uint8_t, 16> sourceContainerId{};
    std::array<std::uint8_t, 16> sourceNodeId{};
    std::array<std::uint8_t, 16> targetNodeId{};
};

struct SpawnablePackage final {
    std::uint32_t packageVersion = 0;
    bool autoDestroyWhenIdle = true;
    bool hasDeferredSignals = false;
    bool hasChildDeferredSignals = false;
    std::optional<std::array<std::uint8_t, 16>> onStartNodeId;
    std::optional<std::array<std::uint8_t, 16>> onDestroyNodeId;
    std::vector<SpawnablePackageGameIoPort> gameIoPorts;
    std::vector<std::uint8_t> parentBytecode;
    std::vector<std::uint8_t> rootRenderConfiguration;
    std::vector<std::uint8_t> rootGpuContainersJson;
    std::vector<std::uint8_t> rootGpuProgramsJson;
    std::vector<std::uint8_t> cpuPrograms;
    std::vector<SpawnablePackageTemplate> templates;
    std::vector<SpawnablePackageDestroyRoute> destroyRoutes;
    std::vector<SpawnablePackageSignalRoute> signalRoutes;
};

// Loads an LFXP package without engine or JSON dependencies. The output changes
// only on success. GPU snapshots remain opaque UTF-8 JSON host payloads.
LIBRA_RUNTIME_CORE_API auto loadSpawnablePackage(
    const std::uint8_t* bytes, std::size_t byteCount, SpawnablePackage& out) noexcept -> Result;

}
