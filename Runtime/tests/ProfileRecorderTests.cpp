#include "liberafx/platform/LbProfileRecorder.h"
#include "liberafx/LbVfxRuntime.h"
#include <array>
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <thread>

namespace {
auto clock(void* context) noexcept -> std::uint64_t
{
    return *static_cast<std::uint64_t*>(context);
}
}

int main()
{
    auto counters = std::array<liberafx::ProfileCounter, 16>{};
    auto recorder = liberafx::ProfileRecorder{};
    auto time = std::uint64_t{10};
    recorder.initialize(counters.data(), counters.size(), &clock, &time);
    auto hooks = recorder.hooks(true, true);
    {
        auto outer = liberafx::ProfileScope(hooks, liberafx::ProfileEvent::Evaluate);
        time = 20;
        {
            auto inner = liberafx::ProfileScope(hooks, liberafx::ProfileEvent::Node, 0);
            time = 35;
        }
        time = 50;
    }
    auto value = liberafx::ProfileMeasurement{};
    assert(recorder.read(static_cast<size_t>(liberafx::ProfileEvent::Evaluate), value));
    assert(value.calls == 1 && value.inclusiveNs == 40 && value.exclusiveNs == 25 &&
        value.minNs == 40 && value.maxNs == 40);
    assert(recorder.read(static_cast<size_t>(liberafx::ProfileEvent::Count), value));
    assert(value.nodeIndex == 0 && value.inclusiveNs == 15 && value.exclusiveNs == 15);
    assert(liberafx::ProfileRecorder::begin(&recorder, liberafx::ProfileEvent::Node, 100) == 0);
    assert(recorder.dropped() == 1);
    auto instance = liberafx::Instance{};
    assert(instance.setProfiling({nullptr, hooks.begin, nullptr}) == liberafx::Result::InvalidArgument);
    assert(instance.setProfiling(hooks) == liberafx::Result::Ok);
    recorder.initialize(counters.data(), counters.size());
    auto worker = std::thread([&]
    {
        for (auto index = 0; index < 1000; ++index)
        {
            auto scope = liberafx::ProfileScope(hooks, liberafx::ProfileEvent::Evaluate);
        }
    });
    for (auto index = 0; index < 1000; ++index)
    {
        auto scope = liberafx::ProfileScope(hooks, liberafx::ProfileEvent::Evaluate);
        recorder.read(static_cast<size_t>(liberafx::ProfileEvent::Evaluate), value);
        assert(value.exclusiveNs <= value.inclusiveNs);
    }
    worker.join();
    assert(recorder.read(static_cast<size_t>(liberafx::ProfileEvent::Evaluate), value));
    assert(value.calls == 2000 && value.exclusiveNs <= value.inclusiveNs);
}
