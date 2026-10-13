# x86 定址模式

## x86 定址模式總覽
id: x86-addressing-modes
category: 概念
arch: x86
tags: 定址模式, addressing, 立即定址, 暫存器定址, 直接定址, 間接定址, 基底, 索引, 比例, 位移
---
運算元取得資料的方式：

```
立即定址          MOV EAX, 5              常數寫在指令中
暫存器定址        MOV EAX, EBX            資料在暫存器
直接定址          MOV EAX, [var] / var    位址是固定的變數位址
暫存器間接定址    MOV EAX, [EBX]          位址存在暫存器中
基底相對定址      MOV EAX, [EBP+8]        暫存器 + 位移（參數、區域變數）
索引定址          MOV EAX, arr[ESI]       變數位址 + 索引暫存器
基底+索引         MOV EAX, [EBX+ESI]      兩個暫存器相加
比例索引          MOV EAX, [EBX+ESI*4+8]  基底 + 索引×比例(1,2,4,8) + 位移
```
有效位址（EA）的一般式：EA = 基底 + 索引 × 比例 + 位移

存取陣列第 i 個 DWORD 元素：[arr + ESI*4]（ESI = i），或每次 ADD ESI, 4。
16 位元（8086）只能用 BX、BP、SI、DI 當位址暫存器，組合限定為 [BX/BP + SI/DI + 位移]，沒有比例。

## 有效位址與 LEA
id: x86-lea
category: 概念
arch: x86
tags: LEA, 有效位址, effective address, OFFSET, 位址, 指標, 快速乘法
---
LEA（Load Effective Address）只計算「位址」，不讀取記憶體。

```
lea  esi, [arr + 8]       ; ESI = arr 的位址 + 8
mov  esi, OFFSET arr      ; 效果類似：取得 arr 的位址（只能用常數位址）
mov  eax, [arr + 8]       ; 這個才是讀取記憶體內容
```
LEA 的好處：可以計算含暫存器的位址，例如 LEA EAX, [EBX+ESI*4]。

LEA 也常被當成「不影響旗標的快速算術」：
```
lea eax, [eax + eax*4]    ; EAX = EAX × 5
lea eax, [ebx + 10]       ; EAX = EBX + 10
lea eax, [eax*8]          ; EAX = EAX × 8
```

## 運算元大小與 PTR
id: x86-ptr-size
category: 概念
arch: x86
tags: PTR, BYTE PTR, WORD PTR, DWORD PTR, 運算元大小, size, operand size mismatch
---
當組譯器無法從運算元判斷大小時，必須用 PTR 指定：

```
mov  [ebx], 5               ; 錯誤！不知道要寫 1、2 還是 4 個位元組
mov  BYTE PTR [ebx], 5      ; 寫 1 個位元組
mov  DWORD PTR [ebx], 5     ; 寫 4 個位元組（NASM 寫法：mov dword [ebx], 5）
inc  WORD PTR [esi]
```
有暫存器的一方會決定大小：MOV AL, [EBX] 就是讀 1 個位元組。

PTR 也可以「暫時改變」變數的型態：
```
val DWORD 12345678h
mov al, BYTE PTR val        ; AL = 78h（小端序，最低位址的位元組）
mov ax, WORD PTR val+2      ; AX = 1234h
```

## MASM 運算子 OFFSET、LENGTHOF、SIZEOF、TYPE
id: x86-masm-operators
category: 概念
arch: x86
tags: OFFSET, LENGTHOF, SIZEOF, TYPE, PTR, MASM, 運算子, 陣列長度
---
```
arr DWORD 10, 20, 30, 40

mov esi, OFFSET arr     ; arr 的位址
mov ecx, LENGTHOF arr   ; 元素個數 = 4
mov ebx, SIZEOF arr     ; 總位元組數 = 4 × 4 = 16
mov edx, TYPE arr       ; 每個元素大小 = 4
```
注意：LENGTHOF 只計算第一行宣告的元素；如果陣列寫成多行，後面幾行不算在內。
NASM 沒有這些運算子，常用 len equ $ - msg 計算長度（$ 代表目前位址）。

## 8086 實體位址計算
id: x86-physical-address
category: 推導
arch: x86
tags: 實體位址, physical address, 段位址, 偏移, segment, offset, 20位元, 8086, 推導
---
公式：實體位址 = 段位址 × 10h + 偏移位址

例：CS = 1234h、IP = 0010h
```
1234h × 10h = 12340h     （十六進位後面補 0）
12340h + 0010h = 12350h
```
例：DS = 2000h、BX = 0100h、MOV AL, [BX+5]
```
有效位址 EA = 0100h + 5 = 0105h
實體位址 = 20000h + 0105h = 20105h
```
不同的段:偏移可能指向同一個實體位址（例如 1235:0000 也是 12350h）。
「數值推導」頁可以輸入段與偏移，自動列出推導過程。
