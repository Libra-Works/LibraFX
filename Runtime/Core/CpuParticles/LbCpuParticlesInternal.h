#pragma once
#include "liberafx/LbCpuParticles.h"
#include "liberafx/LbMeshEmission.h"
#include "liberafx/host/LbProceduralAdapter.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>
#include <vector>

namespace liberafx::cpu_particle_detail {
using Id = std::array<std::uint8_t, 16>;
inline auto nonzero(const Id& id) noexcept -> bool
{
    return std::any_of(id.begin(), id.end(), [](auto value) { return value != 0; });
}
inline auto typeId(std::uint8_t suffix) noexcept -> Id
{
    return {0x7c, 0x7e, 0x10, 0x00, 0x25, 0x21, 0x4b, 0xdb, 0xb8, 0xa0, 0x21, 0, 0, 0, 0, suffix};
}
struct Buffer final {
    void* data = nullptr;
    std::size_t alignment = 16;
    ~Buffer() { reset(); }
    void reset() noexcept
    {
        if (data)
        {
            ::operator delete(data, std::align_val_t{alignment});
            data = nullptr;
        }
    }
    void allocate(std::size_t bytes, std::size_t align)
    {
        alignment = std::max<std::size_t>(16, align);
        if (bytes != 0)
        {
            data = ::operator new(bytes, std::align_val_t{alignment});
        }
    }
};
struct Io final {
    std::uint8_t kind = 0;
    std::uint32_t width = 0;
    Id node{}, root{};
    std::uint32_t component = 0;
    std::array<double, 4> sampled{};
};
struct Stage final {
    Id entry{};
    std::vector<std::uint8_t> bytes;
    Program program;
    std::vector<CustomNodeBinding> bindings;
    std::vector<Io> io;
    Buffer work, defaults;
    std::size_t workStride = 0, defaultStride = 0;
};
struct BatchInput final {
    std::uint32_t source = 0xffffffffu;
    std::uint8_t component = 0;
    std::array<double,4> constants{};
};
enum class BatchOp : std::uint8_t { Copy, Add, Subtract, Multiply, Divide, Broadcast, Clamp, Compose, Sphere, LifetimeColor,
    Position, Velocity, Color, Size, Life, Age, Id, Seed, Delta, SetPosition, SetVelocity, SetColor, SetSize, SetLife, Move };
struct DecodedBatchInstruction final {
    Id sourceNode{};
    BatchOp op{};
    std::uint32_t output = 0;
    std::uint8_t width = 0;
    std::array<BatchInput,4> inputs{};
};
// Hot execution record. High-bit operands address the shared constant pool;
// other operands address a node/component register in the block scratch.
struct BatchInstruction final {
    std::uint16_t output = 0;
    BatchOp op{};
    std::uint8_t width = 0;
    std::array<std::uint16_t,4> inputs{};
};
static_assert(sizeof(BatchInstruction) == 12);
struct ParticleBatchPlan final {
    static constexpr std::size_t maximumNodes = 64, batchSize = 32;
    bool enabled = false;
    std::vector<BatchInstruction> values, commands;
    std::vector<double> constants;
    std::vector<Id> sourceNodes;
};
// Canonical simulation storage. Every component is contiguous across particles;
// rendering snapshots are assembled only at the read boundary. Stable slot
// indices retain ID, retirement and interpreter ordering without an AoS mirror.
struct ParticleStorage final {
    std::array<std::vector<double>, 3> position, velocity;
    std::array<std::vector<double>, 4> color;
    std::array<std::vector<double>, 2> size;
    std::vector<double> age, life, realStart;
    std::vector<std::uint64_t> id;
    std::vector<std::uint8_t> active;
    void initialize(std::size_t capacity)
    {
        for (auto& channel : position) { channel.resize(capacity); }
        for (auto& channel : velocity) { channel.resize(capacity); }
        for (auto& channel : color) { channel.resize(capacity, 1); }
        for (auto& channel : size) { channel.resize(capacity, 1); }
        age.resize(capacity); life.resize(capacity, 2); realStart.resize(capacity);
        id.resize(capacity); active.resize(capacity);
    }
    void reset(std::uint32_t slot, std::uint64_t identity, double realTime) noexcept
    {
        for (auto& channel : position) { channel[slot] = 0; }
        for (auto& channel : velocity) { channel[slot] = 0; }
        for (auto& channel : color) { channel[slot] = 1; }
        for (auto& channel : size) { channel[slot] = 1; }
        id[slot] = identity; age[slot] = 0; life[slot] = 2;
        realStart[slot] = realTime; active[slot] = 0;
    }
    CpuParticleSnapshot snapshot(std::uint32_t slot) const noexcept
    {
        auto out = CpuParticleSnapshot{};
        out.id = id[slot];
        for (auto lane = 0u; lane < 3; ++lane) { out.position[lane] = position[lane][slot]; }
        for (auto lane = 0u; lane < 4; ++lane) { out.color[lane] = color[lane][slot]; }
        for (auto lane = 0u; lane < 2; ++lane) { out.size[lane] = size[lane][slot]; }
        return out;
    }
};
struct ParticleVM final {
    Instance stages[2];
};
struct Inbound final {
    Id node{};
    std::uint32_t stage = 0;
};
struct Outbound final {
    CpuParticleEvent event;
    Id target{};
};
struct System;
struct Callback final {
    System* system = nullptr;
    Id node{};
    std::uint32_t operation = 0, stage = 0;
};
struct System final {
    ~System()
    {
        instances.reset();
        // Both kinds of VM borrow the stage programs and node descriptors.
        // Destroy them before member destruction releases those descriptors.
        sharedUpdate.reset();
        sharedSpawn.reset();
        if (emitterModel) libra_procedural_model_release(emitterModel);
    }
    std::vector<std::uint8_t> emitterDefinitionBytes;
    procedural_adapter::Definition emitterDefinition;
    LibraProceduralModelInstance* emitterModel = nullptr;
    std::uint64_t emitterVertexRevision = 0;
    std::array<float, 3> emitterPivot{};
    MeshEmissionSurface emitterSurface;
    Result refreshEmitter(double time);
    Id id{}, emissionSource{};
    std::uint32_t capacity = 0, seed = 0, queueCapacity = 0, burst = 0, emissionComponent = 0;
    double rate = 0, remainder = 0, lastTotal = 0, lastTime = -1, stepDelta = 0;
    double frameDelta = 0;
    double realTime = 0, realDelta = 0;
    std::uint32_t initialBurst = 0;
    std::uint64_t nextId = 1;
    std::size_t live = 0;
    Stage stages[2];
    ParticleBatchPlan updateBatch, spawnBatch;
    Buffer sharedSpawnWork;
    std::unique_ptr<Instance> sharedSpawn;
    SignalHandle sharedSpawnEntry{};
    Buffer sharedUpdateWork;
    std::unique_ptr<Instance> sharedUpdate;
    SignalHandle sharedUpdateEntry{};
    ParticleStorage particleData;
    std::unique_ptr<ParticleVM[]> instances;
    std::vector<std::uint32_t> activeIndices;
    std::uint32_t current = UINT32_MAX;
    std::vector<CustomNodeDescriptor> descriptors;
    std::vector<Callback> callbacks;
    std::vector<Inbound> incoming;
    std::size_t incomingCount = 0;
    std::vector<Outbound> pending, delivery;
    std::size_t pendingCount = 0, deliveryCount = 0;
    std::vector<CpuParticleEvent> events;
    std::size_t eventHead = 0, eventCount = 0;
    Result failure = Result::Ok;

    Result initialize(const CustomNodeDescriptor* registry, std::size_t registryCount, bool enableBatching = false);
    Result tick(Instance& root, double time, bool emitting, const HostQualitySnapshot& quality) noexcept;
    Result run(std::uint32_t slot, std::uint32_t stage, double time, const HostQualitySnapshot& quality) noexcept;
    void buildBatches();
    void buildBatch(std::uint32_t stageIndex);
    void optimizeBatch(ParticleBatchPlan& plan);
    Result evaluateBatch(const ParticleBatchPlan& plan, const std::uint32_t* particles, std::size_t size, bool* commandsStarted = nullptr) noexcept;
    Result spawnBatches(std::uint32_t count, double time, const HostQualitySnapshot& quality) noexcept;
    Result updateBatches(double time, const HostQualitySnapshot& quality) noexcept;
    Result publish(const Io& io, const double* values, double time) noexcept;
    Result collect(std::uint32_t slot, std::uint32_t stage, double time) noexcept;
    static Result invoke(CustomNodeInvocation& call) noexcept;
    static Result observe(void* context, const std::uint8_t nodeId[16]) noexcept;
};
Result parse(const std::uint8_t* bytes, std::size_t count,
    std::vector<std::unique_ptr<System>>& systems);
}

namespace liberafx {
struct CpuParticleWorld::Impl final {
    std::vector<std::unique_ptr<cpu_particle_detail::System>> systems;
    std::vector<CustomNodeDescriptor> signals;
};
}
