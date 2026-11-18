#include "LibraViewerEndpoint.h"
#include "LibraPreviewConnectionInvitation.h"
#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include <atomic>
#include <chrono>
#include <mutex>
#include <thread>

#if PLATFORM_WINDOWS
#include "Windows/WindowsHWrapper.h"
#include <sddl.h>
#endif

struct FLibraViewerEndpoint::FState
{
    FString name, instanceId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    FString pending;
    std::mutex mutex;
    std::atomic<bool> connected{false};
#if PLATFORM_WINDOWS
    HANDLE stop = nullptr;
    std::thread worker;

    bool transfer(HANDLE pipe, bool write, void* bytes, DWORD count, DWORD& transferred,
        std::chrono::steady_clock::time_point deadline)
    {
        auto remaining = std::chrono::duration_cast<std::chrono::milliseconds>(
            deadline - std::chrono::steady_clock::now()).count();
        if (remaining <= 0) return false;
        auto event = CreateEventW(nullptr, true, false, nullptr);
        if (!event) return false;
        auto operation = OVERLAPPED{};
        operation.hEvent = event;
        auto ok = write ? WriteFile(pipe, bytes, count, &transferred, &operation) :
            ReadFile(pipe, bytes, count, &transferred, &operation);
        if (!ok && GetLastError() == ERROR_IO_PENDING)
        {
            HANDLE waits[] = {stop, event};
            auto result = WaitForMultipleObjects(2, waits, false, static_cast<DWORD>(remaining));
            if (result != WAIT_OBJECT_0 + 1) CancelIoEx(pipe, &operation);
            ok = GetOverlappedResult(pipe, &operation, &transferred, true) &&
                result == WAIT_OBJECT_0 + 1;
        }
        CloseHandle(event);
        return ok != 0;
    }

    FString respond(const FString& text)
    {
        auto request = TSharedPtr<FJsonObject>{};
        auto reader = TJsonReaderFactory<>::Create(text);
        auto reply = MakeShared<FJsonObject>();
        auto type = FString{};
        auto version = double{};
        auto accepted = false;
        if (FJsonSerializer::Deserialize(reader, request) && request.IsValid() &&
            request->TryGetStringField(TEXT("type"), type) &&
            request->TryGetNumberField(TEXT("version"), version) && version == 1)
        {
            if (type == TEXT("librafx_viewer_query"))
            {
                reply->SetStringField(TEXT("type"), TEXT("librafx_viewer"));
                reply->SetNumberField(TEXT("version"), 1);
                reply->SetNumberField(TEXT("processId"), FPlatformProcess::GetCurrentProcessId());
                reply->SetStringField(TEXT("instanceId"), instanceId);
                reply->SetStringField(TEXT("engine"), TEXT("Unreal"));
                reply->SetStringField(TEXT("name"), name);
                reply->SetBoolField(TEXT("connected"), connected.load());
            }
            else if (type == TEXT("librafx_viewer_connect"))
            {
                auto expected = FString{};
                auto invitation = FString{};
                auto parsed = FLibraPreviewConnectionInvitation{};
                auto error = FString{};
                if (request->TryGetStringField(TEXT("instanceId"), expected) && expected == instanceId &&
                    request->TryGetStringField(TEXT("invitation"), invitation) &&
                    ParseLibraPreviewConnectionInvitation(invitation, parsed, error))
                {
                    auto lock = std::lock_guard<std::mutex>(mutex);
                    if (pending.IsEmpty())
                    {
                        pending = MoveTemp(invitation);
                        accepted = true;
                    }
                }
            }
        }
        reply->SetBoolField(TEXT("accepted"), accepted);
        auto output = FString{};
        auto writer = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&output);
        FJsonSerializer::Serialize(reply, writer);
        return output + TEXT("\n");
    }

    void serve()
    {
        // A DACL for exactly the current user, not all interactive users.
        auto token = HANDLE{};
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return;
        auto length = DWORD{};
        GetTokenInformation(token, TokenUser, nullptr, 0, &length);
        auto user = TArray<uint8>{};
        user.SetNumUninitialized(length);
        if (!length || !GetTokenInformation(token, TokenUser, user.GetData(), length, &length))
        {
            CloseHandle(token);
            return;
        }
        CloseHandle(token);
        auto sid = LPWSTR{};
        if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(user.GetData())->User.Sid, &sid)) return;
        auto dacl = FString(TEXT("D:P(A;;GA;;;")) + sid + TEXT(")");
        LocalFree(sid);
        auto descriptor = PSECURITY_DESCRIPTOR{};
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(*dacl, SDDL_REVISION_1, &descriptor, nullptr)) return;
        auto security = SECURITY_ATTRIBUTES{};
        security.nLength = sizeof(security);
        security.lpSecurityDescriptor = descriptor;
        auto pipeName = FString::Printf(TEXT("\\\\.\\pipe\\LibraFX.Viewer.%u"),
            FPlatformProcess::GetCurrentProcessId());
        auto pipe = CreateNamedPipeW(*pipeName, PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED |
            FILE_FLAG_FIRST_PIPE_INSTANCE, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_REJECT_REMOTE_CLIENTS,
            1, 8192, 8192, 0, &security);
        LocalFree(descriptor);
        if (pipe == INVALID_HANDLE_VALUE)
        {
            return;
        }
        auto event = CreateEventW(nullptr, true, false, nullptr);
        if (!event)
        {
            CloseHandle(pipe);
            return;
        }
        // Keep the pipe name discoverable while switching between clients.
        while (WaitForSingleObject(stop, 0) != WAIT_OBJECT_0)
        {
            ResetEvent(event);
            auto operation = OVERLAPPED{};
            operation.hEvent = event;
            auto connectedPipe = ConnectNamedPipe(pipe, &operation) != 0;
            auto error = connectedPipe ? ERROR_SUCCESS : GetLastError();
            if (error == ERROR_PIPE_CONNECTED)
            {
                connectedPipe = true;
            }
            else if (error == ERROR_IO_PENDING)
            {
                HANDLE waits[] = {stop, event};
                auto result = WaitForMultipleObjects(2, waits, false, INFINITE);
                if (result != WAIT_OBJECT_0 + 1)
                {
                    CancelIoEx(pipe, &operation);
                }
                auto transferred = DWORD{};
                auto completed = GetOverlappedResult(pipe, &operation, &transferred, true);
                if (result != WAIT_OBJECT_0 + 1)
                {
                    break;
                }
                connectedPipe = completed != 0;
                error = connectedPipe ? ERROR_SUCCESS : GetLastError();
            }
            if (!connectedPipe && error != ERROR_NO_DATA && error != ERROR_BROKEN_PIPE)
            {
                break;
            }
            if (connectedPipe)
            {
                auto bytes = TArray<uint8>{};
                auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
                auto complete = false;
                while (bytes.Num() <= 8192 && !complete)
                {
                    uint8 buffer[512];
                    auto count = DWORD{};
                    if (!transfer(pipe, false, buffer, sizeof(buffer), count, deadline) || !count) break;
                    for (auto index = DWORD{}; index < count; ++index)
                    {
                        if (buffer[index] == '\n')
                        {
                            complete = index == count - 1;
                            if (!complete) bytes.SetNum(8193);
                            break;
                        }
                        bytes.Add(buffer[index]);
                    }
                }
                if (complete && bytes.Num() <= 8192)
                {
                    auto decoded = FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(bytes.GetData()), bytes.Num());
                    auto reply = respond(FString(decoded.Length(), decoded.Get()));
                    auto encoded = FTCHARToUTF8(*reply);
                    auto offset = DWORD{};
                    while (offset < static_cast<DWORD>(encoded.Length()))
                    {
                        auto written = DWORD{};
                        if (!transfer(pipe, true, const_cast<ANSICHAR*>(encoded.Get()) + offset,
                            encoded.Length() - offset, written, deadline) || !written) break;
                        offset += written;
                    }
                    uint8 acknowledgement = 0;
                    auto received = DWORD{};
                    transfer(pipe, false, &acknowledgement, 1, received, deadline);
                }
            }
            DisconnectNamedPipe(pipe);
        }
        CloseHandle(event);
        CloseHandle(pipe);
    }
#endif
};

FLibraViewerEndpoint::FLibraViewerEndpoint() = default;
FLibraViewerEndpoint::~FLibraViewerEndpoint() { Stop(); }
void FLibraViewerEndpoint::Start(const FString& name)
{
    Stop();
#if PLATFORM_WINDOWS
    state = MakeUnique<FState>();
    state->name = name.Left(128).IsEmpty() ? TEXT("Unreal") : name.Left(128);
    state->stop = CreateEventW(nullptr, true, false, nullptr);
    if (!state->stop) { state.Reset(); return; }
    state->worker = std::thread([data = state.Get()] { data->serve(); });
#endif
}
void FLibraViewerEndpoint::Stop()
{
#if PLATFORM_WINDOWS
    if (state)
    {
        SetEvent(state->stop);
        if (state->worker.joinable()) state->worker.join();
        CloseHandle(state->stop);
    }
#endif
    state.Reset();
}
void FLibraViewerEndpoint::SetConnected(bool connected)
{
    if (state) state->connected.store(connected);
}
bool FLibraViewerEndpoint::TakeInvitation(FString& invitation)
{
    if (!state) return false;
    auto lock = std::lock_guard<std::mutex>(state->mutex);
    if (state->pending.IsEmpty()) return false;
    invitation = MoveTemp(state->pending);
    state->pending.Reset();
    return true;
}
