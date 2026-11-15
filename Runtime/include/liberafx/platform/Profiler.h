#pragma once

#include <cstdint>

namespace liberafx {

enum class ProfileEvent : std::uint8_t {
    Load, Initialize, Cleanup, Schedule, Evaluate, Node, Signal,
    Bind, ReadValue, ReadParameter, WriteParameter, RenderSubmit, Count
};

inline const char* profileEventName(ProfileEvent event) noexcept
{
    switch (event) {
    case ProfileEvent::Load: return "Libra.Load";
    case ProfileEvent::Initialize: return "Libra.Initialize";
    case ProfileEvent::Cleanup: return "Libra.Cleanup";
    case ProfileEvent::Schedule: return "Libra.Schedule";
    case ProfileEvent::Evaluate: return "Libra.Evaluate";
    case ProfileEvent::Node: return "Libra.Node";
    case ProfileEvent::Signal: return "Libra.Signal";
    case ProfileEvent::Bind: return "Libra.Bind";
    case ProfileEvent::ReadValue: return "Libra.ReadValue";
    case ProfileEvent::ReadParameter: return "Libra.ReadParameter";
    case ProfileEvent::WriteParameter: return "Libra.WriteParameter";
    case ProfileEvent::RenderSubmit: return "Libra.RenderSubmit";
    default: return "Libra.Unknown";
    }
}

constexpr auto noProfileNode = std::uint32_t{0xffffffffu};
// A successful begin must be paired with end on the same thread. The host may
// return false when tracing is disabled. Callbacks must not re-enter the runtime.
using ProfileToken = std::uint64_t;
using BeginProfile = ProfileToken (*)(void* context, ProfileEvent event, std::uint32_t nodeIndex) noexcept;
using EndProfile = void (*)(void* context, ProfileToken token) noexcept;

struct ProfilingHooks final {
    void* context = nullptr;
    BeginProfile begin = nullptr;
    EndProfile end = nullptr;
    bool nodes = false;
    bool accessors = false;

    bool valid() const noexcept { return (begin == nullptr) == (end == nullptr); }
};

class ProfileScope final {
public:
    explicit ProfileScope(const ProfilingHooks& hooks, ProfileEvent event,
                          std::uint32_t nodeIndex = noProfileNode, bool enabled = true) noexcept
        : context_(hooks.context)
    {
        if (enabled && hooks.begin != nullptr && hooks.end != nullptr)
        {
            token_ = hooks.begin(context_, event, nodeIndex);
            if (token_ != 0) end_ = hooks.end;
        }
    }
    ~ProfileScope() { if (end_ != nullptr) end_(context_, token_); }
    ProfileScope(const ProfileScope&) = delete;
    ProfileScope& operator=(const ProfileScope&) = delete;

private:
    void* context_ = nullptr;
    EndProfile end_ = nullptr;
    ProfileToken token_ = 0;
};

}
