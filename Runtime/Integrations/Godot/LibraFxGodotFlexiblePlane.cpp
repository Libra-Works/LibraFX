#include "LibraFxGodotFlexiblePlane.h"
#include <godot_cpp/classes/base_material3d.hpp>
#include <godot_cpp/classes/thread.hpp>
#include <godot_cpp/core/memory.hpp>
#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/color.hpp>
#include <godot_cpp/variant/packed_color_array.hpp>
#include <godot_cpp/variant/packed_int32_array.hpp>
#include <godot_cpp/variant/packed_vector2_array.hpp>
#include <godot_cpp/variant/packed_vector3_array.hpp>
#include <godot_cpp/variant/utility_functions.hpp>
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
    if (!materialArray.is_empty() || !textureArray.is_empty())
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
    if (inputs.size() > 6)
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
            !item.has("sourceNodeId") || !item.has("materialParameter") ||
            item.get("materialParameter", godot::Variant{}).get_type() != godot::Variant::NIL)
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

auto FlexiblePlaneRenderer::create(godot::Node3D& parent,
    const flexible_adapter::Definition& definition, const godot::Ref<godot::Texture2D>& texture,
    std::unique_ptr<FlexiblePlaneRenderer>& output, godot::String& error) -> bool
{
    if (!godot::Thread::is_main_thread() || !texture.is_valid() || !flexible_adapter::validate(definition))
    {
        error = "Flexible Plane definition or texture is invalid.";
        return false;
    }
    auto instance = std::unique_ptr<FlexiblePlaneRenderer>(new FlexiblePlaneRenderer(definition));
    instance->mesh_.instantiate();
    instance->material_.instantiate();
    instance->material_->set_texture(godot::BaseMaterial3D::TEXTURE_ALBEDO, texture);
    instance->material_->set_shading_mode(godot::BaseMaterial3D::SHADING_MODE_UNSHADED);
    instance->material_->set_transparency(godot::BaseMaterial3D::TRANSPARENCY_ALPHA);
    instance->material_->set_vertex_color_use_as_albedo(true);
    instance->material_->set_flag(godot::BaseMaterial3D::FLAG_USE_TEXTURE_REPEAT, definition.options.uvMode == 1);
    instance->material_->set_cull_mode(godot::BaseMaterial3D::CULL_DISABLED);
    auto* node = memnew(godot::MeshInstance3D);
    node->set_name("Flexible Plane");
    node->set_mesh(instance->mesh_);
    node->set_material_override(instance->material_);
    parent.add_child(node);
    instance->nodeId_ = node->get_instance_id();
    output = std::move(instance);
    return true;
}

FlexiblePlaneRenderer::~FlexiblePlaneRenderer()
{
    if (!godot::Thread::is_main_thread())
    {
        godot::UtilityFunctions::push_error("Flexible Plane must be destroyed on the Godot main thread.");
        return;
    }
    if (auto* node = godot::Object::cast_to<godot::MeshInstance3D>(godot::ObjectDB::get_instance(nodeId_)))
    {
        node->queue_free();
    }
}

auto FlexiblePlaneRenderer::update(SpawnableHost& host, const std::uint8_t* container,
    std::uint64_t instance, double time, godot::Camera3D* camera,
    flexible_adapter::HistoryScope& history, godot::String& error) -> bool
{
    if (!godot::Thread::is_main_thread())
    {
        error = "Flexible Plane update must run on the Godot main thread.";
        return false;
    }
    auto* node = godot::Object::cast_to<godot::MeshInstance3D>(godot::ObjectDB::get_instance(nodeId_));
    if (!node)
    {
        error = "Flexible Plane node is unavailable.";
        return false;
    }
    auto localCamera = godot::Vector3{};
    if (definition_.options.facing == 1)
    {
        if (!camera)
        {
            error = "Flexible Plane requires a camera.";
            return false;
        }
        const auto transform = node->get_global_transform();
        const auto determinant = transform.basis.determinant();
        if (!std::isfinite(determinant) || std::abs(determinant) < 1e-12)
        {
            error = "Flexible Plane camera transform is singular.";
            return false;
        }
        localCamera = transform.affine_inverse().xform(camera->get_global_transform().origin);
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
    auto vertices = godot::PackedVector3Array{};
    auto uv = godot::PackedVector2Array{};
    auto colors = godot::PackedColorArray{};
    auto indices = godot::PackedInt32Array{};
    const auto counts = geometry_.counts();
    vertices.resize(counts.vertices);
    uv.resize(counts.vertices);
    colors.resize(counts.vertices);
    indices.resize(counts.indices);
    for (auto index = 0u; index < counts.vertices; ++index)
    {
        const auto& v = geometry_.vertices()[index];
        vertices.set(index, godot::Vector3(v.x, v.y, v.z));
        uv.set(index, godot::Vector2(v.u, v.v));
        colors.set(index, godot::Color(v.r, v.g, v.b, v.a));
    }
    for (auto index = 0u; index < counts.indices; ++index)
    {
        indices.set(index, static_cast<std::int32_t>(geometry_.indices()[index]));
    }
    auto arrays = godot::Array{};
    arrays.resize(godot::Mesh::ARRAY_MAX);
    arrays[godot::Mesh::ARRAY_VERTEX] = vertices;
    arrays[godot::Mesh::ARRAY_TEX_UV] = uv;
    arrays[godot::Mesh::ARRAY_COLOR] = colors;
    arrays[godot::Mesh::ARRAY_INDEX] = indices;
    mesh_->clear_surfaces();
    if (counts.indices != 0)
    {
        mesh_->add_surface_from_arrays(godot::Mesh::PRIMITIVE_TRIANGLES, arrays);
    }
    return true;
}
}
