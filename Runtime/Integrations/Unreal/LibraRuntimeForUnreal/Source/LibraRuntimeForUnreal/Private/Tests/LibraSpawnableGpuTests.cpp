#if WITH_DEV_AUTOMATION_TESTS

#include "LibraSpawnableAsset.h"
#include "LibraEffectComponent.h"
#include "LibraGpuEffectRuntime.h"
#include "DynamicRHI.h"
#include "RenderingThread.h"
#include "Engine/World.h"
#include "Components/SceneComponent.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "liberafx/LbSpawnablePackage.h"

namespace
{
TArray<ULibraGpuEffectRuntime*> FindGpuRuntimes(AActor* actor, bool activeOnly = true)
{
    auto objects = TArray<UObject*>{};
    GetObjectsWithOuter(actor, objects, false);
    auto runtimes = TArray<ULibraGpuEffectRuntime*>{};
    for (auto object : objects)
    {
        auto runtime = Cast<ULibraGpuEffectRuntime>(object);
        if (runtime && (!activeOnly || runtime->HasRuntimeState()))
        {
            runtimes.Add(runtime);
        }
    }
    return runtimes;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraSpawnableGpuFixtureTest,
    "Libra.Runtime.Spawnable.GpuFixture",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraSpawnableGpuFixtureTest::RunTest(const FString&)
{
    auto fixture = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraSpawnableGpuFixture="), fixture) || fixture.IsEmpty())
    {
        AddInfo(TEXT("GpuFixture requires an explicit compiler-produced finite Spawnable GPU fixture."));
        return true;
    }
    auto bytes = TArray<uint8>{};
    if (!TestTrue(TEXT("GPU LFXP fixture loads"), FFileHelper::LoadFileToArray(bytes, *fixture))) return false;
    auto metadata = liberafx::SpawnablePackage{};
    if (!TestTrue(TEXT("GPU package loads"), liberafx::loadSpawnablePackage(bytes.GetData(), bytes.Num(), metadata) ==
        liberafx::Result::Ok && metadata.onStartNodeId.has_value())) return false;
    bytes[24] = 0;
    auto package = NewObject<ULibraSpawnableAsset>();
    if (!TestEqual(TEXT("GPU package validates"), package->SetPackageBytes(bytes), ELibraRuntimeResult::Ok)) return false;
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, FName(TEXT("LibraSpawnableGpuWorld")),
        nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated GPU world"), world)) return false;
    auto actor = world->SpawnActor<AActor>();
    auto component = NewObject<ULibraEffectComponent>(actor);
    component->bAutoPlay = false;
    component->Package = package;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();
    TestEqual(TEXT("Compiled GPU package starts"), component->Play(), ELibraRuntimeResult::Ok);
    AddInfo(component->LastError);
    TestEqual(TEXT("One GPU emitter is owned by first child"), component->GetGpuInstanceCount(), 1);
    component->TickComponent(0.5f, LEVELTICK_All, nullptr);
    auto emitters = FindGpuRuntimes(actor);
    if (TestEqual(TEXT("One actual child GPU runtime"), emitters.Num(), 1))
    {
        auto report = FString{};
        TestTrue(TEXT("Actual GPU dispatch and particle readback"), emitters[0]->VerifyGpuSmoke(report));
        AddInfo(report);
        component->PlayRate = 0;
        component->TickComponent(1, LEVELTICK_All, nullptr);
        TestTrue(TEXT("PlayRate zero does not advance GPU time"), FMath::IsNearlyEqual(emitters[0]->GetSimulationTime(), 0.5));
        component->PlayRate = 1;
        component->Pause();
        component->TickComponent(0.25f, LEVELTICK_All, nullptr);
        TestTrue(TEXT("Public Pause retains a frozen GPU scope"),
            !component->bIsPlaying && component->GetGpuInstanceCount() == 1 &&
            FMath::IsNearlyEqual(emitters[0]->GetSimulationTime(), 0.5));
        TestEqual(TEXT("Public Play resumes without recreating scopes"), component->Play(false), ELibraRuntimeResult::Ok);
    }
    auto start = FGuid{};
    FGuid::ParseExact(BytesToHex(metadata.onStartNodeId->data(), 16), EGuidFormats::Digits, start);
    TestEqual(TEXT("Second GPU scope spawns at independent time"), component->DispatchNodeSignal(start), ELibraRuntimeResult::Ok);
    component->TickComponent(0.25f, LEVELTICK_All, nullptr);
    emitters = FindGpuRuntimes(actor);
    TestEqual(TEXT("Independent GPU buffers for both children"), emitters.Num(), 2);
    TestTrue(TEXT("First GPU retains its local time"), emitters.ContainsByPredicate(
        [](const auto* item) { return FMath::IsNearlyEqual(item->GetSimulationTime(), 0.75); }));
    TestTrue(TEXT("Second GPU begins at local zero"), emitters.ContainsByPredicate(
        [](const auto* item) { return FMath::IsNearlyEqual(item->GetSimulationTime(), 0.25); }));
    component->TickComponent(1.25f, LEVELTICK_All, nullptr);
    TestEqual(TEXT("First natural expiry releases only its own GPU"), component->GetGpuInstanceCount(), 1);
    component->Stop();
    TestEqual(TEXT("Stop releases the remaining GPU"), component->LastResult, ELibraRuntimeResult::Ok);
    TestEqual(TEXT("No GPU instances survive Stop"), component->GetGpuInstanceCount(), 0);
    TestEqual(TEXT("GPU force fixture restarts"), component->Play(), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("EndRequest dispatches the user's spawn entry, not natural end"),
        component->NotifyEndRequest(start), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Two children before scoped GPU force"), component->GetGpuInstanceCount(), 2);
    if (TestEqual(TEXT("Finite GPU fixture has one template"), static_cast<int32>(metadata.templates.size()), 1))
    {
        auto containerId = FGuid{};
        FGuid::ParseExact(BytesToHex(metadata.templates[0].containerId.data(), 16), EGuidFormats::Digits, containerId);
        TestEqual(TEXT("Scoped force interrupts first GPU owner"),
            component->ForceTerminateChild(containerId, 1), ELibraRuntimeResult::Ok);
        TestEqual(TEXT("Sibling GPU scope remains alive"), component->GetGpuInstanceCount(), 1);
        TestEqual(TEXT("Duplicate scoped force does not release sibling"),
            component->ForceTerminateChild(containerId, 1), ELibraRuntimeResult::Ok);
        TestEqual(TEXT("Sibling still owns one GPU"), component->GetGpuInstanceCount(), 1);
    }
    TestEqual(TEXT("Whole-effect GPU force"), component->ForceTerminate(), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("No child GPU survives force"), component->GetGpuInstanceCount(), 0);
    TestTrue(TEXT("Forced GPU root is finished"), component->bRootFinished && !component->bIsPlaying);
    TestEqual(TEXT("Fresh GPU playback after force"), component->Play(), ELibraRuntimeResult::Ok);
    component->DestroyComponent();
    emitters = FindGpuRuntimes(actor, false);
    TestTrue(TEXT("Component destruction releases owned GPU runtime state"),
        !emitters.ContainsByPredicate([](const auto* item) { return item->HasRuntimeState(); }));
    world->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraSpawnableRootGpuFixtureTest,
    "Libra.Runtime.Spawnable.RootGpuFixture",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraSpawnableRootGpuFixtureTest::RunTest(const FString&)
{
    auto fixture = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraSpawnableRootGpuFixture="), fixture) || fixture.IsEmpty())
    {
        AddInfo(TEXT("RootGpuFixture requires the compiler-produced RootGpu.lfxp fixture."));
        return true;
    }
    auto bytes = TArray<uint8>{};
    auto legacy = TArray<uint8>{};
    if (!TestTrue(TEXT("Root GPU fixtures load"), FFileHelper::LoadFileToArray(bytes, *fixture) &&
        FFileHelper::LoadFileToArray(legacy, *FPaths::ChangeExtension(fixture, TEXT("legacy.lfxp"))))) return false;
    auto package = NewObject<ULibraSpawnableAsset>();
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, FName(TEXT("LibraSpawnableRootGpuWorld")),
        nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated root GPU world"), world)) return false;
    auto actor = world->SpawnActor<AActor>();
    auto component = NewObject<ULibraEffectComponent>(actor);
    component->bAutoPlay = false;
    component->Package = package;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();
    TestEqual(TEXT("Legacy GPU snapshots load as a package"), package->SetPackageBytes(legacy), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Non-executable legacy GPU rejects playback"), component->Play(), ELibraRuntimeResult::UnsupportedBinary);
    TestTrue(TEXT("Legacy rejection explains re-export"), component->LastError.Contains(TEXT("re-export")));
    auto automatic = bytes;
    automatic[24] = 1;
    TestEqual(TEXT("Root auto-idle package loads"), package->SetPackageBytes(automatic), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Root GPU rejects undefined auto-idle draining"), component->Play(), ELibraRuntimeResult::UnsupportedBinary);
    TestTrue(TEXT("Auto-idle rejection is actionable"), component->LastError.Contains(TEXT("AutoDestroyWhenIdle")));
    TestEqual(TEXT("Rejected roots create no GPU scope"), component->GetGpuInstanceCount(), 0);
    TestEqual(TEXT("Manual root GPU package loads"), package->SetPackageBytes(bytes), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Manual root GPU starts"), component->Play(), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Root owns one GPU scope without children"), component->GetGpuInstanceCount(), 1);
    component->TickComponent(0.25f, LEVELTICK_All, nullptr);
    auto emitters = FindGpuRuntimes(actor);
    TestEqual(TEXT("One actual root GPU runtime"), emitters.Num(), 1);
    auto standaloneAsset = emitters.Num() == 1 ? emitters[0]->Effect.Get() : nullptr;
    if (emitters.Num() == 1)
    {
        auto report = FString{};
        TestTrue(TEXT("Root GPU dispatch produces live particles"), emitters[0]->VerifyGpuSmoke(report));
        AddInfo(report);
    }
    TestEqual(TEXT("Natural end retires root GPU"), component->RequestNaturalEnd(), ELibraRuntimeResult::Ok);
    TestTrue(TEXT("Manual root naturally finishes"), component->bRootFinished);
    TestEqual(TEXT("No GPU scope survives root completion"), component->GetGpuInstanceCount(), 0);
    component->Stop();
    TestEqual(TEXT("Root GPU force fixture restarts"), component->Play(), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Root GPU exists before force"), component->GetGpuInstanceCount(), 1);
    TestEqual(TEXT("Whole-effect force immediately retires root GPU"),
        component->ForceTerminate(), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Root GPU no longer renders after force"), component->GetGpuInstanceCount(), 0);
    TestTrue(TEXT("Root force disables playback"), component->bRootFinished && !component->bIsPlaying);
    TestEqual(TEXT("Duplicate root GPU force succeeds"), component->ForceTerminate(), ELibraRuntimeResult::Ok);
    component->Stop();
    if (TestNotNull(TEXT("The compiled root supplied a reusable GPU asset"), standaloneAsset))
    {
        TestEqual(TEXT("Assign the same emitter as a standalone effect"),
            component->SetGpuEffect(standaloneAsset), ELibraRuntimeResult::Ok);
        TestEqual(TEXT("Standalone GPU starts"), component->Play(), ELibraRuntimeResult::Ok);
        TestEqual(TEXT("Standalone GPU owns one active state"), component->GetGpuInstanceCount(), 1);
        component->Pause();
        TestEqual(TEXT("Standalone pause retains its active state"), component->GetGpuInstanceCount(), 1);
        component->Stop();
        TestEqual(TEXT("Standalone Stop no longer counts a cached but retired runtime"),
            component->GetGpuInstanceCount(), 0);
    }
    world->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraSpawnableFluidFixtureTest,
    "Libra.Runtime.Spawnable.Fluid2DCompilerFixtures",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraSpawnableFluidFixtureTest::RunTest(const FString&)
{
    auto fixtureDirectory = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraFluidFixtures="), fixtureDirectory) ||
        fixtureDirectory.IsEmpty())
    {
        AddInfo(TEXT("Fluid2D fixtures require -LibraFluidFixtures=<compiler fixture directory>."));
        return true;
    }
    if (GUsingNullRHI || GMaxRHIFeatureLevel < ERHIFeatureLevel::SM5)
    {
        AddError(TEXT("Fluid2D fixture playback requires a real SM5 GPU."));
        return false;
    }
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false,
        FName(TEXT("LibraSpawnableFluid2DWorld")), nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated compiled-fluid playback world"), world)) return false;

    auto runFixture = [this, world, &fixtureDirectory](const TCHAR* name, bool expectParticle,
        bool expectTemplateFluid)
    {
        auto packagePath = FPaths::Combine(fixtureDirectory, FString(name) + TEXT(".lfxp"));
        auto bytes = TArray<uint8>{};
        if (!TestTrue(FString::Printf(TEXT("%s compiler package reads"), name),
            FFileHelper::LoadFileToArray(bytes, *packagePath))) return false;
        auto metadata = liberafx::SpawnablePackage{};
        if (!TestTrue(FString::Printf(TEXT("%s native package validates"), name),
            liberafx::loadSpawnablePackage(bytes.GetData(), bytes.Num(), metadata) ==
                liberafx::Result::Ok)) return false;

        auto asset = NewObject<ULibraSpawnableAsset>();
        const auto importResult = asset->SetPackageBytes(bytes);
        if (importResult != ELibraRuntimeResult::Ok)
        {
            AddError(FString::Printf(TEXT("%s SetPackageBytes returned %d (root snapshots=%llu bytes, root manifest=%llu bytes, templates=%d)"),
                name, static_cast<int32>(importResult),
                static_cast<uint64>(metadata.rootGpuContainersJson.size()),
                static_cast<uint64>(metadata.rootGpuProgramsJson.size()),
                static_cast<int32>(metadata.templates.size())));
            for (auto index = 0; index < static_cast<int32>(metadata.templates.size()); ++index)
                AddInfo(FString::Printf(TEXT("%s template%d GPU snapshot/manifest bytes=%llu/%llu"),
                    name, index, static_cast<uint64>(metadata.templates[index].gpuContainersJson.size()),
                    static_cast<uint64>(metadata.templates[index].gpuProgramsJson.size())));
            return false;
        }
        auto actor = world->SpawnActor<AActor>();
        auto sceneRoot = NewObject<USceneComponent>(actor);
        actor->SetRootComponent(sceneRoot);
        sceneRoot->RegisterComponent();
        auto component = NewObject<ULibraEffectComponent>(actor);
        component->bAutoPlay = false;
        component->Package = asset;
        actor->AddInstanceComponent(component);
        component->RegisterComponent();
        if (!TestEqual(FString::Printf(TEXT("%s starts through the public effect component"), name),
            component->Play(), ELibraRuntimeResult::Ok))
        {
            AddError(component->LastError);
            return false;
        }

        if (expectTemplateFluid)
        {
            TestTrue(TEXT("Spawnable template carries the fluid executable payload"),
                !metadata.templates.empty() && !metadata.templates[0].gpuProgramsJson.empty());
            TestTrue(TEXT("Single nested fixture has an authored root OnStart entry"),
                metadata.onStartNodeId.has_value());
        }
        component->TickComponent(0.03333334f, LEVELTICK_All, nullptr);
        auto runtimes = FindGpuRuntimes(actor);
        auto fluidRuntimes = TArray<ULibraGpuEffectRuntime*>{};
        auto particleRuntimes = TArray<ULibraGpuEffectRuntime*>{};
        for (auto* runtime : runtimes)
        {
            (runtime->IsFluid2D() ? fluidRuntimes : particleRuntimes).Add(runtime);
        }
        TestTrue(FString::Printf(TEXT("%s creates at least one fluid runtime"), name),
            !fluidRuntimes.IsEmpty());
        TestEqual(FString::Printf(TEXT("%s preserves particle programs separately from fluids"), name),
            particleRuntimes.IsEmpty(), !expectParticle);
        if (expectParticle)
            TestTrue(TEXT("Mixed scope creates an actual particle runtime"),
                particleRuntimes.ContainsByPredicate([](const auto* runtime) { return !runtime->IsFluid2D(); }));

        for (auto* runtime : fluidRuntimes)
        {
            auto pixels = TArray<FLinearColor>{};
            auto report = FString{};
            if (!TestTrue(TEXT("Compiler settings dispatch into real RGBA16F output"),
                runtime->VerifyFluid2DReadback(pixels, report)))
            {
                AddError(report);
                continue;
            }
            if (FCString::Stricmp(name, TEXT("odd-grid")) == 0)
            {
                TestEqual(TEXT("23x37 ceil-dispatch fixture returns exactly its non-square output cells"),
                    pixels.Num(), 23 * 37);
                TestFalse(TEXT("23x37 odd-dispatch output remains finite at all bounds"),
                    pixels.ContainsByPredicate([](const auto& pixel) {
                        return !FMath::IsFinite(pixel.R) || !FMath::IsFinite(pixel.G) ||
                            !FMath::IsFinite(pixel.B) || !FMath::IsFinite(pixel.A);
                    }));
                TestEqual(TEXT("Odd-grid settings preserve the authored 39 Jacobi iterations"),
                    runtime->VerifyFluid2DSettings(23, 37, 39), true);
            }
            auto colored = pixels.FindByPredicate([](const auto& pixel) { return pixel.A > 0.01f; });
            TestNotNull(TEXT("Compiled source emits nontransparent texels"), colored);
            if (colored)
            {
                TestTrue(TEXT("Fluid color channels survive adapter import and GPU execution"),
                    colored->R > colored->G * 2 && colored->G > colored->B * 1.5f);
            }
        }
        if (expectParticle && !particleRuntimes.IsEmpty())
        {
            auto report = FString{};
            TestTrue(TEXT("Mixed fixture retains actual GPU particle execution"),
                particleRuntimes[0]->VerifyGpuSmoke(report));
            AddInfo(report);
            if (FCString::Stricmp(name, TEXT("generated-output")) == 0)
            {
                TestTrue(TEXT("Generated DyeOutput is drawn through the actual GPU particle billboard pipeline"),
                    particleRuntimes[0]->VerifyGeneratedTextureFramebuffer(report));
                AddInfo(report);
            }
        }
        if (expectTemplateFluid)
            TestTrue(TEXT("Nested template contains an actual independent fluid runtime"),
                fluidRuntimes.ContainsByPredicate([](const auto* runtime) { return runtime->IsFluid2D(); }));

        if (expectTemplateFluid && !fluidRuntimes.IsEmpty())
        {
            TestEqual(TEXT("Root OnStart automatically creates one child fluid instance"),
                component->GetGpuInstanceCount(), 1);
            component->TickComponent(1.9f, LEVELTICK_All, nullptr);
            TestEqual(TEXT("Child fluid remains alive before its authored two-second lifetime"),
                component->GetGpuInstanceCount(), 1);
            component->TickComponent(0.2f, LEVELTICK_All, nullptr);
            TestEqual(TEXT("Authored child lifetime retires only its nested fluid runtime"),
                component->GetGpuInstanceCount(), 0);
            TestTrue(TEXT("AutoDestroyWhenIdle=false keeps the root alive after child expiry"),
                component->bIsPlaying && !component->bRootFinished);
            component->TickComponent(0.03333334f, LEVELTICK_All, nullptr);
            TestEqual(TEXT("Expired OnStart child is not respawned implicitly"),
                component->GetGpuInstanceCount(), 0);
        }
        else if (!fluidRuntimes.IsEmpty() && FCString::Stricmp(name, TEXT("generated-output")) != 0)
        {
            component->TickComponent(0.25f, LEVELTICK_All, nullptr);
            TestTrue(TEXT("AutoDestroyWhenIdle=false retains a live root fluid after idle time"),
                component->GetGpuInstanceCount() == runtimes.Num() &&
                component->bIsPlaying && !component->bRootFinished);
            const auto timeBeforePause = fluidRuntimes[0]->GetSimulationTime();
            component->Pause();
            actor->SetActorLocation(FVector(250, -125, 50));
            component->TickComponent(0.25f, LEVELTICK_All, nullptr);
            FlushRenderingCommands();
            TestTrue(TEXT("Paused fluid holds simulation while public presentation tick remains active"),
                FMath::IsNearlyEqual(fluidRuntimes[0]->GetSimulationTime(), timeBeforePause));
            TestEqual(TEXT("Pause preserves all GPU scopes"), component->GetGpuInstanceCount(), runtimes.Num());
            auto presentation = FMatrix44f(
                sceneRoot->GetComponentTransform().ToMatrixWithScale().GetTransposed());
            if (FCString::Stricmp(name, TEXT("draw-matrix")) == 0)
            {
                presentation = FMatrix44f::Identity;
                presentation.M[0][3] = 25;
            }
            else if (FCString::Stricmp(name, TEXT("owner-matrix")) == 0)
            {
                presentation = FMatrix44f::Identity;
            }
            TestTrue(FString::Printf(TEXT("%s paused presentation preserves authored values or follows the owner fallback"), name),
                fluidRuntimes[0]->VerifyFluid2DPresentation(presentation, true));
            actor->SetActorHiddenInGame(true);
            component->TickComponent(0, LEVELTICK_All, nullptr);
            TestFalse(TEXT("Hidden owner suppresses the paused fluid presentation"),
                fluidRuntimes[0]->VerifyFluid2DPresentation(presentation, true));
            actor->SetActorHiddenInGame(false);
            component->TickComponent(0, LEVELTICK_All, nullptr);
            TestTrue(TEXT("Restoring owner visibility restores the fluid presentation"),
                fluidRuntimes[0]->VerifyFluid2DPresentation(presentation, true));
            TestEqual(TEXT("Root fluid resumes through public Play"), component->Play(false), ELibraRuntimeResult::Ok);
            if (FCString::Stricmp(name, TEXT("owner-matrix")) == 0)
            {
                TestEqual(TEXT("Host can update the compiled OwnerMatrix input"),
                    component->SetOwnerMatrix({1, 0, 0, 250, 0, 1, 0, -125,
                        0, 0, 1, 50, 0, 0, 0, 1}), ELibraRuntimeResult::Ok);
                component->TickComponent(0.001f, LEVELTICK_All, nullptr);
                presentation = FMatrix44f(
                    sceneRoot->GetComponentTransform().ToMatrixWithScale().GetTransposed());
                TestTrue(TEXT("Resumed OwnerMatrix applies the current owner transform exactly once"),
                    fluidRuntimes[0]->VerifyFluid2DPresentation(presentation, true));
            }
            TestEqual(TEXT("Natural end retires the root fluid scope"),
                component->RequestNaturalEnd(), ELibraRuntimeResult::Ok);
            TestEqual(TEXT("No root fluid runtime survives authored natural end"),
                component->GetGpuInstanceCount(), 0);
        }
        component->Stop();
        TestEqual(TEXT("Stop retires all fluid and particle render states"),
            component->GetGpuInstanceCount(), 0);
        const auto restartResult = component->Play();
        if (!TestEqual(TEXT("Stopped package can reset cleanly"), restartResult, ELibraRuntimeResult::Ok))
        {
            AddError(FString::Printf(TEXT("%s restart returned %d: %s"), name,
                static_cast<int32>(restartResult), *component->LastError));
        }
        component->TickComponent(0.03333334f, LEVELTICK_All, nullptr);
        TestTrue(TEXT("Restart re-creates the authored active GPU/fluid scope"),
            component->GetGpuInstanceCount() > 0);
        component->Stop();
        component->DestroyComponent();
        return true;
    };

    auto success = runFixture(TEXT("fluid-only"), false, false);
    success &= runFixture(TEXT("mixed"), true, false);
    success &= runFixture(TEXT("nested"), false, true);
    success &= runFixture(TEXT("odd-grid"), false, false);
    success &= runFixture(TEXT("draw-matrix"), false, false);
    success &= runFixture(TEXT("owner-matrix"), false, false);
    success &= runFixture(TEXT("generated-output"), true, false);
    success &= runFixture(TEXT("generated-fields"), true, false);
    world->DestroyWorld(false);
    return success;
}

#endif
