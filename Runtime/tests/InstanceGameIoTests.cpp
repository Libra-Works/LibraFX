#include "liberafx/Interop.h"
#include <array>
#include <cmath>
#include <cstdlib>
#include <cstdint>
#include <cstring>
#include <filesystem>
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
auto read(const std::filesystem::path& path) -> std::vector<std::uint8_t>
{
    auto file = std::ifstream(path, std::ios::binary);
    require(file.good(), "Missing compiler-produced instance Game I/O fixture.");
    return {std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
}
struct Host final {
    LibraSpawnableHostHandle value = 0;
    explicit Host(const std::vector<std::uint8_t>& bytes)
    {
        require(libra_spawnable_host_create(bytes.data(), bytes.size(), 8, 4 * 1024 * 1024,
            32, 10000, &value) == LIBRA_OK, "Cannot load instance Game I/O package.");
    }
    ~Host() { if (value) libra_spawnable_host_release(value); }
};
auto spawn(Host& host) -> LibraGeneratorInstance
{
    require(libra_spawnable_host_fire_game_signal(host.value, "spawn") == LIBRA_OK, "Spawn failed.");
    auto event = LibraSpawnableHostEvent{};
    auto has = std::uint32_t{};
    while (libra_spawnable_host_next_event(host.value, &event, &has) == LIBRA_OK && has)
        if (event.kind == 1) return event.instanceId;
    throw std::runtime_error("Spawn identity was not published.");
}
}

auto main() -> int
{
    const auto* folder = std::getenv("LIBERAFX_INSTANCE_GAMEIO_FIXTURES");
    if (!folder || !*folder) return 77;
    try {
        const auto bytes = read(std::filesystem::path(folder) / "InstanceGameIo.lfxp");
        const auto container = read(std::filesystem::path(folder) / "InstanceGameIo.container");
        require(container.size() == 16, "Invalid instance container identity.");
        auto host = Host(bytes);
        require(libra_spawnable_host_advance(host.value, 0, 0) == LIBRA_OK, "Initial advance failed.");
        const auto first = spawn(host);
        const auto second = spawn(host);
        auto generation = std::uint64_t{};
        require(libra_spawnable_host_instance_target(host.value, container.data(), first, &generation) == LIBRA_OK &&
            generation != 0, "Cannot acquire generation-safe instance target.");
        const auto speed = 42.0;
        require(libra_spawnable_host_set_instance_property(host.value, generation, container.data(),
            first, "speed", &speed, 1) == LIBRA_OK, "Instance input update failed.");
        auto matrix = std::array<double, 16>{};
        for (auto index = 0u; index < 16; ++index) matrix[index] = index % 5 == 0 ? 1 : 0;
        matrix[3] = 123;
        require(libra_spawnable_host_set_instance_property(host.value, generation, container.data(),
            first, "matrix", matrix.data(), 16) == LIBRA_OK, "Matrix input update failed.");
        require(libra_spawnable_host_advance(host.value, 1, 1) == LIBRA_OK, "Instance evaluation failed.");
        auto values = std::array<double, 16>{};
        auto width = std::uint32_t{};
        require(libra_spawnable_host_get_instance_property(host.value, generation, container.data(),
            first, "result", values.data(), &width) == LIBRA_OK && width == 1 && values[0] == speed,
            "Targeted property did not reach its output.");
        require(libra_spawnable_host_get_instance_property(host.value, generation, container.data(),
            second, "result", values.data(), &width) == LIBRA_OK && values[0] == 0,
            "Targeted property leaked into its sibling.");
        require(libra_spawnable_host_get_instance_property(host.value, generation, container.data(),
            first, "matrixResult", values.data(), &width) == LIBRA_OK && width == 16 && values == matrix,
            "Matrix rows did not retain the instance-local override.");
        require(libra_spawnable_host_get_instance_property(host.value, generation, container.data(),
            second, "matrixResult", values.data(), &width) == LIBRA_OK && width == 16 &&
            values[0] == 1 && values[5] == 1 && values[10] == 1 && values[15] == 1 && values[3] == 0,
            "An untouched sibling must retain the identity Matrix default.");
        require(libra_spawnable_host_fire_instance_signal(host.value, generation, container.data(),
            first, "ping") == LIBRA_OK, "Targeted signal failed.");
        auto event = LibraGameIoEventV2{};
        auto has = std::uint32_t{};
        require(libra_spawnable_host_next_game_io_event(host.value, &event, &has) == LIBRA_OK &&
            has && event.instanceId == first && std::memcmp(event.containerId, container.data(), 16) == 0,
            "Output signal lost its instance identity.");
        auto name = std::array<std::uint8_t, 128>{};
        auto size = std::uint64_t{};
        require(libra_spawnable_host_copy_instance_game_io_port_name(host.value, container.data(),
            event.portIndex, name.data(), name.size(), &size) == LIBRA_OK &&
            std::string(reinterpret_cast<const char*>(name.data()), size) == "pong",
            "Output signal name resolved against the wrong port table.");
        const auto drainHost = [&] {
            auto notice = LibraSpawnableHostEvent{};
            while (libra_spawnable_host_next_event(host.value, &notice, &has) == LIBRA_OK && has) {}
        };
        const auto drainGameIo = [&] {
            auto count = 0u;
            while (libra_spawnable_host_next_game_io_event(host.value, &event, &has) == LIBRA_OK && has) ++count;
            return count;
        };
        drainHost();
        for (auto index = 0u; index < 32; ++index) {
            require(libra_spawnable_host_fire_instance_signal(host.value, generation, container.data(),
                first, "ping") == LIBRA_OK, "A bounded signal queue rejected too early.");
            drainHost();
        }
        require(libra_spawnable_host_fire_instance_signal(host.value, generation, container.data(),
            first, "ping") == LIBRA_QUEUE_REJECTED && drainGameIo() == 32,
            "Instance I/O must apply bounded backpressure without losing published signals.");
        require(libra_spawnable_host_advance(host.value, 1, 0) == LIBRA_OK && drainGameIo() == 1,
            "Retrying a pending advance must publish the deferred signal exactly once.");
        drainHost();
        auto other = Host(bytes);
        require(libra_spawnable_host_advance(other.value, 0, 0) == LIBRA_OK, "Other host failed.");
        const auto otherInstance = spawn(other);
        require(libra_spawnable_host_set_instance_property(other.value, generation, container.data(),
            otherInstance, "speed", &speed, 1) == LIBRA_INVALID_HANDLE, "Cross-host generation was accepted.");
        require(libra_spawnable_host_fire_instance_signal(host.value, generation, container.data(),
            first, "ping") == LIBRA_OK &&
            libra_spawnable_host_fire_instance_signal(host.value, generation, container.data(),
            second, "ping") == LIBRA_OK, "Cannot queue the Force-cleanup fixture.");
        require(libra_spawnable_host_force_terminate(host.value, LIBRA_SPAWNABLE_SCOPE_INSTANCE,
            container.data(), first) == LIBRA_OK, "Force cleanup failed.");
        require(libra_spawnable_host_next_game_io_event(host.value, &event, &has) == LIBRA_OK &&
            has && event.instanceId == second && drainGameIo() == 0,
            "Force must discard only the selected instance's undelivered I/O.");
        require(libra_spawnable_host_set_instance_property(host.value, generation, container.data(),
            first, "speed", &speed, 1) == LIBRA_INVALID_HANDLE, "A retired instance remained writable.");
        require(libra_spawnable_host_fire_instance_signal(host.value, generation, container.data(),
            second, "ping") == LIBRA_OK && libra_spawnable_host_stop(host.value) == LIBRA_OK &&
            drainGameIo() == 0, "Manual Stop must retire live instances and clear their queued I/O.");
        require(libra_spawnable_host_get_instance_property(host.value, generation, container.data(),
            second, "result", values.data(), &width) == LIBRA_INVALID_HANDLE,
            "A manually stopped instance remained readable.");
        std::cout << "Compiler-produced instance Game I/O isolation, Matrix, signal and generation checks passed.\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
