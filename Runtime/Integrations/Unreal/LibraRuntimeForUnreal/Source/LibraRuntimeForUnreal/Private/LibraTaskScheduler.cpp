#include "LibraTaskScheduler.h"
#include "ProfilingDebugging/CpuProfilerTrace.h"

FLibraTaskScheduler::~FLibraTaskScheduler() { Wait(); }

auto FLibraTaskScheduler::GetScheduler() -> liberafx::JobScheduler
{
    return {&Enqueue, this};
}

bool FLibraTaskScheduler::IsCompleted() const
{
    return !task.IsValid() || task.IsCompleted();
}

void FLibraTaskScheduler::Wait()
{
    TRACE_CPUPROFILER_EVENT_SCOPE(Libra_TaskWait);
    if (task.IsValid()) task.Wait();
    task = {};
}

auto FLibraTaskScheduler::Enqueue(void* context, liberafx::Job job, void* data) noexcept -> bool
{
    if (context == nullptr || job == nullptr) return false;
    auto scheduler = static_cast<FLibraTaskScheduler*>(context);
    if (!scheduler->IsCompleted()) return false;
    TRACE_CPUPROFILER_EVENT_SCOPE(Libra_TaskLaunch);
    scheduler->task = UE::Tasks::Launch(UE_SOURCE_LOCATION, [job, data]
    {
        TRACE_CPUPROFILER_EVENT_SCOPE(Libra_WorkerTask);
        job(data);
    });
    return true;
}
