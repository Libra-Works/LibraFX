#pragma once

#include "CoreMinimal.h"
#include "Containers/Ticker.h"
#include "LibraRuntimeHandles.h"
#include "LibraPreviewDiscovery.h"
#include "UObject/StrongObjectPtr.h"

class UWorld;
class AActor;
class FAdvancedPreviewScene;
class FEditorViewportClient;
class SDockTab;
class SEditorViewport;
class SWidget;
struct FLibraPreviewConnectionInvitation;
class FLibraEditorGpuPreview;
class UMaterialInstanceDynamic;
class ULibraEffectComponent;
class ULibraEditorConnectionComponent;
class UStaticMeshComponent;

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
    friend class FLibraPreviewDiscoveryTest;
    friend class FLibraPreviewPostStartupDiscoveryTest;
    friend class FLibraPreviewCpuSceneTest;
    friend class FLibraPreviewCpuFixtureTest;
    bool Tick(float deltaTime);
    void HandlePostEngineInit();
    TSharedRef<SWidget> BuildPreviewContent();
    void ConfigureScene(const FString& json, int64 programSequence);
    FString UpdateScene(int64 programSequence, double timeSeconds);
    void CommitPendingScene();
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

    struct FRenderInput
    {
        FString id;
        FString parameterName;
        FString parameterKind;
        int32 materialSlot = 0;
        int32 association = 2;
        int32 index = -1;
        int32 sourceComponent = 0;
        FGuid sourceNodeId;
        FLibraValueHandle handle;
        bool hasBinding = false;
        TArray<double> defaults;
    };
    struct FRenderNode
    {
        FGuid id;
        FString kind;
        FString assetPath;
        bool billboard = false;
        uint32 frame = 0;
        FVector4 uvRect = FVector4(0, 0, 1, 1);
        TStrongObjectPtr<UStaticMeshComponent> component;
        TArray<TStrongObjectPtr<UMaterialInstanceDynamic>> materials;
        TArray<FRenderInput> inputs;
    };

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
    TStrongObjectPtr<ULibraEditorConnectionComponent> connection;
    TUniquePtr<FLibraEditorGpuPreview> gpuPreview;
    TUniquePtr<FLibraPreviewConnectionInvitation> pendingConnection;
    TUniquePtr<FLibraPreviewDiscovery> discovery;
    TArray<FLibraPreviewDiscoveryCandidate> discoveredPeers;
    FString connectionNotice;
    TArray<FRenderNode> renderNodes;
    TArray<FRenderNode> pendingRenderNodes;
    int64 pendingRenderProgramSequence = -1;
    int64 acceptedRenderRootSequence = -1;
    double pendingRenderStarted = 0;
    TArray<FString> sceneErrors;
    int64 renderProgramSequence = 0;
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
    int32 previousPreviewProfileIndex = INDEX_NONE;
    FString previousPreviewProfileName;
};
