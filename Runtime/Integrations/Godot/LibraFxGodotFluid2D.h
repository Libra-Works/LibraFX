#pragma once

#include "liberafx/LbSpawnableHost.h"
#include "liberafx/gpu/LbFluid2D.h"
#include <godot_cpp/classes/node.hpp>
#include <godot_cpp/classes/node3d.hpp>
#ifdef LIBERAFX_GODOT_TEST_HOOKS
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/packed_float64_array.hpp>
#endif
#include <godot_cpp/variant/string.hpp>
#include <array>
#include <memory>
#include <vector>

namespace liberafx::godot_adapter {

using FluidNodeId = std::array<std::uint8_t, 16>;
using FluidMatrixRows = std::array<FluidNodeId, 4>;

void registerFluid2DClasses();

class PackageFluid2DRenderer;

class FluidRenderScopeNode final : public godot::Node3D {
    GDCLASS(FluidRenderScopeNode, godot::Node3D)

public:
    void setRenderer(PackageFluid2DRenderer* renderer);
    void _notification(int what);

protected:
    static void _bind_methods() {}

private:
    PackageFluid2DRenderer* renderer_ = nullptr;
};

struct Fluid2DProgram final {
    FluidNodeId nodeId{};
    FluidNodeId containerId{};
    FluidMatrixRows matrixRows{};
    liberafx::gpu::Fluid2DSettings settings{};
    float sizeScale = 100.0f;
    bool hasMatrix = false;
    GeneratorInstanceId instanceId = 0;
    double startTime = 0.0;
};

struct Fluid2DTemplate final {
    FluidNodeId containerId{};
    std::vector<Fluid2DProgram> programs;
};

class PackageFluid2DRenderer final {
public:
    static auto create(std::vector<Fluid2DProgram> rootPrograms,
        std::vector<Fluid2DTemplate> templates,
        std::unique_ptr<PackageFluid2DRenderer>& out, godot::String& error) -> Result;
    ~PackageFluid2DRenderer();

    auto attach(godot::Node& owner, godot::String& error) -> Result;
    auto handleEvent(const SpawnableHostEvent& event, godot::String& error) -> Result;
    auto advance(SpawnableHost& host, double elapsed, double delta, godot::String& error) -> Result;
    void synchronizePresentation(bool exiting = false);
    auto isEmpty() const noexcept -> bool;
#ifdef LIBERAFX_GODOT_TEST_HOOKS
    auto testReadFields(godot::Dictionary& fields, godot::String& error) -> Result;
    auto testDisableSourceAndZeroForces(godot::String& error) -> Result;
    auto testSetPressureIterations(std::uint32_t iterations, godot::String& error) -> Result;
    auto testSetDrawMatrix(const godot::PackedFloat64Array& values, godot::String& error) -> Result;
#endif

private:
    struct Impl;
    explicit PackageFluid2DRenderer(std::unique_ptr<Impl> impl) noexcept;
    std::unique_ptr<Impl> impl_;
};

}
