using System.Collections.Generic;
using Assam.Core.Analysis;

namespace Assam.Core.Samples
{
    public sealed class SampleProgram
    {
        public string Name { get; set; }
        public AsmArch Arch { get; set; }
        public string Code { get; set; }
        /// <summary>推導模擬建議的初始暫存器設定（可留空）。</summary>
        public string InitialRegisters { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>內建範例程式（程式碼分析頁與推導模擬頁的「載入範例」）。</summary>
    public static class SampleCode
    {
        public static readonly IList<SampleProgram> All = new List<SampleProgram>
        {
            new SampleProgram
            {
                Name = "x86：基本算術與旗標（適合推導）",
                Arch = AsmArch.X86Intel,
                Code =
@"; 基本算術：觀察每一步暫存器與旗標的變化
mov eax, 7Fh        ; EAX = 127
add eax, 1          ; 127 + 1
mov bl, 7Fh
add bl, 1           ; 8 位元有號溢位：OF=1
mov ecx, 10
sub ecx, 12         ; 10 - 12 → 負數，CF=1（借位）
xor edx, edx        ; 清零技巧
mov eax, 100
mov ebx, 7
div ebx             ; 100 ÷ 7 = 14 … 2
shl eax, 2          ; 14 × 4 = 56
"
            },
            new SampleProgram
            {
                Name = "x86：LOOP 計算 1+2+…+10",
                Arch = AsmArch.X86Intel,
                Code =
@"; 用 LOOP 計算 1 + 2 + ... + 10
    xor eax, eax        ; 總和 = 0
    mov ecx, 10         ; 迴圈 10 次
L1:
    add eax, ecx        ; 總和 += ECX
    loop L1             ; ECX--，不為 0 就繼續
; 結果 EAX = 55
"
            },
            new SampleProgram
            {
                Name = "x86 MASM（Irvine32）：陣列加總",
                Arch = AsmArch.X86Intel,
                Code =
@"INCLUDE Irvine32.inc

.data
arr     DWORD 10, 20, 30, 40, 50
msg     BYTE ""Sum = "", 0

.code
main PROC
    mov  esi, OFFSET arr        ; ESI 指向陣列開頭
    mov  ecx, LENGTHOF arr      ; ECX = 元素個數 5
    xor  eax, eax               ; EAX = 0（總和）
L1:
    add  eax, [esi]             ; 加上目前元素
    add  esi, TYPE arr          ; 移到下一個元素（+4）
    loop L1
    mov  edx, OFFSET msg
    call WriteString
    call WriteDec               ; 顯示 EAX
    call Crlf
    exit
main ENDP
END main
"
            },
            new SampleProgram
            {
                Name = "x86 MASM：找陣列最大值（有號比較）",
                Arch = AsmArch.X86Intel,
                Code =
@".data
nums SDWORD -5, 12, 7, -30, 25, 3

.code
main PROC
    mov  esi, OFFSET nums
    mov  ecx, LENGTHOF nums
    mov  eax, [esi]             ; 先假設第一個是最大值
next:
    cmp  [esi], eax
    jle  skip                   ; 有號比較：小於等於就跳過
    mov  eax, [esi]             ; 找到更大的
skip:
    add  esi, 4
    loop next
    ; EAX = 25
    ret
main ENDP
END main
"
            },
            new SampleProgram
            {
                Name = "x86：遞迴階乘 5!（觀察堆疊）",
                Arch = AsmArch.X86Intel,
                Code =
@"main PROC
    mov  eax, 5
    call Factorial      ; EAX = 5! = 120
    ret
main ENDP

; 輸入 EAX = n，輸出 EAX = n!
Factorial PROC
    cmp  eax, 1
    jbe  done           ; n <= 1 時回傳 1
    push eax            ; 保存 n
    dec  eax
    call Factorial      ; EAX = (n-1)!
    pop  ebx            ; 取回 n
    mul  ebx            ; EAX = (n-1)! × n
    ret
done:
    mov  eax, 1
    ret
Factorial ENDP
END main
"
            },
            new SampleProgram
            {
                Name = "x86：字串長度（REPNE SCASB）",
                Arch = AsmArch.X86Intel,
                Code =
@".data
text BYTE ""Assembly"", 0

.code
main PROC
    mov  edi, OFFSET text
    mov  ecx, 0FFFFFFFFh    ; 最多找這麼多個字元
    xor  al, al             ; 要找的是 0（字串結尾）
    cld
    repne scasb             ; 一直比較直到找到 0
    not  ecx
    dec  ecx                ; ECX = 字串長度 8
    ret
main ENDP
END main
"
            },
            new SampleProgram
            {
                Name = "x86 MASM 16 位元（DOS）：顯示字串",
                Arch = AsmArch.X86Intel,
                Code =
@".model small
.stack 100h
.data
msg db 'Hello, DOS!', 0Dh, 0Ah, '$'
.code
main proc
    mov ax, @data
    mov ds, ax          ; 設定資料區段
    mov ah, 09h         ; DOS 功能 09h：顯示字串
    mov dx, offset msg
    int 21h
    mov ah, 4Ch         ; 結束程式
    int 21h
main endp
end main
"
            },
            new SampleProgram
            {
                Name = "x86 NASM（Linux 32 位元）：Hello World",
                Arch = AsmArch.X86Intel,
                Code =
@"section .data
    msg db 'Hello, Linux!', 10
    len equ $ - msg

section .text
    global _start
_start:
    mov eax, 4          ; sys_write
    mov ebx, 1          ; stdout
    mov ecx, msg        ; 字串位址
    mov edx, len        ; 長度
    int 80h
    mov eax, 1          ; sys_exit
    xor ebx, ebx
    int 80h
"
            },
            new SampleProgram
            {
                Name = "x86 AT&T（GAS）：兩數相加",
                Arch = AsmArch.X86Att,
                Code =
@"    .globl add_two
    .text
add_two:
    pushl %ebp
    movl  %esp, %ebp
    movl  8(%ebp), %eax     # 第一個參數
    addl  12(%ebp), %eax    # 加上第二個參數
    popl  %ebp
    ret
"
            },
            new SampleProgram
            {
                Name = "8051：P1.0 LED 閃爍（含延遲副程式）",
                Arch = AsmArch.I8051,
                Code =
@"        ORG   0000H
MAIN:   CPL   P1.0          ; LED 反相
        ACALL DELAY
        SJMP  MAIN

; 12MHz 下約 0.1 秒
DELAY:  MOV   R6, #200
D1:     MOV   R7, #250
D2:     DJNZ  R7, D2
        DJNZ  R6, D1
        RET
        END
"
            },
            new SampleProgram
            {
                Name = "8051：跑馬燈（RL A）",
                Arch = AsmArch.I8051,
                Code =
@"        ORG  0000H
        MOV  A, #0FEH       ; 1111 1110：P1.0 的 LED 亮（低電位驅動）
LOOP:   MOV  P1, A
        ACALL DELAY
        RL   A              ; 左旋 1 位元，亮的 LED 往左移
        SJMP LOOP

DELAY:  MOV  R5, #2
D1:     MOV  R6, #250
D2:     MOV  R7, #250
D3:     DJNZ R7, D3
        DJNZ R6, D2
        DJNZ R5, D1
        RET
        END
"
            },
            new SampleProgram
            {
                Name = "ARM（32 位元）：陣列加總",
                Arch = AsmArch.Arm,
                Code =
@"    .syntax unified
    .text
    .global sum_array
@ R0 = 陣列位址，R1 = 元素個數，回傳 R0 = 總和
sum_array:
    MOV   R2, #0            @ 總和
loop:
    CMP   R1, #0
    BEQ   done
    LDR   R3, [R0], #4      @ 讀取元素後 R0 += 4（後索引）
    ADD   R2, R2, R3
    SUBS  R1, R1, #1
    B     loop
done:
    MOV   R0, R2
    BX    LR
"
            },
            new SampleProgram
            {
                Name = "ARM64：兩數最大值",
                Arch = AsmArch.Arm64,
                Code =
@"    .global max2
// X0、X1 為參數，回傳較大者
max2:
    CMP   X0, X1
    CSEL  X0, X0, X1, GT    // X0 > X1 ? X0 : X1
    RET
"
            },
            new SampleProgram
            {
                Name = "MIPS（MARS）：陣列加總並印出",
                Arch = AsmArch.Mips,
                Code =
@"        .data
arr:    .word 3, 5, 7, 9
n:      .word 4
        .text
main:   la    $t0, arr          # $t0 = 陣列位址
        lw    $t1, n            # $t1 = 元素個數
        li    $t2, 0            # 總和
loop:   beq   $t1, $zero, done
        lw    $t3, 0($t0)
        add   $t2, $t2, $t3
        addi  $t0, $t0, 4
        addi  $t1, $t1, -1
        j     loop
done:   move  $a0, $t2
        li    $v0, 1            # 印出整數
        syscall
        li    $v0, 10           # 結束
        syscall
"
            },
            new SampleProgram
            {
                Name = "RISC-V（RARS）：1 加到 10",
                Arch = AsmArch.RiscV,
                Code =
@"    .text
main:
    li   t0, 0          # sum
    li   t1, 1          # i
    li   t2, 10
loop:
    add  t0, t0, t1
    addi t1, t1, 1
    ble  t1, t2, loop
    mv   a0, t0
    li   a7, 1          # 印出整數
    ecall
    li   a7, 10         # 結束
    ecall
"
            },
            new SampleProgram
            {
                Name = "AVR（Arduino UNO）：D13 LED 閃爍",
                Arch = AsmArch.Avr,
                Code =
@".include ""m328pdef.inc""
.def temp = r16
        .org 0x0000
        rjmp main
main:
        ldi  temp, (1<<5)
        out  DDRB, temp         ; PB5（D13）設為輸出
blink:
        sbi  PORTB, 5           ; LED 亮
        rcall delay
        cbi  PORTB, 5           ; LED 滅
        rcall delay
        rjmp blink
delay:
        ldi  r18, 41
d1:     ldi  r19, 150
d2:     ldi  r20, 128
d3:     dec  r20
        brne d3
        dec  r19
        brne d2
        dec  r18
        brne d1
        ret
"
            }
        };
    }
}
