#pragma once
#include "liberafx/host/LbProceduralAdapter.h"
#include <godot_cpp/classes/texture2drd.hpp>
#include <memory>

namespace liberafx::godot_adapter {
class ProceduralGpu final
{
public:
    ProceduralGpu();
    ~ProceduralGpu();
    auto initialize(const procedural_adapter::Definition& definition,
        const std::array<godot::Ref<godot::Texture2DRD>, 4>& textures, godot::String& error) -> bool;
    auto update(const std::vector<LibraProceduralPixelOperation>& operations, double time) -> bool;
    auto texture() const -> godot::Ref<godot::Texture2DRD>;
private:
    struct State;
    std::shared_ptr<State> state_;
};
}
