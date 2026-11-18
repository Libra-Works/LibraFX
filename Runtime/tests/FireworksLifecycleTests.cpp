#include "liberafx/LbInterop.h"
#include <array>
#include <cmath>
#include <cstring>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
using Id = std::array<std::uint8_t, 16>;
void require(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
}
void check(LibraResult result)
{
    if (result != LIBRA_OK) throw std::runtime_error("Native Fireworks result " + std::to_string(result));
}
auto read(const std::string& path) -> std::vector<std::uint8_t>
{
    auto file = std::ifstream(path, std::ios::binary);
    require(file.good(), "Missing converter/editor-produced Fireworks fixture.");
    return {std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
}
void near(const LibraValue& value, double x, double y, double z)
{
    require(value.componentCount == 3 && std::abs(value.x - x) < 1e-8 &&
        std::abs(value.y - y) < 1e-8 && std::abs(value.z - z) < 1e-8, "Incorrect compiled particle position.");
}
auto event(LibraGeneratorPool pool, std::uint32_t kind) -> LibraGeneratorEvent
{
    auto value = LibraGeneratorEvent{};
    auto has = std::uint32_t{};
    check(libra_generator_next_event(pool, &value, &has));
    require(has == 1 && value.kind == kind, "Incorrect lifecycle event.");
    return value;
}
void empty(LibraGeneratorPool pool)
{
    auto value = LibraGeneratorEvent{};
    auto has = std::uint32_t{};
    check(libra_generator_next_event(pool, &value, &has));
    require(has == 0, "Unexpected repeated Destroy or Stop event.");
}
}

int main(int argc, char** argv)
{
    try {
        require(argc == 2, "Usage: liberafx_fireworks_lifecycle_tests <isolated compiled sample folder>");
        auto folder = std::string(argv[1]) + "/";
        auto fixture = read(folder + "Fireworks.native-fixture");
        require(fixture.size() == 8 + 11 * 16 && std::memcmp(fixture.data(), "LFXF", 4) == 0 &&
            fixture[4] == 1 && fixture[5] == 0 && fixture[6] == 0 && fixture[7] == 0, "Invalid test identity fixture.");
        auto ids = std::array<Id, 11>{};
        for (auto index = std::size_t{}; index < ids.size(); ++index)
            std::memcpy(ids[index].data(), fixture.data() + 8 + index * 16, 16);
        auto launchBytes = read(folder + "Launch.lfxb");
        auto burstBytes = read(folder + "Burst.lfxb");
        auto parentBytes = read(folder + "parent.lfxb");
        auto launchProgram = LibraProgram{}, burstProgram = LibraProgram{}, parentProgram = LibraProgram{};
        check(libra_program_create(launchBytes.data(), launchBytes.size(), &launchProgram));
        check(libra_program_create(burstBytes.data(), burstBytes.size(), &burstProgram));
        auto launch = LibraGeneratorPool{}, burst = LibraGeneratorPool{};
        check(libra_generator_pool_create(launchProgram, ids[1].data(), 2, 1024 * 1024, 16, &launch));
        check(libra_generator_pool_create(burstProgram, ids[6].data(), 2, 1024 * 1024, 16, &burst));
        auto lifecycle = LibraGeneratorLifecycle{};
        lifecycle.flags = LIBRA_LIFECYCLE_LIFE;
        std::memcpy(lifecycle.lifeNodeId, ids[3].data(), 16);
        check(libra_generator_configure_lifecycle(launch, &lifecycle));
        std::memcpy(lifecycle.lifeNodeId, ids[8].data(), 16);
        check(libra_generator_configure_lifecycle(burst, &lifecycle));
        auto bindings = std::array<LibraSpawnableBinding, 2>{};
        for (auto index = 0u; index < 2; ++index) {
            auto& binding = bindings[index];
            auto base = index == 0 ? 1u : 6u;
            std::memcpy(binding.containerId, ids[base].data(), 16);
            std::memcpy(binding.signalTypeId, ids[base + 1].data(), 16);
            std::memcpy(binding.templateNodeIds[0], ids[base + 3].data(), 16);
            binding.pool = index == 0 ? launch : burst;
            binding.inputCount = 1; binding.widths[0] = 3;
        }
        check(libra_program_create_spawnable(parentBytes.data(), parentBytes.size(),
            bindings.data(), bindings.size(), &parentProgram));
        auto parent = LibraInstance{};
        check(libra_instance_create(parentProgram, &parent));
        check(libra_evaluate(parent, 0, nullptr, 0, nullptr));
        check(libra_dispatch_signal(parent, ids[0].data(), 0, 10000));
        auto processed = std::uint32_t{};
        check(libra_generator_process_spawns(launch, 16, &processed));
        require(processed == 1, "OnStart must enqueue Launch exactly once.");
        auto launchSpawn = event(launch, 1);
        check(libra_generator_start(launch, launchSpawn.instanceId));
        empty(burst);
        auto value = LibraValue{};
        check(libra_generator_value(launch, launchSpawn.instanceId, ids[5].data(), &value));
        near(value, 0, 0, 20);
        check(libra_generator_evaluate(launch, launchSpawn.instanceId, 1));
        check(libra_generator_value(launch, launchSpawn.instanceId, ids[5].data(), &value));
        near(value, 0, 0, 140);
        check(libra_generator_evaluate(launch, launchSpawn.instanceId, 2.5));
        auto destroy = event(launch, 3);
        require(destroy.instanceId == launchSpawn.instanceId, "Destroy identity mismatch.");
        empty(launch);
        check(libra_generator_value(launch, destroy.instanceId, ids[5].data(), &value));
        near(value, 0, 0, 260); // Clamped to 2 seconds, not overshooting frame.
        auto captures = std::array<LibraGeneratorCapture, 3>{};
        auto finalPosition = std::array<double, 3>{value.x, value.y, value.z};
        for (auto lane = 0u; lane < captures.size(); ++lane) {
            std::memcpy(captures[lane].nodeId, ids[9].data(), 16);
            captures[lane].component = lane;
            captures[lane].value = finalPosition[lane];
        }
        auto burstId = LibraGeneratorInstance{};
        check(libra_generator_spawn(burst, 2, captures.data(), captures.size(), &burstId));
        auto burstSpawn = event(burst, 1);
        require(burstSpawn.startTime == 2, "Burst must spawn at natural expiry time.");
        check(libra_generator_start(burst, burstId));
        // Final frozen lanes were routed before release.
        check(libra_generator_finalize(launch, destroy.instanceId));
        event(launch, 2);
        require(libra_generator_value(launch, destroy.instanceId, ids[5].data(), &value) == LIBRA_INVALID_HANDLE,
            "Finalized launch should no longer be readable.");
        check(libra_generator_evaluate(burst, burstId, 2.5));
        check(libra_generator_value(burst, burstId, ids[9].data(), &value));
        near(value, 0, 0, 260);
        check(libra_generator_value(burst, burstId, ids[10].data(), &value));
        near(value, 55, 0, 230);
        check(libra_generator_evaluate(burst, burstId, 3.5));
        event(burst, 3);
        check(libra_generator_finalize(burst, burstId));
        event(burst, 2);
        empty(burst);
        // Manual Stop and Reset never generate Destroy.
        check(libra_generator_spawn(launch, 10, nullptr, 0, &launchSpawn.instanceId));
        event(launch, 1);
        check(libra_generator_start(launch, launchSpawn.instanceId));
        check(libra_generator_stop(launch, launchSpawn.instanceId));
        event(launch, 2); empty(launch);
        check(libra_generator_spawn(launch, 20, nullptr, 0, &launchSpawn.instanceId));
        event(launch, 1);
        check(libra_generator_start(launch, launchSpawn.instanceId));
        check(libra_generator_reset(launch));
        event(launch, 2); empty(launch);

        auto managedParent = LibraInstance{};
        check(libra_instance_create(parentProgram, &managedParent));
        auto rootLifetime = LibraLifetimeSample{};
        check(libra_effect_start(managedParent, 1, 0, ids[0].data(), 10000, &rootLifetime));
        require(rootLifetime.age == 0 && !rootLifetime.expired, "Incorrect initial root lifetime.");
        require(libra_effect_start(managedParent, 1, 0, ids[0].data(), 10000, &rootLifetime) == LIBRA_NOT_READY,
            "Root OnStart must not execute twice.");
        check(libra_generator_process_spawns(launch, 16, &processed));
        require(processed == 1, "Managed root OnStart did not enqueue exactly one Launch.");
        auto managedSpawn = event(launch, 1);
        check(libra_generator_start(launch, managedSpawn.instanceId));
        check(libra_effect_advance(managedParent, 2, &rootLifetime));
        require(rootLifetime.age == 1 && rootLifetime.expired, "Root final evaluation must clamp to expiry.");
        require(libra_effect_advance(managedParent, 2, &rootLifetime) == LIBRA_NOT_READY,
            "Root expiry must only be reported once.");
        check(libra_generator_stop(launch, managedSpawn.instanceId));
        event(launch, 2); empty(launch);
        check(libra_instance_release(managedParent));
        check(libra_instance_release(parent));
        check(libra_program_release(parentProgram));
        check(libra_generator_pool_release(launch));
        check(libra_generator_pool_release(burst));
        check(libra_program_release(launchProgram));
        check(libra_program_release(burstProgram));
        std::cout << "Compiled Fireworks native PASS: OnStart -> rising Launch -> natural Destroy at z=260 -> captured Burst -> expiry; Stop/Reset no Destroy.\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
