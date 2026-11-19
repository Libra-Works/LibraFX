#pragma once

#include "liberafx/host/LbSpawnableDebugJob.h"
#if defined(_WIN32)
// Windows.h is included by the owning debug adapter before this header.
#include "liberafx/host/LbWindowsExecutionDebugClient.h"
#endif
#include <memory>
#include <thread>

namespace liberafx::godot_adapter {

// Engine-free state for the Godot main-thread facade. Mirrors the C ABI's
// NativeSpawnableDebug ownership/transport sequence without an interop registry
// or an inline scheduler. Only SpawnableDebugJob enters Core on the worker.
// The facade must not read runtime() while pending(), even after transport loss.
class GodotSpawnableDebug final {
public:
    using Request = host::SpawnableDebugJob::Request;

    static auto create(const std::uint8_t* bytes, std::size_t byteCount,
                       std::unique_ptr<GodotSpawnableDebug>& out) noexcept -> Result
    {
        auto package = SpawnablePackage{};
        auto result = loadSpawnablePackage(bytes, byteCount, package);
        if (result != Result::Ok) {
            return result;
        }
        try {
            auto owner = std::unique_ptr<SpawnableHost>{};
            result = SpawnableHost::create(std::move(package), SpawnableHostOptions{}, owner);
            if (result != Result::Ok) {
                return result;
            }
            auto candidate = std::unique_ptr<GodotSpawnableDebug>(
                new GodotSpawnableDebug(std::move(owner)));
#if defined(_WIN32)
            // Identity is for the complete original LFXP, not its root LFXB.
            candidate->identity_ = host::ExecutionDebugProtocol::programIdentity(bytes, byteCount);
#endif
            out = std::move(candidate);
            return Result::Ok;
        } catch (...) {
            return Result::InsufficientWork;
        }
    }

    ~GodotSpawnableDebug()
    {
        requestStop();
        if (worker_.joinable()) {
            worker_.join();
        }
        removeHooks();
    }

    auto runtime() noexcept -> SpawnableHost& { return *host_; }
    auto pending() const noexcept -> bool { return job_.pending(); }
    auto attached() const noexcept -> bool
    {
#if defined(_WIN32)
        return client_ != nullptr;
#else
        return false;
#endif
    }
    auto transportRunning() const noexcept -> bool
    {
#if defined(_WIN32)
        return client_ && client_->running();
#else
        return false;
#endif
    }

    auto attach(std::uint64_t generation, const SpawnableScope& scope,
                const char* invitation) noexcept -> Result
    {
        if (pending() || attached()) {
            return Result::Busy;
        }
#if defined(_WIN32)
        if (generation != host_->debugGeneration()) {
            return Result::InvalidHandle;
        }
        auto target = SpawnableDebugTarget{};
        auto result = host_->debugTarget(scope, target);
        if (result != Result::Ok) {
            return result;
        }
        try {
            client_ = std::make_unique<host::WindowsExecutionDebugClient>(
                identity_, target.hostGeneration, target.scope.containerId, target.scope.instanceId);
            target_ = target;
            result = host_->setDebugHooks(target_, client_->hooks());
            if (result != Result::Ok) {
                client_.reset();
                return result;
            }
            hooked_ = true;
            if (client_->start(invitation)) {
                return Result::Ok;
            }
            result = Result::InvalidArgument;
        } catch (...) {
            result = Result::InsufficientWork;
        }
        removeHooks();
        return result;
#else
        static_cast<void>(generation);
        static_cast<void>(scope);
        static_cast<void>(invitation);
        return Result::UnsupportedBinary;
#endif
    }

    auto submit(const Request& request) noexcept -> Result
    {
        return job_.submit(request, {&GodotSpawnableDebug::enqueue, this});
    }

    auto poll(Result& workResult) noexcept -> Result
    {
        auto result = job_.poll(workResult);
        if (result == Result::Ok && worker_.joinable()) {
            worker_.join();
        }
        return result;
    }

    void requestStop() noexcept
    {
#if defined(_WIN32)
        if (client_) {
            // Releases a paused evaluator BEFORE anyone joins its worker.
            client_->stop();
        }
#endif
    }

    auto detach() noexcept -> Result
    {
        requestStop();
        if (pending()) {
            return Result::Busy;
        }
        removeHooks();
        return Result::Ok;
    }

private:
    explicit GodotSpawnableDebug(std::unique_ptr<SpawnableHost> owner) noexcept
        : host_(std::move(owner)), job_(*host_) {}

    static auto enqueue(void* context, Job run, void* argument) noexcept -> bool
    {
        auto& self = *static_cast<GodotSpawnableDebug*>(context);
        if (self.worker_.joinable()) {
            return false;
        }
        try {
            // Captures only Core's copied request, never a Godot object.
            self.worker_ = std::thread(run, argument);
            return true;
        } catch (...) {
            return false;
        }
    }

    void removeHooks() noexcept
    {
#if defined(_WIN32)
        if (hooked_) {
            // A retired child may return InvalidHandle: its Instance (including
            // the hook) is already gone. Generation prevents rebinding it.
            static_cast<void>(host_->setDebugHooks(target_, {}));
            hooked_ = false;
        }
        client_.reset();
#endif
    }

    std::unique_ptr<SpawnableHost> host_;
    host::SpawnableDebugJob job_;
    std::thread worker_;
#if defined(_WIN32)
    std::unique_ptr<host::WindowsExecutionDebugClient> client_;
    SpawnableDebugTarget target_{};
    std::uint64_t identity_ = 0;
    bool hooked_ = false;
#endif
};

} // namespace liberafx::godot_adapter
