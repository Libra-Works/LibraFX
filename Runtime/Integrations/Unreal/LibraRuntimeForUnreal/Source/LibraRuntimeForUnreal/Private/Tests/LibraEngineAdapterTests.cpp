#if WITH_DEV_AUTOMATION_TESTS
#include "LibraTaskScheduler.h"
#include "LibraRenderDispatcher.h"
#include "LibraCpuProfiler.h"
#include "Misc/AutomationTest.h"
#include "HAL/Event.h"
#include "HAL/PlatformProcess.h"
#include "RenderingThread.h"
#include "liberafx/SceneBindings.h"
#include <array>
#include <limits>

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraEngineTaskTest, "Libra.Runtime.Adapters.EngineTasks",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraEngineTaskTest::RunTest(const FString&)
{
    struct FJobState { FEvent* gate; int32 result = 0; };
    auto state = FJobState{FPlatformProcess::GetSynchEventFromPool(true)};
    auto scheduler = FLibraTaskScheduler{};
    auto adapter = scheduler.GetScheduler();
    auto job = +[](void* raw) noexcept
    {
        auto& jobState = *static_cast<FJobState*>(raw);
        jobState.gate->Wait();
        jobState.result = 7;
    };
    TestTrue(TEXT("UE accepts a native Core job"), adapter.enqueue(adapter.context, job, &state));
    TestFalse(TEXT("Running task object cannot be overwritten"), adapter.enqueue(adapter.context, job, &state));
    state.gate->Trigger();
    scheduler.Wait();
    TestEqual(TEXT("UE worker completed the native job"), state.result, 7);
    TestTrue(TEXT("Engine completion is visible"), scheduler.IsCompleted());
    FPlatformProcess::ReturnSynchEventToPool(state.gate);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraEngineRenderTest, "Libra.Runtime.Adapters.RenderThreadSnapshot",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraEngineRenderTest::RunTest(const FString&)
{
    class FBackend final : public ILibraRenderBackend
    {
    public:
        bool onRenderThread = false;
        float position = 0;
        bool Submit(FRHICommandListImmediate&, liberafx::RenderBatch batch) override
        {
            onRenderThread = IsInRenderingThread();
            position = batch.commands[0].vertices[0].position[0];
            return true;
        }
    };
    auto backend = MakeShared<FBackend, ESPMode::ThreadSafe>();
    auto storage = std::array<liberafx::DrawCommand, 1>{};
    auto vertices = std::array<liberafx::RenderVertex, 2>{};
    vertices[0].position[0] = 3;
    auto queue = liberafx::RenderQueue{};
    queue.initialize(storage.data(), storage.size());
    queue.begin();
    auto command = liberafx::DrawCommand{};
    command.primitive = liberafx::Primitive::Lines;
    command.material.value = 1;
    command.vertices = vertices.data();
    command.vertexCount = vertices.size();
    queue.append(command);
    queue.seal();
    auto result = TSharedPtr<FLibraRenderSubmission, ESPMode::ThreadSafe>{};
    TestEqual(TEXT("Oversized snapshot is rejected before releasing buffers"),
        FLibraRenderDispatcher::Submit(queue, backend, result, 1), liberafx::RenderResult::CapacityExceeded);
    auto gate = FPlatformProcess::GetSynchEventFromPool(true);
    if (GIsThreadedRendering)
    {
        ENQUEUE_RENDER_COMMAND(LibraTestRenderGate)([gate](FRHICommandListImmediate&) { gate->Wait(); });
    }
    TestEqual(TEXT("Engine takes an owned snapshot"),
        FLibraRenderDispatcher::Submit(queue, backend, result), liberafx::RenderResult::Ok);
    vertices[0].position[0] = 99;
    gate->Trigger();
    FlushRenderingCommands();
    FPlatformProcess::ReturnSynchEventToPool(gate);
    if (!result) return false;
    TestEqual(TEXT("Snapshot submission completes"), result->GetResult(), liberafx::RenderResult::Ok);
    TestTrue(TEXT("Backend is called on engine render thread"), backend->onRenderThread);
    TestEqual(TEXT("Borrowed vertex storage is not retained"), backend->position, 3.0f);
    TestEqual(TEXT("Core storage is reusable"), queue.begin(), liberafx::RenderResult::Ok);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraIndependentProfilerTest, "Libra.Runtime.Profiling.IndependentRecording",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraIndependentProfilerTest::RunTest(const FString&)
{
    auto profiler = FLibraCpuProfiler{};
    auto bytes = std::array<uint8, 12>{};
    bytes[4] = 1;
    profiler.Initialize(TConstArrayView<uint8>(bytes.data(), bytes.size()), 0, false, true);
    {
        auto scope = liberafx::ProfileScope(profiler.GetHooks(), liberafx::ProfileEvent::Evaluate);
    }
    auto values = TArray<FLibraProfileMeasurement>{};
    auto dropped = uint64{0};
    TestTrue(TEXT("Native measurements do not require Insights"), profiler.Snapshot(values, dropped));
    TestEqual(TEXT("One completed scope is recorded"), values.Num(), 1);
    TestEqual(TEXT("Exact call count"), values[0].Calls, uint64{1});
    TestTrue(TEXT("Exclusive time is bounded by inclusive time"), values[0].ExclusiveNs <= values[0].InclusiveNs);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraSpriteAtlasBindingTest, "Libra.Runtime.Scene.SpriteAtlasSampling",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraSpriteAtlasBindingTest::RunTest(const FString&)
{
    auto frame = liberafx::AtlasFrame{};
    TestTrue(TEXT("Negative frame wraps around the atlas"),
        liberafx::sampleSpriteAtlas(-1.0, 4, 2, frame));
    TestEqual(TEXT("Frame index wraps to the final cell"), frame.frameIndex, uint32{7});
    TestEqual(TEXT("Atlas frame starts at top-right cell U"), frame.uvRect[0], 0.75f);
    TestEqual(TEXT("Atlas frame starts in the second row"), frame.uvRect[1], 0.5f);
    TestEqual(TEXT("Atlas frame width is normalized"), frame.uvRect[2], 0.25f);
    TestEqual(TEXT("Atlas frame height is normalized"), frame.uvRect[3], 0.5f);
    TestFalse(TEXT("Zero-width atlases are rejected"), liberafx::sampleSpriteAtlas(0.0, 0, 2, frame));
    TestFalse(TEXT("Non-finite frames are rejected"),
        liberafx::sampleSpriteAtlas(std::numeric_limits<double>::infinity(), 2, 2, frame));
    return true;
}
#endif
