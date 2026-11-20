#pragma once
#include "liberafx/LbProceduralPixel.h"
#include "liberafx/host/LbFlexiblePlaneAdapter.h"
#include <cstring>
#include <memory>

namespace liberafx::procedural_adapter {

using Input = flexible_adapter::Input;
using Read = flexible_adapter::Read;
struct Definition
{
    std::uint32_t kind = 0, resolution = 256, blend = 0;
    bool doubleSided = true;
    float emission = 1;
    std::uint32_t textureMask = 0;
    LibraProceduralModelDescriptor model{};
    std::vector<LibraProceduralModelModifier> modifiers;
    LibraProceduralPixelProgram pixel{};
    std::vector<LibraProceduralPixelOperation> operations;
    std::vector<Input> inputs;
};

// PFX1 is deliberately bounded and endian-explicit. It is not a memory dump of
// engine objects. C ABI structures contain only uint32 and IEEE float32 fields.
inline auto decode(const std::uint8_t* bytes, std::size_t count, Definition& output) -> LibraResult
{
    const auto endian = std::uint32_t{1};
    if (!bytes || count < 48 || count > 4096 || *reinterpret_cast<const std::uint8_t*>(&endian) != 1)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    std::uint32_t header[8]{};
    std::memcpy(header, bytes, sizeof(header));
    auto emission = 0.0f;
    std::memcpy(&emission, bytes + 24, sizeof(emission));
    if (header[0] != 0x31584650u || header[1] != 1 || header[2] > 2 || header[3] < 16 ||
        header[3] > 1024 || (header[3] & (header[3] - 1)) != 0 || header[4] > 1 || header[5] > 1 ||
        !std::isfinite(emission) || emission < 0 || emission > 64 || header[7] != 0)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    auto candidate = Definition{};
    candidate.kind = header[2]; candidate.resolution = header[3]; candidate.blend = header[4];
    candidate.doubleSided = header[5] != 0; candidate.emission = emission;
    if (candidate.kind == 0)
    {
        if (count < 116)
        {
            return LIBRA_INVALID_ARGUMENT;
        }
        std::memcpy(&candidate.model, bytes + 32, sizeof(candidate.model));
        auto size = std::uint32_t{};
        std::memcpy(&size, bytes + 112, sizeof(size));
        if (size > LIBRA_PROCEDURAL_MAX_MODIFIERS || count != 116 + size * sizeof(LibraProceduralModelModifier))
        {
            return LIBRA_INVALID_ARGUMENT;
        }
        candidate.modifiers.resize(size);
        if (size != 0)
        {
            std::memcpy(candidate.modifiers.data(), bytes + 116, size * sizeof(LibraProceduralModelModifier));
        }
        auto counts = LibraProceduralModelCounts{};
        const auto result = libra_procedural_model_predict(&candidate.model, candidate.modifiers.data(), size, &counts);
        if (result != LIBRA_OK)
        {
            return result;
        }
    }
    else
    {
        std::memcpy(&candidate.pixel, bytes + 32, sizeof(candidate.pixel));
        const auto size = candidate.pixel.operationCount;
        if (size == 0 || size > LIBRA_PIXEL_MAX_OPERATIONS || count != 48 + size * sizeof(LibraProceduralPixelOperation) ||
            candidate.pixel.domain != (candidate.kind == 2 ? LIBRA_PIXEL_DOMAIN_MATERIAL : LIBRA_PIXEL_DOMAIN_TEXTURE))
        {
            return LIBRA_INVALID_ARGUMENT;
        }
        candidate.operations.resize(size);
        std::memcpy(candidate.operations.data(), bytes + 48, size * sizeof(LibraProceduralPixelOperation));
        auto usesTime = std::uint32_t{};
        const auto result = libra_procedural_pixel_validate(&candidate.pixel, candidate.operations.data(), size, &usesTime);
        if (result != LIBRA_OK)
        {
            return result;
        }
    }
    output = std::move(candidate);
    return LIBRA_OK;
}

inline auto read(const Input& input, const Read& source, float* output) -> LibraResult
{
    if (input.width == 0 || input.width > 4 || input.component + input.width > 4)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    auto values = input.defaults;
    auto width = static_cast<std::uint8_t>(input.width);
    if (input.connected)
    {
        if (!source)
        {
            return LIBRA_INVALID_ARGUMENT;
        }
        const auto result = source(input.source, values.data(), width);
        if (result != LIBRA_OK || width < input.component + input.width)
        {
            return result == LIBRA_OK ? LIBRA_INVALID_ARGUMENT : result;
        }
    }
    for (auto i = 0u; i < input.width; ++i)
    {
        const auto value = values[input.connected ? input.component + i : i];
        if (!std::isfinite(value) || std::abs(value) > 1000000)
        {
            return LIBRA_NON_FINITE;
        }
        output[i] = static_cast<float>(value);
    }
    return LIBRA_OK;
}

class Resource final
{
public:
    explicit Resource(const Definition& definition) : definition_(definition), modifiers_(definition.modifiers),
        operations_(definition.operations) {}
    Resource(const Resource&) = delete;
    auto operator=(const Resource&) -> Resource& = delete;
    ~Resource()
    {
        libra_procedural_model_release(model_);
    }
    auto initialize() -> LibraResult
    {
        const auto blocks = definition_.kind == 0 ? 4u : 3u;
        const auto stages = definition_.kind == 0 ? modifiers_.size() : operations_.size();
        if (definition_.inputs.size() != stages * blocks)
        {
            return LIBRA_INVALID_ARGUMENT;
        }
        if (definition_.kind == 0)
        {
            return libra_procedural_model_create(&definition_.model, modifiers_.data(),
                static_cast<std::uint32_t>(modifiers_.size()), &model_);
        }
        const auto result = libra_procedural_pixel_validate(&definition_.pixel, operations_.data(),
            static_cast<std::uint32_t>(operations_.size()), &usesTime_);
        return result == LIBRA_OK && !validTextureSlots() ? LIBRA_INVALID_ARGUMENT : result;
    }
    // Pixel operation arrays remain borrowed until next update; engine GPU adapters
    // copy changed contents into retained upload storage before submitting RT work.
    auto update(const Read& source, double time, bool& dirty) -> LibraResult
    {
        if (!std::isfinite(time) || std::abs(time) > 1e9)
        {
            return LIBRA_INVALID_ARGUMENT;
        }
        const auto blocks = definition_.kind == 0 ? 4u : 3u;
        auto valuesChanged = false;
        for (auto i = std::size_t{}; i < definition_.inputs.size(); ++i)
        {
            auto values = std::array<float, 4>{};
            const auto result = read(definition_.inputs[i], source, values.data());
            if (result != LIBRA_OK)
            {
                return result;
            }
            auto* target = definition_.kind == 0 ? modifiers_[i / blocks].values + (i % blocks) * 4 :
                operations_[i / blocks].values + (i % blocks) * 4;
            if (std::memcmp(target, values.data(), sizeof(values)) != 0)
            {
                std::memcpy(target, values.data(), sizeof(values));
                valuesChanged = true;
            }
        }
        if (definition_.kind == 0)
        {
            auto changed = std::uint32_t{};
            const auto result = libra_procedural_model_evaluate(model_, &definition_.model, modifiers_.data(),
                static_cast<std::uint32_t>(modifiers_.size()), time, &changed);
            dirty = changed != 0 || first_;
            if (result == LIBRA_OK)
            {
                first_ = false;
            }
            return result;
        }
        if (valuesChanged || !pixelValid_)
        {
            const auto result = libra_procedural_pixel_validate(&definition_.pixel, operations_.data(),
                static_cast<std::uint32_t>(operations_.size()), &usesTime_);
            if (result != LIBRA_OK || !validTextureSlots())
            {
                pixelValid_ = false;
                return result == LIBRA_OK ? LIBRA_INVALID_ARGUMENT : result;
            }
            pixelValid_ = true;
        }
        dirty = first_ || valuesChanged || (usesTime_ != 0 && time != previousTime_);
        first_ = false; previousTime_ = time;
        return LIBRA_OK;
    }
    auto view(LibraProceduralModelView& output) const -> LibraResult
    {
        return libra_procedural_model_view(model_, &output);
    }
    auto operations() const -> const std::vector<LibraProceduralPixelOperation>& { return operations_; }
    auto definition() const -> const Definition& { return definition_; }
private:
    auto validTextureSlots() const -> bool
    {
        for (const auto& op : operations_)
        {
            if (op.kind == LIBRA_PIXEL_TEXTURE_SAMPLE &&
                (definition_.textureMask & (1u << static_cast<std::uint32_t>(op.values[0]))) == 0)
            {
                return false;
            }
        }
        return true;
    }
    Definition definition_;
    std::vector<LibraProceduralModelModifier> modifiers_;
    std::vector<LibraProceduralPixelOperation> operations_;
    LibraProceduralModelInstance* model_ = nullptr;
    std::uint32_t usesTime_ = 0;
    bool first_ = true;
    bool pixelValid_ = true;
    double previousTime_ = 0;
};
}
