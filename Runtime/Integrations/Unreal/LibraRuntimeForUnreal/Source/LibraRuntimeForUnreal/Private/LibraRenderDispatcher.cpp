#include "LibraRenderDispatcher.h"
#include "RenderingThread.h"
#include "RHICommandList.h"
#include "ProfilingDebugging/CpuProfilerTrace.h"

namespace {
struct FRenderSnapshot final
{
    TArray<liberafx::DrawCommand> commands;
    TArray<liberafx::RenderVertex> vertices;
    TArray<uint32> indices;
};
}

auto FLibraRenderDispatcher::Submit(liberafx::RenderQueue& queue,
    TSharedRef<ILibraRenderBackend, ESPMode::ThreadSafe> backend,
    TSharedPtr<FLibraRenderSubmission, ESPMode::ThreadSafe>& submission,
    uint64 maximumSnapshotBytes) -> liberafx::RenderResult
{
    if (!IsInGameThread() || maximumSnapshotBytes == 0) return liberafx::RenderResult::InvalidArgument;
    struct FSubmitContext
    {
        TSharedRef<ILibraRenderBackend, ESPMode::ThreadSafe> backend;
        TSharedPtr<FLibraRenderSubmission, ESPMode::ThreadSafe> submission;
        uint64 maximumBytes;
        liberafx::RenderResult failure = liberafx::RenderResult::CapacityExceeded;
    };
    auto context = FSubmitContext{backend, nullptr, maximumSnapshotBytes};
    auto status = queue.submit([](void* raw, liberafx::RenderBatch batch) noexcept -> bool
    {
        TRACE_CPUPROFILER_EVENT_SCOPE(Libra_RenderSnapshot);
        auto& context = *static_cast<FSubmitContext*>(raw);
        if (batch.count > MAX_int32 || batch.count > context.maximumBytes / sizeof(liberafx::DrawCommand))
            return false;
        auto bytes = uint64{batch.count} * sizeof(liberafx::DrawCommand);
        auto vertexCount = uint64{0};
        auto indexCount = uint64{0};
        for (auto index = size_t{0}; index < batch.count; ++index)
        {
            const auto& command = batch.commands[index];
            if (command.vertexCount > (context.maximumBytes - bytes) / sizeof(liberafx::RenderVertex)) return false;
            bytes += command.vertexCount * sizeof(liberafx::RenderVertex);
            if (command.indexCount > (context.maximumBytes - bytes) / sizeof(uint32)) return false;
            bytes += command.indexCount * sizeof(uint32);
            vertexCount += command.vertexCount;
            indexCount += command.indexCount;
            if (vertexCount > MAX_int32 || indexCount > MAX_int32) return false;
        }
        auto snapshot = MakeShared<FRenderSnapshot, ESPMode::ThreadSafe>();
        snapshot->commands.Append(batch.commands, static_cast<int32>(batch.count));
        snapshot->vertices.SetNumUninitialized(static_cast<int32>(vertexCount));
        snapshot->indices.SetNumUninitialized(static_cast<int32>(indexCount));
        auto vertexOffset = int32{0};
        auto indexOffset = int32{0};
        for (auto& command : snapshot->commands)
        {
            if (command.vertexCount != 0)
            {
                FMemory::Memcpy(snapshot->vertices.GetData() + vertexOffset, command.vertices,
                    command.vertexCount * sizeof(liberafx::RenderVertex));
                command.vertices = snapshot->vertices.GetData() + vertexOffset;
                vertexOffset += static_cast<int32>(command.vertexCount);
            }
            else command.vertices = nullptr;
            if (command.indexCount != 0)
            {
                FMemory::Memcpy(snapshot->indices.GetData() + indexOffset, command.indices,
                    command.indexCount * sizeof(uint32));
                command.indices = snapshot->indices.GetData() + indexOffset;
                indexOffset += static_cast<int32>(command.indexCount);
            }
            else command.indices = nullptr;
        }
        context.submission = MakeShared<FLibraRenderSubmission, ESPMode::ThreadSafe>();
        auto result = context.submission.ToSharedRef();
        auto backend = context.backend;
        ENQUEUE_RENDER_COMMAND(LibraSubmitBatch)([snapshot, result, backend](FRHICommandListImmediate& commands)
        {
            TRACE_CPUPROFILER_EVENT_SCOPE(Libra_RenderSubmit);
            auto accepted = backend->Submit(commands,
                {snapshot->commands.GetData(), static_cast<size_t>(snapshot->commands.Num())});
            result->result.store(accepted ? liberafx::RenderResult::Ok : liberafx::RenderResult::Rejected,
                std::memory_order_release);
        });
        return true;
    }, &context);
    if (status == liberafx::RenderResult::Rejected) return context.failure;
    if (status == liberafx::RenderResult::Ok) submission = MoveTemp(context.submission);
    return status;
}
