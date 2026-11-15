#include "LibraEditorPreviewSession.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "LibraEffectComponent.h"
#include "LibraEditorConnectionComponent.h"
#include "Misc/CoreDelegates.h"

void FLibraEditorPreviewSession::Start()
{
    if (FPlatformMisc::GetEnvironmentVariable(TEXT("LIBRA_AUTOMATION_PREVIEW")) != TEXT("1")) return;
    preExit = FCoreDelegates::OnPreExit.AddRaw(this, &FLibraEditorPreviewSession::Stop);
    ticker = FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateRaw(this, &FLibraEditorPreviewSession::Tick));
}

bool FLibraEditorPreviewSession::Tick(float deltaTime)
{
    if (!world.IsValid())
    {
        if (!GEngine) return true;
        auto initialization = UWorld::InitializationValues{};
        initialization.InitializeScenes(false).AllowAudioPlayback(false).CreatePhysicsScene(false)
            .CreateNavigation(false).CreateAISystem(false).ShouldSimulatePhysics(false)
            .SetTransactional(false).CreateFXSystem(false);
        world.Reset(UWorld::CreateWorld(EWorldType::EditorPreview, false, NAME_None, nullptr,
            false, ERHIFeatureLevel::Num, &initialization));
        if (!world.IsValid()) return false;
        auto parameters = FActorSpawnParameters{};
        parameters.ObjectFlags = RF_Transient;
        auto actor = world->SpawnActor<AActor>(AActor::StaticClass(), FTransform::Identity, parameters);
        if (!actor) { Stop(); return false; }
        effect.Reset(NewObject<ULibraEffectComponent>(actor));
        effect->bAutoPlay = false;
        actor->AddInstanceComponent(effect.Get());
        effect->RegisterComponent();
        connection.Reset(NewObject<ULibraEditorConnectionComponent>(actor));
        connection->Target = effect.Get();
        connection->Host = TEXT("127.0.0.1");
        auto port = int32{8766};
        auto portText = FPlatformMisc::GetEnvironmentVariable(TEXT("LIBRA_PREVIEW_PORT"));
        if (!portText.IsEmpty()) LexTryParseString(port, *portText);
        connection->Port = port;
        connection->AccessToken = FPlatformMisc::GetEnvironmentVariable(TEXT("LIBRA_PREVIEW_TOKEN"));
        connection->CertificateFingerprint = FPlatformMisc::GetEnvironmentVariable(TEXT("LIBRA_PREVIEW_FINGERPRINT"));
        actor->AddInstanceComponent(connection.Get());
        connection->RegisterComponent();
        if (!connection->ConnectToEditor())
        {
            UE_LOG(LogTemp, Warning, TEXT("Libra automation preview could not connect; check the paired editor session."));
            Stop();
            return false;
        }
    }
    if (effect->IsComponentTickEnabled())
        effect->TickComponent(deltaTime, LEVELTICK_All, &effect->PrimaryComponentTick);
    connection->TickComponent(deltaTime, LEVELTICK_All, &connection->PrimaryComponentTick);
    return true;
}

void FLibraEditorPreviewSession::Stop()
{
    // Module shutdown runs after the UObject purge; release the world before it.
    if (preExit.IsValid()) FCoreDelegates::OnPreExit.Remove(preExit);
    preExit.Reset();
    if (ticker.IsValid()) FTSTicker::GetCoreTicker().RemoveTicker(ticker);
    ticker.Reset();
    if (connection.IsValid())
    {
        connection->DisconnectFromEditor();
        connection->UnregisterComponent();
    }
    if (effect.IsValid())
    {
        effect->Stop();
        effect->UnregisterComponent();
    }
    connection.Reset();
    effect.Reset();
    if (world.IsValid()) world->DestroyWorld(false);
    world.Reset();
}
