# x86 副程式、堆疊框架與呼叫慣例

## 程序 PROC／ENDP 與參數傳遞
id: x86-proc
category: 概念
arch: x86
tags: PROC, ENDP, 副程式, 程序, 參數, 暫存器傳參數, 堆疊傳參數, USES
---
MASM 定義程序：
```
SumTwo PROC              ; 參數：EAX、EBX；回傳 EAX
    add eax, ebx
    ret
SumTwo ENDP
```
傳遞參數的三種方式：
1. 暫存器（最快，Irvine32 的程序都用這種）
2. 堆疊（C 語言、Win32 API 的方式）
3. 全域變數（不建議）

保存暫存器：程序會改到的暫存器，若呼叫者還要用，就在開頭 PUSH、結尾 POP；
MASM 可以寫 MyProc PROC USES esi ecx 自動產生 PUSH/POP。

## 堆疊框架（EBP、參數、區域變數）
id: x86-stack-frame
category: 概念
arch: x86
tags: 堆疊框架, stack frame, EBP, ESP, 參數, 區域變數, prologue, epilogue, ENTER, LEAVE
---
標準的函式開頭（prologue）與結尾（epilogue）：
```
MyFunc PROC
    push ebp            ; 保存呼叫者的 EBP
    mov  ebp, esp       ; 建立自己的框架
    sub  esp, 8         ; 保留 8 位元組區域變數
    ...
    mov  eax, [ebp+8]   ; 第 1 個參數
    mov  ebx, [ebp+12]  ; 第 2 個參數
    mov  [ebp-4], eax   ; 第 1 個區域變數
    ...
    mov  esp, ebp       ; 釋放區域變數（或用 LEAVE）
    pop  ebp
    ret
MyFunc ENDP
```
堆疊圖（32 位元，高位址在上）：
```
[ebp+12]  參數 2
[ebp+8]   參數 1
[ebp+4]   返回位址（CALL 推入）
[ebp]     舊的 EBP
[ebp-4]   區域變數 1
[ebp-8]   區域變數 2   ← ESP
```
參數由右到左推入，所以第 1 個參數離 EBP 最近（EBP+8）。

## 呼叫慣例 cdecl、stdcall、fastcall
id: x86-calling-conventions
category: 概念
arch: x86
tags: 呼叫慣例, calling convention, cdecl, stdcall, fastcall, 參數順序, 清除堆疊, 32位元
---
32 位元常見的呼叫慣例：
```
            參數順序      誰清除堆疊          回傳
cdecl       由右到左推入  呼叫者 ADD ESP, n   EAX（C 語言預設，可變參數）
stdcall     由右到左推入  被呼叫者 RET n      EAX（Win32 API）
fastcall    前兩個用 ECX、EDX，其餘推入堆疊，被呼叫者清除
```
cdecl 呼叫 printf 的例子：
```
push eax              ; 第 2 個參數
push OFFSET fmt       ; 第 1 個參數
call printf
add  esp, 8           ; 呼叫者清除 2 個參數
```
暫存器保存責任（cdecl/stdcall）：EAX、ECX、EDX 可被副程式破壞（呼叫者保存）；EBX、ESI、EDI、EBP 必須由副程式保存。

## x64 呼叫慣例（Windows 與 Linux）
id: x64-calling-conventions
category: 概念
arch: x86
tags: x64, 64位元, 呼叫慣例, RCX, RDX, R8, R9, RDI, RSI, shadow space, System V, Windows
---
Windows x64：
・前 4 個整數參數：RCX、RDX、R8、R9；其餘放堆疊
・呼叫前要保留 32 位元組的 shadow space（SUB RSP, 28h 常見，含對齊）
・CALL 之前 RSP 必須 16 位元組對齊
・回傳值 RAX；需保存 RBX、RBP、RDI、RSI、R12～R15

Linux（System V AMD64）：
・前 6 個整數參數：RDI、RSI、RDX、RCX、R8、R9
・沒有 shadow space；同樣要求 16 位元組對齊
・回傳值 RAX；需保存 RBX、RBP、R12～R15
・系統呼叫用 SYSCALL：號碼放 RAX，參數 RDI、RSI、RDX、R10、R8、R9

## 遞迴與堆疊推導
id: x86-recursion
category: 推導
arch: x86
tags: 遞迴, recursion, 階乘, factorial, 堆疊, 推導, 費氏數列
---
遞迴的關鍵是「每一層都要保存自己的資料」，通常放在堆疊：
```
; 輸入 EAX = n，輸出 EAX = n!
Factorial PROC
    cmp  eax, 1
    jbe  done          ; 終止條件：n <= 1
    push eax           ; 保存 n
    dec  eax
    call Factorial     ; EAX = (n-1)!
    pop  ebx           ; 取回 n
    mul  ebx           ; EAX = (n-1)! × n
    ret
done:
    mov  eax, 1
    ret
Factorial ENDP
```
n = 3 時的堆疊變化（每層推入「n」和「返回位址」）：
```
呼叫 F(3)：push 3 → call F(2)：push 2 → call F(1)：回傳 1
返回 F(2)：pop 2 → 1×2 = 2 → 返回 F(3)：pop 3 → 2×3 = 6
```
「推導模擬」頁載入「遞迴階乘」範例，可以看到每一步 ESP 和堆疊內容的變化。

## 巨集 MACRO
id: x86-macro
category: 概念
arch: x86
tags: 巨集, macro, MACRO, ENDM, 展開, 程序與巨集差別
---
巨集在組譯時「展開」成程式碼（像文字取代），程序則是執行時 CALL：
```
mPrintChar MACRO ch
    push eax
    mov  al, ch
    call WriteChar
    pop  eax
ENDM

    mPrintChar 'A'      ; 這一行會被展開成上面 4 行
```
巨集：不需要 CALL/RET，速度快，但每用一次程式就變大。
程序：只有一份程式碼，比較省空間，但有呼叫成本。
NASM 寫法是 %macro 名稱 參數個數 … %endmacro，參數用 %1、%2。
