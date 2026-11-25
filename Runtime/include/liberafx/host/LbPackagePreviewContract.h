#pragma once

#include "liberafx/LbLivePreview.h"
#include "liberafx/LbSpawnableHost.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <vector>

namespace liberafx::host {

struct ParameterDeltaUpdate final {
    std::array<std::uint8_t, 16> containerId{};
    std::array<std::uint8_t, 16> nodeId{};
    std::uint32_t inputSlot = 0;
    std::uint32_t component = 0;
    bool isInt32 = false;
    std::int32_t intValue = 0;
    double floatValue = 0.0;
    bool reset = false;
    bool isCurve = false;
    std::vector<CurveKey> curveKeys;
};

// Every engine preview integration must implement all methods of this contract.
class IPackagePreviewAdapter {
public:
    virtual ~IPackagePreviewAdapter() = default;
    [[nodiscard]] virtual auto loadPackage(const std::uint8_t* bytes, std::size_t byteCount,
        bool playing, double playbackRate) -> Result = 0;
    [[nodiscard]] virtual auto setPlayback(bool playing, double playbackRate) -> Result = 0;
    // Older adapters cannot safely interpret an FCurve as scalar lanes.
    [[nodiscard]] virtual auto supportsCurveDeltas() const noexcept -> bool { return false; }
    [[nodiscard]] virtual auto applyParameterDelta(const ParameterDeltaUpdate* updates,
        std::size_t updateCount) -> Result = 0;
    virtual void resetPreview() = 0;
    [[nodiscard]] virtual auto hasBuiltinSpriteMaterials() const -> bool = 0;
};

struct PreviewClockState final {
    double lastEditorTime = 0.0;
    bool hasEditorTime = false;
};

LIBRA_RUNTIME_CORE_API auto computePreviewTimeStep(PreviewClockState& clock,
    double targetTimeSeconds, double currentHostTimeSeconds, bool playing,
    bool& outNeedsRestart, double& outStepDelta, int& outStepCount) noexcept -> void;

LIBRA_RUNTIME_CORE_API auto parseParameterDeltaJson(const char* jsonUtf8, std::size_t length,
    std::vector<ParameterDeltaUpdate>& outUpdates) -> Result;

LIBRA_RUNTIME_CORE_API auto applyParameterDeltaJson(SpawnableHost& host,
    const char* jsonUtf8, std::size_t length) -> Result;

LIBRA_RUNTIME_CORE_API auto dispatchPackagePreviewFrame(const livepreview::FrameView& frame,
    IPackagePreviewAdapter& adapter) -> Result;

}
