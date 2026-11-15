#include "LibraPreviewDiscovery.h"
#include "LibraPreviewConnectionInvitation.h"

#include <atomic>
#include <chrono>
#include <mutex>
#include <thread>

#if PLATFORM_WINDOWS
#include "Windows/WindowsHWrapper.h"
#endif

namespace
{
    constexpr char discoveryRequest[] = "librafx_preview_connection\n";
    constexpr auto discoveryPrefix = TEXT("LibraFX.Preview.");
    constexpr auto maximumPeers = 32;
    constexpr auto maximumReplyBytes = 4096;

    bool validDiscoveryPipe(const FString& name, uint32& processId)
    {
        if (!name.StartsWith(discoveryPrefix, ESearchCase::CaseSensitive)) return false;
        auto suffix = name.Mid(FCString::Strlen(discoveryPrefix));
        if (suffix.IsEmpty() || suffix.Len() > 10 || suffix[0] == TEXT('0')) return false;
        auto value = uint64{0};
        for (auto c : suffix)
        {
            if (c < TEXT('0') || c > TEXT('9')) return false;
            value = value * 10 + c - TEXT('0');
        }
        if (!value || value > MAX_uint32) return false;
        processId = static_cast<uint32>(value);
        return true;
    }

    bool decodeDiscoveryReply(const TArray<uint8>& bytes, FString& invitation)
    {
        if (bytes.IsEmpty() || bytes.Num() > maximumReplyBytes || bytes.Last() != '\n') return false;
        for (auto index = 0; index < bytes.Num() - 1; ++index)
            if (!bytes[index] || bytes[index] == '\n') return false;
        auto text = FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(bytes.GetData()), bytes.Num() - 1);
        auto candidate = FString(text.Length(), text.Get());
        auto roundtrip = FTCHARToUTF8(*candidate);
        if (roundtrip.Length() != bytes.Num() - 1 ||
            FMemory::Memcmp(roundtrip.Get(), bytes.GetData(), roundtrip.Length())) return false;
        auto parsed = FLibraPreviewConnectionInvitation{};
        auto error = FString{};
        if (!ParseLibraPreviewConnectionInvitation(candidate, parsed, error)) return false;
        invitation = MoveTemp(candidate);
        return true;
    }

    bool validDiscoveryOverride(const FString& name)
    {
        if (!name.StartsWith(discoveryPrefix, ESearchCase::CaseSensitive) || name.Len() > 96 ||
            name.Len() == FCString::Strlen(discoveryPrefix)) return false;
        for (auto c : name.Mid(FCString::Strlen(discoveryPrefix)))
            if (!((c >= TEXT('0') && c <= TEXT('9')) || (c >= TEXT('a') && c <= TEXT('z')) ||
                (c >= TEXT('A') && c <= TEXT('Z')) || c == TEXT('.') || c == TEXT('-') || c == TEXT('_'))) return false;
        return true;
    }

#if PLATFORM_WINDOWS
    class FDiscoveryHandle final
    {
    public:
        explicit FDiscoveryHandle(HANDLE value = nullptr) : handle(value) {}
        ~FDiscoveryHandle() { if (IsValid()) CloseHandle(handle); }
        bool IsValid() const { return handle && handle != INVALID_HANDLE_VALUE; }
        HANDLE Get() const { return handle; }
        FDiscoveryHandle(const FDiscoveryHandle&) = delete;
        FDiscoveryHandle& operator=(const FDiscoveryHandle&) = delete;
    private:
        HANDLE handle;
    };

    bool sameDiscoveryUser(HANDLE process)
    {
        auto current = HANDLE{};
        auto remote = HANDLE{};
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &current)) return false;
        auto currentToken = FDiscoveryHandle(current);
        if (!OpenProcessToken(process, TOKEN_QUERY, &remote)) return false;
        auto remoteToken = FDiscoveryHandle(remote);
        auto tokenUser = [](HANDLE token, TArray<uint8>& buffer)
        {
            auto size = DWORD{0};
            GetTokenInformation(token, TokenUser, nullptr, 0, &size);
            if (!size || size > 64 * 1024) return false;
            buffer.SetNumUninitialized(static_cast<int32>(size));
            return GetTokenInformation(token, TokenUser, buffer.GetData(), size, &size) != 0;
        };
        auto currentUser = TArray<uint8>{};
        auto remoteUser = TArray<uint8>{};
        if (!tokenUser(currentToken.Get(), currentUser) || !tokenUser(remoteToken.Get(), remoteUser)) return false;
        return EqualSid(reinterpret_cast<TOKEN_USER*>(currentUser.GetData())->User.Sid,
            reinterpret_cast<TOKEN_USER*>(remoteUser.GetData())->User.Sid) != 0;
    }

    bool trustedDiscoveryPeer(HANDLE pipe, uint32 expectedId, bool automationOverride)
    {
        auto actualId = ULONG{0};
        if (!GetNamedPipeServerProcessId(pipe, &actualId) || (expectedId && actualId != expectedId)) return false;
        auto process = FDiscoveryHandle(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, actualId));
        if (!process.IsValid() || !sameDiscoveryUser(process.Get())) return false;
        auto image = TArray<WCHAR>{};
        image.SetNumUninitialized(32768);
        auto length = DWORD{32768};
        if (!QueryFullProcessImageNameW(process.Get(), 0, image.GetData(), &length)) return false;
        auto path = FString(static_cast<int32>(length), image.GetData());
        auto separator = int32{INDEX_NONE};
        path.FindLastChar(TEXT('\\'), separator);
        return automationOverride || path.Mid(separator + 1).Equals(TEXT("LiberaFx.exe"), ESearchCase::IgnoreCase);
    }

    using FDiscoveryDeadline = std::chrono::steady_clock::time_point;

    DWORD remainingDiscoveryTime(FDiscoveryDeadline deadline)
    {
        auto remaining = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - std::chrono::steady_clock::now()).count();
        return remaining > 0 ? static_cast<DWORD>(remaining) : 0;
    }

    bool discoveryIo(HANDLE pipe, HANDLE cancellation, bool write, void* data, DWORD length,
        FDiscoveryDeadline deadline, DWORD& transferred)
    {
        if (!remainingDiscoveryTime(deadline) || WaitForSingleObject(cancellation, 0) == WAIT_OBJECT_0) return false;
        auto event = FDiscoveryHandle(CreateEventW(nullptr, true, false, nullptr));
        if (!event.IsValid()) return false;
        auto operation = OVERLAPPED{};
        operation.hEvent = event.Get();
        transferred = 0;
        auto completed = write ? WriteFile(pipe, data, length, &transferred, &operation) :
            ReadFile(pipe, data, length, &transferred, &operation);
        if (completed) return true;
        if (GetLastError() != ERROR_IO_PENDING) return false;
        HANDLE waits[] = {cancellation, event.Get()};
        auto result = WaitForMultipleObjects(2, waits, false, remainingDiscoveryTime(deadline));
        if (result != WAIT_OBJECT_0 + 1)
        {
            CancelIoEx(pipe, &operation);
            // The OVERLAPPED and its event must outlive cancellation completion.
            GetOverlappedResult(pipe, &operation, &transferred, true);
            return false;
        }
        return GetOverlappedResult(pipe, &operation, &transferred, false) != 0;
    }

    bool queryDiscoveryPipe(const FString& name, HANDLE cancellation, bool automationOverride, FString& invitation)
    {
        auto processId = uint32{0};
        if (!validDiscoveryPipe(name, processId) && !(automationOverride && validDiscoveryOverride(name))) return false;
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
        auto path = FString(TEXT("\\\\.\\pipe\\")) + name;
        auto raw = INVALID_HANDLE_VALUE;
        while (remainingDiscoveryTime(deadline) && WaitForSingleObject(cancellation, 0) != WAIT_OBJECT_0)
        {
            raw = CreateFileW(*path, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
            if (raw != INVALID_HANDLE_VALUE) break;
            if (GetLastError() != ERROR_PIPE_BUSY || WaitForSingleObject(cancellation, 10) == WAIT_OBJECT_0) return false;
        }
        auto pipe = FDiscoveryHandle(raw);
        if (!pipe.IsValid() || !trustedDiscoveryPeer(pipe.Get(), processId, automationOverride)) return false;
        // Arm the bounded response read first: Root disconnects immediately after its write.
        auto responseEvent = FDiscoveryHandle(CreateEventW(nullptr, true, false, nullptr));
        if (!responseEvent.IsValid()) return false;
        auto responseOperation = OVERLAPPED{};
        responseOperation.hEvent = responseEvent.Get();
        uint8 response[maximumReplyBytes];
        auto responseBytes = DWORD{0};
        auto responseReady = ReadFile(pipe.Get(), response, sizeof(response), &responseBytes, &responseOperation) != 0;
        if (!responseReady && GetLastError() != ERROR_IO_PENDING) return false;
        auto cancelResponse = [&]
        {
            if (!responseReady)
            {
                CancelIoEx(pipe.Get(), &responseOperation);
                GetOverlappedResult(pipe.Get(), &responseOperation, &responseBytes, true);
            }
        };
        auto transferred = DWORD{0};
        constexpr auto requestBytes = static_cast<DWORD>(sizeof(discoveryRequest) - 1);
        if (!discoveryIo(pipe.Get(), cancellation, true, const_cast<char*>(discoveryRequest),
            requestBytes, deadline, transferred) || transferred != requestBytes)
        { cancelResponse(); return false; }
        if (!responseReady)
        {
            HANDLE waits[] = {cancellation, responseEvent.Get()};
            if (WaitForMultipleObjects(2, waits, false, remainingDiscoveryTime(deadline)) != WAIT_OBJECT_0 + 1)
            { cancelResponse(); return false; }
            responseReady = GetOverlappedResult(pipe.Get(), &responseOperation, &responseBytes, false) != 0;
        }
        if (!responseReady || !responseBytes) return false;
        auto bytes = TArray<uint8>{};
        bytes.Append(response, static_cast<int32>(responseBytes));
        for (auto c : bytes)
            if (c == '\n') return decodeDiscoveryReply(bytes, invitation);
        for (;;)
        {
            uint8 chunk[maximumReplyBytes];
            if (!discoveryIo(pipe.Get(), cancellation, false, chunk, static_cast<DWORD>(sizeof(chunk)), deadline, transferred) ||
                !transferred || transferred > static_cast<DWORD>(maximumReplyBytes - bytes.Num())) return false;
            bytes.Append(chunk, static_cast<int32>(transferred));
            for (auto c : bytes)
                if (c == '\n') return decodeDiscoveryReply(bytes, invitation);
        }
    }
#endif
}

struct FLibraPreviewDiscovery::FState
{
    std::mutex mutex;
    std::thread worker;
    std::atomic<bool> cancelled{false};
    std::atomic<bool> complete{false};
    bool consumed = false;
    TArray<FLibraPreviewDiscoveryCandidate> candidates;
    FString error;
#if PLATFORM_WINDOWS
    FDiscoveryHandle cancellation{CreateEventW(nullptr, true, false, nullptr)};
#endif
};

FLibraPreviewDiscovery::FLibraPreviewDiscovery() = default;
FLibraPreviewDiscovery::~FLibraPreviewDiscovery() { Cancel(); }

void FLibraPreviewDiscovery::Cancel()
{
    if (!state) return;
    state->cancelled.store(true);
#if PLATFORM_WINDOWS
    if (state->cancellation.IsValid()) SetEvent(state->cancellation.Get());
#endif
    if (state->worker.joinable()) state->worker.join();
    state.Reset();
}

void FLibraPreviewDiscovery::Begin(const FString& overridePipeName)
{
    Cancel();
    state = MakeShared<FState, ESPMode::ThreadSafe>();
    auto owned = state;
    state->worker = std::thread([owned, overridePipeName]
    {
        auto names = TArray<FString>{};
        auto candidates = TArray<FLibraPreviewDiscoveryCandidate>{};
        auto error = FString{};
#if PLATFORM_WINDOWS
        auto id = uint32{0};
        if (!owned->cancellation.IsValid()) error = TEXT("Local discovery could not start. Use Paste Connection.");
        else if (!overridePipeName.IsEmpty())
        {
            if (validDiscoveryOverride(overridePipeName)) names.Add(overridePipeName);
            else error = TEXT("Invalid local discovery pipe override.");
        }
        else
        {
            auto data = WIN32_FIND_DATAW{};
            auto search = FindFirstFileW(TEXT("\\\\.\\pipe\\*"), &data);
            if (search != INVALID_HANDLE_VALUE)
            {
                auto scanned = 0;
                do
                {
                    auto name = FString(data.cFileName);
                    if (validDiscoveryPipe(name, id)) names.Add(MoveTemp(name));
                    if (names.Num() > maximumPeers || ++scanned > 4096)
                    {
                        error = TEXT("Too many local pipes. Use Paste Connection to select Libra explicitly.");
                        break;
                    }
                } while (!owned->cancelled.load() && FindNextFileW(search, &data));
                FindClose(search);
            }
        }
        if (error.IsEmpty())
        {
            for (const auto& name : names)
            {
                if (owned->cancelled.load()) break;
                auto candidate = FLibraPreviewDiscoveryCandidate{name, FString{}};
                if (queryDiscoveryPipe(name, owned->cancellation.Get(), !overridePipeName.IsEmpty(), candidate.invitation))
                    candidates.Add(MoveTemp(candidate));
            }
            if (candidates.Num() > 16)
            {
                candidates.Reset();
                error = TEXT("Too many Libra instances. Use Paste Connection to select one explicitly.");
            }
            else if (candidates.IsEmpty()) error = TEXT("No Libra found. Start Libra on this PC, then Connect to Libra, or use Paste Connection.");
        }
#else
        error = TEXT("Local discovery is available on Windows. Use Paste Connection.");
#endif
        auto lock = std::lock_guard<std::mutex>(owned->mutex);
        owned->candidates = MoveTemp(candidates);
        owned->error = MoveTemp(error);
        owned->complete.store(true);
    });
}

bool FLibraPreviewDiscovery::IsRunning() const
{
    return state && !state->complete.load();
}

bool FLibraPreviewDiscovery::Poll(TArray<FLibraPreviewDiscoveryCandidate>& candidates, FString& error)
{
    if (!state || !state->complete.load()) return false;
    auto lock = std::lock_guard<std::mutex>(state->mutex);
    if (state->consumed || state->cancelled.load()) return false;
    state->consumed = true;
    candidates = MoveTemp(state->candidates);
    error = MoveTemp(state->error);
    return true;
}

#if WITH_DEV_AUTOMATION_TESTS
#include "LibraEditorPreviewSession.h"
#include "LibraEditorConnectionComponent.h"
#include "LibraEditorGpuPreview.h"
#include "Serialization/JsonSerializer.h"
#include "HAL/PlatformProcess.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"

namespace
{
    TArray<uint8> discoveryTestBytes(const FString& text)
    {
        auto encoded = FTCHARToUTF8(*text);
        auto bytes = TArray<uint8>{};
        bytes.Append(reinterpret_cast<const uint8*>(encoded.Get()), encoded.Length());
        return bytes;
    }

#if PLATFORM_WINDOWS
    class FDiscoveryTestServer final
    {
    public:
        FDiscoveryTestServer(const FString& reply, bool timeout)
        {
            name = FString(discoveryPrefix) + FString::FromInt(static_cast<int32>(GetCurrentProcessId()));
            auto path = FString(TEXT("\\\\.\\pipe\\")) + name;
            pipe = CreateNamedPipeW(*path, PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 4096, 4096, 0, nullptr);
            if (pipe == INVALID_HANDLE_VALUE || !cancellation.IsValid()) return;
            worker = std::thread([this, reply, timeout]
            {
                auto event = FDiscoveryHandle(CreateEventW(nullptr, true, false, nullptr));
                if (!event.IsValid()) return;
                auto operation = OVERLAPPED{};
                operation.hEvent = event.Get();
                auto connected = ConnectNamedPipe(pipe, &operation) != 0;
                if (!connected)
                {
                    auto failure = GetLastError();
                    if (failure == ERROR_PIPE_CONNECTED) connected = true;
                    else if (failure == ERROR_IO_PENDING)
                    {
                        HANDLE waits[] = {cancellation.Get(), event.Get()};
                        auto result = WaitForMultipleObjects(2, waits, false, 3000);
                        auto transferred = DWORD{0};
                        if (result != WAIT_OBJECT_0 + 1)
                        {
                            CancelIoEx(pipe, &operation);
                            GetOverlappedResult(pipe, &operation, &transferred, true);
                        }
                        else connected = GetOverlappedResult(pipe, &operation, &transferred, false) != 0;
                    }
                }
                if (!connected) return;
                auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(3);
                char request[sizeof(discoveryRequest) - 1];
                auto received = DWORD{0};
                while (received < sizeof(request))
                {
                    auto transferred = DWORD{0};
                    if (!discoveryIo(pipe, cancellation.Get(), false, request + received,
                        static_cast<DWORD>(sizeof(request)) - received, deadline, transferred) || !transferred) return;
                    received += transferred;
                }
                validRequest.store(FMemory::Memcmp(request, discoveryRequest, sizeof(request)) == 0);
                if (timeout) { WaitForSingleObject(cancellation.Get(), 5000); return; }
                auto bytes = discoveryTestBytes(reply);
                auto transferred = DWORD{0};
                if (!discoveryIo(pipe, cancellation.Get(), true, bytes.GetData(), static_cast<DWORD>(bytes.Num()),
                    deadline, transferred)) return;
                uint8 closing = 0;
                discoveryIo(pipe, cancellation.Get(), false, &closing, 1, deadline, transferred);
                DisconnectNamedPipe(pipe);
            });
        }
        ~FDiscoveryTestServer()
        {
            if (cancellation.IsValid()) SetEvent(cancellation.Get());
            if (worker.joinable()) worker.join();
            if (pipe != INVALID_HANDLE_VALUE) CloseHandle(pipe);
        }
        FString name;
        std::atomic<bool> validRequest{false};
    private:
        HANDLE pipe = INVALID_HANDLE_VALUE;
        FDiscoveryHandle cancellation{CreateEventW(nullptr, true, false, nullptr)};
        std::thread worker;
    };

    bool waitDiscoveryTest(FLibraPreviewDiscovery& discovery,
        TArray<FLibraPreviewDiscoveryCandidate>& candidates, FString& error)
    {
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(4);
        while (std::chrono::steady_clock::now() < deadline)
        {
            if (discovery.Poll(candidates, error)) return true;
            FPlatformProcess::Sleep(0.005f);
        }
        discovery.Cancel();
        return false;
    }
#endif
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraPreviewDiscoveryTest, "Libra.Editor.Preview.LocalDiscovery",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraPreviewDiscoveryTest::RunTest(const FString&)
{
    auto valid = FString(TEXT("{\"type\":\"librafx_preview_connection\",\"version\":1,\"host\":\"127.0.0.1\","
        "\"port\":8766,\"accessToken\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\","
        "\"certificateFingerprint\":\"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\"}"));
    auto decoded = FString(TEXT("unchanged"));
    TestTrue(TEXT("The newline-delimited strict invitation is accepted"), decodeDiscoveryReply(discoveryTestBytes(valid + TEXT("\n")), decoded));
    for (const auto& invalid : {valid, valid + TEXT("\nextra"), FString(TEXT("{}\n")), FString::ChrN(4097, TEXT('x')) + TEXT("\n")})
        TestFalse(TEXT("Malformed, unframed and oversized replies are rejected"), decodeDiscoveryReply(discoveryTestBytes(invalid), decoded));
    auto invalidUtf8 = discoveryTestBytes(valid + TEXT("\n"));
    invalidUtf8[1] = 0xff;
    TestFalse(TEXT("Invalid UTF8 is not silently repaired"), decodeDiscoveryReply(invalidUtf8, decoded));
    auto id = uint32{0};
    TestFalse(TEXT("Remote pipe paths are never accepted"), validDiscoveryPipe(TEXT("\\\\remote\\pipe\\LibraFX.Preview.1"), id));
    TestFalse(TEXT("Pipe suffix paths are never accepted"), validDiscoveryPipe(TEXT("LibraFX.Preview.1\\extra"), id));

    auto session = FLibraEditorPreviewSession{};
    session.discovery = MakeUnique<FLibraPreviewDiscovery>();
    session.discovery->state = MakeShared<FLibraPreviewDiscovery::FState, ESPMode::ThreadSafe>();
    session.discovery->state->candidates = {{TEXT("LibraFX.Preview.1"), valid},
        {TEXT("LibraFX.Preview.2"), valid.Replace(TEXT("8766"), TEXT("9000"))}};
    session.discovery->state->complete.store(true);
    session.PollDiscovery();
    TestTrue(TEXT("Multiple valid peers require explicit selection"), session.discoveredPeers.Num() == 2 && !session.pendingConnection);
    session.SelectDiscoveredPeer(TEXT("LibraFX.Preview.2"));
    TestTrue(TEXT("Explicit selection queues only the selected endpoint"), session.pendingConnection && session.pendingConnection->port == 9000);
    session.DisconnectPreview();
    TestFalse(TEXT("Disconnect cancels pending discovery/connection state"), session.pendingConnection.IsValid());

#if PLATFORM_WINDOWS
    auto discovery = FLibraPreviewDiscovery{};
    auto candidates = TArray<FLibraPreviewDiscoveryCandidate>{};
    auto error = FString{};
    for (auto iteration = 0; iteration < 2; ++iteration)
    {
        auto server = FDiscoveryTestServer(valid + TEXT("\n"), false);
        discovery.Begin(server.name);
        TestTrue(TEXT("A current-user local fixture responds asynchronously"), waitDiscoveryTest(discovery, candidates, error));
        TestTrue(TEXT("Repeated connect/disconnect discovers one valid peer"), candidates.Num() == 1 && error.IsEmpty());
        TestTrue(TEXT("The request contains exactly 27 ASCII bytes including newline"), server.validRequest.load());
        discovery.Cancel();
        TestFalse(TEXT("Cancelled discovery cannot publish a late result"), discovery.Poll(candidates, error));
    }
    {
        auto server = FDiscoveryTestServer(TEXT("{}\n"), false);
        discovery.Begin(server.name);
        TestTrue(TEXT("Malformed peer replies complete with a safe error"), waitDiscoveryTest(discovery, candidates, error));
        TestTrue(TEXT("Invalid peers never replace a session"), candidates.IsEmpty() && !error.IsEmpty());
    }
    {
        auto server = FDiscoveryTestServer(valid + TEXT("\n"), true);
        discovery.Begin(server.name);
        auto started = std::chrono::steady_clock::now();
        TestTrue(TEXT("A silent peer reaches the bounded I/O deadline"), waitDiscoveryTest(discovery, candidates, error));
        TestTrue(TEXT("Silent peer response has a two-second deadline"),
            candidates.IsEmpty() && std::chrono::steady_clock::now() - started < std::chrono::seconds(3));
    }
    discovery.Begin(TEXT("LibraFX.Preview.4294967295"));
    TestTrue(TEXT("No running process completes without contacting other peers"), waitDiscoveryTest(discovery, candidates, error));
    TestTrue(TEXT("No running Libra is actionable, not a selected arbitrary peer"), candidates.IsEmpty() && !error.IsEmpty());
    {
        auto server = FDiscoveryTestServer(valid + TEXT("\n"), true);
        session.discovery = MakeUnique<FLibraPreviewDiscovery>();
        session.discovery->Begin(server.name);
        FPlatformProcess::Sleep(0.02f);
        auto started = std::chrono::steady_clock::now();
        session.Stop();
        TestTrue(TEXT("Session Stop cancels pending I/O rather than waiting for its handshake deadline"),
            !session.discovery && std::chrono::steady_clock::now() - started < std::chrono::seconds(1));
    }
#endif
    session.Stop();
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FLibraPreviewPostStartupDiscoveryTest, "Libra.Editor.Preview.PostStartupDiscovery",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FLibraPreviewPostStartupDiscoveryTest::RunTest(const FString&)
{
    auto overridePipe = FString{};
    if (!FParse::Value(FCommandLine::Get(), TEXT("LibraPreviewDiscoveryPipe="), overridePipe) || overridePipe.IsEmpty())
    {
        AddInfo(TEXT("PostStartupDiscovery requires an explicitly selected ephemeral Root pipe name."));
        return true;
    }
    auto session = FLibraEditorPreviewSession{};
    session.bEngineReady = true;
    session.bPreviewRequested = true;
    session.Tick(0);
    if (!TestTrue(TEXT("Preview starts isolated and disconnected without credential bootstrap"),
        session.world.IsValid() && session.connection.IsValid() && !session.connection->bIsConnected))
    {
        session.Stop();
        return false;
    }
    session.DiscoverLibra();
    auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(60);
    auto sawConnection = false;
    auto sawGpuProgram = false;
    auto sawExecution = false;
    auto sawRemotePlaying = false;
    auto sawRemotePause = false;
    auto pausedAt = std::chrono::steady_clock::time_point{};
    while (std::chrono::steady_clock::now() < deadline)
    {
        session.Tick(0.01f);
        sawConnection |= session.connection->bIsConnected;
        if (sawConnection && !session.connection->bIsConnected) break;
        auto status = TSharedPtr<FJsonObject>{};
        auto reader = TJsonReaderFactory<>::Create(session.gpuPreview->Status(0));
        if (FJsonSerializer::Deserialize(reader, status) && status.IsValid())
        {
            const TArray<TSharedPtr<FJsonValue>>* programs = nullptr;
            const TArray<TSharedPtr<FJsonValue>>* errors = nullptr;
            auto executed = false;
            if (status->TryGetArrayField(TEXT("programs"), programs) && !programs->IsEmpty() &&
                status->TryGetArrayField(TEXT("errors"), errors) && errors->IsEmpty())
            {
                sawGpuProgram = true;
                status->TryGetBoolField(TEXT("runtimeExecuted"), executed);
                sawExecution |= executed;
                auto playing = false;
                for (const auto& program : *programs)
                {
                    auto object = program.IsValid() ? program->AsObject() : nullptr;
                    auto value = false;
                    if (object && object->TryGetBoolField(TEXT("playing"), value)) playing |= value;
                }
                sawRemotePlaying |= playing;
                if (sawRemotePlaying && sawExecution && !playing)
                {
                    if (!sawRemotePause) pausedAt = std::chrono::steady_clock::now();
                    sawRemotePause = true;
                    // Keep ordinary 4Hz feedback alive long enough for Root to observe Pause.
                    if (std::chrono::steady_clock::now() - pausedAt > std::chrono::milliseconds(600)) break;
                }
            }
        }
        FPlatformProcess::Sleep(0.01f);
    }
    TestTrue(TEXT("One-click post-startup discovery negotiates a real pinned preview connection"),
        sawConnection);
    TestTrue(TEXT("The discovered Root delivers an accepted GPU configuration"), sawGpuProgram);
    TestTrue(TEXT("The actual native GPU interpreter submits work from that Root delivery"), sawExecution);
    TestTrue(TEXT("Root remote Play is received without native test-generated Play"), sawRemotePlaying);
    TestTrue(TEXT("Root remote Pause is received after real GPU execution and fed back before disconnect"), sawRemotePause);
    session.DisconnectPreview();
    TestFalse(TEXT("Manual disconnect ends the discovered connection"), session.connection->bIsConnected);
    session.Stop();
    return true;
}
#endif
