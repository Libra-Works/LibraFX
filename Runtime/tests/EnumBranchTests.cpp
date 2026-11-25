#include "liberafx/LbVfxRuntime.h"

#include <array>
#include <cassert>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <vector>

namespace {

using liberafx::CustomExecution;
using liberafx::CustomNodeDescriptor;
using liberafx::CustomNodeInvocation;
using liberafx::CustomNodeBinding;
using liberafx::Instance;
using liberafx::ParameterHandle;
using liberafx::Program;
using liberafx::Result;
using liberafx::SignalHandle;
using liberafx::ValueHandle;

constexpr auto noInput = std::uint32_t{0xffffffffu};
constexpr auto instructionSize = std::size_t{256};
constexpr auto enumValueOpcode = std::uint8_t{43};
constexpr auto enumSelectOpcode = std::uint8_t{44};
constexpr auto branchTimeOpcode = std::uint8_t{45};
constexpr auto customParameterOpcode = std::uint8_t{40};
constexpr auto customSignalOpcode = std::uint8_t{41};
constexpr auto enumCaseRecordSize = std::size_t{40};
constexpr auto enumType = std::array<std::uint8_t, 16>{
    0x31, 0x2a, 0x49, 0x90, 0x51, 0x7d, 0x45, 0x10,
    0x91, 0x4c, 0x73, 0x1e, 0x20, 0x2a, 0x4c, 0x05};
constexpr auto stateNodeType = std::array<std::uint8_t, 16>{
    0x42, 0x2a, 0x49, 0x90, 0x51, 0x7d, 0x45, 0x10,
    0x91, 0x4c, 0x73, 0x1e, 0x20, 0x2a, 0x4c, 0x06};
constexpr auto signalNodeType = std::array<std::uint8_t, 16>{
    0x53, 0x2a, 0x49, 0x90, 0x51, 0x7d, 0x45, 0x10,
    0x91, 0x4c, 0x73, 0x1e, 0x20, 0x2a, 0x4c, 0x07};
constexpr auto delayNodeType = std::array<std::uint8_t, 16>{
    0x64, 0x2a, 0x49, 0x90, 0x51, 0x7d, 0x45, 0x10,
    0x91, 0x4c, 0x73, 0x1e, 0x20, 0x2a, 0x4c, 0x08};

struct Guard final {
    std::uint32_t selector = 0;
    std::int32_t parent = -1;
    std::vector<std::int32_t> cases;
    std::vector<std::int32_t> excluded;
    bool includesDefault = false;
};

struct Case final {
    std::int32_t value = 0;
    std::uint32_t source = noInput;
    std::array<double, 4> defaults{};
};

void write16(std::uint8_t* output, std::uint16_t value)
{
    output[0] = static_cast<std::uint8_t>(value);
    output[1] = static_cast<std::uint8_t>(value >> 8);
}

void write32(std::uint8_t* output, std::uint32_t value)
{
    for (auto index = 0u; index < 4; ++index)
        output[index] = static_cast<std::uint8_t>(value >> (index * 8));
}

void writeI32(std::uint8_t* output, std::int32_t value)
{
    auto bits = std::uint32_t{};
    std::memcpy(&bits, &value, sizeof(bits));
    write32(output, bits);
}

void writeDouble(std::uint8_t* output, double value)
{
    auto bits = std::uint64_t{};
    std::memcpy(&bits, &value, sizeof(bits));
    for (auto index = 0u; index < 8; ++index)
        output[index] = static_cast<std::uint8_t>(bits >> (index * 8));
}

auto nodeOffset(std::size_t index) noexcept -> std::size_t
{
    return 16 + index * instructionSize;
}

void makeInstruction(std::vector<std::uint8_t>& bytes, std::size_t index, std::uint8_t opcode,
                     std::uint8_t width, std::uint32_t inputA, std::uint32_t inputB,
                     double defaultA, double defaultB, std::uint32_t guard,
                     const std::uint8_t* typeId = nullptr)
{
    auto current = bytes.data() + nodeOffset(index);
    current[0] = static_cast<std::uint8_t>(index + 1);
    current[16] = opcode;
    current[17] = width;
    current[18] = inputA == noInput ? 0 : 1;
    current[24] = static_cast<std::uint8_t>(inputA);
    current[25] = static_cast<std::uint8_t>(inputA >> 8);
    current[26] = static_cast<std::uint8_t>(inputA >> 16);
    current[27] = static_cast<std::uint8_t>(inputA >> 24);
    write32(current + 28, inputB);
    writeDouble(current + 32, defaultA);
    writeDouble(current + 64, defaultB);
    write32(current + 112, guard);
    if (typeId != nullptr) std::memcpy(current + 96, typeId, 16);
}

void makeEnumValue(std::vector<std::uint8_t>& bytes, std::size_t index, std::int32_t value)
{
    makeInstruction(bytes, index, enumValueOpcode, 1, noInput, noInput, value, 0, noInput,
                    enumType.data());
}

void makeScalar(std::vector<std::uint8_t>& bytes, std::size_t index, double value,
                std::uint32_t guard)
{
    makeInstruction(bytes, index, 1, 1, noInput, noInput, value, 0, guard);
}

void makeDivide(std::vector<std::uint8_t>& bytes, std::size_t index, std::uint32_t guard)
{
    makeInstruction(bytes, index, 6, 1, noInput, noInput, 1, 0, guard);
}

void makeBranchTime(std::vector<std::uint8_t>& bytes, std::size_t index, std::uint32_t guard)
{
    makeInstruction(bytes, index, branchTimeOpcode, 1, noInput, noInput, 0, 0, guard);
}

void makeEnumSelect(std::vector<std::uint8_t>& bytes, std::size_t index, std::uint32_t selector,
                    std::uint32_t defaultSource, const std::array<double, 4>& fallback)
{
    makeInstruction(bytes, index, enumSelectOpcode, 1, selector, noInput, fallback[0], 0, noInput,
                    enumType.data());
    auto current = bytes.data() + nodeOffset(index);
    current[18] = 1;
    current[24] = static_cast<std::uint8_t>(selector);
    current[25] = static_cast<std::uint8_t>(selector >> 8);
    current[26] = static_cast<std::uint8_t>(selector >> 16);
    current[27] = static_cast<std::uint8_t>(selector >> 24);
    (void)defaultSource;
}

void appendEnumMetadata(std::vector<std::uint8_t>& bytes, std::uint32_t nodeCount,
                        std::uint32_t selectNode, std::uint32_t defaultSource,
                        const std::vector<Case>& cases, const std::vector<Guard>& guards,
                        const std::array<double, 4>& fallback, bool includeSelect)
{
    auto selectCount = includeSelect ? std::size_t{1} : std::size_t{0};
    auto valuesOffset = std::size_t{16};
    auto selectsOffset = valuesOffset + 24;
    auto guardsOffset = selectsOffset + selectCount * 68;
    auto caseOffset = guardsOffset + guards.size() * 40;
    auto caseRecordCount = includeSelect ? cases.size() : std::size_t{0};
    auto guardDataOffset = caseOffset + caseRecordCount * enumCaseRecordSize;
    auto guardDataBytes = std::size_t{0};
    for (const auto& guard : guards)
        guardDataBytes += (guard.cases.size() + guard.excluded.size()) * 4;
    auto metadataSize = guardDataOffset + guardDataBytes;
    auto oldSize = bytes.size();
    bytes.resize(oldSize + metadataSize);
    auto metadata = bytes.data() + oldSize;
    std::memcpy(metadata, "LFEB", 4);
    write16(metadata + 4, 1);
    write16(metadata + 6, static_cast<std::uint16_t>(guards.size()));
    write16(metadata + 8, 1);
    write16(metadata + 10, static_cast<std::uint16_t>(selectCount));
    write32(metadata + 12, static_cast<std::uint32_t>(metadataSize));
    write32(metadata + valuesOffset, 0);
    std::memcpy(metadata + valuesOffset + 4, enumType.data(), enumType.size());
    if (includeSelect)
    {
        auto select = metadata + selectsOffset;
        write32(select, selectNode);
        write32(select + 4, 0);
        write32(select + 8, defaultSource);
        std::memcpy(select + 12, enumType.data(), enumType.size());
        select[28] = 1;
        select[29] = static_cast<std::uint8_t>(cases.size());
        write32(select + 32, static_cast<std::uint32_t>(caseOffset));
        for (auto lane = 0u; lane < 4; ++lane)
            writeDouble(select + 36 + lane * 8, fallback[lane]);
        for (std::size_t index = 0; index < cases.size(); ++index)
        {
            auto branch = metadata + caseOffset + index * enumCaseRecordSize;
            writeI32(branch, cases[index].value);
            write32(branch + 4, cases[index].source);
            for (auto lane = 0u; lane < 4; ++lane)
                writeDouble(branch + 8 + lane * 8, cases[index].defaults[lane]);
        }
    }
    auto cursor = guardDataOffset;
    for (std::size_t index = 0; index < guards.size(); ++index)
    {
        auto record = metadata + guardsOffset + index * 40;
        write32(record, guards[index].selector);
        std::memcpy(record + 4, enumType.data(), enumType.size());
        write32(record + 20, guards[index].parent < 0 ?
            noInput : static_cast<std::uint32_t>(guards[index].parent));
        write32(record + 24, guards[index].includesDefault ? 1u : 0u);
        write16(record + 28, static_cast<std::uint16_t>(guards[index].cases.size()));
        write16(record + 30, static_cast<std::uint16_t>(guards[index].excluded.size()));
        write32(record + 32, static_cast<std::uint32_t>(cursor));
        auto excludedOffset = cursor + guards[index].cases.size() * 4;
        write32(record + 36, static_cast<std::uint32_t>(excludedOffset));
        for (auto value : guards[index].cases)
        {
            writeI32(metadata + cursor, value);
            cursor += 4;
        }
        for (auto value : guards[index].excluded)
        {
            writeI32(metadata + cursor, value);
            cursor += 4;
        }
    }
    auto header = bytes.data();
    std::memcpy(header, "LFXB", 4);
    write16(header + 4, 8);
    header[6] = 1;
    write32(header + 8, nodeCount);
    write16(header + 12, static_cast<std::uint16_t>(instructionSize));
    write16(header + 14, 0);
}

void appendCurrentMetadata(std::vector<std::uint8_t>& bytes, std::size_t count)
{
    bytes[4] = 11;
    auto logical = bytes.size();
    bytes.resize(logical + 16, 0);
    std::memcpy(bytes.data() + logical, "LFLE", 4);
    write16(bytes.data() + logical + 4, 1);
    write32(bytes.data() + logical + 12, 16);
    auto types = bytes.size();
    bytes.resize(types + 8 + count * 8, 0);
    std::memcpy(bytes.data() + types, "LFTY", 4);
    write16(bytes.data() + types + 4, 1);
    write16(bytes.data() + types + 6, 8);
    for (auto index = std::size_t{}; index < count; ++index)
    {
        auto current = bytes.data() + nodeOffset(index);
        auto record = bytes.data() + types + 8 + index * 8;
        auto opcode = current[16];
        if (opcode == customParameterOpcode || opcode == customSignalOpcode)
        {
            write32(current + 44, static_cast<std::uint32_t>(current[112]) |
                (static_cast<std::uint32_t>(current[113]) << 8) |
                (static_cast<std::uint32_t>(current[114]) << 16) |
                (static_cast<std::uint32_t>(current[115]) << 24));
            write32(current + 112, 0);
            record[0] = opcode == customSignalOpcode ? 0 : 1;
        }
        else if (opcode == enumValueOpcode)
        {
            record[0] = 7; record[1] = 1; record[2] = 7;
        }
        else if (opcode == enumSelectOpcode)
        {
            record[0] = 1; record[1] = 1; record[2] = 7;
        }
        else if (opcode == branchTimeOpcode)
        {
            record[0] = 1;
        }
        else
        {
            record[0] = 1; record[1] = opcode == 6 ? 2 : 1;
            record[2] = 1; record[3] = opcode == 6 ? 1 : 0;
        }
    }
}

auto makeValueProgram(bool branchTime, bool divide, bool explicitDefault) -> std::vector<std::uint8_t>
{
    constexpr auto count = std::size_t{5};
    auto bytes = std::vector<std::uint8_t>(16 + count * instructionSize);
    makeEnumValue(bytes, 0, 0);
    makeScalar(bytes, 1, 42, noInput);
    if (branchTime) makeBranchTime(bytes, 2, 0);
    else makeScalar(bytes, 2, 9, 0);
    if (divide) makeDivide(bytes, 3, 1);
    else makeScalar(bytes, 3, 42, 1);
    makeEnumSelect(bytes, 4, 0, explicitDefault ? 1 : noInput, {-7, 0, 0, 0});
    auto cases = std::vector<Case>{
        {0, 2, {branchTime ? 0.0 : 9.0, 0, 0, 0}},
        {1, 3, {divide ? 0.0 : 42.0, 0, 0, 0}}};
    auto guards = std::vector<Guard>{
        {0, -1, {0}, {}, false},
        {0, -1, {1}, {}, false}};
    appendEnumMetadata(bytes, count, 4, explicitDefault ? 1 : noInput,
        cases, guards, {-7, 0, 0, 0}, true);
    appendCurrentMetadata(bytes, count);
    return bytes;
}

auto makeCustomStateProgram() -> std::vector<std::uint8_t>
{
    constexpr auto count = std::size_t{4};
    auto bytes = std::vector<std::uint8_t>(16 + count * instructionSize);
    makeEnumValue(bytes, 0, 1);
    auto state = bytes.data() + nodeOffset(1);
    state[0] = 2;
    state[16] = customParameterOpcode;
    state[17] = 1;
    std::memcpy(state + 24, stateNodeType.data(), stateNodeType.size());
    write32(state + 40, 1);
    write32(state + 48, noInput);
    write32(state + 56, noInput);
    write32(state + 64, noInput);
    write32(state + 72, noInput);
    for (auto branch = 0u; branch < 4; ++branch)
        write32(state + 208 + branch * 4, noInput);
    write32(state + 112, 0);
    makeScalar(bytes, 2, 42, 1);
    makeEnumSelect(bytes, 3, 0, noInput, {-7, 0, 0, 0});
    auto cases = std::vector<Case>{{0, 1, {0, 0, 0, 0}}, {1, 2, {42, 0, 0, 0}}};
    auto guards = std::vector<Guard>{{0, -1, {0}, {}, false}, {0, -1, {1}, {}, false}};
    appendEnumMetadata(bytes, count, 3, noInput, cases, guards, {-7, 0, 0, 0}, true);
    appendCurrentMetadata(bytes, count);
    return bytes;
}

auto makeSignalProgram(const std::array<std::uint8_t, 16>& signalType) -> std::vector<std::uint8_t>
{
    constexpr auto count = std::size_t{2};
    auto bytes = std::vector<std::uint8_t>(16 + count * instructionSize);
    makeEnumValue(bytes, 0, 1);
    auto signal = bytes.data() + nodeOffset(1);
    signal[0] = 2;
    signal[16] = customSignalOpcode;
    signal[19] = 1;
    std::memcpy(signal + 24, signalType.data(), signalType.size());
    write32(signal + 40, 1);
    for (auto input = 0u; input < 4; ++input)
        write32(signal + 48 + input * 8, noInput);
    write32(signal + 112, 0);
    for (auto branch = 0u; branch < 4; ++branch)
        write32(signal + 208 + branch * 4, noInput);
    auto cases = std::vector<Case>{{0, noInput, {0, 0, 0, 0}}};
    auto guards = std::vector<Guard>{{0, -1, {0}, {}, false}};
    appendEnumMetadata(bytes, count, 0, noInput, cases, guards, {0, 0, 0, 0}, false);
    appendCurrentMetadata(bytes, count);
    return bytes;
}

auto inlineJob(void*, void (*job)(void*) noexcept, void* context) noexcept -> bool
{
    job(context);
    return true;
}

auto update(Instance& instance, double time) -> Result
{
    auto status = instance.updateAsync(time, inlineJob, nullptr);
    return status == Result::Ok ? instance.result() : status;
}

auto stateCallback(CustomNodeInvocation& call) noexcept -> Result
{
    if (call.state == nullptr || call.stateBytes < sizeof(std::uint32_t) ||
        call.outputs == nullptr || call.outputWidth != 1)
        return Result::InvalidArgument;
    auto count = std::uint32_t{};
    std::memcpy(&count, call.state, sizeof(count));
    ++count;
    std::memcpy(call.state, &count, sizeof(count));
    call.outputs[0] = count;
    return Result::Ok;
}

auto signalCallback(CustomNodeInvocation& call) noexcept -> Result
{
    auto count = static_cast<std::uint32_t*>(call.context);
    if (count == nullptr) return Result::InvalidArgument;
    ++*count;
    call.selectedBranch = 0;
    return Result::Ok;
}

auto delayCallback(CustomNodeInvocation& call) noexcept -> Result
{
    call.selectedBranch = 0;
    call.deferSignal = true;
    call.delaySeconds = 1;
    return Result::Ok;
}

void checkValue(Instance& instance, const std::uint8_t* nodeId, double expected)
{
    auto handle = ValueHandle{};
    assert(instance.bindValue(nodeId, handle) == Result::Ok);
    auto values = std::array<double, 4>{};
    assert(instance.value(handle, values.data()) == Result::Ok);
    assert(handle.componentCount == 1 && values[0] == expected);
}

auto loadSimple(const std::vector<std::uint8_t>& bytes, Program& program) -> Result
{
    return Program::load(bytes.data(), bytes.size(), program);
}

void verifyInactiveDivideAndDefaults()
{
    auto bytes = makeValueProgram(false, true, false);
    auto program = Program{};
    assert(loadSimple(bytes, program) == Result::Ok);
    alignas(64) auto work = std::array<double, 128>{};
    alignas(64) auto defaults = std::array<double, 128>{};
    auto instance = Instance{};
    assert(instance.initialize(program, work.data(), work.size(), defaults.data(), defaults.size()) == Result::Ok);
    assert(update(instance, 0) == Result::Ok);
    checkValue(instance, bytes.data() + nodeOffset(4), 9);
    auto selector = ParameterHandle{};
    assert(instance.bindParameter(bytes.data() + nodeOffset(0), 0, 0, selector) == Result::Ok);
    assert(instance.setParameter(selector, 1) == Result::Ok);
    assert(update(instance, 1) == Result::DivideByZero);
    assert(instance.setParameter(selector, 99) == Result::Ok);
    assert(update(instance, 2) == Result::Ok);
    checkValue(instance, bytes.data() + nodeOffset(4), -7);

    auto explicitBytes = makeValueProgram(false, true, true);
    auto explicitProgram = Program{};
    assert(loadSimple(explicitBytes, explicitProgram) == Result::Ok);
    alignas(64) auto explicitWork = std::array<double, 128>{};
    alignas(64) auto explicitDefaults = std::array<double, 128>{};
    auto explicitInstance = Instance{};
    assert(explicitInstance.initialize(explicitProgram, explicitWork.data(), explicitWork.size(),
        explicitDefaults.data(), explicitDefaults.size()) == Result::Ok);
    assert(explicitInstance.bindParameter(explicitBytes.data() + nodeOffset(0), 0, 0, selector) == Result::Ok);
    assert(explicitInstance.setParameter(selector, 99) == Result::Ok);
    assert(update(explicitInstance, 0) == Result::Ok);
    checkValue(explicitInstance, explicitBytes.data() + nodeOffset(4), 42);

    auto missingCaseBytes = makeValueProgram(false, false, false);
    auto metadata = missingCaseBytes.data() + nodeOffset(5);
    auto caseRecordsOffset = std::size_t{16 + 24 + 68 + 2 * 40};
    write32(metadata + caseRecordsOffset + enumCaseRecordSize + 4, noInput);
    writeDouble(metadata + caseRecordsOffset + enumCaseRecordSize + 8, 77);
    auto missingCaseProgram = Program{};
    assert(loadSimple(missingCaseBytes, missingCaseProgram) == Result::Ok);
    alignas(64) auto missingCaseWork = std::array<double, 128>{};
    alignas(64) auto missingCaseDefaults = std::array<double, 128>{};
    auto missingCaseInstance = Instance{};
    assert(missingCaseInstance.initialize(missingCaseProgram, missingCaseWork.data(),
        missingCaseWork.size(), missingCaseDefaults.data(), missingCaseDefaults.size()) == Result::Ok);
    assert(missingCaseInstance.bindParameter(missingCaseBytes.data() + nodeOffset(0), 0, 0, selector) == Result::Ok);
    assert(missingCaseInstance.setParameter(selector, 1) == Result::Ok);
    assert(update(missingCaseInstance, 0) == Result::Ok);
    checkValue(missingCaseInstance, missingCaseBytes.data() + nodeOffset(4), 77);

    auto declaredDefaultBytes = makeValueProgram(false, false, false);
    auto declaredMetadata = declaredDefaultBytes.data() + nodeOffset(5);
    write32(declaredMetadata + caseRecordsOffset + enumCaseRecordSize + 4, noInput);
    auto declaredProgram = Program{};
    assert(loadSimple(declaredDefaultBytes, declaredProgram) == Result::Ok);
    alignas(64) auto declaredWork = std::array<double, 128>{};
    alignas(64) auto declaredDefaults = std::array<double, 128>{};
    auto declaredInstance = Instance{};
    assert(declaredInstance.initialize(declaredProgram, declaredWork.data(), declaredWork.size(),
        declaredDefaults.data(), declaredDefaults.size()) == Result::Ok);
    assert(declaredInstance.bindParameter(declaredDefaultBytes.data() + nodeOffset(0), 0, 0, selector) == Result::Ok);
    assert(declaredInstance.setParameter(selector, 1) == Result::Ok);
    assert(update(declaredInstance, 0) == Result::Ok);
    checkValue(declaredInstance, declaredDefaultBytes.data() + nodeOffset(4), 42);
}

void verifyBranchClockPausesAndResumes()
{
    auto bytes = makeValueProgram(true, false, false);
    auto program = Program{};
    assert(loadSimple(bytes, program) == Result::Ok);
    alignas(64) auto work = std::array<double, 128>{};
    alignas(64) auto defaults = std::array<double, 128>{};
    auto instance = Instance{};
    assert(instance.initialize(program, work.data(), work.size(), defaults.data(), defaults.size()) == Result::Ok);
    auto selector = ParameterHandle{};
    assert(instance.bindParameter(bytes.data() + nodeOffset(0), 0, 0, selector) == Result::Ok);
    assert(update(instance, 0) == Result::Ok);
    assert(update(instance, 1) == Result::Ok);
    checkValue(instance, bytes.data() + nodeOffset(4), 1);
    assert(instance.setParameter(selector, 1) == Result::Ok);
    assert(update(instance, 2) == Result::Ok);
    assert(update(instance, 5) == Result::Ok);
    checkValue(instance, bytes.data() + nodeOffset(4), 42);
    assert(instance.setParameter(selector, 0) == Result::Ok);
    assert(update(instance, 6) == Result::Ok);
    checkValue(instance, bytes.data() + nodeOffset(4), 2);
    assert(update(instance, 6.5) == Result::Ok);
    checkValue(instance, bytes.data() + nodeOffset(4), 2.5);
    assert(instance.setParameter(selector, 99) == Result::Ok);
    assert(update(instance, 7) == Result::Ok);
    checkValue(instance, bytes.data() + nodeOffset(4), -7);
}

void verifyCallerOwnedStateIsPreserved()
{
    auto bytes = makeCustomStateProgram();
    auto descriptor = CustomNodeDescriptor{};
    std::memcpy(descriptor.typeId, stateNodeType.data(), stateNodeType.size());
    descriptor.abiVersion = 1;
    descriptor.execution = CustomExecution::Parameter;
    descriptor.outputWidth = 1;
    descriptor.stateBytes = sizeof(std::uint32_t);
    descriptor.stateAlignment = 16;
    descriptor.function = stateCallback;
    alignas(64) auto bindings = std::array<CustomNodeBinding, 4>{};
    auto program = Program{};
    assert(Program::load(bytes.data(), bytes.size(), &descriptor, 1, bindings.data(), bindings.size(),
        program) == Result::Ok);
    alignas(64) auto work = std::array<double, 128>{};
    alignas(64) auto defaults = std::array<double, 128>{};
    auto instance = Instance{};
    assert(instance.initialize(program, work.data(), work.size(), defaults.data(), defaults.size()) == Result::Ok);
    assert(update(instance, 0) == Result::Ok);
    checkValue(instance, bytes.data() + nodeOffset(3), 42);
    auto selector = ParameterHandle{};
    assert(instance.bindParameter(bytes.data() + nodeOffset(0), 0, 0, selector) == Result::Ok);
    assert(instance.setParameter(selector, 0) == Result::Ok);
    assert(update(instance, 1) == Result::Ok);
    checkValue(instance, bytes.data() + nodeOffset(3), 1);
    assert(instance.setParameter(selector, 1) == Result::Ok);
    assert(update(instance, 2) == Result::Ok);
    checkValue(instance, bytes.data() + nodeOffset(3), 42);
    assert(instance.setParameter(selector, 0) == Result::Ok);
    assert(update(instance, 3) == Result::Ok);
    checkValue(instance, bytes.data() + nodeOffset(3), 2);
}

void verifySignalSuppressionAndPausedDelay()
{
    auto signalCalls = std::uint32_t{0};
    auto signalDescriptor = CustomNodeDescriptor{};
    std::memcpy(signalDescriptor.typeId, signalNodeType.data(), signalNodeType.size());
    signalDescriptor.abiVersion = 1;
    signalDescriptor.execution = CustomExecution::Signal;
    signalDescriptor.branchCount = 1;
    signalDescriptor.function = signalCallback;
    signalDescriptor.context = &signalCalls;
    auto signalBytes = makeSignalProgram(signalNodeType);
    alignas(64) auto signalBindings = std::array<CustomNodeBinding, 2>{};
    auto signalProgram = Program{};
    assert(Program::load(signalBytes.data(), signalBytes.size(), &signalDescriptor, 1,
        signalBindings.data(), signalBindings.size(), signalProgram) == Result::Ok);
    alignas(64) auto signalWork = std::array<double, 128>{};
    alignas(64) auto signalDefaults = std::array<double, 128>{};
    auto signalInstance = Instance{};
    assert(signalInstance.initialize(signalProgram, signalWork.data(), signalWork.size(),
        signalDefaults.data(), signalDefaults.size()) == Result::Ok);
    auto signalHandle = SignalHandle{};
    assert(signalInstance.bindSignal(signalBytes.data() + nodeOffset(1), signalHandle) == Result::Ok);
    auto selector = ParameterHandle{};
    assert(signalInstance.bindParameter(signalBytes.data() + nodeOffset(0), 0, 0, selector) == Result::Ok);
    assert(update(signalInstance, 0) == Result::Ok);
    assert(signalInstance.dispatchSignal(signalHandle, 0, 16) == Result::Ok && signalCalls == 0);
    assert(signalInstance.setParameter(selector, 0) == Result::Ok);
    assert(update(signalInstance, 1) == Result::Ok);
    assert(signalInstance.dispatchSignal(signalHandle, 1, 16) == Result::Ok && signalCalls == 1);
    assert(signalInstance.setParameter(selector, 1) == Result::Ok);
    assert(update(signalInstance, 2) == Result::Ok);
    assert(signalInstance.dispatchSignal(signalHandle, 2, 16) == Result::Ok && signalCalls == 1);

    auto delayDescriptor = CustomNodeDescriptor{};
    std::memcpy(delayDescriptor.typeId, delayNodeType.data(), delayNodeType.size());
    delayDescriptor.abiVersion = 1;
    delayDescriptor.execution = CustomExecution::Signal;
    delayDescriptor.branchCount = 1;
    delayDescriptor.function = delayCallback;
    auto delayBytes = makeSignalProgram(delayNodeType);
    alignas(64) auto delayBindings = std::array<CustomNodeBinding, 2>{};
    auto delayProgram = Program{};
    assert(Program::load(delayBytes.data(), delayBytes.size(), &delayDescriptor, 1,
        delayBindings.data(), delayBindings.size(), delayProgram) == Result::Ok);
    alignas(64) auto delayWork = std::array<double, 128>{};
    alignas(64) auto delayDefaults = std::array<double, 128>{};
    auto delayInstance = Instance{};
    assert(delayInstance.initialize(delayProgram, delayWork.data(), delayWork.size(),
        delayDefaults.data(), delayDefaults.size()) == Result::Ok);
    auto deferred = std::array<liberafx::DeferredSignal, 2>{};
    assert(delayInstance.setDeferredSignalStorage(deferred.data(), deferred.size()) == Result::Ok);
    auto delayHandle = SignalHandle{};
    assert(delayInstance.bindSignal(delayBytes.data() + nodeOffset(1), delayHandle) == Result::Ok);
    assert(delayInstance.bindParameter(delayBytes.data() + nodeOffset(0), 0, 0, selector) == Result::Ok);
    assert(delayInstance.setParameter(selector, 0) == Result::Ok);
    assert(update(delayInstance, 0) == Result::Ok);
    assert(delayInstance.dispatchSignal(delayHandle, 0, 16) == Result::Ok);
    auto due = 0.0;
    auto hasSignal = false;
    assert(delayInstance.nextDeferredSignal(due, hasSignal) == Result::Ok && hasSignal && due == 1);
    assert(delayInstance.setParameter(selector, 1) == Result::Ok);
    assert(update(delayInstance, 1) == Result::Ok);
    assert(delayInstance.nextDeferredSignal(due, hasSignal) == Result::Ok && hasSignal &&
        due == std::numeric_limits<double>::max());
    assert(update(delayInstance, 5) == Result::Ok);
    assert(delayInstance.setParameter(selector, 0) == Result::Ok);
    assert(update(delayInstance, 6) == Result::Ok);
    assert(delayInstance.nextDeferredSignal(due, hasSignal) == Result::Ok && hasSignal && due == 6);
    assert(delayInstance.dispatchDeferredSignal(6, 16) == Result::Ok);
    assert(delayInstance.nextDeferredSignal(due, hasSignal) == Result::Ok && !hasSignal);
}

}

void verifyCurrentEnumParameter()
{
    auto bytes = std::vector<std::uint8_t>(16 + instructionSize);
    makeEnumValue(bytes, 0, 0);
    appendEnumMetadata(bytes, 1, 0, noInput, {}, {}, {}, false);
    bytes[4] = 11;
    auto offset = bytes.size();
    bytes.resize(offset + 16, 0);
    std::memcpy(bytes.data() + offset, "LFLE", 4);
    write16(bytes.data() + offset + 4, 1);
    write32(bytes.data() + offset + 12, 16);
    offset = bytes.size();
    bytes.resize(offset + 16, 0);
    std::memcpy(bytes.data() + offset, "LFTY", 4);
    write16(bytes.data() + offset + 4, 1);
    write16(bytes.data() + offset + 6, 8);
    bytes[offset + 8] = 7;
    bytes[offset + 9] = 1;
    bytes[offset + 10] = 7;
    auto program = Program{};
    assert(loadSimple(bytes, program) == Result::Ok);
    alignas(64) auto work = std::array<double, 128>{};
    alignas(64) auto defaults = std::array<double, 128>{};
    auto instance = Instance{};
    assert(instance.initialize(program, work.data(), work.size(), defaults.data(), defaults.size()) == Result::Ok);
    auto handle = ParameterHandle{};
    assert(instance.bindParameter(bytes.data() + nodeOffset(0), 0, 0, handle) == Result::Ok);
    assert(instance.setParameterInt32(handle, -2147483647) == Result::Ok);
    auto value = double{};
    assert(instance.parameter(handle, value) == Result::Ok && value == -2147483647);
    assert(Instance::validateParameter(program, bytes.data() + nodeOffset(0), 0, 0, true, 2147483647, 0) == Result::Ok);
    assert(update(instance, 0) == Result::Ok);
    checkValue(instance, bytes.data() + nodeOffset(0), -2147483647);
}

int main()
{
    verifyCurrentEnumParameter();
    verifyInactiveDivideAndDefaults();
    verifyBranchClockPausesAndResumes();
    verifyCallerOwnedStateIsPreserved();
    verifySignalSuppressionAndPausedDelay();
}
