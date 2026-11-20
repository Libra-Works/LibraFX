#if WITH_DEV_AUTOMATION_TESTS && WITH_EDITOR

#include "LibraEffectComponent.h"
#include "LibraNativeRenderPrimitives.h"
#include "LibraSpawnableAsset.h"
#include "LibraSpawnableComponent.h"
#include "Camera/CameraComponent.h"
#include "Components/SceneCaptureComponent2D.h"
#include "Components/SceneComponent.h"
#include "Engine/Engine.h"
#include "Engine/SceneCapture2D.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "Engine/TextureRenderTarget2D.h"
#include "Engine/World.h"
#include "DynamicRHI.h"
#include "ImageUtils.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "Materials/Material.h"
#include "Materials/MaterialExpressionMultiply.h"
#include "Materials/MaterialExpressionPerInstanceCustomData.h"
#include "Materials/MaterialExpressionTextureSampleParameter2D.h"
#include "Materials/MaterialExpressionVertexColor.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "RHI.h"
#include "RenderingThread.h"
#include "ShaderCompiler.h"
#include "HAL/PlatformTime.h"

namespace LibraNativeRenderRhiTests
{
struct FSceneCapture
{
    ASceneCapture2D* Actor = nullptr;
    UTextureRenderTarget2D* Target = nullptr;
    USceneCaptureComponent2D* Component = nullptr;
};

auto CreateWorld(FAutomationTestBase& test, const TCHAR* name) -> UWorld*
{
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto* world = UWorld::CreateWorld(EWorldType::Game, false, FName(name), nullptr,
        true, ERHIFeatureLevel::Num, &init);
    if (!test.TestNotNull(TEXT("Isolated real-RHI world"), world))
    {
        return nullptr;
    }
    if (GEngine)
    {
        GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(world);
    }
    return world;
}

void DestroyWorld(UWorld*& world)
{
    if (world)
    {
        world->DestroyWorld(false);
        if (GEngine)
        {
            GEngine->DestroyWorldContext(world);
        }
        world = nullptr;
    }
}

auto CreateCapture(UWorld& world, const FVector& center, double distance) -> FSceneCapture
{
    auto capture = FSceneCapture{};
    capture.Actor = world.SpawnActor<ASceneCapture2D>();
    if (!capture.Actor)
    {
        return capture;
    }
    capture.Target = NewObject<UTextureRenderTarget2D>(capture.Actor);
    capture.Target->ClearColor = FLinearColor::Black;
    capture.Target->InitCustomFormat(256, 256, PF_B8G8R8A8, false);
    capture.Target->UpdateResourceImmediate(true);
    capture.Component = capture.Actor->GetCaptureComponent2D();
    capture.Component->TextureTarget = capture.Target;
    capture.Component->bCaptureEveryFrame = false;
    capture.Component->bCaptureOnMovement = false;
    capture.Component->CaptureSource = ESceneCaptureSource::SCS_FinalColorLDR;
    capture.Component->FOVAngle = 90.0f;
    capture.Component->bOverride_CustomNearClippingPlane = true;
    capture.Component->CustomNearClippingPlane = 0.01f;
    capture.Component->ShowFlags.SetLighting(false);
    capture.Component->ShowFlags.SetTranslucency(true);
    capture.Component->ShowFlags.SetAtmosphere(false);
    capture.Component->ShowFlags.SetFog(false);
    capture.Component->ShowFlags.SetPostProcessing(false);
    const auto location = center + FVector(0.0, -distance, 0.0);
    capture.Actor->SetActorLocationAndRotation(location, (center - location).Rotation());
    return capture;
}

auto CapturePixels(UWorld& world, FSceneCapture& capture, TArray<FColor>& pixels,
    bool updateWorld = true) -> bool
{
    if (!capture.Component || !capture.Target)
    {
        return false;
    }
    if (updateWorld)
    {
        world.SendAllEndOfFrameUpdates();
    }
    FlushRenderingCommands();
    UE_LOG(LogTemp, Warning, TEXT("Libra RHI acceptance: scene capture pre-render flush completed"));
    capture.Component->CaptureScene();
    UE_LOG(LogTemp, Warning, TEXT("Libra RHI acceptance: CaptureScene returned"));
    FlushRenderingCommands();
    UE_LOG(LogTemp, Warning, TEXT("Libra RHI acceptance: scene capture render flush completed"));
    pixels.Reset();
    const auto pixelsRead = capture.Target->GameThread_GetRenderTargetResource()->ReadPixels(pixels);
    UE_LOG(LogTemp, Warning, TEXT("Libra RHI acceptance: render-target pixels read"));
    return pixelsRead &&
        pixels.Num() == 256 * 256;
}

auto SavePixels(const FString& outputDirectory, const TCHAR* name,
    const TArray<FColor>& pixels) -> bool
{
    if (outputDirectory.IsEmpty())
    {
        return true;
    }
    IFileManager::Get().MakeDirectory(*outputDirectory, true);
    auto png = TArray64<uint8>{};
    FImageUtils::PNGCompressImageArray(256, 256,
        TArrayView64<const FColor>(pixels.GetData(), pixels.Num()), png);
    return FFileHelper::SaveArrayToFile(png, *FPaths::Combine(outputDirectory, name));
}

auto HasRedPixels(const TArray<FColor>& pixels) -> bool
{
    return pixels.ContainsByPredicate([](const FColor& pixel)
    {
        return pixel.R > 50 && pixel.R > pixel.G * 1.5f && pixel.R > pixel.B * 1.5f;
    });
}

auto HasGreenPixels(const TArray<FColor>& pixels) -> bool
{
    return pixels.ContainsByPredicate([](const FColor& pixel)
    {
        return pixel.G > 50 && pixel.G > pixel.R * 1.5f && pixel.G > pixel.B * 1.5f;
    });
}

auto HasAnyBrightPixels(const TArray<FColor>& pixels) -> bool
{
    return pixels.ContainsByPredicate([](const FColor& pixel)
    {
        return pixel.R > 35 || pixel.G > 35 || pixel.B > 35;
    });
}

auto WaitForShaderCompilation(FAutomationTestBase& test, const TCHAR* label) -> bool
{
    constexpr auto timeoutSeconds = 120.0;
    const auto deadline = FPlatformTime::Seconds() + timeoutSeconds;
    while (GShaderCompilingManager && GShaderCompilingManager->IsCompiling() &&
        FPlatformTime::Seconds() < deadline)
    {
        GShaderCompilingManager->ProcessAsyncResults(0.01f, false);
        FPlatformProcess::Sleep(0.01f);
    }
    const auto complete = GShaderCompilingManager && !GShaderCompilingManager->IsCompiling();
    return test.TestTrue(FString::Printf(TEXT("%s shaders compile within %.0f seconds"),
        label, timeoutSeconds), complete);
}

auto CreateTexture(UObject& outer) -> UTexture2D*
{
    auto* texture = UTexture2D::CreateTransient(1, 1, PF_B8G8R8A8);
    if (!texture)
    {
        return nullptr;
    }
    texture->AddToRoot();
    texture->SRGB = false;
    auto& mip = texture->GetPlatformData()->Mips[0];
    auto* pixel = static_cast<uint8*>(mip.BulkData.Lock(LOCK_READ_WRITE));
    if (!pixel)
    {
        texture->RemoveFromRoot();
        return nullptr;
    }
    pixel[0] = 255;
    pixel[1] = 255;
    pixel[2] = 255;
    pixel[3] = 255;
    mip.BulkData.Unlock();
    texture->UpdateResource();
    texture->Rename(nullptr, &outer);
    return texture;
}

auto CreateCpuSpriteMaterial(UObject& outer, UTexture2D& texture) -> UMaterial*
{
    auto* material = NewObject<UMaterial>(&outer, NAME_None, RF_Transient);
    material->BlendMode = BLEND_Translucent;
    material->SetShadingModel(MSM_Unlit);
    material->TwoSided = true;
    auto needsRecompile = false;
    material->SetMaterialUsage(needsRecompile, MATUSAGE_InstancedStaticMeshes);
    auto* textureParameter = NewObject<UMaterialExpressionTextureSampleParameter2D>(material);
    textureParameter->ParameterName = TEXT("Texture");
    textureParameter->Texture = &texture;
    textureParameter->SamplerType = SAMPLERTYPE_LinearColor;
    material->GetExpressionCollection().AddExpression(textureParameter);
    auto* instanceColor = NewObject<UMaterialExpressionPerInstanceCustomData3Vector>(material);
    instanceColor->DataIndex = 0;
    instanceColor->ConstDefaultValue = FLinearColor::Black;
    material->GetExpressionCollection().AddExpression(instanceColor);
    auto* colorTimesTexture = NewObject<UMaterialExpressionMultiply>(material);
    colorTimesTexture->A.Connect(0, instanceColor);
    colorTimesTexture->B.Connect(0, textureParameter);
    material->GetExpressionCollection().AddExpression(colorTimesTexture);
    auto* instanceAlpha = NewObject<UMaterialExpressionPerInstanceCustomData>(material);
    instanceAlpha->DataIndex = 3;
    instanceAlpha->ConstDefaultValue = 0.0f;
    material->GetExpressionCollection().AddExpression(instanceAlpha);
    material->GetEditorOnlyData()->EmissiveColor.Connect(0, colorTimesTexture);
    material->GetEditorOnlyData()->Opacity.Connect(0, instanceAlpha);
    material->PostEditChange();
    material->ForceRecompileForRendering(EMaterialShaderPrecompileMode::Synchronous);
    return material;
}

auto CreateFlexiblePlaneMaterial(UObject& outer, UTexture2D& texture) -> UMaterial*
{
    auto* material = NewObject<UMaterial>(&outer, NAME_None, RF_Transient);
    material->BlendMode = BLEND_Opaque;
    material->SetShadingModel(MSM_Unlit);
    material->TwoSided = true;
    auto* textureParameter = NewObject<UMaterialExpressionTextureSampleParameter2D>(material);
    textureParameter->ParameterName = TEXT("Texture");
    textureParameter->Texture = &texture;
    textureParameter->SamplerType = SAMPLERTYPE_LinearColor;
    material->GetExpressionCollection().AddExpression(textureParameter);
    auto* vertexColor = NewObject<UMaterialExpressionVertexColor>(material);
    material->GetExpressionCollection().AddExpression(vertexColor);
    auto* colorTimesTexture = NewObject<UMaterialExpressionMultiply>(material);
    colorTimesTexture->A.Connect(0, vertexColor);
    colorTimesTexture->B.Connect(0, textureParameter);
    material->GetExpressionCollection().AddExpression(colorTimesTexture);
    material->GetEditorOnlyData()->EmissiveColor.Connect(0, colorTimesTexture);
    material->PostEditChange();
    material->ForceRecompileForRendering(EMaterialShaderPrecompileMode::Synchronous);
    return material;
}

auto CreateEffectActor(UWorld& world, ULibraEffectComponent*& component,
    USceneComponent*& root) -> AActor*
{
    auto* actor = world.SpawnActor<AActor>();
    if (!actor)
    {
        return nullptr;
    }
    root = NewObject<USceneComponent>(actor);
    actor->SetRootComponent(root);
    root->RegisterComponent();
    component = NewObject<ULibraEffectComponent>(actor);
    component->bAutoPlay = false;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();
    return actor;
}

auto RunNativeMeshPrimitiveLifetimeAcceptance(FAutomationTestBase& test,
    const FString& outputDirectory) -> bool
{
    auto* world = CreateWorld(test, TEXT("LibraRhiNativeMeshLifetimeWorld"));
    if (!world)
    {
        return false;
    }
    ON_SCOPE_EXIT
    {
        DestroyWorld(world);
        FlushRenderingCommands();
    };

    auto* actor = world->SpawnActor<AActor>();
    if (!test.TestNotNull(TEXT("Native mesh lifetime actor"), actor))
    {
        return false;
    }
    auto* root = NewObject<USceneComponent>(actor);
    actor->SetRootComponent(root);
    root->RegisterComponent();
    auto* owner = NewObject<ULibraSpawnableComponent>(actor);
    actor->AddInstanceComponent(owner);
    owner->RegisterComponent();
    auto* mesh = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Plane.Plane"));
    auto* material = UMaterial::GetDefaultMaterial(MD_Surface);
    if (!test.TestNotNull(TEXT("Native mesh lifetime owner"), owner) ||
        !test.TestNotNull(TEXT("Native mesh lifetime fixture"), mesh) ||
        !test.TestNotNull(TEXT("Native mesh lifetime material"), material))
    {
        return false;
    }
    auto materials = TArray<TObjectPtr<UMaterialInterface>>{};
    materials.Add(material);
    auto capture = CreateCapture(*world, FVector::ZeroVector, 350.0);
    if (!test.TestNotNull(TEXT("Native mesh lifetime scene capture"), capture.Component))
    {
        return false;
    }

    auto pixels = TArray<FColor>{};
    for (auto cycle = 0; cycle < 3; ++cycle)
    {
        auto primitive = MakeUnique<FLibraNativeMeshPrimitive>();
        const auto transform = FTransform(FQuat(FVector::XAxisVector, UE_HALF_PI),
            FVector::ZeroVector, FVector(cycle == 1 ? -1.0 : 1.0, 1.0, 1.0));
        if (!test.TestTrue(TEXT("One ordinary Draw registers one native mesh primitive"),
            primitive->Create(*owner, mesh, MakeArrayView(materials), transform,
                0, FGuid::NewGuid(), FGuid::NewGuid(), 0)))
        {
            return false;
        }
        if (!test.TestTrue(TEXT("Native mesh primitive renders active pixels"),
                CapturePixels(*world, capture, pixels)) ||
            !test.TestTrue(TEXT("Native mesh active capture is non-clear"),
                HasAnyBrightPixels(pixels)))
        {
            return false;
        }
        if (cycle == 0 && !test.TestTrue(TEXT("Native mesh active pixels are saved"),
            SavePixels(outputDirectory, TEXT("single-draw-active.png"), pixels)))
        {
            return false;
        }

        primitive->Remove();
        FlushRenderingCommands();
        if (!test.TestFalse(TEXT("Removed descriptor no longer exposes its scene proxy"),
            primitive->HasSceneProxy()) ||
            !test.TestTrue(TEXT("Native mesh post-Stop capture is readable"),
                CapturePixels(*world, capture, pixels, false)) ||
            !test.TestTrue(TEXT("Native mesh post-Stop capture is clear"),
                !HasAnyBrightPixels(pixels)))
        {
            return false;
        }
        if (cycle == 0 && !test.TestTrue(TEXT("Native mesh stopped pixels are saved"),
            SavePixels(outputDirectory, TEXT("single-draw-stopped.png"), pixels)))
        {
            return false;
        }
        primitive.Reset();
        UE_LOG(LogTemp, Warning,
            TEXT("Libra RHI acceptance: ordinary native mesh Stop cycle %d completed"), cycle + 1);
    }
    return true;
}

auto RunOpaqueAcceptance(FAutomationTestBase& test, const FString& fixture,
    const FString& outputDirectory) -> bool
{
    auto bytes = TArray<uint8>{};
    if (!test.TestTrue(TEXT("Opaque instancing package loads"),
        FFileHelper::LoadFileToArray(bytes, *fixture)))
    {
        return false;
    }
    auto* world = CreateWorld(test, TEXT("LibraRhiOpaqueWorld"));
    if (!world)
    {
        return false;
    }
    ON_SCOPE_EXIT
    {
        DestroyWorld(world);
        FlushRenderingCommands();
    };
    auto* package = NewObject<ULibraSpawnableAsset>();
    if (!test.TestEqual(TEXT("Opaque fixture package validates"),
        package->SetPackageBytes(bytes), ELibraRuntimeResult::Ok))
    {
        return false;
    }
    auto* actor = static_cast<AActor*>(nullptr);
    auto* root = static_cast<USceneComponent*>(nullptr);
    auto* component = static_cast<ULibraEffectComponent*>(nullptr);
    actor = CreateEffectActor(*world, component, root);
    if (!test.TestNotNull(TEXT("Opaque fixture actor"), actor))
    {
        return false;
    }
    component->Package = package;
    const auto mirrorDrawTransforms = FParse::Param(FCommandLine::Get(), TEXT("LibraRhiMirrorOpaque"));
    if (mirrorDrawTransforms)
    {
        root->SetRelativeScale3D(FVector(-1.0, 1.0, 1.0));
    }
    auto* sourceMesh = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cube.Cube"));
    if (!test.TestNotNull(TEXT("Engine primitive fixture mesh"), sourceMesh))
    {
        return false;
    }
    component->AssetBindings.Add({TEXT("/Engine/BasicShapes/Cube.Cube"), sourceMesh});
    component->AssetBindings.Add({TEXT("/Tests/Transparent"), sourceMesh});
    if (!test.TestEqual(TEXT("Opaque package starts"), component->Play(), ELibraRuntimeResult::Ok))
    {
        return false;
    }
    for (auto tick = 0; tick < 4; ++tick)
    {
        component->TickComponent(0.1f, LEVELTICK_All, nullptr);
    }
    if (!WaitForShaderCompilation(test, TEXT("Opaque instancing")))
    {
        component->Stop();
        return false;
    }
    auto transforms = TArray<FTransform>{};
    auto bounds = TArray<FBoxSphereBounds>{};
    auto drawIds = TArray<FGuid>{};
    auto containerIds = TArray<FGuid>{};
    auto instanceIds = TArray<int64>{};
    component->GetDrawSnapshots(transforms, bounds, drawIds, containerIds, instanceIds);
    auto combinedBounds = FBox(ForceInit);
    for (const auto& bound : bounds)
    {
        combinedBounds += bound.GetBox();
    }
    const auto center = combinedBounds.IsValid ? combinedBounds.GetCenter() : FVector::ZeroVector;
    const auto radius = combinedBounds.IsValid ? combinedBounds.GetExtent().Size() : 100.0;
    auto capture = CreateCapture(*world, center, FMath::Max(300.0, radius * 4.0));
    if (!test.TestNotNull(TEXT("Opaque scene capture"), capture.Component))
    {
        component->Stop();
        return false;
    }
    auto logical = 0, primitives = 0, batches = 0;
    auto cpuBatches = 0, liveCpuParticles = 0, capacity = 0;
    auto submittedBytes = uint64{};
    component->GetNativeRenderSubmissionStats(logical, primitives, batches,
        cpuBatches, liveCpuParticles, capacity, submittedBytes);
    const auto safeBatching = batches == 1 && primitives == 1 && logical > batches;
    const auto mirroredDrawsStaySeparate = mirrorDrawTransforms && batches == 0 &&
        logical > 1 && primitives == logical;
    if (!test.TestTrue(TEXT("Opaque native scene submission matches the transform parity"),
        (safeBatching || mirroredDrawsStaySeparate) && submittedBytes > 0))
    {
        component->Stop();
        return false;
    }
    auto pixels = TArray<FColor>{};
    if (!test.TestTrue(TEXT("D3D11 opaque batch renders to the target"),
        CapturePixels(*world, capture, pixels)))
    {
        component->Stop();
        return false;
    }
    if (!test.TestTrue(TEXT("Rendered opaque instances produce non-clear pixels"),
        HasAnyBrightPixels(pixels)) ||
        !test.TestTrue(TEXT("Opaque render evidence is saved in the isolated directory"),
            SavePixels(outputDirectory, TEXT("opaque-active.png"), pixels)))
    {
        component->Stop();
        return false;
    }
    UE_LOG(LogTemp, Warning, TEXT("Libra RHI acceptance: opaque active capture completed"));
    component->Stop();
    UE_LOG(LogTemp, Warning, TEXT("Libra RHI acceptance: opaque Stop returned"));
    FlushRenderingCommands();
    UE_LOG(LogTemp, Warning, TEXT("Libra RHI acceptance: opaque render flush completed"));
    component->GetNativeRenderSubmissionStats(logical, primitives, batches,
        cpuBatches, liveCpuParticles, capacity, submittedBytes);
    if (!test.TestTrue(TEXT("Stop removes the opaque scene primitive"),
        logical == 0 && primitives == 0 && batches == 0) ||
        !test.TestTrue(TEXT("D3D11 opaque Stop capture is readable"),
            CapturePixels(*world, capture, pixels, false)) ||
        !test.TestTrue(TEXT("Opaque Stop/flush removes the rendered pixels"),
            !HasAnyBrightPixels(pixels)) ||
        !test.TestTrue(TEXT("Opaque stopped evidence is saved"),
            SavePixels(outputDirectory, TEXT("opaque-stopped.png"), pixels)))
    {
        return false;
    }
    UE_LOG(LogTemp, Warning, TEXT("Libra RHI acceptance: opaque stopped capture completed"));
    return true;
}

auto RunCpuSpriteAcceptance(FAutomationTestBase& test, const FString& fixture,
    const FString& outputDirectory) -> bool
{
    const auto nested = FParse::Param(FCommandLine::Get(), TEXT("LibraRhiNestedCpuSprite"));
    auto bytes = TArray<uint8>{};
    if (!test.TestTrue(TEXT("CPU Sprite package loads"),
        FFileHelper::LoadFileToArray(bytes, *fixture)))
    {
        return false;
    }
    auto* world = CreateWorld(test, TEXT("LibraRhiCpuSpriteWorld"));
    if (!world)
    {
        return false;
    }
    ON_SCOPE_EXIT
    {
        DestroyWorld(world);
        FlushRenderingCommands();
    };
    auto* package = NewObject<ULibraSpawnableAsset>();
    if (!test.TestEqual(TEXT("CPU fixture package validates"),
        package->SetPackageBytes(bytes), ELibraRuntimeResult::Ok))
    {
        return false;
    }
    auto* component = static_cast<ULibraEffectComponent*>(nullptr);
    auto* root = static_cast<USceneComponent*>(nullptr);
    auto* actor = CreateEffectActor(*world, component, root);
    if (!test.TestNotNull(TEXT("CPU Sprite actor"), actor))
    {
        return false;
    }
    root->SetWorldScale3D(FVector(100.0));
    component->Package = package;
    auto* camera = NewObject<UCameraComponent>(actor);
    camera->SetupAttachment(root);
    camera->RegisterComponent();
    const auto cameraLocation = FVector(0.0, -500.0, 0.0);
    camera->SetWorldLocationAndRotation(cameraLocation,
        (FVector::ZeroVector - cameraLocation).Rotation());
    component->EffectCameraOverride = camera;
    auto* texture = CreateTexture(*component);
    if (!test.TestNotNull(TEXT("Synthetic CPU Sprite texture"), texture))
    {
        return false;
    }
    auto* material = CreateCpuSpriteMaterial(*component, *texture);
    if (!test.TestNotNull(TEXT("CPU Sprite RGBA custom-data material"), material))
    {
        texture->RemoveFromRoot();
        return false;
    }
    component->AssetBindings.Add({TEXT("builtin:cpu-particle-alpha"), material});
    if (!test.TestEqual(TEXT("CPU particle package starts"), component->Play(), ELibraRuntimeResult::Ok))
    {
        texture->RemoveFromRoot();
        return false;
    }
    const auto ticks = nested ? 30 : 4;
    for (auto tick = 0; tick < ticks; ++tick)
    {
        component->TickComponent(0.1f, LEVELTICK_All, nullptr);
    }
    if (!WaitForShaderCompilation(test, TEXT("CPU Sprite custom data")))
    {
        component->Stop();
        texture->RemoveFromRoot();
        return false;
    }
    auto capture = CreateCapture(*world, FVector::ZeroVector, 500.0);
    if (!test.TestNotNull(TEXT("CPU Sprite scene capture"), capture.Component))
    {
        component->Stop();
        texture->RemoveFromRoot();
        return false;
    }
    auto logical = 0, primitives = 0, batches = 0;
    auto cpuBatches = 0, liveCpuParticles = 0, capacity = 0;
    auto submittedBytes = uint64{};
    component->GetNativeRenderSubmissionStats(logical, primitives, batches,
        cpuBatches, liveCpuParticles, capacity, submittedBytes);
    UE_LOG(LogTemp, Display, TEXT("Libra RHI CPU: live=%d batches=%d primitives=%d capacity=%d stagedBytes=%llu"),
        liveCpuParticles, cpuBatches, primitives, capacity, submittedBytes);
    if (!test.TestTrue(TEXT("CPU Sprite has live native instances and RGBA payload"),
        liveCpuParticles > 0 && cpuBatches == 1 && primitives == 1 &&
        capacity >= liveCpuParticles && submittedBytes > 0 &&
        (!nested || liveCpuParticles == 3)))
    {
        component->Stop();
        texture->RemoveFromRoot();
        return false;
    }
    auto pixels = TArray<FColor>{};
    if (!test.TestTrue(TEXT("D3D11 CPU Sprite render target is readable"),
        CapturePixels(*world, capture, pixels)))
    {
        component->Stop();
        texture->RemoveFromRoot();
        return false;
    }
    SavePixels(outputDirectory, TEXT("cpu-sprite-active.png"), pixels);
    if (!test.TestTrue(TEXT("Per-instance RGB and alpha produce visible CPU Sprite pixels"),
        HasAnyBrightPixels(pixels)))
    {
        component->Stop();
        texture->RemoveFromRoot();
        return false;
    }
    if (FParse::Param(FCommandLine::Get(), TEXT("LibraRhiCpuTinted")))
    {
        auto maxRed = uint8{};
        for (const auto& pixel : pixels)
        {
            maxRed = FMath::Max(maxRed, pixel.R);
        }
        if (!test.TestTrue(TEXT("Authored per-instance RGB tint reaches the pixel shader"),
                HasRedPixels(pixels)) ||
            !test.TestTrue(TEXT("Overlapping half-alpha particles remain below opaque red"),
                maxRed > 100 && maxRed < 250))
        {
            component->Stop();
            texture->RemoveFromRoot();
            return false;
        }
    }
    if (nested)
    {
        for (auto tick = 0; tick < 15; ++tick)
        {
            component->TickComponent(0.1f, LEVELTICK_All, nullptr);
        }
        component->GetNativeRenderSubmissionStats(logical, primitives, batches,
            cpuBatches, liveCpuParticles, capacity, submittedBytes);
        if (!test.TestTrue(TEXT("Particle death naturally retires both nested scopes"),
                logical == 0 && primitives == 0 && cpuBatches == 0) ||
            !test.TestTrue(TEXT("Natural CPU retirement capture is readable"),
                CapturePixels(*world, capture, pixels)) ||
            !test.TestTrue(TEXT("Natural CPU retirement leaves no pixels"),
                !HasAnyBrightPixels(pixels)) ||
            !test.TestEqual(TEXT("Nested CPU restarts after natural completion"),
                component->Play(), ELibraRuntimeResult::Ok))
        {
            component->Stop();
            texture->RemoveFromRoot();
            return false;
        }
        component->TickComponent(0.1f, LEVELTICK_All, nullptr);
        component->GetNativeRenderSubmissionStats(logical, primitives, batches,
            cpuBatches, liveCpuParticles, capacity, submittedBytes);
        if (!test.TestTrue(TEXT("Restart creates one fresh nested CPU batch"),
            cpuBatches == 1 && primitives == 1 && liveCpuParticles == 3))
        {
            component->Stop();
            texture->RemoveFromRoot();
            return false;
        }
    }
    component->Stop();
    FlushRenderingCommands();
    component->GetNativeRenderSubmissionStats(logical, primitives, batches,
        cpuBatches, liveCpuParticles, capacity, submittedBytes);
    if (!test.TestTrue(TEXT("Stop releases CPU Sprite native instances"),
        logical == 0 && primitives == 0 && cpuBatches == 0) ||
        !test.TestTrue(TEXT("CPU Sprite stopped capture is readable"),
            CapturePixels(*world, capture, pixels, false)) ||
        !test.TestTrue(TEXT("CPU Sprite Stop/flush removes all rendered pixels"),
            !HasAnyBrightPixels(pixels)) ||
        !test.TestTrue(TEXT("CPU Sprite stopped evidence is saved"),
            SavePixels(outputDirectory, TEXT("cpu-sprite-stopped.png"), pixels)))
    {
        texture->RemoveFromRoot();
        return false;
    }
    texture->RemoveFromRoot();
    return true;
}

auto RunFlexiblePlaneAcceptance(FAutomationTestBase& test, const FString& fixture,
    const FString& outputDirectory) -> bool
{
    auto bytes = TArray<uint8>{};
    if (!test.TestTrue(TEXT("Fixed-point FlexiblePlane package loads"),
        FFileHelper::LoadFileToArray(bytes, *fixture)))
    {
        return false;
    }
    auto* world = CreateWorld(test, TEXT("LibraRhiFlexiblePlaneWorld"));
    if (!world)
    {
        return false;
    }
    ON_SCOPE_EXIT
    {
        DestroyWorld(world);
        FlushRenderingCommands();
    };
    auto* package = NewObject<ULibraSpawnableAsset>();
    if (!test.TestEqual(TEXT("Fixed-point FlexiblePlane package validates"),
        package->SetPackageBytes(bytes), ELibraRuntimeResult::Ok))
    {
        return false;
    }
    auto* component = static_cast<ULibraEffectComponent*>(nullptr);
    auto* root = static_cast<USceneComponent*>(nullptr);
    auto* actor = CreateEffectActor(*world, component, root);
    if (!test.TestNotNull(TEXT("FlexiblePlane effect actor"), actor))
    {
        return false;
    }
    component->Package = package;
    auto* texture = CreateTexture(*component);
    if (!test.TestNotNull(TEXT("Synthetic FlexiblePlane texture"), texture))
    {
        return false;
    }
    auto* material = CreateFlexiblePlaneMaterial(*component, *texture);
    if (!test.TestNotNull(TEXT("Synthetic FlexiblePlane vertex-color material"), material))
    {
        texture->RemoveFromRoot();
        return false;
    }
    auto* cubeSource = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cube.Cube"));
    if (!test.TestNotNull(TEXT("FlexiblePlane fixture cube binding"), cubeSource))
    {
        texture->RemoveFromRoot();
        return false;
    }
    component->AssetBindings.Add({TEXT("/Game/FX/T_Ribbon"), texture});
    component->AssetBindings.Add({TEXT("builtin:flexible-plane"), material});
    component->AssetBindings.Add({TEXT("/Engine/BasicShapes/Cube.Cube"), cubeSource});
    if (!test.TestEqual(TEXT("Fixed-point FlexiblePlane effect starts"),
        component->Play(), ELibraRuntimeResult::Ok))
    {
        texture->RemoveFromRoot();
        return false;
    }
    for (auto tick = 0; tick < 4; ++tick)
    {
        component->TickComponent(0.1f, LEVELTICK_All, nullptr);
    }
    if (!WaitForShaderCompilation(test, TEXT("FlexiblePlane vertex colors")))
    {
        component->Stop();
        texture->RemoveFromRoot();
        return false;
    }
    auto capture = CreateCapture(*world, FVector(1.0, 0.0, 0.0), 8.0);
    if (!test.TestNotNull(TEXT("FlexiblePlane scene capture"), capture.Component))
    {
        component->Stop();
        texture->RemoveFromRoot();
        return false;
    }
    capture.Actor->SetActorLocationAndRotation(FVector(1.0, 0.0, 8.0), FRotator(-90.0, 0.0, 0.0));
    auto logical = 0, primitives = 0, batches = 0;
    auto cpuBatches = 0, liveCpuParticles = 0, capacity = 0;
    auto submittedBytes = uint64{};
    component->GetNativeRenderSubmissionStats(logical, primitives, batches,
        cpuBatches, liveCpuParticles, capacity, submittedBytes);
    if (!test.TestTrue(TEXT("FlexiblePlane registers an effect-owned native primitive"),
        logical > 0 && primitives > 0 && submittedBytes > 0))
    {
        component->Stop();
        texture->RemoveFromRoot();
        return false;
    }
    auto pixels = TArray<FColor>{};
    if (!test.TestTrue(TEXT("D3D11 FlexiblePlane render target is readable"),
        CapturePixels(*world, capture, pixels)))
    {
        component->Stop();
        texture->RemoveFromRoot();
        return false;
    }
    if (!test.TestTrue(TEXT("FlexiblePlane render evidence is saved"),
            SavePixels(outputDirectory, TEXT("flexible-plane-active.png"), pixels)) ||
        !test.TestTrue(TEXT("Fixed-point FlexiblePlane vertex colors render red and green"),
            HasRedPixels(pixels) && HasGreenPixels(pixels)))
    {
        component->Stop();
        texture->RemoveFromRoot();
        return false;
    }
    component->Stop();
    FlushRenderingCommands();
    component->GetNativeRenderSubmissionStats(logical, primitives, batches,
        cpuBatches, liveCpuParticles, capacity, submittedBytes);
    if (!test.TestTrue(TEXT("Stop removes the FlexiblePlane native primitive"),
        logical == 0 && primitives == 0) ||
        !test.TestTrue(TEXT("FlexiblePlane stopped capture is readable"),
            CapturePixels(*world, capture, pixels)) ||
        !test.TestTrue(TEXT("FlexiblePlane Stop/flush removes its rendered colors"),
            !HasRedPixels(pixels) && !HasGreenPixels(pixels)) ||
        !test.TestTrue(TEXT("FlexiblePlane stopped evidence is saved"),
            SavePixels(outputDirectory, TEXT("flexible-plane-stopped.png"), pixels)))
    {
        texture->RemoveFromRoot();
        return false;
    }
    texture->RemoveFromRoot();
    return true;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraNativeRenderRhiAcceptanceTest,
    "Libra.Runtime.Rendering.RealRhiAcceptance",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraNativeRenderRhiAcceptanceTest::RunTest(const FString&)
{
    auto opaqueFixture = FString{};
    auto cpuFixture = FString{};
    auto flexibleFixture = FString{};
    auto outputDirectory = FString{};
    FParse::Value(FCommandLine::Get(), TEXT("LibraRealRenderOutput="), outputDirectory);
    if (!TestTrue(TEXT("This acceptance requires a real D3D11 RHI, not NullRHI"),
        GDynamicRHI && FString(GDynamicRHI->GetName()).Contains(TEXT("D3D11")) &&
        GMaxRHIFeatureLevel == ERHIFeatureLevel::SM5))
    {
        return false;
    }
    if (!TestTrue(TEXT("NullRHI was not requested"), !FParse::Param(FCommandLine::Get(), TEXT("nullrhi"))))
    {
        return false;
    }
    if (FParse::Param(FCommandLine::Get(), TEXT("LibraRhiPrimitiveLifetimeOnly")))
    {
        return LibraNativeRenderRhiTests::RunNativeMeshPrimitiveLifetimeAcceptance(
            *this, outputDirectory);
    }
    if (FParse::Param(FCommandLine::Get(), TEXT("LibraRhiCpuSpriteOnly")))
    {
        if (!FParse::Value(FCommandLine::Get(), TEXT("LibraCpuParticleFixture="), cpuFixture))
        {
            AddError(TEXT("Supply an isolated CPU Sprite fixture."));
            return false;
        }
        return LibraNativeRenderRhiTests::RunCpuSpriteAcceptance(*this, cpuFixture,
            outputDirectory);
    }
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraOpaqueBatchFixture="), opaqueFixture) ||
        !FParse::Value(FCommandLine::Get(), TEXT("LibraCpuParticleFixture="), cpuFixture) ||
        !FParse::Value(FCommandLine::Get(), TEXT("LibraFlexiblePlaneFixture="), flexibleFixture))
    {
        AddError(TEXT("Supply isolated opaque, CPU Sprite and fixed-point FlexiblePlane fixtures."));
        return false;
    }
    if (!LibraNativeRenderRhiTests::RunOpaqueAcceptance(*this, opaqueFixture, outputDirectory))
    {
        return false;
    }
    if (FParse::Param(FCommandLine::Get(), TEXT("LibraRhiOnlyOrdinary")))
    {
        return true;
    }
    if (!LibraNativeRenderRhiTests::RunCpuSpriteAcceptance(*this, cpuFixture, outputDirectory))
    {
        return false;
    }
    return LibraNativeRenderRhiTests::RunFlexiblePlaneAcceptance(*this,
        flexibleFixture, outputDirectory);
}

#endif
