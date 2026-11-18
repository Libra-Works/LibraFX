#pragma pack(push, 1)
#include "liberafx/LbInterop.h"
#pragma pack(pop)
#include "liberafx/LbExecutionDebugInterop.h"
#if defined(_WIN32)
#define NOMINMAX
#include <Windows.h>
#include <chrono>
#include <thread>
#endif
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <array>
#include <cassert>
#include <cmath>
#include <cstdlib>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <limits>
#include <string>
#include <vector>
#if defined(_MSC_VER) && defined(_DEBUG)
#include <crtdbg.h>
#endif

namespace {
void write32(std::vector<std::uint8_t>& bytes, std::size_t offset, std::uint32_t value)
{
    for (auto i = 0u; i < 4; ++i) bytes[offset + i] = static_cast<std::uint8_t>(value >> (8 * i));
}
void number(std::vector<std::uint8_t>& bytes, std::size_t offset, double value)
{
    auto bits = std::uint64_t{};
    std::memcpy(&bits, &value, sizeof(bits));
    for (auto i = 0u; i < 8; ++i) bytes[offset + i] = static_cast<std::uint8_t>(bits >> (8 * i));
}
auto scalarFixture() -> std::vector<std::uint8_t>
{
    auto bytes = std::vector<std::uint8_t>(12 + 44 * 3);
    std::memcpy(bytes.data(), "LFXB", 4);
    bytes[4] = 1; bytes[6] = 1; bytes[8] = 3;
    for (auto i = 0u; i < 3; ++i) {
        auto offset = 12 + i * 44;
        bytes[offset + 15] = static_cast<std::uint8_t>(i + 1);
        bytes[offset + 16] = static_cast<std::uint8_t>(i + 1);
        write32(bytes, offset + 20, UINT32_MAX);
        write32(bytes, offset + 24, UINT32_MAX);
    }
    number(bytes, 12 + 28, 3);
    write32(bytes, 12 + 88 + 20, 0);
    write32(bytes, 12 + 88 + 24, 1);
    return bytes;
}
auto builtinFixture() -> std::vector<std::uint8_t>
{
    auto bytes = std::vector<std::uint8_t>(272);
    std::memcpy(bytes.data(), "LFXB", 4);
    bytes[4] = 3; bytes[6] = 1; bytes[8] = 1; bytes[13] = 1;
    bytes[31] = 1; bytes[32] = 40; bytes[33] = 1; bytes[34] = 1;
    const std::uint8_t id[16] = {0x74, 0x30, 0x5a, 0xc1, 0x91, 0xb2, 0x4c, 0x33, 0x8d, 0x44, 0, 0, 0, 0, 0, 1};
    std::memcpy(bytes.data() + 40, id, 16); // Built-in scalar Abs, ABI 1.
    write32(bytes, 56, 1);
    for (auto i = 0u; i < 4; ++i) {
        write32(bytes, 64 + i * 8, UINT32_MAX);
        write32(bytes, 224 + i * 4, UINT32_MAX);
    }
    number(bytes, 96, -8);
    return bytes;
}
auto lifecycleFixture() -> std::vector<std::uint8_t>
{
    auto bytes = std::vector<std::uint8_t>(16 + 2 * 256);
    std::memcpy(bytes.data(), "LFXB", 4);
    bytes[4] = 3; bytes[6] = 1; bytes[8] = 2; bytes[13] = 1;
    bytes[31] = 1; bytes[32] = 1; bytes[33] = 1;
    write32(bytes, 16 + 24, UINT32_MAX);
    write32(bytes, 16 + 28, UINT32_MAX);
    number(bytes, 16 + 32, 3);
    auto signal = std::size_t{16 + 256};
    bytes[signal + 15] = 2; bytes[signal + 16] = 41; bytes[signal + 19] = 1;
    const std::uint8_t type[] = {0x98, 0x9f, 0xa5, 0xb5, 0x48, 0x5f, 0x4e, 0x75,
                                 0x85, 0x42, 0x9c, 0x55, 0, 0, 0, 1};
    std::memcpy(bytes.data() + signal + 24, type, 16);
    write32(bytes, signal + 40, 1);
    for (auto i = 0u; i < 4; ++i) {
        write32(bytes, signal + 48 + i * 8, UINT32_MAX);
        write32(bytes, signal + 208 + i * 4, UINT32_MAX);
    }
    return bytes;
}
auto gameIoFixture() -> std::vector<std::uint8_t>
{
    const auto names = std::array<std::string, 4>{"speed", "result", "enter", "done"};
    auto metadataSize = std::size_t{8};
    for (const auto& name : names) metadataSize += 24 + name.size() + 1;
    auto bytes = std::vector<std::uint8_t>(16 + 4 * 256 + metadataSize);
    std::memcpy(bytes.data(), "LFXB", 4);
    bytes[4] = 6; bytes[6] = 1; bytes[8] = 4; bytes[13] = 1;
    auto writeScalar = [&](std::size_t offset, std::uint8_t nodeId, std::uint32_t source) {
        bytes[offset + 15] = nodeId;
        bytes[offset + 16] = 1;
        bytes[offset + 17] = 1;
        bytes[offset + 18] = source == UINT32_MAX ? 0 : 1;
        write32(bytes, offset + 24, source);
        write32(bytes, offset + 28, UINT32_MAX);
    };
    writeScalar(16, 1, UINT32_MAX);
    writeScalar(16 + 256, 2, 0);
    auto first = bytes.data() + 16 + 2 * 256;
    first[15] = 3; first[16] = 41; first[19] = 1;
    const std::uint8_t passthrough[] = {0x98, 0x9f, 0xa5, 0xb5, 0x48, 0x5f, 0x4e, 0x75,
                                        0x85, 0x42, 0x9c, 0x55, 0, 0, 0, 1};
    std::memcpy(first + 24, passthrough, 16);
    write32(bytes, 16 + 2 * 256 + 40, 1);
    for (auto input = 0u; input < 4; ++input)
        write32(bytes, 16 + 2 * 256 + 48 + input * 8, UINT32_MAX);
    for (auto branch = 0u; branch < 4; ++branch)
        write32(bytes, 16 + 2 * 256 + 208 + branch * 4, branch == 0 ? 3 : UINT32_MAX);
    auto second = bytes.data() + 16 + 3 * 256;
    second[15] = 4; second[16] = 41; second[19] = 1;
    const std::uint8_t outputSignal[] = {0x7f, 0xf0, 0x78, 0xd2, 0xdc, 0xe1, 0x42, 0xb0,
                                         0x93, 0xfb, 0xb4, 0x02, 0, 0, 0, 1};
    std::memcpy(second + 24, outputSignal, 16);
    write32(bytes, 16 + 3 * 256 + 40, 1);
    for (auto input = 0u; input < 4; ++input)
        write32(bytes, 16 + 3 * 256 + 48 + input * 8, UINT32_MAX);
    for (auto branch = 0u; branch < 4; ++branch)
        write32(bytes, 16 + 3 * 256 + 208 + branch * 4, UINT32_MAX);
    auto metadata = bytes.data() + 16 + 4 * 256;
    std::memcpy(metadata, "LFIO", 4);
    metadata[4] = 4;
    auto writePort = [&](std::size_t offset, std::uint8_t nodeId, std::uint8_t kind,
                         std::uint8_t width,
                         const std::string& name) {
        metadata[offset + 15] = nodeId;
        metadata[offset + 16] = kind;
        metadata[offset + 17] = width;
        auto nameOffset = offset + 24;
        metadata[offset + 20] = static_cast<std::uint8_t>(name.size());
        std::memcpy(metadata + nameOffset, name.data(), name.size());
    };
    auto offset = std::size_t{8};
    writePort(offset, 1, 0, 1, names[0]); offset += 24 + names[0].size() + 1;
    writePort(offset, 2, 2, 1, names[1]); offset += 24 + names[1].size() + 1;
    writePort(offset, 3, 1, 0, names[2]); offset += 24 + names[2].size() + 1;
    writePort(offset, 4, 3, 0, names[3]);
    return bytes;
}
// Matches the Unity sample bytecode so its actual port/shape wiring is checked
// by the native validator, not merely by a C# compiler.
auto unityFixture() -> std::vector<std::uint8_t>
{
    auto bytes = std::vector<std::uint8_t>(16 + 5 * 96);
    std::memcpy(bytes.data(), "LFXB", 4);
    bytes[4] = 2; bytes[6] = 1; bytes[8] = 5; bytes[12] = 96;
    const std::uint8_t opcodes[] = {2, 5, 3, 25, 39};
    const std::uint8_t widths[] = {1, 1, 1, 4, 4};
    const std::uint8_t inputs[] = {0, 1, 1, 1, 4};
    for (auto i = 0u; i < 5; ++i) {
        auto offset = 16 + i * 96;
        bytes[offset + 15] = static_cast<std::uint8_t>(i + 1);
        bytes[offset + 16] = opcodes[i];
        bytes[offset + 17] = widths[i];
        bytes[offset + 18] = inputs[i];
        write32(bytes, offset + 24, i == 0 ? UINT32_MAX : i - 1);
        write32(bytes, offset + 28, UINT32_MAX);
    }
    bytes[16 + 4 * 96 + 20] = 1;
    number(bytes, 16 + 96 + 64, 0.2);
    number(bytes, 16 + 2 * 96 + 64, 0.2);
    return bytes;
}
void verifyEffectLifetime()
{
    auto bytes = scalarFixture();
    auto program = LibraProgram{};
    assert(libra_program_create(bytes.data(), bytes.size(), &program) == LIBRA_OK);
    auto instance = LibraInstance{};
    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    auto timeId = std::array<std::uint8_t, 16>{}; timeId[15] = 2;
    auto binding = LibraBinding{};
    assert(libra_bind_value(instance, timeId.data(), &binding) == LIBRA_OK);
    auto sample = LibraLifetimeSample{};
    assert(libra_effect_advance(instance, 0, &sample) == LIBRA_NOT_READY);
    assert(libra_effect_start(instance, -1, 0, nullptr, 0, &sample) == LIBRA_INVALID_ARGUMENT);
    assert(libra_effect_start(instance, 2, 0, nullptr, 0, &sample) == LIBRA_OK);
    assert(sample.age == 0 && sample.remaining == 2 && !sample.expired);
    assert(libra_evaluate(instance, 1, nullptr, 0, nullptr) == LIBRA_NOT_READY);
    assert(libra_effect_advance(instance, 3, &sample) == LIBRA_OK);
    assert(sample.age == 2 && sample.remaining == 0 && sample.normalizedAge == 1 && sample.expired);
    auto value = LibraValue{};
    assert(libra_values_read(instance, &binding, 1, &value) == LIBRA_OK && value.x == 2);
    assert(libra_effect_advance(instance, 3, &sample) == LIBRA_NOT_READY);
    assert(libra_effect_stop(instance) == LIBRA_OK);
    assert(libra_effect_finish(instance) == LIBRA_NOT_READY);
    assert(libra_instance_release(instance) == LIBRA_OK);

    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    assert(libra_effect_start(instance, 0, 0, nullptr, 0, &sample) == LIBRA_OK && sample.expired);
    assert(libra_instance_release(instance) == LIBRA_OK);
    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    assert(libra_effect_start(instance, 1, 0, nullptr, 0, &sample) == LIBRA_OK);
    assert(libra_effect_schedule(instance, 2, 42) == LIBRA_OK);
    assert(libra_effect_advance(instance, 3, &sample) == LIBRA_OK && sample.expired);
    assert(libra_effect_finish(instance) == LIBRA_NOT_READY);
    auto scheduled = LibraScheduledEvent{};
    auto hasScheduled = std::uint32_t{};
    assert(libra_effect_next_scheduled(instance, &scheduled, &hasScheduled) == LIBRA_OK &&
           hasScheduled && scheduled.time == 2 && scheduled.token == 42);
    assert(libra_effect_drain(instance, 3) == LIBRA_NOT_READY);
    assert(libra_effect_take_scheduled(instance, &scheduled, &hasScheduled) == LIBRA_OK && !hasScheduled);
    assert(libra_effect_drain(instance, 2) == LIBRA_OK);
    assert(libra_effect_take_scheduled(instance, &scheduled, &hasScheduled) == LIBRA_OK &&
           hasScheduled && scheduled.time == 2 && scheduled.token == 42);
    assert(libra_effect_finish(instance) == LIBRA_OK);
    assert(libra_instance_release(instance) == LIBRA_OK);
    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    assert(libra_effect_start(instance, 0, 1, nullptr, 0, &sample) == LIBRA_OK && sample.infinite);
    assert(libra_effect_advance(instance, 3, &sample) == LIBRA_OK && sample.age == 3 && !sample.expired);
    assert(libra_effect_stop(instance) == LIBRA_OK);
    assert(libra_effect_advance(instance, 4, &sample) == LIBRA_NOT_READY);
    assert(libra_instance_release(instance) == LIBRA_OK);
    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    assert(libra_evaluate(instance, 0, nullptr, 0, nullptr) == LIBRA_OK);
    assert(libra_effect_start(instance, 2, 0, nullptr, 0, &sample) == LIBRA_NOT_READY);
    assert(libra_instance_release(instance) == LIBRA_OK);
    assert(libra_program_release(program) == LIBRA_OK);

    bytes = gameIoFixture();
    assert(libra_program_create(bytes.data(), bytes.size(), &program) == LIBRA_OK);
    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    assert(libra_game_io_configure(instance, 8, nullptr, 0) == LIBRA_OK);
    auto speed = 3.0;
    assert(libra_game_io_set_property(instance, "speed", &speed, 1) == LIBRA_OK);
    auto startId = std::array<std::uint8_t, 16>{}; startId[15] = 3;
    assert(libra_effect_start(instance, 2, 0, startId.data(), 8, &sample) == LIBRA_OK);
    auto event = LibraGameIoEvent{};
    auto hasEvent = std::uint32_t{};
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent &&
           event.portIndex == 1 && event.values[0] == 3);
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent &&
           event.portIndex == 3 && event.kind == 3);
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && !hasEvent);
    assert(libra_game_io_start(instance, 0, startId.data(), 8) == LIBRA_NOT_READY);
    assert(libra_game_io_update(instance, 1) == LIBRA_NOT_READY);
    assert(libra_game_io_fire_signal(instance, "enter", 1, 8) == LIBRA_INVALID_ARGUMENT);
    assert(libra_game_io_fire_signal(instance, "enter", 0, 8) == LIBRA_OK);
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent &&
           event.portIndex == 3 && event.kind == 3);
    speed = 7;
    assert(libra_game_io_set_property(instance, "speed", &speed, 1) == LIBRA_OK);
    assert(libra_effect_advance(instance, 1, &sample) == LIBRA_OK && sample.age == 1);
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent &&
           event.portIndex == 1 && event.values[0] == 7);
    assert(libra_effect_advance(instance, 3, &sample) == LIBRA_OK && sample.age == 2 && sample.expired);
    assert(libra_game_io_fire_signal(instance, "enter", 2, 8) == LIBRA_NOT_READY);
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && !hasEvent);
    assert(libra_effect_destroy(instance, startId.data(), 8) == LIBRA_OK);
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent &&
           event.portIndex == 3 && event.kind == 3);
    assert(libra_effect_destroy(instance, startId.data(), 8) == LIBRA_OK);
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && !hasEvent);
    assert(libra_effect_finish(instance) == LIBRA_OK);
    assert(libra_instance_release(instance) == LIBRA_OK);
    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    assert(libra_effect_start(instance, 2, 0, nullptr, 0, &sample) == LIBRA_OK);
    assert(libra_game_io_configure(instance, 8, nullptr, 0) == LIBRA_INVALID_VALUE_TYPE);
    assert(libra_instance_release(instance) == LIBRA_OK);

    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    assert(libra_game_io_configure(instance, 1, nullptr, 0) == LIBRA_OK);
    assert(libra_effect_start(instance, 2, 0, startId.data(), 8, &sample) == LIBRA_QUEUE_REJECTED);
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent &&
           event.portIndex == 1);
    assert(libra_effect_start(instance, 2, 0, startId.data(), 8, &sample) == LIBRA_QUEUE_REJECTED);
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && !hasEvent);
    assert(libra_effect_stop(instance) == LIBRA_OK);
    assert(libra_instance_release(instance) == LIBRA_OK);

    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    assert(libra_game_io_configure(instance, 8, nullptr, 0) == LIBRA_OK);
    speed = 3;
    assert(libra_game_io_set_property(instance, "speed", &speed, 1) == LIBRA_OK);
    auto lifeId = std::array<std::uint8_t, 16>{}; lifeId[15] = 2;
    assert(libra_effect_start_captured(instance, lifeId.data(), startId.data(), 8, &sample) == LIBRA_OK &&
           sample.remaining == 3);
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent);
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent && event.kind == 3);
    speed = 1;
    assert(libra_game_io_set_property(instance, "speed", &speed, 1) == LIBRA_OK);
    assert(libra_effect_advance(instance, 2, &sample) == LIBRA_OK && !sample.expired &&
           sample.remaining == 1 && std::abs(sample.normalizedAge - 2.0 / 3.0) < 1e-12);
    assert(libra_effect_advance(instance, 4, &sample) == LIBRA_OK && sample.expired && sample.age == 3);
    assert(libra_instance_release(instance) == LIBRA_OK);

    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    assert(libra_game_io_configure(instance, 8, nullptr, 0) == LIBRA_OK);
    speed = -1;
    assert(libra_game_io_set_property(instance, "speed", &speed, 1) == LIBRA_OK);
    assert(libra_effect_start_captured(instance, lifeId.data(), startId.data(), 8, &sample) == LIBRA_INVALID_ARGUMENT);
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && !hasEvent);
    assert(libra_effect_start_captured(instance, lifeId.data(), startId.data(), 8, &sample) == LIBRA_INVALID_ARGUMENT);
    assert(libra_effect_stop(instance) == LIBRA_OK);
    assert(libra_instance_release(instance) == LIBRA_OK);

    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    assert(libra_game_io_configure(instance, 8, nullptr, 0) == LIBRA_OK);
    assert(libra_effect_start(instance, 0, 0, startId.data(), 8, &sample) == LIBRA_OK && sample.expired);
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent &&
           event.portIndex == 1);
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent &&
           event.portIndex == 3);
    assert(libra_effect_advance(instance, 0, &sample) == LIBRA_NOT_READY);
    assert(libra_game_io_fire_signal(instance, "enter", 0, 8) == LIBRA_NOT_READY);
    assert(libra_instance_release(instance) == LIBRA_OK);

    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    assert(libra_game_io_configure(instance, 1, nullptr, 0) == LIBRA_OK);
    assert(libra_effect_start(instance, 2, 0, nullptr, 0, &sample) == LIBRA_OK);
    speed = 5;
    assert(libra_game_io_set_property(instance, "speed", &speed, 1) == LIBRA_OK);
    assert(libra_effect_advance(instance, 1, &sample) == LIBRA_QUEUE_REJECTED);
    assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent);
    assert(libra_effect_advance(instance, 1, &sample) == LIBRA_NOT_READY);
    assert(libra_effect_stop(instance) == LIBRA_OK);
    assert(libra_instance_release(instance) == LIBRA_OK);

    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    assert(libra_game_io_configure(instance, 8, nullptr, 0) == LIBRA_OK);
    assert(libra_game_io_start(instance, 0, nullptr, 0) == LIBRA_OK);
    assert(libra_effect_start(instance, 2, 0, nullptr, 0, &sample) == LIBRA_NOT_READY);
    assert(libra_instance_release(instance) == LIBRA_OK);
    assert(libra_program_release(program) == LIBRA_OK);
}
auto readCompilerFixture(const std::filesystem::path& directory, const char* name)
    -> std::vector<std::uint8_t>
{
    auto stream = std::ifstream{directory / name, std::ios::binary};
    assert(stream.good());
    return {std::istreambuf_iterator<char>{stream}, std::istreambuf_iterator<char>{}};
}
auto compilerFixtureNode(const std::vector<std::uint8_t>& bytes, std::uint8_t opcode,
                         std::size_t occurrence = 0) -> const std::uint8_t*
{
    assert(bytes.size() >= 16 && bytes[4] == 10 && bytes[5] == 0);
    auto count = std::uint32_t{};
    for (auto lane = 0u; lane < 4; ++lane)
        count |= std::uint32_t{bytes[8 + lane]} << (lane * 8);
    assert(bytes[12] == 0 && bytes[13] == 1);
    for (auto index = 0u; index < count; ++index) {
        auto current = bytes.data() + 16 + std::size_t{index} * 256;
        if (current[16] != opcode) continue;
        if (occurrence-- == 0) return current;
    }
    assert(false && "Compiler fixture is missing the requested instruction.");
    return nullptr;
}
void verifyInt32InteropFixture(const std::filesystem::path& directory)
{
    auto bytes = readCompilerFixture(directory, "Int32Add.lfxb");
    auto program = LibraProgram{};
    assert(libra_program_create(bytes.data(), bytes.size(), &program) == LIBRA_OK);
    auto instance = LibraInstance{};
    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    auto outputBinding = LibraBinding{};
    auto operation = compilerFixtureNode(bytes, 49);
    assert(libra_bind_value(instance, operation, &outputBinding) == LIBRA_OK);
    auto outputType = LIBRA_VALUE_TYPE_UNKNOWN;
    assert(libra_value_type(instance, outputBinding, &outputType) == LIBRA_OK &&
        outputType == LIBRA_VALUE_TYPE_INT32);
    auto leftBinding = LibraBinding{};
    auto rightBinding = LibraBinding{};
    assert(libra_bind_parameter(instance, compilerFixtureNode(bytes, 48, 0), 0, 0,
        &leftBinding) == LIBRA_OK);
    assert(libra_bind_parameter(instance, compilerFixtureNode(bytes, 48, 1), 0, 0,
        &rightBinding) == LIBRA_OK);
    auto parameterType = LIBRA_VALUE_TYPE_UNKNOWN;
    assert(libra_parameter_type(instance, leftBinding, &parameterType) == LIBRA_OK &&
        parameterType == LIBRA_VALUE_TYPE_INT32);
    auto left = std::int32_t{};
    assert(libra_parameter_get_int32(instance, leftBinding, &left) == LIBRA_OK && left == 14);
    assert(libra_parameter_set(instance, leftBinding, 1.5) == LIBRA_INVALID_VALUE_TYPE);
    assert(libra_parameter_set_int32(instance, leftBinding,
        std::numeric_limits<std::int32_t>::max()) == LIBRA_OK);
    assert(libra_parameter_set_int32(instance, rightBinding, 1) == LIBRA_OK);
    auto output = LibraValue{};
    assert(libra_evaluate(instance, 0, &outputBinding, 1, &output) == LIBRA_ARITHMETIC_OVERFLOW);
    auto exactValue = std::int32_t{};
    assert(libra_value_read_int32(instance, outputBinding, &exactValue) == LIBRA_ARITHMETIC_OVERFLOW);
    assert(libra_instance_release(instance) == LIBRA_OK);
    assert(libra_program_release(program) == LIBRA_OK);

    auto conversions = readCompilerFixture(directory, "Conversions.lfxb");
    assert(libra_program_create(conversions.data(), conversions.size(), &program) == LIBRA_OK);
    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    auto intBinding = LibraBinding{};
    auto floatBinding = LibraBinding{};
    assert(libra_bind_value(instance, compilerFixtureNode(conversions, 53), &intBinding) == LIBRA_OK);
    assert(libra_bind_value(instance, compilerFixtureNode(conversions, 54), &floatBinding) == LIBRA_OK);
    assert(libra_value_type(instance, intBinding, &outputType) == LIBRA_OK &&
        outputType == LIBRA_VALUE_TYPE_INT32);
    assert(libra_value_type(instance, floatBinding, &outputType) == LIBRA_OK &&
        outputType == LIBRA_VALUE_TYPE_FLOAT);
    auto sourceBinding = LibraBinding{};
    assert(libra_bind_parameter(instance, compilerFixtureNode(conversions, 1), 0, 0,
        &sourceBinding) == LIBRA_OK);
    assert(libra_parameter_type(instance, sourceBinding, &parameterType) == LIBRA_OK &&
        parameterType == LIBRA_VALUE_TYPE_FLOAT);
    assert(libra_parameter_set_int32(instance, sourceBinding, 4) == LIBRA_INVALID_VALUE_TYPE);
    auto outputs = std::array<LibraBinding, 2>{intBinding, floatBinding};
    auto values = std::array<LibraValue, 2>{};
    assert(libra_evaluate(instance, 0, outputs.data(), outputs.size(), values.data()) == LIBRA_OK);
    assert(libra_value_read_int32(instance, intBinding, &exactValue) == LIBRA_OK && exactValue == 3);
    assert(values[1].x == 3);
    assert(libra_parameter_set(instance, sourceBinding, 2147483648.0) == LIBRA_OK);
    assert(libra_evaluate(instance, 0, outputs.data(), outputs.size(), values.data()) ==
        LIBRA_ARITHMETIC_OVERFLOW);
    assert(libra_parameter_set(instance, sourceBinding,
        std::numeric_limits<double>::quiet_NaN()) == LIBRA_NON_FINITE);
    assert(libra_instance_release(instance) == LIBRA_OK);
    assert(libra_program_release(program) == LIBRA_OK);

    auto divide = readCompilerFixture(directory, "Int32Divide.lfxb");
    assert(libra_program_create(divide.data(), divide.size(), &program) == LIBRA_OK);
    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    auto dividend = LibraBinding{};
    auto divisor = LibraBinding{};
    auto quotient = LibraBinding{};
    assert(libra_bind_parameter(instance, compilerFixtureNode(divide, 48, 1), 0, 0,
        &divisor) == LIBRA_OK);
    assert(libra_bind_parameter(instance, compilerFixtureNode(divide, 48, 0), 0, 0,
        &dividend) == LIBRA_OK);
    assert(libra_parameter_set_int32(instance, dividend, 8) == LIBRA_OK);
    assert(libra_parameter_set_int32(instance, divisor, 0) == LIBRA_OK);
    assert(libra_bind_value(instance, compilerFixtureNode(divide, 52), &quotient) == LIBRA_OK);
    auto quotientValue = LibraValue{};
    assert(libra_evaluate(instance, 0, &quotient, 1, &quotientValue) == LIBRA_DIVIDE_BY_ZERO);
    assert(libra_instance_release(instance) == LIBRA_OK);
    assert(libra_program_release(program) == LIBRA_OK);
}
#if defined(_MSC_VER) && defined(_DEBUG)
auto frameAllocations = 0u;
auto __cdecl allocationHook(int kind, void*, size_t, int, long, const unsigned char*, int) -> int
{
    if (kind == _HOOK_ALLOC || kind == _HOOK_REALLOC) ++frameAllocations;
    return 1;
}
#endif
}

#if defined(_WIN32)
static void verifyPackageDebugger(const std::filesystem::path& path)
{
    auto file = std::ifstream(path / "GameIo.lfxp", std::ios::binary);
    auto bytes = std::vector<std::uint8_t>{std::istreambuf_iterator<char>(file), {}};
    assert(!bytes.empty());
    auto hash = UINT64_C(14695981039346656037);
    for (auto b : bytes) hash = (hash ^ b) * UINT64_C(1099511628211);
    auto host = LibraSpawnableHostHandle{}, foreign = LibraSpawnableHostHandle{};
    for (auto* handle : {&host, &foreign})
        assert(libra_spawnable_host_create(bytes.data(), bytes.size(), 8, 4 * 1024 * 1024, 128, 10000, handle) == LIBRA_OK);
    assert(libra_spawnable_host_advance(host, 0, 0) == LIBRA_OK);
    assert(libra_spawnable_host_fire_game_signal(host, "spawn") == LIBRA_OK);
    assert(libra_spawnable_host_fire_game_signal(host, "spawn") == LIBRA_OK);
    auto spawned = std::vector<LibraSpawnableHostEvent>{};
    for (;;) {
        auto item = LibraSpawnableHostEvent{};
        auto has = uint32_t{};
        assert(libra_spawnable_host_next_event(host, &item, &has) == LIBRA_OK);
        if (!has) break;
        if (item.kind == 1) spawned.push_back(item);
    }
    assert(spawned.size() == 2 && spawned[0].instanceId != spawned[1].instanceId);
    auto generation = uint64_t{};
    assert(libra_spawnable_debug_generation(host, &generation) == LIBRA_OK && generation != 0);
    assert(libra_spawnable_debug_attach(foreign, generation, nullptr, 0, "") == LIBRA_INVALID_HANDLE);
    auto verify = [&](const LibraSpawnableHostEvent* child, double time) {
        auto invitation = std::string("a3b102031405060708090a0b0c0d0e0");
        invitation += child ? "2" : "1";
        assert(invitation.size() == 32);
        auto pipe = CreateNamedPipeA(("\\\\.\\pipe\\LiberaFx.ExecutionDebug." + invitation).c_str(),
            PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_NOWAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 4096, 4096, 0, nullptr);
        assert(pipe != INVALID_HANDLE_VALUE);
        assert(libra_spawnable_debug_attach(host, generation, child ? child->containerId : nullptr,
            child ? child->instanceId : 0, invitation.c_str()) == LIBRA_OK);
        auto until = [](auto predicate) {
            auto end = std::chrono::steady_clock::now() + std::chrono::seconds(4);
            while (!predicate()) {
                assert(std::chrono::steady_clock::now() < end);
                std::this_thread::sleep_for(std::chrono::milliseconds(2));
            }
        };
        until([&] { return ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED; });
        auto exchange = [&](uint8_t op) {
            auto command = std::array<uint8_t, 32>{};
            auto frame = std::array<uint8_t, 984>{};
            std::memcpy(command.data(), "LFD2", 4);
            command[4] = op;
            for (auto i = 0u; i < 8; ++i) command[8 + i] = static_cast<uint8_t>(hash >> (8 * i));
            auto count = DWORD{};
            assert(WriteFile(pipe, command.data(), static_cast<DWORD>(command.size()), &count, nullptr) && count == command.size());
            until([&] { return PeekNamedPipe(pipe, nullptr, 0, nullptr, &count, nullptr) && count >= frame.size(); });
            assert(ReadFile(pipe, frame.data(), static_cast<DWORD>(frame.size()), &count, nullptr) && count == frame.size());
            return frame;
        };
        auto attached = exchange(1);
        assert(attached[5] == 0 && attached[952] == static_cast<uint8_t>(generation));
        assert(attached[960] == (child ? static_cast<uint8_t>(child->instanceId) : 0));
        if (child) assert(std::memcmp(attached.data() + 968, child->containerId, 16) == 0);
        assert(exchange(4)[5] == 0);
        assert(libra_spawnable_debug_submit(host, 0, time, .01, nullptr, 0, nullptr, nullptr) == LIBRA_OK);
        until([&] { return exchange(0)[4] == 2; });
        auto count = uint64_t{};
        assert(libra_spawnable_host_active_count(host, &count) == LIBRA_BUSY);
        assert(libra_spawnable_host_fire_game_signal(host, "spawn") == LIBRA_BUSY);
        assert(libra_spawnable_debug_submit(host, 0, time, .01, nullptr, 0, nullptr, nullptr) == LIBRA_BUSY);
        assert(exchange(5)[5] == 0);
        until([&] { return exchange(0)[4] == 2; });
        CloseHandle(pipe); // Lose the editor while the real worker is paused.
        auto work = static_cast<LibraResult>(LIBRA_NOT_READY);
        until([&] { return libra_spawnable_debug_poll(host, &work) == LIBRA_OK; });
        assert(work == LIBRA_OK);
        assert(libra_spawnable_debug_detach(host) == LIBRA_OK);
        assert(libra_spawnable_host_active_count(host, &count) == LIBRA_OK && count == 2);
    };
    verify(nullptr, .1);
    verify(&spawned[0], .2);
    assert(libra_spawnable_host_force_terminate(host, 1, spawned[0].containerId, spawned[0].instanceId) == LIBRA_OK);
    assert(libra_spawnable_debug_attach(host, generation, spawned[0].containerId, spawned[0].instanceId, "") == LIBRA_INVALID_HANDLE);
    assert(libra_spawnable_host_release(host) == LIBRA_OK && libra_spawnable_host_release(foreign) == LIBRA_OK);
}
#endif

static void verifyRawLogicalQuality(const std::filesystem::path& directory)
{
    const auto bytes = readCompilerFixture(directory, "AuthoredQuality.lfxb");
    const auto ids = readCompilerFixture(directory, "AuthoredQuality.ids");
    assert(ids.size() >= 96);
    auto program = LibraProgram{};
    auto instance = LibraInstance{};
    assert(libra_program_create(bytes.data(), bytes.size(), &program) == LIBRA_OK);
    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    auto bindings = std::array<LibraBinding, 2>{};
    assert(libra_bind_value(instance, ids.data() + 32, &bindings[0]) == LIBRA_OK);
    assert(libra_bind_value(instance, ids.data() + 80, &bindings[1]) == LIBRA_OK);
    for (auto frame = 0u; frame < 2; ++frame) {
        auto time = static_cast<double>(frame);
        auto values = std::array<LibraValue, 2>{};
        assert(libra_instance_set_quality(instance, 1, frame ? 2 : 0, 0, frame + 1) == LIBRA_OK);
        assert(libra_evaluate(instance, time, bindings.data(), 2, values.data()) == LIBRA_OK);
        assert(libra_dispatch_signal(instance, ids.data(), time, 10000) == LIBRA_OK);
        assert(libra_values_read(instance, bindings.data(), 2, values.data()) == LIBRA_OK);
        assert(values[0].x == frame && values[1].x == (frame + 1) * 2);
    }
    assert(libra_instance_release(instance) == LIBRA_OK);
    assert(libra_program_release(program) == LIBRA_OK);
}

int main()
{
#if defined(_MSC_VER) && defined(_DEBUG)
    _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE);
    _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif
    verifyEffectLifetime();
    if (auto fixtureDirectory = std::getenv("LIBERAFX_INT32_FIXTURE_DIR"))
        verifyInt32InteropFixture(std::filesystem::path{fixtureDirectory});
    if (auto fixtures = std::getenv("LIBERAFX_DEBUG_QUALITY_FIXTURE_DIR"))
        verifyRawLogicalQuality(fixtures);
#if defined(_WIN32)
    if (auto fixtures = std::getenv("LIBERAFX_DEBUG_PACKAGE_FIXTURE_DIR"))
        verifyPackageDebugger(fixtures);
#endif
    if (auto bytecodePath = std::getenv("LIBERAFX_CONDITIONAL_BYTECODE")) {
        auto stream = std::ifstream{bytecodePath, std::ios::binary};
        auto bytes = std::vector<std::uint8_t>(
            std::istreambuf_iterator<char>{stream}, std::istreambuf_iterator<char>{});
        assert(stream.good() || stream.eof());
        for (auto spawnable = 0; spawnable < 2; ++spawnable) {
            auto program = LibraProgram{};
            auto status = spawnable
                ? libra_program_create_spawnable_v2(bytes.data(), bytes.size(), nullptr, 0, nullptr, 0, &program)
                : libra_program_create(bytes.data(), bytes.size(), &program);
            assert(status == LIBRA_OK);
            auto instance = LibraInstance{};
            assert(libra_instance_create(program, &instance) == LIBRA_OK);
            assert(libra_game_io_configure(instance, 32, nullptr, 0) == LIBRA_OK);
            assert(libra_game_io_start(instance, 0, nullptr, 10000) == LIBRA_OK);
            assert(libra_game_io_update(instance, 0.5) == LIBRA_OK);
            assert(libra_instance_release(instance) == LIBRA_OK);
            assert(libra_program_release(program) == LIBRA_OK);
        }
    }
    if (auto bytecodePath = std::getenv("LIBERAFX_GAMEIO_MATRIX_BYTECODE")) {
        auto stream = std::ifstream{bytecodePath, std::ios::binary};
        auto bytes = std::vector<std::uint8_t>(
            std::istreambuf_iterator<char>{stream}, std::istreambuf_iterator<char>{});
        assert(stream.good() || stream.eof());
        auto program = LibraProgram{};
        assert(libra_program_create(bytes.data(), bytes.size(), &program) == LIBRA_OK);
        auto instance = LibraInstance{};
        assert(libra_instance_create(program, &instance) == LIBRA_OK);
        assert(libra_game_io_configure(instance, 8, nullptr, 92) == LIBRA_OK);
        auto owner = std::array<double, 16>{
            2, 0, 0, 10, 0, 2, 0, 20, 0, 0, 2, 30, 0, 0, 0, 1};
        auto local = std::array<double, 16>{
            1, 0, 0, 2, 0, 1, 0, 3, 0, 0, 1, 4, 0, 0, 0, 1};
        assert(libra_game_io_set_owner_matrix(instance, owner.data()) == LIBRA_OK);
        assert(libra_game_io_set_property(instance, "local", local.data(), 16) == LIBRA_OK);
        assert(libra_game_io_set_property(instance, "local", local.data(), 4) == LIBRA_INVALID_VALUE_TYPE);
        assert(libra_game_io_start(instance, 0, nullptr, 0) == LIBRA_OK);
        auto oldEvent = LibraGameIoEvent{};
        auto newEvent = LibraGameIoEventV2{};
        auto hasEvent = std::uint32_t{};
        assert(libra_game_io_next_event(instance, &oldEvent, &hasEvent) == LIBRA_INVALID_VALUE_TYPE &&
               !hasEvent);
        assert(libra_game_io_next_event_v2(instance, &newEvent, &hasEvent) == LIBRA_OK && hasEvent &&
               newEvent.instanceId == 92 && newEvent.portIndex == 2 &&
               newEvent.kind == 2 && newEvent.width == 16 &&
               newEvent.values[0] == 2 && newEvent.values[3] == 14 &&
               newEvent.values[7] == 26 && newEvent.values[11] == 38 &&
               newEvent.values[15] == 1);
        owner[3] = 15;
        assert(libra_game_io_set_owner_matrix(instance, owner.data()) == LIBRA_OK);
        assert(libra_game_io_update(instance, 1) == LIBRA_OK);
        assert(libra_game_io_next_event_v2(instance, &newEvent, &hasEvent) == LIBRA_OK && hasEvent &&
               newEvent.values[3] == 19);
        assert(libra_instance_release(instance) == LIBRA_OK);
        assert(libra_instance_create(program, &instance) == LIBRA_OK);
        assert(libra_game_io_configure(instance, 8, nullptr, 92) == LIBRA_OK);
        assert(libra_game_io_set_owner_matrix(instance, owner.data()) == LIBRA_OK);
        assert(libra_game_io_set_property(instance, "local", local.data(), 16) == LIBRA_OK);
        auto lifetime = LibraLifetimeSample{};
        assert(libra_effect_start(instance, 2, 0, nullptr, 0, &lifetime) == LIBRA_OK);
        assert(libra_game_io_next_event_v2(instance, &newEvent, &hasEvent) == LIBRA_OK && hasEvent &&
               newEvent.values[3] == 19);
        owner[3] = 20;
        assert(libra_game_io_set_owner_matrix(instance, owner.data()) == LIBRA_OK);
        assert(libra_effect_advance(instance, 5, &lifetime) == LIBRA_OK &&
               lifetime.expired && lifetime.age == 2);
        assert(libra_game_io_next_event_v2(instance, &newEvent, &hasEvent) == LIBRA_OK && hasEvent &&
               newEvent.values[3] == 24);
        assert(libra_instance_release(instance) == LIBRA_OK);
        assert(libra_program_release(program) == LIBRA_OK);
    }
    if (auto bytecodePath = std::getenv("LIBERAFX_GAMEIO_BYTECODE")) {
        auto stream = std::ifstream{bytecodePath, std::ios::binary};
        auto bytes = std::vector<std::uint8_t>(
            std::istreambuf_iterator<char>{stream}, std::istreambuf_iterator<char>{});
        assert(stream.good() || stream.eof());
        auto program = LibraProgram{};
        assert(libra_program_create(bytes.data(), bytes.size(), &program) == LIBRA_OK);
        auto instance = LibraInstance{};
        assert(libra_instance_create(program, &instance) == LIBRA_OK);
        auto containerId = std::array<std::uint8_t, 16>{};
        containerId[15] = 77;
        assert(libra_game_io_configure(instance, 4, containerId.data(), 91) == LIBRA_OK);
        auto position = std::array<double, 3>{4, 5, 6};
        assert(libra_game_io_set_property(instance, "position", position.data(), position.size()) == LIBRA_OK);
        assert(libra_game_io_start(instance, 0, nullptr, 0) == LIBRA_OK);
        auto event = LibraGameIoEvent{};
        auto hasEvent = std::uint32_t{};
        assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent &&
               event.containerId[15] == 77 && event.instanceId == 91 &&
               event.portIndex == 2 && event.kind == 2 && event.width == 4 && event.isColor &&
               event.values[0] == 1 && event.values[3] == 1);
        assert(libra_game_io_fire_signal(instance, "trigger", 1, 8) == LIBRA_OK);
        assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent &&
               event.portIndex == 3 && event.kind == 3);
        assert(libra_instance_release(instance) == LIBRA_OK);
        assert(libra_instance_create(program, &instance) == LIBRA_OK);
        assert(libra_game_io_configure(instance, 4, containerId.data(), 91) == LIBRA_OK);
        assert(libra_game_io_set_property(instance, "position", position.data(), position.size()) == LIBRA_OK);
        auto lifetime = LibraLifetimeSample{};
        assert(libra_effect_start(instance, 2, 0, nullptr, 0, &lifetime) == LIBRA_OK);
        assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent &&
               event.portIndex == 2 && event.kind == 2);
        assert(libra_effect_advance(instance, 1, &lifetime) == LIBRA_OK);
        assert(libra_game_io_fire_signal(instance, "trigger", 1, 8) == LIBRA_OK);
        assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent &&
               event.portIndex == 3 && event.kind == 3);
        assert(libra_effect_advance(instance, 3, &lifetime) == LIBRA_OK && lifetime.expired);
        assert(libra_game_io_fire_signal(instance, "trigger", 2, 8) == LIBRA_NOT_READY);
        assert(libra_instance_release(instance) == LIBRA_OK);
        assert(libra_program_release(program) == LIBRA_OK);
    }
    {
        auto bytes = gameIoFixture();
        auto program = LibraProgram{};
        assert(libra_program_create(bytes.data(), bytes.size(), &program) == LIBRA_OK);
        auto instance = LibraInstance{};
        assert(libra_instance_create(program, &instance) == LIBRA_OK);
        assert(libra_game_io_configure(instance, 1, nullptr, 42) == LIBRA_OK);
        auto inputValue = std::array<double, 1>{2};
        assert(libra_game_io_set_property(instance, "speed", inputValue.data(), 1) == LIBRA_OK);
        assert(libra_game_io_set_property(instance, "speed", inputValue.data(), 2) == LIBRA_INVALID_VALUE_TYPE);
        assert(libra_game_io_fire_signal(instance, "enter", 0, 4) == LIBRA_NOT_READY);
        assert(libra_game_io_start(instance, 0, nullptr, 0) == LIBRA_OK);
        auto event = LibraGameIoEvent{};
        auto hasEvent = std::uint32_t{};
        assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent &&
               event.instanceId == 42 && event.portIndex == 1 && event.kind == 2 && event.values[0] == 2);
        inputValue[0] = 5;
        assert(libra_game_io_set_property(instance, "speed", inputValue.data(), 1) == LIBRA_OK);
        assert(libra_game_io_update(instance, 1) == LIBRA_OK);
        assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent &&
               event.portIndex == 1 && event.values[0] == 5);
        assert(libra_game_io_fire_signal(instance, "enter", 2, 4) == LIBRA_OK);
        assert(libra_game_io_fire_signal(instance, "enter", 3, 4) == LIBRA_QUEUE_REJECTED);
        assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent &&
               event.portIndex == 3 && event.kind == 3);
        assert(libra_game_io_fire_signal(instance, "enter", 4, 4) == LIBRA_OK);
        assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && hasEvent);
        assert(libra_game_io_next_event(instance, &event, &hasEvent) == LIBRA_OK && !hasEvent);
        assert(libra_instance_release(instance) == LIBRA_OK);
        assert(libra_program_release(program) == LIBRA_OK);
        bytes.back() = 0xff;
        assert(libra_program_create(bytes.data(), bytes.size(), &program) == LIBRA_INVALID_BINARY);
    }
    {
        auto bytes = lifecycleFixture();
        auto program = LibraProgram{};
        assert(libra_program_create_spawnable(bytes.data(), bytes.size(), nullptr, 0, &program) == LIBRA_OK);
        auto container = std::array<std::uint8_t, 16>{}; container[15] = 90;
        auto pool = LibraGeneratorPool{};
        assert(libra_generator_pool_create_ex(program, container.data(), 1, 65536, 4, 256, &pool) == LIBRA_OK);
        auto config = LibraGeneratorLifecycle{};
        config.flags = LIBRA_LIFECYCLE_LIFE | LIBRA_LIFECYCLE_ON_START;
        config.lifeNodeId[15] = 1;
        config.onStartNodeId[15] = 2;
        config.instructionBudget = 2;
        assert(libra_generator_configure_lifecycle(pool, &config) == LIBRA_OK);
        auto captures = std::array<LibraGeneratorCapture, 256>{};
        for (auto& capture : captures) { capture.nodeId[15] = 1; capture.value = 3; }
        assert(libra_generator_enqueue_spawn(pool, 10, captures.data(), 256) == LIBRA_INVALID_ARGUMENT);
        assert(libra_generator_enqueue_spawn_ex(pool, 10, captures.data(), 257) == LIBRA_INVALID_ARGUMENT);
        assert(libra_generator_enqueue_spawn_ex(pool, 10, captures.data(), 256) == LIBRA_OK);
        auto count = std::uint32_t{};
        assert(libra_generator_process_spawns(pool, 1, &count) == LIBRA_OK && count == 1);
        auto event = LibraGeneratorEvent{};
        auto has = std::uint32_t{};
        assert(libra_generator_next_event(pool, &event, &has) == LIBRA_OK && has && event.kind == 1);
        auto id = event.instanceId;
        assert(libra_generator_evaluate(pool, id, 10) == LIBRA_NOT_READY);
        auto lifetime = LibraLifetimeSample{};
        assert(libra_generator_lifetime(pool, id, &lifetime) == LIBRA_NOT_READY);
        assert(libra_generator_lifetime(pool, id, nullptr) == LIBRA_INVALID_ARGUMENT);
        assert(libra_generator_start(pool, id) == LIBRA_OK);
        assert(libra_generator_lifetime(pool, id, &lifetime) == LIBRA_OK &&
               lifetime.age == 0 && lifetime.remaining == 3 && lifetime.normalizedAge == 0 &&
               !lifetime.expired && !lifetime.infinite);
        assert(libra_generator_start(pool, id) == LIBRA_OK);
        assert(libra_generator_evaluate(pool, id, 11) == LIBRA_OK);
        assert(libra_generator_lifetime(pool, id, &lifetime) == LIBRA_OK &&
               lifetime.age == 1 && lifetime.remaining == 2 && lifetime.normalizedAge == 1.0 / 3);
        assert(libra_generator_evaluate(pool, id, 13) == LIBRA_OK);
        assert(libra_generator_lifetime(pool, id, &lifetime) == LIBRA_OK &&
               lifetime.age == 3 && lifetime.remaining == 0 && lifetime.normalizedAge == 1 && lifetime.expired);
        assert(libra_generator_finalize(pool, id) == LIBRA_NOT_READY);
        assert(libra_generator_next_event(pool, &event, &has) == LIBRA_OK && has && event.kind == 3 && event.instanceId == id);
        assert(libra_generator_finalize(pool, id) == LIBRA_OK);
        assert(libra_generator_lifetime(pool, id, &lifetime) == LIBRA_INVALID_HANDLE);
        assert(libra_generator_next_event(pool, &event, &has) == LIBRA_OK && has && event.kind == 2 && event.instanceId == id);
        assert(libra_generator_next_event(pool, &event, &has) == LIBRA_OK && !has);
        assert(libra_generator_pool_release(pool) == LIBRA_OK);
        assert(libra_program_release(program) == LIBRA_OK);
    }
    {
        auto templateBytes = scalarFixture();
        auto templateProgram = LibraProgram{};
        assert(libra_program_create(templateBytes.data(), templateBytes.size(), &templateProgram) == LIBRA_OK);
        auto container = std::array<std::uint8_t, 16>{}; container[15] = 77;
        auto pool = LibraGeneratorPool{};
        assert(libra_generator_pool_create(templateProgram, container.data(), 2, 65536, 4, &pool) == LIBRA_OK);
        assert(libra_program_release(templateProgram) == LIBRA_OK);
        auto capture = LibraGeneratorCapture{};
        capture.nodeId[15] = 1; capture.value = 9;
        assert(libra_generator_enqueue_spawn(pool, 2, &capture, 1) == LIBRA_OK);
        assert(libra_generator_enqueue_spawn(pool, 4, &capture, 1) == LIBRA_OK);
        auto processed = std::uint32_t{};
        assert(libra_generator_process_spawns(pool, 2, &processed) == LIBRA_OK && processed == 2);
        auto event1 = LibraGeneratorEvent{};
        auto event2 = LibraGeneratorEvent{};
        auto hasEvent = std::uint32_t{};
        assert(libra_generator_next_event(pool, &event1, &hasEvent) == LIBRA_OK && hasEvent && event1.kind == 1);
        assert(libra_generator_next_event(pool, &event2, &hasEvent) == LIBRA_OK && hasEvent && event2.kind == 1);
        assert(event1.instanceId != event2.instanceId && event1.containerId[15] == 77);
        assert(libra_generator_evaluate(pool, event1.instanceId, 6) == LIBRA_OK);
        auto infiniteLifetime = LibraLifetimeSample{};
        assert(libra_generator_lifetime(pool, event1.instanceId, &infiniteLifetime) == LIBRA_OK &&
               infiniteLifetime.age == 4 && std::isinf(infiniteLifetime.remaining) &&
               infiniteLifetime.normalizedAge == 0 && infiniteLifetime.infinite && !infiniteLifetime.expired);
        assert(libra_generator_evaluate(pool, event2.instanceId, 6) == LIBRA_OK);
        auto node = std::array<std::uint8_t, 16>{}; node[15] = 3;
        auto value = LibraValue{};
        assert(libra_generator_value(pool, event1.instanceId, node.data(), &value) == LIBRA_OK && value.x == 13);
        assert(libra_generator_value(pool, event2.instanceId, node.data(), &value) == LIBRA_OK && value.x == 11);
        assert(libra_generator_stop(pool, event1.instanceId) == LIBRA_OK);
        assert(libra_generator_evaluate(pool, event1.instanceId, 6) == LIBRA_INVALID_HANDLE);
        assert(libra_generator_reset(pool) == LIBRA_OK);
        assert(libra_generator_pool_release(pool) == LIBRA_OK);
        assert(libra_generator_pool_release(pool) == LIBRA_INVALID_HANDLE);
    }
    auto bytes = scalarFixture();
    auto program = LibraProgram{};
    auto unalignedWire = std::vector<std::uint8_t>(bytes.size() + 1);
    std::memcpy(unalignedWire.data() + 1, bytes.data(), bytes.size());
    assert(libra_program_create(unalignedWire.data() + 1, bytes.size(), &program) == LIBRA_OK);
    auto instance = LibraInstance{};
    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    auto id = std::array<std::uint8_t, 16>{};
    id[15] = 3;
    auto binding = LibraBinding{};
    assert(libra_bind_value(instance, id.data(), &binding) == LIBRA_OK);
    auto duplicate = LibraBinding{};
    assert(libra_bind_value(instance, id.data(), &duplicate) == LIBRA_OK && duplicate == binding);
    auto output = LibraValue{};
    assert(libra_values_read(instance, &binding, 1, &output) == LIBRA_NOT_READY);
    assert(libra_evaluate(instance, 7, &binding, 1, &output) == LIBRA_OK && output.x == 10 && output.componentCount == 1);
    struct alignas(16) ValueStorage { double prefix; LibraValue value; };
    auto valueStorage = ValueStorage{};
    assert(reinterpret_cast<std::uintptr_t>(&valueStorage.value) % 16 == 8);
    assert(libra_evaluate(instance, 7, &binding, 1, &valueStorage.value) == LIBRA_OK &&
           valueStorage.value.x == 10);
    id[15] = 1;
    auto parameter = LibraBinding{};
    assert(libra_bind_parameter(instance, id.data(), 0, 0, &parameter) == LIBRA_OK);
    auto value = double{};
    assert(libra_parameter_get(instance, parameter, &value) == LIBRA_OK && value == 3);
    assert(libra_parameter_set(instance, parameter, 4) == LIBRA_OK);
    assert(libra_parameter_set(instance, parameter, std::numeric_limits<double>::infinity()) == LIBRA_NON_FINITE);
    assert(libra_evaluate(instance, 7, &binding, 1, &output) == LIBRA_OK && output.x == 11);
    // Public program disposal and caller-byte mutation cannot invalidate a live instance.
    std::memset(bytes.data(), 0, bytes.size());
    assert(libra_program_release(program) == LIBRA_OK);
    assert(libra_program_release(program) == LIBRA_INVALID_HANDLE);
    auto invalidInstance = LibraInstance{123};
    assert(libra_instance_create(program, &invalidInstance) == LIBRA_INVALID_HANDLE && invalidInstance == 0);
    assert(libra_evaluate(instance, 8, &binding, 1, &output) == LIBRA_OK && output.x == 12);
    auto invalidBinding = LibraBinding{UINT32_MAX};
    assert(libra_evaluate(instance, 0, &invalidBinding, 1, &output) == LIBRA_INVALID_HANDLE);
    assert(libra_evaluate(instance, 0, nullptr, 1, &output) == LIBRA_INVALID_ARGUMENT);
    assert(libra_evaluate(instance, 0, nullptr, 0, nullptr) == LIBRA_OK);
    assert(libra_bind_parameter(instance, id.data(), 256, 0, &duplicate) == LIBRA_INVALID_ARGUMENT);
    assert(libra_parameter_get(instance, 0, &value) == LIBRA_INVALID_HANDLE);
    assert(libra_parameter_set(instance, UINT32_MAX, 1) == LIBRA_INVALID_HANDLE);
    // Same native storage and caller arrays reused; Debug MSVC observes every
    // allocation/reallocation through the shared CRT, including inside the DLL.
#if defined(_MSC_VER) && defined(_DEBUG)
    auto previousHook = _CrtSetAllocHook(allocationHook);
#endif
    for (auto frame = 0u; frame < 10000; ++frame) {
        assert(libra_parameter_set(instance, parameter, frame) == LIBRA_OK);
        assert(libra_evaluate(instance, frame, &binding, 1, &output) == LIBRA_OK && output.x == frame * 2);
    }
#if defined(_MSC_VER) && defined(_DEBUG)
    _CrtSetAllocHook(previousHook);
    assert(frameAllocations == 0);
#endif
    assert(libra_instance_release(instance) == LIBRA_OK);
    assert(libra_instance_release(instance) == LIBRA_INVALID_HANDLE);
    assert(libra_evaluate(instance, 0, &binding, 1, &output) == LIBRA_INVALID_HANDLE);

    bytes = builtinFixture();
    assert(libra_program_create(bytes.data(), bytes.size(), &program) == LIBRA_OK);
    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    assert(libra_bind_value(instance, bytes.data() + 16, &binding) == LIBRA_OK);
    assert(libra_evaluate(instance, 0, &binding, 1, &output) == LIBRA_OK && output.x == 8);
    assert(libra_bind_parameter(instance, bytes.data() + 16, 0, 0, &parameter) == LIBRA_OK);
    assert(libra_parameter_set(instance, parameter, -3) == LIBRA_OK);
    assert(libra_evaluate(instance, 0, &binding, 1, &output) == LIBRA_OK && output.x == 3);
    assert(libra_instance_release(instance) == LIBRA_OK);
    assert(libra_program_release(program) == LIBRA_OK);

    bytes = unityFixture();
    assert(libra_program_create(bytes.data(), bytes.size(), &program) == LIBRA_OK);
    assert(libra_instance_create(program, &instance) == LIBRA_OK);
    auto bindings = std::array<LibraBinding, 3>{};
    auto outputs = std::array<LibraValue, 3>{};
    assert(libra_bind_value(instance, bytes.data() + 16 + 96, &bindings[0]) == LIBRA_OK);
    assert(libra_bind_value(instance, bytes.data() + 16 + 4 * 96, &bindings[1]) == LIBRA_OK);
    assert(libra_bind_value(instance, bytes.data() + 16, &bindings[2]) == LIBRA_OK);
    assert(libra_evaluate(instance, 1, bindings.data(), 3, outputs.data()) == LIBRA_OK);
    assert(outputs[0].x == 0.2 && outputs[1].componentCount == 4 && outputs[1].x == 0.4 &&
           outputs[1].w == 0.4 && outputs[2].x == 1);
    assert(libra_bind_parameter(instance, bytes.data() + 16 + 96, 1, 0, &parameter) == LIBRA_OK);
    assert(libra_parameter_set(instance, parameter, 0.3) == LIBRA_OK);
    assert(libra_evaluate(instance, 1, bindings.data(), 3, outputs.data()) == LIBRA_OK && outputs[1].x == 0.5);
    assert(libra_instance_release(instance) == LIBRA_OK);
    assert(libra_program_release(program) == LIBRA_OK);

    auto atlas = LibraAtlasFrame{};
    assert(libra_sample_sprite_atlas(17.9, 4, 4, &atlas) == LIBRA_OK && atlas.frameIndex == 1 &&
           atlas.u == 0.25f && atlas.v == 0 && atlas.width == 0.25f);
    assert(libra_sample_sprite_rect(42, 0.125f, 0.25f, 0.5f, 0.25f, &atlas) == LIBRA_OK &&
           atlas.frameIndex == 42 && atlas.u == 0.125f && atlas.v == 0.25f &&
           atlas.width == 0.5f && atlas.height == 0.25f);
    assert(libra_sample_sprite_rect(0, 0.9f, 0, 0.2f, 1, &atlas) == LIBRA_INVALID_ARGUMENT);
    auto durations = std::array<double, 3>{{1.0, 2.0, 1.0}};
    auto sequenceFrame = std::uint32_t{};
    assert(libra_sample_sprite_sequence(-1, 0.25, 4, durations.data(), 3, 1, 2, 0, 1,
        &sequenceFrame) == LIBRA_OK && sequenceFrame == 1);
    assert(libra_sample_sprite_sequence(2, 0, 4, durations.data(), 3, 1, 2, 1, 0,
        &sequenceFrame) == LIBRA_OK && sequenceFrame == 2);
    assert(libra_sample_sprite_sequence(-1, 0, 4, durations.data(), 3, 2, 1, 1, 1,
        &sequenceFrame) == LIBRA_INVALID_ARGUMENT);
    assert(libra_sample_sprite_atlas(-1, 4, 4, &atlas) == LIBRA_OK && atlas.frameIndex == 15);
    auto quad = std::array<LibraSpriteVertex, 4>{};
    assert(libra_make_sprite_quad(&atlas, 2, 4, quad.data()) == LIBRA_OK &&
           quad[0].x == -1 && quad[0].y == 2 && quad[0].u == 0.75f &&
           quad[2].x == 1 && quad[2].y == -2 && quad[2].v == 1);
    struct alignas(16) VertexStorage { std::uint32_t prefix; LibraSpriteVertex vertices[4]; };
    auto vertexStorage = VertexStorage{};
    assert(reinterpret_cast<std::uintptr_t>(vertexStorage.vertices) % 16 == 4);
    assert(libra_make_sprite_quad(&atlas, 2, 4, vertexStorage.vertices) == LIBRA_OK &&
           vertexStorage.vertices[2].u == 1);
    assert(libra_make_sprite_quad(&atlas, -1, 1, quad.data()) == LIBRA_INVALID_ARGUMENT);
    atlas.width = 0.5f;
    assert(libra_make_sprite_quad(&atlas, 1, 1, quad.data()) == LIBRA_INVALID_ARGUMENT);
    assert(libra_sample_sprite_atlas(0, 0, 4, &atlas) == LIBRA_INVALID_ARGUMENT);
    assert(libra_sample_sprite_atlas(0, UINT32_MAX, 2, &atlas) == LIBRA_INVALID_ARGUMENT);
    assert(libra_sample_sprite_atlas(std::numeric_limits<double>::quiet_NaN(), 1, 1, &atlas) == LIBRA_INVALID_ARGUMENT);
    assert(libra_sample_sprite_atlas(0, 1, 1, nullptr) == LIBRA_INVALID_ARGUMENT);
    bytes = scalarFixture();
    bytes[0] = '!';
    program = 123;
    assert(libra_program_create(bytes.data(), bytes.size(), &program) == LIBRA_INVALID_BINARY && program == 0);
    bytes = scalarFixture(); bytes[4] = 99;
    assert(libra_program_create(bytes.data(), bytes.size(), &program) == LIBRA_UNSUPPORTED_BINARY);
    bytes = scalarFixture(); write32(bytes, 8, UINT32_MAX);
    assert(libra_program_create(bytes.data(), bytes.size(), &program) == LIBRA_INVALID_BINARY);
    bytes = builtinFixture(); bytes[55] = 255;
    assert(libra_program_create(bytes.data(), bytes.size(), &program) == LIBRA_UNKNOWN_CUSTOM_NODE);
    assert(libra_program_create(nullptr, 0, &program) == LIBRA_INVALID_ARGUMENT);
    assert(libra_program_create(bytes.data(), bytes.size(), nullptr) == LIBRA_INVALID_ARGUMENT);
}
