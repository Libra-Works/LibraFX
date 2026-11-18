#if WITH_DEV_AUTOMATION_TESTS

#include "LibraSpawnableAsset.h"
#include "LibraEffectComponent.h"
#include "Engine/World.h"
#include "Components/StaticMeshComponent.h"
#include "Materials/Material.h"
#include "Materials/MaterialExpressionVectorParameter.h"
#include "liberafx/LbSpawnablePackage.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

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

    auto component = NewObject<ULibraEffectComponent>(actor);
    component->bAutoPlay = false;
    component->Package = package;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();
    TestEqual(TEXT("Missing material binding rejects playback"), component->Play(), ELibraRuntimeResult::UnsupportedBinary);
    TestEqual(TEXT("Failed playback creates no renderer"), component->GetRenderObjectCount(), 0);
    TestFalse(TEXT("Failed playback does not start the host"), component->bIsPlaying);
#if WITH_EDITOR
    auto material = NewObject<UMaterial>(component, NAME_None, RF_Transient);
    material->SetShadingModel(MSM_Unlit);
    auto color = NewObject<UMaterialExpressionVectorParameter>(material);
    color->ParameterName = TEXT("Color");
    color->DefaultValue = FLinearColor::White;
    material->GetExpressionCollection().AddExpression(color);
    material->GetEditorOnlyData()->EmissiveColor.Connect(0, color);
    material->PostEditChange();
    component->AssetBindings.Add({TEXT("builtin:unlit-color"), material});
#endif
    TestEqual(TEXT("Component starts the shared host"), component->Play(), ELibraRuntimeResult::Ok);
    TestTrue(TEXT("Root is initially playing"), component->bIsPlaying);
    TestEqual(TEXT("Launch owns one renderer"), component->GetRenderObjectCount(), 1);
    component->TickComponent(1, LEVELTICK_All, nullptr);
    auto meshes = TArray<UStaticMeshComponent*>{};
    actor->GetComponents(meshes);
    TestEqual(TEXT("One real engine mesh component"), meshes.Num(), 1);
    if (meshes.Num() == 1)
        TestTrue(TEXT("Renderer reads the native child's position"), FMath::IsNearlyEqual(meshes[0]->GetRelativeLocation().Z, 140.0));
    component->TickComponent(1, LEVELTICK_All, nullptr);
    TestEqual(TEXT("Launch renderer is replaced by twelve burst renderers"), component->GetRenderObjectCount(), 12);
    TestTrue(TEXT("Root waits for spawned children after its deadline"), component->bIsPlaying);
    component->TickComponent(1.5f, LEVELTICK_All, nullptr);
    TestTrue(TEXT("Root finishes after child lifecycle drains"), component->bRootFinished);
    TestFalse(TEXT("Finished root stops ticking"), component->bIsPlaying);
    TestEqual(TEXT("Natural completion releases all renderers"), component->GetRenderObjectCount(), 0);
    TestEqual(TEXT("Restart creates a fresh renderer"), component->Play(), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Restart has one launch renderer"), component->GetRenderObjectCount(), 1);
    component->Stop();
    TestEqual(TEXT("Manual stop releases the host"), component->LastResult, ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Manual stop releases all renderers"), component->GetRenderObjectCount(), 0);
    auto repeated = bytes;
    repeated[24] = 0;
    auto repeatedAsset = NewObject<ULibraSpawnableAsset>(component);
    TestEqual(TEXT("Manual-root fixture remains valid"), repeatedAsset->SetPackageBytes(repeated), ELibraRuntimeResult::Ok);
    component->Package = repeatedAsset;
    auto metadata = liberafx::SpawnablePackage{};
    TestTrue(TEXT("Root signal metadata loads"), liberafx::loadSpawnablePackage(
        repeated.GetData(), repeated.Num(), metadata) == liberafx::Result::Ok && metadata.onStartNodeId.has_value());
    if (metadata.onStartNodeId)
    {
        auto start = FGuid{};
        FGuid::ParseExact(BytesToHex(metadata.onStartNodeId->data(), 16), EGuidFormats::Digits, start);
        TestEqual(TEXT("Repeated fixture starts first instance"), component->Play(), ELibraRuntimeResult::Ok);
        component->TickComponent(0.5f, LEVELTICK_All, nullptr);
        TestEqual(TEXT("Second spawn at a different root time"), component->DispatchNodeSignal(start), ELibraRuntimeResult::Ok);
        component->TickComponent(0.5f, LEVELTICK_All, nullptr);
        TestEqual(TEXT("Two same-template instances own distinct renderers"), component->GetRenderObjectCount(), 2);
        meshes.Reset();
        actor->GetComponents(meshes);
        TestTrue(TEXT("First instance keeps its own time and values"),
            meshes.ContainsByPredicate([](const auto* mesh) { return FMath::IsNearlyEqual(mesh->GetRelativeLocation().Z, 140.0); }));
        TestTrue(TEXT("Second instance starts at local time zero"),
            meshes.ContainsByPredicate([](const auto* mesh) { return FMath::IsNearlyEqual(mesh->GetRelativeLocation().Z, 80.0); }));
        component->Stop();
        TestEqual(TEXT("Stop releases overlapping instances"), component->LastResult, ELibraRuntimeResult::Ok);
        TestEqual(TEXT("No renderer survives Stop"), component->GetRenderObjectCount(), 0);
    }
    world->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraSpawnableGameIoRenderTest,
    "Libra.Runtime.Spawnable.GameIoRendering",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraSpawnableGameIoRenderTest::RunTest(const FString&)
{
    auto fixture = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraSpawnableGameIoFixture="), fixture) || fixture.IsEmpty())
    {
        AddInfo(TEXT("GameIoRendering requires the compiler-produced GameIo.lfxp fixture."));
        return true;
    }
    auto bytes = TArray<uint8>{};
    auto identities = FString{};
    auto ids = TSharedPtr<FJsonObject>{};
    if (!TestTrue(TEXT("Game I/O fixture loads"), FFileHelper::LoadFileToArray(bytes, *fixture) &&
        FFileHelper::LoadFileToString(identities, *FPaths::ChangeExtension(fixture, TEXT("identities.json"))) &&
        FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(identities), ids))) return false;
    auto package = NewObject<ULibraSpawnableAsset>();
    if (!TestEqual(TEXT("Game I/O package validates"), package->SetPackageBytes(bytes), ELibraRuntimeResult::Ok)) return false;
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, FName(TEXT("LibraGameIoRenderingWorld")),
        nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated game I/O world"), world)) return false;
    auto actor = world->SpawnActor<AActor>();
    auto component = NewObject<ULibraEffectComponent>(actor);
    component->bAutoPlay = false;
    component->Package = package;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();
    TestEqual(TEXT("Root rendering starts without child instances"), component->Play(), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Root owns one renderer"), component->GetRenderObjectCount(), 1);
    TestEqual(TEXT("Named property input is accepted"), component->SetGameProperty(TEXT("position"), {10}), ELibraRuntimeResult::Ok);
    component->TickComponent(0.1f, LEVELTICK_All, nullptr);
    TestEqual(TEXT("Named signal creates the first scope"), component->FireGameSignal(TEXT("spawn")), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Root and first child's draw objects"), component->GetRenderObjectCount(), 3);
    component->SetGameProperty(TEXT("position"), {20});
    component->TickComponent(0.1f, LEVELTICK_All, nullptr);
    auto meshes = TArray<UStaticMeshComponent*>{};
    actor->GetComponents(meshes);
    auto tag = [&](const TCHAR* field)
    {
        auto id = FGuid{};
        FGuid::Parse(ids->GetStringField(field), id);
        return FName(*id.ToString());
    };
    auto rootTag = tag(TEXT("rootDraw")), captureTag = tag(TEXT("captureDraw")), liveTag = tag(TEXT("liveDraw"));
    auto has = [&](FName nodeTag, double x)
    {
        return meshes.ContainsByPredicate([&](const auto* mesh)
            { return mesh->ComponentHasTag(nodeTag) && FMath::IsNearlyEqual(mesh->GetRelativeLocation().X, x); });
    };
    TestTrue(TEXT("Root follows its own property"), has(rootTag, 20));
    TestTrue(TEXT("Captured child remains frozen"), has(captureTag, 10));
    TestTrue(TEXT("Live child follows the new property"), has(liveTag, 20));
    component->FireGameSignal(TEXT("spawn"));
    component->SetGameProperty(TEXT("position"), {30});
    component->TickComponent(0.1f, LEVELTICK_All, nullptr);
    meshes.Reset();
    actor->GetComponents(meshes);
    TestEqual(TEXT("Two children and root own five renderers"), component->GetRenderObjectCount(), 5);
    TestTrue(TEXT("First capture retains ten"), has(captureTag, 10));
    TestTrue(TEXT("Second capture retains twenty"), has(captureTag, 20));
    TestTrue(TEXT("Live values update in both children"), has(liveTag, 30) &&
        meshes.FilterByPredicate([&](const auto* mesh)
            { return mesh->ComponentHasTag(liveTag) && FMath::IsNearlyEqual(mesh->GetRelativeLocation().X, 30.0); }).Num() == 2);
    TestEqual(TEXT("Natural end is explicitly requested"), component->RequestNaturalEnd(), ELibraRuntimeResult::Ok);
    component->TickComponent(2.1f, LEVELTICK_All, nullptr);
    TestTrue(TEXT("Root finishes after child scopes drain"), component->bRootFinished);
    TestEqual(TEXT("Natural root end removes root and child rendering"), component->GetRenderObjectCount(), 0);
    component->Stop();
    TestEqual(TEXT("Force fixture restarts on the public whole-effect component"),
        component->Play(), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("First force-test child"), component->FireGameSignal(TEXT("spawn")), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Second force-test child"), component->FireGameSignal(TEXT("spawn")), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Force test has root and two child render scopes"), component->GetRenderObjectCount(), 5);
    auto templateId = FGuid{};
    FGuid::Parse(ids->GetStringField(TEXT("template")), templateId);
    TestEqual(TEXT("Force one exact child"), component->ForceTerminateChild(templateId, 1), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Force retains root and sibling rendering"), component->GetRenderObjectCount(), 3);
    TestEqual(TEXT("Duplicate child force is idempotent"), component->ForceTerminateChild(templateId, 1), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Duplicate does not touch the sibling"), component->GetRenderObjectCount(), 3);
    TestEqual(TEXT("Public whole-effect force"), component->ForceTerminate(), ELibraRuntimeResult::Ok);
    TestTrue(TEXT("Force marks the public owner finished and not playing"),
        component->bRootFinished && !component->bIsPlaying);
    TestEqual(TEXT("Whole-effect force releases root and every child"), component->GetRenderObjectCount(), 0);
    TestEqual(TEXT("Whole-effect force duplicate"), component->ForceTerminate(), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Public Play restarts after force"), component->Play(), ELibraRuntimeResult::Ok);
    component->Stop();
    world->DestroyWorld(false);
    return true;
}

#endif
