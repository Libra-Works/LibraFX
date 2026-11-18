#pragma once

#include <cstddef>
#include <cstdint>
#include <limits>

namespace liberafx {

enum class BufferKind : std::uint8_t { Vertex, Index, Instance };
struct BufferHandle final { std::uint64_t value = 0; };
struct BufferDescription final {
    std::size_t bytes = 0;
    std::size_t stride = 0;
    BufferKind kind = BufferKind::Vertex;
};

// Called only on the adapter's permitted graphics/owner thread. Upload copies or
// stages all bytes before returning; it must not retain the borrowed data pointer.
struct BufferBackend final {
    void* context = nullptr;
    bool (*create)(void*, BufferDescription, BufferHandle&) noexcept = nullptr;
    bool (*upload)(void*, BufferHandle, const void*, std::size_t) noexcept = nullptr;
    void (*destroy)(void*, BufferHandle) noexcept = nullptr;
};

class DynamicRenderBuffer final {
public:
    DynamicRenderBuffer() = default;
    DynamicRenderBuffer(const DynamicRenderBuffer&) = delete;
    auto operator=(const DynamicRenderBuffer&) -> DynamicRenderBuffer& = delete;
    ~DynamicRenderBuffer() { reset(); }

    auto initialize(BufferBackend backend, BufferKind kind, std::size_t stride) noexcept -> bool
    {
        if (handle_.value != 0 || backend.create == nullptr || backend.upload == nullptr ||
            backend.destroy == nullptr || stride == 0 || kind > BufferKind::Instance) return false;
        backend_ = backend;
        kind_ = kind;
        stride_ = stride;
        return true;
    }

    auto upload(const void* data, std::size_t bytes) noexcept -> bool
    {
        if (backend_.create == nullptr || data == nullptr || bytes == 0 || bytes % stride_ != 0 ||
            bytes > std::numeric_limits<std::size_t>::max() / 2) return false;
        if (bytes <= capacity_) return backend_.upload(backend_.context, handle_, data, bytes);
        auto capacity = capacity_ == 0 ? bytes : capacity_ * 2;
        if (capacity < bytes) capacity = bytes;
        auto candidate = BufferHandle{};
        if (!backend_.create(backend_.context, {capacity, stride_, kind_}, candidate) || candidate.value == 0) return false;
        if (!backend_.upload(backend_.context, candidate, data, bytes))
        {
            backend_.destroy(backend_.context, candidate);
            return false;
        }
        if (handle_.value != 0) backend_.destroy(backend_.context, handle_);
        handle_ = candidate;
        capacity_ = capacity;
        return true;
    }

    void reset() noexcept
    {
        if (handle_.value != 0) backend_.destroy(backend_.context, handle_);
        handle_ = {};
        capacity_ = 0;
    }
    auto handle() const noexcept -> BufferHandle { return handle_; }
    auto capacity() const noexcept -> std::size_t { return capacity_; }

private:
    BufferBackend backend_;
    BufferHandle handle_;
    BufferKind kind_ = BufferKind::Vertex;
    std::size_t stride_ = 0;
    std::size_t capacity_ = 0;
};

}
