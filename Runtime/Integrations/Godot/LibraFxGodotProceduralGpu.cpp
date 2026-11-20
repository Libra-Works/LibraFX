#include "LibraFxGodotProceduralGpu.h"
#include "LibraProceduralShaderSource.h"
#include "LibraFxGodotRenderTaskQueue.h"
#include <godot_cpp/classes/rd_shader_source.hpp>
#include <godot_cpp/classes/rd_shader_spirv.hpp>
#include <godot_cpp/classes/rd_texture_format.hpp>
#include <godot_cpp/classes/rd_texture_view.hpp>
#include <godot_cpp/classes/rd_sampler_state.hpp>
#include <godot_cpp/classes/rd_uniform.hpp>
#include <godot_cpp/classes/rendering_device.hpp>
#include <godot_cpp/variant/packed_byte_array.hpp>
#include <godot_cpp/variant/typed_array.hpp>
#include <atomic>

namespace liberafx::godot_adapter {
using namespace godot;
struct ProceduralGpu::State
{
    Ref<detail::GpuRenderTaskQueue> queue;
    Ref<Texture2DRD> output;
    RenderingDevice* device = nullptr;
    RID shader, pipeline, buffer, image, uniformSet, sampler, white;
    std::array<Ref<Texture2DRD>, 4> dependencies;
    std::uint32_t count = 0, outputRegister = 0, resolution = 0;
    struct Upload
    {
        std::atomic<bool> busy{false};
        PackedByteArray operations, push;
    };
    std::array<Upload, 3> uploads;
    std::atomic<bool> failed{false};
    void release()
    {
        if (!device)
        {
            return;
        }
        for (const auto& rid : {uniformSet, buffer, pipeline, shader, sampler, image, white})
        {
            if (rid.is_valid())
            {
                device->free_rid(rid);
            }
        }
        device = nullptr;
    }
};
ProceduralGpu::ProceduralGpu() : state_(std::make_shared<State>())
{
    state_->queue.instantiate();
}
ProceduralGpu::~ProceduralGpu()
{
    const auto state = state_;
    if (state->output.is_valid())
    {
        state->output->set_texture_rd_rid(RID{});
    }
    state->queue->enqueue([state]() { state->release(); });
    if (auto* server = RenderingServer::get_singleton())
    {
        server->force_sync();
    }
}
auto ProceduralGpu::initialize(const procedural_adapter::Definition& definition,
    const std::array<Ref<Texture2DRD>, 4>& textures, String& error) -> bool
{
    auto state = state_;
    state->count = definition.pixel.operationCount;
    state->outputRegister = definition.pixel.outputRegister;
    state->resolution = definition.resolution;
    state->dependencies = textures;
    for (auto& upload : state->uploads)
    {
        upload.operations.resize(state->count * sizeof(LibraProceduralPixelOperation));
        upload.push.resize(16);
    }
    auto* server = RenderingServer::get_singleton();
    if (!server)
    {
        error = "Procedural textures require RenderingServer.";
        return false;
    }
    auto detail = std::make_shared<String>();
    state->queue->enqueue([state, detail]()
    {
        auto* server = RenderingServer::get_singleton();
        state->device = server ? server->get_rendering_device() : nullptr;
        if (!state->device)
        {
            *detail = "Procedural textures require a global RenderingDevice (Forward+ or Mobile).";
            return;
        }
        auto* device = state->device;
        auto source = Ref<RDShaderSource>{}; source.instantiate();
        source->set_stage_source(RenderingDevice::SHADER_STAGE_COMPUTE, String::utf8(procedural_shader::source));
        auto spirv = device->shader_compile_spirv_from_source(source);
        if (spirv.is_null() || !spirv->get_stage_compile_error(RenderingDevice::SHADER_STAGE_COMPUTE).is_empty())
        {
            *detail = spirv.is_valid() ? spirv->get_stage_compile_error(RenderingDevice::SHADER_STAGE_COMPUTE) : String("Cannot compile procedural shader.");
            return;
        }
        state->shader = device->shader_create_from_spirv(spirv, "Libera FX procedural");
        state->pipeline = device->compute_pipeline_create(state->shader);
        state->buffer = device->storage_buffer_create(state->count * sizeof(LibraProceduralPixelOperation));
        auto format = Ref<RDTextureFormat>{}; format.instantiate();
        format->set_texture_type(RenderingDevice::TEXTURE_TYPE_2D);
        format->set_width(state->resolution); format->set_height(state->resolution);
        format->set_format(RenderingDevice::DATA_FORMAT_R16G16B16A16_SFLOAT);
        format->set_usage_bits(RenderingDevice::TEXTURE_USAGE_STORAGE_BIT | RenderingDevice::TEXTURE_USAGE_SAMPLING_BIT);
        auto view = Ref<RDTextureView>{}; view.instantiate();
        state->image = device->texture_create(format, view);
        format->set_width(1); format->set_height(1);
        format->set_format(RenderingDevice::DATA_FORMAT_R8G8B8A8_UNORM);
        format->set_usage_bits(RenderingDevice::TEXTURE_USAGE_SAMPLING_BIT);
        auto whiteBytes = PackedByteArray{}; whiteBytes.resize(4);
        std::memset(whiteBytes.ptrw(), 255, 4);
        auto data = TypedArray<PackedByteArray>{}; data.append(whiteBytes);
        state->white = device->texture_create(format, view, data);
        auto sampler = Ref<RDSamplerState>{}; sampler.instantiate();
        sampler->set_min_filter(RenderingDevice::SAMPLER_FILTER_LINEAR);
        sampler->set_mag_filter(RenderingDevice::SAMPLER_FILTER_LINEAR);
        sampler->set_repeat_u(RenderingDevice::SAMPLER_REPEAT_MODE_REPEAT);
        sampler->set_repeat_v(RenderingDevice::SAMPLER_REPEAT_MODE_REPEAT);
        state->sampler = device->sampler_create(sampler);
        auto uniforms = TypedArray<RDUniform>{};
        auto storage = Ref<RDUniform>{}; storage.instantiate(); storage->set_binding(0);
        storage->set_uniform_type(RenderingDevice::UNIFORM_TYPE_STORAGE_BUFFER); storage->add_id(state->buffer); uniforms.append(storage);
        auto image = Ref<RDUniform>{}; image.instantiate(); image->set_binding(1);
        image->set_uniform_type(RenderingDevice::UNIFORM_TYPE_IMAGE); image->add_id(state->image); uniforms.append(image);
        for (auto i = 0; i < 4; ++i)
        {
            auto texture = Ref<RDUniform>{}; texture.instantiate(); texture->set_binding(i + 2);
            texture->set_uniform_type(RenderingDevice::UNIFORM_TYPE_SAMPLER_WITH_TEXTURE); texture->add_id(state->sampler);
            texture->add_id(state->dependencies[i].is_valid() ? state->dependencies[i]->get_texture_rd_rid() : state->white);
            uniforms.append(texture);
        }
        state->uniformSet = device->uniform_set_create(uniforms, state->shader, 0);
        if (!state->pipeline.is_valid() || !state->buffer.is_valid() || !state->image.is_valid() || !state->uniformSet.is_valid())
        {
            *detail = "Cannot allocate procedural GPU resources.";
        }
    });
    server->force_sync();
    if (!detail->is_empty() || !state->image.is_valid())
    {
        error = *detail;
        return false;
    }
    state->output.instantiate(); state->output->set_texture_rd_rid(state->image);
    return true;
}
auto ProceduralGpu::update(const std::vector<LibraProceduralPixelOperation>& operations, double time) -> bool
{
    auto state = state_;
    if (state->failed || operations.size() != state->count)
    {
        return false;
    }
    for (auto i = 0u; i < state->uploads.size(); ++i)
    {
        auto expected = false;
        auto& upload = state->uploads[i];
        if (!upload.busy.compare_exchange_strong(expected, true))
        {
            continue;
        }
        std::memcpy(upload.operations.ptrw(), operations.data(), operations.size() * sizeof(LibraProceduralPixelOperation));
        const auto seconds = static_cast<float>(time);
        auto* push = upload.push.ptrw();
        std::memcpy(push, &state->count, 4); std::memcpy(push + 4, &state->outputRegister, 4);
        std::memcpy(push + 8, &state->resolution, 4); std::memcpy(push + 12, &seconds, 4);
        const auto queued = state->queue->enqueue([state, i]()
        {
            auto& upload = state->uploads[i];
            if (!state->device || state->device->buffer_update(state->buffer, 0, upload.operations.size(), upload.operations) != OK)
            {
                state->failed = true;
                upload.busy = false;
                return;
            }
            const auto list = state->device->compute_list_begin();
            state->device->compute_list_bind_compute_pipeline(list, state->pipeline);
            state->device->compute_list_bind_uniform_set(list, state->uniformSet, 0);
            state->device->compute_list_set_push_constant(list, upload.push, 16);
            state->device->compute_list_dispatch(list, (state->resolution + 7) / 8, (state->resolution + 7) / 8, 1);
            state->device->compute_list_end();
            upload.busy = false;
        });
        if (!queued)
        {
            upload.busy = false;
        }
        return queued;
    }
    // Bounded upload storage: never grow a queue of large per-frame snapshots.
    return false;
}
auto ProceduralGpu::texture() const -> Ref<Texture2DRD> { return state_->output; }
}
