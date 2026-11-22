#if WITH_DEV_AUTOMATION_TESTS
#include "Camera/CameraComponent.h"
#include "Components/SceneComponent.h"
#include "Engine/Texture2D.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "ImageUtils.h"
#include "LibraSpawnableAsset.h"
#include "LibraSpawnableComponent.h"
#include "Materials/Material.h"
#include "Materials/MaterialExpressionAdd.h"
#include "Materials/MaterialExpressionComponentMask.h"
#include "Materials/MaterialExpressionMultiply.h"
#include "Materials/MaterialExpressionTextureCoordinate.h"
#include "Materials/MaterialExpressionTextureSampleParameter2D.h"
#include "Materials/MaterialExpressionVectorParameter.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraCpuParticleSpriteTest, "Libra.Runtime.ParticleSpriteAnimation",
                                 EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraCpuParticleSpriteTest::RunTest(const FString &parameters)
{
    FString directory;
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraParticleSpriteFixtures="), directory))
    {
        AddInfo(TEXT("Skipped: supply -LibraParticleSpriteFixtures=<authoring fixture directory>."));
        return true;
    }
    auto init = UWorld::InitializationValues()
                    .AllowAudioPlayback(false)
                    .CreatePhysicsScene(false)
                    .RequiresHitProxies(false)
                    .CreateNavigation(false)
                    .CreateAISystem(false)
                    .ShouldSimulatePhysics(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, NAME_None, nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated world"), world))
    {
        return false;
    }
    ON_SCOPE_EXIT
    {
        world->DestroyWorld(false);
    };
    auto atlas = FImageUtils::ImportFileAsTexture2D(FPaths::Combine(directory, TEXT("atlas.png")));
    if (!TestNotNull(TEXT("Atlas loaded"), atlas))
    {
        return false;
    }
    auto material = NewObject<UMaterial>();
    material->BlendMode = BLEND_Translucent;
    material->TwoSided = true;
    material->SetShadingModel(MSM_Unlit);
    const auto expression = [material]<typename T>()
    {
        auto value = NewObject<T>(material);
        material->GetExpressionCollection().AddExpression(value);
        value->Material = material;
        value->UpdateMaterialExpressionGuid(true, false);
        return value;
    };
    auto uv = expression.operator()<UMaterialExpressionTextureCoordinate>();
    auto rect = expression.operator()<UMaterialExpressionVectorParameter>();
    rect->ParameterName = TEXT("AtlasRect");
    rect->DefaultValue = FLinearColor(0, 0, 1, 1);
    auto offset = expression.operator()<UMaterialExpressionComponentMask>();
    offset->R = true;
    offset->G = true;
    offset->B = false;
    offset->A = false;
    offset->Input.Connect(5, rect);
    auto size = expression.operator()<UMaterialExpressionComponentMask>();
    size->R = false;
    size->G = false;
    size->B = true;
    size->A = true;
    size->Input.Connect(5, rect);
    auto scaled = expression.operator()<UMaterialExpressionMultiply>();
    scaled->A.Connect(0, uv);
    scaled->B.Connect(0, size);
    auto shifted = expression.operator()<UMaterialExpressionAdd>();
    shifted->A.Connect(0, scaled);
    shifted->B.Connect(0, offset);
    auto texture = expression.operator()<UMaterialExpressionTextureSampleParameter2D>();
    texture->ParameterName = TEXT("Texture");
    texture->Texture = LoadObject<UTexture2D>(nullptr,
        TEXT("/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture"));
    texture->AutoSetSampleType();
    texture->Coordinates.Connect(0, shifted);
    material->GetEditorOnlyData()->EmissiveColor.Connect(0, texture);
    material->GetEditorOnlyData()->Opacity.Connect(4, texture);
    auto recompile = false;
    material->UpdateCachedExpressionData();
    material->SetMaterialUsage(recompile, MATUSAGE_InstancedStaticMeshes);
    material->PostEditChange();
    for (const auto layered : {false, true})
    {
        TArray<uint8> bytes;
        const auto name = layered ? TEXT("cpu-composition.lfxb") : TEXT("cpu-sequence.lfxb");
        if (!TestTrue(TEXT("Read compiled package"),
                      FFileHelper::LoadFileToArray(bytes, *FPaths::Combine(directory, name))))
        {
            return false;
        }
        auto package = NewObject<ULibraSpawnableAsset>();
        if (!TestEqual(TEXT("Load package"), package->SetPackageBytes(bytes), ELibraRuntimeResult::Ok))
        {
            return false;
        }
        auto actor = world->SpawnActor<AActor>();
        auto root = NewObject<USceneComponent>(actor);
        actor->SetRootComponent(root);
        root->RegisterComponent();
        auto camera = NewObject<UCameraComponent>(actor);
        camera->RegisterComponent();
        auto effect = NewObject<ULibraSpawnableComponent>(actor);
        effect->bAutoPlay = false;
        effect->Package = package;
        effect->EffectCameraOverride = camera;
        effect->AssetBindings.Add({TEXT("builtin:cpu-particle-alpha"), material});
        effect->AssetBindings.Add({TEXT("/Game/LibraTests/ParticleSpriteAtlas.ParticleSpriteAtlas"), atlas});
        actor->AddInstanceComponent(effect);
        effect->RegisterComponent();
        if (!TestEqual(TEXT("Play CPU animation"), effect->Play(), ELibraRuntimeResult::Ok))
        {
            AddError(effect->LastError);
            return false;
        }
        const auto tick = [&]
        {
            effect->TickComponent(1.f / 60, LEVELTICK_All, &effect->PrimaryComponentTick);
            FWorldDelegates::OnWorldPostActorTick.Broadcast(world, LEVELTICK_All, 1.f / 60);
        };
        for (auto i = 0; i < 50; ++i)
        {
            tick();
            TestEqual(TEXT("Animated scene update"), effect->LastResult, ELibraRuntimeResult::Ok);
        }
        TestEqual(TEXT("Stop clears scene"), effect->Stop(), ELibraRuntimeResult::Ok);
        TestEqual(TEXT("Restart authored clip"), effect->Play(), ELibraRuntimeResult::Ok);
        tick();
        TestEqual(TEXT("Recycled scene update"), effect->LastResult, ELibraRuntimeResult::Ok);
        effect->Stop();
        AddInfo(
            FString::Printf(TEXT("%s: metadata 4, material/texture binding, scene updates and replay passed."), name));
    }
    return !HasAnyErrors();
}
#endif
