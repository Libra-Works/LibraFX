#include "liberafx/host/LbProgramSession.h"
#include "liberafx/host/LbProjectNodes.h"
#include "liberafx/LbLivePreview.h"
#include "liberafx/LbCpuParticles.h"
#include "liberafx/LbSpawnableHost.h"
#include "LibraFxGodotRender.h"
#include "LibraFxGodotGpu.h"
#include "LibraFxGodotDebugPlayback.h"
#include <godot_cpp/classes/node.hpp>
#include <godot_cpp/classes/object.hpp>
#include <godot_cpp/classes/thread.hpp>
#include <godot_cpp/classes/camera3d.hpp>
#include <godot_cpp/classes/viewport.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/godot.hpp>
#include <godot_cpp/variant/array.hpp>
#include <godot_cpp/variant/callable.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/node_path.hpp>
#include <godot_cpp/variant/packed_byte_array.hpp>
#include <godot_cpp/variant/packed_float64_array.hpp>
#include <godot_cpp/variant/utility_functions.hpp>
#include <array>
#include <cmath>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

namespace liberafx::godot_adapter {

using godot::Node;
using godot::Array;
using godot::Dictionary;
using godot::Camera3D;
using godot::NodePath;
using godot::PackedByteArray;
using godot::PackedFloat64Array;
using godot::String;
using godot::Thread;
using godot::Mesh;
using godot::Ref;
using godot::Texture2D;
using liberafx::NamedIoEvent;
using liberafx::NamedIoKind;
using liberafx::SpawnableHost;
using liberafx::SpawnableHostOptions;
using liberafx::SpawnablePackage;
using host::ProgramCompletion;
using host::ProgramSession;
using host::ProgramSessionOptions;

class LibraEffectPlayer final : public Node {
    GDCLASS(LibraEffectPlayer, Node)

    struct Playback final {
        std::unique_ptr<ProgramSession> session;
        std::unique_ptr<SpawnableHost> packageHost;
        std::unique_ptr<PackageRenderRenderer> renderer;
        std::unique_ptr<PackageGpuRenderer> gpu;
        InstanceId instance{};
        bool active = false;
        double elapsed = 0;
        double duration = -1;

        Result complete(RequestId request)
        {
            auto processed = std::size_t{};
            auto result = session->process(1, processed);
            if (result != Result::Ok) {
                return result;
            }
            auto completion = ProgramCompletion{};
            result = session->takeCompletion(request, completion);
            if (result != Result::Ok) {
                return result;
            }
            if (completion.result == Result::Ok && completion.kind == InstanceRequestKind::Create) {
                instance = completion.instance;
                active = true;
            }
            return completion.result;
        }

        ~Playback()
        {
            if (packageHost) {
                packageHost->stop();
                return;
            }
            if (!session) {
                return;
            }
            if (active) {
                auto request = RequestId{};
                if (session->submitControl(InstanceRequestKind::Destroy, instance, request) == Result::Ok) {
                    complete(request);
                }
            }
            session->close();
        }
    };

public:
    int play(const PackedByteArray& bytecode, double duration)
    {
        if (bytecode.size() >= 4 && bytecode[0] == 'L' && bytecode[1] == 'F' &&
            bytecode[2] == 'X' && bytecode[3] == 'P')
        {
            return play_package(bytecode, duration);
        }
        return playStandalone(bytecode, duration, 0, false);
    }

    int playStandalone(const PackedByteArray& bytecode, double duration, double previewTime, bool preview)
    {
        if (!Thread::is_main_thread() || !is_inside_tree()) {
            return code(Result::InvalidArgument);
        }

        auto lifetime = Lifetime::infinite();
        if (duration != -1) {
            auto result = Lifetime::finite(duration, lifetime);
            if (result != Result::Ok) {
                return code(result);
            }
        }

        auto candidate = std::make_unique<Playback>();
        auto options = ProgramSessionOptions{};
        options.instanceCapacity = 1;
        options.requestCapacity = 1;
        auto result = ProgramSession::create({1, 1}, options, candidate->session);
        if (result != Result::Ok) {
            return code(result);
        }
        auto request = RequestId{};
        result = candidate->session->submitProgram(InstanceRequestKind::Create, {}, bytecode.ptr(),
            static_cast<std::size_t>(bytecode.size()), lifetime, ReplacementPolicy::Restart, request);
        if (result == Result::Ok) {
            result = candidate->complete(request);
        }
        if (result == Result::Ok && preview) {
            auto finished = false;
            result = candidate->session->advance(candidate->instance, previewTime, previewTime, finished);
            if (result == Result::Ok && finished) {
                result = Result::NotReady;
            }
        }
        if (result == Result::Ok) {
            candidate->duration = duration;
            candidate->elapsed = previewTime;
            ++playbackGeneration_;
            completedCpuEventHost_.reset();
            playback_ = std::move(candidate);
            set_process(!preview);
        }
        return code(result);
    }

    int apply_preview_frame(const PackedByteArray& bytes)
    {
        if (!Thread::is_main_thread() || !is_inside_tree()) {
            return code(Result::InvalidArgument);
        }
        auto frame = livepreview::FrameView{};
        if (livepreview::decodeFrame(bytes.ptr(), static_cast<std::size_t>(bytes.size()), frame) !=
            livepreview::DecodeResult::Ok) {
            return code(Result::InvalidBinary);
        }
        if (frame.kind == livepreview::Kind::Control || frame.kind == livepreview::Kind::Scene) {
            return code(Result::UnsupportedBinary);
        }
        auto state = previewSequence_.validate(frame);
        if (state == livepreview::SequenceResult::Stale) {
            return code(Result::Ok);
        }
        if (state == livepreview::SequenceResult::Resync) {
            return code(Result::QueueRejected);
        }
        if (frame.kind == livepreview::Kind::CompileError) {
            previewError_ = String::utf8(reinterpret_cast<const char*>(frame.payload), frame.payloadLength);
            previewSequence_.commit(frame);
            return code(Result::Ok);
        }
        if (frame.timeSeconds < 0) {
            return code(Result::InvalidArgument);
        }
        if (frame.kind == livepreview::Kind::Program) {
            if (frame.payloadLength < 12 || std::memcmp(frame.payload, "LFXB", 4) != 0) {
                return code(Result::InvalidBinary);
            }
            if (frame.payload[4] != 1 && frame.payload[4] != 2) {
                return code(Result::UnsupportedBinary);
            }
            auto program = PackedByteArray{};
            if (program.resize(static_cast<std::int64_t>(frame.payloadLength)) != godot::OK) {
                return code(Result::InsufficientWork);
            }
            std::memcpy(program.ptrw(), frame.payload, frame.payloadLength);
            auto result = playStandalone(program, -1, frame.timeSeconds, true);
            if (result != code(Result::Ok)) {
                return result;
            }
            previewBytecode_ = std::move(program);
        } else {
            if (!playback_ || playbackGeneration_ != previewPlaybackGeneration_ || previewBytecode_.is_empty()) {
                return code(Result::QueueRejected);
            }
            auto delta = frame.timeSeconds - playback_->elapsed;
            auto result = delta < 0
                ? playStandalone(previewBytecode_, -1, frame.timeSeconds, true)
                : advance(delta);
            if (result != code(Result::Ok)) {
                return result;
            }
        }
        previewPlaybackGeneration_ = playbackGeneration_;
        previewError_ = String{};
        previewSequence_.commit(frame);
        return code(Result::Ok);
    }

    void reset_preview_stream()
    {
        if (!Thread::is_main_thread()) {
            return;
        }
        if (playback_ && playbackGeneration_ == previewPlaybackGeneration_ && !previewBytecode_.is_empty()) {
            stop();
        }
        previewSequence_.reset();
        previewBytecode_ = PackedByteArray{};
        previewError_ = String{};
    }

    String get_preview_error() const { return Thread::is_main_thread() ? previewError_ : String{}; }

    int play_package(const PackedByteArray& packageBytes, double duration)
    {
        if (!Thread::is_main_thread() || !is_inside_tree()) {
            return code(Result::InvalidArgument);
        }

        gpuError_ = String{};
        auto lifetime = Lifetime::infinite();
        if (duration != -1) {
            auto result = Lifetime::finite(duration, lifetime);
            if (result != Result::Ok) {
                return code(result);
            }
        }

        auto package = SpawnablePackage{};
        auto result = liberafx::loadSpawnablePackage(packageBytes.ptr(),
            static_cast<std::size_t>(packageBytes.size()), package);
        if (result != Result::Ok) {
            return code(result);
        }
        auto candidate = std::make_unique<Playback>();
        result = PackageGpuRenderer::create(package, textureBindings_, candidate->gpu, gpuError_);
        if (result == Result::Ok) {
            result = PackageRenderRenderer::create(package, meshBindings_, textureBindings_, materialBindings_,
                candidate->renderer);
        }
        if (result != Result::Ok) {
            return code(result);
        }
        auto options = SpawnableHostOptions{};
        const auto projectNodes = liberafx::host::projectNodes();
        result = SpawnableHost::create(std::move(package), options,
            projectNodes.descriptors, projectNodes.count, candidate->packageHost);
        if (result == Result::Ok) {
            result = updateCurrentCamera(*candidate->packageHost);
        }
        if (result == Result::Ok) {
            result = candidate->renderer->attach(*this);
        }
        if (result == Result::Ok && candidate->gpu) {
            result = candidate->gpu->attach(*this, gpuError_);
        }
        if (result == Result::Ok) {
            candidate->duration = duration;
            ++playbackGeneration_;
            completedCpuEventHost_.reset();
            playback_ = std::move(candidate);
            set_process(true);
        }
        return code(result);
    }

    int advance(double delta)
    {
        if (!Thread::is_main_thread() || !std::isfinite(delta) || delta < 0) {
            return code(Result::InvalidArgument);
        }
        if (dispatchingScriptAction_)
        {
            return code(Result::Busy);
        }
        if (!playback_) {
            return code(Result::NotReady);
        }
        auto elapsed = playback_->elapsed + delta;
        if (!std::isfinite(elapsed)) {
            return code(Result::InvalidArgument);
        }
        if (playback_->packageHost) {
            auto generation = playbackGeneration_;
            auto result = updateCurrentCamera(*playback_->packageHost);
            if (result != Result::Ok) {
                return code(result);
            }
            result = playback_->packageHost->advance(elapsed, delta);
            if (result == Result::Ok) {
                playback_->elapsed = elapsed;
                if (playback_->renderer) {
                    result = playback_->renderer->advance(*playback_->packageHost, elapsed, playback_->gpu.get(), gpuError_);
                    if (result != Result::Ok) {
                        if (!gpuError_.is_empty()) {
                            stop();
                        }
                        return code(result);
                    }
                    if (playback_->gpu) {
                        result = playback_->gpu->advance(*playback_->packageHost, elapsed, delta,
                            gpuError_, gpuSpawnPerSecond_);
                        if (result != Result::Ok) {
                            stop();
                            return code(result);
                        }
                    }
                }
                drainGameIoEvents();
                if (generation != playbackGeneration_) {
                    return code(Result::Ok);
                }
                if (!playback_->packageHost->package().cpuPrograms.empty()) {
                    emit_signal("cpu_particle_events_ready");
                    if (generation != playbackGeneration_) {
                        return code(Result::Ok);
                    }
                }
                if (playback_ && ((playback_->duration >= 0 && elapsed >= playback_->duration) ||
                    playback_->packageHost->rootFinished())) {
                    if (playback_->duration >= 0 && elapsed >= playback_->duration &&
                        !playback_->packageHost->rootFinished()) {
                        result = playback_->packageHost->stop();
                    } else if (!playback_->packageHost->package().cpuPrograms.empty()) {
                        // Keep the terminal queue available to polling callers
                        // without retaining the render nodes or GPU adapter.
                        completedCpuEventHost_ = std::move(playback_->packageHost);
                    }
                    ++playbackGeneration_;
                    playback_.reset();
                    completedCpuEventHost_.reset();
                    set_process(false);
                }
            }
            return code(result);
        }
        auto finished = false;
        auto result = playback_->session->advance(playback_->instance, elapsed, delta, finished);
        if (result != Result::Ok || finished) {
            stop();
        } else {
            playback_->elapsed = elapsed;
        }
        return code(result);
    }

    void stop()
    {
        if (Thread::is_main_thread()) {
            if (playback_) {
                ++playbackGeneration_;
            }
            playback_.reset();
            set_process(false);
        }
    }

    bool is_playing() const { return Thread::is_main_thread() && playback_ != nullptr; }

    Dictionary pop_cpu_particle_event()
    {
        auto out = Dictionary{};
        if (!Thread::is_main_thread()) {
            return out;
        }
        auto* host = playback_ ? playback_->packageHost.get() : completedCpuEventHost_.get();
        if (!host) {
            return out;
        }
        auto event = CpuParticleEvent{};
        if (!host->popCpuParticleEvent(event)) {
            if (!playback_) {
                completedCpuEventHost_.reset();
            }
            return out;
        }
        auto values = PackedFloat64Array{};
        for (auto index = std::uint32_t{}; index < event.width; ++index) {
            values.append(event.values[index]);
        }
        out["system_id"] = uuidString(event.systemId);
        out["node_id"] = uuidString(event.nodeId);
        out["particle_id"] = String::num_uint64(event.particleId);
        out["time"] = event.time;
        out["values"] = values;
        out["kind"] = static_cast<std::int64_t>(event.kind);
        out["width"] = static_cast<std::int64_t>(event.width);
        return out;
    }

    std::int64_t get_playback_generation() const
    {
        return Thread::is_main_thread() ? static_cast<std::int64_t>(playbackGeneration_) : -1;
    }

    String get_gpu_error() const { return Thread::is_main_thread() ? gpuError_ : String{}; }

    void set_gpu_spawn_per_second(double value)
    {
        if (Thread::is_main_thread() && std::isfinite(value) && value >= 0) {
            gpuSpawnPerSecond_ = value;
        }
    }

    double get_gpu_spawn_per_second() const { return Thread::is_main_thread() ? gpuSpawnPerSecond_ : 0; }

    PackedFloat64Array read_value(const String& nodeId)
    {
        auto values = PackedFloat64Array{};
        if (!is_playing()) {
            return values;
        }

        auto id = std::array<std::uint8_t, 16>{};
        if (!parse_node_id(nodeId, id)) {
            return values;
        }
        auto components = std::array<double, 4>{};
        auto width = std::uint8_t{};
        auto result = playback_->packageHost
            ? playback_->packageHost->rootValue(id.data(), components.data(), width)
            : playback_->session->read(playback_->instance, id.data(), components.data(), width);
        if (result == Result::Ok) {
            values.resize(width);
            for (auto index = 0u; index < width; ++index) {
                values.set(index, components[index]);
            }
        }
        return values;
    }

    Array get_game_io_ports() const
    {
        auto ports = Array{};
        if (!is_playing() || !playback_->packageHost) {
            return ports;
        }
        for (const auto& port : playback_->packageHost->package().gameIoPorts) {
            auto item = Dictionary{};
            item["name"] = String::utf8(port.name.c_str());
            item["kind"] = static_cast<std::int64_t>(port.kind);
            item["kind_name"] = ioKindName(port.kind);
            item["width"] = static_cast<std::int64_t>(port.width);
            item["is_color"] = port.isColor;
            ports.push_back(item);
        }
        return ports;
    }

    int set_game_property(const String& name, const PackedFloat64Array& values)
    {
        if (!Thread::is_main_thread() || !playback_ || !playback_->packageHost) {
            return code(Result::NotReady);
        }
        auto utf8Name = name.utf8();
        auto lanes = std::vector<double>{};
        lanes.reserve(static_cast<std::size_t>(values.size()));
        for (auto index = 0; index < values.size(); ++index) {
            lanes.push_back(values[index]);
        }
        return code(playback_->packageHost->setGameProperty(utf8Name.get_data(),
            lanes.data(), lanes.size()));
    }

    int fire_game_signal(const String& name)
    {
        if (!Thread::is_main_thread() || !playback_ || !playback_->packageHost) {
            return code(Result::NotReady);
        }
        if (dispatchingScriptAction_)
        {
            return code(Result::Busy);
        }
        auto utf8Name = name.utf8();
        return code(playback_->packageHost->fireGameSignal(utf8Name.get_data()));
    }

    bool bind_script_action(const String& signalName, const godot::Callable& handler, const String& resultProperty)
    {
        if (!Thread::is_main_thread() || !isScriptAction(signalName) ||
            !isScriptPortName(signalName) || !handler.is_valid() || handler.get_argument_count() != 1 ||
            (!resultProperty.is_empty() && !isScriptPortName(resultProperty)) || scriptActions_.has(signalName))
        {
            return false;
        }
        auto action = Array{};
        action.push_back(handler);
        action.push_back(resultProperty);
        scriptActions_[signalName] = action;
        return true;
    }

    bool unbind_script_action(const String& signalName)
    {
        return Thread::is_main_thread() && scriptActions_.erase(signalName);
    }

    int dispatch_signal(const String& nodeId)
    {
        if (!Thread::is_main_thread() || !playback_ || !playback_->packageHost) {
            return code(Result::NotReady);
        }
        auto id = std::array<std::uint8_t, 16>{};
        if (!parse_node_id(nodeId, id)) {
            return code(Result::InvalidArgument);
        }
        return code(playback_->packageHost->dispatchSignal(id.data()));
    }

    int set_owner_matrix(const PackedFloat64Array& values)
    {
        if (!Thread::is_main_thread() || !playback_ || !playback_->packageHost) {
            return code(Result::NotReady);
        }
        auto lanes = std::vector<double>{};
        lanes.reserve(static_cast<std::size_t>(values.size()));
        for (auto index = 0; index < values.size(); ++index) {
            lanes.push_back(values[index]);
        }
        return code(playback_->packageHost->setOwnerMatrix(lanes.data(), lanes.size()));
    }

    int set_mesh_binding(const String& identity, const Ref<Mesh>& mesh)
    {
        if (!Thread::is_main_thread()) {
            return code(Result::InvalidArgument);
        }
        if (playback_) {
            return code(Result::Busy);
        }
        if (!mesh.is_valid()) {
            return code(Result::InvalidArgument);
        }
        auto utf8Identity = identity.utf8();
        auto* identityData = utf8Identity.get_data();
        if (!identityData) {
            return code(Result::InvalidArgument);
        }
        auto key = std::string(identityData);
        if (key.empty() || key.size() > 512) {
            return code(Result::InvalidArgument);
        }
        meshBindings_[key] = mesh;
        return code(Result::Ok);
    }

    int clear_mesh_binding(const String& identity)
    {
        if (!Thread::is_main_thread()) {
            return code(Result::InvalidArgument);
        }
        if (playback_) {
            return code(Result::Busy);
        }
        auto utf8Identity = identity.utf8();
        auto* identityData = utf8Identity.get_data();
        if (!identityData) {
            return code(Result::InvalidArgument);
        }
        auto key = std::string(identityData);
        return meshBindings_.erase(key) ? code(Result::Ok) : code(Result::NodeNotFound);
    }

    int set_texture_binding(const String& identity, const Ref<Texture2D>& texture)
    {
        if (!Thread::is_main_thread()) {
            return code(Result::InvalidArgument);
        }
        if (playback_) {
            return code(Result::Busy);
        }
        if (!texture.is_valid()) {
            return code(Result::InvalidArgument);
        }
        auto utf8Identity = identity.utf8();
        auto* identityData = utf8Identity.get_data();
        if (!identityData) {
            return code(Result::InvalidArgument);
        }
        auto key = std::string(identityData);
        if (key.empty() || key.size() > 512) {
            return code(Result::InvalidArgument);
        }
        textureBindings_[key] = texture;
        return code(Result::Ok);
    }

    int clear_texture_binding(const String& identity)
    {
        if (!Thread::is_main_thread()) {
            return code(Result::InvalidArgument);
        }
        if (playback_) {
            return code(Result::Busy);
        }
        auto utf8Identity = identity.utf8();
        auto* identityData = utf8Identity.get_data();
        if (!identityData) {
            return code(Result::InvalidArgument);
        }
        auto key = std::string(identityData);
        return textureBindings_.erase(key) ? code(Result::Ok) : code(Result::NodeNotFound);
    }

    int set_material_binding(const String& identity, const Ref<godot::Material>& material)
    {
        if (!Thread::is_main_thread() || !material.is_valid()) {
            return code(Result::InvalidArgument);
        }
        if (playback_) {
            return code(Result::Busy);
        }
        auto utf8 = identity.utf8();
        if (utf8.length() == 0 || utf8.length() > 512) {
            return code(Result::InvalidArgument);
        }
        materialBindings_[std::string(utf8.get_data(), utf8.length())] = material;
        return code(Result::Ok);
    }

    int clear_material_binding(const String& identity)
    {
        if (!Thread::is_main_thread()) {
            return code(Result::InvalidArgument);
        }
        if (playback_) {
            return code(Result::Busy);
        }
        auto utf8 = identity.utf8();
        if (utf8.length() == 0 || utf8.length() > 512) {
            return code(Result::InvalidArgument);
        }
        return materialBindings_.erase(std::string(utf8.get_data(), utf8.length()))
            ? code(Result::Ok) : code(Result::NodeNotFound);
    }

    void set_camera_path(const NodePath& path)
    {
        if (Thread::is_main_thread()) {
            cameraPath_ = path;
        }
    }

    NodePath get_camera_path() const { return cameraPath_; }

    void _process(double delta) override
    {
        const auto cpuPlayback = playback_ && playback_->packageHost &&
            !playback_->packageHost->package().cpuPrograms.empty();
        const auto result = advance(delta);
        if (cpuPlayback && result != code(Result::Ok)) {
            godot::UtilityFunctions::push_error(
                String("Libra FX CPU package advance failed: ") + String::num_int64(result));
            set_process(false);
        }
    }
    void _exit_tree() override
    {
        reset_preview_stream();
        stop();
    }

protected:
    static void _bind_methods()
    {
        godot::ClassDB::bind_method(godot::D_METHOD("play", "bytecode", "duration"), &LibraEffectPlayer::play, -1.0);
        godot::ClassDB::bind_method(godot::D_METHOD("play_package", "package_bytes", "duration"), &LibraEffectPlayer::play_package, -1.0);
        godot::ClassDB::bind_method(godot::D_METHOD("advance", "delta"), &LibraEffectPlayer::advance);
        godot::ClassDB::bind_method(godot::D_METHOD("stop"), &LibraEffectPlayer::stop);
        godot::ClassDB::bind_method(godot::D_METHOD("is_playing"), &LibraEffectPlayer::is_playing);
        godot::ClassDB::bind_method(godot::D_METHOD("pop_cpu_particle_event"), &LibraEffectPlayer::pop_cpu_particle_event);
        godot::ClassDB::bind_method(godot::D_METHOD("read_value", "node_id"), &LibraEffectPlayer::read_value);
        godot::ClassDB::bind_method(godot::D_METHOD("get_game_io_ports"), &LibraEffectPlayer::get_game_io_ports);
        godot::ClassDB::bind_method(godot::D_METHOD("set_game_property", "name", "values"), &LibraEffectPlayer::set_game_property);
        godot::ClassDB::bind_method(godot::D_METHOD("fire_game_signal", "name"), &LibraEffectPlayer::fire_game_signal);
        godot::ClassDB::bind_method(godot::D_METHOD("bind_script_action", "signal_name", "handler", "result_property"),
            &LibraEffectPlayer::bind_script_action, String{});
        godot::ClassDB::bind_method(godot::D_METHOD("unbind_script_action", "signal_name"),
            &LibraEffectPlayer::unbind_script_action);
        godot::ClassDB::bind_method(godot::D_METHOD("dispatch_signal", "node_id"), &LibraEffectPlayer::dispatch_signal);
        godot::ClassDB::bind_method(godot::D_METHOD("set_owner_matrix", "values"), &LibraEffectPlayer::set_owner_matrix);
        godot::ClassDB::bind_method(godot::D_METHOD("set_mesh_binding", "asset_identity", "mesh"), &LibraEffectPlayer::set_mesh_binding);
        godot::ClassDB::bind_method(godot::D_METHOD("clear_mesh_binding", "asset_identity"), &LibraEffectPlayer::clear_mesh_binding);
        godot::ClassDB::bind_method(godot::D_METHOD("set_texture_binding", "asset_identity", "texture"), &LibraEffectPlayer::set_texture_binding);
        godot::ClassDB::bind_method(godot::D_METHOD("set_material_binding", "asset_identity", "material"), &LibraEffectPlayer::set_material_binding);
        godot::ClassDB::bind_method(godot::D_METHOD("clear_material_binding", "asset_identity"), &LibraEffectPlayer::clear_material_binding);
        godot::ClassDB::bind_method(godot::D_METHOD("get_gpu_error"), &LibraEffectPlayer::get_gpu_error);
        godot::ClassDB::bind_method(godot::D_METHOD("get_playback_generation"),
            &LibraEffectPlayer::get_playback_generation);
        godot::ClassDB::bind_method(godot::D_METHOD("set_gpu_spawn_per_second", "value"),
            &LibraEffectPlayer::set_gpu_spawn_per_second);
        godot::ClassDB::bind_method(godot::D_METHOD("get_gpu_spawn_per_second"),
            &LibraEffectPlayer::get_gpu_spawn_per_second);
        ADD_PROPERTY(godot::PropertyInfo(godot::Variant::FLOAT, "gpu_spawn_per_second"),
            "set_gpu_spawn_per_second", "get_gpu_spawn_per_second");
        godot::ClassDB::bind_method(godot::D_METHOD("apply_preview_frame", "bytes"), &LibraEffectPlayer::apply_preview_frame);
        godot::ClassDB::bind_method(godot::D_METHOD("reset_preview_stream"), &LibraEffectPlayer::reset_preview_stream);
        godot::ClassDB::bind_method(godot::D_METHOD("get_preview_error"), &LibraEffectPlayer::get_preview_error);
        godot::ClassDB::bind_method(godot::D_METHOD("clear_texture_binding", "asset_identity"), &LibraEffectPlayer::clear_texture_binding);
        godot::ClassDB::bind_method(godot::D_METHOD("set_camera_path", "path"), &LibraEffectPlayer::set_camera_path);
        godot::ClassDB::bind_method(godot::D_METHOD("get_camera_path"), &LibraEffectPlayer::get_camera_path);
        ADD_PROPERTY(godot::PropertyInfo(godot::Variant::NODE_PATH, "camera_path"),
            "set_camera_path", "get_camera_path");
        ADD_SIGNAL(godot::MethodInfo("game_io_event",
            godot::PropertyInfo(godot::Variant::DICTIONARY, "event")));
        ADD_SIGNAL(godot::MethodInfo("cpu_particle_events_ready"));
    }

private:
    static int code(Result result) { return static_cast<int>(result); }

    static String ioKindName(std::uint8_t kind)
    {
        switch (static_cast<NamedIoKind>(kind)) {
        case NamedIoKind::InputProperty: return "input_property";
        case NamedIoKind::InputSignal: return "input_signal";
        case NamedIoKind::OutputProperty: return "output_property";
        case NamedIoKind::OutputSignal: return "output_signal";
        case NamedIoKind::OwnerMatrix: return "owner_matrix";
        }
        return "unknown";
    }

    Result updateCurrentCamera(SpawnableHost& host) const
    {
        const auto& ports = host.package().gameIoPorts;
        auto cameraPort = ports.end();
        for (auto item = ports.begin(); item != ports.end(); ++item) {
            if (item->name == "__LFX_CAMERA_V1") {
                cameraPort = item;
                break;
            }
        }
        if (cameraPort == ports.end()) {
            return Result::Ok;
        }
        if (cameraPort->kind != static_cast<std::uint8_t>(NamedIoKind::InputProperty) ||
            cameraPort->width != 4) {
            return Result::InvalidBinary;
        }

        auto position = std::array<double, 4>{0, 0, 0, 0};
        auto camera = static_cast<Camera3D*>(nullptr);
        if (!cameraPath_.is_empty()) {
            auto* target = get_node_or_null(cameraPath_);
            camera = godot::Object::cast_to<Camera3D>(target);
        }
        if (!camera) {
            auto* viewport = get_viewport();
            if (viewport) {
                camera = viewport->get_camera_3d();
            }
        }
        if (camera) {
            auto worldPosition = camera->get_global_position();
            position = {worldPosition.x, worldPosition.y, worldPosition.z, 1};
        }
        return host.setGameProperty("__LFX_CAMERA_V1", position.data(), position.size());
    }

    static String uuidString(const std::uint8_t* bytes)
    {
        static constexpr char digits[] = "0123456789abcdef";
        auto text = std::array<char, 37>{};
        auto output = std::size_t{};
        for (auto index = 0u; index < 16; ++index) {
            if (index == 4 || index == 6 || index == 8 || index == 10) {
                text[output++] = '-';
            }
            text[output++] = digits[bytes[index] >> 4];
            text[output++] = digits[bytes[index] & 0x0f];
        }
        return String::utf8(text.data());
    }

    static bool isScriptAction(const String& name)
    {
        return name.length() > 7 && name.begins_with("action.");
    }

    static bool isScriptPortName(const String& name)
    {
        if (name.is_empty() || name[0] == U' ' || name[name.length() - 1] == U' ' ||
            name.utf8().length() > 128)
        {
            return false;
        }
        for (auto index = std::int64_t{}; index < name.length(); ++index)
        {
            const auto character = name[index];
            if (character < 0x20 || (character >= 0x7f && character <= 0x9f))
            {
                return false;
            }
        }
        return !name.strip_edges().is_empty();
    }

    void invokeScriptAction(const String& name, const Dictionary& item, std::uint64_t generation)
    {
        if (!scriptActions_.has(name))
        {
            godot::UtilityFunctions::push_warning(String("No LIBERA FX script action is bound to ") + name);
            return;
        }
        const auto action = static_cast<Array>(scriptActions_[name]);
        const auto handler = static_cast<godot::Callable>(action[0]);
        const auto resultProperty = static_cast<String>(action[1]);
        if (!handler.is_valid())
        {
            godot::UtilityFunctions::push_warning(String("Invalid LIBERA FX script action: ") + name);
            return;
        }
        dispatchingScriptAction_ = true;
        const auto result = handler.call(item.duplicate(true));
        dispatchingScriptAction_ = false;
        if (generation != playbackGeneration_ || resultProperty.is_empty())
        {
            return;
        }
        if (result.get_type() != godot::Variant::PACKED_FLOAT64_ARRAY ||
            set_game_property(resultProperty, static_cast<PackedFloat64Array>(result)) != code(Result::Ok))
        {
            godot::UtilityFunctions::push_warning(String("Invalid LIBERA FX script action result: ") + name);
        }
    }

    void drainGameIoEvents()
    {
        if (!playback_ || !playback_->packageHost) {
            return;
        }
        auto* activeHost = playback_->packageHost.get();
        auto generation = playbackGeneration_;
        auto event = NamedIoEvent{};
        const auto& ports = activeHost->package().gameIoPorts;
        while (activeHost->popGameIoEvent(event)) {
            auto portName = String{};
            auto scoped = event.identity.instanceId != 0;
            for (auto index = 0u; index < 16; ++index) {
                scoped = scoped || event.identity.containerId[index] != 0;
            }
            if (scoped) {
                auto* scopedName = static_cast<const char*>(nullptr);
                // Never reinterpret a child scope's port index as a root index.
                if (activeHost->instancePortName(event.identity.containerId,
                    event.portIndex, scopedName) != Result::Ok || !scopedName) {
                    continue;
                }
                portName = String::utf8(scopedName);
            } else {
                if (event.portIndex >= ports.size()) {
                    continue;
                }
                portName = String::utf8(ports[event.portIndex].name.c_str());
            }
            auto item = Dictionary{};
            item["name"] = portName;
            item["kind"] = static_cast<std::int64_t>(event.kind);
            item["kind_name"] = ioKindName(static_cast<std::uint8_t>(event.kind));
            item["container_id"] = uuidString(event.identity.containerId);
            item["instance_id"] = String::num_uint64(event.identity.instanceId);
            item["is_color"] = event.isColor;
            auto values = PackedFloat64Array{};
            values.resize(event.width);
            for (auto index = 0u; index < event.width; ++index) {
                values.set(index, event.values[index]);
            }
            item["values"] = values;
            if (!scoped && event.kind == NamedIoKind::OutputSignal && isScriptAction(portName))
            {
                invokeScriptAction(portName, item, generation);
                if (generation != playbackGeneration_)
                {
                    return;
                }
            }
            emit_signal("game_io_event", item);
            if (generation != playbackGeneration_ || !playback_ ||
                playback_->packageHost.get() != activeHost) {
                return;
            }
        }
    }

    static bool parse_node_id(const String& text, std::array<std::uint8_t, 16>& id)
    {
        if (text.length() != 36) {
            return false;
        }
        auto digit = 0u;
        for (auto index = 0; index < 36; ++index) {
            auto character = text[index];
            if (index == 8 || index == 13 || index == 18 || index == 23) {
                if (character != '-') {
                    return false;
                }
                continue;
            }
            auto value = character >= '0' && character <= '9' ? character - '0' :
                character >= 'a' && character <= 'f' ? character - 'a' + 10 :
                character >= 'A' && character <= 'F' ? character - 'A' + 10 : 16;
            if (value >= 16) {
                return false;
            }
            id[digit / 2] |= static_cast<std::uint8_t>(value << (digit % 2 == 0 ? 4 : 0));
            ++digit;
        }
        return true;
    }

    std::unique_ptr<Playback> playback_;
    std::unique_ptr<SpawnableHost> completedCpuEventHost_;
    Dictionary scriptActions_;
    bool dispatchingScriptAction_ = false;
    MeshBindings meshBindings_;
    TextureBindings textureBindings_;
    MaterialBindings materialBindings_;
    String gpuError_;
    livepreview::SequenceCursor previewSequence_;
    PackedByteArray previewBytecode_;
    String previewError_;
    std::uint64_t previewPlaybackGeneration_ = 0;
    NodePath cameraPath_;
    double gpuSpawnPerSecond_ = 100.0;
    std::uint64_t playbackGeneration_ = 0;
};

void initialize(godot::ModuleInitializationLevel level)
{
    if (level == godot::MODULE_INITIALIZATION_LEVEL_SCENE) {
        godot::ClassDB::register_class<LibraEffectPlayer>();
        registerGodotDebugPlayback();
    }
}

void uninitialize(godot::ModuleInitializationLevel) {}

}

extern "C" GDExtensionBool GDE_EXPORT liberafx_godot_init(
    GDExtensionInterfaceGetProcAddress getProcAddress, GDExtensionClassLibraryPtr library,
    GDExtensionInitialization* initialization)
{
    auto init = godot::GDExtensionBinding::InitObject(getProcAddress, library, initialization);
    init.register_initializer(liberafx::godot_adapter::initialize);
    init.register_terminator(liberafx::godot_adapter::uninitialize);
    init.set_minimum_library_initialization_level(godot::MODULE_INITIALIZATION_LEVEL_SCENE);
    return init.init();
}
