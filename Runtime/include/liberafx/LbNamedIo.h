#pragma once

#include "liberafx/LbLifetime.h"
#include <cstddef>
#include <cstdint>
#include <memory>

namespace liberafx {

enum class NamedIoKind : std::uint8_t {
    InputProperty, InputSignal, OutputProperty, OutputSignal, OwnerMatrix
};

struct NamedIoPort final {
    const char* name = nullptr;
    NamedIoKind kind{};
    std::uint8_t nodeId[16]{};
    std::uint8_t additionalNodeIds[3][16]{};
    std::uint8_t width = 0;
    bool isColor = false;
    std::uint8_t nodeCount = 0;
};

struct NamedIoIdentity final {
    std::uint8_t containerId[16]{};
    std::uint64_t instanceId = 0;
};

struct NamedIoEvent final {
    NamedIoIdentity identity{};
    std::uint32_t portIndex = 0;
    NamedIoKind kind{};
    std::uint8_t width = 0;
    bool isColor = false;
    double values[16]{};
};

// Explicit game-facing ports only, not group-local boundary ports. An
// owner-thread facade around one initialized Instance. Descriptors and
// names are copied at creation; the Instance and its Program must outlive it.
// Events are queued data, never callbacks into host objects during evaluation.
class LIBRA_RUNTIME_CORE_API NamedIo final {
public:
    static Result create(Instance& instance, const NamedIoPort* ports,
                         std::size_t count, std::size_t eventCapacity,
                         std::unique_ptr<NamedIo>& out,
                         const NamedIoIdentity& identity = {}) noexcept;
    ~NamedIo();
    NamedIo(const NamedIo&) = delete;
    NamedIo& operator=(const NamedIo&) = delete;

    Result setProperty(const char* name, const double* values, std::size_t width) noexcept;
    Result setOwnerMatrix(const double* values, std::size_t width) noexcept;
    // Must be called after initial input/binding setup. Calling it again is
    // idempotent; an explicit restart uses a newly initialized instance/facade.
    Result start(double time, const std::uint8_t* onStartNodeId,
                 std::size_t instructionBudget) noexcept;
    // Capture a finite duration from the initial evaluation before publishing
    // properties or dispatching OnStart. Requires a fresh facade.
    Result startCaptured(double time, const std::uint8_t* onStartNodeId,
                         std::size_t instructionBudget, const std::uint8_t lifeNodeId[16],
                         Lifetime& lifetime) noexcept;
    Result update(double time) noexcept;
    Result fireSignal(const char* name, double time, std::size_t instructionBudget) noexcept;
    Result fireLifecycleSignal(const SignalHandle& signal, double time, std::size_t instructionBudget) noexcept;
    Result fireDeferredSignal(double time, std::size_t instructionBudget) noexcept;
    // Intended for a compiled output-signal callback. Only queues a record.
    Result emitSignal(const char* name) noexcept;
    // Retry this after draining if update reported QueueRejected.
    Result publishProperties() noexcept;
    bool popEvent(NamedIoEvent& out) noexcept;
    bool peekEvent(NamedIoEvent& out) const noexcept;
    const char* portName(std::size_t index) const noexcept;

private:
    struct Impl;
    static Result observeSignal(void* context, const std::uint8_t nodeId[16]) noexcept;
    Result startInternal(double time, const std::uint8_t* onStartNodeId,
                         std::size_t instructionBudget, const std::uint8_t* lifeNodeId,
                         Lifetime* lifetime) noexcept;
    explicit NamedIo(std::unique_ptr<Impl> impl) noexcept;
    std::unique_ptr<Impl> impl_;
};

}
