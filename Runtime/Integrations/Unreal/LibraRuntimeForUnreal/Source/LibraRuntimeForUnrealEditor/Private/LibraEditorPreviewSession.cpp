#include "LibraEditorPreviewSession.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Engine/DirectionalLight.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "GameFramework/Actor.h"
#include "LibraEditorAssetAdapter.h"
#include "LibraEffectComponent.h"
#include "LibraEditorConnectionComponent.h"
#include "Materials/Material.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Materials/MaterialExpressionAdd.h"
#include "Materials/MaterialExpressionMultiply.h"
#include "Materials/MaterialExpressionTextureCoordinate.h"
#include "Materials/MaterialExpressionTextureSample.h"
#include "Materials/MaterialExpressionTextureSampleParameter2D.h"
#include "Materials/MaterialExpressionVectorParameter.h"
#include "Components/StaticMeshComponent.h"
#include "EditorViewportClient.h"
#include "Framework/Docking/TabManager.h"
#include "Misc/CoreDelegates.h"
#include "Misc/CommandLine.h"
#include "Misc/PackageName.h"
#include "Misc/Parse.h"
#include "PreviewScene.h"
#include "Slate/SceneViewport.h"
#include "Widgets/Docking/SDockTab.h"
#include "Widgets/SViewport.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "liberafx/SceneBindings.h"
#if WITH_DEV_AUTOMATION_TESTS
#include "Misc/AutomationTest.h"
#endif

namespace
{
    const FName PreviewTabId(TEXT("LibraFxPreview"));

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
        auto coordinates = NewObject<UMaterialExpressionTextureCoordinate>(material);
        auto scaleParameter = NewObject<UMaterialExpressionVectorParameter>(material);
        scaleParameter->ParameterName = TEXT("AtlasScale");
        scaleParameter->DefaultValue = FLinearColor::White;
        auto offsetParameter = NewObject<UMaterialExpressionVectorParameter>(material);
        offsetParameter->ParameterName = TEXT("AtlasOffset");
        offsetParameter->DefaultValue = FLinearColor::Transparent;
        auto multiply = NewObject<UMaterialExpressionMultiply>(material);
        multiply->A.Connect(0, coordinates);
        multiply->B.Connect(0, scaleParameter);
        auto add = NewObject<UMaterialExpressionAdd>(material);
        add->A.Connect(0, multiply);
        add->B.Connect(0, offsetParameter);
        textureSample->Coordinates.Connect(0, add);
        auto& expressions = material->GetExpressionCollection();
        expressions.AddExpression(textureSample);
        expressions.AddExpression(coordinates);
        expressions.AddExpression(scaleParameter);
        expressions.AddExpression(offsetParameter);
        expressions.AddExpression(multiply);
        expressions.AddExpression(add);
        material->GetEditorOnlyData()->BaseColor.Connect(0, textureSample);
        material->GetEditorOnlyData()->Opacity.Connect(4, textureSample);
        material->PostEditChange();
        return material;
    }

    FVector4 SetParameterValue(UMaterialInstanceDynamic* material, const FMaterialParameterInfo& info,
        const FString& kind, const FVector4& value)
    {
        if (!IsFinite(value)) return value;
        if (kind == TEXT("scalar"))
        {
            material->SetScalarParameterValueByInfo(info, static_cast<float>(value.X));
            auto actual = static_cast<float>(value.X);
            if (material->GetScalarParameterValue(info, actual)) return FVector4(actual, 0, 0, 0);
        }
        else if (kind == TEXT("vector"))
        {
            material->SetVectorParameterValueByInfo(info, FLinearColor(value.X, value.Y, value.Z, value.W));
            auto actual = FLinearColor(value.X, value.Y, value.Z, value.W);
            if (material->GetVectorParameterValue(info, actual))
                return FVector4(actual.R, actual.G, actual.B, actual.A);
        }
        return value;
    }
}

FLibraEditorPreviewSession::FLibraEditorPreviewSession() = default;
FLibraEditorPreviewSession::~FLibraEditorPreviewSession() = default;

void FLibraEditorPreviewSession::Start()
{
    auto enabled = FPlatformMisc::GetEnvironmentVariable(TEXT("LIBRA_AUTOMATION_PREVIEW")) == TEXT("1") ||
        FParse::Param(FCommandLine::Get(), TEXT("LibraPreview"));
    UE_LOG(LogTemp, Display, TEXT("LIBERA FX preview bootstrap: %s."), enabled ? TEXT("enabled") : TEXT("disabled"));
    if (!enabled) return;
    preExit = FCoreDelegates::OnPreExit.AddRaw(this, &FLibraEditorPreviewSession::Stop);
    FGlobalTabmanager::Get()->RegisterNomadTabSpawner(PreviewTabId,
        FOnSpawnTab::CreateRaw(this, &FLibraEditorPreviewSession::SpawnPreviewTab))
        .SetDisplayName(FText::FromString(TEXT("LIBERA FX Preview")));
    bTabRegistered = true;
    ticker = FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateRaw(this, &FLibraEditorPreviewSession::Tick));
}

TSharedRef<SDockTab> FLibraEditorPreviewSession::SpawnPreviewTab(const FSpawnTabArgs& args)
{
    (void)args;
    return SNew(SDockTab)
        .TabRole(ETabRole::NomadTab)
        [
            viewportWidget.ToSharedRef()
        ];
}

bool FLibraEditorPreviewSession::Tick(float deltaTime)
{
    if (!world.IsValid())
    {
        if (!GEngine) return true;
        UE_LOG(LogTemp, Display, TEXT("LIBERA FX preview: creating isolated scene."));
        previewScene = MakeUnique<FPreviewScene>();
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

        auto cube = NewObject<UStaticMeshComponent>(actor, NAME_None, RF_Transient);
        cube->SetStaticMesh(LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cube.Cube")));
        actor->SetRootComponent(cube);
        actor->AddInstanceComponent(cube);
        cube->SetRelativeLocation(FVector(0, 0, 50));
        cube->RegisterComponent();

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
        actor->AddInstanceComponent(connection.Get());
        connection->RegisterComponent();
        if (!connection->ConnectToEditor())
        {
            UE_LOG(LogTemp, Warning, TEXT("LIBERA FX preview could not connect: %s"), *connection->Status);
            Stop();
            return false;
        }
        UE_LOG(LogTemp, Display, TEXT("LIBERA FX preview: transport connected and hello queued."));
        viewportWidget = SNew(SViewport).EnableGammaCorrection(false);
        viewportClient = MakeShared<FEditorViewportClient>(nullptr, previewScene.Get(), nullptr);
        viewportClient->SetViewLocation(FVector(350, -450, 320));
        viewportClient->SetViewRotation(FRotator(-22, 38, 0));
        viewportClient->SetViewMode(VMI_Lit);
        viewportClient->SetRealtime(true);
        sceneViewport = MakeShareable(new FSceneViewport(viewportClient.Get(), viewportWidget));
        viewportWidget->SetViewportInterface(sceneViewport.ToSharedRef());
        previewTab = FGlobalTabmanager::Get()->TryInvokeTab(PreviewTabId);
    }
    if (effect->IsComponentTickEnabled())
        effect->TickComponent(deltaTime, LEVELTICK_All, &effect->PrimaryComponentTick);
    connection->TickComponent(deltaTime, LEVELTICK_All, &connection->PrimaryComponentTick);
    return true;
}

void FLibraEditorPreviewSession::ConfigureScene(const FString& json, int64 programSequence)
{
    check(IsInGameThread());
    for (auto& node : renderNodes)
        if (node.component.IsValid()) node.component->DestroyComponent();
    renderNodes.Reset();
    sceneErrors.Reset();
    renderProgramSequence = programSequence;
    if (json.IsEmpty() || !effect.IsValid() || !previewActor.IsValid()) return;
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
    auto byNode = TMap<FGuid, FLibraValueHandle>{};
    auto target = effect.Get();
    auto rootComponent = previewActor->GetRootComponent();
    for (const auto& value : *nodes)
    {
        auto object = value.IsValid() ? value->AsObject() : nullptr;
        if (!object.IsValid()) continue;
        auto idText = FString{};
        auto kind = FString{};
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
        auto component = NewObject<UStaticMeshComponent>(previewActor.Get(), NAME_None, RF_Transient);
        component->SetStaticMesh(mesh);
        component->SetupAttachment(rootComponent);
        previewActor->AddInstanceComponent(component);
        component->RegisterComponent();
        node.component.Reset(component);

        const TArray<TSharedPtr<FJsonValue>>* materialConfigs = nullptr;
        object->TryGetArrayField(TEXT("materials"), materialConfigs);
        auto materialOverrides = TMap<int32, TSharedPtr<FJsonObject>>{};
        if (materialConfigs)
            for (const auto& materialValue : *materialConfigs)
                if (auto materialObject = materialValue.IsValid() ? materialValue->AsObject() : nullptr)
                {
                    auto slot = 0;
                    ReadInteger(materialObject, TEXT("slot"), slot);
                    materialOverrides.Add(slot, materialObject);
                }
        for (auto slot = 0; slot < component->GetNumMaterials(); ++slot)
        {
            auto parentMaterial = component->GetMaterial(slot);
            if (auto overrideData = materialOverrides.Find(slot))
            {
                auto materialPath = FString{};
                if ((*overrideData)->TryGetStringField(TEXT("path"), materialPath) && IsAllowedPreviewPath(materialPath))
                    if (auto loaded = LoadObject<UMaterialInterface>(nullptr, *materialPath))
                        parentMaterial = loaded;
            }
            if (kind == TEXT("sprite") && slot == 0)
                if (auto texture = LoadObject<UTexture2D>(nullptr, *assetPath))
                    parentMaterial = CreateSpriteMaterial(texture);
            if (!parentMaterial) continue;
            auto dynamic = UMaterialInstanceDynamic::Create(parentMaterial, previewActor.Get());
            component->SetMaterial(slot, dynamic);
            node.materials.SetNum(FMath::Max(node.materials.Num(), slot + 1));
            node.materials[slot].Reset(dynamic);
            if (auto overrideData = materialOverrides.Find(slot))
            {
                const TArray<TSharedPtr<FJsonValue>>* parameters = nullptr;
                if ((*overrideData)->TryGetArrayField(TEXT("parameters"), parameters))
                    for (const auto& parameterValue : *parameters)
                        if (auto parameter = parameterValue.IsValid() ? parameterValue->AsObject() : nullptr)
                        {
                            auto name = FString{};
                            auto parameterKind = FString{};
                            auto texturePath = FString{};
                            if (!parameter->TryGetStringField(TEXT("name"), name) ||
                                !parameter->TryGetStringField(TEXT("kind"), parameterKind)) continue;
                            auto association = 0;
                            auto index = -1;
                            ReadInteger(parameter, TEXT("association"), association);
                            ReadInteger(parameter, TEXT("index"), index);
                            auto info = FMaterialParameterInfo(FName(*name),
                                static_cast<EMaterialParameterAssociation>(FMath::Clamp(association, 0, 2)), index);
                            if (parameterKind == TEXT("texture"))
                            {
                                parameter->TryGetStringField(TEXT("value"), texturePath);
                                if (IsAllowedPreviewPath(texturePath))
                                    if (auto texture = LoadObject<UTexture>(nullptr, *texturePath))
                                        dynamic->SetTextureParameterValueByInfo(info, texture);
                            }
                        }
            }
        }
        if (kind == TEXT("sprite"))
        {
            auto spriteMaterial = node.materials.IsEmpty() ? nullptr : node.materials[0].Get();
            if (spriteMaterial)
                if (auto texture = LoadObject<UTexture2D>(nullptr, *assetPath))
                    spriteMaterial->SetTextureParameterValue(TEXT("SpriteTexture"), texture);
        }
        const TArray<TSharedPtr<FJsonValue>>* textureOverrides = nullptr;
        if (object->TryGetArrayField(TEXT("textures"), textureOverrides))
            for (const auto& textureValue : *textureOverrides)
                if (auto textureObject = textureValue.IsValid() ? textureValue->AsObject() : nullptr)
                {
                    auto name = FString{};
                    auto texturePath = FString{};
                    auto slot = 0;
                    auto association = 0;
                    auto index = -1;
                    if (!textureObject->TryGetStringField(TEXT("name"), name) ||
                        !textureObject->TryGetStringField(TEXT("path"), texturePath) || texturePath.IsEmpty() ||
                        !IsAllowedPreviewPath(texturePath) ||
                        !ReadInteger(textureObject, TEXT("slot"), slot) ||
                        slot < 0 || slot >= node.materials.Num() || !node.materials[slot].IsValid())
                        continue;
                    ReadInteger(textureObject, TEXT("association"), association);
                    ReadInteger(textureObject, TEXT("index"), index);
                    if (auto texture = LoadObject<UTexture>(nullptr, *texturePath))
                    {
                        auto info = FMaterialParameterInfo(FName(*name),
                            static_cast<EMaterialParameterAssociation>(FMath::Clamp(association, 0, 2)), index);
                        node.materials[slot]->SetTextureParameterValueByInfo(info, texture);
                    }
                }
        const TArray<TSharedPtr<FJsonValue>>* inputValues = nullptr;
        object->TryGetArrayField(TEXT("inputs"), inputValues);
        if (inputValues && inputValues->Num() <= 256)
            for (const auto& inputValue : *inputValues)
            {
                auto inputObject = inputValue.IsValid() ? inputValue->AsObject() : nullptr;
                if (!inputObject.IsValid()) continue;
                auto input = FRenderInput{};
                if (!inputObject->TryGetStringField(TEXT("id"), input.id)) continue;
                auto width = 1;
                ReadInteger(inputObject, TEXT("width"), width);
                input.defaults = ReadDefaults(inputObject, width);
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
                }
                ReadInteger(inputObject, TEXT("sourceComponent"), input.sourceComponent);
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
                }
                node.inputs.Add(MoveTemp(input));
            }
        renderNodes.Add(MoveTemp(node));
    }
}

FString FLibraEditorPreviewSession::UpdateScene(int64 programSequence, double timeSeconds)
{
    check(IsInGameThread());
    if (programSequence != renderProgramSequence || !effect.IsValid() || !connection.IsValid() ||
        effect->IsEvaluationPending()) return {};
    for (const auto& node : renderNodes)
        for (const auto& input : node.inputs)
            if (input.hasBinding)
            {
                auto value = FVector4{};
                auto width = 0;
                if (effect->ReadBoundValue(input.handle, value, width) == ELibraRuntimeResult::Busy) return {};
            }

    auto status = MakeShared<FJsonObject>();
    status->SetStringField(TEXT("type"), TEXT("render_status"));
    status->SetNumberField(TEXT("programSequence"), programSequence);
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
        auto values = TMap<FString, FVector4>{};
        auto parameterStatus = TArray<TSharedPtr<FJsonValue>>{};
        for (const auto& input : node.inputs)
        {
            auto value = ToVector4(input.defaults);
            if (input.hasBinding)
            {
                auto width = 0;
                auto result = effect->ReadBoundValue(input.handle, value, width);
                if (result != ELibraRuntimeResult::Ok) continue;
                auto sourceValue = value;
                value = ToVector4(input.defaults);
                for (auto index = 0; index < input.defaults.Num() && index < 4; ++index)
                    if (input.sourceComponent + index >= 0 && input.sourceComponent + index < width)
                        value[index] = sourceValue[input.sourceComponent + index];
            }
            if (!IsFinite(value))
            {
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
                    value = SetParameterValue(node.materials[input.materialSlot].Get(), info, input.parameterKind, value);
                auto parameter = MakeShared<FJsonObject>();
                parameter->SetStringField(TEXT("name"), input.parameterName);
                parameter->SetNumberField(TEXT("slot"), input.materialSlot);
                parameter->SetStringField(TEXT("kind"), input.parameterKind);
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
        node.component->SetRelativeTransform(transform);
        auto tint = values.FindRef(TEXT("tint"));
        if (values.Contains(TEXT("tint")))
            for (auto& material : node.materials)
                if (material.IsValid())
                    material->SetVectorParameterValue(TEXT("Tint"),
                        FLinearColor(tint.X, tint.Y, tint.Z, tint.W));

        if (node.kind == TEXT("sprite"))
        {
            auto frameValue = values.Contains(TEXT("frame")) ? values.FindRef(TEXT("frame")).X : -1.0;
            auto frameConnected = false;
            for (const auto& input : node.inputs)
                if (input.id == TEXT("frame") && input.hasBinding) frameConnected = true;
            auto automaticFrame = !frameConnected && frameValue < 0;
            if (automaticFrame)
            {
                auto fps = values.Contains(TEXT("fps")) ? values.FindRef(TEXT("fps")).X : 0.0;
                auto playing = values.Contains(TEXT("playing")) ? values.FindRef(TEXT("playing")).X : 1.0;
                frameValue = playing > 0 ? timeSeconds * FMath::Max(0.0, fps) : 0.0;
            }
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
            auto atlas = liberafx::AtlasFrame{};
            if (!liberafx::sampleSpriteAtlas(frameValue, static_cast<uint32>(columnsValue),
                static_cast<uint32>(rowsValue), atlas))
            {
                addError(TEXT("Sprite atlas frame could not be sampled."));
                continue;
            }
            node.frame = atlas.frameIndex;
            node.uvRect = FVector4(atlas.uvRect[0], atlas.uvRect[1], atlas.uvRect[2], atlas.uvRect[3]);
            for (auto& material : node.materials)
                if (material.IsValid())
                {
                    material->SetVectorParameterValue(TEXT("AtlasScale"),
                        FLinearColor(node.uvRect.Z, node.uvRect.W, 0, 0));
                    material->SetVectorParameterValue(TEXT("AtlasOffset"),
                        FLinearColor(node.uvRect.X, node.uvRect.Y, 0, 0));
                }
        }
        auto item = MakeShared<FJsonObject>();
        item->SetStringField(TEXT("nodeId"), node.id.ToString(EGuidFormats::DigitsWithHyphensLower));
        item->SetStringField(TEXT("kind"), node.kind);
        item->SetStringField(TEXT("assetPath"), node.assetPath);
        item->SetBoolField(TEXT("rendered"), true);
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
    if (preExit.IsValid()) FCoreDelegates::OnPreExit.Remove(preExit);
    preExit.Reset();
    if (ticker.IsValid()) FTSTicker::GetCoreTicker().RemoveTicker(ticker);
    ticker.Reset();
    renderNodes.Reset();
    sceneErrors.Reset();
    if (previewTab.IsValid()) previewTab->RequestCloseTab();
    previewTab.Reset();
    if (bTabRegistered)
    {
        FGlobalTabmanager::Get()->UnregisterNomadTabSpawner(PreviewTabId);
        bTabRegistered = false;
    }
    sceneViewport.Reset();
    viewportClient.Reset();
    viewportWidget.Reset();
    if (connection.IsValid())
    {
        connection->EditorAssetRequestHandler = {};
        connection->EditorSceneConfigHandler = {};
        connection->EditorRenderStatusHandler = {};
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
