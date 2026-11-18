#if WITH_DEV_AUTOMATION_TESTS && PLATFORM_WINDOWS
#include "LibraSpawnableComponent.h"
#include "LibraSpawnableAsset.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformTime.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "Windows/WindowsHWrapper.h"
#include <array>

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraPackageDebuggerWorkerTest, "Libra.Runtime.Debugger.PackageWorker",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraPackageDebuggerWorkerTest::RunTest(const FString&)
{
    auto directory = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraDebuggerFixture="), directory) || FPaths::IsRelative(directory))
    {
        AddError(TEXT("Supply -LibraDebuggerFixture=<absolute authored lifecycle fixture directory>."));
        return false;
    }
    auto bytes = TArray<uint8>{};
    auto ids = TArray<uint8>{};
    if (!TestTrue(TEXT("Load authored package and scope identities"),
        FFileHelper::LoadFileToArray(bytes, *FPaths::Combine(directory, TEXT("ForceCurrent.lfxp"))) &&
        FFileHelper::LoadFileToArray(ids, *FPaths::Combine(directory, TEXT("ForceCurrent.ids"))) && ids.Num() == 48))
        return false;
    auto id = [&ids](int32 offset) {
        auto word = [&ids](int32 i) {
            return (uint32{ids[i]} << 24) | (uint32{ids[i + 1]} << 16) |
                (uint32{ids[i + 2]} << 8) | uint32{ids[i + 3]};
        };
        return FGuid(word(offset), word(offset + 4), word(offset + 8), word(offset + 12));
    };

    auto invitation = FGuid::NewGuid().ToString(EGuidFormats::Digits).ToLower();
    auto name = FString(TEXT("\\\\.\\pipe\\LiberaFx.ExecutionDebug.")) + invitation;
    auto pipe = CreateNamedPipeW(*name, PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_NOWAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 4096, 4096, 0, nullptr);
    if (!TestTrue(TEXT("Open private in-process test controller"), pipe != INVALID_HANDLE_VALUE)) return false;
    ON_SCOPE_EXIT { CloseHandle(pipe); };
    auto identity = uint64{14695981039346656037ULL};
    for (auto byte : bytes) identity = (identity ^ byte) * 1099511628211ULL;
    auto deadline = FPlatformTime::Seconds() + 5;
    auto until = [&deadline](auto predicate) {
        do {
            if (predicate()) return true;
            FPlatformProcess::SleepNoStats(.002f);
        } while (FPlatformTime::Seconds() < deadline);
        return false;
    };
    auto frame = std::array<uint8, 984>{};
    auto exchange = [&](uint8 operation) {
        auto command = std::array<uint8, 32>{};
        FMemory::Memcpy(command.data(), "LFD2", 4);
        command[4] = operation;
        for (auto i = 0u; i < 8; ++i) command[8 + i] = static_cast<uint8>(identity >> (8 * i));
        auto count = DWORD{};
        return WriteFile(pipe, command.data(), static_cast<DWORD>(command.size()), &count, nullptr) &&
            count == command.size() &&
            until([&] { return PeekNamedPipe(pipe, nullptr, 0, nullptr, &count, nullptr) && count >= frame.size(); }) &&
            ReadFile(pipe, frame.data(), static_cast<DWORD>(frame.size()), &count, nullptr) &&
            count == frame.size() && FMemory::Memcmp(frame.data(), "LFD2", 4) == 0;
    };
    auto decode = [&frame](int32 offset) {
        auto value = uint64{};
        for (auto i = 0u; i < 8; ++i) value |= uint64{frame[offset + i]} << (8 * i);
        return value;
    };

    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto* world = UWorld::CreateWorld(EWorldType::Game, false, NAME_None, nullptr,
        true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated game world"), world)) return false;
    GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(world);
    ON_SCOPE_EXIT { world->DestroyWorld(false); GEngine->DestroyWorldContext(world); };
    auto* actor = world->SpawnActor<AActor>();
    if (!TestNotNull(TEXT("Debugger test owner"), actor)) return false;
    auto* asset = NewObject<ULibraSpawnableAsset>(actor);
    if (!TestEqual(TEXT("Load package asset"), asset->SetPackageBytes(bytes), ELibraRuntimeResult::Ok)) return false;
    auto* component = NewObject<ULibraSpawnableComponent>(actor);
    component->bAutoPlay = false;
    component->Package = asset;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();
    if (!TestEqual(TEXT("OnStart creates the only child"), component->Play(), ELibraRuntimeResult::Ok)) return false;
    constexpr auto firstChild = int64{1}; // This fixture starts one child in a new host.
    auto generation = component->GetDebuggerGeneration();
    deadline = FPlatformTime::Seconds() + 5;
    if (!TestEqual(TEXT("Attach exact child on game thread"),
            component->AttachExecutionDebugger(generation, id(0), firstChild, invitation), ELibraRuntimeResult::Ok) ||
        !TestTrue(TEXT("Connect and report scope"), until([&] { return ConnectNamedPipe(pipe, nullptr) ||
            GetLastError() == ERROR_PIPE_CONNECTED; }) && exchange(1) && frame[5] == 0 &&
            decode(952) == static_cast<uint64>(generation) && decode(960) == firstChild &&
            FMemory::Memcmp(frame.data() + 968, ids.GetData(), 16) == 0) ||
        !TestTrue(TEXT("Arm Pause"), exchange(4) && frame[5] == 0) ||
        !TestEqual(TEXT("Queue authored child notification"), component->NotifyChildEndRequest(id(0), firstChild, id(16)),
            ELibraRuntimeResult::Ok) ||
        !TestTrue(TEXT("Worker paused before the exact Force node"), until([&] { return exchange(0) && frame[4] == 2; }) &&
            FMemory::Memcmp(frame.data() + 16, ids.GetData() + 16, 16) == 0))
        return false;

    auto draws = component->GetRenderObjectCount();
    auto start = FPlatformTime::Seconds();
    component->TickComponent(.016f, LEVELTICK_All, nullptr);
    TestEqual(TEXT("Conflicting mutation reports Busy"), component->ForceTerminate(), ELibraRuntimeResult::Busy);
    TestEqual(TEXT("Paused snapshot stays visible"), component->GetRenderObjectCount(), draws);
    TestTrue(TEXT("Game-thread tick under one second while worker remains paused"),
        FPlatformTime::Seconds() - start < 1 && exchange(0) && frame[4] == 2);
    start = FPlatformTime::Seconds();
    component->DestroyComponent(); // The private controller stays open until teardown returns.
    auto count = DWORD{};
    auto closed = !PeekNamedPipe(pipe, nullptr, 0, nullptr, &count, nullptr) &&
        (GetLastError() == ERROR_BROKEN_PIPE || GetLastError() == ERROR_PIPE_NOT_CONNECTED);
    return TestTrue(TEXT("Destroy releases and joins paused worker under two seconds"),
        FPlatformTime::Seconds() - start < 2) &&
        TestTrue(TEXT("Destroy closes the runtime side of the private controller"), closed);
}
#endif
