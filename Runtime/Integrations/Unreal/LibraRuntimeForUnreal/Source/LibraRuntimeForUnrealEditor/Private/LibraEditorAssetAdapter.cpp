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
#include "Async/Async.h"
#include <atomic>
#include <memory>

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

    struct FCatalogEntry
    {
        FString Path, Name;
        uint8 Kind = 0;
    };

    // Keep path/name/class metadata only. Neither listing nor indexing loads packages,
    // textures, meshes, materials or thumbnails. All callers use the game thread.
    class FCatalogIndex final
    {
    public:
        FCatalogIndex()
        {
            auto& registry = FAssetRegistryModule::GetRegistry();
            const auto revision = Revision;
            Added = registry.OnAssetAdded().AddLambda([revision](const FAssetData&) { ++*revision; });
            Removed = registry.OnAssetRemoved().AddLambda([revision](const FAssetData&) { ++*revision; });
            Updated = registry.OnAssetUpdated().AddLambda([revision](const FAssetData&) { ++*revision; });
            UpdatedOnDisk = registry.OnAssetUpdatedOnDisk().AddLambda([revision](const FAssetData&) { ++*revision; });
            Renamed = registry.OnAssetRenamed().AddLambda([revision](const FAssetData&, const FString&) { ++*revision; });
        }
        ~FCatalogIndex()
        {
            if (FModuleManager::Get().IsModuleLoaded(TEXT("AssetRegistry")))
            {
                auto& registry = FAssetRegistryModule::GetRegistry();
                registry.OnAssetAdded().Remove(Added);
                registry.OnAssetRemoved().Remove(Removed);
                registry.OnAssetUpdated().Remove(Updated);
                registry.OnAssetUpdatedOnDisk().Remove(UpdatedOnDisk);
                registry.OnAssetRenamed().Remove(Renamed);
            }
            if (Pending.IsValid()) Pending.Wait();
        }
        const TArray<FCatalogEntry>* Read()
        {
            if (Pending.IsValid() && Pending.IsReady())
            {
                Entries = MoveTemp(Pending.GetMutable());
                Pending = {};
                IndexedRevision = BuildingRevision;
                Ready = true;
            }
            if (!Pending.IsValid() && (!Ready || IndexedRevision != Revision->load()))
            {
                BuildingRevision = Revision->load();
                auto& registry = FAssetRegistryModule::GetRegistry();
                TArray<FARFilter> filters;
                for (const auto* type : { UStaticMesh::StaticClass(), UTexture2D::StaticClass(), UMaterialInterface::StaticClass() })
                {
                    FARFilter filter;
                    filter.ClassPaths.Add(type->GetClassPathName());
                    filter.PackagePaths = { FName(TEXT("/Game")), FName(TEXT("/Engine")) };
                    filter.bRecursiveClasses = true;
                    filter.bRecursivePaths = true;
                    filters.Add(MoveTemp(filter));
                }
                // Include unsaved in-memory assets without accessing UObjects on workers.
                TArray<FCatalogEntry> memory;
                for (auto kind = 0; kind < filters.Num(); ++kind)
                {
                    TArray<FAssetData> assets;
                    registry.GetInMemoryAssets(filters[kind], assets);
                    for (const auto& asset : assets)
                    {
                        auto path = asset.GetSoftObjectPath().ToString();
                        if (IsAllowedAssetPath(path)) memory.Add({MoveTemp(path), asset.AssetName.ToString(), static_cast<uint8>(kind)});
                    }
                    filters[kind].bIncludeOnlyOnDiskAssets = true;
                }
                Pending = Async(EAsyncExecution::ThreadPool, [&registry, filters = MoveTemp(filters), result = MoveTemp(memory)]() mutable
                {
                    for (auto kind = 0; kind < filters.Num(); ++kind)
                        registry.EnumerateAssets(filters[kind], [&result, kind](const FAssetData& asset)
                        {
                            auto path = asset.GetSoftObjectPath().ToString();
                            if (IsAllowedAssetPath(path)) result.Add({MoveTemp(path), asset.AssetName.ToString(), static_cast<uint8>(kind)});
                            return true;
                        });
                    result.Sort([](const FCatalogEntry& left, const FCatalogEntry& right) { return left.Path < right.Path; });
                    // A loaded asset may also appear in the on-disk registry.
                    auto write = 0;
                    for (auto read = 0; read < result.Num(); ++read)
                        if (write == 0 || result[read].Path != result[write - 1].Path)
                        {
                            if (write != read) result[write] = MoveTemp(result[read]);
                            ++write;
                        }
                    result.SetNum(write);
                    return result;
                });
            }
            return Ready && IndexedRevision == Revision->load() ? &Entries : nullptr;
        }
    private:
        std::shared_ptr<std::atomic<uint64>> Revision = std::make_shared<std::atomic<uint64>>(1);
        uint64 IndexedRevision = 0, BuildingRevision = 0;
        bool Ready = false;
        FDelegateHandle Added, Removed, Updated, UpdatedOnDisk, Renamed;
        TArray<FCatalogEntry> Entries;
        TFuture<TArray<FCatalogEntry>> Pending;
    };
    TUniquePtr<FCatalogIndex> CatalogIndex;

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
        auto folder = FString{};
        auto offset = 0;
        auto limit = 32;
        request->TryGetStringField(TEXT("query"), query);
        request->TryGetStringField(TEXT("assetClass"), assetClass);
        if (request->HasField(TEXT("folder")) && !request->TryGetStringField(TEXT("folder"), folder))
            return TEXT("Catalog folder must be a string.");
        folder.TrimStartAndEndInline();
        while (folder.EndsWith(TEXT("/"))) folder.LeftChopInline(1);
        if (!folder.IsEmpty() && (folder.Len() > 512 ||
            !(folder == TEXT("/Game") || folder == TEXT("/Engine") || folder.StartsWith(TEXT("/Game/")) || folder.StartsWith(TEXT("/Engine/"))) ||
            !FPackageName::IsValidLongPackageName(folder, true)))
            return TEXT("Catalog folder must be /Game or /Engine and optionally a subfolder.");
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
        if (query.Len() > 256) return TEXT("Catalog query is too long.");
        if (!assetClass.IsEmpty() && assetClass != TEXT("model") && assetClass != TEXT("texture") &&
            assetClass != TEXT("material")) return TEXT("Unsupported Unreal asset class.");

        bool foldersOnly = false;
        if (request->HasField(TEXT("foldersOnly")) && !request->TryGetBoolField(TEXT("foldersOnly"), foldersOnly))
            return TEXT("foldersOnly must be a boolean.");
        if (foldersOnly)
        {
            TArray<FString> folders;
            if (folder.IsEmpty()) folders = { TEXT("/Engine"), TEXT("/Game") };
            else FAssetRegistryModule::GetRegistry().GetSubPaths(folder, folders, false);
            folders.Sort();
            TArray<TSharedPtr<FJsonValue>> values;
            for (int32 index = offset; index < folders.Num() && values.Num() < limit; ++index)
            {
                auto entry = MakeShared<FJsonObject>();
                entry->SetStringField(TEXT("path"), folders[index]);
                entry->SetStringField(TEXT("name"), FPackageName::GetShortName(folders[index]));
                values.Add(MakeShared<FJsonValueObject>(entry));
            }
            reply->SetStringField(TEXT("type"), TEXT("asset_result"));
            reply->SetStringField(TEXT("requestId"), requestId);
            reply->SetStringField(TEXT("operation"), TEXT("catalog"));
            reply->SetStringField(TEXT("folder"), folder);
            reply->SetArrayField(TEXT("folders"), MoveTemp(values));
            reply->SetArrayField(TEXT("assets"), {});
            reply->SetNumberField(TEXT("total"), folders.Num());
            reply->SetNumberField(TEXT("offset"), offset);
            reply->SetField(TEXT("error"), MakeShared<FJsonValueNull>());
            return {};
        }

        if (!CatalogIndex) CatalogIndex = MakeUnique<FCatalogIndex>();
        const auto* assets = CatalogIndex->Read();
        auto entries = TArray<TSharedPtr<FJsonValue>>{};
        auto total = 0;
        const TCHAR* kinds[] = { TEXT("model"), TEXT("texture"), TEXT("material") };
        if (assets) for (const auto& asset : *assets)
        {
            if ((!folder.IsEmpty() && !asset.Path.StartsWith(folder + TEXT("/"), ESearchCase::IgnoreCase)) ||
                (!assetClass.IsEmpty() && assetClass != kinds[asset.Kind]) ||
                (!query.IsEmpty() && !asset.Name.Contains(query, ESearchCase::IgnoreCase) &&
                    !asset.Path.Contains(query, ESearchCase::IgnoreCase))) continue;
            if (total++ < offset || entries.Num() >= limit) continue;
            auto entry = MakeShared<FJsonObject>();
            entry->SetStringField(TEXT("path"), asset.Path);
            entry->SetStringField(TEXT("name"), asset.Name);
            entry->SetStringField(TEXT("assetClass"), kinds[asset.Kind]);
            entry->SetStringField(TEXT("engine"), TEXT("Unreal"));
            entries.Add(MakeShared<FJsonValueObject>(entry));
        }
        reply->SetStringField(TEXT("type"), TEXT("asset_result"));
        reply->SetStringField(TEXT("requestId"), requestId);
        reply->SetStringField(TEXT("operation"), TEXT("catalog"));
        reply->SetStringField(TEXT("folder"), folder);
        reply->SetArrayField(TEXT("assets"), MoveTemp(entries));
        reply->SetNumberField(TEXT("total"), total);
        reply->SetBoolField(TEXT("pending"), assets == nullptr);
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
            (!FMath::IsFinite(requestedSize) || requestedSize < 16 || requestedSize > 256 || FMath::FloorToDouble(requestedSize) != requestedSize))
            return TEXT("Thumbnail size must be an integer from 16 to 256.");
        size = static_cast<int32>(requestedSize);
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

void FLibraEditorAssetAdapter::Shutdown()
{
    check(IsInGameThread());
    CatalogIndex.Reset();
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
