#include "liberafx/Interop.h"
#include <array>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <filesystem>
#include <iostream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {
using Id = std::array<std::uint8_t, 16>;
void require(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
}
void check(LibraResult result)
{
    if (result != LIBRA_OK) throw std::runtime_error("Scoped C ABI result " + std::to_string(result));
}
auto read(const std::filesystem::path& path) -> std::vector<std::uint8_t>
{
    auto file = std::ifstream(path, std::ios::binary);
    require(file.good(), "Missing compiler-produced Fireworks fixture.");
    return {std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
}
struct Host final {
    LibraSpawnableHostHandle value = 0;
    ~Host() { if (value) (void)libra_spawnable_host_release(value); }
};
auto next(Host& host, std::uint32_t kind) -> LibraSpawnableHostEvent
{
    auto event = LibraSpawnableHostEvent{};
    auto has = std::uint32_t{};
    check(libra_spawnable_host_next_event(host.value, &event, &has));
    require(has == 1 && event.kind == kind, "Incorrect forwarded host event kind.");
    return event;
}
void empty(Host& host)
{
    auto event = LibraSpawnableHostEvent{};
    auto has = std::uint32_t{};
    check(libra_spawnable_host_next_event(host.value, &event, &has));
    require(!has, "Unexpected duplicate or synthetic natural lifecycle event.");
}
void run(const std::vector<std::uint8_t>& bytes, const Id& start, const Id& position)
{
    auto host = Host{};
    check(libra_spawnable_host_create(bytes.data(), bytes.size(), 32, 4 * 1024 * 1024,
        128, 10000, &host.value));
    check(libra_spawnable_host_advance(host.value, 0, 0));
    auto first = next(host, LIBRA_SPAWNABLE_EVENT_SPAWN);
    require(first.instanceId != 0, "Spawn did not preserve the full instance identity.");
    empty(host);

    // Both new entry points enforce the creating owner thread, without freeing
    // work or coalescing a notification on a foreign caller.
    auto wrongForce = LibraResult{LIBRA_OK}, wrongEnd = LibraResult{LIBRA_OK};
    auto wrongAdvance = LibraResult{LIBRA_OK};
    auto worker = std::thread([&] {
        wrongAdvance = libra_spawnable_host_advance(host.value, 1, 1);
        wrongForce = libra_spawnable_host_force_terminate(
            host.value, LIBRA_SPAWNABLE_SCOPE_EFFECT, nullptr, 0);
        wrongEnd = libra_spawnable_host_notify_end_request(
            host.value, LIBRA_SPAWNABLE_SCOPE_EFFECT, nullptr, 0, start.data());
    });
    worker.join();
    require(wrongForce == LIBRA_BUSY && wrongEnd == LIBRA_BUSY && wrongAdvance == LIBRA_BUSY,
        "Foreign thread must receive Busy even without an attached debugger.");
    empty(host);
    require(libra_spawnable_host_force_terminate(host.value, 99, nullptr, 0) == LIBRA_INVALID_ARGUMENT,
        "Unknown scope was not rejected.");
    require(libra_spawnable_host_force_terminate(host.value, LIBRA_SPAWNABLE_SCOPE_INSTANCE,
        nullptr, first.instanceId) == LIBRA_INVALID_ARGUMENT, "Missing container UUID was not rejected.");
    require(libra_spawnable_host_notify_end_request(host.value, LIBRA_SPAWNABLE_SCOPE_EFFECT,
        nullptr, 0, nullptr) == LIBRA_INVALID_ARGUMENT, "Missing signal entry was not rejected.");

    // The user's request entry here is OnStart, which spawns. It does NOT mean
    // natural end; the two arrivals must produce two distinct siblings.
    check(libra_spawnable_host_notify_end_request(host.value, LIBRA_SPAWNABLE_SCOPE_EFFECT,
        nullptr, 0, start.data()));
    auto second = next(host, LIBRA_SPAWNABLE_EVENT_SPAWN);
    check(libra_spawnable_host_notify_end_request(host.value, LIBRA_SPAWNABLE_SCOPE_EFFECT,
        nullptr, 0, start.data()));
    auto third = next(host, LIBRA_SPAWNABLE_EVENT_SPAWN);
    require(first.instanceId != second.instanceId && second.instanceId != third.instanceId &&
        std::memcmp(first.containerId, second.containerId, 16) == 0 &&
        std::memcmp(first.containerId, third.containerId, 16) == 0, "EndRequest arrivals lost identity.");
    auto count = std::uint64_t{};
    check(libra_spawnable_host_active_count(host.value, &count));
    require(count == 3, "EndRequest incorrectly stopped or merged an instance.");

    check(libra_spawnable_host_force_terminate(host.value, LIBRA_SPAWNABLE_SCOPE_INSTANCE,
        first.containerId, first.instanceId));
    auto forced = next(host, LIBRA_SPAWNABLE_EVENT_FORCE_TERMINATED);
    require(forced.instanceId == first.instanceId && forced.time == 0 &&
        std::memcmp(forced.containerId, first.containerId, 16) == 0,
        "Force event changed target identity or timestamp.");
    empty(host);
    check(libra_spawnable_host_force_terminate(host.value, LIBRA_SPAWNABLE_SCOPE_INSTANCE,
        first.containerId, first.instanceId));
    empty(host);
    auto value = LibraValue{};
    require(libra_spawnable_host_child_value(host.value, first.containerId, first.instanceId,
        position.data(), &value) == LIBRA_INVALID_HANDLE, "Forced child retained a readable snapshot.");
    check(libra_spawnable_host_child_value(host.value, second.containerId, second.instanceId,
        position.data(), &value));
    check(libra_spawnable_host_child_value(host.value, third.containerId, third.instanceId,
        position.data(), &value));
    check(libra_spawnable_host_active_count(host.value, &count));
    require(count == 2, "Target force affected a sibling.");

    // A natural-end request in progress must not prevent whole-effect force.
    check(libra_spawnable_host_request_natural_end(host.value));
    check(libra_spawnable_host_force_terminate(host.value, LIBRA_SPAWNABLE_SCOPE_EFFECT, nullptr, 0));
    forced = next(host, LIBRA_SPAWNABLE_EVENT_FORCE_TERMINATED);
    auto zero = Id{};
    require(!forced.instanceId && std::memcmp(forced.containerId, zero.data(), 16) == 0,
        "Whole-effect render invalidation did not preserve its zero identity.");
    empty(host); // no Destroy/End or sibling Stop synthesis
    check(libra_spawnable_host_force_terminate(host.value, LIBRA_SPAWNABLE_SCOPE_EFFECT, nullptr, 0));
    empty(host);
    check(libra_spawnable_host_active_count(host.value, &count));
    require(count == 0, "Whole-effect force retained active instances.");
    require(libra_spawnable_host_advance(host.value, 2, 2) == LIBRA_NOT_READY,
        "Forced host resumed evaluation.");
    require(libra_spawnable_host_notify_end_request(host.value, LIBRA_SPAWNABLE_SCOPE_EFFECT,
        nullptr, 0, start.data()) == LIBRA_NOT_READY, "Forced host accepted another EndRequest.");
    check(libra_spawnable_host_release(host.value));
    auto stale = host.value;
    host.value = 0;
    require(libra_spawnable_host_force_terminate(stale, LIBRA_SPAWNABLE_SCOPE_EFFECT, nullptr, 0) ==
        LIBRA_INVALID_HANDLE, "Released handle remained valid.");
}
}

int main(int argc, char** argv)
{
    auto directory = argc == 2 ? argv[1] : std::getenv("LIBERAFX_FORCE_FIXTURE_DIR");
    if (argc == 1 && (!directory || !*directory))
    {
        return 77;
    }
    try {
        require(argc == 1 || argc == 2,
            "Usage: liberafx_interop_scoped_termination_tests [isolated Fireworks fixture folder]");
        auto folder = std::filesystem::path(directory);
        auto ids = read(folder / "Fireworks.native-fixture");
        require(ids.size() == 8 + 11 * 16 && std::memcmp(ids.data(), "LFXF", 4) == 0 &&
            ids[4] == 1, "Invalid Fireworks identity fixture.");
        auto start = Id{}, position = Id{};
        std::memcpy(start.data(), ids.data() + 8, 16);
        std::memcpy(position.data(), ids.data() + 8 + 5 * 16, 16);
        run(read(folder / "Fireworks.lfxp"), start, position);
        std::cout << "Scoped force/EndRequest C ABI checks passed.\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
