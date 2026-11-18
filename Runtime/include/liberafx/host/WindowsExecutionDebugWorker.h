#pragma once

#include "liberafx/host/WindowsExecutionDebugClient.h"

namespace liberafx::host {
// For hosts without an existing async scheduler. The owner enqueues and
// observes; only the worker enters the core. Transport never touches Instance.
class WindowsExecutionDebugWorker final {
public:
    WindowsExecutionDebugWorker(Instance& instance, std::uint64_t identity)
        : instance_(instance), client_(identity) {}
    ~WindowsExecutionDebugWorker()
    {
        client_.stop();
        if (worker_.joinable()) worker_.join();
        if (hooked_) instance_.setDebugHooks({});
    }
    Result start(const char* invitation)
    {
        auto result = instance_.setDebugHooks(client_.hooks());
        if (result != Result::Ok) return result;
        hooked_ = true;
        return client_.start(invitation) ? Result::Ok : Result::InvalidArgument;
    }
    Result schedule(double time) noexcept
    {
        if (worker_.joinable()) return Result::Busy;
        completed_.store(false, std::memory_order_relaxed);
        return instance_.updateAsync(time, enqueue, this);
    }
    Result finish() noexcept
    {
        if (!worker_.joinable()) return Result::NotReady;
        if (!completed_.load(std::memory_order_acquire)) return Result::Busy;
        worker_.join();
        return result_;
    }
private:
    static bool enqueue(void* context, Job job, void* data) noexcept
    {
        auto& self = *static_cast<WindowsExecutionDebugWorker*>(context);
        try {
            self.worker_ = std::thread([&self, job, data] {
                job(data);
                self.result_ = self.instance_.result();
                if (self.result_ == Result::Ok)
                    self.result_ = self.instance_.dispatchGeneratedSignals(10000);
                self.completed_.store(true, std::memory_order_release);
            });
            return true;
        } catch (...) { return false; }
    }
    Instance& instance_;
    WindowsExecutionDebugClient client_;
    std::thread worker_;
    std::atomic<bool> completed_{false};
    Result result_ = Result::NotReady;
    bool hooked_ = false;
};
}
