#include <windows.h>
#include <shellapi.h>
#include <string>
#include "arguments.h"

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    wchar_t module[32768]{};
    DWORD length = GetModuleFileNameW(nullptr, module, 32768);
    if (!length || length >= 32768) return 1;
    std::wstring root(module, length);
    root.resize(root.find_last_of(L"\\/"));
#ifdef DOTMIC_SETUP_LAUNCHER
    std::wstring directory = root + L"\\内部ファイル";
    std::wstring executable = directory + L"\\DotMic.Setup.exe";
    const wchar_t* verb = L"runas";
#else
    std::wstring directory = root + L"\\内部ファイル\\UI";
    std::wstring executable = directory + L"\\DotMic.App.exe";
    const wchar_t* verb = L"open";
#endif
    int count = 0;
    LPWSTR* arguments = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!arguments) return 1;
    std::wstring parameters;
    for (int i = 1; i < count; ++i) { if (i > 1) parameters += L' '; parameters += quoteArgument(arguments[i]); }
    LocalFree(arguments);
    SHELLEXECUTEINFOW launch{};
    launch.cbSize = sizeof(launch); launch.fMask = SEE_MASK_FLAG_NO_UI;
    launch.lpVerb = verb; launch.lpFile = executable.c_str(); launch.lpParameters = parameters.c_str();
    launch.lpDirectory = directory.c_str(); launch.nShow = SW_SHOWNORMAL;
    if (ShellExecuteExW(&launch)) return 0;
    DWORD error = GetLastError();
    if (error == ERROR_CANCELLED) return 0;
    MessageBoxW(nullptr, L"起動できませんでした。\nZIP全体を展開してから開いてください。", L"DOT MIC", MB_OK | MB_ICONERROR);
    return 1;
}
