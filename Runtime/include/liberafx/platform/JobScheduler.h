#pragma once

namespace liberafx {

using Job = void (*)(void*) noexcept;
// Returning false must mean that the job was not and will not be executed.
using Enqueue = bool (*)(void* context, Job job, void* jobData) noexcept;

}
