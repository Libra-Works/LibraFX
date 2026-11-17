#if WITH_DEV_AUTOMATION_TESTS

#include "LibraSpawnableAsset.h"
#include "LibraSpawnableComponent.h"
#include "Engine/World.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraSpawnableComponentFixtureTest,
    "Libra.Runtime.Spawnable.ComponentFixture",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraSpawnableComponentFixtureTest::RunTest(const FString&)
{
    auto fixture = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraSpawnableFixture="), fixture) || fixture.IsEmpty())
    {
        AddInfo(TEXT("SpawnableComponentFixture requires an explicit isolated LFXP fixture path."));
        return true;
    }

    auto bytes = TArray<uint8>{};
    if (!TestTrue(TEXT("LFXP fixture loads"), FFileHelper::LoadFileToArray(bytes, *fixture))) return false;
    auto package = NewObject<ULibraSpawnableAsset>();
    if (!TestEqual(TEXT("LFXP asset validates"), package->SetPackageBytes(bytes), ELibraRuntimeResult::Ok))
        return false;
    TestEqual(TEXT("Imported package remains valid"), package->ValidatePackage(), ELibraRuntimeResult::Ok);

    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, FName(TEXT("LibraSpawnableFixtureWorld")),
        nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated game world"), world)) return false;
    auto actor = world->SpawnActor<AActor>();
    if (!TestNotNull(TEXT("Spawnable fixture actor"), actor))
    {
        world->DestroyWorld(false);
        return false;
    }

    auto component = NewObject<ULibraSpawnableComponent>(actor);
    component->bAutoPlay = false;
    component->Package = package;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();
    TestEqual(TEXT("Component starts the shared host"), component->Play(), ELibraRuntimeResult::Ok);
    TestTrue(TEXT("Root is initially playing"), component->bIsPlaying);
    component->TickComponent(2, LEVELTICK_All, nullptr);
    TestTrue(TEXT("Root waits for spawned children after its deadline"), component->bIsPlaying);
    component->TickComponent(1.5f, LEVELTICK_All, nullptr);
    TestTrue(TEXT("Root finishes after child lifecycle drains"), component->bRootFinished);
    TestFalse(TEXT("Finished root stops ticking"), component->bIsPlaying);
    TestEqual(TEXT("Manual stop releases the host"), component->Stop(), ELibraRuntimeResult::Ok);
    world->DestroyWorld(false);
    return true;
}

#endif
