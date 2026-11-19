#include "LibraEditorPreviewSession.h"
#include "Misc/App.h"

#include "AdvancedPreviewScene.h"
#include "AdvancedPreviewSceneModule.h"
#include "AssetRegistry/AssetRegistryModule.h"
#include "AssetCompilingManager.h"
#include "Async/TaskGraphInterfaces.h"
#include "AssetViewerSettings.h"
#include "Engine/DirectionalLight.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "Engine/TextureRenderTarget2D.h"
#include "Components/SceneCaptureComponent2D.h"
#include "GameFramework/Actor.h"
#include "InputKeyEventArgs.h"
#include "LibraEditorAssetAdapter.h"
#include "LibraEditorGpuPreview.h"
#include "LibraEditorDebugDraw.h"
#include "LibraPreviewConnectionInvitation.h"
#include "LibraEffectComponent.h"
#include "LibraEffectAsset.h"
#include "LibraEditorConnectionComponent.h"
#include "Materials/Material.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "MaterialShared.h"
#include "RHI.h"
#include "RenderingThread.h"
#include "Materials/MaterialExpressionAdd.h"
#include "Materials/MaterialExpressionComponentMask.h"
#include "Materials/MaterialExpressionMultiply.h"
#include "Materials/MaterialExpressionTextureCoordinate.h"
#include "Materials/MaterialExpressionTextureSample.h"
#include "Materials/MaterialExpressionTextureSampleParameter2D.h"
#include "Materials/MaterialExpressionVectorParameter.h"
#include "Components/StaticMeshComponent.h"
#include "Components/SceneComponent.h"
#include "EditorViewportClient.h"
#include "SEditorViewport.h"
#include "SCommonEditorViewportToolbarBase.h"
#include "Editor/EditorPerProjectUserSettings.h"
#include "Framework/Application/SlateApplication.h"
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
#include "Misc/FileHelper.h"
#include "HAL/FileManager.h"
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
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "liberafx/LbSceneBindings.h"
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

    TSharedPtr<FJsonObject> ParseJson(const FString& text)
    {
        auto reader = TJsonReaderFactory<>::Create(text);
        auto object = TSharedPtr<FJsonObject>{};
        return FJsonSerializer::Deserialize(reader, object) ? object : nullptr;
    }

    FString SerializeJson(const TSharedRef<FJsonObject>& object)
    {
        auto text = FString{};
        auto writer = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&text);
        FJsonSerializer::Serialize(object, writer);
        writer->Close();
        return text;
    }

    bool IsAllowedPreviewPath(const FString& path)
    {
        return FPackageName::IsValidObjectPath(path) &&
            (path.StartsWith(TEXT("/Game/")) || path.StartsWith(TEXT("/Engine/")));
    }

    bool ReadInteger(const TSharedPtr<FJsonObject>& object, const TCHAR* key, int32& value)
    {
        auto number = double{0};
        if (!object.IsValid() || !object->TryGetNumberField(key, number) || !FMath::IsFinite(number) ||
            number < MIN_int32 || number > MAX_int32 || FMath::FloorToDouble(number) != number)
            return false;
        value = static_cast<int32>(number);
        return true;
    }

    TArray<double> ReadDefaults(const TSharedPtr<FJsonObject>& input, int32 width)
    {
        auto result = TArray<double>{};
        result.Init(0.0, FMath::Clamp(width, 1, 4));
        auto defaults = static_cast<const TArray<TSharedPtr<FJsonValue>>*>(nullptr);
        if (input.IsValid() && input->TryGetArrayField(TEXT("defaults"), defaults))
            for (auto index = 0; index < result.Num() && index < defaults->Num(); ++index)
                if ((*defaults)[index].IsValid() && (*defaults)[index]->Type == EJson::Number)
                    result[index] = (*defaults)[index]->AsNumber();
        return result;
    }

    FVector4 ToVector4(const TArray<double>& values, const FVector4& fallback = FVector4(0, 0, 0, 0))
    {
        auto result = fallback;
        for (auto index = 0; index < values.Num() && index < 4; ++index)
            result[index] = values[index];
        return result;
    }

    bool IsFinite(const FVector4& value)
    {
        for (auto index = 0; index < 4; ++index)
            if (!FMath::IsFinite(value[index]) || FMath::Abs(value[index]) > TNumericLimits<float>::Max())
                return false;
        return true;
    }

    UMaterial* CreateSpriteMaterial(UTexture2D* texture)
    {
        auto material = NewObject<UMaterial>(GetTransientPackage(), NAME_None, RF_Transient);
        material->MaterialDomain = MD_Surface;
        material->BlendMode = BLEND_Translucent;
        material->SetShadingModel(MSM_Unlit);
        material->TwoSided = true;

        auto textureSample = NewObject<UMaterialExpressionTextureSampleParameter2D>(material);
        textureSample->ParameterName = TEXT("SpriteTexture");
        textureSample->Texture = texture;
        textureSample->AutoSetSampleType();
        auto coordinates = NewObject<UMaterialExpressionTextureCoordinate>(material);
        auto scaleParameter = NewObject<UMaterialExpressionVectorParameter>(material);
        scaleParameter->ParameterName = TEXT("AtlasScale");
        scaleParameter->DefaultValue = FLinearColor::White;
        auto offsetParameter = NewObject<UMaterialExpressionVectorParameter>(material);
        offsetParameter->ParameterName = TEXT("AtlasOffset");
        offsetParameter->DefaultValue = FLinearColor::Transparent;
        auto scaleMask = NewObject<UMaterialExpressionComponentMask>(material);
        scaleMask->R = true;
        scaleMask->G = true;
        scaleMask->Input.Connect(0, scaleParameter);
        auto offsetMask = NewObject<UMaterialExpressionComponentMask>(material);
        offsetMask->R = true;
        offsetMask->G = true;
        offsetMask->Input.Connect(0, offsetParameter);
        auto multiply = NewObject<UMaterialExpressionMultiply>(material);
        multiply->A.Connect(0, coordinates);
        multiply->B.Connect(0, scaleMask);
        auto add = NewObject<UMaterialExpressionAdd>(material);
        add->A.Connect(0, multiply);
        add->B.Connect(0, offsetMask);
        textureSample->Coordinates.Connect(0, add);
        auto tintParameter = NewObject<UMaterialExpressionVectorParameter>(material);
        tintParameter->ParameterName = TEXT("Tint");
        tintParameter->DefaultValue = FLinearColor::White;
        auto tintedColor = NewObject<UMaterialExpressionMultiply>(material);
        tintedColor->A.Connect(0, textureSample);
        tintedColor->B.Connect(0, tintParameter);
        auto tintedAlpha = NewObject<UMaterialExpressionMultiply>(material);
        tintedAlpha->A.Connect(4, textureSample);
        tintedAlpha->B.Connect(4, tintParameter);
        auto& expressions = material->GetExpressionCollection();
        expressions.AddExpression(textureSample);
        expressions.AddExpression(coordinates);
        expressions.AddExpression(scaleParameter);
        expressions.AddExpression(offsetParameter);
        expressions.AddExpression(scaleMask);
        expressions.AddExpression(offsetMask);
        expressions.AddExpression(multiply);
        expressions.AddExpression(add);
        expressions.AddExpression(tintParameter);
        expressions.AddExpression(tintedColor);
        expressions.AddExpression(tintedAlpha);
        material->GetEditorOnlyData()->EmissiveColor.Connect(0, tintedColor);
        material->GetEditorOnlyData()->Opacity.Connect(0, tintedAlpha);
        material->PostEditChange();
        return material;
    }

    bool HasPreviewParameter(UMaterialInterface* material, const FMaterialParameterInfo& info, const FString& kind)
    {
        auto infos = TArray<FMaterialParameterInfo>{};
        auto ids = TArray<FGuid>{};
        if (kind == TEXT("scalar")) material->GetAllScalarParameterInfo(infos, ids);
        else if (kind == TEXT("vector")) material->GetAllVectorParameterInfo(infos, ids);
        else if (kind == TEXT("texture")) material->GetAllTextureParameterInfo(infos, ids);
        else return false;
        return infos.Contains(info);
    }

    int32 PreviewShaderState(UMaterialInterface* material)
    {
        if (!material || GUsingNullRHI) return -1;
        auto resource = material->GetMaterialResource(GMaxRHIShaderPlatform);
        if (!resource) return -1;
        if (!resource->IsCompilationFinished()) return 0;
        auto map = resource->GetGameThreadShaderMap();
        return map && map->IsValidForRendering() ? 1 : -1;
    }

    FString DescribePreviewMaterial(UMaterialInterface* material)
    {
        if (!material) return TEXT("material=<null>");
        auto infos = TArray<FMaterialParameterInfo>{};
        auto ids = TArray<FGuid>{};
        material->GetAllVectorParameterInfo(infos, ids);
        auto description = FString::Printf(TEXT("material=%s, vectors=["), *material->GetPathName());
        for (auto i = 0; i < FMath::Min(infos.Num(), 8); ++i)
            description += FString::Printf(TEXT("%s%s(association=%d,index=%d)"),
                i ? TEXT(", ") : TEXT(""), *infos[i].Name.ToString().Left(96),
                static_cast<int32>(infos[i].Association), infos[i].Index);
        if (infos.Num() > 8) description += TEXT(", ...");
        return description + TEXT("]");
    }

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

    bool SetParameterValue(UMaterialInstanceDynamic* material, const FMaterialParameterInfo& info,
        const FString& kind, const FVector4& value, FVector4& actualValue)
    {
        if (!material || !IsFinite(value) || !HasPreviewParameter(material->Parent, info, kind)) return false;
        if (kind == TEXT("scalar"))
        {
            material->SetScalarParameterValueByInfo(info, static_cast<float>(value.X));
            auto actual = float{0};
            if (!material->GetScalarParameterValue(info, actual)) return false;
            actualValue = FVector4(actual, 0, 0, 0);
            return IsFinite(actualValue);
        }
        else if (kind == TEXT("vector"))
        {
            material->SetVectorParameterValueByInfo(info, FLinearColor(value.X, value.Y, value.Z, value.W));
            auto actual = FLinearColor{};
            if (!material->GetVectorParameterValue(info, actual)) return false;
            actualValue = FVector4(actual.R, actual.G, actual.B, actual.A);
            return IsFinite(actualValue);
        }
        return false;
    }
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
    if (debugDraw) debugDraw->Clear();
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
                + SHorizontalBox::Slot().AutoWidth()
                [
                    makeToggle(TEXT("Environment"), FName(TEXT("LibraFx.Preview.Environment")), &FAdvancedPreviewScene::IsEnvironmentEnabled,
                        &FAdvancedPreviewScene::HandleToggleEnvironment)
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
                        for (const auto& node : renderNodes)
                            if (node.component.IsValid()) bounds += node.component->Bounds.GetBox();
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
        effect.Reset(NewObject<ULibraEffectComponent>(actor));
        effect->bAutoPlay = false;
        actor->AddInstanceComponent(effect.Get());
        effect->RegisterComponent();
        connection.Reset(NewObject<ULibraEditorConnectionComponent>(actor));
        gpuPreview = MakeUnique<FLibraEditorGpuPreview>(actor);
        debugDraw = MakeUnique<FLibraEditorDebugDraw>();
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
        connection->EditorSceneConfigHandler = [this](const FString& request, int64 sequence)
        {
            ConfigureScene(request, sequence);
        };
        connection->EditorRenderStatusHandler = [this](int64 sequence, double time)
        {
            return UpdateScene(sequence, time);
        };
        connection->EditorGpuConfigHandler = [this](const FString& request, int64 sequence)
        {
            if (gpuPreview) gpuPreview->Configure(request, sequence);
        };
        connection->EditorGpuPlaybackHandler = [this](int64 sequence, bool playing, double time, bool programChanged, double rate)
        {
            if (programChanged) acceptedRenderRootSequence = sequence;
            if (gpuPreview) gpuPreview->SetPlayback(sequence, playing, time, programChanged, rate);
        };
        connection->EditorGpuResetHandler = [this]
        {
            if (debugDraw) debugDraw->Clear();
            acceptedRenderRootSequence = -1;
            pendingRenderNodes.Reset();
            pendingRenderProgramSequence = -1;
            if (gpuPreview) gpuPreview->Reset();
        };
        connection->EditorGpuStatusHandler = [this](int64 sequence)
        {
            return gpuPreview ? gpuPreview->Status(sequence) : FString{};
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
#if !UE_VERSION_OLDER_THAN(5, 8, 0)
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
    if (effect->IsComponentTickEnabled())
        effect->TickComponent(deltaTime, LEVELTICK_All, &effect->PrimaryComponentTick);
    connection->TickComponent(deltaTime, LEVELTICK_All, &connection->PrimaryComponentTick);
    CommitPendingScene();
    if (gpuPreview) gpuPreview->Tick(deltaTime, connection->bIsConnected);
    return true;
}

void FLibraEditorPreviewSession::ConfigureScene(const FString& json, int64 programSequence)
{
    check(IsInGameThread());
    if (debugDraw) debugDraw->Clear();
    pendingRenderNodes.Reset();
    pendingRenderProgramSequence = -1;
    sceneErrors.Reset();
    if (json.IsEmpty())
    {
        for (auto& node : renderNodes)
            if (node.component.IsValid()) node.component->DestroyComponent();
        renderNodes.Reset();
        renderProgramSequence = programSequence;
        return;
    }
    if (!effect.IsValid() || !previewActor.IsValid()) return;
    auto root = ParseJson(json);
    if (!root.IsValid())
    {
        sceneErrors.Add(TEXT("Invalid render configuration JSON."));
        return;
    }
    auto nodes = static_cast<const TArray<TSharedPtr<FJsonValue>>*>(nullptr);
    if (!root->TryGetArrayField(TEXT("nodes"), nodes) || nodes->Num() > 128)
    {
        sceneErrors.Add(TEXT("Render configuration must contain at most 128 nodes."));
        return;
    }
    if (debugDraw)
    {
        auto error = FString{};
        if (!debugDraw->Configure(*root, *previewActor.Get(), *effect.Get(), programSequence, error))
        { sceneErrors.Add(error); return; }
    }
    auto byNode = TMap<FGuid, FLibraValueHandle>{};
    auto candidateNodes = TArray<FRenderNode>{};
    auto nodeIds = TSet<FGuid>{};
    auto target = effect.Get();
    auto rootComponent = previewActor->GetRootComponent();
    for (const auto& value : *nodes)
    {
        auto object = value.IsValid() ? value->AsObject() : nullptr;
        if (!object.IsValid()) { sceneErrors.Add(TEXT("Render node must be an object.")); continue; }
        auto idText = FString{};
        auto kind = FString{};
        if (object->TryGetStringField(TEXT("kind"), kind) &&
            (kind == TEXT("debugText") || kind == TEXT("debugModel"))) continue;
        auto assetPath = FString{};
        auto id = FGuid{};
        if (!object->TryGetStringField(TEXT("nodeId"), idText) || !FGuid::Parse(idText, id) || !id.IsValid() ||
            !object->TryGetStringField(TEXT("kind"), kind) ||
            (kind != TEXT("model") && kind != TEXT("sprite")) ||
            !object->TryGetStringField(TEXT("assetPath"), assetPath) || !IsAllowedPreviewPath(assetPath))
        {
            sceneErrors.Add(TEXT("Render node has an invalid ID, kind, or Unreal asset path."));
            continue;
        }
        if (nodeIds.Contains(id)) { sceneErrors.Add(TEXT("Duplicate render node ID.")); continue; }
        nodeIds.Add(id);
        auto spriteTexture = kind == TEXT("sprite") ? LoadObject<UTexture2D>(nullptr, *assetPath) : nullptr;
        if (kind == TEXT("sprite") && !spriteTexture)
        {
            sceneErrors.Add(TEXT("Sprite Texture2D could not be loaded: ") + assetPath);
            continue;
        }
        auto meshPath = kind == TEXT("sprite") ? TEXT("/Engine/BasicShapes/Plane.Plane") : *assetPath;
        auto mesh = LoadObject<UStaticMesh>(nullptr, meshPath);
        if (!mesh)
        {
            sceneErrors.Add(TEXT("Render mesh is unsupported or could not be loaded: ") + assetPath);
            continue;
        }

        auto node = FRenderNode{};
        node.id = id;
        node.kind = kind;
        node.assetPath = assetPath;
        object->TryGetBoolField(TEXT("billboard"), node.billboard);
        if (kind == TEXT("sprite"))
        {
            const TSharedPtr<FJsonObject>* animationObject = nullptr;
            if (object->TryGetObjectField(TEXT("spriteAnimation"), animationObject))
            {
                auto& animation = node.spriteAnimation;
                auto version = int32{};
                auto textureWidth = int32{}, textureHeight = int32{};
                const TArray<TSharedPtr<FJsonValue>>* cells = nullptr;
                const TArray<TSharedPtr<FJsonValue>>* frames = nullptr;
                if (!animationObject || !*animationObject ||
                    !ReadInteger(*animationObject, TEXT("version"), version) || (version != 1 && version != 2) ||
                    !ReadInteger(*animationObject, TEXT("textureWidth"), textureWidth) ||
                    !ReadInteger(*animationObject, TEXT("textureHeight"), textureHeight) ||
                    textureWidth < 1 || textureWidth > 8192 || textureHeight < 1 || textureHeight > 8192 ||
                    !(*animationObject)->TryGetNumberField(TEXT("fps"), animation.fps) ||
                    !FMath::IsFinite(animation.fps) || animation.fps <= 0 || animation.fps > 10000 ||
                    !ReadInteger(*animationObject, TEXT("startFrame"), animation.startFrame) ||
                    !ReadInteger(*animationObject, TEXT("endFrame"), animation.endFrame) ||
                    !(*animationObject)->TryGetBoolField(TEXT("loop"), animation.loop) ||
                    !(*animationObject)->TryGetArrayField(TEXT("cells"), cells) ||
                    cells->Num() < 1 || cells->Num() > 512 ||
                    !(*animationObject)->TryGetArrayField(TEXT("frames"), frames) ||
                    frames->Num() < 1 || frames->Num() > 4096 ||
                    animation.startFrame < 0 || animation.endFrame < animation.startFrame ||
                    animation.endFrame >= frames->Num())
                {
                    sceneErrors.Add(TEXT("Sprite animation metadata is missing, unsupported or out of range."));
                    continue;
                }
                animation.present = true;
                animation.version = version;
                auto cellIds = TSet<FGuid>{};
                auto names = TSet<FString>{};
                auto invalid = false;
                for (const auto& cellValue : *cells)
                {
                    auto cellObject = cellValue.IsValid() ? cellValue->AsObject() : nullptr;
                    auto cell = FSpriteCell{};
                    auto cellId = FString{};
                    auto u = double{}, v = double{}, uvWidth = double{}, uvHeight = double{};
                    if (!cellObject.IsValid() ||
                        !cellObject->TryGetStringField(TEXT("id"), cellId) || !FGuid::Parse(cellId, cell.id) ||
                        !cell.id.IsValid() || !cellObject->TryGetStringField(TEXT("name"), cell.name) ||
                        cell.name.TrimStartAndEnd().IsEmpty() || cell.name.Len() > 80 ||
                        names.Contains(cell.name.TrimStartAndEnd().ToLower()) ||
                        !ReadInteger(cellObject, TEXT("x"), cell.x) ||
                        !ReadInteger(cellObject, TEXT("y"), cell.y) ||
                        !ReadInteger(cellObject, TEXT("width"), cell.pixelWidth) ||
                        !ReadInteger(cellObject, TEXT("height"), cell.pixelHeight) ||
                        cell.x < 0 || cell.y < 0 || cell.pixelWidth <= 0 || cell.pixelHeight <= 0 ||
                        static_cast<int64>(cell.x) + cell.pixelWidth > textureWidth ||
                        static_cast<int64>(cell.y) + cell.pixelHeight > textureHeight ||
                        !cellObject->TryGetNumberField(TEXT("u"), u) ||
                        !cellObject->TryGetNumberField(TEXT("v"), v) ||
                        !cellObject->TryGetNumberField(TEXT("uvWidth"), uvWidth) ||
                        !cellObject->TryGetNumberField(TEXT("uvHeight"), uvHeight) ||
                        !FMath::IsFinite(u) || !FMath::IsFinite(v) ||
                        !FMath::IsFinite(uvWidth) || !FMath::IsFinite(uvHeight) ||
                        FMath::Abs(u - static_cast<double>(cell.x) / textureWidth) > 1e-9 ||
                        FMath::Abs(v - static_cast<double>(cell.y) / textureHeight) > 1e-9 ||
                        FMath::Abs(uvWidth - static_cast<double>(cell.pixelWidth) / textureWidth) > 1e-9 ||
                        FMath::Abs(uvHeight - static_cast<double>(cell.pixelHeight) / textureHeight) > 1e-9 ||
                        cellIds.Contains(cell.id))
                    {
                        invalid = true;
                        break;
                    }
                    names.Add(cell.name.TrimStartAndEnd().ToLower());
                    cellIds.Add(cell.id);
                    cell.u = static_cast<float>(u);
                    cell.v = static_cast<float>(v);
                    cell.width = static_cast<float>(uvWidth);
                    cell.height = static_cast<float>(uvHeight);
                    animation.cells.Add(cell);
                }
                if (invalid)
                {
                    sceneErrors.Add(TEXT("Sprite animation contains an invalid or duplicate pixel/UV rectangle."));
                    continue;
                }
                auto frameIds = TSet<FGuid>{};
                for (const auto& frameValue : *frames)
                {
                    auto frameObject = frameValue.IsValid() ? frameValue->AsObject() : nullptr;
                    auto frame = FSpriteFrame{};
                    auto frameId = FString{}, cellId = FString{};
                    if (!frameObject.IsValid() ||
                        !frameObject->TryGetStringField(TEXT("id"), frameId) || !FGuid::Parse(frameId, frame.id) ||
                        !frame.id.IsValid() || frameIds.Contains(frame.id) ||
                        !frameObject->TryGetStringField(TEXT("cellId"), cellId) || !FGuid::Parse(cellId, frame.cellId) ||
                        !frame.cellId.IsValid() ||
                        !frameObject->TryGetNumberField(TEXT("durationFrames"), frame.durationFrames) ||
                        !FMath::IsFinite(frame.durationFrames) || frame.durationFrames <= 0 ||
                        frame.durationFrames > 1000000)
                    {
                        invalid = true;
                        break;
                    }
                    frame.cellIndex = animation.cells.IndexOfByPredicate(
                        [&](const auto& cell) { return cell.id == frame.cellId; });
                    if (frame.cellIndex == INDEX_NONE)
                    {
                        invalid = true;
                        break;
                    }
                    frameIds.Add(frame.id);
                    animation.durations.Add(frame.durationFrames);
                    animation.frames.Add(frame);
                }
                if (invalid)
                {
                    sceneErrors.Add(TEXT("Sprite animation contains an invalid frame reference or duration."));
                    continue;
                }
                if (version == 2 && !animation.composition.Read(*animationObject, animation.fps, animation.loop,
                    animation.durations, animation.startFrame, animation.endFrame, node.billboard))
                {
                    sceneErrors.Add(TEXT("Invalid layered sprite clock, order or pose keys."));
                    continue;
                }
            }
        }
        auto component = NewObject<UStaticMeshComponent>(previewActor.Get(), NAME_None, RF_Transient);
        component->SetStaticMesh(mesh);
        if (node.spriteAnimation.version == 2)
            component->SetTranslucentSortPriority(node.spriteAnimation.composition.Order);
        component->SetupAttachment(rootComponent);
        node.component.Reset(component);
        if (component->GetNumMaterials() <= 0)
        { sceneErrors.Add(TEXT("Render mesh has no usable material slots.")); continue; }

        const TArray<TSharedPtr<FJsonValue>>* materialConfigs = nullptr;
        object->TryGetArrayField(TEXT("materials"), materialConfigs);
        auto materialOverrides = TMap<int32, TSharedPtr<FJsonObject>>{};
        if (materialConfigs)
        {
            if (materialConfigs->Num() > 256) { sceneErrors.Add(TEXT("Too many material overrides.")); continue; }
            for (const auto& materialValue : *materialConfigs)
                if (auto materialObject = materialValue.IsValid() ? materialValue->AsObject() : nullptr)
                {
                    auto slot = 0;
                    if (!ReadInteger(materialObject, TEXT("slot"), slot) || slot < 0 ||
                        slot >= component->GetNumMaterials() || materialOverrides.Contains(slot))
                    { sceneErrors.Add(TEXT("Invalid or duplicate material slot.")); continue; }
                    materialOverrides.Add(slot, materialObject);
                }
                else sceneErrors.Add(TEXT("Material override must be an object."));
        }
        for (auto slot = 0; slot < component->GetNumMaterials(); ++slot)
        {
            auto parentMaterial = component->GetMaterial(slot);
            if (auto overrideData = materialOverrides.Find(slot))
            {
                auto materialPath = FString{};
                if ((*overrideData)->TryGetStringField(TEXT("path"), materialPath) && !materialPath.IsEmpty())
                {
                    auto loaded = kind == TEXT("model") && materialPath == TEXT("builtin:unlit-color")
                        ? static_cast<UMaterialInterface*>(CreatePreviewColorMaterial())
                        : (IsAllowedPreviewPath(materialPath) ? LoadObject<UMaterialInterface>(nullptr, *materialPath) : nullptr);
                    if (!loaded) { sceneErrors.Add(TEXT("Material could not be loaded: ") + materialPath); continue; }
                    parentMaterial = loaded;
                }
            }
            if (kind == TEXT("sprite") && slot == 0 && !materialOverrides.Contains(slot))
                parentMaterial = CreateSpriteMaterial(spriteTexture);
            if (!parentMaterial) { sceneErrors.Add(TEXT("Render mesh has an unavailable material slot.")); continue; }
            if (kind == TEXT("model") && assetPath.StartsWith(TEXT("/Engine/BasicShapes/")) &&
                parentMaterial->GetPathName().StartsWith(TEXT("/Engine/BasicShapes/BasicShapeMaterial")) &&
                !HasPreviewParameter(parentMaterial, FMaterialParameterInfo(TEXT("Color")), TEXT("vector")))
            {
                const TArray<TSharedPtr<FJsonValue>>* requestedInputs = nullptr;
                if (object->TryGetArrayField(TEXT("inputs"), requestedInputs))
                    for (const auto& requestedValue : *requestedInputs)
                    {
                        auto requested = requestedValue.IsValid() ? requestedValue->AsObject() : nullptr;
                        const TSharedPtr<FJsonObject>* parameter = nullptr;
                        auto name = FString{};
                        auto parameterSlot = 0;
                        if (requested && requested->TryGetObjectField(TEXT("materialParameter"), parameter) &&
                            parameter && *parameter && (*parameter)->TryGetStringField(TEXT("name"), name) &&
                            ReadInteger(*parameter, TEXT("slot"), parameterSlot) && parameterSlot == slot && name == TEXT("Color"))
                        { parentMaterial = CreatePreviewColorMaterial(); break; }
                    }
            }
            auto dynamic = UMaterialInstanceDynamic::Create(parentMaterial, previewActor.Get());
            component->SetMaterial(slot, dynamic);
            node.materials.SetNum(FMath::Max(node.materials.Num(), slot + 1));
            node.materials[slot].Reset(dynamic);
            if (auto overrideData = materialOverrides.Find(slot))
            {
                const TArray<TSharedPtr<FJsonValue>>* parameters = nullptr;
                if ((*overrideData)->TryGetArrayField(TEXT("parameters"), parameters))
                {
                    if (parameters->Num() > 256) { sceneErrors.Add(TEXT("Too many material parameter overrides.")); continue; }
                    for (const auto& parameterValue : *parameters)
                        if (auto parameter = parameterValue.IsValid() ? parameterValue->AsObject() : nullptr)
                        {
                            auto name = FString{};
                            auto parameterKind = FString{};
                            auto texturePath = FString{};
                            if (!parameter->TryGetStringField(TEXT("name"), name) ||
                                !parameter->TryGetStringField(TEXT("kind"), parameterKind))
                            { sceneErrors.Add(TEXT("Malformed material parameter override.")); continue; }
                            auto association = static_cast<int32>(EMaterialParameterAssociation::GlobalParameter);
                            auto index = -1;
                            ReadInteger(parameter, TEXT("association"), association);
                            ReadInteger(parameter, TEXT("index"), index);
                            auto info = FMaterialParameterInfo(FName(*name),
                                static_cast<EMaterialParameterAssociation>(FMath::Clamp(association, 0, 2)), index);
                            if (association < 0 || association > 2 || index < -1 ||
                                !HasPreviewParameter(parentMaterial, info, parameterKind))
                            { sceneErrors.Add(TEXT("Material parameter does not exist: ") + name); continue; }
                            if (parameterKind == TEXT("texture"))
                            {
                                parameter->TryGetStringField(TEXT("value"), texturePath);
                                auto texture = IsAllowedPreviewPath(texturePath) ? LoadObject<UTexture>(nullptr, *texturePath) : nullptr;
                                if (!texture) { sceneErrors.Add(TEXT("Material texture could not be loaded: ") + texturePath); continue; }
                                dynamic->SetTextureParameterValueByInfo(info, texture);
                                auto actual = static_cast<UTexture*>(nullptr);
                                if (!dynamic->GetTextureParameterValue(info, actual) || actual != texture)
                                    sceneErrors.Add(TEXT("Material texture override was not applied: ") + name);
                            }
                            else
                            {
                                auto requested = FVector4{};
                                auto valid = true;
                                if (parameterKind == TEXT("scalar"))
                                    valid = parameter->TryGetNumberField(TEXT("value"), requested.X);
                                else
                                {
                                    const TArray<TSharedPtr<FJsonValue>>* components = nullptr;
                                    valid = parameter->TryGetArrayField(TEXT("value"), components) && components->Num() == 4;
                                    if (valid)
                                        for (auto componentIndex = 0; componentIndex < 4; ++componentIndex)
                                        {
                                            if (!(*components)[componentIndex].IsValid() || (*components)[componentIndex]->Type != EJson::Number)
                                            { valid = false; break; }
                                            requested[componentIndex] = (*components)[componentIndex]->AsNumber();
                                        }
                                }
                                auto actual = FVector4{};
                                if (!valid || !SetParameterValue(dynamic, info, parameterKind, requested, actual))
                                    sceneErrors.Add(TEXT("Material default could not be applied/read back: ") + name);
                            }
                        }
                        else sceneErrors.Add(TEXT("Material parameter must be an object."));
                }
            }
        }
        if (kind == TEXT("sprite"))
        {
            auto spriteMaterial = node.materials.IsEmpty() ? nullptr : node.materials[0].Get();
            if (spriteMaterial)
            {
                if (!HasPreviewParameter(spriteMaterial->Parent, FMaterialParameterInfo(TEXT("SpriteTexture")), TEXT("texture")) ||
                    !HasPreviewParameter(spriteMaterial->Parent, FMaterialParameterInfo(TEXT("AtlasScale")), TEXT("vector")) ||
                    !HasPreviewParameter(spriteMaterial->Parent, FMaterialParameterInfo(TEXT("AtlasOffset")), TEXT("vector")))
                    sceneErrors.Add(TEXT("Sprite material must expose SpriteTexture, AtlasScale and AtlasOffset."));
                spriteMaterial->SetTextureParameterValue(TEXT("SpriteTexture"), spriteTexture);
                auto actual = static_cast<UTexture*>(nullptr);
                if (!spriteMaterial->GetTextureParameterValue(FMaterialParameterInfo(TEXT("SpriteTexture")), actual) || actual != spriteTexture)
                    sceneErrors.Add(TEXT("Sprite texture did not reach its atlas material."));
            }
        }
        const TArray<TSharedPtr<FJsonValue>>* textureOverrides = nullptr;
        if (object->TryGetArrayField(TEXT("textures"), textureOverrides))
            for (const auto& textureValue : *textureOverrides)
                if (auto textureObject = textureValue.IsValid() ? textureValue->AsObject() : nullptr)
                {
                    auto name = FString{};
                    auto texturePath = FString{};
                    auto slot = 0;
                    auto association = static_cast<int32>(EMaterialParameterAssociation::GlobalParameter);
                    auto index = -1;
                    if (!textureObject->TryGetStringField(TEXT("name"), name) ||
                        !textureObject->TryGetStringField(TEXT("path"), texturePath) || texturePath.IsEmpty() ||
                        !IsAllowedPreviewPath(texturePath) ||
                        !ReadInteger(textureObject, TEXT("slot"), slot) ||
                        slot < 0 || slot >= node.materials.Num() || !node.materials[slot].IsValid())
                    { sceneErrors.Add(TEXT("Invalid texture override descriptor.")); continue; }
                    ReadInteger(textureObject, TEXT("association"), association);
                    ReadInteger(textureObject, TEXT("index"), index);
                    auto texture = LoadObject<UTexture>(nullptr, *texturePath);
                    auto info = FMaterialParameterInfo(FName(*name),
                        static_cast<EMaterialParameterAssociation>(FMath::Clamp(association, 0, 2)), index);
                    if (!texture || association < 0 || association > 2 || index < -1 ||
                        !HasPreviewParameter(node.materials[slot]->Parent, info, TEXT("texture")))
                    { sceneErrors.Add(TEXT("Texture or material parameter is unavailable: ") + name); continue; }
                    {
                        node.materials[slot]->SetTextureParameterValueByInfo(info, texture);
                        auto actual = static_cast<UTexture*>(nullptr);
                        if (!node.materials[slot]->GetTextureParameterValue(info, actual) || actual != texture)
                            sceneErrors.Add(TEXT("Texture override was not applied: ") + name);
                    }
                }
        const TArray<TSharedPtr<FJsonValue>>* inputValues = nullptr;
        object->TryGetArrayField(TEXT("inputs"), inputValues);
        if (inputValues && inputValues->Num() > 256) sceneErrors.Add(TEXT("Too many render inputs."));
        if (inputValues && inputValues->Num() <= 256)
            for (const auto& inputValue : *inputValues)
            {
                auto inputObject = inputValue.IsValid() ? inputValue->AsObject() : nullptr;
                if (!inputObject.IsValid()) { sceneErrors.Add(TEXT("Render input must be an object.")); continue; }
                auto input = FRenderInput{};
                if (!inputObject->TryGetStringField(TEXT("id"), input.id))
                { sceneErrors.Add(TEXT("Render input ID is missing.")); continue; }
                auto width = 1;
                if (!ReadInteger(inputObject, TEXT("width"), width))
                { sceneErrors.Add(TEXT("Render input width is missing or invalid.")); continue; }
                if (width < 1 || width > 4) { sceneErrors.Add(TEXT("Invalid render input width.")); continue; }
                const TArray<TSharedPtr<FJsonValue>>* defaultValues = nullptr;
                if (!inputObject->TryGetArrayField(TEXT("defaults"), defaultValues) || defaultValues->Num() != width)
                { sceneErrors.Add(TEXT("Render defaults must match their declared width.")); continue; }
                auto validDefaults = true;
                for (const auto& defaultValue : *defaultValues)
                    validDefaults &= defaultValue.IsValid() && defaultValue->Type == EJson::Number;
                if (!validDefaults) { sceneErrors.Add(TEXT("Render defaults must be numeric.")); continue; }
                input.defaults = ReadDefaults(inputObject, width);
                if (!IsFinite(ToVector4(input.defaults))) { sceneErrors.Add(TEXT("Non-finite render defaults.")); continue; }
                auto sourceText = FString{};
                if (inputObject->TryGetStringField(TEXT("sourceNodeId"), sourceText) && FGuid::Parse(sourceText, input.sourceNodeId) &&
                    input.sourceNodeId.IsValid())
                {
                    if (!byNode.Contains(input.sourceNodeId))
                    {
                        auto handle = FLibraValueHandle{};
                        if (target->BindValue(input.sourceNodeId, handle) == ELibraRuntimeResult::Ok)
                            byNode.Add(input.sourceNodeId, handle);
                    }
                    if (auto handle = byNode.Find(input.sourceNodeId))
                    {
                        input.handle = *handle;
                        input.hasBinding = true;
                    }
                    else sceneErrors.Add(TEXT("Render input source is unavailable in the accepted program: ") + sourceText);
                }
                else if (!sourceText.IsEmpty()) sceneErrors.Add(TEXT("Render source node ID is invalid."));
                ReadInteger(inputObject, TEXT("sourceComponent"), input.sourceComponent);
                if (input.sourceComponent < 0 || input.sourceComponent > 3)
                    sceneErrors.Add(TEXT("Invalid render source component."));
                const TSharedPtr<FJsonObject>* parameterPointer = nullptr;
                if (inputObject->TryGetObjectField(TEXT("materialParameter"), parameterPointer) &&
                    parameterPointer && parameterPointer->IsValid())
                {
                    auto parameter = *parameterPointer;
                    parameter->TryGetStringField(TEXT("name"), input.parameterName);
                    parameter->TryGetStringField(TEXT("kind"), input.parameterKind);
                    ReadInteger(parameter, TEXT("slot"), input.materialSlot);
                    ReadInteger(parameter, TEXT("association"), input.association);
                    ReadInteger(parameter, TEXT("index"), input.index);
                    auto info = FMaterialParameterInfo(FName(*input.parameterName),
                        static_cast<EMaterialParameterAssociation>(FMath::Clamp(input.association, 0, 2)), input.index);
                    if (input.materialSlot < 0 || input.materialSlot >= node.materials.Num() ||
                        !node.materials[input.materialSlot].IsValid() || input.association < 0 || input.association > 2 ||
                        input.index < -1 || (input.parameterKind != TEXT("scalar") && input.parameterKind != TEXT("vector")) ||
                        !HasPreviewParameter(node.materials[input.materialSlot]->Parent, info, input.parameterKind))
                        sceneErrors.Add(FString::Printf(
                            TEXT("Requested material parameter is unavailable: %s (slot=%d, association=%d, index=%d). Unreal associations: Layer=0, Blend=1, Global=2."),
                            *input.parameterName, input.materialSlot, input.association, input.index) +
                            TEXT(" ") + DescribePreviewMaterial(input.materialSlot >= 0 && input.materialSlot < node.materials.Num() &&
                                node.materials[input.materialSlot].IsValid() ? node.materials[input.materialSlot]->Parent : nullptr));
                }
                node.inputs.Add(MoveTemp(input));
            }
        candidateNodes.Add(MoveTemp(node));
    }
    if (!sceneErrors.IsEmpty()) return;
    pendingRenderNodes = MoveTemp(candidateNodes);
    pendingRenderProgramSequence = programSequence;
    pendingRenderStarted = FPlatformTime::Seconds();
    CommitPendingScene();
}

void FLibraEditorPreviewSession::CommitPendingScene()
{
    if (pendingRenderProgramSequence < 0) return;
    if (acceptedRenderRootSequence >= 0 && acceptedRenderRootSequence != pendingRenderProgramSequence)
    {
        pendingRenderNodes.Reset();
        pendingRenderProgramSequence = -1;
        sceneErrors = {TEXT("Pending render configuration became stale before its shaders were ready.")};
        return;
    }
    auto waiting = false;
    for (const auto& node : pendingRenderNodes)
        for (const auto& material : node.materials)
        {
            auto shaderState = PreviewShaderState(material.Get());
            if (shaderState < 0)
            {
                sceneErrors = {TEXT("A preview material shader is unavailable or failed compilation; the previous scene was preserved.")};
                pendingRenderNodes.Reset();
                pendingRenderProgramSequence = -1;
                return;
            }
            waiting |= shaderState == 0;
        }
    if (waiting)
    {
        sceneErrors = {TEXT("Preview material shaders are compiling; the previous scene remains visible.")};
        if (FPlatformTime::Seconds() - pendingRenderStarted < 120) return;
        sceneErrors = {TEXT("Preview shader compilation timed out; the previous scene was preserved.")};
        pendingRenderNodes.Reset();
        pendingRenderProgramSequence = -1;
        return;
    }
    for (auto& node : renderNodes)
        if (node.component.IsValid()) node.component->DestroyComponent();
    renderNodes = MoveTemp(pendingRenderNodes);
    renderProgramSequence = pendingRenderProgramSequence;
    pendingRenderProgramSequence = -1;
    sceneErrors.Reset();
    for (auto& node : renderNodes)
    {
        previewActor->AddInstanceComponent(node.component.Get());
        node.component->RegisterComponent();
    }
}

FString FLibraEditorPreviewSession::UpdateScene(int64 programSequence, double timeSeconds)
{
    check(IsInGameThread());
    if (!effect.IsValid() || !connection.IsValid() ||
        effect->IsEvaluationPending()) return {};
    auto currentProgram = programSequence == renderProgramSequence;
    if (debugDraw)
    {
        auto error = FString{};
        if (!debugDraw->Update(*effect.Get(), programSequence, error)) sceneErrors.Add(error);
    }
    for (const auto& node : renderNodes)
        for (const auto& input : node.inputs)
            if (currentProgram && input.hasBinding)
            {
                auto value = FVector4{};
                auto width = 0;
                if (effect->ReadBoundValue(input.handle, value, width) == ELibraRuntimeResult::Busy) return {};
            }

    auto status = MakeShared<FJsonObject>();
    status->SetStringField(TEXT("type"), TEXT("render_status"));
    status->SetNumberField(TEXT("programSequence"), renderProgramSequence);
    if (!currentProgram) status->SetNumberField(TEXT("requestedProgramSequence"), programSequence);
    status->SetNumberField(TEXT("timeSeconds"), timeSeconds);
    auto rendered = TArray<TSharedPtr<FJsonValue>>{};
    auto errors = TArray<TSharedPtr<FJsonValue>>{};
    for (const auto& error : sceneErrors)
        errors.Add(MakeShared<FJsonValueString>(error));
    auto addError = [&errors](const FString& error)
    {
        if (errors.Num() < 128) errors.Add(MakeShared<FJsonValueString>(error));
    };
    auto parameterReportBudget = 512;
    for (auto& node : renderNodes)
    {
        if (!node.component.IsValid()) continue;
        auto nodeRendered = node.component->IsRegistered();
        for (const auto& material : node.materials)
            nodeRendered &= PreviewShaderState(material.Get()) == 1;
        if (!nodeRendered) addError(TEXT("A render component or its material shader is unavailable."));
        auto values = TMap<FString, FVector4>{};
        auto parameterStatus = TArray<TSharedPtr<FJsonValue>>{};
        for (const auto& input : node.inputs)
        {
            if (!currentProgram) continue;
            auto value = ToVector4(input.defaults);
            if (input.hasBinding)
            {
                auto width = 0;
                auto result = effect->ReadBoundValue(input.handle, value, width);
                if (result != ELibraRuntimeResult::Ok)
                { nodeRendered = false; addError(TEXT("Bound render value could not be read.")); continue; }
                auto sourceValue = value;
                if (input.sourceComponent + input.defaults.Num() > width)
                { nodeRendered = false; addError(TEXT("Bound render source does not have the requested components.")); continue; }
                value = ToVector4(input.defaults);
                for (auto index = 0; index < input.defaults.Num() && index < 4; ++index)
                    if (input.sourceComponent + index >= 0 && input.sourceComponent + index < width)
                        value[index] = sourceValue[input.sourceComponent + index];
            }
            if (!IsFinite(value))
            {
                nodeRendered = false;
                addError(TEXT("Non-finite or float-range-exceeding render input was ignored."));
                continue;
            }
            values.Add(input.id, value);
            if (!input.parameterName.IsEmpty())
            {
                auto info = FMaterialParameterInfo(FName(*input.parameterName),
                    static_cast<EMaterialParameterAssociation>(FMath::Clamp(input.association, 0, 2)), input.index);
                if (input.materialSlot >= 0 && input.materialSlot < node.materials.Num() &&
                    node.materials[input.materialSlot].IsValid())
                {
                    auto actual = FVector4{};
                    if (!SetParameterValue(node.materials[input.materialSlot].Get(), info, input.parameterKind, value, actual))
                    { nodeRendered = false; addError(TEXT("Material parameter could not be read back: ") + input.parameterName); continue; }
                    value = actual;
                }
                else { nodeRendered = false; addError(TEXT("Material parameter slot became unavailable.")); continue; }
                auto parameter = MakeShared<FJsonObject>();
                parameter->SetStringField(TEXT("name"), input.parameterName);
                parameter->SetNumberField(TEXT("slot"), input.materialSlot);
                parameter->SetStringField(TEXT("kind"), input.parameterKind);
                parameter->SetStringField(TEXT("materialPath"), node.materials[input.materialSlot]->Parent->GetPathName());
                if (input.parameterKind == TEXT("scalar"))
                    parameter->SetNumberField(TEXT("value"), value.X);
                else
                {
                    auto components = TArray<TSharedPtr<FJsonValue>>{};
                    components.Add(MakeShared<FJsonValueNumber>(value.X));
                    components.Add(MakeShared<FJsonValueNumber>(value.Y));
                    components.Add(MakeShared<FJsonValueNumber>(value.Z));
                    components.Add(MakeShared<FJsonValueNumber>(value.W));
                    parameter->SetArrayField(TEXT("value"), MoveTemp(components));
                }
                if (parameterReportBudget > 0)
                {
                    parameterStatus.Add(MakeShared<FJsonValueObject>(parameter));
                    --parameterReportBudget;
                }
            }
        }

        auto position = values.FindRef(TEXT("position"));
        auto rotation = values.FindRef(TEXT("rotation"));
        auto scale = values.FindRef(TEXT("scale"));
        if (!values.Contains(TEXT("scale"))) scale = FVector4(1, 1, 1, 0);
        if (!IsFinite(position) || !IsFinite(rotation) || !IsFinite(scale))
        {
            addError(TEXT("Non-finite pose was ignored."));
            continue;
        }
        auto transform = FTransform(
            FRotator(rotation.Y, rotation.Z, rotation.X),
            FVector(position.X, position.Y, position.Z),
            FVector(scale.X, scale.Y, scale.Z));
        if (node.billboard && viewportClient.IsValid())
        {
            auto toCamera = viewportClient->GetViewLocation() - transform.GetLocation();
            if (!toCamera.IsNearlyZero())
                transform.SetRotation(FQuat::FindBetweenNormals(FVector::UpVector, toCamera.GetSafeNormal()));
        }
        if (currentProgram && nodeRendered && node.spriteAnimation.version != 2)
            node.component->SetRelativeTransform(transform);
        auto tint = values.FindRef(TEXT("tint"));
        if (values.Contains(TEXT("tint")))
            for (auto& material : node.materials)
                if (material.IsValid())
                {
                    auto tintName = FName(TEXT("Tint"));
                    auto explicitColor = node.inputs.ContainsByPredicate([](const auto& input) { return input.parameterName == TEXT("Color"); });
                    if (!HasPreviewParameter(material->Parent, FMaterialParameterInfo(tintName), TEXT("vector")))
                    {
                        if (!explicitColor && node.kind == TEXT("model") &&
                            HasPreviewParameter(material->Parent, FMaterialParameterInfo(TEXT("Color")), TEXT("vector")))
                            tintName = TEXT("Color");
                        else
                        {
                            if (!tint.Equals(FVector4(1, 1, 1, 1)))
                            { nodeRendered = false; addError(TEXT("This material cannot apply the requested model tint.")); }
                            continue;
                        }
                    }
                    auto actual = FVector4{};
                    if (!SetParameterValue(material.Get(), FMaterialParameterInfo(tintName), TEXT("vector"), tint, actual))
                    { nodeRendered = false; addError(TEXT("Preview tint could not be read back.")); }
                }

        if (currentProgram && node.kind == TEXT("sprite"))
        {
            auto frameValue = values.Contains(TEXT("frame")) ? values.FindRef(TEXT("frame")).X : -1.0;
            auto frameConnected = false;
            for (const auto& input : node.inputs)
                if (input.id == TEXT("frame") && input.hasBinding) frameConnected = true;
            auto playing = values.Contains(TEXT("playing")) ? values.FindRef(TEXT("playing")).X >= 0.5 : true;
            auto atlas = liberafx::AtlasFrame{};
            auto sampled = false;
            if (node.spriteAnimation.present)
            {
                if (frameConnected && frameValue < 0)
                {
                    addError(TEXT("Connected sprite frame input must be a non-negative sequence index."));
                    continue;
                }
                auto sequenceFrame = uint32{};
                auto keyedPose = liberafx::SpritePose{};
                sampled = node.spriteAnimation.version == 2
                    ? node.spriteAnimation.composition.Sample(timeSeconds, node.spriteAnimation.fps,
                        node.spriteAnimation.loop, node.spriteAnimation.durations, sequenceFrame, keyedPose)
                    : liberafx::sampleSpriteSequence(frameValue, timeSeconds,
                    node.spriteAnimation.fps, node.spriteAnimation.durations.GetData(),
                    static_cast<uint32>(node.spriteAnimation.durations.Num()),
                    static_cast<uint32>(node.spriteAnimation.startFrame),
                    static_cast<uint32>(node.spriteAnimation.endFrame), node.spriteAnimation.loop,
                    playing, sequenceFrame);
                sampled = sampled && node.spriteAnimation.frames.IsValidIndex(sequenceFrame);
                if (sampled)
                {
                    const auto& frame = node.spriteAnimation.frames[sequenceFrame];
                    const auto& cell = node.spriteAnimation.cells[frame.cellIndex];
                    sampled = liberafx::sampleSpriteRect(sequenceFrame, cell.u, cell.v, cell.width, cell.height, atlas);
                    if (sampled && nodeRendered && node.spriteAnimation.version == 2)
                        node.component->SetRelativeTransform(FLibraSpriteCompositionRender::Transform(
                            keyedPose, cell.pixelWidth, cell.pixelHeight));
                }
            }
            else
            {
                auto automaticFrame = !frameConnected && frameValue < 0;
                if (automaticFrame)
                    frameValue = playing ? timeSeconds * FMath::Max(0.0,
                        values.Contains(TEXT("fps")) ? values.FindRef(TEXT("fps")).X : 0.0) : 0.0;
                auto columnsValue = values.Contains(TEXT("columns")) ? values.FindRef(TEXT("columns")).X : 1.0;
                auto rowsValue = values.Contains(TEXT("rows")) ? values.FindRef(TEXT("rows")).X : 1.0;
                if (!FMath::IsFinite(columnsValue) || !FMath::IsFinite(rowsValue) || columnsValue < 1 ||
                    rowsValue < 1 || columnsValue > MAX_uint32 || rowsValue > MAX_uint32)
                {
                    addError(TEXT("Invalid sprite atlas dimensions."));
                    continue;
                }
                if (FMath::FloorToDouble(columnsValue) != columnsValue || FMath::FloorToDouble(rowsValue) != rowsValue)
                {
                    addError(TEXT("Sprite atlas dimensions must be integers."));
                    continue;
                }
                sampled = liberafx::sampleSpriteAtlas(frameValue, static_cast<uint32>(columnsValue),
                    static_cast<uint32>(rowsValue), atlas);
            }
            if (!sampled)
            {
                addError(node.spriteAnimation.present
                    ? TEXT("Arbitrary sprite sequence rectangle could not be sampled.")
                    : TEXT("Sprite atlas frame could not be sampled."));
                continue;
            }
            node.frame = atlas.frameIndex;
            node.uvRect = FVector4(atlas.uvRect[0], atlas.uvRect[1], atlas.uvRect[2], atlas.uvRect[3]);
            for (auto& material : node.materials)
                if (material.IsValid())
                {
                    auto actual = FVector4{};
                    if (!SetParameterValue(material.Get(), FMaterialParameterInfo(TEXT("AtlasScale")), TEXT("vector"),
                        FVector4(node.uvRect.Z, node.uvRect.W, 0, 0), actual) ||
                        !SetParameterValue(material.Get(), FMaterialParameterInfo(TEXT("AtlasOffset")), TEXT("vector"),
                        FVector4(node.uvRect.X, node.uvRect.Y, 0, 0), actual))
                    { nodeRendered = false; addError(TEXT("Sprite atlas parameters were not applied.")); }
                }
        }
        auto item = MakeShared<FJsonObject>();
        item->SetStringField(TEXT("nodeId"), node.id.ToString(EGuidFormats::DigitsWithHyphensLower));
        item->SetStringField(TEXT("kind"), node.kind);
        item->SetStringField(TEXT("assetPath"), node.assetPath);
        item->SetBoolField(TEXT("rendered"), nodeRendered);
        item->SetNumberField(TEXT("frame"), node.frame);
        auto uv = TArray<TSharedPtr<FJsonValue>>{};
        uv.Add(MakeShared<FJsonValueNumber>(node.uvRect.X));
        uv.Add(MakeShared<FJsonValueNumber>(node.uvRect.Y));
        uv.Add(MakeShared<FJsonValueNumber>(node.uvRect.Z));
        uv.Add(MakeShared<FJsonValueNumber>(node.uvRect.W));
        item->SetArrayField(TEXT("uvRect"), MoveTemp(uv));
        item->SetArrayField(TEXT("parameters"), MoveTemp(parameterStatus));
        rendered.Add(MakeShared<FJsonValueObject>(item));
    }
    status->SetArrayField(TEXT("nodes"), MoveTemp(rendered));
    status->SetArrayField(TEXT("errors"), MoveTemp(errors));
    auto json = SerializeJson(status);
    auto encoded = FTCHARToUTF8(*json);
    if (encoded.Length() > 900000)
    {
        status->SetArrayField(TEXT("nodes"), TArray<TSharedPtr<FJsonValue>>{});
        errors.Add(MakeShared<FJsonValueString>(TEXT("Render status was truncated to fit the live-preview message limit.")));
        status->SetArrayField(TEXT("errors"), MoveTemp(errors));
        json = SerializeJson(status);
    }
    return json;
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
    debugDraw.Reset();
    gpuPreview.Reset();
    pendingRenderNodes.Reset();
    pendingRenderProgramSequence = -1;
    acceptedRenderRootSequence = -1;
    renderNodes.Reset();
    sceneErrors.Reset();
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
        connection->EditorSceneConfigHandler = {};
        connection->EditorRenderStatusHandler = {};
        connection->EditorGpuConfigHandler = {};
        connection->EditorGpuPlaybackHandler = {};
        connection->EditorGpuResetHandler = {};
        connection->EditorGpuStatusHandler = {};
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
    previewActor.Reset();
    world.Reset();
    previewScene.Reset();
}

#if WITH_DEV_AUTOMATION_TESTS
namespace
{
    bool WaitForPreviewTestShaders(TFunctionRef<void()> update, TFunctionRef<bool()> done)
    {
        auto deadline = FPlatformTime::Seconds() + 120;
        do
        {
            if (GShaderCompilingManager) GShaderCompilingManager->ProcessAsyncResults(false, false);
            FAssetCompilingManager::Get().ProcessAsyncTasks(true);
            FTaskGraphInterface::Get().ProcessThreadUntilIdle(ENamedThreads::GameThread);
            update();
            if (done()) return true;
            FPlatformProcess::Sleep(0.01f);
        } while (FPlatformTime::Seconds() < deadline);
        return false;
    }

    bool CapturePreviewTestComponent(UStaticMeshComponent* component, TArray<FFloat16Color>& pixels, FString& diagnostic)
    {
        auto target = TStrongObjectPtr<UTextureRenderTarget2D>(NewObject<UTextureRenderTarget2D>(GetTransientPackage(), NAME_None, RF_Transient));
        target->ClearColor = FLinearColor::Black;
        target->InitCustomFormat(32, 32, PF_FloatRGBA, true);
        target->UpdateResourceImmediate();
        auto capture = TStrongObjectPtr<USceneCaptureComponent2D>(
            NewObject<USceneCaptureComponent2D>(component->GetOwner(), NAME_None, RF_Transient));
        capture->TextureTarget = target.Get();
        capture->CaptureSource = SCS_SceneColorHDR;
        capture->PrimitiveRenderMode = ESceneCapturePrimitiveRenderMode::PRM_UseShowOnlyList;
        capture->bCaptureEveryFrame = false;
        capture->bCaptureOnMovement = false;
        capture->FOVAngle = 45;
        // The bounds-fitted camera can be closer than the editor's global near plane.
        capture->bOverride_CustomNearClippingPlane = true;
        capture->CustomNearClippingPlane = 0.1f;
        capture->ShowFlags.SetTemporalAA(false);
        capture->ShowFlags.SetFog(false);
        capture->ShowFlags.SetAtmosphere(false);
        capture->RegisterComponent();
        capture->ShowOnlyComponent(component);
        auto deadline = FPlatformTime::Seconds() + 5;
        auto attempts = 0;
        auto read = false;
        auto eye = FVector{};
        do
        {
            FAssetCompilingManager::Get().ProcessAsyncTasks(true);
            FTaskGraphInterface::Get().ProcessThreadUntilIdle(ENamedThreads::GameThread);
            if (GShaderCompilingManager) GShaderCompilingManager->ProcessAsyncResults(false, false);
            component->UpdateBounds();
            auto bounds = component->Bounds;
            auto direction = component->GetStaticMesh()->GetPathName().EndsWith(TEXT("/Plane.Plane"))
                ? component->GetUpVector() : FVector::ForwardVector;
            eye = bounds.Origin + direction * FMath::Max(bounds.SphereRadius * 3.0, 10.0);
            capture->SetWorldLocationAndRotation(eye, (bounds.Origin - eye).Rotation());
            component->GetWorld()->SendAllEndOfFrameUpdates();
            FlushRenderingCommands();
            ++attempts;
            // Shader completion alone does not finish mesh builds or deferred PSO/proxy creation.
            if (!component->IsCompiling() && !component->IsPSOPrecaching() && component->GetSceneProxy())
            {
                capture->CaptureScene();
                FlushRenderingCommands();
                read = target->GameThread_GetRenderTargetResource()->ReadFloat16Pixels(pixels);
                if (read && pixels.Num() == 1024 &&
                    FMath::Max3(pixels[528].R.GetFloat(), pixels[528].G.GetFloat(), pixels[528].B.GetFloat()) > 0.01f)
                    break;
            }
            FPlatformProcess::Sleep(0.01f);
        } while (FPlatformTime::Seconds() < deadline);
        auto maximum = FVector{};
        auto nonBlack = 0;
        for (const auto& pixel : pixels)
        {
            maximum.X = FMath::Max(maximum.X, double(pixel.R.GetFloat()));
            maximum.Y = FMath::Max(maximum.Y, double(pixel.G.GetFloat()));
            maximum.Z = FMath::Max(maximum.Z, double(pixel.B.GetFloat()));
            if (FMath::Max3(pixel.R.GetFloat(), pixel.G.GetFloat(), pixel.B.GetFloat()) > 0.01f) ++nonBlack;
        }
        auto center = pixels.Num() == 1024 ? FLinearColor(pixels[528]) : FLinearColor::Black;
        diagnostic = FString::Printf(TEXT("Capture read=%d attempts=%d pixels=%d nonBlack=%d center=%s maxRGB=%s meshCompiling=%d PSO=%d proxy=%d registered=%d visible=%d eye=%s bounds=%s"),
            read, attempts, pixels.Num(), nonBlack, *center.ToString(), *maximum.ToString(),
            component->IsCompiling(), component->IsPSOPrecaching(), component->GetSceneProxy() != nullptr,
            component->IsRegistered(), component->IsVisible(), *eye.ToString(), *component->Bounds.Origin.ToString());
        capture->DestroyComponent();
        return read && pixels.Num() == 1024;
    }
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraPreviewStockMaterialTest, "Libra.Editor.Preview.StockMaterialContract",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraPreviewStockMaterialTest::RunTest(const FString&)
{
    for (const auto path : { TEXT("/Engine/BasicShapes/Cube.Cube"), TEXT("/Engine/BasicShapes/Sphere.Sphere") })
    {
        auto mesh = LoadObject<UStaticMesh>(nullptr, path);
        if (!TestNotNull(FString::Printf(TEXT("Stock mesh %s loads"), path), mesh)) continue;
        const auto& materials = mesh->GetStaticMaterials();
        for (auto slot = 0; slot < FMath::Min(materials.Num(), 8); ++slot)
            AddInfo(FString::Printf(TEXT("Stock mesh=%s slot=%d %s"), path, slot,
                *DescribePreviewMaterial(materials[slot].MaterialInterface)));
    }
    auto material = LoadObject<UMaterialInterface>(nullptr,
        TEXT("/Engine/BasicShapes/BasicShapeMaterial.BasicShapeMaterial"));
    TestNotNull(TEXT("Stock BasicShapeMaterial loads independently of mesh defaults"), material);
    AddInfo(DescribePreviewMaterial(material));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraPreviewCpuSceneTest, "Libra.Editor.Preview.CpuSceneTransactions",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraPreviewCpuSceneTest::RunTest(const FString&)
{
    auto session = FLibraEditorPreviewSession{};
    session.bEngineReady = true;
    session.bPreviewRequested = true;
    session.Tick(0);
    auto model = FString(TEXT("{\"nodes\":[{\"nodeId\":\"efdd5b14-c3b2-4f08-b35a-6aa5940c3c16\",\"kind\":\"model\","
        "\"assetPath\":\"/Engine/BasicShapes/Cube.Cube\",\"materials\":[{\"slot\":0,\"path\":\"builtin:unlit-color\"}],"
        "\"inputs\":[{\"id\":\"material_color\",\"width\":4,\"defaults\":[0.2,0.4,0.6,1],"
        "\"materialParameter\":{\"name\":\"Color\",\"kind\":\"vector\",\"slot\":0,\"association\":2,\"index\":-1}}]}]}"));
    session.ConfigureScene(model, 1);
    for (const auto& error : session.sceneErrors) AddInfo(error);
    TestTrue(TEXT("A real model is committed only after its material shader is usable"),
        WaitForPreviewTestShaders([&] { session.CommitPendingScene(); }, [&] { return session.pendingRenderProgramSequence < 0; }) &&
        session.renderNodes.Num() == 1 && session.sceneErrors.IsEmpty());
    if (session.renderNodes.Num() != 1)
        for (const auto& error : session.sceneErrors) AddError(error);
    if (session.renderNodes.Num() != 1) { session.Stop(); return false; }
    auto old = session.renderNodes[0].component.Get();
    session.UpdateScene(1, 0);
    auto actual = FLinearColor{};
    TestTrue(TEXT("Color is the actual applied native MID parameter, not requested-value feedback"),
        session.renderNodes[0].materials[0]->GetVectorParameterValue(FMaterialParameterInfo(TEXT("Color")), actual) &&
        actual.Equals(FLinearColor(0.2f, 0.4f, 0.6f, 1), 0.0001f));
    auto pixels = TArray<FFloat16Color>{};
    auto diagnostic = FString{};
    auto read = CapturePreviewTestComponent(old, pixels, diagnostic);
    AddInfo(diagnostic);
    TestTrue(TEXT("The isolated cube produces real colored render-target pixels, not just material feedback"),
        read &&
        pixels[16 * 32 + 16].R.GetFloat() > 0.01f &&
        pixels[16 * 32 + 16].B.GetFloat() > pixels[16 * 32 + 16].R.GetFloat());
    for (const auto& invalid : {
        model.Replace(TEXT("/Engine/BasicShapes/Cube.Cube"), TEXT("/Engine/BasicShapes/MissingMesh.MissingMesh")),
        model.Replace(TEXT("builtin:unlit-color"), TEXT("/Engine/BasicShapes/MissingMaterial.MissingMaterial")),
        model.Replace(TEXT("builtin:unlit-color"), TEXT("builtin:unknown-color")),
        model.Replace(TEXT("\"name\":\"Color\""), TEXT("\"name\":\"MissingParameter\"")),
        model.Replace(TEXT("\"association\":2"), TEXT("\"association\":0")),
        FString(TEXT("{\"nodes\":[{\"nodeId\":\"efdd5b14-c3b2-4f08-b35a-6aa5940c3c16\",\"kind\":\"sprite\","
            "\"assetPath\":\"/Engine/EngineResources/MissingTexture.MissingTexture\"}]}"))})
    {
        session.ConfigureScene(invalid, 2);
        TestTrue(TEXT("Invalid assets/parameters reject the whole replacement and preserve the previous scene"),
            session.renderNodes.Num() == 1 && session.renderNodes[0].component.Get() == old &&
            session.renderProgramSequence == 1 && !session.sceneErrors.IsEmpty());
    }
    auto sprite = FString(TEXT("{\"nodes\":[{\"nodeId\":\"efdd5b14-c3b2-4f08-b35a-6aa5940c3c16\",\"kind\":\"sprite\","
        "\"assetPath\":\"/Engine/EngineResources/DefaultTexture.DefaultTexture\",\"inputs\":["
        "{\"id\":\"frame\",\"width\":1,\"defaults\":[-1]},{\"id\":\"fps\",\"width\":1,\"defaults\":[4]},"
        "{\"id\":\"columns\",\"width\":1,\"defaults\":[2]},{\"id\":\"rows\",\"width\":1,\"defaults\":[2]}]}]}"));
    session.ConfigureScene(sprite, 3);
    TestTrue(TEXT("A real textured sprite shader compiles before its replacement becomes visible"),
        WaitForPreviewTestShaders([&] { session.CommitPendingScene(); }, [&] { return session.pendingRenderProgramSequence < 0; }) &&
        session.renderProgramSequence == 3 && session.renderNodes.Num() == 1);
    if (session.renderProgramSequence == 3 && session.renderNodes.Num() == 1)
    {
        session.UpdateScene(3, 0.25);
        auto material = session.renderNodes[0].materials[0].Get();
        auto texture = static_cast<UTexture*>(nullptr);
        auto scale = FLinearColor{};
        auto offset = FLinearColor{};
        TestTrue(TEXT("SpriteTexture is a real Texture2D reference"),
            material->GetTextureParameterValue(FMaterialParameterInfo(TEXT("SpriteTexture")), texture) &&
            texture == LoadObject<UTexture2D>(nullptr, TEXT("/Engine/EngineResources/DefaultTexture.DefaultTexture")));
        TestTrue(TEXT("Time-varying atlas frame reaches actual material UV uniforms"),
            material->GetVectorParameterValue(FMaterialParameterInfo(TEXT("AtlasScale")), scale) &&
            material->GetVectorParameterValue(FMaterialParameterInfo(TEXT("AtlasOffset")), offset) &&
            scale.Equals(FLinearColor(0.5f, 0.5f, 0, 0)) && offset.Equals(FLinearColor(0.5f, 0, 0, 0)));
    }
    session.Stop();
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraPreviewCpuFixtureTest, "Libra.Editor.Preview.CpuRootFixture",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraPreviewCpuFixtureTest::RunTest(const FString&)
{
    auto directory = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraPreviewCpuFixture="), directory) || directory.IsEmpty())
    {
        AddInfo(TEXT("CpuRootFixture requires an explicit isolated Root-exported fixture directory."));
        return true;
    }
    auto bytes = TArray<uint8>{};
    auto configuration = FString{};
    auto expectedText = FString{};
    auto programPath = directory / TEXT("program.lfxb");
    auto configPath = directory / TEXT("render-config.json");
    auto expectedPath = directory / TEXT("expected.json");
    if (!TestTrue(TEXT("Fixture files have bounded sizes"),
        IFileManager::Get().FileSize(*programPath) > 0 && IFileManager::Get().FileSize(*programPath) <= ULibraEffectAsset::MaximumBytecodeBytes &&
        IFileManager::Get().FileSize(*configPath) > 0 && IFileManager::Get().FileSize(*configPath) <= 1024 * 1024 &&
        IFileManager::Get().FileSize(*expectedPath) > 0 && IFileManager::Get().FileSize(*expectedPath) <= 1024 * 1024)) return false;
    if (!FFileHelper::LoadFileToArray(bytes, *programPath) ||
        !FFileHelper::LoadFileToString(configuration, *configPath) || !FFileHelper::LoadFileToString(expectedText, *expectedPath))
    { AddError(TEXT("Could not read the explicitly selected CPU fixture.")); return false; }
    auto expected = ParseJson(expectedText);
    const TArray<TSharedPtr<FJsonValue>>* frames = nullptr;
    if (!expected || !expected->TryGetArrayField(TEXT("frames"), frames) || frames->IsEmpty() || frames->Num() > 16)
    { AddError(TEXT("Fixture expected.json must contain one to sixteen frames.")); return false; }
    auto session = FLibraEditorPreviewSession{};
    session.bEngineReady = true;
    session.bPreviewRequested = true;
    session.Tick(0);
    auto asset = TStrongObjectPtr<ULibraEffectAsset>(NewObject<ULibraEffectAsset>(GetTransientPackage(), NAME_None, RF_Transient));
    if (!TestTrue(TEXT("Actual Root bytecode loads into the native effect"),
        asset->SetBytecode(bytes) == ELibraRuntimeResult::Ok && session.effect->SetEffect(asset.Get()) == ELibraRuntimeResult::Ok))
    { session.Stop(); return false; }
    session.ConfigureScene(configuration, 1);
    auto committed = TestTrue(TEXT("Actual Root rendering assets/shaders validate transactionally"),
        WaitForPreviewTestShaders([&] { session.CommitPendingScene(); }, [&] { return session.pendingRenderProgramSequence < 0; }) &&
        session.sceneErrors.IsEmpty());
    if (!committed)
    {
        for (const auto& error : session.sceneErrors) AddError(error);
        session.Stop();
        return false;
    }
    for (const auto& frameValue : *frames)
    {
        auto frame = frameValue.IsValid() ? frameValue->AsObject() : nullptr;
        auto time = double{0};
        const TArray<TSharedPtr<FJsonValue>>* expectedNodes = nullptr;
        if (!frame || !frame->TryGetNumberField(TEXT("timeSeconds"), time) || !frame->TryGetArrayField(TEXT("nodes"), expectedNodes))
        { AddError(TEXT("Malformed expected fixture frame.")); break; }
        session.effect->EvaluateAtTime(time);
        if (!TestTrue(TEXT("Native bytecode evaluation completes at a fixed fixture time"),
            WaitForPreviewTestShaders([&] { session.effect->TickComponent(0, LEVELTICK_All, &session.effect->PrimaryComponentTick); },
                [&] { return !session.effect->IsEvaluationPending(); }))) break;
        session.UpdateScene(1, time);
        TestEqual(TEXT("Native component count matches the real Root fixture"), session.renderNodes.Num(), expectedNodes->Num());
        for (const auto& nodeValue : *expectedNodes)
        {
            auto object = nodeValue.IsValid() ? nodeValue->AsObject() : nullptr;
            auto idText = FString{};
            auto id = FGuid{};
            if (!object || !object->TryGetStringField(TEXT("nodeId"), idText) || !FGuid::Parse(idText, id))
            { AddError(TEXT("Malformed expected fixture node.")); continue; }
            auto node = session.renderNodes.FindByPredicate([&](const auto& item) { return item.id == id; });
            if (!TestTrue(TEXT("Every expected Root renderer exists natively"), node != nullptr)) continue;
            auto vector = [&](const TCHAR* name, FVector& out)
            {
                const TArray<TSharedPtr<FJsonValue>>* components = nullptr;
                if (!object->TryGetArrayField(name, components) || components->Num() != 3) return false;
                out = FVector((*components)[0]->AsNumber(), (*components)[1]->AsNumber(), (*components)[2]->AsNumber());
                return true;
            };
            auto position = FVector{};
            auto scale = FVector{};
            auto rotation = FVector{};
            TestTrue(TEXT("Actual native component position matches Root fixed-time expectations"),
                vector(TEXT("position"), position) && node->component->GetRelativeLocation().Equals(position, 0.001));
            TestTrue(TEXT("Actual native component scale matches Root fixed-time expectations"),
                vector(TEXT("scale"), scale) && node->component->GetRelativeScale3D().Equals(scale, 0.001));
            if (vector(TEXT("rotation"), rotation) && !node->billboard)
                TestTrue(TEXT("Actual native rotation matches Root Euler-degree bindings"),
                    node->component->GetRelativeRotation().Quaternion().Equals(FRotator(rotation.Y, rotation.Z, rotation.X).Quaternion(), 0.0001));
            auto drawOrder = int32{}, sequenceFrame = int32{};
            if (ReadInteger(object, TEXT("drawOrder"), drawOrder))
                TestEqual(TEXT("Actual native translucency sort priority"), node->component->TranslucencySortPriority, drawOrder);
            if (ReadInteger(object, TEXT("frameIndex"), sequenceFrame))
                TestEqual(TEXT("Actual composition sequence index"), node->frame, static_cast<uint32>(sequenceFrame));
            const TArray<TSharedPtr<FJsonValue>>* parameters = nullptr;
            if (object->TryGetArrayField(TEXT("parameters"), parameters))
                for (const auto& parameterValue : *parameters)
                {
                    auto parameter = parameterValue->AsObject();
                    auto name = parameter->GetStringField(TEXT("name"));
                    auto slot = 0;
                    ReadInteger(parameter, TEXT("slot"), slot);
                    const TArray<TSharedPtr<FJsonValue>>* components = nullptr;
                    auto info = FMaterialParameterInfo(FName(*name));
                    auto association = static_cast<int32>(EMaterialParameterAssociation::GlobalParameter);
                    auto index = -1;
                    ReadInteger(parameter, TEXT("association"), association);
                    ReadInteger(parameter, TEXT("index"), index);
                    info.Association = static_cast<EMaterialParameterAssociation>(association);
                    info.Index = index;
                    if (slot < 0 || slot >= node->materials.Num())
                    { AddError(TEXT("Expected fixture material slot is unavailable.")); continue; }
                    auto scalar = double{0};
                    if (parameter->TryGetNumberField(TEXT("value"), scalar))
                    {
                        auto actualScalar = float{0};
                        TestTrue(TEXT("Fixture scalar expectation comes from the actual native MID"),
                            node->materials[slot]->GetScalarParameterValue(info, actualScalar) &&
                            FMath::IsNearlyEqual(double(actualScalar), scalar, 0.001));
                        continue;
                    }
                    auto texturePath = FString{};
                    if (parameter->TryGetStringField(TEXT("value"), texturePath))
                    {
                        auto actualTexture = static_cast<UTexture*>(nullptr);
                        TestTrue(TEXT("Fixture texture expectation comes from the actual native MID"),
                            node->materials[slot]->GetTextureParameterValue(info, actualTexture) &&
                            actualTexture && actualTexture->GetPathName() == texturePath);
                        continue;
                    }
                    auto actual = FLinearColor{};
                    auto readValue = node->materials[slot]->GetVectorParameterValue(info, actual);
                    auto matches = TestTrue(FString::Printf(
                        TEXT("Fixture native MID %s (slot=%d, association=%d, index=%d) matches actual color/atlas expectation"),
                        *name, slot, association, index),
                        slot >= 0 && slot < node->materials.Num() && parameter->TryGetArrayField(TEXT("value"), components) &&
                        components->Num() == 4 && readValue &&
                        actual.Equals(FLinearColor((*components)[0]->AsNumber(), (*components)[1]->AsNumber(),
                            (*components)[2]->AsNumber(), (*components)[3]->AsNumber()), 0.001f));
                    if (!matches)
                        AddInfo(FString::Printf(TEXT("Native MID vector read succeeded=%s; actual=%s."),
                            readValue ? TEXT("true") : TEXT("false"), *actual.ToString()));
                    if (node->kind == TEXT("model") && name == TEXT("Color") && slot == 0 && readValue &&
                        FMath::Max3(actual.R, actual.G, actual.B) > 0.01f)
                    {
                        auto pixels = TArray<FFloat16Color>{};
                        auto diagnostic = FString{};
                        auto captured = CapturePreviewTestComponent(node->component.Get(), pixels, diagnostic);
                        AddInfo(FString::Printf(TEXT("Root node=%s time=%g %s"), *idText, time, *diagnostic));
                        auto pixel = pixels.Num() == 1024 ? FLinearColor(pixels[528]) : FLinearColor::Black;
                        auto pixelMaximum = FMath::Max3(pixel.R, pixel.G, pixel.B);
                        auto materialMaximum = FMath::Max3(actual.R, actual.G, actual.B);
                        TestTrue(TEXT("Actual Root model draws pixels with the applied native Color hue"),
                            captured && pixelMaximum > 0.01f &&
                            FMath::IsNearlyEqual(pixel.R / pixelMaximum, actual.R / materialMaximum, 0.08f) &&
                            FMath::IsNearlyEqual(pixel.G / pixelMaximum, actual.G / materialMaximum, 0.08f) &&
                            FMath::IsNearlyEqual(pixel.B / pixelMaximum, actual.B / materialMaximum, 0.08f));
                    }
                }
            if (node->kind == TEXT("sprite") && node->materials.Num() > 0)
            {
                auto material = node->materials[0].Get();
                auto texture = static_cast<UTexture*>(nullptr);
                auto tint = FLinearColor{};
                auto textureRead = material->GetTextureParameterValue(FMaterialParameterInfo(TEXT("SpriteTexture")), texture);
                auto tintRead = material->GetVectorParameterValue(FMaterialParameterInfo(TEXT("Tint")), tint);
                TestTrue(TEXT("Root sprite uses the actual configured texture and native Tint"),
                    textureRead && texture && texture->GetPathName() == node->assetPath && tintRead);
                auto pixels = TArray<FFloat16Color>{};
                auto diagnostic = FString{};
                auto captured = CapturePreviewTestComponent(node->component.Get(), pixels, diagnostic);
                AddInfo(FString::Printf(TEXT("Root sprite=%s time=%g Tint=%s %s"),
                    *idText, time, *tint.ToString(), *diagnostic));
                auto pixel = pixels.Num() == 1024 ? FLinearColor(pixels[528]) : FLinearColor::Black;
                auto pixelMaximum = FMath::Max3(pixel.R, pixel.G, pixel.B);
                TestTrue(TEXT("Actual Root sprite produces real textured nonblack pixels"),
                    captured && pixelMaximum > 0.01f);
                if (node->assetPath == TEXT("/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture") && tintRead)
                {
                    auto tintMaximum = FMath::Max3(tint.R, tint.G, tint.B);
                    TestTrue(TEXT("Stock-white Root sprite pixels match the actual native Tint RGB hue"),
                        captured && pixelMaximum > 0.01f && tintMaximum > 0.01f &&
                        FMath::IsNearlyEqual(pixel.R / pixelMaximum, tint.R / tintMaximum, 0.08f) &&
                        FMath::IsNearlyEqual(pixel.G / pixelMaximum, tint.G / tintMaximum, 0.08f) &&
                        FMath::IsNearlyEqual(pixel.B / pixelMaximum, tint.B / tintMaximum, 0.08f));
                }
            }
        }
    }
    session.Stop();
    return true;
}

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

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraShaderInputRangeTest, "Libra.Editor.Rendering.ShaderInputRange",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraShaderInputRangeTest::RunTest(const FString&)
{
    TestTrue(TEXT("HDR components remain valid"), IsFinite(FVector4(2, 4, -2, 1)));
    TestFalse(TEXT("Finite doubles outside shader float range are rejected"),
        IsFinite(FVector4(1e300, 1, 1, 1)));
    auto defaults = TArray<double>{1e300, 1, 1, 1};
    TestFalse(TEXT("Defaults are checked before narrowing to shader floats"), IsFinite(ToVector4(defaults)));
    return true;
}
#endif
