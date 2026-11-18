#if WITH_DEV_AUTOMATION_TESTS
#include "LibraQualityContext.h"
#include "Async/Async.h"
#include "Misc/AutomationTest.h"
#include "Scalability.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraQualitySnapshotTest, "Libra.Runtime.Quality.GameThreadSnapshot",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraQualitySnapshotTest::RunTest(const FString&)
{
    using liberafx::Result;
    auto snapshot = liberafx::HostQualitySnapshot{};
    auto tier = Scalability::GetQualityLevels().EffectsQuality;
    auto result = ReadLibraQualitySnapshot(73, snapshot);
    if (tier >= 0 && tier <= 4)
    {
        TestEqual(TEXT("Read engine effects quality"), result, Result::Ok);
        TestEqual(TEXT("Preserve frame boundary"), snapshot.frameSerial, uint64{73});
        TestEqual(TEXT("Keep engine provenance"), snapshot.source, liberafx::HostQualitySource::EngineEffects);
        TestEqual(TEXT("Read actual configured tier"), static_cast<int32>(snapshot.tier), tier);
        auto equal = false;
        TestEqual(TEXT("Worker-safe copied comparison"),
            liberafx::compareHostQuality(snapshot, liberafx::QualityComparison::Equal,
                static_cast<liberafx::HostQualityTier>(tier), equal), Result::Ok);
        TestTrue(TEXT("Copied tier compares equal"), equal);
    }
    else
    {
        TestEqual(TEXT("Custom tier is explicitly unknown"), result, Result::InvalidValueType);
        TestEqual(TEXT("No invented fallback"), snapshot.availability, liberafx::HostQualityAvailability::Unknown);
    }
    auto workerSnapshot = liberafx::HostQualitySnapshot{};
    auto worker = Async(EAsyncExecution::Thread, [&] { return ReadLibraQualitySnapshot(74, workerSnapshot); });
    TestEqual(TEXT("Do not read engine state on worker"), worker.Get(), Result::Busy);
    TestEqual(TEXT("Worker gets no partially published engine data"),
        workerSnapshot.availability, liberafx::HostQualityAvailability::Unprovided);
    return true;
}
#endif
