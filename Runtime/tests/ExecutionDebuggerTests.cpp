#include "liberafx/LbExecutionDebugger.h"
#include "liberafx/host/LbExecutionDebugProtocol.h"
#include <atomic>
#include <cassert>

int main()
{
    using namespace liberafx;
    auto storage = std::array<ExecutionDebugger::NodeId, 2>{};
    auto debugger = ExecutionDebugger(storage.data(), storage.size());
    auto first = ExecutionDebugger::NodeId{{1}};
    auto second = ExecutionDebugger::NodeId{{2}};
    assert(debugger.breakpoint(first, true) == Result::Ok);
    assert(debugger.attach() == Result::Ok);
    auto input = DebugInputValue{};
    input.width = 1;
    input.kind = DebugValueKind::Numeric;
    input.components[0] = 123;
    auto visit = DebugNodeVisit{first.data(), &input, 1, 0.5};
    assert(debugger.hooks().beforeNode(&debugger, visit) == Result::InvalidArgument);
    auto executed = std::atomic<unsigned>{0};
    auto worker = std::thread([&] {
        assert(debugger.hooks().beforeNode(&debugger, visit) == Result::Ok);
        ++executed;
        visit.nodeId = second.data();
        input.components[0] = 456;
        assert(debugger.hooks().beforeNode(&debugger, visit) == Result::Ok);
        ++executed;
        debugger.complete();
    });
    assert(debugger.waitForPause(std::chrono::seconds(2)) && executed == 0);
    auto snapshot = DebugPauseSnapshot{};
    assert(debugger.snapshot(snapshot) == Result::Ok && snapshot.nodeId == first &&
        snapshot.inputs[0].components[0] == 123 && snapshot.time == 0.5);
    assert(debugger.resume(true) == Result::Ok);
    assert(debugger.waitForPause(std::chrono::seconds(2)) && executed == 1);
    assert(debugger.snapshot(snapshot) == Result::Ok && snapshot.nodeId == second &&
        snapshot.inputs[0].components[0] == 456 && snapshot.sequence == 2);
    assert(debugger.resume(false) == Result::Ok);
    worker.join();
    assert(executed == 2 && debugger.state() == DebugExecutionState::Complete);
    assert(debugger.snapshot(snapshot) == Result::NotReady);
    assert(debugger.close() == Result::Ok);

    assert(debugger.attach() == Result::Ok);
    auto result = Result::Ok;
    visit.nodeId = first.data();
    worker = std::thread([&] { result = debugger.hooks().beforeNode(&debugger, visit); });
    assert(debugger.waitForPause(std::chrono::seconds(2)));
    assert(debugger.close() == Result::Busy);
    worker.join();
    assert(result == Result::NotReady && debugger.close() == Result::Ok);

    // After a lost peer the real worker gets to finish the suspended node,
    // with neither a replay nor an aborted partially executed update.
    auto protocol = host::ExecutionDebugProtocol(0xd0aa6218672cf5abULL);
    auto command = host::ExecutionDebugProtocol::Command{};
    auto frame = host::ExecutionDebugProtocol::Frame{};
    std::memcpy(command.data(), "LFD2", 4);
    assert(protocol.exchange(command, frame) && frame[5] == static_cast<unsigned>(Result::InvalidBinary));
    for (auto i = 0u; i < 8; ++i) command[8 + i] = static_cast<std::uint8_t>(0xd0aa6218672cf5abULL >> (i * 8));
    command[4] = 2;
    std::memcpy(command.data() + 16, first.data(), 16);
    assert(protocol.exchange(command, frame) && frame[5] == 0);
    command[4] = 1;
    assert(protocol.exchange(command, frame) && frame[5] == 0);
    auto& controller = protocol.debugger();
    visit.signalSourceNodeId = second.data();
    input.connected = true;
    std::memcpy(input.sourceNodeId, second.data(), 16);
    worker = std::thread([&] { result = controller.hooks().beforeNode(&controller, visit); });
    assert(controller.waitForPause(std::chrono::seconds(2)));
    command[4] = 0;
    assert(protocol.exchange(command, frame) && frame[4] == 2 && frame[6] == 1 && frame[8] == 1 &&
        frame[16] == 1 && frame[40] == 2 && frame[57] == 1 && frame[59] == 1 && frame[60] == 2);
    controller.detach();
    worker.join();
    assert(result == Result::Ok && controller.state() == DebugExecutionState::Detached);
}
