#include "LibraSpawnableAssetDetails.h"

#include "AssetRegistry/AssetData.h"
#include "DetailCategoryBuilder.h"
#include "DetailLayoutBuilder.h"
#include "DetailWidgetRow.h"
#include "EditorReimportHandler.h"
#include "IPropertyUtilities.h"
#include "LibraSourceProjectDetails.h"
#include "LibraSpawnableAsset.h"
#include "Misc/MessageDialog.h"
#include "PropertyCustomizationHelpers.h"
#include "ScopedTransaction.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "LibraSpawnableAssetDetails"

namespace {
FText KindLabel(ELibraPackageResourceKind kind)
{
    switch (kind)
    {
        case ELibraPackageResourceKind::Model: return LOCTEXT("Model", "Model");
        case ELibraPackageResourceKind::Texture: return LOCTEXT("Texture", "Texture");
        case ELibraPackageResourceKind::Material: return LOCTEXT("Material", "Material");
        default: return FText::GetEmpty();
    }
}

void AddResourceDetails(IDetailLayoutBuilder& builder, TWeakObjectPtr<ULibraSpawnableAsset> weakAsset,
    const TWeakPtr<IPropertyUtilities>& utilities)
{
    auto* asset = weakAsset.Get();
    if (!asset) return;
    auto& category = builder.EditCategory("LibraResourceOverrides",
        LOCTEXT("ResourceCategory", "LIBERA FX Resource Overrides"), ECategoryPriority::Important);
    auto keys = TArray<FLibraPackageResourceOverride>{};
    auto error = FString{};
    const auto read = asset->GetPackageResourceKeys(keys, error);
    auto message = !read ? FText::FromString(error) : keys.IsEmpty()
        ? LOCTEXT("NoResources", "This package has no external renderer resources.")
        : LOCTEXT("ResourceHelp", "Replace package resources for this Unreal asset. Clear a selection to use the original binding. Component bindings take precedence. Restart playback to apply changes.");
    category.AddCustomRow(LOCTEXT("ResourceHelpSearch", "LIBERA FX resources"))
        .WholeRowContent()[SNew(STextBlock).AutoWrapText(true).Text(message)];
    category.AddCustomRow(LOCTEXT("RefreshSearch", "Refresh resource list"))
        .WholeRowContent()[
            SNew(SButton).Text(LOCTEXT("Refresh", "Refresh resource list"))
            .OnClicked_Lambda([utilities] {
                if (auto pinned = utilities.Pin()) pinned->RequestForceRefresh();
                return FReply::Handled();
            })
        ];
    keys.Sort([](const auto& a, const auto& b) {
        if (a.Kind != b.Kind) return a.Kind < b.Kind;
        return a.Identity.Compare(b.Identity, ESearchCase::CaseSensitive) < 0;
    });
    for (const auto& key : keys)
    {
        auto label = FText::Format(LOCTEXT("ResourceKey", "{0}: {1}"),
            KindLabel(key.Kind), FText::FromString(key.Identity));
        category.AddCustomRow(label)
            .NameContent()[SNew(STextBlock).Text(label).ToolTipText(label).AutoWrapText(true)]
            .ValueContent().MinDesiredWidth(220)
            [
                SNew(SObjectPropertyEntryBox)
                .AllowedClass(ULibraSpawnableAsset::GetResourceClass(key.Kind))
                .AllowClear(true)
                .ObjectPath_Lambda([weakAsset, identity = key.Identity, kind = key.Kind] {
                    if (auto* package = weakAsset.Get())
                        if (auto* replacement = package->FindResourceOverride(identity, kind))
                            return replacement->GetPathName();
                    return FString{};
                })
                .OnObjectChanged_Lambda([weakAsset, identity = key.Identity, kind = key.Kind](const FAssetData& selected) {
                    auto* package = weakAsset.Get();
                    if (!package) return;
                    auto* replacement = selected.IsValid() ? selected.GetAsset() : nullptr;
                    if (selected.IsValid() && !replacement)
                    {
                        FMessageDialog::Open(EAppMsgType::Ok, LOCTEXT("AssetMissing", "Could not load the selected asset. The previous override was kept."));
                        return;
                    }
                    auto transaction = FScopedTransaction(LOCTEXT("SetResource", "Change LIBERA FX resource override"));
                    auto detail = FString{};
                    if (!package->SetResourceOverride(identity, kind, replacement, detail))
                    {
                        transaction.Cancel();
                        FMessageDialog::Open(EAppMsgType::Ok, FText::FromString(detail));
                    }
                })
            ];
    }
    for (const auto& entry : asset->GetResourceOverrides())
    {
        if (keys.ContainsByPredicate([&](const auto& key) {
            return key.Kind == entry.Kind && key.Identity.Equals(entry.Identity, ESearchCase::CaseSensitive);
        })) continue;
        auto label = FText::Format(LOCTEXT("UnlistedKey", "Not in resource list: {0}: {1} → {2}"),
            KindLabel(entry.Kind), FText::FromString(entry.Identity),
            FText::FromString(GetPathNameSafe(entry.Asset.Get())));
        category.AddCustomRow(label)
            .NameContent()[SNew(STextBlock).Text(label).ToolTipText(label).AutoWrapText(true)]
            .ValueContent()[
                SNew(SButton).Text(LOCTEXT("ClearOverride", "Clear override"))
                .OnClicked_Lambda([weakAsset, utilities, identity = entry.Identity, kind = entry.Kind] {
                    if (auto* package = weakAsset.Get())
                    {
                        auto transaction = FScopedTransaction(LOCTEXT("RemoveResource", "Clear LIBERA FX resource override"));
                        auto detail = FString{};
                        if (!package->SetResourceOverride(identity, kind, nullptr, detail)) transaction.Cancel();
                    }
                    if (auto pinned = utilities.Pin()) pinned->RequestForceRefresh();
                    return FReply::Handled();
                })
            ];
    }
}
}

auto FLibraSpawnableAssetDetails::MakeInstance() -> TSharedRef<IDetailCustomization>
{
    return MakeShared<FLibraSpawnableAssetDetails>();
}

void FLibraSpawnableAssetDetails::CustomizeDetails(IDetailLayoutBuilder& detailBuilder)
{
    auto objects = TArray<TWeakObjectPtr<UObject>>{};
    detailBuilder.GetObjectsBeingCustomized(objects);
    const auto utilities = TWeakPtr<IPropertyUtilities>(detailBuilder.GetPropertyUtilities());
    const auto single = objects.Num() == 1 ? objects[0] : TWeakObjectPtr<UObject>{};
    LibraSourceProject::AddDetails(detailBuilder, single);
    AddResourceDetails(detailBuilder, Cast<ULibraSpawnableAsset>(single.Get()), utilities);
    auto& category = detailBuilder.EditCategory("LibraPackage",
        LOCTEXT("PackageCategory", "LIBERA FX Package"), ECategoryPriority::Important);
    category.AddCustomRow(LOCTEXT("ReimportSearch", "Reimport from source"))
        .WholeRowContent()
        [
            SNew(SButton)
            .Text(LOCTEXT("ReimportLabel", "Reimport from source"))
            .ToolTipText(LOCTEXT("ReimportTooltip",
                "Update the embedded package from its source .lfxp file. "
                "If the source has moved, select its new location. "
                "A failed import leaves the existing package unchanged. Save the asset afterwards."))
            .IsEnabled_Lambda([objects] {
                for (const auto& object : objects)
                    if (Cast<ULibraSpawnableAsset>(object.Get())) return true;
                return false;
            })
            .OnClicked_Lambda([objects, utilities] {
                for (const auto& object : objects)
                    if (auto* asset = Cast<ULibraSpawnableAsset>(object.Get()))
                        FReimportManager::Instance()->Reimport(asset, true, true);
                if (auto pinned = utilities.Pin()) pinned->RequestForceRefresh();
                return FReply::Handled();
            })
        ];
}

#undef LOCTEXT_NAMESPACE
