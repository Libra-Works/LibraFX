#if WITH_DEV_AUTOMATION_TESTS
#include "LibraDebugDrawScope.h"
#include "LibraLiveGameSession.h"
#include "LibraEffectComponent.h"
#include "Components/TextRenderComponent.h"
#include "Dom/JsonObject.h"
#include "Engine/World.h"
#include "Engine/Engine.h"
#include "GameFramework/Actor.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/ScopeExit.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraDebugDrawCleanupTest, "Libra.Editor.DebugDraw.WorldAndCleanup",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraDebugDrawCleanupTest::RunTest(const FString&)
{
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, NAME_None, nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated debug-render world"), world)) return false;
    ON_SCOPE_EXIT { world->DestroyWorld(false); };
    auto actor = world->SpawnActor<AActor>();
    if (!TestNotNull(TEXT("Isolated scope owner"), actor)) return false;
    auto json = FString(TEXT("{\"nodes\":[{\"nodeId\":\"65000000-0000-0000-0000-000000000001\",")
        TEXT("\"kind\":\"debugText\",\"previewOnly\":true,\"debugVersion\":1,\"text\":\"World label\",\"size\":2,")
        TEXT("\"inputs\":[")
        TEXT("{\"id\":\"row0\",\"width\":4,\"sourceComponent\":0,\"defaults\":[1,0,0,17]},")
        TEXT("{\"id\":\"row1\",\"width\":4,\"sourceComponent\":0,\"defaults\":[0,1,0,23]},")
        TEXT("{\"id\":\"row2\",\"width\":4,\"sourceComponent\":0,\"defaults\":[0,0,1,31]},")
        TEXT("{\"id\":\"row3\",\"width\":4,\"sourceComponent\":0,\"defaults\":[0,0,0,1]},")
        TEXT("{\"id\":\"visible\",\"width\":1,\"sourceComponent\":0,\"defaults\":[1]}]}]}"));
    auto root = TSharedPtr<FJsonObject>{};
    if (!FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(json), root)) return false;
    auto scope = FLibraDebugDrawScope{};
    auto error = FString{};
    TestTrue(TEXT("Explicit preview text is prepared"), scope.Configure(*root, *actor, {}, {}, 1, error));
    TestTrue(TEXT("Core queued snapshot reaches real world component"), scope.Update(1, error));
    auto labels = TArray<UTextRenderComponent*>{};
    actor->GetComponents(labels);
    if (!TestEqual(TEXT("One actual TextRenderComponent"), labels.Num(), 1)) return false;
    auto old = TWeakObjectPtr<UTextRenderComponent>(labels[0]);
    TestTrue(TEXT("Position is world-space Matrix translation"), labels[0]->GetComponentLocation().Equals(FVector(17, 23, 31)));
    TestEqual(TEXT("Actual world text"), labels[0]->Text.ToString(), FString(TEXT("World label")));
    scope.Clear(); // Used for stop, disconnect and retransfer.
    TestEqual(TEXT("Scope ownership released"), scope.Count(), 0);
    TestTrue(TEXT("Old component is not retained/rendered"), !old.IsValid() || !old->IsRegistered());
    TestTrue(TEXT("Retransfer creates a fresh bounded scope"), scope.Configure(*root, *actor, {}, {}, 2, error));
    TestTrue(TEXT("Stale program clears instead of retaining old debug pixels"), scope.Update(1, error));
    TestEqual(TEXT("Stale scope cleared"), scope.Count(), 0);
    {
        auto temporary = FLibraDebugDrawScope{};
        TestTrue(TEXT("Owned destructor scope configured"), temporary.Configure(*root, *actor, {}, {}, 3, error));
        TestTrue(TEXT("Owned destructor scope rendered"), temporary.Update(3, error));
    }
    labels.Reset();
    actor->GetComponents(labels);
    TestEqual(TEXT("Destruction removes all debug components"), labels.Num(), 0);
    return true;
}
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraDebugLiveDisconnectTest, "Libra.Editor.DebugDraw.LiveDisconnect",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraDebugLiveDisconnectTest::RunTest(const FString&)
{
    auto fixture = FString{};
    auto bytes = TArray<uint8>{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraDebugFixture="), fixture) ||
        !FFileHelper::LoadFileToArray(bytes, *fixture))
    { AddError(TEXT("Supply isolated -LibraDebugFixture=<compiler-produced text-preview.lfxp>.")); return false; }
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto* world = UWorld::CreateWorld(EWorldType::Game, false, NAME_None, nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated disconnect world"), world)) return false;
    GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(world);
    ON_SCOPE_EXIT { GEngine->DestroyWorldContext(world); world->DestroyWorld(false); };
    auto* actor = world->SpawnActor<AActor>();
    auto session = FLibraLiveGameSession::Create(true, actor, {});
    if (!TestTrue(TEXT("Development session"), static_cast<bool>(session))) return false;
    auto request = liberafx::RequestId{};
    if (!TestTrue(TEXT("Preview package accepted"), session->SubmitCreate(bytes, TEXT("debug"), request) == liberafx::Result::Ok)) return false;
    session->Process();
    auto completion = FLibraLiveGameSession::FCompletion{};
    if (!TestTrue(TEXT("Preview package executes"), session->TakeCompletion(request, completion) == liberafx::Result::Ok &&
        completion.result == liberafx::Result::Ok)) return false;
    auto* playback = session->Resolve(completion.instance);
    if (!TestNotNull(TEXT("Real playback"), playback)) return false;
    auto labels = TArray<UTextRenderComponent*>{};
    playback->GetOwner()->GetComponents(labels);
    if (!TestEqual(TEXT("Compiler debug text reached actual adapter"), labels.Num(), 1)) return false;
    auto stale = completion.instance;
    ++stale.generation;
    TestTrue(TEXT("Wrong generation cannot clear debug"), session->ClearPreviewDebugDraw(stale) == liberafx::Result::InvalidHandle);
    TestEqual(TEXT("Wrong generation left actual debug untouched"), playback->GetRenderObjectCount(), 1);
    TestTrue(TEXT("Debug-only disconnect"), session->DisconnectPreviewDebugDraw() == liberafx::Result::Ok);
    labels.Reset(); playback->GetOwner()->GetComponents(labels);
    TestEqual(TEXT("Forwarding actually destroys debug components"), labels.Num(), 0);
    TestTrue(TEXT("Normal playback instance/lifetime survives"),
        session->Resolve(completion.instance) == playback && playback->bIsPlaying);
    session->SubmitCreate(bytes, TEXT("after disconnect"), request);
    session->Process();
    TestTrue(TEXT("Disconnected session still executes normal effects"),
        session->TakeCompletion(request, completion) == liberafx::Result::Ok && completion.result == liberafx::Result::Ok);
    auto* next = session->Resolve(completion.instance);
    TestTrue(TEXT("Future scopes cannot resurrect disconnected debug"), next && next->bIsPlaying && next->GetRenderObjectCount() == 0);
    session->ShutdownAndForceDestroy(); // explicit isolated test teardown only
    return true;
}
#endif
