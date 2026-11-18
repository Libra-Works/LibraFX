#include "LibraLiveGameSession.h"
#include "LibraEffectComponent.h"
#include "LibraSpawnableAsset.h"
#include "GameFramework/Actor.h"
#include "Engine/World.h"
#include "Components/SceneComponent.h"
#include "UObject/StrongObjectPtr.h"
#include "liberafx/host/LbLivePackage.h"
#include <deque>
#include <mutex>
#include <unordered_map>

namespace {
constexpr auto maximumPackageBytes = 16 * 1024 * 1024;
auto same(liberafx::SessionId a, liberafx::SessionId b) -> bool { return a.high == b.high && a.low == b.low; }
}

struct FLibraLiveGameSession::FImpl final
{
    struct FPlayback final
    {
        TStrongObjectPtr<ULibraEffectComponent> component;
        TStrongObjectPtr<ULibraSpawnableAsset> asset;
        TWeakObjectPtr<AActor> playbackOwner;
        FString label;
        uint64 byteCount = 0;
        bool restartable = false;
        ~FPlayback()
        {
            check(IsInGameThread());
            if (IsValid(component.Get()))
            {
                component->Stop();
                component->DestroyComponent();
            }
            if (playbackOwner.IsValid()) playbackOwner->Destroy();
        }
    };
    struct FSlot final
    {
        uint64 generation = 1;
        TUniquePtr<FPlayback> playback;
    };
    struct FRequest final
    {
        liberafx::InstanceRequestKind kind{};
        liberafx::InstanceId target{};
        liberafx::ReplacementPolicy policy{};
        TArray<uint8> bytes;
        FString label;
        FCompletion completion;
        bool complete = false;
    };
    mutable std::mutex gate;
    TWeakObjectPtr<AActor> owner;
    // Keep host bindings alive independently of asset GC while requests queue.
    TArray<TStrongObjectPtr<UObject>> retainedBindings;
    TArray<FLibraSpawnableAssetBinding> bindings;
    TArray<FSlot> slots;
    std::deque<uint64> pending;
    std::unordered_map<uint64, TUniquePtr<FRequest>> requests;
    liberafx::SessionId id{};
    uint64 sequence = 0, queuedBytes = 0, activeBytes = 0, maxOwnedBytes = 0;
    uint32 requestCapacity = 0;
    bool closing = false, closed = false, processing = false;
    bool suppressPreviewDebugDraw = false;

    auto Find(liberafx::InstanceId target) const -> FSlot*
    {
        if (!same(target.session, id) || target.incarnation != 1 || target.slot >= static_cast<uint64>(slots.Num()))
            return nullptr;
        auto& slot = const_cast<FSlot&>(slots[static_cast<int32>(target.slot)]);
        return slot.playback && slot.generation == target.generation ? &slot : nullptr;
    }
    auto IdFor(int32 index) const -> liberafx::InstanceId
    { return {id, 1, slots[index].generation, static_cast<uint64>(index)}; }
    auto Execute(FRequest& request) -> liberafx::Result
    {
        auto* slot = request.kind == liberafx::InstanceRequestKind::Create ? nullptr : Find(request.target);
        if (request.kind != liberafx::InstanceRequestKind::Create && !slot) return liberafx::Result::InvalidHandle;
        if (request.kind == liberafx::InstanceRequestKind::Destroy)
        {
            auto count = slot->playback->byteCount;
            slot->playback.Reset();
            auto lock = std::lock_guard<std::mutex>(gate);
            activeBytes -= count;
            slot->generation = slot->generation == UINT64_MAX ? 0 : slot->generation + 1;
            return liberafx::Result::Ok;
        }
        if (request.kind == liberafx::InstanceRequestKind::NaturalEnd)
            return IsValid(slot->playback->component.Get()) ?
                static_cast<liberafx::Result>(slot->playback->component->RequestNaturalEnd()) : liberafx::Result::InvalidHandle;
        if (request.kind == liberafx::InstanceRequestKind::Replace &&
            (request.policy != liberafx::ReplacementPolicy::Restart || !slot->playback->restartable ||
             liberafx::host::validateLiveRestart(request.bytes.GetData(), request.bytes.Num()) != liberafx::Result::Ok))
        {
            request.completion.error = TEXT("Only explicit Restart of root-render numeric/manual-lifetime packages is supported. Children, signals, game I/O, GPU and state migration are rejected.");
            return liberafx::Result::UnsupportedBinary;
        }
        if (!owner.IsValid()) return liberafx::Result::NotReady;
        if (request.kind == liberafx::InstanceRequestKind::Create)
        {
            for (auto& available : slots)
                if (!available.playback && available.generation) { slot = &available; break; }
            if (!slot) return liberafx::Result::QueueRejected;
        }
        auto candidate = MakeUnique<FPlayback>();
        candidate->label = request.kind == liberafx::InstanceRequestKind::Create ? request.label : slot->playback->label;
        candidate->byteCount = request.bytes.Num();
        candidate->asset.Reset(NewObject<ULibraSpawnableAsset>(GetTransientPackage(), NAME_None, RF_Transient));
        auto result = candidate->asset->SetPackageBytes(request.bytes);
        if (result != ELibraRuntimeResult::Ok) return static_cast<liberafx::Result>(result);
        auto* world = owner->GetWorld();
        if (!world) return liberafx::Result::NotReady;
        auto spawn = FActorSpawnParameters{};
        spawn.Owner = owner.Get();
        spawn.ObjectFlags |= RF_Transient;
        auto* candidateActor = world->SpawnActor<AActor>(AActor::StaticClass(), owner->GetActorTransform(), spawn);
        if (!candidateActor) return liberafx::Result::NotReady;
        candidate->playbackOwner = candidateActor;
        // Candidate render resources are real but cannot become visible during
        // preflight/failure, even while the render thread runs independently.
        candidateActor->SetActorHiddenInGame(true);
        auto* root = NewObject<USceneComponent>(candidateActor, NAME_None, RF_Transient);
        candidateActor->SetRootComponent(root);
        candidateActor->AddInstanceComponent(root);
        root->RegisterComponent();
        candidateActor->SetActorTransform(owner->GetActorTransform());
        if (owner->GetRootComponent())
            candidateActor->AttachToActor(owner.Get(), FAttachmentTransformRules::KeepWorldTransform);
        candidate->component.Reset(NewObject<ULibraEffectComponent>(candidateActor, NAME_None, RF_Transient));
        auto* component = candidate->component.Get();
        if (suppressPreviewDebugDraw) component->ClearPreviewDebugDraw();
        component->bAutoPlay = false;
        component->Package = candidate->asset.Get();
        component->AssetBindings = bindings;
        candidateActor->AddInstanceComponent(component);
        component->RegisterComponent();
        // Synchronous preflight/initial evaluation. No candidate survives a failed
        // start. Commit happens before the next scene/render frame is submitted.
        result = component->Play();
        if (result != ELibraRuntimeResult::Ok)
        {
            request.completion.error = component->LastError;
            return static_cast<liberafx::Result>(result);
        }
        candidate->restartable = liberafx::host::validateLiveRestart(request.bytes.GetData(), request.bytes.Num()) == liberafx::Result::Ok;
        auto previous = MoveTemp(slot->playback);
        {
            auto lock = std::lock_guard<std::mutex>(gate);
            activeBytes += candidate->byteCount;
            if (previous) activeBytes -= previous->byteCount;
        }
        slot->playback = MoveTemp(candidate);
        previous.Reset();
        candidateActor->SetActorHiddenInGame(false);
        request.completion.instance = IdFor(static_cast<int32>(slot - slots.GetData()));
        return liberafx::Result::Ok;
    }
};

FLibraLiveGameSession::FLibraLiveGameSession() = default;
FLibraLiveGameSession::~FLibraLiveGameSession()
{
    checkf(IsInGameThread() && (!impl || impl->closed),
        TEXT("Explicitly close or ShutdownAndForceDestroy a live session on the game thread before destruction."));
}
auto FLibraLiveGameSession::Create(bool developmentOptIn, AActor* owner,
    const TArray<FLibraSpawnableAssetBinding>& bindings, uint32 instanceCapacity, uint32 requestCapacity,
    uint64 maxOwnedBytes) -> TUniquePtr<FLibraLiveGameSession>
{
#if UE_BUILD_SHIPPING
    return {};
#else
    if (!IsInGameThread() || !developmentOptIn || !IsValid(owner) || !instanceCapacity || instanceCapacity > 256 ||
        !requestCapacity || requestCapacity > 1024 || maxOwnedBytes < maximumPackageBytes) return {};
    auto result = TUniquePtr<FLibraLiveGameSession>(new FLibraLiveGameSession);
    result->impl = MakeUnique<FImpl>();
    auto& state = *result->impl;
    state.owner = owner;
    state.bindings = bindings;
    for (const auto& binding : bindings)
        if (binding.Asset) state.retainedBindings.Emplace(binding.Asset.Get());
    state.slots.SetNum(instanceCapacity);
    state.requestCapacity = requestCapacity;
    state.maxOwnedBytes = maxOwnedBytes;
    auto nonce = FGuid::NewGuid();
    state.id = {(uint64{nonce.A} << 32) | nonce.B, (uint64{nonce.C} << 32) | nonce.D};
    return result;
#endif
}
auto FLibraLiveGameSession::GetSessionId() const -> liberafx::SessionId { return impl->id; }
auto FLibraLiveGameSession::Submit(liberafx::InstanceRequestKind kind, liberafx::InstanceId target,
    TConstArrayView<uint8> bytes, const FString& label, liberafx::ReplacementPolicy policy,
    liberafx::RequestId& out) -> liberafx::Result
{
    out = {};
    auto& state = *impl;
    auto lock = std::lock_guard<std::mutex>(state.gate);
    if (state.closed || (state.closing && kind != liberafx::InstanceRequestKind::Destroy)) return liberafx::Result::NotReady;
    if (kind != liberafx::InstanceRequestKind::Create && (!same(target.session, state.id) || target.incarnation != 1))
        return liberafx::Result::InvalidHandle;
    if (bytes.Num() > maximumPackageBytes || ((kind == liberafx::InstanceRequestKind::Create ||
        kind == liberafx::InstanceRequestKind::Replace) && bytes.IsEmpty())) return liberafx::Result::InvalidArgument;
    if (state.requests.size() >= state.requestCapacity || state.queuedBytes + state.activeBytes + bytes.Num() > state.maxOwnedBytes)
        return liberafx::Result::QueueRejected;
    if (state.sequence == UINT64_MAX) return liberafx::Result::CounterOverflow;
    auto request = MakeUnique<FImpl::FRequest>();
    request->kind = kind;
    request->target = target;
    request->policy = policy;
    request->bytes.Append(bytes.GetData(), bytes.Num());
    request->label = label.Left(128);
    out = {state.id, 1, ++state.sequence};
    request->completion.request = out;
    request->completion.instance = target;
    state.queuedBytes += bytes.Num();
    state.pending.push_back(out.sequence);
    state.requests.emplace(out.sequence, MoveTemp(request));
    return liberafx::Result::Ok;
}
auto FLibraLiveGameSession::SubmitCreate(TConstArrayView<uint8> bytes, const FString& label, liberafx::RequestId& out) -> liberafx::Result
{ return Submit(liberafx::InstanceRequestKind::Create, {}, bytes, label, liberafx::ReplacementPolicy::Restart, out); }
auto FLibraLiveGameSession::SubmitReplace(liberafx::InstanceId target, TConstArrayView<uint8> bytes,
    liberafx::ReplacementPolicy policy, liberafx::RequestId& out) -> liberafx::Result
{ return Submit(liberafx::InstanceRequestKind::Replace, target, bytes, {}, policy, out); }
auto FLibraLiveGameSession::SubmitDestroy(liberafx::InstanceId target, liberafx::RequestId& out) -> liberafx::Result
{ return Submit(liberafx::InstanceRequestKind::Destroy, target, {}, {}, {}, out); }
auto FLibraLiveGameSession::SubmitNaturalEnd(liberafx::InstanceId target, liberafx::RequestId& out) -> liberafx::Result
{ return Submit(liberafx::InstanceRequestKind::NaturalEnd, target, {}, {}, {}, out); }
auto FLibraLiveGameSession::TakeCompletion(liberafx::RequestId request, FCompletion& out) -> liberafx::Result
{
    auto& state = *impl;
    auto lock = std::lock_guard<std::mutex>(state.gate);
    if (!same(request.session, state.id) || request.incarnation != 1) return liberafx::Result::InvalidHandle;
    auto found = state.requests.find(request.sequence);
    if (found == state.requests.end()) return liberafx::Result::InvalidHandle;
    if (!found->second->complete) return liberafx::Result::NotReady;
    out = found->second->completion;
    state.requests.erase(found);
    return liberafx::Result::Ok;
}
auto FLibraLiveGameSession::Process(uint32 limit) -> liberafx::Result
{
    if (!IsInGameThread()) return liberafx::Result::InvalidArgument;
    auto& state = *impl;
    if (state.processing) return liberafx::Result::Busy;
    auto guard = TGuardValue<bool>(state.processing, true);
    for (auto index = uint32{}; index < limit; ++index)
    {
        auto* request = static_cast<FImpl::FRequest*>(nullptr);
        {
            auto lock = std::lock_guard<std::mutex>(state.gate);
            if (state.pending.empty()) break;
            request = state.requests.at(state.pending.front()).Get();
            state.pending.pop_front();
        }
        auto result = state.Execute(*request);
        {
            auto lock = std::lock_guard<std::mutex>(state.gate);
            state.queuedBytes -= request->bytes.Num();
            request->bytes.Empty();
            request->completion.result = result;
            request->complete = true;
        }
    }
    return liberafx::Result::Ok;
}
auto FLibraLiveGameSession::Resolve(liberafx::InstanceId target) const -> ULibraEffectComponent*
{
    if (!IsInGameThread()) return nullptr;
    auto* slot = impl->Find(target);
    return slot ? slot->playback->component.Get() : nullptr;
}
auto FLibraLiveGameSession::List() const -> TArray<FLiveInstance>
{
    auto result = TArray<FLiveInstance>{};
    if (!IsInGameThread()) return result;
    for (auto index = int32{}; index < impl->slots.Num(); ++index)
    {
        const auto& slot = impl->slots[index];
        if (!slot.playback) continue;
        auto* component = slot.playback->component.Get();
        result.Add({impl->IdFor(index), slot.playback->label, !IsValid(component) || component->bRootFinished,
            IsValid(component) ? component->GetRenderObjectCount() : 0});
    }
    return result;
}
auto FLibraLiveGameSession::ClearPreviewDebugDraw(liberafx::InstanceId target) -> liberafx::Result
{
    if (!IsInGameThread()) return liberafx::Result::InvalidArgument;
    if (impl->processing) return liberafx::Result::Busy;
    auto* slot = impl->Find(target); // exact session, incarnation, generation
    if (!slot) return liberafx::Result::InvalidHandle;
    if (IsValid(slot->playback->component.Get())) slot->playback->component->ClearPreviewDebugDraw();
    return liberafx::Result::Ok;
}
auto FLibraLiveGameSession::DisconnectPreviewDebugDraw() -> liberafx::Result
{
    if (!IsInGameThread()) return liberafx::Result::InvalidArgument;
    if (impl->processing) return liberafx::Result::Busy;
    impl->suppressPreviewDebugDraw = true;
    for (const auto& live : List()) ClearPreviewDebugDraw(live.id);
    return liberafx::Result::Ok;
}
auto FLibraLiveGameSession::AttachPreviewDebugDraw() -> liberafx::Result
{
    if (!IsInGameThread()) return liberafx::Result::InvalidArgument;
    if (impl->processing || impl->closing || impl->closed) return liberafx::Result::NotReady;
    impl->suppressPreviewDebugDraw = false; // cleared instances are never resurrected
    return liberafx::Result::Ok;
}
auto FLibraLiveGameSession::Close() -> liberafx::Result
{
    if (!IsInGameThread()) return liberafx::Result::InvalidArgument;
    auto& state = *impl;
    if (state.processing) return liberafx::Result::Busy;
    DisconnectPreviewDebugDraw();
    auto lock = std::lock_guard<std::mutex>(state.gate);
    state.closing = true;
    for (const auto& slot : state.slots) if (slot.playback) return liberafx::Result::NotReady;
    if (!state.requests.empty()) return liberafx::Result::NotReady;
    state.closed = true;
    return liberafx::Result::Ok;
}
auto FLibraLiveGameSession::ShutdownAndForceDestroy() -> liberafx::Result
{
    if (!IsInGameThread()) return liberafx::Result::InvalidArgument;
    auto& state = *impl;
    if (state.processing) return liberafx::Result::Busy;
    {
        auto lock = std::lock_guard<std::mutex>(state.gate);
        state.closing = state.closed = true;
        for (auto sequence : state.pending)
        {
            auto& request = *state.requests.at(sequence);
            request.bytes.Empty();
            request.complete = true;
            request.completion.result = liberafx::Result::NotReady;
            request.completion.error = TEXT("Engine owner shut down before application.");
        }
        state.pending.clear();
        state.queuedBytes = 0;
    }
    for (auto& slot : state.slots) slot.playback.Reset();
    auto lock = std::lock_guard<std::mutex>(state.gate);
    state.activeBytes = 0;
    return liberafx::Result::Ok;
}
