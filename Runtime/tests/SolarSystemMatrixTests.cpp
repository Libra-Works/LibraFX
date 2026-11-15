#include "liberafx/VfxRuntime.h"
#include <cmath>
#include <cstdint>
#include <fstream>
#include <iostream>
#include <iterator>
#include <new>
#include <vector>

namespace {
auto enqueue(void*, liberafx::Job job, void* context) noexcept -> bool
{
    job(context);
    return true;
}
}

// Executes fixtures exported by SolarSystemSampleTests through the production VM.
int main(int argc, char** argv)
{
    if (argc != 3) return 2;
    auto file = std::ifstream(argv[1], std::ios::binary);
    auto bytes = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(file), {});
    auto checks = std::ifstream(argv[2]);
    if (bytes.size() < 16 || !checks) return 2;
    auto count = bytes[8] | (std::uint32_t{bytes[9]} << 8) |
        (std::uint32_t{bytes[10]} << 16) | (std::uint32_t{bytes[11]} << 24);
    auto bindings = std::vector<liberafx::CustomNodeBinding>(count);
    auto program = liberafx::Program{};
    if (liberafx::Program::load(bytes.data(), bytes.size(), nullptr, 0, bindings.data(), bindings.size(), program)
        != liberafx::Result::Ok) return 3;
    auto alignment = std::align_val_t{program.workAlignment()};
    auto work = static_cast<double*>(::operator new(program.workBytes(), alignment));
    auto passed = true;
    auto checked = 0u;
    {
        auto instance = liberafx::Instance{};
        if (instance.initialize(program, work, program.workBytes() / sizeof(double)) != liberafx::Result::Ok)
            passed = false;
        auto time = double{};
        auto index = std::size_t{};
        auto width = unsigned{};
        while (passed && checks >> time >> index >> width)
        {
            if (width > 4 || index >= program.nodeCount()) { passed = false; break; }
            if (instance.updateAsync(time, enqueue, nullptr) != liberafx::Result::Ok ||
                instance.result() != liberafx::Result::Ok) { passed = false; break; }
            for (auto lane = 0u; lane < width; ++lane)
            {
                auto expected = double{};
                if (!(checks >> expected) || !std::isfinite(work[index * 4 + lane]) ||
                    std::abs(work[index * 4 + lane] - expected) > 1e-8)
                {
                    std::cerr << "Matrix mismatch at time " << time << ", slot " << index
                        << ", lane " << lane << ": " << work[index * 4 + lane] << " != " << expected << '\n';
                    passed = false;
                }
            }
            ++checked;
        }
    }
    ::operator delete(work, alignment);
    if (!passed || checked == 0) return 4;
    std::cout << checked << " production-runtime matrix/renderer-binding checks passed.\n";
    return 0;
}
