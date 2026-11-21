#include "liberafx/LbEffectWorld.h"
#include <algorithm>
#include <atomic>
#include <cmath>
#include <condition_variable>
#include <functional>
#include <mutex>
#include <thread>
#include <vector>

namespace liberafx
{
struct EffectWorld::Impl final
{
    EffectWorldOptions options;
    std::thread::id owner = std::this_thread::get_id();
    std::vector<std::thread> threads;
    std::mutex mutex;
    std::condition_variable wake, done;
    bool stopping = false, active = false;
    std::size_t epoch = 0, pending = 0, count = 0, rotation = 0, lastEffects = 0, lastExecutingLanes = 0;
    std::atomic<std::size_t> cursor{0};
    EffectFrame *frames = nullptr;
    struct Scheduled
    {
        std::size_t index = 0, work = 0, rank = 0;
    };
    std::vector<Scheduled> order;
    std::vector<SpawnableHost *> unique;
    ~Impl()
    {
        {
            auto lock = std::lock_guard<std::mutex>(mutex);
            stopping = true;
        }
        wake.notify_all();
        for (auto &thread : threads)
        {
            thread.join();
        }
    }
    void consume() noexcept
    {
        for (;;)
        {
            const auto task = cursor.fetch_add(1, std::memory_order_relaxed);
            if (task >= count)
            {
                return;
            }
            auto &frame = frames[order[task].index];
            try
            {
                frame.result = frame.host->advance(frame.time, frame.delta);
            }
            catch (...)
            {
                frame.result = Result::NotReady;
            }
        }
    }
    static void run(void *context) noexcept
    {
        auto &self = *static_cast<Impl *>(context);
        self.consume();
        {
            auto lock = std::lock_guard<std::mutex>(self.mutex);
            --self.pending;
            self.done.notify_one();
        }
    }
    void worker() noexcept
    {
        auto observed = std::size_t{};
        auto lock = std::unique_lock<std::mutex>(mutex);
        for (;;)
        {
            wake.wait(lock, [&] { return stopping || epoch != observed; });
            if (stopping)
            {
                return;
            }
            observed = epoch;
            lock.unlock();
            run(this);
            lock.lock();
        }
    }
};
EffectWorld::EffectWorld(std::unique_ptr<Impl> impl) noexcept : impl_(std::move(impl))
{
}
EffectWorld::~EffectWorld() = default;
Result EffectWorld::create(const EffectWorldOptions &options, std::unique_ptr<EffectWorld> &out) noexcept
{
    out.reset();
    if (!options.maximumEffects || options.maximumEffects > 65536 || !options.workers || options.workers > 16)
    {
        return Result::InvalidArgument;
    }
    try
    {
        auto impl = std::make_unique<Impl>();
        impl->options = options;
        impl->order.resize(options.maximumEffects);
        impl->unique.resize(options.maximumEffects);
        impl->options.workers = std::min({options.workers, options.maximumEffects,
                                          static_cast<std::size_t>(std::max(1u, std::thread::hardware_concurrency()))});
        if (!options.scheduler.enqueue)
        {
            impl->threads.reserve(impl->options.workers - 1);
            for (auto index = std::size_t{1}; index < impl->options.workers; ++index)
            {
                impl->threads.emplace_back([context = impl.get()] { context->worker(); });
            }
        }
        out.reset(new EffectWorld(std::move(impl)));
        return Result::Ok;
    }
    catch (...)
    {
        return Result::InsufficientWork;
    }
}
Result EffectWorld::advance(EffectFrame *frames, std::size_t count) noexcept
{
    auto &impl = *impl_;
    if (impl.owner != std::this_thread::get_id() || impl.active)
    {
        return Result::Busy;
    }
    if ((!frames && count) || count > impl.options.maximumEffects)
    {
        return Result::InvalidArgument;
    }
    for (auto i = std::size_t{}; i < count; ++i)
    {
        if (!frames[i].host || !std::isfinite(frames[i].time) || !std::isfinite(frames[i].delta) ||
            frames[i].time < 0 || frames[i].delta < 0)
        {
            return Result::InvalidArgument;
        }
        if (!frames[i].host->supportsWorkerExecution())
        {
            return Result::UnsupportedBinary;
        }
        impl.unique[i] = frames[i].host;
    }
    std::sort(impl.unique.begin(), impl.unique.begin() + count, std::less<SpawnableHost *>{});
    for (auto i = std::size_t{1}; i < count; ++i)
    {
        if (impl.unique[i] == impl.unique[i - 1])
        {
            return Result::InvalidArgument;
        }
    }
    impl.lastEffects = count;
    impl.lastExecutingLanes = 0;
    if (!count)
    {
        return Result::Ok;
    }
    impl.active = true;
    impl.frames = frames;
    impl.count = count;
    impl.cursor.store(0, std::memory_order_relaxed);
    impl.rotation %= count;
    // Let small effects finish before large jobs occupy lanes. Equal-cost jobs
    // rotate each frame. This is host-level fairness, not mid-host preemption.
    auto totalWork = std::size_t{};
    for (auto i = std::size_t{}; i < count; ++i)
    {
        impl.order[i] = {i, frames[i].host->simulationWorkEstimate(), (i + count - impl.rotation) % count};
        totalWork += impl.order[i].work;
    }
    const auto parallel = count > 1 && impl.options.workers > 1 && totalWork >= impl.options.minimumParallelWork;
    impl.lastExecutingLanes = parallel ? impl.options.workers : 1;
    if (parallel)
    {
        std::sort(impl.order.begin(), impl.order.begin() + count, [](const auto &left, const auto &right)
                  { return left.work != right.work ? left.work < right.work : left.rank < right.rank; });
    }
    // Small frames stay inline without waking all the shared workers.
    if (!parallel)
    {
        impl.consume();
    }
    else
    {
        {
            auto lock = std::lock_guard<std::mutex>(impl.mutex);
            impl.pending = impl.options.workers - 1;
            ++impl.epoch;
        }
        if (impl.options.scheduler.enqueue)
        {
            for (auto group = std::size_t{1}; group < impl.options.workers; ++group)
            {
                if (!impl.options.scheduler.enqueue(impl.options.scheduler.context, Impl::run, &impl))
                {
                    Impl::run(&impl);
                }
            }
        }
        else
        {
            impl.wake.notify_all();
        }
        impl.consume();
        auto lock = std::unique_lock<std::mutex>(impl.mutex);
        impl.done.wait(lock, [&] { return impl.pending == 0; });
    }
    ++impl.rotation;
    impl.frames = nullptr;
    impl.count = 0;
    impl.active = false;
    return Result::Ok;
}
EffectWorldStats EffectWorld::stats() const noexcept
{
    return {impl_->options.maximumEffects, impl_->options.workers, impl_->lastEffects, impl_->lastExecutingLanes};
}
} // namespace liberafx
