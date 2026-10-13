# x86 指令觀念

## MOV、MOVZX、MOVSX、XCHG 的規則
id: x86-mov-rules
category: 概念
arch: x86
tags: MOV, MOVZX, MOVSX, XCHG, 資料搬移, 規則, 記憶體到記憶體, 區段暫存器
---
MOV 目的, 來源 的限制：
1. 兩個運算元大小必須相同（MOV EAX, BX 是錯的）
2. 不能兩個都是記憶體（MOV [a], [b] 是錯的，要經過暫存器）
3. 不能把立即值直接放進區段暫存器（MOV DS, 1000h 錯）
4. 不能修改 CS、EIP
5. 目的不能是立即值

大小不同時：
```
movzx eax, bl     ; 零延伸：無號數放大（BL=FFh → EAX=000000FFh）
movsx eax, bl     ; 符號延伸：有號數放大（BL=FFh → EAX=FFFFFFFFh，即 -1）
```
XCHG 交換兩個運算元（至少一個是暫存器）：
```
xchg eax, ebx
```

## 加減法指令與旗標
id: x86-add-sub
category: 概念
arch: x86
tags: ADD, SUB, INC, DEC, NEG, ADC, SBB, 加法, 減法, 旗標, 算術
---
```
add eax, ebx    ; EAX = EAX + EBX
sub eax, 10     ; EAX = EAX - 10
inc ecx         ; ECX + 1（不影響 CF！）
dec ecx         ; ECX - 1（不影響 CF！）
neg eax         ; EAX = -EAX（二補數）
adc edx, 0      ; EDX = EDX + 0 + CF（加上進位）
sbb edx, 0      ; EDX = EDX - 0 - CF（減去借位）
```
旗標：ADD/SUB/NEG/ADC/SBB 更新 CF、OF、SF、ZF、AF、PF；INC/DEC 不改 CF。
因為 INC/DEC 不影響 CF，多精度運算的迴圈可以用 INC 計數而不破壞 ADC 需要的進位。

## CMP 與條件跳躍（有號 vs 無號）
id: x86-cmp-jcc
category: 概念
arch: x86
tags: CMP, 條件跳躍, Jcc, JE, JNE, JG, JL, JA, JB, JGE, JLE, JAE, JBE, 有號, 無號, 比較
---
CMP a, b 計算 a - b，只設定旗標不存結果。之後用條件跳躍：

```
條件        無號數          有號數
a == b      JE / JZ         JE / JZ
a != b      JNE / JNZ       JNE / JNZ
a >  b      JA  (CF=0,ZF=0) JG  (ZF=0, SF=OF)
a >= b      JAE (CF=0)      JGE (SF=OF)
a <  b      JB  (CF=1)      JL  (SF≠OF)
a <= b      JBE (CF=1或ZF=1) JLE (ZF=1 或 SF≠OF)
```
記法：Above/Below 是無號（A、B），Greater/Less 是有號（G、L）。

例：EAX = -1（FFFFFFFFh）、EBX = 1，CMP EAX, EBX 之後
・JG 不跳（有號 -1 > 1 不成立）
・JA 會跳（無號 4294967295 > 1）

其他：JS／JNS（看 SF）、JO／JNO（看 OF）、JC／JNC（看 CF）、JECXZ（ECX=0 時跳）。

## 乘法 MUL 與 IMUL
id: x86-mul
category: 概念
arch: x86
tags: MUL, IMUL, 乘法, EDX:EAX, 無號乘法, 有號乘法, multiply
---
MUL（無號）／單運算元 IMUL（有號）的結果放在固定暫存器：

```
運算元大小   被乘數   結果
8 位元       AL       AX
16 位元      AX       DX:AX
32 位元      EAX      EDX:EAX
64 位元      RAX      RDX:RAX
```
例：
```
mov eax, 10000h
mov ebx, 10000h
mul ebx           ; EDX:EAX = 100000000h → EDX = 1, EAX = 0, CF=OF=1
```
CF=OF=1 表示結果的高半部有用到（乘積放不進單一暫存器）。

IMUL 還有雙／三運算元形式（只保留低位，常用）：
```
imul eax, ebx       ; EAX = EAX × EBX
imul eax, ebx, 10   ; EAX = EBX × 10
```
注意：MUL 不能乘立即值（MUL 5 是錯的），要先放進暫存器。

## 除法 DIV 與 IDIV
id: x86-div
category: 概念
arch: x86
tags: DIV, IDIV, 除法, 商, 餘數, CDQ, 除法溢位, divide, EDX
---
被除數在固定暫存器，商與餘數也放在固定位置：

```
除數大小   被除數     商    餘數
8 位元     AX         AL    AH
16 位元    DX:AX      AX    DX
32 位元    EDX:EAX    EAX   EDX
```
無號除法前要清除高位：
```
mov eax, 100
xor edx, edx      ; 很重要！EDX 必須是 0
mov ecx, 7
div ecx           ; EAX = 14（商），EDX = 2（餘數）
```
有號除法前用 CDQ 做符號延伸：
```
mov eax, -100
cdq               ; EDX = FFFFFFFFh
mov ecx, 7
idiv ecx          ; EAX = -14，EDX = -2（餘數和被除數同號）
```
除以 0、或商放不下目的暫存器，都會產生「除法錯誤（#DE）」使程式當掉。最常見原因就是忘了清 EDX。

## 邏輯運算與位元遮罩
id: x86-logic
category: 概念
arch: x86
tags: AND, OR, XOR, NOT, TEST, 遮罩, mask, 位元運算, 邏輯運算
---
```
and al, 0Fh       ; 只保留低 4 位元（清除其他位元）
or  al, 80h       ; 把第 7 位元設為 1
xor al, 01h       ; 把第 0 位元反相
not al            ; 全部位元反相（不影響旗標）
test al, 1        ; 判斷第 0 位元（AND 但不存結果），ZF=1 代表是 0
xor eax, eax      ; EAX = 0（比 MOV EAX, 0 短）
```
AND、OR、XOR、TEST 會使 CF=0、OF=0，並依結果設定 ZF、SF、PF。

位元操作口訣：
・清除用 AND（遮罩中要清的位元放 0）
・設定用 OR（遮罩中要設的位元放 1）
・反相用 XOR（遮罩中要反的位元放 1）
・檢查用 TEST

## 移位與旋轉指令
id: x86-shift-rotate
category: 概念
arch: x86
tags: SHL, SHR, SAR, SAL, ROL, ROR, RCL, RCR, 移位, 旋轉, 乘以2, 除以2
---
```
shl eax, 1     ; 左移，右邊補 0，最後移出的位元到 CF（= 乘以 2）
shr eax, 1     ; 邏輯右移，左邊補 0（= 無號數除以 2）
sar eax, 1     ; 算術右移，左邊補符號位元（= 有號數除以 2，向下取整）
rol al, 1      ; 循環左移，最高位元繞到最低位元並複製到 CF
ror al, 1      ; 循環右移
rcl al, 1      ; 帶進位循環左移（CF 也參與）
rcr al, 1      ; 帶進位循環右移
shl eax, cl    ; 移位次數放在 CL（只能用 CL 或立即值）
```
注意 SAR 與 IDIV 對負數的差別：-7 SAR 1 = -4（向下取整），-7 IDIV 2 = -3（向 0 取整）。
「數值推導」頁可以一步一步看移位過程。

## 堆疊 PUSH 與 POP
id: x86-stack
category: 概念
arch: x86
tags: 堆疊, stack, PUSH, POP, ESP, LIFO, 後進先出
---
x86 堆疊是「後進先出（LIFO）」並且往低位址成長：

```
push eax    ; ESP = ESP - 4，然後 [ESP] = EAX
pop  ebx    ; EBX = [ESP]，然後 ESP = ESP + 4
```
例：ESP = 0019FF00h
```
push 1      ; ESP = 0019FEFCh，[0019FEFC] = 1
push 2      ; ESP = 0019FEF8h，[0019FEF8] = 2
pop  eax    ; EAX = 2，ESP = 0019FEFCh
pop  ebx    ; EBX = 1，ESP = 0019FF00h
```
用途：保存暫存器、傳遞參數、CALL/RET 的返回位址、區域變數、反轉資料順序。
PUSH 和 POP 的次數必須相同（堆疊平衡），否則 RET 會取到錯誤的返回位址。
16 位元模式一次推入 2 個位元組，x64 一次 8 個位元組。

## CALL 與 RET
id: x86-call-ret
category: 概念
arch: x86
tags: CALL, RET, 副程式, 返回位址, 堆疊, procedure, 呼叫
---
```
call MySub     ; 1. 把下一行指令的位址（返回位址）PUSH 進堆疊
               ; 2. 跳到 MySub
...
MySub PROC
    ...
    ret        ; 從堆疊 POP 返回位址並跳回去
MySub ENDP
```
RET n：返回後再把 ESP 加 n，用來清除呼叫者推入的參數（stdcall）。
副程式內 PUSH 的東西必須在 RET 之前 POP 掉，否則 RET 會把資料當成返回位址。

## LOOP 指令
id: x86-loop
category: 概念
arch: x86
tags: LOOP, 迴圈, ECX, CX, 計數, LOOPE, LOOPNE, JECXZ
---
LOOP label 等於：
```
dec ecx          ; （但 LOOP 不影響旗標）
jnz label
```
典型寫法：
```
    mov ecx, 10      ; 執行 10 次
L1:
    ; ... 迴圈內容 ...
    loop L1
```
注意：
1. 先減 1 再判斷，所以 ECX = 10 會執行 10 次
2. 若進入迴圈時 ECX = 0，減 1 後變成 FFFFFFFFh，會執行 4294967296 次！可在迴圈前用 JECXZ 跳過
3. 迴圈內若要用 ECX，先 PUSH ECX，結束前 POP ECX
4. 巢狀迴圈要保存外層的 ECX
5. LOOP 只能跳 -128～+127 位元組，迴圈太長時改用 DEC ECX / JNZ

## 字串指令與 REP 前綴
id: x86-string-instructions
category: 概念
arch: x86
tags: 字串指令, MOVSB, STOSB, LODSB, SCASB, CMPSB, REP, REPE, REPNE, DF, CLD, ESI, EDI
---
字串指令使用 ESI（來源）、EDI（目的）、ECX（次數），並依 DF 決定方向（CLD 遞增、STD 遞減）：

```
MOVSB/W/D   [EDI] ← [ESI]，兩者前進          （複製）
STOSB/W/D   [EDI] ← AL/AX/EAX，EDI 前進      （填值）
LODSB/W/D   AL/AX/EAX ← [ESI]，ESI 前進      （讀取）
SCASB/W/D   比較 AL 與 [EDI]，EDI 前進        （搜尋）
CMPSB/W/D   比較 [ESI] 與 [EDI]，兩者前進     （比較）
```
前綴：
・REP：重複 ECX 次（配合 MOVS、STOS）
・REPE/REPZ：相等時繼續（配合 CMPS 比較字串）
・REPNE/REPNZ：不相等時繼續（配合 SCAS 找字元）

例：複製 100 個位元組
```
cld
mov esi, OFFSET src
mov edi, OFFSET dst
mov ecx, 100
rep movsb
```

## 條件設定 SETcc 與條件搬移 CMOVcc
id: x86-setcc-cmov
category: 概念
arch: x86
tags: SETcc, CMOVcc, SETE, SETL, CMOVG, CMOVL, 無分支, branchless
---
不用跳躍就能依條件設定值，避免分支預測失敗：
```
cmp  eax, ebx
setl cl            ; EAX < EBX（有號）時 CL = 1，否則 0
cmovg eax, ebx     ; EAX > EBX 時 EAX = EBX（相當於取最小值）
```
SETcc 的目的是 8 位元；CMOVcc 不能使用立即值，來源要是暫存器或記憶體。
