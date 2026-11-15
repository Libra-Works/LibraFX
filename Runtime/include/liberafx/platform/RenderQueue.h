#pragma once

#include <atomic>
#include <cmath>
#include <cstddef>
#include <cstdint>

namespace liberafx {

enum class RenderResult : std::uint8_t { Ok, InvalidArgument, Busy, NotReady, CapacityExceeded, Rejected };
enum class Primitive : std::uint8_t { Mesh, Triangles, Lines };
enum class BlendMode : std::uint8_t { Opaque, Alpha, Additive, Premultiplied };
enum class ResourceKind : std::uint8_t { Mesh, Texture, Material };

struct ResourceId {
    std::uint64_t value = 0;
};

struct RenderVertex {
    float position[3]{};
    float uv[2]{};
    float color[4]{1, 1, 1, 1};
};

struct DrawCommand {
    Primitive primitive = Primitive::Mesh;
    BlendMode blend = BlendMode::Alpha;
    ResourceId geometry;
    ResourceId material;
    ResourceId texture;
    const RenderVertex* vertices = nullptr;
    std::size_t vertexCount = 0;
    const std::uint32_t* indices = nullptr;
    std::size_t indexCount = 0;
    float transform[12]{1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0};
    float tint[4]{1, 1, 1, 1};
    std::uint64_t sortKey = 0;
    bool depthTest = true;
    bool depthWrite = false;
};

struct RenderBatch {
    const DrawCommand* commands;
    std::size_t count;
};

// Host callbacks own all engine objects. No engine or GPU API is used on the VFX worker.
using ResolveResource = bool (*)(void* context, const std::uint8_t assetId[16], ResourceKind kind,
                                ResourceId& out) noexcept;
using SubmitRenderBatch = bool (*)(void* context, RenderBatch batch) noexcept;

class RenderQueue final {
public:
    RenderResult initialize(DrawCommand* storage, std::size_t capacity) noexcept
    {
        if (phase_.load(std::memory_order_acquire) != Phase::Empty) return RenderResult::Busy;
        if (capacity > 0 && storage == nullptr) return RenderResult::InvalidArgument;
        storage_ = storage;
        capacity_ = capacity;
        count_ = 0;
        initialized_ = true;
        return RenderResult::Ok;
    }

    // One writer fills caller-owned command/vertex storage, then publishes it with seal().
    RenderResult begin() noexcept
    {
        if (!initialized_) return RenderResult::NotReady;
        auto expected = Phase::Empty;
        if (!phase_.compare_exchange_strong(expected, Phase::Writing, std::memory_order_acq_rel))
            return RenderResult::Busy;
        count_ = 0;
        return RenderResult::Ok;
    }

    RenderResult append(const DrawCommand& command) noexcept
    {
        if (phase_.load(std::memory_order_relaxed) != Phase::Writing) return RenderResult::NotReady;
        if (!valid(command)) return RenderResult::InvalidArgument;
        if (count_ == capacity_) return RenderResult::CapacityExceeded;
        storage_[count_++] = command;
        return RenderResult::Ok;
    }

    RenderResult seal() noexcept
    {
        auto expected = Phase::Writing;
        return phase_.compare_exchange_strong(expected, Phase::Ready, std::memory_order_release)
            ? RenderResult::Ok : RenderResult::NotReady;
    }

    // Invoke on the host's render thread. Borrowed buffers must be consumed/copied before the callback returns.
    // A rejection retains the batch for retry; no buffers may be reused until a submission succeeds.
    RenderResult submit(SubmitRenderBatch submitBatch, void* context) noexcept
    {
        if (submitBatch == nullptr) return RenderResult::InvalidArgument;
        auto expected = Phase::Ready;
        if (!phase_.compare_exchange_strong(expected, Phase::Submitting, std::memory_order_acquire))
            return RenderResult::NotReady;
        auto accepted = submitBatch(context, {storage_, count_});
        phase_.store(accepted ? Phase::Empty : Phase::Ready, std::memory_order_release);
        return accepted ? RenderResult::Ok : RenderResult::Rejected;
    }

    std::size_t capacity() const noexcept { return capacity_; }
    std::size_t capacityGrowthCount() const noexcept { return 0; }

private:
    enum class Phase : std::uint8_t { Empty, Writing, Ready, Submitting };
    static auto valid(const DrawCommand& command) noexcept -> bool
    {
        if (command.primitive > Primitive::Lines || command.blend > BlendMode::Premultiplied ||
            command.material.value == 0 ||
            (command.primitive == Primitive::Mesh && command.geometry.value == 0) ||
            (command.primitive != Primitive::Mesh && command.vertexCount == 0) ||
            (command.vertexCount > 0 && command.vertices == nullptr) ||
            (command.indexCount > 0 && command.indices == nullptr) ||
            command.vertexCount > 1'000'000 || command.indexCount > 3'000'000)
            return false;
        for (auto value : command.transform)
            if (!std::isfinite(value)) return false;
        for (auto value : command.tint)
            if (!std::isfinite(value) || value < 0) return false;
        if (command.tint[3] > 1) return false;
        if (command.vertexCount > 0)
            for (auto index = std::size_t{0}; index < command.indexCount; ++index)
                if (command.indices[index] >= command.vertexCount) return false;
        return true;
    }

    DrawCommand* storage_ = nullptr;
    std::size_t capacity_ = 0;
    std::size_t count_ = 0;
    bool initialized_ = false;
    std::atomic<Phase> phase_{Phase::Empty};
};

}
