#pragma once
#include "liberafx/LbParticleSprite.h"
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/array.hpp>
#include <cmath>

namespace liberafx::godot_adapter
{
inline Result readParticleSprite(const godot::Variant &value, bool &present, ParticleSpriteAnimation &output)
{
    present = value.get_type() != godot::Variant::NIL;
    if (!present)
    {
        return Result::Ok;
    }
    if (value.get_type() != godot::Variant::DICTIONARY)
    {
        return Result::InvalidBinary;
    }
    const auto object = godot::Dictionary(value);
    const auto number = [](const godot::Variant &raw, double &out)
    {
        if (raw.get_type() != godot::Variant::INT && raw.get_type() != godot::Variant::FLOAT)
        {
            return false;
        }
        out = raw;
        return std::isfinite(out);
    };
    auto version = 0.0, parts = 0.0;
    if (!number(object.get("version", godot::Variant{}), version) || version != 1 ||
        !number(object.get("partCount", godot::Variant{}), parts) || parts < 0 || parts > 32 ||
        std::floor(parts) != parts || object.get("values", godot::Variant{}).get_type() != godot::Variant::ARRAY)
    {
        return Result::InvalidBinary;
    }
    const auto array = godot::Array(object["values"]);
    if (array.size() > 1'200'000)
    {
        return Result::InvalidBinary;
    }
    auto values = std::vector<double>(array.size());
    for (auto i = 0; i < array.size(); ++i)
    {
        if (!number(array[i], values[i]))
        {
            return Result::InvalidBinary;
        }
    }
    return output.configure(values.data(), values.size(), static_cast<std::size_t>(parts));
}
} // namespace liberafx::godot_adapter
