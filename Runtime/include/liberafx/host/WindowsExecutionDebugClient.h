#pragma once

// Include Windows.h (or Unreal's WindowsHWrapper.h) before this adapter.
#include "liberafx/host/ExecutionDebugProtocol.h"
#include <atomic>
#include <string>

namespace liberafx::host {
// The matching managed pipe server uses CurrentUserOnly. The engine must opt
// in with that server's fresh 128-bit invitation; this is never auto-discovered.
class WindowsExecutionDebugClient final {
public:
    explicit WindowsExecutionDebugClient(std::uint64_t identity, std::uint64_t generation = 0,
        const std::uint8_t* container = nullptr, std::uint64_t instance = 0) noexcept
        : protocol_(identity, generation, container, instance) {}
    ~WindowsExecutionDebugClient() { stop(); if (stop_) CloseHandle(stop_); }
    ExecutionDebugHooks hooks() noexcept { return protocol_.debugger().hooks(); }
    bool running() const noexcept { return running_.load(std::memory_order_acquire); }
    bool start(const char* invitation)
    {
        if (!invitation || std::strlen(invitation) != 32 || worker_.joinable()) return false;
        auto path = std::string{"\\\\.\\pipe\\LiberaFx.ExecutionDebug."};
        for (auto i = 0u; i < 32; ++i)
        {
            auto ch = invitation[i];
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return false;
            path += ch;
        }
        if (invitation[32] != '\0') return false;
        stop_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!stop_) return false;
        running_.store(true, std::memory_order_release);
        // Construction failure propagates to the host's error boundary.
        worker_ = std::thread([this, path] {
            run(path.c_str());
            protocol_.debugger().detach();
            running_.store(false, std::memory_order_release);
        });
        return true;
    }
    // Call before joining the evaluation worker. Keep this object alive until
    // the worker is joined and its hooks have been removed.
    void stop() noexcept
    {
        protocol_.debugger().detach();
        if (stop_) SetEvent(stop_);
        if (worker_.joinable()) worker_.join();
    }
private:
    bool transfer(HANDLE pipe, HANDLE event, std::uint8_t* bytes, DWORD length, bool send) noexcept
    {
        auto deadline = GetTickCount64() + 2500;
        while (length)
        {
            if (WaitForSingleObject(stop_, 0) != WAIT_TIMEOUT) return false;
            auto now = GetTickCount64();
            if (now >= deadline) return false;
            ResetEvent(event);
            auto operation = OVERLAPPED{};
            operation.hEvent = event;
            auto count = DWORD{};
            auto ok = send ? WriteFile(pipe, bytes, length, &count, &operation) :
                ReadFile(pipe, bytes, length, &count, &operation);
            if (!ok)
            {
                if (GetLastError() != ERROR_IO_PENDING) return false;
                HANDLE wait[] = {stop_, event};
                if (WaitForMultipleObjects(2, wait, FALSE, static_cast<DWORD>(deadline - now)) != WAIT_OBJECT_0 + 1)
                {
                    CancelIoEx(pipe, &operation);
                    GetOverlappedResult(pipe, &operation, &count, TRUE);
                    return false;
                }
                if (!GetOverlappedResult(pipe, &operation, &count, FALSE)) return false;
            }
            if (!count || count > length) return false;
            length -= count;
            bytes += count;
        }
        return true;
    }
    void run(const char* path) noexcept
    {
        auto pipe = CreateFileA(path, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) return;
        auto event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (event)
        {
            auto command = ExecutionDebugProtocol::Command{};
            auto frame = ExecutionDebugProtocol::Frame{};
            while (transfer(pipe, event, command.data(), static_cast<DWORD>(command.size()), false) &&
                protocol_.exchange(command, frame) &&
                transfer(pipe, event, frame.data(), static_cast<DWORD>(frame.size()), true)) {}
            CloseHandle(event);
        }
        CloseHandle(pipe);
    }
    ExecutionDebugProtocol protocol_;
    HANDLE stop_ = nullptr;
    std::atomic<bool> running_{false};
    std::thread worker_;
};
}
