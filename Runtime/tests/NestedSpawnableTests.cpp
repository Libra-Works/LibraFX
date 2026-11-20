#include "liberafx/LbCpuParticles.h"
#include "liberafx/LbSpawnableHost.h"
#include <algorithm>
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

namespace liberafx::nested_spawnable_tests {
using Id = std::array<std::uint8_t, 16>;

void require(bool condition, const char* message)
{
    if (!condition)
    {
        throw std::runtime_error(message);
    }
}

auto parseId(const char* text) -> Id
{
    auto result = Id{};
    auto output = std::size_t{};
    auto high = -1;
    for (auto index = std::size_t{}; text[index]; ++index)
    {
        if (text[index] == '-')
        {
            continue;
        }
        auto digit = 0;
        if (text[index] >= '0' && text[index] <= '9')
        {
            digit = text[index] - '0';
        }
        else if (text[index] >= 'a' && text[index] <= 'f')
        {
            digit = text[index] - 'a' + 10;
        }
        else if (text[index] >= 'A' && text[index] <= 'F')
        {
            digit = text[index] - 'A' + 10;
        }
        else
        {
            throw std::runtime_error("Invalid UUID fixture constant.");
        }
        if (high < 0)
        {
            high = digit;
        }
        else
        {
            require(output < result.size(), "UUID fixture constant is too long.");
            result[output++] = static_cast<std::uint8_t>((high << 4) | digit);
            high = -1;
        }
    }
    require(output == result.size() && high < 0,
            "UUID fixture constant has an invalid length.");
    return result;
}

auto read(const std::string& path) -> std::vector<std::uint8_t>
{
    auto file = std::ifstream(path, std::ios::binary);
    require(file.good(), "Missing compiler-produced nested fixture.");
    return {std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
}

auto registry() -> std::vector<CustomNodeDescriptor>
{
    auto result = std::vector<CustomNodeDescriptor>{};
    auto emitterCount = std::size_t{};
    const auto* emitters = cpuEmitterNodes(emitterCount);
    if (emitterCount)
    {
        result.insert(result.end(), emitters, emitters + emitterCount);
    }
    return result;
}

auto createHost(const std::string& path,
                std::unique_ptr<SpawnableHost>& host) -> SpawnablePackage
{
    const auto bytes = read(path);
    auto package = SpawnablePackage{};
    require(loadSpawnablePackage(bytes.data(), bytes.size(), package) ==
                Result::Ok &&
                package.packageVersion == 13,
            "Native reader rejected a compiler-produced nested package.");
    const auto descriptors = registry();
    const auto result = SpawnableHost::create(
        std::move(package), {}, descriptors.data(), descriptors.size(), host);
    if (result != Result::Ok)
    {
        throw std::runtime_error("Native host rejected nested package with result " +
                                 std::to_string(static_cast<unsigned>(result)));
    }
    return host->package();
}

auto countScope(const SpawnableHost& host, const Id& container,
                GeneratorInstanceId instance, const Id& system) -> std::size_t
{
    auto scope = SpawnableScope{};
    scope.kind = SpawnableScopeKind::Instance;
    std::copy(container.begin(), container.end(), scope.containerId);
    scope.instanceId = instance;
    auto count = std::size_t{};
    require(host.cpuParticleCount(scope, system.data(), count) == Result::Ok,
            "Could not query the scoped CPU world.");
    return count;
}

auto childSpawn(const SpawnableHostEvent& event, const Id& container)
    -> bool
{
    return event.kind == SpawnableHostEventKind::Spawn &&
           std::memcmp(event.containerId, container.data(), 16) == 0;
}

void verifyCpuRetention(const std::string& path)
{
    auto host = std::unique_ptr<SpawnableHost>{};
    const auto package = createHost(path, host);
    const auto parentId = parseId("7c48aa90-2948-41ab-83de-d5bb346971e7");
    const auto childId = parseId("be66a9d1-da67-46ad-9443-fb191b058d61");
    const auto systemId = parseId("86dd68ee-bcba-4825-b23d-ba9e5fce5d0c");
    require(package.templates.size() == 2,
            "Retention fixture did not contain both static templates.");
    require(host->advance(0, 0) == Result::Ok,
            "Could not evaluate nested retention fixture at start.");
    auto parentInstance = GeneratorInstanceId{};
    auto childCount = 0u;
    auto event = SpawnableHostEvent{};
    while (host->popEvent(event))
    {
        if (childSpawn(event, parentId))
        {
            parentInstance = event.instanceId;
        }
        if (childSpawn(event, childId))
        {
            ++childCount;
        }
    }
    require(parentInstance != 0 && childCount == 1,
            "Parent OnStart did not spawn its dynamically owned child.");
    require(countScope(*host, parentId, parentInstance, systemId) == 3,
            "Parent CPU system did not emit its finite three-particle burst.");
    require(host->advance(1, 1) == Result::Ok,
            "Could not reach the parent's natural lifetime deadline.");
    require(countScope(*host, parentId, parentInstance, systemId) == 3 &&
                host->activeCount() == 2,
            "Natural parent expiry did not retain its descendant and CPU particles.");
    require(host->advance(4, 3) == Result::Ok &&
                countScope(*host, parentId, parentInstance, systemId) == 0 &&
                host->activeCount() == 2,
            "Parent CPU particles did not finish independently after emissions stopped.");
    const auto atTen = host->advance(10, 6);
    const auto finalActive = host->activeCount();
    require(atTen == Result::Ok && finalActive == 0 && host->rootFinished(),
            "Parent was not retired after its nested child completed.");
}

void verifyCaptureIsolation(const std::string& path)
{
    auto host = std::unique_ptr<SpawnableHost>{};
    (void)createHost(path, host);
    auto firstParent = parseId("d61771e6-8114-453d-a4b7-b3af3a1b9a16");
    auto secondParent = parseId("bb3a20aa-7021-425b-82db-8c54aa939420");
    auto firstChild = parseId("643631b3-ce5b-414f-a2e3-6fb5a313a634");
    auto secondChild = parseId("6b5a47a5-9e53-4ebd-966f-f2d576868f99");
    require(host->advance(0, 0) == Result::Ok,
            "Could not evaluate nested capture fixture at start.");
    auto children = std::array<GeneratorInstanceId, 2>{};
    auto parents = std::array<GeneratorInstanceId, 2>{};
    auto event = SpawnableHostEvent{};
    while (host->popEvent(event))
    {
        if (childSpawn(event, firstParent))
        {
            parents[0] = event.instanceId;
        }
        else if (childSpawn(event, secondParent))
        {
            parents[1] = event.instanceId;
        }
        else if (childSpawn(event, firstChild))
        {
            children[0] = event.instanceId;
        }
        else if (childSpawn(event, secondChild))
        {
            children[1] = event.instanceId;
        }
    }
    require(parents[0] && parents[1] && children[0] && children[1],
            "The two parent instances did not each spawn their own child.");
    const auto assertValue = [&](const Id& container, GeneratorInstanceId instance,
                                 const char* nodeText, double expected) {
        const auto node = parseId(nodeText);
        auto values = std::array<double, 4>{};
        auto width = std::uint8_t{};
        require(host->childValue(container.data(), instance, node.data(),
                                 values.data(), width) == Result::Ok &&
                    width == 1 && std::abs(values[0] - expected) < 1e-8,
                "Nested CaptureAtSpawn did not resolve from the immediate parent instance.");
    };
    const auto firstTargets = std::array<const char*, 5>{
        "78cba0c2-fc66-4fbc-81cc-b8a1941b3c92",
        "d9637a38-2464-4d5c-8e71-ca61a2c90ff0",
        "7ddd2565-01af-4aa3-bd08-4d907abf7bd6",
        "f58fdd59-95ea-4175-8e73-e63fc4018e06",
        "5ce0a336-4176-4c59-9e57-cf0d57df74f7"};
    const auto secondTargets = std::array<const char*, 5>{
        "86183d92-7ac1-46f5-a4cc-25532059704b",
        "5b3c1e8c-3f74-47fc-b2ff-e8ed84bbe845",
        "49e6eaa0-0368-45f3-b702-08924ad51588",
        "14b47f2e-8554-4195-8b97-cd6cf14eca77",
        "58e3f265-6e2d-4a4b-96a5-bec5d2a63eb8"};
    for (auto index = 0u; index < 5; ++index)
    {
        assertValue(firstChild, children[0], firstTargets[index], index + 1);
        assertValue(secondChild, children[1], secondTargets[index], index + 11);
    }
    require(host->advance(1, 1) == Result::Ok,
            "Could not update Live captures from each parent instance.");
    auto liveFirst = std::array<double, 4>{};
    auto liveSecond = std::array<double, 4>{};
    auto widthFirst = std::uint8_t{};
    auto widthSecond = std::uint8_t{};
    const auto liveFirstId = parseId("390c5ec6-a4c1-450d-9c52-fd5fba17c2c3");
    const auto liveSecondId = parseId("916841cd-5351-40bf-a17e-dddd55c3e699");
    const auto firstLiveResult =
        host->childValue(firstChild.data(), children[0], liveFirstId.data(),
                         liveFirst.data(), widthFirst);
    const auto secondLiveResult =
        host->childValue(secondChild.data(), children[1],
                         liveSecondId.data(), liveSecond.data(), widthSecond);
    require(firstLiveResult == Result::Ok && secondLiveResult == Result::Ok &&
                widthFirst == 1 && widthSecond == 1 &&
                liveFirst[0] == 1 && liveSecond[0] == 1,
            "Live inputs did not update to their immediate parents' time.");

    auto forcedScope = SpawnableScope{};
    forcedScope.kind = SpawnableScopeKind::Instance;
    std::copy(firstParent.begin(), firstParent.end(), forcedScope.containerId);
    forcedScope.instanceId = parents[0];
    require(host->forceTerminate(forcedScope) == Result::Ok &&
                host->activeCount() == 2,
            "Forcing one parent did not clear only its owned subtree.");
    auto forcedParent = false;
    auto forcedChild = false;
    while (host->popEvent(event))
    {
        require(event.kind == SpawnableHostEventKind::ForceTerminated,
                "Forced subtree cleanup emitted a natural lifecycle event.");
        forcedParent = forcedParent ||
                       (event.instanceId == parents[0] &&
                        std::memcmp(event.containerId, firstParent.data(), 16) ==
                            0);
        forcedChild = forcedChild ||
                      (event.instanceId == children[0] &&
                       std::memcmp(event.containerId, firstChild.data(), 16) ==
                           0);
    }
    const auto forcedChildValue =
        host->childValue(firstChild.data(), children[0], liveFirstId.data(),
                         liveFirst.data(), widthFirst);
    const auto siblingChildValue =
        host->childValue(secondChild.data(), children[1], liveSecondId.data(),
                         liveSecond.data(), widthSecond);
    require(forcedParent && forcedChild &&
                forcedChildValue == Result::InvalidHandle &&
                siblingChildValue == Result::Ok,
            "Subtree force leaked retired scopes or affected the sibling parent.");
}

void verifySameParentTemplateCapture(const std::string& path)
{
    auto host = std::unique_ptr<SpawnableHost>{};
    (void)createHost(path, host);
    const auto parentId = parseId("30000000-0000-4000-8000-000000000004");
    const auto childId = parseId("30000000-0000-4000-8000-000000000007");
    const auto childValueId = parseId("30000000-0000-4000-8000-000000000009");
    require(host->advance(0, 0) == Result::Ok,
            "Could not evaluate repeated same-template spawn fixture.");
    auto parentSpawns = 0u;
    auto childInstances = std::array<GeneratorInstanceId, 2>{};
    auto childCount = 0u;
    auto event = SpawnableHostEvent{};
    while (host->popEvent(event))
    {
        if (childSpawn(event, parentId))
        {
            ++parentSpawns;
        }
        else if (childSpawn(event, childId) && childCount < childInstances.size())
        {
            childInstances[childCount++] = event.instanceId;
        }
    }
    require(parentSpawns == 2 && childCount == 2 &&
                childInstances[0] != childInstances[1],
            "Repeated same-template spawns did not create separate dynamic child scopes.");
    for (auto index = 0u; index < childInstances.size(); ++index)
    {
        auto value = std::array<double, 4>{};
        auto width = std::uint8_t{};
        require(host->childValue(childId.data(), childInstances[index],
                                 childValueId.data(), value.data(), width) ==
                    Result::Ok &&
                    width == 1 && value[0] == static_cast<double>(index + 1),
                "Repeated same-parent spawns did not preserve their individual captures.");
    }
}

void verifyNestedCpuOnlyRetention(const std::string& path)
{
    auto host = std::unique_ptr<SpawnableHost>{};
    (void)createHost(path, host);
    const auto parentId = parseId("40000000-0000-4000-8000-000000000002");
    const auto childId = parseId("40000000-0000-4000-8000-000000000005");
    const auto systemId = parseId("40000000-0000-4000-8000-000000000008");
    require(host->advance(0, 0) == Result::Ok,
            "Could not evaluate nested CPU-only retention fixture.");
    auto parentInstance = GeneratorInstanceId{};
    auto childInstance = GeneratorInstanceId{};
    auto event = SpawnableHostEvent{};
    while (host->popEvent(event))
    {
        if (childSpawn(event, parentId))
        {
            parentInstance = event.instanceId;
        }
        else if (childSpawn(event, childId))
        {
            childInstance = event.instanceId;
        }
    }
    const auto initialCpuCount =
        childInstance ? countScope(*host, childId, childInstance, systemId) : 0;
    require(parentInstance && childInstance && initialCpuCount == 3,
            "Nested CPU system did not emit its finite burst.");
    auto previous = 0.0;
    for (auto tick = 1u; tick <= 39; ++tick)
    {
        const auto now = tick / 10.0;
        require(host->advance(now, now - previous) == Result::Ok,
                "Could not advance the nested CPU render lifetime.");
        previous = now;
        require(host->activeCount() == 2 &&
                    countScope(*host, childId, childInstance, systemId) == 3,
                "A scope or its particles retired before 4s.");
        while (host->popEvent(event))
        {
            require(event.kind != SpawnableHostEventKind::Destroy &&
                        event.kind != SpawnableHostEventKind::Stop,
                    "A retirement notice invalidated a scope with live CPU particles.");
        }
    }
    require(host->advance(4.1, 4.1 - previous) == Result::Ok &&
                host->activeCount() == 0 && host->rootFinished(),
            "CPU-only subtree did not retire after its last particle expired.");
    auto expected = std::array<SpawnableHostEventKind, 4>{
        SpawnableHostEventKind::Destroy, SpawnableHostEventKind::Stop,
        SpawnableHostEventKind::Destroy, SpawnableHostEventKind::Stop};
    for (auto index = 0u; index < expected.size(); ++index)
    {
        const auto& container = index < 2 ? childId : parentId;
        require(host->popEvent(event) && event.kind == expected[index] &&
                    std::memcmp(event.containerId, container.data(), 16) == 0 &&
                    event.instanceId == (index < 2 ? childInstance : parentInstance) &&
                    event.time >= 4,
                "The child and parent did not retire in order after the CPU drain.");
    }
    require(!host->popEvent(event), "Duplicate CPU subtree retirement notice.");
}

void verifyTerminalCpuEvents(const std::string& path)
{
    auto host = std::unique_ptr<SpawnableHost>{};
    (void)createHost(path, host);
    const auto child = parseId("40000000-0000-4000-8000-000000000005");
    auto scope = SpawnableScope{};
    scope.kind = SpawnableScopeKind::Instance;
    std::copy(child.begin(), child.end(), scope.containerId);
    require(host->advance(0, 0) == Result::Ok, "Could not start CPU event fixture.");
    auto lifecycle = SpawnableHostEvent{};
    while (host->popEvent(lifecycle))
    {
        if (childSpawn(lifecycle, child))
        {
            scope.instanceId = lifecycle.instanceId;
        }
    }
    require(scope.instanceId && host->hasCpuParticleEvents(),
            "Aggregate predicate did not report a live child queue.");
    require(host->advance(1, 1) == Result::Ok &&
                host->advance(2, 1) == Result::Ok &&
                host->advance(3, 1) == Result::Ok &&
                host->advance(4, 1) == Result::Ok && host->rootFinished(),
            "CPU event fixture did not finish naturally.");
    auto event = CpuParticleEvent{};
    require(!host->popCpuParticleEvent(event) && host->hasCpuParticleEvents(),
            "An empty root pop hid a retired child queue.");
    auto events = 0u;
    while (host->popCpuParticleEvent(scope, event))
    {
        ++events;
    }
    require(events == 12 && !host->hasCpuParticleEvents(),
            "Aggregate predicate did not track the terminal drain.");
}
} // namespace liberafx::nested_spawnable_tests

auto main(int argc, char** argv) -> int
{
    using namespace liberafx::nested_spawnable_tests;
    try
    {
        require(argc == 5 || argc == 6,
                "Usage: liberafx_nested_spawnable_tests <retention.lfxp> <capture.lfxp> <same-parent.lfxp> <cpu-retention.lfxp> [cpu-events.lfxp]");
        verifyCpuRetention(argv[1]);
        verifyCaptureIsolation(argv[2]);
        verifySameParentTemplateCapture(argv[3]);
        verifyNestedCpuOnlyRetention(argv[4]);
        if (argc == 6)
        {
            verifyTerminalCpuEvents(argv[5]);
        }
        std::cout << "Nested Spawnable ownership and CPU retention fixtures passed.\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
