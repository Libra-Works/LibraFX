#if WITH_DEV_AUTOMATION_TESTS

#include "Engine/Texture2D.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "HAL/PlatformTime.h"
#include "LibraGpuEffectAsset.h"
#include "LibraGpuEffectRuntime.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "RenderingThread.h"
#include "UObject/GarbageCollection.h"

namespace
{
class FLibraGpuSoftLoadCommand final : public IAutomationLatentCommand
{
public:
    FLibraGpuSoftLoadCommand(FAutomationTestBase* test, UWorld* world,
        ULibraGpuEffectRuntime* runtime, ULibraGpuEffectAsset* expected)
        : Test(test), World(world), Runtime(runtime), Expected(expected), Started(FPlatformTime::Seconds()) {}

    ~FLibraGpuSoftLoadCommand() override
    {
        if (Runtime) Runtime->Stop();
        FlushRenderingCommands();
        if (World) World->DestroyWorld(false);
        FlushRenderingCommands();
    }

    bool Update() override
    {
        if (!Runtime->Effect)
        {
            if (FPlatformTime::Seconds() - Started < 15) return false;
            Test->AddError(FString::Printf(TEXT("Async GPU asset load timed out: %s"), *Runtime->LastError));
            return true;
        }
        Test->TestTrue(TEXT("Streamable load assigned native asset hard reference"), Runtime->Effect == Expected);
        Test->TestTrue(TEXT("Async asset is playing without an external tool"), Runtime->bIsPlaying);
        Runtime->Tick(0.016f);
        Runtime->Pause();
        Test->TestFalse(TEXT("Pause stops simulation ticks"), Runtime->bIsPlaying);
        Test->TestTrue(TEXT("Pause can resume retained GPU resources"), Runtime->Play());
        Test->TestTrue(TEXT("Reset creates a fresh independent instance"), Runtime->ResetEffect());
        Runtime->Tick(0.016f);
        Runtime->Stop();
        Test->TestFalse(TEXT("Stop stops gameplay"), Runtime->bIsPlaying);
        Test->TestTrue(TEXT("Stop preserves the serialized hard asset binding"), Runtime->Effect == Expected);
        Runtime->LoadSoftEffect();
        Runtime->Stop();
        Test->TestFalse(TEXT("Stopping a pending soft load does not restart playback"), Runtime->bIsPlaying);
        Test->AddInfo(TEXT("Native saved asset hard reference and StreamableManager runtime lifecycle passed."));
        return true;
    }

private:
    FAutomationTestBase* Test;
    UWorld* World;
    ULibraGpuEffectRuntime* Runtime;
    ULibraGpuEffectAsset* Expected;
    double Started;
};
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGpuNativeAssetLifecycleTest,
    "Libra.GPU.Assets.NativePackageGameplayLifecycle",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGpuNativeAssetLifecycleTest::RunTest(const FString& parameters)
{
    FString objectPath;
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraGpuAsset="), objectPath))
    {
        AddInfo(TEXT("Native package fixture not selected; rerun fresh process with -LibraGpuAsset=/Game/...Asset.Asset"));
        return true;
    }
    auto asset = LoadObject<ULibraGpuEffectAsset>(nullptr, *objectPath);
    if (!TestNotNull(TEXT("Saved native GPU asset loads from package"), asset)) return false;
    FString error;
    if (!TestTrue(TEXT("Reloaded serialized program validates"), asset->ValidateProgram(error)))
    {
        AddError(error);
        return false;
    }
    TestTrue(TEXT("Reloaded native asset retains compiled kernels"), !asset->GetKernels().IsEmpty());
    TestTrue(TEXT("Runtime mode is explicitly GPU register interpreter"),
        asset->GetExecutionMode() == ELibraGpuExecutionMode::GpuRegisterInterpreter);
    TestTrue(TEXT("Reloaded native asset retains render resource dependencies"), !asset->GetTextures().IsEmpty());
    FString texturePath;
    FParse::Value(FCommandLine::Get(), TEXT("LibraGpuTexture="), texturePath);
    for (const auto& binding : asset->GetTextures())
    {
        if (binding.bBuiltinWhite)
        {
            TestNull(TEXT("Serialized explicit builtin remains independent of a texture UObject"), binding.Texture.Get());
            continue;
        }
        TestNotNull(TEXT("Saved hard Texture2D reference resolves"), binding.Texture.Get());
        if (binding.Texture && !texturePath.IsEmpty())
            TestEqual(TEXT("Native texture dependency survives package reload"), binding.Texture->GetPathName(), texturePath);
    }
    FString expectedMaterial;
    if (FParse::Value(FCommandLine::Get(), TEXT("LibraGpuExpectedMaterial="), expectedMaterial))
    {
        auto expectedBlend = expectedMaterial == TEXT("builtin:unlit-additive") ?
            ELibraGpuBillboardBlend::Additive : ELibraGpuBillboardBlend::Alpha;
        float expectedSizeScale = 1;
        FParse::Value(FCommandLine::Get(), TEXT("LibraGpuExpectedSizeScale="), expectedSizeScale);
        for (const auto& step : asset->GetFlow())
            if (step.Kind == TEXT("RenderBillboard"))
            {
                TestEqual(TEXT("Serialized renderer material survives fresh package reload"), step.BillboardBlend, expectedBlend);
                TestEqual(TEXT("Serialized draw-size scale survives fresh package reload"), step.SizeScale, expectedSizeScale);
            }
    }

    auto init = UWorld::InitializationValues().AllowAudioPlayback(false).RequiresHitProxies(false)
        .CreatePhysicsScene(false).CreateNavigation(false).CreateAISystem(false)
        .ShouldSimulatePhysics(false).SetTransactional(false);
    auto world = UWorld::CreateWorld(EWorldType::Game, false, FName(TEXT("LibraGpuLifecycleWorld")),
        nullptr, true, ERHIFeatureLevel::Num, &init);
    auto actor = world->SpawnActor<AActor>();
    if (!actor)
    {
        world->DestroyWorld(false);
        AddError(TEXT("Could not construct isolated GPU gameplay actor."));
        return false;
    }
    auto hard = NewObject<ULibraGpuEffectRuntime>(actor);
    hard->Initialize(actor);
    TestTrue(TEXT("Gameplay runtime accepts hard native asset reference"), hard->SetEffect(asset));
    TestTrue(TEXT("Native runtime plays in a real render world"), hard->Play());
    hard->Tick(0.016f);
    auto invalid = NewObject<ULibraGpuEffectAsset>();
    TestFalse(TEXT("Invalid asset replacement is rejected"), hard->SetEffect(invalid));
    TestTrue(TEXT("Rejected replacement preserves active asset and playback"), hard->Effect == asset && hard->bIsPlaying);
    CollectGarbage(RF_NoFlags);
    TestTrue(TEXT("Hard-bound GPU asset remains alive across GC"), IsValid(hard->Effect.Get()));
    hard->Stop();
    FlushRenderingCommands();

    auto soft = NewObject<ULibraGpuEffectRuntime>(actor);
    soft->Initialize(actor);
    soft->SoftEffect = TSoftObjectPtr<ULibraGpuEffectAsset>(FSoftObjectPath(objectPath));
    soft->bAutoPlay = true;
    soft->LoadSoftEffect();
    ADD_LATENT_AUTOMATION_COMMAND(FLibraGpuSoftLoadCommand(this, world, soft, asset));
    return true;
}

#endif
