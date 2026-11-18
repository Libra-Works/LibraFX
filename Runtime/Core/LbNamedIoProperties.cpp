#include "LbNamedIoInternal.h"

#include <cmath>

namespace liberafx {

auto NamedIo::Impl::setParameters(const ParameterHandle* parameters, const double* values,
                                  std::size_t width) noexcept -> Result
{
    auto previousValues = std::array<double, 16>{};
    for (auto lane = std::size_t{}; lane < width; ++lane)
    {
        auto result = instance.parameter(parameters[lane], previousValues[lane]);
        if (result != Result::Ok)
        {
            return result;
        }
    }
    for (auto lane = std::size_t{}; lane < width; ++lane)
    {
        auto result = instance.setParameter(parameters[lane], values[lane]);
        if (result != Result::Ok)
        {
            for (auto previous = std::size_t{}; previous < lane; ++previous)
            {
                (void)instance.setParameter(parameters[previous], previousValues[previous]);
            }
            return result;
        }
    }
    return Result::Ok;
}

auto NamedIo::Impl::readOutputValues(const named_io_detail::Port& port,
                                     std::array<double, 16>& values) noexcept -> Result
{
    auto rowWidth = port.width == 16 ? 4u : port.width;
    for (auto row = 0u; row < port.nodeCount; ++row)
    {
        auto result = instance.value(port.values[row], values.data() + row * rowWidth);
        if (result != Result::Ok)
        {
            return result;
        }
    }
    return Result::Ok;
}

auto NamedIo::Impl::outputChanged(const named_io_detail::Port& port,
                                  const std::array<double, 16>& values) const noexcept -> bool
{
    if (!port.published)
    {
        return true;
    }
    for (auto lane = 0u; lane < port.width; ++lane)
    {
        if (values[lane] != port.previous[lane])
        {
            return true;
        }
    }
    return false;
}

auto NamedIo::Impl::publishProperties() noexcept -> Result
{
    auto pending = std::size_t{};
    for (const auto& port : ports)
    {
        if (port.kind != NamedIoKind::OutputProperty)
        {
            continue;
        }
        auto values = std::array<double, 16>{};
        auto result = readOutputValues(port, values);
        if (result != Result::Ok)
        {
            return result;
        }
        if (outputChanged(port, values))
        {
            ++pending;
        }
    }
    if (pending > events.size() - count)
    {
        return Result::QueueRejected;
    }
    for (auto index = std::size_t{}; index < ports.size(); ++index)
    {
        auto& port = ports[index];
        if (port.kind != NamedIoKind::OutputProperty)
        {
            continue;
        }
        auto values = std::array<double, 16>{};
        auto result = readOutputValues(port, values);
        if (result != Result::Ok)
        {
            return result;
        }
        if (outputChanged(port, values))
        {
            append(index, values.data());
            port.previous = values;
            port.published = true;
        }
    }
    return Result::Ok;
}

auto NamedIo::setProperty(const char* name, const double* values, std::size_t width) noexcept -> Result
{
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto port = impl_->find(name, NamedIoKind::InputProperty);
    if (!port)
    {
        return Result::NodeNotFound;
    }
    if (!values || width != port->width)
    {
        return Result::InvalidValueType;
    }
    for (auto lane = std::size_t{}; lane < width; ++lane)
    {
        if (!std::isfinite(values[lane]))
        {
            return Result::NonFinite;
        }
    }
    return impl_->setParameters(port->parameters.data(), values, width);
}

auto NamedIo::setOwnerMatrix(const double* values, std::size_t width) noexcept -> Result
{
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto port = static_cast<named_io_detail::Port*>(nullptr);
    for (auto& candidate : impl_->ports)
    {
        if (candidate.kind == NamedIoKind::OwnerMatrix)
        {
            port = &candidate;
            break;
        }
    }
    if (!port)
    {
        return Result::NodeNotFound;
    }
    if (!values || width != 16)
    {
        return Result::InvalidValueType;
    }
    for (auto lane = std::size_t{}; lane < width; ++lane)
    {
        if (!std::isfinite(values[lane]))
        {
            return Result::NonFinite;
        }
    }
    return impl_->setParameters(port->parameters.data(), values, width);
}

auto NamedIo::publishProperties() noexcept -> Result
{
    return impl_->executing ? Result::Busy : impl_->publishProperties();
}

}
