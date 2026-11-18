// Native C ABI target defines LIBRA_INTEROP_BUILD, exactly as for LbInterop.cpp.
// Unreal's automatic Core wrappers do not: keep this TU empty there; its
// owner-thread facade uses the C++ host service, not a second exported C DLL.
#if defined(LIBRA_INTEROP_BUILD)
#include "liberafx/LbInteropSession.h"
#include "liberafx/host/LbProgramSession.h"
#include "liberafx/host/LbLivePackage.h"
#include <array>
#include <mutex>
#include <unordered_map>

namespace {
using liberafx::host::ProgramSession;
static_assert(static_cast<unsigned>(liberafx::InstanceRequestKind::Create) == LIBRA_SESSION_CREATE);
static_assert(static_cast<unsigned>(liberafx::InstanceRequestKind::NaturalEnd) == LIBRA_SESSION_NATURAL_END);
static_assert(static_cast<unsigned>(liberafx::InstanceRequestKind::Destroy) == LIBRA_SESSION_DESTROY);
static_assert(static_cast<unsigned>(liberafx::InstanceRequestKind::Replace) == LIBRA_SESSION_REPLACE);
static_assert(static_cast<unsigned>(liberafx::ReplacementPolicy::Restart) == LIBRA_SESSION_RESTART);
auto sessionsMutex = std::mutex{};
auto sessions = std::unordered_map<std::uint64_t, std::shared_ptr<ProgramSession>>{};

auto findSession(LibraSessionId id) -> std::shared_ptr<ProgramSession>
{
    auto lock = std::lock_guard<std::mutex>(sessionsMutex);
    auto found = sessions.find(id.incarnation);
    if (found == sessions.end()) return {};
    auto nonce = found->second->id();
    return nonce.high == id.high && nonce.low == id.low ? found->second : nullptr;
}
template<class Function>
auto guard(Function function) noexcept -> LibraResult
{
    try { return function(); }
    catch (const std::bad_alloc&) { return LIBRA_OUT_OF_MEMORY; }
    catch (...) { return LIBRA_INTERNAL_ERROR; }
}
auto convert(LibraSessionInstanceId id) noexcept -> liberafx::InstanceId
{
    return {{id.session.high, id.session.low}, id.session.incarnation, id.generation, id.slot};
}
auto convert(LibraSessionRequestId id) noexcept -> liberafx::RequestId
{
    return {{id.session.high, id.session.low}, id.session.incarnation, id.sequence};
}
auto convert(liberafx::RequestId id) noexcept -> LibraSessionRequestId
{
    return {{id.session.high, id.session.low, id.incarnation}, id.sequence};
}
auto convert(liberafx::InstanceId id) noexcept -> LibraSessionInstanceId
{
    return {{id.session.high, id.session.low, id.incarnation}, id.generation, id.slot};
}
template<class Function>
auto withSession(LibraSessionId id, Function function) noexcept -> LibraResult
{
    return guard([&]() -> LibraResult {
        auto session = findSession(id);
        return session ? static_cast<LibraResult>(function(*session)) : LIBRA_INVALID_HANDLE;
    });
}
}

uint32_t LIBRA_INTEROP_CALL libra_session_abi_version(void) { return 1; }
LibraResult LIBRA_INTEROP_CALL libra_session_validate_live_restart(const uint8_t* bytes, uint64_t byteCount)
{
    if (!bytes || byteCount > 16 * 1024 * 1024 || byteCount > SIZE_MAX) return LIBRA_INVALID_ARGUMENT;
    return static_cast<LibraResult>(liberafx::host::validateLiveRestart(bytes, static_cast<std::size_t>(byteCount)));
}

LibraResult LIBRA_INTEROP_CALL libra_session_create(const LibraSessionOptions* options, LibraSessionId* outSession)
{
    if (!outSession) return LIBRA_INVALID_ARGUMENT;
    *outSession = {};
    if (!options || options->structSize != sizeof(LibraSessionOptions) ||
        options->flags != LIBRA_SESSION_DEVELOPMENT_OPT_IN ||
        options->maxProgramBytes > SIZE_MAX || options->maxOwnedBytes > SIZE_MAX)
        return LIBRA_INVALID_ARGUMENT;
    return guard([&]() -> LibraResult {
        auto nativeOptions = liberafx::host::ProgramSessionOptions{};
        nativeOptions.instanceCapacity = options->instanceCapacity;
        nativeOptions.requestCapacity = options->requestCapacity;
        nativeOptions.maxProgramBytes = static_cast<std::size_t>(options->maxProgramBytes);
        nativeOptions.maxOwnedBytes = static_cast<std::size_t>(options->maxOwnedBytes);
        auto lock = std::lock_guard<std::mutex>(sessionsMutex);
        if (sessions.size() >= 128) return LIBRA_QUEUE_REJECTED;
        auto candidate = std::unique_ptr<ProgramSession>{};
        auto result = ProgramSession::create({options->nonceHigh, options->nonceLow}, nativeOptions, candidate);
        if (result != liberafx::Result::Ok) return static_cast<LibraResult>(result);
        auto id = LibraSessionId{options->nonceHigh, options->nonceLow, candidate->incarnation()};
        // Allocate registry node before transferring an open session into it:
        // allocation failure must not destruct an unclosed Core session.
        try {
            auto retained = std::shared_ptr<ProgramSession>(std::move(candidate));
            try { sessions.emplace(id.incarnation, retained); }
            catch (...) { (void)retained->close(); throw; }
        } catch (...) {
            if (candidate) (void)candidate->close();
            throw;
        }
        *outSession = id;
        return LIBRA_OK;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_session_submit_program(
    LibraSessionId session, uint32_t kind, LibraSessionInstanceId target,
    const uint8_t* bytes, uint64_t byteCount, double duration, uint32_t infinite,
    uint32_t policy, LibraSessionRequestId* outRequest)
{
    if (!outRequest) return LIBRA_INVALID_ARGUMENT;
    *outRequest = {};
    if ((kind != LIBRA_SESSION_CREATE && kind != LIBRA_SESSION_REPLACE) ||
        infinite > 1 || policy > LIBRA_SESSION_RESTART || byteCount > SIZE_MAX)
        return LIBRA_INVALID_ARGUMENT;
    auto lifetime = liberafx::Lifetime::infinite();
    if (!infinite && liberafx::Lifetime::finite(duration, lifetime) != liberafx::Result::Ok)
        return LIBRA_INVALID_ARGUMENT;
    return withSession(session, [&](ProgramSession& host) {
        auto request = liberafx::RequestId{};
        auto result = host.submitProgram(static_cast<liberafx::InstanceRequestKind>(kind), convert(target),
            bytes, static_cast<std::size_t>(byteCount), lifetime,
            static_cast<liberafx::ReplacementPolicy>(policy), request);
        if (result == liberafx::Result::Ok) *outRequest = convert(request);
        return result;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_session_submit_control(LibraSessionId session, uint32_t kind,
    LibraSessionInstanceId target, LibraSessionRequestId* outRequest)
{
    if (!outRequest) return LIBRA_INVALID_ARGUMENT;
    *outRequest = {};
    if (kind != LIBRA_SESSION_NATURAL_END && kind != LIBRA_SESSION_DESTROY) return LIBRA_INVALID_ARGUMENT;
    return withSession(session, [&](ProgramSession& host) {
        auto request = liberafx::RequestId{};
        auto result = host.submitControl(static_cast<liberafx::InstanceRequestKind>(kind), convert(target), request);
        if (result == liberafx::Result::Ok) *outRequest = convert(request);
        return result;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_session_take_completion(LibraSessionId session,
    LibraSessionRequestId request, LibraSessionCompletion* outCompletion)
{
    if (!outCompletion) return LIBRA_INVALID_ARGUMENT;
    *outCompletion = {};
    return withSession(session, [&](ProgramSession& host) {
        auto completion = liberafx::host::ProgramCompletion{};
        auto result = host.takeCompletion(convert(request), completion);
        if (result == liberafx::Result::Ok) *outCompletion = {convert(completion.request),
            convert(completion.instance), static_cast<std::uint32_t>(completion.kind),
            static_cast<LibraResult>(completion.result)};
        return result;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_session_process(LibraSessionId session, uint32_t limit, uint32_t* outProcessed)
{
    if (!outProcessed) return LIBRA_INVALID_ARGUMENT;
    *outProcessed = 0;
    return withSession(session, [&](ProgramSession& host) {
        auto processed = std::size_t{};
        auto result = host.process(limit, processed);
        *outProcessed = static_cast<std::uint32_t>(processed);
        return result;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_session_advance(LibraSessionId session, LibraSessionInstanceId instance,
    double elapsed, double frameDelta, uint32_t* outFinished)
{
    if (!outFinished) return LIBRA_INVALID_ARGUMENT;
    *outFinished = 0;
    return withSession(session, [&](ProgramSession& host) {
        auto finished = false;
        auto result = host.advance(convert(instance), elapsed, frameDelta, finished);
        if (result == liberafx::Result::Ok) *outFinished = finished ? 1u : 0u;
        return result;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_session_read_value(LibraSessionId session,
    LibraSessionInstanceId instance, const uint8_t nodeId[16], LibraValue* outValue)
{
    if (!nodeId || !outValue) return LIBRA_INVALID_ARGUMENT;
    *outValue = {};
    return withSession(session, [&](ProgramSession& host) {
        auto values = std::array<double, 4>{};
        auto width = std::uint8_t{};
        auto result = host.read(convert(instance), nodeId, values.data(), width);
        if (result == liberafx::Result::Ok) *outValue = {values[0], values[1], values[2], values[3], width, 0};
        return result;
    });
}
LibraResult LIBRA_INTEROP_CALL libra_session_close(LibraSessionId session)
{
    return withSession(session, [](ProgramSession& host) { return host.close(); });
}
LibraResult LIBRA_INTEROP_CALL libra_session_release(LibraSessionId id)
{
    return guard([&]() -> LibraResult {
        auto host = findSession(id);
        if (!host) return LIBRA_INVALID_HANDLE;
        auto result = host->close();
        if (result != liberafx::Result::Ok) return static_cast<LibraResult>(result);
        auto lock = std::lock_guard<std::mutex>(sessionsMutex);
        sessions.erase(id.incarnation);
        return LIBRA_OK;
    });
}
#endif
