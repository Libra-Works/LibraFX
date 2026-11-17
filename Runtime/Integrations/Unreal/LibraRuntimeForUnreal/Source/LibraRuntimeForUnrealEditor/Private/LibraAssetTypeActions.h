#pragma once

#include "AssetTypeActions_Base.h"

class FLibraAssetTypeActions final : public FAssetTypeActions_Base
{
public:
    FLibraAssetTypeActions(UClass* assetClass, FText displayName, EAssetTypeCategories::Type category)
        : AssetClass(assetClass), DisplayName(MoveTemp(displayName)), Category(category)
    {
    }

    virtual FText GetName() const override { return DisplayName; }
    virtual FColor GetTypeColor() const override { return FColor(205, 45, 115); }
    virtual UClass* GetSupportedClass() const override { return AssetClass; }
    virtual uint32 GetCategories() override { return Category; }

private:
    UClass* AssetClass;
    FText DisplayName;
    EAssetTypeCategories::Type Category;
};
