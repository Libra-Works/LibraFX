#if WITH_DEV_AUTOMATION_TESTS
#include "LibraLiveGameSession.h"
#include "LibraLiveGameEndpointComponent.h"
#include "LibraEffectComponent.h"
#include "Engine/World.h"
#include "Engine/Engine.h"
#include "Components/StaticMeshComponent.h"
#include "Materials/Material.h"
#include "Materials/MaterialExpressionVectorParameter.h"
#include "Misc/AutomationTest.h"
#include "Misc/Base64.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include <atomic>
#include <array>
#include <thread>
#include <vector>

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraLiveGameSessionTest, "Libra.Runtime.LiveGame.ComponentSession",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraLiveGameSessionTest::RunTest(const FString&)
{
    auto fixture = FString{}, root = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraLiveFixture="), fixture) ||
        !FParse::Value(FCommandLine::Get(), TEXT("LibraLiveSessionRoot="), root))
    { AddError(TEXT("Supply explicit isolated -LibraLiveFixture=<compiler-output-directory> and -LibraLiveSessionRoot=<private-spool-root>.")); return false; }
    auto before = TArray<uint8>{}, after = TArray<uint8>{}, unsupported = TArray<uint8>{};
    if (!TestTrue(TEXT("Before compiler fixture"), FFileHelper::LoadFileToArray(before, *FPaths::Combine(fixture, TEXT("Live.before.lfxp")))) ||
        !TestTrue(TEXT("After compiler fixture"), FFileHelper::LoadFileToArray(after, *FPaths::Combine(fixture, TEXT("Live.after.lfxp")))) ||
        !TestTrue(TEXT("Unsupported compiler fixture"), FFileHelper::LoadFileToArray(unsupported, *FPaths::Combine(fixture, TEXT("Live.unsupported.lfxp")))))
        return false;
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto* world = UWorld::CreateWorld(EWorldType::Game, false, FName(TEXT("LibraLiveIsolatedWorld")),
        nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Real isolated game world"), world)) return false;
    GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(world);
    ON_SCOPE_EXIT
    {
        GEngine->DestroyWorldContext(world);
        world->DestroyWorld(false);
    };
    auto* actor = world->SpawnActor<AActor>();
    if (!TestNotNull(TEXT("Isolated endpoint owner"), actor)) return false;
    auto* endpoint = NewObject<ULibraLiveGameEndpointComponent>(actor);
    endpoint->bDevelopmentOptIn = true;
    endpoint->IsolatedSessionRoot = root;
#if WITH_EDITOR
    auto* material = NewObject<UMaterial>(endpoint, NAME_None, RF_Transient);
    material->SetShadingModel(MSM_Unlit);
    auto* color = NewObject<UMaterialExpressionVectorParameter>(material);
    color->ParameterName = TEXT("Color");
    color->DefaultValue = FLinearColor::White;
    material->GetExpressionCollection().AddExpression(color);
    material->GetEditorOnlyData()->EmissiveColor.Connect(0, color);
    material->PostEditChange();
    endpoint->AssetBindings.Add({TEXT("builtin:unlit-color"), material});
#endif
    actor->AddInstanceComponent(endpoint);
    endpoint->RegisterComponent();
    if (!TestTrue(TEXT("Explicit development endpoint starts"), endpoint->StartEndpoint()))
    { endpoint->StopEndpoint(); return false; }
    auto* session = endpoint->GetSession();
    auto sequence = uint64{};
    auto debugOwner = FString{};
    auto send = [&](const FString& operation, const FString& target, const TArray<uint8>& bytes) {
        auto descriptorText = FString{};
        FFileHelper::LoadFileToString(descriptorText, *FPaths::Combine(endpoint->EndpointDirectory, TEXT("endpoint.json")));
        auto descriptor = TSharedPtr<FJsonObject>{};
        auto descriptorReader = TJsonReaderFactory<>::Create(descriptorText);
        FJsonSerializer::Deserialize(descriptorReader, descriptor);
        auto command = MakeShared<FJsonObject>();
        command->SetNumberField(TEXT("protocol"), 1);
        command->SetStringField(TEXT("session"), descriptor->GetStringField(TEXT("session")));
        command->SetStringField(TEXT("sequence"), LexToString(++sequence));
        command->SetStringField(TEXT("operation"), operation);
        command->SetStringField(TEXT("debugOwner"), debugOwner);
        command->SetStringField(TEXT("target"), target);
        command->SetStringField(TEXT("packageBase64"), FBase64::Encode(bytes));
        command->SetStringField(TEXT("label"), TEXT("Compiler live fixture"));
        command->SetStringField(TEXT("policy"), TEXT("restart"));
        auto text = FString{};
        auto writer = TJsonWriterFactory<>::Create(&text);
        FJsonSerializer::Serialize(command, writer);
        FFileHelper::SaveStringToFile(text, *FPaths::Combine(endpoint->EndpointDirectory, TEXT("request.json")));
        endpoint->PumpEndpoint(); endpoint->PumpEndpoint();
        auto responseText = FString{};
        FFileHelper::LoadFileToString(responseText, *FPaths::Combine(endpoint->EndpointDirectory, TEXT("response.json")));
        auto response = TSharedPtr<FJsonObject>{};
        auto reader = TJsonReaderFactory<>::Create(responseText);
        FJsonSerializer::Deserialize(reader, response);
        TestTrue(TEXT("Response matches command sequence"), response && response->GetStringField(TEXT("sequence")) == LexToString(sequence));
        return response;
    };
    auto created = send(TEXT("create"), {}, before);
    if (!created || !TestEqual(TEXT("Tool spool creates actual effect"), created->GetStringField(TEXT("result")), FString(TEXT("Ok"))))
    { endpoint->StopEndpoint(); return false; }
    auto listed = session->List();
    TestEqual(TEXT("Live selectable instance"), listed.Num(), 1);
    auto id = listed[0].id;
    auto selected = created->GetStringField(TEXT("target"));
    auto* original = session->Resolve(id);
    auto* originalOwner = original ? original->GetOwner() : nullptr;
    TestNotNull(TEXT("Real public effect component"), original);
    TestEqual(TEXT("Real rendered object from compiled graph"), original->GetRenderObjectCount(), 1);
    auto meshes = TArray<UStaticMeshComponent*>{};
    original->GetOwner()->GetComponents(meshes);
    TestEqual(TEXT("Real game mesh component"), meshes.Num(), 1);
    auto firstPosition = meshes.IsEmpty() ? FVector::ZeroVector : meshes[0]->GetRelativeLocation();
    auto failed = send(TEXT("replace"), selected, unsupported);
    TestEqual(TEXT("Side-effect migration explicitly rejected"), failed->GetStringField(TEXT("result")), FString(TEXT("UnsupportedBinary")));
    TestTrue(TEXT("Unsupported graph protects old component"), session->Resolve(id) == original && original->GetRenderObjectCount() == 1);
    auto invalid = send(TEXT("replace"), selected, TArray<uint8>{1, 2, 3});
    TestTrue(TEXT("Malformed graph protects old component"),
        invalid->GetStringField(TEXT("result")) != TEXT("Ok") && session->Resolve(id) == original);
    auto changed = send(TEXT("replace"), selected, after);
    TestEqual(TEXT("Supported authored graph update commits"), changed->GetStringField(TEXT("result")), FString(TEXT("Ok")));
    TestTrue(TEXT("Generation-scoped instance now owns replacement component"), session->Resolve(id) != original && session->Resolve(id));
    TestTrue(TEXT("Replaced game actor is actually destroyed"), originalOwner && originalOwner->IsActorBeingDestroyed());
    meshes.Reset(); session->Resolve(id)->GetOwner()->GetComponents(meshes);
    TestEqual(TEXT("Exactly one real renderer after commit"), meshes.Num(), 1);
    if (!meshes.IsEmpty()) TestFalse(TEXT("New graph arithmetic reaches real renderer transform"), meshes[0]->GetRelativeLocation().Equals(firstPosition));

    auto workerRequests = std::array<liberafx::RequestId, 4>{};
    auto failures = std::atomic<int>{};
    auto workers = std::vector<std::thread>{};
    for (auto index = std::size_t{}; index < workerRequests.size(); ++index)
        workers.emplace_back([&, index]() {
            if (session->SubmitCreate(before, TEXT("Worker component"), workerRequests[index]) != liberafx::Result::Ok) ++failures;
            if (session->Process() != liberafx::Result::InvalidArgument) ++failures;
        });
    for (auto& worker : workers) worker.join();
    TestEqual(TEXT("Concurrent workers only queue"), failures.load(), 0);
    session->Process(64);
    for (auto request : workerRequests)
    {
        auto done = FLibraLiveGameSession::FCompletion{};
        TestTrue(TEXT("Worker completion"), session->TakeCompletion(request, done) == liberafx::Result::Ok && done.result == liberafx::Result::Ok);
        auto* component = session->Resolve(done.instance);
        TestTrue(TEXT("Worker creation produces actual game render component"), component && component->GetRenderObjectCount() == 1);
        auto destroy = liberafx::RequestId{};
        auto worker = std::thread([&]() { if (session->SubmitDestroy(done.instance, destroy) != liberafx::Result::Ok) ++failures; });
        worker.join();
        session->Process();
        auto destroyed = FLibraLiveGameSession::FCompletion{};
        TestTrue(TEXT("Worker destruction releases real component"), session->TakeCompletion(destroy, destroyed) == liberafx::Result::Ok &&
            destroyed.result == liberafx::Result::Ok && !session->Resolve(done.instance));
        session->SubmitDestroy(done.instance, destroy); session->Process();
        TestTrue(TEXT("Stale generation is rejected"), session->TakeCompletion(destroy, destroyed) == liberafx::Result::Ok &&
            destroyed.result == liberafx::Result::InvalidHandle);
        TestTrue(TEXT("Stale generation cannot clear another playback's debug"),
            session->ClearPreviewDebugDraw(done.instance) == liberafx::Result::InvalidHandle);
    }
    TestEqual(TEXT("Worker destroy submissions"), failures.load(), 0);
    auto* connectedPlayback = session->Resolve(id);
    auto normalRenderCount = connectedPlayback->GetRenderObjectCount();
    debugOwner = TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    TestEqual(TEXT("Explicit debug lease attach"), send(TEXT("debug-attach"), {}, {})->GetStringField(TEXT("result")), FString(TEXT("Ok")));
    debugOwner = TEXT("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    TestEqual(TEXT("New window can explicitly take ownership"), send(TEXT("debug-attach"), {}, {})->GetStringField(TEXT("result")), FString(TEXT("Ok")));
    debugOwner = TEXT("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    TestEqual(TEXT("Old disconnect cannot clear newer transfer"), send(TEXT("debug-disconnect"), {}, {})->GetStringField(TEXT("result")), FString(TEXT("InvalidHandle")));
    TestEqual(TEXT("Old replacement cannot resurrect debug"), send(TEXT("replace"), selected, after)->GetStringField(TEXT("result")), FString(TEXT("InvalidHandle")));
    debugOwner = TEXT("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    TestEqual(TEXT("Debug-only disconnect acknowledged"), send(TEXT("debug-disconnect"), {}, {})->GetStringField(TEXT("result")), FString(TEXT("Ok")));
    TestTrue(TEXT("Debug disconnect preserves ordinary component and renderers"),
        session->Resolve(id) == connectedPlayback && connectedPlayback->GetRenderObjectCount() == normalRenderCount);
    TestTrue(TEXT("Graceful close does not force old lifetime"), session->Close() == liberafx::Result::NotReady && session->Resolve(id));
    auto destroy = liberafx::RequestId{};
    session->SubmitDestroy(id, destroy); session->Process();
    auto completion = FLibraLiveGameSession::FCompletion{};
    session->TakeCompletion(destroy, completion);
    TestTrue(TEXT("Close drains after explicit destroy"), session->Close() == liberafx::Result::Ok);
    auto bounded = FLibraLiveGameSession::Create(true, actor, endpoint->AssetBindings, 1, 2);
    auto one = liberafx::RequestId{}, two = liberafx::RequestId{}, overflow = liberafx::RequestId{};
    TestTrue(TEXT("Bounded requests accept first"), bounded->SubmitCreate(before, TEXT("Credit one"), one) == liberafx::Result::Ok);
    TestTrue(TEXT("Bounded requests accept second"), bounded->SubmitCreate(before, TEXT("Credit two"), two) == liberafx::Result::Ok);
    TestTrue(TEXT("Bounded queue rejects excess"), bounded->SubmitCreate(before, TEXT("Overflow"), overflow) == liberafx::Result::QueueRejected);
    bounded->Process();
    TestTrue(TEXT("Unconsumed completion reserves queue credit"), bounded->SubmitCreate(before, TEXT("Unconsumed"), overflow) == liberafx::Result::QueueRejected);
    TestTrue(TEXT("One real component fits"), bounded->TakeCompletion(one, completion) == liberafx::Result::Ok && completion.result == liberafx::Result::Ok);
    TestTrue(TEXT("Second component fails without touching first"), bounded->TakeCompletion(two, completion) == liberafx::Result::Ok &&
        completion.result == liberafx::Result::QueueRejected && bounded->List().Num() == 1);
    bounded->ShutdownAndForceDestroy();
    endpoint->StopEndpoint();
    TestTrue(TEXT("New connection starts"), endpoint->StartEndpoint());
    session = endpoint->GetSession();
    auto pending = liberafx::RequestId{};
    session->SubmitCreate(before, TEXT("Queued at shutdown"), pending);
    session->ShutdownAndForceDestroy();
    TestTrue(TEXT("Queued shutdown completion survives"), session->TakeCompletion(pending, completion) == liberafx::Result::Ok &&
        completion.result == liberafx::Result::NotReady);
    TestTrue(TEXT("Cross-session ID rejected"), session->SubmitDestroy(id, destroy) != liberafx::Result::Ok);
    endpoint->StopEndpoint();
    return true;
}
#endif
