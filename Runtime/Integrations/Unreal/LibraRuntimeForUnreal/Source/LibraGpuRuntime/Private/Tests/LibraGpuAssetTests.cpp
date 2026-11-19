#if WITH_DEV_AUTOMATION_TESTS

#include "LibraGpuEffectAsset.h"
#include "Misc/AutomationTest.h"
#include "Misc/Base64.h"
#include "HAL/FileManager.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"

namespace
{
void SetWord(TArray<uint8>& bytes, int32 offset, uint32 value)
{
    for (auto i = 0; i < 4; ++i) bytes[offset + i] = uint8(value >> (i * 8));
}

FString Bundle(bool invalid)
{
    TArray<uint8> packet;
    packet.SetNumZeroed(192);
    FMemory::Memcpy(packet.GetData(), "LGPK", 4);
    SetWord(packet, 4, 1);
    SetWord(packet, 8, 64);
    SetWord(packet, 12, 2);
    SetWord(packet, 16, 1);
    SetWord(packet, 24, 80);
    SetWord(packet, 28, 48);
    SetWord(packet, 64, invalid ? 42 : 0);
    SetWord(packet, 72, MAX_uint32);
    SetWord(packet, 76, MAX_uint32);
    SetWord(packet, 80, MAX_uint32);
    SetWord(packet, 92, MAX_uint32);
    SetWord(packet, 96, 0x3f800000);
    SetWord(packet, 128, 18);
    SetWord(packet, 132, MAX_uint32);
    SetWord(packet, 140, MAX_uint32);
    SetWord(packet, 144, MAX_uint32);
    SetWord(packet, 152, 5);
    SetWord(packet, 156, MAX_uint32);
    return FString::Printf(TEXT("{\"version\":1,\"purpose\":\"SourceExport\",\"runtimeExecuted\":false,")
        TEXT("\"canonicalJson\":\"{}\",\"usf\":\"source only\",\"metadata\":{\"version\":1,")
        TEXT("\"capacity\":4,\"particleStride\":80,\"uniformBufferBytes\":48,")
        TEXT("\"contentHash\":\"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a\",")
        TEXT("\"sourceHash\":\"cc8bac8c92ab27cddfc0cbf92f17d23b43f2cae747744de80f25fe546a24ff5e\",")
        TEXT("\"purpose\":\"SourceExport\",\"runtimeExecuted\":false,")
        TEXT("\"format\":\"Usf\",\"resources\":[],\"flow\":[{\"kind\":\"Spawn\",\"kernelId\":\"spawn\"}]},")
        TEXT("\"kernels\":[{\"id\":\"spawn\",\"stage\":\"Spawn\",\"packet\":\"%s\"}],\"textures\":[]}"),
        *FBase64::Encode(packet));
}

FString BillboardBundle()
{
    auto bundle = Bundle(false).Replace(TEXT("\"resources\":[]"),
        TEXT("\"resources\":[{\"id\":\"billboardTexture\",\"kind\":\"Texture2D\",\"register\":0}]"));
    bundle = bundle.Replace(TEXT("\"textures\":[]"),
        TEXT("\"textures\":[{\"id\":\"billboardTexture\",\"objectPath\":\"builtin:white\"}]"));
    return bundle.Replace(TEXT("\"flow\":[{\"kind\":\"Spawn\",\"kernelId\":\"spawn\"}]"),
        TEXT("\"flow\":[{\"kind\":\"Spawn\",\"kernelId\":\"spawn\"},")
        TEXT("{\"id\":\"draw\",\"kind\":\"RenderBillboard\",\"resourceId\":\"billboardTexture\"}]"));
}

FString RendererBundle()
{
    return BillboardBundle().LeftChop(1) +
        TEXT(",\"rendererBindingsVersion\":1,\"rendererBindings\":[{\"flowId\":\"draw\",")
        TEXT("\"renderer\":\"builtin:billboard\",\"material\":\"builtin:unlit-additive\",\"sizeScale\":100}]}");
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGpuAssetImportTest,
    "Libra.GPU.Assets.TransactionalPacketValidation",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGpuAssetImportTest::RunTest(const FString& parameters)
{
    auto asset = NewObject<ULibraGpuEffectAsset>();
    FString error;
    TestTrue(TEXT("Valid bounded packet imports"), asset->ImportBundle(Bundle(false), error));
    TestEqual(TEXT("Capacity"), asset->GetCapacity(), 4);
    TestTrue(TEXT("Imported program validates"), asset->ValidateProgram(error));
    auto original = asset->GetKernels()[0].Bytes;
    TestFalse(TEXT("Unknown opcode is rejected"), asset->ImportBundle(Bundle(true), error));
    TestTrue(TEXT("Rejected reimport retains old packet"), asset->GetKernels()[0].Bytes == original);
    TestTrue(TEXT("Old program remains valid"), asset->ValidateProgram(error));
    auto memoryAsset = ULibraGpuEffectAsset::CreateInMemory(GetTransientPackage(), Bundle(false), error);
    TestNotNull(TEXT("Validated frontend bundle can create an in-memory asset without editor factory"), memoryAsset);
    TestNull(TEXT("Invalid in-memory replacement produces no playable asset"),
        ULibraGpuEffectAsset::CreateInMemory(GetTransientPackage(), Bundle(true), error));
    auto missingBinding = Bundle(false).Replace(TEXT("\"resources\":[]"),
        TEXT("\"resources\":[{\"id\":\"billboardTexture\",\"kind\":\"Texture2D\"}]"));
    TestFalse(TEXT("Missing declared texture binding is rejected"), asset->ImportBundle(missingBinding, error));
    TestTrue(TEXT("Missing binding diagnostic identifies the resource and required mapping"),
        error.Contains(TEXT("Missing Texture2D binding")) && error.Contains(TEXT("billboardTexture")) &&
        error.Contains(TEXT("resourceAssetPaths")));
    TestTrue(TEXT("Missing binding rejection preserves the working program"), asset->GetKernels()[0].Bytes == original);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGpuBuiltinBindingTest,
    "Libra.GPU.Assets.ExplicitBuiltinRendererBindings",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGpuBuiltinBindingTest::RunTest(const FString& parameters)
{
    FString error;
    auto asset = ULibraGpuEffectAsset::CreateInMemory(GetTransientPackage(), RendererBundle(), error);
    if (!TestNotNull(TEXT("Explicit white/additive renderer creates a playable native asset"), asset))
    {
        AddError(error);
        return false;
    }
    TestTrue(TEXT("Builtin binding validates without UObject texture"), asset->ValidateProgram(error));
    TestTrue(TEXT("White is an explicit serialized binding"), asset->GetTextures()[0].bBuiltinWhite);
    TestNull(TEXT("Builtin texture does not pretend to be an imported UTexture2D"), asset->GetTextures()[0].Texture.Get());
    TestEqual(TEXT("Additive selects a real blend mode"), asset->GetFlow().Last().BillboardBlend, ELibraGpuBillboardBlend::Additive);
    TestEqual(TEXT("Draw-size multiplier retained"), asset->GetFlow().Last().SizeScale, 100.f);
    auto deliveryJson = RendererBundle().LeftChop(1) + TEXT(",\"programSequence\":1,\"nodeId\":\"GPUStarter\"}");
    deliveryJson = deliveryJson.Replace(TEXT("\"usf\":"), TEXT("\"shaderSource\":"))
        .Replace(TEXT("\"kernels\":"), TEXT("\"registerKernels\":"))
        .Replace(TEXT("\"packet\":"), TEXT("\"base64\":"))
        .Replace(TEXT("\"textures\":[{\"id\":\"billboardTexture\",\"objectPath\":\"builtin:white\"}]"),
            TEXT("\"resourceAssetPaths\":{\"billboardTexture\":\"builtin:white\"}"));
    auto deliveryAsset = ULibraGpuEffectAsset::CreateInMemory(GetTransientPackage(), deliveryJson, error);
    if (TestNotNull(TEXT("Exact frontend delivery field names accept white and versioned renderer bindings"), deliveryAsset))
        TestEqual(TEXT("Frontend renderer settings are not dropped"), deliveryAsset->GetFlow().Last().SizeScale, 100.f);
    auto reject = [this, asset, &error](const FString& bundle, const TCHAR* diagnostic)
    {
        TestFalse(TEXT("Invalid binding is rejected atomically"), asset->ImportBundle(bundle, error));
        TestTrue(TEXT("Actionable binding diagnostic"), error.Contains(diagnostic));
        TestTrue(TEXT("Working builtin asset survives rejection"), asset->ValidateProgram(error));
        TestEqual(TEXT("Working draw configuration survives rejection"), asset->GetFlow().Last().SizeScale, 100.f);
    };
    reject(RendererBundle().Replace(TEXT("builtin:white"), TEXT("builtin:unknown")), TEXT("Unknown builtin GPU texture"));
    reject(RendererBundle().Replace(TEXT("builtin:unlit-additive"), TEXT("/Game/FX/M_Test.M_Test")), TEXT("native UE materials are not evaluated"));
    reject(RendererBundle().Replace(TEXT("builtin:billboard"), TEXT("builtin:mesh")), TEXT("Unsupported GPU renderer"));
    reject(RendererBundle().Replace(TEXT("\"flowId\":\"draw\""), TEXT("\"flowId\":\"missing\"")), TEXT("matching rendererBindings"));
    reject(RendererBundle().Replace(TEXT("\"sizeScale\":100"), TEXT("\"sizeScale\":0")), TEXT("sizeScale"));
    reject(RendererBundle().Replace(TEXT("\"sizeScale\":100"), TEXT("\"sizeScale\":1e-100")), TEXT("sizeScale"));
    reject(RendererBundle().Replace(TEXT("\"sizeScale\":100"), TEXT("\"sizeScale\":10001")), TEXT("sizeScale"));
    reject(RendererBundle().Replace(TEXT("\"rendererBindingsVersion\":1"), TEXT("\"rendererBindingsVersion\":2")), TEXT("rendererBindingsVersion"));
    auto legacy = ULibraGpuEffectAsset::CreateInMemory(GetTransientPackage(), BillboardBundle(), error);
    if (TestNotNull(TEXT("Legacy bundle remains compatible with explicit white texture"), legacy))
    {
        TestEqual(TEXT("Absent renderer extension retains legacy alpha"), legacy->GetFlow().Last().BillboardBlend, ELibraGpuBillboardBlend::Alpha);
        TestEqual(TEXT("Legacy world draw size is unchanged"), legacy->GetFlow().Last().SizeScale, 1.f);
    }
    return true;
}

#if WITH_EDITOR
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGpuWriteBuiltinPackageTest,
    "Libra.GPU.Assets.SaveExplicitBuiltinPackage",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGpuWriteBuiltinPackageTest::RunTest(const FString& parameters)
{
    if (!FParse::Param(FCommandLine::Get(), TEXT("LibraGpuWriteBindingFixture")))
    {
        AddInfo(TEXT("Package fixture write not selected; use -LibraGpuWriteBindingFixture only in an isolated test project."));
        return true;
    }
    auto packageName = FString(TEXT("/Game/LibraGpuBindingValidation/FX_ExplicitWhite"));
    auto filename = FPackageName::LongPackageNameToFilename(packageName, FPackageName::GetAssetPackageExtension());
    if (IFileManager::Get().FileExists(*filename))
    {
        AddError(TEXT("Refusing to overwrite an existing GPU package fixture."));
        return false;
    }
    auto plugin = IPluginManager::Get().FindPlugin(TEXT("LibraRuntimeForUnreal"));
    FString json, error;
    if (!plugin || !FFileHelper::LoadFileToString(json, *FPaths::Combine(plugin->GetBaseDir(),
        TEXT("Source/LibraGpuRuntime/Private/Tests/Fixtures/white-emitter.lfxgpu"))))
    {
        AddError(TEXT("Canonical default emitter fixture is missing."));
        return false;
    }
    auto package = CreatePackage(*packageName);
    auto asset = NewObject<ULibraGpuEffectAsset>(package, TEXT("FX_ExplicitWhite"), RF_Public | RF_Standalone);
    if (!asset->ImportBundle(json, error)) { AddError(error); return false; }
    FSavePackageArgs args;
    args.TopLevelFlags = RF_Public | RF_Standalone;
    args.SaveFlags = SAVE_NoError;
    if (!UPackage::SavePackage(package, asset, *filename, args))
    {
        AddError(TEXT("Native explicit builtin package could not be saved."));
        return false;
    }
    AddInfo(TEXT("Saved native builtin white/additive/sizeScale100 fixture; fresh-process reload/cook are separate proofs."));
    return true;
}
#endif

#endif
