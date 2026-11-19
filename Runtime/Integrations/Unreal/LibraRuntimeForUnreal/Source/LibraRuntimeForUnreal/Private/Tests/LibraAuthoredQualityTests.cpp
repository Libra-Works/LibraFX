#if WITH_DEV_AUTOMATION_TESTS
#include "LibraEffectComponent.h"
#include "LibraSpawnableAsset.h"
#include "Engine/Engine.h"
#include "Engine/StaticMesh.h"
#include "Engine/World.h"
#include "Components/StaticMeshComponent.h"
#include "Materials/Material.h"
#include "Materials/MaterialExpressionVectorParameter.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "Scalability.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraAuthoredQualityTest, "Libra.Runtime.Quality.AuthoredComponent",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraAuthoredQualityTest::RunTest(const FString&)
{
    auto directory = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraQualityFixture="), directory))
    {
        AddError(TEXT("Supply -LibraQualityFixture=<isolated compiler-produced fixture directory>."));
        return false;
    }
    auto bytes = TArray<uint8>{};
    auto identities = FString{};
    if (!TestTrue(TEXT("Real authored package"), FFileHelper::LoadFileToArray(bytes,
            *FPaths::Combine(directory, TEXT("AuthoredQuality.lfxp")))) ||
        !TestTrue(TEXT("Real authored identities"), FFileHelper::LoadFileToString(identities,
            *FPaths::Combine(directory, TEXT("AuthoredQuality.json")))))
        return false;
    auto ids = TSharedPtr<FJsonObject>{};
    if (!TestTrue(TEXT("Parse identities"), FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(identities), ids)))
        return false;
    auto conditionId = FGuid{}, resultId = FGuid{};
    if (!TestTrue(TEXT("Quality node UUID"), FGuid::Parse(ids->GetStringField(TEXT("quality")), conditionId)) ||
        !TestTrue(TEXT("Signal-derived value UUID"), FGuid::Parse(ids->GetStringField(TEXT("result")), resultId)))
        return false;
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto* world = UWorld::CreateWorld(EWorldType::Game, false, NAME_None, nullptr,
        true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated actual game world"), world)) return false;
    GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(world);
    ON_SCOPE_EXIT
    {
        world->DestroyWorld(false);
        GEngine->DestroyWorldContext(world);
    };
    auto* actor = world->SpawnActor<AActor>();
    auto* asset = NewObject<ULibraSpawnableAsset>(actor);
    if (!TestEqual(TEXT("Engine package validation"), asset->SetPackageBytes(bytes), ELibraRuntimeResult::Ok))
        return false;
    auto* component = NewObject<ULibraEffectComponent>(actor);
    component->bAutoPlay = false;
    component->Package = asset;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();
    if (!TestEqual(TEXT("Public component publishes quality before authored OnStart"),
        component->Play(), ELibraRuntimeResult::Ok))
        return false;
    auto condition = FVector4{}, value = FVector4{};
    auto width = int32{};
    TestEqual(TEXT("Read actual selected quality condition"),
        component->ReadRootValue(conditionId, condition, width), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Actual engine quality chooses the authored Signal branch"), condition.X,
        Scalability::GetQualityLevels().EffectsQuality >= 2 ? 1.0 : 0.0);
    TestEqual(TEXT("Read Signal-derived pure value"),
        component->ReadRootValue(resultId, value, width), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Two FIFO Vector4 captures reach the component"), value.X, 2.0);
    component->ForceTerminate();
    return true;
}

#if WITH_EDITOR
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraAuthoredPhaseRenderTest, "Libra.Runtime.Lifecycle.AuthoredPhaseRender",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraAuthoredPhaseRenderTest::RunTest(const FString&)
{
    auto directory = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraLifecycleFixture="), directory))
    {
        AddError(TEXT("Supply -LibraLifecycleFixture=<isolated compiler-produced fixture directory>."));
        return false;
    }
    auto bytes = TArray<uint8>{};
    if (!TestTrue(TEXT("Real rendered phase preset"), FFileHelper::LoadFileToArray(bytes,
        *FPaths::Combine(directory, TEXT("PhasePresetRender.lfxp"))))) return false;
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, NAME_None, nullptr,
        true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated phase rendering world"), world)) return false;
    GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(world);
    ON_SCOPE_EXIT
    {
        world->DestroyWorld(false);
        GEngine->DestroyWorldContext(world);
    };
    auto actor = world->SpawnActor<AActor>();
    auto asset = NewObject<ULibraSpawnableAsset>(actor);
    if (!TestEqual(TEXT("Phase package validation"), asset->SetPackageBytes(bytes), ELibraRuntimeResult::Ok))
        return false;
    auto component = NewObject<ULibraEffectComponent>(actor);
    component->bAutoPlay = false;
    component->Package = asset;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();
    auto material = NewObject<UMaterial>(component, NAME_None, RF_Transient);
    material->SetShadingModel(MSM_Unlit);
    auto color = NewObject<UMaterialExpressionVectorParameter>(material);
    color->ParameterName = TEXT("Color");
    color->DefaultValue = FLinearColor::White;
    material->GetExpressionCollection().AddExpression(color);
    material->GetEditorOnlyData()->EmissiveColor.Connect(0, color);
    material->PostEditChange();
    component->AssetBindings.Add({TEXT("builtin:unlit-color"), material});
    auto cube = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cube.Cube"));
    if (!TestNotNull(TEXT("Actual model fixture"), cube)) return false;
    // Keep the engine asset untouched; bind a private model with a tint-capable material.
    auto model = DuplicateObject<UStaticMesh>(cube, component);
    model->SetMaterial(0, material);
    component->AssetBindings.Add({TEXT("/Engine/BasicShapes/Cube.Cube"), model});
    auto playResult = component->Play();
    if (!TestEqual(*FString::Printf(TEXT("Public component starts the actual phase preset: %s"),
        *component->LastError), playResult, ELibraRuntimeResult::Ok)) return false;
    auto originalMesh = static_cast<UStaticMeshComponent*>(nullptr);
    auto sample = [&](float delta, int32 phase)
    {
        component->TickComponent(delta, LEVELTICK_All, nullptr);
        TestEqual(TEXT("Phase evaluation succeeds"), component->LastResult, ELibraRuntimeResult::Ok);
        TestEqual(TEXT("All phases retain one render object"), component->GetRenderObjectCount(), 1);
        auto meshes = TArray<UStaticMeshComponent*>{};
        actor->GetComponents(meshes);
        if (!TestEqual(TEXT("One real engine mesh"), meshes.Num(), 1)) return;
        if (!originalMesh) originalMesh = meshes[0];
        TestTrue(TEXT("Phase changes retain the same mesh and instance"), meshes[0] == originalMesh);
        TestTrue(TEXT("Phase selects its actual engine transform"),
            FMath::IsNearlyEqual(meshes[0]->GetRelativeLocation().X, double(phase - 1)));
        auto dynamic = Cast<UMaterialInstanceDynamic>(meshes[0]->GetMaterial(0));
        if (!TestNotNull(TEXT("Actual material instance"), dynamic)) return;
        auto expected = phase == 0 ? FLinearColor::Red :
            (phase == 1 ? FLinearColor::Green : FLinearColor::Blue);
        TestTrue(TEXT("Start red, Loop green, End blue reaches engine material"),
            dynamic->K2_GetVectorParameterValue(TEXT("Color")).Equals(expected));
        TestTrue(TEXT("Phase completion never adds an implicit Life Time"), component->bIsPlaying);
    };
    sample(0, 0);
    sample(1, 0);
    sample(0.5f, 1);
    sample(2.5f, 1);
    sample(0.5f, 2);
    sample(95.5f, 2);
    TestEqual(TEXT("Explicit ForceEffect ends the retained instance"),
        component->ForceTerminate(), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Explicit force clears the rendered scope"), component->GetRenderObjectCount(), 0);
    return true;
}
#endif
#endif
