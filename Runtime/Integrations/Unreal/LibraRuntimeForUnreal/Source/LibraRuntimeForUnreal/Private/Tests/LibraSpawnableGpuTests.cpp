#if WITH_DEV_AUTOMATION_TESTS

#include "LibraSpawnableAsset.h"
#include "LibraEffectComponent.h"
#include "LibraGpuEffectComponent.h"
#include "Engine/World.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "liberafx/SpawnablePackage.h"

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
    auto emitters = TArray<ULibraGpuEffectComponent*>{};
    actor->GetComponents(emitters);
    if (emitters.Num() == 1)
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
    emitters.Reset();
    actor->GetComponents(emitters);
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
    component->Play();
    component->DestroyComponent();
    emitters.Reset();
    actor->GetComponents(emitters);
    TestTrue(TEXT("Component destruction releases owned emitters"),
        !emitters.ContainsByPredicate([](const auto* item) { return item->IsRegistered(); }));
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
    auto emitters = TArray<ULibraGpuEffectComponent*>{};
    actor->GetComponents(emitters);
    TestEqual(TEXT("One actual root GPU component"), emitters.Num(), 1);
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
    world->DestroyWorld(false);
    return true;
}

#endif
