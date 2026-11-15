#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include "liberafx/platform/JobScheduler.h"

namespace liberafx {

enum class Result : std::uint8_t {
    Ok,
    InvalidArgument,
    InvalidBinary,
    UnsupportedBinary,
    InsufficientWork,
    Busy,
    QueueRejected,
    NotReady,
    NodeNotFound,
    DivideByZero,
    NonFinite,
    InvalidValueType
};

class Program final {
public:
    // The program and its bytecode must remain immutable and alive while instances use them.
    static Result load(const void* bytes, std::size_t size, Program& out) noexcept;
    std::size_t nodeCount() const noexcept { return nodeCount_; }
    std::size_t workBytes() const noexcept { return nodeCount_ * (version_ == 1 ? sizeof(double) : 4 * sizeof(double)); }
    std::size_t parameterValueCount() const noexcept { return nodeCount_ * (version_ == 1 ? 2 : 8); }
    std::uint16_t version() const noexcept { return version_; }

private:
    friend class Instance;
    const std::uint8_t* bytes_ = nullptr;
    std::uint32_t nodeCount_ = 0;
    std::uint16_t version_ = 0;
};

class Instance final {
public:
    // Call control/read methods from one owner thread; the scheduler runs the job on a worker.
    // Work is owned by the caller and must remain alive until all jobs complete.
    Result initialize(const Program& program, double* work, std::size_t workCount) noexcept;
    // The optional defaults buffer is caller-owned, separate from work, and has parameterValueCount() lanes.
    Result initialize(const Program& program, double* work, std::size_t workCount,
                      double* defaults, std::size_t defaultsCount) noexcept;
    Result updateAsync(double time, Enqueue enqueue, void* queueContext) noexcept;
    // inputSlot is 0 for A/defaultA or 1 for B/defaultB; only finite, valid lanes may be changed.
    Result setParameter(const std::uint8_t nodeId[16], std::uint8_t inputSlot,
                        std::uint8_t component, double value) noexcept;
    Result result() const noexcept;
    Result value(const std::uint8_t nodeId[16], double& out) const noexcept;
    Result valueComponents(const std::uint8_t nodeId[16], double out[4], std::uint8_t& componentCount) const noexcept;
    std::size_t capacityGrowthCount() const noexcept { return 0; }

private:
    enum class Phase : std::uint8_t { Idle, Running, Complete };
    static void run(void* context) noexcept;
    Result evaluate() noexcept;

    const Program* program_ = nullptr;
    double* work_ = nullptr;
    double* defaults_ = nullptr;
    double time_ = 0;
    Result result_ = Result::NotReady;
    std::atomic<Phase> phase_{Phase::Idle};
};

}
