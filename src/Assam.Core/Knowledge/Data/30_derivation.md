# 推導方法

## 執行追蹤表（Trace Table）的做法
id: derive-trace-table
category: 推導
arch: 通用
tags: 追蹤, trace, 追蹤表, 推導, 手算, 執行結果, 暫存器變化, 考試
---
步驟：
1. 列出會變動的暫存器、旗標、記憶體當作欄位
2. 每執行一行就填一列，只寫「改變的值」也可以
3. 數值用十六進位，並注意暫存器大小
4. 條件跳躍前，先寫下目前的旗標再判斷

範例：
```
        mov  eax, 5
        mov  ecx, 3
L1:     add  eax, ecx
        loop L1
```
追蹤：
```
行          EAX   ECX   說明
mov eax,5    5     -
mov ecx,3    5     3
add eax,ecx  8     3
loop L1      8     2    ECX≠0 → 跳回 L1
add eax,ecx  10    2
loop L1      10    1    跳回
add eax,ecx  11    1
loop L1      11    0    ECX=0 → 結束
```
結果 EAX = 11（5 + 3 + 2 + 1）。可以用「推導模擬」頁自動產生並核對。

## 二進位轉十進位（位權展開）
id: derive-bin-to-dec
category: 推導
arch: 通用
tags: 二進位轉十進位, 位權展開, binary to decimal, 推導, 進位轉換
---
每一位乘上 2 的次方再相加（從右邊第 0 位開始）：
```
1011 0110b
= 1×2^7 + 0×2^6 + 1×2^5 + 1×2^4 + 0×2^3 + 1×2^2 + 1×2^1 + 0×2^0
= 128 + 32 + 16 + 4 + 2
= 182
```
十六進位同理，乘上 16 的次方：
```
2F4h = 2×16^2 + 15×16^1 + 4×16^0 = 512 + 240 + 4 = 756
```
常用 2 的次方：2^4=16、2^8=256、2^10=1024、2^16=65536、2^32=4294967296。

## 十進位轉二進位（除 2 取餘）
id: derive-dec-to-bin
category: 推導
arch: 通用
tags: 十進位轉二進位, 除2取餘, decimal to binary, 推導, 進位轉換, 十六進位
---
一直除以 2，記下餘數，最後「由下往上」讀：
```
45 ÷ 2 = 22 … 1
22 ÷ 2 = 11 … 0
11 ÷ 2 =  5 … 1
 5 ÷ 2 =  2 … 1
 2 ÷ 2 =  1 … 0
 1 ÷ 2 =  0 … 1
→ 101101b
```
轉十六進位就除以 16：
```
756 ÷ 16 = 47 … 4
 47 ÷ 16 =  2 … 15 (F)
  2 ÷ 16 =  0 … 2
→ 2F4h
```
快速法：先轉十六進位，再把每個十六進位數字換成 4 個位元。

## 負數的二補數推導
id: derive-twos-complement
category: 推導
arch: 通用
tags: 二補數, 負數, 推導, 反相加一, two's complement, 有號數
---
求 8 位元 -45：
```
45      = 0010 1101
反相    = 1101 0010
加 1    = 1101 0011 = D3h
驗算：D3h = 211，211 - 256 = -45 ✓
```
反過來，已知 8 位元 B5h 是多少（有號）：
```
B5h = 1011 0101，最高位元 1 → 負數
反相 0100 1010，加 1 → 0100 1011 = 75
所以 B5h = -75（或 181 - 256 = -75）
```
快速法：從右邊找到第一個 1，它和右邊的位元不變，左邊全部反相。

## 加法旗標推導
id: derive-add-flags
category: 推導
arch: x86
tags: 旗標推導, ADD, CF, OF, ZF, SF, PF, AF, 加法, 推導
---
以 8 位元 ADD AL, BL（AL = 9Ch、BL = 85h）為例：
```
  進位  0011 1000 (第 7 位元還有進位 → CF)
        1001 1100   9Ch = 156（有號 -100）
      + 1000 0101   85h = 133（有號 -123）
      -----------
     (1)0010 0001   21h
```
・CF = 1：無號 156 + 133 = 289 > 255
・OF = 1：有號 -100 + -123 = -223 < -128（負 + 負 = 正，符號錯了）
・ZF = 0：結果 21h 不是 0
・SF = 0：結果最高位元是 0
・PF = 1：21h = 0010 0001 有 2 個 1（偶數）
・AF = 1：低 4 位元 C + 5 = 17 > 15，第 3 位元有進位到第 4 位元

「數值推導」頁輸入兩個數字與位元數，就能得到這樣的推導。

## 減法（CMP）旗標推導
id: derive-sub-flags
category: 推導
arch: x86
tags: 旗標推導, SUB, CMP, 借位, CF, OF, 減法, 推導, 條件跳躍
---
CMP a, b 和 SUB 一樣計算 a - b：
・CF = 1：無號 a < b（需要借位）
・ZF = 1：a == b
・SF：結果的最高位元
・OF = 1：有號結果超出範圍（異號相減且結果符號與 a 不同）

例：8 位元 CMP AL, BL，AL = 05h、BL = 0Ah
```
05h - 0Ah = FBh（-5）
CF=1（5 < 10 需要借位）、ZF=0、SF=1、OF=0
```
接著的跳躍：JB 會跳（無號小於）、JL 會跳（SF≠OF → 1≠0）、JA/JG 不跳。

例：AL = 80h（-128）、BL = 01h
```
80h - 01h = 7Fh
CF=0（128 ≥ 1）、SF=0、OF=1（-128 - 1 = -129 溢位）
JL：SF≠OF → 0≠1 → 會跳（-128 < 1 正確！）
```
這就是為什麼有號比較要看 SF≠OF，而不是只看 SF。

## 迴圈執行次數推導
id: derive-loop-count
category: 推導
arch: 通用
tags: 迴圈次數, 推導, LOOP, DJNZ, 計數, 執行次數, 邊界
---
LOOP／DJNZ：計數器初值為 n（n ≠ 0）時，迴圈主體執行 n 次。
計數器初值為 0：x86 LOOP 執行 2^32 次（16 位元為 65536 次）；8051 DJNZ 執行 256 次。

CMP + 條件跳躍的迴圈：把迴圈變數的初值、每次的變化量、結束條件列出來：
```
    mov  ecx, 2
L1: ...
    add  ecx, 3
    cmp  ecx, 20
    jl   L1
```
ECX：2 → 5 → 8 → 11 → 14 → 17 → 20（不小於 20 停止），主體執行 6 次。
公式：次數 = ⌈(終值 - 初值) ÷ 步長⌉ = ⌈18 ÷ 3⌉ = 6。

巢狀迴圈的總次數是相乘：外圈 10 次、內圈 20 次 → 內圈主體 200 次。

## 乘除法結果推導
id: derive-mul-div
category: 推導
arch: x86
tags: 乘法, 除法, 推導, EDX:EAX, MUL, DIV, 商, 餘數, 結果位置
---
MUL 範例（16 位元）：AX = 1234h、BX = 0100h、MUL BX
```
1234h × 0100h = 123400h（4660 × 256 = 1192960）
DX:AX = 0012:3400h → DX = 0012h、AX = 3400h
DX ≠ 0 → CF = OF = 1
```
DIV 範例（8 位元）：AX = 0107h（263）、BL = 10h（16）、DIV BL
```
263 ÷ 16 = 16 … 7
AL = 10h（商）、AH = 07h（餘數）
```
DIV 溢位範例：AX = 1000h（4096）、BL = 02h、DIV BL → 商 2048 > 255，放不進 AL → 除法錯誤。

## 移位結果推導
id: derive-shift
category: 推導
arch: x86
tags: 移位, 推導, SHL, SHR, SAR, ROL, ROR, CF, 二進位
---
AL = 1011 0010b（B2h），各指令移 1 位元的結果：
```
SHL AL,1  → 0110 0100 (64h)  CF=1（移出的最高位元）
SHR AL,1  → 0101 1001 (59h)  CF=0（移出的最低位元）
SAR AL,1  → 1101 1001 (D9h)  CF=0（左邊補符號位元 1）
ROL AL,1  → 0110 0101 (65h)  CF=1（最高位元繞到最低位元）
ROR AL,1  → 0101 1001 (59h)  CF=0（最低位元 0 繞到最高位元）
RCL AL,1（原 CF=1）→ 0110 0101 (65h)  CF=1
```
有號觀點：B2h = -78，SAR 1 得 D9h = -39（除以 2）。

## 堆疊內容推導
id: derive-stack
category: 推導
arch: x86
tags: 堆疊, 推導, PUSH, POP, ESP, 堆疊圖, CALL, RET
---
畫堆疊圖時記得：x86 堆疊往低位址長，PUSH 先減 ESP 再存。

例：ESP = 1000h，執行
```
push 10h     ; ESP=0FFCh，[0FFCh]=10h
push 20h     ; ESP=0FF8h，[0FF8h]=20h
call F       ; ESP=0FF4h，[0FF4h]=返回位址
F: pop eax   ; EAX=返回位址！（不是 20h），ESP=0FF8h
```
這是常見陷阱：副程式中第一個 POP 取到的是返回位址。要讀參數請用 [ESP+4]／[EBP+8]。

## 符號延伸與零延伸推導
id: derive-extension
category: 推導
arch: x86
tags: 符號延伸, 零延伸, MOVSX, MOVZX, CBW, CWD, CDQ, 推導
---
把 8 位元 F0h 放大到 16 位元：
```
零延伸（MOVZX）：00F0h = 240     前面補 0
符號延伸（MOVSX）：FFF0h = -16    前面補最高位元（1）
```
x86 的符號延伸指令：
・CBW：AL → AX
・CWD：AX → DX:AX
・CWDE：AX → EAX
・CDQ：EAX → EDX:EAX
有號除法前一定要做符號延伸，無號除法前則把高位清 0。

## 小端序記憶體內容推導
id: derive-little-endian
category: 推導
arch: x86
tags: 小端序, 記憶體內容, 推導, DWORD, 位元組順序, PTR
---
```
.data
val DWORD 12345678h
w   WORD  0ABCDh
```
假設 val 在 0404000h，記憶體內容：
```
0404000: 78 56 34 12 CD AB
```
讀取：
・MOV AL, BYTE PTR val → 78h
・MOV AX, WORD PTR val → 5678h
・MOV AX, WORD PTR val+1 → 3456h
・MOV EAX, DWORD PTR val+2 → ABCD1234h（跨到 w 的位元組）
