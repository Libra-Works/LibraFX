#pragma once
#include "liberafx/LbProceduralModel.h"
#include <godot_cpp/classes/array_mesh.hpp>
#include <godot_cpp/variant/packed_byte_array.hpp>

namespace liberafx::godot_adapter {
class ProceduralMesh final
{
public:
    auto initialize(const LibraProceduralModelView& view) -> bool;
    auto update(const LibraProceduralModelView& view) -> bool;
    auto mesh() const -> godot::Ref<godot::ArrayMesh> { return mesh_; }
    auto pivot() const -> godot::Vector3 { return pivot_; }
private:
    godot::Ref<godot::ArrayMesh> mesh_;
    godot::PackedByteArray vertices_, attributes_, indices_;
    std::uint32_t vertexCount_ = 0, indexCount_ = 0, vertexStride_ = 0, normalStride_ = 0, attributeStride_ = 0, indexStride_ = 0;
    std::uint32_t positionOffset_ = 0, normalOffset_ = 0, tangentOffset_ = 0, uvOffset_ = 0, uv2Offset_ = 0, colorOffset_ = 0;
    std::uint64_t vertexRevision_ = 0, indexRevision_ = 0;
    godot::Vector3 pivot_;
};
}
