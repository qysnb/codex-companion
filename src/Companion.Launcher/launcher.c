#define UNICODE
#define _UNICODE
#include <windows.h>
#include <wchar.h>
#include <string.h>

/* A nonresident native entry point. No shell, unpacking, scripts or networking. */
int WINAPI WinMain(HINSTANCE instance, HINSTANCE previous, LPSTR arguments, int show)
{
    wchar_t runtime[32768], directory[32768], command[32770];
    const wchar_t suffix[] = L"app\\bin\\CodexCompanion.exe";
    DWORD length = GetModuleFileNameW(NULL, runtime, 32768);
    wchar_t *separator;
    size_t base;
    int check = strcmp(arguments, "--check-layout") == 0;
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    DWORD attributes;
    (void)instance; (void)previous; (void)show;
    if (arguments[0] && !check) return 2;
    if (!length || length >= 32768 || !(separator = wcsrchr(runtime, L'\\'))) return 3;
    base = (size_t)(separator - runtime) + 1;
    if (base + (sizeof(suffix) / sizeof(wchar_t)) >= 32768) return 3;
    memcpy(runtime + base, suffix, sizeof(suffix));
    attributes = GetFileAttributesW(runtime);
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY)) {
        if (!check) MessageBoxW(NULL, L"The app folder is missing. Extract the complete package and keep the app folder next to CodexCompanion.exe.", L"Codex Companion", MB_OK | MB_ICONERROR);
        return 4;
    }
    if (check) return 0;
    wcscpy(directory, runtime);
    *wcsrchr(directory, L'\\') = L'\0';
    command[0] = L'"'; wcscpy(command + 1, runtime);
    length = (DWORD)wcslen(command);
    command[length] = L'"'; command[length + 1] = L'\0';
    ZeroMemory(&startup, sizeof(startup)); startup.cb = sizeof(startup);
    ZeroMemory(&process, sizeof(process));
    startup.dwFlags = STARTF_USESHOWWINDOW; startup.wShowWindow = SW_SHOWNOACTIVATE;
    if (!CreateProcessW(runtime, command, NULL, NULL, FALSE, 0, NULL, directory, &startup, &process)) {
        MessageBoxW(NULL, L"Codex Companion could not start. Keep the complete app folder and consult docs/INSTALL.md.", L"Codex Companion", MB_OK | MB_ICONERROR);
        return 5;
    }
    CloseHandle(process.hThread); CloseHandle(process.hProcess);
    return 0;
}
