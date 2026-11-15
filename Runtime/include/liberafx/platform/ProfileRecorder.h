#pragma once

#include "liberafx/platform/Profiler.h"
#include <array>
#include <atomic>
#include <chrono>
#include <cstddef>

namespace liberafx {

struct ProfileMeasurement final {
    ProfileEvent event = ProfileEvent::Evaluate;
    std::uint32_t nodeIndex = noProfileNode;
    std::uint64_t calls = 0;
    std::uint64_t inclusiveNs = 0;
    std::uint64_t exclusiveNs = 0;
    std::uint64_t minNs = 0;
    std::uint64_t maxNs = 0;
};

struct ProfileCounter final {
    mutable std::atomic_flag locked = ATOMIC_FLAG_INIT;
    ProfileMeasurement measurement;
};

using ProfileClock = std::uint64_t (*)(void*) noexcept;

// Caller-owned counters; no per-scope allocation, engine dependency or async jobs.
// Configure/reset only while all profiling scopes have completed.
class ProfileRecorder final {
public:
    void initialize(ProfileCounter* counters, std::size_t count,
                    ProfileClock clock = nullptr, void* clockContext = nullptr) noexcept
    {
        counters_ = counters;
        count_ = counters == nullptr ? 0 : count;
        clock_ = clock == nullptr ? &steadyClock : clock;
        clockContext_ = clockContext;
        dropped_.store(0, std::memory_order_relaxed);
        for (auto index = std::size_t{0}; index < count_; ++index)
            counters_[index].measurement = {};
    }

    auto hooks(bool nodes = false, bool accessors = false) noexcept -> ProfilingHooks
    {
        return {this, &begin, &end, nodes, accessors};
    }
    std::uint64_t dropped() const noexcept { return dropped_.load(std::memory_order_relaxed); }
    std::size_t capacity() const noexcept { return count_; }

    bool read(std::size_t index, ProfileMeasurement& out) const noexcept
    {
        if (index >= count_) return false;
        auto& counter = counters_[index];
        lock(counter);
        out = counter.measurement;
        counter.locked.clear(std::memory_order_release);
        return out.calls != 0;
    }

    static ProfileToken begin(void* context, ProfileEvent event, std::uint32_t nodeIndex) noexcept
    {
        auto recorder = static_cast<ProfileRecorder*>(context);
        constexpr auto events = static_cast<std::size_t>(ProfileEvent::Count);
        if (static_cast<std::size_t>(event) >= events ||
            (event == ProfileEvent::Node &&
             (recorder->count_ < events || nodeIndex >= recorder->count_ - events)))
        {
            recorder->dropped_.fetch_add(1, std::memory_order_relaxed);
            return 0;
        }
        auto index = event == ProfileEvent::Node ? events + std::size_t{nodeIndex} :
            static_cast<std::size_t>(event);
        if (index >= recorder->count_ || threadState_.depth == threadState_.stack.size())
        {
            recorder->dropped_.fetch_add(1, std::memory_order_relaxed);
            return 0;
        }
        auto& entry = threadState_.stack[threadState_.depth++];
        entry = {recorder, event, nodeIndex, index, recorder->clock_(recorder->clockContext_), 0};
        return threadState_.depth;
    }

    static void end(void* context, ProfileToken token) noexcept
    {
        auto recorder = static_cast<ProfileRecorder*>(context);
        if (token == 0 || token != threadState_.depth ||
            threadState_.stack[threadState_.depth - 1].recorder != recorder)
        {
            recorder->dropped_.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        auto entry = threadState_.stack[--threadState_.depth];
        auto now = recorder->clock_(recorder->clockContext_);
        auto inclusive = now >= entry.start ? now - entry.start : 0;
        auto exclusive = inclusive >= entry.children ? inclusive - entry.children : 0;
        if (threadState_.depth != 0) threadState_.stack[threadState_.depth - 1].children += inclusive;
        auto& counter = recorder->counters_[entry.index];
        lock(counter);
        auto& measurement = counter.measurement;
        measurement.event = entry.event;
        measurement.nodeIndex = entry.event == ProfileEvent::Node ? entry.nodeIndex : noProfileNode;
        if (measurement.calls == 0 || inclusive < measurement.minNs) measurement.minNs = inclusive;
        if (inclusive > measurement.maxNs) measurement.maxNs = inclusive;
        ++measurement.calls;
        measurement.inclusiveNs += inclusive;
        measurement.exclusiveNs += exclusive;
        counter.locked.clear(std::memory_order_release);
    }

private:
    struct Entry final {
        ProfileRecorder* recorder = nullptr;
        ProfileEvent event = ProfileEvent::Evaluate;
        std::uint32_t nodeIndex = noProfileNode;
        std::size_t index = 0;
        std::uint64_t start = 0;
        std::uint64_t children = 0;
    };
    struct ThreadState final {
        std::array<Entry, 64> stack{};
        std::size_t depth = 0;
    };
    static thread_local ThreadState threadState_;
    static std::uint64_t steadyClock(void*) noexcept
    {
        return static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count());
    }
    static void lock(ProfileCounter& counter) noexcept
    {
        while (counter.locked.test_and_set(std::memory_order_acquire)) {}
    }
    ProfileCounter* counters_ = nullptr;
    std::size_t count_ = 0;
    ProfileClock clock_ = &steadyClock;
    void* clockContext_ = nullptr;
    std::atomic<std::uint64_t> dropped_{0};
};

inline thread_local ProfileRecorder::ThreadState ProfileRecorder::threadState_{};

}
