#include "liberafx/LbVfxRuntime.h"

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <array>
#include <cassert>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <filesystem>
#include <iterator>
#include <iostream>
#include <limits>
#include <string>
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

auto read16(const std::uint8_t* bytes) noexcept -> std::uint16_t
{
    return static_cast<std::uint16_t>(bytes[0]) |
        static_cast<std::uint16_t>(static_cast<std::uint16_t>(bytes[1]) << 8);
}

auto read32(const std::uint8_t* bytes) noexcept -> std::uint32_t
{
    return static_cast<std::uint32_t>(bytes[0]) |
        static_cast<std::uint32_t>(bytes[1]) << 8 |
        static_cast<std::uint32_t>(bytes[2]) << 16 |
        static_cast<std::uint32_t>(bytes[3]) << 24;
}

auto fixture(const char* name) -> std::vector<std::uint8_t>
{
    auto directory = std::getenv("LIBERAFX_INT32_FIXTURE_DIR");
    assert(directory != nullptr && directory[0] != '\0');
    auto path = std::filesystem::path(directory) / name;
    auto stream = std::ifstream(path, std::ios::binary);
    assert(stream.good());
    return {std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>()};
}

auto nodeId(const std::vector<std::uint8_t>& bytes, std::uint8_t opcode,
            std::size_t occurrence = 0) -> const std::uint8_t*
{
    assert(bytes.size() >= 16 && read16(bytes.data() + 4) == 10);
    auto count = read32(bytes.data() + 8);
    for (auto index = 0u; index < count; ++index)
    {
        auto current = bytes.data() + 16 + index * 256;
        if (current[16] != opcode) continue;
        if (occurrence-- == 0) return current;
    }
    assert(false && "Compiler-generated fixture does not contain the requested opcode.");
    return nullptr;
}

struct Loaded {
    liberafx::Program program;
    liberafx::Instance instance;
    alignas(16) std::array<double, 4096> work{};
    alignas(16) std::array<double, 4096> defaults{};
    Queue queue;
};

void initialize(Loaded& loaded, const std::vector<std::uint8_t>& bytes)
{
    assert(liberafx::Program::load(bytes.data(), bytes.size(), loaded.program) == liberafx::Result::Ok);
    assert(loaded.program.version() == 10);
    assert(loaded.program.workBytes() / sizeof(double) <= loaded.work.size());
    assert(loaded.program.parameterValueCount() <= loaded.defaults.size());
    assert(loaded.instance.initialize(loaded.program, loaded.work.data(),
        (loaded.program.workBytes() + sizeof(double) - 1) / sizeof(double), loaded.defaults.data(),
        loaded.program.parameterValueCount()) == liberafx::Result::Ok);
}

void evaluate(Loaded& loaded)
{
    assert(loaded.instance.updateAsync(0, enqueue, &loaded.queue) == liberafx::Result::Ok);
    run(loaded.queue);
}

auto parameter(Loaded& loaded, const std::uint8_t* instruction,
               std::uint8_t slot = 0) -> liberafx::ParameterHandle
{
    auto handle = liberafx::ParameterHandle{};
    assert(loaded.instance.bindParameter(instruction, slot, 0, handle) == liberafx::Result::Ok);
    return handle;
}

auto output(Loaded& loaded, const std::uint8_t* instruction) -> double
{
    auto handle = liberafx::ValueHandle{};
    assert(loaded.instance.bindValue(instruction, handle) == liberafx::Result::Ok);
    auto values = std::array<double, 4>{};
    assert(loaded.instance.value(handle, values.data()) == liberafx::Result::Ok);
    return values[0];
}

auto int32Output(Loaded& loaded, const std::uint8_t* instruction) -> std::int32_t
{
    auto handle = liberafx::ValueHandle{};
    assert(loaded.instance.bindValue(instruction, handle) == liberafx::Result::Ok);
    auto type = liberafx::ValueType::None;
    assert(loaded.program.valueType(handle.nodeIndex, type) == liberafx::Result::Ok);
    assert(type == liberafx::ValueType::Int32);
    auto value = std::int32_t{};
    assert(loaded.instance.valueInt32(handle, value) == liberafx::Result::Ok);
    return value;
}

void verifyArithmetic(const char* filename, std::uint8_t opcode, std::int32_t expected)
{
    auto bytes = fixture(filename);
    auto loaded = Loaded{};
    initialize(loaded, bytes);
    auto operation = nodeId(bytes, opcode);
    evaluate(loaded);
    assert(loaded.instance.result() == liberafx::Result::Ok);
    assert(int32Output(loaded, operation) == expected);
}

void verifyFailure(const char* filename, std::uint8_t opcode,
                   std::int32_t left, std::int32_t right, liberafx::Result expected)
{
    auto bytes = fixture(filename);
    auto loaded = Loaded{};
    initialize(loaded, bytes);
    auto leftParameter = parameter(loaded, nodeId(bytes, 48, 0));
    auto rightParameter = parameter(loaded, nodeId(bytes, 48, 1));
    auto operation = nodeId(bytes, opcode);
    assert(loaded.instance.setParameter(leftParameter, left) == liberafx::Result::Ok);
    assert(loaded.instance.setParameter(rightParameter, right) == liberafx::Result::Ok);
    evaluate(loaded);
    assert(loaded.instance.result() == expected);
}

void verifyOverrides()
{
    auto bytes = fixture("Int32Add.lfxb");
    auto loaded = Loaded{};
    initialize(loaded, bytes);
    auto left = parameter(loaded, nodeId(bytes, 48, 0));
    assert(loaded.instance.setParameter(left, 1.5) == liberafx::Result::InvalidValueType);
    assert(loaded.instance.setParameter(left, 2147483648.0) == liberafx::Result::InvalidValueType);
    assert(loaded.instance.setParameterInt32(left, std::numeric_limits<std::int32_t>::min()) ==
        liberafx::Result::Ok);
}

void verifyConversions()
{
    auto bytes = fixture("Conversions.lfxb");
    auto loaded = Loaded{};
    initialize(loaded, bytes);
    auto source = nodeId(bytes, 1);
    auto toInt = nodeId(bytes, 53);
    auto toFloat = nodeId(bytes, 54);
    auto sourceParameter = parameter(loaded, source);

    evaluate(loaded);
    assert(loaded.instance.result() == liberafx::Result::Ok);
    assert(int32Output(loaded, toInt) == 3);
    assert(output(loaded, toFloat) == 3);
    auto floatHandle = liberafx::ValueHandle{};
    assert(loaded.instance.bindValue(toFloat, floatHandle) == liberafx::Result::Ok);
    auto wrongTypedValue = std::int32_t{};
    assert(loaded.instance.valueInt32(floatHandle, wrongTypedValue) ==
        liberafx::Result::InvalidValueType);

    assert(loaded.instance.setParameter(sourceParameter, 2147483647.9) == liberafx::Result::Ok);
    evaluate(loaded);
    assert(loaded.instance.result() == liberafx::Result::Ok);
    assert(int32Output(loaded, toInt) == std::numeric_limits<std::int32_t>::max());

    assert(loaded.instance.setParameter(sourceParameter, -2147483648.9) == liberafx::Result::Ok);
    evaluate(loaded);
    assert(loaded.instance.result() == liberafx::Result::Ok);
    assert(int32Output(loaded, toInt) == std::numeric_limits<std::int32_t>::min());

    assert(loaded.instance.setParameter(sourceParameter, 2147483648.0) == liberafx::Result::Ok);
    evaluate(loaded);
    assert(loaded.instance.result() == liberafx::Result::ArithmeticOverflow);

    assert(loaded.instance.setParameter(sourceParameter,
        std::numeric_limits<double>::quiet_NaN()) == liberafx::Result::NonFinite);
}

void verifyMalformedMetadata()
{
    auto bytes = fixture("Int32Add.lfxb");
    auto malformed = bytes;
    auto table = malformed.size() - (8 + 3 * 8);
    malformed[table + 8] = 1; // The first Int32 literal is falsely marked as Float.
    auto program = liberafx::Program{};
    assert(liberafx::Program::load(malformed.data(), malformed.size(), program) ==
        liberafx::Result::InvalidBinary);
}

}

int main()
{
    if (std::getenv("LIBERAFX_INT32_FIXTURE_DIR") == nullptr)
    {
        std::cout << "LIBERAFX_INT32_FIXTURE_DIR must name compiler-produced Int32 fixtures.\n";
        return 77;
    }
    verifyArithmetic("Int32Add.lfxb", 49, 17);
    verifyArithmetic("Int32Subtract.lfxb", 50, 11);
    verifyArithmetic("Int32Multiply.lfxb", 51, 42);
    verifyArithmetic("Int32Divide.lfxb", 52, 4);

    verifyFailure("Int32Add.lfxb", 49, std::numeric_limits<std::int32_t>::max(), 1,
        liberafx::Result::ArithmeticOverflow);
    verifyFailure("Int32Subtract.lfxb", 50, std::numeric_limits<std::int32_t>::min(), 1,
        liberafx::Result::ArithmeticOverflow);
    verifyFailure("Int32Multiply.lfxb", 51, std::numeric_limits<std::int32_t>::max(), 2,
        liberafx::Result::ArithmeticOverflow);
    verifyFailure("Int32Divide.lfxb", 52, 1, 0, liberafx::Result::DivideByZero);
    verifyFailure("Int32Divide.lfxb", 52, std::numeric_limits<std::int32_t>::min(), -1,
        liberafx::Result::ArithmeticOverflow);

    verifyOverrides();
    verifyConversions();
    verifyMalformedMetadata();
    return 0;
}
