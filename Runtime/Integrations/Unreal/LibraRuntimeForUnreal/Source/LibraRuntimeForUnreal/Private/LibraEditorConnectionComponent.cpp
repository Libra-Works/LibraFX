#include "LibraEditorConnectionComponent.h"

#include "GameFramework/Actor.h"
#include "LibraEditorTransport.h"
#include "LibraEffectAsset.h"
#include "LibraEffectComponent.h"
#include "liberafx/LivePreview.h"
#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

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
    struct FWatch { FGuid id; FLibraValueHandle handle; };
    TArray<FGuid> watchedIds;
    TArray<FWatch> watched;
    TArray<FLibraProfileMeasurement> profileSnapshot;
    uint64 profileSnapshotId = 0;
    uint64 dropped = 0;
    int32 profileOffset = 0;
    int64 profileProgramSequence = 0;
    int64 programSequence = 0;
    int64 lastValueSequence = -1;
    double lastValueTime = 0;
    double nextFeedback = 0;
    double interval = 0.25;
    bool controlPending = false;
    bool recordRequested = false;
    bool nodesRequested = true;
    bool accessorsRequested = false;
    bool originalRecording = false;
    bool originalNodes = false;
    bool originalAccessors = false;
    bool profilePending = false;
};

namespace {
void BindWatched(FLibraEditorConnectionState& state)
{
    state.watched.Reset();
    state.lastValueSequence = -1;
    if (auto target = state.target.Get())
        for (const auto& id : state.watchedIds)
        {
            auto handle = FLibraValueHandle{};
            if (target->BindValue(id, handle) == ELibraRuntimeResult::Ok) state.watched.Add({id, handle});
        }
}

bool ApplyControl(FLibraEditorConnectionState& state, const liberafx::livepreview::FrameView& frame)
{
    auto encoded = FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(frame.payload), static_cast<int32>(frame.payloadLength));
    auto reader = TJsonReaderFactory<>::Create(FString(encoded.Length(), encoded.Get()));
    TSharedPtr<FJsonObject> object;
    FString type;
    if (!FJsonSerializer::Deserialize(reader, object) || !object || !object->TryGetStringField(TEXT("type"), type)) return false;
    if (type == TEXT("profile_control"))
    {
        auto enabled = false;
        auto nodes = false;
        auto accessors = false;
        auto interval = double{0};
        if (!object->TryGetBoolField(TEXT("enabled"), enabled) ||
            !object->TryGetBoolField(TEXT("nodes"), nodes) ||
            !object->TryGetBoolField(TEXT("accessors"), accessors) ||
            !object->TryGetNumberField(TEXT("intervalMs"), interval) ||
            !FMath::IsFinite(interval) || interval < 100 || interval > 5000 || FMath::FloorToDouble(interval) != interval)
            return false;
        state.recordRequested = enabled;
        state.nodesRequested = nodes;
        state.accessorsRequested = accessors;
        state.interval = interval / 1000;
        state.controlPending = true;
        state.profileSnapshot.Reset();
        state.profilePending = false;
        state.nextFeedback = 0;
    }
    else if (type == TEXT("watch_values"))
    {
        const TArray<TSharedPtr<FJsonValue>>* ids = nullptr;
        if (!object->TryGetArrayField(TEXT("nodeIds"), ids) || ids->Num() > 64) return false;
        auto watched = TArray<FGuid>{};
        for (const auto& value : *ids)
        {
            FString text;
            auto id = FGuid{};
            if (!value->TryGetString(text) || !FGuid::Parse(text, id) || !id.IsValid() || watched.Contains(id)) return false;
            watched.Add(id);
        }
        state.watchedIds = MoveTemp(watched);
        BindWatched(state);
        state.nextFeedback = 0;
    }
    return true;
}

void SendFeedback(FLibraEditorConnectionState& state, int64 programSequence)
{
    auto target = state.target.Get();
    if (!target || !state.sequence.hasProgram()) return;
    auto now = FPlatformTime::Seconds();
    if (state.profilePending || now >= state.nextFeedback)
    {
        if (!state.profilePending)
        {
            state.nextFeedback = now + state.interval;
            if (state.recordRequested && target->CaptureProfiling(state.profileSnapshot, state.dropped))
            {
                ++state.profileSnapshotId;
                state.profileOffset = 0;
                state.profileProgramSequence = programSequence;
                state.profilePending = true;
            }
        }
        if (state.profilePending)
        {
            auto text = FString{};
            auto writer = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&text);
            writer->WriteObjectStart();
            writer->WriteValue(TEXT("type"), TEXT("profile"));
            writer->WriteValue(TEXT("programSequence"), state.profileProgramSequence);
            writer->WriteValue(TEXT("snapshotId"), state.profileSnapshotId);
            writer->WriteValue(TEXT("offset"), state.profileOffset);
            writer->WriteValue(TEXT("total"), state.profileSnapshot.Num());
            writer->WriteValue(TEXT("dropped"), state.dropped);
            writer->WriteArrayStart(TEXT("metrics"));
            auto end = FMath::Min(state.profileOffset + 256, state.profileSnapshot.Num());
            for (auto index = state.profileOffset; index < end; ++index)
            {
                const auto& metric = state.profileSnapshot[index];
                writer->WriteObjectStart();
                writer->WriteValue(TEXT("event"), metric.Event);
                if (metric.NodeId.IsValid()) writer->WriteValue(TEXT("nodeId"), metric.NodeId.ToString(EGuidFormats::DigitsWithHyphensLower));
                else writer->WriteNull(TEXT("nodeId"));
                writer->WriteValue(TEXT("calls"), metric.Calls);
                writer->WriteValue(TEXT("inclusiveNs"), metric.InclusiveNs);
                writer->WriteValue(TEXT("exclusiveNs"), metric.ExclusiveNs);
                writer->WriteValue(TEXT("minNs"), metric.MinNs);
                writer->WriteValue(TEXT("maxNs"), metric.MaxNs);
                writer->WriteObjectEnd();
            }
            writer->WriteArrayEnd();
            writer->WriteObjectEnd();
            writer->Close();
            if (state.transport.QueueText(text))
            {
                state.profileOffset = end;
                if (end == state.profileSnapshot.Num())
                {
                    state.profileSnapshot.Reset();
                    state.profilePending = false;
                }
            }
        }
        if (!state.watched.IsEmpty() &&
            (state.lastValueSequence != programSequence || state.lastValueTime != target->LastEvaluatedTimeSeconds))
        {
            auto text = FString{};
            auto writer = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&text);
            writer->WriteObjectStart();
            writer->WriteValue(TEXT("type"), TEXT("values"));
            writer->WriteValue(TEXT("programSequence"), programSequence);
            writer->WriteValue(TEXT("evaluationTime"), target->LastEvaluatedTimeSeconds);
            writer->WriteArrayStart(TEXT("values"));
            auto received = int32{0};
            for (const auto& watch : state.watched)
            {
                auto value = FVector4{};
                auto width = int32{0};
                if (target->ReadBoundValue(watch.handle, value, width) != ELibraRuntimeResult::Ok) continue;
                writer->WriteObjectStart();
                writer->WriteValue(TEXT("nodeId"), watch.id.ToString(EGuidFormats::DigitsWithHyphensLower));
                writer->WriteArrayStart(TEXT("components"));
                for (auto index = 0; index < width; ++index) writer->WriteValue(value[index]);
                writer->WriteArrayEnd();
                writer->WriteObjectEnd();
                ++received;
            }
            writer->WriteArrayEnd();
            writer->WriteObjectEnd();
            writer->Close();
            if (received != 0 && state.transport.QueueText(text))
            {
                state.lastValueSequence = programSequence;
                state.lastValueTime = target->LastEvaluatedTimeSeconds;
            }
        }
    }
}
}

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
    candidate->originalRecording = Target->bRecordProfiling;
    candidate->originalNodes = Target->bProfileNodes;
    candidate->originalAccessors = Target->bProfileAccessors;
    candidate->transport.QueueText(
#if WITH_EDITOR
        TEXT("{\"type\":\"hello\",\"engine\":\"Unreal\",\"role\":\"editor\",\"version\":1,\"capabilities\":[\"profiling\",\"values\"]}")
#else
        TEXT("{\"type\":\"hello\",\"engine\":\"Unreal\",\"role\":\"runtime\",\"version\":1,\"capabilities\":[\"profiling\",\"values\"]}")
#endif
    );
    OriginalEffect = Target->Effect;
    State = MoveTemp(candidate);
    AddTickPrerequisiteComponent(Target);
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
        if (auto target = previous->target.Get()) RemoveTickPrerequisiteComponent(target);
        previous->transport.Stop();
        if (restoreEffect)
            if (auto target = previous->target.Get(); target && PreviewEffect && target->Effect == PreviewEffect)
            {
                target->Stop();
                target->Effect = OriginalEffect;
                target->ConfigureRecording(previous->originalRecording, previous->originalNodes, previous->originalAccessors);
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
    if (frame.kind == wire::Kind::Control) return ApplyControl(*state, frame);
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
        state->programSequence = static_cast<int64>(FMath::Min(frame.sequence, static_cast<uint64>(MAX_int64)));
        BindWatched(*state);
        state->profileSnapshot.Reset();
        state->profilePending = false;
        state->profileSnapshotId = 0;
        state->nextFeedback = 0;
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
    if (state->controlPending)
    {
        if (auto target = state->target.Get())
        {
            auto result = target->ConfigureRecording(state->recordRequested, state->nodesRequested, state->accessorsRequested);
            if (result == ELibraRuntimeResult::Ok)
            {
                state->controlPending = false;
                if (state->sequence.hasProgram()) state->pendingEvaluation = true;
            }
        }
    }
    SendFeedback(*state, state->programSequence);
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
