#pragma once

namespace liberafx {

using Job = void (*)(void*) noexcept;
// Returning false must mean that the job was not and will not be executed.
using Enqueue = bool (*)(void* context, Job job, void* jobData) noexcept;

// Engine-owned task objects stay behind this callback boundary. The host must
// join accepted jobs before destroying their data, Instance or scheduler context.
struct JobScheduler final {
    Enqueue enqueue = nullptr;
    void* context = nullptr;
};

}
