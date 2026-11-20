#include "LibraFxGodotFluid2D.h"
#include <godot_cpp/classes/array_mesh.hpp>
#include <godot_cpp/variant/packed_vector2_array.hpp>
#include <godot_cpp/variant/packed_vector3_array.hpp>
#include <godot_cpp/variant/packed_int32_array.hpp>
#include "LibraFluid2DShaderSource.h"
#include "LibraFxGodotRenderTaskQueue.h"
#include <godot_cpp/classes/quad_mesh.hpp>
#include <godot_cpp/classes/rd_shader_source.hpp>
#include <godot_cpp/classes/rd_shader_spirv.hpp>
#include <godot_cpp/classes/rd_texture_format.hpp>
#include <godot_cpp/classes/rd_texture_view.hpp>
#include <godot_cpp/classes/rd_uniform.hpp>
#include <godot_cpp/classes/ref_counted.hpp>
#include <godot_cpp/classes/rendering_device.hpp>
#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/classes/shader.hpp>
#include <godot_cpp/classes/shader_material.hpp>
#include <godot_cpp/classes/texture2drd.hpp>
#include <godot_cpp/classes/thread.hpp>
#include <godot_cpp/classes/viewport.hpp>
#include <godot_cpp/classes/world3d.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/core/memory.hpp>
#include <godot_cpp/variant/callable_method_pointer.hpp>
#include <godot_cpp/variant/packed_byte_array.hpp>
#ifdef LIBERAFX_GODOT_TEST_HOOKS
#include <godot_cpp/variant/packed_float32_array.hpp>
#endif
#include <godot_cpp/variant/typed_array.hpp>
#include <godot_cpp/variant/vector2.hpp>
#include <godot_cpp/variant/vector3.hpp>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <functional>
#include <limits>
#include <mutex>
#include <utility>

namespace liberafx::godot_adapter {
namespace detail {

using namespace godot;
using liberafx::gpu::Fluid2DDispatch;
using liberafx::gpu::Fluid2DField;
using liberafx::gpu::Fluid2DFrame;
using liberafx::gpu::Fluid2DPass;
using liberafx::gpu::Fluid2DPlan;
using liberafx::gpu::Fluid2DSettings;
using liberafx::gpu::Fluid2DUniforms;

constexpr auto passCount = 13u;
constexpr auto fieldCount = 7u;
constexpr auto uniformBytes = sizeof(Fluid2DUniforms);
constexpr auto maximumActiveFluids = 16u;
constexpr auto fluidSpatialSource = R"(
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, depth_draw_never, blend_premul_alpha;
uniform sampler2D fluid_texture : filter_linear, repeat_disable;
uniform sampler2D fluid_fields : filter_linear, repeat_disable;
uniform int model_id = 0;
uniform float initial_depth = 0.25;
uniform float ignition_temperature = 0.5;
uniform float height_scale = 1.0;
uniform vec2 domain_size = vec2(1.0);
uniform vec2 field_texel = vec2(0.0078125);

float depth_at(vec2 uv) { return textureLod(fluid_fields, clamp(uv, vec2(0), vec2(1)), 0.0).b; }
void vertex() {
    if (model_id == 2) {
        vec2 uv = vec2(UV.x, 1.0 - UV.y);
        VERTEX.y += (depth_at(uv) - initial_depth) * height_scale;
        float du = (depth_at(uv + vec2(field_texel.x, 0)) - depth_at(uv - vec2(field_texel.x, 0))) / (2.0 * field_texel.x * domain_size.x);
        float dv = (depth_at(uv + vec2(0, field_texel.y)) - depth_at(uv - vec2(0, field_texel.y))) / (2.0 * field_texel.y * domain_size.y);
        NORMAL = normalize(vec3(-du, 1.0, -dv));
    }
}

void fragment() {
    vec2 uv = vec2(UV.x, 1.0 - UV.y);
    vec4 dye = texture(fluid_texture, uv);
    ALBEDO = dye.rgb;
    ALPHA = dye.a;
    if (model_id == 3) {
        vec4 field = texture(fluid_fields, uv);
        float heat = max(field.a - ignition_temperature, 0.0);
        vec3 fire = clamp(vec3(heat * 2.0, heat * 0.7, heat * 0.2), vec3(0), vec3(1));
        ALBEDO = vec3(0.08, 0.08, 0.09) * dye.a;
        EMISSION = fire * (1.0 + min(heat, 8.0)) * dye.a;
    } else if (model_id == 1 || model_id == 2) {
        vec4 field = texture(fluid_fields, uv);
        vec3 normal = normalize(NORMAL);
        float fresnel = 0.02 + 0.98 * pow(1.0 - clamp(abs(dot(VIEW, normal)), 0.0, 1.0), 5.0);
        vec3 tint = dye.rgb / max(dye.a, 0.000001);
        float light = 0.45 + 0.55 * clamp(dot(normal, normalize(vec3(0.3, 0.8, 0.5))), 0.0, 1.0);
        vec3 water = mix(tint * light, vec3(0.45, 0.65, 0.85), fresnel);
        float edge = model_id == 1 ? clamp(fwidth(field.b) * 4.0, 0.0, 1.0) : clamp(length(dFdx(normal)) + length(dFdy(normal)), 0.0, 1.0);
        float foam = clamp(edge * (0.3 + length(field.rg) * 0.2), 0.0, 1.0);
        water = mix(water, vec3(0.9, 0.96, 1.0), foam);
        ALBEDO = water * dye.a;
    }
}
)";

auto error(String& target, const String& message, Result result = Result::NotReady) -> Result
{
    target = message;
    return result;
}

auto passIndex(Fluid2DPass pass) -> std::size_t
{
    return static_cast<std::size_t>(pass);
}

auto fieldIndex(Fluid2DField field) -> int
{
    return static_cast<int>(field);
}

auto fieldRid(const std::array<RID, fieldCount>& fields, Fluid2DField field, const RID& dummy) -> RID
{
    const auto index = fieldIndex(field);
    return index >= 0 && static_cast<std::size_t>(index) < fields.size()
        ? fields[static_cast<std::size_t>(index)] : dummy;
}

auto storageUniform(std::int32_t binding, const RID& resource) -> Ref<RDUniform>
{
    auto uniform = Ref<RDUniform>{};
    uniform.instantiate();
    uniform->set_uniform_type(RenderingDevice::UNIFORM_TYPE_STORAGE_BUFFER);
    uniform->set_binding(binding);
    uniform->add_id(resource);
    return uniform;
}

auto bufferUniform(std::int32_t binding, const RID& resource) -> Ref<RDUniform>
{
    auto uniform = Ref<RDUniform>{};
    uniform.instantiate();
    uniform->set_uniform_type(RenderingDevice::UNIFORM_TYPE_UNIFORM_BUFFER);
    uniform->set_binding(binding);
    uniform->add_id(resource);
    return uniform;
}

auto imageUniform(std::int32_t binding, const RID& resource) -> Ref<RDUniform>
{
    auto uniform = Ref<RDUniform>{};
    uniform.instantiate();
    uniform->set_uniform_type(RenderingDevice::UNIFORM_TYPE_IMAGE);
    uniform->set_binding(binding);
    uniform->add_id(resource);
    return uniform;
}

struct FluidResources final {
    std::array<RID, fieldCount> fields{};
    RID output;
    RID fieldOutput;
    RID colorOutput;
    RID uniforms;
    std::vector<RID> uniformSets;
    std::shared_ptr<double> simulationTime = std::make_shared<double>(0);
};

class FluidRenderBridge final : public RefCounted {
    GDCLASS(FluidRenderBridge, RefCounted)

public:
    FluidRenderBridge()
    {
        queue_.instantiate();
    }

    static void _bind_methods()
    {
    }

    auto initialize(String& detail) -> Result
    {
        if (!Thread::is_main_thread()) {
            return error(detail, "Godot Fluid2D initialization must run on the main thread",
                Result::InvalidArgument);
        }
        auto result = std::make_shared<Result>(Result::NotReady);
        auto message = std::make_shared<String>();
        enqueue([this, result, message]() {
            *result = initializeOnRenderThread(*message);
        });
        if (auto* server = RenderingServer::get_singleton()) {
            server->force_sync();
        }
        detail = *message;
        return *result;
    }

    auto allocate(const Fluid2DSettings& settings, const Fluid2DPlan& plan,
        FluidResources& output, String& error) -> Result
    {
        auto result = std::make_shared<Result>(Result::NotReady);
        auto message = std::make_shared<String>();
        auto resources = std::make_shared<FluidResources>();
        enqueue([this, settings, plan, resources, result, message]() {
            *result = allocateOnRenderThread(settings, plan, *resources, *message);
        });
        if (auto* server = RenderingServer::get_singleton()) {
            server->force_sync();
        }
        error = *message;
        if (*result == Result::Ok) {
            output = std::move(*resources);
        }
        return *result;
    }

#ifdef LIBERAFX_GODOT_TEST_HOOKS
    auto testReadFields(const FluidResources& resources, std::uint64_t fieldBytes,
        std::array<PackedByteArray, fieldCount>& output, String& detail) -> Result
    {
        auto result = std::make_shared<Result>(Result::NotReady);
        auto message = std::make_shared<String>();
        auto fields = std::make_shared<std::array<PackedByteArray, fieldCount>>();
        enqueue([this, resources, fieldBytes, result, message, fields]() {
            if (!device_) {
                *message = "Global Fluid2D RenderingDevice is unavailable for test readback";
                return;
            }
            for (auto index = std::size_t{}; index < fieldCount; ++index) {
                (*fields)[index] = device_->buffer_get_data(resources.fields[index]);
                if (static_cast<std::uint64_t>((*fields)[index].size()) != fieldBytes) {
                    *message = "Global Fluid2D field readback returned an unexpected byte count";
                    return;
                }
            }
            *result = Result::Ok;
        });
        if (auto* server = RenderingServer::get_singleton()) {
            server->force_sync();
        } else {
            return error(detail, "Godot RenderingServer is unavailable for Fluid2D test readback");
        }
        detail = *message;
        if (*result == Result::Ok) {
            output = std::move(*fields);
        }
        return *result;
    }

    auto testRebuildUniformSets(FluidResources& resources, const Fluid2DPlan& plan,
        String& detail) -> Result
    {
        auto result = std::make_shared<Result>(Result::NotReady);
        auto message = std::make_shared<String>();
        enqueue([this, &resources, &plan, result, message]() {
            if (!device_) {
                *message = "Global Fluid2D RenderingDevice is unavailable";
                return;
            }
            auto replacement = std::vector<RID>{};
            replacement.reserve(plan.count);
            for (auto index = 0u; index < plan.count; ++index) {
                const auto& dispatch = plan.passes[index];
                const auto pass = passIndex(dispatch.pass);
                auto bindings = TypedArray<RDUniform>{};
                bindings.append(storageUniform(0, fieldRid(resources.fields, dispatch.read0, dummy_)));
                bindings.append(storageUniform(1, fieldRid(resources.fields, dispatch.read1, dummy_)));
                bindings.append(storageUniform(2, fieldRid(resources.fields, dispatch.write0, dummy_)));
                bindings.append(storageUniform(3, fieldRid(resources.fields, dispatch.write1, dummy_)));
                bindings.append(bufferUniform(4, resources.uniforms));
                bindings.append(imageUniform(5, dispatch.pass == Fluid2DPass::RenderFields ? resources.fieldOutput : dispatch.pass == Fluid2DPass::RenderColor ? resources.colorOutput : resources.output));
                const auto set = device_->uniform_set_create(bindings, shaders_[pass], 0);
                if (!set.is_valid()) {
                    for (const auto& created : replacement) {
                        device_->free_rid(created);
                    }
                    *message = "Could not rebuild Fluid2D test pressure iteration bindings";
                    return;
                }
                replacement.push_back(set);
            }
            for (const auto& prior : resources.uniformSets) {
                if (prior.is_valid()) {
                    device_->free_rid(prior);
                }
            }
            resources.uniformSets = std::move(replacement);
            *result = Result::Ok;
        });
        if (auto* server = RenderingServer::get_singleton()) {
            server->force_sync();
        } else {
            return error(detail, "Godot RenderingServer is unavailable for Fluid2D test setup");
        }
        detail = *message;
        return *result;
    }

    auto testZeroVelocity(const FluidResources& resources, std::uint64_t fieldBytes,
        String& detail) -> Result
    {
        auto zero = PackedByteArray{};
        if (zero.resize(static_cast<std::int64_t>(fieldBytes)) != OK) {
            return error(detail, "Could not allocate Fluid2D test velocity reset", Result::InsufficientWork);
        }
        zero.fill(0);
        auto result = std::make_shared<Result>(Result::NotReady);
        auto message = std::make_shared<String>();
        enqueue([this, resources, zero, result, message]() {
            if (!device_) {
                *message = "Global Fluid2D RenderingDevice is unavailable";
                return;
            }
            for (const auto field : {Fluid2DField::Velocity, Fluid2DField::VelocityScratch}) {
                if (device_->buffer_update(resources.fields[static_cast<std::size_t>(field)],
                        0, static_cast<std::uint32_t>(zero.size()), zero) != OK) {
                    *message = "Could not clear Fluid2D test velocity";
                    return;
                }
            }
            *result = Result::Ok;
        });
        if (auto* server = RenderingServer::get_singleton()) {
            server->force_sync();
        } else {
            return error(detail, "Godot RenderingServer is unavailable for Fluid2D test setup");
        }
        detail = *message;
        return *result;
    }
#endif

    void dispatch(const FluidResources& resources, const Fluid2DSettings& settings,
        const Fluid2DPlan& plan, const Fluid2DFrame& frame)
    {
        enqueue([this, resources, settings, plan, frame]() {
            dispatchOnRenderThread(resources, settings, plan, frame);
        });
    }

    auto release(FluidResources resources, String& error) -> Result
    {
        auto result = std::make_shared<Result>(Result::Ok);
        auto message = std::make_shared<String>();
        enqueue([this, resources = std::move(resources), result, message]() mutable {
            releaseOnRenderThread(resources);
            *result = Result::Ok;
            *message = String{};
        });
        if (auto* server = RenderingServer::get_singleton()) {
            server->force_sync();
        }
        error = *message;
        return *result;
    }

    void shutdown()
    {
        enqueue([this]() {
            if (!device_) {
                return;
            }
            for (auto index = std::size_t{}; index < passCount; ++index) {
                if (pipelines_[index].is_valid()) {
                    device_->free_rid(pipelines_[index]);
                    pipelines_[index] = RID{};
                }
                if (shaders_[index].is_valid()) {
                    device_->free_rid(shaders_[index]);
                    shaders_[index] = RID{};
                }
            }
            if (dummy_.is_valid()) {
                device_->free_rid(dummy_);
                dummy_ = RID{};
            }
        });
        if (auto* server = RenderingServer::get_singleton()) {
            server->force_sync();
        }
    }

    auto hasError(String& error) -> bool
    {
        std::lock_guard<std::mutex> lock(errorMutex_);
        if (asyncError_.is_empty()) {
            return false;
        }
        error = asyncError_;
        asyncError_ = String{};
        return true;
    }

private:
    using Task = std::function<void()>;

    void enqueue(Task task)
    {
        if (!queue_->enqueue(std::move(task)))
        {
            recordError("Godot RenderingServer is unavailable");
        }
    }

    void recordError(const String& detail)
    {
        std::lock_guard<std::mutex> lock(errorMutex_);
        if (asyncError_.is_empty()) {
            asyncError_ = detail;
        }
    }

    auto initializeOnRenderThread(String& detail) -> Result
    {
        auto* server = RenderingServer::get_singleton();
        device_ = server ? server->get_rendering_device() : nullptr;
        if (!device_) {
            detail = "Godot GPU fluid requires a RenderingDevice renderer (Forward+ or Mobile); "
                "Compatibility and headless/null devices are unsupported";
            return Result::NotReady;
        }
        for (auto pass = std::size_t{}; pass < passCount; ++pass) {
            auto source = Ref<RDShaderSource>{};
            source.instantiate();
            source->set_stage_source(RenderingDevice::SHADER_STAGE_COMPUTE,
                String::utf8(liberafx::gpu::fluid2DComputeSource(static_cast<Fluid2DPass>(pass))));
            const auto spirv = device_->shader_compile_spirv_from_source(source);
            if (spirv.is_null()) {
                detail = "Godot could not compile the trusted Fluid2D compute shader";
                releasePipelineResources();
                return Result::NotReady;
            }
            const auto compileError = spirv->get_stage_compile_error(RenderingDevice::SHADER_STAGE_COMPUTE);
            if (!compileError.is_empty()) {
                detail = "Fluid2D compute shader: " + compileError;
                releasePipelineResources();
                return Result::NotReady;
            }
            shaders_[pass] = device_->shader_create_from_spirv(spirv, "LibraFX Fluid2D pass");
            if (!shaders_[pass].is_valid()) {
                detail = "Godot could not create a Fluid2D compute shader";
                releasePipelineResources();
                return Result::NotReady;
            }
            pipelines_[pass] = device_->compute_pipeline_create(shaders_[pass]);
            if (!pipelines_[pass].is_valid()) {
                detail = "Godot could not create a Fluid2D compute pipeline";
                releasePipelineResources();
                return Result::NotReady;
            }
        }
        auto zero = PackedByteArray{};
        if (zero.resize(16) != OK) {
            detail = "Could not allocate Fluid2D dummy storage";
            releasePipelineResources();
            return Result::InsufficientWork;
        }
        zero.fill(0);
        dummy_ = device_->storage_buffer_create(16, zero);
        if (!dummy_.is_valid()) {
            detail = "Could not create Fluid2D dummy storage";
            releasePipelineResources();
            return Result::NotReady;
        }
        return Result::Ok;
    }

    auto allocateOnRenderThread(const Fluid2DSettings& settings, const Fluid2DPlan& plan,
        FluidResources& resources, String& detail) -> Result
    {
        if (!device_) {
            return error(detail, "Godot Fluid2D RenderingDevice is not initialized");
        }
        const auto cells = static_cast<std::uint64_t>(settings.width) * settings.height;
        const auto fieldBytes = cells * 16;
        if (fieldBytes > std::numeric_limits<std::uint32_t>::max()) {
            return error(detail, "Fluid2D field allocation exceeds Godot's buffer limit",
                Result::InsufficientWork);
        }
        auto zero = PackedByteArray{};
        if (zero.resize(static_cast<std::int64_t>(fieldBytes)) != OK) {
            return error(detail, "Could not allocate zeroed Fluid2D field data", Result::InsufficientWork);
        }
        zero.fill(0);
        for (auto& field : resources.fields) {
            field = device_->storage_buffer_create(static_cast<std::uint32_t>(fieldBytes), zero);
            if (!field.is_valid()) {
                releaseOnRenderThread(resources);
                return error(detail, "Could not allocate a Fluid2D field buffer",
                    Result::InsufficientWork);
            }
        }

        const auto textureUsage = RenderingDevice::TEXTURE_USAGE_SAMPLING_BIT |
            RenderingDevice::TEXTURE_USAGE_STORAGE_BIT;
        if (!device_->texture_is_format_supported_for_usage(
                RenderingDevice::DATA_FORMAT_R16G16B16A16_SFLOAT, textureUsage)) {
            releaseOnRenderThread(resources);
            return error(detail, "Godot's RenderingDevice cannot sample and store RGBA16F Fluid2D output",
                Result::UnsupportedBinary);
        }
        auto format = Ref<RDTextureFormat>{};
        format.instantiate();
        format->set_texture_type(RenderingDevice::TEXTURE_TYPE_2D);
        format->set_format(RenderingDevice::DATA_FORMAT_R16G16B16A16_SFLOAT);
        format->set_width(settings.width);
        format->set_height(settings.height);
        format->set_depth(1);
        format->set_array_layers(1);
        format->set_mipmaps(1);
        format->set_samples(RenderingDevice::TEXTURE_SAMPLES_1);
        format->set_usage_bits(textureUsage);
        auto initialImage = PackedByteArray{};
        const auto imageBytes = cells * 8;
        if (initialImage.resize(static_cast<std::int64_t>(imageBytes)) != OK) {
            releaseOnRenderThread(resources);
            return error(detail, "Could not allocate Fluid2D output texture data",
                Result::InsufficientWork);
        }
        initialImage.fill(0);
        auto view = Ref<RDTextureView>{};
        view.instantiate();
        auto textureData = TypedArray<PackedByteArray>{};
        textureData.append(initialImage);
        resources.output = device_->texture_create(format, view, textureData);
        if (!resources.output.is_valid()) {
            releaseOnRenderThread(resources);
            return error(detail, "Godot could not create a sampleable RGBA16F Fluid2D output texture "
                "with storage usage", Result::UnsupportedBinary);
        }
        if (!device_->texture_is_format_supported_for_usage(RenderingDevice::DATA_FORMAT_R32G32B32A32_SFLOAT, textureUsage))
        {
            releaseOnRenderThread(resources);
            return error(detail, "Sampled RGBA32Float fluid fields are unsupported", Result::UnsupportedBinary);
        }
        format->set_format(RenderingDevice::DATA_FORMAT_R32G32B32A32_SFLOAT);
        if (initialImage.resize(static_cast<std::int64_t>(cells * 16)) != OK)
        {
            releaseOnRenderThread(resources);
            return error(detail, "Could not stage fluid field output", Result::InsufficientWork);
        }
        initialImage.fill(0);
        textureData.clear(); textureData.append(initialImage);
        resources.fieldOutput = device_->texture_create(format, view, textureData);
        if (!resources.fieldOutput.is_valid())
        {
            releaseOnRenderThread(resources);
            return error(detail, "Could not allocate fluid field output", Result::NotReady);
        }
        format->set_format(RenderingDevice::DATA_FORMAT_R16G16B16A16_SFLOAT);
        initialImage.resize(static_cast<std::int64_t>(cells * 8)); initialImage.fill(0);
        textureData.clear(); textureData.append(initialImage);
        resources.colorOutput = device_->texture_create(format, view, textureData);
        if (!resources.colorOutput.is_valid())
        {
            releaseOnRenderThread(resources);
            return error(detail, "Could not allocate straight-alpha fluid color", Result::NotReady);
        }

        auto initialUniforms = PackedByteArray{};
        if (initialUniforms.resize(uniformBytes) != OK) {
            releaseOnRenderThread(resources);
            return error(detail, "Could not allocate Fluid2D uniform data", Result::InsufficientWork);
        }
        const auto modelUniforms = liberafx::gpu::makeFluid2DUniforms(settings, 0);
        std::memcpy(initialUniforms.ptrw(), &modelUniforms, uniformBytes);
        resources.uniforms = device_->uniform_buffer_create(uniformBytes, initialUniforms);
        if (!resources.uniforms.is_valid()) {
            releaseOnRenderThread(resources);
            return error(detail, "Could not allocate Fluid2D uniform buffer", Result::NotReady);
        }
        resources.uniformSets.reserve(plan.count);
        for (auto index = 0u; index < plan.count; ++index) {
            const auto& dispatch = plan.passes[index];
            const auto pass = passIndex(dispatch.pass);
            auto bindings = TypedArray<RDUniform>{};
            bindings.append(storageUniform(0, fieldRid(resources.fields, dispatch.read0, dummy_)));
            bindings.append(storageUniform(1, fieldRid(resources.fields, dispatch.read1, dummy_)));
            bindings.append(storageUniform(2, fieldRid(resources.fields, dispatch.write0, dummy_)));
            bindings.append(storageUniform(3, fieldRid(resources.fields, dispatch.write1, dummy_)));
            bindings.append(bufferUniform(4, resources.uniforms));
            bindings.append(imageUniform(5, dispatch.pass == Fluid2DPass::RenderFields ? resources.fieldOutput : dispatch.pass == Fluid2DPass::RenderColor ? resources.colorOutput : resources.output));
            const auto set = device_->uniform_set_create(bindings, shaders_[pass], 0);
            if (!set.is_valid()) {
                releaseOnRenderThread(resources);
                return error(detail, "Could not bind a Fluid2D pass to its native fields",
                    Result::NotReady);
            }
            resources.uniformSets.push_back(set);
        }
        if (settings.model == 1 || settings.model == 2)
        {
            const auto initialize = passIndex(Fluid2DPass::InitializeWater);
            const auto render = passIndex(Fluid2DPass::RenderDye);
            const auto renderFields = passIndex(Fluid2DPass::RenderFields);
            const auto renderColor = passIndex(Fluid2DPass::RenderColor);
            auto initializationSets = std::array<RID, 4>{};
            for (auto index = 0u; index < 4; ++index)
            {
                auto bindings = TypedArray<RDUniform>{};
                bindings.append(storageUniform(0, index == 1 || index == 3 ? resources.fields[2] : index == 2 ? resources.fields[0] : dummy_));
                bindings.append(storageUniform(1, index == 2 ? resources.fields[2] : dummy_));
                bindings.append(storageUniform(2, index == 0 ? resources.fields[2] : dummy_));
                bindings.append(storageUniform(3, dummy_));
                bindings.append(bufferUniform(4, resources.uniforms));
                bindings.append(imageUniform(5, index == 2 ? resources.fieldOutput : index == 3 ? resources.colorOutput : resources.output));
                initializationSets[index] = device_->uniform_set_create(bindings, shaders_[index == 0 ? initialize : index == 1 ? render : index == 2 ? renderFields : renderColor], 0);
                if (!initializationSets[index].is_valid())
                {
                    for (const auto& set : initializationSets)
                    {
                        if (set.is_valid()) device_->free_rid(set);
                    }
                    releaseOnRenderThread(resources);
                    return error(detail, "Could not initialize native water storage", Result::NotReady);
                }
            }
            const auto list = device_->compute_list_begin();
            for (auto index = 0u; index < 4; ++index)
            {
                device_->compute_list_bind_compute_pipeline(list, pipelines_[index == 0 ? initialize : index == 1 ? render : index == 2 ? renderFields : renderColor]);
                device_->compute_list_bind_uniform_set(list, initializationSets[index], 0);
                device_->compute_list_dispatch(list, (settings.width + 7) / 8, (settings.height + 7) / 8, 1);
                device_->compute_list_add_barrier(list);
            }
            device_->compute_list_end();
            for (const auto& set : initializationSets)
            {
                device_->free_rid(set);
            }
        }
        return Result::Ok;
    }

    void dispatchOnRenderThread(const FluidResources& resources, const Fluid2DSettings& settings,
        const Fluid2DPlan& plan, const Fluid2DFrame& frame)
    {
        if (!device_ || frame.substeps == 0) {
            return;
        }
        for (auto substep = 0u; substep < frame.substeps; ++substep) {
            const auto uniforms = liberafx::gpu::makeFluid2DUniforms(settings, frame.stepDelta, *resources.simulationTime);
            *resources.simulationTime += frame.stepDelta;
            auto bytes = PackedByteArray{};
            if (bytes.resize(uniformBytes) != OK) {
                recordError("Could not stage Fluid2D step uniforms");
                return;
            }
            std::memcpy(bytes.ptrw(), &uniforms, uniformBytes);
            if (device_->buffer_update(resources.uniforms, 0, uniformBytes, bytes) != OK) {
                recordError("Could not upload Fluid2D step uniforms");
                return;
            }
            const auto list = device_->compute_list_begin();
            if (list < 0) {
                recordError("Godot could not begin a Fluid2D compute list");
                return;
            }
            for (auto index = 0u; index < plan.count; ++index) {
                const auto pass = passIndex(plan.passes[index].pass);
                device_->compute_list_bind_compute_pipeline(list, pipelines_[pass]);
                device_->compute_list_bind_uniform_set(list, resources.uniformSets[index], 0);
                device_->compute_list_dispatch(list, (settings.width + 7) / 8,
                    (settings.height + 7) / 8, 1);
                device_->compute_list_add_barrier(list);
            }
            device_->compute_list_end();
        }
    }

    void releaseOnRenderThread(FluidResources& resources)
    {
        if (!device_) {
            resources = FluidResources{};
            return;
        }
        for (const auto& uniformSet : resources.uniformSets) {
            if (uniformSet.is_valid()) {
                device_->free_rid(uniformSet);
            }
        }
        resources.uniformSets.clear();
        if (resources.colorOutput.is_valid())
        {
            device_->free_rid(resources.colorOutput); resources.colorOutput = RID{};
        }
        if (resources.fieldOutput.is_valid())
        {
            device_->free_rid(resources.fieldOutput);
            resources.fieldOutput = RID{};
        }
        if (resources.uniforms.is_valid()) {
            device_->free_rid(resources.uniforms);
            resources.uniforms = RID{};
        }
        if (resources.output.is_valid()) {
            device_->free_rid(resources.output);
            resources.output = RID{};
        }
        for (auto& field : resources.fields) {
            if (field.is_valid()) {
                device_->free_rid(field);
                field = RID{};
            }
        }
    }

    void releasePipelineResources()
    {
        if (!device_) {
            return;
        }
        for (auto index = std::size_t{}; index < passCount; ++index) {
            if (pipelines_[index].is_valid()) {
                device_->free_rid(pipelines_[index]);
                pipelines_[index] = RID{};
            }
            if (shaders_[index].is_valid()) {
                device_->free_rid(shaders_[index]);
                shaders_[index] = RID{};
            }
        }
        if (dummy_.is_valid()) {
            device_->free_rid(dummy_);
            dummy_ = RID{};
        }
    }

    Ref<GpuRenderTaskQueue> queue_;
    std::mutex errorMutex_;
    String asyncError_;
    RenderingDevice* device_ = nullptr; // The renderer owns the global device.
    std::array<RID, passCount> shaders_{};
    std::array<RID, passCount> pipelines_{};
    RID dummy_;
};

struct FluidState final {
    Fluid2DProgram program;
    Fluid2DPlan plan{};
    FluidResources resources;
    Ref<Texture2DRD> texture;
    Ref<Texture2DRD> fieldTexture;
    Ref<Texture2DRD> colorTexture;
    Ref<Mesh> mesh;
    Ref<ShaderMaterial> material;
    Transform3D drawTransform;
    bool drawMatrixIsWorld = false; // Exported GPU Draw Matrices are already world-space.
    RID drawItem;
};

auto transformFromRows(const std::array<std::array<double, 4>, 4>& matrix,
    Transform3D& output, String& detail) -> Result
{
    for (const auto& row : matrix) {
        for (const auto value : row) {
            if (!std::isfinite(value) || std::abs(value) > std::numeric_limits<float>::max()) {
                return error(detail, "Fluid2D transform contains a nonfinite or out-of-range value",
                    Result::NonFinite);
            }
        }
    }
    if (std::abs(matrix[3][0]) > 1e-5 || std::abs(matrix[3][1]) > 1e-5 ||
        std::abs(matrix[3][2]) > 1e-5 || std::abs(matrix[3][3] - 1.0) > 1e-5) {
        return error(detail, "Fluid2D transform must be affine", Result::InvalidArgument);
    }
    const auto real = [](double value) { return static_cast<real_t>(value); };
    const auto basis = Basis(
        Vector3(real(matrix[0][0]), real(matrix[1][0]), real(matrix[2][0])),
        Vector3(real(matrix[0][1]), real(matrix[1][1]), real(matrix[2][1])),
        Vector3(real(matrix[0][2]), real(matrix[1][2]), real(matrix[2][2])));
    output = Transform3D(basis,
        Vector3(real(matrix[0][3]), real(matrix[1][3]), real(matrix[2][3])));
    return Result::Ok;
}

auto transformFor(SpawnableHost& host, const Fluid2DProgram& program,
    Transform3D& output, String& detail) -> Result
{
    auto matrix = std::array<std::array<double, 4>, 4>{};
    if (program.hasMatrix) {
        for (auto row = 0u; row < 4; ++row) {
            auto width = std::uint8_t{};
            const auto result = program.instanceId == 0
                ? host.rootValue(program.matrixRows[row].data(), matrix[row].data(), width)
                : host.childValue(program.containerId.data(), program.instanceId,
                    program.matrixRows[row].data(), matrix[row].data(), width);
            if (result != Result::Ok || width != 4) {
                return error(detail, "Could not read the Fluid2D transform from its execution scope",
                    Result::InvalidValueType);
            }
        }
    } else {
        matrix = {{
            {{1, 0, 0, 0}},
            {{0, 1, 0, 0}},
            {{0, 0, 1, 0}},
            {{0, 0, 0, 1}}
        }};
    }
    return transformFromRows(matrix, output, detail);
}

}
using namespace detail;

void registerFluid2DClasses()
{
    ClassDB::register_internal_class<GpuRenderTaskQueue>();
    ClassDB::register_internal_class<FluidRenderBridge>();
    ClassDB::register_internal_class<FluidRenderScopeNode>();
}

void FluidRenderScopeNode::setRenderer(PackageFluid2DRenderer* renderer)
{
    renderer_ = renderer;
    set_notify_transform(renderer != nullptr);
    set_process_mode(Node::PROCESS_MODE_ALWAYS);
    set_process_internal(renderer != nullptr);
}

void FluidRenderScopeNode::_notification(int what)
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

struct PackageFluid2DRenderer::Impl {
    Ref<FluidRenderBridge> bridge;
    std::vector<Fluid2DTemplate> templates;
    std::vector<std::unique_ptr<FluidState>> active;
    Ref<Shader> shader;
    std::uint64_t rootNodeId = 0;
    RenderingServer* server = nullptr;
    std::uint64_t reservedBytes = 0;

    auto createState(const Fluid2DProgram& program, std::unique_ptr<FluidState>& output,
        String& detail) -> Result
    {
        const auto bytes = static_cast<std::uint64_t>(program.settings.width) * program.settings.height * 144;
        if (bytes > 256ull * 1024 * 1024 - reservedBytes)
        {
            return error(detail, "Active Fluid2D storage exceeds the 256 MiB effect budget", Result::InsufficientWork);
        }
        auto state = std::make_unique<FluidState>();
        state->program = program;
        state->drawMatrixIsWorld = program.hasMatrix;
        if (!liberafx::gpu::makeFluid2DPlan(program.settings, state->plan)) {
            return error(detail, "Invalid Fluid2D settings or dispatch plan", Result::InvalidArgument);
        }
        const auto result = bridge->allocate(program.settings, state->plan, state->resources, detail);
        if (result != Result::Ok) {
            return result;
        }
        state->texture.instantiate();
        state->texture->set_texture_rd_rid(state->resources.output);
        state->fieldTexture.instantiate();
        state->fieldTexture->set_texture_rd_rid(state->resources.fieldOutput);
        state->colorTexture.instantiate(); state->colorTexture->set_texture_rd_rid(state->resources.colorOutput);
        reservedBytes += bytes;
        output = std::move(state);
        return Result::Ok;
    }

    auto attachState(FluidRenderScopeNode& root, FluidState& state, String& detail) -> Result
    {
        if (!state.program.automaticPlane)
        {
            return Result::Ok;
        }
        if (state.drawItem.is_valid()) {
            return error(detail, "Fluid2D draw item is already attached", Result::Busy);
        }
        const auto width = state.program.settings.domainWidth * state.program.sizeScale;
        const auto height = state.program.settings.domainHeight * state.program.sizeScale;
        if (!std::isfinite(width) || !std::isfinite(height) ||
            width <= 0 || height <= 0 ||
            width > std::numeric_limits<float>::max() || height > std::numeric_limits<float>::max()) {
            return error(detail, "Fluid2D output size is outside Godot's 3D mesh range",
                Result::InvalidArgument);
        }
        state.material.instantiate();
        if (state.material.is_null() || shader.is_null()) {
            return error(detail, "Godot could not create the Fluid2D spatial material and plane");
        }
        state.material->set_shader(shader);
        state.material->set_shader_parameter("fluid_texture", state.texture);
        state.material->set_shader_parameter("fluid_fields", state.fieldTexture);
        state.material->set_shader_parameter("model_id", static_cast<int>(state.program.settings.model));
        state.material->set_shader_parameter("initial_depth", state.program.settings.initialWaterDepth);
        state.material->set_shader_parameter("ignition_temperature", state.program.settings.ignitionTemperature);
        state.material->set_shader_parameter("height_scale", state.program.sizeScale);
        state.material->set_shader_parameter("domain_size", Vector2(state.program.settings.domainWidth, state.program.settings.domainHeight));
        state.material->set_shader_parameter("field_texel", Vector2(1.0 / state.program.settings.width, 1.0 / state.program.settings.height));
        if (state.program.settings.model == 2)
        {
            auto mesh = Ref<ArrayMesh>{}; mesh.instantiate();
            auto vertices = PackedVector3Array{}; auto uv = PackedVector2Array{}; auto indices = PackedInt32Array{};
            const auto columns = std::min(state.program.settings.width - 1, 128u);
            const auto rows = std::min(state.program.settings.height - 1, 128u);
            for (auto y = 0u; y <= rows; ++y)
                for (auto x = 0u; x <= columns; ++x)
                {
                    const auto u = static_cast<real_t>(x) / columns, v = static_cast<real_t>(y) / rows;
                    vertices.append(Vector3((u - .5) * width, 0, (v - .5) * height));
                    uv.append(Vector2(u, 1.0 - v));
                }
            for (auto y = 0u; y < rows; ++y)
                for (auto x = 0u; x < columns; ++x)
                {
                    const auto index = y * (columns + 1) + x;
                    for (const auto vertex : {index, index + columns + 1, index + 1, index + 1, index + columns + 1, index + columns + 2})
                    {
                        indices.append(static_cast<int>(vertex));
                    }
                }
            auto arrays = Array{}; arrays.resize(Mesh::ARRAY_MAX);
            arrays[Mesh::ARRAY_VERTEX] = vertices; arrays[Mesh::ARRAY_TEX_UV] = uv; arrays[Mesh::ARRAY_INDEX] = indices;
            mesh->add_surface_from_arrays(Mesh::PRIMITIVE_TRIANGLES, arrays);
            mesh->surface_set_material(0, state.material);
            const auto depth = state.program.settings.initialWaterDepth * state.program.sizeScale;
            mesh->set_custom_aabb(AABB(Vector3(-width * .5, -depth, -height * .5), Vector3(width, depth * 16, height)));
            state.mesh = mesh;
        }
        else
        {
            auto mesh = Ref<QuadMesh>{}; mesh.instantiate();
            mesh->set_size(Vector2(static_cast<real_t>(width), static_cast<real_t>(height)));
            mesh->set_material(state.material); state.mesh = mesh;
        }
        state.drawItem = server->instance_create();
        if (!state.drawItem.is_valid()) {
            return error(detail, "Godot could not create a Fluid2D 3D rendering instance");
        }
        server->instance_set_base(state.drawItem, state.mesh->get_rid());
        const auto world = root.get_world_3d();
        server->instance_set_scenario(state.drawItem,
            world.is_valid() ? world->get_scenario() : RID{});
        server->instance_set_transform(state.drawItem,
            root.get_global_transform() * state.drawTransform);
        server->instance_geometry_set_cast_shadows_setting(state.drawItem,
            RenderingServer::SHADOW_CASTING_SETTING_OFF);
        server->instance_set_visible(state.drawItem, false);
        return Result::Ok;
    }

    void releaseState(std::unique_ptr<FluidState>& state)
    {
        if (!state) {
            return;
        }
        if (state->drawItem.is_valid() && server) {
            server->free_rid(state->drawItem);
            state->drawItem = RID{};
        }
        if (state->texture.is_valid()) {
            state->texture->set_texture_rd_rid(RID{});
            state->texture.unref();
        }
        for (auto* texture : {&state->fieldTexture, &state->colorTexture})
        {
            if (texture->is_valid())
            {
                (*texture)->set_texture_rd_rid(RID{});
                texture->unref();
            }
        }
        auto detail = String{};
        bridge->release(std::move(state->resources), detail);
        reservedBytes -= static_cast<std::uint64_t>(state->program.settings.width) * state->program.settings.height * 144;
        state.reset();
    }

    ~Impl()
    {
        auto* root = rootNodeId != 0
            ? Object::cast_to<FluidRenderScopeNode>(ObjectDB::get_instance(rootNodeId))
            : nullptr;
        if (root) {
            root->setRenderer(nullptr);
            root->hide();
        }
        for (auto& state : active) {
            releaseState(state);
        }
        if (root) {
            root->queue_free();
        }
        if (bridge.is_valid()) {
            bridge->shutdown();
            bridge.unref();
        }
    }
};

PackageFluid2DRenderer::PackageFluid2DRenderer(std::unique_ptr<Impl> impl) noexcept
    : impl_(std::move(impl))
{
}

PackageFluid2DRenderer::~PackageFluid2DRenderer() = default;

auto PackageFluid2DRenderer::create(std::vector<Fluid2DProgram> rootPrograms,
    std::vector<Fluid2DTemplate> templates,
    std::unique_ptr<PackageFluid2DRenderer>& out, String& detail) -> Result
{
    detail = String{};
    if (!Thread::is_main_thread() || out) {
        return error(detail, "Godot Fluid2D must be created on the main thread",
            Result::InvalidArgument);
    }
    auto total = rootPrograms.size();
    for (const auto& definition : templates) {
        total += definition.programs.size();
    }
    if (total == 0) {
        return Result::Ok;
    }
    if (rootPrograms.size() > maximumActiveFluids) {
        return error(detail, "A Godot Fluid2D root scope can contain at most 16 fluid programs",
            Result::UnsupportedBinary);
    }
    for (const auto& program : rootPrograms) {
        if (!liberafx::gpu::validFluid2DSettings(program.settings) ||
            !std::isfinite(program.sizeScale) || program.sizeScale <= 0 ||
            program.sizeScale > 10000) {
            return error(detail, "Invalid Godot Fluid2D settings or renderer size",
                Result::InvalidArgument);
        }
    }
    for (const auto& definition : templates) {
        if (definition.programs.size() > maximumActiveFluids) {
            return error(detail, "A Godot Fluid2D Spawnable scope can contain at most 16 fluid programs",
                Result::UnsupportedBinary);
        }
        for (const auto& program : definition.programs) {
            if (!liberafx::gpu::validFluid2DSettings(program.settings) ||
                !std::isfinite(program.sizeScale) || program.sizeScale <= 0 ||
                program.sizeScale > 10000) {
                return error(detail, "Invalid Godot Fluid2D settings or renderer size",
                    Result::InvalidArgument);
            }
        }
    }

    auto impl = std::make_unique<Impl>();
    impl->server = RenderingServer::get_singleton();
    if (!impl->server) {
        return error(detail, "Godot RenderingServer is unavailable", Result::NotReady);
    }
    impl->templates = std::move(templates);
    impl->bridge.instantiate();
    auto result = impl->bridge->initialize(detail);
    if (result != Result::Ok) {
        return result;
    }
    for (const auto& program : rootPrograms) {
        std::unique_ptr<FluidState> state;
        result = impl->createState(program, state, detail);
        if (result != Result::Ok) {
            return result;
        }
        impl->active.push_back(std::move(state));
    }
    out = std::unique_ptr<PackageFluid2DRenderer>(
        new PackageFluid2DRenderer(std::move(impl)));
    return Result::Ok;
}

auto PackageFluid2DRenderer::attach(Node& owner, String& detail) -> Result
{
    detail = String{};
    if (!Thread::is_main_thread() || !owner.is_inside_tree() || impl_->rootNodeId != 0) {
        return error(detail, "Godot Fluid2D requires an active scene owner on the main thread");
    }
    if (impl_->active.empty() && impl_->templates.empty()) {
        return Result::Ok;
    }
    impl_->shader.instantiate();
    if (impl_->shader.is_null()) {
        return error(detail, "Godot could not create the Fluid2D spatial shader");
    }
    impl_->shader->set_code(String(fluidSpatialSource));
    auto* root = memnew(FluidRenderScopeNode);
    auto rootName = String("LibraFxFluid2DRoot");
    auto suffix = std::uint64_t{2};
    while (owner.has_node(NodePath(rootName))) {
        rootName = "LibraFxFluid2DRoot_" + String::num_uint64(suffix++);
    }
    root->set_name(rootName);
    const auto id = root->get_instance_id();
    owner.add_child(root, true);
    auto* attached = Object::cast_to<FluidRenderScopeNode>(ObjectDB::get_instance(id));
    if (!attached || attached->get_parent() != &owner) {
        if (attached) {
            attached->hide();
            attached->queue_free();
        }
        return error(detail, "Godot could not attach the Fluid2D 3D scope");
    }
    const auto world = attached->get_world_3d();
    if (!world.is_valid() || !world->get_scenario().is_valid()) {
        if (auto* parent = attached->get_parent()) {
            parent->remove_child(attached);
        }
        attached->queue_free();
        return error(detail, "Godot Fluid2D requires an active World3D scenario for spatial output");
    }
    impl_->rootNodeId = id;
    attached->setRenderer(this);
    for (auto& state : impl_->active) {
        const auto result = impl_->attachState(*attached, *state, detail);
        if (result != Result::Ok) {
            attached->setRenderer(nullptr);
            if (auto* parent = attached->get_parent()) {
                parent->remove_child(attached);
            }
            attached->queue_free();
            impl_->rootNodeId = 0;
            return result;
        }
    }
    synchronizePresentation();
    return Result::Ok;
}

auto PackageFluid2DRenderer::handleEvent(const SpawnableHostEvent& event, String& detail) -> Result
{
    detail = String{};
    if (!Thread::is_main_thread()) {
        return error(detail, "Godot Fluid2D lifecycle events must run on the main thread",
            Result::InvalidArgument);
    }
    auto containerId = FluidNodeId{};
    std::copy_n(event.containerId, containerId.size(), containerId.data());
    const auto matches = [&](const auto& state) {
        return state->program.instanceId == event.instanceId &&
            state->program.containerId == containerId;
    };
    if (event.kind == SpawnableHostEventKind::Spawn) {
        const auto definition = std::find_if(impl_->templates.begin(), impl_->templates.end(),
            [&](const auto& item) { return item.containerId == containerId; });
        if (definition == impl_->templates.end()) {
            return Result::Ok;
        }
        if (impl_->active.size() + definition->programs.size() > maximumActiveFluids) {
            return error(detail, "Godot Fluid2D supports at most 16 active root and Spawnable programs",
                Result::InsufficientWork);
        }
        auto* root = Object::cast_to<FluidRenderScopeNode>(ObjectDB::get_instance(impl_->rootNodeId));
        if (!root || event.instanceId == 0 || !std::isfinite(event.time) ||
            std::any_of(impl_->active.begin(), impl_->active.end(), matches)) {
            return error(detail, "Invalid or duplicate Godot Fluid2D Spawnable instance",
                Result::InvalidArgument);
        }
        auto added = std::vector<std::unique_ptr<FluidState>>{};
        for (const auto& source : definition->programs) {
            auto program = source;
            program.containerId = containerId;
            program.instanceId = event.instanceId;
            program.startTime = event.time;
            std::unique_ptr<FluidState> state;
            auto result = impl_->createState(program, state, detail);
            if (result != Result::Ok) {
                for (auto& created : added) {
                    impl_->releaseState(created);
                }
                return result;
            }
            result = impl_->attachState(*root, *state, detail);
            if (result != Result::Ok) {
                impl_->releaseState(state);
                for (auto& created : added) {
                    impl_->releaseState(created);
                }
                return result;
            }
            added.push_back(std::move(state));
        }
        for (auto& state : added) {
            impl_->active.push_back(std::move(state));
        }
        return Result::Ok;
    }
    if (event.kind == SpawnableHostEventKind::Stop ||
        event.kind == SpawnableHostEventKind::Destroy ||
        event.kind == SpawnableHostEventKind::ForceTerminated) {
        const auto all = event.kind == SpawnableHostEventKind::ForceTerminated && event.instanceId == 0;
        auto retired = std::remove_if(impl_->active.begin(), impl_->active.end(), [&](auto& state) {
            if (!all && !matches(state)) {
                return false;
            }
            impl_->releaseState(state);
            return true;
        });
        impl_->active.erase(retired, impl_->active.end());
    }
    return Result::Ok;
}

auto PackageFluid2DRenderer::advance(SpawnableHost& host, double elapsed, double delta,
    String& detail) -> Result
{
    detail = String{};
    if (!Thread::is_main_thread() || !std::isfinite(elapsed) || elapsed < 0 ||
        !std::isfinite(delta) || delta < 0) {
        return error(detail, "Invalid Godot Fluid2D frame time", Result::InvalidArgument);
    }
    if (impl_->bridge->hasError(detail)) {
        return Result::NotReady;
    }
    for (const auto& state : impl_->active) {
        auto transform = Transform3D{};
        auto result = transformFor(host, state->program, transform, detail);
        if (result != Result::Ok) {
            return result;
        }
        state->drawTransform = transform;
        state->drawMatrixIsWorld = state->program.hasMatrix;
        const auto localTime = std::max(0.0, elapsed - state->program.startTime);
        const auto localDelta = std::min(delta, localTime);
        Fluid2DFrame frame{};
        if (!liberafx::gpu::makeFluid2DFrame(state->program.settings, localDelta, frame)) {
            return error(detail, "Godot rejected a Fluid2D frame delta", Result::InvalidArgument);
        }
        if (frame.substeps != 0) {
            impl_->bridge->dispatch(state->resources, state->program.settings, state->plan, frame);
        }
    }
    synchronizePresentation();
    return Result::Ok;
}

void PackageFluid2DRenderer::synchronizePresentation(bool exiting)
{
    if (!impl_ || !impl_->server) {
        return;
    }
    auto* scope = impl_->rootNodeId != 0
        ? Object::cast_to<FluidRenderScopeNode>(ObjectDB::get_instance(impl_->rootNodeId))
        : nullptr;
    if (!scope) {
        return;
    }
    auto ownerVisible = !exiting;
    if (!exiting && scope->is_inside_tree()) {
        auto* ancestor = scope->get_parent();
        while (ancestor && !Object::cast_to<Node3D>(ancestor)) {
            if (Object::cast_to<Viewport>(ancestor)) {
                ancestor = nullptr;
                break;
            }
            ancestor = ancestor->get_parent();
        }
        auto* spatialOwner = Object::cast_to<Node3D>(ancestor);
        auto ownerTransform = Transform3D{};
        if (spatialOwner) {
            ownerVisible = spatialOwner->is_visible_in_tree();
            ownerTransform = spatialOwner->get_global_transform();
        }
        if (scope->get_global_transform() != ownerTransform) {
            scope->set_global_transform(ownerTransform);
        }
    }
    for (auto& state : impl_->active) {
        if (!state->drawItem.is_valid()) {
            continue;
        }
        if (exiting || !scope->is_inside_tree()) {
            impl_->server->instance_set_visible(state->drawItem, false);
            impl_->server->instance_set_scenario(state->drawItem, RID{});
            continue;
        }
        const auto world = scope->get_world_3d();
        impl_->server->instance_set_scenario(state->drawItem,
            world.is_valid() ? world->get_scenario() : RID{});
        const auto worldTransform = state->drawMatrixIsWorld
            ? state->drawTransform : scope->get_global_transform();
        impl_->server->instance_set_transform(state->drawItem, worldTransform);
        impl_->server->instance_set_visible(state->drawItem,
            world.is_valid() && ownerVisible && scope->is_visible_in_tree());
    }
}

auto PackageFluid2DRenderer::isEmpty() const noexcept -> bool
{
    return !impl_ || (impl_->active.empty() && impl_->templates.empty());
}

auto PackageFluid2DRenderer::outputTexture(const FluidNodeId& node, const FluidNodeId& container,
    GeneratorInstanceId instance, bool fields, bool straight) const -> Ref<Texture2D>
{
    if (!impl_ || !Thread::is_main_thread())
    {
        return {};
    }
    for (const auto& state : impl_->active)
    {
        if (state->program.nodeId == node && state->program.containerId == container &&
            state->program.instanceId == instance)
        {
            return fields ? state->fieldTexture : straight ? state->colorTexture : state->texture;
        }
    }
    return {};
}

auto PackageFluid2DRenderer::fieldMapping(const FluidNodeId& node, const FluidNodeId& container,
    GeneratorInstanceId instance, const Transform3D& consumerToWorld, std::array<Vector4, 4>& mapping, String& detail) const -> Result
{
    if (!impl_ || !Thread::is_main_thread()) return error(detail, "Field coordinates require the main thread", Result::NotReady);
    auto* scope = Object::cast_to<FluidRenderScopeNode>(ObjectDB::get_instance(impl_->rootNodeId));
    for (const auto& state : impl_->active)
    {
        if (state->program.nodeId != node || state->program.containerId != container || state->program.instanceId != instance) continue;
        if (!scope) return error(detail, "No spatial field scope", Result::NotReady);
        const auto source = state->drawMatrixIsWorld ? state->drawTransform : scope->get_global_transform();
        if (std::abs(source.basis.determinant()) < 1.e-8 || std::abs(consumerToWorld.basis.determinant()) < 1.e-8)
            return error(detail, "Field coordinates require invertible producer and consumer transforms", Result::InvalidArgument);
        const auto inverse = source.affine_inverse() * consumerToWorld;
        const auto toConsumer = consumerToWorld.basis.inverse();
        const auto vAxis = state->program.settings.model == 2 ? 2 : 1;
        const auto width = state->program.settings.domainWidth * state->program.sizeScale;
        const auto height = state->program.settings.domainHeight * state->program.sizeScale;
        const auto u = inverse.basis[0] / width, v = inverse.basis[vAxis] / height;
        mapping[0] = Vector4(u.x, u.y, u.z, inverse.origin.x / width + .5);
        mapping[1] = Vector4(v.x, v.y, v.z, inverse.origin[vAxis] / height + .5);
        const auto velocityU = toConsumer.xform(source.basis.get_column(0) * state->program.sizeScale);
        const auto velocityV = toConsumer.xform(source.basis.get_column(vAxis) * state->program.sizeScale);
        mapping[2] = Vector4(velocityU.x, velocityU.y, velocityU.z, 0);
        mapping[3] = Vector4(velocityV.x, velocityV.y, velocityV.z, 0);
        return Result::Ok;
    }
    return error(detail, "Field producer is outside this scope", Result::InvalidArgument);
}

#ifdef LIBERAFX_GODOT_TEST_HOOKS
auto PackageFluid2DRenderer::testReadFields(Dictionary& fields, String& detail) -> Result
{
    detail = String{};
    if (!Thread::is_main_thread() || !impl_ || impl_->active.empty()) {
        return error(detail, "Fluid2D test readback requires an active root field set",
            Result::InvalidArgument);
    }
    const auto& state = *impl_->active.front();
    const auto cells = static_cast<std::uint64_t>(state.program.settings.width) *
        state.program.settings.height;
    auto rawFields = std::array<PackedByteArray, fieldCount>{};
    auto result = impl_->bridge->testReadFields(state.resources, cells * 16, rawFields, detail);
    if (result != Result::Ok) {
        return result;
    }
    const auto names = std::array<const char*, fieldCount>{
        "velocity", "velocityScratch", "dye", "dyeScratch", "divergence", "pressure",
        "pressureScratch"};
    fields["width"] = static_cast<std::int64_t>(state.program.settings.width);
    fields["height"] = static_cast<std::int64_t>(state.program.settings.height);
    fields["pressureIterations"] =
        static_cast<std::int64_t>(state.program.settings.pressureIterations);
    for (auto index = std::size_t{}; index < fieldCount; ++index) {
        auto values = PackedFloat32Array{};
        const auto floatCount = rawFields[index].size() / static_cast<std::int64_t>(sizeof(float));
        if (values.resize(floatCount) != OK) {
            return error(detail, "Could not allocate Fluid2D test field values",
                Result::InsufficientWork);
        }
        std::memcpy(values.ptrw(), rawFields[index].ptr(), static_cast<std::size_t>(rawFields[index].size()));
        fields[names[index]] = values;
    }
    return Result::Ok;
}

auto PackageFluid2DRenderer::testDisableSourceAndZeroForces(String& detail) -> Result
{
    detail = String{};
    if (!Thread::is_main_thread() || !impl_ || impl_->active.empty()) {
        return error(detail, "Fluid2D test source setup requires an active root field set",
            Result::InvalidArgument);
    }
    auto& state = *impl_->active.front();
    const auto cells = static_cast<std::uint64_t>(state.program.settings.width) *
        state.program.settings.height;
    const auto result = impl_->bridge->testZeroVelocity(state.resources, cells * 16, detail);
    if (result != Result::Ok) {
        return result;
    }
    state.program.settings.sourceEnabled = false;
    state.program.settings.sourceVelocityX = 0;
    state.program.settings.sourceVelocityY = 0;
    state.program.settings.buoyancy = 0;
    return Result::Ok;
}

auto PackageFluid2DRenderer::testSetPressureIterations(std::uint32_t iterations,
    String& detail) -> Result
{
    detail = String{};
    if (!Thread::is_main_thread() || !impl_ || impl_->active.empty() ||
        iterations < 1 || iterations > 128) {
        return error(detail, "Fluid2D test pressure iteration count is invalid",
            Result::InvalidArgument);
    }
    auto& state = *impl_->active.front();
    auto settings = state.program.settings;
    settings.pressureIterations = iterations;
    auto plan = Fluid2DPlan{};
    if (!liberafx::gpu::makeFluid2DPlan(settings, plan)) {
        return error(detail, "Could not create the Fluid2D test pressure plan",
            Result::InvalidArgument);
    }
    const auto result = impl_->bridge->testRebuildUniformSets(state.resources, plan, detail);
    if (result != Result::Ok) {
        return result;
    }
    state.program.settings = settings;
    state.plan = plan;
    return Result::Ok;
}

auto PackageFluid2DRenderer::testSetDrawMatrix(const PackedFloat64Array& values,
    String& detail) -> Result
{
    detail = String{};
    if (!Thread::is_main_thread() || !impl_ || impl_->active.empty() || values.size() != 16) {
        return error(detail, "Fluid2D test Draw Matrix requires 16 values and an active root field set",
            Result::InvalidArgument);
    }
    auto matrix = std::array<std::array<double, 4>, 4>{};
    for (auto index = 0; index < values.size(); ++index) {
        matrix[static_cast<std::size_t>(index / 4)][static_cast<std::size_t>(index % 4)] =
            values[index];
    }
    auto transform = Transform3D{};
    const auto result = transformFromRows(matrix, transform, detail);
    if (result != Result::Ok) {
        return result;
    }
    impl_->active.front()->drawTransform = transform;
    impl_->active.front()->drawMatrixIsWorld = true;
    synchronizePresentation();
    return Result::Ok;
}
#endif

}
