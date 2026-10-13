# x86 程式框架與輸入輸出

## MASM 32 位元程式框架（Irvine32）
id: x86-masm-irvine-template
category: 範例
arch: x86
tags: MASM, Irvine32, 程式框架, 範本, template, Hello World, 32位元, Windows
---
Irvine 教科書（Assembly Language for x86 Processors）常用的框架：
```
INCLUDE Irvine32.inc

.data
msg BYTE "Hello, World!", 0

.code
main PROC
    mov  edx, OFFSET msg
    call WriteString
    call Crlf
    exit                    ; 結束程式（Irvine 巨集）
main ENDP
END main
```
不使用 Irvine 函式庫的純 Win32 版本：
```
.386
.model flat, stdcall
.stack 4096
ExitProcess PROTO, dwExitCode:DWORD

.code
main PROC
    mov  eax, 5
    add  eax, 6
    INVOKE ExitProcess, 0
main ENDP
END main
```

## Irvine32 常用程序
id: x86-irvine32-procs
category: 概念
arch: x86
tags: Irvine32, WriteString, WriteDec, WriteInt, WriteHex, ReadInt, ReadString, Crlf, DumpRegs, 輸出, 輸入
---
輸出：
```
WriteString   顯示 EDX 指向、以 0 結尾的字串
WriteDec      以無號十進位顯示 EAX
WriteInt      以有號十進位顯示 EAX（帶 + 號）
WriteHex      以 8 位數十六進位顯示 EAX
WriteBin      以二進位顯示 EAX
WriteChar     顯示 AL 的字元
Crlf          換行
DumpRegs      顯示所有暫存器與旗標（除錯好用）
DumpMem       顯示記憶體（ESI=位址、ECX=個數、EBX=單位大小）
```
輸入：
```
ReadInt       讀取有號整數 → EAX
ReadDec       讀取無號整數 → EAX
ReadChar      讀取一個字元 → AL（不回顯）
ReadString    讀字串到 EDX 緩衝區，ECX = 緩衝區大小，回傳長度在 EAX
```
其他：Clrscr 清螢幕、Delay 延遲 EAX 毫秒、Randomize + RandomRange 產生亂數、SetTextColor 設定顏色。
這些程序除了回傳值之外不會改變其他暫存器。

## MASM 16 位元 DOS 程式框架
id: x86-dos-template
category: 範例
arch: x86
tags: DOS, 16位元, .model small, INT 21h, @data, 8086, TASM, 程式框架
---
```
.model small
.stack 100h
.data
msg db 'Hello, DOS!', 0Dh, 0Ah, '$'
.code
main proc
    mov ax, @data
    mov ds, ax          ; 設定 DS 指向資料段
    mov ah, 09h         ; 顯示字串
    mov dx, offset msg
    int 21h
    mov ah, 4Ch         ; 結束程式，回到 DOS
    int 21h
main endp
end main
```
現在的 Windows 不能直接執行 16 位元程式，可以用 DOSBox 搭配 MASM/TASM 或 emu8086 練習。

## DOS INT 21h 常用功能
id: x86-int21h
category: 概念
arch: x86
tags: INT 21h, DOS, 中斷, AH, 功能呼叫, 輸入, 輸出, 結束程式, 4Ch, 09h, 02h, 01h
---
功能號碼放在 AH，然後執行 INT 21h：
```
AH=01h  讀取一個字元（有回顯）→ AL
AH=02h  顯示 DL 中的字元
AH=07h  讀取字元（無回顯）→ AL
AH=09h  顯示 DS:DX 指向的字串（必須以 '$' 結尾）
AH=0Ah  讀取一行輸入到 DS:DX 緩衝區（第 1 位元組放最大長度，第 2 位元組回傳實際長度）
AH=2Ch  取得系統時間（CH 時、CL 分、DH 秒）
AH=3Ch/3Dh/3Eh/3Fh/40h  建立／開啟／關閉／讀取／寫入檔案
AH=4Ch  結束程式（AL = 結束碼）
```
範例：顯示一個字元 'A'
```
mov ah, 02h
mov dl, 'A'
int 21h
```

## BIOS 中斷 INT 10h 與 INT 16h
id: x86-bios-int
category: 概念
arch: x86
tags: BIOS, INT 10h, INT 16h, 顯示, 游標, 鍵盤, 開機程式, 中斷
---
BIOS 服務在作業系統之前就可以使用（開機磁區、DOS 程式）：
```
INT 10h  顯示服務
  AH=00h 設定顯示模式（AL=03h 文字模式 80×25，AL=13h 圖形 320×200×256 色）
  AH=02h 設定游標位置（BH=頁、DH=列、DL=行）
  AH=0Eh 電傳打字輸出 AL 的字元
INT 16h  鍵盤服務
  AH=00h 等待按鍵：AL=ASCII 碼、AH=掃描碼
  AH=01h 檢查有無按鍵（ZF=1 表示沒有）
```

## NASM Linux 32 位元 Hello World
id: x86-nasm-linux32
category: 範例
arch: x86
tags: NASM, Linux, int 0x80, int 80h, 32位元, sys_write, sys_exit, Hello World, ld
---
```
section .data
    msg db 'Hello, Linux!', 10
    len equ $ - msg          ; 字串長度

section .text
    global _start
_start:
    mov eax, 4               ; sys_write
    mov ebx, 1               ; 檔案代碼 1 = stdout
    mov ecx, msg             ; NASM 中變數名稱就是位址
    mov edx, len
    int 0x80
    mov eax, 1               ; sys_exit
    xor ebx, ebx             ; 結束碼 0
    int 0x80
```
組譯與連結：nasm -f elf32 hello.asm && ld -m elf_i386 hello.o -o hello
注意 NASM 與 MASM 的差異：NASM 的 mov ecx, msg 取得位址；要讀取內容必須寫 mov ecx, [msg]。

## NASM Linux 64 位元 Hello World
id: x86-nasm-linux64
category: 範例
arch: x86
tags: NASM, Linux, syscall, 64位元, x64, sys_write, Hello World, RAX, RDI, RSI, RDX
---
```
section .data
    msg db 'Hello, x64!', 10
    len equ $ - msg

section .text
    global _start
_start:
    mov rax, 1          ; sys_write
    mov rdi, 1          ; stdout
    mov rsi, msg
    mov rdx, len
    syscall
    mov rax, 60         ; sys_exit
    xor rdi, rdi
    syscall
```
組譯：nasm -f elf64 hello.asm && ld hello.o -o hello
32 位元與 64 位元 Linux 的系統呼叫號碼不同（write 是 4 與 1，exit 是 1 與 60）。

## 在 Visual Studio 2019 組譯 MASM
id: x86-vs2019-masm
category: 做法
arch: x86
tags: Visual Studio, VS2019, MASM, 設定, Build Customizations, ml.exe, 專案, 組譯, Irvine
---
1. 建立「空專案」（C++ → 空專案）
2. 在專案上按右鍵 → 建置相依性 → 組建自訂 → 勾選 masm(.targets, .props)
3. 新增檔案，副檔名改成 .asm（檔案屬性的「項目類型」應為 Microsoft Macro Assembler）
4. 專案屬性 → 連結器 → 系統 → 子系統：主控台
5. 專案屬性 → 連結器 → 進階 → 進入點：main（若程式用 END main）
6. 使用 Irvine32：Microsoft Macro Assembler → General → Include Paths 加入 Irvine 資料夾；連結器輸入加入 irvine32.lib、kernel32.lib、user32.lib
7. 32 位元程式請把方案平台設為 x86；64 位元程式改用 x64 並使用 ml64 的語法（沒有 .model、INVOKE 受限）

除錯：設定中斷點後按 F5，偵錯 → 視窗 → 暫存器／記憶體／反組譯 可以看執行過程。

## C 與組合語言混合
id: x86-c-and-asm
category: 做法
arch: x86
tags: inline asm, __asm, C語言, 混合, extern, 內嵌組合語言, VS2019
---
1. VC++ 內嵌組合語言（只支援 32 位元 x86）：
```
int a = 5, b;
__asm {
    mov eax, a
    shl eax, 1
    mov b, eax
}
```
2. 獨立 .asm 檔案 + extern（x86 與 x64 都可）：
```
; add.asm
.model flat, c
.code
AddTwo PROC a:DWORD, b:DWORD
    mov eax, a
    add eax, b
    ret
AddTwo ENDP
END
```
C 端宣告：extern "C" int AddTwo(int a, int b);
x64 不支援 __asm，必須用獨立 .asm 檔或編譯器內建函式（intrinsics）。
