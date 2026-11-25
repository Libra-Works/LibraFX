#include "liberafx/LbVfxRuntime.h"
#include "liberafx/LbEffectLifecycle.h"
#include "liberafx/LbGeneratorPool.h"
#include "liberafx/LbNamedIo.h"
#include "liberafx/LbSpawnableHost.h"
#include "../Core/LbLogicalNodes.h"

#ifdef NDEBUG
#undef NDEBUG
#endif

#include <array>
#include <algorithm>
#include <cassert>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <limits>
#include <string>
#include <utility>
#include <vector>
#if defined(_MSC_VER) && defined(_DEBUG)
#include <crtdbg.h>
#endif

namespace {

using namespace liberafx;

constexpr auto noSlot = std::uint8_t{0xff};
constexpr auto maxExactInteger = std::uint64_t{9007199254740991};
constexpr auto maxFixtureInstructions = std::size_t{10000};

struct alignas(16) StateBuffer final {
    std::array<std::byte, 64> bytes{};
    auto data() noexcept -> void* { return bytes.data(); }
    auto size() const noexcept -> std::size_t { return bytes.size(); }
};

constexpr auto compareId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x01};
constexpr auto andId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x02};
constexpr auto orId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x03};
constexpr auto signalToNumberId = std::array<std::uint8_t, 16>{
    0xe9, 0x77, 0x8c, 0x40, 0x20, 0x9b, 0x4c, 0xe8,
    0x91, 0x8d, 0x35, 0xc0, 0x00, 0x00, 0x00, 0x04};
constexpr auto fixtureNode(std::uint8_t number) noexcept -> std::array<std::uint8_t, 16>
{
    return {0x35, 0x10, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00,
        0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, number};
}

auto findDescriptor(const std::array<std::uint8_t, 16>& typeId) -> const LogicalNodeDescriptor&
{
    auto count = std::size_t{};
    auto descriptors = logicalNodes(count);
    for (auto index = std::size_t{}; index < count; ++index)
        if (std::equal(typeId.begin(), typeId.end(), descriptors[index].typeId))
            return descriptors[index];
    assert(false && "logical node descriptor is missing");
    return descriptors[0];
}

auto invocationFor(const LogicalNodeDescriptor& descriptor, void* state, std::size_t stateBytes,
                   double* output, std::uint8_t inputSlot, std::uint64_t tick,
                   std::uint32_t configuration = 0) -> LogicalNodeInvocation
{
    auto invocation = LogicalNodeInvocation{};
    invocation.state = state;
    invocation.stateBytes = stateBytes;
    invocation.outputs = output;
    invocation.outputWidth = descriptor.outputWidth;
    invocation.eventTick = tick;
    invocation.signalInputSlot = inputSlot;
    invocation.configuration = configuration;
    return invocation;
}

void verifyCompareModes()
{
    auto descriptor = findDescriptor(compareId);
    assert(descriptor.update != nullptr && descriptor.onSignal == nullptr);
    assert(descriptor.inputCount == 2 && descriptor.parameterCount == 2 &&
        descriptor.outputWidth == 1 && descriptor.signalBranchCount == 1);
    alignas(16) auto inputs = std::array<double, 8>{};
    inputs[0] = 4;
    inputs[4] = 4;
    auto widths = std::array<std::uint8_t, 2>{1, 1};
    auto state = StateBuffer{};
    alignas(16) auto output = std::array<double, 4>{};
    auto invocation = invocationFor(descriptor, state.data(), state.size(), output.data(), noSlot, 1);
    invocation.inputs = inputs.data();
    invocation.inputCount = 2;
    invocation.inputWidths = widths.data();

    // Exact equality and initial true in edge mode both work on first evaluation.
    invocation.configuration = 2;
    assert(descriptor.update(invocation) == Result::Ok);
    assert(output[0] == 1 && invocation.emittedBranches == 1);
    descriptor.commit(invocation);
    assert(descriptor.update(invocation) == Result::Ok);
    assert(output[0] == 1 && invocation.emittedBranches == 0);
    descriptor.commit(invocation);

    // Every operator is a direct scalar comparison; no epsilon is introduced.
    inputs[0] = 1;
    inputs[4] = 2;
    auto operationTruth = std::array<bool, 6>{true, true, false, true, false, false};
    for (auto operation = std::uint8_t{}; operation < operationTruth.size(); ++operation)
    {
        invocation.configuration = static_cast<std::uint32_t>(operation);
        assert(descriptor.update(invocation) == Result::Ok);
        assert((output[0] == 1) == operationTruth[operation]);
        descriptor.commit(invocation);
    }

    // EachUpdate emits for every true evaluation; Edge emits only on false -> true.
    inputs[0] = 3;
    inputs[4] = 2;
    invocation.configuration = 5u | (1u << 8u);
    assert(descriptor.update(invocation) == Result::Ok && invocation.emittedBranches == 1);
    descriptor.commit(invocation);
    assert(descriptor.update(invocation) == Result::Ok && invocation.emittedBranches == 1);
    descriptor.commit(invocation);
    invocation.configuration = 5u;
    assert(descriptor.update(invocation) == Result::Ok && invocation.emittedBranches == 0);
    descriptor.commit(invocation);
    inputs[0] = 1;
    assert(descriptor.update(invocation) == Result::Ok && invocation.emittedBranches == 0);
    descriptor.commit(invocation);
    inputs[0] = 3;
    assert(descriptor.update(invocation) == Result::Ok && invocation.emittedBranches == 1);
    descriptor.commit(invocation);

    // A failed whole-program update must not commit an edge transition.
    auto failedState = StateBuffer{};
    alignas(16) auto failedOutput = std::array<double, 4>{};
    auto failedInvocation = invocationFor(descriptor, failedState.data(), failedState.size(),
        failedOutput.data(), noSlot, 4, 5);
    failedInvocation.inputs = inputs.data();
    failedInvocation.inputCount = 2;
    failedInvocation.inputWidths = widths.data();
    assert(descriptor.update(failedInvocation) == Result::Ok && failedInvocation.emittedBranches == 1);
    // Intentionally skip commit to model a later instruction failing.
    assert(descriptor.update(failedInvocation) == Result::Ok && failedInvocation.emittedBranches == 1);
    descriptor.commit(failedInvocation);
    assert(descriptor.update(failedInvocation) == Result::Ok && failedInvocation.emittedBranches == 0);
}

void verifyAndAndOr()
{
    auto andDescriptor = findDescriptor(andId);
    auto orDescriptor = findDescriptor(orId);
    auto andState = StateBuffer{};
    auto orState = StateBuffer{};
    alignas(16) auto andOutput = std::array<double, 4>{};
    alignas(16) auto orOutput = std::array<double, 4>{};
    auto andUpdate = invocationFor(andDescriptor, andState.data(), andState.size(), andOutput.data(), noSlot, 7);
    auto orUpdate = invocationFor(orDescriptor, orState.data(), orState.size(), orOutput.data(), noSlot, 7);
    assert(andDescriptor.update(andUpdate) == Result::Ok);
    andDescriptor.commit(andUpdate);
    assert(orDescriptor.update(orUpdate) == Result::Ok);

    auto a = invocationFor(andDescriptor, andState.data(), andState.size(), andOutput.data(), 0, 7);
    auto b = invocationFor(andDescriptor, andState.data(), andState.size(), andOutput.data(), 1, 7);
    assert(andDescriptor.onSignal(a) == Result::Ok && a.emittedBranches == 0 && andOutput[0] == 0);
    assert(andDescriptor.onSignal(a) == Result::Ok && a.emittedBranches == 0 && andOutput[0] == 0);
    assert(andDescriptor.onSignal(b) == Result::Ok && b.emittedBranches == 1 && andOutput[0] == 1);
    assert(andDescriptor.onSignal(a) == Result::Ok && a.emittedBranches == 0 && andOutput[0] == 1);

    auto reset = invocationFor(andDescriptor, andState.data(), andState.size(), andOutput.data(), 2, 7);
    assert(andDescriptor.onSignal(reset) == Result::Ok && reset.emittedBranches == 0 && andOutput[0] == 0);
    assert(andDescriptor.onSignal(a) == Result::Ok && a.emittedBranches == 0);
    assert(andDescriptor.onSignal(b) == Result::Ok && b.emittedBranches == 0 &&
        andOutput[0] == 0); // Reset cannot bypass the one-emission-per-tick guard.

    // SameUpdate discards a partial slot mask at the next successful update.
    auto nextUpdate = invocationFor(andDescriptor, andState.data(), andState.size(), andOutput.data(), noSlot, 8);
    assert(andDescriptor.update(nextUpdate) == Result::Ok && andOutput[0] == 0);
    andDescriptor.commit(nextUpdate);
    a.eventTick = 8;
    b.eventTick = 8;
    assert(andDescriptor.onSignal(a) == Result::Ok && a.emittedBranches == 0);
    assert(andDescriptor.onSignal(b) == Result::Ok && b.emittedBranches == 1 && andOutput[0] == 1);
    auto laterUpdate = invocationFor(andDescriptor, andState.data(), andState.size(), andOutput.data(), noSlot, 9);
    assert(andDescriptor.update(laterUpdate) == Result::Ok && andOutput[0] == 0);
    andDescriptor.commit(laterUpdate);
    a.eventTick = 9;
    b.eventTick = 9;
    assert(andDescriptor.onSignal(a) == Result::Ok && a.emittedBranches == 0);
    auto resetBeforeFire = invocationFor(andDescriptor, andState.data(), andState.size(), andOutput.data(), 2, 9);
    assert(andDescriptor.onSignal(resetBeforeFire) == Result::Ok && andOutput[0] == 0);
    assert(andDescriptor.onSignal(b) == Result::Ok && b.emittedBranches == 0);
    assert(andDescriptor.onSignal(b) == Result::Ok && b.emittedBranches == 0);
    assert(andDescriptor.onSignal(a) == Result::Ok && a.emittedBranches == 1 && andOutput[0] == 1);

    // Accumulating mode keeps the first distinct slot across update ticks.
    auto accumulatedState = StateBuffer{};
    alignas(16) auto accumulatedOutput = std::array<double, 4>{};
    auto accumulatingUpdate = invocationFor(andDescriptor, accumulatedState.data(), accumulatedState.size(),
        accumulatedOutput.data(), noSlot, 1, 1);
    assert(andDescriptor.update(accumulatingUpdate) == Result::Ok);
    andDescriptor.commit(accumulatingUpdate);
    auto accumulatedA = invocationFor(andDescriptor, accumulatedState.data(), accumulatedState.size(),
        accumulatedOutput.data(), 0, 1, 1);
    auto accumulatedB = invocationFor(andDescriptor, accumulatedState.data(), accumulatedState.size(),
        accumulatedOutput.data(), 1, 2, 1);
    assert(andDescriptor.onSignal(accumulatedA) == Result::Ok && accumulatedA.emittedBranches == 0);
    assert(andDescriptor.onSignal(accumulatedA) == Result::Ok && accumulatedA.emittedBranches == 0);
    accumulatingUpdate.eventTick = 2;
    assert(andDescriptor.update(accumulatingUpdate) == Result::Ok && accumulatedOutput[0] == 0);
    andDescriptor.commit(accumulatingUpdate);
    assert(andDescriptor.onSignal(accumulatedB) == Result::Ok &&
        accumulatedB.emittedBranches == 1 && accumulatedOutput[0] == 1);

    auto orA = invocationFor(orDescriptor, orState.data(), orState.size(), orOutput.data(), 0, 7);
    auto orB = invocationFor(orDescriptor, orState.data(), orState.size(), orOutput.data(), 1, 7);
    auto orReset = invocationFor(orDescriptor, orState.data(), orState.size(), orOutput.data(), 2, 7);
    assert(orDescriptor.onSignal(orA) == Result::Ok && orA.emittedBranches == 1 && orOutput[0] == 1);
    assert(orDescriptor.onSignal(orB) == Result::Ok && orB.emittedBranches == 0 && orOutput[0] == 1);
    assert(orDescriptor.onSignal(orReset) == Result::Ok && orOutput[0] == 0);
    assert(orDescriptor.onSignal(orB) == Result::Ok && orB.emittedBranches == 0 && orOutput[0] == 0);

    // Caller-owned state for distinct gates cannot satisfy one another.
    auto isolatedState = StateBuffer{};
    alignas(16) auto isolatedOutput = std::array<double, 4>{};
    auto isolatedB = invocationFor(andDescriptor, isolatedState.data(), isolatedState.size(),
        isolatedOutput.data(), 1, 10);
    auto isolatedUpdate = invocationFor(andDescriptor, andState.data(), andState.size(),
        andOutput.data(), noSlot, 10);
    assert(andDescriptor.update(isolatedUpdate) == Result::Ok);
    andDescriptor.commit(isolatedUpdate);
    a.eventTick = 10;
    assert(andDescriptor.onSignal(a) == Result::Ok);
    assert(andDescriptor.onSignal(isolatedB) == Result::Ok && isolatedOutput[0] == 0);
}

void verifySignalToNumber()
{
    auto descriptor = findDescriptor(signalToNumberId);
    assert(descriptor.update != nullptr && descriptor.onSignal != nullptr);
    assert(descriptor.parameterCount == 1 && descriptor.signalInputCount == 2 &&
        descriptor.outputWidth == 1 && descriptor.signalBranchCount == 0);

    auto state = StateBuffer{};
    alignas(16) auto output = std::array<double, 4>{};
    auto pulseUpdate = invocationFor(descriptor, state.data(), state.size(), output.data(), noSlot, 1, 0);
    assert(descriptor.update(pulseUpdate) == Result::Ok && output[0] == 0);
    auto signal = invocationFor(descriptor, state.data(), state.size(), output.data(), 0, 1, 0);
    assert(descriptor.onSignal(signal) == Result::Ok && output[0] == 1);
    assert(descriptor.onSignal(signal) == Result::Ok && output[0] == 1);
    auto reset = invocationFor(descriptor, state.data(), state.size(), output.data(), 1, 1, 0);
    assert(descriptor.onSignal(reset) == Result::Ok && output[0] == 0);
    pulseUpdate.eventTick = 2;
    assert(descriptor.update(pulseUpdate) == Result::Ok && output[0] == 0);

    auto latchState = StateBuffer{};
    alignas(16) auto latchOutput = std::array<double, 4>{};
    auto latchUpdate = invocationFor(descriptor, latchState.data(), latchState.size(), latchOutput.data(), noSlot, 1, 1);
    auto latchSignal = invocationFor(descriptor, latchState.data(), latchState.size(), latchOutput.data(), 0, 1, 1);
    auto latchReset = invocationFor(descriptor, latchState.data(), latchState.size(), latchOutput.data(), 1, 2, 1);
    assert(descriptor.update(latchUpdate) == Result::Ok && latchOutput[0] == 0);
    assert(descriptor.onSignal(latchSignal) == Result::Ok && latchOutput[0] == 1);
    latchUpdate.eventTick = 2;
    assert(descriptor.update(latchUpdate) == Result::Ok && latchOutput[0] == 1);
    assert(descriptor.onSignal(latchReset) == Result::Ok && latchOutput[0] == 0);
    latchUpdate.eventTick = 3;
    assert(descriptor.update(latchUpdate) == Result::Ok && latchOutput[0] == 0);

    auto counterState = StateBuffer{};
    alignas(16) auto counterOutput = std::array<double, 4>{};
    auto counterUpdate = invocationFor(descriptor, counterState.data(), counterState.size(), counterOutput.data(), noSlot, 1, 2);
    auto counterSignal = invocationFor(descriptor, counterState.data(), counterState.size(), counterOutput.data(), 0, 1, 2);
    auto counterReset = invocationFor(descriptor, counterState.data(), counterState.size(), counterOutput.data(), 1, 1, 2);
    assert(descriptor.update(counterUpdate) == Result::Ok && counterOutput[0] == 0);
    assert(descriptor.onSignal(counterSignal) == Result::Ok && counterOutput[0] == 1);
    assert(descriptor.onSignal(counterSignal) == Result::Ok && counterOutput[0] == 2);
    counterUpdate.eventTick = 2;
    assert(descriptor.update(counterUpdate) == Result::Ok && counterOutput[0] == 2);
    counterReset.eventTick = 2;
    assert(descriptor.onSignal(counterReset) == Result::Ok && counterOutput[0] == 0);
    assert(descriptor.onSignal(counterSignal) == Result::Ok && counterOutput[0] == 1);

    // Exercise the precision boundary without requiring an impractical number of events.
    std::memcpy(counterState.bytes.data(), &maxExactInteger, sizeof(maxExactInteger));
    auto beforeOverflow = counterOutput[0];
    assert(descriptor.onSignal(counterSignal) == Result::CounterOverflow);
    assert(counterOutput[0] == beforeOverflow);
}

auto loadFile(const std::filesystem::path& path) -> std::vector<std::uint8_t>
{
    auto file = std::ifstream(path, std::ios::binary);
    assert(file.good());
    return {std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
}

auto loadProgramFile(const std::filesystem::path& path) -> std::vector<std::uint8_t>
{
    auto bytes = loadFile(path);
    auto package = SpawnablePackage{};
    assert(loadSpawnablePackage(bytes.data(), bytes.size(), package) == Result::Ok);
    return std::move(package.parentBytecode);
}

auto runInline(void* context, Job job, void* jobContext) noexcept -> bool
{
    (void)context;
    job(jobContext);
    return true;
}

auto loadLogicalProgram(const std::vector<std::uint8_t>& bytes, Program& program,
                        std::vector<CustomNodeBinding>& bindings) -> Result
{
    bindings.resize(maxFixtureInstructions);
    auto result = Program::load(bytes.data(), bytes.size(), nullptr, 0,
        bindings.data(), bindings.size(), program);
    if (result != Result::Ok)
    {
        std::fprintf(stderr, "Program::load failed: Result=%d bytes=%zu bindings=%zu\n",
            static_cast<int>(result), bytes.size(), bindings.size());
        std::fflush(stderr);
        std::exit(2);
    }
    return result;
}

auto makeDefaults(const Program& program) -> std::vector<std::max_align_t>
{
    auto parameterCount = program.parameterValueCount();
    auto storageCount = std::max(std::size_t{1},
        (parameterCount * sizeof(double) + sizeof(std::max_align_t) - 1) / sizeof(std::max_align_t));
    return std::vector<std::max_align_t>(storageCount);
}

void verifyCompiledFixture(const std::filesystem::path& directory)
{
    auto bytes = loadProgramFile(directory / "logical-runtime.lfxp");
    auto bindings = std::vector<CustomNodeBinding>{};
    auto program = Program{};
    assert(loadLogicalProgram(bytes, program, bindings) == Result::Ok);
    assert(program.version() == 11);
    auto workWords = (program.workBytes() + sizeof(double) - 1) / sizeof(double);
    auto work = std::vector<std::max_align_t>(
        (workWords * sizeof(double) + sizeof(std::max_align_t) - 1) / sizeof(std::max_align_t));
    auto defaults = makeDefaults(program);
    auto instance = Instance{};
    assert(instance.initialize(program, reinterpret_cast<double*>(work.data()), workWords,
        reinterpret_cast<double*>(defaults.data()), program.parameterValueCount()) == Result::Ok);

    auto bindValue = [&](const std::array<std::uint8_t, 16>& id)
    {
        auto handle = ValueHandle{};
        assert(instance.bindValue(id.data(), handle) == Result::Ok);
        return handle;
    };
    auto value = [&](const ValueHandle& handle)
    {
        auto components = std::array<double, 4>{};
        assert(instance.value(handle, components.data()) == Result::Ok);
        return components;
    };
    auto dispatch = [&](const std::array<std::uint8_t, 16>& id)
    {
        auto handle = SignalHandle{};
        assert(instance.bindSignal(id.data(), handle) == Result::Ok);
        assert(instance.dispatchSignal(handle, 0, 128) == Result::Ok);
        assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    };

    // IDs are shared with Tests/LogicalSignalTests.cs and intentionally fixed so
    // this native fixture exercises compiler-produced bytecode and routes.
    constexpr auto compare = fixtureNode(1);
    constexpr auto compareCounter = fixtureNode(2);
    constexpr auto andNode = fixtureNode(3);
    constexpr auto andCounter = fixtureNode(4);
    constexpr auto andA = fixtureNode(5);
    constexpr auto andB = fixtureNode(6);
    constexpr auto andReset = fixtureNode(7);
    constexpr auto andAdd = fixtureNode(8);
    constexpr auto andVector = fixtureNode(9);
    constexpr auto orNode = fixtureNode(14);
    constexpr auto orA = fixtureNode(15);
    constexpr auto orB = fixtureNode(16);
    constexpr auto orReset = fixtureNode(17);
    constexpr auto orCounter = fixtureNode(18);
    constexpr auto accumulatingAnd = fixtureNode(19);
    constexpr auto accumulatingA = fixtureNode(20);
    constexpr auto accumulatingB = fixtureNode(21);
    constexpr auto accumulatingCounter = fixtureNode(23);
    constexpr auto pulseNode = fixtureNode(24);
    constexpr auto pulseSignal = fixtureNode(25);
    constexpr auto pulseReset = fixtureNode(26);
    constexpr auto latchNode = fixtureNode(27);
    constexpr auto latchSignal = fixtureNode(28);
    constexpr auto latchReset = fixtureNode(29);
    constexpr auto counterNode = fixtureNode(30);
    constexpr auto counterSignal = fixtureNode(31);
    constexpr auto counterReset = fixtureNode(32);
    constexpr auto eachCounter = fixtureNode(36);
    constexpr auto compareLessEqual = fixtureNode(37);
    constexpr auto compareEqual = fixtureNode(38);
    constexpr auto compareNotEqual = fixtureNode(39);
    constexpr auto compareGreaterEqual = fixtureNode(40);
    constexpr auto compareGreater = fixtureNode(41);

    auto condition = bindValue(compare);
    auto compareCount = bindValue(compareCounter);
    auto pulse = bindValue(andNode);
    auto andCount = bindValue(andCounter);
    auto addValue = bindValue(andAdd);
    auto vectorValue = bindValue(andVector);
    auto lessEqualValue = bindValue(compareLessEqual);
    auto equalValue = bindValue(compareEqual);
    auto notEqualValue = bindValue(compareNotEqual);
    auto greaterEqualValue = bindValue(compareGreaterEqual);
    auto greaterValue = bindValue(compareGreater);
    auto eachCount = bindValue(eachCounter);
    auto orPulse = bindValue(orNode);
    auto orCount = bindValue(orCounter);
    auto accumulatedPulse = bindValue(accumulatingAnd);
    auto accumulatedCount = bindValue(accumulatingCounter);
    auto pulseModeValue = bindValue(pulseNode);
    auto latchValue = bindValue(latchNode);
    auto counterValue = bindValue(counterNode);

    assert(instance.updateAsync(0, runInline, nullptr) == Result::Ok);
    assert(instance.result() == Result::Ok && value(condition)[0] == 1);
    assert(value(lessEqualValue)[0] == 1 && value(equalValue)[0] == 1 &&
        value(notEqualValue)[0] == 1 && value(greaterEqualValue)[0] == 1 &&
        value(greaterValue)[0] == 1);
    assert(instance.dispatchGeneratedSignals(0) == Result::InvalidArgument);
    assert(instance.dispatchGeneratedSignals(1) == Result::InstructionBudgetExceeded);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    assert(value(compareCount)[0] == 1); // Initial true emitted in Edge mode, without a frame delay.
    assert(value(eachCount)[0] == 1);

    dispatch(andA);
    dispatch(andA);
    assert(value(pulse)[0] == 0 && value(andCount)[0] == 0);
    dispatch(andB);
    assert(value(pulse)[0] == 1 && value(andCount)[0] == 1);
    assert(value(addValue)[0] == 6);
    auto vector = value(vectorValue);
    assert(vector[0] == 1 && vector[1] == 1 && vector[2] == 1);
    dispatch(andB);
    assert(value(andCount)[0] == 1 && value(pulse)[0] == 1);
    dispatch(andReset);
    assert(value(pulse)[0] == 0 && value(addValue)[0] == 5);
    dispatch(andA);
    dispatch(andB);
    assert(value(andCount)[0] == 1 && value(pulse)[0] == 0);

    // OR coalesces both input slots to one emission for this update.
    dispatch(orA);
    dispatch(orB);
    dispatch(orA);
    assert(value(orPulse)[0] == 1 && value(orCount)[0] == 1);
    dispatch(orReset);
    assert(value(orPulse)[0] == 0 && value(orCount)[0] == 1);

    // Accumulating AND retains its first slot across a successful update.
    dispatch(accumulatingA);
    assert(value(accumulatedPulse)[0] == 0 && value(accumulatedCount)[0] == 0);

    // Signal To Number Pulse, Latch and Counter use ordered Reset events.
    dispatch(pulseSignal);
    dispatch(pulseSignal);
    assert(value(pulseModeValue)[0] == 1);
    dispatch(pulseReset);
    assert(value(pulseModeValue)[0] == 0);
    dispatch(latchSignal);
    assert(value(latchValue)[0] == 1);
    dispatch(latchReset);
    assert(value(latchValue)[0] == 0);
    dispatch(counterSignal);
    dispatch(counterSignal);
    assert(value(counterValue)[0] == 2);
    dispatch(counterReset);
    assert(value(counterValue)[0] == 0);
    dispatch(counterSignal);
    assert(value(counterValue)[0] == 1);

    auto compareInput = ParameterHandle{};
    assert(instance.bindParameter(fixtureNode(10).data(), 0, 0, compareInput) == Result::Ok);
    assert(instance.setParameter(compareInput, 3) == Result::Ok);
    assert(instance.updateAsync(2, runInline, nullptr) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    assert(value(compareCount)[0] == 1 && value(eachCount)[0] == 2);
    assert(value(condition)[0] == 0);
    assert(value(pulseModeValue)[0] == 0 && value(latchValue)[0] == 0 &&
        value(counterValue)[0] == 1);
    assert(value(accumulatedPulse)[0] == 0);
    assert(instance.setParameter(compareInput, 1) == Result::Ok);
    assert(instance.updateAsync(3, runInline, nullptr) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    assert(value(condition)[0] == 1 && value(compareCount)[0] == 2 && value(eachCount)[0] == 3);
    dispatch(accumulatingB);
    assert(value(accumulatedPulse)[0] == 1 && value(accumulatedCount)[0] == 1);

    // A second Instance uses independent caller-owned logical state.
    auto secondWork = std::vector<std::max_align_t>(
        (workWords * sizeof(double) + sizeof(std::max_align_t) - 1) / sizeof(std::max_align_t));
    auto secondDefaults = makeDefaults(program);
    auto second = Instance{};
    assert(second.initialize(program, reinterpret_cast<double*>(secondWork.data()), workWords,
        reinterpret_cast<double*>(secondDefaults.data()), program.parameterValueCount()) == Result::Ok);
    assert(instance.updateAsync(4, runInline, nullptr) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    assert(second.updateAsync(4, runInline, nullptr) == Result::Ok);
    assert(second.dispatchGeneratedSignals(128) == Result::Ok);
    auto firstAndA = SignalHandle{};
    auto firstAndB = SignalHandle{};
    auto secondAndA = SignalHandle{};
    auto secondAndB = SignalHandle{};
    assert(instance.bindSignal(andA.data(), firstAndA) == Result::Ok);
    assert(instance.bindSignal(andB.data(), firstAndB) == Result::Ok);
    assert(second.bindSignal(andA.data(), secondAndA) == Result::Ok);
    assert(second.bindSignal(andB.data(), secondAndB) == Result::Ok);
    assert(instance.dispatchSignal(firstAndA, 1, 128) == Result::Ok);
    assert(second.dispatchSignal(secondAndB, 1, 128) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    assert(second.dispatchGeneratedSignals(128) == Result::Ok);
    auto secondPulseHandle = ValueHandle{};
    assert(second.bindValue(andNode.data(), secondPulseHandle) == Result::Ok);
    auto secondPulse = std::array<double, 4>{};
    auto firstPulse = std::array<double, 4>{};
    assert(instance.value(pulse, firstPulse.data()) == Result::Ok && firstPulse[0] == 0);
    assert(second.value(secondPulseHandle, secondPulse.data()) == Result::Ok && secondPulse[0] == 0);
    assert(instance.dispatchSignal(firstAndB, 1, 128) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    assert(instance.value(pulse, firstPulse.data()) == Result::Ok && firstPulse[0] == 1);
    assert(second.dispatchSignal(secondAndA, 1, 128) == Result::Ok);
    assert(second.dispatchGeneratedSignals(128) == Result::Ok);
    assert(second.value(secondPulseHandle, secondPulse.data()) == Result::Ok && secondPulse[0] == 1);
}

void verifyGroupIsolationFixture(const std::filesystem::path& directory)
{
    auto bytes = loadProgramFile(directory / "logical-groups.lfxp");
    auto bindings = std::vector<CustomNodeBinding>{};
    auto program = Program{};
    assert(loadLogicalProgram(bytes, program, bindings) == Result::Ok);
    assert(program.version() == 11);
    auto workWords = (program.workBytes() + sizeof(double) - 1) / sizeof(double);
    auto work = std::vector<std::max_align_t>(
        (workWords * sizeof(double) + sizeof(std::max_align_t) - 1) / sizeof(std::max_align_t));
    auto defaults = makeDefaults(program);
    auto instance = Instance{};
    assert(instance.initialize(program, reinterpret_cast<double*>(work.data()), workWords,
        reinterpret_cast<double*>(defaults.data()), program.parameterValueCount()) == Result::Ok);

    auto bindValue = [&](const std::array<std::uint8_t, 16>& id)
    {
        auto handle = ValueHandle{};
        assert(instance.bindValue(id.data(), handle) == Result::Ok);
        return handle;
    };
    auto bindSignal = [&](const std::array<std::uint8_t, 16>& id)
    {
        auto handle = SignalHandle{};
        assert(instance.bindSignal(id.data(), handle) == Result::Ok);
        return handle;
    };
    auto value = [&](const ValueHandle& handle)
    {
        auto components = std::array<double, 4>{};
        assert(instance.value(handle, components.data()) == Result::Ok);
        return components[0];
    };
    auto dispatch = [&](const std::array<std::uint8_t, 16>& id)
    {
        auto handle = bindSignal(id);
        assert(instance.dispatchSignal(handle, 0, 128) == Result::Ok);
        assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    };

    auto groupOneCount = bindValue(fixtureNode(90));
    auto groupTwoCount = bindValue(fixtureNode(91));
    auto groupOnePulseConsumer = bindValue(fixtureNode(93));
    auto groupTwoPulseConsumer = bindValue(fixtureNode(95));
    assert(instance.updateAsync(0, runInline, nullptr) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    assert(value(groupOneCount) == 0 && value(groupTwoCount) == 0);
    assert(value(groupOnePulseConsumer) == 10 && value(groupTwoPulseConsumer) == 20);

    // Interleaving A from one Group and B from another must not satisfy either gate.
    dispatch(fixtureNode(80));
    dispatch(fixtureNode(84));
    assert(value(groupOneCount) == 0 && value(groupTwoCount) == 0);
    assert(value(groupOnePulseConsumer) == 10 && value(groupTwoPulseConsumer) == 20);

    dispatch(fixtureNode(81));
    assert(value(groupOneCount) == 1 && value(groupTwoCount) == 0);
    assert(value(groupOnePulseConsumer) == 11 && value(groupTwoPulseConsumer) == 20);
    dispatch(fixtureNode(83));
    assert(value(groupOneCount) == 1 && value(groupTwoCount) == 1);
    assert(value(groupOnePulseConsumer) == 11 && value(groupTwoPulseConsumer) == 21);

    assert(instance.updateAsync(1, runInline, nullptr) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    assert(value(groupOneCount) == 1 && value(groupTwoCount) == 1);
    assert(value(groupOnePulseConsumer) == 10 && value(groupTwoPulseConsumer) == 20);
}

void verifyGuardedLogicalFixture(const std::filesystem::path& directory)
{
    auto bytes = loadProgramFile(directory / "logical-guards.lfxp");
    auto bindings = std::vector<CustomNodeBinding>{};
    auto program = Program{};
    assert(loadLogicalProgram(bytes, program, bindings) == Result::Ok);
    assert(program.version() == 11);
    auto workWords = (program.workBytes() + sizeof(double) - 1) / sizeof(double);
    auto work = std::vector<std::max_align_t>(
        (workWords * sizeof(double) + sizeof(std::max_align_t) - 1) / sizeof(std::max_align_t));
    auto defaults = makeDefaults(program);
    auto instance = Instance{};
    assert(instance.initialize(program, reinterpret_cast<double*>(work.data()), workWords,
        reinterpret_cast<double*>(defaults.data()), program.parameterValueCount()) == Result::Ok);

    auto bindValue = [&](const std::array<std::uint8_t, 16>& id)
    {
        auto handle = ValueHandle{};
        assert(instance.bindValue(id.data(), handle) == Result::Ok);
        return handle;
    };
    auto readValue = [&](const ValueHandle& handle)
    {
        auto components = std::array<double, 4>{};
        assert(instance.value(handle, components.data()) == Result::Ok);
        return components[0];
    };
    auto bindParameter = [&](const std::array<std::uint8_t, 16>& id)
    {
        auto handle = ParameterHandle{};
        assert(instance.bindParameter(id.data(), 0, 0, handle) == Result::Ok);
        return handle;
    };
    auto dispatch = [&](const std::array<std::uint8_t, 16>& id)
    {
        auto handle = SignalHandle{};
        assert(instance.bindSignal(id.data(), handle) == Result::Ok);
        assert(instance.dispatchSignal(handle, 0, 128) == Result::Ok);
        assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    };
    auto compareCount = bindValue(fixtureNode(105));
    auto andPulse = bindValue(fixtureNode(106));
    auto andCount = bindValue(fixtureNode(110));
    auto guardedCounter = bindValue(fixtureNode(111));
    auto mode = bindParameter(fixtureNode(100));
    auto compareB = bindParameter(fixtureNode(104));

    // Enum case 1 is active. Compare, AND and their counters are guarded to case 0.
    assert(instance.updateAsync(0, runInline, nullptr) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    assert(readValue(compareCount) == 0 && readValue(andCount) == 0);
    dispatch(fixtureNode(107)); // Inactive AND arrivals are discarded.
    dispatch(fixtureNode(112));
    assert(readValue(guardedCounter) == 1);

    // Compare becomes true while inactive; its Edge history must remain untouched.
    assert(instance.setParameter(compareB, 2) == Result::Ok);
    assert(instance.updateAsync(1, runInline, nullptr) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    assert(readValue(compareCount) == 0);

    assert(instance.setParameter(mode, 0) == Result::Ok);
    assert(instance.updateAsync(2, runInline, nullptr) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    assert(readValue(compareCount) == 1); // First active true condition emits.
    dispatch(fixtureNode(108));
    assert(readValue(andPulse) == 0 && readValue(andCount) == 0); // Inactive A did not stick.
    dispatch(fixtureNode(107));
    assert(readValue(andPulse) == 1 && readValue(andCount) == 1);

    // A false condition during an inactive interval must not change Compare's
    // last committed true edge state. The reactivated true value is not a new edge.
    assert(instance.setParameter(mode, 1) == Result::Ok);
    assert(instance.setParameter(compareB, 3) == Result::Ok);
    assert(instance.updateAsync(3, runInline, nullptr) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    assert(readValue(compareCount) == 1);
    dispatch(fixtureNode(112));
    assert(readValue(guardedCounter) == 2);

    assert(instance.setParameter(mode, 0) == Result::Ok);
    assert(instance.setParameter(compareB, 2) == Result::Ok);
    assert(instance.updateAsync(4, runInline, nullptr) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    assert(readValue(compareCount) == 1); // Inactive Compare did not commit false.
    dispatch(fixtureNode(112)); // Counter is now inactive and must not mutate.
    assert(readValue(guardedCounter) == 2);
    dispatch(fixtureNode(107)); // Accumulate A while active.
    assert(readValue(andPulse) == 0 && readValue(andCount) == 1);

    assert(instance.setParameter(mode, 1) == Result::Ok);
    assert(instance.updateAsync(5, runInline, nullptr) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    dispatch(fixtureNode(108)); // B arrives while AND is inactive and is discarded.
    assert(readValue(andCount) == 1);

    assert(instance.setParameter(mode, 0) == Result::Ok);
    assert(instance.updateAsync(6, runInline, nullptr) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    dispatch(fixtureNode(108));
    assert(readValue(andPulse) == 1 && readValue(andCount) == 2);
}

void verifyMatrixLateRefreshFixture(const std::filesystem::path& directory)
{
    auto bytes = loadProgramFile(directory / "logical-matrix.lfxp");
    auto bindings = std::vector<CustomNodeBinding>{};
    auto program = Program{};
    assert(loadLogicalProgram(bytes, program, bindings) == Result::Ok);
    auto workWords = (program.workBytes() + sizeof(double) - 1) / sizeof(double);
    auto work = std::vector<std::max_align_t>(
        (workWords * sizeof(double) + sizeof(std::max_align_t) - 1) / sizeof(std::max_align_t));
    auto defaults = makeDefaults(program);
    auto instance = Instance{};
    assert(instance.initialize(program, reinterpret_cast<double*>(work.data()), workWords,
        reinterpret_cast<double*>(defaults.data()), program.parameterValueCount()) == Result::Ok);
    auto read = [&](std::uint8_t id, std::array<double, 4>& values, std::uint8_t& width)
    {
        auto node = fixtureNode(id);
        assert(instance.valueComponents(node.data(), values.data(), width) == Result::Ok);
    };

    auto counter = std::array<double, 4>{};
    auto position = std::array<double, 4>{};
    auto counterWidth = std::uint8_t{};
    auto positionWidth = std::uint8_t{};
    assert(instance.updateAsync(0, runInline, nullptr) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    read(233, counter, counterWidth);
    read(236, position, positionWidth);
    assert(counterWidth == 1 && counter[0] == 1);
    assert(positionWidth == 3 && position[0] == 1);

    // The converter's render config points ModelDraw.position at this exact
    // DecomposeTransform node; both values must be visible in their update tick.
    assert(instance.updateAsync(1, runInline, nullptr) == Result::Ok);
    assert(instance.dispatchGeneratedSignals(128) == Result::Ok);
    read(233, counter, counterWidth);
    read(236, position, positionWidth);
    assert(counterWidth == 1 && counter[0] == 2);
    assert(positionWidth == 3 && position[0] == 2);
}

auto overrideBuiltinMatrixCallback(CustomNodeInvocation& call) noexcept -> Result
{
    auto calls = static_cast<unsigned*>(call.context);
    ++*calls;
    for (auto lane = std::uint8_t{}; lane < call.outputWidth; ++lane)
        call.outputs[lane] = 0;
    return Result::Ok;
}

void verifyLateRefreshRejectsShadowedBuiltin(const std::filesystem::path& directory)
{
    auto bytes = loadProgramFile(directory / "logical-matrix.lfxp");
    constexpr auto matrixTransformRow0 = std::array<std::uint8_t, 16>{
        0xb0, 0xaf, 0x61, 0xd2, 0x29, 0xf6, 0x4c, 0xc9,
        0xa7, 0x1e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01};
    auto builtinCount = std::size_t{};
    auto builtins = builtinNodes(builtinCount);
    auto original = static_cast<const CustomNodeDescriptor*>(nullptr);
    for (auto index = std::size_t{}; index < builtinCount; ++index)
        if (std::equal(matrixTransformRow0.begin(), matrixTransformRow0.end(), builtins[index].typeId))
            original = &builtins[index];
    assert(original != nullptr);

    auto calls = 0u;
    auto shadow = *original;
    shadow.function = overrideBuiltinMatrixCallback;
    shadow.context = &calls;
    auto bindings = std::vector<CustomNodeBinding>(maxFixtureInstructions);
    auto program = Program{};
    assert(Program::load(bytes.data(), bytes.size(), &shadow, 1,
        bindings.data(), bindings.size(), program) == Result::Ok);
    auto workWords = (program.workBytes() + sizeof(double) - 1) / sizeof(double);
    auto work = std::vector<std::max_align_t>(
        (workWords * sizeof(double) + sizeof(std::max_align_t) - 1) / sizeof(std::max_align_t));
    auto defaults = makeDefaults(program);
    auto instance = Instance{};
    assert(instance.initialize(program, reinterpret_cast<double*>(work.data()), workWords,
        reinterpret_cast<double*>(defaults.data()), program.parameterValueCount()) == Result::Ok);
    assert(instance.updateAsync(0, runInline, nullptr) == Result::Ok);
    assert(calls == 1);
    assert(instance.dispatchGeneratedSignals(128) == Result::UnsupportedBinary);
    assert(calls == 1); // The external override was not called during late refresh.
}

void verifyHostIntegrationFixture(const std::filesystem::path& directory)
{
    auto bytes = loadProgramFile(directory / "logical-host.lfxp");
    auto bindings = std::vector<CustomNodeBinding>{};
    auto program = Program{};
    assert(loadLogicalProgram(bytes, program, bindings) == Result::Ok);
    assert(program.version() == 11);

    auto initialize = [&](Instance& instance, std::vector<std::max_align_t>& work,
                          std::vector<std::max_align_t>& defaults)
    {
        auto workWords = (program.workBytes() + sizeof(double) - 1) / sizeof(double);
        work.resize((workWords * sizeof(double) + sizeof(std::max_align_t) - 1) /
            sizeof(std::max_align_t));
        defaults = makeDefaults(program);
        assert(instance.initialize(program, reinterpret_cast<double*>(work.data()), workWords,
            reinterpret_cast<double*>(defaults.data()), program.parameterValueCount()) == Result::Ok);
    };
    auto readValue = [](Instance& instance, std::uint8_t id)
    {
        auto node = fixtureNode(id);
        auto value = 0.0;
        assert(instance.value(node.data(), value) == Result::Ok);
        return value;
    };

    // NamedIo automatically drains generated events before publishing outputs.
    {
        auto instance = Instance{};
        auto work = std::vector<std::max_align_t>{};
        auto defaults = std::vector<std::max_align_t>{};
        initialize(instance, work, defaults);
        auto ports = std::array<NamedIoPort, 7>{};
        auto setPort = [&](std::size_t index, const char* name, NamedIoKind kind,
                           std::uint8_t id, std::uint8_t width)
        {
            ports[index].name = name;
            ports[index].kind = kind;
            ports[index].width = width;
            auto node = fixtureNode(id);
            std::copy(node.begin(), node.end(), ports[index].nodeId);
        };
        setPort(0, "Threshold", NamedIoKind::InputProperty, 210, 1);
        setPort(1, "Manual", NamedIoKind::InputSignal, 217, 0);
        setPort(2, "StartCount", NamedIoKind::OutputProperty, 221, 1);
        setPort(3, "UpdateCount", NamedIoKind::OutputProperty, 222, 1);
        setPort(4, "ManualCount", NamedIoKind::OutputProperty, 223, 1);
        setPort(5, "StartAdd", NamedIoKind::OutputProperty, 224, 1);
        setPort(6, "UpdateAdd", NamedIoKind::OutputProperty, 225, 1);
        auto io = std::unique_ptr<NamedIo>{};
        assert(NamedIo::create(instance, ports.data(), ports.size(), 32, io) == Result::Ok);
        auto latest = std::array<double, 5>{};
        auto drain = [&]()
        {
            auto event = NamedIoEvent{};
            while (io->popEvent(event))
            {
                if (event.kind != NamedIoKind::OutputProperty) continue;
                auto name = io->portName(event.portIndex);
                assert(name != nullptr);
                if (std::strcmp(name, "StartCount") == 0) latest[0] = event.values[0];
                else if (std::strcmp(name, "UpdateCount") == 0) latest[1] = event.values[0];
                else if (std::strcmp(name, "ManualCount") == 0) latest[2] = event.values[0];
                else if (std::strcmp(name, "StartAdd") == 0) latest[3] = event.values[0];
                else if (std::strcmp(name, "UpdateAdd") == 0) latest[4] = event.values[0];
            }
        };
        assert(io->start(0, nullptr, 128) == Result::Ok);
        drain();
        assert(latest[0] == 1 && latest[1] == 0 && latest[2] == 0 &&
            latest[3] == 1 && latest[4] == 5);
        assert(io->fireSignal("Manual", 0, 128) == Result::Ok);
        drain();
        assert(latest[2] == 1 && latest[3] == 2);
        auto threshold = 2.0;
        assert(io->setProperty("Threshold", &threshold, 1) == Result::Ok);
        assert(io->update(1) == Result::Ok);
        drain();
        assert(latest[0] == 1 && latest[1] == 1 && latest[2] == 1 &&
            latest[3] == 1 && latest[4] == 6);
    }

    // Direct EffectLifecycle calls have the same automatic flush boundary.
    {
        auto instance = Instance{};
        auto work = std::vector<std::max_align_t>{};
        auto defaults = std::vector<std::max_align_t>{};
        initialize(instance, work, defaults);
        auto thresholdNode = fixtureNode(210);
        auto threshold = ParameterHandle{};
        assert(instance.bindParameter(thresholdNode.data(), 0, 0, threshold) == Result::Ok);
        auto lifecycle = EffectLifecycle(instance);
        auto sample = LifetimeSample{};
        assert(lifecycle.start(Lifetime::infinite(), nullptr, 128, sample) == Result::Ok);
        assert(readValue(instance, 207) == 1 && readValue(instance, 215) == 0);
        assert(readValue(instance, 219) == 1 && readValue(instance, 220) == 5);
        assert(instance.setParameter(threshold, 2) == Result::Ok);
        assert(lifecycle.advance(1, sample) == Result::Ok);
        assert(readValue(instance, 207) == 1 && readValue(instance, 215) == 1);
        assert(readValue(instance, 219) == 0 && readValue(instance, 220) == 6);
    }

    // GeneratorPool's emitted Signal timestamps use start + local age; no
    // external dispatch call is made between start/evaluate and the assertions.
    {
        auto container = fixtureNode(250);
        auto pool = std::unique_ptr<GeneratorPool>{};
        assert(GeneratorPool::create(program, container.data(), 1, 1u << 20u, 32, pool) == Result::Ok);
        auto lifecycle = GeneratorLifecycle{};
        lifecycle.instructionBudget = 128;
        assert(pool->configureLifecycle(lifecycle) == Result::Ok);
        auto outputIds = std::array<std::uint8_t, 2 * 16>{};
        auto outputNodes = std::array<std::array<std::uint8_t, 16>, 2>{
            fixtureNode(207), fixtureNode(215)};
        for (auto index = std::size_t{}; index < outputNodes.size(); ++index)
            std::copy(outputNodes[index].begin(), outputNodes[index].end(), outputIds.begin() + index * 16);
        assert(pool->setSignalOutputs(outputIds.data(), outputNodes.size()) == Result::Ok);
        auto instanceId = GeneratorInstanceId{};
        assert(pool->spawn(10, nullptr, 0, instanceId) == Result::Ok);
        assert(pool->start(instanceId) == Result::Ok);
        auto event = GeneratorSignalEvent{};
        auto startEvents = std::array<std::array<std::uint8_t, 16>, 1>{};
        for (auto index = std::size_t{}; index < startEvents.size(); ++index)
        {
            assert(pool->popSignalEvent(event));
            std::copy(std::begin(event.nodeId), std::end(event.nodeId), startEvents[index].begin());
            assert(event.time == 10 && event.instanceId == instanceId);
        }
        assert((startEvents == std::array<std::array<std::uint8_t, 16>, 1>{
            fixtureNode(207)}));
        auto output = std::array<double, 4>{};
        auto width = std::uint8_t{};
        auto startAdd = fixtureNode(219);
        auto updateAdd = fixtureNode(220);
        assert(pool->value(instanceId, startAdd.data(), output.data(), width) == Result::Ok &&
            width == 1 && output[0] == 1);
        assert(pool->value(instanceId, updateAdd.data(), output.data(), width) == Result::Ok &&
            width == 1 && output[0] == 5);

        auto liveThreshold = GeneratorCapture{};
        auto thresholdNode = fixtureNode(210);
        std::copy(thresholdNode.begin(), thresholdNode.end(), liveThreshold.nodeId);
        liveThreshold.value = 2;
        assert(pool->setLive(instanceId, &liveThreshold, 1) == Result::Ok);
        assert(pool->evaluate(instanceId, 13) == Result::Ok);
        auto updateEvents = std::array<std::array<std::uint8_t, 16>, 1>{};
        for (auto index = std::size_t{}; index < updateEvents.size(); ++index)
        {
            assert(pool->popSignalEvent(event));
            std::copy(std::begin(event.nodeId), std::end(event.nodeId), updateEvents[index].begin());
            assert(event.time == 13 && event.instanceId == instanceId);
        }
        assert((updateEvents == std::array<std::array<std::uint8_t, 16>, 1>{
            fixtureNode(215)}));
        assert(pool->value(instanceId, updateAdd.data(), output.data(), width) == Result::Ok &&
            width == 1 && output[0] == 6);
        auto age = 0.0;
        assert(pool->elapsed(instanceId, age) == Result::Ok && age == 3);
    }
}

void verifyLogicalPackage(const std::filesystem::path& path)
{
    auto bytes = loadFile(path);
    auto package = SpawnablePackage{};
    assert(loadSpawnablePackage(bytes.data(), bytes.size(), package) == Result::Ok);
    auto host = std::unique_ptr<SpawnableHost>{};
    assert(SpawnableHost::create(std::move(package), {}, host) == Result::Ok);
    auto positionId = fixtureNode(236);
    auto previous = 0.0;
    auto count = 0.0;
    for (auto time : {0.0, 0.5, 2.0})
    {
        assert(host->advance(time, time - previous) == Result::Ok);
        auto position = std::array<double, 4>{};
        auto width = std::uint8_t{};
        assert(host->rootValue(positionId.data(), position.data(), width) == Result::Ok);
        ++count;
        assert(width == 3 && position[0] == count && position[1] == count && position[2] == count);
        assert(!host->rootFinished());
        previous = time;
    }
}

}

int main(int argc, char** argv)
{
#if defined(_MSC_VER) && defined(_DEBUG)
    _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE);
    _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif
    verifyCompareModes();
    verifyAndAndOr();
    verifySignalToNumber();
    if (argc > 1)
    {
        auto directory = std::filesystem::path(argv[1]);
        verifyCompiledFixture(directory);
        verifyGroupIsolationFixture(directory);
        verifyGuardedLogicalFixture(directory);
        verifyMatrixLateRefreshFixture(directory);
        verifyLateRefreshRejectsShadowedBuiltin(directory);
        verifyHostIntegrationFixture(directory);
    }
    if (argc > 2) verifyLogicalPackage(argv[2]);
}
