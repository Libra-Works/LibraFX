#include "liberafx/SpawnablePackage.h"
#include <algorithm>
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>
#include <string>
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
        require(argc == 3, "Usage: liberafx_spawnable_package_tests <compiler-produced .lfxp> <metadata .lfxp>");
        const auto bytes = read(argv[1]);
        auto package = liberafx::SpawnablePackage{};
        require(liberafx::loadSpawnablePackage(bytes.data(), bytes.size(), package) == liberafx::Result::Ok,
            "Could not decode compiler-produced LFXP.");
        require(package.packageVersion == 9 && package.parentBytecode.size() > 0 &&
            package.templates.size() == 2 && package.destroyRoutes.size() == 1 &&
            package.onStartNodeId.has_value() && package.autoDestroyWhenIdle,
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
            metadata.signalRoutes.size() == 1,
            "Decoded LFXP optional/runtime metadata is incomplete.");

        auto previous = package;
        auto truncated = bytes;
        truncated.pop_back();
        require(liberafx::loadSpawnablePackage(truncated.data(), truncated.size(), package) ==
            liberafx::Result::InvalidBinary && package.parentBytecode == previous.parentBytecode,
            "Truncated LFXP was accepted or changed the output package.");
        auto unsupported = bytes;
        unsupported[4] = 2;
        require(liberafx::loadSpawnablePackage(unsupported.data(), unsupported.size(), package) ==
            liberafx::Result::UnsupportedBinary && package.parentBytecode == previous.parentBytecode,
            "Unknown LFXP version was accepted or changed the output package.");
        std::cout << "Compiler-produced LFXP decode and malformed-input checks passed.\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
