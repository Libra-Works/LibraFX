#include "LibraLiveGameEndpointComponent.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "Misc/Base64.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace {
auto targetText(liberafx::InstanceId id) -> FString
{ return FString::Printf(TEXT("%016llx:%016llx"), static_cast<unsigned long long>(id.generation), static_cast<unsigned long long>(id.slot)); }
auto jsonText(const TSharedRef<FJsonObject>& object) -> FString
{
    auto text = FString{};
    auto writer = TJsonWriterFactory<>::Create(&text);
    FJsonSerializer::Serialize(object, writer);
    return text;
}
auto resultText(liberafx::Result result) -> FString
{
    switch (result)
    {
    case liberafx::Result::Ok: return TEXT("Ok");
    case liberafx::Result::InvalidHandle: return TEXT("InvalidHandle");
    case liberafx::Result::UnsupportedBinary: return TEXT("UnsupportedBinary");
    case liberafx::Result::InvalidBinary: return TEXT("InvalidBinary");
    case liberafx::Result::NotReady: return TEXT("NotReady");
    case liberafx::Result::QueueRejected: return TEXT("QueueRejected");
    case liberafx::Result::Busy: return TEXT("Busy");
    default: return FString::Printf(TEXT("RuntimeError%d"), static_cast<int32>(result));
    }
}
auto parseUnsigned(const FString& text, uint64& out, int32 radix) -> bool
{
    if (text.IsEmpty() || text.Len() > (radix == 16 ? 16 : 20)) return false;
    out = 0;
    for (auto character : text) {
        if (!(character >= '0' && character <= '9') &&
            !(radix == 16 && ((character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F'))))
            return false;
        auto digit = static_cast<uint64>(character >= '0' && character <= '9' ? character - '0' :
            character >= 'a' && character <= 'f' ? character - 'a' + 10 : character - 'A' + 10);
        if (out > (UINT64_MAX - digit) / static_cast<uint64>(radix)) return false;
        out = out * static_cast<uint64>(radix) + digit;
    }
    return true;
}
}
ULibraLiveGameEndpointComponent::ULibraLiveGameEndpointComponent()
{ PrimaryComponentTick.bCanEverTick = true; }
ULibraLiveGameEndpointComponent::~ULibraLiveGameEndpointComponent() = default;
void ULibraLiveGameEndpointComponent::BeginPlay() { Super::BeginPlay(); StartEndpoint(); }
void ULibraLiveGameEndpointComponent::EndPlay(const EEndPlayReason::Type reason) { StopEndpoint(); Super::EndPlay(reason); }
void ULibraLiveGameEndpointComponent::OnComponentDestroyed(bool destroyingHierarchy)
{ StopEndpoint(); Super::OnComponentDestroyed(destroyingHierarchy); }
void ULibraLiveGameEndpointComponent::TickComponent(float deltaTime, ELevelTick tickType, FActorComponentTickFunction* tickFunction)
{ Super::TickComponent(deltaTime, tickType, tickFunction); PumpEndpoint(); }
auto ULibraLiveGameEndpointComponent::StartEndpoint() -> bool
{
    check(IsInGameThread());
    if (session) return true;
    if (!bDevelopmentOptIn) return false;
    if (IsolatedSessionRoot.IsEmpty() || FPaths::IsRelative(IsolatedSessionRoot) ||
        !IFileManager::Get().DirectoryExists(*IsolatedSessionRoot))
    { LastError = TEXT("Supply an existing absolute private isolated session root, never a project directory."); return false; }
    session = FLibraLiveGameSession::Create(true, GetOwner(), AssetBindings);
    if (!session) { LastError = TEXT("Live endpoint requires a non-shipping game-thread owner and explicit development opt-in."); return false; }
    lastSequence = 0;
    hasWaiting = false;
    auto id = session->GetSessionId();
    identity = FString::Printf(TEXT("%016llx%016llx"), static_cast<unsigned long long>(id.high), static_cast<unsigned long long>(id.low));
    EndpointDirectory = FPaths::Combine(IsolatedSessionRoot, TEXT("unreal-") + identity);
    if (!IFileManager::Get().MakeDirectory(*EndpointDirectory, true))
    { LastError = TEXT("Could not create isolated endpoint directory."); StopEndpoint(); return false; }
    auto descriptor = MakeShared<FJsonObject>();
    descriptor->SetNumberField(TEXT("protocol"), 1);
    descriptor->SetStringField(TEXT("session"), identity);
    descriptor->SetStringField(TEXT("engine"), TEXT("Unreal"));
    descriptor->SetStringField(TEXT("transport"), TEXT("isolated-local-spool"));
    descriptor->SetStringField(TEXT("supportedUpdate"), TEXT("restart-root-render-v1-v2"));
    descriptor->SetNumberField(TEXT("process"), FPlatformProcess::GetCurrentProcessId());
    Publish(TEXT("endpoint.json"), jsonText(descriptor));
    if (!IFileManager::Get().FileExists(*FPaths::Combine(EndpointDirectory, TEXT("endpoint.json"))))
    { StopEndpoint(); return false; }
    return true;
}
void ULibraLiveGameEndpointComponent::Publish(const FString& name, const FString& text)
{
    auto path = FPaths::Combine(EndpointDirectory, name);
    auto temporary = path + TEXT(".tmp");
    if (!FFileHelper::SaveStringToFile(text, *temporary, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM) ||
        !IFileManager::Get().Move(*path, *temporary, true, true))
        LastError = TEXT("Could not publish isolated live-session response.");
}
void ULibraLiveGameEndpointComponent::Reply(const FString& sequence, liberafx::Result result,
    const FString& error, const FString& target)
{
    auto response = MakeShared<FJsonObject>();
    response->SetNumberField(TEXT("protocol"), 1);
    response->SetStringField(TEXT("session"), identity);
    response->SetStringField(TEXT("sequence"), sequence);
    response->SetStringField(TEXT("result"), resultText(result));
    response->SetStringField(TEXT("error"), error);
    response->SetStringField(TEXT("target"), target);
    auto instances = TArray<TSharedPtr<FJsonValue>>{};
    for (const auto& live : session->List())
    {
        auto item = MakeShared<FJsonObject>();
        item->SetStringField(TEXT("target"), targetText(live.id));
        item->SetStringField(TEXT("label"), live.label);
        item->SetBoolField(TEXT("finished"), live.finished);
        item->SetNumberField(TEXT("renderCount"), live.renderObjects);
        instances.Add(MakeShared<FJsonValueObject>(item));
    }
    response->SetArrayField(TEXT("instances"), instances);
    LastError = error;
    Publish(TEXT("response.json"), jsonText(response));
}
void ULibraLiveGameEndpointComponent::PumpEndpoint()
{
    check(IsInGameThread());
    if (!session) return;
    session->Process();
    if (hasWaiting)
    {
        auto completion = FLibraLiveGameSession::FCompletion{};
        auto result = session->TakeCompletion(waitingRequest, completion);
        if (result == liberafx::Result::NotReady) return;
        Reply(waitingSequence, result == liberafx::Result::Ok ? completion.result : result,
            completion.error, completion.result == liberafx::Result::Ok ? targetText(completion.instance) : FString{});
        hasWaiting = false;
    }
    auto path = FPaths::Combine(EndpointDirectory, TEXT("request.json"));
    auto size = IFileManager::Get().FileSize(*path);
    if (size < 0) return;
    if (size > 24 * 1024 * 1024) { LastError = TEXT("Oversized isolated command rejected."); return; }
    auto text = FString{};
    if (!FFileHelper::LoadFileToString(text, *path)) return;
    IFileManager::Get().Delete(*path);
    auto command = TSharedPtr<FJsonObject>{};
    auto reader = TJsonReaderFactory<>::Create(text);
    auto sequenceText = FString{}, commandSession = FString{}, operation = FString{};
    auto sequence = uint64{};
    auto protocol = double{};
    if (!FJsonSerializer::Deserialize(reader, command) || !command ||
        !command->TryGetNumberField(TEXT("protocol"), protocol) || protocol != 1 ||
        !command->TryGetStringField(TEXT("session"), commandSession) || commandSession != identity ||
        !command->TryGetStringField(TEXT("sequence"), sequenceText) || !parseUnsigned(sequenceText, sequence, 10) ||
        sequence <= lastSequence || !command->TryGetStringField(TEXT("operation"), operation))
    { Reply(sequenceText, liberafx::Result::InvalidHandle, TEXT("Wrong session/protocol or replayed command.")); return; }
    lastSequence = sequence;
    if (operation == TEXT("list")) { Reply(sequenceText, liberafx::Result::Ok, {}); return; }
    auto target = liberafx::InstanceId{};
    target.session = session->GetSessionId();
    target.incarnation = 1;
    if (operation != TEXT("create"))
    {
        auto selection = FString{}, generation = FString{}, slot = FString{};
        if (!command->TryGetStringField(TEXT("target"), selection) || !selection.Split(TEXT(":"), &generation, &slot) ||
            !parseUnsigned(generation, target.generation, 16) || !parseUnsigned(slot, target.slot, 16))
        { Reply(sequenceText, liberafx::Result::InvalidHandle, TEXT("Invalid selected instance.")); return; }
    }
    auto result = liberafx::Result::InvalidArgument;
    auto request = liberafx::RequestId{};
    if (operation == TEXT("create") || operation == TEXT("replace"))
    {
        auto encoded = FString{}, label = FString{}, policy = FString{};
        auto bytes = TArray<uint8>{};
        if (!command->TryGetStringField(TEXT("packageBase64"), encoded) || encoded.Len() > 22369624 ||
            !FBase64::Decode(encoded, bytes))
        { Reply(sequenceText, liberafx::Result::InvalidArgument, TEXT("Supply at most 16 MiB compiled LFXP bytes.")); return; }
        command->TryGetStringField(TEXT("label"), label);
        command->TryGetStringField(TEXT("policy"), policy);
        if (operation == TEXT("replace") && policy != TEXT("restart"))
        { Reply(sequenceText, liberafx::Result::UnsupportedBinary, TEXT("Explicit restart required; state migration is unsupported.")); return; }
        result = operation == TEXT("create") ? session->SubmitCreate(bytes, label, request) :
            session->SubmitReplace(target, bytes, liberafx::ReplacementPolicy::Restart, request);
    }
    else if (operation == TEXT("destroy")) result = session->SubmitDestroy(target, request);
    else if (operation == TEXT("end")) result = session->SubmitNaturalEnd(target, request);
    if (result != liberafx::Result::Ok) { Reply(sequenceText, result, TEXT("Request was not accepted.")); return; }
    hasWaiting = true;
    waitingSequence = sequenceText;
    waitingRequest = request;
}
void ULibraLiveGameEndpointComponent::StopEndpoint()
{
    check(IsInGameThread());
    if (session) { session->ShutdownAndForceDestroy(); session.Reset(); }
    if (!EndpointDirectory.IsEmpty()) IFileManager::Get().Delete(*FPaths::Combine(EndpointDirectory, TEXT("endpoint.json")));
    hasWaiting = false;
}
