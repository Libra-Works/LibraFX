#pragma once

#include "liberafx/LbRuntimeResult.h"
#include "liberafx/LbVfxRuntime.h"
#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>
#include "liberafx/host/LbWindowsExecutionDebugWorker.h"
#endif
#include "LibraFxGodotSpawnableDebug.h"
#include "LibraFxGodotRender.h"
#include "LibraFxGodotGpu.h"

#include <godot_cpp/classes/camera3d.hpp>
#include <godot_cpp/classes/node.hpp>
#include <godot_cpp/classes/thread.hpp>
#include <godot_cpp/classes/viewport.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/packed_byte_array.hpp>
#include <godot_cpp/variant/packed_float64_array.hpp>
#include <godot_cpp/variant/string.hpp>

#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <deque>
#include <memory>
#include <new>
#include <thread>
#include <vector>

namespace liberafx::godot_adapter {

using godot::Dictionary;
using godot::Node;
using godot::PackedByteArray;
using godot::PackedFloat64Array;
using godot::String;

// .lfxb/.lfxp debug player. Package loading is separate from debugger attach:
// first obtain the generation and (for a child) its Spawn event, then attach
// using the editor's fresh invitation. Poll before reading host-owned values.
// Core jobs contain no engine objects; Godot values stay on the main thread.
class LibraFxDebugPlayer final : public Node {
    GDCLASS(LibraFxDebugPlayer, Node)

    struct AlignedStorage final {
        void* data = nullptr;
        std::size_t alignment = alignof(std::max_align_t);

        ~AlignedStorage() { reset(); }
        AlignedStorage() = default;
        AlignedStorage(const AlignedStorage&) = delete;
        AlignedStorage& operator=(const AlignedStorage&) = delete;

        auto allocate(std::size_t bytes, std::size_t requestedAlignment) noexcept -> bool
        {
            reset();
            if (bytes == 0) {
                return true;
            }
            if (requestedAlignment < alignof(double) || requestedAlignment > 64 ||
                (requestedAlignment & (requestedAlignment - 1)) != 0) {
                return false;
            }
            alignment = requestedAlignment;
            try {
                data = ::operator new(bytes, std::align_val_t{alignment});
            } catch (...) {
                data = nullptr;
                return false;
            }
            std::memset(data, 0, bytes);
            return true;
        }

        void reset() noexcept
        {
            if (data) {
                ::operator delete(data, std::align_val_t{alignment});
                data = nullptr;
            }
        }

        auto doubles() noexcept -> double* { return static_cast<double*>(data); }
    };

    struct Playback final {
        std::vector<std::uint8_t> bytecode;
        Program program;
        AlignedStorage work;
        AlignedStorage defaults;
        Instance instance;
        std::unique_ptr<GodotSpawnableDebug> package;
        // Never captured by a Core job. Only the main-thread facade accesses
        // these after poll has acquired completion and joined the worker.
        std::unique_ptr<PackageRenderRenderer> renderer;
        std::unique_ptr<PackageGpuRenderer> gpu;
        std::deque<SpawnableHostEvent> events;
        String gpuError;
        Result renderResult = Result::NotReady;
#if defined(_WIN32)
        std::unique_ptr<host::WindowsExecutionDebugWorker> debugWorker;
#endif
        bool pending = false;
        bool closing = false;
        bool failed = false;
        bool pendingAdvance = false;
        double elapsed = 0;
        double requestedTime = 0;
        double requestedDelta = 0;
        Result lastResult = Result::NotReady;

        ~Playback() { shutdown(); }

        auto initialize(const PackedByteArray& source) noexcept -> Result
        {
            auto size = static_cast<std::size_t>(source.size());
            if (!source.ptr() || size < 12 || size > 16u * 1024u * 1024u) {
                return Result::InvalidArgument;
            }
            auto result = validateProgram(source.ptr(), size);
            if (result != Result::Ok) {
                return result;
            }
            try {
                bytecode.assign(source.ptr(), source.ptr() + size);
            } catch (...) {
                return Result::InsufficientWork;
            }
            result = Program::load(bytecode.data(), bytecode.size(), program);
            if (result != Result::Ok) {
                return result;
            }

            auto workBytes = program.workBytes();
            auto workCount = workBytes / sizeof(double) + (workBytes % sizeof(double) ? 1u : 0u);
            auto defaultsCount = program.parameterValueCount();
            constexpr auto maxWorkBytes = std::size_t{16} * 1024u * 1024u;
            if (workCount > maxWorkBytes / sizeof(double) ||
                defaultsCount > maxWorkBytes / sizeof(double)) {
                return Result::InsufficientWork;
            }
            if (!work.allocate(workCount * sizeof(double), program.workAlignment()) ||
                !defaults.allocate(defaultsCount * sizeof(double), program.defaultsAlignment())) {
                return Result::InsufficientWork;
            }
            return instance.initialize(program, work.doubles(), workCount,
                defaults.doubles(), defaultsCount);
        }

        auto attachDebugger(const char* invitation) noexcept -> Result
        {
#if defined(_WIN32)
            auto identity = host::ExecutionDebugProtocol::programIdentity(
                bytecode.data(), bytecode.size());
            try {
                debugWorker = std::make_unique<host::WindowsExecutionDebugWorker>(instance, identity);
            } catch (...) {
                return Result::InsufficientWork;
            }
            auto result = debugWorker->start(invitation);
            if (result != Result::Ok) {
                debugWorker.reset();
            }
            return result;
#else
            static_cast<void>(invitation);
            return Result::UnsupportedBinary;
#endif
        }

        auto submit(double delta) noexcept -> Result
        {
            if (!std::isfinite(delta) || delta < 0) {
                return Result::InvalidArgument;
            }
            if (closing || failed) {
                return Result::NotReady;
            }
            if (pending) {
                return Result::Busy;
            }
            auto nextTime = elapsed + delta;
            if (!std::isfinite(nextTime)) {
                return Result::InvalidArgument;
            }
            if (package) {
                auto request = GodotSpawnableDebug::Request{};
                request.time = nextTime;
                request.frameDelta = delta;
                auto result = submitPackage(request);
                if (result == Result::Ok) {
                    requestedTime = nextTime;
                    requestedDelta = delta;
                    pendingAdvance = true;
                }
                return result;
            }
#if defined(_WIN32)
            if (!debugWorker) {
                return Result::NotReady;
            }
            auto result = instance.setTimeContext(nextTime, delta);
            if (result != Result::Ok) {
                return result;
            }
            result = debugWorker->schedule(nextTime);
            if (result == Result::Ok) {
                requestedTime = nextTime;
                pending = true;
                pendingAdvance = true;
            }
            return result;
#else
            return Result::UnsupportedBinary;
#endif
        }

        auto submitPackage(const GodotSpawnableDebug::Request& request) noexcept -> Result
        {
            if (!package || closing || failed) {
                return Result::NotReady;
            }
            if (pending) {
                return Result::Busy;
            }
            auto result = package->submit(request);
            if (result == Result::Ok) {
                pending = true;
                pendingAdvance = false;
            }
            return result;
        }

        auto poll() noexcept -> Result
        {
            if (!pending) {
                return lastResult;
            }
            auto result = Result::NotReady;
            if (package) {
                auto status = package->poll(result);
                if (status != Result::Ok) {
                    return status;
                }
            } else {
#if defined(_WIN32)
                if (!debugWorker || !debugWorker->isComplete()) {
                    return Result::Busy;
                }
                result = debugWorker->finish();
                if (result == Result::Busy) {
                    return result;
                }
#else
                result = Result::UnsupportedBinary;
#endif
            }
            pending = false;
            lastResult = result;
            if (result == Result::Ok) {
                if (pendingAdvance) {
                    elapsed = requestedTime;
                }
            } else {
                // Package operations have the same retry boundary as the
                // C ABI (notably QueueRejected after draining events). Keep
                // the error visible but do not permanently disable the host.
                failed = !package;
            }
            return result;
        }

        void beginClose() noexcept
        {
            closing = true;
            if (package) {
                package->requestStop();
            }
#if defined(_WIN32)
            if (debugWorker) {
                debugWorker->requestStop();
            }
#endif
        }

        auto detachIfReady() noexcept -> Result
        {
            if (package) {
                if (pending && poll() == Result::Busy) {
                    return Result::Busy;
                }
                return package->detach();
            }
#if defined(_WIN32)
            if (!debugWorker) {
                return Result::NotReady;
            }
            if (pending) {
                auto result = poll();
                if (result == Result::Busy) {
                    return result;
                }
            }
            auto result = debugWorker->detach();
            if (result == Result::Ok) {
                debugWorker.reset();
            }
            return result;
#else
            return Result::UnsupportedBinary;
#endif
        }

        void shutdown() noexcept
        {
            if (package) {
                // Session destruction stops transport, releases any pause,
                // joins evaluation, then removes hooks before freeing Core.
                package.reset();
                pending = false;
                return;
            }
#if defined(_WIN32)
            if (!debugWorker) {
                return;
            }
            beginClose();
            if (pending) {
                // requestStop releases a Core pause. The standalone v1/v2
                // program is capped at 10,000 instructions; after release the
                // existing worker's bounded evaluation can finish and join.
                while (pending && debugWorker && !debugWorker->isComplete()) {
                    std::this_thread::yield();
                }
                if (pending) {
                    static_cast<void>(poll());
                }
            }
            auto result = detachIfReady();
            if (result != Result::Ok) {
                // The worker's own destructor performs the final stop/join/
                // hook-removal ordering; it never leaves a dangling callback.
                debugWorker.reset();
            }
#endif
        }

        static auto validateProgram(const std::uint8_t* bytes, std::size_t size) noexcept -> Result
        {
            if (!bytes || size < 12 || std::memcmp(bytes, "LFXB", 4) != 0) {
                return Result::InvalidBinary;
            }
            auto version = static_cast<std::uint16_t>(bytes[4] | (std::uint16_t(bytes[5]) << 8));
            if (version != 1 && version != 2) {
                return Result::UnsupportedBinary;
            }
            auto count = std::uint32_t{};
            for (auto index = 0u; index < 4; ++index) {
                count |= std::uint32_t(bytes[8 + index]) << (index * 8);
            }
            if (count > 10000) {
                return Result::InvalidBinary;
            }
            auto headerBytes = version == 1 ? std::size_t{12} : std::size_t{16};
            auto instructionBytes = version == 1 ? std::size_t{44} : std::size_t{96};
            return size == headerBytes + std::size_t{count} * instructionBytes
                ? Result::Ok : Result::InvalidBinary;
        }
    };

public:
    LibraFxDebugPlayer() = default;
    ~LibraFxDebugPlayer() override { closeAndJoin(); }

    int load_package(const PackedByteArray& bytes)
    {
        if (!godot::Thread::is_main_thread()) {
            return code(Result::InvalidArgument);
        }
        if (playback_) {
            return code(Result::Busy);
        }
        gpuError_ = String{};
        try {
            auto candidate = std::make_unique<Playback>();
            auto result = GodotSpawnableDebug::create(bytes.ptr(),
                static_cast<std::size_t>(bytes.size()), candidate->package);
            if (result != Result::Ok) {
                return code(result);
            }
            auto& package = candidate->package->runtime().package();
            result = PackageGpuRenderer::create(package, textureBindings_,
                candidate->gpu, gpuError_);
            if (result == Result::Ok) {
                result = PackageRenderRenderer::create(package, meshBindings_,
                    textureBindings_, materialBindings_, candidate->renderer);
            }
            if (result == Result::Ok) {
                result = candidate->renderer->attach(*this);
            }
            if (result == Result::Ok && candidate->gpu) {
                result = candidate->gpu->attach(*this, gpuError_);
            }
            if (result != Result::Ok) {
                return code(result);
            }
            candidate->gpuError = gpuError_;
            playback_ = std::move(candidate);
            return code(Result::Ok);
        } catch (...) {
            return code(Result::InsufficientWork);
        }
    }

    int set_mesh_binding(const String& identity, const godot::Ref<godot::Mesh>& mesh)
    {
        return setBinding(identity, mesh, meshBindings_);
    }

    int set_texture_binding(const String& identity, const godot::Ref<godot::Texture2D>& texture)
    {
        return setBinding(identity, texture, textureBindings_);
    }

    int set_material_binding(const String& identity, const godot::Ref<godot::Material>& material)
    {
        return setBinding(identity, material, materialBindings_);
    }

    String get_gpu_error() const
    {
        return godot::Thread::is_main_thread() ? gpuError_ : String{};
    }

    void set_gpu_spawn_per_second(double value)
    {
        if (godot::Thread::is_main_thread() && std::isfinite(value) && value >= 0) {
            gpuSpawnPerSecond_ = value;
        }
    }

    double get_gpu_spawn_per_second() const
    {
        return godot::Thread::is_main_thread() ? gpuSpawnPerSecond_ : 0;
    }

    // Zero means unavailable. Do not query host state during an in-flight job.
    std::int64_t get_debugger_generation() const
    {
        return packageBoundary() == Result::Ok
            ? static_cast<std::int64_t>(playback_->package->runtime().debugGeneration()) : 0;
    }

    int attach_package_debugger(std::int64_t generation, const String& containerId,
                                std::int64_t instanceId, const String& invitation)
    {
        auto result = packageBoundary();
        if (result != Result::Ok) {
            return code(result);
        }
        auto scope = SpawnableScope{};
        if (!parseScope(containerId, instanceId, scope) || generation == 0) {
            return code(Result::InvalidArgument);
        }
        auto utf8 = invitation.utf8();
        if (utf8.length() != 32) {
            return code(Result::InvalidArgument);
        }
        return code(playback_->package->attach(static_cast<std::uint64_t>(generation),
            scope, utf8.get_data()));
    }

    int detach_package_debugger()
    {
        // Unlike other operations this must be available while paused.
        if (!godot::Thread::is_main_thread()) {
            return code(Result::InvalidArgument);
        }
        if (!playback_ || !playback_->package) {
            return code(Result::NotReady);
        }
        // Busy means the pause was released; poll completion and retry detach.
        return code(playback_->package->detach());
    }

    int dispatch_signal(const String& nodeId)
    {
        return submitPackageSignal(host::SpawnableDebugOperation::Signal, nodeId, {}, {}, 0);
    }

    int fire_game_signal(const String& name)
    {
        return submitPackageSignal(host::SpawnableDebugOperation::GameSignal, {}, name, {}, 0);
    }

    int request_natural_end()
    {
        return submitPackageSignal(host::SpawnableDebugOperation::NaturalEnd, {}, {}, {}, 0);
    }

    int notify_end_request(const String& nodeId, const String& containerId, std::int64_t instanceId)
    {
        return submitPackageSignal(host::SpawnableDebugOperation::NotifyEnd,
            nodeId, {}, containerId, instanceId);
    }

    int force_terminate(const String& containerId, std::int64_t instanceId)
    {
        auto result = packageBoundary();
        if (result != Result::Ok) {
            return code(result);
        }
        auto scope = SpawnableScope{};
        if (!parseScope(containerId, instanceId, scope)) {
            return code(Result::InvalidArgument);
        }
        // Core force is a boundary-only cleanup, not signal evaluation.
        result = playback_->package->runtime().forceTerminate(scope);
        if (result == Result::Ok) {
            result = synchronizeVisuals(false);
        }
        return code(result);
    }

    int set_game_property(const String& name, const PackedFloat64Array& values)
    {
        auto result = packageBoundary();
        if (result != Result::Ok) {
            return code(result);
        }
        auto utf8 = name.utf8();
        if (!validName(utf8) || !validValues(values, false)) {
            return code(Result::InvalidArgument);
        }
        return code(playback_->package->runtime().setGameProperty(utf8.get_data(),
            values.ptr(), static_cast<std::size_t>(values.size())));
    }

    int set_owner_matrix(const PackedFloat64Array& values)
    {
        auto result = packageBoundary();
        if (result != Result::Ok) {
            return code(result);
        }
        if (!validValues(values, true)) {
            return code(Result::InvalidArgument);
        }
        return code(playback_->package->runtime().setOwnerMatrix(values.ptr(), 16));
    }

    godot::Array get_game_io_ports() const
    {
        auto out = godot::Array{};
        if (packageBoundary() != Result::Ok) {
            return out;
        }
        for (const auto& port : playback_->package->runtime().package().gameIoPorts) {
            auto item = Dictionary{};
            item["name"] = String::utf8(port.name.c_str());
            item["kind"] = static_cast<int>(port.kind);
            item["width"] = static_cast<int>(port.width);
            item["is_color"] = port.isColor;
            item["node_id"] = uuidString(port.nodeId.data());
            out.push_back(item);
        }
        return out;
    }

    // Lifecycle events expose exact container+instance identities for child
    // selection. Pull, rather than emit signals, to avoid reentrant owner free.
    Dictionary next_package_event()
    {
        auto out = Dictionary{};
        auto result = packageBoundary(true);
        out["result"] = code(result);
        out["has_event"] = false;
        if (result != Result::Ok) {
            return out;
        }
        auto event = SpawnableHostEvent{};
        if (!playback_->events.empty()) {
            event = playback_->events.front();
            playback_->events.pop_front();
            out["has_event"] = true;
            out["kind"] = static_cast<int>(event.kind);
            out["container_id"] = uuidString(event.containerId);
            out["instance_id"] = static_cast<std::int64_t>(event.instanceId);
            out["time"] = event.time;
            out["seed"] = static_cast<std::int64_t>(event.seed);
            out["signal_node_id"] = uuidString(event.signalNodeId);
        }
        return out;
    }

    Dictionary next_game_io_event()
    {
        auto out = Dictionary{};
        auto result = packageBoundary(true);
        out["result"] = code(result);
        out["has_event"] = false;
        if (result != Result::Ok) {
            return out;
        }
        auto event = NamedIoEvent{};
        auto& owner = playback_->package->runtime();
        if (!owner.popGameIoEvent(event)) {
            return out;
        }
        auto name = static_cast<const char*>(nullptr);
        if (event.identity.instanceId) {
            result = owner.instancePortName(event.identity.containerId, event.portIndex, name);
        } else if (event.portIndex < owner.package().gameIoPorts.size()) {
            name = owner.package().gameIoPorts[event.portIndex].name.c_str();
        } else {
            result = Result::InvalidBinary;
        }
        // Preserve matrices as all 16 lanes, never truncate to a runtime vec4.
        if (event.width > 16) {
            result = Result::InvalidBinary;
        }
        out["result"] = code(result);
        out["has_event"] = true;
        out["container_id"] = uuidString(event.identity.containerId);
        out["instance_id"] = static_cast<std::int64_t>(event.identity.instanceId);
        out["port_index"] = static_cast<std::int64_t>(event.portIndex);
        out["kind"] = static_cast<int>(event.kind);
        out["is_color"] = event.isColor;
        if (name) {
            out["name"] = String::utf8(name);
        }
        if (event.width <= 16) {
            auto values = PackedFloat64Array{};
            values.resize(event.width);
            for (auto index = 0u; index < event.width; ++index) {
                values.set(index, event.values[index]);
            }
            out["values"] = values;
        }
        return out;
    }

    // Result-bearing alternative to read_value(), including explicit child
    // identity. Reading during a pause returns Busy, never races Core memory.
    Dictionary read_package_value(const String& nodeId, const String& containerId,
                                  std::int64_t instanceId)
    {
        auto out = Dictionary{};
        auto result = packageBoundary();
        auto values = PackedFloat64Array{};
        if (result == Result::Ok) {
            auto node = std::array<std::uint8_t, 16>{};
            auto scope = SpawnableScope{};
            if (!parseNodeId(nodeId, node) || !parseScope(containerId, instanceId, scope)) {
                result = Result::InvalidArgument;
            } else {
                auto components = std::array<double, 4>{};
                auto width = std::uint8_t{};
                auto& owner = playback_->package->runtime();
                result = scope.kind == SpawnableScopeKind::Effect
                    ? owner.rootValue(node.data(), components.data(), width)
                    : owner.childValue(scope.containerId, scope.instanceId, node.data(),
                        components.data(), width);
                if (result == Result::Ok && width <= components.size()) {
                    values.resize(width);
                    for (auto index = 0u; index < width; ++index) {
                        values.set(index, components[index]);
                    }
                } else if (result == Result::Ok) {
                    result = Result::InvalidBinary;
                }
            }
        }
        out["result"] = code(result);
        out["values"] = values;
        return out;
    }

    int start_debug(const PackedByteArray& bytecode, const String& invitation)
    {
        if (!godot::Thread::is_main_thread()) {
            return code(Result::InvalidArgument);
        }
        if (playback_) {
            return code(Result::Busy);
        }
        auto candidate = std::unique_ptr<Playback>{};
        try {
            candidate = std::make_unique<Playback>();
        } catch (...) {
            return code(Result::InsufficientWork);
        }
        auto result = candidate->initialize(bytecode);
        if (result != Result::Ok) {
            return code(result);
        }
        auto invitationUtf8 = invitation.utf8();
        if (invitationUtf8.length() != 32) {
            return code(Result::InvalidArgument);
        }
        result = candidate->attachDebugger(invitationUtf8.get_data());
        if (result != Result::Ok) {
            return code(result);
        }
        playback_ = std::move(candidate);
        return code(Result::Ok);
    }

    int advance(double delta)
    {
        if (!godot::Thread::is_main_thread() || !playback_) {
            return code(Result::NotReady);
        }
        if (playback_->package) {
            auto boundary = packageBoundary();
            if (boundary != Result::Ok) {
                return code(boundary);
            }
            auto result = updateCurrentCamera();
            if (result != Result::Ok) {
                return code(result);
            }
        }
        return code(playback_->submit(delta));
    }

    Dictionary poll()
    {
        auto status = Dictionary{};
        if (!godot::Thread::is_main_thread()) {
            status["result"] = code(Result::InvalidArgument);
            status["closed"] = false;
            return status;
        }
        if (!playback_) {
            status["active"] = false;
            status["busy"] = false;
            status["closing"] = false;
            status["closed"] = true;
            status["result"] = code(Result::NotReady);
            status["time"] = 0.0;
            status["debug_transport_worker_running"] = false;
            return status;
        }

        auto wasPending = playback_->pending;
        auto result = playback_->poll();
        if (wasPending && !playback_->pending && !playback_->closing && playback_->package) {
            // Even a rejected operation may have queued lifecycle events. Drain
            // those without advancing GPU simulation or reading failed values.
            static_cast<void>(synchronizeVisuals(result == Result::Ok));
        }
        if (result == Result::Ok && playback_->renderer &&
            playback_->renderResult != Result::Ok) {
            result = playback_->renderResult;
        }
        auto closing = playback_->closing;
        auto pending = playback_->pending;
        auto elapsed = playback_->elapsed;
        auto failed = playback_->failed;
        if (closing && !pending) {
            auto detached = playback_->detachIfReady();
            if (detached == Result::Ok) {
                playback_.reset();
                status["active"] = false;
                status["busy"] = false;
                status["closing"] = false;
                status["closed"] = true;
                status["result"] = code(result);
                status["time"] = elapsed;
                status["debug_transport_worker_running"] = false;
                return status;
            }
            if (detached != Result::NotReady) {
                result = detached;
            }
        }
        status["active"] = !closing && !failed;
        status["busy"] = pending;
        status["closing"] = closing;
        status["closed"] = false;
        status["result"] = code(result);
        status["time"] = elapsed;
        status["package"] = playback_->package != nullptr;
        status["visual_preview"] = playback_->renderer != nullptr;
        status["render_result"] = code(playback_->renderResult);
        status["gpu_error"] = playback_->gpuError;
        status["gpu_emitters"] = static_cast<std::int64_t>(
            playback_->gpu ? playback_->gpu->emitterCount() : 0);
        if (playback_->package) {
            status["debugger_attached"] = playback_->package->attached();
            status["debug_transport_worker_running"] = playback_->package->transportRunning();
            // Completion must be acquired by poll() above before host reads.
            if (!pending) {
                status["generation"] = static_cast<std::int64_t>(
                    playback_->package->runtime().debugGeneration());
                status["active_count"] = static_cast<std::int64_t>(
                    playback_->package->runtime().activeCount());
                status["root_finished"] = playback_->package->runtime().rootFinished();
            }
            return status;
        }
#if defined(_WIN32)
        status["debug_transport_worker_running"] = playback_->debugWorker &&
            playback_->debugWorker->transportRunning();
#else
        status["debug_transport_worker_running"] = false;
#endif
        return status;
    }

    PackedFloat64Array read_value(const String& nodeId)
    {
        auto values = PackedFloat64Array{};
        if (!godot::Thread::is_main_thread() || !playback_ || playback_->pending ||
            playback_->closing || playback_->failed) {
            return values;
        }
        auto id = std::array<std::uint8_t, 16>{};
        if (!parseNodeId(nodeId, id)) {
            return values;
        }
        auto components = std::array<double, 4>{};
        auto width = std::uint8_t{};
        auto result = playback_->package
            ? playback_->package->runtime().rootValue(id.data(), components.data(), width)
            : playback_->instance.valueComponents(id.data(), components.data(), width);
        if (result == Result::Ok && width <= components.size()) {
            values.resize(width);
            for (auto index = 0u; index < width; ++index) {
                values.set(index, components[index]);
            }
        }
        return values;
    }

    int close()
    {
        if (!godot::Thread::is_main_thread()) {
            return code(Result::InvalidArgument);
        }
        if (!playback_) {
            return code(Result::NotReady);
        }
        playback_->beginClose();
        return code(Result::Ok);
    }

    void _exit_tree() override { closeAndJoin(); }

protected:
    static void _bind_methods()
    {
        godot::ClassDB::bind_method(godot::D_METHOD("start_debug", "bytecode", "invitation"),
            &LibraFxDebugPlayer::start_debug);
        godot::ClassDB::bind_method(godot::D_METHOD("load_package", "package_bytes"),
            &LibraFxDebugPlayer::load_package);
        godot::ClassDB::bind_method(godot::D_METHOD("set_mesh_binding", "identity", "mesh"),
            &LibraFxDebugPlayer::set_mesh_binding);
        godot::ClassDB::bind_method(godot::D_METHOD("set_texture_binding", "identity", "texture"),
            &LibraFxDebugPlayer::set_texture_binding);
        godot::ClassDB::bind_method(godot::D_METHOD("set_material_binding", "identity", "material"),
            &LibraFxDebugPlayer::set_material_binding);
        godot::ClassDB::bind_method(godot::D_METHOD("get_gpu_error"), &LibraFxDebugPlayer::get_gpu_error);
        godot::ClassDB::bind_method(godot::D_METHOD("set_gpu_spawn_per_second", "value"),
            &LibraFxDebugPlayer::set_gpu_spawn_per_second);
        godot::ClassDB::bind_method(godot::D_METHOD("get_gpu_spawn_per_second"),
            &LibraFxDebugPlayer::get_gpu_spawn_per_second);
        godot::ClassDB::bind_method(godot::D_METHOD("get_debugger_generation"),
            &LibraFxDebugPlayer::get_debugger_generation);
        godot::ClassDB::bind_method(godot::D_METHOD("attach_package_debugger",
            "generation", "container_id", "instance_id", "invitation"),
            &LibraFxDebugPlayer::attach_package_debugger);
        godot::ClassDB::bind_method(godot::D_METHOD("detach_package_debugger"),
            &LibraFxDebugPlayer::detach_package_debugger);
        godot::ClassDB::bind_method(godot::D_METHOD("dispatch_signal", "node_id"),
            &LibraFxDebugPlayer::dispatch_signal);
        godot::ClassDB::bind_method(godot::D_METHOD("fire_game_signal", "name"),
            &LibraFxDebugPlayer::fire_game_signal);
        godot::ClassDB::bind_method(godot::D_METHOD("request_natural_end"),
            &LibraFxDebugPlayer::request_natural_end);
        godot::ClassDB::bind_method(godot::D_METHOD("notify_end_request",
            "node_id", "container_id", "instance_id"), &LibraFxDebugPlayer::notify_end_request);
        godot::ClassDB::bind_method(godot::D_METHOD("force_terminate", "container_id", "instance_id"),
            &LibraFxDebugPlayer::force_terminate);
        godot::ClassDB::bind_method(godot::D_METHOD("set_game_property", "name", "values"),
            &LibraFxDebugPlayer::set_game_property);
        godot::ClassDB::bind_method(godot::D_METHOD("set_owner_matrix", "values"),
            &LibraFxDebugPlayer::set_owner_matrix);
        godot::ClassDB::bind_method(godot::D_METHOD("get_game_io_ports"),
            &LibraFxDebugPlayer::get_game_io_ports);
        godot::ClassDB::bind_method(godot::D_METHOD("next_package_event"),
            &LibraFxDebugPlayer::next_package_event);
        godot::ClassDB::bind_method(godot::D_METHOD("next_game_io_event"),
            &LibraFxDebugPlayer::next_game_io_event);
        godot::ClassDB::bind_method(godot::D_METHOD("read_package_value",
            "node_id", "container_id", "instance_id"), &LibraFxDebugPlayer::read_package_value);
        godot::ClassDB::bind_method(godot::D_METHOD("advance", "delta"), &LibraFxDebugPlayer::advance);
        godot::ClassDB::bind_method(godot::D_METHOD("poll"), &LibraFxDebugPlayer::poll);
        godot::ClassDB::bind_method(godot::D_METHOD("read_value", "node_id"), &LibraFxDebugPlayer::read_value);
        godot::ClassDB::bind_method(godot::D_METHOD("close"), &LibraFxDebugPlayer::close);
    }

private:
    static auto code(Result result) noexcept -> int { return static_cast<int>(result); }

    template<class T, class Bindings>
    auto setBinding(const String& identity, const godot::Ref<T>& resource, Bindings& bindings) -> int
    {
        if (!godot::Thread::is_main_thread()) {
            return code(Result::InvalidArgument);
        }
        if (playback_) {
            return code(Result::Busy);
        }
        auto utf8 = identity.utf8();
        if (resource.is_null() || utf8.length() == 0 || utf8.length() > 512 ||
            std::strlen(utf8.get_data()) != static_cast<std::size_t>(utf8.length())) {
            return code(Result::InvalidArgument);
        }
        bindings[std::string(utf8.get_data(), static_cast<std::size_t>(utf8.length()))] = resource;
        return code(Result::Ok);
    }

    auto synchronizeVisuals(bool updateValues) -> Result
    {
        auto& state = *playback_;
        auto& owner = state.package->runtime();
        auto result = Result::Ok;
        auto event = SpawnableHostEvent{};
        // Rendering must not steal events needed for debugger child selection.
        // Game-I/O events are a separate Core queue, never consumed here.
        while (owner.popEvent(event)) {
            state.events.push_back(event);
            auto handled = state.renderer->handleEvent(event, state.gpu.get(), state.gpuError);
            if (result == Result::Ok) {
                result = handled;
            }
        }
        if (result == Result::Ok && updateValues) {
            result = state.renderer->updateValues(owner, state.elapsed);
            if (result == Result::Ok && state.gpu) {
                result = state.gpu->advance(owner, state.elapsed,
                    state.pendingAdvance ? state.requestedDelta : 0,
                    state.gpuError, gpuSpawnPerSecond_);
            }
        }
        state.renderResult = result;
        gpuError_ = state.gpuError;
        return result;
    }

    auto updateCurrentCamera() -> Result
    {
        auto& owner = playback_->package->runtime();
        for (const auto& port : owner.package().gameIoPorts) {
            if (port.name != "__LFX_CAMERA_V1") {
                continue;
            }
            if (port.kind != static_cast<std::uint8_t>(NamedIoKind::InputProperty) || port.width != 4) {
                return Result::InvalidBinary;
            }
            auto position = std::array<double, 4>{};
            auto* viewport = get_viewport();
            auto* camera = viewport ? viewport->get_camera_3d() : nullptr;
            if (camera) {
                auto world = camera->get_global_position();
                position = {world.x, world.y, world.z, 1};
            }
            return owner.setGameProperty(port.name.c_str(), position.data(), position.size());
        }
        return Result::Ok;
    }

    auto packageBoundary(bool allowFailed = false) const noexcept -> Result
    {
        if (!godot::Thread::is_main_thread()) {
            return Result::InvalidArgument;
        }
        if (!playback_ || !playback_->package) {
            return Result::NotReady;
        }
        if (playback_->pending) {
            return Result::Busy;
        }
        if (playback_->closing || (playback_->failed && !allowFailed)) {
            return Result::NotReady;
        }
        return Result::Ok;
    }

    static auto validName(const godot::CharString& name) noexcept -> bool
    {
        return name.length() > 0 && name.length() <= 128 &&
            std::strlen(name.get_data()) == static_cast<std::size_t>(name.length());
    }

    static auto validValues(const PackedFloat64Array& values, bool matrix) noexcept -> bool
    {
        if (matrix ? values.size() != 16 : (values.size() < 1 || values.size() > 16)) {
            return false;
        }
        for (auto index = std::int64_t{}; index < values.size(); ++index) {
            if (!std::isfinite(values[index])) {
                return false;
            }
        }
        return true;
    }

    static auto parseScope(const String& containerId, std::int64_t instanceId,
                           SpawnableScope& scope) noexcept -> bool
    {
        if (containerId.is_empty()) {
            return instanceId == 0;
        }
        auto id = std::array<std::uint8_t, 16>{};
        if (instanceId <= 0 || !parseNodeId(containerId, id)) {
            return false;
        }
        scope.kind = SpawnableScopeKind::Instance;
        std::memcpy(scope.containerId, id.data(), id.size());
        scope.instanceId = static_cast<std::uint64_t>(instanceId);
        return true;
    }

    auto submitPackageSignal(host::SpawnableDebugOperation operation,
                             const String& nodeId, const String& name,
                             const String& containerId, std::int64_t instanceId) -> int
    {
        auto result = packageBoundary();
        if (result != Result::Ok) {
            return code(result);
        }
        auto request = GodotSpawnableDebug::Request{};
        request.operation = operation;
        if (!parseScope(containerId, instanceId, request.scope)) {
            return code(Result::InvalidArgument);
        }
        if ((operation == host::SpawnableDebugOperation::Signal ||
             operation == host::SpawnableDebugOperation::NotifyEnd) &&
            !parseNodeId(nodeId, request.node)) {
            return code(Result::InvalidArgument);
        }
        if (operation == host::SpawnableDebugOperation::GameSignal) {
            auto utf8 = name.utf8();
            if (!validName(utf8)) {
                return code(Result::InvalidArgument);
            }
            std::memcpy(request.name.data(), utf8.get_data(),
                static_cast<std::size_t>(utf8.length()));
        }
        result = updateCurrentCamera();
        if (result != Result::Ok) {
            return code(result);
        }
        return code(playback_->submitPackage(request));
    }

    static auto uuidString(const std::uint8_t* bytes) -> String
    {
        constexpr auto hex = "0123456789abcdef";
        auto text = std::array<char, 37>{};
        auto cursor = 0u;
        for (auto index = 0u; index < 16; ++index) {
            if (index == 4 || index == 6 || index == 8 || index == 10) {
                text[cursor++] = '-';
            }
            text[cursor++] = hex[bytes[index] >> 4];
            text[cursor++] = hex[bytes[index] & 15];
        }
        return String::utf8(text.data());
    }

    static auto parseNodeId(const String& value, std::array<std::uint8_t, 16>& out) noexcept -> bool
    {
        auto utf8 = value.utf8();
        auto text = utf8.get_data();
        if (!text || utf8.length() != 36) {
            return false;
        }
        auto byteIndex = 0u;
        auto highNibble = -1;
        for (auto index = 0u; index < 36; ++index) {
            if (index == 8 || index == 13 || index == 18 || index == 23) {
                if (text[index] != '-') {
                    return false;
                }
                continue;
            }
            auto character = text[index];
            auto nibble = character >= '0' && character <= '9' ? character - '0' :
                character >= 'a' && character <= 'f' ? character - 'a' + 10 :
                character >= 'A' && character <= 'F' ? character - 'A' + 10 : -1;
            if (nibble < 0) {
                return false;
            }
            if (highNibble < 0) {
                highNibble = nibble;
            } else {
                out[byteIndex++] = static_cast<std::uint8_t>((highNibble << 4) | nibble);
                highNibble = -1;
            }
        }
        return byteIndex == out.size() && highNibble < 0;
    }

    void closeAndJoin() noexcept
    {
        if (playback_) {
            playback_->shutdown();
            playback_.reset();
        }
    }

    std::unique_ptr<Playback> playback_;
    MeshBindings meshBindings_;
    TextureBindings textureBindings_;
    MaterialBindings materialBindings_;
    String gpuError_;
    double gpuSpawnPerSecond_ = 100;
};

// Call this at Godot's SCENE initialization level from the owning extension
// initializer. Keeping registration separate lets the adapter include this
// debug-only class without modifying the parent-owned registration function.
inline void registerGodotDebugPlayback()
{
    godot::ClassDB::register_class<LibraFxDebugPlayer>();
}

} // namespace liberafx::godot_adapter
