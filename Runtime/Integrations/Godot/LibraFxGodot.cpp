#include "liberafx/host/LbProgramSession.h"
#include <godot_cpp/classes/node.hpp>
#include <godot_cpp/classes/thread.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/godot.hpp>
#include <godot_cpp/variant/packed_byte_array.hpp>
#include <godot_cpp/variant/packed_float64_array.hpp>
#include <array>
#include <cmath>
#include <memory>

namespace liberafx::godot_adapter {

using godot::Node;
using godot::PackedByteArray;
using godot::PackedFloat64Array;
using godot::String;
using godot::Thread;
using host::ProgramCompletion;
using host::ProgramSession;
using host::ProgramSessionOptions;

class LibraEffectPlayer final : public Node {
    GDCLASS(LibraEffectPlayer, Node)

    struct Playback final {
        std::unique_ptr<ProgramSession> session;
        InstanceId instance{};
        bool active = false;
        double elapsed = 0;

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
        if (result == Result::Ok) {
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
        if (!playback_) {
            return code(Result::NotReady);
        }
        auto elapsed = playback_->elapsed + delta;
        if (!std::isfinite(elapsed)) {
            return code(Result::InvalidArgument);
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
            playback_.reset();
            set_process(false);
        }
    }

    bool is_playing() const { return Thread::is_main_thread() && playback_ != nullptr; }

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
        if (playback_->session->read(playback_->instance, id.data(), components.data(), width) == Result::Ok) {
            values.resize(width);
            for (auto index = 0u; index < width; ++index) {
                values.set(index, components[index]);
            }
        }
        return values;
    }

    void _process(double delta) override { advance(delta); }
    void _exit_tree() override { stop(); }

protected:
    static void _bind_methods()
    {
        godot::ClassDB::bind_method(godot::D_METHOD("play", "bytecode", "duration"), &LibraEffectPlayer::play, -1.0);
        godot::ClassDB::bind_method(godot::D_METHOD("advance", "delta"), &LibraEffectPlayer::advance);
        godot::ClassDB::bind_method(godot::D_METHOD("stop"), &LibraEffectPlayer::stop);
        godot::ClassDB::bind_method(godot::D_METHOD("is_playing"), &LibraEffectPlayer::is_playing);
        godot::ClassDB::bind_method(godot::D_METHOD("read_value", "node_id"), &LibraEffectPlayer::read_value);
    }

private:
    static int code(Result result) { return static_cast<int>(result); }

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
};

void initialize(godot::ModuleInitializationLevel level)
{
    if (level == godot::MODULE_INITIALIZATION_LEVEL_SCENE) {
        godot::ClassDB::register_class<LibraEffectPlayer>();
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
