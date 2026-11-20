#pragma once

#include <godot_cpp/classes/ref_counted.hpp>
#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/variant/callable_method_pointer.hpp>
#include <deque>
#include <functional>
#include <mutex>
#include <utility>

namespace liberafx::godot_adapter::detail {

// Shared by GPU resource owners using the global RenderingDevice. Owners must
// retain this queue and captured state until shutdown work has been drained.
// Enqueuing work does not submit/sync a local device or read resources to CPU.
class GpuRenderTaskQueue final : public godot::RefCounted {
    GDCLASS(GpuRenderTaskQueue, godot::RefCounted)

public:
    using Task = std::function<void()>;

    static void _bind_methods()
    {
    }

    auto enqueue(Task task) -> bool
    {
        auto shouldSchedule = false;
        {
            std::lock_guard<std::mutex> lock(mutex_);
            tasks_.push_back(std::move(task));
            if (!scheduled_)
            {
                scheduled_ = true;
                shouldSchedule = true;
            }
        }
        if (shouldSchedule)
        {
            if (auto* server = godot::RenderingServer::get_singleton())
            {
                server->call_on_render_thread(godot::create_custom_callable_function_pointer(
                    this, &GpuRenderTaskQueue::runTasks));
            }
            else
            {
                std::lock_guard<std::mutex> lock(mutex_);
                tasks_.clear();
                scheduled_ = false;
                return false;
            }
        }
        return true;
    }

private:
    void runTasks()
    {
        for (;;)
        {
            auto task = Task{};
            {
                std::lock_guard<std::mutex> lock(mutex_);
                if (tasks_.empty())
                {
                    scheduled_ = false;
                    return;
                }
                task = std::move(tasks_.front());
                tasks_.pop_front();
            }
            task();
        }
    }

    std::mutex mutex_;
    std::deque<Task> tasks_;
    bool scheduled_ = false;
};

}
