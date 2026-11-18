#if WITH_DEV_AUTOMATION_TESTS

#include "LibraEffectAsset.h"
#include "LibraEffectComponent.h"
#include "LibraNativeState.h"
#include "liberafx/GeneratorPool.h"
#include "Engine/World.h"
#include "HAL/PlatformProcess.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/ScopeExit.h"
#include <array>

namespace {
auto ScalarBytecode(double value) -> TArray<uint8>
{
    TArray<uint8> bytes;
    bytes.SetNumZeroed(56);
    FMemory::Memcpy(bytes.GetData(), "LFXB", 4);
    bytes[4] = 1;
    bytes[6] = 1;
    bytes[8] = 1;
    bytes[27] = 1;
    bytes[28] = 1;
    for (auto index = 32; index < 40; ++index) bytes[index] = 255;
    auto bits = uint64{};
    FMemory::Memcpy(&bits, &value, sizeof(value));
    for (auto index = 0; index < 8; ++index) bytes[40 + index] = static_cast<uint8>(bits >> (index * 8));
    return bytes;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraAssetBytecodeTest,
    "Libra.Runtime.Assets.ImmutableBytecode", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraAssetBytecodeTest::RunTest(const FString& parameters)
{
    auto bytes = ScalarBytecode(3);
    auto asset = NewObject<ULibraEffectAsset>();
    TestEqual(TEXT("Valid bytecode imports"), asset->SetBytecode(bytes), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Node count"), asset->GetNodeCount(), 1);
    auto original = TArray<uint8>(asset->GetBytecode());
    bytes[40] ^= 1;
    TestTrue(TEXT("Input buffers are not borrowed"), asset->GetBytecode()[40] == original[40]);
    bytes[0] = 0;
    TestEqual(TEXT("Malformed replacement is rejected"), asset->SetBytecode(bytes), ELibraRuntimeResult::InvalidBinary);
    TestTrue(TEXT("Failed replacement preserves the previous asset"),
        FMemory::Memcmp(asset->GetBytecode().GetData(), original.GetData(), original.Num()) == 0);
    TestEqual(TEXT("Previous asset stays valid"), asset->ValidateBytecode(), ELibraRuntimeResult::Ok);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGroupSignalFixtureTest,
    "Libra.Runtime.Binding.GroupSignalFixture",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGroupSignalFixtureTest::RunTest(const FString&)
{
    auto directory = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraGroupSignalFixture="), directory) || directory.IsEmpty())
    {
        AddInfo(TEXT("GroupSignalFixture requires an explicit isolated fixture directory."));
        return true;
    }
    auto bytes = TArray<uint8>{};
    auto entryText = FString{};
    if (!TestTrue(TEXT("Fixture bytecode and entry ID load"),
        FFileHelper::LoadFileToArray(bytes, *(directory / TEXT("signal-program.lfxb"))) &&
        FFileHelper::LoadFileToString(entryText, *(directory / TEXT("signal-entry.txt"))))) return false;
    auto entry = FGuid{};
    if (!TestTrue(TEXT("Fixture entry ID is valid"), FGuid::Parse(entryText, entry)) ||
        !TestTrue(TEXT("Fixture includes v4 FCurve bytecode"), bytes.Num() >= 16 && bytes[4] == 4)) return false;
    auto asset = NewObject<ULibraEffectAsset>();
    if (!TestEqual(TEXT("Unreal effect asset accepts Group Signal with FCurve"),
        asset->SetBytecode(bytes), ELibraRuntimeResult::Ok)) return false;
    auto component = NewObject<ULibraEffectComponent>();
    auto handle = FLibraSignalHandle{};
    TestEqual(TEXT("Unreal effect component accepts the compiled asset"),
        component->SetEffect(asset), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Unreal effect component binds the authored Signal"),
        component->BindSignal(entry, handle), ELibraRuntimeResult::Ok);
    component->Stop();
    auto compiled = FCompiledLibraProgram(bytes);
    if (!TestTrue(TEXT("Unreal registry resolves the builtin Signal nodes"), compiled.result == liberafx::Result::Ok))
        return false;
    auto workBytes = compiled.program.workBytes();
    auto work = static_cast<double*>(FMemory::Malloc(workBytes, compiled.program.workAlignment()));
    if (!TestNotNull(TEXT("Caller-owned work memory"), work)) return false;
    auto instance = liberafx::Instance{};
    auto initialized = instance.initialize(compiled.program, work, workBytes / sizeof(double));
    if (TestTrue(TEXT("Fixture instance initializes"), initialized == liberafx::Result::Ok))
    {
        auto enqueue = [](void*, liberafx::Job job, void* context) noexcept -> bool { job(context); return true; };
        auto evaluated = instance.updateAsync(0, enqueue, nullptr);
        TestTrue(TEXT("Fixture evaluates"), evaluated == liberafx::Result::Ok && instance.result() == liberafx::Result::Ok);
        uint8 entryBytes[16]{};
        LibraNodeIdBytes(entry, entryBytes);
        TestTrue(TEXT("Three instructions cannot traverse all four Signal nodes"),
            instance.dispatchSignal(entryBytes, 0, 3) == liberafx::Result::InstructionBudgetExceeded);
        TestTrue(TEXT("Four instructions traverse the explicit Group Signal chain"),
            instance.dispatchSignal(entryBytes, 0, 4) == liberafx::Result::Ok);
    }
    FMemory::Free(work);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraSignalFanoutFixtureTest,
    "Libra.Runtime.Binding.SignalFanoutFixture",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraSignalFanoutFixtureTest::RunTest(const FString&)
{
    auto directory = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraGroupSignalFixture="), directory) || directory.IsEmpty())
    {
        AddInfo(TEXT("SignalFanoutFixture requires an explicit isolated fixture directory."));
        return true;
    }
    auto bytes = TArray<uint8>{};
    auto entryText = FString{};
    auto entry = FGuid{};
    if (!TestTrue(TEXT("Compiler-generated v5 fan-out fixture loads"),
        FFileHelper::LoadFileToArray(bytes, *(directory / TEXT("signal-fanout.lfxb"))) &&
        FFileHelper::LoadFileToString(entryText, *(directory / TEXT("signal-entry.txt"))) &&
        FGuid::Parse(entryText, entry) && bytes.Num() >= 16 && bytes[4] == 5)) return false;
    auto asset = NewObject<ULibraEffectAsset>();
    if (!TestEqual(TEXT("Effect asset accepts v5 fan-out"), asset->SetBytecode(bytes), ELibraRuntimeResult::Ok))
        return false;
    auto component = NewObject<ULibraEffectComponent>();
    auto handle = FLibraSignalHandle{};
    TestEqual(TEXT("Effect component initializes v5 fan-out"), component->SetEffect(asset), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Effect component binds fan-out entry"), component->BindSignal(entry, handle), ELibraRuntimeResult::Ok);
    component->Stop();
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, FName(TEXT("LibraSignalFanoutWorld")),
        nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated game world"), world)) return false;
    auto actor = world->SpawnActor<AActor>();
    if (!TestNotNull(TEXT("Signal test actor"), actor))
    {
        world->DestroyWorld(false);
        return false;
    }
    auto live = NewObject<ULibraEffectComponent>(actor);
    live->bAutoPlay = false;
    actor->AddInstanceComponent(live);
    live->RegisterComponent();
    auto liveHandle = FLibraSignalHandle{};
    TestEqual(TEXT("Registered component loads fan-out"), live->SetEffect(asset), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Registered component binds entry"), live->BindSignal(entry, liveHandle), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Component schedules evaluation"), live->EvaluateAtTime(0), ELibraRuntimeResult::Ok);
    auto deadline = FPlatformTime::Seconds() + 5.0;
    while (live->IsEvaluationPending() && FPlatformTime::Seconds() < deadline)
    {
        live->TickComponent(0, LEVELTICK_All, nullptr);
        FPlatformProcess::Sleep(0.001f);
    }
    if (TestFalse(TEXT("Evaluation completes within timeout"), live->IsEvaluationPending()))
    {
        TestEqual(TEXT("Component evaluation succeeds"), live->LastResult, ELibraRuntimeResult::Ok);
        TestEqual(TEXT("Component shares branch budget"), live->DispatchSignal(liveHandle, 4),
            ELibraRuntimeResult::InstructionBudgetExceeded);
        TestEqual(TEXT("Component dispatches both branches"), live->DispatchSignal(liveHandle, 5),
            ELibraRuntimeResult::Ok);
    }
    live->Stop();
    world->DestroyWorld(false);
    auto compiled = FCompiledLibraProgram(bytes);
    if (!TestTrue(TEXT("Unreal registry resolves fan-out"), compiled.result == liberafx::Result::Ok)) return false;
    auto workBytes = compiled.program.workBytes();
    auto work = static_cast<double*>(FMemory::Malloc(workBytes, compiled.program.workAlignment()));
    if (!TestNotNull(TEXT("Caller-owned work memory"), work)) return false;
    {
        auto instance = liberafx::Instance{};
        if (TestTrue(TEXT("Fan-out instance initializes"),
            instance.initialize(compiled.program, work, workBytes / sizeof(double)) == liberafx::Result::Ok))
        {
            auto enqueue = [](void*, liberafx::Job job, void* context) noexcept -> bool { job(context); return true; };
            auto evaluated = instance.updateAsync(0, enqueue, nullptr);
            TestTrue(TEXT("Fan-out fixture evaluates"), evaluated == liberafx::Result::Ok &&
                instance.result() == liberafx::Result::Ok);
            uint8 entryBytes[16]{};
            LibraNodeIdBytes(entry, entryBytes);
            TestTrue(TEXT("Budget is shared by both branches"),
                instance.dispatchSignal(entryBytes, 0, 4) == liberafx::Result::InstructionBudgetExceeded);
            TestTrue(TEXT("Both branches execute through Unreal's runtime"),
                instance.dispatchSignal(entryBytes, 0, 5) == liberafx::Result::Ok);
        }
    }
    FMemory::Free(work);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraNodeIdentityTest,
    "Libra.Runtime.Binding.NodeIdentity", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraNodeIdentityTest::RunTest(const FString& parameters)
{
    uint8 bytes[16]{};
    LibraNodeIdBytes(FGuid(0x00112233, 0x44556677, 0x8899AABB, 0xCCDDEEFF), bytes);
    for (auto index = 0; index < 16; ++index)
        TestEqual(TEXT("Node IDs match compiler network-byte-order GUIDs"), bytes[index], static_cast<uint8>(index * 0x11));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraClockFixtureTest,
    "Libra.Runtime.Binding.ClockFixture", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraClockFixtureTest::RunTest(const FString&)
{
    auto directory = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraClockFixture="), directory) || directory.IsEmpty())
    {
        AddInfo(TEXT("ClockFixture requires an explicit isolated fixture directory."));
        return true;
    }
    auto bytes = TArray<uint8>{};
    auto ids = TArray<FString>{};
    if (!TestTrue(TEXT("Compiler-generated clock fixture loads"),
        FFileHelper::LoadFileToArray(bytes, *(directory / TEXT("clock-fixture.lfxb"))) &&
        FFileHelper::LoadFileToStringArray(ids, *(directory / TEXT("clock-fixture.lfxb.ids"))) && ids.Num() >= 3))
    {
        return false;
    }
    auto effectAge = FGuid{};
    auto delta = FGuid{};
    auto instanceAge = FGuid{};
    if (!TestTrue(TEXT("Clock node IDs are valid"), FGuid::Parse(ids[0], effectAge) &&
        FGuid::Parse(ids[1], delta) && FGuid::Parse(ids[2], instanceAge)))
    {
        return false;
    }
    auto asset = NewObject<ULibraEffectAsset>();
    if (!TestEqual(TEXT("Clock bytecode imports"), asset->SetBytecode(bytes), ELibraRuntimeResult::Ok))
    {
        return false;
    }
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, FName(TEXT("LibraClockWorld")),
        nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated game world"), world))
    {
        return false;
    }
    auto actor = world->SpawnActor<AActor>();
    if (!TestNotNull(TEXT("Clock test actor"), actor))
    {
        world->DestroyWorld(false);
        return false;
    }
    auto component = NewObject<ULibraEffectComponent>(actor);
    component->bAutoPlay = false;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();
    TestEqual(TEXT("Component loads clock fixture"), component->SetEffect(asset), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Manual evaluation starts"), component->EvaluateAtTime(2), ELibraRuntimeResult::Ok);
    auto deadline = FPlatformTime::Seconds() + 5.0;
    while (component->IsEvaluationPending() && FPlatformTime::Seconds() < deadline)
    {
        component->TickComponent(0, LEVELTICK_All, nullptr);
        FPlatformProcess::Sleep(0.001f);
    }
    if (TestFalse(TEXT("Manual evaluation completes"), component->IsEvaluationPending()))
    {
        auto value = double{};
        TestTrue(TEXT("Effect Age uses evaluated time"), component->GetScalar(effectAge, value) && value == 2);
        TestTrue(TEXT("Root Instance Age uses evaluated time"), component->GetScalar(instanceAge, value) && value == 2);
    }
    component->PlayRate = 0.5;
    TestEqual(TEXT("Playback resumes"), component->Play(false), ELibraRuntimeResult::Ok);
    deadline = FPlatformTime::Seconds() + 5.0;
    auto scaledTicks = true;
    while (component->LastEvaluatedTimeSeconds <= 2 && FPlatformTime::Seconds() < deadline)
    {
        auto previous = component->TimeSeconds;
        component->TickComponent(0.25f, LEVELTICK_All, nullptr);
        scaledTicks = scaledTicks && component->TimeSeconds == previous + 0.125;
        FPlatformProcess::Sleep(0.001f);
    }
    TestTrue(TEXT("Playback rate scales effect time"), scaledTicks);
    component->Pause();
    deadline = FPlatformTime::Seconds() + 5.0;
    while (component->IsEvaluationPending() && FPlatformTime::Seconds() < deadline)
    {
        component->TickComponent(0, LEVELTICK_All, nullptr);
        FPlatformProcess::Sleep(0.001f);
    }
    if (TestFalse(TEXT("Playback evaluation completes"), component->IsEvaluationPending()))
    {
        auto value = double{};
        TestTrue(TEXT("Playback advances local effect time"), component->LastEvaluatedTimeSeconds > 2);
        TestTrue(TEXT("Effect Age matches the completed update"),
            component->GetScalar(effectAge, value) && value == component->LastEvaluatedTimeSeconds);
        TestTrue(TEXT("Delta Time is the unscaled game tick"), component->GetScalar(delta, value) && value == 0.25);
        TestTrue(TEXT("Root Instance Age matches Effect Age"),
            component->GetScalar(instanceAge, value) && value == component->LastEvaluatedTimeSeconds);
    }
    component->Stop();
    world->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraChildDelayFixtureTest,
    "Libra.Runtime.Binding.ChildDelayFixture", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraChildDelayFixtureTest::RunTest(const FString&)
{
    auto directory = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraClockFixture="), directory) || directory.IsEmpty())
    {
        AddInfo(TEXT("ChildDelayFixture requires an explicit isolated fixture directory."));
        return true;
    }
    auto bytes = TArray<uint8>{};
    auto ids = TArray<FString>{};
    if (!TestTrue(TEXT("Authored child Delay fixture loads"),
        FFileHelper::LoadFileToArray(bytes, *(directory / TEXT("child-delay.lfxb"))) &&
        FFileHelper::LoadFileToStringArray(ids, *(directory / TEXT("child-delay.lfxb.ids"))) && ids.Num() == 6))
    {
        return false;
    }
    uint8 nodeIds[6][16]{};
    for (auto index = 0; index < 6; ++index)
    {
        auto id = FGuid{};
        if (!TestTrue(TEXT("Authored node ID is valid"), FGuid::Parse(ids[index], id)))
        {
            return false;
        }
        LibraNodeIdBytes(id, nodeIds[index]);
    }
    auto compiled = FCompiledLibraProgram(bytes);
    if (!TestTrue(TEXT("Unreal registry resolves child Delay"), compiled.result == liberafx::Result::Ok))
    {
        return false;
    }
    auto pool = std::unique_ptr<liberafx::GeneratorPool>{};
    uint8 container[16]{};
    if (!TestTrue(TEXT("Bounded child pool initializes"),
        liberafx::GeneratorPool::create(compiled.program, container, 1, 4 * 1024 * 1024, 8, pool) == liberafx::Result::Ok))
    {
        return false;
    }
    auto lifecycle = liberafx::GeneratorLifecycle{};
    FMemory::Memcpy(lifecycle.onStartNodeId, nodeIds[0], 16);
    FMemory::Memcpy(lifecycle.lifeNodeId, nodeIds[1], 16);
    lifecycle.hasOnStart = true;
    lifecycle.hasLife = true;
    lifecycle.enableDeferredSignals = true;
    lifecycle.instructionBudget = 10000;
    auto capture = liberafx::GeneratorCapture{};
    FMemory::Memcpy(capture.nodeId, nodeIds[5], 16);
    capture.component = 2;
    capture.value = 20;
    auto instance = liberafx::GeneratorInstanceId{};
    if (!TestTrue(TEXT("Lifecycle starts with authored OnStart"),
        pool->configureLifecycle(lifecycle) == liberafx::Result::Ok &&
        pool->spawn(10, &capture, 1, instance) == liberafx::Result::Ok &&
        pool->setTimeContext(instance, 10, 0.125) == liberafx::Result::Ok &&
        pool->start(instance) == liberafx::Result::Ok))
    {
        return false;
    }
    auto event = liberafx::GeneratorEvent{};
    TestTrue(TEXT("Spawn is emitted"), pool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Spawn);
    auto due = double{};
    auto pending = false;
    TestTrue(TEXT("Delay is relative to instance start"), pool->nextDeferredSignal(instance, due, pending) ==
        liberafx::Result::Ok && pending && due == 13);
    auto age = 0.0;
    auto values = std::array<double, 4>{};
    auto width = uint8{};
    TestTrue(TEXT("Lifetime boundary evaluates without destroying reserved work"),
        pool->setTimeContext(instance, 12, 0.125) == liberafx::Result::Ok &&
        pool->evaluate(instance, 12) == liberafx::Result::Ok && !pool->popEvent(event));
    TestTrue(TEXT("Child clock advances while draining"),
        pool->setTimeContext(instance, 12.5, 0.125) == liberafx::Result::Ok &&
        pool->evaluate(instance, 12.5) == liberafx::Result::Ok &&
        pool->value(instance, nodeIds[2], values.data(), width) == liberafx::Result::Ok && values[0] == 2.5);
    TestTrue(TEXT("Game delta remains unscaled"),
        pool->value(instance, nodeIds[3], values.data(), width) == liberafx::Result::Ok && values[0] == 0.125);
    TestTrue(TEXT("First continuation schedules another local Delay"),
        pool->setTimeContext(instance, 13, 0.25) == liberafx::Result::Ok &&
        pool->evaluate(instance, 13) == liberafx::Result::Ok &&
        pool->dispatchDeferredSignal(instance, 13, 10000) == liberafx::Result::Ok &&
        pool->nextDeferredSignal(instance, due, pending) == liberafx::Result::Ok && pending && due == 14 &&
        !pool->popEvent(event));
    TestTrue(TEXT("Final continuation naturally destroys the child exactly once"),
        pool->setTimeContext(instance, 14, 0.5) == liberafx::Result::Ok &&
        pool->evaluate(instance, 14) == liberafx::Result::Ok &&
        pool->dispatchDeferredSignal(instance, 14, 10000) == liberafx::Result::Ok &&
        pool->popEvent(event) && event.kind == liberafx::GeneratorEventKind::Destroy && !pool->popEvent(event));
    TestTrue(TEXT("Destroy timestamp is actual elapsed time"),
        pool->elapsed(instance, age) == liberafx::Result::Ok && age == 4);
    TestTrue(TEXT("Final output is still readable before retirement"),
        pool->value(instance, nodeIds[4], values.data(), width) == liberafx::Result::Ok && width == 3 && values[2] == 500);
    TestTrue(TEXT("Completed child retires"), pool->finalize(instance) == liberafx::Result::Ok);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraBoundParameterTest,
    "Libra.Runtime.Binding.InstanceIsolationAndLifetime",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraBoundParameterTest::RunTest(const FString& parameters)
{
    auto asset = NewObject<ULibraEffectAsset>();
    TestEqual(TEXT("Fixture imports"), asset->SetBytecode(ScalarBytecode(3)), ELibraRuntimeResult::Ok);
    auto first = NewObject<ULibraEffectComponent>();
    auto second = NewObject<ULibraEffectComponent>();
    TestEqual(TEXT("First instance"), first->SetEffect(asset), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Second instance"), second->SetEffect(asset), ELibraRuntimeResult::Ok);
    auto firstHandle = FLibraParameterHandle{};
    auto secondHandle = FLibraParameterHandle{};
    auto nodeId = FGuid(0, 0, 0, 1);
    TestEqual(TEXT("Bind first"), first->BindParameter(nodeId, firstHandle), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Bind second"), second->BindParameter(nodeId, secondHandle), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Override first"), first->SetBoundParameter(firstHandle, 9), ELibraRuntimeResult::Ok);
    auto value = double{};
    TestEqual(TEXT("Read second"), second->ReadBoundParameter(secondHandle, value), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Defaults are not shared"), value, 3.0);
    TestEqual(TEXT("Reject a different component's handle"),
        second->SetBoundParameter(firstHandle, 2), ELibraRuntimeResult::InvalidHandle);
    first->Stop();
    TestEqual(TEXT("Recreate the same program"), first->SetEffect(asset), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Recreated instances reject old handles"),
        first->ReadBoundParameter(firstHandle, value), ELibraRuntimeResult::InvalidHandle);
    first->Stop();
    second->Stop();
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraTransactionalEffectTest,
    "Libra.Runtime.Assets.RejectedProgramPreservesInstance",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraTransactionalEffectTest::RunTest(const FString& parameters)
{
    auto original = NewObject<ULibraEffectAsset>();
    auto replacement = NewObject<ULibraEffectAsset>();
    TestEqual(TEXT("Original imports"), original->SetBytecode(ScalarBytecode(3)), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Replacement imports"), replacement->SetBytecode(ScalarBytecode(5)), ELibraRuntimeResult::Ok);
    auto component = NewObject<ULibraEffectComponent>();
    TestEqual(TEXT("Original instance"), component->SetEffect(original), ELibraRuntimeResult::Ok);
    auto handle = FLibraParameterHandle{};
    TestEqual(TEXT("Bind original"), component->BindParameter(FGuid(0, 0, 0, 1), handle), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Original override"), component->SetBoundParameter(handle, 7), ELibraRuntimeResult::Ok);
    component->MaximumWorkingBytes = 1;
    TestEqual(TEXT("Replacement exceeds budget"), component->SetEffect(replacement), ELibraRuntimeResult::InsufficientWork);
    TestTrue(TEXT("Original asset remains selected"), component->Effect == original);
    auto value = double{};
    TestEqual(TEXT("Original binding remains usable"), component->ReadBoundParameter(handle, value), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Original overrides remain intact"), value, 7.0);
    component->Stop();
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraInt32FixtureTest,
    "Libra.Runtime.Binding.Int32Fixture", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraInt32FixtureTest::RunTest(const FString&)
{
    auto directory = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraInt32Fixture="), directory) || directory.IsEmpty())
    {
        AddInfo(TEXT("Int32Fixture requires an explicit compiler-produced fixture directory."));
        return true;
    }
    auto bytes = TArray<uint8>{};
    if (!TestTrue(TEXT("Int32 fixture loads"),
        FFileHelper::LoadFileToArray(bytes, *(directory / TEXT("Int32Add.lfxb"))))) return false;
    auto asset = NewObject<ULibraEffectAsset>();
    if (!TestEqual(TEXT("Typed v10 bytecode imports"), asset->SetBytecode(bytes), ELibraRuntimeResult::Ok))
        return false;
    auto findNode = [&bytes](uint8 opcode, int32 occurrence = 0)
    {
        auto count = static_cast<uint32>(bytes[8]) | (static_cast<uint32>(bytes[9]) << 8) |
            (static_cast<uint32>(bytes[10]) << 16) | (static_cast<uint32>(bytes[11]) << 24);
        for (auto index = uint32{}; index < count; ++index)
        {
            auto offset = 16 + index * 256;
            if (bytes[offset + 16] != opcode || occurrence-- != 0) continue;
            auto id = FGuid{};
            FGuid::ParseExact(BytesToHex(bytes.GetData() + offset, 16), EGuidFormats::Digits, id);
            return id;
        }
        return FGuid{};
    };
    auto leftId = findNode(48);
    auto rightId = findNode(48, 1);
    auto outputId = findNode(49);
    if (!TestTrue(TEXT("Compiler node identities resolve"),
        leftId.IsValid() && rightId.IsValid() && outputId.IsValid())) return false;
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, FName(TEXT("LibraInt32World")),
        nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated Int32 game world"), world)) return false;
    ON_SCOPE_EXIT { world->DestroyWorld(false); };
    auto actor = world->SpawnActor<AActor>();
    if (!TestNotNull(TEXT("Int32 component actor"), actor)) return false;
    auto component = NewObject<ULibraEffectComponent>(actor);
    component->bAutoPlay = false;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();
    if (!TestEqual(TEXT("Component loads Int32 effect"), component->SetEffect(asset), ELibraRuntimeResult::Ok))
        return false;
    auto type = ELibraValueType::Unknown;
    TestEqual(TEXT("Output type query"), component->GetValueType(outputId, type), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Output is semantically Int32"), type, ELibraValueType::Int32);
    auto left = FLibraParameterHandle{};
    auto right = FLibraParameterHandle{};
    auto output = FLibraValueHandle{};
    TestEqual(TEXT("Bind left"), component->BindParameter(leftId, left), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Bind right"), component->BindParameter(rightId, right), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Bind output"), component->BindValue(outputId, output), ELibraRuntimeResult::Ok);
    auto evaluate = [component]()
    {
        auto result = component->EvaluateAtTime(0);
        if (result != ELibraRuntimeResult::Ok) return result;
        auto deadline = FPlatformTime::Seconds() + 5.0;
        while (component->IsEvaluationPending() && FPlatformTime::Seconds() < deadline)
        {
            component->TickComponent(0, LEVELTICK_All, nullptr);
            FPlatformProcess::Sleep(0.001f);
        }
        return component->IsEvaluationPending() ? ELibraRuntimeResult::Busy : component->LastResult;
    };
    auto value = int32{};
    TestEqual(TEXT("Evaluate compiler Int32 add"), evaluate(), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Read exact Int32"), component->ReadBoundInt32Value(output, value), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Exact Int32 sum"), value, 17);
    TestEqual(TEXT("Fractional writes are rejected"), component->SetBoundParameter(left, 1.5),
        ELibraRuntimeResult::InvalidValueType);
    TestEqual(TEXT("Full-range typed write"), component->SetBoundInt32Parameter(left, MAX_int32),
        ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Typed right write"), component->SetBoundInt32Parameter(right, 1), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Overflow reaches the public component"), evaluate(), ELibraRuntimeResult::ArithmeticOverflow);
    auto foreign = NewObject<ULibraEffectComponent>();
    TestEqual(TEXT("Foreign component rejects the typed handle"), foreign->SetBoundInt32Parameter(left, 0),
        ELibraRuntimeResult::InvalidHandle);
    if (!TestTrue(TEXT("Mixed conversion/custom fixture loads"),
        FFileHelper::LoadFileToArray(bytes, *(directory / TEXT("Mixed.lfxb"))))) return false;
    auto mixed = NewObject<ULibraEffectAsset>();
    TestEqual(TEXT("v10 Float custom node binding imports"), mixed->SetBytecode(bytes), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Component loads explicit conversions"), component->SetEffect(mixed), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Old typed handles are rejected"), component->SetBoundInt32Parameter(left, 0),
        ELibraRuntimeResult::InvalidHandle);
    auto absoluteId = findNode(40);
    TestTrue(TEXT("Mixed fixture contains the real custom Abs opcode"), absoluteId.IsValid());
    TestEqual(TEXT("Mixed output type query"), component->GetValueType(absoluteId, type), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Explicit conversion retains Float semantics"), type, ELibraValueType::Float);
    TestEqual(TEXT("Mixed fixture evaluates"), evaluate(), ELibraRuntimeResult::Ok);
    auto converted = double{};
    TestTrue(TEXT("Explicit truncation and the Float custom node execute exactly"),
        component->GetScalar(absoluteId, converted) && converted == 3.0);
    auto mixedOutput = FLibraValueHandle{};
    TestEqual(TEXT("Bind mixed output"), component->BindValue(absoluteId, mixedOutput), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Float output rejects an Int32 read"), component->ReadBoundInt32Value(mixedOutput, value),
        ELibraRuntimeResult::InvalidValueType);
    component->Stop();
    return true;
}

#endif
