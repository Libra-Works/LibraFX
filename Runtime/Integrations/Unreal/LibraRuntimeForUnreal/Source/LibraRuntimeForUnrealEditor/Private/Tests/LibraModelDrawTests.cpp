#if WITH_DEV_AUTOMATION_TESTS
#include "LibraEffectActor.h"
#include "LibraEffectComponent.h"
#include "LibraSpawnableAsset.h"
#include "AssetCompilingManager.h"
#include "Async/TaskGraphInterfaces.h"
#include "Components/SceneCaptureComponent2D.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/Engine.h"
#include "Engine/SceneCapture2D.h"
#include "Engine/StaticMesh.h"
#include "Engine/TextureRenderTarget2D.h"
#include "Engine/World.h"
#include "Editor.h"
#include "EngineUtils.h"
#include "FileHelpers.h"
#include "GameFramework/PlayerStart.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformTime.h"
#include "ImageUtils.h"
#include "Materials/Material.h"
#include "Materials/MaterialExpressionVectorParameter.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "RenderingThread.h"
#include "ShaderCompiler.h"
#include "Tests/AutomationCommon.h"
#include "Tests/AutomationEditorCommon.h"
#include "UObject/SavePackage.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraModelDrawTest, "Libra.Editor.ModelDraw.CubePackage",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraModelDrawTest::RunTest(const FString& parameters)
{
    auto directory = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraModelDrawFixture="), directory) ||
        FPaths::IsRelative(directory))
    {
        AddError(TEXT("Supply -LibraModelDrawFixture=<absolute compiler-produced fixture directory>."));
        return false;
    }
    auto saveDemo = FParse::Param(FCommandLine::Get(), TEXT("LibraModelDrawSaveDemo"));
    auto materialAnimation = FParse::Param(FCommandLine::Get(), TEXT("LibraModelDrawMaterialAnimation"));
    auto folder = FString(materialAnimation ? TEXT("/Game/LiberaFxMaterialSample/") : TEXT("/Game/LiberaFxModelDrawSample/"));
    auto name = FString(materialAnimation ? TEXT("MaterialPulse") : TEXT("Cube"));
    auto materialName = FString(materialAnimation ? TEXT("PulseMaterial") : TEXT("CubeMaterial"));
    if (saveDemo)
    {
        auto expectedProject = FString{};
        if (!FParse::Value(FCommandLine::Get(), TEXT("LibraModelDrawVerificationProject="), expectedProject) ||
            FPaths::IsRelative(expectedProject) || !FPaths::IsSamePath(expectedProject, FPaths::GetProjectFilePath()))
        {
            AddError(TEXT("Saving a demo requires the explicitly named isolated verification project."));
            return false;
        }
        for (const auto& assetName : {name + TEXT("Package"), materialName, name})
        {
            auto path = folder + assetName;
            auto extension = assetName == name ?
                FPackageName::GetMapPackageExtension() : FPackageName::GetAssetPackageExtension();
            if (IFileManager::Get().FileExists(*FPackageName::LongPackageNameToFilename(path, extension)))
            {
                AddError(TEXT("Refusing to overwrite an existing ModelDraw sample."));
                return false;
            }
        }
    }
    auto bytes = TArray<uint8>{};
    auto source = FPaths::Combine(directory, name + TEXT(".lfxp"));
    if (!TestTrue(TEXT("Bounded compiler-produced Cube package"),
        IFileManager::Get().FileSize(*source) > 0 && IFileManager::Get().FileSize(*source) <= 1024 * 1024 &&
        FFileHelper::LoadFileToArray(bytes, *source))) return false;
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, NAME_None, nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated model rendering world"), world)) return false;
    GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(world);
    ON_SCOPE_EXIT
    {
        world->DestroyWorld(false);
        GEngine->DestroyWorldContext(world);
    };
    auto actor = world->SpawnActor<ALibraEffectActor>();
    auto component = actor->EffectComponent;
    component->bAutoPlay = false;
    auto asset = NewObject<ULibraSpawnableAsset>(actor);
    if (!TestEqual(TEXT("Compiled model package validates"), asset->SetPackageBytes(bytes), ELibraRuntimeResult::Ok))
        return false;
    component->Package = asset;
    auto materialOuter = saveDemo
        ? static_cast<UObject*>(CreatePackage(*(folder + materialName)))
        : static_cast<UObject*>(actor);
    auto material = NewObject<UMaterial>(materialOuter, saveDemo ? FName(*materialName) : NAME_None,
        saveDemo ? RF_Public | RF_Standalone : RF_Transient);
    material->SetShadingModel(MSM_Unlit);
    auto color = NewObject<UMaterialExpressionVectorParameter>(material);
    color->ParameterName = TEXT("Color");
    color->DefaultValue = FLinearColor::White;
    material->GetExpressionCollection().AddExpression(color);
    material->GetEditorOnlyData()->EmissiveColor.Connect(0, color);
    material->PostEditChange();
    auto cube = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cube.Cube"));
    if (!TestNotNull(TEXT("Actual engine Cube"), cube)) return false;
    if (!materialAnimation) component->AssetBindings.Add({TEXT("/Game/LiberaFxSamples/Cube.Cube"), cube});
    auto materialKey = materialAnimation
        ? TEXT("/Game/LiberaFxMaterialSample/PulseMaterial.PulseMaterial") : TEXT("builtin:unlit-color");
    component->AssetBindings.Add({materialKey, material});
    if (!TestEqual(*FString::Printf(TEXT("Public effect component plays authored model: %s"), *component->LastError),
        component->Play(), ELibraRuntimeResult::Ok)) return false;
    component->TickComponent(0, LEVELTICK_All, nullptr);
    auto meshes = TArray<UStaticMeshComponent*>{};
    actor->GetComponents(meshes);
    if (!TestEqual(TEXT("Exactly one real model renderer"), meshes.Num(), 1)) return false;
    TestTrue(TEXT("Authored mesh"), meshes[0]->GetStaticMesh() == cube);
    TestTrue(TEXT("Authored scale"), meshes[0]->GetRelativeScale3D().Equals(FVector(1, 1.5, 0.75)));
    TestTrue(TEXT("Authored rotation"),
        meshes[0]->GetRelativeRotation().Quaternion().Equals(FRotator(30, 0, 15).Quaternion()));
    auto dynamic = Cast<UMaterialInstanceDynamic>(meshes[0]->GetMaterial(0));
    if (!TestNotNull(TEXT("Real instance material"), dynamic)) return false;
    TestTrue(TEXT("Compiled FloatX4 drives the exposed material color"),
        dynamic->K2_GetVectorParameterValue(TEXT("Color")).Equals(
            materialAnimation ? FLinearColor::White : FLinearColor(0.15f, 0.65f, 1, 1)));
    if (GShaderCompilingManager) GShaderCompilingManager->FinishAllCompilation();
    auto capture = world->SpawnActor<ASceneCapture2D>();
    auto camera = capture->GetCaptureComponent2D();
    auto target = NewObject<UTextureRenderTarget2D>(capture);
    target->ClearColor = FLinearColor::Black;
    target->InitCustomFormat(256, 256, PF_FloatRGBA, true);
    target->UpdateResourceImmediate();
    camera->TextureTarget = target;
    camera->bCaptureEveryFrame = false;
    camera->bCaptureOnMovement = false;
    camera->CaptureSource = ESceneCaptureSource::SCS_SceneColorHDR;
    camera->ShowFlags.SetTemporalAA(false);
    camera->ShowFlags.SetFog(false);
    camera->ShowFlags.SetAtmosphere(false);
    camera->PrimitiveRenderMode = ESceneCapturePrimitiveRenderMode::PRM_UseShowOnlyList;
    camera->ShowOnlyComponent(meshes[0]);
    auto viewLocation = FVector(-300, -400, 200);
    capture->SetActorLocation(viewLocation);
    capture->SetActorRotation((-viewLocation).Rotation());
    auto captureFrame = [&](int time)
    {
        auto linearPixels = TArray<FFloat16Color>{};
        auto deadline = FPlatformTime::Seconds() + 5;
        auto read = false;
        do
        {
            FAssetCompilingManager::Get().ProcessAsyncTasks(true);
            FTaskGraphInterface::Get().ProcessThreadUntilIdle(ENamedThreads::GameThread);
            if (GShaderCompilingManager) GShaderCompilingManager->ProcessAsyncResults(false, false);
            world->SendAllEndOfFrameUpdates();
            FlushRenderingCommands();
            if (!meshes[0]->IsCompiling() && !meshes[0]->IsPSOPrecaching() && meshes[0]->GetSceneProxy())
            {
                camera->CaptureScene();
                FlushRenderingCommands();
                read = target->GameThread_GetRenderTargetResource()->ReadFloat16Pixels(linearPixels);
                if (read && linearPixels.Num() == 256 * 256 && linearPixels[128 * 256 + 128].B.GetFloat() > 0.5f)
                    break;
            }
            FPlatformProcess::Sleep(0.01f);
        } while (FPlatformTime::Seconds() < deadline);
        if (!TestTrue(TEXT("Read actual camera pixels after mesh/PSO readiness"), read) ||
            !TestEqual(TEXT("Camera dimensions"), linearPixels.Num(), 256 * 256)) return false;
        auto pixels = TArray<FColor>{};
        pixels.Reserve(linearPixels.Num());
        for (const auto& pixel : linearPixels)
            pixels.Add(FLinearColor(pixel.R.GetFloat(), pixel.G.GetFloat(), pixel.B.GetFloat(), pixel.A.GetFloat()).ToFColor(true));
        auto center = pixels[128 * 256 + 128];
        auto png = TArray64<uint8>{};
        FImageUtils::PNGCompressImageArray(256, 256, pixels, png);
        auto filename = name + TEXT(".Unreal") + (materialAnimation ? FString::Printf(TEXT(".%d"), time) : TEXT("")) + TEXT(".png");
        TestTrue(TEXT("Save local render evidence"),
            FFileHelper::SaveArrayToFile(png, *FPaths::Combine(directory, filename)));
        auto expectedPixel = materialAnimation
            ? time == 2 ? center.R < 20 && center.G < 20
                : time == 1 ? center.R > 64 && center.R < center.B * 0.95
                : center.R > 180 && center.G > 180
            : center.G > center.R * 1.2 && center.B > center.R * 1.3;
        return TestTrue(*FString::Printf(TEXT("Camera renders graph color at %d (RGB %d %d %d)"),
            time, center.R, center.G, center.B), center.B > 80 && expectedPixel);
    };
    if (materialAnimation)
    {
        auto previous = 0;
        for (auto time : {0, 1, 2, 4})
        {
            component->TickComponent(static_cast<float>(time - previous), LEVELTICK_All, nullptr);
            previous = time;
            auto currentMeshes = TArray<UStaticMeshComponent*>{};
            actor->GetComponents(currentMeshes);
            if (!TestTrue(TEXT("Forward animation retains its mesh and material instance"),
                currentMeshes.Num() == 1 && currentMeshes[0] == meshes[0] &&
                meshes[0]->GetMaterial(0) == dynamic && component->LastResult == ELibraRuntimeResult::Ok)) return false;
            auto expected = static_cast<float>(0.5 + 0.5 * FMath::Cos(UE_DOUBLE_PI / 2 * time));
            if (!TestTrue(TEXT("Native time and Lerp drive white-blue-white material color"),
                dynamic->K2_GetVectorParameterValue(TEXT("Color")).Equals(FLinearColor(expected, expected, 1, 1), 0.001f)) ||
                !captureFrame(time)) return false;
        }
    }
    else if (!captureFrame(0)) return false;
    component->ForceTerminate();
    TestEqual(TEXT("Stopping releases model objects"), component->GetRenderObjectCount(), 0);
    if (saveDemo)
    {
        auto packageOuter = CreatePackage(*(folder + name + TEXT("Package")));
        auto savedAsset = NewObject<ULibraSpawnableAsset>(packageOuter, FName(*(name + TEXT("Package"))), RF_Public | RF_Standalone);
        if (!TestEqual(TEXT("Persistent package validates"), savedAsset->SetPackageBytes(bytes), ELibraRuntimeResult::Ok))
            return false;
        auto saveAsset = [](UPackage* outer, UObject* object)
        {
            auto filename = FPackageName::LongPackageNameToFilename(outer->GetName(),
                FPackageName::GetAssetPackageExtension());
            IFileManager::Get().MakeDirectory(*FPaths::GetPath(filename), true);
            auto args = FSavePackageArgs{};
            args.TopLevelFlags = RF_Public | RF_Standalone;
            args.SaveFlags = SAVE_NoError;
            return UPackage::SavePackage(outer, object, *filename, args);
        };
        if (!TestTrue(TEXT("Save model package"), saveAsset(packageOuter, savedAsset)) ||
            !TestTrue(TEXT("Save model material"), saveAsset(CastChecked<UPackage>(materialOuter), material))) return false;
        auto editorWorld = UEditorLoadingAndSavingUtils::NewBlankMap(false);
        if (!TestNotNull(TEXT("Owned demonstration map"), editorWorld)) return false;
        auto demoActor = editorWorld->SpawnActor<ALibraEffectActor>();
        demoActor->SetActorLabel(TEXT("Compiled ModelDraw ") + name);
        demoActor->EffectComponent->Package = savedAsset;
        if (!materialAnimation) demoActor->EffectComponent->AssetBindings.Add({TEXT("/Game/LiberaFxSamples/Cube.Cube"), cube});
        demoActor->EffectComponent->AssetBindings.Add({materialKey, material});
        auto start = editorWorld->SpawnActor<APlayerStart>();
        start->SetActorLocation(viewLocation);
        start->SetActorRotation((-viewLocation).Rotation());
        TestTrue(TEXT("Save reproducible authored model map"),
            UEditorLoadingAndSavingUtils::SaveMap(editorWorld, folder + name));
    }
    return true;
}

DEFINE_LATENT_AUTOMATION_COMMAND_ONE_PARAMETER(FLibraVerifySavedModelPlay, FAutomationTestBase*, test);

bool FLibraVerifySavedModelPlay::Update()
{
    auto context = GEditor->GetPIEWorldContext();
    if (!test->TestNotNull(TEXT("Saved Cube map starts a real Play session"), context)) return true;
    auto actors = 0;
    for (TActorIterator<ALibraEffectActor> it(context->World()); it; ++it)
    {
        ++actors;
        auto component = it->EffectComponent;
        test->TestTrue(TEXT("Saved public effect component auto-plays"), component->bAutoPlay && component->bIsPlaying);
        test->TestEqual(TEXT("Saved embedded package evaluates"), component->LastResult, ELibraRuntimeResult::Ok);
        test->TestEqual(TEXT("Saved map recreates the model"), component->GetRenderObjectCount(), 1);
        auto meshes = TArray<UStaticMeshComponent*>{};
        it->GetComponents(meshes);
        if (!test->TestEqual(TEXT("One actual saved-scene model"), meshes.Num(), 1)) continue;
        test->TestTrue(TEXT("Saved mesh binding references the engine asset"),
            meshes[0]->GetStaticMesh() &&
            meshes[0]->GetStaticMesh()->GetPathName() == TEXT("/Engine/BasicShapes/Cube.Cube"));
        auto dynamic = Cast<UMaterialInstanceDynamic>(meshes[0]->GetMaterial(0));
        if (test->TestNotNull(TEXT("Saved material instantiates"), dynamic))
        {
            auto materialAnimation = FParse::Param(FCommandLine::Get(), TEXT("LibraModelDrawMaterialAnimation"));
            auto value = static_cast<float>(0.5 + 0.5 * FMath::Cos(UE_DOUBLE_PI / 2 * component->LastEvaluatedTimeSeconds));
            auto expected = materialAnimation ? FLinearColor(value, value, 1, 1) : FLinearColor(0.15f, 0.65f, 1, 1);
            test->TestTrue(TEXT("Saved material retains compiled graph color and clock"),
                dynamic->K2_GetVectorParameterValue(TEXT("Color")).Equals(expected, 0.001f));
        }
    }
    test->TestEqual(TEXT("One public effect actor"), actors, 1);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraSavedModelDrawTest, "Libra.Editor.ModelDraw.SavedScenePlay",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraSavedModelDrawTest::RunTest(const FString& parameters)
{
    auto expectedProject = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraModelDrawVerificationProject="), expectedProject) ||
        FPaths::IsRelative(expectedProject) || !FPaths::IsSamePath(expectedProject, FPaths::GetProjectFilePath()))
    {
        AddError(TEXT("Loading the saved demo requires the explicitly named isolated verification project."));
        return false;
    }
    auto materialAnimation = FParse::Param(FCommandLine::Get(), TEXT("LibraModelDrawMaterialAnimation"));
    auto filename = FPackageName::LongPackageNameToFilename(materialAnimation
        ? TEXT("/Game/LiberaFxMaterialSample/MaterialPulse") : TEXT("/Game/LiberaFxModelDrawSample/Cube"),
        FPackageName::GetMapPackageExtension());
    if (!TestNotNull(TEXT("Saved Cube map reloads independently of the source fixture"),
        UEditorLoadingAndSavingUtils::LoadMap(filename))) return false;
    ADD_LATENT_AUTOMATION_COMMAND(FStartPIECommand(false));
    ADD_LATENT_AUTOMATION_COMMAND(FWaitLatentCommand(1.0f));
    ADD_LATENT_AUTOMATION_COMMAND(FLibraVerifySavedModelPlay(this));
    ADD_LATENT_AUTOMATION_COMMAND(FEndPlayMapCommand());
    return true;
}
#endif
