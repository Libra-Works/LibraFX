#include "liberafx/LbSpawnableHost.h"
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iostream>
#include <iterator>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
using Id = std::array<std::uint8_t, 16>;

void require(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
}

auto read(const std::string& path) -> std::vector<std::uint8_t>
{
    auto file = std::ifstream(path, std::ios::binary);
    require(file.good(), "Missing compiler-produced Fireworks fixture.");
    return {std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
}

auto readIds(const std::string& path) -> std::array<Id, 11>
{
    const auto bytes = read(path);
    require(bytes.size() == 8 + 11 * 16 && std::memcmp(bytes.data(), "LFXF", 4) == 0 &&
        bytes[4] == 1 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0,
        "Invalid Fireworks identity fixture.");
    auto ids = std::array<Id, 11>{};
    for (auto index = std::size_t{}; index < ids.size(); ++index)
        std::memcpy(ids[index].data(), bytes.data() + 8 + index * 16, 16);
    return ids;
}

auto nextEvent(liberafx::SpawnableHost& host) -> liberafx::SpawnableHostEvent
{
    auto event = liberafx::SpawnableHostEvent{};
    require(host.popEvent(event), "Expected SpawnableHost event is missing.");
    return event;
}

void requireValue(liberafx::SpawnableHost& host, const Id& container, std::uint64_t instance,
                  const Id& node, const std::array<double, 3>& expected)
{
    auto actual = std::array<double, 4>{};
    auto width = std::uint8_t{};
    require(host.childValue(container.data(), instance, node.data(), actual.data(), width) ==
            liberafx::Result::Ok && width == expected.size(),
        "Could not read the expected compiler-produced child value.");
    for (auto lane = std::size_t{}; lane < expected.size(); ++lane)
        require(std::abs(actual[lane] - expected[lane]) < 1e-8,
            "Compiler-produced child value does not match expected lifecycle output.");
}

auto createHost(const std::vector<std::uint8_t>& bytes, const liberafx::SpawnableHostOptions& options,
                std::unique_ptr<liberafx::SpawnableHost>& host) -> void
{
    auto package = liberafx::SpawnablePackage{};
    require(liberafx::loadSpawnablePackage(bytes.data(), bytes.size(), package) == liberafx::Result::Ok,
        "Could not decode compiler-produced LFXP.");
    const auto result = liberafx::SpawnableHost::create(std::move(package), options, host);
    if (result != liberafx::Result::Ok)
        throw std::runtime_error("Could not create SpawnableHost from compiler-produced LFXP: " +
            std::to_string(static_cast<unsigned>(result)));
}

void verifyNaturalLifecycle(const std::vector<std::uint8_t>& bytes, const std::array<Id, 11>& ids)
{
    auto host = std::unique_ptr<liberafx::SpawnableHost>{};
    createHost(bytes, {}, host);
    require(host->advance(0, 0) == liberafx::Result::Ok && host->activeCount() == 1,
        "Root OnStart must spawn one Launch instance.");
    const auto launchSpawn = nextEvent(*host);
    require(launchSpawn.kind == liberafx::SpawnableHostEventKind::Spawn &&
        std::memcmp(launchSpawn.containerId, ids[1].data(), 16) == 0 && launchSpawn.time == 0,
        "Root OnStart did not produce the expected Launch event.");

    require(host->advance(1, 1) == liberafx::Result::Ok,
        "SpawnableHost failed to advance the compiler-produced Launch.");
    requireValue(*host, ids[1], launchSpawn.instanceId, ids[5], {0, 0, 140});

    require(host->advance(2, 1) == liberafx::Result::Ok && host->activeCount() == 1,
        "Launch expiry must route one sibling Burst at its exact deadline.");
    const auto destroy = nextEvent(*host);
    const auto stopped = nextEvent(*host);
    const auto burstSpawn = nextEvent(*host);
    require(destroy.kind == liberafx::SpawnableHostEventKind::Destroy &&
        std::memcmp(destroy.containerId, ids[1].data(), 16) == 0 && destroy.time == 2 &&
        destroy.instanceId == launchSpawn.instanceId &&
        stopped.kind == liberafx::SpawnableHostEventKind::Stop &&
        std::memcmp(stopped.containerId, ids[1].data(), 16) == 0 && stopped.time == 2 &&
        stopped.instanceId == launchSpawn.instanceId &&
        burstSpawn.kind == liberafx::SpawnableHostEventKind::Spawn &&
        std::memcmp(burstSpawn.containerId, ids[6].data(), 16) == 0 && burstSpawn.time == 2,
        "Launch Destroy, Stop and Burst Spawn events are not ordered or timed correctly.");
    requireValue(*host, ids[6], burstSpawn.instanceId, ids[9], {0, 0, 260});
    requireValue(*host, ids[6], burstSpawn.instanceId, ids[10], {0, 0, 260});

    require(host->advance(3.5, 1.5) == liberafx::Result::Ok &&
        host->activeCount() == 0 && host->rootFinished(),
        "SpawnableHost must naturally finish after the final child Destroy.");
    const auto burstDestroy = nextEvent(*host);
    const auto burstStop = nextEvent(*host);
    require(burstDestroy.kind == liberafx::SpawnableHostEventKind::Destroy &&
        std::memcmp(burstDestroy.containerId, ids[6].data(), 16) == 0 &&
        burstDestroy.time == 3.5 && burstDestroy.instanceId == burstSpawn.instanceId,
        "Burst did not emit exactly one Destroy at its life deadline.");
    require(burstStop.kind == liberafx::SpawnableHostEventKind::Stop &&
        std::memcmp(burstStop.containerId, ids[6].data(), 16) == 0 &&
        burstStop.time == 3.5 && burstStop.instanceId == burstSpawn.instanceId,
        "Burst did not emit the matching cleanup event.");
    auto unexpected = liberafx::SpawnableHostEvent{};
    require(!host->popEvent(unexpected), "SpawnableHost emitted an unexpected lifecycle event.");
}

void verifyManualStop(const std::vector<std::uint8_t>& bytes)
{
    auto host = std::unique_ptr<liberafx::SpawnableHost>{};
    createHost(bytes, {}, host);
    require(host->advance(0, 0) == liberafx::Result::Ok, "Could not start manual-stop fixture.");
    const auto spawn = nextEvent(*host);
    require(spawn.kind == liberafx::SpawnableHostEventKind::Spawn,
        "Manual-stop fixture did not spawn its child.");
    require(host->stop() == liberafx::Result::Ok && host->activeCount() == 0,
        "Manual Stop did not release active child instances.");
    auto event = liberafx::SpawnableHostEvent{};
    auto foundDestroy = false;
    while (host->popEvent(event))
        foundDestroy = foundDestroy || event.kind == liberafx::SpawnableHostEventKind::Destroy;
    require(!foundDestroy, "Manual Stop must not publish natural Destroy events.");
}

void verifyCustomNodes(const std::vector<std::uint8_t>& bytes, const Id& container)
{
    using liberafx::Result;
    auto package = liberafx::SpawnablePackage{};
    require(liberafx::loadSpawnablePackage(bytes.data(), bytes.size(), package) == Result::Ok,
        "Could not decode the custom-node package.");
    auto host = std::unique_ptr<liberafx::SpawnableHost>{};
    require(liberafx::SpawnableHost::create(package, {}, host) == Result::UnknownCustomNode && !host,
        "An unregistered custom node must fail to load.");
    {
        auto descriptor = liberafx::CustomNodeDescriptor{};
        constexpr auto typeId = Id{0xc3, 0xde, 0x33, 0x77, 0x59, 0xae, 0x4a, 0x2d,
            0x89, 0x91, 0x7f, 0x38, 0xe5, 0xd8, 0x21, 0x11};
        std::memcpy(descriptor.typeId, typeId.data(), typeId.size());
        descriptor.abiVersion = 1;
        descriptor.execution = liberafx::CustomExecution::Parameter;
        descriptor.inputCount = 2;
        descriptor.inputWidths[0] = 1;
        descriptor.inputWidths[1] = 1;
        descriptor.outputWidth = 1;
        descriptor.function = [](liberafx::CustomNodeInvocation& invocation) noexcept {
            invocation.outputs[0] = invocation.inputs[0] * invocation.inputs[4] + 41;
            return Result::Ok;
        };
        require(liberafx::SpawnableHost::create(std::move(package), {}, &descriptor, 1, host) == Result::Ok,
            "The same custom descriptor must bind in both root and child programs.");
        descriptor.function = nullptr;
    }
    require(host->advance(0, 0) == Result::Ok && host->activeCount() == 1,
        "The custom-node fixture must spawn its child.");
    const auto spawn = nextEvent(*host);
    auto value = std::array<double, 4>{};
    auto width = std::uint8_t{};
    constexpr auto root = Id{0xa1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1};
    constexpr auto child = Id{0xa2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2};
    const auto rootResult = host->rootValue(root.data(), value.data(), width);
    require(rootResult == Result::Ok && width == 1 && std::abs(value[0] - 42) < 1e-5,
        ("Root custom output: status " + std::to_string(static_cast<int>(rootResult)) +
         ", width " + std::to_string(width) + ", value " + std::to_string(value[0])).c_str());
    require(host->childValue(container.data(), spawn.instanceId, child.data(), value.data(), width) ==
        Result::Ok && width == 1 && std::abs(value[0] - 42) < 1e-5,
        "The child did not execute its registered custom node.");
}

void verifyEventBackpressure(const std::vector<std::uint8_t>& bytes, const std::array<Id, 11>& ids)
{
    auto options = liberafx::SpawnableHostOptions{};
    options.eventCapacity = 1;
    auto host = std::unique_ptr<liberafx::SpawnableHost>{};
    createHost(bytes, options, host);
    require(host->advance(0, 0) == liberafx::Result::Ok,
        "Could not start event backpressure fixture.");
    auto result = host->advance(2, 0);
    require(result == liberafx::Result::QueueRejected,
        "A full host event queue must reject, not silently lose, lifecycle events.");
    auto events = std::vector<liberafx::SpawnableHostEvent>{};
    auto event = liberafx::SpawnableHostEvent{};
    while (host->popEvent(event)) events.push_back(event);
    for (auto attempt = 0u; attempt < 8 && result == liberafx::Result::QueueRejected; ++attempt) {
        result = host->advance(2, 0);
        while (host->popEvent(event)) events.push_back(event);
    }
    require(result == liberafx::Result::Ok && events.size() == 4 &&
        events[0].kind == liberafx::SpawnableHostEventKind::Spawn &&
        events[1].kind == liberafx::SpawnableHostEventKind::Destroy &&
        events[2].kind == liberafx::SpawnableHostEventKind::Stop &&
        events[3].kind == liberafx::SpawnableHostEventKind::Spawn &&
        std::memcmp(events[3].containerId, ids[6].data(), 16) == 0 &&
        host->activeCount() == 1,
        "Draining and retrying a backpressured lifecycle must resume without replay or event loss.");
}
}

auto main(int argc, char** argv) -> int
{
    try {
        require(argc == 2, "Usage: liberafx_spawnable_host_tests <isolated compiler-produced fixture folder>");
        const auto folder = std::string(argv[1]) + "/";
        const auto package = read(folder + "Fireworks.lfxp");
        const auto ids = readIds(folder + "Fireworks.native-fixture");
        verifyNaturalLifecycle(package, ids);
        verifyManualStop(package);
        verifyEventBackpressure(package, ids);
        verifyCustomNodes(read(folder + "CustomSpawnable.lfxp"), ids[1]);
        std::cout << "Compiler-produced LFXP SpawnableHost lifecycle checks passed.\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
