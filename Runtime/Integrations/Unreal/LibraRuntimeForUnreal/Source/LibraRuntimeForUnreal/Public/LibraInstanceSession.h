#pragma once

#include "CoreMinimal.h"
#include "HAL/CriticalSection.h"
#include "liberafx/LbInstanceSession.h"
#include <array>

// Native-only facade: no UObject is an ID, and no UObject/asset/render operation
// is ever performed by worker requests. Create/process/read/advance/release on
// game thread; workers may Submit*/TakeCompletion. There is no network transport.
class LIBRARUNTIMEFORUNREAL_API FLibraInstanceSession final
{
public:
    static TUniquePtr<FLibraInstanceSession> Create(bool developmentOptIn, liberafx::Result& outResult,
        uint32 instanceCapacity = 32, uint32 requestCapacity = 64,
        uint64 maxOwnedBytes = 64ull * 1024 * 1024);
    ~FLibraInstanceSession();
    FLibraInstanceSession(const FLibraInstanceSession&) = delete;
    FLibraInstanceSession& operator=(const FLibraInstanceSession&) = delete;

    liberafx::Result SubmitCreate(TConstArrayView<uint8> bytecode, liberafx::RequestId& out,
        liberafx::Lifetime lifetime = liberafx::Lifetime::infinite());
    liberafx::Result SubmitReplace(liberafx::InstanceId target, TConstArrayView<uint8> bytecode,
        liberafx::ReplacementPolicy policy, liberafx::RequestId& out,
        liberafx::Lifetime lifetime = liberafx::Lifetime::infinite());
    liberafx::Result SubmitNaturalEnd(liberafx::InstanceId target, liberafx::RequestId& out);
    liberafx::Result SubmitDestroy(liberafx::InstanceId target, liberafx::RequestId& out);
    // Completion has no local resource pointer; returned host memory is reclaimed.
    struct FCompletion final
    {
        liberafx::RequestId request{};
        liberafx::InstanceId instance{};
        liberafx::InstanceRequestKind kind{};
        liberafx::Result result = liberafx::Result::NotReady;
    };
    liberafx::Result TakeCompletion(liberafx::RequestId request, FCompletion& out);
    liberafx::Result Process(uint32 limit, uint32& processed);
    liberafx::Result Advance(liberafx::InstanceId target, double elapsed, double frameDelta, bool& finished);
    // Compiler/Guid big-endian UUID bytes; never memcpy an FGuid's memory here.
    liberafx::Result ReadValue(liberafx::InstanceId target, const uint8 nodeId[16],
        std::array<double, 4>& values, uint8& width);
    // NotReady retains the facade, disables new work except Destroy, and never
    // force-stops an effect. Drain/explicitly destroy, consume completions, retry.
    liberafx::Result CloseAndRelease();
private:
    FLibraInstanceSession() = default;
    struct FImpl;
    TSharedPtr<FImpl, ESPMode::ThreadSafe> Snapshot() const;
    mutable FCriticalSection stateMutex;
    TSharedPtr<FImpl, ESPMode::ThreadSafe> impl;
};
