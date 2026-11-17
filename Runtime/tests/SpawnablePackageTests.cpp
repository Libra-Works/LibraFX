#include "liberafx/SpawnablePackage.h"
#include "liberafx/SpawnableHost.h"
#include <algorithm>
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace {
void require(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
}

auto read(const std::string& path) -> std::vector<std::uint8_t>
{
    auto file = std::ifstream(path, std::ios::binary);
    require(file.good(), "Missing compiler-produced LFXP fixture.");
    return {std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
}
}

auto main(int argc, char** argv) -> int
{
    try {
        require(argc == 7, "Usage: liberafx_spawnable_package_tests <compiler-produced .lfxp> <metadata .lfxp> <legacy-v1 .lfxp> <legacy-v2 .lfxp> <gpu-programs .lfxp> <template-gameio .lfxp>");
        const auto bytes = read(argv[1]);
        auto package = liberafx::SpawnablePackage{};
        require(liberafx::loadSpawnablePackage(bytes.data(), bytes.size(), package) == liberafx::Result::Ok,
            "Could not decode compiler-produced LFXP.");
        require(package.packageVersion == 11 && package.parentBytecode.size() > 0 &&
            package.templates.size() == 2 && package.destroyRoutes.size() == 1 &&
            package.onStartNodeId.has_value() && package.autoDestroyWhenIdle &&
            !package.rootRenderConfiguration.empty() &&
            package.rootGpuContainersJson == std::vector<std::uint8_t>{'[', ']'} &&
            package.rootGpuProgramsJson == std::vector<std::uint8_t>{
                '{', '"', 'v', 'e', 'r', 's', 'i', 'o', 'n', '"', ':', '1', ',', '"',
                'p', 'r', 'o', 'g', 'r', 'a', 'm', 's', '"', ':', '[', ']', '}'},
            "Decoded LFXP lifecycle metadata is incomplete.");
        require(package.destroyRoutes[0].captures.size() == 1 &&
            std::any_of(package.templates.begin(), package.templates.end(), [&](const auto& item) {
                return package.destroyRoutes[0].sourceContainerId == item.containerId;
            }),
            "Decoded LFXP Destroy capture route is incorrect.");

        const auto metadataBytes = read(argv[2]);
        auto metadata = liberafx::SpawnablePackage{};
        require(liberafx::loadSpawnablePackage(metadataBytes.data(), metadataBytes.size(), metadata) ==
            liberafx::Result::Ok, "Could not decode LFXP metadata fixture.");
        require(!metadata.autoDestroyWhenIdle && metadata.hasDeferredSignals &&
            metadata.hasChildDeferredSignals && metadata.gameIoPorts.size() == 2 &&
            metadata.gameIoPorts[0].kind == 1 && metadata.gameIoPorts[0].width == 0 &&
            metadata.gameIoPorts[0].runtimeNodeIds.has_value() &&
            metadata.templates.size() == 1 && metadata.templates[0].inputs.size() == 1 &&
            metadata.templates[0].inputs[0].isLive &&
            metadata.templates[0].liveInputs.has_value() &&
            metadata.templates[0].capturedInputs.has_value() &&
            metadata.templates[0].signalInputs.has_value() &&
            metadata.templates[0].signalOutputs.has_value() &&
            metadata.templates[0].lifecycle.has_value() &&
            metadata.destroyRoutes.size() == 1 && metadata.destroyRoutes[0].delayNodeId.has_value() &&
            metadata.signalRoutes.size() == 1 &&
            metadata.rootRenderConfiguration == std::vector<std::uint8_t>{11, 12} &&
            metadata.rootGpuProgramsJson == std::vector<std::uint8_t>{
                '{', '"', 'v', 'e', 'r', 's', 'i', 'o', 'n', '"', ':', '1', ',', '"',
                'p', 'r', 'o', 'g', 'r', 'a', 'm', 's', '"', ':', '[', ']', '}'},
            "Decoded LFXP optional/runtime metadata is incomplete.");

        const auto legacyBytes = read(argv[3]);
        auto legacy = liberafx::SpawnablePackage{};
        require(liberafx::loadSpawnablePackage(legacyBytes.data(), legacyBytes.size(), legacy) ==
            liberafx::Result::Ok && legacy.packageVersion == 9 &&
            legacy.parentBytecode == package.parentBytecode && legacy.templates.size() == package.templates.size() &&
            legacy.rootRenderConfiguration.empty() && legacy.rootGpuContainersJson.empty() &&
            legacy.rootGpuProgramsJson.empty() &&
            std::all_of(legacy.templates.begin(), legacy.templates.end(),
                [](const auto& item) { return item.gpuProgramsJson.empty(); }),
            "Legacy v1 LFXP data changed or invented root/program payloads.");
        auto legacyHost = std::unique_ptr<liberafx::SpawnableHost>{};
        require(liberafx::SpawnableHost::create(legacy, {}, legacyHost) == liberafx::Result::Ok &&
            legacyHost != nullptr,
            "CPU-only v1 package was no longer usable by the native host.");

        const auto legacyV2Bytes = read(argv[4]);
        auto legacyV2 = liberafx::SpawnablePackage{};
        require(liberafx::loadSpawnablePackage(legacyV2Bytes.data(), legacyV2Bytes.size(), legacyV2) ==
            liberafx::Result::Ok && legacyV2.packageVersion == 10 &&
            legacyV2.rootRenderConfiguration == package.rootRenderConfiguration &&
            legacyV2.rootGpuContainersJson == package.rootGpuContainersJson &&
            legacyV2.rootGpuProgramsJson.empty() &&
            std::all_of(legacyV2.templates.begin(), legacyV2.templates.end(),
                [](const auto& item) { return item.gpuProgramsJson.empty(); }),
            "Legacy v2 layout changed or invented executable GPU payloads.");
        auto legacyV2Host = std::unique_ptr<liberafx::SpawnableHost>{};
        require(liberafx::SpawnableHost::create(legacyV2, {}, legacyV2Host) == liberafx::Result::Ok &&
            legacyV2Host != nullptr,
            "CPU-only v2 package was no longer usable by the native host.");

        const auto gpuBytes = read(argv[5]);
        auto gpu = liberafx::SpawnablePackage{};
        require(liberafx::loadSpawnablePackage(gpuBytes.data(), gpuBytes.size(), gpu) ==
            liberafx::Result::Ok && gpu.packageVersion == 11 && gpu.templates.size() == 1 &&
            gpu.rootGpuProgramsJson.size() > 0 && gpu.templates[0].gpuProgramsJson.size() > 0,
            "Could not decode the executable GPU package fixture.");
        const auto gpuContainersText = std::string(
            gpu.rootGpuContainersJson.begin(), gpu.rootGpuContainersJson.end());
        const auto rootProgramsText = std::string(gpu.rootGpuProgramsJson.begin(), gpu.rootGpuProgramsJson.end());
        const auto templateProgramsText = std::string(
            gpu.templates[0].gpuProgramsJson.begin(), gpu.templates[0].gpuProgramsJson.end());
        require(rootProgramsText.find("\"nodeId\"") != std::string::npos &&
            templateProgramsText.find("\"nodeId\"") != std::string::npos &&
            rootProgramsText.find("\"programs\":[") != std::string::npos &&
            templateProgramsText.find("\"programs\":[") != std::string::npos &&
            gpuContainersText.find("\"id\"") != std::string::npos &&
            rootProgramsText.find("\"unreal\"") != std::string::npos &&
            templateProgramsText.find("\"unityMetadataJson\"") != std::string::npos,
            "Executable GPU programs were not kept separate by root/template scope.");

        const auto templateGameIoBytes = read(argv[6]);
        auto templateGameIoPackage = liberafx::SpawnablePackage{};
        require(liberafx::loadSpawnablePackage(templateGameIoBytes.data(), templateGameIoBytes.size(),
            templateGameIoPackage) == liberafx::Result::Ok,
            "Could not decode the intentionally malformed template Game I/O fixture.");
        auto rejectedHost = std::unique_ptr<liberafx::SpawnableHost>{};
        require(liberafx::SpawnableHost::create(std::move(templateGameIoPackage), {}, rejectedHost) ==
                liberafx::Result::InvalidBinary && !rejectedHost,
            "Native Spawnable host accepted named Game I/O inside a template program.");

        auto previous = package;
        auto truncated = bytes;
        truncated.pop_back();
        require(liberafx::loadSpawnablePackage(truncated.data(), truncated.size(), package) ==
            liberafx::Result::InvalidBinary && package.parentBytecode == previous.parentBytecode &&
            package.rootRenderConfiguration == previous.rootRenderConfiguration,
            "Truncated LFXP was accepted or changed the output package.");
        auto truncatedRoot = bytes;
        truncatedRoot.pop_back();
        for (auto index = 0; index < 8; ++index)
            truncatedRoot[8 + index] = static_cast<std::uint8_t>(
                static_cast<std::uint64_t>(truncatedRoot.size()) >> (8 * index));
        require(liberafx::loadSpawnablePackage(truncatedRoot.data(), truncatedRoot.size(), package) ==
            liberafx::Result::InvalidBinary && package.rootRenderConfiguration == previous.rootRenderConfiguration,
            "Truncated root payload changed the output package.");
        auto unsupported = bytes;
        unsupported[4] = 255;
        require(liberafx::loadSpawnablePackage(unsupported.data(), unsupported.size(), package) ==
            liberafx::Result::UnsupportedBinary && package.parentBytecode == previous.parentBytecode,
            "Unknown LFXP version was accepted or changed the output package.");
        auto invalidTemplateGpu = gpuBytes;
        invalidTemplateGpu.back() = 0xff;
        require(liberafx::loadSpawnablePackage(invalidTemplateGpu.data(), invalidTemplateGpu.size(), package) ==
            liberafx::Result::InvalidBinary && package.rootRenderConfiguration == previous.rootRenderConfiguration,
            "Invalid template GPU UTF-8 was accepted or changed the output package.");
        auto invalidRootGpu = gpuBytes;
        const auto programTailBytes = std::size_t{4} + gpu.rootGpuProgramsJson.size() +
            std::size_t{4} + gpu.templates[0].gpuProgramsJson.size();
        const auto rootProgramsOffset = invalidRootGpu.size() - programTailBytes + sizeof(std::uint32_t);
        invalidRootGpu[rootProgramsOffset] = 0xff;
        require(liberafx::loadSpawnablePackage(invalidRootGpu.data(), invalidRootGpu.size(), package) ==
            liberafx::Result::InvalidBinary && package.rootGpuProgramsJson == previous.rootGpuProgramsJson,
            "Invalid root GPU UTF-8 was accepted or changed the output package.");
        auto mixed = bytes;
        mixed[4] = 1;
        require(liberafx::loadSpawnablePackage(mixed.data(), mixed.size(), package) ==
            liberafx::Result::UnsupportedBinary, "Mismatched container/package versions were accepted.");
        std::cout << "Compiler-produced LFXP decode and malformed-input checks passed.\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
