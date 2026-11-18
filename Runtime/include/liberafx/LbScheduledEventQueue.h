#pragma once

#include "liberafx/LbVfxRuntime.h"
#include <cmath>
#include <cstddef>
#include <cstdint>

namespace liberafx {

struct ScheduledEvent final {
    double time = 0;
    std::uint64_t token = 0;
};

// Owner-thread queue using caller-owned storage. Equal timestamps remain FIFO.
class ScheduledEventQueue final {
public:
    ScheduledEventQueue(ScheduledEvent* storage, std::size_t capacity) noexcept
        : storage_(storage), capacity_(storage ? capacity : 0) {}
    ScheduledEventQueue(const ScheduledEventQueue&) = delete;
    ScheduledEventQueue& operator=(const ScheduledEventQueue&) = delete;

    auto schedule(double time, std::uint64_t token) noexcept -> Result
    {
        if (!std::isfinite(time) || time < 0 || !token) return Result::InvalidArgument;
        if (count_ == capacity_) return Result::QueueRejected;
        auto index = count_;
        while (index && storage_[index - 1].time > time) {
            storage_[index] = storage_[index - 1];
            --index;
        }
        storage_[index] = {time, token};
        ++count_;
        return Result::Ok;
    }

    auto peek(ScheduledEvent& out) const noexcept -> bool
    {
        if (!count_) return false;
        out = storage_[0];
        return true;
    }

    auto pop(ScheduledEvent& out) noexcept -> bool
    {
        if (!peek(out)) return false;
        for (auto index = std::size_t{1}; index < count_; ++index) storage_[index - 1] = storage_[index];
        --count_;
        return true;
    }

    auto size() const noexcept -> std::size_t { return count_; }
    // Cancel all matching opaque continuations; preserve other equal-time FIFO.
    void cancel(std::uint64_t token) noexcept
    {
        auto kept = std::size_t{};
        for (auto index = std::size_t{}; index < count_; ++index) {
            if (storage_[index].token == token) continue;
            storage_[kept++] = storage_[index];
        }
        count_ = kept;
    }
    void clear() noexcept { count_ = 0; }

private:
    ScheduledEvent* storage_;
    std::size_t capacity_;
    std::size_t count_ = 0;
};

}
