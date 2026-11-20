#pragma once
#include "liberafx/host/LbProceduralAdapter.h"
#include <unordered_map>

namespace liberafx::procedural_adapter {
using Identity = std::vector<std::uint8_t>;

// IDs, authoring labels and effect owners are deliberately absent. Equality is
// checked against the complete bytes, so a hash collision never aliases output.
inline auto staticIdentity(const Definition& definition) -> Identity
{
    for (const auto& input : definition.inputs)
    {
        if (input.connected)
        {
            return {};
        }
    }
    auto modifiers = definition.modifiers;
    auto operations = definition.operations;
    const auto blocks = definition.kind == 0 ? 4u : 3u;
    const auto stages = definition.kind == 0 ? modifiers.size() : operations.size();
    if (definition.inputs.size() != stages * blocks)
    {
        return {};
    }
    for (auto i = std::size_t{}; i < definition.inputs.size(); ++i)
    {
        auto* values = definition.kind == 0 ? modifiers[i / blocks].values + i % blocks * 4 :
            operations[i / blocks].values + i % blocks * 4;
        if (definition.inputs[i].width != 4 || read(definition.inputs[i], {}, values) != LIBRA_OK)
        {
            return {};
        }
    }
    if (definition.kind == 0)
    {
        if ((definition.model.flags & (4u | 8u)) != 0)
        {
            return {};
        }
        for (const auto& modifier : modifiers)
        {
            if ((modifier.flags & 16u) != 0 ||
                ((modifier.kind == 3 || modifier.kind == 5) && modifier.values[3] != 0))
            {
                return {};
            }
        }
    }
    else
    {
        auto time = std::uint32_t{};
        if (libra_procedural_pixel_validate(&definition.pixel, operations.data(), operations.size(), &time) != LIBRA_OK || time != 0)
        {
            return {};
        }
    }
    auto key = Identity{};
    const auto append = [&](const void* data, std::size_t size)
    {
        const auto* bytes = static_cast<const std::uint8_t*>(data);
        key.insert(key.end(), bytes, bytes + size);
    };
    append(&definition.kind, sizeof(definition.kind));
    append(&definition.resolution, sizeof(definition.resolution));
    append(&definition.blend, sizeof(definition.blend));
    append(&definition.doubleSided, sizeof(definition.doubleSided));
    append(&definition.emission, sizeof(definition.emission));
    append(&definition.textureMask, sizeof(definition.textureMask));
    if (definition.kind == 0)
    {
        append(&definition.model, sizeof(definition.model));
        if (!modifiers.empty()) append(modifiers.data(), modifiers.size() * sizeof(modifiers[0]));
    }
    else
    {
        append(&definition.pixel, sizeof(definition.pixel));
        append(operations.data(), operations.size() * sizeof(operations[0]));
    }
    return key;
}

inline auto appendDependency(Identity& key, const Identity& dependency) -> bool
{
    if (key.empty() || dependency.empty())
    {
        key.clear();
        return false;
    }
    const auto size = static_cast<std::uint32_t>(dependency.size());
    const auto* bytes = reinterpret_cast<const std::uint8_t*>(&size);
    key.insert(key.end(), bytes, bytes + sizeof(size));
    key.insert(key.end(), dependency.begin(), dependency.end());
    return true;
}

template<class T> class StaticResourceCache final
{
    struct Hash
    {
        auto operator()(const Identity& key) const noexcept -> std::size_t
        {
            auto hash = std::size_t{2166136261u};
            for (const auto value : key) hash = (hash ^ value) * 16777619u;
            return hash;
        }
    };
    std::unordered_map<Identity, std::weak_ptr<T>, Hash> entries_;
public:
    // Engine adapters call on their owner thread. Weak entries retain no GPU
    // objects: the last effect lease releases the output immediately.
    auto find(const Identity& key) -> std::shared_ptr<T>
    {
        const auto found = entries_.find(key);
        return found == entries_.end() ? nullptr : found->second.lock();
    }
    void insert(const Identity& key, const std::shared_ptr<T>& value)
    {
        if (key.empty()) return;
        for (auto entry = entries_.begin(); entry != entries_.end();)
        {
            if (entry->second.expired()) entry = entries_.erase(entry);
            else ++entry;
        }
        if (entries_.size() < 256) entries_[key] = value;
    }
};
}
