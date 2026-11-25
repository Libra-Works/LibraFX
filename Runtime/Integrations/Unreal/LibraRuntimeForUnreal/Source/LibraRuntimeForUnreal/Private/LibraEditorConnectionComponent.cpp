#include "LibraEditorConnectionComponent.h"

#include "GameFramework/Actor.h"
#include "LibraEditorTransport.h"
#include "LibraEffectComponent.h"
#include "LibraSpawnableAsset.h"
#include "LibraNativeState.h"
#include "liberafx/LbLivePreview.h"
#include "liberafx/host/LbPackagePreviewContract.h"
#include "Dom/JsonObject.h"
#include "Policies/CondensedJsonPrintPolicy.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "HAL/PlatformProperties.h"
#include "Misc/App.h"
#include "Misc/EngineVersion.h"
#include "Containers/Ticker.h"

struct FLibraEditorConnectionState final
{
    FLibraEditorTransport transport;
    liberafx::livepreview::SequenceCursor sequence;
    liberafx::host::PreviewClockState previewClock;
    TWeakObjectPtr<ULibraEffectComponent> target;
    double pendingTime = 0;
    bool pendingEvaluation = false;
    bool rejectedSnapshot = false;
    bool awaitingSnapshot = false;
    FString diagnostic;
    TArray<FGuid> watchedIds;
    int64 programSequence = 0;
    int64 lastValueSequence = -1;
    double lastValueTime = 0;
    double nextFeedback = 0;
    FString lastTransportStatus;
};

namespace {
bool ApplyControl(FLibraEditorConnectionState& state, const liberafx::livepreview::FrameView& frame)
{
    auto encoded = FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(frame.payload), static_cast<int32>(frame.payloadLength));
    auto reader = TJsonReaderFactory<>::Create(FString(encoded.Length(), encoded.Get()));
    TSharedPtr<FJsonObject> object;
    FString type;
    if (!FJsonSerializer::Deserialize(reader, object) || !object || !object->TryGetStringField(TEXT("type"), type))
    {
        return false;
    }
    if (type != TEXT("watch_values"))
    {
        return true;
    }
    const TArray<TSharedPtr<FJsonValue>>* ids = nullptr;
    if (!object->TryGetArrayField(TEXT("nodeIds"), ids) || ids->Num() > 64)
    {
        return false;
    }
    auto watched = TArray<FGuid>{};
    for (const auto& value : *ids)
    {
        FString text;
        auto id = FGuid{};
        if (!value->TryGetString(text) || !FGuid::Parse(text, id) || !id.IsValid() || watched.Contains(id))
        {
            return false;
        }
        watched.Add(id);
    }
    state.watchedIds = MoveTemp(watched);
    state.lastValueSequence = -1;
    state.nextFeedback = 0;
    return true;
}

void SendFeedback(FLibraEditorConnectionState& state, int64 programSequence)
{
    auto target = state.target.Get();
    if (!target || !state.sequence.hasProgram() || state.watchedIds.IsEmpty())
    {
        return;
    }
    auto now = FPlatformTime::Seconds();
    auto time = target->LastEvaluatedTimeSeconds;
    if (now < state.nextFeedback && state.lastValueSequence == programSequence && state.lastValueTime == time)
    {
        return;
    }
    state.nextFeedback = now + 0.25;
    auto text = FString{};
    auto writer = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&text);
    writer->WriteObjectStart();
    writer->WriteValue(TEXT("type"), TEXT("values"));
    writer->WriteValue(TEXT("programSequence"), programSequence);
    writer->WriteValue(TEXT("evaluationTime"), time);
    writer->WriteArrayStart(TEXT("values"));
    auto received = int32{0};
    for (const auto& nodeId : state.watchedIds)
    {
        auto value = FVector4{};
        auto width = int32{};
        if (target->ReadRootValue(nodeId, value, width) != ELibraRuntimeResult::Ok)
        {
            continue;
        }
        writer->WriteObjectStart();
        writer->WriteValue(TEXT("nodeId"), nodeId.ToString(EGuidFormats::DigitsWithHyphensLower));
        writer->WriteArrayStart(TEXT("components"));
        for (auto index = 0; index < width; ++index)
        {
            writer->WriteValue(value[index]);
        }
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
        state.lastValueTime = time;
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
    if (!Target && GetOwner())
    {
        Target = GetOwner()->FindComponentByClass<ULibraEffectComponent>();
    }
    if (!Target || !Target->IsRegistered())
    {
        Status = TEXT("A registered Libra Effect component is required for .lfxp preview.");
        return false;
    }
    auto candidate = MakeShared<FLibraEditorConnectionState, ESPMode::NotThreadSafe>();
    auto settings = FLibraEditorTransportSettings{};
    settings.Host = Host;
    settings.Port = static_cast<uint16>(Port);
    settings.AccessToken = AccessToken;
    settings.CertificateFingerprint = CertificateFingerprint;
    settings.PackagePreview = true;
    if (!candidate->transport.Start(settings, Status)) return false;
    candidate->target = Target;
    auto hello = MakeShared<FJsonObject>();
    hello->SetStringField(TEXT("type"), TEXT("hello"));
    hello->SetStringField(TEXT("engine"), TEXT("Unreal"));
    hello->SetStringField(TEXT("role"), TEXT("editor"));
    hello->SetNumberField(TEXT("version"), 1);
    auto capabilities = TArray<TSharedPtr<FJsonValue>>{};
    for (const auto& capability : {TEXT("values"), TEXT("assets"), TEXT("thumbnails"),
        TEXT("resourceIdentity"), TEXT("unrealPackagePreview")})
    {
        capabilities.Add(MakeShared<FJsonValueString>(capability));
    }
    hello->SetArrayField(TEXT("capabilities"), MoveTemp(capabilities));
    auto viewerName = FString(FApp::GetProjectName());
    if (GetOwner()) viewerName += TEXT(" / ") + GetOwner()->GetName();
    hello->SetStringField(TEXT("viewerName"), viewerName);
    hello->SetStringField(TEXT("viewerId"), ViewerId.ToString(EGuidFormats::DigitsWithHyphensLower));
    hello->SetStringField(TEXT("engineVersion"), FEngineVersion::Current().ToString());
    hello->SetStringField(TEXT("platform"), FPlatformProperties::PlatformName());
    auto helloText = FString{};
    auto helloWriter = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&helloText);
    FJsonSerializer::Serialize(hello, helloWriter);
    helloWriter->Close();
    auto helloQueued = candidate->transport.QueueText(
#if WITH_EDITOR
        helloText
#else
        TEXT("{\"type\":\"hello\",\"engine\":\"Unreal\",\"role\":\"runtime\",\"version\":1,\"capabilities\":[\"values\",\"unrealPackagePreview\"]}")
#endif
    );
    if (!helloQueued)
    {
        candidate->transport.Stop();
        Status = TEXT("The preview transport could not queue its viewer hello.");
        return false;
    }
    OriginalPackage = Target->Package;
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
        if (auto target = previous->target.Get())
        {
            RemoveTickPrerequisiteComponent(target);
            if (restoreEffect && PreviewPackage && target->Package == PreviewPackage)
            {
                target->Stop();
                target->SetPackage(OriginalPackage.Get());
            }
        }
        previous->transport.Stop();
    }
    PreviewPackage = nullptr;
    OriginalPackage = nullptr;
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
    if (frame.kind == wire::Kind::Control)
    {
        const auto encoded = FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(frame.payload), static_cast<int32>(frame.payloadLength));
        const auto reader = TJsonReaderFactory<>::Create(FString(encoded.Length(), encoded.Get()));
        TSharedPtr<FJsonObject> object;
        FString type;
        FJsonSerializer::Deserialize(reader, object);
        // The JSON reader is forward-only; reuse the object parsed above.
        if (object && object->TryGetStringField(TEXT("type"), type) &&
            type == TEXT("preview_reset") && object->Values.Num() == 1)
        {
            state->pendingEvaluation = false;
            if (auto target = state->target.Get()) target->Stop();
            return true;
        }
        return ApplyControl(*state, frame);
    }
    if (static_cast<uint16>(frame.kind) == 5)
    {
        if (frame.payloadLength > 1024 * 1024)
        {
            QueueEditorMessage(TEXT("{\"type\":\"asset_result\",\"error\":\"Scene request exceeds the 1 MiB limit.\"}"));
            return true;
        }
        auto utf8 = FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(frame.payload), static_cast<int32>(frame.payloadLength));
        auto request = FString(utf8.Length(), utf8.Get());
        auto reader = TJsonReaderFactory<>::Create(request);
        auto object = TSharedPtr<FJsonObject>{};
        auto type = FString{};
        if (!FJsonSerializer::Deserialize(reader, object) || !object || !object->TryGetStringField(TEXT("type"), type))
        {
            QueueEditorMessage(TEXT("{\"type\":\"asset_result\",\"error\":\"Invalid scene request JSON.\"}"));
            return true;
        }
        if (type == TEXT("asset_catalog") || type == TEXT("asset_details") || type == TEXT("asset_thumbnail") ||
            type == TEXT("resource_identity_project") || type == TEXT("resource_identity_probe") ||
            type == TEXT("resource_identity_schema") ||
            type == TEXT("resource_identity_deliver"))
        {
            if (EditorAssetRequestHandler)
            {
                auto response = EditorAssetRequestHandler(request);
                if (!response.IsEmpty()) QueueEditorMessage(response);
            }
            else QueueEditorMessage(TEXT("{\"type\":\"asset_result\",\"error\":\"Unreal asset discovery is unavailable.\"}"));
        }
        else if (type == TEXT("parameter_delta"))
        {
            auto target = state->target.Get();
            if (!target || !target->IsRegistered() || !PreviewPackage || target->Package != PreviewPackage)
            {
                Status = TEXT("Parameter delta requires the active .lfxp preview package.");
                return false;
            }
            auto sequenceResult = state->sequence.validate(frame);
            if (sequenceResult == wire::SequenceResult::Stale)
            {
                return true;
            }
            if (sequenceResult == wire::SequenceResult::Resync)
            {
                if (!state->rejectedSnapshot && !state->awaitingSnapshot) RequestSnapshot();
                return true;
            }
            struct FDeltaOnlyAdapter final : public liberafx::host::IPackagePreviewAdapter
            {
                ULibraEffectComponent& target;
                explicit FDeltaOnlyAdapter(ULibraEffectComponent& value) : target(value) {}
                auto loadPackage(const std::uint8_t*, std::size_t, bool, double) -> liberafx::Result override
                {
                    return liberafx::Result::UnsupportedBinary;
                }
                auto setPlayback(bool, double) -> liberafx::Result override
                {
                    return liberafx::Result::UnsupportedBinary;
                }
                auto supportsCurveDeltas() const noexcept -> bool override
                {
                    return true;
                }
                auto applyParameterDelta(const liberafx::host::ParameterDeltaUpdate* updates,
                    std::size_t updateCount) -> liberafx::Result override
                {
                    return ToNativeResult(target.SetNodeParameters(updates, updateCount));
                }
                void resetPreview() override
                {
                    target.Stop();
                }
                auto hasBuiltinSpriteMaterials() const -> bool override
                {
                    return true;
                }
            } adapter(*target);
            auto result = liberafx::host::dispatchPackagePreviewFrame(frame, adapter);
            if (result == liberafx::Result::Ok)
            {
                if (state->sequence.commit(frame) != wire::SequenceResult::Accept)
                {
                    RequestSnapshot();
                    return true;
                }
                LastAppliedSequence = static_cast<int64>(FMath::Min(frame.sequence,
                    static_cast<uint64>(MAX_int64)));
                state->diagnostic.Reset();
                LastResult = ELibraRuntimeResult::Ok;
            }
            else
            {
                state->diagnostic = FString::Printf(TEXT("Runtime rejected parameter_delta (result %d)."),
                    static_cast<int32>(result));
                Status = state->diagnostic;
                LastResult = ToLibraResult(result);
                RequestSnapshot();
            }
            OnPreviewUpdated.Broadcast(LastResult, LastAppliedSequence);
            return true;
        }
        else if (type == TEXT("reconnect"))
        {
            auto requestId = FString{};
            auto parsedId = FGuid{};
            if (!object->TryGetStringField(TEXT("requestId"), requestId) ||
                !FGuid::ParseExact(requestId, EGuidFormats::DigitsWithHyphens, parsedId) || !parsedId.IsValid())
            {
                QueueEditorMessage(TEXT("{\"type\":\"asset_result\",\"requestId\":\"\",\"operation\":\"reconnect\",\"error\":\"Invalid reconnect request UUID.\"}"));
                return true;
            }
            auto reply = MakeShared<FJsonObject>();
            reply->SetStringField(TEXT("type"), TEXT("asset_result"));
            reply->SetStringField(TEXT("requestId"), requestId);
            reply->SetStringField(TEXT("operation"), TEXT("reconnect"));
            reply->SetField(TEXT("error"), MakeShared<FJsonValueNull>());
            auto response = FString{};
            auto writer = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&response);
            FJsonSerializer::Serialize(reply, writer);
            writer->Close();
            if (!QueueEditorMessage(response)) return true;
            auto weakThis = TWeakObjectPtr<ULibraEditorConnectionComponent>(this);
            FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateLambda([weakThis](float)
            {
                if (auto connection = weakThis.Get())
                    connection->ConnectToEditor();
                return false;
            }), 0.1f);
        }
        return true;
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
        if (!PreviewPackage && target->Package != OriginalPackage)
        {
            OriginalPackage = target->Package;
        }
        auto result = ELibraRuntimeResult::InvalidBinary;
        auto candidate = static_cast<ULibraSpawnableAsset*>(nullptr);
        if (frame.payloadLength >= 4 && FMemory::Memcmp(frame.payload, "LFXP", 4) == 0)
        {
            candidate = NewObject<ULibraSpawnableAsset>(this);
            result = candidate->SetPackageBytes(TConstArrayView<uint8>(frame.payload,
                static_cast<int32>(frame.payloadLength)));
            if (result == ELibraRuntimeResult::Ok)
            {
                target->Stop();
                result = target->SetPackage(candidate);
            }
            if (result == ELibraRuntimeResult::Ok)
            {
                target->PlayRate = frame.playbackRate;
                result = target->Play(true);
            }
        }
        if (result != ELibraRuntimeResult::Ok)
        {
            state->rejectedSnapshot = true;
            state->awaitingSnapshot = false;
            state->pendingEvaluation = false;
            state->diagnostic = target->LastError.IsEmpty()
                ? TEXT("Runtime rejected the compiled .lfxp effect package.")
                : target->LastError;
            Status = state->diagnostic;
            LastResult = result;
            OnPreviewUpdated.Broadcast(result, static_cast<int64>(FMath::Min(frame.sequence,
                static_cast<uint64>(MAX_int64))));
            return true;
        }
        PreviewPackage = candidate;
        state->programSequence = static_cast<int64>(FMath::Min(frame.sequence,
            static_cast<uint64>(MAX_int64)));
        state->nextFeedback = 0;
        state->lastValueSequence = -1;
        state->rejectedSnapshot = false;
        state->awaitingSnapshot = false;
        state->sequence.commit(frame);
        state->previewClock = {};
        state->diagnostic.Reset();
        state->pendingTime = frame.timeSeconds;
        state->pendingEvaluation = true;
        bEditorPlaying = (frame.flags & 1u) != 0;
        QueueEditorMessage(FString::Printf(TEXT("{\"type\":\"package_preview_ack\",\"programSequence\":%lld}"),
            state->programSequence));
        LastAppliedSequence = state->programSequence;
        LastResult = ELibraRuntimeResult::Ok;
        OnPreviewProgramChanged.Broadcast(LastResult, LastAppliedSequence);
        if (State != state) return true;
        OnPreviewUpdated.Broadcast(LastResult, LastAppliedSequence);
        return true;
    }
    if (frame.kind == wire::Kind::Playback && PreviewPackage && target->Package == PreviewPackage)
    {
        if (state->rejectedSnapshot || state->awaitingSnapshot) return true;
        auto requestedPlaying = (frame.flags & 1u) != 0;
        target->PlayRate = frame.playbackRate;
        if (requestedPlaying && !target->bIsPlaying)
        {
            auto result = target->Play(false);
            if (result != ELibraRuntimeResult::Ok)
            {
                LastResult = result;
                Status = target->LastError;
                RequestSnapshot();
                OnPreviewUpdated.Broadcast(result, LastAppliedSequence);
                return true;
            }
        }
        if (state->sequence.commit(frame) != wire::SequenceResult::Accept)
        {
            RequestSnapshot();
            return true;
        }
        state->diagnostic.Reset();
        state->pendingTime = frame.timeSeconds;
        state->pendingEvaluation = true;
        bEditorPlaying = requestedPlaying;
        LastAppliedSequence = static_cast<int64>(FMath::Min(frame.sequence,
            static_cast<uint64>(MAX_int64)));
        LastResult = ELibraRuntimeResult::Ok;
        OnPreviewUpdated.Broadcast(LastResult, LastAppliedSequence);
        return true;
    }
    Status = TEXT("The editor sent a preview frame unsupported by the .lfxp connection.");
    return false;
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
    SendFeedback(*state, state->programSequence);
    if (state->pendingEvaluation)
    {
        auto target = state->target.Get();
        if (!target || !target->IsRegistered() || !PreviewPackage || target->Package != PreviewPackage)
        {
            Status = TEXT("The active .lfxp preview target is no longer available.");
            CloseConnection(false);
            return;
        }
        auto needsRestart = false;
        auto stepDelta = 1.0 / 60.0;
        auto stepCount = 0;
        liberafx::host::computePreviewTimeStep(state->previewClock, state->pendingTime,
            target->TimeSeconds, bEditorPlaying, needsRestart, stepDelta, stepCount);
        auto result = ELibraRuntimeResult::Ok;
        if (needsRestart)
        {
            result = target->Play(true);
        }
        for (auto step = 0; result == ELibraRuntimeResult::Ok && step < stepCount &&
            target->TimeSeconds + 1e-5 < state->pendingTime && !target->bRootFinished; ++step)
        {
            if (!target->bIsPlaying)
            {
                result = target->Play(false);
                if (result != ELibraRuntimeResult::Ok) break;
            }
            auto stepTime = FMath::Min(stepDelta, state->pendingTime - target->TimeSeconds);
            auto stepFrameDelta = static_cast<float>(stepTime);
            target->TickComponent(stepFrameDelta, LEVELTICK_All, &target->PrimaryComponentTick);
            result = target->LastResult;
        }
        if (result != ELibraRuntimeResult::Ok)
        {
            state->diagnostic = target->LastError.IsEmpty()
                ? TEXT("Runtime failed while evaluating the .lfxp preview time.")
                : target->LastError;
            Status = state->diagnostic;
            LastResult = result;
            state->pendingEvaluation = false;
            OnPreviewUpdated.Broadcast(result, LastAppliedSequence);
            return;
        }
        if (target->TimeSeconds + 1e-5 >= state->pendingTime || target->bRootFinished || stepCount == 0)
        {
            state->pendingEvaluation = false;
            if (!bEditorPlaying && target->bIsPlaying)
            {
                target->Pause();
            }
        }
    }
    Status = state->diagnostic.IsEmpty() ? state->transport.GetStatus() : state->diagnostic;
    auto transportStatus = state->transport.GetStatus();
    if (transportStatus != state->lastTransportStatus)
    {
        state->lastTransportStatus = transportStatus;
        UE_LOG(LogTemp, Display, TEXT("LIBERA FX preview transport: %s"), *transportStatus);
    }
}

bool ULibraEditorConnectionComponent::QueueEditorMessage(const FString& message)
{
    return State && State->transport.QueueText(message);
}
