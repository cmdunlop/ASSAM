/* ASSAM 測試用：有匯出函式的 DLL（mathlib32.dll / mathlib64.dll） */
#include <windows.h>

__declspec(dllexport) int __stdcall AddNumbers(int a, int b) { return a + b; }
__declspec(dllexport) int Multiply(int a, int b) { return a * b; }
__declspec(dllexport) unsigned Factorial(unsigned n) { return n <= 1 ? 1 : n * Factorial(n - 1); }
__declspec(dllexport) int ShowMessage(void) { return MessageBoxA(NULL, "Hi from DLL", "ASSAM", MB_OK); }
__declspec(dllexport) double Average(const double *v, int n)
{
    double s = 0;
    for (int i = 0; i < n; i++) s += v[i];
    return n ? s / n : 0;
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID reserved) { (void)h; (void)reason; (void)reserved; return TRUE; }
