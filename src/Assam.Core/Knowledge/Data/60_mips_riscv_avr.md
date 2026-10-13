# MIPS、RISC-V、AVR

## MIPS 暫存器與使用慣例
id: mips-registers
category: 概念
arch: MIPS
tags: MIPS, 暫存器, $zero, $t0, $s0, $a0, $v0, $ra, $sp, 使用慣例, 呼叫者保存
---
```
$0  $zero   恆為 0
$1  $at     組譯器保留
$2-$3   $v0-$v1  回傳值；syscall 服務號碼放 $v0
$4-$7   $a0-$a3  參數
$8-$15  $t0-$t7  暫存（呼叫者保存）
$16-$23 $s0-$s7  保存暫存器（被呼叫者保存）
$24-$25 $t8-$t9  暫存
$26-$27 $k0-$k1  核心保留
$28 $gp  全域指標    $29 $sp  堆疊指標
$30 $fp  框架指標    $31 $ra  返回位址
```
另外 HI、LO 存放乘除法結果（mfhi、mflo 取出）。MIPS 沒有旗標暫存器，比較結果用 slt 放進暫存器。

## MIPS 指令格式 R／I／J
id: mips-formats
category: 概念
arch: MIPS
tags: MIPS, 指令格式, R型, I型, J型, opcode, rs, rt, rd, shamt, funct, 機器碼
---
所有指令都是 32 位元：
```
R 型：op(6) rs(5) rt(5) rd(5) shamt(5) funct(6)   例：add $t0, $t1, $t2
I 型：op(6) rs(5) rt(5) immediate(16)             例：addi、lw、sw、beq
J 型：op(6) address(26)                           例：j、jal
```
編碼範例：add $t0, $t1, $t2（$t0=8、$t1=9、$t2=10）
```
op=0, rs=9, rt=10, rd=8, shamt=0, funct=32
000000 01001 01010 01000 00000 100000 = 0x012A4020
```
I 型立即值是 16 位元有號數（-32768～32767），更大的常數用 lui + ori（或虛擬指令 li）。

## MIPS 陣列存取 lw／sw
id: mips-load-store
category: 概念
arch: MIPS
tags: MIPS, lw, sw, 陣列, 位址計算, sll, 載入, 儲存, 對齊
---
MIPS 只能用「位移(基底)」定址：lw $t0, 8($s0) → 讀取 $s0 + 8。
存取 int 陣列 A[i]（$s0 = A 的位址、$s1 = i）：
```
sll  $t1, $s1, 2       # $t1 = i × 4
add  $t1, $t1, $s0     # $t1 = &A[i]
lw   $t0, 0($t1)       # $t0 = A[i]
```
字組存取的位址必須是 4 的倍數，否則會產生位址錯誤例外。
讀字串字元用 lbu（無號）比 lb 安全。

## MIPS 分支、跳躍與延遲槽
id: mips-branch
category: 概念
arch: MIPS
tags: MIPS, beq, bne, slt, 分支, 跳躍, 延遲槽, delay slot, blt, 虛擬指令
---
基本分支只有 beq、bne（以及 bgtz、blez、bltz、bgez 跟 0 比較）。其他比較用 slt：
```
# if ($t0 < $t1) goto L
slt  $t2, $t0, $t1
bne  $t2, $zero, L
```
MARS/SPIM 提供虛擬指令 blt、bgt、ble、bge，組譯時展開成 slt + bne/beq。

延遲槽：真實 MIPS 管線中，分支指令後面那一條指令「一定會執行」。MARS 預設關閉延遲槽模擬；若開啟，常在分支後放 nop。

## MIPS 函式呼叫 jal／jr $ra
id: mips-function
category: 概念
arch: MIPS
tags: MIPS, jal, jr, $ra, 函式, 堆疊, 呼叫, 遞迴, $sp
---
```
    li   $a0, 5
    jal  square          # $ra = 返回位址
    move $s0, $v0        # 回傳值

square:
    mul  $v0, $a0, $a0
    jr   $ra
```
巢狀或遞迴呼叫要先把 $ra（以及要保留的 $s 暫存器）存到堆疊：
```
func:
    addi $sp, $sp, -8
    sw   $ra, 4($sp)
    sw   $s0, 0($sp)
    ...
    lw   $s0, 0($sp)
    lw   $ra, 4($sp)
    addi $sp, $sp, 8
    jr   $ra
```

## MARS／SPIM 系統呼叫
id: mips-syscall
category: 概念
arch: MIPS
tags: MIPS, syscall, MARS, SPIM, $v0, 印出整數, 印出字串, 讀取整數, 結束程式
---
把服務號碼放 $v0、參數放 $a0（$a1），再執行 syscall：
```
1   印出整數         $a0 = 整數
4   印出字串         $a0 = 字串位址（.asciiz）
5   讀取整數         結果在 $v0
8   讀取字串         $a0 = 緩衝區、$a1 = 長度
10  結束程式
11  印出字元         $a0 = 字元
12  讀取字元         結果在 $v0
```
範例：
```
        .data
msg:    .asciiz "Hello\n"
        .text
main:   li   $v0, 4
        la   $a0, msg
        syscall
        li   $v0, 10
        syscall
```

## RISC-V 暫存器與 ABI 名稱
id: riscv-registers
category: 概念
arch: RISC-V
tags: RISC-V, 暫存器, x0, ra, sp, a0, t0, s0, ABI, 呼叫慣例
---
32 個整數暫存器 x0～x31，通常寫 ABI 名稱：
```
x0  zero  恆為 0           x1  ra    返回位址
x2  sp    堆疊指標         x3  gp    全域指標      x4  tp  執行緒指標
x5-x7   t0-t2  暫存        x8  s0/fp 保存／框架指標 x9  s1
x10-x11 a0-a1  參數／回傳值 x12-x17 a2-a7 參數（a7 也放系統呼叫號碼）
x18-x27 s2-s11 保存暫存器   x28-x31 t3-t6 暫存
```
RISC-V 沒有旗標暫存器，分支直接比較兩個暫存器（beq、bne、blt、bge、bltu、bgeu）。

## RISC-V 基本指令與範例
id: riscv-basics
category: 範例
arch: RISC-V
tags: RISC-V, addi, lw, sw, beq, jal, ecall, RARS, 範例, 迴圈
---
```
    li   t0, 0          # sum = 0
    li   t1, 1          # i = 1
    li   t2, 10
loop:
    add  t0, t0, t1     # sum += i
    addi t1, t1, 1      # i++
    ble  t1, t2, loop   # i <= 10 繼續
    mv   a0, t0
    li   a7, 1          # RARS：印出整數
    ecall
    li   a7, 10         # RARS：結束
    ecall
```
函式呼叫：call func（或 jal ra, func），返回用 ret（jalr x0, 0(ra)）。
立即值 12 位元有號；大常數用 lui + addi 或 li 虛擬指令。

## AVR 架構概觀
id: avr-overview
category: 概念
arch: AVR
tags: AVR, Arduino, ATmega328P, 暫存器, R16, X, Y, Z, SREG, I/O, PORTB, DDRB, PINB
---
AVR 是 Arduino UNO（ATmega328P）使用的 8 位元 RISC 微控制器：
・32 個 8 位元暫存器 R0～R31；只有 R16～R31 能用 LDI、SUBI、ANDI 等立即值指令
・R26:R27 = X、R28:R29 = Y、R30:R31 = Z 三個 16 位元指標
・SREG 狀態暫存器：I（全域中斷）、T、H、S、V、N、Z、C
・I/O 暫存器用 IN/OUT 存取：DDRx 設方向（1=輸出）、PORTx 輸出、PINx 讀輸入
・大多數指令 1 個時脈週期（16MHz 時 62.5ns）

Arduino D13 腳位 = PB5：
```
ldi r16, (1<<5)
out DDRB, r16      ; PB5 設為輸出
sbi PORTB, 5       ; 輸出高電位（LED 亮）
cbi PORTB, 5       ; 輸出低電位
```

## AVR LED 閃爍與延遲推導
id: avr-blink
category: 範例
arch: AVR
tags: AVR, Arduino, LED, 閃爍, 延遲, delay, DEC, BRNE, 時脈週期, 推導
---
```
delay:
        ldi  r18, 41
d1:     ldi  r19, 150
d2:     ldi  r20, 128
d3:     dec  r20          ; 1 週期
        brne d3           ; 跳躍 2 週期，不跳 1 週期
        dec  r19
        brne d2
        dec  r18
        brne d1
        ret
```
推導（16MHz，每週期 62.5ns）：
・最內層每圈 3 週期（dec 1 + brne 2），128 圈約 128×3 - 1 = 383 週期
・中層每圈 ≈ 1(ldi) + 383 + 3 = 387，150 圈 ≈ 58050 週期
・外層 41 圈 ≈ 41 × (1 + 58050 + 3) ≈ 2380214 週期 ≈ 0.149 秒
調整三層的初值即可得到需要的時間。
