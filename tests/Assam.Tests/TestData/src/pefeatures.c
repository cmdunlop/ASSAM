/*
 * 載入器測試用 DLL（不使用 C 執行階段，只用 clang-cl + lld-link 建置，永遠不會被執行）。
 * 涵蓋：一般匯入、以序號匯入、延遲載入匯入、轉送匯出、別名匯出、只有序號的匯出、TLS 回呼。
 * 建置方式見 build.sh。
 */
typedef unsigned long DWORD;
typedef unsigned long long ULONG64;
#ifdef _WIN64
typedef unsigned long long ULONG_PTR;
#else
typedef unsigned long ULONG_PTR;
#endif

/* 一般匯入（kernel32） */
__declspec(dllimport) void __stdcall Sleep(DWORD ms);
/* 以序號匯入（ordlib.dll 的 #7，import lib 由 ordlib.def 以 NONAME 產生） */
__declspec(dllimport) int __cdecl OrdFunc(int x);
/* 延遲載入（user32.dll，連結時加 /DELAYLOAD:user32.dll） */
__declspec(dllimport) int __stdcall MessageBoxA(void *owner, const char *text, const char *caption, unsigned type);

/* lld-link 的延遲載入需要這個輔助函式（通常由 delayimp.lib 提供，這裡放一個空的版本） */
void *__stdcall __delayLoadHelper2(const void *descriptor, void **iatSlot)
{
    (void)descriptor;
    (void)iatSlot;
    return 0;
}

__declspec(dllexport) int __cdecl AddTwo(int a, int b)
{
    return a + b;
}

int __cdecl HiddenByOrdinal(int a)
{
    return a * 3;
}

__declspec(dllexport) int __cdecl UseImports(int a)
{
    Sleep(1);
    MessageBoxA(0, "pefeatures", "ASSAM", 0);
    return OrdFunc(a);
}

/* TLS：_tls_used 由 C 執行階段提供，這裡自己定義；回呼放在 .CRT$XLB（AddressOfCallBacks 直接指向它，避免對齊填充造成提早結束） */
static void __stdcall TlsCallbackOne(void *module, DWORD reason, void *reserved)
{
    (void)module;
    (void)reason;
    (void)reserved;
}

static void __stdcall TlsCallbackTwo(void *module, DWORD reason, void *reserved)
{
    (void)module;
    (void)reason;
    (void)reserved;
}

typedef void(__stdcall *TlsCallback)(void *, DWORD, void *);
#pragma section(".CRT$XLA", read)
#pragma section(".CRT$XLB", read)
#pragma section(".CRT$XLZ", read)
__declspec(allocate(".CRT$XLA")) const TlsCallback __xl_a = 0;
__declspec(allocate(".CRT$XLB")) const TlsCallback __xl_b[2] = { TlsCallbackOne, TlsCallbackTwo };
__declspec(allocate(".CRT$XLZ")) const TlsCallback __xl_z = 0;

DWORD _tls_index;
char _tls_start = 0, _tls_end = 0;

typedef struct
{
    ULONG_PTR StartAddressOfRawData;
    ULONG_PTR EndAddressOfRawData;
    ULONG_PTR AddressOfIndex;
    ULONG_PTR AddressOfCallBacks;
    DWORD SizeOfZeroFill;
    DWORD Characteristics;
} TlsDirectory;

const TlsDirectory _tls_used = {
    (ULONG_PTR)&_tls_start, (ULONG_PTR)&_tls_end, (ULONG_PTR)&_tls_index, (ULONG_PTR)__xl_b, 0, 0
};

int __stdcall DllMain(void *module, DWORD reason, void *reserved)
{
    (void)module;
    (void)reason;
    (void)reserved;
    return 1;
}
