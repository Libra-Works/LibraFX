#if WITH_DEV_AUTOMATION_TESTS

#include "LibraGpuEffectAsset.h"
#include "Misc/AutomationTest.h"
#include "Misc/Base64.h"

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

#endif
