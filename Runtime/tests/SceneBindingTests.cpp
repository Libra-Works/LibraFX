#include "liberafx/SceneBindings.h"
#include "liberafx/platform/RenderBuffers.h"
#include <array>
#include <cstdlib>
#include <limits>

namespace {
void require(bool value) { if (!value) std::abort(); }
struct Backend final {
    std::uint64_t next = 0;
    std::size_t creations = 0;
    std::size_t destroyed = 0;
    bool reject = false;
};
auto create(void* context, liberafx::BufferDescription description, liberafx::BufferHandle& out) noexcept -> bool
{
    auto backend = static_cast<Backend*>(context);
    require(description.bytes >= description.stride);
    ++backend->creations;
    out.value = ++backend->next;
    return true;
}
auto upload(void* context, liberafx::BufferHandle, const void*, std::size_t) noexcept -> bool
{
    return !static_cast<Backend*>(context)->reject;
}
void destroy(void* context, liberafx::BufferHandle) noexcept
{
    ++static_cast<Backend*>(context)->destroyed;
}
}

auto main() -> int
{
    auto frame = liberafx::AtlasFrame{};
    require(liberafx::sampleSpriteAtlas(5.9, 4, 2, frame) && frame.frameIndex == 5 &&
        frame.uvRect[0] == 0.25f && frame.uvRect[1] == 0.5f);
    require(liberafx::sampleSpriteAtlas(-1, 4, 2, frame) && frame.frameIndex == 7);
    require(liberafx::sampleSpriteAtlas(8, 4, 2, frame) && frame.frameIndex == 0);
    require(!liberafx::sampleSpriteAtlas(0, 0, 2, frame) &&
        !liberafx::sampleSpriteAtlas(std::numeric_limits<double>::infinity(), 4, 2, frame));
    auto vertices = std::array<liberafx::RenderVertex, 4>{};
    liberafx::RenderVertex quad[4]{};
    require(liberafx::makeSpriteQuad(frame, 2, 4, quad) &&
        quad[0].position[0] == -1 && quad[0].position[1] == 2 &&
        quad[2].uv[0] == 0.25f && quad[2].uv[1] == 0.5f);
    auto backend = Backend{};
    auto buffer = liberafx::DynamicRenderBuffer{};
    require(buffer.initialize({&backend, create, upload, destroy}, liberafx::BufferKind::Vertex,
        sizeof(liberafx::RenderVertex)));
    require(buffer.upload(vertices.data(), sizeof(vertices)) && backend.creations == 1);
    require(buffer.upload(vertices.data(), sizeof(vertices)) && backend.creations == 1);
    auto original = buffer.handle().value;
    auto larger = std::array<liberafx::RenderVertex, 8>{};
    backend.reject = true;
    require(!buffer.upload(larger.data(), sizeof(larger)) &&
        buffer.handle().value == original && backend.destroyed == 1);
    backend.reject = false;
    require(buffer.upload(larger.data(), sizeof(larger)) &&
        buffer.handle().value != original && backend.destroyed == 2);
    buffer.reset();
    require(backend.destroyed == 3);
    return 0;
}
