#if WITH_DEV_AUTOMATION_TESTS
#include "LibraDebugDrawScope.h"
#include "LibraLiveGameSession.h"
#include "LibraEffectComponent.h"
#include "Components/SceneCaptureComponent2D.h"
#include "Components/StaticMeshComponent.h"
#include "Components/TextRenderComponent.h"
#include "Dom/JsonObject.h"
#include "Engine/World.h"
#include "Engine/Engine.h"
#include "Engine/SceneCapture2D.h"
#include "Engine/StaticMesh.h"
#include "Engine/TextureRenderTarget2D.h"
#include "GameFramework/Actor.h"
#include "ImageUtils.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "RenderingThread.h"
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
    ON_SCOPE_EXIT { session->ShutdownAndForceDestroy(); };
    auto request = liberafx::RequestId{};
    auto completion = FLibraLiveGameSession::FCompletion{};
    if (!TestEqual(TEXT("Queue a preview payload before attachment"),
        session->SubmitCreate(bytes, TEXT("unattached"), request), liberafx::Result::Ok))
    {
        return false;
    }
    session->Process();
    if (!TestTrue(TEXT("Unattached package still executes"), session->TakeCompletion(request, completion) ==
        liberafx::Result::Ok && completion.result == liberafx::Result::Ok))
    {
        return false;
    }
    auto* unattached = session->Resolve(completion.instance);
    if (!TestTrue(TEXT("Only attachment can permit debug objects on new transfers"),
        unattached && unattached->GetRenderObjectCount() == 0 &&
        session->AttachPreviewDebugDraw() == liberafx::Result::Ok &&
        unattached->GetRenderObjectCount() == 0))
    {
        return false;
    }
    if (!TestTrue(TEXT("Preview package accepted"), session->SubmitCreate(bytes, TEXT("debug"), request) == liberafx::Result::Ok)) return false;
    session->Process();
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
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraDebugEngineAssetPreviewTest, "Libra.Editor.DebugDraw.EngineAssetPreview",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraDebugEngineAssetPreviewTest::RunTest(const FString&)
{
    auto previewFixture = FString{}, distributionFixture = FString{};
    auto previewBytes = TArray<uint8>{}, distributionBytes = TArray<uint8>{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraDebugFixture="), previewFixture) ||
        !FParse::Value(FCommandLine::Get(), TEXT("LibraDebugDistributionFixture="), distributionFixture) ||
        !FFileHelper::LoadFileToArray(previewBytes, *previewFixture) ||
        !FFileHelper::LoadFileToArray(distributionBytes, *distributionFixture))
    {
        AddError(TEXT("Supply isolated engine-preview.lfxp and distribution.lfxp fixtures."));
        return false;
    }

    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto* world = UWorld::CreateWorld(EWorldType::Game, false, NAME_None, nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated engine-asset debug world"), world)) return false;
    GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(world);
    ON_SCOPE_EXIT { GEngine->DestroyWorldContext(world); world->DestroyWorld(false); };

    auto* owner = world->SpawnActor<AActor>();
    auto session = FLibraLiveGameSession::Create(true, owner, {});
    if (!TestTrue(TEXT("Development live session"), !!session)) return false;
    if (!TestEqual(TEXT("Explicitly attach debug preview"), session->AttachPreviewDebugDraw(), liberafx::Result::Ok))
    {
        return false;
    }
    auto request = liberafx::RequestId{};
    if (!TestEqual(TEXT("Submit compiler debug package"),
        session->SubmitCreate(previewBytes, TEXT("engine debug preview"), request), liberafx::Result::Ok))
        return false;
    session->Process();
    auto completion = FLibraLiveGameSession::FCompletion{};
    if (!TestTrue(TEXT("Engine preview package executes"),
        session->TakeCompletion(request, completion) == liberafx::Result::Ok &&
        completion.result == liberafx::Result::Ok))
    {
        AddError(completion.error);
        session->ShutdownAndForceDestroy();
        return false;
    }

    auto* playback = session->Resolve(completion.instance);
    if (!TestNotNull(TEXT("Live package component"), playback)) return false;
    auto* playbackOwner = playback->GetOwner();
    auto meshes = TArray<UStaticMeshComponent*>{};
    auto labels = TArray<UTextRenderComponent*>{};
    playbackOwner->GetComponents(meshes);
    playbackOwner->GetComponents(labels);
    if (!TestTrue(TEXT("Compiler debug model and text reach real engine components"),
        meshes.Num() == 1 && labels.Num() == 1 && meshes[0]->GetStaticMesh() &&
        meshes[0]->GetStaticMesh()->GetPathName() == TEXT("/Engine/BasicShapes/Cube.Cube")))
    {
        session->ShutdownAndForceDestroy();
        return false;
    }

    auto capture = world->SpawnActor<ASceneCapture2D>();
    auto target = NewObject<UTextureRenderTarget2D>(capture);
    target->ClearColor = FLinearColor::Black;
    target->InitCustomFormat(128, 128, PF_B8G8R8A8, true);
    target->UpdateResourceImmediate();
    auto* camera = capture->GetCaptureComponent2D();
    camera->TextureTarget = target;
    camera->bCaptureEveryFrame = false;
    camera->bCaptureOnMovement = false;
    camera->CaptureSource = ESceneCaptureSource::SCS_FinalColorLDR;
    camera->PrimitiveRenderMode = ESceneCapturePrimitiveRenderMode::PRM_UseShowOnlyList;
    camera->ShowFlags.SetFog(false);
    camera->ShowFlags.SetAtmosphere(false);
    camera->ShowFlags.SetLighting(false);
    camera->ShowFlags.SetPostProcessing(false);

    auto capturePixels = [&](UPrimitiveComponent* primitive, const TCHAR* suffix)
    {
        camera->ClearShowOnlyComponents();
        camera->ShowOnlyComponent(primitive);
        const auto forward = primitive->GetForwardVector();
        const auto isText = Cast<UTextRenderComponent>(primitive) != nullptr;
        const auto distance = isText ? 30.0 : 350.0;
        camera->FOVAngle = isText ? 10.0f : 90.0f;
        const auto directions = TArray<FVector>{
            forward, -forward, primitive->GetRightVector(), -primitive->GetRightVector(),
            primitive->GetUpVector(), -primitive->GetUpVector()
        };
        auto pixels = TArray<FColor>{};
        auto visiblePixels = int32{};
        for (const auto& direction : directions)
        {
            capture->SetActorLocation(primitive->GetComponentLocation() + direction * distance);
            capture->SetActorRotation((-direction).Rotation());
            world->SendAllEndOfFrameUpdates();
            camera->CaptureScene();
            FlushRenderingCommands();
            if (!target->GameThread_GetRenderTargetResource()->ReadPixels(pixels) ||
                pixels.Num() != 128 * 128)
                continue;
            visiblePixels = pixels.FilterByPredicate([](const auto& pixel)
            {
                return pixel.R > 12 || pixel.G > 12 || pixel.B > 12;
            }).Num();
            if (visiblePixels > 0) break;
        }
        if (visiblePixels == 0) return false;
        auto png = TArray64<uint8>{};
        FImageUtils::PNGCompressImageArray(128, 128, pixels, png);
        const auto outputDirectory = FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("LibraAcceptance"));
        IFileManager::Get().MakeDirectory(*outputDirectory, true);
        const auto outputPath = FPaths::Combine(outputDirectory,
            FGuid::NewGuid().ToString(EGuidFormats::Digits) + suffix + TEXT(".png"));
        return FFileHelper::SaveArrayToFile(png, *outputPath);
    };
    if (!TestTrue(TEXT("Engine Cube produces actual SceneCapture pixels"),
        capturePixels(meshes[0], TEXT("-debug-model"))) ||
        !TestTrue(TEXT("World debug text produces actual SceneCapture pixels"),
            capturePixels(labels[0], TEXT("-debug-text"))))
    {
        session->ShutdownAndForceDestroy();
        return false;
    }

    auto stale = completion.instance;
    ++stale.generation;
    TestEqual(TEXT("Wrong generation cannot retire debug scope"),
        session->ClearPreviewDebugDraw(stale), liberafx::Result::InvalidHandle);
    meshes.Reset();
    labels.Reset();
    playbackOwner->GetComponents(meshes);
    playbackOwner->GetComponents(labels);
    TestTrue(TEXT("Wrong generation leaves real model and text alive"), meshes.Num() == 1 && labels.Num() == 1);
    TestEqual(TEXT("Debug-only disconnect retires the scope"),
        session->DisconnectPreviewDebugDraw(), liberafx::Result::Ok);
    meshes.Reset();
    labels.Reset();
    playbackOwner->GetComponents(meshes);
    playbackOwner->GetComponents(labels);
    TestTrue(TEXT("Disconnect destroys both actual world components"), meshes.IsEmpty() && labels.IsEmpty());
    TestTrue(TEXT("Disconnect preserves normal effect playback"),
        session->Resolve(completion.instance) == playback && playback->bIsPlaying);

    if (!TestEqual(TEXT("Submit a new preview after disconnect"),
        session->SubmitCreate(previewBytes, TEXT("after disconnect"), request), liberafx::Result::Ok))
    {
        session->ShutdownAndForceDestroy();
        return false;
    }
    session->Process();
    auto nextCompletion = FLibraLiveGameSession::FCompletion{};
    if (!TestTrue(TEXT("New normal playback remains available"),
        session->TakeCompletion(request, nextCompletion) == liberafx::Result::Ok &&
        nextCompletion.result == liberafx::Result::Ok))
    {
        session->ShutdownAndForceDestroy();
        return false;
    }
    auto* nextPlayback = session->Resolve(nextCompletion.instance);
    meshes.Reset();
    labels.Reset();
    if (nextPlayback) nextPlayback->GetOwner()->GetComponents(meshes);
    if (nextPlayback) nextPlayback->GetOwner()->GetComponents(labels);
    TestTrue(TEXT("Disconnected preview does not resurrect debug components"),
        nextPlayback && nextPlayback->bIsPlaying && meshes.IsEmpty() && labels.IsEmpty());
    session->ShutdownAndForceDestroy();

    auto* distributionOwner = world->SpawnActor<AActor>();
    auto distribution = FLibraLiveGameSession::Create(true, distributionOwner, {});
    if (!TestTrue(TEXT("Distribution session"), !!distribution)) return false;
    if (!TestEqual(TEXT("Submit compiler distribution package"),
        distribution->SubmitCreate(distributionBytes, TEXT("distribution"), request), liberafx::Result::Ok))
    {
        distribution->ShutdownAndForceDestroy();
        return false;
    }
    distribution->Process();
    auto distributionCompletion = FLibraLiveGameSession::FCompletion{};
    if (!TestTrue(TEXT("Distribution package executes"),
        distribution->TakeCompletion(request, distributionCompletion) == liberafx::Result::Ok &&
        distributionCompletion.result == liberafx::Result::Ok))
    {
        AddError(distributionCompletion.error);
        distribution->ShutdownAndForceDestroy();
        return false;
    }
    auto* distributionPlayback = distribution->Resolve(distributionCompletion.instance);
    auto* distributionPlaybackOwner = distributionPlayback ? distributionPlayback->GetOwner() : nullptr;
    meshes.Reset();
    labels.Reset();
    if (distributionPlaybackOwner)
    {
        distributionPlaybackOwner->GetComponents(meshes);
        distributionPlaybackOwner->GetComponents(labels);
    }
    TestTrue(TEXT("Distribution has no preview-only debug model or text"),
        distributionPlayback && meshes.IsEmpty() && labels.IsEmpty());
    distribution->ShutdownAndForceDestroy();
    return true;
}
#endif
