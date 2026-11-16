#pragma once

#include "liberafx/VfxRuntime.h"
#include <array>

namespace liberafx {

inline constexpr auto signalPassthroughTypeId = std::array<std::uint8_t, 16>{
    0x98, 0x9f, 0xa5, 0xb5, 0x48, 0x5f, 0x4e, 0x75, 0x85, 0x42, 0x9c, 0x55, 0, 0, 0, 1};

inline auto signalPassthrough(CustomNodeInvocation& call) noexcept -> Result
{
    call.selectedBranch = 0;
    return Result::Ok;
}

}
