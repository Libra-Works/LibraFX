#pragma once

#include "CoreMinimal.h"
#include "Containers/Ticker.h"
#include "LibraPreviewDiscovery.h"
#include "LibraViewerEndpoint.h"
#include "UObject/StrongObjectPtr.h"

class UWorld;
class AActor;
class FAdvancedPreviewScene;
class FEditorViewportClient;
class SDockTab;
class SEditorViewport;
class SWidget;
struct FLibraPreviewConnectionInvitation;
class UMaterialInstanceDynamic;
class UCameraComponent;
class ULibraEffectComponent;
class ULibraEditorConnectionComponent;

// Isolated editor preview with optional automatic pairing.
class FLibraEditorPreviewSession final
{
public:
    FLibraEditorPreviewSession();
    ~FLibraEditorPreviewSession();
    void Start();
    void Stop();

private:
    friend class FLibraPreviewDeferredTabTest;
    friend class FLibraPreviewMenuTest;
    friend class FLibraPreviewEmptySceneTest;
    friend class FLibraPreviewProfileRestartTest;
    friend class FLibraPreviewDiscoveryTest;
    bool Tick(float deltaTime);
    void HandlePostEngineInit();
    TSharedRef<SWidget> BuildPreviewContent();
    TSharedRef<SDockTab> SpawnPreviewTab(const class FSpawnTabArgs& args);
    TSharedPtr<SDockTab> OpenPreviewTab();
    TSharedPtr<SDockTab> FindPreviewTabToReuse(const struct FTabId& id) const;
    void OnPreviewTabClosed(TSharedRef<SDockTab> tab);
    void RegisterPreviewMenu();
    void DiscoverLibra();
    void PollDiscovery();
    void SelectDiscoveredPeer(const FString& pipeName);
    void ConnectFromClipboard();
    bool AcceptConnectionInvitation(const FString& text);
    void ApplyPendingConnection();
    void DisconnectPreview();
    void SavePreviewProfile();

    FTSTicker::FDelegateHandle ticker;
    FDelegateHandle preExit;
    FDelegateHandle postEngineInit;
    FDelegateHandle menuStartup;
    FName tabId = TEXT("LibraFxPreview");
    FName menuEntryId = TEXT("LibraFxShowPreview");
    FName menuName = TEXT("LevelEditor.MainMenu.Window");
    TSharedPtr<FAdvancedPreviewScene> previewScene;
    TStrongObjectPtr<UWorld> world;
    TStrongObjectPtr<AActor> previewActor;
    TStrongObjectPtr<ULibraEffectComponent> effect;
    TStrongObjectPtr<UCameraComponent> previewCamera;
    TStrongObjectPtr<ULibraEditorConnectionComponent> connection;
    TUniquePtr<FLibraPreviewConnectionInvitation> pendingConnection;
    TUniquePtr<FLibraPreviewDiscovery> discovery;
    FLibraViewerEndpoint viewerEndpoint;
    TArray<FLibraPreviewDiscoveryCandidate> discoveredPeers;
    FString connectionNotice;
    TSharedPtr<SEditorViewport> viewportWidget;
    TSharedPtr<FEditorViewportClient> viewportClient;
    TWeakPtr<SDockTab> previewTab;
    TSharedPtr<SWidget> previewSettings;
    bool bTabRegistered = false;
    bool bStarted = false;
    bool bOpenRequested = false;
    bool bSettingsVisible = true;
    bool bEngineReady = false;
    bool bPreviewRequested = false;
    bool bTransportRequested = false;
    bool bAutoConnect = false;
    double previewUnitsPerMetre = 100.0;
    int32 previousPreviewProfileIndex = INDEX_NONE;
    FString previousPreviewProfileName;
};
