# 執行檔分析與反組譯（EXE／DLL／COM／.NET）

## 什麼是反組譯與合法使用提醒
id: bin-what-is-disasm
category: 概念
arch: 通用
tags: 反組譯, 反組譯器, disassembly, disassembler, 逆向工程, reverse engineering, 合法使用, 授權, EXE, DLL
---
反組譯（Disassembly）是把執行檔裡的機器碼翻回組合語言助憶碼的過程，方向和組譯相反：
```
組譯：  mov eax, 5        →  B8 05 00 00 00
反組譯：B8 05 00 00 00    →  mov eax, 5
```
反組譯只能還原「指令」，原本的變數名稱、註解、巨集、型別通常都不見了（除非檔案附有除錯符號 .pdb）。所以看到的會是 sub_401560、loc_401578、[ebp-4] 這類名稱，要靠自己推敲意義。
另外還有「反編譯」（Decompile）：嘗試直接還原成 C 或 C#，.NET 程式因為保留了大量中繼資料，反編譯效果特別好。

常見用途：
・學習：看 C 編譯器把 if、for、函式呼叫變成什麼指令
・除錯：沒有原始碼時找出程式當掉的位置
・相容性與安全研究：了解程式使用了哪些 API、哪些檔案格式

合法使用提醒：
・只分析自己有權分析的程式：自己寫的作業、老師提供的範例、開放原始碼軟體，或授權條款明確允許分析的檔案
・很多商業軟體的授權條款（EULA）禁止逆向工程；破解序號、移除保護、散布修改版都可能違反著作權法或授權條款
・分析來路不明的檔案（可能是惡意程式）要在隔離環境中進行；本程式只「讀取」檔案內容，不會執行它

## PE 檔案格式總覽
id: bin-pe-overview
category: 概念
arch: x86
tags: PE, PE32, PE32+, Portable Executable, EXE, DLL, DOS 標頭, e_lfanew, COFF 標頭, 選用標頭, Optional Header, 資料目錄, 區段表, 檔案格式
---
Windows 的 EXE、DLL、SYS 都是 PE（Portable Executable）格式。從檔案開頭依序是：
```
位移 0      DOS 標頭（64 位元組）：e_magic = "MZ"（4D 5A）
位移 3Ch    e_lfanew（4 位元組）：PE 簽章所在的檔案位置
            DOS stub：在 DOS 下執行時印出 "This program cannot be run in DOS mode."
e_lfanew    PE 簽章 "PE\0\0"（50 45 00 00）
+4          COFF 檔頭（20 位元組）
+24         選用標頭（Optional Header，PE32 為 224 位元組，PE32+ 為 240 位元組）
            區段表（每個區段 40 位元組）
            各區段的內容（.text、.rdata、.data ……）
```
COFF 檔頭重點欄位：
・Machine：014Ch = x86（32 位元）、8664h = x64
・NumberOfSections：區段數
・Characteristics：0002h 可執行檔、2000h 是 DLL

選用標頭（其實一定要有）重點欄位：
・Magic：10Bh = PE32（32 位元）、20Bh = PE32+（64 位元）
・AddressOfEntryPoint：進入點的 RVA
・ImageBase：偏好的載入位址（32 位元 EXE 常見 00400000h；64 位元 EXE 常見 140000000h）
・SectionAlignment（記憶體中，通常 1000h）、FileAlignment（檔案中，通常 200h）
・Subsystem：2 = GUI 視窗程式、3 = 主控台程式
・DllCharacteristics：0040h 支援 ASLR（DYNAMIC_BASE）、0100h 支援 DEP（NX_COMPAT）
・資料目錄（Data Directory）：16 組「RVA + 大小」

常用的資料目錄編號：
```
0 匯出表 Export        1 匯入表 Import       2 資源 Resource
3 例外 Exception(.pdata) 4 憑證 Security      5 重定位 Base Relocation
6 除錯 Debug           9 TLS                 10 載入設定 Load Config
12 IAT                 13 延遲載入匯入        14 CLR 標頭（.NET 組件）
```
注意：第 4 項（數位簽章）記錄的是「檔案位置」，其他都是 RVA。

區段表每一項：Name（8 位元組）、VirtualSize、VirtualAddress（RVA）、SizeOfRawData、PointerToRawData（檔案位置）、Characteristics（20000000h 可執行、40000000h 可讀、80000000h 可寫）。

## RVA、VA 與檔案位置的換算
id: bin-rva-va-offset
category: 推導
arch: 通用
tags: RVA, VA, 虛擬位址, 相對虛擬位址, 檔案位置, file offset, ImageBase, PointerToRawData, VirtualAddress, 位址換算, 推導
---
三種位址：
・VA（虛擬位址）：程式載入後在記憶體中的位址，反組譯清單顯示的就是 VA
・RVA（相對虛擬位址）：相對於 ImageBase 的距離，PE 檔頭裡大多存 RVA
・檔案位置（File Offset）：在磁碟檔案中的第幾個位元組，用十六進位編輯器看到的位置

公式：
```
VA  = ImageBase + RVA
RVA = VA - ImageBase
檔案位置 = RVA - 區段.VirtualAddress + 區段.PointerToRawData
RVA      = 檔案位置 - 區段.PointerToRawData + 區段.VirtualAddress
```
先找出 RVA 落在哪個區段：VirtualAddress ≤ RVA < VirtualAddress + VirtualSize。

完整範例：ImageBase = 00400000h，區段表如下
```
區段     VirtualAddress  VirtualSize  PointerToRawData  SizeOfRawData
.text    00001000h       00001C34h    00000400h         00001E00h
.rdata   00003000h       00000520h    00002200h         00000600h
.data    00004000h       00000080h    00002800h         00000200h
```
題目一：字串在 VA 00403010h，它在檔案的哪裡？
```
RVA = 00403010h - 00400000h = 3010h
3000h ≤ 3010h < 3000h + 520h  → 在 .rdata
檔案位置 = 3010h - 3000h + 2200h = 2210h
```
題目二：進入點 AddressOfEntryPoint = 14E0h，VA 與檔案位置？
```
VA = 00400000h + 14E0h = 004014E0h
1000h ≤ 14E0h < 2C34h → 在 .text
檔案位置 = 14E0h - 1000h + 400h = 8E0h
```
題目三：十六進位編輯器在檔案位置 523h 看到指令，它的 VA？
```
400h ≤ 523h < 400h + 1E00h → 在 .text
RVA = 523h - 400h + 1000h = 1123h
VA  = 00400000h + 1123h = 00401123h
```
注意：若 RVA 超過 SizeOfRawData（例如 .bss 未初始化資料），那部分在檔案中不存在，載入時補 0。

## 常見區段：.text .data .rdata .bss .idata .reloc
id: bin-sections
category: 概念
arch: 通用
tags: 區段, section, .text, .data, .rdata, .bss, .idata, .edata, .reloc, .rsrc, .pdata, .tls, 程式碼區, 資料區
---
```
.text    程式碼（可執行、唯讀），反組譯主要看這裡
.data    已初始化的全域變數（可讀寫），例如 int count = 7;
.rdata   唯讀資料：字串常數、const 陣列、浮點常數；MSVC 也把匯入表、除錯目錄放在這裡
.bss     未初始化的全域變數（int buf[100];），檔案中不佔空間，載入時補 0
.idata   匯入表與 IAT（MinGW 常獨立成一個區段）
.edata   匯出表（DLL 才有，MSVC 通常併入 .rdata）
.reloc   基底重定位資料（ASLR 或 DLL 換位址載入時使用）
.rsrc    資源：圖示、對話框、字串表、版本資訊、manifest
.pdata   x64 的例外處理資料（每個函式的開始、結束位址與 unwind 資訊）
.tls     執行緒區域儲存（Thread Local Storage）的初始資料
.CRT     C 執行階段的初始化函式指標表（MSVC）
```
區段名稱只是慣例，作業系統真正看的是區段的屬性旗標（可執行／可讀／可寫）。
看到 UPX0、UPX1 或名稱奇怪、內容很亂（高熵）的區段，常表示檔案被壓縮或加殼，直接反組譯看不到原本的程式。

## 匯入表與 IAT：為什麼是 call dword ptr [__imp_X]
id: bin-imports-iat
category: 概念
arch: x86
tags: 匯入表, Import Table, IAT, 匯入位址表, __imp_, call dword ptr, API, kernel32, 延遲載入, delay load, 間接呼叫, 序號
---
程式呼叫 DLL 中的函式（例如 ExitProcess）時，編譯時並不知道它在記憶體的哪裡，所以：
1. 執行檔的「匯入表」記錄需要哪些 DLL、哪些函式（名稱或序號）
2. 每個函式在 IAT（Import Address Table，匯入位址表）佔一個槽位（32 位元 4 位元組、64 位元 8 位元組）
3. Windows 載入程式時，把每個函式的真實位址填進 IAT
4. 程式碼透過 IAT 間接呼叫

```
; 32 位元：FF 15 + 槽位的絕對位址
call dword ptr [__imp_ExitProcess]     ; 呼叫 IAT 槽位裡存的位址
; 64 位元：FF 15 + RIP 相對位移
call qword ptr [__imp_GetStdHandle]
; 跳板（thunk）：編譯器不知道函式來自 DLL 時，先 call 一個小函式，再由它跳過去
j_printf:
    jmp dword ptr [__imp_printf]
```
ASSAM 把 IAT 槽位命名為 __imp_函式名；以序號匯入（沒有名稱）的寫成 __imp_DLL名_序號，例如 __imp_WS2_32.dll_115。註解中的 KERNEL32.dll!ExitProcess 表示「KERNEL32.dll 裡的 ExitProcess」。

匯入表結構（資料目錄第 1 項）：每個 DLL 一筆 IMAGE_IMPORT_DESCRIPTOR（20 位元組）：OriginalFirstThunk（名稱表 INT 的 RVA）、Name（DLL 名稱的 RVA）、FirstThunk（IAT 的 RVA），最後一筆全為 0。
INT／IAT 的每一項：最高位元為 1 表示以序號匯入（低 16 位元是序號）；否則是指向「Hint（2 位元組）+ 函式名稱」的 RVA。

延遲載入（Delay Load，資料目錄第 13 項）：DLL 在第一次呼叫該函式時才載入。IAT 一開始指向一段輔助程式（__delayLoadHelper2），它呼叫 LoadLibrary 與 GetProcAddress 後把真正位址填回 IAT。

看匯入表是分析程式最快的方法：用到 MessageBoxA 表示會跳出訊息方塊、CreateFileA/WriteFile 表示會寫檔、只有 LoadLibraryA + GetProcAddress 則可能是加殼程式自己找 API。

## 匯出表與 DLL：序號與轉送匯出
id: bin-exports-dll
category: 概念
arch: 通用
tags: 匯出表, Export Table, DLL, 序號, ordinal, 轉送匯出, forwarder, GetProcAddress, 名稱修飾, dllexport, .def
---
DLL 用匯出表（資料目錄第 0 項）公開函式給其他程式使用。IMAGE_EXPORT_DIRECTORY 重點欄位：
```
Name                  DLL 名稱
Base                  序號基底（通常為 1）
NumberOfFunctions     匯出位址表 EAT 的項數
NumberOfNames         有名稱的匯出數
AddressOfFunctions    EAT：每項是函式的 RVA
AddressOfNames        名稱 RVA 陣列（依字母排序，可二分搜尋）
AddressOfNameOrdinals 對應的 EAT 索引（2 位元組）
```
用名稱找函式：在名稱表找到第 i 個 → 讀 NameOrdinals[i] 得到索引 k → EAT[k] 就是函式 RVA；序號 = Base + k。
只有序號沒有名稱的匯出，只能用 GetProcAddress(h, (LPCSTR)序號) 取得。

轉送匯出（Forwarder）：若 EAT 中的 RVA 落在匯出目錄本身的範圍內，它指的不是程式碼，而是一個字串，例如 "NTDLL.RtlAllocateHeap"，表示「請改用另一個 DLL 的函式」。kernel32.dll 有許多函式就是轉送到 ntdll.dll 或 kernelbase.dll。

名稱修飾：32 位元 stdcall 函式匯出時可能帶有參數位元組數，例如 MinGW 編譯
```
__declspec(dllexport) int __stdcall AddNumbers(int a, int b);   → 匯出名 AddNumbers@8
__declspec(dllexport) int Multiply(int a, int b);                → 匯出名 Multiply
```
@8 表示兩個 4 位元組參數，函式用 ret 8 清除。MSVC 可用 .def 檔去掉修飾；C++ 函式則會有 ?Func@@YAHHH@Z 這種 C++ 名稱修飾（用 extern "C" 可避免）。

DLL 的進入點是 DllMain（由 _DllMainCRTStartup 呼叫），在載入、卸載、建立執行緒時被呼叫，不是被匯出的函式。

## ImageBase、ASLR 與重定位
id: bin-imagebase-aslr
category: 概念
arch: 通用
tags: ImageBase, ASLR, 重定位, relocation, .reloc, DYNAMIC_BASE, 基底位址, HIGHLOW, DIR64, 載入位址
---
ImageBase 是程式「希望」被載入的位址；反組譯工具顯示的位址都是以 ImageBase 計算的。
```
32 位元 EXE 預設 00400000h      32 位元 DLL（MSVC）預設 10000000h
64 位元 EXE 預設 140000000h     64 位元 DLL（MSVC）預設 180000000h
```
ASLR（位址空間配置隨機化）：DllCharacteristics 有 DYNAMIC_BASE（0040h）時，Windows 每次開機或載入都可能把程式放到不同位址，讓攻擊者猜不到程式碼的位置。

換位址後，程式中寫死的絕對位址（例如 push offset str、call dword ptr [IAT槽位]）都要修正，這就是 .reloc 區段的用途：
```
重定位區塊：PageRVA（4 位元組）、BlockSize（4 位元組）、接著每項 2 位元組
每項：高 4 位元 = 類型（3 = HIGHLOW 32 位元、10 = DIR64 64 位元），低 12 位元 = 頁內位移
修正方式：該位置的值 += 差值（實際載入位址 - ImageBase）
```
推導：ImageBase = 00400000h，實際載入在 01350000h，差值 = 00F50000h。
指令 push 00403000h 的運算元有重定位項，載入後變成 00403000h + 00F50000h = 01353000h。

x64 程式大多使用 RIP 相對定址，需要的重定位比 32 位元少很多。
用除錯器看到的位址和反組譯清單不同時：先減掉模組實際基底得到 RVA，再加上 ImageBase 對照。

## 進入點與 C 執行階段的啟動流程
id: bin-entry-crt
category: 概念
arch: x86
tags: 進入點, entry point, AddressOfEntryPoint, mainCRTStartup, WinMainCRTStartup, __tmainCRTStartup, main, 啟動程式, CRT, __getmainargs, _initterm, __main, 找 main
---
執行檔的進入點（AddressOfEntryPoint）通常不是 main，而是 C 執行階段（CRT）的啟動程式：
```
主控台程式（MSVC）   mainCRTStartup    → __scrt_common_main_seh → main(argc, argv, envp)
視窗程式（MSVC）     WinMainCRTStartup → WinMain(hInstance, hPrev, lpCmdLine, nCmdShow)
Unicode 版本        wmainCRTStartup / wWinMainCRTStartup
DLL                 _DllMainCRTStartup → DllMain
MinGW              mainCRTStartup → __tmainCRTStartup → main
```
啟動程式要做的事：初始化安全 cookie、設定堆積與 stdio、呼叫全域建構函式（_initterm）、取得命令列參數（MSVC 用 __p___argc／__p___argv，MinGW 用 __getmainargs）、呼叫 main，最後把 main 的回傳值交給 exit。

在反組譯中找 main 的技巧：
1. 從進入點往下追，找「把 argc、argv、envp 當參數」的那個 call
2. 這個 call 的回傳值（EAX）緊接著被傳給 exit 或 _cexit
3. MinGW 的 main 開頭會呼叫 __main（執行全域建構函式），32 位元還會先 and esp, 0FFFFFFF0h 對齊堆疊
```
; MinGW 32 位元 __tmainCRTStartup 末段的典型樣子
call  __getmainargs
...
call  sub_401560        ; ← 這就是 main（參數是 argc、argv、envp）
mov   [esp], eax
call  exit              ; 把 main 的回傳值當結束碼
```
4. 用字串找：程式印出的訊息字串通常被 main 或它呼叫的函式引用

## 編譯器產生的函式開頭與結尾
id: bin-prologue-epilogue
category: 範例
arch: x86
tags: prologue, epilogue, 函式開頭, 函式結尾, push ebp, mov ebp esp, sub esp, leave, ret, 堆疊框架, shadow space, and esp, 對齊
---
32 位元，有堆疊框架（未最佳化或 /Oy-）：
```
push ebp              ; 保存呼叫者的 EBP
mov  ebp, esp         ; 建立新的堆疊框架
sub  esp, 10h         ; 保留 16 位元組區域變數
push esi              ; 保存要用到的非揮發暫存器（EBX、ESI、EDI）
...
pop  esi
mov  esp, ebp         ; 釋放區域變數（這兩行等於 leave）
pop  ebp
ret                   ; cdecl：呼叫者清參數
ret  8                ; stdcall：被呼叫者清掉 8 位元組參數
```
MinGW 的 main（32 位元）多了堆疊對齊與 __main：
```
push ebp
mov  ebp, esp
and  esp, 0FFFFFFF0h  ; 讓 ESP 對齊 16 位元組（清除最低 4 個位元）
sub  esp, 20h
call __main           ; 執行 C++ 全域建構函式等初始化
```
最佳化後常省略 EBP 框架，直接用 ESP：
```
sub  esp, 1Ch
...                   ; 參數在 [esp+20h]、[esp+24h]
add  esp, 1Ch
ret
```
x64 Windows：
```
sub  rsp, 28h         ; 20h 的 shadow space（給被呼叫者存 RCX、RDX、R8、R9）+ 8 讓 RSP 對齊 16
...
add  rsp, 28h
ret
```
為什麼是 28h 而不是 20h？call 推入 8 位元組返回位址後 RSP 會變成「16 的倍數 + 8」，再減 28h 就回到 16 的倍數，呼叫其他函式時才符合對齊規定。

## 呼叫慣例在機器碼中的樣子
id: bin-calling-convention-asm
category: 概念
arch: x86
tags: 呼叫慣例, calling convention, cdecl, stdcall, fastcall, thiscall, x64, System V, add esp, ret n, rcx rdx r8 r9, rdi rsi, 參數傳遞, 反組譯
---
從反組譯判斷呼叫慣例的方法：看參數怎麼放、誰清堆疊。
```
cdecl（C 函式預設）
    push 2
    push 1
    call _add             ; 參數由右到左推入
    add  esp, 8           ; ← 呼叫者清除 8 位元組參數
stdcall（Win32 API、WINAPI）
    push 0
    call dword ptr [__imp_ExitProcess]   ; 呼叫後沒有 add esp
    ; 被呼叫者用 ret 4 清除參數；名稱修飾為 _ExitProcess@4
fastcall（MSVC __fastcall）
    mov  edx, 2           ; 第 2 個參數
    mov  ecx, 1           ; 第 1 個參數
    call @add@8           ; 其餘參數在堆疊，被呼叫者清除
thiscall（MSVC C++ 成員函式）
    mov  ecx, esi         ; this 指標放 ECX
    call ?Get@Point@@QAEHXZ
```
GCC／MinGW 常不用 push，而是先保留空間再用 mov 放參數：
```
mov  dword ptr [esp+4], 2
mov  dword ptr [esp], 1
call sub_401500
```
呼叫 stdcall 函式後 MinGW 常接一行 sub esp, N，用來補回被呼叫者 ret N 清掉的空間。

x64：
```
Windows x64：RCX, RDX, R8, R9（浮點 XMM0～XMM3），第 5 個起放 [rsp+20h]、[rsp+28h]…，呼叫者保留 20h shadow space
Linux／macOS（System V AMD64）：RDI, RSI, RDX, RCX, R8, R9（浮點 XMM0～XMM7），沒有 shadow space
回傳值：EAX／RAX（32 位元回傳 64 位元整數用 EDX:EAX；浮點用 ST0 或 XMM0）
```
Windows x64 呼叫後要保留的暫存器：RBX、RBP、RDI、RSI、R12～R15；RAX、RCX、RDX、R8～R11 可能被改掉。

## 區域變數與參數的位址：[ebp-4]、[ebp+8]、[rsp+20h]
id: bin-locals-params
category: 概念
arch: x86
tags: 區域變數, 參數, ebp-4, ebp+8, 堆疊框架, stack frame, 位移, 返回位址, rsp+20h, home space, 反組譯
---
32 位元有 EBP 框架時，堆疊長這樣（位址由下往上增加）：
```
[ebp+10h]  第 3 個參數
[ebp+0Ch]  第 2 個參數
[ebp+8]    第 1 個參數
[ebp+4]    返回位址（call 推入）
[ebp]      舊的 EBP（push ebp）
[ebp-4]    第 1 個區域變數
[ebp-8]    第 2 個區域變數
```
口訣：正位移是參數，負位移是區域變數，[ebp+8] 是第一個參數。

範例：反組譯 → C
```
push ebp
mov  ebp, esp
sub  esp, 4
mov  eax, [ebp+8]       ; a
add  eax, [ebp+0Ch]     ; a + b
mov  [ebp-4], eax       ; int sum = a + b;
mov  eax, [ebp-4]       ; return sum;
leave
ret
```
對應 int add(int a, int b) { int sum = a + b; return sum; }

沒有 EBP 框架時用 ESP：函式一進來 [esp] 是返回位址、[esp+4] 是第 1 個參數；之後每 push 一次或 sub esp, N，參數的位移就要跟著加。例如 MinGW 的 Multiply：
```
mov  eax, [esp+8]       ; b
imul eax, [esp+4]       ; a * b
ret
```
x64 Windows：前 4 個參數在 RCX、RDX、R8、R9，函式開頭常把它們存回呼叫者準備的 home space：mov [rsp+8], rcx。執行 sub rsp, 28h 之後，第 5 個參數在 [rsp+50h]（28h + 返回位址 8 + shadow 20h）。

## 編譯器最佳化的常見樣式
id: bin-compiler-idioms
category: 概念
arch: x86
tags: 最佳化, optimization, 編譯器, xor eax eax, test jz, lea 乘法, cmov, setcc, 迴圈展開, 尾呼叫, tail call, 反組譯樣式, idiom
---
看懂這些「慣用寫法」，反組譯就容易很多：
```
xor  eax, eax            → eax = 0（比 mov eax, 0 短：2 位元組 vs 5 位元組）
test eax, eax / jz L     → if (eax == 0) goto L
test al, 1 / jnz L       → if (x & 1)，判斷奇數
or   eax, 0FFFFFFFFh     → eax = -1（3 位元組）
lea  eax, [ecx+ecx*4]    → eax = ecx * 5
lea  eax, [eax+eax*2]    → eax = eax * 3
lea  eax, [ecx+8]        → eax = ecx + 8（不影響旗標）
shl  eax, 3              → eax * 8
sar  eax, 1              → 有號除以 2（負數還要先加 1 修正）
neg  eax / sbb eax, eax  → eax = (eax != 0) ? -1 : 0
setg al / movzx eax, al  → return a > b;
cmp  eax, ebx / cmovl eax, ebx → eax = max(eax, ebx)，不用跳躍
```
迴圈展開（Loop Unrolling）：一次處理 2 或 4 個元素，減少比較與跳躍次數
```
L:  add  eax, [edx]
    add  eax, [edx+4]
    add  edx, 8          ; 一次前進兩個 int
    cmp  edx, ecx
    jne  L
```
尾呼叫（Tail Call）：函式最後一件事是呼叫另一個函式時，用 jmp 取代 call + ret
```
jmp  sub_401600          ; 等於 return sub_401600(...);
```
其他：迴圈條件常被移到迴圈底部（先跳到底部判斷一次）；if-else 的區塊順序可能被調換；常數運算在編譯時就算好了；小函式被內嵌（inline）後就不會出現 call。

## 除以常數變成乘以魔術數字
id: bin-magic-division
category: 推導
arch: x86
tags: 除法最佳化, 魔術數字, magic number, 除以常數, mul, imul, shr, 0AAAAAAABh, 0CCCCCCCDh, 55555556h, 推導, 反組譯
---
除法指令很慢，所以編譯器把「除以常數」改成「乘以近似 2^n / d 的數，再右移」。

無號 x / 3：
```
mov  eax, 0AAAAAAABh
mul  ecx                 ; EDX:EAX = x × 0AAAAAAABh
shr  edx, 1              ; EDX = x / 3
```
推導：
```
0AAAAAAABh = 2863311531 = (2^33 + 1) / 3（也就是 2^33 / 3 無條件進位）
mul 之後 EDX = floor(x × M / 2^32)，再 shr 1 → floor(x × M / 2^33)
x × M / 2^33 = x/3 + x/(3 × 2^33)，後面的誤差 < 1/6，不會影響整數部分
驗算 x = 100：100 × 2863311531 = 286331153100，÷ 2^33(8589934592) = 33.33… → 33 ✓
```
看到魔術數字反推除數：d ≈ 2^(32+右移位數) / M。例：0CCCCCCCDh 配 shr edx, 3 → 2^35 / 3435973837 ≈ 10，所以是 x / 10。

有號 x / 3：
```
mov  eax, 55555556h      ; (2^32 + 2) / 3
imul ecx                 ; EDX = floor(x × M / 2^32)
mov  eax, edx
shr  eax, 31             ; 取符號位元：負數時為 1
add  edx, eax            ; 負數加 1，讓結果往 0 取整（C 的規則）
```
驗算 x = -7：-7 × 1431655766 / 2^32 = -2.33… → floor = -3，再加 1 = -2，與 C 的 -7 / 3 = -2 相同。

取餘數 x % 3 通常接著算 x - (x / 3) × 3：lea eax, [edx+edx*2] 再 sub ecx, eax。
除以 2 的次方則直接右移：無號用 shr；有號要先加 (2^n - 1) 修正負數再 sar。

## switch 跳躍表
id: bin-switch-jumptable
category: 範例
arch: x86
tags: switch, case, 跳躍表, jump table, jmp dword ptr, 間接跳躍, eax*4, 反組譯
---
case 值連續時，編譯器把 switch 編成「跳躍表」：一個存放各 case 位址的陣列。
```
switch (x) { case 0: … case 1: … case 2: … case 3: … case 4: … default: … }

mov  eax, [ebp+8]        ; x
cmp  eax, 4
ja   loc_default         ; 無號比較：x > 4 或 x 是負數都跳到 default
jmp  dword ptr [eax*4+jumptable]   ; 依 x 取出表中的位址再跳
jumptable:
    dd loc_case0, loc_case1, loc_case2, loc_case3, loc_case4
```
重點：
・cmp + ja 用「無號」比較，一次擋掉超出範圍與負數兩種情況
・case 從 1 開始時，前面常有 sub eax, 1（或 dec eax）
・x64 的表通常存 32 位元相對位移而不是完整位址：
```
lea    rdx, [rip+table]
movsxd rax, dword ptr [rdx+rax*4]
add    rax, rdx
jmp    rax
```
・case 值很分散時，編譯器改用一連串 cmp/je 或二分搜尋式的比較
・跳躍表是放在程式碼區中的資料，線性掃描的反組譯器可能把它誤當成指令

## 安全 cookie 與堆疊保護
id: bin-stack-cookie
category: 概念
arch: x86
tags: 安全 cookie, security cookie, /GS, __security_check_cookie, __stack_chk_fail, stack protector, 堆疊保護, 緩衝區溢位, canary
---
為了偵測緩衝區溢位，編譯器在區域陣列和返回位址之間放一個隨機值（cookie／canary），函式返回前檢查它有沒有被改掉。

MSVC（/GS，預設開啟）：
```
mov  eax, dword ptr [___security_cookie]
xor  eax, ebp                  ; 和 EBP 混合，每個框架都不同
mov  [ebp-4], eax              ; 放在區域變數與返回位址之間
...
mov  ecx, [ebp-4]
xor  ecx, ebp
call @__security_check_cookie@4   ; fastcall：值放在 ECX；不相符就立即終止程式
```
x64 MSVC 改用 xor rax, rsp，檢查函式名稱是 __security_check_cookie。

GCC（-fstack-protector）：
```
mov  rax, qword ptr fs:[28h]   ; Linux x64：canary 存在 TLS
mov  [rbp-8], rax
...
mov  rax, [rbp-8]
sub  rax, qword ptr fs:[28h]
jne  L_fail
L_fail: call __stack_chk_fail  ; 不相符就中止
```
MinGW 則從全域變數 __stack_chk_guard 取值。

反組譯時看到這些程式碼可以直接略過，它們和程式邏輯無關；但它也告訴你：這個函式有區域陣列（例如 char buf[64]）。

## __chkstk 與大型堆疊配置
id: bin-chkstk
category: 概念
arch: x86
tags: __chkstk, ___chkstk_ms, _alloca, 堆疊探測, stack probe, guard page, 大型區域陣列, 4KB
---
Windows 的堆疊是用「保護頁」（guard page）一頁一頁（4KB）往下擴張的。如果一次 sub esp, 10000h，可能直接跳過保護頁去存取還沒配置的記憶體而當掉。
所以區域變數超過一頁（4096 位元組）時，編譯器改成呼叫 __chkstk，它從高位址往低位址每 4KB 碰一下（探測），讓堆疊正常擴張。
```
; MSVC 32 位元：__chkstk 自己會把 ESP 減掉 EAX
mov  eax, 2010h
call __chkstk
; MSVC x64：只探測，呼叫者自己減
mov  eax, 2028h
call __chkstk
sub  rsp, rax
; MinGW（32／64 位元）
mov  eax, 1010h
call ___chkstk_ms
sub  esp, eax
```
在反組譯中看到 mov eax, 很大的數 + call __chkstk，就表示這個函式有很大的區域陣列，EAX 的值約等於區域變數的總大小。
_alloca（在堆疊上動態配置）也會用到類似的探測程式。

## 字串與全域變數的引用
id: bin-string-global-refs
category: 範例
arch: x86
tags: 字串, 全域變數, push offset, lea rcx, rip, 引用, 交叉參考, xref, str_, data_, 反組譯
---
32 位元程式用絕對位址引用資料：
```
push offset str_404000          ; printf 的格式字串 "Sum = %d\n"
call dword ptr [__imp_printf]
add  esp, 8
mov  eax, dword ptr [data_404010]    ; 讀取全域變數
add  dword ptr [data_404010], 1      ; global_counter++
mov  dword ptr [esp], offset str_404000   ; GCC 常用 mov 放參數
```
64 位元程式用 RIP 相對定址：
```
lea  rcx, [rip+0x2fe9]          ; ASSAM 會顯示成 lea rcx, [str_140004000]
call qword ptr [__imp_puts]
mov  eax, dword ptr [rip+0x3005] ; 讀取全域變數
```
ASSAM 的命名慣例：str_位址 是字串、data_位址 是其他全域資料、sub_位址 是函式、loc_位址 是跳躍目標、__imp_名稱 是匯入表槽位。
分析技巧：先看程式用到哪些字串（錯誤訊息、提示文字、格式字串），再回頭找「誰引用了這個字串」，通常很快就能定位到關鍵函式。格式字串 "%d" 的參數個數也能告訴你 printf 有幾個參數。

## x64 RIP 相對定址的位址推導
id: bin-rip-relative
category: 推導
arch: x86
tags: RIP 相對定址, RIP-relative, x64, 位移, disp32, 下一條指令, 位址計算, 推導, lea rip
---
x64 的 [rip+位移] 是以「下一條指令的位址」為基準：
```
目標位址 = 這條指令的位址 + 指令長度 + 位移（有號 32 位元）
```
範例一：
```
0000000140001010: 48 8D 0D E9 2F 00 00    lea rcx, [rip+0x2fe9]
指令長度 = 7 → 下一條指令 = 140001017h
位移 = 00002FE9h（little-endian 的 E9 2F 00 00）
目標 = 140001017h + 2FE9h = 140004000h   → 字串所在位址
```
範例二（呼叫匯入函式）：
```
0000000140002000: FF 15 FA 1F 00 00       call qword ptr [rip+0x1ffa]
下一條 = 140002006h，目標 = 140002006h + 1FFAh = 140004000h → IAT 槽位，例如 __imp_GetStdHandle
```
範例三（負位移）：位移位元組 F0 FF FF FF = -10h
```
指令在 140003000h、長度 6 → 140003006h - 10h = 140002FF6h
```
為什麼 x64 要這樣設計？程式碼不必寫死絕對位址，載入到任何位址都能執行（位置無關），也就幾乎不需要重定位。

## 填充位元組與函式邊界
id: bin-padding
category: 概念
arch: x86
tags: 填充, padding, int3, 0CCh, nop, 90h, 多位元組 nop, 函式邊界, 對齊, align, xchg ax ax, lea esi
---
編譯器把每個函式的開頭對齊到 16 位元組，中間空出來的位元組用填充指令補滿：
```
MSVC：int3（CCh）           → 萬一執行到就觸發中斷，方便抓錯
GCC／MinGW：nop（90h）或多位元組 nop：
    66 90                xchg ax, ax
    0F 1F 40 00          nop dword ptr [eax+0]
    8D 74 26 00          lea esi, [esi+0]      ; 什麼都不改的指令
    8D B6 00 00 00 00    lea esi, [esi+0]
```
判斷函式邊界的線索：
1. ret 或無條件 jmp 之後接著填充位元組
2. 下一個 16 位元組對齊的位址開始出現 push ebp、sub esp、mov edi, edi（MSVC 熱修補用的 2 位元組 nop）等開頭
3. 被 call 呼叫的位址一定是函式開頭
4. x64 的 .pdata 直接列出每個函式的開始與結束位址，最可靠

反組譯清單中看到一串 int3 或 nop 不必理會，它們不會被執行。

## 線性掃描 vs 遞迴下降反組譯
id: bin-linear-vs-recursive
category: 概念
arch: 通用
tags: 線性掃描, linear sweep, 遞迴下降, recursive descent, 反組譯演算法, 資料混在程式碼, objdump, IDA, 間接跳躍
---
兩種反組譯策略：
```
線性掃描（Linear Sweep）：從區段開頭一條接一條解碼到結尾（objdump -d 的做法）
    優點：不會漏掉任何位元組，簡單
    缺點：程式碼中夾雜資料（跳躍表、字串、對齊填充）時會被當成指令，
          而且解碼錯位後，後面一段指令都會跟著錯
遞迴下降（Recursive Descent）：從進入點、匯出函式出發，跟著 jmp／jcc／call 走
    優點：只解碼真正會執行到的路徑，能區分程式碼與資料，能自動找出函式
    缺點：透過暫存器或函式指標的間接呼叫（call eax、虛擬函式表、callback）追不到，可能漏掉函式
```
實用的反組譯器（例如 ASSAM、IDA、Ghidra）多採「遞迴下降為主，再掃描沒走到的空白區」的混合做法，並用函式開頭樣式（push ebp / mov ebp, esp）判斷空白區裡是否還有函式。

反組譯結果可疑的徵兆：出現 (bad)、大量 add [eax], al（其實是 00 00 位元組的資料）、跳到指令中間、看起來毫無意義的指令序列。

## .NET 組件與 IL（中間語言）
id: bin-dotnet-il
category: 範例
arch: 通用
tags: .NET, IL, CIL, MSIL, 組件, assembly, 堆疊機, ldarg, ldloc, stloc, ldstr, call, callvirt, newobj, ret, br, C#, 中繼資料, 反編譯, _CorExeMain
---
C# 編譯後的 EXE／DLL 也是 PE 檔，但裡面放的是 IL（Intermediate Language）而不是 x86 機器碼：
・資料目錄第 14 項（CLR 標頭）不為 0 就是 .NET 組件
・原生進入點只有一行 jmp dword ptr [__imp__CorExeMain]（mscoree.dll），真正的程式由 CLR 執行時才 JIT 編譯成機器碼
・中繼資料（Metadata）保留了類別、方法、欄位的名稱與型別，所以很容易反編譯回 C#

IL 是「堆疊機」：運算元先推到評估堆疊，指令再從堆疊取用。常見指令：
```
ldarg.0 / ldarg.1   推入第 0、1 個參數（實體方法的 ldarg.0 是 this）
ldloc.0 / stloc.0   推入／取出存到第 0 個區域變數
ldc.i4 5            推入 int 常數 5（ldc.i4.0～ldc.i4.8 有短格式）
ldstr "Hi"          推入字串參考
add sub mul div     從堆疊取兩個值運算後推回結果
call                呼叫靜態或非虛擬方法
callvirt            呼叫虛擬／實體方法（會先檢查 null）
newobj              建立物件並呼叫建構函式
br / br.s           無條件跳躍；brtrue、brfalse、beq、blt 為條件跳躍
ret                 返回（堆疊上若有值就是回傳值）
```
C# 方法與它的 IL（逐行對應的寫法）：
```
static int Add(int a, int b)
{
    int c = a + b;
    return c;
}

.method private hidebysig static int32 Add(int32 a, int32 b) cil managed
{
    .maxstack 2
    .locals init (int32 V_0)
    IL_0000: ldarg.0        // 堆疊：a
    IL_0001: ldarg.1        // 堆疊：a, b
    IL_0002: add            // 堆疊：a+b
    IL_0003: stloc.0        // c = a+b
    IL_0004: ldloc.0        // 堆疊：c
    IL_0005: ret            // return c
}
```
Hello World 的 Main：ldstr "Hello" → call void [mscorlib]System.Console::WriteLine(string) → ret。
實際編譯結果會因設定而略有不同：Release 版最佳化後常只剩 ldarg.0、ldarg.1、add、ret；Debug 版則多出 nop、br.s 與額外的區域變數，方便除錯器設定中斷點。

## DOS MZ 與 COM 執行檔格式
id: bin-dos-mz-com
category: 概念
arch: x86
tags: DOS, COM, MZ, EXE, 段落, paragraph, CS:IP, PSP, 100h, 載入模組, 實體位址, 重定位, 16 位元
---
COM 檔：沒有任何檔頭，整個檔案就是程式碼與資料
```
・載入到一個區段的位移 100h（前面 0～FFh 是 PSP 程式區段前綴，存命令列等資訊）
・CS = DS = ES = SS = 同一個區段，IP = 100h，SP 通常是 FFFEh
・程式加資料加堆疊必須在 64KB 內；原始碼要寫 org 100h
```
MZ 格式的 DOS EXE：有檔頭，可以超過 64KB、使用多個區段
```
位移 00h  e_magic    "MZ"
     02h  e_cblp     最後一頁的位元組數
     04h  e_cp       檔案頁數（每頁 512 位元組）
     06h  e_crlc     重定位項數
     08h  e_cparhdr  檔頭大小（以段落計，1 段落 = 16 位元組）
     0Eh  e_ss, 10h e_sp   初始 SS:SP（SS 相對於載入區段）
     14h  e_ip, 16h e_cs   初始 CS:IP（CS 相對於載入區段）
     18h  e_lfarlc   重定位表的位置
```
載入模組（Load Module）從檔案位置 e_cparhdr × 16 開始。DOS 把它放在 PSP 之後：起始區段 = PSP 區段 + 10h，CS = 起始區段 + e_cs。
重定位表的每一項（位移, 區段）指向一個字組，載入時要加上起始區段，例如 mov ax, seg data（MASM 的 mov ax, @data）。

實體位址推導（真實模式）：
```
實體位址 = 區段 × 16 + 位移
CS:IP = 1234h:0010h → 12340h + 0010h = 12350h
不同的區段:位移可以指向同一個實體位址：1235h:0000h 也是 12350h
```
檔頭大小 e_cparhdr = 2（32 位元組）、e_cs = 0、e_ip = 0 時：進入點在檔案位置 2 × 16 + 0 = 20h。

## 從反組譯推回 C 程式的步驟
id: bin-recover-c
category: 做法
arch: 通用
tags: 反組譯, 還原 C, 逆向, 找 main, API, 字串, 迴圈, if, 虛擬碼, 分析步驟, 讀懂反組譯
---
步驟：
1. 先看全貌：是 32 還是 64 位元、主控台還是視窗程式、是不是 .NET、有沒有加殼
2. 看匯入的 API：printf/puts 表示主控台輸出，MessageBoxA 表示訊息方塊，CreateFileA/ReadFile 表示讀寫檔案
3. 看字串：提示文字、格式字串（"%d"）、錯誤訊息，找出引用它們的函式
4. 找 main：從進入點經過 CRT 啟動程式，找傳入 argc/argv 且回傳值交給 exit 的那個 call
5. 一個函式一個函式看：
   ・開頭結尾（push ebp…leave/ret）可以略過，sub esp, N 告訴你區域變數的大小
   ・[ebp+8]、[ebp+0Ch] 是參數，[ebp-4] 是區域變數，先替它們取名字
   ・call 前面放的參數 → 函式的參數；call 後的 EAX → 回傳值
6. 還原控制結構：
```
cmp  eax, 10                     if (x <= 10) {
jg   L_else                          ...
...                              } else {
jmp  L_end                           ...
L_else: ...                      }
L_end:

    xor  esi, esi                for (i = 0; i < n; i++) {
L_top:                               sum += a[i];
    add  eax, [ebx+esi*4]        }
    inc  esi
    cmp  esi, ecx
    jl   L_top                  ← 往回跳的條件跳躍 = 迴圈
```
   ・注意：組合語言的條件跳躍通常是 C 條件的「相反」（C 寫 x <= 10，組合語言寫 jg 跳過 then 區塊）
   ・jl/jg 是有號比較（int），jb/ja 是無號比較（unsigned、指標、陣列索引範圍檢查）
7. 寫成虛擬碼並逐步改名，最後用「推導模擬」頁執行函式驗證你的理解

## 反組譯結果看起來不對時
id: bin-debug-disasm
category: 除錯
arch: x86
tags: 反組譯錯誤, bad, 資料當成程式碼, 位元模式, 加殼, UPX, 錯位, 除錯, 反組譯問題
---
常見狀況與原因：
```
出現 (bad) 或一堆 add byte ptr [eax], al
    → 解碼到資料（00 00 位元組），或從錯誤的位址開始解碼
指令看起來合理但數值很怪（例如 dec eax、inc ecx 連續出現）
    → 用錯位元模式：把 64 位元程式碼當 32 位元解碼（48h 在 32 位元是 dec eax，在 64 位元是 REX.W 前綴）
同一段位元組有兩種解法
    → 有程式故意跳到指令中間（反分析技巧），或跳躍表、字串夾在程式碼中
函式很少、匯入只有 LoadLibraryA／GetProcAddress、區段名 UPX0/UPX1
    → 檔案被壓縮或加殼，真正的程式在執行時才解開
找不到 main
    → 先找 CRT 啟動程式，再找把回傳值交給 exit 的 call；或從程式印出的字串反查
.NET 程式只看到一行 jmp
    → 真正的程式是 IL，要看 .NET 方法（IL 反組譯）
DOS 程式位址看起來重複
    → 16 位元用「區段:位移」，不同區段可能有相同的位移
```
建議：先看匯入表與字串掌握大方向，再深入單一函式；不確定時用「推導模擬」頁執行一小段程式碼驗證。
