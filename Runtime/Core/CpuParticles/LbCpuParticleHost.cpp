#include "../LbSpawnableHostInternal.h"

namespace liberafx {
auto SpawnableHost::cpuParticleCount(const std::uint8_t id[16], std::size_t& out) const noexcept -> Result
{
    return cpuParticleCount(SpawnableScope{}, id, out);
}
auto SpawnableHost::cpuParticleAt(const std::uint8_t id[16], std::size_t index, CpuParticleSnapshot& out) const noexcept -> Result
{
    return cpuParticleAt(SpawnableScope{}, id, index, out);
}
auto SpawnableHost::cpuParticleCount(const SpawnableScope& scope,
                                    const std::uint8_t id[16],
                                    std::size_t& out) const noexcept -> Result
{
    out = 0;
    if (!id)
    {
        return Result::InvalidArgument;
    }
    if (impl_->operationActive)
    {
        return Result::Busy;
    }
    if (impl_->failure != Result::Ok)
    {
        return impl_->failure;
    }
    if (!spawnable_host_detail::validScope(scope))
    {
        return Result::InvalidArgument;
    }
    if (scope.kind == SpawnableScopeKind::Effect)
    {
        return impl_->cpuParticles
                   ? impl_->cpuParticles->count(id, out)
                   : Result::InvalidHandle;
    }
    auto state = impl_->findTemplate(scope.containerId);
    auto instance = state ? impl_->findInstance(*state, scope.instanceId) : nullptr;
    return instance && instance->cpuParticles
               ? instance->cpuParticles->count(id, out)
               : Result::InvalidHandle;
}
auto SpawnableHost::cpuParticleAt(const SpawnableScope& scope,
                                  const std::uint8_t id[16],
                                  std::size_t index,
                                  CpuParticleSnapshot& out) const noexcept -> Result
{
    out = {};
    if (!id)
    {
        return Result::InvalidArgument;
    }
    if (impl_->operationActive)
    {
        return Result::Busy;
    }
    if (impl_->failure != Result::Ok)
    {
        return impl_->failure;
    }
    if (!spawnable_host_detail::validScope(scope))
    {
        return Result::InvalidArgument;
    }
    if (scope.kind == SpawnableScopeKind::Effect)
    {
        return impl_->cpuParticles
                   ? impl_->cpuParticles->at(id, index, out)
                   : Result::InvalidHandle;
    }
    auto state = impl_->findTemplate(scope.containerId);
    auto instance = state ? impl_->findInstance(*state, scope.instanceId) : nullptr;
    return instance && instance->cpuParticles
               ? instance->cpuParticles->at(id, index, out)
               : Result::InvalidHandle;
}
auto SpawnableHost::cpuParticleTiming(const SpawnableScope& scope, const std::uint8_t id[16],
    std::size_t index, CpuParticleTiming& out) const noexcept -> Result
{
    out = {};
    if (!id || !spawnable_host_detail::validScope(scope)) return Result::InvalidArgument;
    if (impl_->operationActive) return Result::Busy;
    if (impl_->failure != Result::Ok) return impl_->failure;
    if (scope.kind == SpawnableScopeKind::Effect)
        return impl_->cpuParticles ? impl_->cpuParticles->timing(id,index,out) : Result::InvalidHandle;
    const auto state = impl_->findTemplate(scope.containerId);
    const auto instance = state ? impl_->findInstance(*state,scope.instanceId) : nullptr;
    return instance && instance->cpuParticles ? instance->cpuParticles->timing(id,index,out) : Result::InvalidHandle;
}

auto SpawnableHost::popCpuParticleEvent(CpuParticleEvent& out) noexcept -> bool
{
    return popCpuParticleEvent(SpawnableScope{}, out);
}
auto SpawnableHost::popCpuParticleEvent(const SpawnableScope& scope,
                                        CpuParticleEvent& out) noexcept -> bool
{
    out = {};
    if (impl_->operationActive ||
        !spawnable_host_detail::validScope(scope) ||
        impl_->failure != Result::Ok)
    {
        return false;
    }
    if (scope.kind == SpawnableScopeKind::Effect)
    {
        return impl_->cpuParticles && impl_->cpuParticles->popEvent(out);
    }
    auto state = impl_->findTemplate(scope.containerId);
    auto instance = state ? impl_->findInstance(*state, scope.instanceId) : nullptr;
    if (instance && instance->cpuParticles &&
        instance->cpuParticles->popEvent(out))
    {
        return true;
    }
    const auto found = std::find_if(
        impl_->retiredCpuEvents.begin(), impl_->retiredCpuEvents.end(),
        [&](const auto& item) {
            return item.instanceId == scope.instanceId &&
                   spawnable_host_detail::sameId(item.containerId.data(),
                                                 scope.containerId);
        });
    if (found == impl_->retiredCpuEvents.end())
    {
        return false;
    }
    out = found->event;
    impl_->retiredCpuEvents.erase(found);
    return true;
}
}
