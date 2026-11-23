#include "LibraFxGodotFlexiblePlane.h"
#include <godot_cpp/classes/base_material3d.hpp>
#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/classes/thread.hpp>
#include <godot_cpp/classes/world3d.hpp>
#include <godot_cpp/core/memory.hpp>
#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/color.hpp>
#include <godot_cpp/variant/packed_color_array.hpp>
#include <godot_cpp/variant/packed_int32_array.hpp>
#include <godot_cpp/variant/packed_vector2_array.hpp>
#include <godot_cpp/variant/packed_vector3_array.hpp>
#include <godot_cpp/variant/utility_functions.hpp>
#include <algorithm>
#include <cmath>
#include <cstring>

namespace liberafx::godot_adapter {
namespace flexible_godot {

auto text(const godot::Dictionary& object, const char* key, godot::String& output) -> bool
{
    const auto value = object.get(key, godot::Variant{});
    if (value.get_type() != godot::Variant::STRING)
    {
        return false;
    }
    output = value;
    return true;
}

auto number(const godot::Variant& value, double& output) -> bool
{
    if (value.get_type() != godot::Variant::FLOAT && value.get_type() != godot::Variant::INT)
    {
        return false;
    }
    output = value;
    return flexible_adapter::finiteFloat(output);
}

auto integer(const godot::Dictionary& object, const char* key, int& output) -> bool
{
    auto value = 0.0;
    if (!number(object.get(key, godot::Variant{}), value) || std::trunc(value) != value ||
        value < 0 || value > 4096)
    {
        return false;
    }
    output = static_cast<int>(value);
    return true;
}

auto array(const godot::Variant& value, int width, double* output) -> bool
{
    if (value.get_type() != godot::Variant::ARRAY)
    {
        return false;
    }
    auto values = godot::Array{};
    values = value;
    if (values.size() != width)
    {
        return false;
    }
    for (auto index = 0; index < width; ++index)
    {
        if (!number(values[index], output[index]))
        {
            return false;
        }
    }
    return true;
}

auto guid(const godot::String& text, flexible_adapter::Id& id) -> bool
{
    if (text.length() != 36)
    {
        return false;
    }
    auto digit = 0u;
    for (auto index = 0; index < 36; ++index)
    {
        const auto ch = text[index];
        if (index == 8 || index == 13 || index == 18 || index == 23)
        {
            if (ch != '-')
            {
                return false;
            }
            continue;
        }
        const auto value = ch >= '0' && ch <= '9' ? ch - '0' :
            ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 :
            ch >= 'A' && ch <= 'F' ? ch - 'A' + 10 : 16;
        if (value >= 16)
        {
            return false;
        }
        id[digit / 2] |= static_cast<std::uint8_t>(value << (digit % 2 == 0 ? 4 : 0));
        ++digit;
    }
    return std::any_of(id.begin(), id.end(), [](auto value) { return value != 0; });
}

auto parse(const godot::Dictionary& node, flexible_adapter::Definition& output) -> bool
{
    auto kind = godot::String{}, path = godot::String{}, nodeId = godot::String{};
    auto drawId = flexible_adapter::Id{};
    if (!text(node, "kind", kind) || kind != "flexiblePlane" ||
        !text(node, "assetPath", path) || path.strip_edges().is_empty() ||
        !text(node, "nodeId", nodeId) || !guid(nodeId, drawId))
    {
        return false;
    }
    const auto materials = node.get("materials", godot::Variant{});
    const auto textures = node.get("textures", godot::Variant{});
    const auto billboard = node.get("billboard", godot::Variant{});
    if (materials.get_type() != godot::Variant::ARRAY || textures.get_type() != godot::Variant::ARRAY ||
        billboard.get_type() != godot::Variant::BOOL || static_cast<bool>(billboard))
    {
        return false;
    }
    auto materialArray = godot::Array{};
    auto textureArray = godot::Array{};
    materialArray = materials;
    textureArray = textures;
    if (materialArray.size() > 1)
    {
        return false;
    }
    const auto planeValue = node.get("flexiblePlane", godot::Variant{});
    const auto inputValue = node.get("inputs", godot::Variant{});
    if (planeValue.get_type() != godot::Variant::DICTIONARY || inputValue.get_type() != godot::Variant::ARRAY)
    {
        return false;
    }
    auto plane = godot::Dictionary{};
    plane = planeValue;
    auto version = 0, capacity = 0;
    auto sourceKind = godot::String{}, source = godot::String{}, facing = godot::String{}, uv = godot::String{};
    auto age = 0.0, repeat = 0.0;
    auto up = std::array<double, 3>{};
    if (!integer(plane, "version", version) || version != 1 || !integer(plane, "capacity", capacity) ||
        !text(plane, "sourceKind", sourceKind) || (sourceKind != "list" && sourceKind != "history") ||
        !text(plane, "sourceNodeId", source) || !guid(source, output.source) ||
        !text(plane, "facing", facing) || (facing != "camera" && facing != "fixedUp") ||
        !text(plane, "uvMode", uv) || (uv != "stretch" && uv != "repeat") ||
        !number(plane.get("maxAge", godot::Variant{}), age) ||
        !number(plane.get("repeatLength", godot::Variant{}), repeat) ||
        !array(plane.get("up", godot::Variant{}), 3, up.data()))
    {
        return false;
    }
    output.history = sourceKind == "history";
    output.capacity = static_cast<std::uint32_t>(capacity);
    output.maxAge = age;
    output.options = {facing == "camera" ? 1u : 0u, uv == "repeat" ? 1u : 0u,
        static_cast<float>(up[0]), static_cast<float>(up[1]), static_cast<float>(up[2]),
        0, 0, 0, static_cast<float>(repeat)};
    const auto pointValue = plane.get("points", godot::Variant{});
    if (pointValue.get_type() != godot::Variant::ARRAY)
    {
        return false;
    }
    auto points = godot::Array{};
    points = pointValue;
    if (points.size() > 64)
    {
        return false;
    }
    for (auto index = 0; index < points.size(); ++index)
    {
        if (points[index].get_type() != godot::Variant::DICTIONARY)
        {
            return false;
        }
        auto point = godot::Dictionary{};
        point = points[index];
        auto position = std::array<double, 3>{};
        auto color = std::array<double, 4>{};
        auto width = 0.0;
        if (!array(point.get("position", godot::Variant{}), 3, position.data()) ||
            !array(point.get("color", godot::Variant{}), 4, color.data()) ||
            !number(point.get("width", godot::Variant{}), width) || width < 0)
        {
            return false;
        }
        output.points.push_back({static_cast<float>(position[0]), static_cast<float>(position[1]),
            static_cast<float>(position[2]), static_cast<float>(width), static_cast<float>(color[0]),
            static_cast<float>(color[1]), static_cast<float>(color[2]), static_cast<float>(color[3])});
    }
    auto inputs = godot::Array{};
    inputs = inputValue;
    if (inputs.size() > 256)
    {
        return false;
    }
    for (auto index = 0; index < inputs.size(); ++index)
    {
        if (inputs[index].get_type() != godot::Variant::DICTIONARY)
        {
            return false;
        }
        auto item = godot::Dictionary{};
        item = inputs[index];
        auto id = godot::String{};
        auto width = 0, component = 0;
        auto parsed = flexible_adapter::Input{};
        if (!text(item, "id", id) || !integer(item, "width", width) || width < 1 || width > 4 ||
            !integer(item, "sourceComponent", component) || component + width > 4 ||
            !array(item.get("defaults", godot::Variant{}), width, parsed.defaults.data()) ||
            !item.has("sourceNodeId") || !item.has("materialParameter"))
        {
            return false;
        }
        const auto matParam = item.get("materialParameter", godot::Variant{});
        if (matParam.get_type() == godot::Variant::DICTIONARY)
        {
            continue;
        }
        if (matParam.get_type() != godot::Variant::NIL)
        {
            return false;
        }
        parsed.id = id.utf8().get_data();
        parsed.width = static_cast<std::uint32_t>(width);
        parsed.component = static_cast<std::uint32_t>(component);
        const auto sourceValue = item.get("sourceNodeId", godot::Variant{});
        if (sourceValue.get_type() != godot::Variant::NIL)
        {
            if (sourceValue.get_type() != godot::Variant::STRING)
            {
                return false;
            }
            auto sourceText = godot::String{};
            sourceText = sourceValue;
            if (!guid(sourceText, parsed.source))
            {
                return false;
            }
            parsed.connected = true;
        }
        output.inputs.push_back(std::move(parsed));
    }
    return flexible_adapter::validate(output);
}
}

FlexiblePlaneRenderer::FlexiblePlaneRenderer(const flexible_adapter::Definition& definition)
    : definition_(definition), geometry_(definition)
{
}

auto FlexiblePlaneRenderer::prepare(const godot::Dictionary& node,
    const godot::Ref<godot::Texture2D>& texture, flexible_adapter::Definition& definition,
    godot::String& error) -> bool
{
    auto candidate = flexible_adapter::Definition{};
    if (!godot::Thread::is_main_thread() || !texture.is_valid() || !flexible_godot::parse(node, candidate))
    {
        error = "Flexible Plane descriptor or texture binding is invalid.";
        return false;
    }
    definition = std::move(candidate);
    return true;
}

auto FlexiblePlaneRenderer::create(godot::Node3D& scope,
    const flexible_adapter::Definition& definition, const godot::Ref<godot::Texture2D>& texture,
    std::unique_ptr<FlexiblePlaneRenderer>& output, godot::String& error,
    const godot::Ref<godot::Material>& hostMaterial) -> bool
{
    if (!godot::Thread::is_main_thread() || (!texture.is_valid() && hostMaterial.is_null()) ||
        !flexible_adapter::validate(definition))
    {
        error = "Flexible Plane definition or texture is invalid.";
        return false;
    }
    auto instance = std::unique_ptr<FlexiblePlaneRenderer>(new FlexiblePlaneRenderer(definition));
    instance->mesh_.instantiate();
    if (hostMaterial.is_valid())
    {
        auto duplicated = hostMaterial->duplicate();
        instance->hostMaterial_ = godot::Ref<godot::Material>(duplicated);
        if (instance->hostMaterial_.is_null())
        {
            instance->hostMaterial_ = hostMaterial;
        }
    }
    else
    {
        instance->material_.instantiate();
    }
    if (instance->mesh_.is_null() || (instance->material_.is_null() && instance->hostMaterial_.is_null())) {
        error = "Godot could not allocate Flexible Plane rendering resources.";
        return false;
    }
    if (instance->material_.is_valid())
    {
        instance->material_->set_texture(godot::BaseMaterial3D::TEXTURE_ALBEDO, texture);
        instance->material_->set_shading_mode(godot::BaseMaterial3D::SHADING_MODE_UNSHADED);
        instance->material_->set_transparency(godot::BaseMaterial3D::TRANSPARENCY_ALPHA);
        instance->material_->set_flag(godot::BaseMaterial3D::FLAG_ALBEDO_FROM_VERTEX_COLOR, true);
        instance->material_->set_flag(godot::BaseMaterial3D::FLAG_USE_TEXTURE_REPEAT, definition.options.uvMode == 1);
        instance->material_->set_cull_mode(godot::BaseMaterial3D::CULL_DISABLED);
    }
    auto maximumPoints = definition.history ? definition.capacity
        : static_cast<std::uint32_t>(definition.points.size());
    instance->maxVertices_ = maximumPoints * 2;
    instance->maxIndices_ = (maximumPoints - 1) * 6;
    instance->vertices_.resize(instance->maxVertices_);
    instance->uv_.resize(instance->maxVertices_);
    instance->colors_.resize(instance->maxVertices_);
    instance->indices_.resize(instance->maxIndices_);
    if (instance->vertices_.size() != instance->maxVertices_ ||
        instance->uv_.size() != instance->maxVertices_ ||
        instance->colors_.size() != instance->maxVertices_ ||
        instance->indices_.size() != instance->maxIndices_) {
        error = "Godot could not allocate Flexible Plane geometry buffers.";
        return false;
    }
    auto* server = godot::RenderingServer::get_singleton();
    if (!server) {
        error = "Godot RenderingServer is unavailable.";
        return false;
    }
    instance->instance_ = OwnedRenderingRid(server->instance_create());
    if (!instance->instance_.isValid()) {
        error = "Godot could not create a Flexible Plane rendering instance.";
        return false;
    }
    server->instance_set_base(instance->instance_.get(), instance->mesh_->get_rid());
    auto world = scope.get_world_3d();
    server->instance_set_scenario(instance->instance_.get(),
        world.is_valid() ? world->get_scenario() : godot::RID{});
    server->instance_set_transform(instance->instance_.get(), scope.get_global_transform());
    server->instance_set_visible(instance->instance_.get(), false);
    output = std::move(instance);
    return true;
}

auto FlexiblePlaneRenderer::initializeSurface(godot::String& error) -> bool
{
    auto arrays = godot::Array{};
    arrays.resize(godot::Mesh::ARRAY_MAX);
    arrays[godot::Mesh::ARRAY_VERTEX] = vertices_;
    arrays[godot::Mesh::ARRAY_TEX_UV] = uv_;
    arrays[godot::Mesh::ARRAY_COLOR] = colors_;
    arrays[godot::Mesh::ARRAY_INDEX] = indices_;
    mesh_->add_surface_from_arrays(godot::Mesh::PRIMITIVE_TRIANGLES, arrays, {},
        godot::Dictionary{}, godot::Mesh::ARRAY_FLAG_USE_DYNAMIC_UPDATE);
    if (mesh_->get_surface_count() != 1) {
        error = "Godot could not create the Flexible Plane dynamic mesh surface.";
        return false;
    }
    mesh_->surface_set_material(0, hostMaterial_.is_valid()
        ? hostMaterial_
        : godot::Ref<godot::Material>(material_));
    auto* server = godot::RenderingServer::get_singleton();
    if (!server) {
        error = "Godot RenderingServer is unavailable.";
        return false;
    }
    const auto meshFormat = mesh_->surface_get_format(0);
    const auto serverFormat = godot::BitField<godot::RenderingServer::ArrayFormat>(
        static_cast<std::int64_t>(meshFormat));
    vertexStride_ = server->mesh_surface_get_format_vertex_stride(serverFormat,
        static_cast<std::int32_t>(maxVertices_));
    attributeStride_ = server->mesh_surface_get_format_attribute_stride(serverFormat,
        static_cast<std::int32_t>(maxVertices_));
    indexStride_ = server->mesh_surface_get_format_index_stride(serverFormat,
        static_cast<std::int32_t>(maxVertices_));
    vertexOffset_ = server->mesh_surface_get_format_offset(serverFormat,
        static_cast<std::int32_t>(maxVertices_), godot::Mesh::ARRAY_VERTEX);
    uvOffset_ = server->mesh_surface_get_format_offset(serverFormat,
        static_cast<std::int32_t>(maxVertices_), godot::Mesh::ARRAY_TEX_UV);
    colorOffset_ = server->mesh_surface_get_format_offset(serverFormat,
        static_cast<std::int32_t>(maxVertices_), godot::Mesh::ARRAY_COLOR);
    if (vertexStride_ == 0 || attributeStride_ == 0 ||
        (indexStride_ != 2 && indexStride_ != 4) ||
        vertexOffset_ + sizeof(godot::Vector3) > vertexStride_ ||
        uvOffset_ + sizeof(godot::Vector2) > attributeStride_ ||
        colorOffset_ + 4 > attributeStride_) {
        error = "Godot returned an unsupported Flexible Plane mesh format.";
        return false;
    }
    vertexBytes_.resize(static_cast<std::int64_t>(vertexStride_) * maxVertices_);
    attributeBytes_.resize(static_cast<std::int64_t>(attributeStride_) * maxVertices_);
    indexBytes_.resize(static_cast<std::int64_t>(indexStride_) * maxIndices_);
    if (vertexBytes_.size() != static_cast<std::int64_t>(vertexStride_) * maxVertices_ ||
        attributeBytes_.size() != static_cast<std::int64_t>(attributeStride_) * maxVertices_ ||
        indexBytes_.size() != static_cast<std::int64_t>(indexStride_) * maxIndices_) {
        error = "Godot could not allocate Flexible Plane upload buffers.";
        return false;
    }
    surfaceInitialized_ = true;
    return true;
}

auto FlexiblePlaneRenderer::updateSurface(godot::String& error) -> bool
{
    auto* server = godot::RenderingServer::get_singleton();
    if (!server || !surfaceInitialized_) {
        error = "Flexible Plane dynamic mesh surface is unavailable.";
        return false;
    }
    auto* vertexData = vertexBytes_.ptrw();
    auto* attributeData = attributeBytes_.ptrw();
    auto* indexData = indexBytes_.ptrw();
    std::memset(vertexData, 0, static_cast<std::size_t>(vertexBytes_.size()));
    std::memset(attributeData, 0, static_cast<std::size_t>(attributeBytes_.size()));
    std::memset(indexData, 0, static_cast<std::size_t>(indexBytes_.size()));
    const auto* vertices = vertices_.ptr();
    const auto* uvs = uv_.ptr();
    const auto* colors = colors_.ptr();
    const auto* indices = indices_.ptr();
    for (auto index = 0u; index < maxVertices_; ++index) {
        const auto& vertex = vertices[index];
        const auto vertexOffset = static_cast<std::size_t>(index) * vertexStride_ + vertexOffset_;
        std::memcpy(vertexData + vertexOffset, &vertex, sizeof(vertex));
        const auto& uv = uvs[index];
        const auto uvOffset = static_cast<std::size_t>(index) * attributeStride_ + uvOffset_;
        std::memcpy(attributeData + uvOffset, &uv, sizeof(uv));
        const auto& color = colors[index];
        const auto colorOffset = static_cast<std::size_t>(index) * attributeStride_ + colorOffset_;
        const auto packColor = [](float value) {
            return static_cast<std::uint8_t>(std::clamp(value, 0.0f, 1.0f) * 255.0f);
        };
        attributeData[colorOffset] = packColor(color.r);
        attributeData[colorOffset + 1] = packColor(color.g);
        attributeData[colorOffset + 2] = packColor(color.b);
        attributeData[colorOffset + 3] = packColor(color.a);
    }
    for (auto index = 0u; index < maxIndices_; ++index) {
        const auto meshIndex = static_cast<std::uint32_t>(indices[index]);
        const auto offset = static_cast<std::size_t>(index) * indexStride_;
        if (indexStride_ == 2) {
            const auto compactIndex = static_cast<std::uint16_t>(meshIndex);
            std::memcpy(indexData + offset, &compactIndex, sizeof(compactIndex));
        } else {
            std::memcpy(indexData + offset, &meshIndex, sizeof(meshIndex));
        }
    }
    server->mesh_surface_update_vertex_region(mesh_->get_rid(), 0, 0, vertexBytes_);
    server->mesh_surface_update_attribute_region(mesh_->get_rid(), 0, 0, attributeBytes_);
    server->mesh_surface_update_index_region(mesh_->get_rid(), 0, 0, indexBytes_);
    return true;
}

FlexiblePlaneRenderer::~FlexiblePlaneRenderer()
{
    instance_.reset();
}

void FlexiblePlaneRenderer::setTexture(const godot::Ref<godot::Texture2D>& texture)
{
    if (!texture.is_valid())
    {
        return;
    }
    if (material_.is_valid())
    {
        material_->set_texture(godot::BaseMaterial3D::TEXTURE_ALBEDO, texture);
    }
}

void FlexiblePlaneRenderer::applyProceduralSettings(int blend, bool doubleSided, float emission)
{
    if (material_.is_null())
    {
        return;
    }
    material_->set_blend_mode(blend == 1
        ? godot::BaseMaterial3D::BLEND_MODE_ADD
        : godot::BaseMaterial3D::BLEND_MODE_MIX);
    material_->set_cull_mode(doubleSided
        ? godot::BaseMaterial3D::CULL_DISABLED
        : godot::BaseMaterial3D::CULL_BACK);
    material_->set_feature(godot::BaseMaterial3D::FEATURE_EMISSION, emission > 0.0f);
    if (emission > 0.0f)
    {
        material_->set_emission(godot::Color(1.0f, 1.0f, 1.0f, 1.0f));
        material_->set_emission_energy_multiplier(emission);
    }
}

void FlexiblePlaneRenderer::setShaderParameter(const godot::StringName& name, const godot::Variant& value)
{
    if (auto shaderMat = godot::Ref<godot::ShaderMaterial>(hostMaterial_); shaderMat.is_valid())
    {
        shaderMat->set_shader_parameter(name, value);
    }
}

void FlexiblePlaneRenderer::synchronize(godot::Node3D& scope, bool enabled, bool exiting)
{
    auto* server = godot::RenderingServer::get_singleton();
    if (!server || !instance_.isValid()) {
        return;
    }
    if (exiting || !scope.is_inside_tree()) {
        server->instance_set_visible(instance_.get(), false);
        server->instance_set_scenario(instance_.get(), godot::RID{});
        return;
    }
    auto world = scope.get_world_3d();
    server->instance_set_scenario(instance_.get(),
        world.is_valid() ? world->get_scenario() : godot::RID{});
    server->instance_set_transform(instance_.get(), scope.get_global_transform());
    server->instance_set_visible(instance_.get(), enabled && hasGeometry_ && scope.is_visible_in_tree());
}

auto FlexiblePlaneRenderer::update(godot::Node3D& scope, SpawnableHost& host, const std::uint8_t* container,
    std::uint64_t instance, double time, godot::Camera3D* camera,
    flexible_adapter::HistoryScope& history, godot::String& error) -> bool
{
    if (!godot::Thread::is_main_thread()) {
        error = "Flexible Plane update must run on the Godot main thread.";
        return false;
    }
    if (!instance_.isValid()) {
        error = "Flexible Plane RenderingServer instance is unavailable.";
        return false;
    }
    auto* server = godot::RenderingServer::get_singleton();
    if (!server) {
        error = "Godot RenderingServer is unavailable.";
        return false;
    }
    auto world = scope.get_world_3d();
    server->instance_set_scenario(instance_.get(),
        world.is_valid() ? world->get_scenario() : godot::RID{});
    const auto scopeTransform = scope.get_global_transform();
    server->instance_set_transform(instance_.get(), scopeTransform);
    server->instance_set_visible(instance_.get(), scope.is_visible_in_tree());
    auto localCamera = godot::Vector3{};
    if (definition_.options.facing == 1)
    {
        if (!camera)
        {
            error = "Flexible Plane requires a camera.";
            return false;
        }
        const auto determinant = scopeTransform.basis.determinant();
        if (!std::isfinite(determinant) || std::abs(determinant) < 1e-12)
        {
            error = "Flexible Plane camera transform is singular.";
            return false;
        }
        localCamera = scopeTransform.affine_inverse().xform(camera->get_global_transform().origin);
    }
    const auto read = [&host, container, instance](const flexible_adapter::Id& source,
        double* values, std::uint8_t& width) -> LibraResult {
        const auto result = container ? host.childValue(container, instance, source.data(), values, width)
                                      : host.rootValue(source.data(), values, width);
        if (result == Result::ArithmeticOverflow)
        {
            return LIBRA_ARITHMETIC_OVERFLOW;
        }
        return static_cast<LibraResult>(result);
    };
    const auto result = geometry_.update(definition_, history, read, time,
        static_cast<float>(localCamera.x), static_cast<float>(localCamera.y), static_cast<float>(localCamera.z));
    if (result != LIBRA_OK)
    {
        error = godot::String("Flexible Plane native evaluation failed: ") + godot::String::num_int64(result);
        return false;
    }
    const auto counts = geometry_.counts();
    if (counts.vertices > maxVertices_ || counts.indices > maxIndices_) {
        error = "Flexible Plane geometry exceeded its reserved mesh capacity.";
        return false;
    }
    if (counts.indices == 0) {
        hasGeometry_ = false;
        server->instance_set_visible(instance_.get(), false);
        return true;
    }
    auto minimum = godot::Vector3{};
    auto maximum = godot::Vector3{};
    for (auto index = 0u; index < maxVertices_; ++index) {
        vertices_.set(index, godot::Vector3{});
        uv_.set(index, godot::Vector2{});
        colors_.set(index, godot::Color(0, 0, 0, 0));
    }
    for (auto index = 0u; index < maxIndices_; ++index) {
        indices_.set(index, 0);
    }
    for (auto index = 0u; index < counts.vertices; ++index) {
        const auto& v = geometry_.vertices()[index];
        const auto vertex = godot::Vector3(v.x, v.y, v.z);
        vertices_.set(index, vertex);
        uv_.set(index, godot::Vector2(v.u, v.v));
        colors_.set(index, godot::Color(v.r, v.g, v.b, v.a));
        minimum.x = index == 0 ? vertex.x : std::min(minimum.x, vertex.x);
        minimum.y = index == 0 ? vertex.y : std::min(minimum.y, vertex.y);
        minimum.z = index == 0 ? vertex.z : std::min(minimum.z, vertex.z);
        maximum.x = index == 0 ? vertex.x : std::max(maximum.x, vertex.x);
        maximum.y = index == 0 ? vertex.y : std::max(maximum.y, vertex.y);
        maximum.z = index == 0 ? vertex.z : std::max(maximum.z, vertex.z);
    }
    for (auto index = 0u; index < counts.indices; ++index) {
        indices_.set(index, static_cast<std::int32_t>(geometry_.indices()[index]));
    }
    if (surfaceInitialized_) {
        if (!updateSurface(error)) {
            return false;
        }
    } else if (!initializeSurface(error)) {
        return false;
    }
    mesh_->set_custom_aabb(godot::AABB(minimum, maximum - minimum));
    hasGeometry_ = true;
    ++surfaceUpdates_;
    server->instance_set_visible(instance_.get(), scope.is_visible_in_tree());
    return true;
}
}
