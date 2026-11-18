#pragma once

#include "liberafx/SpawnableHost.h"
#include <atomic>
#include <array>
#include <cstring>

namespace liberafx::host {
enum class SpawnableDebugOperation : std::uint8_t { Advance, Signal, GameSignal, NaturalEnd, NotifyEnd };

// Exactly one copied request. The owner may access the host only after poll
// has acquired completion; no engine object is captured in this task.
class LIBRA_RUNTIME_CORE_API SpawnableDebugJob final {
public:
    struct Request final {
        SpawnableDebugOperation operation = SpawnableDebugOperation::Advance;
        double time = 0, frameDelta = 0;
        SpawnableScope scope{};
        std::array<std::uint8_t, 16> node{};
        std::array<char, 129> name{};
    };
    explicit SpawnableDebugJob(SpawnableHost& host) noexcept : host_(host) {}
    bool pending() const noexcept { return pending_; }
    Result submit(const Request& request, JobScheduler scheduler) noexcept
    {
        if (pending_) return Result::Busy;
        if (!scheduler.enqueue || request.name.back() != '\0') return Result::InvalidArgument;
        request_ = request;
        done_.store(false, std::memory_order_relaxed);
        pending_ = true;
        if (!scheduler.enqueue(scheduler.context, run, this)) {
            pending_ = false;
            return Result::QueueRejected;
        }
        return Result::Ok;
    }
    Result poll(Result& result) noexcept
    {
        if (!pending_) return Result::NotReady;
        if (!done_.load(std::memory_order_acquire)) return Result::Busy;
        result = result_;
        pending_ = false;
        return Result::Ok;
    }
private:
    static void run(void* self) noexcept;
    SpawnableHost& host_;
    Request request_{};
    std::atomic<bool> done_{false};
    bool pending_ = false;
    Result result_ = Result::NotReady;
};
}
