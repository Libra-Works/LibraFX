#ifdef NDEBUG
#undef NDEBUG
#endif
#include "liberafx/ExecutionDebugger.h"
#include "liberafx/host/SpawnableDebugJob.h"
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <vector>

namespace {
using namespace liberafx;
struct Queue {
    Job job = nullptr;
    void* context = nullptr;
    static bool enqueue(void* queue, Job job, void* context) noexcept
    {
        auto& self = *static_cast<Queue*>(queue);
        self.job = job;
        self.context = context;
        return true;
    }
};

auto fixture(const char* root, const char* name) -> std::vector<std::uint8_t>
{
    auto file = std::ifstream(std::filesystem::path(root) / name, std::ios::binary);
    assert(file.good());
    return {std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
}

auto find(const std::vector<std::uint8_t>& bytes, std::uint8_t opcode) -> const std::uint8_t*
{
    auto scalar = bytes[4] == 1;
    auto stride = scalar ? std::size_t{44} : std::size_t{256};
    auto header = scalar ? std::size_t{12} : std::size_t{16};
    auto count = std::uint32_t{};
    for (auto index = 0u; index < 4; ++index) count |= std::uint32_t{bytes[8 + index]} << (index * 8);
    for (auto index = 0u; index < count; ++index)
        if (bytes[header + stride * index + 16] == opcode) return bytes.data() + header + stride * index;
    assert(false);
    return nullptr;
}

void verify(const std::vector<std::uint8_t>& bytes, bool integer)
{
    auto program = Program{};
    assert(Program::load(bytes.data(), bytes.size(), program) == Result::Ok);
    auto instance = Instance{};
    alignas(16) double work[4096]{}, defaults[4096]{};
    assert(instance.initialize(program, work, 4096, defaults, 4096) == Result::Ok);
    auto add = find(bytes, integer ? 49 : 3);
    auto addId = ExecutionDebugger::NodeId{};
    std::memcpy(addId.data(), add, 16);
    auto breakpoints = std::array<ExecutionDebugger::NodeId, 1>{};
    auto debugger = ExecutionDebugger(breakpoints.data(), breakpoints.size());
    assert(debugger.breakpoint(addId, true) == Result::Ok && debugger.attach() == Result::Ok);
    assert(instance.setDebugHooks(debugger.hooks()) == Result::Ok);
    auto parameter = ParameterHandle{};
    auto multiply = integer ? nullptr : find(bytes, 5);
    if (multiply)
    {
        assert(instance.bindParameter(multiply, 1, 0, parameter) == Result::Ok);
        assert(instance.setParameter(parameter, 6) == Result::Ok);
    }
    auto queue = Queue{};
    assert(instance.updateAsync(1.25, Queue::enqueue, &queue) == Result::Ok);
    auto worker = std::thread([&] { queue.job(queue.context); debugger.complete(); });
    assert(debugger.waitForPause(std::chrono::seconds(3)));
    assert(instance.result() == Result::NotReady && instance.setDebugHooks({}) == Result::Busy);
    assert(instance.initialize(program, work, 4096) == Result::Busy);
    auto pause = DebugPauseSnapshot{};
    assert(debugger.snapshot(pause) == Result::Ok && pause.nodeId == addId &&
        pause.inputCount == 2 && pause.time == 1.25 &&
        pause.inputs[0].connected && pause.inputs[1].connected &&
        pause.inputs[0].components[0] == (integer ? 14 : 3) &&
        pause.inputs[1].components[0] == (integer ? 3 : 4) &&
        pause.inputs[0].kind == (integer ? DebugValueKind::Int32 : DebugValueKind::Numeric));
    if (multiply)
    {
        assert(instance.setParameter(parameter, 1) == Result::Busy);
        assert(debugger.resume(true) == Result::Ok && debugger.waitForPause(std::chrono::seconds(3)));
        assert(debugger.snapshot(pause) == Result::Ok && pause.sequence == 2 &&
            std::memcmp(pause.nodeId.data(), multiply, 16) == 0 &&
            pause.inputs[0].components[0] == 7 && pause.inputs[0].connected &&
            pause.inputs[1].components[0] == 6 && !pause.inputs[1].connected);
    }
    assert(debugger.resume(false) == Result::Ok);
    worker.join();
    assert(instance.result() == Result::Ok && debugger.close() == Result::Ok);
    auto handle = ValueHandle{};
    assert(instance.bindValue(multiply ? multiply : add, handle) == Result::Ok);
    auto result = std::array<double, 4>{};
    assert(instance.value(handle, result.data()) == Result::Ok && result[0] == (integer ? 17 : 42));
    assert(instance.setDebugHooks({}) == Result::Ok);

    assert(debugger.attach() == Result::Ok && instance.setDebugHooks(debugger.hooks()) == Result::Ok);
    assert(instance.updateAsync(2, Queue::enqueue, &queue) == Result::Ok);
    worker = std::thread([&] { queue.job(queue.context); debugger.complete(); });
    assert(debugger.waitForPause(std::chrono::seconds(3)) && debugger.close() == Result::Busy);
    worker.join();
    assert(instance.result() == Result::NotReady && instance.setDebugHooks({}) == Result::Ok);
    assert(debugger.close() == Result::Ok);
}

void verifyGuarded(const std::vector<std::uint8_t>& bytes)
{
    auto program = Program{};
    auto bindings = std::array<CustomNodeBinding, 64>{};
    assert(Program::load(bytes.data(), bytes.size(), nullptr, 0, bindings.data(), bindings.size(), program) == Result::Ok);
    auto instance = Instance{};
    alignas(64) double work[4096]{}, defaults[4096]{};
    assert(instance.initialize(program, work, 4096, defaults, 4096) == Result::Ok);
    auto inactive = find(bytes, 6);
    auto select = find(bytes, 44);
    auto breakpoints = std::array<ExecutionDebugger::NodeId, 2>{};
    auto debugger = ExecutionDebugger(breakpoints.data(), breakpoints.size());
    for (auto id : {inactive, select})
    {
        auto nodeId = ExecutionDebugger::NodeId{};
        std::memcpy(nodeId.data(), id, 16);
        assert(debugger.breakpoint(nodeId, true) == Result::Ok);
    }
    assert(debugger.attach() == Result::Ok && instance.setDebugHooks(debugger.hooks()) == Result::Ok);
    auto queue = Queue{};
    assert(instance.updateAsync(0.25, Queue::enqueue, &queue) == Result::Ok);
    auto worker = std::thread([&] { queue.job(queue.context); debugger.complete(); });
    assert(debugger.waitForPause(std::chrono::seconds(3)));
    auto stopped = DebugPauseSnapshot{};
    assert(debugger.snapshot(stopped) == Result::Ok && std::memcmp(stopped.nodeId.data(), select, 16) == 0);
    assert(stopped.inputCount == 2 && stopped.inputs[0].kind == DebugValueKind::Int32 &&
        stopped.inputs[1].connected && stopped.inputs[1].components[0] == 9);
    debugger.detach();
    worker.join();
    assert(instance.result() == Result::Ok && instance.setDebugHooks({}) == Result::Ok);
    auto result = double{};
    assert(instance.value(select, result) == Result::Ok && result == 9);
}

void verifyAuthoredPackageVisits(const char* quality, const char* phase)
{
    auto run = [](const std::vector<std::uint8_t>& bytes, const ExecutionDebugHooks& hooks) {
        auto package = SpawnablePackage{};
        assert(loadSpawnablePackage(bytes.data(), bytes.size(), package) == Result::Ok);
        auto host = std::unique_ptr<SpawnableHost>{};
        assert(SpawnableHost::create(std::move(package), {}, host) == Result::Ok);
        assert(host->setHostQuality({HostQualityAvailability::Known, HostQualityTier::Ultra,
            HostQualitySource::HostPolicy, 1}) == Result::Ok);
        auto target = SpawnableDebugTarget{};
        assert(host->debugTarget({}, target) == Result::Ok);
        assert(host->setDebugHooks(target, hooks) == Result::Ok);
        auto job = liberafx::host::SpawnableDebugJob(*host);
        auto queue = Queue{};
        for (auto time : {0.0, 0.25, 1.0}) {
            auto request = liberafx::host::SpawnableDebugJob::Request{};
            request.time = time;
            request.frameDelta = time == 0 ? 0 : .25;
            assert(job.submit(request, {Queue::enqueue, &queue}) == Result::Ok);
            assert(job.submit(request, {Queue::enqueue, &queue}) == Result::Busy);
            auto worker = std::thread([&] { queue.job(queue.context); });
            worker.join();
            auto result = Result::NotReady;
            assert(job.poll(result) == Result::Ok && result == Result::Ok);
        }
        assert(host->setDebugHooks(target, {}) == Result::Ok);
    };
    if (quality) {
        struct Observer final {
            const std::uint8_t* ids;
            int captures = 0, active = 0, inactive = 0;
            static Result visit(void* context, const DebugNodeVisit& visit) noexcept
            {
                auto& out = *static_cast<Observer*>(context);
                if (std::memcmp(visit.nodeId, out.ids + 16, 16) == 0 && visit.signalSourceNodeId) {
                    assert(visit.inputCount == 1 && visit.inputs[0].width == 4);
                    assert(visit.inputs[0].components[0] == out.captures + 1);
                    ++out.captures;
                }
                if (std::memcmp(visit.nodeId, out.ids + 48, 16) == 0) {
                    assert(visit.signalSourceNodeId && std::memcmp(visit.signalSourceNodeId, out.ids + 32, 16) == 0);
                    ++out.active;
                }
                if (std::memcmp(visit.nodeId, out.ids + 64, 16) == 0) ++out.inactive;
                return Result::Ok;
            }
        };
        auto ids = fixture(quality, "AuthoredQuality.ids");
        auto observed = Observer{ids.data()};
        run(fixture(quality, "AuthoredQuality.lfxp"), {&observed, Observer::visit});
        assert(observed.captures == 2 && observed.active == 2 && observed.inactive == 0);
    }
    if (phase) {
        struct PhaseObserver { const std::uint8_t* phaseId; int count = 0; };
        const auto instructions = fixture(phase, "PhasePreset.lfxb");
        auto observed = PhaseObserver{find(instructions, 55)};
        run(fixture(phase, "PhasePreset.lfxp"), {&observed, [](void* data, const DebugNodeVisit& visit) noexcept {
            auto& observed = *static_cast<PhaseObserver*>(data);
            if (std::memcmp(visit.nodeId, observed.phaseId, 16) == 0) ++observed.count;
            return Result::Ok;
        }});
        assert(observed.count > 0);
    }
}
}

int main()
{
    auto scalar = std::getenv("LIBERAFX_DEBUG_FIXTURE_DIR");
    auto integer = std::getenv("LIBERAFX_INT32_FIXTURE_DIR");
    if (!scalar || !integer)
    {
        std::cout << "Compiler-produced debugger/Int32 fixture directories are required.\n";
        return 77;
    }
    verify(fixture(scalar, "DebugScalar.lfxb"), false);
    verify(fixture(integer, "Int32Add.lfxb"), true);
    auto guarded = std::getenv("LIBERAFX_DEBUG_BRANCH_FIXTURE_DIR");
    if (guarded) verifyGuarded(fixture(guarded, "enum-branch-divide.lfxb"));
    verifyAuthoredPackageVisits(std::getenv("LIBERAFX_DEBUG_QUALITY_FIXTURE_DIR"),
        std::getenv("LIBERAFX_DEBUG_PHASE_FIXTURE_DIR"));
}
