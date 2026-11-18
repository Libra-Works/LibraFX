#if WITH_DEV_AUTOMATION_TESTS && WITH_EDITOR

#include "LibraEffectComponent.h"
#include "LibraSpawnableAsset.h"
#include "Components/SceneComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Dom/JsonObject.h"
#include "Engine/Engine.h"
#include "Engine/Texture2D.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "HAL/FileManager.h"
#include "Materials/Material.h"
#include "Materials/MaterialExpressionComponentMask.h"
#include "Materials/MaterialExpressionMultiply.h"
#include "Materials/MaterialExpressionTextureSampleParameter2D.h"
#include "Materials/MaterialExpressionVectorParameter.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace {
template<int32 Count>
bool ReadLanes(const TSharedPtr<FJsonObject>& object, const TCHAR* field, double (&lanes)[Count])
{
    const TArray<TSharedPtr<FJsonValue>>* values = nullptr;
    if (!object || !object->TryGetArrayField(field, values) || values->Num() != Count) return false;
    for (auto index = 0; index < Count; ++index)
        if (!(*values)[index] || !(*values)[index]->TryGetNumber(lanes[index]) || !FMath::IsFinite(lanes[index]))
            return false;
    return true;
}

TSharedPtr<FJsonObject> AsFixtureObject(const TSharedPtr<FJsonValue>& value)
{
    return value && value->Type == EJson::Object ? value->AsObject() : nullptr;
}

UMaterial* MakeSpriteProbeMaterial(UObject* outer, UTexture2D* texture)
{
    auto material = NewObject<UMaterial>(outer, NAME_None, RF_Transient);
    material->BlendMode = BLEND_Translucent;
    material->TwoSided = true;
    material->SetShadingModel(MSM_Unlit);
    auto sample = NewObject<UMaterialExpressionTextureSampleParameter2D>(material);
    sample->ParameterName = TEXT("Texture");
    sample->Texture = texture;
    sample->AutoSetSampleType();
    auto rect = NewObject<UMaterialExpressionVectorParameter>(material);
    rect->ParameterName = TEXT("AtlasRect");
    rect->DefaultValue = FLinearColor(0, 0, 1, 1);
    auto tint = NewObject<UMaterialExpressionVectorParameter>(material);
    tint->ParameterName = TEXT("Tint");
    tint->DefaultValue = FLinearColor::White;
    auto tinted = NewObject<UMaterialExpressionMultiply>(material);
    tinted->A.Connect(0, sample);
    tinted->B.Connect(0, tint);
    // Probe the actual MID values; this material is not a pixel-compositing test.
    auto uv = NewObject<UMaterialExpressionComponentMask>(material);
    uv->R = uv->G = true;
    uv->Input.Connect(0, rect);
    sample->Coordinates.Connect(0, uv);
    material->GetExpressionCollection().AddExpression(sample);
    material->GetExpressionCollection().AddExpression(rect);
    material->GetExpressionCollection().AddExpression(tint);
    material->GetExpressionCollection().AddExpression(tinted);
    material->GetExpressionCollection().AddExpression(uv);
    material->GetEditorOnlyData()->EmissiveColor.Connect(0, tinted);
    material->GetEditorOnlyData()->Opacity.Connect(4, sample);
    material->PostEditChange();
    return material;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGameSpriteCompositionTest,
    "Libra.Runtime.SpriteComposition.GamePackage",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGameSpriteCompositionTest::RunTest(const FString&)
{
    auto directory = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraSpriteCompositionFixture="), directory) ||
        directory.IsEmpty() || FPaths::IsRelative(directory))
    {
        AddError(TEXT("Supply -LibraSpriteCompositionFixture=<absolute compiler-produced fixture directory>."));
        return false;
    }

    const auto packagePath = directory / TEXT("composition.lfxp");
    const auto expectedPath = directory / TEXT("expected.json");
    constexpr auto maxFixtureBytes = 1024 * 1024;
    auto boundedFile = [](const FString& path)
    {
        const auto size = IFileManager::Get().FileSize(*path);
        return size > 0 && size <= maxFixtureBytes;
    };
    auto bytes = TArray<uint8>{};
    auto expectedText = FString{};
    if (!TestTrue(TEXT("Bounded compiler-produced package and expectations"),
        boundedFile(packagePath) && boundedFile(expectedPath) &&
        FFileHelper::LoadFileToArray(bytes, *packagePath) && FFileHelper::LoadFileToString(expectedText, *expectedPath)))
        return false;
    auto expected = TSharedPtr<FJsonObject>{};
    const TArray<TSharedPtr<FJsonValue>>* frames = nullptr;
    if (!TestTrue(TEXT("Fixture covers start, cell change, interpolation, hold and common wrap"),
        FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(expectedText), expected) && expected &&
        expected->TryGetArrayField(TEXT("frames"), frames) && frames->Num() == 5)) return false;

    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, NAME_None, nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated game world"), world)) return false;
    GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(world);
    ON_SCOPE_EXIT
    {
        world->DestroyWorld(false);
        GEngine->DestroyWorldContext(world);
    };
    auto actor = world->SpawnActor<AActor>();
    if (!TestNotNull(TEXT("Game effect owner"), actor)) return false;
    auto root = NewObject<USceneComponent>(actor, NAME_None, RF_Transient);
    actor->SetRootComponent(root);
    actor->AddInstanceComponent(root);
    root->RegisterComponent();
    actor->SetActorLocationAndRotation(FVector(30, -11, 5), FRotator(0, 27, 0));
    auto package = NewObject<ULibraSpawnableAsset>(actor);
    if (!TestEqual(TEXT("Public package validates compiled sprite payload"), package->SetPackageBytes(bytes),
        ELibraRuntimeResult::Ok)) return false;
    auto component = NewObject<ULibraEffectComponent>(actor);
    component->bAutoPlay = false;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();
    if (!TestEqual(TEXT("Public component accepts package"), component->SetPackage(package),
        ELibraRuntimeResult::Ok)) return false;
    const auto texturePath = TEXT("/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture");
    auto texture = LoadObject<UTexture2D>(nullptr, texturePath);
    if (!TestNotNull(TEXT("Fixture atlas"), texture)) return false;
    component->AssetBindings.Add({texturePath, texture});
    component->AssetBindings.Add({TEXT("builtin:sprite"), MakeSpriteProbeMaterial(component, texture)});
    const auto playResult = component->Play();
    if (!TestEqual(*FString::Printf(TEXT("Public game package starts: %s"), *component->LastError),
        playResult, ELibraRuntimeResult::Ok)) return false;

    auto previous = 0.0;
    auto originalMeshes = TMap<FGuid, UStaticMeshComponent*>{};
    constexpr double sampleTimes[]{0, 0.25, 0.75, 1.75, 2};
    for (auto frameIndex = 0; frameIndex < frames->Num(); ++frameIndex)
    {
        const auto frame = AsFixtureObject((*frames)[frameIndex]);
        const TArray<TSharedPtr<FJsonValue>>* parts = nullptr;
        auto time = double{};
        if (!TestTrue(TEXT("Frame has two simultaneous parts at a fixed clock time"),
            frame && frame->TryGetNumberField(TEXT("timeSeconds"), time) && time == sampleTimes[frameIndex] &&
            frame->TryGetArrayField(TEXT("nodes"), parts) && parts->Num() == 2)) return false;
        component->TickComponent(static_cast<float>(time - previous), LEVELTICK_All, nullptr);
        previous = time;
        if (!TestEqual(*FString::Printf(TEXT("Game tick %.2f: %s"), time, *component->LastError),
            component->LastResult, ELibraRuntimeResult::Ok)) return false;
        TestTrue(TEXT("Public game component clock advances without ending"), component->bIsPlaying &&
            FMath::IsNearlyEqual(component->TimeSeconds, time));
        TestEqual(TEXT("Exactly two owned render objects"), component->GetRenderObjectCount(), 2);
        auto meshes = TArray<UStaticMeshComponent*>{};
        actor->GetComponents(meshes);
        if (!TestEqual(TEXT("Exactly two real mesh components"), meshes.Num(), 2)) return false;
        auto seen = TSet<FGuid>{};
        for (const auto& value : *parts)
        {
            const auto part = AsFixtureObject(value);
            auto text = FString{};
            auto id = FGuid{};
            auto order = double{};
            double position[3]{}, rotation[3]{}, scale[3]{};
            const TArray<TSharedPtr<FJsonValue>>* parameters = nullptr;
            if (!TestTrue(TEXT("Valid expected stable part, XY transform, order and atlas"),
                part && part->TryGetStringField(TEXT("nodeId"), text) && FGuid::Parse(text, id) && !seen.Contains(id) &&
                part->TryGetNumberField(TEXT("drawOrder"), order) && (order == 0 || order == 2) &&
                ReadLanes(part, TEXT("position"), position) && ReadLanes(part, TEXT("rotation"), rotation) &&
                ReadLanes(part, TEXT("scale"), scale) &&
                part->TryGetArrayField(TEXT("parameters"), parameters) && parameters->Num() == 2)) return false;
            seen.Add(id);
            const auto tag = FName(*id.ToString());
            const auto match = meshes.FindByPredicate([&](const auto* mesh) {
                return mesh->ComponentHasTag(TEXT("LibraSpawnableRenderer")) && mesh->ComponentHasTag(tag);
            });
            if (!TestTrue(TEXT("Expected draw ID identifies a real gameplay mesh"), match != nullptr)) return false;
            const auto mesh = *match;
            if (frameIndex == 0) originalMeshes.Add(id, mesh);
            TestTrue(TEXT("Cell and pose changes retain the same registered renderer"),
                originalMeshes.FindRef(id) == mesh && mesh->IsRegistered() && mesh->IsVisible());
            const auto expectedPosition = FVector(position[0], position[1], position[2]);
            TestTrue(TEXT("Interpolated XY position keeps owner attachment"),
                mesh->GetAttachParent() == root && mesh->GetRelativeLocation().Equals(expectedPosition, 0.001) &&
                mesh->GetComponentLocation().Equals(root->GetComponentTransform().TransformPosition(expectedPosition), 0.001));
            TestTrue(TEXT("Centered plane scale follows the selected cell's size and keyed scale"),
                mesh->GetRelativeScale3D().Equals(FVector(scale[0], scale[1], scale[2]), 0.001));
            TestTrue(TEXT("Z rotation follows interpolated or held pose"),
                mesh->GetRelativeRotation().Quaternion().Equals(FRotator(rotation[1], rotation[2], rotation[0]).Quaternion(), 0.0001));
            TestEqual(TEXT("Layer ordering uses real Unreal translucency sort priority"),
                mesh->TranslucencySortPriority, static_cast<int32>(order));
            double atlasScale[4]{}, atlasOffset[4]{};
            if (!TestTrue(TEXT("Valid expected atlas rectangle"),
                ReadLanes(AsFixtureObject((*parameters)[0]), TEXT("value"), atlasScale) &&
                ReadLanes(AsFixtureObject((*parameters)[1]), TEXT("value"), atlasOffset))) return false;
            const auto material = Cast<UMaterialInstanceDynamic>(mesh->GetMaterial(0));
            if (!TestNotNull(TEXT("Runtime sprite MID"), material)) return false;
            TestTrue(TEXT("Runtime atlas selects, holds and wraps the correct top-left-origin cell"),
                material->K2_GetVectorParameterValue(TEXT("AtlasRect")).Equals(FLinearColor(
                    static_cast<float>(atlasOffset[0]), static_cast<float>(atlasOffset[1]),
                    static_cast<float>(atlasScale[0]), static_cast<float>(atlasScale[1])), 0.0001f));
            auto actualTexture = static_cast<UTexture*>(nullptr);
            TestTrue(TEXT("Runtime binds the fixture texture"),
                material->GetTextureParameterValue(FMaterialParameterInfo(TEXT("Texture")), actualTexture) &&
                actualTexture == texture);
        }
    }
    component->Stop();
    TestFalse(TEXT("Public Stop ends package playback"), component->bIsPlaying);
    TestEqual(TEXT("Public Stop releases all owned render objects"), component->GetRenderObjectCount(), 0);
    auto remaining = TArray<UStaticMeshComponent*>{};
    actor->GetComponents(remaining);
    TestEqual(TEXT("Neither gameplay mesh remains on the owner"), remaining.Num(), 0);
    TestTrue(TEXT("Owner root survives package cleanup"), actor->GetRootComponent() == root && root->IsRegistered());
    return true;
}

#endif
