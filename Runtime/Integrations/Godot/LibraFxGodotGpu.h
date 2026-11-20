#pragma once

#include "liberafx/LbSpawnableHost.h"
#include <godot_cpp/classes/node.hpp>
#include <godot_cpp/classes/ref.hpp>
#include <godot_cpp/classes/texture2d.hpp>
#include <godot_cpp/variant/string.hpp>
#ifdef LIBERAFX_GODOT_TEST_HOOKS
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/packed_float64_array.hpp>
#endif
#include <array>
#include <vector>
#include <memory>
#include <string>
#include <unordered_map>

namespace liberafx::godot_adapter {

struct GeneratedGpuDrawTexture final {
    std::array<std::uint8_t, 16> nodeId{}, producerNodeId{};
    std::int32_t slot = 0, association = 2, index = -1;
    godot::String name, outputId;
    bool mainTexture = false;
};

// Main-thread .lfxp GPU controller. Trusted compute and publication run on the
// render thread's global RenderingDevice; delivered shader text is never executed.
class PackageGpuRenderer final {
public:
    static auto create(const SpawnablePackage& package,
        const std::unordered_map<std::string, godot::Ref<godot::Texture2D>>& textureBindings,
        std::unique_ptr<PackageGpuRenderer>& out, godot::String& error) -> Result;
    ~PackageGpuRenderer();

    // create() succeeds without a renderer when the package has no GPU payload.
    auto attach(godot::Node& owner, godot::String& error) -> Result;
    auto handleEvent(const SpawnableHostEvent& event, godot::String& error) -> Result;
    auto advance(SpawnableHost& host, double elapsed, double delta, godot::String& error,
        double spawnPerSecond = 100.0) -> Result;
    auto emitterCount() const noexcept -> std::size_t;
    auto drawBindings(const std::array<std::uint8_t, 16>& container) const
        -> const std::vector<GeneratedGpuDrawTexture>&;
    auto outputTexture(const std::array<std::uint8_t, 16>& producer,
        const std::array<std::uint8_t, 16>& container, std::uint64_t instance,
        const godot::String& output) const -> godot::Ref<godot::Texture2D>;
#ifdef LIBERAFX_GODOT_TEST_HOOKS
    auto testReadParticleState(std::size_t emitterIndex, godot::Dictionary& fields, godot::String& error) -> Result;
    auto testReadFluidFields(godot::Dictionary& fields, godot::String& error) -> Result;
    auto testDisableFluidSourceAndZeroForces(godot::String& error) -> Result;
    auto testSetFluidPressureIterations(std::uint32_t iterations, godot::String& error) -> Result;
    auto testSetFluidDrawMatrix(const godot::PackedFloat64Array& values,
        godot::String& error) -> Result;
#endif

private:
    struct Impl;
    explicit PackageGpuRenderer(std::unique_ptr<Impl> impl) noexcept;
    std::unique_ptr<Impl> impl_;
};

}
