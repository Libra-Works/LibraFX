#if WITH_DEV_AUTOMATION_TESTS
#include "LibraEffectComponent.h"
#include "LibraSpawnableAsset.h"
#include "Components/SceneCaptureComponent2D.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/SceneCapture2D.h"
#include "Engine/TextureRenderTarget2D.h"
#include "Engine/World.h"
#include "ImageUtils.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "RenderingThread.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraBillboardWorldTest,
    "Libra.Runtime.Matrix.WorldBillboards",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraBillboardWorldTest::RunTest(const FString&)
{
    auto fixture = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraBillboardFixture="), fixture) || fixture.IsEmpty())
    {
        AddInfo(TEXT("WorldBillboards requires an isolated compiler-produced moving-camera.lfxp."));
        return true;
    }
    auto bytes = TArray<uint8>{};
    auto text = FString{};
    auto expected = TSharedPtr<FJsonObject>{};
    if (!TestTrue(TEXT("Compiler package and expected poses load"),
        FFileHelper::LoadFileToArray(bytes, *fixture) &&
        FFileHelper::LoadFileToString(text, *FPaths::ChangeExtension(fixture, TEXT("expected.json"))) &&
        FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(text), expected))) return false;
    auto package = NewObject<ULibraSpawnableAsset>();
    if (!TestEqual(TEXT("Billboard package validates"), package->SetPackageBytes(bytes), ELibraRuntimeResult::Ok)) return false;
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, FName(TEXT("LibraBillboardWorld")),
        nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated Billboard world"), world)) return false;
    auto actor = world->SpawnActor<AActor>();
    auto component = NewObject<ULibraEffectComponent>(actor);
    component->bAutoPlay = false;
    component->Package = package;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();
    component->SetComponentTickEnabled(false);
    TestEqual(TEXT("World Billboards start"), component->Play(), ELibraRuntimeResult::Ok);
    auto capture = world->SpawnActor<ASceneCapture2D>();
    auto target = NewObject<UTextureRenderTarget2D>(capture);
    target->InitAutoFormat(640, 480);
    auto camera = capture->GetCaptureComponent2D();
    camera->TextureTarget = target;
    camera->bCaptureEveryFrame = false;
    camera->bCaptureOnMovement = false;
    camera->ShowFlags.SetLighting(false);
    camera->ShowFlags.SetAtmosphere(false);
    camera->ShowFlags.SetFog(false);
    camera->PrimitiveRenderMode = ESceneCapturePrimitiveRenderMode::PRM_UseShowOnlyList;
    capture->SetActorLocation(FVector(300, -450, 300));
    capture->SetActorRotation((-capture->GetActorLocation()).Rotation());
    auto previous = 0.0;
    auto vector = [](const TSharedPtr<FJsonObject>& object, const TCHAR* key)
    {
        const auto& values = object->GetArrayField(key);
        return FVector(values[0]->AsNumber(), values[1]->AsNumber(), values[2]->AsNumber());
    };
    for (const auto& frameValue : expected->GetArrayField(TEXT("frames")))
    {
        const auto& frame = frameValue->AsObject();
        auto time = frame->GetNumberField(TEXT("timeSeconds"));
        if (time > previous)
            component->TickComponent(static_cast<float>(time - previous), LEVELTICK_All, nullptr);
        previous = time;
        TestFalse(TEXT("Billboards add no implicit lifetime"), component->bRootFinished);
        auto meshes = TArray<UStaticMeshComponent*>{};
        actor->GetComponents(meshes);
        TestEqual(TEXT("Ordinary Groups and ordered Transform produce actual render components"), meshes.Num(),
            frame->GetArrayField(TEXT("nodes")).Num());
        for (const auto& poseValue : frame->GetArrayField(TEXT("nodes")))
        {
            const auto& pose = poseValue->AsObject();
            auto id = FGuid{};
            FGuid::Parse(pose->GetStringField(TEXT("nodeId")), id);
            auto mesh = meshes.FindByPredicate([&](const auto* candidate)
                { return candidate->ComponentHasTag(FName(*id.ToString())); });
            if (!TestTrue(TEXT("The authored draw identity reaches its component"), mesh != nullptr)) continue;
            TestTrue(TEXT("World position matches compiler expectation"),
                (*mesh)->GetRelativeLocation().Equals(vector(pose, TEXT("position")), 1e-4));
            TestTrue(TEXT("Mesh-local scale matches compiler expectation"),
                (*mesh)->GetRelativeScale3D().Equals(vector(pose, TEXT("scale")), 1e-4));
            auto forward = (*mesh)->GetRelativeRotation().RotateVector(FVector::ForwardVector);
            TestTrue(TEXT("Facing agrees with explicit moving camera, without double rotation"),
                FVector::DotProduct(forward, vector(pose, TEXT("forward"))) > 0.99999);
            TestTrue(TEXT("Rendered Up and roll agree with the native world matrix"), FVector::DotProduct(
                (*mesh)->GetRelativeRotation().RotateVector(FVector::UpVector), vector(pose, TEXT("up"))) > 0.99999);
            if (pose->GetBoolField(TEXT("tree")))
                TestTrue(TEXT("Tree preserves World Up"), FVector::DotProduct(
                    (*mesh)->GetRelativeRotation().RotateVector(FVector::UpVector), FVector::UpVector) > 0.99999);
        }
        if (FParse::Param(FCommandLine::Get(), TEXT("LibraCaptureBillboards")))
        {
            camera->ClearShowOnlyComponents();
            for (auto mesh : meshes) camera->ShowOnlyComponent(mesh);
            world->SendAllEndOfFrameUpdates();
            camera->CaptureScene();
            FlushRenderingCommands();
            auto pixels = TArray<FColor>{};
            TestTrue(TEXT("The actual engine render target is readable"),
                target->GameThread_GetRenderTargetResource()->ReadPixels(pixels));
            TestTrue(TEXT("Actual rendering contains Billboard meshes"), pixels.ContainsByPredicate(
                [](const auto& pixel) { return pixel.R > 16 || pixel.G > 16 || pixel.B > 16; }));
            auto png = TArray64<uint8>{};
            FImageUtils::PNGCompressImageArray(640, 480, TArrayView64<const FColor>(pixels.GetData(), pixels.Num()), png);
            auto filename = FPaths::GetPath(fixture) / FString::Printf(TEXT("unreal-billboard-%.1f.png"), time);
            TestTrue(TEXT("Rendered evidence saves in the isolated fixture directory"), FFileHelper::SaveArrayToFile(png, *filename));
        }
    }
    component->Stop();
    world->DestroyWorld(false);
    return !HasAnyErrors();
}
#endif
