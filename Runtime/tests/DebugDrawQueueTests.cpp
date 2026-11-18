#include "liberafx/platform/DebugDrawQueue.h"
#include <cstdlib>
#include <cstring>

namespace {
void require(bool value) { if (!value) std::abort(); }
struct Adapter final {
    bool reject = false;
    std::size_t displayed = 0, submissions = 0;
    liberafx::DebugDrawCommand owned[2]{};
    static auto submit(void* context, liberafx::DebugDrawBatch batch) noexcept -> bool
    {
        auto& self = *static_cast<Adapter*>(context);
        if (self.reject || batch.count > 2) return false;
        for (auto index = std::size_t{0}; index < batch.count; ++index) self.owned[index] = batch.commands[index];
        self.displayed = batch.count; // Whole frame replaces previous per-instance observations.
        ++self.submissions;
        return true;
    }
    void disconnect() { displayed = 0; }
};
}
int main()
{
    using liberafx::RenderResult;
    auto storage = new liberafx::DebugDrawCommand[2]{};
    auto queue = liberafx::DebugDrawQueue{};
    auto adapter = Adapter{};
    require(queue.initialize(storage, 2) == RenderResult::Ok);
    require(queue.begin() == RenderResult::Ok);
    auto command = liberafx::DebugDrawCommand{};
    command.nodeId[0] = 65;
    std::strcpy(command.text, "world-space observation");
    command.world[3] = 17;
    require(queue.append(command) == RenderResult::Ok);
    require(queue.append(command) == RenderResult::InvalidArgument);
    command.instanceId = 2; // Same authored node, distinct live instance.
    command.kind = liberafx::DebugDrawKind::Model;
    std::strcpy(command.asset, "opaque-model-binding");
    require(queue.append(command) == RenderResult::Ok);
    command.instanceId = 3;
    require(queue.append(command) == RenderResult::CapacityExceeded);
    require(queue.seal() == RenderResult::Ok);
    adapter.reject = true;
    require(queue.submit(Adapter::submit, &adapter) == RenderResult::Rejected && adapter.displayed == 0);
    require(queue.begin() == RenderResult::Busy);
    adapter.reject = false;
    require(queue.submit(Adapter::submit, &adapter) == RenderResult::Ok && adapter.displayed == 2);
    storage[0].text[0] = 'X';
    require(adapter.owned[0].text[0] == 'w' && adapter.owned[0].world[3] == 17);
    require(queue.begin() == RenderResult::Ok && queue.seal() == RenderResult::Ok);
    require(queue.submit(Adapter::submit, &adapter) == RenderResult::Ok && adapter.displayed == 0);
    command.world[15] = 0;
    require(!liberafx::DebugDrawQueue::valid(command)); // No projective-world fallback.
    queue.reset(); // Stop/retransfer may discard a pending batch only after joining workers.
    adapter.disconnect();
    require(adapter.displayed == 0);
    delete[] storage;
}
