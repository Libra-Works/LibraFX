#include "LbInteropInternal.h"

using namespace liberafx::interop_detail;

LibraResult LIBRA_INTEROP_CALL libra_sample_sprite_atlas(double frame, uint32_t columns, uint32_t rows,
                                                         LibraAtlasFrame *outFrame) {
  if (!outFrame)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outFrame = {};
  auto atlas = liberafx::AtlasFrame{};
  if (!liberafx::sampleSpriteAtlas(frame, columns, rows, atlas))
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outFrame = {atlas.frameIndex, atlas.uvRect[0], atlas.uvRect[1], atlas.uvRect[2], atlas.uvRect[3]};
  return LIBRA_OK;
}

LibraResult LIBRA_INTEROP_CALL libra_sample_sprite_rect(uint32_t frameIndex, float u, float v, float width,
                                                        float height, LibraAtlasFrame *outFrame) {
  if (!outFrame)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outFrame = {};
  auto atlas = liberafx::AtlasFrame{};
  if (!liberafx::sampleSpriteRect(frameIndex, u, v, width, height, atlas))
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outFrame = {atlas.frameIndex, atlas.uvRect[0], atlas.uvRect[1], atlas.uvRect[2], atlas.uvRect[3]};
  return LIBRA_OK;
}

LibraResult LIBRA_INTEROP_CALL libra_sample_sprite_sequence(double frameInput, double elapsedSeconds, double fps,
                                                            const double *durationFrames, uint32_t frameCount,
                                                            uint32_t startFrame, uint32_t endFrame, uint32_t loop,
                                                            uint32_t playing, uint32_t *outFrameIndex) {
  if (!outFrameIndex || loop > 1 || playing > 1)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outFrameIndex = 0;
  auto frame = std::uint32_t{};
  if (!liberafx::sampleSpriteSequence(frameInput, elapsedSeconds, fps, durationFrames, frameCount, startFrame, endFrame,
                                      loop != 0, playing != 0, frame))
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outFrameIndex = frame;
  return LIBRA_OK;
}

LibraResult LIBRA_INTEROP_CALL libra_sample_sprite_composition(double elapsedSeconds, double fps, double durationFrames,
                                                               uint32_t loop, const double *cellDurations,
                                                               uint32_t frameCount, const double *keys,
                                                               uint32_t keyCount, uint32_t *outFrameIndex,
                                                               double outPose[5]) {
  if (!outFrameIndex || !outPose || loop > 1 || keyCount > 256 || (keyCount && !keys))
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  auto frame = std::uint32_t{};
  auto pose = liberafx::SpritePose{};
  if (!liberafx::sampleSpriteComposition(elapsedSeconds, fps, durationFrames, loop != 0, cellDurations, frameCount,
                                         keys, keyCount, frame, pose, [](const double *input, std::uint32_t index) {
                                           const auto *key = input + index * 6;
                                           return liberafx::SpritePoseKey{key[0],
                                                                          {key[1], key[2], key[3], key[4], key[5]}};
                                         }))
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  *outFrameIndex = frame;
  outPose[0] = pose.x;
  outPose[1] = pose.y;
  outPose[2] = pose.rotationDegrees;
  outPose[3] = pose.scaleX;
  outPose[4] = pose.scaleY;
  return LIBRA_OK;
}

LibraResult LIBRA_INTEROP_CALL libra_make_sprite_quad(const LibraAtlasFrame *frame, float width, float height,
                                                      LibraSpriteVertex outVertices[4]) {
  if (!frame || !outVertices)
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  auto atlas = liberafx::AtlasFrame{frame->frameIndex, {frame->u, frame->v, frame->width, frame->height}};
  liberafx::RenderVertex vertices[4]{};
  if (!liberafx::makeSpriteQuad(atlas, width, height, vertices))
  {
    return LIBRA_INVALID_ARGUMENT;
  }
  for (auto i = 0u; i < 4; ++i)
  {
    const auto &vertex = vertices[i];
    outVertices[i] = {vertex.position[0], vertex.position[1], vertex.position[2], vertex.uv[0],   vertex.uv[1],
                      vertex.color[0],    vertex.color[1],    vertex.color[2],    vertex.color[3]};
  }
  return LIBRA_OK;
}
