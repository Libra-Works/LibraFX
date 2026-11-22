#include "LbSpawnableHostInternal.h"

namespace liberafx
{
void SpawnableHost::Impl::configureExecutionSafety() noexcept
{
    callbackReuseSafe = callbackWorkerSafe = callbackPreparationSafe = true;
    const auto usedBy = [](const auto &bindings, const CustomNodeDescriptor &descriptor)
    {
        return std::any_of(bindings.begin(), bindings.end(), [&](const auto &binding)
                           { return binding.descriptor && sameCustomNodeDescriptor(*binding.descriptor, descriptor); });
    };
    auto builtinCount = std::size_t{};
    const auto *builtins = cpuEmitterNodes(builtinCount);
    for (const auto &descriptor : projectDescriptors)
    {
        if (std::any_of(builtins, builtins + builtinCount,
                        [&](const auto &builtin) { return sameCustomNodeDescriptor(builtin, descriptor); }))
        {
            continue;
        }
        auto used = usedBy(parentBindings, descriptor) || (cpuParticles && cpuParticles->usesDescriptor(descriptor));
        for (const auto &state : templates)
        {
            used = used || usedBy(state->bindings, descriptor) ||
                   (state->cpuDescriptorWorld && state->cpuDescriptorWorld->usesDescriptor(descriptor));
        }
        if (!used)
        {
            continue;
        }
        const auto policy =
            std::find_if(options.customNodePolicies.begin(), options.customNodePolicies.end(),
                         [&](const auto &item) { return sameCustomNodeDescriptor(item.descriptor, descriptor); });
        const auto declared = policy != options.customNodePolicies.end();
        callbackReuseSafe &= declared && policy->reusable;
        callbackWorkerSafe &= declared && policy->workerSafe;
        callbackPreparationSafe &= declared && policy->parallelPreparation;
    }
}
Result SpawnableHost::Impl::recycleCpuWorld(TemplateState &state, InstanceInfo &instance) noexcept
{
    if (!retainForPool || !instance.cpuParticles)
    {
        return Result::Ok;
    }
    const auto result = instance.cpuParticles->resetForPoolReuse();
    if (result != Result::Ok)
    {
        return result;
    }
    // Storage is reserved before playback. Never allocate a bank entry on return.
    if (state.idleCpuWorlds.size() == state.idleCpuWorlds.capacity())
    {
        return Result::InsufficientWork;
    }
    state.idleCpuWorlds.push_back(std::move(instance.cpuParticles));
    return Result::Ok;
}
Result SpawnableHost::prepareForPoolReuse() noexcept
{
    if (!supportsPoolReuse())
    {
        return Result::UnsupportedBinary;
    }
    auto &host = *impl_;
    try
    {
        for (auto &state : host.templates)
        {
            auto result = state->pool->prepareStorage();
            if (result != Result::Ok)
            {
                return result;
            }
            state->instances.reserve(host.options.maxActivePerTemplate);
            state->idleCpuWorlds.reserve(host.options.maxActivePerTemplate);
            if (state->data->cpuPrograms.empty())
            {
                continue;
            }
            for (auto index = state->idleCpuWorlds.size(); index < host.options.maxActivePerTemplate; ++index)
            {
                auto world = std::unique_ptr<CpuParticleWorld>{};
                result = CpuParticleWorld::create(state->data->cpuPrograms.data(), state->data->cpuPrograms.size(),
                                                  host.projectDescriptors.data(), host.projectDescriptors.size(), world,
                                                  host.options.cpuParticleBatching);
                if (result != Result::Ok)
                {
                    return result;
                }
                state->idleCpuWorlds.push_back(std::move(world));
            }
        }
        host.retainForPool = true;
        return Result::Ok;
    }
    catch (...)
    {
        return Result::InsufficientWork;
    }
}
} // namespace liberafx
