#pragma once

#include "liberafx/LbInstanceSession.h"
#include <cassert>
#include <cstring>
#include <memory>
#include <new>
#include <thread>
#include <unordered_map>
#include <vector>

namespace liberafx::host {

// Optional HOST convenience layer. Allocations here belong to the caller of
// InstanceSession; the portable execution/queue kernel still borrows its memory.
// No engine objects, SDK callbacks, transport or rendering are provided.
struct ProgramSessionOptions final {
    std::size_t instanceCapacity = 32, requestCapacity = 64;
    std::size_t maxProgramBytes = 16 * 1024 * 1024;
    std::size_t maxOwnedBytes = 64 * 1024 * 1024;
};
struct ProgramCompletion final {
    RequestId request{};
    InstanceId instance{};
    InstanceRequestKind kind{};
    Result result = Result::NotReady;
};

class ProgramSession final {
    struct AlignedMemory final {
        void* data = nullptr;
        std::size_t alignment = 16;
        ~AlignedMemory() { if (data) ::operator delete(data, std::align_val_t{alignment}); }
        void allocate(std::size_t bytes, std::size_t requested)
        {
            alignment = requested < 16 ? 16 : requested;
            if (bytes) data = ::operator new(bytes, std::align_val_t{alignment});
        }
    };
    struct Resource final {
        std::vector<std::uint8_t> bytes;
        Program program;
        AlignedMemory work, defaults;
        std::unique_ptr<LifecycleSessionEffect> effect;
        std::size_t charge = 0;
    };
public:
    static Result create(SessionId id, const ProgramSessionOptions& options,
                         std::unique_ptr<ProgramSession>& out)
    {
        if (out) return Result::Busy; // Never replace/destruct an open host implicitly.
        if (!(id.high || id.low) || !options.instanceCapacity || options.instanceCapacity > 4096 ||
            !options.requestCapacity || options.requestCapacity > 8192 ||
            options.maxProgramBytes < 12 || options.maxProgramBytes > 16 * 1024 * 1024 ||
            !options.maxOwnedBytes || options.maxOwnedBytes > 1024ull * 1024 * 1024)
            return Result::InvalidArgument;
        auto candidate = std::unique_ptr<ProgramSession>(new ProgramSession(id, options));
        if (!candidate->session_->incarnation()) return Result::CounterOverflow;
        out = std::move(candidate);
        return Result::Ok;
    }
    ~ProgramSession() = default; // Explicit successful close is required.
    ProgramSession(const ProgramSession&) = delete;
    ProgramSession& operator=(const ProgramSession&) = delete;

    SessionId id() const noexcept { return id_; }
    std::uint64_t incarnation() const noexcept { return session_->incarnation(); }
    bool isOwnerThread() const noexcept { return std::this_thread::get_id() == owner_; }

    // Worker-safe. Copies bytecode and allocates bounded HOST work/defaults.
    // Program validation is pure; Instance initialization/evaluation/cleanup
    // happen ONLY in owner process(). No caller byte pointer is retained.
    Result submitProgram(InstanceRequestKind kind, InstanceId target,
                         const std::uint8_t* bytes, std::size_t byteCount,
                         Lifetime lifetime, ReplacementPolicy policy, RequestId& out)
    {
        out = {};
        if (kind != InstanceRequestKind::Create && kind != InstanceRequestKind::Replace)
            return Result::InvalidArgument;
        if (!bytes || byteCount < 12 || byteCount > options_.maxProgramBytes)
            return Result::InvalidArgument;
        if (std::memcmp(bytes, "LFXB", 4) != 0) return Result::InvalidBinary;
        // This contract is deliberately pure v1/v2; never omit custom state,
        // children, scripts, logical events, assets or engine render operations.
        auto version = std::uint16_t(bytes[4]) | (std::uint16_t(bytes[5]) << 8);
        if (version != 1 && version != 2) return Result::UnsupportedBinary;
        auto count = std::uint32_t{};
        for (auto index = 0u; index < 4; ++index)
            count |= std::uint32_t(bytes[8 + index]) << (index * 8);
        if (count > 10000) return Result::InvalidBinary;
        auto expected = (version == 1 ? std::size_t{12} : std::size_t{16}) +
            std::size_t{count} * (version == 1 ? 44 : 96);
        if (byteCount != expected) return Result::InvalidBinary;

        auto lock = std::lock_guard<std::mutex>(resourcesMutex_);
        // Credits cover live effects plus accepted/unconsumed candidates.
        if (resources_.size() >= options_.instanceCapacity + options_.requestCapacity)
            return Result::QueueRejected;
        if (byteCount > options_.maxOwnedBytes - ownedBytes_) return Result::QueueRejected;
        auto resource = std::make_unique<Resource>();
        resource->bytes.assign(bytes, bytes + byteCount);
        auto result = Program::load(resource->bytes.data(), resource->bytes.size(), resource->program);
        if (result != Result::Ok) return result;
        auto workBytes = resource->program.workBytes();
        auto defaultsCount = resource->program.parameterValueCount();
        auto defaultsBytes = defaultsCount * sizeof(double); // <= 10000 * 8 lanes * 8.
        auto charge = byteCount + workBytes + defaultsBytes + sizeof(Resource) + sizeof(LifecycleSessionEffect);
        if (charge > options_.maxOwnedBytes - ownedBytes_) return Result::QueueRejected;
        resource->charge = charge;
        resource->work.allocate(workBytes, resource->program.workAlignment());
        resource->defaults.allocate(defaultsBytes, resource->program.defaultsAlignment());
        resource->effect = std::make_unique<LifecycleSessionEffect>(resource->program,
            static_cast<double*>(resource->work.data), workBytes / sizeof(double),
            static_cast<double*>(resource->defaults.data), defaultsCount, lifetime);
        auto* effect = resource->effect.get();
        resources_.emplace(effect, std::move(resource));
        result = session_->submit({kind, target, effect, policy}, out);
        if (result == Result::Ok) ownedBytes_ += charge;
        else resources_.erase(effect);
        return result;
    }
    Result submitControl(InstanceRequestKind kind, InstanceId target, RequestId& out) noexcept
    {
        if (kind != InstanceRequestKind::NaturalEnd && kind != InstanceRequestKind::Destroy) {
            out = {};
            return Result::InvalidArgument;
        }
        return session_->submit({kind, target}, out);
    }
    Result takeCompletion(RequestId request, ProgramCompletion& out) noexcept
    {
        auto completion = InstanceCompletion{};
        auto result = session_->takeCompletion(request, completion);
        if (result != Result::Ok) return result;
        out = {completion.request, completion.instance, completion.kind, completion.result};
        if (completion.returnedEffect) {
            auto lock = std::lock_guard<std::mutex>(resourcesMutex_);
            auto found = resources_.find(completion.returnedEffect);
            assert(found != resources_.end());
            if (found != resources_.end()) {
                ownedBytes_ -= found->second->charge;
                resources_.erase(found); // Core state cleanup already ran on owner.
            }
        }
        return Result::Ok;
    }
    Result process(std::size_t limit, std::size_t& processed) noexcept
    {
        processed = 0;
        return isOwnerThread() ? session_->process(limit, processed) : Result::InvalidArgument;
    }
    Result advance(InstanceId id, double time, double delta, bool& finished) noexcept
    {
        return isOwnerThread() ? session_->advance(id, time, delta, finished) : Result::InvalidArgument;
    }
    Result read(InstanceId id, const std::uint8_t nodeId[16], double out[4], std::uint8_t& width) noexcept
    {
        return isOwnerThread() ? session_->read(id, nodeId, out, width) : Result::InvalidArgument;
    }
    Result close() noexcept
    {
        if (!isOwnerThread()) return Result::InvalidArgument;
        // A consumer may already have removed the Core completion but still be
        // erasing its returned host resource. Never release during that window.
        auto lock = std::lock_guard<std::mutex>(resourcesMutex_);
        auto result = session_->close();
        if (result == Result::Ok && !resources_.empty()) return Result::NotReady;
        return result;
    }
private:
    ProgramSession(SessionId id, ProgramSessionOptions options)
        : id_(id), options_(options), owner_(std::this_thread::get_id()),
          slots_(options.instanceCapacity), requests_(options.requestCapacity)
    {
        session_ = std::make_unique<InstanceSession>(id, slots_.data(), slots_.size(),
            requests_.data(), requests_.size());
    }
    SessionId id_;
    ProgramSessionOptions options_;
    std::thread::id owner_;
    std::vector<InstanceSlot> slots_;
    std::vector<InstanceRequestStorage> requests_;
    std::mutex resourcesMutex_;
    std::unordered_map<SessionEffect*, std::unique_ptr<Resource>> resources_;
    std::size_t ownedBytes_ = 0;
    std::unique_ptr<InstanceSession> session_; // Destroy kernel before caller memory.
};
}
