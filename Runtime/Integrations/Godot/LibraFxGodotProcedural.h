#pragma once
#include "liberafx/LbSpawnableHost.h"
#include <godot_cpp/classes/node3d.hpp>
#include <godot_cpp/classes/texture2d.hpp>
#include <cstdint>
#include <memory>
#include <string>

namespace liberafx::godot_adapter {
class ProceduralPackageRenderer final
{
public:
    ProceduralPackageRenderer();
    ~ProceduralPackageRenderer();
    auto prepare(const SpawnablePackage& package) -> Result;
    auto attach(godot::Node3D& owner) -> Result;
    auto handleEvent(const SpawnableHostEvent& event) -> Result;
    auto update(SpawnableHost& host, double time) -> Result;
    void synchronize(bool visible, bool exiting);
    auto hasDraws() const -> bool;
    auto drawCount() const -> std::size_t;
    auto resolveResourceTexture(const std::uint8_t* container, std::uint64_t instance,
                                const std::string& resourceId, godot::Ref<godot::Texture2D>& outTexture,
                                int& outBlend, bool& outDoubleSided, float& outEmission) const -> bool;
private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
}
