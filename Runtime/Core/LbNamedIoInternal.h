#pragma once

#include "liberafx/LbNamedIo.h"

#include <array>
#include <cstring>
#include <string>
#include <vector>

namespace liberafx {
namespace named_io_detail {

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

inline auto validName(const char* name, std::size_t& length) noexcept -> bool
{
    length = 0;
    if (!name)
    {
        return false;
    }
    for (; length <= 128 && name[length]; ++length)
    {
        if (static_cast<unsigned char>(name[length]) < 32 || name[length] == 127)
        {
            return false;
        }
    }
    return length > 0 && length <= 128 && name[0] != ' ' && name[length - 1] != ' ';
}

inline auto inlineJob(void*, Job job, void* context) noexcept -> bool
{
    job(context);
    return true;
}

struct ExecutionScope final {
    bool& flag;

    explicit ExecutionScope(bool& value) noexcept : flag(value)
    {
        flag = true;
    }

    ~ExecutionScope()
    {
        flag = false;
    }
};

}

struct NamedIo::Impl final {
    Instance& instance;
    std::vector<named_io_detail::Port> ports;
    std::vector<NamedIoEvent> events;
    std::size_t head = 0;
    std::size_t count = 0;
    std::size_t instructionBudget = 10000;
    bool started = false;
    bool executing = false;
    bool dispatching = false;
    bool observerAttached = false;
    NamedIoIdentity identity{};

    explicit Impl(Instance& value, std::size_t capacity) : instance(value), events(capacity) {}

    auto find(const char* name, NamedIoKind kind) noexcept -> named_io_detail::Port*
    {
        auto length = std::size_t{};
        if (!named_io_detail::validName(name, length))
        {
            return nullptr;
        }
        for (auto& port : ports)
        {
            if (port.kind == kind && port.name.size() == length &&
                std::memcmp(port.name.data(), name, length) == 0)
            {
                return &port;
            }
        }
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
        if (values)
        {
            for (auto lane = 0u; lane < port.width; ++lane)
            {
                event.values[lane] = values[lane];
            }
        }
        ++count;
    }

    auto addPort(const NamedIoPort& source) -> Result;
    auto setParameters(const ParameterHandle* parameters, const double* values,
                       std::size_t width) noexcept -> Result;
    auto readOutputValues(const named_io_detail::Port& port,
                          std::array<double, 16>& values) noexcept -> Result;
    auto outputChanged(const named_io_detail::Port& port,
                       const std::array<double, 16>& values) const noexcept -> bool;
    auto publishProperties() noexcept -> Result;
};

}
