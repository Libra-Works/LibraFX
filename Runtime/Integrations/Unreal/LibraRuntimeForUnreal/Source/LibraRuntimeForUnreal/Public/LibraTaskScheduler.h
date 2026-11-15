#pragma once

#include "CoreMinimal.h"
#include "Tasks/Task.h"
#include "liberafx/platform/JobScheduler.h"

// One owner thread submits/waits; task bodies run on UE workers.
class LIBRARUNTIMEFORUNREAL_API FLibraTaskScheduler final
{
public:
    ~FLibraTaskScheduler();
    FLibraTaskScheduler() = default;
    FLibraTaskScheduler(const FLibraTaskScheduler&) = delete;
    FLibraTaskScheduler& operator=(const FLibraTaskScheduler&) = delete;
    auto GetScheduler() -> liberafx::JobScheduler;
    bool IsCompleted() const;
    void Wait();

private:
    static auto Enqueue(void* context, liberafx::Job job, void* data) noexcept -> bool;
    UE::Tasks::FTask task;
};
