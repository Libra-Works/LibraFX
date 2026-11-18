#include "LbNamedIoInternal.h"

namespace liberafx {

auto NamedIo::start(double time, const std::uint8_t* onStartNodeId,
                    std::size_t instructionBudget) noexcept -> Result
{
    return startInternal(time, onStartNodeId, instructionBudget, nullptr, nullptr);
}

auto NamedIo::startCaptured(double time, const std::uint8_t* onStartNodeId,
                            std::size_t instructionBudget, const std::uint8_t lifeNodeId[16],
                            Lifetime& lifetime) noexcept -> Result
{
    if (!lifeNodeId || impl_->started)
    {
        return Result::InvalidArgument;
    }
    return startInternal(time, onStartNodeId, instructionBudget, lifeNodeId, &lifetime);
}

auto NamedIo::startInternal(double time, const std::uint8_t* onStartNodeId,
                            std::size_t instructionBudget, const std::uint8_t* lifeNodeId,
                            Lifetime* lifetime) noexcept -> Result
{
    if (impl_->executing)
    {
        return Result::Busy;
    }
    if (impl_->started)
    {
        return Result::Ok;
    }
    if (onStartNodeId && !instructionBudget)
    {
        return Result::InvalidArgument;
    }
    auto entry = SignalHandle{};
    if (onStartNodeId)
    {
        auto status = impl_->instance.bindSignal(onStartNodeId, entry);
        if (status != Result::Ok)
        {
            return status;
        }
    }
    auto life = ValueHandle{};
    if (lifeNodeId)
    {
        auto status = impl_->instance.bindValue(lifeNodeId, life);
        if (status != Result::Ok)
        {
            return status;
        }
        if (life.componentCount != 1)
        {
            return Result::InvalidValueType;
        }
    }
    auto scope = named_io_detail::ExecutionScope{impl_->executing};
    auto status = impl_->instance.updateAsync(time, named_io_detail::inlineJob, nullptr);
    if (status != Result::Ok)
    {
        return status;
    }
    status = impl_->instance.result();
    if (status != Result::Ok)
    {
        return status;
    }
    if (lifeNodeId)
    {
        auto value = std::array<double, 4>{};
        status = impl_->instance.value(life, value.data());
        if (status != Result::Ok)
        {
            return status;
        }
        status = Lifetime::finite(value[0], *lifetime);
        if (status != Result::Ok)
        {
            return status;
        }
    }
    status = impl_->publishProperties();
    if (status != Result::Ok)
    {
        return status;
    }

    // A partially traversed signal must not be replayed after an error.
    impl_->started = true;
    impl_->instructionBudget = instructionBudget ? instructionBudget : 10000;
    auto dispatch = named_io_detail::ExecutionScope{impl_->dispatching};
    status = impl_->instance.dispatchGeneratedSignals(impl_->instructionBudget);
    if (status != Result::Ok)
    {
        return status;
    }
    status = impl_->publishProperties();
    if (status != Result::Ok || !onStartNodeId)
    {
        return status;
    }
    status = impl_->instance.dispatchSignal(entry, time, instructionBudget);
    return status == Result::Ok ? impl_->publishProperties() : status;
}

auto NamedIo::update(double time) noexcept -> Result
{
    if (impl_->executing)
    {
        return Result::Busy;
    }
    if (!impl_->started)
    {
        return Result::NotReady;
    }
    auto scope = named_io_detail::ExecutionScope{impl_->executing};
    auto status = impl_->instance.updateAsync(time, named_io_detail::inlineJob, nullptr);
    if (status != Result::Ok)
    {
        return status;
    }
    status = impl_->instance.result();
    if (status != Result::Ok)
    {
        return status;
    }
    auto dispatch = named_io_detail::ExecutionScope{impl_->dispatching};
    status = impl_->instance.dispatchGeneratedSignals(impl_->instructionBudget);
    return status == Result::Ok ? impl_->publishProperties() : status;
}

auto NamedIo::fireSignal(const char* name, double time,
                         std::size_t instructionBudget) noexcept -> Result
{
    if (impl_->executing)
    {
        return Result::Busy;
    }
    if (!impl_->started)
    {
        return Result::NotReady;
    }
    auto port = impl_->find(name, NamedIoKind::InputSignal);
    if (!port)
    {
        return Result::NodeNotFound;
    }
    auto scope = named_io_detail::ExecutionScope{impl_->executing};
    auto dispatch = named_io_detail::ExecutionScope{impl_->dispatching};
    auto status = impl_->instance.dispatchSignal(port->signal, time, instructionBudget);
    return status == Result::Ok ? impl_->publishProperties() : status;
}

auto NamedIo::fireLifecycleSignal(const SignalHandle& signal, double time,
                                 std::size_t instructionBudget) noexcept -> Result
{
    if (impl_->executing)
    {
        return Result::Busy;
    }
    if (!impl_->started)
    {
        return Result::NotReady;
    }
    auto scope = named_io_detail::ExecutionScope{impl_->executing};
    auto dispatch = named_io_detail::ExecutionScope{impl_->dispatching};
    auto status = impl_->instance.dispatchSignal(signal, time, instructionBudget);
    return status == Result::Ok ? impl_->publishProperties() : status;
}

auto NamedIo::fireDeferredSignal(double time,
                                std::size_t instructionBudget) noexcept -> Result
{
    if (impl_->executing)
    {
        return Result::Busy;
    }
    if (!impl_->started)
    {
        return Result::NotReady;
    }
    auto scope = named_io_detail::ExecutionScope{impl_->executing};
    auto dispatch = named_io_detail::ExecutionScope{impl_->dispatching};
    auto status = impl_->instance.dispatchDeferredSignal(time, instructionBudget);
    return status == Result::Ok ? impl_->publishProperties() : status;
}

}
