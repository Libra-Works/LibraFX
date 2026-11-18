#pragma once

#include "liberafx/SpawnablePackage.h"

namespace liberafx::host {

// Transactional restart is intentionally narrower than package playback.
// No children, queued signals, external I/O, GPU state or destroy side effects
// may be lost by the atomic replacement of a root-render-only instance.
inline auto validateLiveRestart(const std::uint8_t* bytes, std::size_t count) noexcept -> Result
{
    auto package = SpawnablePackage{};
    auto result = loadSpawnablePackage(bytes, count, package);
    if (result != Result::Ok) return result;
    if (package.autoDestroyWhenIdle || package.hasDeferredSignals || package.hasChildDeferredSignals ||
        package.onStartNodeId || package.onDestroyNodeId || !package.templates.empty() ||
        !package.destroyRoutes.empty() || !package.signalRoutes.empty() || !package.gameIoPorts.empty())
        return Result::UnsupportedBinary;
    // The converter serializes absent GPU payloads as empty bytes or [].
    auto emptyPayload = [](const auto& payload, const char* canonical) {
        auto index = std::size_t{};
        for (auto byte : payload) {
            if (byte == ' ' || byte == '\t' || byte == '\r' || byte == '\n') continue;
            if (!canonical[index] || byte != canonical[index++]) return false;
        }
        return payload.empty() || !canonical[index];
    };
    if (!emptyPayload(package.rootGpuContainersJson, "[]") ||
        (!emptyPayload(package.rootGpuProgramsJson, "[]") &&
         !emptyPayload(package.rootGpuProgramsJson, "{\"version\":1,\"programs\":[]}")))
        return Result::UnsupportedBinary;
    auto program = Program{};
    result = Program::load(package.parentBytecode.data(), package.parentBytecode.size(), program);
    if (result != Result::Ok) return result;
    return program.version() <= 2 ? Result::Ok : Result::UnsupportedBinary;
}

}
