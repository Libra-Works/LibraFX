#include "liberafx/SceneBindings.h"
#include "liberafx/platform/RenderBuffers.h"
#include <array>
#include <cstdlib>
#include <cstdio>
#include <limits>

namespace {
void require(bool value, const char* expression, int line)
{
    if (!value)
    {
        std::fprintf(stderr, "Scene binding check failed at line %d: %s\n", line, expression);
        std::exit(1);
    }
}
#define require(condition) require((condition), #condition, __LINE__)
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
    auto rectangleFrame = liberafx::AtlasFrame{};
    require(liberafx::sampleSpriteRect(12, 0.125f, 0.25f, 0.5f, 0.25f, rectangleFrame) &&
        rectangleFrame.frameIndex == 12 && rectangleFrame.uvRect[0] == 0.125f && rectangleFrame.uvRect[3] == 0.25f);
    require(!liberafx::sampleSpriteRect(0, 0.9f, 0, 0.2f, 1, rectangleFrame));
    auto durations = std::array{1.0, 2.0, 1.0};
    auto selected = std::uint32_t{};
    require(liberafx::sampleSpriteSequence(-1, 0.25, 4, durations.data(), 3, 1, 2,
        false, true, selected) && selected == 1);
    require(liberafx::sampleSpriteSequence(0, 5, 4, durations.data(), 3, 1, 2,
        true, false, selected) && selected == 2);
    require(liberafx::sampleSpriteSequence(0, 5, 4, durations.data(), 3, 1, 2,
        false, false, selected) && selected == 1);
    require(liberafx::sampleSpriteSequence(2, 0, 4, durations.data(), 3, 1, 2,
        true, false, selected) && selected == 2);
    require(!liberafx::sampleSpriteSequence(-1, 0, 4, durations.data(), 3, 2, 1,
        true, true, selected));
    auto shortPart = std::array{0.5, 1.0};
    auto longPart = std::array{4.0};
    auto keys = std::array{liberafx::SpritePoseKey{0.5, {2, 4, 10, 1, 2}},
        liberafx::SpritePoseKey{2.5, {10, -4, 90, 3, 0.5}}};
    auto pose = liberafx::SpritePose{};
    require(liberafx::sampleSpriteComposition(0.75, 2, 4, true, shortPart.data(), 2,
        nullptr, 0, selected, pose) && selected == 1 && pose.scaleX == 1);
    require(liberafx::sampleSpriteComposition(0.75, 2, 4, true, longPart.data(), 1,
        keys.data(), 2, selected, pose) && selected == 0 && pose.x == 6 && pose.y == 0 &&
        pose.rotationDegrees == 50 && pose.scaleX == 2 && pose.scaleY == 1.25);
    require(liberafx::sampleSpriteComposition(1.75, 2, 4, true, shortPart.data(), 2,
        nullptr, 0, selected, pose) && selected == 1);
    require(liberafx::sampleSpriteComposition(2, 2, 4, true, shortPart.data(), 2,
        nullptr, 0, selected, pose) && selected == 0);
    require(liberafx::sampleSpriteComposition(2, 2, 4, false, longPart.data(), 1,
        keys.data(), 2, selected, pose) && pose.x == 10 && pose.rotationDegrees == 90);
    require(!liberafx::sampleSpriteComposition(0, 2, 1, true, shortPart.data(), 2,
        nullptr, 0, selected, pose));
    keys[1].frame = keys[0].frame;
    require(!liberafx::sampleSpriteComposition(0, 2, 4, true, longPart.data(), 1,
        keys.data(), 2, selected, pose));
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
