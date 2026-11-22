#if WITH_DEV_AUTOMATION_TESTS
#include "Camera/CameraComponent.h"
#include "Components/SceneComponent.h"
#include "Engine/Texture2D.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "LibraSpawnableAsset.h"
#include "LibraSpawnableComponent.h"
#include "Materials/Material.h"
#include "Materials/MaterialExpressionTextureSampleParameter2D.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/ScopeExit.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraSharedSimulationTest, "Libra.Runtime.SharedSimulation",
                                 EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraSharedSimulationTest::RunTest(const FString &)
{
    FString path;
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraSharedFixture="), path))
    {
        AddWarning(TEXT("Shared simulation fixture not supplied; pass -LibraSharedFixture=<CPU .lfxb> to execute this "
                        "acceptance test."));
        return true;
    }
    TArray<uint8> bytes;
    if (!TestTrue(TEXT("Read compiled CPU fixture"), FFileHelper::LoadFileToArray(bytes, *path)))
    {
        return false;
    }
    auto *package = NewObject<ULibraSpawnableAsset>();
    if (!TestEqual(TEXT("Accept compiled fixture"), package->SetPackageBytes(bytes), ELibraRuntimeResult::Ok))
    {
        return false;
    }
    auto init = UWorld::InitializationValues()
                    .AllowAudioPlayback(false)
                    .CreatePhysicsScene(false)
                    .RequiresHitProxies(false)
                    .CreateNavigation(false)
                    .CreateAISystem(false)
                    .ShouldSimulatePhysics(false);
    auto *world = UWorld::CreateWorld(EWorldType::Game, false, NAME_None, nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated game world"), world))
    {
        return false;
    }
    ON_SCOPE_EXIT
    {
        world->DestroyWorld(false);
    };
    // CPU Sprite materials are explicit engine bindings, independent of the
    // portable simulation package. Supply the documented contract for this test.
    auto *material = NewObject<UMaterial>(GetTransientPackage(), NAME_None, RF_Transient);
    material->BlendMode = BLEND_Additive;
    material->TwoSided = true;
    material->SetShadingModel(MSM_Unlit);
    auto *texture = NewObject<UMaterialExpressionTextureSampleParameter2D>(material);
    texture->ParameterName = TEXT("Texture");
    texture->Texture =
        LoadObject<UTexture2D>(nullptr, TEXT("/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture"));
    material->GetExpressionCollection().AddExpression(texture);
    material->GetEditorOnlyData()->EmissiveColor.Connect(0, texture);
    material->GetEditorOnlyData()->Opacity.Connect(4, texture);
    auto needsRecompile = false;
    material->SetMaterialUsage(needsRecompile, MATUSAGE_InstancedStaticMeshes);
    material->PostEditChange();
    auto *alphaMaterial = DuplicateObject<UMaterial>(material, GetTransientPackage());
    alphaMaterial->BlendMode = BLEND_Translucent;
    alphaMaterial->PostEditChange();
    TArray<ULibraSpawnableComponent *> effects;
    for (auto i = 0; i < 8; ++i)
    {
        auto *actor = world->SpawnActor<AActor>();
        auto *root = NewObject<USceneComponent>(actor);
        actor->SetRootComponent(root);
        root->RegisterComponent();
        auto *effect = NewObject<ULibraSpawnableComponent>(actor);
        effect->bAutoPlay = false;
        effect->Package = package;
        auto *camera = NewObject<UCameraComponent>(actor);
        camera->RegisterComponent();
        effect->EffectCameraOverride = camera;
        effect->AssetBindings.Add({TEXT("builtin:cpu-particle-additive"), material});
        effect->AssetBindings.Add({TEXT("builtin:cpu-particle-alpha"), alphaMaterial});
        actor->AddInstanceComponent(effect);
        effect->RegisterComponent();
        const auto prepared = effect->PrewarmEffect();
        const auto played = effect->Play();
        AddInfo(FString::Printf(TEXT("Shared effect %d prepare=%d play=%d detail=%s"), i, static_cast<int32>(prepared),
                                static_cast<int32>(played), *effect->LastError));
        if (!TestEqual(TEXT("Prepare shared pool"), prepared, ELibraRuntimeResult::Ok) ||
            !TestEqual(TEXT("Acquire playback"), played, ELibraRuntimeResult::Ok) ||
            !TestTrue(TEXT("Uses prepared storage"), effect->UsesPreparedStorage()))
        {
            return false;
        }
        effects.Add(effect);
    }
    const auto tick = [&]
    {
        for (auto *effect : effects)
        {
            effect->TickComponent(1.0f / 60, LEVELTICK_All, &effect->PrimaryComponentTick);
        }
        FWorldDelegates::OnWorldPostActorTick.Broadcast(world, LEVELTICK_All, 1.0f / 60);
    };
    tick();
    tick();
    tick();
    for (auto *effect : effects)
    {
        TestEqual(TEXT("Eight instances share one simulation frame"), effect->LastSimulationBatchSize, 8);
        TestEqual(TEXT("Shared advance succeeded"), effect->LastResult, ELibraRuntimeResult::Ok);
    }
    const auto pausedTime = effects[0]->RealTimeSeconds;
    for (auto *effect : effects)
    {
        effect->TickComponent(1.0f / 60, LEVELTICK_All, &effect->PrimaryComponentTick);
    }
    effects[0]->Pause();
    FWorldDelegates::OnWorldPostActorTick.Broadcast(world, LEVELTICK_All, 1.0f / 60);
    TestEqual(TEXT("Queued pause does not consume an unexecuted clock step"), effects[0]->RealTimeSeconds, pausedTime);
    TestEqual(TEXT("Resume paused playback"), effects[0]->Play(false), ELibraRuntimeResult::Ok);
    tick();
    for (auto *effect : effects)
    {
        effect->TickComponent(1.0f / 60, LEVELTICK_All, &effect->PrimaryComponentTick);
    }
    TestEqual(TEXT("Stop queued playback"), effects[0]->Stop(), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Recycle queued playback"), effects[0]->Play(), ELibraRuntimeResult::Ok);
    FWorldDelegates::OnWorldPostActorTick.Broadcast(world, LEVELTICK_All, 1.0f / 60);
    TestEqual(TEXT("Old queued generation was excluded"), effects[1]->LastSimulationBatchSize, 7);
    tick();
    tick();
    for (auto *effect : effects)
    {
        TestEqual(TEXT("Recycled playback rejoins shared frame"), effect->LastSimulationBatchSize, 8);
        TestEqual(TEXT("Return prepared playback"), effect->Stop(), ELibraRuntimeResult::Ok);
    }
    AddInfo(TEXT("Prepared pooling, UE task jobs, gathered frames, queued Stop/restart identity and reuse passed."));
    return !HasAnyErrors();
}
#endif
