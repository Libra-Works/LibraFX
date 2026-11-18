#include "LibraEditorAssetAdapter.h"
#include "LibraEditorResourceIdentityAdapter.h"

#include "AssetRegistry/AssetData.h"
#include "AssetRegistry/AssetRegistryModule.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "Engine/TextureRenderTarget2D.h"
#include "ImageUtils.h"
#include "Materials/MaterialInterface.h"
#include "Misc/Base64.h"
#include "Misc/PackageName.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "ObjectTools.h"
#include "TextureResource.h"
#include "UObject/SoftObjectPath.h"

namespace
{
    constexpr int32 MaxCatalogPageSize = 64;
    constexpr int32 MaxAssetParameters = 256;
    constexpr int32 MaxThumbnailBytes = 256 * 1024;

    FString AssetClassFor(const UObject* asset)
    {
        if (asset->IsA<UStaticMesh>()) return TEXT("model");
        if (asset->IsA<UTexture2D>()) return TEXT("texture");
        if (asset->IsA<UMaterialInterface>()) return TEXT("material");
        return {};
    }

    bool IsAllowedAssetPath(const FString& path)
    {
        return FPackageName::IsValidObjectPath(path) &&
            (path.StartsWith(TEXT("/Game/")) || path.StartsWith(TEXT("/Engine/")));
    }

    FString Serialize(const TSharedRef<FJsonObject>& object)
    {
        auto text = FString{};
        auto writer = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&text);
        FJsonSerializer::Serialize(object, writer);
        writer->Close();
        return text;
    }

    void AddError(const TSharedRef<FJsonObject>& reply, const FString& requestId,
        const FString& operation, const FString& error)
    {
        reply->SetStringField(TEXT("type"), TEXT("asset_result"));
        reply->SetStringField(TEXT("requestId"), requestId);
        reply->SetStringField(TEXT("operation"), operation);
        reply->SetField(TEXT("asset"), MakeShared<FJsonValueNull>());
        reply->SetStringField(TEXT("error"), error);
    }

    TSharedRef<FJsonObject> DescribeMaterial(UMaterialInterface* material, int32 slot, int32& parameterBudget)
    {
        auto entry = MakeShared<FJsonObject>();
        entry->SetNumberField(TEXT("slot"), slot);
        entry->SetStringField(TEXT("path"), material->GetPathName());
        entry->SetStringField(TEXT("name"), material->GetName());
        auto parameters = TArray<TSharedPtr<FJsonValue>>{};
        auto append = [&parameters, &parameterBudget](const FString& name, const FString& kind, TSharedRef<FJsonValue> value,
            const FMaterialParameterInfo& info)
        {
            if (parameterBudget <= 0) return;
            --parameterBudget;
            auto parameter = MakeShared<FJsonObject>();
            parameter->SetStringField(TEXT("name"), name);
            parameter->SetStringField(TEXT("kind"), kind);
            parameter->SetField(TEXT("value"), value);
            parameter->SetNumberField(TEXT("association"), static_cast<int32>(info.Association));
            parameter->SetNumberField(TEXT("index"), info.Index);
            parameters.Add(MakeShared<FJsonValueObject>(parameter));
        };
        auto ignoredIds = TArray<FGuid>{};
        auto scalarInfos = TArray<FMaterialParameterInfo>{};
        material->GetAllScalarParameterInfo(scalarInfos, ignoredIds);
        for (const auto& info : scalarInfos)
        {
            auto value = float{0};
            if (material->GetScalarParameterValue(info, value))
                append(info.Name.ToString(), TEXT("scalar"), MakeShared<FJsonValueNumber>(value), info);
        }
        ignoredIds.Reset();
        auto vectorInfos = TArray<FMaterialParameterInfo>{};
        material->GetAllVectorParameterInfo(vectorInfos, ignoredIds);
        for (const auto& info : vectorInfos)
        {
            auto value = FLinearColor::White;
            if (!material->GetVectorParameterValue(info, value)) continue;
            auto components = TArray<TSharedPtr<FJsonValue>>{};
            components.Add(MakeShared<FJsonValueNumber>(value.R));
            components.Add(MakeShared<FJsonValueNumber>(value.G));
            components.Add(MakeShared<FJsonValueNumber>(value.B));
            components.Add(MakeShared<FJsonValueNumber>(value.A));
            append(info.Name.ToString(), TEXT("vector"), MakeShared<FJsonValueArray>(MoveTemp(components)), info);
        }
        ignoredIds.Reset();
        auto textureInfos = TArray<FMaterialParameterInfo>{};
        material->GetAllTextureParameterInfo(textureInfos, ignoredIds);
        for (const auto& info : textureInfos)
        {
            auto texture = static_cast<UTexture*>(nullptr);
            if (!material->GetTextureParameterValue(info, texture)) continue;
            if (texture)
                append(info.Name.ToString(), TEXT("texture"), MakeShared<FJsonValueString>(texture->GetPathName()), info);
            else
                append(info.Name.ToString(), TEXT("texture"), MakeShared<FJsonValueNull>(), info);
        }
        entry->SetArrayField(TEXT("parameters"), MoveTemp(parameters));
        return entry;
    }

    void AddAssetIdentity(const TSharedRef<FJsonObject>& data, UObject* asset)
    {
        data->SetStringField(TEXT("path"), asset->GetPathName());
        data->SetStringField(TEXT("name"), asset->GetName());
        data->SetStringField(TEXT("assetClass"), AssetClassFor(asset));
        data->SetStringField(TEXT("engine"), TEXT("Unreal"));
    }

    FString Catalog(const TSharedRef<FJsonObject>& reply, const FString& requestId,
        const TSharedRef<FJsonObject>& request)
    {
        auto query = FString{};
        auto assetClass = FString{};
        auto offset = 0;
        auto limit = 32;
        request->TryGetStringField(TEXT("query"), query);
        request->TryGetStringField(TEXT("assetClass"), assetClass);
        auto number = double{0};
        if (request->TryGetNumberField(TEXT("offset"), number))
        {
            if (!FMath::IsFinite(number) || number < 0 || number > MAX_int32 || FMath::FloorToDouble(number) != number)
                return TEXT("Invalid catalog offset.");
            offset = static_cast<int32>(number);
        }
        if (request->TryGetNumberField(TEXT("limit"), number))
        {
            if (!FMath::IsFinite(number) || number < 1 || number > MaxCatalogPageSize || FMath::FloorToDouble(number) != number)
                return TEXT("Catalog limit must be from 1 to 64.");
            limit = static_cast<int32>(number);
        }
        if (query.Len() > 128) return TEXT("Catalog query is too long.");
        if (!assetClass.IsEmpty() && assetClass != TEXT("model") && assetClass != TEXT("texture") &&
            assetClass != TEXT("material")) return TEXT("Unsupported Unreal asset class.");

        auto assets = TArray<FAssetData>{};
        auto& registry = FAssetRegistryModule::GetRegistry();
        auto addClass = [&registry, &assets](UClass* type)
        {
            auto matching = TArray<FAssetData>{};
            registry.GetAssetsByClass(type->GetClassPathName(), matching, true);
            assets.Append(MoveTemp(matching));
        };
        if (assetClass.IsEmpty() || assetClass == TEXT("model")) addClass(UStaticMesh::StaticClass());
        if (assetClass.IsEmpty() || assetClass == TEXT("texture")) addClass(UTexture2D::StaticClass());
        if (assetClass.IsEmpty() || assetClass == TEXT("material")) addClass(UMaterialInterface::StaticClass());
        auto seen = TSet<FString>{};
        assets.RemoveAll([&query, &seen](const FAssetData& data)
        {
            auto path = data.GetSoftObjectPath().ToString();
            if (!IsAllowedAssetPath(path) || seen.Contains(path)) return true;
            seen.Add(path);
            return
                (!query.IsEmpty() && !data.AssetName.ToString().Contains(query, ESearchCase::IgnoreCase) &&
                 !path.Contains(query, ESearchCase::IgnoreCase));
        });
        assets.Sort([](const FAssetData& left, const FAssetData& right)
        {
            return left.GetSoftObjectPath().ToString() < right.GetSoftObjectPath().ToString();
        });
        auto entries = TArray<TSharedPtr<FJsonValue>>{};
        auto start = FMath::Min(offset, assets.Num());
        auto end = FMath::Min(start + limit, assets.Num());
        for (auto index = start; index < end; ++index)
        {
            const auto& asset = assets[index];
            auto entry = MakeShared<FJsonObject>();
            auto path = asset.GetSoftObjectPath().ToString();
            entry->SetStringField(TEXT("path"), path);
            entry->SetStringField(TEXT("name"), asset.AssetName.ToString());
            entry->SetStringField(TEXT("assetClass"), assetClass.IsEmpty()
                ? (asset.AssetClassPath == UStaticMesh::StaticClass()->GetClassPathName() ? TEXT("model")
                    : asset.AssetClassPath == UTexture2D::StaticClass()->GetClassPathName() ? TEXT("texture") : TEXT("material"))
                : assetClass);
            entry->SetStringField(TEXT("engine"), TEXT("Unreal"));
            entries.Add(MakeShared<FJsonValueObject>(entry));
        }
        reply->SetStringField(TEXT("type"), TEXT("asset_result"));
        reply->SetStringField(TEXT("requestId"), requestId);
        reply->SetStringField(TEXT("operation"), TEXT("catalog"));
        reply->SetArrayField(TEXT("assets"), MoveTemp(entries));
        reply->SetNumberField(TEXT("total"), assets.Num());
        reply->SetNumberField(TEXT("offset"), offset);
        reply->SetField(TEXT("error"), MakeShared<FJsonValueNull>());
        return {};
    }

    FString Details(const TSharedRef<FJsonObject>& reply, const FString& requestId,
        const TSharedRef<FJsonObject>& request)
    {
        auto path = FString{};
        if (!request->TryGetStringField(TEXT("path"), path) || !IsAllowedAssetPath(path))
            return TEXT("Asset path must be a valid /Game or /Engine object path.");
        auto asset = StaticLoadObject(UObject::StaticClass(), nullptr, *path);
        if (!asset) return TEXT("Asset was not found or could not be loaded.");
        auto assetClass = AssetClassFor(asset);
        if (assetClass.IsEmpty()) return TEXT("Unsupported Unreal asset type.");
        auto details = MakeShared<FJsonObject>();
        AddAssetIdentity(details, asset);
        auto materials = TArray<TSharedPtr<FJsonValue>>{};
        auto parameterBudget = MaxAssetParameters;
        if (auto mesh = Cast<UStaticMesh>(asset))
        {
            const auto& slots = mesh->GetStaticMaterials();
            for (auto index = 0; index < slots.Num() && index < 32 && parameterBudget > 0; ++index)
                if (auto material = slots[index].MaterialInterface)
                    materials.Add(MakeShared<FJsonValueObject>(DescribeMaterial(material, index, parameterBudget)));
        }
        else if (auto material = Cast<UMaterialInterface>(asset))
            materials.Add(MakeShared<FJsonValueObject>(DescribeMaterial(material, 0, parameterBudget)));
        details->SetArrayField(TEXT("materials"), MoveTemp(materials));
        if (auto texture = Cast<UTexture2D>(asset))
        {
            details->SetNumberField(TEXT("width"), texture->GetSizeX());
            details->SetNumberField(TEXT("height"), texture->GetSizeY());
        }
        reply->SetStringField(TEXT("type"), TEXT("asset_result"));
        reply->SetStringField(TEXT("requestId"), requestId);
        reply->SetStringField(TEXT("operation"), TEXT("details"));
        reply->SetObjectField(TEXT("asset"), details);
        reply->SetField(TEXT("error"), MakeShared<FJsonValueNull>());
        return {};
    }

    FString Thumbnail(const TSharedRef<FJsonObject>& reply, const FString& requestId,
        const TSharedRef<FJsonObject>& request)
    {
        auto path = FString{};
        auto size = 128;
        auto requestedSize = double{128};
        if (!request->TryGetStringField(TEXT("path"), path) || !IsAllowedAssetPath(path))
            return TEXT("Asset path must be a valid /Game or /Engine object path.");
        if (request->TryGetNumberField(TEXT("size"), requestedSize) &&
            (!FMath::IsFinite(requestedSize) || requestedSize != 128))
            return TEXT("Thumbnail size must be 128.");
        auto asset = StaticLoadObject(UObject::StaticClass(), nullptr, *path);
        if (!asset || AssetClassFor(asset).IsEmpty()) return TEXT("Thumbnail asset is unsupported or unavailable.");
        auto target = NewObject<UTextureRenderTarget2D>(GetTransientPackage());
        target->ClearColor = FColor::Transparent;
        target->InitCustomFormat(size, size, PF_B8G8R8A8, true);
        target->UpdateResourceImmediate(true);
        auto resource = target->GameThread_GetRenderTargetResource();
        if (!resource) return TEXT("Unreal's render hardware interface is unavailable for thumbnails.");
        ThumbnailTools::RenderThumbnail(asset, size, size, ThumbnailTools::EThumbnailTextureFlushMode::AlwaysFlush,
            resource);
        auto pixels = TArray<FColor>{};
        if (!resource->ReadPixels(pixels) ||
            pixels.Num() != size * size)
            return TEXT("Unreal could not render a CPU-readable thumbnail for this asset.");
        auto png = TArray64<uint8>{};
        FImageUtils::PNGCompressImageArray(size, size,
            TArrayView64<const FColor>(pixels.GetData(), pixels.Num()), png);
        if (png.IsEmpty() || png.Num() > MaxThumbnailBytes) return TEXT("Thumbnail exceeds the bounded image size.");
        auto encoded = FString{};
        encoded = FBase64::Encode(png.GetData(), static_cast<uint32>(png.Num()));
        if (encoded.Len() > 900000) return TEXT("Thumbnail cannot fit the live-preview message limit.");
        reply->SetStringField(TEXT("type"), TEXT("asset_result"));
        reply->SetStringField(TEXT("requestId"), requestId);
        reply->SetStringField(TEXT("operation"), TEXT("thumbnail"));
        reply->SetStringField(TEXT("path"), path);
        reply->SetStringField(TEXT("mime"), TEXT("image/png"));
        reply->SetStringField(TEXT("imageBase64"), encoded);
        reply->SetNumberField(TEXT("width"), size);
        reply->SetNumberField(TEXT("height"), size);
        reply->SetField(TEXT("error"), MakeShared<FJsonValueNull>());
        return {};
    }
}

FString FLibraEditorAssetAdapter::ProcessRequest(const FString& requestText)
{
    check(IsInGameThread());
    auto reader = TJsonReaderFactory<>::Create(requestText);
    auto request = TSharedPtr<FJsonObject>{};
    auto type = FString{};
    auto requestId = FString{};
    auto operation = FString{};
    auto reply = MakeShared<FJsonObject>();
    auto error = FString{};
    auto id = FGuid{};
    if (!FJsonSerializer::Deserialize(reader, request) || !request ||
        !request->TryGetStringField(TEXT("requestId"), requestId) ||
        !FGuid::ParseExact(requestId, EGuidFormats::DigitsWithHyphens, id) || !id.IsValid())
        error = TEXT("Invalid asset request UUID.");
    else if (!request->TryGetStringField(TEXT("type"), type))
        error = TEXT("Asset request type is required.");
    else if (type == TEXT("resource_identity_project") || type == TEXT("resource_identity_probe") ||
        type == TEXT("resource_identity_schema") ||
        type == TEXT("resource_identity_deliver"))
        return FLibraEditorResourceIdentityAdapter::ProcessRequest(requestText);
    else if (type == TEXT("asset_catalog"))
    {
        operation = TEXT("catalog");
        error = Catalog(reply, requestId, request.ToSharedRef());
    }
    else if (type == TEXT("asset_details"))
    {
        operation = TEXT("details");
        error = Details(reply, requestId, request.ToSharedRef());
    }
    else if (type == TEXT("asset_thumbnail"))
    {
        operation = TEXT("thumbnail");
        error = Thumbnail(reply, requestId, request.ToSharedRef());
    }
    else
        error = TEXT("Unsupported asset operation.");
    if (!error.IsEmpty()) AddError(reply, requestId, operation, error);
    auto response = Serialize(reply);
    auto encoded = FTCHARToUTF8(*response);
    if (encoded.Length() > 900000)
    {
        reply = MakeShared<FJsonObject>();
        AddError(reply, requestId, operation, TEXT("Asset response exceeds the transport message limit."));
        response = Serialize(reply);
    }
    return response;
}
