#if WITH_DEV_AUTOMATION_TESTS

#include "../LibraEditorResourceIdentityAdapter.h"
#include "AssetRegistry/AssetRegistryModule.h"
#include "Dom/JsonObject.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "Materials/Material.h"
#include "Materials/MaterialExpressionVectorParameter.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/PackageName.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UObject/Package.h"
#include "UObject/StrongObjectPtr.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraResourceIdentityTest,
    "Libra.Editor.Resources.Identity",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraResourceIdentityTest::RunTest(const FString&)
{
    auto authorizedProject = FString{};
    const auto currentProject = FPaths::GetProjectFilePath();
    if (!FParse::Value(FCommandLine::Get(), TEXT("-LibraResourceProject="), authorizedProject) ||
        FPaths::IsRelative(authorizedProject) || currentProject.IsEmpty() ||
        !authorizedProject.EndsWith(TEXT(".uproject")) ||
        !FPaths::IsSamePath(authorizedProject, FPaths::ConvertRelativePathToFull(currentProject)))
    {
        AddError(TEXT("Pass -LibraResourceProject=<absolute path to the current dedicated .uproject>."));
        return false;
    }

    const auto serialize = [](const TSharedRef<FJsonObject>& object)
    {
        auto json = FString{};
        auto writer = TJsonWriterFactory<>::Create(&json);
        FJsonSerializer::Serialize(object, writer);
        return json;
    };
    const auto send = [this, &serialize](const TSharedRef<FJsonObject>& request)
    {
        const auto requestId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
        request->SetStringField(TEXT("requestId"), requestId);
        const auto json = FLibraEditorResourceIdentityAdapter::ProcessRequest(serialize(request));
        auto reply = TSharedPtr<FJsonObject>{};
        auto echoedId = FString{};
        auto replyType = FString{};
        if (!FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(json), reply) || !reply ||
            !reply->TryGetStringField(TEXT("requestId"), echoedId) || echoedId != requestId ||
            !reply->TryGetStringField(TEXT("type"), replyType) || replyType != TEXT("resource_identity_result"))
        {
            AddError(FString::Printf(TEXT("Invalid resource identity reply: %s"), *json));
            return MakeShared<FJsonObject>();
        }
        return reply.ToSharedRef();
    };
    const auto field = [](const TSharedRef<FJsonObject>& reply, const TCHAR* key)
    {
        auto value = FString{};
        reply->TryGetStringField(key, value);
        return value;
    };
    const auto expect = [this, &serialize, &field](const TCHAR* label, const TSharedRef<FJsonObject>& reply,
        const TCHAR* status, const TCHAR* kind = nullptr)
    {
        if (field(reply, TEXT("status")) == status && (!kind || field(reply, TEXT("actualKind")) == kind))
            return true;
        AddError(FString::Printf(TEXT("%s: expected %s/%s, got %s"), label, status,
            kind ? kind : TEXT("(any kind)"), *serialize(reply)));
        return false;
    };
    const auto binding = [](const TCHAR* format, const FString& value)
    {
        auto json = MakeShared<FJsonObject>();
        json->SetStringField(TEXT("format"), format);
        json->SetStringField(TEXT("value"), value);
        return json;
    };
    const auto requestFor = [](const TCHAR* type, const FString& id)
    {
        auto request = MakeShared<FJsonObject>();
        request->SetStringField(TEXT("type"), type);
        request->SetStringField(TEXT("projectId"), id);
        return request;
    };
    const auto probe = [&](const FString& id, const FString& path, const TCHAR* format = TEXT("unreal.object-path"))
    {
        auto request = requestFor(TEXT("resource_identity_probe"), id);
        request->SetObjectField(TEXT("binding"), binding(format, path));
        return send(request);
    };

    auto statusRequest = requestFor(TEXT("resource_identity_project"), FString{});
    statusRequest->SetStringField(TEXT("operation"), TEXT("status"));
    auto projectStatus = send(statusRequest);
    if (field(projectStatus, TEXT("projectState")) == TEXT("Unregistered") &&
        FParse::Param(FCommandLine::Get(), TEXT("LibraInitializeResourceProject")))
    {
        statusRequest->SetStringField(TEXT("operation"), TEXT("initialize"));
        projectStatus = send(statusRequest);
    }
    const TSharedPtr<FJsonObject>* project = nullptr;
    auto projectId = FString{};
    auto engine = FString{};
    auto projectGuid = FGuid{};
    if (!expect(TEXT("Dedicated project must be registered and its local marker verified"), projectStatus, TEXT("Ready")) ||
        !projectStatus->TryGetObjectField(TEXT("project"), project) || !project || !project->IsValid() ||
        !(*project)->TryGetStringField(TEXT("engine"), engine) || engine != TEXT("Unreal") ||
        !(*project)->TryGetStringField(TEXT("projectId"), projectId) ||
        !FGuid::ParseExact(projectId, EGuidFormats::DigitsWithHyphens, projectGuid) || !projectGuid.IsValid())
    {
        AddError(TEXT("Provision a Ready identity in the dedicated project, or explicitly pass -LibraInitializeResourceProject. Clone policy is never automated."));
        return false;
    }

    const auto cubePath = FString(TEXT("/Engine/BasicShapes/Cube.Cube"));
    const auto fakeProject = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    auto passed = expect(TEXT("Different project is gated before asset lookup"),
        probe(fakeProject, cubePath), TEXT("Offline"));
    passed &= expect(TEXT("Missing project is gated"), probe(FString{}, cubePath), TEXT("Offline"));
    passed &= expect(TEXT("Unsupported binding format"), probe(projectId, cubePath, TEXT("unity.asset-guid-local-id.v1")),
        TEXT("QueryFailure"));

    auto& registry = FAssetRegistryModule::GetRegistry();
    registry.WaitForCompletion();
    if (!TestFalse(TEXT("Asset Registry completed its initial scan"), registry.IsLoadingAssets())) return false;
    passed &= expect(TEXT("Engine Cube probe"), probe(projectId, cubePath), TEXT("Resolved"), TEXT("model"));

    const auto prefix = TEXT("/Game/LibraVerification_") + FGuid::NewGuid().ToString(EGuidFormats::Digits);
    const auto texturePackage = TStrongObjectPtr<UPackage>(CreatePackage(*(prefix / TEXT("Texture"))));
    const auto materialPackage = TStrongObjectPtr<UPackage>(CreatePackage(*(prefix / TEXT("Material"))));
    const auto texture = TStrongObjectPtr<UTexture2D>(
        NewObject<UTexture2D>(texturePackage.Get(), TEXT("Texture"), RF_Public | RF_Standalone));
    const auto material = TStrongObjectPtr<UMaterial>(
        NewObject<UMaterial>(materialPackage.Get(), TEXT("Material"), RF_Public | RF_Standalone));
    FAssetRegistryModule::AssetCreated(texture.Get());
    FAssetRegistryModule::AssetCreated(material.Get());
    ON_SCOPE_EXIT
    {
        FAssetRegistryModule::AssetDeleted(material.Get());
        FAssetRegistryModule::AssetDeleted(texture.Get());
        material->ClearFlags(RF_Public | RF_Standalone);
        texture->ClearFlags(RF_Public | RF_Standalone);
        materialPackage->SetDirtyFlag(false);
        texturePackage->SetDirtyFlag(false);
    };

    const auto parameterName = FString(TEXT("LibraAcceptanceTint"));
    const auto defaultTint = FLinearColor(0.125f, 0.25f, 0.5f, 1.0f);
    auto* color = NewObject<UMaterialExpressionVectorParameter>(material.Get());
    color->ParameterName = FName(*parameterName);
    color->DefaultValue = defaultTint;
    material->GetExpressionCollection().AddExpression(color);
    material->GetEditorOnlyData()->EmissiveColor.Connect(0, color);
    material->PostEditChange();
    materialPackage->SetDirtyFlag(false);
    texturePackage->SetDirtyFlag(false);

    const auto texturePath = texture->GetPathName();
    const auto materialPath = material->GetPathName();
    const auto missingPath = prefix / TEXT("Missing.Missing");
    passed &= expect(TEXT("In-memory Texture2D probe"), probe(projectId, texturePath), TEXT("Resolved"), TEXT("texture"));
    passed &= expect(TEXT("In-memory Material probe"), probe(projectId, materialPath), TEXT("Resolved"), TEXT("material"));
    passed &= expect(TEXT("Missing object path"), probe(projectId, missingPath), TEXT("Missing"));

    const auto schemaRequest = requestFor(TEXT("resource_identity_schema"), projectId);
    schemaRequest->SetObjectField(TEXT("resourceBinding"), binding(TEXT("unreal.object-path"), texturePath));
    passed &= expect(TEXT("Texture rejected as a material schema target"), send(schemaRequest), TEXT("TypeMismatch"));
    schemaRequest->SetObjectField(TEXT("resourceBinding"), binding(TEXT("unreal.object-path"), materialPath));
    const auto schema = send(schemaRequest);
    if (!expect(TEXT("Material schema"), schema, TEXT("Resolved"), TEXT("material"))) return false;

    const TArray<TSharedPtr<FJsonValue>>* parameters = nullptr;
    if (!schema->TryGetArrayField(TEXT("parameters"), parameters) || !parameters)
    {
        AddError(TEXT("Material schema did not contain a parameter array."));
        return false;
    }
    auto foundLocator = TSharedPtr<FJsonObject>{};
    for (const auto& parameter : *parameters)
    {
        if (!parameter || parameter->Type != EJson::Object) continue;
        const auto item = parameter->AsObject();
        if (item && field(item.ToSharedRef(), TEXT("name")) == parameterName)
        {
            foundLocator = item;
            break;
        }
    }
    if (!foundLocator)
    {
        AddError(FString::Printf(TEXT("Schema omitted the fixture vector parameter: %s"), *serialize(schema)));
        return false;
    }

    auto association = double{0};
    auto index = double{0};
    auto defaults = TArray<TSharedPtr<FJsonValue>>{};
    if (field(foundLocator.ToSharedRef(), TEXT("kind")) != TEXT("vector") ||
        !foundLocator->TryGetNumberField(TEXT("association"), association) ||
        association != static_cast<int32>(EMaterialParameterAssociation::GlobalParameter) ||
        !foundLocator->TryGetNumberField(TEXT("index"), index) || index != INDEX_NONE ||
        !FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(
            field(foundLocator.ToSharedRef(), TEXT("defaultValueJson"))), defaults) || defaults.Num() != 4 ||
        defaults[0]->AsNumber() != defaultTint.R || defaults[1]->AsNumber() != defaultTint.G ||
        defaults[2]->AsNumber() != defaultTint.B || defaults[3]->AsNumber() != defaultTint.A)
    {
        AddError(FString::Printf(TEXT("Invalid typed vector metadata: %s"), *serialize(foundLocator.ToSharedRef())));
        return false;
    }
    auto locator = MakeShared<FJsonObject>();
    locator->SetStringField(TEXT("name"), parameterName);
    locator->SetNumberField(TEXT("association"), association);
    locator->SetNumberField(TEXT("index"), index);
    auto parameterRequest = requestFor(TEXT("resource_identity_probe"), projectId);
    parameterRequest->SetObjectField(TEXT("resourceBinding"), binding(TEXT("unreal.object-path"), materialPath));
    parameterRequest->SetObjectField(TEXT("parameterBinding"), binding(TEXT("unreal.material-parameter.v1"), serialize(locator)));
    passed &= expect(TEXT("Typed material parameter probe"), send(parameterRequest), TEXT("Resolved"), TEXT("vector"));
    locator->SetStringField(TEXT("name"), TEXT("LibraMissingParameter"));
    parameterRequest->SetObjectField(TEXT("parameterBinding"), binding(TEXT("unreal.material-parameter.v1"), serialize(locator)));
    passed &= expect(TEXT("Missing material parameter"), send(parameterRequest), TEXT("Missing"));

    // Unreal delivery uses the exact object path and locator directly, not a Unity-style binding asset.
    // Exercise those values using the same engine APIs as the renderer (no package playback here).
    auto* loadedMaterial = LoadObject<UMaterialInterface>(nullptr, *materialPath);
    passed &= TestTrue(TEXT("Typed engine loads use the registered paths without a binding table"),
        LoadObject<UStaticMesh>(nullptr, *cubePath) != nullptr &&
        LoadObject<UTexture2D>(nullptr, *texturePath) == texture.Get() && loadedMaterial == material.Get());
    if (!loadedMaterial) return false;
    const auto instance = TStrongObjectPtr<UMaterialInstanceDynamic>(
        UMaterialInstanceDynamic::Create(loadedMaterial, GetTransientPackage()));
    if (!TestNotNull(TEXT("Runtime material instance"), instance.Get())) return false;
    const auto parameterInfo = FMaterialParameterInfo(FName(*parameterName),
        static_cast<EMaterialParameterAssociation>(static_cast<int32>(association)), static_cast<int32>(index));
    const auto overrideTint = FLinearColor(0.75f, 0.5f, 0.25f, 1.0f);
    instance->SetVectorParameterValueByInfo(parameterInfo, overrideTint);
    auto appliedTint = FLinearColor{};
    passed &= TestTrue(TEXT("Runtime MID applies the exact probed vector locator"),
        instance->GetVectorParameterValue(parameterInfo, appliedTint) && appliedTint.Equals(overrideTint));
    return passed;
}

#endif
