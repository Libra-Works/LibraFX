#pragma once

#include "liberafx/host/LbFlexiblePlaneAdapter.h"
#include "liberafx/LbSpawnableHost.h"
#include <godot_cpp/classes/array_mesh.hpp>
#include <godot_cpp/classes/camera3d.hpp>
#include <godot_cpp/classes/mesh_instance3d.hpp>
#include <godot_cpp/classes/standard_material3d.hpp>
#include <godot_cpp/classes/texture2d.hpp>
#include <godot_cpp/core/object.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <memory>

namespace liberafx::godot_adapter {

// All methods and destruction must run on Godot's owner thread.
class FlexiblePlaneRenderer final {
public:
    static auto prepare(const godot::Dictionary& node, const godot::Ref<godot::Texture2D>& texture,
                        flexible_adapter::Definition& definition, godot::String& error) -> bool;
    static auto create(godot::Node3D& parent, const flexible_adapter::Definition& definition,
                       const godot::Ref<godot::Texture2D>& texture,
                       std::unique_ptr<FlexiblePlaneRenderer>& output, godot::String& error) -> bool;
    ~FlexiblePlaneRenderer();

    // Shared history belongs to exactly one root/child scope. container == nullptr means root.
    auto update(SpawnableHost& host, const std::uint8_t* container, std::uint64_t instance,
                double time, godot::Camera3D* camera, flexible_adapter::HistoryScope& history,
                godot::String& error) -> bool;
private:
    explicit FlexiblePlaneRenderer(const flexible_adapter::Definition& definition);
    flexible_adapter::Definition definition_;
    flexible_adapter::Geometry geometry_;
    std::uint64_t nodeId_ = 0;
    godot::Ref<godot::ArrayMesh> mesh_;
    godot::Ref<godot::StandardMaterial3D> material_;
};
}
