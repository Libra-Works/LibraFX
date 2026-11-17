#pragma once

#include "IDetailCustomization.h"

class FLibraSpawnableAssetDetails final : public IDetailCustomization
{
public:
    static TSharedRef<IDetailCustomization> MakeInstance();
    virtual void CustomizeDetails(IDetailLayoutBuilder& detailBuilder) override;
};
