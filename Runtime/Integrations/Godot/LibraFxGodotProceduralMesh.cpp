#include "LibraFxGodotProceduralMesh.h"
#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/variant/packed_vector3_array.hpp>
#include <godot_cpp/variant/packed_vector2_array.hpp>
#include <godot_cpp/variant/packed_float32_array.hpp>
#include <godot_cpp/variant/packed_color_array.hpp>
#include <godot_cpp/variant/packed_int32_array.hpp>
#include <godot_cpp/variant/array.hpp>
#include <algorithm>
#include <cstring>

namespace liberafx::godot_adapter {
using namespace godot;
auto ProceduralMesh::initialize(const LibraProceduralModelView& view) -> bool
{
    vertexCount_ = view.vertexCount; indexCount_ = view.indexCount;
    auto vertices = PackedVector3Array{}, normals = PackedVector3Array{};
    auto tangents = PackedFloat32Array{};
    auto uv = PackedVector2Array{}, uv2 = PackedVector2Array{};
    auto colors = PackedColorArray{}; auto indices = PackedInt32Array{};
    vertices.resize(vertexCount_); normals.resize(vertexCount_); tangents.resize(vertexCount_ * 4);
    uv.resize(vertexCount_); uv2.resize(vertexCount_); colors.resize(vertexCount_); indices.resize(indexCount_);
    for (auto i = 0u; i < vertexCount_; ++i)
    {
        const auto& v = view.vertices[i];
        vertices.set(i, Vector3(v.position[0], v.position[1], v.position[2]));
        normals.set(i, Vector3(v.normal[0], v.normal[1], v.normal[2]));
        for (auto c = 0u; c < 4; ++c)
        {
            tangents.set(i * 4 + c, v.tangent[c]);
        }
        uv.set(i, Vector2(v.uv[0], v.uv[1])); uv2.set(i, Vector2(v.uv2[0], v.uv2[1]));
        colors.set(i, Color(v.color[0], v.color[1], v.color[2], v.color[3]));
    }
    for (auto i = 0u; i < indexCount_; i += 3)
    {
        // Canonical generator uses CCW; Godot front faces are clockwise.
        indices.set(i, view.indices[i]); indices.set(i + 1, view.indices[i + 2]); indices.set(i + 2, view.indices[i + 1]);
    }
    auto arrays = Array{}; arrays.resize(Mesh::ARRAY_MAX);
    arrays[Mesh::ARRAY_VERTEX] = vertices; arrays[Mesh::ARRAY_NORMAL] = normals; arrays[Mesh::ARRAY_TANGENT] = tangents;
    arrays[Mesh::ARRAY_TEX_UV] = uv; arrays[Mesh::ARRAY_TEX_UV2] = uv2; arrays[Mesh::ARRAY_COLOR] = colors; arrays[Mesh::ARRAY_INDEX] = indices;
    mesh_.instantiate();
    mesh_->add_surface_from_arrays(Mesh::PRIMITIVE_TRIANGLES, arrays, {}, Dictionary{}, Mesh::ARRAY_FLAG_USE_DYNAMIC_UPDATE);
    auto* server = RenderingServer::get_singleton();
    if (!server || mesh_->get_surface_count() != 1)
    {
        return false;
    }
    const auto format = BitField<RenderingServer::ArrayFormat>(static_cast<std::int64_t>(mesh_->surface_get_format(0)));
    vertexStride_ = server->mesh_surface_get_format_vertex_stride(format, vertexCount_);
    normalStride_ = server->mesh_surface_get_format_normal_tangent_stride(format, vertexCount_);
    attributeStride_ = server->mesh_surface_get_format_attribute_stride(format, vertexCount_);
    indexStride_ = server->mesh_surface_get_format_index_stride(format, vertexCount_);
    positionOffset_ = server->mesh_surface_get_format_offset(format, vertexCount_, Mesh::ARRAY_VERTEX);
    normalOffset_ = server->mesh_surface_get_format_offset(format, vertexCount_, Mesh::ARRAY_NORMAL);
    tangentOffset_ = server->mesh_surface_get_format_offset(format, vertexCount_, Mesh::ARRAY_TANGENT);
    uvOffset_ = server->mesh_surface_get_format_offset(format, vertexCount_, Mesh::ARRAY_TEX_UV);
    uv2Offset_ = server->mesh_surface_get_format_offset(format, vertexCount_, Mesh::ARRAY_TEX_UV2);
    colorOffset_ = server->mesh_surface_get_format_offset(format, vertexCount_, Mesh::ARRAY_COLOR);
    const auto bytes = static_cast<std::uint64_t>(vertexStride_ + normalStride_) * vertexCount_;
    if (vertexStride_ != 12 || normalStride_ != 8 || (indexStride_ != 2 && indexStride_ != 4) ||
        positionOffset_ + 12 > vertexStride_ || normalOffset_ + (vertexCount_ - 1) * normalStride_ + 4 > bytes ||
        tangentOffset_ + (vertexCount_ - 1) * normalStride_ + 4 > bytes ||
        uvOffset_ + 8 > attributeStride_ || uv2Offset_ + 8 > attributeStride_ || colorOffset_ + 4 > attributeStride_)
    {
        return false;
    }
    vertices_.resize(bytes); attributes_.resize(static_cast<std::uint64_t>(attributeStride_) * vertexCount_);
    indices_.resize(static_cast<std::uint64_t>(indexStride_) * indexCount_);
    return update(view);
}
auto ProceduralMesh::update(const LibraProceduralModelView& view) -> bool
{
    auto* server = RenderingServer::get_singleton();
    if (!server || mesh_.is_null() || view.vertexCount != vertexCount_ || view.indexCount != indexCount_)
    {
        return false;
    }
    if (vertexRevision_ != view.vertexRevision)
    {
        auto* vertices = vertices_.ptrw(); auto* attributes = attributes_.ptrw();
        const auto packOct = [](std::uint8_t* output, const Vector2& oct)
        {
            const auto x = static_cast<std::uint16_t>(std::clamp(static_cast<float>(oct.x), 0.0f, 1.0f) * 65535.0f);
            const auto y = static_cast<std::uint16_t>(std::clamp(static_cast<float>(oct.y), 0.0f, 1.0f) * 65535.0f);
            std::memcpy(output, &x, 2); std::memcpy(output + 2, &y, 2);
        };
        for (auto i = 0u; i < vertexCount_; ++i)
        {
            const auto& v = view.vertices[i];
            std::memcpy(vertices + i * vertexStride_ + positionOffset_, v.position, 12);
            packOct(vertices + normalOffset_ + i * normalStride_, Vector3(v.normal[0], v.normal[1], v.normal[2]).octahedron_encode());
            packOct(vertices + tangentOffset_ + i * normalStride_, Vector3(v.tangent[0], v.tangent[1], v.tangent[2]).octahedron_tangent_encode(v.tangent[3]));
            std::memcpy(attributes + i * attributeStride_ + uvOffset_, v.uv, 8);
            std::memcpy(attributes + i * attributeStride_ + uv2Offset_, v.uv2, 8);
            for (auto c = 0u; c < 4; ++c)
            {
                attributes[i * attributeStride_ + colorOffset_ + c] = static_cast<std::uint8_t>(std::clamp(v.color[c], 0.0f, 1.0f) * 255.0f);
            }
        }
        server->mesh_surface_update_vertex_region(mesh_->get_rid(), 0, 0, vertices_);
        server->mesh_surface_update_attribute_region(mesh_->get_rid(), 0, 0, attributes_);
        vertexRevision_ = view.vertexRevision;
    }
    if (indexRevision_ != view.indexRevision)
    {
        auto* indices = indices_.ptrw();
        for (auto i = 0u; i < indexCount_; i += 3)
        {
            for (auto c = 0u; c < 3; ++c)
            {
                const auto value = view.indices[i + (c == 0 ? 0 : 3 - c)];
                if (indexStride_ == 2)
                {
                    const auto compact = static_cast<std::uint16_t>(value);
                    std::memcpy(indices + (i + c) * 2, &compact, 2);
                }
                else
                {
                    std::memcpy(indices + (i + c) * 4, &value, 4);
                }
            }
        }
        server->mesh_surface_update_index_region(mesh_->get_rid(), 0, 0, indices_);
        indexRevision_ = view.indexRevision;
    }
    const auto minimum = Vector3(view.boundsMin[0], view.boundsMin[1], view.boundsMin[2]);
    const auto maximum = Vector3(view.boundsMax[0], view.boundsMax[1], view.boundsMax[2]);
    mesh_->set_custom_aabb(AABB(minimum, maximum - minimum));
    pivot_ = Vector3(view.pivot[0], view.pivot[1], view.pivot[2]);
    return true;
}
}
