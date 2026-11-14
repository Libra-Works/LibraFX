#include "liberafx/VfxRuntime.h"

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iterator>
#include <thread>
#include <vector>

namespace {

struct Queue {
    liberafx::Job job = nullptr;
    void* data = nullptr;
};

auto enqueue(void* context, liberafx::Job job, void* data) noexcept -> bool
{
    auto queue = static_cast<Queue*>(context);
    if (queue->job != nullptr) return false;
    queue->job = job;
    queue->data = data;
    return true;
}

void run(Queue& queue)
{
    auto job = queue.job;
    auto data = queue.data;
    queue = {};
    assert(job != nullptr);
    job(data);
}

void write32(std::vector<std::uint8_t>& bytes, std::size_t offset, std::uint32_t value)
{
    for (auto index = 0u; index < 4; ++index)
        bytes[offset + index] = static_cast<std::uint8_t>(value >> (index * 8));
}

void writeDouble(std::vector<std::uint8_t>& bytes, std::size_t offset, double value)
{
    auto bits = std::uint64_t{};
    std::memcpy(&bits, &value, sizeof(value));
    for (auto index = 0u; index < 8; ++index)
        bytes[offset + index] = static_cast<std::uint8_t>(bits >> (index * 8));
}

auto fixture() -> std::vector<std::uint8_t>
{
    auto bytes = std::vector<std::uint8_t>(12 + 44 * 3);
    std::memcpy(bytes.data(), "LFXB", 4);
    bytes[4] = 1;
    bytes[6] = 1;
    write32(bytes, 8, 3);
    for (auto index = 0u; index < 3; ++index)
    {
        auto offset = 12 + index * 44;
        bytes[offset + 15] = static_cast<std::uint8_t>(index + 1);
        bytes[offset + 16] = static_cast<std::uint8_t>(index + 1);
        write32(bytes, offset + 20, 0xffffffffu);
        write32(bytes, offset + 24, 0xffffffffu);
    }
    writeDouble(bytes, 12 + 28, 3);
    write32(bytes, 12 + 2 * 44 + 20, 0);
    write32(bytes, 12 + 2 * 44 + 24, 1);
    return bytes;
}

void verify(const std::vector<std::uint8_t>& bytes)
{
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(bytes.data(), bytes.size(), program) == liberafx::Result::Ok);
    assert(program.nodeCount() == 3 && program.workBytes() == 3 * sizeof(double));
    auto work = std::array<double, 3>{};
    auto workOther = std::array<double, 3>{};
    auto instance = liberafx::Instance{};
    auto other = liberafx::Instance{};
    assert(instance.initialize(program, work.data(), 2) == liberafx::Result::InsufficientWork);
    assert(instance.initialize(program, work.data(), 3) == liberafx::Result::Ok);
    assert(other.initialize(program, workOther.data(), 3) == liberafx::Result::Ok);
    auto first = Queue{};
    auto second = Queue{};
    assert(instance.updateAsync(7, enqueue, &first) == liberafx::Result::Ok);
    assert(instance.updateAsync(8, enqueue, &first) == liberafx::Result::Busy);
    assert(instance.result() == liberafx::Result::NotReady);
    assert(other.updateAsync(2, enqueue, &second) == liberafx::Result::Ok);
    auto firstWorker = std::thread([&first] { run(first); });
    auto secondWorker = std::thread([&second] { run(second); });
    firstWorker.join();
    secondWorker.join();
    auto value = double{};
    auto id = bytes.data() + 12 + 2 * 44;
    assert(instance.value(id, value) == liberafx::Result::Ok && value == 10);
    assert(other.value(id, value) == liberafx::Result::Ok && value == 5);
    assert(instance.capacityGrowthCount() == 0);
}

}

auto main(int argc, char** argv) -> int
{
    auto bytes = fixture();
    verify(bytes);
    auto corrupt = bytes;
    corrupt[6] = 2;
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(corrupt.data(), corrupt.size(), program) == liberafx::Result::UnsupportedBinary);
    corrupt = bytes;
    write32(corrupt, 12 + 20, 0);
    assert(liberafx::Program::load(corrupt.data(), corrupt.size(), program) == liberafx::Result::InvalidBinary);
    corrupt = bytes;
    std::memcpy(corrupt.data() + 12 + 44, corrupt.data() + 12, 16);
    assert(liberafx::Program::load(corrupt.data(), corrupt.size(), program) == liberafx::Result::InvalidBinary);
    corrupt = bytes;
    corrupt[12 + 2 * 44 + 16] = 6;
    write32(corrupt, 12 + 2 * 44 + 24, 0xffffffffu);
    assert(liberafx::Program::load(corrupt.data(), corrupt.size(), program) == liberafx::Result::Ok);
    auto work = std::array<double, 3>{};
    auto instance = liberafx::Instance{};
    auto queue = Queue{};
    assert(instance.initialize(program, work.data(), 3) == liberafx::Result::Ok);
    assert(instance.updateAsync(0, enqueue, &queue) == liberafx::Result::Ok);
    run(queue);
    assert(instance.result() == liberafx::Result::DivideByZero);
    if (argc > 1)
    {
        auto input = std::ifstream(argv[1], std::ios::binary);
        assert(input);
        auto generated = std::vector<std::uint8_t>(std::istreambuf_iterator<char>(input), {});
        verify(generated);
    }
    return 0;
}
