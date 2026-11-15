#if !UE_BUILD_SHIPPING

#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "GameFramework/PlayerController.h"
#include "HAL/IConsoleManager.h"
#include "LibraGpuEffectAsset.h"
#include "LibraGpuEffectComponent.h"
#include "RenderingThread.h"

DEFINE_LOG_CATEGORY_STATIC(LogLibraGpuGameSmoke, Log, All);

static FAutoConsoleCommandWithWorldAndArgs LibraGpuGameSmokeCommand(
    TEXT("Libra.GPU.Smoke"),
    TEXT("Development-only: Libra.GPU.Smoke /Game/Folder/Effect.Effect; native asset GPU readback, then stop."),
    FConsoleCommandWithWorldAndArgsDelegate::CreateLambda([](const TArray<FString>& args, UWorld* world)
    {
        if (!world || args.Num() != 1)
        {
            UE_LOG(LogLibraGpuGameSmoke, Error, TEXT("LIBRA_GPU_GAME_SMOKE_FAILED: game world and one native asset path are required."));
            return;
        }
        auto asset = LoadObject<ULibraGpuEffectAsset>(nullptr, *args[0]);
        FString error;
        if (!asset || !asset->ValidateProgram(error))
        {
            UE_LOG(LogLibraGpuGameSmoke, Error, TEXT("LIBRA_GPU_GAME_SMOKE_FAILED: native asset load/validation: %s"), *error);
            return;
        }
        auto actor = world->SpawnActor<AActor>();
        if (!actor)
        {
            UE_LOG(LogLibraGpuGameSmoke, Error, TEXT("LIBRA_GPU_GAME_SMOKE_FAILED: cannot spawn gameplay actor."));
            return;
        }
        auto component = NewObject<ULibraGpuEffectComponent>(actor);
        actor->AddInstanceComponent(component);
        actor->SetRootComponent(component);
        component->bAutoPlay = false;
        component->RegisterComponent();
        FVector cameraPosition = FVector::ZeroVector;
        FRotator cameraRotation = FRotator::ZeroRotator;
        if (auto controller = world->GetFirstPlayerController())
            controller->GetPlayerViewPoint(cameraPosition, cameraRotation);
        component->SetWorldLocation(cameraPosition + cameraRotation.Vector() * 10);
        component->ConfigureEmitter(100, 1);
        auto passed = component->SetEffect(asset) && component->Play();
        FString report = component->LastError;
        if (passed)
        {
            component->TickComponent(0.016f, LEVELTICK_All, nullptr);
            passed = component->VerifyGpuSmoke(report);
        }
        component->Pause();
        component->Stop();
        FlushRenderingCommands();
        actor->Destroy();
        if (passed)
        {
            UE_LOG(LogLibraGpuGameSmoke, Display, TEXT("LIBRA_GPU_GAME_SMOKE_PASSED asset=%s %s"), *args[0], *report);
        }
        else
        {
            UE_LOG(LogLibraGpuGameSmoke, Error, TEXT("LIBRA_GPU_GAME_SMOKE_FAILED asset=%s %s"), *args[0], *report);
        }
    }));

#endif
