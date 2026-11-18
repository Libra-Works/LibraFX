#include "LbNamedIoInternal.h"

#include <new>
#include <utility>

namespace liberafx {

NamedIo::NamedIo(std::unique_ptr<Impl> impl) noexcept : impl_(std::move(impl)) {}
NamedIo::~NamedIo()
{
    if (impl_->observerAttached)
    {
        (void)impl_->instance.setSignalObserver(nullptr, nullptr);
    }
}

auto NamedIo::Impl::addPort(const NamedIoPort& source) -> Result
{
    auto length = std::size_t{};
    if (!named_io_detail::validName(source.name, length))
    {
        return Result::InvalidArgument;
    }
    auto name = std::string(source.name, length);
    for (const auto& existing : ports)
    {
        if (existing.name == name && existing.kind != NamedIoKind::OwnerMatrix &&
            source.kind != NamedIoKind::OwnerMatrix)
        {
            return Result::InvalidArgument;
        }
    }
    auto property = source.kind == NamedIoKind::InputProperty ||
                    source.kind == NamedIoKind::OutputProperty ||
                    source.kind == NamedIoKind::OwnerMatrix;
    auto signal = source.kind == NamedIoKind::InputSignal ||
                  source.kind == NamedIoKind::OutputSignal;
    auto matrix = source.width == 16;
    auto nodeCount = static_cast<unsigned>(source.nodeCount == 0 ? 1 : source.nodeCount);
    if ((!property && !signal) ||
        (signal && (source.width || source.isColor || nodeCount != 1)) ||
        (source.kind == NamedIoKind::OwnerMatrix && (!matrix || source.isColor)) ||
        (property && ((!matrix && (source.width < 1 || source.width > 4 ||
                                    (source.isColor && source.width != 4))) ||
                      (matrix && (source.isColor || nodeCount != 4)) ||
                      (!matrix && nodeCount != 1))))
    {
        return Result::InvalidArgument;
    }

    auto port = named_io_detail::Port{};
    port.name = std::move(name);
    port.kind = source.kind;
    std::memcpy(port.nodeId.data(), source.nodeId, 16);
    port.nodeIds[0] = port.nodeId;
    for (auto row = 1u; row < nodeCount; ++row)
    {
        std::memcpy(port.nodeIds[row].data(), source.additionalNodeIds[row - 1], 16);
    }
    port.width = source.width;
    port.isColor = source.isColor;
    port.nodeCount = static_cast<std::uint8_t>(nodeCount);

    auto status = Result::Ok;
    if (source.kind == NamedIoKind::InputProperty || source.kind == NamedIoKind::OwnerMatrix)
    {
        auto rowWidth = matrix ? 4u : source.width;
        for (auto row = 0u; row < nodeCount; ++row)
        {
            for (auto lane = 0u; lane < rowWidth; ++lane)
            {
                auto index = row * rowWidth + lane;
                status = instance.bindParameter(port.nodeIds[row].data(), 0,
                    static_cast<std::uint8_t>(lane), port.parameters[index]);
                if (status != Result::Ok)
                {
                    return status;
                }
                auto current = double{};
                status = instance.parameter(port.parameters[index], current);
                if (status == Result::Ok)
                {
                    status = instance.setUnconnectedParameter(port.nodeIds[row].data(), 0,
                        static_cast<std::uint8_t>(lane), current);
                }
                if (status != Result::Ok)
                {
                    return status;
                }
            }
        }
    }
    else if (source.kind == NamedIoKind::OutputProperty)
    {
        auto rowWidth = matrix ? 4u : source.width;
        for (auto row = 0u; row < nodeCount; ++row)
        {
            status = instance.bindValue(port.nodeIds[row].data(), port.values[row]);
            if (status != Result::Ok)
            {
                return status;
            }
            if (port.values[row].componentCount != rowWidth)
            {
                return Result::InvalidValueType;
            }
        }
    }
    else
    {
        status = instance.bindSignal(source.nodeId, port.signal);
        if (status != Result::Ok)
        {
            return status;
        }
    }
    ports.push_back(std::move(port));
    return Result::Ok;
}

auto NamedIo::create(Instance& instance, const NamedIoPort* ports, std::size_t count,
                     std::size_t eventCapacity, std::unique_ptr<NamedIo>& out,
                     const NamedIoIdentity& identity) noexcept -> Result
{
    out.reset();
    if ((count && !ports) || count > 256 || !eventCapacity || eventCapacity > 8192)
    {
        return Result::InvalidArgument;
    }
    try
    {
        auto impl = std::make_unique<Impl>(instance, eventCapacity);
        impl->identity = identity;
        impl->ports.reserve(count);
        for (auto i = std::size_t{}; i < count; ++i)
        {
            auto result = impl->addPort(ports[i]);
            if (result != Result::Ok)
            {
                return result;
            }
        }
        auto outputCount = std::size_t{};
        auto ownerMatrixCount = std::size_t{};
        for (const auto& port : impl->ports)
        {
            if (port.kind == NamedIoKind::OutputProperty)
            {
                ++outputCount;
            }
            if (port.kind == NamedIoKind::OwnerMatrix)
            {
                ++ownerMatrixCount;
            }
        }
        if (outputCount > eventCapacity || ownerMatrixCount > 1)
        {
            return Result::InvalidArgument;
        }
        out.reset(new NamedIo(std::move(impl)));
        auto result = instance.setSignalObserver(&NamedIo::observeSignal, out->impl_.get());
        if (result != Result::Ok)
        {
            out.reset();
            return result;
        }
        out->impl_->observerAttached = true;
        return Result::Ok;
    }
    catch (const std::bad_alloc&)
    {
        return Result::InsufficientWork;
    }
    catch (...)
    {
        return Result::InvalidArgument;
    }
}
}
