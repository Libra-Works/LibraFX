#include "LibraSpawnableAssetDetails.h"

#include "DetailCategoryBuilder.h"
#include "DetailLayoutBuilder.h"
#include "DetailWidgetRow.h"
#include "EditorReimportHandler.h"
#include "LibraSpawnableAsset.h"
#include "Widgets/Input/SButton.h"

#define LOCTEXT_NAMESPACE "LibraSpawnableAssetDetails"

auto FLibraSpawnableAssetDetails::MakeInstance() -> TSharedRef<IDetailCustomization>
{
    return MakeShared<FLibraSpawnableAssetDetails>();
}

void FLibraSpawnableAssetDetails::CustomizeDetails(IDetailLayoutBuilder& detailBuilder)
{
    auto objects = TArray<TWeakObjectPtr<UObject>>{};
    detailBuilder.GetObjectsBeingCustomized(objects);
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
            .OnClicked_Lambda([objects] {
                for (const auto& object : objects)
                    if (auto* asset = Cast<ULibraSpawnableAsset>(object.Get()))
                        FReimportManager::Instance()->Reimport(asset, true, true);
                return FReply::Handled();
            })
        ];
}

#undef LOCTEXT_NAMESPACE
