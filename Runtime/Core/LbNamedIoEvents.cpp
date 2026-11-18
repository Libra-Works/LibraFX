#include "LbNamedIoInternal.h"

namespace liberafx {

auto NamedIo::observeSignal(void* context, const std::uint8_t nodeId[16]) noexcept -> Result
{
    auto& impl = *static_cast<Impl*>(context);
    for (const auto& port : impl.ports)
    {
        if (port.kind == NamedIoKind::InputSignal &&
            std::memcmp(port.nodeId.data(), nodeId, 16) == 0)
        {
            return Result::Ok;
        }
    }
    for (auto index = std::size_t{}; index < impl.ports.size(); ++index)
    {
        const auto& port = impl.ports[index];
        if (port.kind != NamedIoKind::OutputSignal ||
            std::memcmp(port.nodeId.data(), nodeId, 16) != 0)
        {
            continue;
        }
        if (impl.count == impl.events.size())
        {
            return Result::QueueRejected;
        }
        impl.append(index, nullptr);
        break;
    }
    return Result::Ok;
}

auto NamedIo::emitSignal(const char* name) noexcept -> Result
{
    if (!impl_->dispatching)
    {
        return Result::Busy;
    }
    auto port = impl_->find(name, NamedIoKind::OutputSignal);
    if (!port)
    {
        return Result::NodeNotFound;
    }
    if (impl_->count == impl_->events.size())
    {
        return Result::QueueRejected;
    }
    auto index = static_cast<std::size_t>(port - impl_->ports.data());
    impl_->append(index, nullptr);
    return Result::Ok;
}

auto NamedIo::popEvent(NamedIoEvent& out) noexcept -> bool
{
    if (impl_->executing || !impl_->count)
    {
        return false;
    }
    out = impl_->events[impl_->head];
    impl_->head = (impl_->head + 1) % impl_->events.size();
    --impl_->count;
    return true;
}

auto NamedIo::peekEvent(NamedIoEvent& out) const noexcept -> bool
{
    if (impl_->executing || !impl_->count)
    {
        return false;
    }
    out = impl_->events[impl_->head];
    return true;
}

auto NamedIo::portName(std::size_t index) const noexcept -> const char*
{
    return index < impl_->ports.size() ? impl_->ports[index].name.c_str() : nullptr;
}

}
