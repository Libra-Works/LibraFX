#include "LibraFxGodotProcedural.h"
#include "LibraFxGodotProceduralMesh.h"
#include "LibraFxGodotProceduralGpu.h"
#include "LibraFxGodotRenderServer.h"
#include <godot_cpp/classes/json.hpp>
#include <godot_cpp/classes/marshalls.hpp>
#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/classes/shader.hpp>
#include <godot_cpp/classes/shader_material.hpp>
#include <godot_cpp/classes/image.hpp>
#include <godot_cpp/classes/image_texture.hpp>
#include <godot_cpp/classes/world3d.hpp>
#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/utility_functions.hpp>
#include <map>
#include "liberafx/host/LbProceduralCache.h"

namespace liberafx::godot_adapter {
namespace procedural_godot {
using namespace godot;
using Id = flexible_adapter::Id;
using Input = procedural_adapter::Input;
static auto string(const Dictionary& object, const char* key, std::string& output) -> bool
{
    const auto value = object.get(key, Variant{});
    if (value.get_type() != Variant::STRING)
    {
        return false;
    }
    const auto text = static_cast<String>(value).utf8(); output = text.get_data();
    return !output.empty() && output.size() <= 128;
}
static auto guid(const std::string& text, Id& output) -> bool
{
    if (text.size() != 36)
    {
        return false;
    }
    auto digit = 0u; output = {};
    for (auto i = 0u; i < 36; ++i)
    {
        const auto ch = text[i];
        if (i == 8 || i == 13 || i == 18 || i == 23)
        {
            if (ch != '-')
            {
                return false;
            }
            continue;
        }
        const auto value = ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : ch >= 'A' && ch <= 'F' ? ch - 'A' + 10 : 16;
        if (value >= 16)
        {
            return false;
        }
        output[digit / 2] |= static_cast<std::uint8_t>(value << (digit % 2 == 0 ? 4 : 0)); ++digit;
    }
    return std::any_of(output.begin(), output.end(), [](auto value) { return value != 0; });
}
static auto number(const Variant& value, double& output) -> bool
{
    if (value.get_type() != Variant::INT && value.get_type() != Variant::FLOAT)
    {
        return false;
    }
    output = value;
    return std::isfinite(output) && std::abs(output) <= 1000000;
}
static auto inputs(const Dictionary& object, std::vector<Input>& output) -> bool
{
    auto value = object.get("inputs", Variant{});
    if (value.get_type() != Variant::ARRAY)
    {
        return false;
    }
    const Array array = value;
    if (array.size() > 128)
    {
        return false;
    }
    for (auto i = 0; i < array.size(); ++i)
    {
        if (array[i].get_type() != Variant::DICTIONARY)
        {
            return false;
        }
        const Dictionary item = array[i];
        auto input = Input{}; auto width = 0.0, component = 0.0;
        if (!string(item, "id", input.id) || !number(item.get("width", Variant{}), width) ||
            width < 1 || width > 4 || width != std::trunc(width) ||
            !number(item.get("sourceComponent", Variant{}), component) || component < 0 || component != std::trunc(component) || component + width > 4)
        {
            return false;
        }
        input.width = static_cast<std::uint32_t>(width); input.component = static_cast<std::uint32_t>(component);
        auto defaults = item.get("defaults", Variant{});
        if (defaults.get_type() != Variant::ARRAY || static_cast<Array>(defaults).size() != input.width)
        {
            return false;
        }
        const Array values = defaults;
        for (auto c = 0u; c < input.width; ++c)
        {
            if (!number(values[c], input.defaults[c]))
            {
                return false;
            }
        }
        auto source = item.get("sourceNodeId", Variant{});
        if (source.get_type() != Variant::NIL)
        {
            auto sourceId = std::string{};
            if (!string(item, "sourceNodeId", sourceId) || !guid(sourceId, input.source))
            {
                return false;
            }
            input.connected = true;
        }
        if (std::any_of(output.begin(), output.end(), [&](const auto& previous) { return previous.id == input.id; }))
        {
            return false;
        }
        output.push_back(std::move(input));
    }
    return true;
}
struct ResourceDefinition
{
    procedural_adapter::Definition native;
    std::array<std::string, 4> textures;
};
struct DrawDefinition
{
    std::string model, material;
    std::array<Input, 4> inputs;
};
struct Definition
{
    std::map<std::string, ResourceDefinition> resources;
    std::vector<DrawDefinition> draws;
    std::uint64_t bytes = 0;
};
static auto prepare(const std::vector<std::uint8_t>& bytes, Definition& output) -> Result
{
    if (bytes.empty())
    {
        return Result::Ok;
    }
    const auto parsed = JSON::parse_string(String::utf8(reinterpret_cast<const char*>(bytes.data()), bytes.size()));
    if (parsed.get_type() != Variant::DICTIONARY)
    {
        return Result::InvalidBinary;
    }
    const Dictionary config = parsed;
    if (!config.has("proceduralResources"))
    {
        return Result::Ok;
    }
    auto version = 0.0;
    const auto resources = config.get("proceduralResources", Variant{}), nodes = config.get("nodes", Variant{});
    if (!number(config.get("proceduralVersion", Variant{}), version) || version != 1 ||
        resources.get_type() != Variant::ARRAY || nodes.get_type() != Variant::ARRAY || static_cast<Array>(resources).size() > 64)
    {
        return Result::UnsupportedBinary;
    }
    const Array list = resources;
    for (auto i = 0; i < list.size(); ++i)
    {
        if (list[i].get_type() != Variant::DICTIONARY)
        {
            return Result::InvalidBinary;
        }
        const Dictionary item = list[i];
        auto id = std::string{}, kind = std::string{}; auto guidValue = Id{};
        const auto encoded = item.get("definition", Variant{});
        if (!string(item, "nodeId", id) || !guid(id, guidValue) || !string(item, "kind", kind) ||
            encoded.get_type() != Variant::STRING || static_cast<String>(encoded).length() > 5500 || output.resources.count(id) != 0)
        {
            return Result::InvalidBinary;
        }
        auto resource = ResourceDefinition{};
        const auto data = Marshalls::get_singleton()->base64_to_raw(encoded);
        const auto decoded = procedural_adapter::decode(data.ptr(), data.size(), resource.native);
        if (decoded != LIBRA_OK || !inputs(item, resource.native.inputs) ||
            kind != (resource.native.kind == 0 ? "ProceduralModel" : resource.native.kind == 1 ? "ProceduralTexture" : "ProceduralMaterial"))
        {
            return Result::InvalidBinary;
        }
        auto textureValue = item.get("textures", Variant{});
        if (textureValue.get_type() != Variant::ARRAY || static_cast<Array>(textureValue).size() > 4)
        {
            return Result::InvalidBinary;
        }
        const Array textures = textureValue;
        for (auto t = 0; t < textures.size(); ++t)
        {
            if (textures[t].get_type() != Variant::DICTIONARY || resource.native.kind != 2)
            {
                return Result::InvalidBinary;
            }
            const Dictionary texture = textures[t]; auto slot = 0.0; auto producer = std::string{};
            if (!number(texture.get("slot", Variant{}), slot) || slot < 0 || slot > 3 || slot != std::trunc(slot) ||
                !string(texture, "producerNodeId", producer) || !resource.textures[static_cast<std::size_t>(slot)].empty())
            {
                return Result::InvalidBinary;
            }
            resource.textures[static_cast<std::size_t>(slot)] = producer;
            resource.native.textureMask |= 1u << static_cast<std::uint32_t>(slot);
        }
        if (resource.native.kind == 0)
        {
            auto counts = LibraProceduralModelCounts{};
            if (libra_procedural_model_predict(&resource.native.model, resource.native.modifiers.data(), resource.native.modifiers.size(), &counts) != LIBRA_OK)
            {
                return Result::InvalidBinary;
            }
            output.bytes += counts.retainedBytes;
        }
        else
        {
            output.bytes += static_cast<std::uint64_t>(resource.native.resolution) * resource.native.resolution * 8 + 16384;
        }
        if (output.bytes > 64 * 1024 * 1024)
        {
            return Result::InsufficientWork;
        }
        output.resources.emplace(id, std::move(resource));
    }
    for (const auto& [id, resource] : output.resources)
    {
        for (const auto& texture : resource.textures)
        {
            if (!texture.empty() && (output.resources.count(texture) == 0 || output.resources.at(texture).native.kind != 1))
            {
                return Result::InvalidBinary;
            }
        }
    }
    const Array drawNodes = nodes;
    for (auto i = 0; i < drawNodes.size(); ++i)
    {
        if (drawNodes[i].get_type() != Variant::DICTIONARY)
        {
            return Result::InvalidBinary;
        }
        const Dictionary node = drawNodes[i]; auto kind = std::string{};
        if (!string(node, "kind", kind) || kind != "proceduralModel")
        {
            continue;
        }
        auto draw = DrawDefinition{}; auto values = std::vector<Input>{};
        if (!string(node, "proceduralModel", draw.model) || output.resources.count(draw.model) == 0 ||
            output.resources.at(draw.model).native.kind != 0 || !inputs(node, values) || values.size() != 4)
        {
            return Result::InvalidBinary;
        }
        const auto material = node.get("proceduralMaterial", Variant{});
        if (material.get_type() != Variant::NIL && (!string(node, "proceduralMaterial", draw.material) ||
            output.resources.count(draw.material) == 0 || output.resources.at(draw.material).native.kind != 2))
        {
            return Result::InvalidBinary;
        }
        constexpr const char* names[] = {"position", "rotation", "scale", "tint"};
        for (auto c = 0u; c < 4; ++c)
        {
            const auto found = std::find_if(values.begin(), values.end(), [&](const auto& input) { return input.id == names[c]; });
            if (found == values.end() || found->width != (c == 3 ? 4u : 3u))
            {
                return Result::InvalidBinary;
            }
            draw.inputs[c] = *found;
        }
        output.draws.push_back(std::move(draw));
    }
    return Result::Ok;
}
struct Resource
{
    procedural_adapter::Identity key;
    std::array<std::shared_ptr<Resource>, 4> retainedDependencies;
    std::unique_ptr<procedural_adapter::Resource> native;
    std::unique_ptr<ProceduralMesh> mesh;
    std::unique_ptr<ProceduralGpu> gpu;
    std::uint64_t revision = 0;
    std::array<std::uint64_t, 4> dependencies{};
};
struct Draw
{
    OwnedRenderingRid instance;
    Ref<ShaderMaterial> material;
    Transform3D local;
};
struct Scope
{
    const Definition* definition = nullptr;
    std::map<std::string, std::shared_ptr<Resource>> resources;
    std::vector<Draw> draws;
    Id container{};
    std::uint64_t instance = 0;
    double started = 0;
    bool visible = true;
};
}

struct ProceduralPackageRenderer::Impl
{
    procedural_godot::Definition rootDefinition;
    std::map<procedural_godot::Id, procedural_godot::Definition> templates;
    std::vector<std::unique_ptr<procedural_godot::Scope>> scopes;
    godot::Node3D* owner = nullptr;
    godot::Ref<godot::ImageTexture> white;
    std::array<godot::Ref<godot::Shader>, 4> shaders;
    std::uint64_t retained = 0;
    bool visible = true;
    auto create(const procedural_godot::Definition& definition, const procedural_godot::Id& container,
        std::uint64_t instance, double time) -> Result;
};
using namespace procedural_godot;

auto ProceduralPackageRenderer::Impl::create(const Definition& definition, const Id& container,
    std::uint64_t instance, double time) -> Result
{
    if (definition.draws.empty() && definition.resources.empty())
    {
        return Result::Ok;
    }
    if (!owner || retained + definition.bytes > 256 * 1024 * 1024)
    {
        return Result::InsufficientWork;
    }
    auto scope = std::make_unique<Scope>(); scope->definition = &definition; scope->container = container;
    scope->instance = instance; scope->started = time;
    for (auto phase = 0u; phase < 3; ++phase)
    {
        for (const auto& [id, data] : definition.resources)
        {
            if (data.native.kind != phase)
            {
                continue;
            }
            static auto cache = procedural_adapter::StaticResourceCache<Resource>{};
            auto key = procedural_adapter::staticIdentity(data.native);
            for (auto slot = 0u; slot < 4 && !key.empty(); ++slot)
            {
                if (!data.textures[slot].empty())
                    procedural_adapter::appendDependency(key, scope->resources.at(data.textures[slot])->key);
            }
            if (auto shared = cache.find(key))
            {
                scope->resources.emplace(id, std::move(shared));
                continue;
            }
            auto retainedResource = std::make_shared<Resource>();
            auto& resource = *retainedResource;
            resource.key = key;
            resource.native = std::make_unique<procedural_adapter::Resource>(data.native);
            const auto result = resource.native->initialize();
            if (result != LIBRA_OK)
            {
                return static_cast<Result>(result);
            }
            if (phase == 0)
            {
                auto view = LibraProceduralModelView{};
                resource.mesh = std::make_unique<ProceduralMesh>();
                if (resource.native->view(view) != LIBRA_OK || !resource.mesh->initialize(view))
                {
                    return Result::InvalidBinary;
                }
            }
            else
            {
                auto textures = std::array<Ref<Texture2DRD>, 4>{};
                for (auto i = 0u; i < 4; ++i)
                {
                    if (!data.textures[i].empty())
                    {
                        resource.retainedDependencies[i] = scope->resources.at(data.textures[i]);
                        textures[i] = resource.retainedDependencies[i]->gpu->texture();
                    }
                }
                resource.gpu = std::make_unique<ProceduralGpu>(); auto error = String{};
                if (!resource.gpu->initialize(data.native, textures, error))
                {
                    UtilityFunctions::push_error(error);
                    return Result::NotReady;
                }
            }
            cache.insert(key, retainedResource);
            scope->resources.emplace(id, std::move(retainedResource));
        }
    }
    auto* server = RenderingServer::get_singleton();
    if (!server)
    {
        return Result::NotReady;
    }
    for (const auto& data : definition.draws)
    {
        auto draw = Draw{};
        const auto* material = data.material.empty() ? nullptr : scope->resources.at(data.material).get();
        const auto& format = material ? material->native->definition() : procedural_adapter::Definition{};
        const auto shaderIndex = format.blend * 2 + (format.doubleSided ? 0 : 1);
        if (shaders[shaderIndex].is_null())
        {
            shaders[shaderIndex].instantiate();
            const auto mode = String("shader_type spatial;\nrender_mode unshaded, shadows_disabled, depth_draw_never, ") +
                (format.blend == 1 ? "blend_add, " : "blend_mix, ") + (format.doubleSided ? "cull_disabled;\n" : "cull_back;\n");
            shaders[shaderIndex]->set_code(mode + String(R"(
uniform sampler2D generated_texture : filter_linear, repeat_enable;
uniform vec4 tint = vec4(1.0);
uniform float emission = 1.0;
void fragment() {
    vec4 value = texture(generated_texture, UV) * COLOR * tint;
    ALBEDO = value.rgb * emission;
    ALPHA = value.a;
}
)"));
        }
        draw.material.instantiate(); draw.material->set_shader(shaders[shaderIndex]);
        draw.material->set_shader_parameter("generated_texture", material ? Ref<Texture2D>(material->gpu->texture()) : Ref<Texture2D>(white));
        draw.material->set_shader_parameter("emission", material ? format.emission : 1.0f);
        draw.instance = OwnedRenderingRid(server->instance_create());
        server->instance_set_base(draw.instance.get(), scope->resources.at(data.model)->mesh->mesh()->get_rid());
        server->instance_geometry_set_material_override(draw.instance.get(), draw.material->get_rid());
        server->instance_set_visible(draw.instance.get(), false);
        scope->draws.push_back(std::move(draw));
    }
    retained += definition.bytes; scopes.push_back(std::move(scope));
    return Result::Ok;
}

ProceduralPackageRenderer::ProceduralPackageRenderer() : impl_(std::make_unique<Impl>()) {}
ProceduralPackageRenderer::~ProceduralPackageRenderer() = default;
auto ProceduralPackageRenderer::prepare(const SpawnablePackage& package) -> Result
{
    auto result = procedural_godot::prepare(package.rootRenderConfiguration, impl_->rootDefinition);
    if (result != Result::Ok)
    {
        return result;
    }
    for (const auto& item : package.templates)
    {
        auto definition = Definition{}; result = procedural_godot::prepare(item.renderConfiguration, definition);
        if (result != Result::Ok)
        {
            return result;
        }
        impl_->templates.emplace(item.containerId, std::move(definition));
    }
    return Result::Ok;
}
auto ProceduralPackageRenderer::attach(Node3D& owner) -> Result
{
    impl_->owner = &owner;
    auto image = Image::create(1, 1, false, Image::FORMAT_RGBA8); image->fill(Color(1, 1, 1, 1));
    impl_->white = ImageTexture::create_from_image(image);
    return impl_->create(impl_->rootDefinition, {}, 0, 0);
}
auto ProceduralPackageRenderer::handleEvent(const SpawnableHostEvent& event) -> Result
{
    auto id = Id{}; std::copy_n(event.containerId, 16, id.begin());
    if (event.kind == SpawnableHostEventKind::Spawn)
    {
        const auto found = impl_->templates.find(id);
        return found == impl_->templates.end() ? Result::InvalidBinary : impl_->create(found->second, id, event.instanceId, event.time);
    }
    if (event.kind == SpawnableHostEventKind::Stop || event.kind == SpawnableHostEventKind::Destroy ||
        event.kind == SpawnableHostEventKind::ForceTerminated)
    {
        for (auto scope = impl_->scopes.begin(); scope != impl_->scopes.end();)
        {
            if (((*scope)->container == id && (*scope)->instance == event.instanceId) ||
                event.kind == SpawnableHostEventKind::ForceTerminated && event.instanceId == 0)
            {
                impl_->retained -= (*scope)->definition->bytes;
                scope = impl_->scopes.erase(scope);
            }
            else
            {
                ++scope;
            }
        }
    }
    return Result::Ok;
}
auto ProceduralPackageRenderer::update(SpawnableHost& host, double time) -> Result
{
    for (auto& scope : impl_->scopes)
    {
        scope->visible = scope->instance != 0 || !host.rootFinished();
        if (!scope->visible)
        {
            continue;
        }
        const auto read = [&](const Id& source, double* values, std::uint8_t& width) -> LibraResult
        {
            return static_cast<LibraResult>(scope->instance == 0 ? host.rootValue(source.data(), values, width) :
                host.childValue(scope->container.data(), scope->instance, source.data(), values, width));
        };
        const auto localTime = std::max(0.0, time - scope->started);
        for (auto phase = 0u; phase < 3; ++phase)
        {
            for (auto& [id, retainedResource] : scope->resources)
            {
                auto& resource = *retainedResource;
                const auto& definition = scope->definition->resources.at(id);
                if (definition.native.kind != phase)
                {
                    continue;
                }
                if (!resource.key.empty() && resource.revision != 0)
                {
                    continue;
                }
                auto dirty = false;
                const auto result = resource.native->update(read, localTime, dirty);
                if (result != LIBRA_OK)
                {
                    return static_cast<Result>(result);
                }
                for (auto slot = 0u; slot < 4; ++slot)
                {
                    if (!definition.textures[slot].empty())
                    {
                        const auto revision = scope->resources.at(definition.textures[slot])->revision;
                        dirty = dirty || revision != resource.dependencies[slot]; resource.dependencies[slot] = revision;
                    }
                }
                if (!dirty)
                {
                    continue;
                }
                if (resource.mesh)
                {
                    auto view = LibraProceduralModelView{};
                    if (resource.native->view(view) != LIBRA_OK || !resource.mesh->update(view))
                    {
                        return Result::InvalidBinary;
                    }
                }
                else if (!resource.gpu->update(resource.native->operations(), localTime))
                {
                    return Result::NotReady;
                }
                ++resource.revision;
            }
        }
        for (auto i = std::size_t{}; i < scope->draws.size(); ++i)
        {
            const auto& definition = scope->definition->draws[i]; auto& draw = scope->draws[i];
            float values[4][4]{};
            for (auto input = 0u; input < 4; ++input)
            {
                const auto result = procedural_adapter::read(definition.inputs[input], read, values[input]);
                if (result != LIBRA_OK)
                {
                    return static_cast<Result>(result);
                }
            }
            auto basis = Basis{};
            basis.set_euler_scale(Vector3(values[1][0], values[1][1], values[1][2]) * .0174532925199433,
                Vector3(values[2][0], values[2][1], values[2][2]));
            draw.local = Transform3D(basis, Vector3(values[0][0], values[0][1], values[0][2]) -
                basis.xform(scope->resources.at(definition.model)->mesh->pivot()));
            draw.material->set_shader_parameter("tint", Color(values[3][0], values[3][1], values[3][2], values[3][3]));
        }
    }
    synchronize(impl_->visible, false);
    return Result::Ok;
}
void ProceduralPackageRenderer::synchronize(bool visible, bool exiting)
{
    impl_->visible = visible;
    auto* server = RenderingServer::get_singleton(); auto* owner = impl_->owner;
    if (!server || !owner)
    {
        return;
    }
    const auto world = owner->get_world_3d();
    for (auto& scope : impl_->scopes)
    {
        for (auto& draw : scope->draws)
        {
            server->instance_set_scenario(draw.instance.get(), !exiting && world.is_valid() ? world->get_scenario() : RID{});
            server->instance_set_visible(draw.instance.get(), !exiting && visible && scope->visible && owner->is_inside_tree() &&
                owner->is_visible_in_tree());
            server->instance_set_transform(draw.instance.get(), owner->get_global_transform() * draw.local);
        }
    }
}
auto ProceduralPackageRenderer::hasDraws() const -> bool
{
    return !impl_->rootDefinition.draws.empty() || std::any_of(impl_->templates.begin(), impl_->templates.end(),
        [](const auto& item) { return !item.second.draws.empty(); });
}
auto ProceduralPackageRenderer::drawCount() const -> std::size_t
{
    auto count = std::size_t{};
    for (const auto& scope : impl_->scopes)
    {
        count += scope->draws.size();
    }
    return count;
}
auto ProceduralPackageRenderer::resolveResourceTexture(const std::uint8_t* container, std::uint64_t instance,
    const std::string& resourceId, godot::Ref<godot::Texture2D>& outTexture,
    int& outBlend, bool& outDoubleSided, float& outEmission) const -> bool
{
    if (!impl_ || resourceId.empty())
    {
        return false;
    }
    auto targetContainer = procedural_godot::Id{};
    if (container)
    {
        std::memcpy(targetContainer.data(), container, targetContainer.size());
    }
    for (const auto& scope : impl_->scopes)
    {
        if (scope->container != targetContainer || scope->instance != instance)
        {
            continue;
        }
        const auto found = scope->resources.find(resourceId);
        if (found == scope->resources.end() || !found->second || !found->second->gpu)
        {
            return false;
        }
        outTexture = godot::Ref<godot::Texture2D>(found->second->gpu->texture());
        const auto& def = found->second->native->definition();
        outBlend = def.blend;
        outDoubleSided = def.doubleSided;
        outEmission = def.emission;
        return outTexture.is_valid();
    }
    return false;
}
}
