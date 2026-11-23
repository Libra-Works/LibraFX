#pragma once

#include "liberafx/host/LbFlexiblePlaneAdapter.h"
#include "LibraFxGodotRenderServer.h"
#include "liberafx/LbSpawnableHost.h"
#include <godot_cpp/classes/array_mesh.hpp>
#include <godot_cpp/classes/camera3d.hpp>
#include <godot_cpp/classes/material.hpp>
#include <godot_cpp/classes/node3d.hpp>
#include <godot_cpp/classes/shader_material.hpp>
#include <godot_cpp/classes/standard_material3d.hpp>
#include <godot_cpp/classes/texture2d.hpp>
#include <godot_cpp/core/object.hpp>
#include <godot_cpp/variant/packed_byte_array.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/packed_color_array.hpp>
#include <godot_cpp/variant/packed_int32_array.hpp>
#include <godot_cpp/variant/packed_vector2_array.hpp>
#include <godot_cpp/variant/packed_vector3_array.hpp>
#include <memory>

namespace liberafx::godot_adapter {

// All methods and destruction must run on Godot's owner thread.
class FlexiblePlaneRenderer final {
public:
    static auto prepare(const godot::Dictionary& node, const godot::Ref<godot::Texture2D>& texture,
                        flexible_adapter::Definition& definition, godot::String& error) -> bool;
    static auto create(godot::Node3D& scope, const flexible_adapter::Definition& definition,
                       const godot::Ref<godot::Texture2D>& texture,
                       std::unique_ptr<FlexiblePlaneRenderer>& output, godot::String& error,
                       const godot::Ref<godot::Material>& hostMaterial = godot::Ref<godot::Material>{}) -> bool;
    ~FlexiblePlaneRenderer();

    // Shared history belongs to exactly one root/child scope. container == nullptr means root.
    auto update(godot::Node3D& scope, SpawnableHost& host, const std::uint8_t* container, std::uint64_t instance,
                double time, godot::Camera3D* camera, flexible_adapter::HistoryScope& history,
                godot::String& error) -> bool;
    void synchronize(godot::Node3D& scope, bool enabled, bool exiting = false);
    void setTexture(const godot::Ref<godot::Texture2D>& texture);
    void applyProceduralSettings(int blend, bool doubleSided, float emission);
    void setShaderParameter(const godot::StringName& name, const godot::Variant& value);
    auto hasAllocatedInstance() const -> bool { return instance_.isValid(); }
    auto surfaceUpdates() const -> std::uint64_t { return surfaceUpdates_; }
private:
    explicit FlexiblePlaneRenderer(const flexible_adapter::Definition& definition);
    auto initializeSurface(godot::String& error) -> bool;
    auto updateSurface(godot::String& error) -> bool;

    flexible_adapter::Definition definition_;
    flexible_adapter::Geometry geometry_;
    OwnedRenderingRid instance_;
    godot::Ref<godot::ArrayMesh> mesh_;
    godot::Ref<godot::StandardMaterial3D> material_;
    godot::Ref<godot::Material> hostMaterial_;
    godot::PackedVector3Array vertices_;
    godot::PackedVector2Array uv_;
    godot::PackedColorArray colors_;
    godot::PackedInt32Array indices_;
    godot::PackedByteArray vertexBytes_;
    godot::PackedByteArray attributeBytes_;
    godot::PackedByteArray indexBytes_;
    std::uint32_t maxVertices_ = 0;
    std::uint32_t maxIndices_ = 0;
    std::uint32_t vertexStride_ = 0;
    std::uint32_t attributeStride_ = 0;
    std::uint32_t indexStride_ = 0;
    std::uint32_t vertexOffset_ = 0;
    std::uint32_t uvOffset_ = 0;
    std::uint32_t colorOffset_ = 0;
    bool surfaceInitialized_ = false;
    bool hasGeometry_ = false;
    std::uint64_t surfaceUpdates_ = 0;
};
}
