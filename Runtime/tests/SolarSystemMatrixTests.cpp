#include "liberafx/LbVfxRuntime.h"
#include "liberafx/LbSpawnablePackage.h"
#include <array>
#include <cmath>
#include <cstdint>
#include <fstream>
#include <iostream>
#include <iterator>
#include <new>
#include <string>
#include <vector>

namespace {
auto parseNodeId(const std::string& text, std::array<std::uint8_t, 16>& nodeId) -> bool
{
    if (text.size() != nodeId.size() * 2)
    {
        return false;
    }
    auto hexValue = [](char value) -> int
    {
        if (value >= '0' && value <= '9')
        {
            return value - '0';
        }
        if (value >= 'a' && value <= 'f')
        {
            return value - 'a' + 10;
        }
        if (value >= 'A' && value <= 'F')
        {
            return value - 'A' + 10;
        }
        return -1;
    };
    for (auto index = std::size_t{}; index < nodeId.size(); ++index)
    {
        auto high = hexValue(text[index * 2]);
        auto low = hexValue(text[index * 2 + 1]);
        if (high < 0 || low < 0)
        {
            return false;
        }
        nodeId[index] = static_cast<std::uint8_t>((high << 4) | low);
    }
    return true;
}
auto enqueue(void*, liberafx::Job job, void* context) noexcept -> bool
{
    job(context);
    return true;
}
}

// Executes fixtures exported by SolarSystemSampleTests through the production VM.
int main(int argc, char** argv)
{
    if (argc != 3)
    {
        return 2;
    }
    auto file = std::ifstream(argv[1], std::ios::binary);
    auto packageBytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(file), {});
    auto checks = std::ifstream(argv[2]);
    auto package = liberafx::SpawnablePackage{};
    if (!checks || liberafx::loadSpawnablePackage(packageBytes.data(), packageBytes.size(), package) != liberafx::Result::Ok ||
        package.parentBytecode.size() < 16)
    {
        return 2;
    }
    auto& bytes = package.parentBytecode;
    auto count = bytes[8] | (std::uint32_t{bytes[9]} << 8) |
        (std::uint32_t{bytes[10]} << 16) | (std::uint32_t{bytes[11]} << 24);
    auto bindings = std::vector<liberafx::CustomNodeBinding>(count);
    auto program = liberafx::Program{};
    auto loaded = liberafx::Program::load(bytes.data(), bytes.size(), nullptr, 0, bindings.data(), bindings.size(), program);
    if (loaded != liberafx::Result::Ok)
    {
        std::cerr << "Matrix fixture load failed: " << static_cast<unsigned>(loaded) << '\n';
        return 3;
    }
    auto alignment = std::align_val_t{program.workAlignment()};
    auto workCount = program.workBytes() / sizeof(double) +
        (program.workBytes() % sizeof(double) == 0 ? 0 : 1);
    auto work = static_cast<double*>(::operator new(workCount * sizeof(double), alignment));
    auto passed = true;
    auto checked = 0u;
    {
        auto instance = liberafx::Instance{};
        auto initialized = instance.initialize(program, work, workCount);
        if (initialized != liberafx::Result::Ok)
        {
            std::cerr << "Matrix fixture instance initialization failed: "
                << static_cast<unsigned>(initialized) << '\n';
            passed = false;
        }
        auto time = double{};
        auto nodeIdText = std::string{};
        auto width = unsigned{};
        while (passed && checks >> time >> nodeIdText >> width)
        {
            auto nodeId = std::array<std::uint8_t, 16>{};
            if (width > 4 || !parseNodeId(nodeIdText, nodeId))
            {
                passed = false;
                break;
            }
            auto updated = instance.updateAsync(time, enqueue, nullptr);
            if (updated != liberafx::Result::Ok || instance.result() != liberafx::Result::Ok)
            {
                std::cerr << "Matrix fixture evaluation failed: " << static_cast<unsigned>(updated)
                    << '\n';
                passed = false;
                break;
            }
            auto values = std::array<double, 4>{};
            auto componentCount = std::uint8_t{};
            auto valueResult = instance.valueComponents(nodeId.data(), values.data(), componentCount);
            if (valueResult != liberafx::Result::Ok || componentCount < width)
            {
                std::cerr << "Matrix fixture node lookup failed for " << nodeIdText << ": "
                    << static_cast<unsigned>(valueResult) << ", components " << static_cast<unsigned>(componentCount)
                    << ", expected " << width << '\n';
                passed = false;
                break;
            }
            for (auto lane = 0u; lane < width; ++lane)
            {
                auto expected = double{};
                if (!(checks >> expected) || !std::isfinite(values[lane]) ||
                    std::abs(values[lane] - expected) > 1e-8)
                {
                    std::cerr << "Matrix mismatch at time " << time << ", node " << nodeIdText
                        << ", lane " << lane << ": " << values[lane] << " != " << expected << '\n';
                    passed = false;
                }
            }
            ++checked;
        }
    }
    ::operator delete(work, alignment);
    if (!passed || checked == 0)
    {
        if (checked == 0)
        {
            std::cerr << "Matrix fixture contained no completed checks.\n";
        }
        return 4;
    }
    std::cout << checked << " production-runtime matrix/renderer-binding checks passed.\n";
    return 0;
}
