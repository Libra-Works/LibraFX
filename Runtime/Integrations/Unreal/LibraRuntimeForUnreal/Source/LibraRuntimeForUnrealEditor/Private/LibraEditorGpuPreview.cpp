#include "LibraEditorGpuPreview.h"

#include "LibraGpuEffectAsset.h"
#include "LibraGpuEffectComponent.h"
#include "GameFramework/Actor.h"
#include "Dom/JsonObject.h"
#include "Misc/PackageName.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

namespace
{
    FString Encode(const TSharedRef<FJsonObject>& object)
    {
        auto text = FString{};
        auto writer = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&text);
        FJsonSerializer::Serialize(object, writer);
        writer->Close();
        return text;
    }

    bool ReadSequence(const TSharedPtr<FJsonObject>& object, int64& sequence)
    {
        auto number = double{0};
        if (!object || !object->TryGetNumberField(TEXT("programSequence"), number) ||
            !FMath::IsFinite(number) || number < 0 || number >= static_cast<double>(MAX_int64) ||
            FMath::FloorToDouble(number) != number) return false;
        sequence = static_cast<int64>(number);
        return true;
    }

    uint64 GpuBytes(const ULibraGpuEffectAsset& asset)
    {
        auto registers = uint32{0};
        auto bytes = uint64{0};
        for (const auto& kernel : asset.GetKernels())
        {
            const auto& packet = kernel.Bytes;
            if (packet.Num() < 64) return MAX_uint64;
            auto count = uint32{0};
            for (auto index = 0; index < 4; ++index)
                count |= uint32(packet[16 + index]) << (8 * index);
            registers = FMath::Max(registers, count);
            bytes += uint64(packet.Num());
        }
        auto capacity = uint64(asset.GetCapacity());
        return bytes + capacity * 80 + FMath::Max(uint64{16}, capacity * registers * 16);
    }

    bool SameProgram(const ULibraGpuEffectAsset& left, const ULibraGpuEffectAsset& right)
    {
        if (left.GetContentHash() != right.GetContentHash() || left.GetSourceHash() != right.GetSourceHash() ||
            left.GetCapacity() != right.GetCapacity() || left.GetKernels().Num() != right.GetKernels().Num() ||
            left.GetFlow().Num() != right.GetFlow().Num() || left.GetTextures().Num() != right.GetTextures().Num())
            return false;
        for (auto index = 0; index < left.GetKernels().Num(); ++index)
        {
            const auto& a = left.GetKernels()[index];
            const auto& b = right.GetKernels()[index];
            if (a.Id != b.Id || a.bUpdate != b.bUpdate || a.Bytes != b.Bytes) return false;
        }
        for (auto index = 0; index < left.GetFlow().Num(); ++index)
            if (left.GetFlow()[index].Kind != right.GetFlow()[index].Kind ||
                left.GetFlow()[index].Index != right.GetFlow()[index].Index) return false;
        for (auto index = 0; index < left.GetTextures().Num(); ++index)
            if (left.GetTextures()[index].Id != right.GetTextures()[index].Id ||
                left.GetTextures()[index].Texture != right.GetTextures()[index].Texture) return false;
        return true;
    }

    void Dispose(ULibraGpuEffectComponent* component)
    {
        if (!component) return;
        component->Stop();
        component->DestroyComponent();
    }
}

FLibraEditorGpuPreview::FLibraEditorGpuPreview(AActor* actor) : owner(actor) {}
FLibraEditorGpuPreview::~FLibraEditorGpuPreview() { Reset(); }

bool FLibraEditorGpuPreview::Configure(const FString& json, int64 programSequence)
{
    check(IsInGameThread());
    auto reader = TJsonReaderFactory<>::Create(json);
    auto root = TSharedPtr<FJsonObject>{};
    auto sequence = int64{0};
    auto version = double{0};
    auto type = FString{};
    const TArray<TSharedPtr<FJsonValue>>* programs = nullptr;
    auto fail = [this](const FString& message)
    {
        errors = {message.Left(4096)};
        return false;
    };
    if (FTCHARToUTF8(*json).Length() > 1024 * 1024 || !FJsonSerializer::Deserialize(reader, root) ||
        !ReadSequence(root, sequence))
        return fail(TEXT("Invalid GPU preview envelope."));
    if (sequence < rootSequence || sequence < appliedSequence) return false;
    if (!root->TryGetStringField(TEXT("type"), type) || type != TEXT("gpu_config") ||
        !root->TryGetNumberField(TEXT("version"), version) || version != 1)
        return fail(TEXT("Invalid GPU preview envelope."));
    if (sequence != programSequence || sequence != rootSequence)
        return fail(TEXT("GPU configuration does not match the accepted root program."));
    if (!root->TryGetArrayField(TEXT("programs"), programs) || programs->Num() > 16)
        return fail(TEXT("GPU preview supports at most 16 root emitters."));
    auto actor = owner.Get();
    if (!actor || !actor->GetWorld()) return fail(TEXT("The isolated GPU preview world is unavailable."));

    auto candidates = TArray<FEmitter>{};
    auto ids = TSet<FGuid>{};
    auto totalBytes = uint64{0};
    for (const auto& item : *programs)
    {
        auto object = item.IsValid() && item->Type == EJson::Object ? item->AsObject() : nullptr;
        auto idText = FString{};
        auto id = FGuid{};
        auto deliverySequence = int64{0};
        const TSharedPtr<FJsonObject>* paths = nullptr;
        if (!object || !ReadSequence(object, deliverySequence) || deliverySequence != sequence ||
            !object->TryGetStringField(TEXT("nodeId"), idText) ||
            !FGuid::ParseExact(idText, EGuidFormats::DigitsWithHyphens, id) || !id.IsValid() ||
            ids.Contains(id) || !object->TryGetObjectField(TEXT("resourceAssetPaths"), paths) ||
            (*paths)->Values.Num() > 64)
            return fail(TEXT("Invalid or duplicate GPU root delivery."));
        ids.Add(id);
        auto candidate = FEmitter{};
        candidate.id = id;
        for (const auto& path : (*paths)->Values)
        {
            auto assetPath = FString{};
            if (!path.Value || !path.Value->TryGetString(assetPath) ||
                assetPath.Len() > 1024 ||
                !FPackageName::IsValidObjectPath(assetPath) ||
                (!assetPath.StartsWith(TEXT("/Game/")) && !assetPath.StartsWith(TEXT("/Engine/"))))
                return fail(TEXT("GPU textures require valid /Game or /Engine object paths."));
            candidate.resourcePaths.Add(path.Key, assetPath);
        }
        auto error = FString{};
        candidate.asset.Reset(ULibraGpuEffectAsset::CreateInMemory(actor, Encode(object.ToSharedRef()), error));
        if (!candidate.asset.IsValid()) return fail(TEXT("GPU asset rejected: ") + error);
        candidate.asset->SetFlags(RF_Transient);
        auto bytes = GpuBytes(*candidate.asset.Get());
        if (bytes > ULibraGpuEffectAsset::MaximumGpuBytes ||
            totalBytes > ULibraGpuEffectAsset::MaximumGpuBytes - bytes)
            return fail(TEXT("Aggregate GPU particle, register and packet memory exceeds 256 MiB."));
        totalBytes += bytes;
        candidates.Add(MoveTemp(candidate));
    }

    // Components are staged without ticking; no candidate can dispatch before the whole replacement validates.
    auto staged = TArray<ULibraGpuEffectComponent*>{};
    for (auto& candidate : candidates)
    {
        auto previous = emitters.FindByPredicate([&candidate](const FEmitter& value) { return value.id == candidate.id; });
        auto samePaths = previous && previous->resourcePaths.Num() == candidate.resourcePaths.Num();
        if (samePaths)
            for (const auto& path : candidate.resourcePaths)
                if (auto oldPath = previous->resourcePaths.Find(path.Key); !oldPath || *oldPath != path.Value)
                    samePaths = false;
        if (samePaths && previous->asset.IsValid() && previous->component.IsValid() &&
            SameProgram(*previous->asset.Get(), *candidate.asset.Get()))
        {
            candidate.asset.Reset(previous->asset.Get());
            candidate.component.Reset(previous->component.Get());
            candidate.timeSeconds = previous->timeSeconds;
            candidate.executed = previous->executed;
            continue;
        }
        candidate.component.Reset(NewObject<ULibraGpuEffectComponent>(actor, NAME_None, RF_Transient));
        auto component = candidate.component.Get();
        component->bAutoPlay = false;
        component->SetupAttachment(actor->GetRootComponent());
        component->RegisterComponent();
        component->SetComponentTickEnabled(false);
        staged.Add(component);
        if (!component->IsRegistered() || !component->SetEffect(candidate.asset.Get()) || !component->ResetEffect())
        {
            auto error = component->LastError;
            for (auto value : staged) Dispose(value);
            return fail(TEXT("GPU component rejected: ") + error);
        }
    }
    for (auto& previous : emitters)
        if (!candidates.ContainsByPredicate([&previous](const FEmitter& value)
            { return value.component.Get() == previous.component.Get(); }))
            Dispose(previous.component.Get());
    for (auto component : staged) actor->AddInstanceComponent(component);
    emitters = MoveTemp(candidates);
    appliedSequence = sequence;
    errors.Reset();
    if (emitters.IsEmpty()) seekBlocked = false;
    if (seekBlocked)
        errors = {TEXT("GPU timeline scrubbing is unsupported. Return the timeline to zero to restart simulation.")};
    for (auto& emitter : emitters)
        if (remotePlaying && !seekBlocked) emitter.component->Play();
        else emitter.component->Pause();
    return true;
}

void FLibraEditorGpuPreview::SetPlayback(int64 programSequence, bool playing, double timeSeconds, bool programChanged, double rate)
{
    check(IsInGameThread());
    if (!FMath::IsFinite(timeSeconds) || timeSeconds < 0 || !FMath::IsFinite(rate) || rate < 0) return;
    if (programSequence < rootSequence) return;
    auto now = FPlatformTime::Seconds();
    rootSequence = programSequence;
    if (hasRemoteTime && !programChanged)
    {
        auto delta = timeSeconds - lastRemoteTime;
        auto expected = remotePlaying ? FMath::Max(0.0, now - lastRemoteArrival) * playbackRate : 0.0;
        if (delta < -0.00001 || (seekBlocked && timeSeconds <= 0.00001))
        {
            seekBlocked = false;
            for (auto& emitter : emitters)
            {
                emitter.component->ResetEffect();
                emitter.timeSeconds = 0;
                emitter.executed = false;
            }
            if (timeSeconds > 0.00001)
                errors = {TEXT("GPU backward seek resets simulation to zero; arbitrary scrubbing is unsupported.")};
            else errors.Reset();
        }
        else if (!emitters.IsEmpty() && delta > expected + 0.25)
        {
            seekBlocked = true;
            errors = {TEXT("GPU forward seek is unsupported. Return the timeline to zero to restart simulation.")};
        }
    }
    playbackRate = rate;
    remotePlaying = playing && rate > 0;
    hasRemoteTime = true;
    lastRemoteTime = timeSeconds;
    lastRemoteArrival = now;
    for (auto& emitter : emitters)
        if (remotePlaying && !seekBlocked) emitter.component->Play();
        else emitter.component->Pause();
}

void FLibraEditorGpuPreview::Tick(float deltaTime, bool connected)
{
    check(IsInGameThread());
    if (!FMath::IsFinite(deltaTime) || deltaTime <= 0) return;
    auto step = double(deltaTime) * playbackRate;
    if (!FMath::IsFinite(step) || step > TNumericLimits<float>::Max())
    {
        seekBlocked = true;
        errors = {TEXT("GPU playback rate exceeds the finite simulation timestep range.")};
    }
    for (auto& emitter : emitters)
    {
        auto component = emitter.component.Get();
        if (!connected || !remotePlaying || seekBlocked) { component->Pause(); continue; }
        if (!component->bIsPlaying && !component->Play()) continue;
        if (step <= 0) continue;
        component->TickComponent(static_cast<float>(step), LEVELTICK_All, &component->PrimaryComponentTick);
        if (component->LastError.IsEmpty())
        {
            emitter.timeSeconds += step;
            emitter.executed = true;
        }
    }
}

void FLibraEditorGpuPreview::Reset()
{
    for (auto& emitter : emitters) Dispose(emitter.component.Get());
    emitters.Reset();
    errors.Reset();
    rootSequence = 0;
    appliedSequence = -1;
    hasRemoteTime = remotePlaying = seekBlocked = false;
    lastRemoteTime = lastRemoteArrival = 0;
    playbackRate = 1;
}

FString FLibraEditorGpuPreview::Status(int64 programSequence) const
{
    auto status = MakeShared<FJsonObject>();
    status->SetStringField(TEXT("type"), TEXT("gpu_status"));
    status->SetNumberField(TEXT("version"), 1);
    status->SetNumberField(TEXT("programSequence"), appliedSequence >= 0 ? appliedSequence : programSequence);
    if (appliedSequence >= 0 && appliedSequence != programSequence)
        status->SetNumberField(TEXT("requestedProgramSequence"), programSequence);
    status->SetStringField(TEXT("mode"), TEXT("GpuRegisterInterpreter"));
    auto executed = false;
    auto programs = TArray<TSharedPtr<FJsonValue>>{};
    for (const auto& emitter : emitters)
    {
        auto item = MakeShared<FJsonObject>();
        item->SetStringField(TEXT("nodeId"), emitter.id.ToString(EGuidFormats::DigitsWithHyphensLower));
        item->SetStringField(TEXT("contentHash"), emitter.asset->GetContentHash());
        item->SetNumberField(TEXT("capacity"), emitter.asset->GetCapacity());
        item->SetBoolField(TEXT("playing"), emitter.component->bIsPlaying);
        item->SetNumberField(TEXT("timeSeconds"), emitter.timeSeconds);
        auto itemErrors = TArray<TSharedPtr<FJsonValue>>{};
        if (!emitter.component->LastError.IsEmpty())
            itemErrors.Add(MakeShared<FJsonValueString>(emitter.component->LastError.Left(4096)));
        item->SetArrayField(TEXT("errors"), MoveTemp(itemErrors));
        programs.Add(MakeShared<FJsonValueObject>(item));
        executed |= emitter.executed;
    }
    status->SetBoolField(TEXT("runtimeExecuted"), executed);
    status->SetArrayField(TEXT("programs"), MoveTemp(programs));
    auto errorValues = TArray<TSharedPtr<FJsonValue>>{};
    for (const auto& error : errors) errorValues.Add(MakeShared<FJsonValueString>(error));
    status->SetArrayField(TEXT("errors"), MoveTemp(errorValues));
    return Encode(status);
}

#if WITH_DEV_AUTOMATION_TESTS
#include "Components/SceneComponent.h"
#include "Misc/AutomationTest.h"
#include "Misc/Base64.h"
#include "PreviewScene.h"

namespace
{
    TSharedRef<FJsonObject> PreviewDelivery(int64 sequence, const FGuid& id, int32 capacity = 4, bool extraRegister = false)
    {
        auto packet = TArray<uint8>{};
        packet.SetNumZeroed(extraRegister ? 128 : 64);
        FMemory::Memcpy(packet.GetData(), "LGPK", 4);
        auto word = [&packet](int32 offset, uint32 value)
        {
            for (auto index = 0; index < 4; ++index) packet[offset + index] = uint8(value >> (8 * index));
        };
        word(4, 1);
        word(8, 64);
        word(24, 80);
        word(28, 48);
        if (extraRegister)
        {
            word(12, 1);
            word(16, 1);
            for (auto offset : {72, 76, 80, 92}) word(offset, MAX_uint32);
        }
        auto metadata = MakeShared<FJsonObject>();
        metadata->SetNumberField(TEXT("version"), 1);
        metadata->SetNumberField(TEXT("capacity"), capacity);
        metadata->SetNumberField(TEXT("particleStride"), 80);
        metadata->SetNumberField(TEXT("uniformBufferBytes"), 48);
        metadata->SetStringField(TEXT("contentHash"), TEXT("44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a"));
        metadata->SetStringField(TEXT("sourceHash"), TEXT("cc8bac8c92ab27cddfc0cbf92f17d23b43f2cae747744de80f25fe546a24ff5e"));
        metadata->SetStringField(TEXT("purpose"), TEXT("SourceExport"));
        metadata->SetBoolField(TEXT("runtimeExecuted"), false);
        metadata->SetStringField(TEXT("format"), TEXT("Usf"));
        metadata->SetArrayField(TEXT("resources"), {});
        auto flow = MakeShared<FJsonObject>();
        flow->SetStringField(TEXT("kind"), TEXT("Spawn"));
        flow->SetStringField(TEXT("kernelId"), TEXT("spawn"));
        metadata->SetArrayField(TEXT("flow"), {MakeShared<FJsonValueObject>(flow)});
        auto kernel = MakeShared<FJsonObject>();
        kernel->SetStringField(TEXT("id"), TEXT("spawn"));
        kernel->SetStringField(TEXT("stage"), TEXT("Spawn"));
        kernel->SetStringField(TEXT("base64"), FBase64::Encode(packet));
        auto delivery = MakeShared<FJsonObject>();
        delivery->SetNumberField(TEXT("programSequence"), sequence);
        delivery->SetStringField(TEXT("nodeId"), id.ToString(EGuidFormats::DigitsWithHyphensLower));
        delivery->SetStringField(TEXT("canonicalJson"), TEXT("{}"));
        delivery->SetStringField(TEXT("shaderSource"), TEXT("source only"));
        delivery->SetObjectField(TEXT("metadata"), metadata);
        delivery->SetArrayField(TEXT("registerKernels"), {MakeShared<FJsonValueObject>(kernel)});
        delivery->SetObjectField(TEXT("resourceAssetPaths"), MakeShared<FJsonObject>());
        return delivery;
    }

    FString PreviewConfig(int64 sequence, const TArray<TSharedPtr<FJsonValue>>& programs)
    {
        auto config = MakeShared<FJsonObject>();
        config->SetStringField(TEXT("type"), TEXT("gpu_config"));
        config->SetNumberField(TEXT("version"), 1);
        config->SetNumberField(TEXT("programSequence"), sequence);
        config->SetArrayField(TEXT("programs"), programs);
        return Encode(config);
    }
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraGpuPreviewTransactionTest, "Libra.Editor.Preview.GpuSceneTransactions",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraGpuPreviewTransactionTest::RunTest(const FString&)
{
    auto construction = FPreviewScene::ConstructionValues{};
    construction.SetTransactional(false).SetCreatePhysicsScene(false).AllowAudioPlayback(false);
    auto scene = MakeShared<FPreviewScene>(construction);
    auto parameters = FActorSpawnParameters{};
    parameters.ObjectFlags = RF_Transient;
    auto actor = scene->GetWorld()->SpawnActor<AActor>(AActor::StaticClass(), FTransform::Identity, parameters);
    if (!TestNotNull(TEXT("GPU fixture actor belongs to an isolated world"), actor)) return false;
    auto cpuRoot = NewObject<USceneComponent>(actor, NAME_None, RF_Transient);
    actor->SetRootComponent(cpuRoot);
    actor->AddInstanceComponent(cpuRoot);
    cpuRoot->RegisterComponent();
    auto preview = FLibraEditorGpuPreview(actor);
    auto firstId = FGuid::NewGuid();
    auto secondId = FGuid::NewGuid();
    preview.SetPlayback(1, false, 0, true);
    TestTrue(TEXT("Real GPU component creation accepts a validated frontend delivery"),
        preview.Configure(PreviewConfig(1, {MakeShared<FJsonValueObject>(PreviewDelivery(1, firstId))}), 1));
    if (preview.emitters.Num() != 1) return false;
    auto firstComponent = preview.emitters[0].component.Get();
    TestFalse(TEXT("Editor GPU automatic ticking is disabled"), firstComponent->IsComponentTickEnabled());
    preview.Tick(0.016f, true);
    TestFalse(TEXT("Paused creation does not claim GPU execution"), preview.emitters[0].executed);
    preview.SetPlayback(1, true, 0, false);
    preview.Tick(0.016f, true);
    TestTrue(TEXT("Manual GPU step submits native interpreter work"), preview.emitters[0].executed);
    preview.SetPlayback(2, false, 0.016, true);
    TestTrue(TEXT("A layout-only program replacement validates"),
        preview.Configure(PreviewConfig(2, {MakeShared<FJsonValueObject>(PreviewDelivery(2, firstId))}), 2));
    TestTrue(TEXT("Unchanged GPU body retains its component and simulation"),
        preview.emitters[0].component.Get() == firstComponent && preview.emitters[0].timeSeconds > 0);
    TestTrue(TEXT("A valid changed packet with identical IR and source labels is accepted"),
        preview.Configure(PreviewConfig(2, {MakeShared<FJsonValueObject>(PreviewDelivery(2, firstId, 4, true))}), 2));
    TestTrue(TEXT("Packet-byte changes replace and reset the simulation despite identical hashes"),
        preview.emitters[0].component.Get() != firstComponent && preview.emitters[0].timeSeconds == 0);
    firstComponent = preview.emitters[0].component.Get();
    preview.SetPlayback(3, false, 0.016, true);
    auto invalid = PreviewDelivery(3, secondId);
    invalid->SetStringField(TEXT("shaderSource"), TEXT("incorrect source hash"));
    TestFalse(TEXT("One bad candidate rejects the complete replacement"),
        preview.Configure(PreviewConfig(3, {
            MakeShared<FJsonValueObject>(PreviewDelivery(3, FGuid::NewGuid())),
            MakeShared<FJsonValueObject>(invalid)}), 3));
    TestTrue(TEXT("Invalid transaction retains every previous component"),
        preview.emitters.Num() == 1 && preview.emitters[0].component.Get() == firstComponent && preview.appliedSequence == 2);
    TestFalse(TEXT("Duplicate GPU node IDs are rejected"),
        preview.Configure(PreviewConfig(3, {MakeShared<FJsonValueObject>(PreviewDelivery(3, firstId)),
            MakeShared<FJsonValueObject>(PreviewDelivery(3, firstId))}), 3));
    TestFalse(TEXT("Aggregate memory is bounded across individually valid emitters"),
        preview.Configure(PreviewConfig(3, {MakeShared<FJsonValueObject>(PreviewDelivery(3, firstId, 2000000)),
            MakeShared<FJsonValueObject>(PreviewDelivery(3, secondId, 2000000))}), 3));
    TestFalse(TEXT("Stale configuration cannot clear current GPU components"), preview.Configure(PreviewConfig(2, {}), 3));
    TestTrue(TEXT("Current empty replacement clears GPU components"), preview.Configure(PreviewConfig(3, {}), 3));
    TestTrue(TEXT("GPU clearing preserves the shared CPU scene root"), cpuRoot->IsRegistered() && actor->GetRootComponent() == cpuRoot);
    preview.Reset();
    preview.SetPlayback(0, false, 0, true);
    TestTrue(TEXT("A new connection generation may restart sequence numbering"),
        preview.Configure(PreviewConfig(0, {MakeShared<FJsonValueObject>(PreviewDelivery(0, firstId))}), 0));
    preview.SetPlayback(0, false, 1, false);
    TestTrue(TEXT("Forward scrubbing reports unsupported operation and pauses GPU"), preview.seekBlocked && !preview.emitters[0].component->bIsPlaying);
    preview.SetPlayback(0, true, 0, false);
    TestTrue(TEXT("Returning to zero resets GPU simulation deterministically"),
        !preview.seekBlocked && preview.emitters[0].timeSeconds == 0 && !preview.emitters[0].executed);
    return true;
}
#endif
