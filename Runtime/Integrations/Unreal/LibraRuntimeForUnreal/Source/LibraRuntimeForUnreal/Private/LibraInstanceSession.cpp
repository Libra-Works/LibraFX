#include "LibraInstanceSession.h"
#include "Misc/ScopeLock.h"
#include "liberafx/host/ProgramSession.h"

struct FLibraInstanceSession::FImpl final
{
    explicit FImpl(std::unique_ptr<liberafx::host::ProgramSession> value) : host(std::move(value)) {}
    std::unique_ptr<liberafx::host::ProgramSession> host;
};

auto FLibraInstanceSession::Create(bool developmentOptIn, liberafx::Result& outResult,
    uint32 instanceCapacity, uint32 requestCapacity, uint64 maxOwnedBytes) -> TUniquePtr<FLibraInstanceSession>
{
    outResult = liberafx::Result::InvalidArgument;
    if (!IsInGameThread() || !developmentOptIn || maxOwnedBytes > SIZE_MAX) return {};
    auto options = liberafx::host::ProgramSessionOptions{};
    options.instanceCapacity = instanceCapacity;
    options.requestCapacity = requestCapacity;
    options.maxOwnedBytes = static_cast<std::size_t>(maxOwnedBytes);
    auto nonce = FGuid::NewGuid();
    auto id = liberafx::SessionId{(uint64{nonce.A} << 32) | nonce.B, (uint64{nonce.C} << 32) | nonce.D};
    auto host = std::unique_ptr<liberafx::host::ProgramSession>{};
    outResult = liberafx::host::ProgramSession::create(id, options, host);
    if (outResult != liberafx::Result::Ok) return {};
    auto result = TUniquePtr<FLibraInstanceSession>(new FLibraInstanceSession);
    result->impl = MakeShared<FImpl, ESPMode::ThreadSafe>(std::move(host));
    return result;
}

FLibraInstanceSession::~FLibraInstanceSession()
{
    // Never "helpfully" Stop() on natural Lifetime or facade destruction.
    // Destroying an active session is a caller lifecycle error, as in Core.
    auto state = Snapshot();
    if (state)
    {
        checkf(IsInGameThread(), TEXT("Release Libra instance sessions on the game thread."));
        auto result = state->host->close();
        checkf(result == liberafx::Result::Ok,
            TEXT("Explicitly drain/destroy effects and consume requests before releasing a Libra session."));
        (void)result;
    }
}
auto FLibraInstanceSession::Snapshot() const -> TSharedPtr<FImpl, ESPMode::ThreadSafe>
{
    auto lock = FScopeLock(&stateMutex);
    return impl;
}
auto FLibraInstanceSession::SubmitCreate(TConstArrayView<uint8> bytecode, liberafx::RequestId& out,
    liberafx::Lifetime lifetime) -> liberafx::Result
{
    out = {};
    auto state = Snapshot();
    return state ? state->host->submitProgram(liberafx::InstanceRequestKind::Create, {},
        bytecode.GetData(), static_cast<std::size_t>(bytecode.Num()), lifetime,
        liberafx::ReplacementPolicy::PreserveState, out) : liberafx::Result::InvalidHandle;
}
auto FLibraInstanceSession::SubmitReplace(liberafx::InstanceId target, TConstArrayView<uint8> bytecode,
    liberafx::ReplacementPolicy policy, liberafx::RequestId& out, liberafx::Lifetime lifetime) -> liberafx::Result
{
    out = {};
    auto state = Snapshot();
    return state ? state->host->submitProgram(liberafx::InstanceRequestKind::Replace, target,
        bytecode.GetData(), static_cast<std::size_t>(bytecode.Num()), lifetime, policy, out) :
        liberafx::Result::InvalidHandle;
}
auto FLibraInstanceSession::SubmitNaturalEnd(liberafx::InstanceId target, liberafx::RequestId& out) -> liberafx::Result
{
    out = {};
    auto state = Snapshot();
    return state ? state->host->submitControl(liberafx::InstanceRequestKind::NaturalEnd, target, out) :
        liberafx::Result::InvalidHandle;
}
auto FLibraInstanceSession::SubmitDestroy(liberafx::InstanceId target, liberafx::RequestId& out) -> liberafx::Result
{
    out = {};
    auto state = Snapshot();
    return state ? state->host->submitControl(liberafx::InstanceRequestKind::Destroy, target, out) :
        liberafx::Result::InvalidHandle;
}
auto FLibraInstanceSession::TakeCompletion(liberafx::RequestId request, FCompletion& out) -> liberafx::Result
{
    auto state = Snapshot();
    if (!state) return liberafx::Result::InvalidHandle;
    auto completion = liberafx::host::ProgramCompletion{};
    auto result = state->host->takeCompletion(request, completion);
    if (result == liberafx::Result::Ok)
        out = {completion.request, completion.instance, completion.kind, completion.result};
    return result;
}
auto FLibraInstanceSession::Process(uint32 limit, uint32& processed) -> liberafx::Result
{
    processed = 0;
    if (!IsInGameThread()) return liberafx::Result::InvalidArgument;
    auto state = Snapshot();
    if (!state) return liberafx::Result::InvalidHandle;
    auto count = std::size_t{};
    auto result = state->host->process(limit, count);
    processed = static_cast<uint32>(count);
    return result;
}
auto FLibraInstanceSession::Advance(liberafx::InstanceId target, double elapsed, double frameDelta,
    bool& finished) -> liberafx::Result
{
    if (!IsInGameThread()) return liberafx::Result::InvalidArgument;
    auto state = Snapshot();
    return state ? state->host->advance(target, elapsed, frameDelta, finished) : liberafx::Result::InvalidHandle;
}
auto FLibraInstanceSession::ReadValue(liberafx::InstanceId target, const uint8 nodeId[16],
    std::array<double, 4>& values, uint8& width) -> liberafx::Result
{
    if (!IsInGameThread()) return liberafx::Result::InvalidArgument;
    auto state = Snapshot();
    return state ? state->host->read(target, nodeId, values.data(), width) : liberafx::Result::InvalidHandle;
}
auto FLibraInstanceSession::CloseAndRelease() -> liberafx::Result
{
    if (!IsInGameThread()) return liberafx::Result::InvalidArgument;
    auto state = Snapshot();
    if (!state) return liberafx::Result::InvalidHandle;
    auto result = state->host->close();
    if (result == liberafx::Result::Ok)
    {
        auto lock = FScopeLock(&stateMutex);
        impl.Reset();
    }
    return result;
}
