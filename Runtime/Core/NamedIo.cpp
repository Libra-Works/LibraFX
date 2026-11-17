#include "liberafx/NamedIo.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <new>
#include <string>
#include <utility>
#include <vector>

namespace liberafx {
namespace {
auto inlineJob(void*, Job job, void* context) noexcept -> bool
{
    job(context);
    return true;
}

auto validName(const char* name, std::size_t& length) noexcept -> bool
{
    length = 0;
    if (!name) return false;
    for (; length <= 128 && name[length]; ++length)
        if (static_cast<unsigned char>(name[length]) < 32 || name[length] == 127) return false;
    return length > 0 && length <= 128 && name[0] != ' ' && name[length - 1] != ' ';
}

struct Port final {
    std::string name;
    NamedIoKind kind{};
    std::array<std::uint8_t, 16> nodeId{};
    std::array<std::array<std::uint8_t, 16>, 4> nodeIds{};
    std::uint8_t width = 0;
    bool isColor = false;
    std::uint8_t nodeCount = 1;
    std::array<ParameterHandle, 16> parameters{};
    std::array<ValueHandle, 4> values{};
    SignalHandle signal{};
    std::array<double, 16> previous{};
    bool published = false;
};
}

struct NamedIo::Impl final {
    Instance& instance;
    std::vector<Port> ports;
    std::vector<NamedIoEvent> events;
    std::size_t head = 0;
    std::size_t count = 0;
    bool started = false;
    bool executing = false;
    bool dispatching = false;
    bool observerAttached = false;
    NamedIoIdentity identity{};

    explicit Impl(Instance& value, std::size_t capacity) : instance(value), events(capacity) {}
    auto find(const char* name, NamedIoKind kind) noexcept -> Port*
    {
        auto length = std::size_t{};
        if (!validName(name, length)) return nullptr;
        for (auto& port : ports)
            if (port.kind == kind && port.name.size() == length &&
                std::memcmp(port.name.data(), name, length) == 0) return &port;
        return nullptr;
    }
    void append(std::size_t index, const double* values) noexcept
    {
        const auto& port = ports[index];
        auto& event = events[(head + count) % events.size()];
        event = {};
        event.identity = identity;
        event.portIndex = static_cast<std::uint32_t>(index);
        event.kind = port.kind;
        event.width = port.width;
        event.isColor = port.isColor;
        if (values) for (auto lane = 0u; lane < port.width; ++lane) event.values[lane] = values[lane];
        ++count;
    }
    Result publishProperties() noexcept;
};

namespace {
struct ExecutionScope final {
    bool& flag;
    explicit ExecutionScope(bool& value) noexcept : flag(value) { flag = true; }
    ~ExecutionScope() { flag = false; }
};
}

NamedIo::NamedIo(std::unique_ptr<Impl> impl) noexcept : impl_(std::move(impl)) {}
NamedIo::~NamedIo()
{
    if (impl_->observerAttached) (void)impl_->instance.setSignalObserver(nullptr, nullptr);
}

auto NamedIo::observeSignal(void* context, const std::uint8_t nodeId[16]) noexcept -> Result
{
    auto& impl = *static_cast<Impl*>(context);
    if (std::any_of(impl.ports.begin(), impl.ports.end(), [&](const auto& port) {
            return port.kind == NamedIoKind::InputSignal &&
                std::memcmp(port.nodeId.data(), nodeId, 16) == 0;
        }))
        return Result::Ok;
    for (auto index = std::size_t{}; index < impl.ports.size(); ++index) {
        const auto& port = impl.ports[index];
        if (port.kind != NamedIoKind::OutputSignal ||
            std::memcmp(port.nodeId.data(), nodeId, 16) != 0) continue;
        if (impl.count == impl.events.size()) return Result::QueueRejected;
        impl.append(index, nullptr);
        break;
    }
    return Result::Ok;
}

auto NamedIo::create(Instance& instance, const NamedIoPort* ports, std::size_t count,
                     std::size_t eventCapacity, std::unique_ptr<NamedIo>& out,
                     const NamedIoIdentity& identity) noexcept -> Result
{
    out.reset();
    if ((count && !ports) || count > 256 || !eventCapacity || eventCapacity > 8192)
        return Result::InvalidArgument;
    try {
        auto impl = std::make_unique<Impl>(instance, eventCapacity);
        impl->identity = identity;
        impl->ports.reserve(count);
        for (auto i = std::size_t{}; i < count; ++i) {
            const auto& source = ports[i];
            auto length = std::size_t{};
            if (!validName(source.name, length)) return Result::InvalidArgument;
            auto name = std::string(source.name, length);
            for (const auto& existing : impl->ports)
                if (existing.name == name && existing.kind != NamedIoKind::OwnerMatrix &&
                    source.kind != NamedIoKind::OwnerMatrix) return Result::InvalidArgument;
            auto property = source.kind == NamedIoKind::InputProperty ||
                            source.kind == NamedIoKind::OutputProperty ||
                            source.kind == NamedIoKind::OwnerMatrix;
            auto signal = source.kind == NamedIoKind::InputSignal ||
                          source.kind == NamedIoKind::OutputSignal;
            auto matrix = source.width == 16;
            auto nodeCount = static_cast<unsigned>(source.nodeCount == 0 ? 1 : source.nodeCount);
            if ((!property && !signal) || (signal && (source.width || source.isColor || nodeCount != 1)) ||
                (source.kind == NamedIoKind::OwnerMatrix && (!matrix || source.isColor)) ||
                (property && (!matrix && (source.width < 1 || source.width > 4 ||
                    (source.isColor && source.width != 4))) ||
                    (matrix && (source.isColor || nodeCount != 4)) ||
                    (!matrix && nodeCount != 1))) return Result::InvalidArgument;
            auto port = Port{};
            port.name = std::move(name);
            port.kind = source.kind;
            std::memcpy(port.nodeId.data(), source.nodeId, 16);
            port.nodeIds[0] = port.nodeId;
            for (auto row = 1u; row < nodeCount; ++row)
                std::memcpy(port.nodeIds[row].data(), source.additionalNodeIds[row - 1], 16);
            port.width = source.width;
            port.isColor = source.isColor;
            port.nodeCount = static_cast<std::uint8_t>(nodeCount);
            auto status = Result::Ok;
            if (source.kind == NamedIoKind::InputProperty || source.kind == NamedIoKind::OwnerMatrix) {
                auto rowWidth = matrix ? 4u : source.width;
                for (auto row = 0u; row < nodeCount; ++row) {
                    for (auto lane = 0u; lane < rowWidth; ++lane) {
                        auto index = row * rowWidth + lane;
                        status = instance.bindParameter(port.nodeIds[row].data(), 0,
                            static_cast<std::uint8_t>(lane), port.parameters[index]);
                        if (status != Result::Ok) return status;
                        auto current = double{};
                        status = instance.parameter(port.parameters[index], current);
                        if (status == Result::Ok)
                            status = instance.setUnconnectedParameter(port.nodeIds[row].data(), 0,
                                static_cast<std::uint8_t>(lane), current);
                        if (status != Result::Ok) return status;
                    }
                }
            } else if (source.kind == NamedIoKind::OutputProperty) {
                auto rowWidth = matrix ? 4u : source.width;
                for (auto row = 0u; row < nodeCount; ++row) {
                    status = instance.bindValue(port.nodeIds[row].data(), port.values[row]);
                    if (status != Result::Ok) return status;
                    if (port.values[row].componentCount != rowWidth) return Result::InvalidValueType;
                }
            } else {
                status = instance.bindSignal(source.nodeId, port.signal);
                if (status != Result::Ok) return status;
            }
            impl->ports.push_back(std::move(port));
        }
        auto outputCount = std::size_t{};
        auto ownerMatrixCount = std::size_t{};
        for (const auto& port : impl->ports)
        {
            if (port.kind == NamedIoKind::OutputProperty) ++outputCount;
            if (port.kind == NamedIoKind::OwnerMatrix) ++ownerMatrixCount;
        }
        if (outputCount > eventCapacity || ownerMatrixCount > 1) return Result::InvalidArgument;
        out.reset(new NamedIo(std::move(impl)));
        auto result = instance.setSignalObserver(&NamedIo::observeSignal, out->impl_.get());
        if (result != Result::Ok) {
            out.reset();
            return result;
        }
        out->impl_->observerAttached = true;
        return Result::Ok;
    } catch (const std::bad_alloc&) { return Result::InsufficientWork; }
    catch (...) { return Result::InvalidArgument; }
}

auto NamedIo::setProperty(const char* name, const double* values, std::size_t width) noexcept -> Result
{
    if (impl_->executing) return Result::Busy;
    auto port = impl_->find(name, NamedIoKind::InputProperty);
    if (!port) return Result::NodeNotFound;
    if (!values || width != port->width) return Result::InvalidValueType;
    for (auto lane = std::size_t{}; lane < width; ++lane)
        if (!std::isfinite(values[lane])) return Result::NonFinite;
    auto old = std::array<double, 16>{};
    for (auto lane = std::size_t{}; lane < width; ++lane) {
        auto result = impl_->instance.parameter(port->parameters[lane], old[lane]);
        if (result != Result::Ok) return result;
    }
    for (auto lane = std::size_t{}; lane < width; ++lane) {
        auto result = impl_->instance.setParameter(port->parameters[lane], values[lane]);
        if (result != Result::Ok) {
            for (auto previous = std::size_t{}; previous < lane; ++previous)
                (void)impl_->instance.setParameter(port->parameters[previous], old[previous]);
            return result;
        }
    }
    return Result::Ok;
}

auto NamedIo::setOwnerMatrix(const double* values, std::size_t width) noexcept -> Result
{
    if (impl_->executing) return Result::Busy;
    auto port = static_cast<Port*>(nullptr);
    for (auto& candidate : impl_->ports)
        if (candidate.kind == NamedIoKind::OwnerMatrix) {
            port = &candidate;
            break;
        }
    if (!port) return Result::NodeNotFound;
    if (!values || width != 16) return Result::InvalidValueType;
    for (auto lane = std::size_t{}; lane < width; ++lane)
        if (!std::isfinite(values[lane])) return Result::NonFinite;
    auto old = std::array<double, 16>{};
    for (auto lane = 0u; lane < width; ++lane) {
        auto result = impl_->instance.parameter(port->parameters[lane], old[lane]);
        if (result != Result::Ok) return result;
    }
    for (auto lane = 0u; lane < width; ++lane) {
        auto result = impl_->instance.setParameter(port->parameters[lane], values[lane]);
        if (result != Result::Ok) {
            for (auto previous = 0u; previous < lane; ++previous)
                (void)impl_->instance.setParameter(port->parameters[previous], old[previous]);
            return result;
        }
    }
    return Result::Ok;
}

auto NamedIo::Impl::publishProperties() noexcept -> Result
{
    auto pending = std::size_t{};
    for (const auto& port : ports) {
        if (port.kind != NamedIoKind::OutputProperty) continue;
        auto values = std::array<double, 16>{};
        auto result = Result::Ok;
        for (auto row = 0u; row < port.nodeCount; ++row) {
            result = instance.value(port.values[row], values.data() + row * (port.width == 16 ? 4 : port.width));
            if (result != Result::Ok) return result;
        }
        auto changed = !port.published;
        for (auto lane = 0u; lane < port.width; ++lane) changed |= values[lane] != port.previous[lane];
        if (changed) ++pending;
    }
    if (pending > events.size() - count) return Result::QueueRejected;
    for (auto i = std::size_t{}; i < ports.size(); ++i) {
        auto& port = ports[i];
        if (port.kind != NamedIoKind::OutputProperty) continue;
        auto values = std::array<double, 16>{};
        auto result = Result::Ok;
        for (auto row = 0u; row < port.nodeCount; ++row) {
            result = instance.value(port.values[row], values.data() + row * (port.width == 16 ? 4 : port.width));
            if (result != Result::Ok) return result;
        }
        auto changed = !port.published;
        for (auto lane = 0u; lane < port.width; ++lane) changed |= values[lane] != port.previous[lane];
        if (changed) {
            append(i, values.data());
            port.previous = values;
            port.published = true;
        }
    }
    return Result::Ok;
}

auto NamedIo::publishProperties() noexcept -> Result
{
    return impl_->executing ? Result::Busy : impl_->publishProperties();
}

auto NamedIo::start(double time, const std::uint8_t* onStartNodeId,
                    std::size_t instructionBudget) noexcept -> Result
{
    return startInternal(time, onStartNodeId, instructionBudget, nullptr, nullptr);
}

auto NamedIo::startCaptured(double time, const std::uint8_t* onStartNodeId,
                            std::size_t instructionBudget, const std::uint8_t lifeNodeId[16],
                            Lifetime& lifetime) noexcept -> Result
{
    if (!lifeNodeId || impl_->started) return Result::InvalidArgument;
    return startInternal(time, onStartNodeId, instructionBudget, lifeNodeId, &lifetime);
}

auto NamedIo::startInternal(double time, const std::uint8_t* onStartNodeId,
                            std::size_t instructionBudget, const std::uint8_t* lifeNodeId,
                            Lifetime* lifetime) noexcept -> Result
{
    if (impl_->executing) return Result::Busy;
    if (impl_->started) return Result::Ok;
    if (onStartNodeId && !instructionBudget) return Result::InvalidArgument;
    auto entry = SignalHandle{};
    if (onStartNodeId) {
        auto status = impl_->instance.bindSignal(onStartNodeId, entry);
        if (status != Result::Ok) return status;
    }
    auto life = ValueHandle{};
    if (lifeNodeId) {
        auto status = impl_->instance.bindValue(lifeNodeId, life);
        if (status != Result::Ok) return status;
        if (life.componentCount != 1) return Result::InvalidValueType;
    }
    auto scope = ExecutionScope{impl_->executing};
    auto status = impl_->instance.updateAsync(time, inlineJob, nullptr);
    if (status != Result::Ok) return status;
    status = impl_->instance.result();
    if (status != Result::Ok) return status;
    if (lifeNodeId) {
        auto value = std::array<double, 4>{};
        status = impl_->instance.value(life, value.data());
        if (status != Result::Ok) return status;
        status = Lifetime::finite(value[0], *lifetime);
        if (status != Result::Ok) return status;
    }
    status = impl_->publishProperties();
    if (status != Result::Ok) return status;
    // A partially traversed signal must not be replayed after an error.
    impl_->started = true;
    if (!onStartNodeId) return Result::Ok;
    auto dispatch = ExecutionScope{impl_->dispatching};
    return impl_->instance.dispatchSignal(entry, time, instructionBudget);
}

auto NamedIo::update(double time) noexcept -> Result
{
    if (impl_->executing) return Result::Busy;
    if (!impl_->started) return Result::NotReady;
    auto scope = ExecutionScope{impl_->executing};
    auto status = impl_->instance.updateAsync(time, inlineJob, nullptr);
    if (status != Result::Ok) return status;
    status = impl_->instance.result();
    return status == Result::Ok ? impl_->publishProperties() : status;
}

auto NamedIo::fireSignal(const char* name, double time, std::size_t instructionBudget) noexcept -> Result
{
    if (impl_->executing) return Result::Busy;
    if (!impl_->started) return Result::NotReady;
    auto port = impl_->find(name, NamedIoKind::InputSignal);
    if (!port) return Result::NodeNotFound;
    auto scope = ExecutionScope{impl_->executing};
    auto dispatch = ExecutionScope{impl_->dispatching};
    return impl_->instance.dispatchSignal(port->signal, time, instructionBudget);
}

auto NamedIo::fireLifecycleSignal(const SignalHandle& signal, double time, std::size_t instructionBudget) noexcept -> Result
{
    if (impl_->executing) return Result::Busy;
    if (!impl_->started) return Result::NotReady;
    auto scope = ExecutionScope{impl_->executing};
    auto dispatch = ExecutionScope{impl_->dispatching};
    return impl_->instance.dispatchSignal(signal, time, instructionBudget);
}

auto NamedIo::fireDeferredSignal(double time, std::size_t instructionBudget) noexcept -> Result
{
    if (impl_->executing) return Result::Busy;
    if (!impl_->started) return Result::NotReady;
    auto scope = ExecutionScope{impl_->executing};
    auto dispatch = ExecutionScope{impl_->dispatching};
    return impl_->instance.dispatchDeferredSignal(time, instructionBudget);
}

auto NamedIo::emitSignal(const char* name) noexcept -> Result
{
    if (!impl_->dispatching) return Result::Busy;
    auto port = impl_->find(name, NamedIoKind::OutputSignal);
    if (!port) return Result::NodeNotFound;
    if (impl_->count == impl_->events.size()) return Result::QueueRejected;
    impl_->append(static_cast<std::size_t>(port - impl_->ports.data()), nullptr);
    return Result::Ok;
}

auto NamedIo::popEvent(NamedIoEvent& out) noexcept -> bool
{
    if (impl_->executing || !impl_->count) return false;
    out = impl_->events[impl_->head];
    impl_->head = (impl_->head + 1) % impl_->events.size();
    --impl_->count;
    return true;
}

auto NamedIo::peekEvent(NamedIoEvent& out) const noexcept -> bool
{
    if (impl_->executing || !impl_->count) return false;
    out = impl_->events[impl_->head];
    return true;
}

auto NamedIo::portName(std::size_t index) const noexcept -> const char*
{
    return index < impl_->ports.size() ? impl_->ports[index].name.c_str() : nullptr;
}
}
