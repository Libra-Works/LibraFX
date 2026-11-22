#include "liberafx/LbParticleSprite.h"
namespace liberafx
{
Result ParticleSpriteAnimation::configure(const double *values, std::size_t count, std::size_t parts) noexcept
{
    if (parts > 32 || (count && !values) || count % 4 || count < parts * 12 || count > 1'200'000)
    {
        return Result::InvalidBinary;
    }
    for (auto i = std::size_t{}; i < count; ++i)
    {
        if (!std::isfinite(values[i]) || std::abs(values[i]) > 1.e30)
        {
            return Result::NonFinite;
        }
    }
    auto cursor = parts * 3;
    for (auto part = std::size_t{}; part < parts; ++part)
    {
        const auto *h = values + part * 12;
        if (h[0] <= 0 || h[0] > 10000 || h[1] <= 0 || h[1] > 4'096'000'000.0 || !integer(h[2], count / 4) ||
            h[2] != cursor || !integer(h[3], 4096) || h[3] < 1 || !integer(h[4], count / 4) || !integer(h[5], 256) ||
            !integer(h[6], 8192) || h[6] < 1 || !integer(h[7], 8192) || h[7] < 1 || !integer(h[8], 1) ||
            !integer(h[9], 1) || h[10] != 0 || h[11] != 0)
        {
            return Result::InvalidBinary;
        }
        const auto frames = static_cast<std::size_t>(h[3]), keys = static_cast<std::size_t>(h[5]);
        if (frames > (count / 4 - cursor) / 2)
        {
            return Result::InvalidBinary;
        }
        auto previous = 0.0;
        for (auto frame = std::size_t{}; frame < frames; ++frame)
        {
            const auto *f = values + (cursor + frame * 2) * 4;
            if (f[0] <= previous || f[0] > h[1] || f[1] < 0 || f[2] < 0 || f[3] <= 0 || f[4] <= 0 ||
                f[1] + f[3] > 1.000001 || f[2] + f[4] > 1.000001 || !integer(f[5], 8192) || f[5] < 1 ||
                !integer(f[6], 8192) || f[6] < 1 || !integer(f[7], 511))
            {
                return Result::InvalidBinary;
            }
            previous = f[0];
        }
        cursor += frames * 2;
        if (h[4] != cursor || keys > (count / 4 - cursor) / 2)
        {
            return Result::InvalidBinary;
        }
        previous = -1;
        for (auto key = std::size_t{}; key < keys; ++key)
        {
            const auto *k = values + (cursor + key * 2) * 4;
            if (k[0] <= previous || k[0] < 0 || k[0] > h[1] || k[6] != 0 || k[7] != 0)
            {
                return Result::InvalidBinary;
            }
            previous = k[0];
        }
        cursor += keys * 2;
    }
    if (cursor != count / 4)
    {
        return Result::InvalidBinary;
    }
    try
    {
        auto candidate = std::vector<double>{};
        if (count)
        {
            candidate.assign(values, values + count);
        }
        values_ = std::move(candidate);
        parts_ = parts;
        return Result::Ok;
    }
    catch (...)
    {
        return Result::InsufficientWork;
    }
}
} // namespace liberafx
