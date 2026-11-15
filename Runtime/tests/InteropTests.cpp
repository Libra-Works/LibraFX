#pragma pack(push, 1)
#include "liberafx/Interop.h"
#pragma pack(pop)
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <array>
#include <cassert>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
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
#if defined(_MSC_VER) && defined(_DEBUG)
auto frameAllocations = 0u;
auto __cdecl allocationHook(int kind, void*, size_t, int, long, const unsigned char*, int) -> int
{
    if (kind == _HOOK_ALLOC || kind == _HOOK_REALLOC) ++frameAllocations;
    return 1;
}
#endif
}

int main()
{
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
        assert(libra_generator_start(pool, id) == LIBRA_OK);
        assert(libra_generator_start(pool, id) == LIBRA_OK);
        assert(libra_generator_evaluate(pool, id, 13) == LIBRA_OK);
        assert(libra_generator_finalize(pool, id) == LIBRA_NOT_READY);
        assert(libra_generator_next_event(pool, &event, &has) == LIBRA_OK && has && event.kind == 3 && event.instanceId == id);
        assert(libra_generator_finalize(pool, id) == LIBRA_OK);
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
