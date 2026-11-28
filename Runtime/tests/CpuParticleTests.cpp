#include "ProgramFixture.h"
#include "liberafx/LbCpuParticles.h"
#include "liberafx/LbSpawnableHost.h"
#include "liberafx/LbParticleAtlas.h"
#include "../Core/CpuParticles/LbCpuParticlesInternal.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <fstream>
#include <iterator>

namespace liberafx::cpu_particle_tests {
using Bytes = std::vector<std::uint8_t>;
auto emptyProgram() -> Bytes
{
    return runtime_test_fixture::promoteSimpleProgram(
        {'L', 'F', 'X', 'B', 2, 0, 1, 0, 0, 0, 0, 0, 96, 0, 0, 0});
}
void u32(Bytes& bytes, std::uint32_t value)
{
    for (auto index = 0u; index < 4; ++index)
    {
        bytes.push_back(static_cast<std::uint8_t>(value >> (index * 8)));
    }
}
void number(Bytes& bytes, double value)
{
    auto bits = std::uint64_t{};
    std::memcpy(&bits, &value, sizeof(bits));
    for (auto index = 0u; index < 8; ++index)
    {
        bytes.push_back(static_cast<std::uint8_t>(bits >> (index * 8)));
    }
}
auto payload(std::uint32_t capacity, std::uint32_t burst, double rate) -> Bytes
{
    auto bytes = Bytes{};
    u32(bytes, 2);
    u32(bytes, 1);
    bytes.resize(24, 0);
    bytes[8] = 1;
    u32(bytes, capacity);
    u32(bytes, 1234);
    u32(bytes, 32);
    number(bytes, rate);
    u32(bytes, burst);
    number(bytes, 0.0);
    bytes.resize(bytes.size() + 16, 0);
    u32(bytes, 0);
    for (auto stage = 0u; stage < 2; ++stage)
    {
        bytes.resize(bytes.size() + 16, 0);
        auto program = emptyProgram();
        u32(bytes, static_cast<std::uint32_t>(program.size()));
        bytes.insert(bytes.end(), program.begin(), program.end());
        u32(bytes, 0);
    }
    u32(bytes, 0);
    return bytes;
}
auto inlineJob(void*, Job job, void* context) noexcept -> bool
{
    job(context);
    return true;
}
void poolTests()
{
    auto atlas = ParticleAtlas{};
    atlas.enabled = true;
    atlas.columns = 2;
    atlas.rows = 2;
    atlas.fps = 4;
    assert(atlas.valid() && atlas.frame(0, 2) == 0 && atlas.frame(0.25, 2) == 1);
    assert(atlas.frame(0.75, 2) == 3 && atlas.frame(1, 2) == 0);
    atlas.startFrame = 1;
    assert(atlas.frame(0, 2) == 1 && atlas.frame(0.75, 2) == 0);
    atlas.loop = false;
    assert(atlas.frame(-1, 2) == 1 && atlas.frame(0.5, 2) == 3 && atlas.frame(1e100, 2) == 3);
    atlas.overLife = true;
    assert(atlas.frame(0, 3) == 1 && atlas.frame(1, 3) == 2 && atlas.frame(2, 3) == 3);
    assert(atlas.frame(3, 3) == 3 && atlas.frame(0, 0) == 1);
    atlas.columns = 0;
    assert(!atlas.valid() && atlas.frame(1, 2) == 0);
    auto bytes = payload(8, 3, 0);
    auto world = std::unique_ptr<CpuParticleWorld>{};
    assert(CpuParticleWorld::create(bytes.data(), bytes.size(), nullptr, 0, world) == Result::Ok);
    auto programBytes = emptyProgram();
    auto program = Program{};
    assert(Program::load(programBytes.data(), programBytes.size(), program) == Result::Ok);
    auto root = Instance{};
    alignas(16) auto work = std::array<double, 128>{};
    assert(root.initialize(program, work.data(), work.size()) == Result::Ok);
    assert(root.updateAsync(0, inlineJob, nullptr) == Result::Ok);
    assert(world->advance(root, 0, true, {}) == Result::Ok);
    auto id = std::array<std::uint8_t, 16>{1};
    auto count = std::size_t{};
    assert(world->count(id.data(), count) == Result::Ok && count == 3);
    auto snapshot = CpuParticleSnapshot{};
    auto timing = CpuParticleTiming{};
    for (auto index = 0u; index < 3; ++index)
    {
        assert(world->at(id.data(), index, snapshot) == Result::Ok && snapshot.id == index + 1);
        assert(snapshot.color[3] == 1 && snapshot.size[0] == 1 && snapshot.position[0] == 0);
    }
    assert(world->advance(root, 0, true, {}) == Result::Ok && world->activeCount() == 3);
    assert(world->advance(root, 1, false, {}) == Result::Ok && world->activeCount() == 3);
    assert(world->timing(id.data(), 0, timing) == Result::Ok && timing.age == 1 && timing.life == 2);
    assert(world->timing(id.data(), 3, timing) != Result::Ok);
    assert(world->advance(root, 2, false, {}) == Result::Ok && world->activeCount() == 0);
    assert(world->advance(root, 1, true, {}) == Result::InvalidArgument);
    bytes = payload(2, 0, 3);
    assert(CpuParticleWorld::create(bytes.data(), bytes.size(), nullptr, 0, world) == Result::Ok);
    assert(world->advance(root, 0, true, {}) == Result::Ok);
    assert(world->advance(root, 1, true, {}) == Result::InsufficientWork);
    assert(world->activeCount() == 0); // Emission is rejected, not silently truncated.
    bytes[0] = 1;
    assert(CpuParticleWorld::create(bytes.data(), bytes.size(), nullptr, 0, world) == Result::InvalidBinary);
    bytes[0] = 3;
    assert(CpuParticleWorld::create(bytes.data(), bytes.size(), nullptr, 0, world) == Result::InvalidBinary);
    bytes = payload(2, 0, 3);
    bytes.pop_back();
    assert(CpuParticleWorld::create(bytes.data(), bytes.size(), nullptr, 0, world) == Result::InvalidBinary);
}
void scopedNodeTests()
{
    using namespace cpu_particle_detail;
    auto system = System{};
    system.particleData.initialize(1);
    system.particleData.reset(0,23,0);
    system.current = 0;
    system.seed = 7;
    system.stepDelta = 0.5;
    auto context = Callback{&system, {}, 0x21, 0};
    auto inputs = std::array<double, 16>{4, 2, 0};
    auto invocation = CustomNodeInvocation{};
    invocation.inputs = inputs.data();
    invocation.context = &context;
    assert(System::invoke(invocation) == Result::Ok);
    context.operation = 0x25;
    inputs = {};
    assert(System::invoke(invocation) == Result::Ok);
    assert(system.particleData.position[0][0] == 2 && system.particleData.position[1][0] == 1);
    auto output = std::array<double, 4>{};
    invocation.outputs = output.data();
    context.operation = 0x16;
    assert(System::invoke(invocation) == Result::Ok && output[0] == 23);
    context.operation = 0x43;
    inputs[4] = 3;
    assert(System::invoke(invocation) == Result::Ok);
    auto first = output;
    assert(System::invoke(invocation) == Result::Ok && output == first);
    system.particleData.id[0] = 24;
    assert(System::invoke(invocation) == Result::Ok && output != first);
    system.pending.resize(2);
    system.events.resize(2);
    auto io = Io{};
    io.kind = 1;
    io.width = 1;
    io.node[0] = 5;
    assert(system.publish(io, output.data(), 1) == Result::Ok);
    assert(system.events[0].particleId == 24 && system.events[0].nodeId[0] == 5);
    assert(system.publish(io, output.data(), 1) == Result::Ok);
    assert(system.publish(io, output.data(), 1) == Result::QueueRejected);
    system.incoming.resize(1);
    context.operation = 0x36;
    assert(System::invoke(invocation) == Result::Ok);
    assert(System::invoke(invocation) == Result::QueueRejected);
}
void compiledFixture(const char* path)
{
    auto stream = std::ifstream(path, std::ios::binary);
    assert(stream.good());
    auto bytes = Bytes(std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>{});
    auto package = SpawnablePackage{};
    assert(loadSpawnablePackage(bytes.data(), bytes.size(), package) == Result::Ok);
    assert(package.packageVersion == 13 && package.cpuPrograms.size() > 24);
    auto id = std::array<std::uint8_t, 16>{};
    std::copy_n(package.cpuPrograms.data() + 8, 16, id.data());
    auto systemMetadata = std::vector<std::unique_ptr<cpu_particle_detail::System>>{};
    assert(cpu_particle_detail::parse(package.cpuPrograms.data(), package.cpuPrograms.size(), systemMetadata) == Result::Ok);
    auto inputSignal = std::array<std::uint8_t, 16>{};
    for (const auto& io : systemMetadata[0]->stages[1].io)
    {
        if (io.kind == 2)
        {
            inputSignal = io.node;
        }
    }
    auto host = std::unique_ptr<SpawnableHost>{};
    assert(SpawnableHost::create(std::move(package), {}, host) == Result::Ok);
    assert(host->advance(0, 0) == Result::Ok);
    auto count = std::size_t{};
    assert(host->cpuParticleCount(id.data(), count) == Result::Ok && count == 3);
    auto event = CpuParticleEvent{};
    auto outputs = std::size_t{};
    while (host->popCpuParticleEvent(event))
    {
        assert(event.kind == 1 && event.width == 1 && event.values[0] == event.particleId);
        assert(std::memcmp(event.systemId, id.data(), 16) == 0);
        ++outputs;
    }
    assert(outputs == 3);
    assert(host->dispatchSignal(inputSignal.data()) == Result::Ok);
    assert(host->advance(1, 1) == Result::Ok);
    auto snapshot = CpuParticleSnapshot{};
    assert(host->cpuParticleAt(id.data(), 0, snapshot) == Result::Ok);
    assert(snapshot.position[0] == 10); // Root Property -> per-particle Update VM -> Move -> native snapshot.
    auto signals = std::size_t{};
    while (host->popCpuParticleEvent(event))
    {
        if (event.kind == 2)
        {
            assert(event.particleId >= 1 && event.particleId <= 3 && event.width == 0);
            ++signals;
        }
    }
    assert(signals == 3); // Ordinary root Signal -> queued broadcast -> per-particle output Signal events.
    assert(host->stop() == Result::Ok && host->activeCount() == 0);
}
}
auto main(int argc, char** argv) -> int
{
    liberafx::cpu_particle_tests::poolTests();
    liberafx::cpu_particle_tests::scopedNodeTests();
    if (argc == 2)
    {
        liberafx::cpu_particle_tests::compiledFixture(argv[1]);
    }
    return 0;
}
