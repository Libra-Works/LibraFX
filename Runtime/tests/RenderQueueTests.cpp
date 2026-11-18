#include "liberafx/platform/LbRenderQueue.h"

#include <cstdlib>
#include <array>
#include <iostream>
#include <limits>
#include <thread>

namespace {
void require(bool condition)
{
    if (!condition) std::abort();
}

struct Renderer {
    bool accept = false;
    std::size_t received = 0;
};

auto submit(void* context, liberafx::RenderBatch batch) noexcept -> bool
{
    auto renderer = static_cast<Renderer*>(context);
    require(batch.count == 2 && batch.commands != nullptr);
    require(batch.commands[0].material.value == 5 && batch.commands[1].sortKey == 42);
    renderer->received += batch.count;
    return renderer->accept;
}
}

int main()
{
    using liberafx::RenderResult;
    auto queue = liberafx::RenderQueue{};
    auto storage = std::array<liberafx::DrawCommand, 2>{};
    require(queue.begin() == RenderResult::NotReady);
    require(queue.initialize(nullptr, 2) == RenderResult::InvalidArgument);
    require(queue.initialize(storage.data(), storage.size()) == RenderResult::Ok);
    auto worker = std::thread([&] {
        require(queue.begin() == RenderResult::Ok);
        auto command = liberafx::DrawCommand{};
        require(queue.append(command) == RenderResult::InvalidArgument);
        command.material.value = 5;
        command.geometry.value = 7;
        command.tint[0] = 8;
        require(queue.append(command) == RenderResult::Ok);
        command.sortKey = 42;
        require(queue.append(command) == RenderResult::Ok);
        require(queue.append(command) == RenderResult::CapacityExceeded);
        require(queue.seal() == RenderResult::Ok);
    });
    worker.join();
    require(queue.begin() == RenderResult::Busy);
    require(queue.initialize(storage.data(), storage.size()) == RenderResult::Busy);
    auto renderer = Renderer{};
    require(queue.submit(&submit, &renderer) == RenderResult::Rejected);
    require(queue.begin() == RenderResult::Busy);
    renderer.accept = true;
    require(queue.submit(&submit, &renderer) == RenderResult::Ok);
    require(renderer.received == 4 && queue.capacity() == 2 && queue.capacityGrowthCount() == 0);
    require(queue.submit(&submit, &renderer) == RenderResult::NotReady);
    require(queue.begin() == RenderResult::Ok);
    auto invalid = liberafx::DrawCommand{};
    invalid.geometry.value = 1;
    invalid.material.value = 1;
    invalid.transform[0] = std::numeric_limits<float>::infinity();
    require(queue.append(invalid) == RenderResult::InvalidArgument);
    auto vertices = std::array<liberafx::RenderVertex, 2>{};
    auto indices = std::array<std::uint32_t, 2>{0, 2};
    auto lines = liberafx::DrawCommand{};
    lines.primitive = liberafx::Primitive::Lines;
    lines.material.value = 1;
    lines.vertices = vertices.data();
    lines.vertexCount = 2;
    lines.indices = indices.data();
    lines.indexCount = 2;
    require(queue.append(lines) == RenderResult::InvalidArgument);
    std::cout << "Portable render queue checks passed.\n";
}
