#pragma once

#include "CoreMinimal.h"
#include "LibraSpawnableComponent.h"
#include "liberafx/LbInstanceSession.h"

class ULibraEffectComponent;

// Real game component adapter backed by the package instance host. Owner/game thread:
// Create, Process, List, Resolve, Close, Shutdown. Worker threads: Submit* and
// TakeCompletion. No worker UObject API.
// The facade must outlive/join producers, just like any shared C++ service.
class LIBRARUNTIMEFORUNREAL_API FLibraLiveGameSession final
{
public:
    struct FCompletion final
    {
        liberafx::RequestId request{};
        liberafx::InstanceId instance{};
        liberafx::Result result = liberafx::Result::NotReady;
        FString error;
    };
    struct FLiveInstance final
    {
        liberafx::InstanceId id{};
        FString label;
        bool finished = false;
        int32 renderObjects = 0;
    };
    static auto Create(bool developmentOptIn, AActor* owner,
        const TArray<FLibraSpawnableAssetBinding>& bindings, uint32 instanceCapacity = 32,
        uint32 requestCapacity = 64, uint64 maxOwnedBytes = 64ull * 1024 * 1024) -> TUniquePtr<FLibraLiveGameSession>;
    ~FLibraLiveGameSession();
    auto SubmitCreate(TConstArrayView<uint8> package, const FString& label, liberafx::RequestId& out) -> liberafx::Result;
    auto SubmitReplace(liberafx::InstanceId target, TConstArrayView<uint8> package,
        liberafx::ReplacementPolicy policy, liberafx::RequestId& out) -> liberafx::Result;
    auto SubmitDestroy(liberafx::InstanceId target, liberafx::RequestId& out) -> liberafx::Result;
    auto SubmitNaturalEnd(liberafx::InstanceId target, liberafx::RequestId& out) -> liberafx::Result;
    auto TakeCompletion(liberafx::RequestId request, FCompletion& out) -> liberafx::Result;
    auto Process(uint32 limit = 16) -> liberafx::Result;
    auto List() const -> TArray<FLiveInstance>;
    // Local owner-thread borrow only; never send UObject addresses over transport.
    auto Resolve(liberafx::InstanceId target) const -> ULibraEffectComponent*;
    auto GetSessionId() const -> liberafx::SessionId;
    auto ClearPreviewDebugDraw(liberafx::InstanceId target) -> liberafx::Result;
    auto DisconnectPreviewDebugDraw() -> liberafx::Result;
    auto AttachPreviewDebugDraw() -> liberafx::Result;
    auto Close() -> liberafx::Result;
    // Explicit owner teardown: force destroys live components, cancels queued
    // operations into consumable failure completions, rejects further work.
    auto ShutdownAndForceDestroy() -> liberafx::Result;
private:
    FLibraLiveGameSession();
    struct FImpl;
    TUniquePtr<FImpl> impl;
    auto Submit(liberafx::InstanceRequestKind kind, liberafx::InstanceId target,
        TConstArrayView<uint8> bytes, const FString& label, liberafx::ReplacementPolicy policy,
        liberafx::RequestId& out) -> liberafx::Result;
};
