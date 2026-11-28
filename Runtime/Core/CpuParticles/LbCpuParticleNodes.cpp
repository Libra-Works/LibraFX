#include "LbCpuParticlesInternal.h"
#include "liberafx/math/LbRandomMath.h"

namespace liberafx::cpu_particle_detail {
auto System::invoke(CustomNodeInvocation& call) noexcept -> Result
{
    auto& context = *static_cast<Callback*>(call.context);
    auto& system = *context.system;
    if (context.operation == 0x36)
    {
        if (system.incomingCount == system.incoming.size())
        {
            return Result::QueueRejected;
        }
        system.incoming[system.incomingCount++] = {context.node, context.stage};
        call.selectedBranch = 0;
        return Result::Ok;
    }
    if (system.current == UINT32_MAX)
    {
        return Result::NotReady;
    }
    const auto particle = system.current;
    auto& particleData = system.particleData;
    auto operation = context.operation;
    if (operation == 0x49) {
        if (particleData.life[particle]==0) return Result::DivideByZero;
        const auto ratio=particleData.age[particle]/particleData.life[particle];
        if (!std::isfinite(ratio)) return Result::NonFinite;
        call.outputs[0]=call.inputs[0]; call.outputs[1]=call.inputs[4]; call.outputs[2]=call.inputs[8];
        call.outputs[3]=1-std::max(0.0,std::min(ratio,1.0));
        return Result::Ok;
    }
    if (operation >= 0x45 && operation <= 0x48) {
        auto sample = MeshEmissionSample{};
        const auto index = static_cast<std::uint32_t>(particleData.id[particle]);
        if (!system.emitterSurface)
        {
            return Result::NotReady;
        }
        const auto status = system.emitterSurface->sample(math::randomUnit(system.seed, index, 11),
                                                          math::randomUnit(system.seed, index, 12),
                                                          math::randomUnit(system.seed, index, 13), sample);
        if (status != Result::Ok) return status;
        if (operation == 0x45) std::copy(sample.position.begin(), sample.position.end(), call.outputs);
        if (operation == 0x46) std::copy(sample.normal.begin(), sample.normal.end(), call.outputs);
        if (operation == 0x47) std::copy(sample.uv.begin(), sample.uv.end(), call.outputs);
        if (operation == 0x48) std::copy(sample.color.begin(), sample.color.end(), call.outputs);
        return Result::Ok;
    }
    if (operation == 5)
    {
        call.selectedBranch = 0;
        return Result::Ok;
    }
    if (operation >= 0x10 && operation <= 0x18)
    {
        switch (operation)
        {
        case 0x10: for (auto lane=0u; lane<3; ++lane) { call.outputs[lane]=particleData.position[lane][particle]; }; break;
        case 0x11: for (auto lane=0u; lane<3; ++lane) { call.outputs[lane]=particleData.velocity[lane][particle]; }; break;
        case 0x12: for (auto lane=0u; lane<4; ++lane) { call.outputs[lane]=particleData.color[lane][particle]; }; break;
        case 0x13: for (auto lane=0u; lane<2; ++lane) { call.outputs[lane]=particleData.size[lane][particle]; }; break;
        case 0x14: call.outputs[0] = particleData.life[particle]; break;
        case 0x15: call.outputs[0] = particleData.age[particle]; break;
        case 0x16: call.outputs[0] = static_cast<double>(particleData.id[particle]); break;
        case 0x17: call.outputs[0] = system.seed; break;
        case 0x18: call.outputs[0] = system.stepDelta; break;
        default: return Result::InvalidArgument;
        }
        return Result::Ok;
    }
    if (operation >= 0x20 && operation <= 0x26)
    {
        if (operation == 0x20)
        {
            for (auto lane=0u; lane<3; ++lane) { particleData.position[lane][particle]=call.inputs[lane]; };
        }
        else if (operation == 0x21)
        {
            for (auto lane=0u; lane<3; ++lane) { particleData.velocity[lane][particle]=call.inputs[lane]; };
        }
        else if (operation == 0x22)
        {
            for (auto lane=0u; lane<4; ++lane) { particleData.color[lane][particle]=call.inputs[lane]; };
        }
        else if (operation == 0x23)
        {
            if (call.inputs[0] < 0 || call.inputs[1] < 0)
            {
                return Result::InvalidArgument;
            }
            for (auto lane=0u; lane<2; ++lane) { particleData.size[lane][particle]=call.inputs[lane]; };
        }
        else if (operation == 0x24)
        {
            if (call.inputs[0] < 0)
            {
                return Result::InvalidArgument;
            }
            particleData.life[particle] = call.inputs[0];
        }
        else if (operation == 0x26)
        {
            if (!std::isfinite(call.inputs[0]) || call.inputs[0] < 0)
            {
                return Result::InvalidArgument;
            }
            particleData.age[particle] = call.inputs[0];
        }
        else
        {
            for (auto lane = 0u; lane < 3; ++lane)
            {
                particleData.velocity[lane][particle] += call.inputs[lane] * system.stepDelta;
                particleData.position[lane][particle] += particleData.velocity[lane][particle] * system.stepDelta;
                if (!std::isfinite(particleData.velocity[lane][particle]) || !std::isfinite(particleData.position[lane][particle]))
                {
                    return Result::InvalidArgument;
                }
            }
        }
        call.selectedBranch = 0;
        return Result::Ok;
    }
    if (operation >= 0x42 && operation <= 0x44)
    {
        auto index = static_cast<std::uint32_t>(particleData.id[particle]);
        if (operation == 0x42)
        {
            for (auto lane = 0u; lane < 3; ++lane)
            {
                if (call.inputs[4 + lane] < 0)
                {
                    return Result::InvalidArgument;
                }
                call.outputs[lane] = call.inputs[lane] +
                    (math::randomUnit(system.seed, index, lane) * 2 - 1) * call.inputs[4 + lane];
            }
        }
        else
        {
            auto radius = call.inputs[4];
            if (radius < 0)
            {
                return Result::InvalidArgument;
            }
            if (operation == 0x43)
            {
                auto point = math::randomSpherePoint(system.seed, index);
                for (auto lane = 0u; lane < 3; ++lane)
                {
                    call.outputs[lane] = call.inputs[lane] + point[lane] * radius;
                }
            }
            else
            {
                auto angle = math::randomUnit(system.seed, index, 0) * 6.2831853071795864769;
                call.outputs[0] = call.inputs[0] + std::cos(angle) * radius;
                call.outputs[1] = call.inputs[1] + std::sin(angle) * radius;
                call.outputs[2] = call.inputs[2];
            }
        }
        return Result::Ok;
    }
    return Result::InvalidArgument;
}

auto System::observe(void* context, const std::uint8_t nodeId[16]) noexcept -> Result
{
    auto& system = *static_cast<System*>(context);
    if (system.current == UINT32_MAX)
    {
        return Result::NotReady;
    }
    for (auto& stage : system.stages)
    {
        for (auto& io : stage.io)
        {
            if (io.kind == 3 && std::memcmp(io.node.data(), nodeId, 16) == 0)
            {
                return system.publish(io, nullptr, system.lastTime);
            }
        }
    }
    return Result::Ok;
}

struct EmitterState final {
    double total = 0, previousTime = -1, previousTrigger = 0;
};
auto rate(CustomNodeInvocation& call) noexcept -> Result
{
    auto& state = *static_cast<EmitterState*>(call.state);
    if (call.inputs[0] < 0 || call.time < state.previousTime)
    {
        return Result::InvalidArgument;
    }
    auto delta = state.previousTime < 0 ? 0 : call.time - state.previousTime;
    auto total = state.total + call.inputs[0] * delta;
    if (!std::isfinite(total) || total > 4294967295.0)
    {
        return Result::InsufficientWork;
    }
    state.total = total;
    state.previousTime = call.time;
    call.outputs[0] = std::floor(total);
    return Result::Ok;
}
auto burst(CustomNodeInvocation& call) noexcept -> Result
{
    auto& state = *static_cast<EmitterState*>(call.state);
    auto count = call.inputs[0];
    auto trigger = call.inputs[4];
    if (count < 0 || std::floor(count) != count || trigger < 0)
    {
        return Result::InvalidArgument;
    }
    if (trigger > state.previousTrigger)
    {
        state.total += count;
    }
    if (!std::isfinite(state.total) || state.total > 4294967295.0)
    {
        return Result::InsufficientWork;
    }
    state.previousTrigger = trigger;
    call.outputs[0] = state.total;
    return Result::Ok;
}
auto initializeEmitter(void* memory, std::size_t, void*) noexcept -> Result
{
    *static_cast<EmitterState*>(memory) = {};
    return Result::Ok;
}
auto emitterDescriptors() noexcept -> const std::array<CustomNodeDescriptor, 2>&
{
    static const auto descriptors = []()
    {
        auto result = std::array<CustomNodeDescriptor, 2>{};
        for (auto index = 0u; index < result.size(); ++index)
        {
            auto& descriptor = result[index];
            auto id = typeId(static_cast<std::uint8_t>(0x40 + index));
            std::copy(id.begin(), id.end(), descriptor.typeId);
            descriptor.abiVersion = 1;
            descriptor.execution = CustomExecution::Parameter;
            descriptor.inputCount = static_cast<std::uint8_t>(index + 1);
            descriptor.inputWidths[0] = 1;
            descriptor.inputWidths[1] = index == 1 ? 1 : 0;
            descriptor.outputWidth = 1;
            descriptor.stateBytes = sizeof(EmitterState);
            descriptor.initialize = initializeEmitter;
            descriptor.function = index == 0 ? rate : burst;
        }
        return result;
    }();
    return descriptors;
}
}
namespace liberafx {
auto cpuEmitterNodes(std::size_t& count) noexcept -> const CustomNodeDescriptor*
{
    const auto& descriptors = cpu_particle_detail::emitterDescriptors();
    count = descriptors.size();
    return descriptors.data();
}
}
