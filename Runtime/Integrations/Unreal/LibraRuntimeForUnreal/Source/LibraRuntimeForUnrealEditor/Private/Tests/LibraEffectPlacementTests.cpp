#if WITH_DEV_AUTOMATION_TESTS

#include "LibraEffectActor.h"
#include "LibraEffectAsset.h"
#include "LibraEffectComponent.h"
#include "LibraEffectActorFactory.h"
#include "LibraGpuEffectAsset.h"
#include "LibraSpawnableAsset.h"
#include "LibraGpuEffectComponent.h"
#include "AssetRegistry/AssetData.h"
#include "AssetSelection.h"
#include "Editor.h"
#include "Engine/World.h"
#include "Engine/Texture2D.h"
#include "Misc/AutomationTest.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraEffectPlacementTest,
    "Libra.Editor.Effect.ViewportAssetPlacement",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraEffectPlacementTest::RunTest(const FString&)
{
    if (!TestNotNull(TEXT("Editor placement service"), GEditor)) return false;
    auto effect = NewObject<ULibraEffectAsset>(GetTransientPackage(), NAME_None, RF_Transient);
    auto package = NewObject<ULibraSpawnableAsset>(GetTransientPackage(), NAME_None, RF_Transient);
    auto gpuEffect = NewObject<ULibraGpuEffectAsset>(GetTransientPackage(), NAME_None, RF_Transient);
    auto unsupported = NewObject<UTexture2D>();
    auto error = FText{};
    auto factory = Cast<ULibraEffectActorFactory>(FActorFactoryAssetProxy::GetFactoryForAssetObject(package));
    if (!TestNotNull(TEXT("Viewport discovers the effect package factory automatically"), factory)) return false;
    TestTrue(TEXT("Factory accepts standalone effects"), factory->CanCreateActorFrom(FAssetData(effect), error));
    TestTrue(TEXT("Factory accepts standalone GPU effects"), factory->CanCreateActorFrom(FAssetData(gpuEffect), error));
    TestFalse(TEXT("Factory rejects unrelated assets"), factory->CanCreateActorFrom(FAssetData(unsupported), error));
    TestTrue(TEXT("Invalid asset rejection is explained"), !error.IsEmpty());
    TestTrue(TEXT("Only the public effect component is addable"),
        ULibraEffectComponent::StaticClass()->HasMetaData(TEXT("BlueprintSpawnableComponent")) &&
        !ULibraSpawnableComponent::StaticClass()->HasMetaData(TEXT("BlueprintSpawnableComponent")) &&
        !ULibraGpuEffectComponent::StaticClass()->HasMetaData(TEXT("BlueprintSpawnableComponent")));
    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Editor, false, FName(TEXT("LibraEffectPlacementWorld")),
        nullptr, true, ERHIFeatureLevel::Num, &init);
    if (!TestNotNull(TEXT("Isolated placement world"), world)) return false;
    GEngine->CreateNewWorldContext(EWorldType::Editor).SetCurrentWorld(world);
    auto transform = FTransform(FRotator(0, 35, 0), FVector(120, -40, 75));
    for (auto asset : {static_cast<UObject*>(effect), static_cast<UObject*>(package), static_cast<UObject*>(gpuEffect)})
    {
        auto actor = Cast<ALibraEffectActor>(factory->CreateActor(asset, world->PersistentLevel, transform));
        if (!TestNotNull(TEXT("Dropping an effect creates its actor"), actor)) continue;
        TestTrue(TEXT("Drop location is retained"), actor->GetActorLocation().Equals(transform.GetLocation()));
        TestTrue(TEXT("Drop rotation is retained"), actor->GetActorQuat().Equals(transform.GetRotation()));
        auto components = TArray<ULibraEffectComponent*>{};
        actor->GetComponents(components);
        TestEqual(TEXT("Exactly one public effect component"), components.Num(), 1);
        TestEqual(TEXT("Dropped asset can be recovered from the actor"), factory->GetAssetFromActorInstance(actor), asset);
        TestTrue(TEXT("Matching asset slot is assigned without playback"),
            actor->EffectComponent->Effect == Cast<ULibraEffectAsset>(asset) &&
            actor->EffectComponent->Package == Cast<ULibraSpawnableAsset>(asset) &&
            actor->EffectComponent->GpuEffect == Cast<ULibraGpuEffectAsset>(asset) &&
            !actor->EffectComponent->bIsPlaying);
        actor->Destroy();
    }
    world->DestroyWorld(false);
    GEngine->DestroyWorldContext(world);
    return true;
}

#endif
