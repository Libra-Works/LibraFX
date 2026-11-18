#if WITH_DEV_AUTOMATION_TESTS

#include "LibraEffectComponent.h"
#include "LibraSpawnableAsset.h"
#include "Engine/World.h"
#include "liberafx/LbSpawnableHost.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraSignalTimelineTest,
    "Libra.Runtime.Spawnable.SignalTimeline",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraSignalTimelineTest::RunTest(const FString&)
{
    auto directory = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraSignalTimelineFixtures="), directory) || directory.IsEmpty())
    {
        AddInfo(TEXT("SignalTimeline requires an explicit compiler-produced timeline fixture directory."));
        return true;
    }
    for (auto scoped : {false, true})
    {
        auto bytes = TArray<uint8>{};
        auto path = FPaths::Combine(directory, scoped ? TEXT("ScopedTimeline.lfxp") : TEXT("Timeline.lfxp"));
        if (!TestTrue(TEXT("Compiler-produced timeline package loads"), FFileHelper::LoadFileToArray(bytes, *path)))
            return false;
        auto metadata = liberafx::SpawnablePackage{};
        if (!TestTrue(TEXT("Native package parser accepts fixture"), liberafx::loadSpawnablePackage(
            bytes.GetData(), bytes.Num(), metadata) == liberafx::Result::Ok)) return false;
        auto host = std::unique_ptr<liberafx::SpawnableHost>{};
        if (!TestTrue(TEXT("Embedded native host accepts package"), liberafx::SpawnableHost::create(
            std::move(metadata), {}, host) == liberafx::Result::Ok)) return false;
        TestTrue(TEXT("Initial runtime keys execute"), host->advance(0, 0) == liberafx::Result::Ok);
        if (scoped)
        {
            TestTrue(TEXT("Skipped frame executes local keys"), host->advance(1, 1) == liberafx::Result::Ok);
            auto signals = TArray<liberafx::SpawnableHostEvent>{};
            auto spawns = TArray<liberafx::SpawnableHostEvent>{};
            for (auto index = 0; index < 128; ++index)
            {
                auto event = liberafx::SpawnableHostEvent{};
                if (!host->popEvent(event)) break;
                if (event.kind == liberafx::SpawnableHostEventKind::Signal) signals.Add(event);
                if (event.kind == liberafx::SpawnableHostEventKind::Spawn) spawns.Add(event);
            }
            TestEqual(TEXT("Two scheduled births"), spawns.Num(), 2);
            TestEqual(TEXT("Two local Signal outputs"), signals.Num(), 2);
            if (signals.Num() == 2)
            {
                TestEqual(TEXT("First local key"), signals[0].time, 0.25);
                TestEqual(TEXT("Later instance has its own local clock"), signals[1].time, 0.75);
                TestTrue(TEXT("Distinct instance identities"), signals[0].instanceId != signals[1].instanceId);
                TestTrue(TEXT("Stable container and Signal identity"),
                    FMemory::Memcmp(signals[0].containerId, signals[1].containerId, 16) == 0 &&
                    FMemory::Memcmp(signals[0].signalNodeId, signals[1].signalNodeId, 16) == 0);
                for (const auto& signal : signals)
                    TestTrue(TEXT("Signal belongs to its birth scope"), spawns.ContainsByPredicate([&](const auto& spawn)
                    {
                        return spawn.instanceId == signal.instanceId &&
                            FMemory::Memcmp(spawn.containerId, signal.containerId, 16) == 0 &&
                            signal.time - spawn.time == 0.25;
                    }));
            }
        }
        else
        {
            auto event = liberafx::NamedIoEvent{};
            TestFalse(TEXT("Preview time-zero key is excluded"), host->popGameIoEvent(event));
            TestTrue(TEXT("Root runtime key executes"), host->advance(0.05, 0.05) == liberafx::Result::Ok);
            TestTrue(TEXT("Named root Signal output"), host->popGameIoEvent(event) &&
                event.kind == liberafx::NamedIoKind::OutputSignal && event.width == 0 && event.identity.instanceId == 0);
            TestTrue(TEXT("Output port name preserved"), event.portIndex < host->package().gameIoPorts.size() &&
                host->package().gameIoPorts[event.portIndex].name == "echo");
            TestFalse(TEXT("Second preview key is excluded"), host->popGameIoEvent(event));
            TestTrue(TEXT("Named input still works"), host->fireGameSignal("trigger") == liberafx::Result::Ok &&
                host->popGameIoEvent(event));
        }
        host.reset();

        auto asset = NewObject<ULibraSpawnableAsset>();
        if (!TestEqual(TEXT("Engine asset accepts package"), asset->SetPackageBytes(bytes), ELibraRuntimeResult::Ok))
            return false;
        auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
            .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
            .ShouldSimulatePhysics(false).SetTransactional(false);
        auto world = UWorld::CreateWorld(EWorldType::Game, false, NAME_None, nullptr, true, ERHIFeatureLevel::Num, &init);
        if (!TestNotNull(TEXT("Isolated timeline world"), world)) return false;
        auto actor = world->SpawnActor<AActor>();
        if (!TestNotNull(TEXT("Timeline owner"), actor))
        {
            world->DestroyWorld(false);
            return false;
        }
        auto component = NewObject<ULibraEffectComponent>(actor);
        component->bAutoPlay = false;
        component->Package = asset;
        actor->AddInstanceComponent(component);
        component->RegisterComponent();
        TestEqual(TEXT("Canonical component starts timeline"), component->Play(), ELibraRuntimeResult::Ok);
        component->TickComponent(1.125f, LEVELTICK_All, nullptr);
        TestEqual(TEXT("Timeline component tick succeeds"), component->LastResult, ELibraRuntimeResult::Ok);
        TestEqual(TEXT("Scoped effect completes, manual root remains live"), component->bRootFinished, scoped);
        if (!scoped)
            TestEqual(TEXT("Canonical component retains named game input"),
                component->FireGameSignal(TEXT("trigger")), ELibraRuntimeResult::Ok);
        TestEqual(TEXT("Timeline can restart"), component->Play(), ELibraRuntimeResult::Ok);
        component->Stop();
        TestFalse(TEXT("Stop cancels playback"), component->bIsPlaying);
        TestEqual(TEXT("Stop leaves no renderer"), component->GetRenderObjectCount(), 0);
        world->DestroyWorld(false);
    }
    return true;
}

#endif
