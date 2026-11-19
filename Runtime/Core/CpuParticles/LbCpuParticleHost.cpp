#include "../LbSpawnableHostInternal.h"

namespace liberafx {
auto SpawnableHost::cpuParticleCount(const std::uint8_t id[16], std::size_t& out) const noexcept -> Result
{
    if (impl_->operationActive)
    {
        return Result::Busy;
    }
    if (impl_->failure != Result::Ok)
    {
        return impl_->failure;
    }
    return impl_->cpuParticles ? impl_->cpuParticles->count(id, out) : Result::InvalidHandle;
}
auto SpawnableHost::cpuParticleAt(const std::uint8_t id[16], std::size_t index, CpuParticleSnapshot& out) const noexcept -> Result
{
    if (impl_->operationActive)
    {
        return Result::Busy;
    }
    if (impl_->failure != Result::Ok)
    {
        return impl_->failure;
    }
    return impl_->cpuParticles ? impl_->cpuParticles->at(id, index, out) : Result::InvalidHandle;
}
auto SpawnableHost::popCpuParticleEvent(CpuParticleEvent& out) noexcept -> bool
{
    return !impl_->operationActive && impl_->cpuParticles && impl_->cpuParticles->popEvent(out);
}
}
