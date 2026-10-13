# 基礎觀念（通用）
# 格式說明：以「## 標題」開始一張知識卡，接著是 id/category/arch/tags，再用 --- 分隔內文。

## 什麼是組合語言
id: basic-what-is-asm
category: 概念
arch: 通用
tags: 組合語言, assembly, 機器語言, 助憶碼, 低階語言, 入門
---
組合語言（Assembly Language）是用「助憶碼」表示機器指令的低階程式語言，一行通常對應一個 CPU 指令。

・機器語言：CPU 真正執行的二進位碼，例如 x86 的 B8 05 00 00 00
・組合語言：同一個指令寫成 MOV EAX, 5，人比較看得懂
・組譯器（Assembler）：把組合語言翻譯成機器碼，例如 MASM、NASM、GAS

特色：
1. 和 CPU 架構綁在一起：x86、ARM、MIPS、8051、AVR 的指令完全不同
2. 直接操作暫存器、記憶體位址與旗標
3. 執行效率高、程式小，常用於驅動程式、嵌入式系統、開機程式、效能關鍵程式與逆向工程
4. 缺點是撰寫費時、不易移植

學習順序建議：數字系統 → 暫存器 → 定址模式 → MOV/ADD/SUB → 旗標與條件跳躍 → 迴圈 → 堆疊與副程式 → 輸入輸出。

## 組譯、連結與開發流程
id: basic-build-flow
category: 概念
arch: 通用
tags: 組譯器, 連結器, assembler, linker, masm, nasm, gas, obj, exe, 流程
---
原始碼 (.asm) → 組譯器 → 目的檔 (.obj/.o) → 連結器 → 執行檔 (.exe/ELF)

常見組譯器：
・MASM（ml.exe／ml64.exe）：微軟，Visual Studio 內建，Intel 語法，台灣課程（Irvine 教科書）最常用
・NASM：跨平台，Intel 語法，Linux 常用
・GAS（as）：GNU 組譯器，預設 AT&T 語法，GCC 產生的組合語言就是這種
・TASM：Borland 舊組譯器，常見於 DOS 時代教材
・8051：Keil A51、ASEM-51；ARM：Keil armasm 或 arm-none-eabi-as；AVR：AVR Assembler、avr-as

範例（NASM，Linux）：
```
nasm -f elf32 hello.asm -o hello.o
ld -m elf_i386 hello.o -o hello
./hello
```
範例（MASM 命令列）：
```
ml /c /coff hello.asm
link /subsystem:console hello.obj
```

## 機器碼、助憶碼與運算元
id: basic-instruction-format
category: 概念
arch: 通用
tags: 運算元, operand, 助憶碼, mnemonic, 指令格式, 標籤, 註解
---
一行組合語言的典型格式：

```
標籤:   助憶碼   運算元1, 運算元2    ; 註解
L1:     ADD      EAX, EBX            ; EAX = EAX + EBX
```

・標籤（Label）：替這個位置取名字，給跳躍或呼叫使用
・助憶碼（Mnemonic）：指令名稱，例如 MOV、ADD、JMP
・運算元（Operand）：指令要處理的資料，可以是暫存器、立即值（常數）或記憶體
・註解：x86 MASM/NASM、8051、AVR 用分號 ;；GAS/MIPS/RISC-V 用 #；ARM GAS 用 @ 或 //

運算元順序：
・Intel 語法（MASM、NASM）：目的在前，MOV 目的, 來源
・AT&T 語法（GAS）：來源在前，movl 來源, 目的
・ARM/MIPS/RISC-V：通常目的在前（但 STR/SW 等儲存指令是「來源暫存器, 位址」）

## 數字系統：二、八、十、十六進位
id: basic-number-systems
category: 概念
arch: 通用
tags: 二進位, 十六進位, 十進位, 八進位, binary, hex, 進位轉換, 位權
---
組合語言常用的寫法：
・十進位：100、-5
・十六進位：0x64（C/NASM/GAS）、64h（MASM/8051，字母開頭要補 0，例如 0FFh）、$64（AVR/6502）
・二進位：01100100b（MASM/8051）、0b01100100（GAS/NASM）
・八進位：144o 或 144q

轉換方法：
1. 其他進位 → 十進位：位權展開。例如 2Fh = 2×16 + 15 = 47
2. 十進位 → 二進位：除 2 取餘數，由下往上讀
3. 二進位 ↔ 十六進位：每 4 個位元一組（0010 1111 = 2F）
4. 二進位 ↔ 八進位：每 3 個位元一組

對照表（必背）：
```
0000=0  0001=1  0010=2  0011=3  0100=4  0101=5  0110=6  0111=7
1000=8  1001=9  1010=A  1011=B  1100=C  1101=D  1110=E  1111=F
```
在「數值推導」頁輸入數字可以看到完整的推導步驟。

## 二補數與有號數範圍
id: basic-twos-complement
category: 概念
arch: 通用
tags: 二補數, 補數, 負數, 有號, 無號, signed, unsigned, two's complement, 範圍
---
電腦用二補數表示負數。求 -X 的方法：X 的二進位「反相後加 1」。

例：8 位元的 -5
```
 5 = 0000 0101
反相 1111 1010
加1  1111 1011 = FBh   → -5 存成 FBh（無號看是 251）
```

判斷有號數正負：看最高位元（MSB），1 為負、0 為正。

範圍：
・8 位元：無號 0～255；有號 -128～127
・16 位元：無號 0～65535；有號 -32768～32767
・32 位元：無號 0～4294967295；有號 -2147483648～2147483647

同一組位元可以當無號也可以當有號，CPU 不知道你的意思：
・加減法指令對兩者都一樣，差別在你看 CF（無號）還是 OF（有號）
・比較後的跳躍要選對：無號用 JA/JB，有號用 JG/JL
・乘除法要選對：無號用 MUL/DIV，有號用 IMUL/IDIV

## 位元、位元組與資料大小
id: basic-data-sizes
category: 概念
arch: 通用
tags: 位元, 位元組, bit, byte, word, dword, qword, nibble, 資料大小, db, dw, dd
---
・位元 bit：0 或 1
・半位元組 nibble：4 位元（一個十六進位數字）
・位元組 byte：8 位元
・字組 word：x86 為 16 位元（ARM/MIPS 的 word 是 32 位元！）
・雙字組 dword：32 位元
・四字組 qword：64 位元

x86 MASM 的資料定義：
```
b1  BYTE   10h        ; 或 DB，8 位元
w1  WORD   1234h      ; 或 DW，16 位元
d1  DWORD  12345678h  ; 或 DD，32 位元
q1  QWORD  1          ; 或 DQ，64 位元
arr DWORD  1, 2, 3, 4 ; 陣列
buf BYTE   100 DUP(0) ; 100 個 0
str BYTE   "Hi", 0    ; 以 0 結尾的字串
```
NASM 用 db/dw/dd/dq 定義、resb/resw/resd 保留未初始化空間；GAS 用 .byte/.word/.long/.quad/.asciz。

## 位元組序：小端序與大端序
id: basic-endianness
category: 概念
arch: 通用
tags: 小端, 大端, endian, little endian, big endian, 位元組序, 記憶體排列
---
多位元組資料存進記憶體時的排列方式：
・小端序（Little Endian）：低位元組放在低位址。x86、大多數 ARM、RISC-V、AVR
・大端序（Big Endian）：高位元組放在低位址。網路傳輸、舊 Motorola、部分 MIPS

例：DWORD 12345678h 存在位址 1000h
```
位址    1000 1001 1002 1003
小端序   78   56   34   12
大端序   12   34   56   78
```
所以用除錯器看 x86 記憶體時數值看起來是「反過來」的。
8051 的 16 位元資料（例如 DPTR、LJMP 位址）在程式碼中是高位元組在前。

## ASCII 字元碼
id: basic-ascii
category: 概念
arch: 通用
tags: ascii, 字元, 字串, 換行, CR, LF, 大小寫, 數字字元
---
常用 ASCII 碼（十六進位）：
・'0'～'9' = 30h～39h → 數字字元減 30h（或減 '0'）就得到數值
・'A'～'Z' = 41h～5Ah；'a'～'z' = 61h～7Ah → 大小寫相差 20h（第 5 位元）
・空白 = 20h；CR（歸位）= 0Dh；LF（換行）= 0Ah；'$' = 24h；NUL = 0

技巧：
・數字轉字元：ADD AL, '0'
・字元轉數字：SUB AL, '0'
・轉大寫：AND AL, 0DFh（清除第 5 位元）
・轉小寫：OR AL, 20h
・大小寫互換：XOR AL, 20h
・DOS 換行：0Dh, 0Ah；Linux 換行：0Ah

## 程式的記憶體區段
id: basic-memory-sections
category: 概念
arch: 通用
tags: 區段, segment, section, text, data, bss, stack, heap, 堆疊, 記憶體配置
---
一個程式在記憶體中通常分成：
・程式碼區（.code／.text）：指令
・資料區（.data）：有初始值的全域變數
・未初始化資料區（.data?／.bss）：沒有初始值的變數
・堆疊（stack）：副程式呼叫的返回位址、參數、區域變數，x86 往低位址成長
・堆積（heap）：動態配置（malloc／new）

MASM 用 .data／.code，NASM 用 section .data／section .text，GAS 用 .data／.text。
8086 真實模式用區段暫存器 CS、DS、SS、ES 指出各區段位置；32/64 位元保護模式通常是平坦（flat）記憶體模型。

## 如何閱讀與手算一段組合語言
id: basic-how-to-read
category: 做法
arch: 通用
tags: 閱讀, 追蹤, trace, 手算, 解題, 考試, 推導, 步驟
---
考試或作業常要你「寫出執行後暫存器的值」，建議步驟：

1. 先確認架構與語法（x86 Intel？AT&T？8051？），運算元順序不同！
2. 列一張追蹤表：每一行指令一列，欄位是會變動的暫存器、旗標、記憶體
3. 一行一行執行，數值一律寫成十六進位，並注意位元數（AL 只有 8 位元）
4. 算術指令後順便推導旗標（CF、ZF、SF、OF）
5. 遇到條件跳躍，用剛才的旗標判斷要不要跳
6. 迴圈先算出執行次數，再看每一圈的變化是否有規律
7. 遇到 PUSH/POP、CALL/RET，另外畫出堆疊圖

本程式的「推導模擬」頁可以自動產生這張追蹤表（x86），並附上每一步的推導過程，可以拿來對答案。
