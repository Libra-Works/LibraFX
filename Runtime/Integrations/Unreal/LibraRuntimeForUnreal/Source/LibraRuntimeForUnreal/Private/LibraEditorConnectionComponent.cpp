#include "LibraEditorConnectionComponent.h"

#include "GameFramework/Actor.h"
#include "LibraEditorTransport.h"
#include "LibraEffectAsset.h"
#include "LibraEffectComponent.h"
#include "liberafx/LivePreview.h"

struct FLibraEditorConnectionState final
{
    FLibraEditorTransport transport;
    liberafx::livepreview::SequenceCursor sequence;
    TWeakObjectPtr<ULibraEffectComponent> target;
    double pendingTime = 0;
    bool pendingEvaluation = false;
    bool rejectedSnapshot = false;
    bool awaitingSnapshot = false;
    FString diagnostic;
};

ULibraEditorConnectionComponent::ULibraEditorConnectionComponent()
{
    PrimaryComponentTick.bCanEverTick = true;
    PrimaryComponentTick.bStartWithTickEnabled = false;
}

ULibraEditorConnectionComponent::~ULibraEditorConnectionComponent() = default;

void ULibraEditorConnectionComponent::BeginPlay()
{
    Super::BeginPlay();
    if (bAutoConnect) ConnectToEditor();
}

void ULibraEditorConnectionComponent::EndPlay(const EEndPlayReason::Type endPlayReason)
{
    CloseConnection(true);
    Super::EndPlay(endPlayReason);
}

void ULibraEditorConnectionComponent::OnUnregister()
{
    CloseConnection(true);
    Super::OnUnregister();
}

void ULibraEditorConnectionComponent::BeginDestroy()
{
    State.Reset();
    Super::BeginDestroy();
}

auto ULibraEditorConnectionComponent::ConnectToEditor() -> bool
{
    check(IsInGameThread());
    DisconnectFromEditor();
    if (!IsRegistered() || Port <= 0 || Port > 65535)
    {
        Status = TEXT("Register the component and supply a valid editor port.");
        return false;
    }
    if (!Target && GetOwner()) Target = GetOwner()->FindComponentByClass<ULibraEffectComponent>();
    if (!Target || !Target->IsRegistered())
    {
        Status = TEXT("A registered Libra Effect component is required.");
        return false;
    }
    auto candidate = MakeShared<FLibraEditorConnectionState, ESPMode::NotThreadSafe>();
    auto settings = FLibraEditorTransportSettings{};
    settings.Host = Host;
    settings.Port = static_cast<uint16>(Port);
    settings.AccessToken = AccessToken;
    settings.CertificateFingerprint = CertificateFingerprint;
    if (!candidate->transport.Start(settings, Status)) return false;
    candidate->target = Target;
    OriginalEffect = Target->Effect;
    State = MoveTemp(candidate);
    LastResult = ELibraRuntimeResult::NotReady;
    LastAppliedSequence = 0;
    Status = TEXT("Connecting to the editor using certificate-pinned TLS.");
    SetComponentTickEnabled(true);
    return true;
}

void ULibraEditorConnectionComponent::CloseConnection(bool restoreEffect)
{
    check(IsInGameThread());
    auto previous = MoveTemp(State);
    if (previous)
    {
        previous->transport.Stop();
        if (restoreEffect)
            if (auto target = previous->target.Get(); target && PreviewEffect && target->Effect == PreviewEffect)
            {
                target->Stop();
                target->Effect = OriginalEffect;
            }
    }
    PreviewEffect = nullptr;
    OriginalEffect = nullptr;
    bIsConnected = false;
    bEditorPlaying = false;
    SetComponentTickEnabled(false);
}

void ULibraEditorConnectionComponent::DisconnectFromEditor()
{
    CloseConnection(true);
    Status = TEXT("Disconnected.");
}

void ULibraEditorConnectionComponent::RequestSnapshot()
{
    check(IsInGameThread());
    if (!State) return;
    State->rejectedSnapshot = false;
    State->awaitingSnapshot = true;
    State->transport.RequestResync();
}

auto ULibraEditorConnectionComponent::ApplyMessage(TConstArrayView<uint8> message) -> bool
{
    namespace wire = liberafx::livepreview;
    auto state = State;
    if (!state) return false;
    auto frame = wire::FrameView{};
    if (wire::decodeFrame(message.GetData(), message.Num(), frame) != wire::DecodeResult::Ok)
    {
        Status = TEXT("The editor sent an invalid compiled preview frame.");
        return false;
    }
    auto sequence = state->sequence.validate(frame);
    if (sequence == wire::SequenceResult::Stale) return true;
    if (sequence == wire::SequenceResult::Resync)
    {
        if (!state->rejectedSnapshot && !state->awaitingSnapshot) RequestSnapshot();
        return true;
    }
    if (frame.kind == wire::Kind::CompileError)
    {
        auto text = FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(frame.payload), static_cast<int32>(frame.payloadLength));
        state->diagnostic = FString(text.Length(), text.Get());
        state->sequence.commit(frame);
        state->pendingEvaluation = false;
        Status = TEXT("Editor compilation failed: ") + state->diagnostic;
        LastResult = ELibraRuntimeResult::UnsupportedBinary;
        OnPreviewUpdated.Broadcast(LastResult, static_cast<int64>(FMath::Min(frame.sequence, static_cast<uint64>(MAX_int64))));
        return true;
    }
    auto target = state->target.Get();
    if (!target || !target->IsRegistered())
    {
        Status = TEXT("The preview target is no longer registered.");
        return false;
    }
    if (frame.kind == wire::Kind::Program)
    {
        auto candidate = NewObject<ULibraEffectAsset>(this);
        auto result = candidate->SetBytecode(TConstArrayView<uint8>(frame.payload, static_cast<int32>(frame.payloadLength)));
        if (result == ELibraRuntimeResult::Ok) result = target->SetEffect(candidate);
        if (result != ELibraRuntimeResult::Ok)
        {
            state->rejectedSnapshot = true;
            state->awaitingSnapshot = false;
            state->pendingEvaluation = false;
            state->diagnostic = TEXT("Runtime rejected the compiled effect; check native node registration, ABI and memory budget.");
            Status = state->diagnostic;
            LastResult = result;
            OnPreviewUpdated.Broadcast(result, static_cast<int64>(FMath::Min(frame.sequence, static_cast<uint64>(MAX_int64))));
            return true;
        }
        PreviewEffect = candidate;
        state->rejectedSnapshot = false;
        state->awaitingSnapshot = false;
    }
    else if (state->rejectedSnapshot || state->awaitingSnapshot) return true;
    state->sequence.commit(frame);
    state->diagnostic.Reset();
    state->pendingTime = frame.timeSeconds;
    state->pendingEvaluation = true;
    bEditorPlaying = (frame.flags & 1u) != 0;
    LastAppliedSequence = static_cast<int64>(FMath::Min(frame.sequence, static_cast<uint64>(MAX_int64)));
    LastResult = ELibraRuntimeResult::Ok;
    if (frame.kind == wire::Kind::Program)
    {
        OnPreviewProgramChanged.Broadcast(LastResult, LastAppliedSequence);
        if (State != state) return true;
    }
    OnPreviewUpdated.Broadcast(LastResult, LastAppliedSequence);
    return true;
}

void ULibraEditorConnectionComponent::TickComponent(float deltaTime, ELevelTick tickType,
    FActorComponentTickFunction* tickFunction)
{
    Super::TickComponent(deltaTime, tickType, tickFunction);
    auto state = State;
    if (!state) return;
    bIsConnected = state->transport.IsConnected();
    auto message = TArray<uint8>{};
    for (auto index = 0; index < 8 && state->transport.TryReceive(message); ++index)
    {
        if (!ApplyMessage(message))
        {
            auto error = Status;
            CloseConnection(true);
            Status = MoveTemp(error);
            return;
        }
        if (State != state) return;
    }
    if (state->pendingEvaluation)
    {
        auto target = state->target.Get();
        if (!target) { CloseConnection(false); return; }
        auto result = target->EvaluateAtTime(state->pendingTime);
        if (result != ELibraRuntimeResult::Busy)
        {
            state->pendingEvaluation = false;
            LastResult = result;
        }
    }
    Status = state->diagnostic.IsEmpty() ? state->transport.GetStatus() : state->diagnostic;
}
