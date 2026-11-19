#pragma once

#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/classes/thread.hpp>
#include <godot_cpp/core/object.hpp>
#include <godot_cpp/variant/utility_functions.hpp>
#include <godot_cpp/variant/rid.hpp>

namespace liberafx::godot_adapter {

class OwnedRenderingRid final {
public:
    OwnedRenderingRid() = default;
    explicit OwnedRenderingRid(godot::RID rid) : rid_(rid) {}
    OwnedRenderingRid(const OwnedRenderingRid&) = delete;
    auto operator=(const OwnedRenderingRid&) -> OwnedRenderingRid& = delete;

    OwnedRenderingRid(OwnedRenderingRid&& other) noexcept : rid_(other.release()) {}

    auto operator=(OwnedRenderingRid&& other) noexcept -> OwnedRenderingRid&
    {
        if (this != &other) {
            reset();
            rid_ = other.release();
        }
        return *this;
    }

    ~OwnedRenderingRid()
    {
        reset();
    }

    auto get() const -> const godot::RID&
    {
        return rid_;
    }

    auto isValid() const -> bool
    {
        return rid_.is_valid();
    }

    void reset() noexcept
    {
        if (!rid_.is_valid()) {
            return;
        }
        if (!godot::Thread::is_main_thread()) {
            godot::UtilityFunctions::push_error(
                "RenderingServer RIDs must be released on the Godot main thread.");
            rid_ = godot::RID{};
            return;
        }
        auto* server = godot::RenderingServer::get_singleton();
        if (server) {
            server->free_rid(rid_);
        }
        rid_ = godot::RID{};
    }

private:
    auto release() noexcept -> godot::RID
    {
        auto rid = rid_;
        rid_ = godot::RID{};
        return rid;
    }

    godot::RID rid_;
};

}
