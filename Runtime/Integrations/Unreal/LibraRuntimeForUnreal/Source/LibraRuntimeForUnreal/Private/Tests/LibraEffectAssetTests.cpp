#if WITH_DEV_AUTOMATION_TESTS

#include "LibraEffectAsset.h"
#include "LibraEffectComponent.h"
#include "LibraNativeState.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"

namespace {
auto ScalarBytecode(double value) -> TArray<uint8>
{
    TArray<uint8> bytes;
    bytes.SetNumZeroed(56);
    FMemory::Memcpy(bytes.GetData(), "LFXB", 4);
    bytes[4] = 1;
    bytes[6] = 1;
    bytes[8] = 1;
    bytes[27] = 1;
    bytes[28] = 1;
    for (auto index = 32; index < 40; ++index) bytes[index] = 255;
    auto bits = uint64{};
    FMemory::Memcpy(&bits, &value, sizeof(value));
    for (auto index = 0; index < 8; ++index) bytes[40 + index] = static_cast<uint8>(bits >> (index * 8));
    return bytes;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraAssetBytecodeTest,
    "Libra.Runtime.Assets.ImmutableBytecode", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraAssetBytecodeTest::RunTest(const FString& parameters)
{
    auto bytes = ScalarBytecode(3);
    auto asset = NewObject<ULibraEffectAsset>();
    TestEqual(TEXT("Valid bytecode imports"), asset->SetBytecode(bytes), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Node count"), asset->GetNodeCount(), 1);
    auto original = TArray<uint8>(asset->GetBytecode());
    bytes[40] ^= 1;
    TestTrue(TEXT("Input buffers are not borrowed"), asset->GetBytecode()[40] == original[40]);
    bytes[0] = 0;
    TestEqual(TEXT("Malformed replacement is rejected"), asset->SetBytecode(bytes), ELibraRuntimeResult::InvalidBinary);
    TestTrue(TEXT("Failed replacement preserves the previous asset"),
        FMemory::Memcmp(asset->GetBytecode().GetData(), original.GetData(), original.Num()) == 0);
    TestEqual(TEXT("Previous asset stays valid"), asset->ValidateBytecode(), ELibraRuntimeResult::Ok);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGroupSignalFixtureTest,
    "Libra.Runtime.Binding.GroupSignalFixture",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGroupSignalFixtureTest::RunTest(const FString&)
{
    auto directory = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraGroupSignalFixture="), directory) || directory.IsEmpty())
    {
        AddInfo(TEXT("GroupSignalFixture requires an explicit isolated fixture directory."));
        return true;
    }
    auto bytes = TArray<uint8>{};
    auto entryText = FString{};
    if (!TestTrue(TEXT("Fixture bytecode and entry ID load"),
        FFileHelper::LoadFileToArray(bytes, *(directory / TEXT("signal-program.lfxb"))) &&
        FFileHelper::LoadFileToString(entryText, *(directory / TEXT("signal-entry.txt"))))) return false;
    auto entry = FGuid{};
    if (!TestTrue(TEXT("Fixture entry ID is valid"), FGuid::Parse(entryText, entry)) ||
        !TestTrue(TEXT("Fixture includes v4 FCurve bytecode"), bytes.Num() >= 16 && bytes[4] == 4)) return false;
    auto asset = NewObject<ULibraEffectAsset>();
    if (!TestEqual(TEXT("Unreal effect asset accepts Group Signal with FCurve"),
        asset->SetBytecode(bytes), ELibraRuntimeResult::Ok)) return false;
    auto component = NewObject<ULibraEffectComponent>();
    auto handle = FLibraSignalHandle{};
    TestEqual(TEXT("Unreal effect component accepts the compiled asset"),
        component->SetEffect(asset), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Unreal effect component binds the authored Signal"),
        component->BindSignal(entry, handle), ELibraRuntimeResult::Ok);
    component->Stop();
    auto compiled = FCompiledLibraProgram(bytes);
    if (!TestTrue(TEXT("Unreal registry resolves the builtin Signal nodes"), compiled.result == liberafx::Result::Ok))
        return false;
    auto workBytes = compiled.program.workBytes();
    auto work = static_cast<double*>(FMemory::Malloc(workBytes, compiled.program.workAlignment()));
    if (!TestNotNull(TEXT("Caller-owned work memory"), work)) return false;
    auto instance = liberafx::Instance{};
    auto initialized = instance.initialize(compiled.program, work, workBytes / sizeof(double));
    if (TestTrue(TEXT("Fixture instance initializes"), initialized == liberafx::Result::Ok))
    {
        auto enqueue = [](void*, liberafx::Job job, void* context) noexcept -> bool { job(context); return true; };
        auto evaluated = instance.updateAsync(0, enqueue, nullptr);
        TestTrue(TEXT("Fixture evaluates"), evaluated == liberafx::Result::Ok && instance.result() == liberafx::Result::Ok);
        uint8 entryBytes[16]{};
        LibraNodeIdBytes(entry, entryBytes);
        TestTrue(TEXT("Three instructions cannot traverse all four Signal nodes"),
            instance.dispatchSignal(entryBytes, 0, 3) == liberafx::Result::InstructionBudgetExceeded);
        TestTrue(TEXT("Four instructions traverse the explicit Group Signal chain"),
            instance.dispatchSignal(entryBytes, 0, 4) == liberafx::Result::Ok);
    }
    FMemory::Free(work);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraNodeIdentityTest,
    "Libra.Runtime.Binding.NodeIdentity", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraNodeIdentityTest::RunTest(const FString& parameters)
{
    uint8 bytes[16]{};
    LibraNodeIdBytes(FGuid(0x00112233, 0x44556677, 0x8899AABB, 0xCCDDEEFF), bytes);
    for (auto index = 0; index < 16; ++index)
        TestEqual(TEXT("Node IDs match compiler network-byte-order GUIDs"), bytes[index], static_cast<uint8>(index * 0x11));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraBoundParameterTest,
    "Libra.Runtime.Binding.InstanceIsolationAndLifetime",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraBoundParameterTest::RunTest(const FString& parameters)
{
    auto asset = NewObject<ULibraEffectAsset>();
    TestEqual(TEXT("Fixture imports"), asset->SetBytecode(ScalarBytecode(3)), ELibraRuntimeResult::Ok);
    auto first = NewObject<ULibraEffectComponent>();
    auto second = NewObject<ULibraEffectComponent>();
    TestEqual(TEXT("First instance"), first->SetEffect(asset), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Second instance"), second->SetEffect(asset), ELibraRuntimeResult::Ok);
    auto firstHandle = FLibraParameterHandle{};
    auto secondHandle = FLibraParameterHandle{};
    auto nodeId = FGuid(0, 0, 0, 1);
    TestEqual(TEXT("Bind first"), first->BindParameter(nodeId, firstHandle), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Bind second"), second->BindParameter(nodeId, secondHandle), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Override first"), first->SetBoundParameter(firstHandle, 9), ELibraRuntimeResult::Ok);
    auto value = double{};
    TestEqual(TEXT("Read second"), second->ReadBoundParameter(secondHandle, value), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Defaults are not shared"), value, 3.0);
    TestEqual(TEXT("Reject a different component's handle"),
        second->SetBoundParameter(firstHandle, 2), ELibraRuntimeResult::InvalidHandle);
    first->Stop();
    TestEqual(TEXT("Recreate the same program"), first->SetEffect(asset), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Recreated instances reject old handles"),
        first->ReadBoundParameter(firstHandle, value), ELibraRuntimeResult::InvalidHandle);
    first->Stop();
    second->Stop();
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraTransactionalEffectTest,
    "Libra.Runtime.Assets.RejectedProgramPreservesInstance",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraTransactionalEffectTest::RunTest(const FString& parameters)
{
    auto original = NewObject<ULibraEffectAsset>();
    auto replacement = NewObject<ULibraEffectAsset>();
    TestEqual(TEXT("Original imports"), original->SetBytecode(ScalarBytecode(3)), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Replacement imports"), replacement->SetBytecode(ScalarBytecode(5)), ELibraRuntimeResult::Ok);
    auto component = NewObject<ULibraEffectComponent>();
    TestEqual(TEXT("Original instance"), component->SetEffect(original), ELibraRuntimeResult::Ok);
    auto handle = FLibraParameterHandle{};
    TestEqual(TEXT("Bind original"), component->BindParameter(FGuid(0, 0, 0, 1), handle), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Original override"), component->SetBoundParameter(handle, 7), ELibraRuntimeResult::Ok);
    component->MaximumWorkingBytes = 1;
    TestEqual(TEXT("Replacement exceeds budget"), component->SetEffect(replacement), ELibraRuntimeResult::InsufficientWork);
    TestTrue(TEXT("Original asset remains selected"), component->Effect == original);
    auto value = double{};
    TestEqual(TEXT("Original binding remains usable"), component->ReadBoundParameter(handle, value), ELibraRuntimeResult::Ok);
    TestEqual(TEXT("Original overrides remain intact"), value, 7.0);
    component->Stop();
    return true;
}

#endif
