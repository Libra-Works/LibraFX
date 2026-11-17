#include "Modules/ModuleManager.h"
#include "AssetToolsModule.h"
#include "LibraAssetTypeActions.h"
#include "LibraEditorPreviewSession.h"
#include "LibraEffectAsset.h"
#include "LibraSpawnableAsset.h"
#include "LibraSpawnableAssetDetails.h"
#include "PropertyEditorModule.h"

#define LOCTEXT_NAMESPACE "LibraRuntimeForUnrealEditor"

class FLibraRuntimeForUnrealEditorModule final : public IModuleInterface
{
public:
    void StartupModule() override
    {
        auto& assetTools = FModuleManager::LoadModuleChecked<FAssetToolsModule>("AssetTools").Get();
        auto category = assetTools.RegisterAdvancedAssetCategory("LibraFX",
            LOCTEXT("AssetCategory", "LIBERA FX"));
        assetActions.Add(MakeShared<FLibraAssetTypeActions>(ULibraEffectAsset::StaticClass(),
            LOCTEXT("EffectAssetType", "LIBERA FX Compiled Effect"), category));
        assetActions.Add(MakeShared<FLibraAssetTypeActions>(ULibraSpawnableAsset::StaticClass(),
            LOCTEXT("SpawnableAssetType", "LIBERA FX Spawnable Package"), category));
        for (const auto& actions : assetActions) assetTools.RegisterAssetTypeActions(actions);

        auto& propertyEditor = FModuleManager::LoadModuleChecked<FPropertyEditorModule>("PropertyEditor");
        propertyEditor.RegisterCustomClassLayout("LibraSpawnableAsset",
            FOnGetDetailCustomizationInstance::CreateStatic(&FLibraSpawnableAssetDetails::MakeInstance));
        propertyEditor.NotifyCustomizationModuleChanged();
        preview.Start();
    }
    void ShutdownModule() override
    {
        preview.Stop();
        if (auto* assetTools = FModuleManager::GetModulePtr<FAssetToolsModule>("AssetTools"))
            for (const auto& actions : assetActions) assetTools->Get().UnregisterAssetTypeActions(actions);
        assetActions.Reset();
        if (auto* propertyEditor = FModuleManager::GetModulePtr<FPropertyEditorModule>("PropertyEditor"))
        {
            propertyEditor->UnregisterCustomClassLayout("LibraSpawnableAsset");
            propertyEditor->NotifyCustomizationModuleChanged();
        }
    }
    bool SupportsDynamicReloading() override { return false; }
private:
    TArray<TSharedRef<IAssetTypeActions>> assetActions;
    FLibraEditorPreviewSession preview;
};

IMPLEMENT_MODULE(FLibraRuntimeForUnrealEditorModule, LibraRuntimeForUnrealEditor)

#undef LOCTEXT_NAMESPACE
