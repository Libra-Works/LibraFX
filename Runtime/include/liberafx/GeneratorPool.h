#pragma once

#include "liberafx/VfxRuntime.h"
#include <cstddef>
#include <cstdint>
#include <memory>

namespace liberafx {

using GeneratorInstanceId = std::uint64_t;

struct GeneratorCapture final {
    std::uint8_t nodeId[16]{};
    std::uint8_t inputSlot = 0;
    std::uint8_t component = 0;
    double value = 0;
};

enum class GeneratorEventKind : std::uint8_t { Spawn = 1, Stop = 2 };
struct GeneratorEvent final {
    GeneratorEventKind kind{};
    std::uint8_t containerId[16]{};
    GeneratorInstanceId instanceId = 0;
    double startTime = 0;
    std::uint64_t seed = 0;
};

// enqueueSpawn may be called concurrently; all other operations belong to one
// owner thread. The Program, bytecode, registry and bindings must outlive the pool.
// Evaluation is synchronous; events are host data, never engine object operations.
class LIBRA_RUNTIME_CORE_API GeneratorPool final {
public:
    static Result create(const Program& program, const std::uint8_t containerId[16],
        std::size_t maxActive, std::size_t memoryBudgetBytes, std::size_t eventCapacity,
        std::unique_ptr<GeneratorPool>& out) noexcept;
    ~GeneratorPool();
    GeneratorPool(const GeneratorPool&) = delete;
    GeneratorPool& operator=(const GeneratorPool&) = delete;

    // CaptureAtSpawn: values address unconnected template parameter defaults.
    Result spawn(double time, const GeneratorCapture* captures, std::size_t count,
                 GeneratorInstanceId& out) noexcept;
    // Allocation-free signal callback path. Up to 16 captured scalar lanes.
    // Owner processes commands outside callback execution. A rejected command
    // remains queued until resources are available or reset clears it.
    Result enqueueSpawn(double time, const GeneratorCapture* captures, std::size_t count) noexcept;
    Result processSpawns(std::size_t maxCommands, std::size_t& processed) noexcept;
    Result stop(GeneratorInstanceId id) noexcept;
    Result reset() noexcept;
    Result evaluate(GeneratorInstanceId id, double time) noexcept;
    Result value(GeneratorInstanceId id, const std::uint8_t nodeId[16],
                 double out[4], std::uint8_t& width) noexcept;
    bool popEvent(GeneratorEvent& out) noexcept;
    std::size_t activeCount() const noexcept;

private:
    struct Impl;
    explicit GeneratorPool(std::unique_ptr<Impl> impl) noexcept;
    std::unique_ptr<Impl> impl_;
};

}
