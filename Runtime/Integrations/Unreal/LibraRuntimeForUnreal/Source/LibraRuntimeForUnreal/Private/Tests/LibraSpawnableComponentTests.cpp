#if WITH_DEV_AUTOMATION_TESTS

#include "LibraSpawnableAsset.h"
#include "LibraEffectComponent.h"
#include "Camera/CameraComponent.h"
#include "Engine/World.h"
#include "Components/SceneComponent.h"
#include "Components/InstancedStaticMeshComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "Materials/Material.h"
#include "Materials/MaterialExpressionTextureSampleParameter2D.h"
#include "Materials/MaterialExpressionVectorParameter.h"
#include "liberafx/LbSpawnablePackage.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "RenderCommandFence.h"
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
    auto root = NewObject<USceneComponent>(actor);
    actor->SetRootComponent(root);
    root->RegisterComponent();

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
    TestEqual(TEXT("Launch uses one native Draw primitive"), component->GetNativeDrawPrimitiveCount(), 1);
    component->TickComponent(1, LEVELTICK_All, nullptr);
    auto meshes = TArray<UStaticMeshComponent*>{};
    actor->GetComponents(meshes);
    TestEqual(TEXT("Ordinary Draw creates no per-Draw mesh component"), meshes.Num(), 0);
    auto transforms = TArray<FTransform>{};
    auto bounds = TArray<FBoxSphereBounds>{};
    auto drawIds = TArray<FGuid>{}, containers = TArray<FGuid>{};
    auto instanceIds = TArray<int64>{};
    component->GetDrawSnapshots(transforms, bounds, drawIds, containers, instanceIds);
    TestEqual(TEXT("One Draw publishes one transform and bound"), transforms.Num(), 1);
    TestTrue(TEXT("Native proxy receives the evaluated child position"),
        transforms.Num() == 1 && FMath::IsNearlyEqual(transforms[0].GetLocation().Z, 140.0));
    TestTrue(TEXT("Native proxy publishes finite nonempty bounds"),
        bounds.Num() == 1 && bounds[0].SphereRadius > 0 && !bounds[0].ContainsNaN());
    auto logicalRenderObjects = 0, nativePrimitives = 0, opaqueBatches = 0;
    auto cpuParticleBatches = 0, liveCpuParticles = 0, allocatedInstanceCapacity = 0;
    auto submittedBytes = uint64{};
    component->GetNativeRenderSubmissionStats(logicalRenderObjects, nativePrimitives, opaqueBatches,
        cpuParticleBatches, liveCpuParticles, allocatedInstanceCapacity, submittedBytes);
    TestTrue(TEXT("Submission diagnostics separate logical objects, primitives and staged bytes"),
        logicalRenderObjects == 1 && nativePrimitives == 1 && opaqueBatches == 0 &&
        cpuParticleBatches == 0 && liveCpuParticles == 0 && submittedBytes > 0);
    component->TickComponent(1, LEVELTICK_All, nullptr);
    TestEqual(TEXT("Launch renderer is replaced by twelve burst renderers"), component->GetRenderObjectCount(), 12);
    TestEqual(TEXT("Twelve Draws use twelve independently sortable native primitives"),
        component->GetNativeDrawPrimitiveCount(), 12);
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
    TestEqual(TEXT("Paused presentation fixture starts"), component->Play(), ELibraRuntimeResult::Ok);
    component->TickComponent(0.1f, LEVELTICK_All, nullptr);
    component->Pause();
    component->GetDrawSnapshots(transforms, bounds, drawIds, containers, instanceIds);
    const auto pausedPosition = transforms.IsEmpty() ? FVector::ZeroVector : transforms[0].GetLocation();
    actor->SetActorLocation(FVector(0, 0, 35));
    component->TickComponent(0, LEVELTICK_All, nullptr);
    component->GetDrawSnapshots(transforms, bounds, drawIds, containers, instanceIds);
    TestTrue(TEXT("Cached presentation follows owner transform while paused"),
        transforms.Num() == 1 && FMath::IsNearlyEqual(
            transforms[0].GetLocation().Z - pausedPosition.Z, 35.0));
    TestTrue(TEXT("Paused presentation retains the native render scope"), component->GetRenderObjectCount() > 0);
    component->Stop();
    actor->SetActorLocation(FVector::ZeroVector);
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
        component->GetDrawSnapshots(transforms, bounds, drawIds, containers, instanceIds);
        TestTrue(TEXT("First instance keeps its own time and values"),
            transforms.ContainsByPredicate([](const auto& transform) { return FMath::IsNearlyEqual(transform.GetLocation().Z, 140.0); }));
        TestTrue(TEXT("Second instance starts at local time zero"),
            transforms.ContainsByPredicate([](const auto& transform) { return FMath::IsNearlyEqual(transform.GetLocation().Z, 80.0); }));
        component->Stop();
        TestEqual(TEXT("Stop releases overlapping instances"), component->LastResult, ELibraRuntimeResult::Ok);
        TestEqual(TEXT("No renderer survives Stop"), component->GetRenderObjectCount(), 0);
    }
    world->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraOpaqueBatchRenderingTest,
    "Libra.Runtime.Rendering.OpaqueBatchCompatibility",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraOpaqueBatchRenderingTest::RunTest(const FString&)
{
    auto fixture = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraOpaqueBatchFixture="), fixture) || fixture.IsEmpty())
    {
        AddInfo(TEXT("OpaqueBatchCompatibility requires the isolated opaque-batch LFXP fixture."));
        return true;
    }

    auto bytes = TArray<uint8>{};
    if (!TestTrue(TEXT("Opaque batch fixture loads"), FFileHelper::LoadFileToArray(bytes, *fixture)))
    {
        return false;
    }
    auto package = NewObject<ULibraSpawnableAsset>();
    if (!TestEqual(TEXT("Opaque batch package validates"),
        package->SetPackageBytes(bytes), ELibraRuntimeResult::Ok))
    {
        return false;
    }

    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false,
        FName(TEXT("LibraOpaqueBatchRenderingWorld")), nullptr, true,
        ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated opaque batch world"), world))
    {
        return false;
    }
    auto actor = world->SpawnActor<AActor>();
    auto root = NewObject<USceneComponent>(actor);
    actor->SetRootComponent(root);
    root->RegisterComponent();
    auto component = NewObject<ULibraEffectComponent>(actor);
    component->bAutoPlay = false;
    component->Package = package;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();

    auto makeMaterial = [&](EBlendMode blendMode)
    {
        auto material = NewObject<UMaterial>(component, NAME_None, RF_Transient);
        material->BlendMode = blendMode;
        material->SetShadingModel(MSM_Unlit);
        auto needsRecompile = false;
        material->SetMaterialUsage(needsRecompile, MATUSAGE_InstancedStaticMeshes);
        material->PostEditChange();
        return material;
    };
    auto makeMesh = [&](UMaterialInterface* material)
    {
        auto* sourceMesh = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cube.Cube"));
        auto* mesh = sourceMesh ? DuplicateObject<UStaticMesh>(sourceMesh, component) : nullptr;
        if (mesh && !mesh->GetStaticMaterials().IsEmpty())
        {
            mesh->GetStaticMaterials()[0].MaterialInterface = material;
        }
        return mesh;
    };
    auto* opaqueMaterial = makeMaterial(BLEND_Opaque);
    auto* transparentMaterial = makeMaterial(BLEND_Translucent);
    auto* opaqueMesh = makeMesh(opaqueMaterial);
    auto* transparentMesh = makeMesh(transparentMaterial);
    if (!TestNotNull(TEXT("Opaque instancing material"), opaqueMaterial) ||
        !TestNotNull(TEXT("Transparent material"), transparentMaterial) ||
        !TestNotNull(TEXT("Opaque cube fixture"), opaqueMesh) ||
        !TestNotNull(TEXT("Transparent cube fixture"), transparentMesh))
    {
        world->DestroyWorld(false);
        return false;
    }
    component->AssetBindings.Add({TEXT("/Engine/BasicShapes/Cube.Cube"), opaqueMesh});
    component->AssetBindings.Add({TEXT("/Tests/Transparent"), opaqueMesh});

    if (!TestEqual(TEXT("Opaque batch fixture starts"), component->Play(), ELibraRuntimeResult::Ok))
    {
        world->DestroyWorld(false);
        return false;
    }
    for (auto tick = 0; tick < 4; ++tick)
    {
        component->TickComponent(0.1f, LEVELTICK_All, nullptr);
    }
    auto renderFence = FRenderCommandFence{};
    renderFence.BeginFence();
    renderFence.Wait();
    auto logicalRenderObjects = 0, nativePrimitives = 0, opaqueBatches = 0;
    auto cpuParticleBatches = 0, liveCpuParticles = 0, allocatedInstanceCapacity = 0;
    auto submittedBytes = uint64{};
    component->GetNativeRenderSubmissionStats(logicalRenderObjects, nativePrimitives, opaqueBatches,
        cpuParticleBatches, liveCpuParticles, allocatedInstanceCapacity, submittedBytes);
    TestTrue(TEXT("Compatible opaque Draws share an instanced native primitive"),
        opaqueBatches >= 1 && logicalRenderObjects > opaqueBatches);
    TestEqual(TEXT("Fixture produces one actual opaque instance batch"), opaqueBatches, 1);
    TestEqual(TEXT("Compatible fixture Draws occupy one native primitive"), nativePrimitives, 1);
    TestTrue(TEXT("Batch diagnostics include the actual submitted payload and allocated staging"),
        submittedBytes > 0 && allocatedInstanceCapacity >= opaqueBatches);
    TestTrue(TEXT("Opaque batch counters do not exceed native scene primitives"),
        nativePrimitives >= opaqueBatches);
    TestTrue(TEXT("Opaque Draw batch creates no StaticMesh Components"),
        component->GetNativeDrawPrimitiveCount() == nativePrimitives);
    auto meshComponents = TArray<UStaticMeshComponent*>{};
    actor->GetComponents(meshComponents);
    TestEqual(TEXT("Native opaque rendering creates no StaticMesh Components"),
        meshComponents.Num(), 0);
    component->Stop();
    component->GetNativeRenderSubmissionStats(logicalRenderObjects, nativePrimitives, opaqueBatches,
        cpuParticleBatches, liveCpuParticles, allocatedInstanceCapacity, submittedBytes);
    TestTrue(TEXT("Stop removes batch and transparent native primitives"),
        logicalRenderObjects == 0 && nativePrimitives == 0 && opaqueBatches == 0);

    component->AssetBindings.Reset();
    component->AssetBindings.Add({TEXT("/Engine/BasicShapes/Cube.Cube"), opaqueMesh});
    component->AssetBindings.Add({TEXT("/Tests/Transparent"), transparentMesh});
    if (!TestEqual(TEXT("Transparent batch-exclusion fixture restarts"),
        component->Play(), ELibraRuntimeResult::Ok))
    {
        world->DestroyWorld(false);
        return false;
    }
    for (auto tick = 0; tick < 4; ++tick)
    {
        component->TickComponent(0.1f, LEVELTICK_All, nullptr);
    }
    renderFence.BeginFence();
    renderFence.Wait();
    component->GetNativeRenderSubmissionStats(logicalRenderObjects, nativePrimitives, opaqueBatches,
        cpuParticleBatches, liveCpuParticles, allocatedInstanceCapacity, submittedBytes);
    TestTrue(TEXT("Translucent material is excluded from opaque instancing"),
        logicalRenderObjects > 0 && opaqueBatches == 0 && nativePrimitives == logicalRenderObjects);
    component->Stop();

    component->AssetBindings.Reset();
    component->AssetBindings.Add({TEXT("/Engine/BasicShapes/Cube.Cube"), opaqueMesh});
    component->AssetBindings.Add({TEXT("/Tests/Transparent"), opaqueMesh});
    root->SetRelativeScale3D(FVector(-1.0, 1.0, 1.0));
    if (!TestEqual(TEXT("Mirrored opaque fixture restarts"), component->Play(), ELibraRuntimeResult::Ok))
    {
        world->DestroyWorld(false);
        return false;
    }
    for (auto tick = 0; tick < 4; ++tick)
    {
        component->TickComponent(0.1f, LEVELTICK_All, nullptr);
    }
    renderFence.BeginFence();
    renderFence.Wait();
    component->GetNativeRenderSubmissionStats(logicalRenderObjects, nativePrimitives, opaqueBatches,
        cpuParticleBatches, liveCpuParticles, allocatedInstanceCapacity, submittedBytes);
    TestTrue(TEXT("Mirrored Draws stay in independent reverse-culling primitives"),
        logicalRenderObjects > 1 && opaqueBatches == 0 && nativePrimitives == logicalRenderObjects);
    component->Stop();
    root->SetRelativeScale3D(FVector::OneVector);
    world->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraCpuSpriteRenderingTest,
    "Libra.Runtime.Rendering.CpuSpriteNativePrimitive",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraCpuSpriteRenderingTest::RunTest(const FString&)
{
    auto fixture = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraCpuParticleFixture="), fixture) || fixture.IsEmpty())
    {
        AddInfo(TEXT("CpuSpriteNativePrimitive requires a compiler-produced CPU particle package."));
        return true;
    }

    auto bytes = TArray<uint8>{};
    if (!TestTrue(TEXT("CPU particle package loads"), FFileHelper::LoadFileToArray(bytes, *fixture)))
    {
        return false;
    }
    auto package = NewObject<ULibraSpawnableAsset>();
    if (!TestEqual(TEXT("CPU particle package validates"),
        package->SetPackageBytes(bytes), ELibraRuntimeResult::Ok))
    {
        return false;
    }

    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false,
        FName(TEXT("LibraCpuSpriteRenderingWorld")), nullptr, true,
        ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated CPU Sprite world"), world))
    {
        return false;
    }
    auto actor = world->SpawnActor<AActor>();
    auto root = NewObject<USceneComponent>(actor);
    actor->SetRootComponent(root);
    root->RegisterComponent();
    auto component = NewObject<ULibraEffectComponent>(actor);
    component->bAutoPlay = false;
    component->Package = package;
    auto camera = NewObject<UCameraComponent>(actor);
    camera->RegisterComponent();
    component->EffectCameraOverride = camera;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();

    auto material = NewObject<UMaterial>(component, NAME_None, RF_Transient);
    material->BlendMode = BLEND_Translucent;
    material->TwoSided = true;
    material->SetShadingModel(MSM_Unlit);
    auto textureParameter = NewObject<UMaterialExpressionTextureSampleParameter2D>(material);
    textureParameter->ParameterName = TEXT("Texture");
    material->GetExpressionCollection().AddExpression(textureParameter);
#if WITH_EDITOR
    material->GetEditorOnlyData()->EmissiveColor.Connect(0, textureParameter);
    material->GetEditorOnlyData()->Opacity.Connect(1, textureParameter);
#endif
    auto needsRecompile = false;
    material->SetMaterialUsage(needsRecompile, MATUSAGE_InstancedStaticMeshes);
    material->PostEditChange();
    if (!TestTrue(TEXT("CPU Sprite material exposes the required instanced texture state"),
        material->GetBlendMode() == BLEND_Translucent && material->IsTwoSided() &&
        material->CheckMaterialUsage(MATUSAGE_InstancedStaticMeshes)))
    {
        world->DestroyWorld(false);
        return false;
    }
    component->AssetBindings.Add({TEXT("builtin:cpu-particle-alpha"), material});
    const auto playResult = component->Play();
    if (!TestTrue(FString::Printf(TEXT("CPU Sprite package starts; result=%d, error=%s"),
        static_cast<int32>(playResult), *component->LastError),
        playResult == ELibraRuntimeResult::Ok))
    {
        world->DestroyWorld(false);
        return false;
    }
    for (auto tick = 0; tick < 4; ++tick)
    {
        component->TickComponent(0.1f, LEVELTICK_All, nullptr);
    }
    auto renderFence = FRenderCommandFence{};
    renderFence.BeginFence();
    renderFence.Wait();
    auto logicalRenderObjects = 0, nativePrimitives = 0, opaqueBatches = 0;
    auto cpuParticleBatches = 0, liveCpuParticles = 0, allocatedInstanceCapacity = 0;
    auto submittedBytes = uint64{};
    component->GetNativeRenderSubmissionStats(logicalRenderObjects, nativePrimitives, opaqueBatches,
        cpuParticleBatches, liveCpuParticles, allocatedInstanceCapacity, submittedBytes);
    TestTrue(TEXT("CPU Sprite uses an effect-owned native instance primitive"),
        logicalRenderObjects == 1 && nativePrimitives == 1 &&
        cpuParticleBatches == 1 && opaqueBatches == 0 && submittedBytes > 0);
    TestTrue(TEXT("CPU fixture yields live instances with separate allocated capacity"),
        liveCpuParticles > 0 && allocatedInstanceCapacity >= liveCpuParticles);
    auto instanceComponents = TArray<UInstancedStaticMeshComponent*>{};
    actor->GetComponents(instanceComponents);
    TestEqual(TEXT("CPU Sprite rendering creates no ISM Component"), instanceComponents.Num(), 0);
    component->Stop();
    component->GetNativeRenderSubmissionStats(logicalRenderObjects, nativePrimitives, opaqueBatches,
        cpuParticleBatches, liveCpuParticles, allocatedInstanceCapacity, submittedBytes);
    TestTrue(TEXT("Stop releases CPU Sprite render state"),
        logicalRenderObjects == 0 && nativePrimitives == 0 && cpuParticleBatches == 0);
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
    TestEqual(TEXT("Root owns one native Draw primitive"), component->GetNativeDrawPrimitiveCount(), 1);
    TestEqual(TEXT("Named property input is accepted"), component->SetGameProperty(TEXT("position"), {10}), ELibraRuntimeResult::Ok);
    component->TickComponent(0.1f, LEVELTICK_All, nullptr);
    TestEqual(TEXT("Named signal creates the first scope"), component->FireGameSignal(TEXT("spawn")), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Root and first child's draw objects"), component->GetRenderObjectCount(), 3);
    component->SetGameProperty(TEXT("position"), {20});
    component->TickComponent(0.1f, LEVELTICK_All, nullptr);
    auto transforms = TArray<FTransform>{};
    auto bounds = TArray<FBoxSphereBounds>{};
    auto drawIds = TArray<FGuid>{}, containers = TArray<FGuid>{};
    auto instanceIds = TArray<int64>{};
    component->GetDrawSnapshots(transforms, bounds, drawIds, containers, instanceIds);
    TestTrue(TEXT("Root and first child retain three native snapshots"), transforms.Num() == 3);
    auto drawId = [&](const TCHAR* field)
    {
        auto id = FGuid{};
        FGuid::Parse(ids->GetStringField(field), id);
        return id;
    };
    auto rootDrawId = drawId(TEXT("rootDraw")), captureDrawId = drawId(TEXT("captureDraw")),
        liveDrawId = drawId(TEXT("liveDraw"));
    auto countAt = [&](FGuid id, double x)
    {
        auto count = 0;
        for (auto index = 0; index < drawIds.Num(); ++index)
        {
            if (drawIds[index] == id && FMath::IsNearlyEqual(transforms[index].GetLocation().X, x))
            {
                ++count;
            }
        }
        return count;
    };
    TestTrue(TEXT("Root and live Draws follow their updated properties"),
        countAt(rootDrawId, 20) == 1 && countAt(liveDrawId, 20) == 1);
    TestTrue(TEXT("Captured child Draw retains its earlier property"), countAt(captureDrawId, 10) == 1);
    component->FireGameSignal(TEXT("spawn"));
    component->SetGameProperty(TEXT("position"), {30});
    component->TickComponent(0.1f, LEVELTICK_All, nullptr);
    component->GetDrawSnapshots(transforms, bounds, drawIds, containers, instanceIds);
    TestEqual(TEXT("Two children and root own five renderers"), component->GetRenderObjectCount(), 5);
    TestEqual(TEXT("Overlapping scopes own five native Draw primitives"), component->GetNativeDrawPrimitiveCount(), 5);
    TestTrue(TEXT("Snapshots retain both captured values and both live values"),
        countAt(captureDrawId, 10) == 1 && countAt(captureDrawId, 20) == 1 &&
        countAt(liveDrawId, 30) == 2 && countAt(rootDrawId, 30) == 1);
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
