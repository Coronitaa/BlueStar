/* Tiny Windows program for BlueStar's Linux CI: proves a game really ran under Wine/Proton by writing
   bluestar-selftest.ok next to its own .exe. Build: x86_64-w64-mingw32-gcc -O2 -o SelfTestGame.exe selftest-game.c */
#include <windows.h>
#include <string.h>

int main(void)
{
    char path[MAX_PATH];
    DWORD n = GetModuleFileNameA(NULL, path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return 2;
    char *slash = strrchr(path, '\');
    if (!slash) return 3;
    strcpy(slash + 1, "bluestar-selftest.ok");
    HANDLE f = CreateFileA(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return 4;
    DWORD written;
    WriteFile(f, "ok\n", 3, &written, NULL);
    CloseHandle(f);
    return 0;
}
