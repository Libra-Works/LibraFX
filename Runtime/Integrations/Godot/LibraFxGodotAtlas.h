#pragma once
#include "liberafx/LbParticleAtlas.h"
#include <cstring>
#include <godot_cpp/variant/dictionary.hpp>
namespace liberafx::godot_adapter {
inline auto readParticleAtlas(const godot::Variant& value, ParticleAtlas& out) -> bool {
    if (value.get_type() == godot::Variant::NIL) { out = {}; return true; }
    if (value.get_type() != godot::Variant::DICTIONARY) return false;
    const auto object = godot::Dictionary(value);
    auto atlas = ParticleAtlas{};
    for (const auto field : {"enabled","loop","overLife"}) {
        const auto item = object.get(field,godot::Variant{});
        if (item.get_type() != godot::Variant::BOOL) return false;
        if (std::strcmp(field,"enabled")==0) atlas.enabled = item;
        if (std::strcmp(field,"loop")==0) atlas.loop = item;
        if (std::strcmp(field,"overLife")==0) atlas.overLife = item;
    }
    for (const auto field : {"columns","rows","startFrame","fps"}) {
        const auto item = object.get(field,godot::Variant{});
        if (item.get_type() != godot::Variant::INT && item.get_type() != godot::Variant::FLOAT) return false;
        const auto number = static_cast<double>(item);
        if (!std::isfinite(number) || number < 0 || number > 1000) return false;
        if (std::strcmp(field,"fps")==0) atlas.fps = number;
        else {
            if (std::floor(number)!=number) return false;
            if (std::strcmp(field,"columns")==0) atlas.columns=static_cast<std::uint32_t>(number);
            if (std::strcmp(field,"rows")==0) atlas.rows=static_cast<std::uint32_t>(number);
            if (std::strcmp(field,"startFrame")==0) atlas.startFrame=static_cast<std::uint32_t>(number);
        }
    }
    if (!atlas.valid()) return false;
    out = atlas; return true;
}
}
