#include "liberafx/LbParticleSprite.h"
#include <array>
#include <cmath>
#include <fstream>
#include <iostream>
#include <limits>
#include <stdexcept>

namespace liberafx::particle_sprite_tests
{
void require(bool valid, const char *message)
{
    if (!valid)
    {
        throw std::runtime_error(message);
    }
}
void near(double a, double b)
{
    require(std::abs(a - b) < 1e-10, "Particle sprite result changed.");
}
void contracts()
{
    auto values =
        std::vector<double>{10, 4,  3,  2,   7,  2,  64, 32, 1, 1, 0, 0, 1, .25, 0, .5, .5, 32, 16, 1,
                            4,  0,  .5, .25, .5, 16, 16, 0,  0, 0, 0, 0, 1, 1,   0, 0,  4,  1,  .5, 3.141592653589793,
                            2,  .5, 0,  0};
    auto clip = ParticleSpriteAnimation{};
    require(clip.configure(values.data(), values.size(), 1) == Result::Ok && clip.gpuCompatible(),
            "Valid sprite rejected.");
    require(clip.textureMatches(64, 32) && !clip.textureMatches(32, 64), "Texture dimensions were ignored.");
    auto sample = ParticleSpriteSample{};
    require(clip.sample(0, .05, sample) == Result::Ok && sample.cell == 1, "First sprite frame not selected.");
    near(sample.x, .125);
    near(sample.y, .0625);
    near(sample.rotation, 3.141592653589793 / 8);
    near(sample.scaleX, .5625);
    near(sample.scaleY, .46875);
    require(clip.sample(0, .1, sample) == Result::Ok && sample.cell == 0, "Frame boundary/hold changed.");
    require(clip.sample(0, .4, sample) == Result::Ok && sample.cell == 1, "Loop boundary changed.");
    auto invalid = values;
    invalid[2] = 100;
    require(clip.configure(invalid.data(), invalid.size(), 1) == Result::InvalidBinary,
            "Out-of-bounds frame records accepted.");
    require(clip.sample(0, .1, sample) == Result::Ok && sample.cell == 0, "Failed configuration damaged the old clip.");
    invalid = values;
    invalid[28] = std::numeric_limits<double>::quiet_NaN();
    require(clip.configure(invalid.data(), invalid.size(), 1) == Result::NonFinite, "Non-finite data accepted.");
    require(clip.sample(1, 0, sample) == Result::InvalidArgument &&
                clip.sample(0, -1, sample) == Result::InvalidArgument,
            "Invalid part/time accepted.");
    values[8] = 0;
    require(clip.configure(values.data(), values.size(), 1) == Result::Ok &&
                clip.sample(0, 100, sample) == Result::Ok && sample.cell == 0,
            "Non-loop final frame not held.");
    near(sample.x, 1);
    near(sample.rotation, 3.141592653589793);
    values[9] = 0;
    require(clip.configure(values.data(), values.size(), 1) == Result::Ok && clip.sample(0, 0, sample) == Result::Ok,
            "Plain sequence failed.");
    near(sample.scaleX, 1);
    near(sample.scaleY, 1);
    near(sample.x, 0);
    require(clip.configure(nullptr, 0, 0) == Result::Ok && clip.partCount() == 0, "Hidden composition not retained.");
}
void fixture(const char *path)
{
    auto stream = std::ifstream(path, std::ios::binary);
    std::uint32_t parts = 0, count = 0;
    stream.read(reinterpret_cast<char *>(&parts), 4);
    stream.read(reinterpret_cast<char *>(&count), 4);
    require(stream.good() && count <= 1'200'000, "Missing/invalid sprite fixture.");
    auto values = std::vector<double>(count);
    stream.read(reinterpret_cast<char *>(values.data()), count * sizeof(double));
    require(stream.good(), "Truncated sprite fixture.");
    auto clip = ParticleSpriteAnimation{};
    require(clip.configure(values.data(), values.size(), parts) == Result::Ok, "Compiler sprite fixture rejected.");
    std::uint32_t samples = 0;
    stream.read(reinterpret_cast<char *>(&samples), 4);
    for (auto i = 0u; i < samples; ++i)
    {
        std::uint32_t part = 0, cell = 0;
        double age = 0;
        std::array<double, 9> expected{};
        stream.read(reinterpret_cast<char *>(&part), 4);
        stream.read(reinterpret_cast<char *>(&age), 8);
        stream.read(reinterpret_cast<char *>(&cell), 4);
        stream.read(reinterpret_cast<char *>(expected.data()), expected.size() * 8);
        require(stream.good(), "Truncated sprite expectations.");
        auto actual = ParticleSpriteSample{};
        require(clip.sample(part, age, actual) == Result::Ok && actual.cell == cell, "Authored frame order changed.");
        for (auto lane = 0u; lane < 4; ++lane)
        {
            near(actual.uv[lane], expected[lane]);
        }
        near(actual.x, expected[4]);
        near(actual.y, expected[5]);
        near(actual.rotation, expected[6]);
        near(actual.scaleX, expected[7]);
        near(actual.scaleY, expected[8]);
    }
}
} // namespace liberafx::particle_sprite_tests
int main(int argc, char **argv)
{
    try
    {
        liberafx::particle_sprite_tests::contracts();
        for (auto i = 1; i < argc; ++i)
        {
            liberafx::particle_sprite_tests::fixture(argv[i]);
        }
        std::cout << "Particle sprite timing, poses, validation and authored fixture parity passed.\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
