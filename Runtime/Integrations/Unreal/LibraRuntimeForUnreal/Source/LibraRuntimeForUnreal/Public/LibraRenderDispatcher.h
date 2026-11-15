#pragma once

#include "CoreMinimal.h"
#include "liberafx/platform/RenderQueue.h"
#include <atomic>

class FRHICommandListImmediate;

// Engine-owned render resources, never UObject loading or graph traversal on RT.
class LIBRARUNTIMEFORUNREAL_API ILibraRenderBackend
{
public:
    virtual ~ILibraRenderBackend() = default;
    virtual bool Submit(FRHICommandListImmediate& commands, liberafx::RenderBatch batch) = 0;
};

class LIBRARUNTIMEFORUNREAL_API FLibraRenderSubmission final
{
public:
    auto GetResult() const -> liberafx::RenderResult { return result.load(std::memory_order_acquire); }

private:
    friend class FLibraRenderDispatcher;
    std::atomic<liberafx::RenderResult> result{liberafx::RenderResult::NotReady};
};

class LIBRARUNTIMEFORUNREAL_API FLibraRenderDispatcher final
{
public:
    // GT snapshots borrowed buffers before releasing the Core queue. Completion
    // means backend submission on RT, not GPU completion. No global render flush.
    static auto Submit(liberafx::RenderQueue& queue,
        TSharedRef<ILibraRenderBackend, ESPMode::ThreadSafe> backend,
        TSharedPtr<FLibraRenderSubmission, ESPMode::ThreadSafe>& submission,
        uint64 maximumSnapshotBytes = 16 * 1024 * 1024) -> liberafx::RenderResult;
};
