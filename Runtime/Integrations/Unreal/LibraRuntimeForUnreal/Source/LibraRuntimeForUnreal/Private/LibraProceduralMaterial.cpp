#include "LibraProceduralMaterial.h"
#include "Materials/Material.h"
#include "UObject/StrongObjectPtr.h"
#include "Engine/Texture2D.h"
#if WITH_EDITOR
#include "Materials/MaterialExpressionTextureSampleParameter2D.h"
#include "Materials/MaterialExpressionVectorParameter.h"
#include "Materials/MaterialExpressionScalarParameter.h"
#include "Materials/MaterialExpressionVertexColor.h"
#include "Materials/MaterialExpressionMultiply.h"
#include "Materials/MaterialExpressionComponentMask.h"
#include "Engine/Texture2D.h"
#include "Misc/PackageName.h"
#include "UObject/SavePackage.h"
#include "HAL/FileManager.h"
#include "Misc/Paths.h"
#endif

namespace LibraProceduralMaterial
{
UTexture2D* WhiteTexture()
{
    static TStrongObjectPtr<UTexture2D> white;
    if (!white.IsValid())
    {
        auto* texture = UTexture2D::CreateTransient(1, 1, PF_B8G8R8A8);
        if (!texture)
        {
            return nullptr;
        }
        texture->SRGB = false;
        auto& mip = texture->GetPlatformData()->Mips[0];
        auto* pixel = static_cast<FColor*>(mip.BulkData.Lock(LOCK_READ_WRITE));
        *pixel = FColor::White;
        mip.BulkData.Unlock();
        texture->UpdateResource();
        white.Reset(texture);
    }
    return white.Get();
}
UMaterialInterface* Resolve(bool additive, bool doubleSided, FString& error)
{
    static TStrongObjectPtr<UMaterialInterface> materials[4];
    const auto index = (additive ? 2 : 0) + (doubleSided ? 0 : 1);
    if (materials[index].IsValid())
    {
        return materials[index].Get();
    }
    const auto name = FString::Printf(TEXT("M_Procedural_%s_%s"), additive ? TEXT("Additive") : TEXT("Alpha"),
        doubleSided ? TEXT("Double") : TEXT("Single"));
    const auto packageName = TEXT("/LibraRuntimeForUnreal/Materials/") + name;
    auto* material = LoadObject<UMaterial>(nullptr, *(packageName + TEXT(".") + name), nullptr, LOAD_NoWarn);
#if WITH_EDITOR
    if (!material)
    {
        auto* package = CreatePackage(*packageName);
        material = NewObject<UMaterial>(package, *name, RF_Public | RF_Standalone);
        material->MaterialDomain = MD_Surface; material->BlendMode = additive ? BLEND_Additive : BLEND_Translucent;
        material->SetShadingModel(MSM_Unlit); material->TwoSided = doubleSided;
        // Portable effect output must also render in previews with post processing disabled.
        material->TranslucencyPass = MTP_BeforeDOF;
        auto& expressions = material->GetExpressionCollection();
        auto* texture = NewObject<UMaterialExpressionTextureSampleParameter2D>(material);
        texture->ParameterName = TEXT("Texture");
        texture->Texture = LoadObject<UTexture2D>(nullptr, TEXT("/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture"));
        texture->AutoSetSampleType(); expressions.AddExpression(texture);
        auto* tint = NewObject<UMaterialExpressionVectorParameter>(material); tint->ParameterName = TEXT("Tint");
        tint->DefaultValue = FLinearColor::White; expressions.AddExpression(tint);
        auto* emission = NewObject<UMaterialExpressionScalarParameter>(material); emission->ParameterName = TEXT("Emission");
        emission->DefaultValue = 1; expressions.AddExpression(emission);
        auto* color = NewObject<UMaterialExpressionVertexColor>(material); expressions.AddExpression(color);
        const auto multiply = [&](UMaterialExpression* a, int32 aOutput, UMaterialExpression* b, int32 bOutput)
        {
            auto* node = NewObject<UMaterialExpressionMultiply>(material);
            node->A.Connect(aOutput, a); node->B.Connect(bOutput, b); expressions.AddExpression(node); return node;
        };
        auto* rgb = multiply(multiply(multiply(texture, 0, color, 0), 0, tint, 0), 0, emission, 0);
        auto* alpha = multiply(multiply(texture, 4, color, 4), 0, tint, 4);
        material->GetEditorOnlyData()->EmissiveColor.Connect(0, rgb);
        material->GetEditorOnlyData()->Opacity.Connect(0, alpha);
        material->PostEditChange();
        // Populate the expression metadata and matching shader map before saving
        // a newly authored master. Loaded/cooked masters reuse that cached data.
        material->ForceRecompileForRendering(EMaterialShaderPrecompileMode::Synchronous);
        package->MarkPackageDirty();
        auto arguments = FSavePackageArgs{}; arguments.TopLevelFlags = RF_Public | RF_Standalone;
        arguments.SaveFlags = SAVE_NoError;
        const auto filename = FPackageName::LongPackageNameToFilename(packageName, FPackageName::GetAssetPackageExtension());
        IFileManager::Get().MakeDirectory(*FPaths::GetPath(filename), true);
        if (!UPackage::SavePackage(package, material, *filename, arguments))
        {
            error = TEXT("Cannot save shipped procedural material master: ") + filename;
            return nullptr;
        }
    }
#endif
    if (!material)
    {
        error = TEXT("The shipped procedural material master was not cooked: ") + packageName;
        return nullptr;
    }
#if WITH_EDITOR
    // Initialize expression metadata and the current platform shader map once
    // per editor process. This reuses DDC; effects and frames reuse the master.
    material->ForceRecompileForRendering(EMaterialShaderPrecompileMode::Synchronous);
#endif
    materials[index].Reset(material);
    return material;
}
}
