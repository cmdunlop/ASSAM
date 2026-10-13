# ARM 組合語言

## ARM 暫存器與 CPSR
id: arm-registers
category: 概念
arch: ARM
tags: ARM, 暫存器, R0, R12, SP, LR, PC, CPSR, R13, R14, R15, N Z C V
---
ARM（32 位元）有 16 個暫存器：
```
R0～R3    傳遞參數與回傳值（AAPCS），呼叫後可能被改變
R4～R11   變數暫存器，被呼叫者必須保存（R11 常當 FP）
R12 (IP)  程序內暫用
R13 (SP)  堆疊指標
R14 (LR)  連結暫存器：BL 呼叫時存放返回位址
R15 (PC)  程式計數器
CPSR      狀態暫存器：N（負）、Z（零）、C（進位）、V（溢位）旗標與處理器模式
```
ARM 是載入／儲存架構（Load/Store）：算術指令只能操作暫存器，記憶體要先 LDR 進暫存器、算完再 STR 回去。

## ARM 條件執行與條件碼
id: arm-conditions
category: 概念
arch: ARM
tags: ARM, 條件碼, 條件執行, EQ, NE, GT, LT, GE, LE, HI, LO, CS, CC, S字尾, BEQ, BNE
---
ARM 指令後面可以加條件碼，條件成立才執行：
```
EQ 相等 (Z=1)          NE 不相等 (Z=0)
GT 有號大於            LT 有號小於        GE 有號大於等於    LE 有號小於等於
HI 無號大於            LO/CC 無號小於     HS/CS 無號大於等於 LS 無號小於等於
MI 負數 (N=1)          PL 非負 (N=0)      VS 溢位 (V=1)      VC 無溢位
AL 永遠（預設）
```
例：不用分支求絕對值
```
CMP   R0, #0
RSBLT R0, R0, #0     @ 若 R0 < 0，R0 = 0 - R0
```
資料處理指令預設「不」更新旗標，要加 S：ADDS、SUBS、MOVS。CMP/CMN/TST/TEQ 一定會更新。
注意 ARM 減法後的 C 旗標代表「沒有借位」，和 x86 的 CF 相反。
Thumb-2 中條件執行要搭配 IT 指令（例如 IT LT）。

## ARM 載入與儲存（LDR／STR）
id: arm-ldr-str
category: 概念
arch: ARM
tags: ARM, LDR, STR, 載入, 儲存, 前索引, 後索引, 回寫, 定址模式, LDRB, =常數
---
```
LDR R0, [R1]           @ R0 = 記憶體[R1]
LDR R0, [R1, #4]       @ R0 = 記憶體[R1 + 4]（R1 不變）
LDR R0, [R1, #4]!      @ 前索引回寫：R1 = R1 + 4，再讀取
LDR R0, [R1], #4       @ 後索引：先讀 [R1]，再 R1 = R1 + 4（走訪陣列常用）
LDR R0, [R1, R2, LSL #2] @ R0 = 記憶體[R1 + R2×4]（陣列第 R2 個元素）
STR R0, [R1]           @ 記憶體[R1] = R0（注意方向：來源在前）
LDRB / STRB            @ 一個位元組；LDRH / STRH 為半字組（16 位元）
LDR R0, =0x12345678    @ 虛擬指令：載入任意 32 位元常數或標籤位址
```
MOV 的立即值有限制（8 位元旋轉偶數位），不能表示的常數改用 LDR Rn, =常數 或 MOVW/MOVT。

## ARM 桶型移位器（Operand2）
id: arm-barrel-shifter
category: 概念
arch: ARM
tags: ARM, 桶型移位器, barrel shifter, Operand2, LSL, LSR, ASR, ROR, 乘以常數
---
ARM 的第二個運算元可以先移位再參與運算，不需要額外指令：
```
ADD R0, R1, R1, LSL #2    @ R0 = R1 + R1×4 = R1 × 5
MOV R0, R1, LSL #3        @ R0 = R1 × 8（等同 LSL R0, R1, #3）
SUB R0, R0, R0, ASR #1    @ R0 = R0 - R0/2
```
移位種類：LSL（邏輯左移）、LSR（邏輯右移）、ASR（算術右移）、ROR（循環右移）、RRX（帶進位右移 1 位）。

## ARM 函式呼叫 BL／BX LR 與 AAPCS
id: arm-function-call
category: 概念
arch: ARM
tags: ARM, 函式呼叫, BL, BX LR, PUSH, POP, AAPCS, 呼叫慣例, 返回, 參數
---
```
    MOV  R0, #3          @ 參數 1
    MOV  R1, #4          @ 參數 2
    BL   add_two         @ LR = 返回位址，跳到 add_two
    @ R0 = 7

add_two:
    ADD  R0, R0, R1
    BX   LR              @ 返回
```
會呼叫其他函式（巢狀呼叫）時，LR 會被覆蓋，所以要先保存：
```
func:
    PUSH {R4, LR}        @ 保存被呼叫者保存暫存器與 LR
    ...
    BL   other
    ...
    POP  {R4, PC}        @ 把 LR 直接 POP 到 PC 就返回了
```
AAPCS 規定：R0～R3 傳參數（超過 4 個放堆疊），R0（R1）回傳；R4～R11 由被呼叫者保存；SP 保持 8 位元組對齊。

## ARM 迴圈與陣列加總範例
id: arm-loop-example
category: 範例
arch: ARM
tags: ARM, 迴圈, 陣列加總, SUBS, BNE, LDR, 範例
---
```
@ R0 = 陣列位址，R1 = 元素個數，回傳 R0 = 總和
sum:
    MOV   R2, #0
loop:
    LDR   R3, [R0], #4     @ 讀取並前進
    ADD   R2, R2, R3
    SUBS  R1, R1, #1       @ 減 1 並更新旗標
    BNE   loop             @ 不為 0 就繼續
    MOV   R0, R2
    BX    LR
```
技巧：用 SUBS 同時減 1 與設定 Z 旗標，就不需要另外 CMP（倒數迴圈比較省指令）。

## ARM64（AArch64）概觀
id: arm64-overview
category: 概念
arch: ARM64
tags: ARM64, AArch64, X0, W0, XZR, SP, X29, X30, 呼叫慣例, 64位元, Apple Silicon, 樹莓派
---
・31 個 64 位元通用暫存器 X0～X30，W0～W30 是低 32 位元
・XZR／WZR：零暫存器；SP：堆疊指標（16 位元組對齊）
・X0～X7：參數與回傳值；X8：間接結果／Linux 系統呼叫號碼；X19～X28：被呼叫者保存
・X29 = FP（框架指標）、X30 = LR（返回位址）
・沒有條件執行，改用 B.cond、CSEL、CSET；沒有 PUSH/POP，改用 STP/LDP

函式開頭與結尾：
```
func:
    STP  X29, X30, [SP, #-16]!   // 保存 FP 與 LR
    MOV  X29, SP
    ...
    LDP  X29, X30, [SP], #16
    RET
```
Linux 系統呼叫：MOV X8, #64（write）、X0=1、X1=字串、X2=長度，SVC #0；MOV X8, #93（exit）。

## Thumb 模式
id: arm-thumb
category: 概念
arch: ARM
tags: ARM, Thumb, Thumb-2, 16位元指令, Cortex-M, IT, .thumb
---
Thumb 是 ARM 的精簡指令編碼（多數指令 16 位元），程式較小。Thumb-2 混合 16/32 位元指令，Cortex-M（STM32 等微控制器）只支援 Thumb。
・GAS 用 .thumb 或 .syntax unified 指定
・BX/BLX 跳到位址最低位元為 1 時切換到 Thumb 狀態
・Thumb-2 的條件執行需要 IT 區塊：
```
CMP   R0, #10
ITE   GT
MOVGT R1, #1
MOVLE R1, #0
```
