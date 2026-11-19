#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <filesystem>
#include <string>

auto WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR arguments, int showWindow) -> int
{
    auto module = std::wstring(32768, L'\0');
    const auto length = GetModuleFileNameW(nullptr, module.data(), static_cast<DWORD>(module.size()));
    if (length == 0 || length == module.size())
    {
        return 1;
    }
    module.resize(length);
    const auto editor = std::filesystem::path(module).parent_path() / L"Editor" / L"LiberaFx.exe";
    auto command = L"\"" + editor.wstring() + L"\" " + arguments;
    auto startup = STARTUPINFOW{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = static_cast<WORD>(showWindow);
    auto process = PROCESS_INFORMATION{};
    if (!CreateProcessW(editor.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process))
    {
        MessageBoxW(nullptr, L"Could not start LIBERA FX. Extract the entire ZIP or repair the installation, then try again.",
            L"LIBERA FX", MB_OK | MB_ICONERROR);
        return 1;
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return 0;
}
