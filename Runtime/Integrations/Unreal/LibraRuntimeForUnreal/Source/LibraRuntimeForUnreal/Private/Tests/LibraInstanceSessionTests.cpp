#if WITH_DEV_AUTOMATION_TESTS
#include "LibraInstanceSession.h"
#include "Async/Async.h"
#include "Misc/AutomationTest.h"
#include <cstring>

namespace {
auto sessionScalar(double value, uint8 opcode = 1) -> TArray<uint8>
{
    auto bytes = TArray<uint8>{};
    bytes.SetNumZeroed(56);
    FMemory::Memcpy(bytes.GetData(), "LFXB", 4);
    bytes[4] = bytes[6] = bytes[8] = 1;
    bytes[27] = 1; bytes[28] = opcode;
    for (auto index = 32; index < 40; ++index) bytes[index] = 0xff;
    auto bits = uint64{};
    std::memcpy(&bits, &value, sizeof(value));
    for (auto index = 0; index < 8; ++index) bytes[40 + index] = static_cast<uint8>(bits >> (index * 8));
    return bytes;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraInstanceSessionRequestTest, "Libra.Runtime.Session.WorkerRequestsAndRestart",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraInstanceSessionRequestTest::RunTest(const FString&)
{
    using liberafx::Result;
    TestTrue(TEXT("Automation owner is the game thread"), IsInGameThread());
    auto result = Result::Ok;
    TestFalse(TEXT("Development opt-in is required"), !!FLibraInstanceSession::Create(false, result));
    auto session = FLibraInstanceSession::Create(true, result, 2, 2);
    if (!session || result != Result::Ok) return false;
    auto bytes = sessionScalar(3);
    auto lifetime = liberafx::Lifetime::infinite();
    TestEqual(TEXT("Finite policy"), liberafx::Lifetime::finite(2, lifetime), Result::Ok);
    auto request = liberafx::RequestId{};
    auto wrongThread = Result::Ok;
    // The game thread waits only for submission; no game-thread application
    // is required to complete the worker task.
    auto submission = Async(EAsyncExecution::Thread, [&]() {
        auto processed = uint32{};
        wrongThread = session->Process(1, processed);
        return session->SubmitCreate(MakeArrayView(bytes), request, lifetime);
    });
    TestEqual(TEXT("Worker submission accepted"), submission.Get(), Result::Ok);
    TestEqual(TEXT("Owner processing requires game thread"), wrongThread, Result::InvalidArgument);
    bytes = sessionScalar(100); // Must not affect the native copied bytecode.
    auto completion = FLibraInstanceSession::FCompletion{};
    TestEqual(TEXT("Creation is not usable before processing"),
        session->TakeCompletion(request, completion), Result::NotReady);
    auto processed = uint32{};
    TestEqual(TEXT("Game thread processes create"), session->Process(1, processed), Result::Ok);
    auto consumption = Async(EAsyncExecution::Thread, [&]() { return session->TakeCompletion(request, completion); });
    TestEqual(TEXT("Worker consumes completion"), consumption.Get(), Result::Ok);
    TestEqual(TEXT("Native instance created"), completion.result, Result::Ok);
    auto id = completion.instance;
    auto values = std::array<double, 4>{};
    auto width = uint8{};
    auto nodeId = std::array<uint8, 16>{};
    nodeId[15] = 1;
    TestEqual(TEXT("Actual native value is readable"),
        session->ReadValue(id, nodeId.data(), values, width), Result::Ok);
    TestEqual(TEXT("Request owns a copy"), values[0], 3.0);
    auto finished = false;
    TestEqual(TEXT("Advance prior graph"), session->Advance(id, 1, 1, finished), Result::Ok);
    auto complete = [&]() {
        TestEqual(TEXT("Owner applies request"), session->Process(1, processed), Result::Ok);
        TestEqual(TEXT("Consume applied result"), session->TakeCompletion(request, completion), Result::Ok);
        return completion.result;
    };
    auto next = sessionScalar(9);
    TestEqual(TEXT("Submit state preservation"), session->SubmitReplace(id, MakeArrayView(next),
        liberafx::ReplacementPolicy::PreserveState, request, lifetime), Result::Ok);
    TestEqual(TEXT("State migration explicitly unsupported"), complete(), Result::UnsupportedBinary);
    auto failed = sessionScalar(9, 6);
    TestEqual(TEXT("Submit failing candidate"), session->SubmitReplace(id, MakeArrayView(failed),
        liberafx::ReplacementPolicy::Restart, request, lifetime), Result::Ok);
    TestEqual(TEXT("Candidate initial evaluation fails"), complete(), Result::DivideByZero);
    TestEqual(TEXT("Old graph remains readable"),
        session->ReadValue(id, nodeId.data(), values, width), Result::Ok);
    TestEqual(TEXT("Old value survives failure"), values[0], 3.0);
    TestEqual(TEXT("Old age survives failure"), session->Advance(id, 0.5, 0.5, finished), Result::InvalidArgument);
    TestEqual(TEXT("Submit graph restart"), session->SubmitReplace(id, MakeArrayView(next),
        liberafx::ReplacementPolicy::Restart, request, lifetime), Result::Ok);
    TestEqual(TEXT("Graph restart commits"), complete(), Result::Ok);
    TestEqual(TEXT("Read replacement graph"), session->ReadValue(id, nodeId.data(), values, width), Result::Ok);
    TestEqual(TEXT("Replacement graph changes actual native output"), values[0], 9.0);
    TestEqual(TEXT("Reset local elapsed clock after restart"), session->Advance(id, 1, 1, finished), Result::Ok);
    TestFalse(TEXT("New lifetime not yet finished"), finished);
    TestEqual(TEXT("Reach natural deadline"), session->Advance(id, 2, 1, finished), Result::Ok);
    TestTrue(TEXT("Natural deadline reported"), finished);
    TestEqual(TEXT("Natural finish retains native state"),
        session->ReadValue(id, nodeId.data(), values, width), Result::Ok);
    TestEqual(TEXT("Shutdown never implicitly force-stops"), session->CloseAndRelease(), Result::NotReady);
    auto destruction = Async(EAsyncExecution::Thread, [&]() { return session->SubmitDestroy(id, request); });
    TestEqual(TEXT("Worker requests explicit destruction"), destruction.Get(), Result::Ok);
    TestEqual(TEXT("Explicit destruction completes"), complete(), Result::Ok);
    TestEqual(TEXT("Destroyed ID rejected"),
        session->ReadValue(id, nodeId.data(), values, width), Result::InvalidHandle);
    TestEqual(TEXT("Release after consuming ownership completions"), session->CloseAndRelease(), Result::Ok);
    return true;
}
#endif
