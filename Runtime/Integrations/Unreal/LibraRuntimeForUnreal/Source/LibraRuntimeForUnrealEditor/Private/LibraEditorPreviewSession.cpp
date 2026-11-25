#include "LibraEditorPreviewSession.h"
#include "Misc/App.h"

#include "AdvancedPreviewScene.h"
#include "AdvancedPreviewSceneModule.h"
#include "AssetViewerSettings.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "Camera/CameraComponent.h"
#include "GameFramework/Actor.h"
#include "InputKeyEventArgs.h"
#include "LibraEditorAssetAdapter.h"
#include "LibraPreviewConnectionInvitation.h"
#include "LibraEffectComponent.h"
#include "LibraEditorConnectionComponent.h"
#include "Materials/Material.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Materials/MaterialExpressionAdd.h"
#include "Materials/MaterialExpressionComponentMask.h"
#include "Materials/MaterialExpressionMultiply.h"
#include "Materials/MaterialExpressionTextureCoordinate.h"
#include "Materials/MaterialExpressionTextureSample.h"
#include "Materials/MaterialExpressionTextureSampleParameter2D.h"
#include "Materials/MaterialExpressionVectorParameter.h"
#include "Materials/MaterialExpressionPerInstanceCustomData.h"
#include "Components/StaticMeshComponent.h"
#include "Components/SceneComponent.h"
#include "EditorViewportClient.h"
#include "SEditorViewport.h"
#include "SCommonEditorViewportToolbarBase.h"
#include "Editor/EditorPerProjectUserSettings.h"
#include "Framework/Application/SlateApplication.h"
#include "Widgets/Input/SSpinBox.h"
#include "Framework/Docking/TabManager.h"
#include "Framework/MultiBox/MultiBoxBuilder.h"
#include "GenericPlatform/GenericApplication.h"
#include "HAL/PlatformApplicationMisc.h"
#include "Layout/Children.h"
#include "ToolMenus.h"
#include "Widgets/SWindow.h"
#include "Misc/CoreDelegates.h"
#include "Misc/CommandLine.h"
#include "Misc/ConfigCacheIni.h"
#include "HAL/PlatformProcess.h"
#include "ShaderCompiler.h"
#include "Misc/PackageName.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Misc/EngineVersionComparison.h"
#include "Modules/ModuleManager.h"
#include "PreviewScene.h"
#include "Widgets/Docking/SDockTab.h"
#include "Widgets/Input/SCheckBox.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Input/SComboButton.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SSplitter.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SNullWidget.h"
#include "Widgets/Text/STextBlock.h"
#if WITH_DEV_AUTOMATION_TESTS
#include "Misc/AutomationTest.h"
#endif

namespace
{
    const FName PreviewTabId(TEXT("LibraFxPreview"));
    const TCHAR* PreviewSettingsSection = TEXT("LibraFxPreview");

    auto& PostEngineInitDelegate()
    {
#if UE_VERSION_OLDER_THAN(5, 8, 0)
        return FCoreDelegates::OnPostEngineInit;
#else
        return FCoreDelegates::GetOnPostEngineInit();
#endif
    }

    bool IsPreviewGridEnabled(const FAdvancedPreviewScene& scene, const FEditorViewportClient& viewport)
    {
#if UE_VERSION_OLDER_THAN(5, 8, 0)
        return scene.IsGridEnabled();
#else
        return viewport.IsSetShowGridChecked();
#endif
    }

    void TogglePreviewGrid(FAdvancedPreviewScene& scene, FEditorViewportClient& viewport)
    {
#if UE_VERSION_OLDER_THAN(5, 8, 0)
        scene.HandleToggleGrid();
        viewport.EngineShowFlags.SetGrid(scene.IsGridEnabled());
#else
        viewport.SetShowGrid();
#endif
    }

    int32 CountDockTabs(const TSharedRef<SWidget>& widget)
    {
        auto count = widget->GetType() == FName(TEXT("SDockTab")) ? 1 : 0;
        auto children = widget->GetChildren();
        if (!children) return count;
        for (auto index = 0; index < children->Num(); ++index)
            count += CountDockTabs(children->GetChildAt(index));
        return count;
    }

    bool FindVisibleWindowPosition(const FVector2f& position, const FVector2f& size,
        const FDisplayMetrics& displays, FVector2f& recovered)
    {
        auto intersects = [&](const FPlatformRect& work)
        {
            return position.X < work.Right && position.X + size.X > work.Left &&
                position.Y < work.Bottom && position.Y + size.Y > work.Top;
        };
        if (displays.MonitorInfo.Num() > 0)
        {
            for (const auto& monitor : displays.MonitorInfo)
                if (intersects(monitor.WorkArea)) return false;
        }
        else if (intersects(displays.PrimaryDisplayWorkAreaRect)) return false;
        const auto& primary = displays.PrimaryDisplayWorkAreaRect;
        if (primary.Right <= primary.Left || primary.Bottom <= primary.Top) return false;
        recovered = FVector2f(static_cast<float>(primary.Left + 32), static_cast<float>(primary.Top + 32));
        return true;
    }

    class FLibraPreviewViewportClient final : public FEditorViewportClient
    {
    public:
        FLibraPreviewViewportClient(FAdvancedPreviewScene* scene, const TSharedRef<SEditorViewport>& widget)
            : FEditorViewportClient(nullptr, scene, widget), advancedScene(scene)
        {
        }

        bool InputKey(const FInputKeyEventArgs& args) override
        {
            return advancedScene->HandleInputKey(args) || FEditorViewportClient::InputKey(args);
        }

        bool InputAxis(const FInputKeyEventArgs& args) override
        {
            return advancedScene->HandleViewportInput(args.Viewport, args.InputDevice, args.Key,
                args.AmountDepressed, args.DeltaTime, args.NumSamples, args.IsGamepad()) ||
                FEditorViewportClient::InputAxis(args);
        }

        FLinearColor GetBackgroundColor() const override
        {
            return advancedScene->GetBackgroundColor();
        }

    private:
        FAdvancedPreviewScene* advancedScene;
    };

    class SLibraPreviewViewport final : public SEditorViewport, public ICommonEditorViewportToolbarInfoProvider
    {
    public:
        SLATE_BEGIN_ARGS(SLibraPreviewViewport) {}
        SLATE_END_ARGS()

        ~SLibraPreviewViewport() override
        {
            // Release the raw-scene client before the derived widget's scene ownership.
            if (Client.IsValid()) Client->Viewport = nullptr;
            Client.Reset();
        }

        void Construct(const FArguments&, const TSharedRef<FAdvancedPreviewScene>& scene, AActor* actor)
        {
            previewScene = scene;
            previewActor = actor;
            SEditorViewport::Construct(SEditorViewport::FArguments{});
        }

        TSharedRef<SEditorViewport> GetViewportWidget() override
        {
            return SharedThis(this);
        }

        TSharedPtr<FExtender> GetExtenders() const override
        {
            return MakeShared<FExtender>();
        }

        void OnFloatingButtonClicked() override
        {
            FSlateApplication::Get().SetKeyboardFocus(SharedThis(this), EFocusCause::SetDirectly);
        }

    protected:
        TSharedRef<FEditorViewportClient> MakeEditorViewportClient() override
        {
            auto client = MakeShared<FLibraPreviewViewportClient>(previewScene.Get(), SharedThis(this));
            auto cameraPosition = FVector(350, -450, 320);
            client->SetViewLocation(cameraPosition);
            client->SetViewRotation((FVector(0, 0, 50) - cameraPosition).Rotation());
            client->SetLookAtLocation(FVector(0, 0, 50));
            client->SetViewMode(VMI_Lit);
            client->SetRealtime(true);
            return client;
        }

        TSharedPtr<SWidget> BuildViewportToolbar() override
        {
            return SNew(SCommonEditorViewportToolbarBase, SharedThis(this)).AddRealtimeButton(true);
        }

        void OnFocusViewportToSelection() override
        {
            auto bounds = FBox(ForceInit);
            if (auto actor = previewActor.Get())
            {
                auto components = TArray<UStaticMeshComponent*>{};
                actor->GetComponents(components);
                for (auto component : components)
                    if (component->IsRegistered() && component->IsVisible())
                        bounds += component->Bounds.GetBox();
            }
            if (bounds.IsValid) GetViewportClient()->FocusViewportOnBox(bounds);
        }

    private:
        TSharedPtr<FAdvancedPreviewScene> previewScene;
        TWeakObjectPtr<AActor> previewActor;
    };

    UMaterial* CreatePreviewColorMaterial()
    {
        auto material = NewObject<UMaterial>(GetTransientPackage(), NAME_None, RF_Transient);
        material->SetShadingModel(MSM_Unlit);
        auto color = NewObject<UMaterialExpressionVectorParameter>(material);
        color->ParameterName = TEXT("Color");
        color->DefaultValue = FLinearColor::White;
        material->GetExpressionCollection().AddExpression(color);
        material->GetEditorOnlyData()->EmissiveColor.Connect(0, color);
        material->PostEditChange();
        return material;
    }


}

    UMaterial* GetBuiltinCpuParticlePreviewMaterial(bool additive)
    {
        static TStrongObjectPtr<UMaterial> cached[2];
        const int32 idx = additive ? 1 : 0;
        if (cached[idx].IsValid()) return cached[idx].Get();
        auto* material = NewObject<UMaterial>(GetTransientPackage(), NAME_None, RF_Transient);
        material->MaterialDomain = MD_Surface;
        material->BlendMode = additive ? BLEND_Additive : BLEND_Translucent;
        material->SetShadingModel(MSM_Unlit);
        material->TwoSided = true;
        material->TranslucencyPass = MTP_BeforeDOF;
        const auto addExpr = [material]<typename T>()
        {
            auto* expr = NewObject<T>(material);
            material->GetExpressionCollection().AddExpression(expr);
            expr->Material = material;
            expr->UpdateMaterialExpressionGuid(true, false);
            return expr;
        };
        auto* uv = addExpr.operator()<UMaterialExpressionTextureCoordinate>();
        auto* rect = addExpr.operator()<UMaterialExpressionVectorParameter>();
        rect->ParameterName = TEXT("AtlasRect");
        rect->DefaultValue = FLinearColor(0, 0, 1, 1);
        auto* offset = addExpr.operator()<UMaterialExpressionComponentMask>();
        offset->R = true; offset->G = true; offset->B = false; offset->A = false;
        offset->Input.Connect(5, rect);
        auto* size = addExpr.operator()<UMaterialExpressionComponentMask>();
        size->R = false; size->G = false; size->B = true; size->A = true;
        size->Input.Connect(5, rect);
        auto* scaled = addExpr.operator()<UMaterialExpressionMultiply>();
        scaled->A.Connect(0, uv); scaled->B.Connect(0, size);
        auto* shifted = addExpr.operator()<UMaterialExpressionAdd>();
        shifted->A.Connect(0, scaled); shifted->B.Connect(0, offset);
        auto* texture = addExpr.operator()<UMaterialExpressionTextureSampleParameter2D>();
        texture->ParameterName = TEXT("Texture");
        texture->Texture = LoadObject<UTexture2D>(nullptr, TEXT("/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture"));
        texture->AutoSetSampleType();
        texture->Coordinates.Connect(0, shifted);
        auto* instanceColor = addExpr.operator()<UMaterialExpressionPerInstanceCustomData3Vector>();
        instanceColor->DataIndex = 0;
        instanceColor->ConstDefaultValue = FLinearColor::White;
        auto* rgb = addExpr.operator()<UMaterialExpressionMultiply>();
        rgb->A.Connect(0, texture); rgb->B.Connect(0, instanceColor);
        auto* instanceAlpha = addExpr.operator()<UMaterialExpressionPerInstanceCustomData>();
        instanceAlpha->DataIndex = 3;
        instanceAlpha->ConstDefaultValue = 1.0f;
        auto* alpha = addExpr.operator()<UMaterialExpressionMultiply>();
        alpha->A.Connect(4, texture); alpha->B.Connect(0, instanceAlpha);
        material->GetEditorOnlyData()->EmissiveColor.Connect(0, rgb);
        material->GetEditorOnlyData()->Opacity.Connect(0, alpha);
        auto needsRecompile = false;
        material->UpdateCachedExpressionData();
        material->SetMaterialUsage(needsRecompile, MATUSAGE_InstancedStaticMeshes);
        material->PostEditChange();
        cached[idx].Reset(material);
        return material;
    }

    FLibraEditorPreviewSession::FLibraEditorPreviewSession() = default;
FLibraEditorPreviewSession::~FLibraEditorPreviewSession() = default;

void FLibraEditorPreviewSession::Start()
{
    if (bStarted) return;
    bStarted = true;
    auto enabled = FPlatformMisc::GetEnvironmentVariable(TEXT("LIBRA_AUTOMATION_PREVIEW")) == TEXT("1") ||
        FParse::Param(FCommandLine::Get(), TEXT("LibraPreview"));
    UE_LOG(LogTemp, Display, TEXT("LIBERA FX preview bootstrap: %s."), enabled ? TEXT("enabled") : TEXT("disabled"));
    bTransportRequested = enabled;
    bPreviewRequested = enabled;
    bOpenRequested = enabled;
    bEngineReady = GIsRunning;
    viewerEndpoint.Start(FApp::GetProjectName());
    GConfig->GetBool(PreviewSettingsSection, TEXT("AutoConnect"), bAutoConnect, GEditorPerProjectIni);
    if (bAutoConnect)
    {
        bPreviewRequested = true;
        bOpenRequested = true;
        if (bEngineReady) DiscoverLibra();
    }
    postEngineInit = PostEngineInitDelegate().AddRaw(this, &FLibraEditorPreviewSession::HandlePostEngineInit);
    preExit = FCoreDelegates::OnPreExit.AddRaw(this, &FLibraEditorPreviewSession::Stop);
    FGlobalTabmanager::Get()->RegisterNomadTabSpawner(tabId,
        FOnSpawnTab::CreateRaw(this, &FLibraEditorPreviewSession::SpawnPreviewTab))
        .SetDisplayName(FText::FromString(TEXT("LIBERA FX Preview")))
        .SetAutoGenerateMenuEntry(false)
        .SetMenuType(ETabSpawnerMenuType::Hidden)
        .SetReuseTabMethod(FOnFindTabToReuse::CreateRaw(this, &FLibraEditorPreviewSession::FindPreviewTabToReuse));
    bTabRegistered = true;
    menuStartup = UToolMenus::RegisterStartupCallback(
        FSimpleMulticastDelegate::FDelegate::CreateRaw(this, &FLibraEditorPreviewSession::RegisterPreviewMenu));
    ticker = FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateRaw(this, &FLibraEditorPreviewSession::Tick));
}

void FLibraEditorPreviewSession::RegisterPreviewMenu()
{
    if (!bStarted) return;
    auto owner = FToolMenuOwnerScoped(this);
    auto menu = UToolMenus::Get()->ExtendMenu(menuName);
    auto& section = menu->FindOrAddSection(TEXT("LibraFxPreview"));
    section.AddMenuEntry(menuEntryId, FText::FromString(TEXT("LIBERA FX Preview")),
        FText::FromString(TEXT("Show the isolated LIBERA FX preview window.")), FSlateIcon(),
        FUIAction(FExecuteAction::CreateLambda([this] { OpenPreviewTab(); })));
}

TSharedPtr<SDockTab> FLibraEditorPreviewSession::FindPreviewTabToReuse(const FTabId& id) const
{
    (void)id;
    auto tab = previewTab.Pin();
    return tab.IsValid() && tab->GetParentDockTabStack().IsValid() && tab->GetDockArea().IsValid() ? tab : nullptr;
}

TSharedPtr<SDockTab> FLibraEditorPreviewSession::OpenPreviewTab()
{
    if (!bTabRegistered) return nullptr;
    bPreviewRequested = true;
    bOpenRequested = false;
    auto tab = FGlobalTabmanager::Get()->TryInvokeTab(tabId);
    if (!tab.IsValid()) return nullptr;
    previewTab = tab;
    tab->ActivateInParent(ETabActivationCause::SetDirectly);
    if (auto window = tab->GetParentWindow())
    {
        if (window->IsWindowMinimized()) window->Restore();
        window->ShowWindow();
        if (window != FGlobalTabmanager::Get()->GetRootWindow() && CountDockTabs(window->GetContent()) == 1)
        {
            auto metrics = FDisplayMetrics{};
            FDisplayMetrics::RebuildDisplayMetrics(metrics);
            auto recovered = FVector2f{};
            if (FindVisibleWindowPosition(window->GetPositionInScreen(), window->GetSizeInScreen(), metrics, recovered))
                window->MoveWindowTo(recovered);
        }
        window->BringToFront();
    }
    return tab;
}

void FLibraEditorPreviewSession::ConnectFromClipboard()
{
    discovery.Reset();
    discoveredPeers.Reset();
    auto text = FString{};
    FPlatformApplicationMisc::ClipboardPaste(text);
    AcceptConnectionInvitation(text);
}

void FLibraEditorPreviewSession::DiscoverLibra()
{
    check(IsInGameThread());
    discovery.Reset();
    discoveredPeers.Reset();
    discovery = MakeUnique<FLibraPreviewDiscovery>();
    auto overridePipe = FString{};
    FParse::Value(FCommandLine::Get(), TEXT("LibraPreviewDiscoveryPipe="), overridePipe);
    connectionNotice = TEXT("Finding Libra on this PC...");
    discovery->Begin(overridePipe);
}

void FLibraEditorPreviewSession::PollDiscovery()
{
    if (!discovery) return;
    auto peers = TArray<FLibraPreviewDiscoveryCandidate>{};
    auto error = FString{};
    if (!discovery->Poll(peers, error)) return;
    discovery.Reset();
    discoveredPeers = MoveTemp(peers);
    if (!error.IsEmpty()) connectionNotice = MoveTemp(error);
    else if (discoveredPeers.Num() == 1) SelectDiscoveredPeer(discoveredPeers[0].pipeName);
    else connectionNotice = discoveredPeers.IsEmpty() ? TEXT("No Libra instance found. Search again or paste connection information.") :
        TEXT("Multiple Libra instances found. Select one explicitly.");
}

void FLibraEditorPreviewSession::SelectDiscoveredPeer(const FString& pipeName)
{
    auto selected = discoveredPeers.FindByPredicate([&pipeName](const auto& peer) { return peer.pipeName == pipeName; });
    if (!selected) return;
    auto invitation = selected->invitation;
    discoveredPeers.Reset();
    AcceptConnectionInvitation(invitation);
}

bool FLibraEditorPreviewSession::AcceptConnectionInvitation(const FString& text)
{
    check(IsInGameThread());
    auto invitation = MakeUnique<FLibraPreviewConnectionInvitation>();
    auto error = FString{};
    if (!ParseLibraPreviewConnectionInvitation(text, *invitation, error))
    {
        connectionNotice = MoveTemp(error);
        return false;
    }
    pendingConnection = MoveTemp(invitation);
    connectionNotice.Reset();
    bTransportRequested = true;
    bPreviewRequested = true;
    if (bEngineReady && connection.IsValid()) ApplyPendingConnection();
    return true;
}

void FLibraEditorPreviewSession::ApplyPendingConnection()
{
    if (!bEngineReady || !connection.IsValid() || !pendingConnection) return;
    auto invitation = MoveTemp(pendingConnection);
    connection->DisconnectFromEditor();
    connection->Host = invitation->host;
    connection->Port = invitation->port;
    connection->AccessToken = invitation->accessToken;
    connection->CertificateFingerprint = invitation->certificateFingerprint;
    if (connection->ConnectToEditor())
        connection->QueueEditorMessage(TEXT("{\"type\":\"resync\"}"));
}

void FLibraEditorPreviewSession::DisconnectPreview()
{
    discovery.Reset();
    discoveredPeers.Reset();
    pendingConnection.Reset();
    bTransportRequested = false;
    connectionNotice.Reset();
    if (connection.IsValid())
    {
        connection->DisconnectFromEditor();
        connection->AccessToken.Reset();
        connection->CertificateFingerprint.Reset();
    }
}

void FLibraEditorPreviewSession::OnPreviewTabClosed(TSharedRef<SDockTab> tab)
{
    if (previewTab.Pin() == tab) previewTab.Reset();
    bOpenRequested = false;
    tab->SetContent(SNullWidget::NullWidget);
}

TSharedRef<SDockTab> FLibraEditorPreviewSession::SpawnPreviewTab(const FSpawnTabArgs& args)
{
    (void)args;
    if (auto live = FindPreviewTabToReuse(FTabId(tabId))) return live.ToSharedRef();
    bPreviewRequested = true;
    auto tab = SNew(SDockTab).TabRole(ETabRole::NomadTab);
    previewTab = tab;
    tab->SetOnTabClosed(SDockTab::FOnTabClosedCallback::CreateRaw(this, &FLibraEditorPreviewSession::OnPreviewTabClosed));
    tab->SetContent(BuildPreviewContent());
    return tab;
}

void FLibraEditorPreviewSession::HandlePostEngineInit()
{
    bEngineReady = true;
    if (bAutoConnect && !discovery) DiscoverLibra();
}

TSharedRef<SWidget> FLibraEditorPreviewSession::BuildPreviewContent()
{
    if (!previewScene.IsValid() || !viewportWidget.IsValid() || !previewSettings.IsValid())
        return SNew(STextBlock)
            .Text(FText::FromString(TEXT("LIBERA FX Preview will initialize when the editor is ready.")));
    auto makeToggle = [this](const TCHAR* label, FName tag, bool (FAdvancedPreviewScene::*query)() const,
        void (FAdvancedPreviewScene::*toggle)()) -> TSharedRef<SWidget>
    {
        return SNew(SCheckBox)
            .Tag(tag)
            .IsChecked_Lambda([this, query]
            {
                return previewScene.IsValid() && (previewScene.Get()->*query)()
                    ? ECheckBoxState::Checked : ECheckBoxState::Unchecked;
            })
            .OnCheckStateChanged_Lambda([this, toggle](ECheckBoxState)
            {
                if (previewScene.IsValid())
                {
                    (previewScene.Get()->*toggle)();
                    SavePreviewProfile();
                }
            })
            [
                SNew(STextBlock).Text(FText::FromString(label))
            ];
    };
    return SNew(SVerticalBox)
            + SVerticalBox::Slot().AutoHeight().Padding(8, 6)
            [
                SNew(SHorizontalBox)
                + SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 16, 0)
                [
                    SNew(SCheckBox)
                    .Tag(FName(TEXT("LibraFx.Preview.Settings")))
                    .IsChecked_Lambda([this]
                    {
                        return bSettingsVisible ? ECheckBoxState::Checked : ECheckBoxState::Unchecked;
                    })
                    .OnCheckStateChanged_Lambda([this](ECheckBoxState state)
                    {
                        bSettingsVisible = state == ECheckBoxState::Checked;
                        SavePreviewProfile();
                    })
                    [
                        SNew(STextBlock).Text(FText::FromString(TEXT("Settings")))
                    ]
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 16, 0)
                [
                    SNew(SCheckBox)
                    .IsChecked_Lambda([this]
                    {
                        return previewScene.IsValid() && viewportClient.IsValid() &&
                            IsPreviewGridEnabled(*previewScene, *viewportClient)
                            ? ECheckBoxState::Checked : ECheckBoxState::Unchecked;
                    })
                    .OnCheckStateChanged_Lambda([this](ECheckBoxState)
                    {
                        if (previewScene.IsValid() && viewportClient.IsValid())
                        {
                            TogglePreviewGrid(*previewScene, *viewportClient);
                            SavePreviewProfile();
                        }
                    })
                    [
                        SNew(STextBlock).Text(FText::FromString(TEXT("Grid")))
                    ]
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 16, 0)
                [
                    makeToggle(TEXT("Floor"), NAME_None, &FAdvancedPreviewScene::IsFloorEnabled,
                        &FAdvancedPreviewScene::HandleToggleFloor)
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 12, 0)
                [
                    makeToggle(TEXT("Environment"), FName(TEXT("LibraFx.Preview.Environment")), &FAdvancedPreviewScene::IsEnvironmentEnabled,
                        &FAdvancedPreviewScene::HandleToggleEnvironment)
                ]
                + SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 4, 0)
                [
                    SNew(STextBlock).Text(FText::FromString(TEXT("Units/m")))
                ]
                + SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
                [
                    SNew(SSpinBox<double>)
                    .MinDesiredWidth(64.0f)
                    .MinValue(0.01)
                    .MaxValue(10000.0)
                    .Value_Lambda([this] { return previewUnitsPerMetre; })
                    .OnValueCommitted_Lambda([this](double newValue, ETextCommit::Type)
                    {
                        previewUnitsPerMetre = FMath::Clamp(newValue, 0.01, 10000.0);
                        if (effect.IsValid()) effect->HostUnitsPerMetre = previewUnitsPerMetre;
                        SavePreviewProfile();
                    })
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(16, 0, 0, 0)
                [
                    SNew(SButton).Text(FText::FromString(TEXT("Connect to Libra")))
                    .ToolTipText(FText::FromString(TEXT("Find a running Libra instance on this PC and connect securely.")))
                    .IsEnabled_Lambda([this] { return !discovery || !discovery->IsRunning(); })
                    .OnClicked_Lambda([this] { DiscoverLibra(); return FReply::Handled(); })
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(8, 0, 0, 0)
                [
                    SNew(SCheckBox)
                    .IsChecked_Lambda([this] { return bAutoConnect ? ECheckBoxState::Checked : ECheckBoxState::Unchecked; })
                    .OnCheckStateChanged_Lambda([this](ECheckBoxState value)
                    {
                        bAutoConnect = value == ECheckBoxState::Checked;
                        GConfig->SetBool(PreviewSettingsSection, TEXT("AutoConnect"), bAutoConnect, GEditorPerProjectIni);
                        GConfig->Flush(false, GEditorPerProjectIni);
                    })
                    [SNew(STextBlock).Text(FText::FromString(TEXT("Connect on startup")))]
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(8, 0, 0, 0)
                [
                    SNew(SComboButton)
                    .Visibility_Lambda([this] { return discoveredPeers.Num() > 1 ? EVisibility::Visible : EVisibility::Collapsed; })
                    .OnGetMenuContent_Lambda([this]
                    {
                        auto menu = FMenuBuilder(true, nullptr);
                        for (const auto& peer : discoveredPeers)
                        {
                            auto name = peer.pipeName;
                            menu.AddMenuEntry(FText::FromString(TEXT("Libra process ") + name.Mid(16)),
                                FText::FromString(TEXT("Connect to this local Libra instance.")), FSlateIcon(),
                                FUIAction(FExecuteAction::CreateLambda([this, name] { SelectDiscoveredPeer(name); })));
                        }
                        return menu.MakeWidget();
                    })
                    .ButtonContent()[SNew(STextBlock).Text(FText::FromString(TEXT("Select Libra")))]
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(8, 0, 0, 0)
                [
                    SNew(SButton).Text(FText::FromString(TEXT("Paste Connection")))
                    .ToolTipText(FText::FromString(TEXT("Advanced: copy connection information in Libra Connections, then paste it here.")))
                    .OnClicked_Lambda([this] { ConnectFromClipboard(); return FReply::Handled(); })
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(8, 0, 0, 0)
                [
                    SNew(SButton).Text(FText::FromString(TEXT("Disconnect")))
                    .IsEnabled_Lambda([this] { return bTransportRequested || pendingConnection.IsValid() || discovery.IsValid() || !discoveredPeers.IsEmpty(); })
                    .OnClicked_Lambda([this] { DisconnectPreview(); return FReply::Handled(); })
                ]
                + SHorizontalBox::Slot().AutoWidth().Padding(16, 0, 0, 0)
                [
                    SNew(SButton)
                    .Text(FText::FromString(TEXT("Frame")))
                    .OnClicked_Lambda([this]
                    {
                        auto bounds = FBox(ForceInit);
                        if (effect.IsValid())
                        {
                            auto transforms = TArray<FTransform>{};
                            auto snapshotBounds = TArray<FBoxSphereBounds>{};
                            auto drawIds = TArray<FGuid>{};
                            auto containerIds = TArray<FGuid>{};
                            auto instanceIds = TArray<int64>{};
                            effect->GetDrawSnapshots(transforms, snapshotBounds, drawIds, containerIds, instanceIds);
                            for (const auto& item : snapshotBounds) bounds += item.GetBox();
                        }
                        if (viewportClient.IsValid() && bounds.IsValid)
                            viewportClient->FocusViewportOnBox(bounds);
                        return FReply::Handled();
                    })
                ]
                + SHorizontalBox::Slot().FillWidth(1).Padding(16, 0, 0, 0)
                [
                    SNew(STextBlock).Text_Lambda([this]
                    {
                        if (!connectionNotice.IsEmpty()) return FText::FromString(connectionNotice);
                        return FText::FromString(connection.IsValid() && !connection->Status.IsEmpty()
                            ? connection->Status : FString(TEXT("Disconnected — preview only")));
                    })
                ]
            ]
            + SVerticalBox::Slot().FillHeight(1)
            [
                SNew(SSplitter)
                + SSplitter::Slot().Value(0.68f)
                [
                    viewportWidget.ToSharedRef()
                ]
                + SSplitter::Slot().Value(0.32f)
                [
                    SNew(SBox)
                    .Visibility_Lambda([this]
                    {
                        return bSettingsVisible ? EVisibility::Visible : EVisibility::Collapsed;
                    })
                    [
                        previewSettings.ToSharedRef()
                    ]
                ]
            ];
}

void FLibraEditorPreviewSession::SavePreviewProfile()
{
    if (!previewScene.IsValid()) return;
    if (auto profile = previewScene->GetCurrentProfile())
    {
        GConfig->SetString(PreviewSettingsSection, TEXT("ProfileName"), *profile->ProfileName, GEditorPerProjectIni);
        GConfig->SetBool(PreviewSettingsSection, TEXT("SettingsVisible"), bSettingsVisible, GEditorPerProjectIni);
        GConfig->SetDouble(PreviewSettingsSection, TEXT("HostUnitsPerMetre"), previewUnitsPerMetre, GEditorPerProjectIni);
#if !UE_VERSION_OLDER_THAN(5, 8, 0)
        if (viewportClient.IsValid())
            GConfig->SetBool(PreviewSettingsSection, TEXT("GridVisible"),
                IsPreviewGridEnabled(*previewScene, *viewportClient), GEditorPerProjectIni);
#endif
        GConfig->Flush(false, GEditorPerProjectIni);
        UAssetViewerSettings::Get()->Save();
    }
}

bool FLibraEditorPreviewSession::Tick(float deltaTime)
{
    viewerEndpoint.SetConnected(connection.IsValid() && connection->bIsConnected);
    auto incomingInvitation = FString{};
    if (viewerEndpoint.TakeInvitation(incomingInvitation) && AcceptConnectionInvitation(incomingInvitation))
        bOpenRequested = true;
    PollDiscovery();
    if (!bEngineReady || !bPreviewRequested) return true;
    if (!world.IsValid())
    {
        if (!GEngine) return true;
        UE_LOG(LogTemp, Display, TEXT("LIBERA FX preview: creating isolated scene."));
        auto userSettings = GetMutableDefault<UEditorPerProjectUserSettings>();
        previousPreviewProfileIndex = userSettings->AssetViewerProfileIndex;
#if UE_VERSION_OLDER_THAN(5, 8, 0)
        previousPreviewProfileName = userSettings->AssetViewerProfileName;
#endif
        auto construction = FPreviewScene::ConstructionValues{};
        construction.SetTransactional(false).SetCreatePhysicsScene(false).AllowAudioPlayback(false);
        previewScene = MakeShared<FAdvancedPreviewScene>(construction);
        auto settings = UAssetViewerSettings::Get();
        auto profileName = FString(TEXT("LIBERA FX"));
        GConfig->GetBool(PreviewSettingsSection, TEXT("SettingsVisible"), bSettingsVisible, GEditorPerProjectIni);
        auto profileIndex = settings->Profiles.IndexOfByPredicate([&profileName](const FPreviewSceneProfile& profile)
        {
            return profile.ProfileName == profileName;
        });
        if (profileIndex == INDEX_NONE)
        {
            auto profile = FPreviewSceneProfile{};
            profile.ProfileName = TEXT("LIBERA FX");
            profile.bSharedProfile = false;
            profile.bIsEngineDefaultProfile = false;
            profileIndex = settings->Profiles.Add(profile);
            settings->PostEditChange();
            settings->Save();
        }
        previewScene->SetProfileIndex(profileIndex);
        userSettings->AssetViewerProfileIndex = profileIndex;
#if UE_VERSION_OLDER_THAN(5, 8, 0)
        userSettings->AssetViewerProfileName = profileName;
#endif
        world.Reset(previewScene->GetWorld());
        if (!world.IsValid())
        {
            UE_LOG(LogTemp, Error, TEXT("LIBERA FX preview: isolated world creation failed."));
            Stop();
            return false;
        }
        auto parameters = FActorSpawnParameters{};
        parameters.ObjectFlags = RF_Transient;
        parameters.Name = FName(TEXT("LibraFxPreview"));
        auto actor = world->SpawnActor<AActor>(AActor::StaticClass(), FTransform::Identity, parameters);
        if (!actor) { Stop(); return false; }
        previewActor.Reset(actor);

        auto rootComponent = NewObject<USceneComponent>(actor, NAME_None, RF_Transient);
        actor->SetRootComponent(rootComponent);
        actor->AddInstanceComponent(rootComponent);
        rootComponent->RegisterComponent();
        GConfig->GetDouble(PreviewSettingsSection, TEXT("HostUnitsPerMetre"), previewUnitsPerMetre, GEditorPerProjectIni);
        previewUnitsPerMetre = FMath::Clamp(previewUnitsPerMetre, 0.01, 10000.0);
        effect.Reset(NewObject<ULibraEffectComponent>(actor));
        effect->bAutoPlay = false;
        actor->AddInstanceComponent(effect.Get());
        effect->RegisterComponent();
        effect->HostUnitsPerMetre = previewUnitsPerMetre;
        effect->bUseSharedSimulation = false;
        effect->AssetBindings.Add({TEXT("builtin:cpu-particle-additive"), GetBuiltinCpuParticlePreviewMaterial(true)});
        effect->AssetBindings.Add({TEXT("builtin:cpu-particle-alpha"), GetBuiltinCpuParticlePreviewMaterial(false)});
        effect->AssetBindings.Add({TEXT("builtin:unlit-color"), CreatePreviewColorMaterial()});
        previewCamera.Reset(NewObject<UCameraComponent>(actor, NAME_None, RF_Transient));
        previewCamera->SetupAttachment(rootComponent);
        actor->AddInstanceComponent(previewCamera.Get());
        previewCamera->RegisterComponent();
        effect->EffectCameraOverride = previewCamera.Get();
        connection.Reset(NewObject<ULibraEditorConnectionComponent>(actor));
        connection->Target = effect.Get();
        connection->Host = TEXT("127.0.0.1");
        auto port = int32{8766};
        auto portText = FPlatformMisc::GetEnvironmentVariable(TEXT("LIBRA_PREVIEW_PORT"));
        if (!portText.IsEmpty()) LexTryParseString(port, *portText);
        connection->Port = port;
        connection->AccessToken = FPlatformMisc::GetEnvironmentVariable(TEXT("LIBRA_PREVIEW_TOKEN"));
        connection->CertificateFingerprint = FPlatformMisc::GetEnvironmentVariable(TEXT("LIBRA_PREVIEW_FINGERPRINT"));
        UE_LOG(LogTemp, Display, TEXT("LIBERA FX preview: endpoint port %d, credential fields %s."),
            port, !connection->AccessToken.IsEmpty() && !connection->CertificateFingerprint.IsEmpty()
                ? TEXT("present") : TEXT("missing"));
        connection->EditorAssetRequestHandler = [](const FString& request)
        {
            return FLibraEditorAssetAdapter::ProcessRequest(request);
        };
        actor->AddInstanceComponent(connection.Get());
        connection->RegisterComponent();
        if (pendingConnection)
            ApplyPendingConnection();
        else if (bTransportRequested && !connection->ConnectToEditor())
        {
            UE_LOG(LogTemp, Warning, TEXT("LIBERA FX preview could not connect: %s"), *connection->Status);
        }
        else if (bTransportRequested)
            UE_LOG(LogTemp, Display, TEXT("LIBERA FX preview: transport connected and hello queued."));
        viewportWidget = SNew(SLibraPreviewViewport, previewScene.ToSharedRef(), actor);
        viewportClient = viewportWidget->GetViewportClient();
        if (auto* floor = const_cast<UStaticMeshComponent*>(previewScene->GetFloorMeshComponent()))
            floor->SetWorldScale3D(FVector(25.0f, 25.0f, 1.0f));
#if UE_VERSION_OLDER_THAN(5, 8, 0)
        viewportClient->EngineShowFlags.SetGrid(previewScene->IsGridEnabled());
#else
        auto gridVisible = viewportClient->IsSetShowGridChecked();
        GConfig->GetBool(PreviewSettingsSection, TEXT("GridVisible"), gridVisible, GEditorPerProjectIni);
        viewportClient->EngineShowFlags.SetGrid(gridVisible);
#endif
        auto& previewModule = FModuleManager::LoadModuleChecked<FAdvancedPreviewSceneModule>(TEXT("AdvancedPreviewScene"));
        previewSettings = previewModule.CreateAdvancedPreviewSceneSettingsWidget(previewScene.ToSharedRef());
        if (auto tab = previewTab.Pin())
            tab->SetContent(BuildPreviewContent());
        else if (bOpenRequested)
            OpenPreviewTab();
    }
    if (viewportClient.IsValid() && previewScene.IsValid())
        if (auto profile = previewScene->GetCurrentProfile())
            profile->SetShowFlags(viewportClient->EngineShowFlags);
    if (viewportClient.IsValid() && previewCamera.IsValid())
        previewCamera->SetWorldLocationAndRotation(viewportClient->GetViewLocation(), viewportClient->GetViewRotation());
    if (effect->IsComponentTickEnabled())
        effect->TickComponent(deltaTime, LEVELTICK_All, &effect->PrimaryComponentTick);
    connection->TickComponent(deltaTime, LEVELTICK_All, &connection->PrimaryComponentTick);
    return true;
}

void FLibraEditorPreviewSession::Stop()
{
    viewerEndpoint.Stop();
    discovery.Reset();
    discoveredPeers.Reset();
    bStarted = false;
    if (menuStartup.IsValid()) UToolMenus::UnRegisterStartupCallback(menuStartup);
    menuStartup.Reset();
    UToolMenus::UnregisterOwner(this);
    if (preExit.IsValid()) FCoreDelegates::OnPreExit.Remove(preExit);
    preExit.Reset();
    if (postEngineInit.IsValid()) PostEngineInitDelegate().Remove(postEngineInit);
    postEngineInit.Reset();
    bPreviewRequested = false;
    bTransportRequested = false;
    bOpenRequested = false;
    pendingConnection.Reset();
    connectionNotice.Reset();
    if (ticker.IsValid()) FTSTicker::GetCoreTicker().RemoveTicker(ticker);
    ticker.Reset();
    SavePreviewProfile();
    if (auto tab = previewTab.Pin())
    {
        tab->SetOnTabClosed(SDockTab::FOnTabClosedCallback());
        tab->SetContent(SNullWidget::NullWidget);
        tab->RequestCloseTab();
    }
    previewTab.Reset();
    previewSettings.Reset();
    if (previousPreviewProfileIndex != INDEX_NONE)
    {
        auto userSettings = GetMutableDefault<UEditorPerProjectUserSettings>();
        userSettings->AssetViewerProfileIndex = previousPreviewProfileIndex;
#if UE_VERSION_OLDER_THAN(5, 8, 0)
        userSettings->AssetViewerProfileName = previousPreviewProfileName;
#endif
        previousPreviewProfileIndex = INDEX_NONE;
        previousPreviewProfileName.Reset();
    }
    if (bTabRegistered)
    {
        FGlobalTabmanager::Get()->UnregisterNomadTabSpawner(tabId);
        bTabRegistered = false;
    }
    viewportClient.Reset();
    viewportWidget.Reset();
    if (connection.IsValid())
    {
        connection->EditorAssetRequestHandler = {};
        connection->DisconnectFromEditor();
        connection->UnregisterComponent();
    }
    if (effect.IsValid())
    {
        effect->Stop();
        effect->UnregisterComponent();
    }
    if (previewActor.IsValid()) previewActor->Destroy();
    connection.Reset();
    effect.Reset();
    previewCamera.Reset();
    previewActor.Reset();
    world.Reset();
    previewScene.Reset();
}

#if WITH_DEV_AUTOMATION_TESTS
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraPreviewDeferredTabTest, "Libra.Editor.Preview.DeferredTabRestoration",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraPreviewDeferredTabTest::RunTest(const FString&)
{
    auto session = FLibraEditorPreviewSession{};
    auto args = FSpawnTabArgs(TSharedPtr<SWindow>{}, FTabId(PreviewTabId));
    auto tab = session.SpawnPreviewTab(args);
    TestEqual(TEXT("Early layout restoration creates a placeholder without dereferencing scene resources"),
        tab->GetContent()->GetType(), FName(TEXT("STextBlock")));
    TestTrue(TEXT("A pre-initialization ticker remains registered"), session.Tick(0));
    TestFalse(TEXT("Restoration does not create a world before engine initialization"), session.world.IsValid());
    session.Stop();
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraPreviewMenuTest, "Libra.Editor.Preview.MenuShowLifecycle",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraPreviewMenuTest::RunTest(const FString&)
{
    if (!FSlateApplication::IsInitialized())
    {
        AddError(TEXT("Native preview menu tests require an initialized Slate application."));
        return false;
    }
    auto manager = FGlobalTabmanager::Get();
    auto hadProductionSpawner = manager->HasTabSpawner(PreviewTabId);
    auto session = FLibraEditorPreviewSession{};
    session.tabId = TEXT("LibraFxPreviewLifecycleTest");
    session.menuEntryId = TEXT("LibraFxPreviewLifecycleTestShow");
    session.menuName = TEXT("LibraFxPreviewLifecycleTest.Window");
    session.Start();
    session.Start();
    session.bEngineReady = false;
    session.bTransportRequested = false;
    session.bOpenRequested = false;
    session.RegisterPreviewMenu();
    session.RegisterPreviewMenu();
    auto menu = UToolMenus::Get()->FindMenu(session.menuName);
    if (!TestNotNull(TEXT("Owned preview menu is registered"), menu)) { session.Stop(); return false; }
    auto entries = 0;
    for (const auto& section : menu->Sections)
        for (const auto& entry : section.Blocks)
            if (entry.Name == session.menuEntryId) ++entries;
    TestEqual(TEXT("Repeated startup/menu registration produces one owned Show entry"), entries, 1);
    auto first = session.OpenPreviewTab();
    if (!TestTrue(TEXT("Show attaches an actual preview tab"), first.IsValid() && first->GetParentWindow().IsValid()))
    { session.Stop(); return false; }
    auto second = session.OpenPreviewTab();
    TestTrue(TEXT("Repeated Show reuses the same attached tab"), second == first);
    auto window = first->GetParentWindow();
    if (window != manager->GetRootWindow())
    {
        window->HideWindow();
        session.OpenPreviewTab();
        TestTrue(TEXT("Show recovers a hidden floating preview window"), window->IsVisible());
        if (CountDockTabs(window->GetContent()) == 1)
        {
            window->MoveWindowTo(FVector2f(1000000.f, 1000000.f));
            session.OpenPreviewTab();
            auto displays = FDisplayMetrics{};
            FDisplayMetrics::RebuildDisplayMetrics(displays);
            auto unused = FVector2f{};
            TestFalse(TEXT("Show brings a fully offscreen dedicated preview back to a work area"),
                FindVisibleWindowPosition(window->GetPositionInScreen(), window->GetSizeInScreen(), displays, unused));
        }
    }
    first->RequestCloseTab();
    session.Tick(0);
    TestFalse(TEXT("Intentional close does not reopen on the next ticker"), session.previewTab.IsValid());
    auto fresh = session.OpenPreviewTab();
    TestTrue(TEXT("Show after close creates a fresh attached tab, not a retained ghost"),
        fresh.IsValid() && fresh != first && fresh->GetParentWindow().IsValid() && fresh->GetParentWindow()->IsVisible());
    session.Stop();
    TestTrue(TEXT("Stopping the fixture preserves the production spawner"),
        manager->HasTabSpawner(PreviewTabId) == hadProductionSpawner);
    auto hasOwnedEntry = false;
    for (const auto& section : menu->Sections)
        hasOwnedEntry |= section.FindEntry(session.menuEntryId) != nullptr;
    TestFalse(TEXT("Stopping unregisters only the fixture's owned menu entry"), hasOwnedEntry);

    auto displays = FDisplayMetrics{};
    displays.PrimaryDisplayWorkAreaRect = FPlatformRect{0, 0, 1920, 1080};
    auto leftMonitor = FMonitorInfo{};
    leftMonitor.WorkArea = FPlatformRect{-1920, 0, 0, 1080};
    displays.MonitorInfo.Add(leftMonitor);
    auto rightMonitor = FMonitorInfo{};
    rightMonitor.WorkArea = displays.PrimaryDisplayWorkAreaRect;
    displays.MonitorInfo.Add(rightMonitor);
    auto recovered = FVector2f{};
    TestFalse(TEXT("Preview on a secondary display is not repositioned"),
        FindVisibleWindowPosition(FVector2f(-1000, 100), FVector2f(600, 400), displays, recovered));
    TestFalse(TEXT("Partly visible preview windows retain their position"),
        FindVisibleWindowPosition(FVector2f(1900, 100), FVector2f(600, 400), displays, recovered));
    TestTrue(TEXT("Fully offscreen preview recovery selects a visible work area"),
        FindVisibleWindowPosition(FVector2f(4000, 100), FVector2f(600, 400), displays, recovered));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraPreviewEmptySceneTest, "Libra.Editor.Preview.EmptySceneAndDeferredConnection",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraPreviewEmptySceneTest::RunTest(const FString&)
{
    auto session = FLibraEditorPreviewSession{};
    auto invitation = FString(TEXT("{\"type\":\"librafx_preview_connection\",\"version\":1,\"host\":\"127.0.0.1\","
        "\"port\":8766,\"accessToken\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\","
        "\"certificateFingerprint\":\"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\"}"));
    TestTrue(TEXT("Validated connection can be queued before scene/engine readiness"),
        session.AcceptConnectionInvitation(invitation));
    session.Tick(0);
    TestFalse(TEXT("Pending manual connection does not create a world before engine readiness"), session.world.IsValid());
    TestTrue(TEXT("Malformed clipboard text leaves the last validated pending invitation intact"),
        !session.AcceptConnectionInvitation(TEXT("{}")) && session.pendingConnection.IsValid());
    session.DisconnectPreview();
    session.bEngineReady = true;
    session.bPreviewRequested = true;
    TestTrue(TEXT("An empty isolated preview initializes without a transport"), session.Tick(0));
    if (!TestTrue(TEXT("Empty preview actor exists"), session.previewActor.IsValid())) { session.Stop(); return false; }
    auto gridWasEnabled = IsPreviewGridEnabled(*session.previewScene, *session.viewportClient);
    TogglePreviewGrid(*session.previewScene, *session.viewportClient);
    session.Tick(0);
    TestEqual(TEXT("Grid toggle survives preview profile application"),
        IsPreviewGridEnabled(*session.previewScene, *session.viewportClient), !gridWasEnabled);
    session.SavePreviewProfile();
#if !UE_VERSION_OLDER_THAN(5, 8, 0)
    auto savedGridVisible = gridWasEnabled;
    TestTrue(TEXT("Viewport-owned grid visibility is persisted"),
        GConfig->GetBool(PreviewSettingsSection, TEXT("GridVisible"), savedGridVisible, GEditorPerProjectIni));
    TestEqual(TEXT("Saved grid visibility matches the viewport"), savedGridVisible, !gridWasEnabled);
#endif
    TogglePreviewGrid(*session.previewScene, *session.viewportClient);
    session.SavePreviewProfile();
    auto meshes = TArray<UStaticMeshComponent*>{};
    session.previewActor->GetComponents(meshes);
    TestEqual(TEXT("Empty preview contains no static mesh placeholder"), meshes.Num(), 0);
    auto camera = session.viewportClient->GetViewLocation();
    auto frameKey = FKeyEvent(EKeys::F, FModifierKeysState{}, uint32{0}, false, uint32{0}, uint32{0});
    session.viewportWidget->OnKeyDown(FGeometry{}, frameKey);
    TestTrue(TEXT("Framing an empty scene does not fabricate bounds or move the camera"),
        session.viewportClient->GetViewLocation().Equals(camera));
    session.viewportClient->SetViewLocation(FVector(123, 456, 789));
    auto args = FSpawnTabArgs(TSharedPtr<SWindow>{}, FTabId(PreviewTabId));
    auto closedTab = session.SpawnPreviewTab(args);
    closedTab->RequestCloseTab();
    auto reopenedTab = session.SpawnPreviewTab(args);
    TestTrue(TEXT("Closing and recreating preview content preserves the existing camera"),
        reopenedTab != closedTab && session.viewportClient->GetViewLocation().Equals(FVector(123, 456, 789)));
    session.Stop();
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraPreviewProfileRestartTest, "Libra.Editor.Preview.ProfileRestart",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraPreviewProfileRestartTest::RunTest(const FString&)
{
    auto fixture = FString{};
    auto phase = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraPreviewProfileTestDir="), fixture) ||
        !FParse::Value(FCommandLine::Get(), TEXT("LibraPreviewProfileTestPhase="), phase))
    {
        AddInfo(TEXT("ProfileRestart requires an isolated -UserDir and an explicit Write or Read phase."));
        return true;
    }
    if (!TestTrue(TEXT("Both active config paths belong to the isolated fixture"),
            !FPaths::IsRelative(fixture) &&
            FPaths::IsUnderDirectory(FPaths::ConvertRelativePathToFull(GEditorIni), fixture) &&
            FPaths::IsUnderDirectory(FPaths::ConvertRelativePathToFull(GEditorPerProjectIni), fixture)))
    {
        return false;
    }
    auto marker = FPaths::Combine(fixture, TEXT("profile-written.txt"));
    auto writing = phase == TEXT("Write");
    if (!TestTrue(TEXT("Write must use a fresh fixture; Read must use the completed Write fixture"),
            (writing || phase == TEXT("Read")) && IFileManager::Get().FileExists(*marker) != writing))
    {
        return false;
    }

    auto settings = GetMutableDefault<UEditorPerProjectUserSettings>();
    const auto& profiles = UAssetViewerSettings::Get()->Profiles;
    auto originalProfile = profiles.IndexOfByPredicate([](const FPreviewSceneProfile& profile)
    {
        return profile.ProfileName != TEXT("LIBERA FX");
    });
    if (!TestTrue(TEXT("A separate stock profile is available for the restore check"), profiles.IsValidIndex(originalProfile)))
    {
        return false;
    }
    auto restoreIndex = TGuardValue<int32>(settings->AssetViewerProfileIndex, originalProfile);
#if UE_VERSION_OLDER_THAN(5, 8, 0)
    auto originalName = profiles[originalProfile].ProfileName;
    auto restoreName = TGuardValue<FString>(settings->AssetViewerProfileName, originalName);
#endif
    auto session = FLibraEditorPreviewSession{};
    session.bEngineReady = true;
    session.bPreviewRequested = true;
    if (!TestTrue(TEXT("Create an isolated preview scene"), session.Tick(0) && session.previewScene.IsValid()))
    {
        session.Stop();
        return false;
    }
    auto content = session.BuildPreviewContent();
    auto findCheckBox = [](auto&& self, const TSharedRef<SWidget>& widget, FName tag) -> TSharedPtr<SCheckBox>
    {
        if (widget->GetTag() == tag)
        {
            return StaticCastSharedRef<SCheckBox>(widget);
        }
        auto children = widget->GetChildren();
        for (auto index = 0; index < children->Num(); ++index)
        {
            if (auto match = self(self, children->GetChildAt(index), tag))
            {
                return match;
            }
        }
        return nullptr;
    };
    auto environment = findCheckBox(findCheckBox, content, FName(TEXT("LibraFx.Preview.Environment")));
    auto panel = findCheckBox(findCheckBox, content, FName(TEXT("LibraFx.Preview.Settings")));
    if (!TestTrue(TEXT("Find the production Environment and Settings checkboxes"), environment.IsValid() && panel.IsValid()))
    {
        session.Stop();
        return false;
    }
    auto profile = session.previewScene->GetCurrentProfile();
    TestTrue(TEXT("The preview uses its dedicated LIBERA FX profile"),
        profile && profile->ProfileName == TEXT("LIBERA FX"));
    if (writing)
    {
        if (!TestTrue(TEXT("The fresh fixture starts with both controls checked"), environment->IsChecked() && panel->IsChecked()))
        {
            session.Stop();
            return false;
        }
        auto activate = [](SCheckBox& control)
        {
            auto input = FKeyEvent(EKeys::SpaceBar, FModifierKeysState{}, uint32{0}, false, uint32{0}, uint32{0});
            return control.OnKeyDown(FGeometry{}, input).IsEventHandled() &&
                control.OnKeyUp(FGeometry{}, input).IsEventHandled();
        };
        TestTrue(TEXT("Keyboard activation is handled by the Environment checkbox"), activate(*environment));
        TestTrue(TEXT("Keyboard activation is handled by the Settings checkbox"), activate(*panel));
    }
    TestFalse(TEXT("Environment control is unchecked"), environment->IsChecked());
    TestFalse(TEXT("Environment is disabled in the preview scene"), session.previewScene->IsEnvironmentEnabled());
    profile = session.previewScene->GetCurrentProfile();
    TestTrue(TEXT("The dedicated profile records the Environment change"), profile && !profile->bShowEnvironment);
    TestFalse(TEXT("Settings control is unchecked"), panel->IsChecked());
    TestFalse(TEXT("The settings panel visibility is disabled"), session.bSettingsVisible);
    content = SNullWidget::NullWidget;
    environment.Reset();
    panel.Reset();
    session.Stop();
    TestEqual(TEXT("Stopping the preview restores the editor's prior profile index"),
        settings->AssetViewerProfileIndex, originalProfile);
#if UE_VERSION_OLDER_THAN(5, 8, 0)
    TestEqual(TEXT("Stopping the preview restores the editor's prior profile name"),
        settings->AssetViewerProfileName, originalName);
#endif
    if (writing && !HasAnyErrors())
    {
        TestTrue(TEXT("Write the completed-phase marker"), FFileHelper::SaveStringToFile(TEXT("Written"), *marker));
    }
    return !HasAnyErrors();
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraPreviewCameraTest, "Libra.Editor.Preview.NativeCameraNavigation",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraPreviewCameraTest::RunTest(const FString&)
{
    auto construction = FPreviewScene::ConstructionValues{};
    construction.SetTransactional(false).SetCreatePhysicsScene(false).AllowAudioPlayback(false);
    auto scene = MakeShared<FAdvancedPreviewScene>(construction);
    auto parameters = FActorSpawnParameters{};
    parameters.ObjectFlags = RF_Transient;
    auto actor = scene->GetWorld()->SpawnActor<AActor>(AActor::StaticClass(), FTransform::Identity, parameters);
    if (!TestNotNull(TEXT("Navigation fixture uses an isolated preview actor"), actor)) return false;
    auto mesh = NewObject<UStaticMeshComponent>(actor, NAME_None, RF_Transient);
    mesh->SetStaticMesh(LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cube.Cube")));
    actor->SetRootComponent(mesh);
    actor->AddInstanceComponent(mesh);
    mesh->RegisterComponent();
    auto widget = SNew(SLibraPreviewViewport, scene, actor);
    auto client = widget->GetViewportClient();
    auto wheel = FInputKeyEventArgs(client->Viewport, FInputDeviceId::CreateFromInternalId(0),
        EKeys::MouseScrollUp, IE_Pressed, uint64{0});
    auto initialLocation = client->GetViewLocation();
    TestTrue(TEXT("Mouse wheel is forwarded to native camera handling"), client->InputKey(wheel));
    TestFalse(TEXT("Native mouse-wheel dolly moves the camera"), client->GetViewLocation().Equals(initialLocation));

    auto navigationLocation = client->GetViewLocation();
    auto navigationRotation = client->GetViewRotation();
    mesh->SetWorldLocation(FVector(400, 250, 150));
    widget->Tick(FGeometry{}, FPlatformTime::Seconds(), 0);
    TestTrue(TEXT("Visible preview participates in native editor camera ticks"), client->IsVisible());
    TestTrue(TEXT("Preview content updates do not reset camera position"),
        client->GetViewLocation().Equals(navigationLocation));
    TestTrue(TEXT("Preview content updates do not reset camera orientation"),
        client->GetViewRotation().Equals(navigationRotation));

    auto frameKey = FKeyEvent(EKeys::F, FModifierKeysState{}, uint32{0}, false, uint32{0}, uint32{0});
    TestTrue(TEXT("Standard viewport command routing handles F"),
        widget->OnKeyDown(FGeometry{}, frameKey).IsEventHandled());
    TestTrue(TEXT("Framing updates the orbit pivot to the visible preview, not the user's level"),
        client->GetLookAtLocation().Equals(mesh->Bounds.GetBox().GetCenter()));
    return true;
}

#endif
