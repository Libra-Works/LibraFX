#if WITH_DEV_AUTOMATION_TESTS

#include "LibraEffectComponent.h"
#include "LibraNativeState.h"
#include "LibraSpawnableAsset.h"
#include "Engine/World.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "liberafx/LbNamedIo.h"
#include "liberafx/LbSpawnablePackage.h"
#include <algorithm>

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraInstanceGameIoFacadeTest,
    "Libra.Runtime.Spawnable.InstanceGameIoFacade",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraInstanceGameIoFacadeTest::RunTest(const FString&)
{
    auto fixture = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraInstanceGameIoFixture="), fixture) || fixture.IsEmpty())
    {
        AddError(TEXT("Supply -LibraInstanceGameIoFixture=<compiler-produced InstanceGameIo.lfxp>; its sibling .container identity is required."));
        return false;
    }

    auto packageBytes = TArray<uint8>{};
    auto containerBytes = TArray<uint8>{};
    const auto containerPath = FPaths::ChangeExtension(fixture, TEXT("container"));
    if (!TestTrue(TEXT("Instance game I/O package and container identity load"),
        FFileHelper::LoadFileToArray(packageBytes, *fixture) &&
        FFileHelper::LoadFileToArray(containerBytes, *containerPath) && containerBytes.Num() == 16))
        return false;

    auto metadata = liberafx::SpawnablePackage{};
    if (!TestTrue(TEXT("Native package parser accepts the instance game I/O package"),
        liberafx::loadSpawnablePackage(packageBytes.GetData(), packageBytes.Num(), metadata) == liberafx::Result::Ok))
        return false;

    auto containerId = FGuid{};
    if (!TestTrue(TEXT("Container identity is representable by the Unreal facade"),
        FGuid::ParseExact(BytesToHex(containerBytes.GetData(), 16), EGuidFormats::Digits, containerId)))
        return false;

    const auto instanceTemplate = std::find_if(metadata.templates.begin(), metadata.templates.end(), [&](const auto& candidate)
    {
        auto candidateId = FGuid{};
        return FGuid::ParseExact(BytesToHex(candidate.containerId.data(), candidate.containerId.size()),
            EGuidFormats::Digits, candidateId) && candidateId == containerId;
    });
    if (!TestTrue(TEXT("Container sidecar identifies one compiled template"),
        instanceTemplate != metadata.templates.end()))
        return false;
    if (!TestTrue(TEXT("Compiled template has executable child bytecode"),
        !instanceTemplate->bytecode.empty()))
        return false;

    auto templateBytecode = TArray<uint8>{};
    templateBytecode.Append(instanceTemplate->bytecode.data(), static_cast<int32>(instanceTemplate->bytecode.size()));
    auto compiledTemplate = FCompiledLibraProgram(templateBytecode);
    if (!TestEqual(TEXT("Template bytecode exposes its instance I/O metadata"),
        compiledTemplate.result, liberafx::Result::Ok))
        return false;

    auto propertyPort = liberafx::ProgramGameIoPort{};
    auto signalPort = liberafx::ProgramGameIoPort{};
    auto hasPropertyPort = false;
    auto hasSignalPort = false;
    for (auto index = std::size_t{}; index < compiledTemplate.program.gameIoPortCount(); ++index)
    {
        auto port = liberafx::ProgramGameIoPort{};
        if (!compiledTemplate.program.gameIoPort(index, port) || !port.name) continue;
        if (!hasPropertyPort && port.kind == static_cast<uint8>(liberafx::NamedIoKind::InputProperty))
        {
            propertyPort = port;
            hasPropertyPort = true;
        }
        if (!hasSignalPort && port.kind == static_cast<uint8>(liberafx::NamedIoKind::InputSignal))
        {
            signalPort = port;
            hasSignalPort = true;
        }
    }
    if (!TestTrue(TEXT("Compiled template exposes instance property and signal inputs"),
        hasPropertyPort && propertyPort.width > 0 && propertyPort.width <= 4 && hasSignalPort))
        return false;

    const auto inputName = FString(UTF8_TO_TCHAR(propertyPort.name));
    const auto inputWidth = static_cast<int32>(propertyPort.width);
    const auto signalName = FString(UTF8_TO_TCHAR(signalPort.name));
    const auto rootSignal = std::find_if(metadata.gameIoPorts.begin(), metadata.gameIoPorts.end(), [](const auto& port)
    {
        return port.kind == static_cast<uint8>(liberafx::NamedIoKind::InputSignal);
    });
    if (!TestTrue(TEXT("Fixture has a root signal that creates child instances"),
        rootSignal != metadata.gameIoPorts.end() && inputWidth > 0 && inputWidth <= 4 &&
        !inputName.IsEmpty() && !signalName.IsEmpty()))
        return false;

    auto asset = NewObject<ULibraSpawnableAsset>();
    if (!TestEqual(TEXT("Engine package asset accepts the fixture"),
        asset->SetPackageBytes(packageBytes), ELibraRuntimeResult::Ok))
        return false;

    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, FName(TEXT("LibraInstanceGameIoFacadeWorld")),
        nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated instance game I/O world"), world))
        return false;

    auto actor = world->SpawnActor<AActor>();
    if (!TestNotNull(TEXT("Facade owner actor"), actor))
    {
        world->DestroyWorld(false);
        return false;
    }

    auto component = NewObject<ULibraEffectComponent>(actor);
    component->bAutoPlay = false;
    component->Package = asset;
    actor->AddInstanceComponent(component);
    component->RegisterComponent();
    if (!TestEqual(TEXT("Public effect component starts the package"),
        component->Play(), ELibraRuntimeResult::Ok))
    {
        world->DestroyWorld(false);
        return false;
    }

    auto collectTargets = [&]()
    {
        auto targets = TArray<TPair<int64, int64>>{};
        for (auto instanceId = int64{1}; instanceId <= 64; ++instanceId)
        {
            auto generation = int64{0};
            if (component->GetInstanceTarget(containerId, instanceId, generation) == ELibraRuntimeResult::Ok)
                targets.Emplace(instanceId, generation);
        }
        return targets;
    };
    auto targets = collectTargets();
    for (auto attempt = 0; targets.Num() < 2 && attempt < 4; ++attempt)
    {
        const auto rootSignalName = FString(UTF8_TO_TCHAR(rootSignal->name.c_str()));
        if (component->FireGameSignal(rootSignalName) != ELibraRuntimeResult::Ok)
        {
            component->Stop();
            world->DestroyWorld(false);
            return false;
        }
        targets = collectTargets();
    }
    if (!TestTrue(TEXT("Two sibling instances resolve through the public facade"), targets.Num() >= 2))
    {
        component->Stop();
        world->DestroyWorld(false);
        return false;
    }

    const auto firstId = targets[0].Key;
    const auto firstGeneration = targets[0].Value;
    const auto secondId = targets[1].Key;
    const auto secondGeneration = targets[1].Value;
    TestTrue(TEXT("Both facade targets have a live host generation"),
        firstGeneration > 0 && secondGeneration == firstGeneration);

    auto firstBefore = TArray<double>{};
    auto secondBefore = TArray<double>{};
    if (!TestTrue(TEXT("Read both sibling properties"),
        component->GetInstanceProperty(firstGeneration, containerId, firstId, inputName, firstBefore) == ELibraRuntimeResult::Ok &&
        component->GetInstanceProperty(secondGeneration, containerId, secondId, inputName, secondBefore) == ELibraRuntimeResult::Ok &&
        firstBefore.Num() == inputWidth && secondBefore.Num() == inputWidth))
    {
        component->Stop();
        world->DestroyWorld(false);
        return false;
    }

    auto requested = TArray<double>{};
    requested.SetNumUninitialized(inputWidth);
    for (auto lane = 0; lane < inputWidth; ++lane)
        requested[lane] = 17.25 + lane;
    if (!TestEqual(TEXT("Set per-instance property through the public facade"),
        component->SetInstanceProperty(firstGeneration, containerId, firstId, inputName, requested),
        ELibraRuntimeResult::Ok))
    {
        component->Stop();
        world->DestroyWorld(false);
        return false;
    }

    component->TickComponent(0.25f, LEVELTICK_All, nullptr);
    auto firstAfter = TArray<double>{};
    auto secondAfter = TArray<double>{};
    TestTrue(TEXT("Read updated and sibling values through the public facade"),
        component->GetInstanceProperty(firstGeneration, containerId, firstId, inputName, firstAfter) == ELibraRuntimeResult::Ok &&
        component->GetInstanceProperty(secondGeneration, containerId, secondId, inputName, secondAfter) == ELibraRuntimeResult::Ok);
    TestEqual(TEXT("The selected sibling property preserves its declared width"), firstAfter.Num(), inputWidth);
    TestEqual(TEXT("The other sibling property preserves its declared width"), secondAfter.Num(), inputWidth);
    for (auto lane = 0; lane < inputWidth && lane < firstAfter.Num() && lane < secondAfter.Num(); ++lane)
    {
        TestEqual(TEXT("Selected instance receives its own value"), firstAfter[lane], requested[lane]);
        TestEqual(TEXT("Sibling value remains isolated"), secondAfter[lane], secondBefore[lane]);
    }

    TestEqual(TEXT("Dispatch child signal through the public facade"),
        component->FireInstanceSignal(firstGeneration, containerId, firstId, signalName),
        ELibraRuntimeResult::Ok);
    auto stillLiveGeneration = int64{0};
    TestEqual(TEXT("Sibling remains addressable after another instance signal"),
        component->GetInstanceTarget(containerId, secondId, stillLiveGeneration), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Sibling retains its generation"), stillLiveGeneration, secondGeneration);
    auto siblingAfterSignal = TArray<double>{};
    TestTrue(TEXT("Sibling property remains readable after signal dispatch"),
        component->GetInstanceProperty(secondGeneration, containerId, secondId, inputName, siblingAfterSignal) ==
            ELibraRuntimeResult::Ok &&
        siblingAfterSignal.Num() == secondBefore.Num());
    for (auto lane = 0; lane < FMath::Min(secondBefore.Num(), siblingAfterSignal.Num()); ++lane)
        TestEqual(TEXT("Signal dispatch does not mutate sibling property"),
            siblingAfterSignal[lane], secondBefore[lane]);

    component->Stop();
    world->DestroyWorld(false);
    return true;
}

#endif
