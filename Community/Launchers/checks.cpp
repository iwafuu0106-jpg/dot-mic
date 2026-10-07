#include <windows.h>
#include <shellapi.h>
#include <iostream>
#include "arguments.h"

int wmain() {
    for (const std::wstring value : { L"", L"--repair", L"--startup", L"日本語の 引数", L"C:\\末尾\\", L"a\\\"b" }) {
        std::wstring command = L"launcher.exe " + quoteArgument(value);
        int count = 0;
        LPWSTR* args = CommandLineToArgvW(command.c_str(), &count);
        if (!args) return 1;
        bool same = count == 2 && value == args[1];
        LocalFree(args);
        if (!same) return 1;
    }
    std::cout << "PASS launcher arguments only; no child process launched.\n";
}
