#include "LibraEditorResourceIdentityAdapter.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "HAL/FileManager.h"
#include "Materials/MaterialInterface.h"
#include "Misc/ConfigCacheIni.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UObject/SoftObjectPath.h"

namespace
{
    const TCHAR* IdentitySection = TEXT("LiberaFx.ResourceIdentity");
    const TCHAR* IdentityKey = TEXT("ProjectId");

    struct FProjectIdentityState
    {
        FString Status;
        FString ProjectId;
        FString Detail;
    };

    FString ProjectMarkerPath()
    {
        return FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("LibraFxProjectIdentity.marker"));
    }

    FString ProjectIdentityConfigPath()
    {
        return FPaths::Combine(FPaths::ProjectConfigDir(), TEXT("LibraFxResourceIdentity.ini"));
    }

    FProjectIdentityState ReadProjectIdentity()
    {
        auto state = FProjectIdentityState{};
        if (!GConfig)
        {
            state.Status = TEXT("QueryFailure");
            state.Detail = TEXT("Unreal configuration is unavailable.");
            return state;
        }

        const auto configPath = ProjectIdentityConfigPath();
        const auto hasProjectFile = IFileManager::Get().FileExists(*configPath);
        if (hasProjectFile)
        {
            auto config = FConfigFile{};
            config.Read(configPath);
            config.GetString(IdentitySection, IdentityKey, state.ProjectId);
        }
        else
        {
            GConfig->GetString(IdentitySection, IdentityKey, state.ProjectId, GGameIni);
        }
        auto projectGuid = FGuid{};
        if (state.ProjectId.IsEmpty() || !FGuid::ParseExact(state.ProjectId, EGuidFormats::DigitsWithHyphens, projectGuid) ||
            !projectGuid.IsValid())
        {
            state.ProjectId.Reset();
            state.Status = hasProjectFile ? TEXT("QueryFailure") : TEXT("Unregistered");
            state.Detail = hasProjectFile ? TEXT("The Unreal project identity file is invalid or unreadable.") :
                TEXT("No explicit Unreal project identity is registered.");
            return state;
        }
        state.ProjectId = projectGuid.ToString(EGuidFormats::DigitsWithHyphensLower);

        auto marker = FString{};
        const auto markerPath = ProjectMarkerPath();
        if (!IFileManager::Get().FileExists(*markerPath))
        {
            state.Status = TEXT("CloneDecisionRequired");
            state.Detail = TEXT("The project identity has no local clone-policy marker.");
            return state;
        }
        if (!FFileHelper::LoadFileToString(marker, *markerPath))
        {
            state.Status = TEXT("QueryFailure");
            state.Detail = TEXT("Could not read the local clone-policy marker.");
            return state;
        }
        marker.TrimStartAndEndInline();
        auto markerGuid = FGuid{};
        if (!FGuid::ParseExact(marker, EGuidFormats::DigitsWithHyphens, markerGuid) ||
            markerGuid != projectGuid)
        {
            state.Status = TEXT("CloneDecisionRequired");
            state.Detail = TEXT("The local project marker differs from the saved project identity.");
            return state;
        }
        state.Status = TEXT("Ready");
        return state;
    }

    bool SaveProjectIdentity(const FGuid& id, FString& error)
    {
        if (!id.IsValid() || !GConfig)
        {
            error = TEXT("A valid project GUID and Unreal configuration are required.");
            return false;
        }
        const auto text = id.ToString(EGuidFormats::DigitsWithHyphensLower);
        const auto path = ProjectIdentityConfigPath();
        const auto temporary = path + TEXT(".") + FGuid::NewGuid().ToString(EGuidFormats::Digits) + TEXT(".tmp");
        const auto content = FString::Printf(TEXT("[%s]\n%s=%s\n"), IdentitySection, IdentityKey, *text);
        if (!FFileHelper::SaveStringToFile(content, *temporary) ||
            !IFileManager::Get().Move(*path, *temporary, true, false, false, true))
        {
            IFileManager::Get().Delete(*temporary, false, true);
            error = TEXT("Could not save the persistent Unreal project identity.");
            return false;
        }
        if (!FFileHelper::SaveStringToFile(text, *ProjectMarkerPath()))
        {
            error = TEXT("Could not write the local clone-policy marker.");
            return false;
        }
        return true;
    }

    FString AssetKind(const UObject* asset)
    {
        if (!asset) return {};
        if (asset->IsA<UStaticMesh>()) return TEXT("model");
        if (asset->IsA<UTexture2D>()) return TEXT("texture");
        if (asset->IsA<UMaterialInterface>()) return TEXT("material");
        return {};
    }

    TSharedRef<FJsonObject> NewReply(const FString& requestId, const FString& operation)
    {
        auto reply = MakeShared<FJsonObject>();
        reply->SetStringField(TEXT("type"), TEXT("resource_identity_result"));
        reply->SetStringField(TEXT("requestId"), requestId);
        reply->SetStringField(TEXT("operation"), operation);
        return reply;
    }

    void AddProject(const TSharedRef<FJsonObject>& reply, const FProjectIdentityState& state)
    {
        auto project = MakeShared<FJsonObject>();
        project->SetStringField(TEXT("engine"), TEXT("Unreal"));
        project->SetStringField(TEXT("projectId"), state.ProjectId);
        reply->SetObjectField(TEXT("project"), project);
        reply->SetStringField(TEXT("projectState"), state.Status);
        if (!state.Detail.IsEmpty()) reply->SetStringField(TEXT("detail"), state.Detail);
    }

    FString SerializeResourceReply(const TSharedRef<FJsonObject>& object)
    {
        auto text = FString{};
        auto writer = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&text);
        FJsonSerializer::Serialize(object, writer);
        writer->Close();
        return text;
    }

    FString SerializeResourceArray(const TArray<TSharedPtr<FJsonValue>>& values)
    {
        auto text = FString{};
        auto writer = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&text);
        FJsonSerializer::Serialize(values, writer);
        writer->Close();
        return text;
    }

    bool ReadBinding(const TSharedPtr<FJsonObject>& object, const TCHAR* field,
        FString& format, FString& value)
    {
        const TSharedPtr<FJsonObject>* binding = nullptr;
        return object->TryGetObjectField(field, binding) && binding && binding->IsValid() &&
            (*binding)->TryGetStringField(TEXT("format"), format) &&
            (*binding)->TryGetStringField(TEXT("value"), value) &&
            !format.IsEmpty() && !value.IsEmpty();
    }

    FString QueryResource(const FString& format, const FString& value, FString& actualKind, FString& detail)
    {
        if (format != TEXT("unreal.object-path") || !FPackageName::IsValidObjectPath(value) ||
            !(value.StartsWith(TEXT("/Game/")) || value.StartsWith(TEXT("/Engine/"))))
        {
            detail = TEXT("Unsupported or malformed Unreal object-path binding.");
            return TEXT("QueryFailure");
        }
        if (FAssetRegistryModule::GetRegistry().IsLoadingAssets())
        {
            detail = TEXT("Unreal Asset Registry is still loading.");
            return TEXT("QueryFailure");
        }
        auto asset = StaticLoadObject(UObject::StaticClass(), nullptr, *value);
        if (!asset)
        {
            const auto packageName = FPackageName::ObjectPathToPackageName(value);
            detail = FPackageName::DoesPackageExist(packageName, nullptr)
                ? TEXT("The package exists but the registered object could not be loaded.")
                : TEXT("The registered Unreal object path is missing.");
            return detail.StartsWith(TEXT("The package exists"))
                ? TEXT("QueryFailure") : TEXT("Missing");
        }
        actualKind = AssetKind(asset);
        if (actualKind.IsEmpty())
        {
            detail = TEXT("The registered Unreal object has an unsupported class.");
            return TEXT("TypeMismatch");
        }
        return TEXT("Resolved");
    }

    FString QueryParameter(const TSharedPtr<FJsonObject>& request, FString& actualKind, FString& detail)
    {
        auto resourceFormat = FString{};
        auto resourceValue = FString{};
        auto parameterFormat = FString{};
        auto parameterValue = FString{};
        if (!ReadBinding(request, TEXT("resourceBinding"), resourceFormat, resourceValue) ||
            !ReadBinding(request, TEXT("parameterBinding"), parameterFormat, parameterValue) ||
            parameterFormat != TEXT("unreal.material-parameter.v1"))
        {
            detail = TEXT("Unsupported or malformed Unreal material parameter binding.");
            return TEXT("QueryFailure");
        }
        auto resourceKind = FString{};
        const auto resourceStatus = QueryResource(resourceFormat, resourceValue, resourceKind, detail);
        if (resourceStatus != TEXT("Resolved")) return resourceStatus;
        auto material = Cast<UMaterialInterface>(StaticLoadObject(UObject::StaticClass(), nullptr, *resourceValue));
        if (!material)
        {
            detail = TEXT("The registered resource is not a material.");
            return TEXT("TypeMismatch");
        }
        auto reader = TJsonReaderFactory<>::Create(parameterValue);
        auto binding = TSharedPtr<FJsonObject>{};
        auto name = FString{};
        auto associationValue = double{0};
        auto indexValue = double{0};
        if (!FJsonSerializer::Deserialize(reader, binding) || !binding ||
            !binding->TryGetStringField(TEXT("name"), name) || name.IsEmpty() || name.Len() > 128 ||
            !binding->TryGetNumberField(TEXT("association"), associationValue) ||
            !binding->TryGetNumberField(TEXT("index"), indexValue) ||
            !FMath::IsFinite(associationValue) || !FMath::IsFinite(indexValue) ||
            FMath::FloorToDouble(associationValue) != associationValue ||
            FMath::FloorToDouble(indexValue) != indexValue ||
            associationValue < 0 || associationValue > 2 || indexValue < -1 || indexValue > 65535)
        {
            detail = TEXT("Invalid Unreal material parameter locator.");
            return TEXT("QueryFailure");
        }
        const auto info = FMaterialParameterInfo(FName(*name),
            static_cast<EMaterialParameterAssociation>(static_cast<int32>(associationValue)),
            static_cast<int32>(indexValue));
        auto scalar = float{0};
        if (material->GetScalarParameterValue(info, scalar))
        {
            actualKind = TEXT("scalar");
            return TEXT("Resolved");
        }
        auto vector = FLinearColor::White;
        if (material->GetVectorParameterValue(info, vector))
        {
            actualKind = TEXT("vector");
            return TEXT("Resolved");
        }
        auto texture = static_cast<UTexture*>(nullptr);
        if (material->GetTextureParameterValue(info, texture))
        {
            actualKind = TEXT("texture");
            return TEXT("Resolved");
        }
        detail = TEXT("The registered material parameter no longer exists.");
        return TEXT("Missing");
    }

    FString ReadMaterialSchema(const TSharedPtr<FJsonObject>& request,
        const TSharedRef<FJsonObject>& reply, FString& detail)
    {
        auto format = FString{};
        auto value = FString{};
        if (!ReadBinding(request, TEXT("resourceBinding"), format, value))
        {
            detail = TEXT("Missing or malformed material resource binding.");
            return TEXT("QueryFailure");
        }
        auto actualKind = FString{};
        auto status = QueryResource(format, value, actualKind, detail);
        if (status != TEXT("Resolved")) return status;
        if (actualKind != TEXT("material"))
        {
            detail = TEXT("The registered Unreal resource is not a material.");
            return TEXT("TypeMismatch");
        }
        auto material = Cast<UMaterialInterface>(StaticLoadObject(UObject::StaticClass(), nullptr, *value));
        if (!material)
        {
            detail = TEXT("The registered Unreal material could not be loaded.");
            return TEXT("QueryFailure");
        }
        auto parameters = TArray<TSharedPtr<FJsonValue>>{};
        auto append = [&parameters](const FMaterialParameterInfo& info, const FString& kind,
            const FString& defaultJson)
        {
            if (parameters.Num() >= 256) return;
            auto item = MakeShared<FJsonObject>();
            item->SetStringField(TEXT("name"), info.Name.ToString());
            item->SetStringField(TEXT("kind"), kind);
            item->SetStringField(TEXT("defaultValueJson"), defaultJson);
            item->SetStringField(TEXT("minimumJson"), TEXT(""));
            item->SetStringField(TEXT("maximumJson"), TEXT(""));
            item->SetNumberField(TEXT("association"), static_cast<int32>(info.Association));
            item->SetNumberField(TEXT("index"), info.Index);
            parameters.Add(MakeShared<FJsonValueObject>(item));
        };
        auto ignoredIds = TArray<FGuid>{};
        auto scalarInfos = TArray<FMaterialParameterInfo>{};
        material->GetAllScalarParameterInfo(scalarInfos, ignoredIds);
        for (const auto& info : scalarInfos)
        {
            auto current = float{0};
            if (material->GetScalarParameterValue(info, current))
                append(info, TEXT("scalar"), FString::SanitizeFloat(current));
        }
        ignoredIds.Reset();
        auto vectorInfos = TArray<FMaterialParameterInfo>{};
        material->GetAllVectorParameterInfo(vectorInfos, ignoredIds);
        for (const auto& info : vectorInfos)
        {
            auto current = FLinearColor::White;
            if (!material->GetVectorParameterValue(info, current)) continue;
            auto values = TArray<TSharedPtr<FJsonValue>>{};
            values.Add(MakeShared<FJsonValueNumber>(current.R));
            values.Add(MakeShared<FJsonValueNumber>(current.G));
            values.Add(MakeShared<FJsonValueNumber>(current.B));
            values.Add(MakeShared<FJsonValueNumber>(current.A));
            append(info, TEXT("vector"), SerializeResourceArray(values));
        }
        ignoredIds.Reset();
        auto textureInfos = TArray<FMaterialParameterInfo>{};
        material->GetAllTextureParameterInfo(textureInfos, ignoredIds);
        for (const auto& info : textureInfos) append(info, TEXT("texture"), TEXT("null"));
        reply->SetArrayField(TEXT("parameters"), MoveTemp(parameters));
        reply->SetStringField(TEXT("actualKind"), TEXT("material"));
        return TEXT("Resolved");
    }

    bool ApplyIdentityPolicy(const FString& operation, FString& error)
    {
        const auto current = ReadProjectIdentity();
        if (operation == TEXT("initialize"))
        {
            if (current.Status != TEXT("Unregistered"))
            {
                error = TEXT("Project identity is already registered; use keep or fork explicitly.");
                return false;
            }
            return SaveProjectIdentity(FGuid::NewGuid(), error);
        }
        if (operation == TEXT("keep"))
        {
            auto projectId = FString{};
            if (!GConfig) return false;
            GConfig->GetString(IdentitySection, IdentityKey, projectId, GGameIni);
            auto id = FGuid{};
            if (!FGuid::ParseExact(projectId, EGuidFormats::DigitsWithHyphens, id) || !id.IsValid())
            {
                error = TEXT("There is no valid project identity to keep.");
                return false;
            }
            return FFileHelper::SaveStringToFile(id.ToString(EGuidFormats::DigitsWithHyphensLower),
                *ProjectMarkerPath());
        }
        if (operation == TEXT("fork")) return SaveProjectIdentity(FGuid::NewGuid(), error);
        error = TEXT("Unsupported project identity operation.");
        return false;
    }
}

FString FLibraEditorResourceIdentityAdapter::ProcessRequest(const FString& requestText)
{
    check(IsInGameThread());
    auto reader = TJsonReaderFactory<>::Create(requestText);
    auto request = TSharedPtr<FJsonObject>{};
    auto requestId = FString{};
    auto type = FString{};
    auto operation = FString{};
    auto error = FString{};
    auto requestGuid = FGuid{};
    if (!FJsonSerializer::Deserialize(reader, request) || !request ||
        !request->TryGetStringField(TEXT("requestId"), requestId) ||
        !FGuid::ParseExact(requestId, EGuidFormats::DigitsWithHyphens, requestGuid) || !requestGuid.IsValid() ||
        !request->TryGetStringField(TEXT("type"), type))
    {
        return TEXT("{\"type\":\"resource_identity_result\",\"operation\":\"invalid\",\"status\":\"QueryFailure\",\"detail\":\"Invalid request.\"}");
    }

    if (type == TEXT("resource_identity_project"))
    {
        request->TryGetStringField(TEXT("operation"), operation);
        if (operation == TEXT("initialize") || operation == TEXT("keep") || operation == TEXT("fork"))
        {
            if (!ApplyIdentityPolicy(operation, error))
            {
                auto reply = NewReply(requestId, operation);
                AddProject(reply, ReadProjectIdentity());
                reply->SetStringField(TEXT("status"), TEXT("QueryFailure"));
                reply->SetStringField(TEXT("detail"), error);
                return SerializeResourceReply(reply);
            }
        }
        auto reply = NewReply(requestId, operation.IsEmpty() ? TEXT("status") : operation);
        const auto state = ReadProjectIdentity();
        AddProject(reply, state);
        reply->SetStringField(TEXT("status"), state.Status);
        return SerializeResourceReply(reply);
    }

    const auto isSchema = type == TEXT("resource_identity_schema");
    auto reply = NewReply(requestId, type == TEXT("resource_identity_probe") ? TEXT("probe") :
        isSchema ? TEXT("schema") : TEXT("invalid"));
    const auto project = ReadProjectIdentity();
    AddProject(reply, project);
    auto expectedProjectId = FString{};
    request->TryGetStringField(TEXT("projectId"), expectedProjectId);
    if (type != TEXT("resource_identity_probe") && !isSchema)
    {
        reply->SetStringField(TEXT("status"), TEXT("QueryFailure"));
        reply->SetStringField(TEXT("detail"), TEXT("Unsupported resource identity request."));
        return SerializeResourceReply(reply);
    }
    if (project.Status != TEXT("Ready") || expectedProjectId != project.ProjectId)
    {
        reply->SetStringField(TEXT("status"), TEXT("Offline"));
        reply->SetStringField(TEXT("detail"), TEXT("The requested Unreal project identity is not active and verified."));
        return SerializeResourceReply(reply);
    }

    if (isSchema)
    {
        auto detail = FString{};
        const auto status = ReadMaterialSchema(request, reply, detail);
        reply->SetStringField(TEXT("status"), status);
        if (!detail.IsEmpty()) reply->SetStringField(TEXT("detail"), detail);
        return SerializeResourceReply(reply);
    }

    auto actualKind = FString{};
    auto detail = FString{};
    auto status = FString{};
    if (request->HasField(TEXT("parameterBinding")))
        status = QueryParameter(request, actualKind, detail);
    else
    {
        auto format = FString{};
        auto value = FString{};
        status = ReadBinding(request, TEXT("binding"), format, value)
            ? QueryResource(format, value, actualKind, detail)
            : TEXT("QueryFailure");
        if (status == TEXT("QueryFailure") && detail.IsEmpty())
            detail = TEXT("Missing or malformed resource binding.");
    }
    reply->SetStringField(TEXT("status"), status);
    reply->SetStringField(TEXT("actualKind"), actualKind);
    if (!detail.IsEmpty()) reply->SetStringField(TEXT("detail"), detail);
    return SerializeResourceReply(reply);
}
