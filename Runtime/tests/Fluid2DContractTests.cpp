#include "liberafx/gpu/LbFluid2D.h"

#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <vector>

namespace liberafx::tests {

auto requireFluid(bool condition, const char* message) -> void
{
    if (!condition)
    {
        throw std::runtime_error(message);
    }
}

auto verifyFluidContract(const char* fixture) -> void
{
    using namespace gpu;
    auto settings = Fluid2DSettings{};
    settings.width = 24;
    settings.height = 40;
    settings.pressureIterations = 39;
    auto plan = Fluid2DPlan{};
    requireFluid(makeFluid2DPlan(settings, plan), "Valid fluid plan rejected.");
    for (auto index = std::uint32_t{0}; index < plan.count; index++)
    {
        const auto& pass = plan.passes[index];
        for (const auto input : {pass.read0, pass.read1})
        {
            requireFluid(input == Fluid2DField::None ||
                (input != pass.write0 && input != pass.write1), "Fluid read/write resource alias.");
        }
    }
    requireFluid(plan.passes[plan.count - 4].read1 == Fluid2DField::PressureScratch,
        "Odd pressure iteration projection uses the wrong ping-pong field.");
    auto frame = Fluid2DFrame{};
    requireFluid(makeFluid2DFrame(settings, 0, frame) && frame.substeps == 0,
        "Zero delta must not dispatch the fluid source.");
    requireFluid(makeFluid2DFrame(settings, 2, frame) && frame.substeps == settings.maximumSubsteps &&
        frame.stepDelta <= settings.maximumStep &&
        std::abs(frame.discardedDelta + frame.stepDelta * frame.substeps - 2) < 0.000001,
        "Fluid frame budget mismatch.");
    requireFluid(!makeFluid2DFrame(settings, -1, frame) &&
        !makeFluid2DFrame(settings, std::numeric_limits<double>::quiet_NaN(), frame),
        "Invalid fluid delta accepted.");
    auto invalid = settings;
    invalid.width = 0;
    const auto previousCount = plan.count;
    requireFluid(!makeFluid2DPlan(invalid, plan) && plan.count == previousCount,
        "Failed fluid plan must not replace the previous plan.");
    if (fixture == nullptr || *fixture == '\0')
    {
        return;
    }
    auto stream = std::ifstream(fixture, std::ios::binary);
    requireFluid(stream.good(), "Cannot read compiler-produced fluid contract fixture.");
    const auto read = [&]() {
        auto bytes = std::array<unsigned char, 4>{};
        stream.read(reinterpret_cast<char*>(bytes.data()), bytes.size());
        requireFluid(stream.good(), "Truncated compiler-produced fluid contract fixture.");
        return static_cast<std::uint32_t>(bytes[0]) |
            (static_cast<std::uint32_t>(bytes[1]) << 8u) |
            (static_cast<std::uint32_t>(bytes[2]) << 16u) |
            (static_cast<std::uint32_t>(bytes[3]) << 24u);
    };
    requireFluid(read() == settings.width && read() == settings.height &&
        read() == settings.pressureIterations, "Compiler fluid dimensions differ from native planning.");
    const auto uniforms = makeFluid2DUniforms(settings, settings.maximumStep);
    auto bits = std::array<std::uint32_t, 20>{};
    std::memcpy(bits.data(), &uniforms, sizeof(uniforms));
    for (const auto value : bits)
    {
        requireFluid(read() == value, "Compiler/native fluid uniform ABI mismatch.");
    }
    requireFluid(read() == plan.count, "Compiler/native fluid dispatch counts differ.");
    for (auto index = std::uint32_t{0}; index < plan.count; index++)
    {
        const auto& pass = plan.passes[index];
        requireFluid(read() == static_cast<std::uint32_t>(pass.pass) &&
            read() == static_cast<std::uint32_t>(pass.read0) &&
            read() == static_cast<std::uint32_t>(pass.read1) &&
            read() == static_cast<std::uint32_t>(pass.write0) &&
            read() == static_cast<std::uint32_t>(pass.write1),
            "Compiler/native fluid dependency or ping-pong plan mismatch.");
    }
    requireFluid(stream.peek() == std::char_traits<char>::eof(), "Unexpected fluid contract fixture tail.");
}

}

auto main() -> int
{
    try
    {
        liberafx::tests::verifyFluidContract(std::getenv("LIBERAFX_FLUID_CONTRACT_FIXTURE"));
        std::cout << "Portable fluid settings, field dependencies and compiler/native ABI passed.\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
