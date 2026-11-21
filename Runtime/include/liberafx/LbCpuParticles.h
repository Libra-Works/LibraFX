#pragma once
#include "liberafx/LbVfxRuntime.h"
#include <memory>

namespace liberafx {

struct CpuParticleSnapshot final {
    std::uint64_t id = 0;
    double position[3]{};
    double color[4]{1, 1, 1, 1};
    double size[2]{1, 1};
};
static_assert(sizeof(CpuParticleSnapshot) == 80);
struct CpuParticleTiming final { double age = 0, life = 0; };

// Every outbound message retains its origin, including messages from particles
// that died in the same step. Property=1, Signal=2. No engine callback is invoked.
struct CpuParticleEvent final {
    std::uint8_t systemId[16]{};
    std::uint8_t nodeId[16]{};
    std::uint64_t particleId = 0;
    double time = 0;
    double values[4]{};
    std::uint32_t kind = 0;
    std::uint32_t width = 0;
};
static_assert(sizeof(CpuParticleEvent) == 88);

// Whole-effect owner creates this once. All particles use contiguous caller
// (effect-host)-owned work/default slabs; no per-node/per-frame allocations.
// Descriptors and payload must outlive the world. All calls are owner-thread.
class LIBRA_RUNTIME_CORE_API CpuParticleWorld final {
public:
    static Result create(const std::uint8_t* payload, std::size_t bytes,
        const CustomNodeDescriptor* registry, std::size_t registryCount,
        std::unique_ptr<CpuParticleWorld>& out, bool enableBatching = true) noexcept;
    ~CpuParticleWorld();
    const CustomNodeDescriptor* signalDescriptors(std::size_t& count) const noexcept;
    // At a new effect step, deliver previous step's explicit LastById reductions
    // before root evaluation, and signals after it. Inputs are then sampled once.
    Result prepare(Instance& root) noexcept;
    Result deliverSignals(Instance& root, double time, std::size_t budget) noexcept;
    Result advance(Instance& root, double time, bool emitting, const HostQualitySnapshot& quality,
        double frameDelta = 0) noexcept;
    Result queueSignal(const std::uint8_t nodeId[16]) noexcept;
    Result count(const std::uint8_t systemId[16], std::size_t& out) const noexcept;
    Result at(const std::uint8_t systemId[16], std::size_t index, CpuParticleSnapshot& out) const noexcept;
    Result timing(const std::uint8_t systemId[16], std::size_t index, CpuParticleTiming& out) const noexcept;
    bool popEvent(CpuParticleEvent& out) noexcept;
    std::size_t pendingEventCount() const noexcept;
    std::size_t activeCount() const noexcept;
    bool canEmit() const noexcept;
    bool hasPendingRoutes() const noexcept;
    void clear() noexcept;
private:
    struct Impl;
    explicit CpuParticleWorld(std::unique_ptr<Impl> impl) noexcept;
    std::unique_ptr<Impl> impl_;
};

// Only the two effect-level emitter nodes belong to the global registry.
LIBRA_RUNTIME_CORE_API const CustomNodeDescriptor* cpuEmitterNodes(std::size_t& count) noexcept;
}
