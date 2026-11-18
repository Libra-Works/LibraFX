#pragma once

#include "liberafx/VfxRuntime.h"
#include "liberafx/ExecutionDebugHooks.h"
#include <array>
#include <cassert>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <mutex>
#include <thread>

namespace liberafx {

enum class DebugExecutionState : std::uint8_t { Detached, Running, Paused, Aborted, Complete };
struct DebugPauseSnapshot final {
    std::array<std::uint8_t, 16> nodeId{};
    std::array<DebugInputValue, 16> inputs{};
    std::size_t inputCount = 0;
    std::uint64_t sequence = 0;
    double time = 0;
    std::array<std::uint8_t, 16> signalSourceNodeId{};
};

// One worker executor, independent thread-safe control. A paused worker retains
// its real call stack; stepping does not replay earlier stateful nodes.
class ExecutionDebugger final {
public:
    using NodeId = std::array<std::uint8_t, 16>;
    ExecutionDebugger(NodeId* breakpoints, std::size_t capacity,
        std::chrono::milliseconds pauseTimeout = std::chrono::seconds(30)) noexcept
        : breakpoints_(breakpoints), capacity_(capacity), timeout_(pauseTimeout),
          owner_(std::this_thread::get_id()) {}
    ~ExecutionDebugger()
    {
        auto lock = std::lock_guard<std::mutex>(mutex_);
        assert(!inCallback_ && "Close and join the executor before destroying its debugger.");
    }
    ExecutionDebugger(const ExecutionDebugger&) = delete;
    ExecutionDebugger& operator=(const ExecutionDebugger&) = delete;
    ExecutionDebugHooks hooks() noexcept { return {this, before}; }
    Result attach() noexcept
    {
        auto lock = std::lock_guard<std::mutex>(mutex_);
        if (attached_ || inCallback_) return Result::Busy;
        if ((!breakpoints_ && capacity_) || timeout_.count() <= 0 ||
            timeout_ > std::chrono::minutes(5)) return Result::InvalidArgument;
        attached_ = true;
        pauseRequested_ = stepNext_ = false;
        snapshot_ = {};
        visitSequence_ = 0;
        state_ = DebugExecutionState::Running;
        return Result::Ok;
    }
    Result breakpoint(NodeId id, bool enabled) noexcept
    {
        auto nonzero = false;
        for (auto byte : id) nonzero = nonzero || byte != 0;
        if (!nonzero) return Result::InvalidArgument;
        auto lock = std::lock_guard<std::mutex>(mutex_);
        if (!breakpoints_ && capacity_) return Result::InvalidArgument;
        for (auto index = std::size_t{}; index < count_; ++index) {
            if (breakpoints_[index] != id) continue;
            if (!enabled) breakpoints_[index] = breakpoints_[--count_];
            return Result::Ok;
        }
        if (!enabled) return Result::Ok;
        if (count_ == capacity_) return Result::QueueRejected;
        breakpoints_[count_++] = id;
        return Result::Ok;
    }
    Result pause() noexcept
    {
        auto lock = std::lock_guard<std::mutex>(mutex_);
        if (!attached_ || state_ != DebugExecutionState::Running) return Result::NotReady;
        pauseRequested_ = true;
        return Result::Ok;
    }
    Result resume(bool singleStep) noexcept
    {
        auto lock = std::lock_guard<std::mutex>(mutex_);
        if (state_ != DebugExecutionState::Paused) return Result::NotReady;
        stepNext_ = singleStep;
        state_ = DebugExecutionState::Running;
        condition_.notify_all();
        return Result::Ok;
    }
    Result snapshot(DebugPauseSnapshot& out) const noexcept
    {
        auto lock = std::lock_guard<std::mutex>(mutex_);
        if (state_ != DebugExecutionState::Paused) return Result::NotReady;
        out = snapshot_;
        return Result::Ok;
    }
    DebugExecutionState sample(DebugPauseSnapshot& out) const noexcept
    {
        auto lock = std::lock_guard<std::mutex>(mutex_);
        out = snapshot_;
        out.sequence = visitSequence_;
        return state_;
    }
    // A lost control connection releases the already validated visit without
    // replaying or abandoning a partially executed stateful update.
    void detach() noexcept
    {
        auto lock = std::lock_guard<std::mutex>(mutex_);
        attached_ = false;
        count_ = 0;
        state_ = DebugExecutionState::Detached;
        condition_.notify_all();
    }
    DebugExecutionState state() const noexcept
    {
        auto lock = std::lock_guard<std::mutex>(mutex_);
        return state_;
    }
    bool waitForPause(std::chrono::milliseconds timeout) noexcept
    {
        auto lock = std::unique_lock<std::mutex>(mutex_);
        condition_.wait_for(lock, timeout, [this] {
            return state_ == DebugExecutionState::Paused || state_ == DebugExecutionState::Complete ||
                state_ == DebugExecutionState::Aborted;
        });
        return state_ == DebugExecutionState::Paused;
    }
    void complete() noexcept
    {
        auto lock = std::lock_guard<std::mutex>(mutex_);
        if (!inCallback_ && state_ == DebugExecutionState::Running) state_ = DebugExecutionState::Complete;
        condition_.notify_all();
    }
    // Busy means wake-up was requested but the worker still owns a callback.
    // Join/await that worker before destroying its instance, buffers or debugger.
    Result close() noexcept
    {
        auto lock = std::lock_guard<std::mutex>(mutex_);
        attached_ = false;
        state_ = inCallback_ ? DebugExecutionState::Aborted : DebugExecutionState::Detached;
        condition_.notify_all();
        return inCallback_ ? Result::Busy : Result::Ok;
    }
    static Result before(void* context, const DebugNodeVisit& visit) noexcept
    {
        return context ? static_cast<ExecutionDebugger*>(context)->visit(visit) : Result::InvalidArgument;
    }
private:
    Result visit(const DebugNodeVisit& visit) noexcept
    {
        auto lock = std::unique_lock<std::mutex>(mutex_);
        if (!attached_) return state_ == DebugExecutionState::Aborted ? Result::NotReady : Result::Ok;
        if (std::this_thread::get_id() == owner_) return Result::InvalidArgument;
        if (inCallback_) return Result::Busy;
        if (!visit.nodeId || (visit.inputCount && !visit.inputs) ||
            visit.inputCount > snapshot_.inputs.size() || !std::isfinite(visit.time)) return Result::InvalidArgument;
        for (auto index = std::size_t{}; index < visit.inputCount; ++index) {
            const auto& input = visit.inputs[index];
            if (static_cast<unsigned>(input.kind) > static_cast<unsigned>(DebugValueKind::Unavailable) ||
                input.width > 4 ||
                (input.kind == DebugValueKind::Unavailable ? input.width != 0 : input.width == 0) ||
                (input.kind == DebugValueKind::Color && input.width != 4) ||
                (input.kind == DebugValueKind::Int32 && (input.width != 1 ||
                    !std::isfinite(input.components[0]) || input.components[0] < -2147483648.0 ||
                    input.components[0] > 2147483647.0 || std::trunc(input.components[0]) != input.components[0])))
                return Result::InvalidArgument;
            for (auto component = 0u; component < input.width; ++component)
                if (!std::isfinite(input.components[component])) return Result::NonFinite;
        }
        auto stopHere = pauseRequested_ || stepNext_;
        for (auto index = std::size_t{}; !stopHere && index < count_; ++index)
            stopHere = std::memcmp(breakpoints_[index].data(), visit.nodeId, 16) == 0;
        if (visitSequence_ == UINT64_MAX) return Result::CounterOverflow;
        std::memcpy(snapshot_.nodeId.data(), visit.nodeId, 16);
        snapshot_.signalSourceNodeId = {};
        if (visit.signalSourceNodeId)
            std::memcpy(snapshot_.signalSourceNodeId.data(), visit.signalSourceNodeId, 16);
        snapshot_.inputCount = visit.inputCount;
        snapshot_.time = visit.time;
        ++visitSequence_;
        if (stopHere) ++snapshot_.sequence;
        snapshot_.inputs = {};
        for (auto index = std::size_t{}; index < snapshot_.inputCount; ++index)
            snapshot_.inputs[index] = visit.inputs[index];
        if (!stopHere) return Result::Ok;
        inCallback_ = true;
        pauseRequested_ = false;
        stepNext_ = false;
        state_ = DebugExecutionState::Paused;
        condition_.notify_all();
        auto released = condition_.wait_for(lock, timeout_, [this] {
            return state_ != DebugExecutionState::Paused;
        });
        if (!released) {
            state_ = DebugExecutionState::Aborted;
            attached_ = false;
            condition_.notify_all();
        }
        inCallback_ = false;
        return state_ == DebugExecutionState::Running || state_ == DebugExecutionState::Detached
            ? Result::Ok : Result::NotReady;
    }
    NodeId* breakpoints_;
    std::size_t capacity_, count_ = 0;
    std::chrono::milliseconds timeout_;
    std::thread::id owner_;
    mutable std::mutex mutex_;
    std::condition_variable condition_;
    DebugPauseSnapshot snapshot_{};
    std::uint64_t visitSequence_ = 0;
    DebugExecutionState state_ = DebugExecutionState::Detached;
    bool attached_ = false, inCallback_ = false, pauseRequested_ = false, stepNext_ = false;
};

}
