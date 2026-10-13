# x86 暫存器與旗標

## x86 通用暫存器
id: x86-registers
category: 概念
arch: x86
tags: 暫存器, register, EAX, EBX, ECX, EDX, ESI, EDI, ESP, EBP, 通用暫存器, 8086
---
32 位元 x86 有 8 個通用暫存器（16 位元時去掉 E，64 位元時改成 R）：

```
EAX  累加器：算術運算、乘除法、函式回傳值
EBX  基底：常存放位址
ECX  計數器：LOOP、REP 的次數，CL 為移位次數
EDX  資料：乘除法的高位部分、I/O 埠號
ESI  來源索引：字串指令的來源位址
EDI  目的索引：字串指令的目的位址
ESP  堆疊指標：永遠指向堆疊頂端（不要隨便拿來當一般暫存器）
EBP  基底指標：存取參數與區域變數（堆疊框架）
```
另外還有：
・EIP：指令指標（下一個要執行的指令位址，不能直接 MOV）
・EFLAGS：旗標暫存器
・區段暫存器：CS、DS、SS、ES、FS、GS

x64 再增加 R8～R15，並把所有暫存器擴充成 64 位元（RAX、RBX…）。

## x86 暫存器的分割（AX、AH、AL）
id: x86-register-parts
category: 概念
arch: x86
tags: AX, AH, AL, EAX, RAX, 8位元, 16位元, 32位元, 64位元, 暫存器分割
---
同一個暫存器可以用不同大小存取：

```
RAX (64)  ┌───────────────────────────────┐
EAX (32)                  ┌───────────────┤
AX  (16)                          ┌───────┤
AH/AL (8)                         │AH │AL │
```
・EAX = 12345678h 時：AX = 5678h，AH = 56h，AL = 78h
・只有 EAX、EBX、ECX、EDX 有 H/L 兩個 8 位元部分；ESI、EDI 等在 x64 才有 SIL、DIL
・修改 AL 不會影響 AH 以及 EAX 的其他位元
・x64 特別規則：寫入 32 位元暫存器（例如 MOV EAX, 1）會把 RAX 的高 32 位元清為 0；寫入 8/16 位元則不會

## 區段暫存器與 8086 記憶體分段
id: x86-segment-registers
category: 概念
arch: x86
tags: 區段, segment, CS, DS, SS, ES, 段位址, 偏移, 8086, 真實模式, @data
---
8086 只有 16 位元暫存器，但要定址 1MB（20 位元），所以用「段:偏移」：

實體位址 = 段位址 × 16 + 偏移位址

・CS：程式碼段（配合 IP 取指令）
・DS：資料段（預設的資料存取）
・SS：堆疊段（配合 SP、BP）
・ES：額外段（字串指令的目的 ES:DI）

DOS 程式開頭必須設定 DS：
```
mov ax, @data
mov ds, ax        ; 不能直接 mov ds, @data（立即值不能放進區段暫存器）
```
32/64 位元 Windows/Linux 使用平坦模式，一般程式不需要管區段暫存器。

## 旗標暫存器 FLAGS／EFLAGS
id: x86-flags
category: 概念
arch: x86
tags: 旗標, flag, eflags, CF, ZF, SF, OF, PF, AF, DF, IF, TF, 狀態
---
算術與邏輯指令執行後會更新狀態旗標：

```
CF 進位旗標  無號運算超出範圍（加法進位、減法借位）
PF 同位旗標  結果低 8 位元有偶數個 1 時為 1
AF 輔助進位  第 3 位元進位到第 4 位元（BCD 用）
ZF 零旗標    結果為 0 時為 1
SF 符號旗標  結果最高位元（=1 表示負數）
OF 溢位旗標  有號運算超出範圍
```
控制旗標：
・DF 方向旗標：字串指令往高位址（CLD，DF=0）或低位址（STD，DF=1）
・IF 中斷旗標：是否允許可遮罩中斷（STI/CLI）
・TF 陷阱旗標：單步執行（除錯用）

哪些指令會影響旗標：
・ADD/SUB/CMP/NEG：全部狀態旗標
・INC/DEC：不影響 CF
・AND/OR/XOR/TEST：CF=OF=0
・MOV/LEA/PUSH/POP/XCHG/NOT：不影響旗標

## CF 與 OF 的差別
id: x86-cf-vs-of
category: 推導
arch: x86
tags: CF, OF, 進位, 溢位, carry, overflow, 有號, 無號, 差別
---
・CF（進位）：把運算元當「無號數」時，結果有沒有超出範圍
・OF（溢位）：把運算元當「有號數」時，結果有沒有超出範圍

8 位元例子：
```
7Fh + 01h = 80h   無號 127+1=128（沒超過 255）CF=0；有號 127+1=128 > 127 → OF=1
FFh + 01h = 00h   無號 255+1=256 > 255 → CF=1；有號 -1+1=0 正常 → OF=0
80h - 01h = 7Fh   無號 128-1=127 CF=0；有號 -128-1=-129 < -128 → OF=1
00h - 01h = FFh   無號 0-1 需要借位 → CF=1；有號 0-1=-1 正常 → OF=0
```
OF 的快速判斷（加法）：兩個同號數相加，結果符號卻改變 → OF=1；異號數相加永遠不會溢位。
減法：異號數相減，且結果符號和被減數不同 → OF=1。

## 指令指標 IP／EIP／RIP
id: x86-instruction-pointer
category: 概念
arch: x86
tags: IP, EIP, RIP, 指令指標, program counter, 程式計數器
---
IP（16 位元）、EIP（32 位元）、RIP（64 位元）存放「下一個要執行的指令位址」。
・不能用 MOV 直接修改，只能透過 JMP、Jcc、CALL、RET、INT、IRET 改變
・CALL 會把下一行的位址推入堆疊，RET 再把它彈回 EIP
・x64 支援 RIP 相對定址：MOV RAX, [RIP + 偏移]，NASM 可用 default rel
