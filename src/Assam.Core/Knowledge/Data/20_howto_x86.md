# x86 常見寫法（做法）

## 交換兩個變數
id: howto-swap
category: 做法
arch: x86
tags: 交換, swap, XCHG, 變數, 暫存器
---
記憶體變數不能直接互換（不能記憶體到記憶體），要經過暫存器：
```
mov  eax, var1
xchg eax, var2      ; EAX 與 var2 交換
mov  var1, eax
```
或用堆疊：
```
push var1
push var2
pop  var1
pop  var2
```
兩個暫存器直接 XCHG EAX, EBX；也可以用 XOR 三次（不需暫存空間，但較慢、較難讀）：
```
xor eax, ebx
xor ebx, eax
xor eax, ebx
```

## 把暫存器清為 0
id: howto-zero-register
category: 做法
arch: x86
tags: 清零, 歸零, XOR, SUB, MOV, zero
---
```
xor eax, eax     ; 最常用：指令最短（2 位元組），CPU 也特別最佳化
sub eax, eax     ; 同樣是 0，也會設定旗標
mov eax, 0       ; 不影響旗標，但指令較長（5 位元組）
```
在條件跳躍前要保留旗標時，必須用 MOV EAX, 0。
x64 中 XOR EAX, EAX 會一併清除 RAX 的高 32 位元，所以不用寫 XOR RAX, RAX。

## 陣列加總
id: howto-array-sum
category: 做法
arch: x86
tags: 陣列, 加總, 總和, sum, array, LOOP, ESI, 迴圈
---
```
.data
arr DWORD 10, 20, 30, 40, 50

.code
    mov  esi, OFFSET arr      ; 指向第一個元素
    mov  ecx, LENGTHOF arr    ; 元素個數
    xor  eax, eax             ; 總和 = 0
L1:
    add  eax, [esi]
    add  esi, TYPE arr        ; 下一個元素（DWORD 為 4）
    loop L1
    ; EAX = 150
```
改用索引的寫法：
```
    xor  esi, esi             ; 索引 i = 0
L1: add  eax, arr[esi*4]
    inc  esi
    cmp  esi, LENGTHOF arr
    jl   L1
```
注意元素大小：BYTE 陣列每次加 1、WORD 加 2、DWORD 加 4；BYTE 陣列加總要先 MOVZX 到 32 位元暫存器再加，避免溢位。

## 找陣列最大值／最小值
id: howto-array-max
category: 做法
arch: x86
tags: 最大值, 最小值, max, min, 陣列, CMP, JG, JL, CMOV
---
```
    mov  esi, OFFSET arr
    mov  ecx, LENGTHOF arr
    mov  eax, [esi]           ; 先假設第一個是最大
    add  esi, 4
    dec  ecx
    jecxz done                ; 只有一個元素
next:
    cmp  [esi], eax
    jle  skip                 ; 有號比較；無號請用 JBE
    mov  eax, [esi]
skip:
    add  esi, 4
    loop next
done:
    ; EAX = 最大值
```
最小值把 JLE 改成 JGE。
不用分支的寫法：mov ebx, [esi] / cmp ebx, eax / cmovg eax, ebx。

## 計算字串長度
id: howto-strlen
category: 做法
arch: x86
tags: 字串長度, strlen, string length, SCASB, 0結尾, 迴圈
---
方法一：一般迴圈
```
    mov  edi, OFFSET str
    xor  ecx, ecx
L1: cmp  BYTE PTR [edi+ecx], 0
    je   done
    inc  ecx
    jmp  L1
done:                         ; ECX = 長度
```
方法二：REPNE SCASB
```
    mov  edi, OFFSET str
    mov  ecx, -1              ; 最多掃描 FFFFFFFFh 次
    xor  al, al               ; 找 0
    cld
    repne scasb
    not  ecx
    dec  ecx                  ; ECX = 長度
```
Irvine32 可以直接呼叫 StrLength（EDX = 字串位址，回傳 EAX）。

## 字串反轉
id: howto-reverse-string
category: 做法
arch: x86
tags: 字串反轉, reverse, 堆疊, PUSH, POP, 雙指標
---
利用堆疊後進先出的特性：
```
    mov  esi, OFFSET str
    mov  ecx, len
L1: movzx eax, BYTE PTR [esi]
    push eax                  ; 逐字推入
    inc  esi
    loop L1

    mov  esi, OFFSET str
    mov  ecx, len
L2: pop  eax                  ; 反向取出
    mov  [esi], al
    inc  esi
    loop L2
```
也可以用頭尾兩個指標互換：ESI 指向開頭、EDI 指向結尾，交換後 ESI++、EDI--，直到 ESI >= EDI。

## 大小寫轉換
id: howto-case-convert
category: 做法
arch: x86
tags: 大小寫, 大寫, 小寫, uppercase, lowercase, AND, OR, XOR, 20h, ASCII
---
大小寫只差第 5 位元（20h）：
```
and al, 11011111b    ; 轉大寫（0DFh）
or  al, 00100000b    ; 轉小寫（20h）
xor al, 00100000b    ; 大小寫互換
```
只轉換字母（避免改到數字或符號）：
```
    cmp al, 'a'
    jb  skip
    cmp al, 'z'
    ja  skip
    and al, 0DFh        ; 'a'~'z' 才轉大寫
skip:
```

## 判斷奇偶數
id: howto-odd-even
category: 做法
arch: x86
tags: 奇數, 偶數, odd, even, TEST, 最低位元, 位元檢查
---
看最低位元（第 0 位元）：1 為奇數、0 為偶數。
```
test eax, 1
jz   is_even      ; ZF=1 → 第 0 位元是 0 → 偶數
; 奇數
```
也可以 SHR EAX, 1 之後看 CF，或 AND EAX, 1 取得 0/1（會改變 EAX）。

## 判斷正負與絕對值
id: howto-abs
category: 做法
arch: x86
tags: 絕對值, abs, 正負, 負數, NEG, CDQ, SAR, 無分支
---
有分支：
```
    test eax, eax
    jns  positive       ; SF=0 → 不是負數
    neg  eax            ; 負數取二補數
positive:
```
無分支（利用 CDQ）：
```
    cdq                 ; EDX = 0（正）或 FFFFFFFFh（負）
    xor  eax, edx
    sub  eax, edx       ; EAX = |EAX|
```
注意：-2147483648 的絕對值放不進 32 位元有號數，NEG 後仍是 80000000h（OF=1）。

## 用移位與加法乘以常數
id: howto-multiply-shift
category: 做法
arch: x86
tags: 乘法, 移位, SHL, LEA, 乘以10, 最佳化, 乘常數
---
左移 n 位等於乘以 2^n，組合起來可以乘任何常數：
```
; EAX × 10 = EAX × 8 + EAX × 2
mov  ebx, eax
shl  eax, 3         ; ×8
shl  ebx, 1         ; ×2
add  eax, ebx       ; ×10
```
用 LEA 更簡潔：
```
lea  eax, [eax + eax*4]   ; ×5
add  eax, eax             ; ×10
```
在 8086 上 MUL 很慢，所以這類寫法很常見；現代 CPU 的 IMUL 已經很快，可讀性優先。

## 除以 2 的冪次
id: howto-divide-shift
category: 做法
arch: x86
tags: 除法, 移位, SHR, SAR, 除以2, 負數, 取餘數, AND
---
・無號數除以 2^n：SHR EAX, n
・有號數除以 2^n：SAR EAX, n（注意負數會向下取整，-7 SAR 1 = -4，和 IDIV 的 -3 不同）
・無號數除以 2^n 的餘數：AND EAX, (2^n - 1)，例如 AND EAX, 7 得到除以 8 的餘數

想讓負數也「向 0 取整」：
```
    cdq                 ; 負數時 EDX = -1
    and  edx, 7         ; 負數時加上 (2^n - 1)
    add  eax, edx
    sar  eax, 3         ; 除以 8，向 0 取整
```

## 位元的設定、清除、反相與檢查
id: howto-bit-ops
category: 做法
arch: x86
tags: 位元, bit, 設定, 清除, 反相, 檢查, BT, BTS, BTR, 遮罩, mask
---
以第 n 位元為例（遮罩 = 1 << n）：
```
or   eax, 00001000b    ; 設定第 3 位元
and  eax, 11110111b    ; 清除第 3 位元（注意 32 位元要用 0FFFFFFF7h）
xor  eax, 00001000b    ; 反相第 3 位元
test eax, 00001000b    ; 檢查第 3 位元，ZF=0 表示是 1
```
80386 以後也可以用：BT（檢查到 CF）、BTS（設定）、BTR（清除）、BTC（反相）：
```
bt   eax, 3
jc   bit_is_one
```

## 把數字轉成十進位字串輸出
id: howto-print-decimal
category: 做法
arch: x86
tags: 十進位輸出, 數字轉字串, itoa, 除以10, 餘數, 堆疊, 印出數字, DOS
---
不斷除以 10 取餘數，餘數 + '0' 就是數字字元；因為先得到個位數，所以用堆疊反轉順序：
```
; 輸入 AX = 要顯示的無號數（16 位元 DOS 版本）
print_num PROC
    mov  bx, 10
    xor  cx, cx           ; 位數計數
next_digit:
    xor  dx, dx           ; DX:AX ÷ 10
    div  bx               ; AX = 商，DX = 餘數
    push dx               ; 保存這一位
    inc  cx
    test ax, ax
    jnz  next_digit
print_loop:
    pop  dx
    add  dl, '0'          ; 轉成 ASCII
    mov  ah, 02h
    int  21h              ; 顯示 DL
    loop print_loop
    ret
print_num ENDP
```
32 位元 Irvine32 直接用 WriteDec／WriteInt 即可。

## 讀取鍵盤輸入的數字
id: howto-read-number
category: 做法
arch: x86
tags: 輸入, 讀取數字, 鍵盤, atoi, 字元轉數字, 乘以10, DOS 01h, ReadInt
---
逐字讀入，每讀一個數字字元就「結果 × 10 + 這一位」：
```
; 結果放在 BX（DOS 16 位元，按 Enter 結束）
    xor  bx, bx
read_loop:
    mov  ah, 01h
    int  21h              ; AL = 輸入字元
    cmp  al, 0Dh          ; Enter？
    je   done
    sub  al, '0'          ; 字元 → 數值
    xor  ah, ah
    mov  cx, ax           ; 暫存這一位
    mov  ax, bx
    mov  dx, 10
    mul  dx               ; AX = BX × 10
    add  ax, cx
    mov  bx, ax
    jmp  read_loop
done:
```
Irvine32：call ReadInt，結果直接在 EAX。

## 氣泡排序
id: howto-bubble-sort
category: 做法
arch: x86
tags: 排序, 氣泡排序, bubble sort, sort, 陣列, 巢狀迴圈, 交換
---
```
; 由小到大排序 DWORD 陣列 arr（有號數）
    mov  ecx, LENGTHOF arr
    dec  ecx                  ; 外圈 n-1 次
outer:
    push ecx                  ; 保存外圈計數
    mov  esi, OFFSET arr
inner:
    mov  eax, [esi]
    cmp  eax, [esi+4]
    jle  no_swap              ; 已經是小的在前
    xchg eax, [esi+4]         ; 交換相鄰兩個元素
    mov  [esi], eax
no_swap:
    add  esi, 4
    loop inner
    pop  ecx
    loop outer
```
重點：巢狀 LOOP 要先 PUSH 外層的 ECX；內層次數直接沿用外層的 ECX（每一輪少比較一次）。

## 費氏數列
id: howto-fibonacci
category: 做法
arch: x86
tags: 費氏數列, fibonacci, 迴圈, 數列
---
```
; 計算第 n 項（F(0)=0, F(1)=1），n 放 ECX，結果在 EAX
    xor  eax, eax         ; F(i)
    mov  ebx, 1           ; F(i+1)
    jecxz done
fib:
    mov  edx, eax
    add  edx, ebx         ; F(i+2) = F(i) + F(i+1)
    mov  eax, ebx
    mov  ebx, edx
    loop fib
done:
```
ECX = 10 時 EAX = 55。32 位元無號數最多到 F(47)，之後會溢位。

## 階乘（迴圈版）
id: howto-factorial-loop
category: 做法
arch: x86
tags: 階乘, factorial, 迴圈, MUL, 乘法
---
```
; n 放 ECX，結果在 EAX
    mov  eax, 1
    jecxz done            ; 0! = 1
fact:
    mul  ecx              ; EDX:EAX = EAX × ECX
    loop fact
done:
```
5! = 120；32 位元最多到 12! = 479001600，13! 會超過 32 位元（MUL 後 EDX ≠ 0，CF=OF=1）。
遞迴版請參考「遞迴與堆疊推導」。

## 64 位元加法（多精度運算 ADC）
id: howto-multi-precision
category: 做法
arch: x86
tags: 多精度, 64位元加法, ADC, SBB, 進位, 大數, carry
---
在 32 位元環境中把兩個 64 位元數相加：
```
; (EDX:EAX) + (ECX:EBX)
add eax, ebx      ; 低 32 位元相加，產生 CF
adc edx, ecx      ; 高 32 位元相加，再加上 CF
```
減法用 SUB + SBB。更長的數字（例如 128 位元）就用迴圈，從低位到高位依序 ADC，迴圈計數用 INC/DEC（它們不影響 CF）。

## if／else 的組合語言寫法
id: howto-if-else
category: 做法
arch: x86
tags: if, else, 條件判斷, CMP, 條件跳躍, 流程控制, 高階語言對照
---
C 語言：
```
if (a > b) x = 1; else x = 2;
```
組合語言（a、b 為有號數）：
```
    mov  eax, a
    cmp  eax, b
    jle  else_part        ; 條件「相反」時跳到 else
    mov  x, 1
    jmp  end_if
else_part:
    mov  x, 2
end_if:
```
技巧：跳躍條件通常寫成原條件的「相反」，讓 then 區塊緊接在後面。
複合條件 a > b && c > d：任一個不成立就跳到 else；a > b || c > d：任一個成立就跳到 then。

## while／for／do-while 迴圈的寫法
id: howto-loops
category: 做法
arch: x86
tags: while, for, do while, 迴圈, 流程控制, 高階語言對照, 跳躍
---
while (i < 10) { ... i++; }
```
    mov  ecx, 0           ; i = 0
while_top:
    cmp  ecx, 10
    jge  while_end        ; 條件不成立就離開
    ; ... 迴圈內容 ...
    inc  ecx
    jmp  while_top
while_end:
```
do { ... } while (i < 10);（至少執行一次）
```
do_top:
    ; ... 迴圈內容 ...
    inc  ecx
    cmp  ecx, 10
    jl   do_top
```
for 迴圈若是固定次數，最簡單的是 MOV ECX, n + LOOP。

## 計算 1 + 2 + … + N
id: howto-sum-1-to-n
category: 做法
arch: x86
tags: 累加, 總和, 1加到N, 迴圈, LOOP, 公式
---
迴圈法：
```
    mov  ecx, 10      ; N
    xor  eax, eax
L1: add  eax, ecx     ; 從 N 加到 1
    loop L1           ; EAX = 55
```
公式法 N×(N+1)/2：
```
    mov  eax, 10
    mov  ebx, eax
    inc  ebx
    mul  ebx          ; EAX = 110
    shr  eax, 1       ; EAX = 55
```

## 最大公因數 GCD
id: howto-gcd
category: 做法
arch: x86
tags: 最大公因數, GCD, 輾轉相除法, 除法, 餘數, DIV
---
輾轉相除法：gcd(a, b) = gcd(b, a mod b)，直到 b = 0。
```
; EAX = a、EBX = b，結果在 EAX
gcd_loop:
    test ebx, ebx
    jz   gcd_done
    xor  edx, edx
    div  ebx          ; EDX = a mod b
    mov  eax, ebx
    mov  ebx, edx
    jmp  gcd_loop
gcd_done:
```
例：gcd(48, 18) → (18, 12) → (12, 6) → (6, 0) → 6

## 判斷質數
id: howto-prime
category: 做法
arch: x86
tags: 質數, prime, 除法, 餘數, 迴圈
---
從 2 試除到 √n（或 n-1），只要有一個能整除就不是質數：
```
; 輸入 ECX = n (n >= 2)，結果 AL = 1 表示質數
    mov  ebx, 2
check:
    mov  eax, ebx
    mul  ebx              ; EAX = ebx²
    cmp  eax, ecx
    ja   is_prime         ; ebx² > n，試完了
    mov  eax, ecx
    xor  edx, edx
    div  ebx
    test edx, edx
    jz   not_prime        ; 整除
    inc  ebx
    jmp  check
is_prime:
    mov  al, 1
    jmp  finish
not_prime:
    mov  al, 0
finish:
```

## 以十六進位顯示一個數字
id: howto-print-hex
category: 做法
arch: x86
tags: 十六進位輸出, hex, 數字轉字串, ROL, AND 0Fh, 查表
---
每次取 4 個位元，轉成 '0'～'9' 或 'A'～'F'：
```
; 顯示 AX 的 4 位十六進位（DOS）
    mov  cx, 4
next:
    rol  ax, 4            ; 把最高的 4 位元轉到最低
    mov  dl, al
    and  dl, 0Fh
    add  dl, '0'
    cmp  dl, '9'
    jbe  print
    add  dl, 7            ; 'A' - '9' - 1 = 7
print:
    push ax
    mov  ah, 02h
    int  21h
    pop  ax
    loop next
```
查表法：hexchars BYTE "0123456789ABCDEF"，用 XLAT 或 MOV DL, hexchars[BX]。
