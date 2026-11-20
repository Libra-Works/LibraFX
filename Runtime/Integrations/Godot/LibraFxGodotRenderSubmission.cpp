#include "LibraFxGodotRender.h"
#include <godot_cpp/classes/base_material3d.hpp>
#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/classes/viewport.hpp>
#include <godot_cpp/classes/world3d.hpp>
#include <godot_cpp/variant/utility_functions.hpp>
#include <algorithm>
#include <cmath>
#include <iterator>

namespace liberafx::godot_adapter {

void LibraRenderScopeNode::setRenderer(PackageRenderRenderer* renderer)
{
    renderer_ = renderer;
    set_notify_transform(renderer != nullptr);
    set_process_mode(godot::Node::PROCESS_MODE_ALWAYS);
    set_process_internal(renderer != nullptr);
}

void LibraRenderScopeNode::_notification(int what)
{
    if (!renderer_) {
        return;
    }
    if (what == NOTIFICATION_INTERNAL_PROCESS || what == NOTIFICATION_TRANSFORM_CHANGED ||
        what == NOTIFICATION_VISIBILITY_CHANGED || what == NOTIFICATION_ENTER_WORLD) {
        renderer_->synchronizePresentation();
    } else if (what == NOTIFICATION_EXIT_WORLD || what == NOTIFICATION_EXIT_TREE) {
        renderer_->synchronizePresentation(true);
    }
}

void PackageRenderRenderer::updateInstance(const OwnedRenderingRid& instance,
    godot::Node3D& scope, const godot::Transform3D& localTransform, bool visible, bool exiting)
{
    auto* server = godot::RenderingServer::get_singleton();
    if (!server || !instance.isValid()) {
        return;
    }
    if (exiting || !scope.is_inside_tree()) {
        server->instance_set_visible(instance.get(), false);
        server->instance_set_scenario(instance.get(), godot::RID{});
        return;
    }
    auto world = scope.get_world_3d();
    server->instance_set_scenario(instance.get(),
        world.is_valid() ? world->get_scenario() : godot::RID{});
    server->instance_set_transform(instance.get(), scope.get_global_transform() * localTransform);
    server->instance_set_visible(instance.get(), visible && scope.is_visible_in_tree());
}

void PackageRenderRenderer::synchronizeScope(godot::Node3D& scope,
    std::vector<RenderInstance>& renderables, bool enabled, bool exiting)
{
    for (auto& renderable : renderables) {
        if (renderable.flexiblePlane) {
            renderable.flexiblePlane->synchronize(scope, enabled, exiting);
        } else if (!renderable.batched) {
            if (!renderable.instance.isValid() && renderable.mesh.is_valid()) {
                auto* server = godot::RenderingServer::get_singleton();
                if (server) {
                    renderable.instance = OwnedRenderingRid(server->instance_create());
                    if (renderable.instance.isValid()) {
                        server->instance_set_base(renderable.instance.get(), renderable.mesh->get_rid());
                    } else {
                        godot::UtilityFunctions::push_error("Unable to create ModelDraw RenderingServer instance.");
                    }
                }
            }
            updateInstance(renderable.instance, scope, renderable.localTransform,
                enabled && renderable.submitted, exiting);
        }
    }
}

auto PackageRenderRenderer::opaqueBatchable(const godot::Ref<godot::Mesh>& mesh) -> bool
{
    if (mesh.is_null() || mesh->get_surface_count() <= 0) {
        return false;
    }
    for (auto surface = 0; surface < mesh->get_surface_count(); ++surface) {
        auto material = mesh->surface_get_material(surface);
        auto* standard = godot::Object::cast_to<godot::StandardMaterial3D>(material.ptr());
        if (!standard || standard->get_transparency() != godot::BaseMaterial3D::TRANSPARENCY_DISABLED ||
            standard->get_blend_mode() != godot::BaseMaterial3D::BLEND_MODE_MIX ||
            standard->get_billboard_mode() != godot::BaseMaterial3D::BILLBOARD_DISABLED ||
            standard->get_depth_draw_mode() != godot::BaseMaterial3D::DEPTH_DRAW_OPAQUE_ONLY ||
            standard->get_depth_test() != godot::BaseMaterial3D::DEPTH_TEST_DEFAULT ||
            standard->get_alpha_antialiasing() != godot::BaseMaterial3D::ALPHA_ANTIALIASING_OFF ||
            standard->get_render_priority() != 0 ||
            standard->get_feature(godot::BaseMaterial3D::FEATURE_REFRACTION) ||
            standard->get_flag(godot::BaseMaterial3D::FLAG_DISABLE_DEPTH_TEST) ||
            standard->get_flag(godot::BaseMaterial3D::FLAG_FIXED_SIZE) ||
            standard->get_flag(godot::BaseMaterial3D::FLAG_USE_SHADOW_TO_OPACITY) ||
            standard->get_flag(godot::BaseMaterial3D::FLAG_PARTICLE_TRAILS_MODE) ||
            standard->get_flag(godot::BaseMaterial3D::FLAG_ALBEDO_TEXTURE_MSDF) ||
            standard->get_next_pass().is_valid()) {
            return false;
        }
    }
    return true;
}

void PackageRenderRenderer::synchronizeModelBatches(bool exiting)
{
    auto* server = godot::RenderingServer::get_singleton();
    if (!server || !root_) {
        return;
    }
    if (exiting) {
        for (auto& batch : modelBatches_) {
            server->instance_set_visible(batch.instance.get(), false);
            server->instance_set_scenario(batch.instance.get(), godot::RID{});
        }
        return;
    }
    for (auto& batch : modelBatches_) {
        batch.members.clear();
    }
    const auto collect = [this](godot::Node3D& scope, std::vector<RenderInstance>& renderables,
                                bool enabled) {
        for (auto& renderable : renderables) {
            renderable.batched = false;
            if (!renderable.submitted || !renderable.modelBatchCandidate ||
                !opaqueBatchable(renderable.mesh) || !scope.is_inside_tree()) {
                continue;
            }
            // MultiMesh has one world-identity instance, so Godot cannot select
            // per-member reversed culling for a mirrored effective transform.
            const auto worldTransform = scope.get_global_transform() * renderable.localTransform;
            const auto determinant = worldTransform.basis.determinant();
            if (!std::isfinite(determinant) || determinant <= 0) {
                continue;
            }
            auto found = std::find_if(modelBatches_.begin(), modelBatches_.end(),
                [&renderable](const auto& batch) { return batch.mesh == renderable.mesh; });
            if (found == modelBatches_.end()) {
                modelBatches_.emplace_back();
                found = std::prev(modelBatches_.end());
                found->mesh = renderable.mesh;
            }
            found->members.push_back({&renderable, &scope, enabled});
        }
    };
    collect(*root_, rootRenderables_, ownerVisible_ && !rootFlexibleHidden_);
    for (auto& child : children_) {
        if (child.node) {
            collect(*child.node, child.renderables, ownerVisible_ && !child.flexibleHidden);
        }
    }
    modelBatches_.erase(std::remove_if(modelBatches_.begin(), modelBatches_.end(),
        [](const auto& batch) { return batch.members.empty(); }), modelBatches_.end());
    for (auto& batch : modelBatches_) {
        if (batch.members.size() < 2) {
            batch.instance.reset();
            batch.multiMesh.reset();
            batch.buffer.resize(0);
            batch.capacity = 0;
            batch.previous.clear();
            batch.uploaded = false;
            continue;
        }
        if (!batch.multiMesh.isValid()) {
            batch.multiMesh = OwnedRenderingRid(server->multimesh_create());
            batch.instance = OwnedRenderingRid(server->instance_create());
            if (!batch.multiMesh.isValid() || !batch.instance.isValid()) {
                godot::UtilityFunctions::push_error("Unable to create opaque ModelDraw batch.");
                batch.instance.reset();
                batch.multiMesh.reset();
                continue;
            }
            server->multimesh_set_mesh(batch.multiMesh.get(), batch.mesh->get_rid());
            server->instance_set_base(batch.instance.get(), batch.multiMesh.get());
        }
        batch.transforms.clear();
        for (auto& member : batch.members) {
            member.renderable->batched = true;
            member.renderable->instance.reset();
            if (member.enabled && member.scope->is_inside_tree() && member.scope->is_visible_in_tree()) {
                batch.transforms.push_back(member.scope->get_global_transform() *
                    member.renderable->localTransform);
            }
        }
        if (batch.members.size() > batch.capacity) {
            auto capacity = std::max(std::uint32_t{1}, batch.capacity);
            while (capacity < batch.members.size()) {
                capacity *= 2;
            }
            batch.capacity = capacity;
            batch.buffer.resize(static_cast<std::int64_t>(capacity) * 12);
            server->multimesh_allocate_data(batch.multiMesh.get(), static_cast<std::int32_t>(capacity),
                godot::RenderingServer::MULTIMESH_TRANSFORM_3D, false, false);
            batch.uploaded = false;
        }
        if (!batch.uploaded || batch.transforms != batch.previous) {
            auto* values = batch.buffer.ptrw();
            for (auto index = std::size_t{}; index < batch.transforms.size(); ++index) {
                const auto& transform = batch.transforms[index];
                for (auto row = 0; row < 3; ++row) {
                    for (auto column = 0; column < 3; ++column) {
                        values[index * 12 + row * 4 + column] =
                            static_cast<float>(transform.basis.get_column(column)[row]);
                    }
                    values[index * 12 + row * 4 + 3] = static_cast<float>(transform.origin[row]);
                }
            }
            server->multimesh_set_buffer(batch.multiMesh.get(), batch.buffer);
            server->multimesh_set_visible_instances(batch.multiMesh.get(),
                static_cast<std::int32_t>(batch.transforms.size()));
            batch.previous = batch.transforms;
            batch.uploaded = true;
        }
        auto world = root_->is_inside_tree() ? root_->get_world_3d() : godot::Ref<godot::World3D>{};
        server->instance_set_scenario(batch.instance.get(),
            world.is_valid() ? world->get_scenario() : godot::RID{});
        server->instance_set_transform(batch.instance.get(), godot::Transform3D{});
        server->instance_set_visible(batch.instance.get(), world.is_valid() && !batch.transforms.empty());
    }
}

void PackageRenderRenderer::synchronizeCpuScope(godot::Node3D& scope,
    std::vector<CpuParticleRenderInstance>& renderables, bool enabled, bool exiting)
{
    for (auto& renderable : renderables)
    {
        if (scope.is_inside_tree() && (!renderable.boundsValid ||
            renderable.boundsBasis != scope.get_global_transform().basis))
        {
            renderable.boundsValid = updateCpuBounds(renderable, scope.get_global_transform().basis);
        }
        updateInstance(renderable.instance, scope, godot::Transform3D{},
            enabled && renderable.boundsValid, exiting);
    }
}

void PackageRenderRenderer::synchronizePresentation(bool exiting)
{
    if (!root_) {
        return;
    }
    ownerVisible_ = !exiting;
    if (!exiting && root_->is_inside_tree()) {
        auto* ancestor = root_->get_parent();
        while (ancestor && !godot::Object::cast_to<godot::Node3D>(ancestor)) {
            if (godot::Object::cast_to<godot::Viewport>(ancestor)) {
                ancestor = nullptr;
                break;
            }
            ancestor = ancestor->get_parent();
        }
        auto* spatialOwner = godot::Object::cast_to<godot::Node3D>(ancestor);
        if (spatialOwner) {
            ownerVisible_ = spatialOwner->is_visible_in_tree();
            const auto transform = spatialOwner->get_global_transform();
            if (root_->get_global_transform() != transform) {
                root_->set_global_transform(transform);
            }
        }
    }
    synchronizeModelBatches(exiting);
    synchronizeScope(*root_, rootRenderables_, ownerVisible_ && !rootFlexibleHidden_, exiting);
    synchronizeCpuScope(*root_, rootCpuRenderables_, ownerVisible_ && !rootFlexibleHidden_, exiting);
    for (auto& child : children_) {
        if (child.node) {
            synchronizeScope(*child.node, child.renderables, ownerVisible_ && !child.flexibleHidden, exiting);
            synchronizeCpuScope(*child.node, child.cpuRenderables,
                ownerVisible_ && !child.flexibleHidden, exiting);
        }
    }
}

}
