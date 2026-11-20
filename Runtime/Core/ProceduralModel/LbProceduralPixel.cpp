#include "liberafx/LbProceduralPixel.h"
#include "liberafx/procedural/LbProceduralNoise.h"
#include <algorithm>
#include <array>
#include <cmath>

namespace liberafx::procedural_pixel {
using Pixel = std::array<float, 4>;
static auto saturate(float x) -> float { return std::clamp(x, 0.0f, 1.0f); }
static auto smooth(float lo, float hi, float x) -> float
{
    const auto t = hi == lo ? (x >= hi ? 1.0f : 0.0f) : saturate((x - lo) / (hi - lo));
    return t * t * (3 - 2 * t);
}
static auto scalar(float x) -> Pixel { return {x, x, x, x}; }
static auto fractal(float x, float y, const LibraProceduralPixelOperation& op, bool ridged) -> float
{
    auto result = 0.0f, weight = 1.0f, total = 0.0f;
    const auto octaves = static_cast<unsigned>(op.values[4]);
    for (auto i = 0u; i < octaves; ++i)
    {
        auto value = procedural_noise::value2(x, y, op.seed + i * 0x9e3779b9u);
        if (ridged)
        {
            value = 1 - std::abs(2 * value - 1);
        }
        result += weight * value;
        total += weight;
        weight *= op.values[5];
        x *= 2;
        y *= 2;
    }
    return result / total;
}
}

extern "C" LIBRA_PROCEDURAL_API LibraResult LIBRA_INTEROP_CALL libra_procedural_pixel_validate(
    const LibraProceduralPixelProgram* program, const LibraProceduralPixelOperation* operations,
    uint32_t count, uint32_t* usesTime)
{
    if (!program || !operations || !usesTime || program->version != LIBRA_PIXEL_VERSION ||
        count == 0 || count > LIBRA_PIXEL_MAX_OPERATIONS || program->operationCount != count ||
        program->outputRegister == 0 || program->outputRegister > count || program->domain > 1)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    uint32_t time = 0;
    for (auto i = 0u; i < count; ++i)
    {
        const auto& op = operations[i];
        if (op.kind >= LIBRA_PIXEL_KIND_COUNT || op.inputA > i || op.inputB > i)
        {
            return LIBRA_INVALID_ARGUMENT;
        }
        for (auto value : op.values)
        {
            if (!std::isfinite(value) || std::abs(value) > 1000000)
            {
                return LIBRA_NON_FINITE;
            }
        }
        if ((op.kind == LIBRA_PIXEL_FRACTAL || op.kind == LIBRA_PIXEL_RIDGED) &&
            (op.values[4] < 1 || op.values[4] > static_cast<float>(LIBRA_PIXEL_MAX_OCTAVES) ||
             op.values[4] != std::floor(op.values[4]) || op.values[5] < 0 || op.values[5] > 1))
        {
            return LIBRA_INVALID_ARGUMENT;
        }
        if ((op.kind == LIBRA_PIXEL_CHANNEL && (op.values[0] < 0 || op.values[0] > 3 ||
            op.values[0] != std::floor(op.values[0]))) ||
            (op.kind == LIBRA_PIXEL_TEXTURE_SAMPLE && (program->domain != LIBRA_PIXEL_DOMAIN_MATERIAL ||
            op.values[0] < 0 || op.values[0] >= static_cast<float>(LIBRA_PIXEL_MAX_TEXTURES) || op.values[0] != std::floor(op.values[0]))))
        {
            return LIBRA_INVALID_ARGUMENT;
        }
        if (op.kind == LIBRA_PIXEL_TIME ||
            ((op.kind >= LIBRA_PIXEL_VALUE_NOISE && op.kind <= LIBRA_PIXEL_CURL) &&
             (op.values[2] != 0 || op.values[3] != 0)))
        {
            time = 1;
        }
    }
    *usesTime = time;
    return LIBRA_OK;
}

extern "C" LIBRA_PROCEDURAL_API LibraResult LIBRA_INTEROP_CALL libra_procedural_pixel_sample(
    const LibraProceduralPixelProgram* program, const LibraProceduralPixelOperation* operations,
    uint32_t count, float u, float v, double time, LibraProceduralPixelSampleTexture sampler,
    void* context, float rgba[4])
{
    using namespace liberafx::procedural_pixel;
    namespace noise = liberafx::procedural_noise;
    uint32_t usesTime = 0;
    const auto validation = libra_procedural_pixel_validate(program, operations, count, &usesTime);
    if (validation != LIBRA_OK)
    {
        return validation;
    }
    if (!rgba || !std::isfinite(u) || !std::isfinite(v) || !std::isfinite(time) || std::abs(time) > 1e9)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    std::array<Pixel, LIBRA_PIXEL_MAX_OPERATIONS + 1> registers{};
    registers[0] = {u, v, 0, 1};
    for (auto i = 0u; i < count; ++i)
    {
        const auto& op = operations[i];
        const auto& a = registers[op.inputA];
        const auto& b = registers[op.inputB];
        const auto* p = op.values;
        auto result = a;
        const auto x = static_cast<float>(a[0] * p[0] + time * p[2]);
        const auto y = static_cast<float>(a[1] * p[1] + time * p[3]);
        switch (op.kind)
        {
        case LIBRA_PIXEL_UV: result = registers[0]; break;
        case LIBRA_PIXEL_CONSTANT: result = {p[0], p[1], p[2], p[3]}; break;
        case LIBRA_PIXEL_TIME: result = scalar(static_cast<float>(time)); break;
        case LIBRA_PIXEL_TRANSFORM_UV:
        {
            const auto angle = p[4] * 0.0174532925199433f;
            const auto sx = (a[0] - p[5]) * p[0], sy = (a[1] - p[6]) * p[1];
            result = {std::cos(angle) * sx - std::sin(angle) * sy + p[5] + p[2],
                std::sin(angle) * sx + std::cos(angle) * sy + p[6] + p[3], a[2], a[3]};
            break;
        }
        case LIBRA_PIXEL_LINEAR: result = scalar(a[0] * p[0] + a[1] * p[1] + p[2]); break;
        case LIBRA_PIXEL_RADIAL: result = scalar(std::hypot(a[0] - p[0], a[1] - p[1]) * p[2]); break;
        case LIBRA_PIXEL_RING: result = scalar(1 - smooth(p[3], p[3] + std::max(p[4], 1e-6f),
            std::abs(std::hypot(a[0] - p[0], a[1] - p[1]) - p[2]))); break;
        case LIBRA_PIXEL_STRIPES: result = scalar(.5f + .5f * std::sin((a[0] * p[0] + a[1] * p[1] + p[2]) * 6.28318530718f)); break;
        case LIBRA_PIXEL_CHECKER: result = scalar(std::fmod(std::abs(std::floor(a[0] * p[0]) + std::floor(a[1] * p[1])), 2)); break;
        case LIBRA_PIXEL_VALUE_NOISE: result = scalar(noise::value2(x, y, op.seed)); break;
        case LIBRA_PIXEL_GRADIENT_NOISE: result = scalar(noise::gradient2(x, y, op.seed)); break;
        case LIBRA_PIXEL_CELLULAR: result = scalar(noise::cellular2(x, y, op.seed, false)); break;
        case LIBRA_PIXEL_CELL_EDGES: result = scalar(noise::cellular2(x, y, op.seed, true)); break;
        case LIBRA_PIXEL_FRACTAL: result = scalar(fractal(x, y, op, false)); break;
        case LIBRA_PIXEL_RIDGED: result = scalar(fractal(x, y, op, true)); break;
        case LIBRA_PIXEL_CURL:
        {
            constexpr float e = .01f;
            result = {(noise::value2(x, y + e, op.seed) - noise::value2(x, y - e, op.seed)) / (2 * e),
                -(noise::value2(x + e, y, op.seed) - noise::value2(x - e, y, op.seed)) / (2 * e), 0, 1};
            break;
        }
        case LIBRA_PIXEL_WARP_UV: result = {a[0] + (b[0] - p[1]) * p[0], a[1] + (b[1] - p[1]) * p[0], a[2], a[3]}; break;
        case LIBRA_PIXEL_CHANNEL: result = scalar(a[static_cast<unsigned>(p[0])]); break;
        case LIBRA_PIXEL_ALPHA_MASK: result[3] = a[3] * b[0]; break;
        case LIBRA_PIXEL_TEXTURE_SAMPLE:
        {
            if (!sampler)
            {
                return LIBRA_INVALID_ARGUMENT;
            }
            const auto status = sampler(context, static_cast<unsigned>(p[0]), a[0], a[1], result.data());
            if (status != LIBRA_OK)
            {
                return status;
            }
            break;
        }
        default:
            for (auto c = 0u; c < 4; ++c)
            {
                switch (op.kind)
                {
                case LIBRA_PIXEL_ADD: result[c] = a[c] + b[c]; break;
                case LIBRA_PIXEL_MULTIPLY: result[c] = a[c] * b[c]; break;
                case LIBRA_PIXEL_SUBTRACT: result[c] = a[c] - b[c]; break;
                case LIBRA_PIXEL_MIN: result[c] = std::min(a[c], b[c]); break;
                case LIBRA_PIXEL_MAX: result[c] = std::max(a[c], b[c]); break;
                case LIBRA_PIXEL_LERP: result[c] = a[c] + (b[c] - a[c]) * p[0]; break;
                case LIBRA_PIXEL_POWER: result[c] = std::pow(std::max(a[c], 1e-6f), std::clamp(p[0], -32.0f, 32.0f)); break;
                case LIBRA_PIXEL_SATURATE: result[c] = saturate(a[c]); break;
                case LIBRA_PIXEL_INVERT: result[c] = 1 - a[c]; break;
                case LIBRA_PIXEL_SMOOTHSTEP: result[c] = smooth(p[0], p[1], a[c]); break;
                case LIBRA_PIXEL_COLOR_RAMP: result[c] = p[c] + (p[c + 4] - p[c]) * smooth(p[8], p[9], a[0]); break;
                default: break;
                }
            }
            break;
        }
        for (const auto value : result)
        {
            if (!std::isfinite(value) || std::abs(value) > 1e20f)
            {
                return LIBRA_NON_FINITE;
            }
        }
        registers[i + 1] = result;
    }
    std::copy(registers[program->outputRegister].begin(), registers[program->outputRegister].end(), rgba);
    return LIBRA_OK;
}
