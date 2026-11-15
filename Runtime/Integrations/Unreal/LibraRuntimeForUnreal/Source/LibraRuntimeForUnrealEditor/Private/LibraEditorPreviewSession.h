#pragma once

#include "CoreMinimal.h"
#include "Containers/Ticker.h"
#include "LibraRuntimeHandles.h"
#include "UObject/StrongObjectPtr.h"

class UWorld;
class AActor;
class FAdvancedPreviewScene;
class FEditorViewportClient;
class SDockTab;
class SEditorViewport;
class SWidget;
class UMaterialInstanceDynamic;
class ULibraEffectComponent;
class ULibraEditorConnectionComponent;
class UStaticMeshComponent;

// Explicit opt-in automation preview, isolated from the user's edited level.
class FLibraEditorPreviewSession final
{
public:
    FLibraEditorPreviewSession();
    ~FLibraEditorPreviewSession();
    void Start();
    void Stop();

private:
    friend class FLibraPreviewDeferredTabTest;
    bool Tick(float deltaTime);
    void HandlePostEngineInit();
    TSharedRef<SWidget> BuildPreviewContent();
    void ConfigureScene(const FString& json, int64 programSequence);
    FString UpdateScene(int64 programSequence, double timeSeconds);
    TSharedRef<SDockTab> SpawnPreviewTab(const class FSpawnTabArgs& args);
    void SavePreviewProfile();

    struct FRenderInput
    {
        FString id;
        FString parameterName;
        FString parameterKind;
        int32 materialSlot = 0;
        int32 association = 0;
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
    TSharedPtr<FAdvancedPreviewScene> previewScene;
    TStrongObjectPtr<UWorld> world;
    TStrongObjectPtr<AActor> previewActor;
    TStrongObjectPtr<UStaticMeshComponent> placeholderCube;
    TStrongObjectPtr<ULibraEffectComponent> effect;
    TStrongObjectPtr<ULibraEditorConnectionComponent> connection;
    TArray<FRenderNode> renderNodes;
    TArray<FString> sceneErrors;
    int64 renderProgramSequence = 0;
    TSharedPtr<SEditorViewport> viewportWidget;
    TSharedPtr<FEditorViewportClient> viewportClient;
    TSharedPtr<SDockTab> previewTab;
    TSharedPtr<SWidget> previewSettings;
    bool bTabRegistered = false;
    bool bSettingsVisible = true;
    bool bEngineReady = false;
    bool bPreviewRequested = false;
    bool bTransportRequested = false;
    int32 previousPreviewProfileIndex = INDEX_NONE;
    FString previousPreviewProfileName;
};
