# 除錯與常見錯誤

## 運算元大小不一致
id: debug-size-mismatch
category: 除錯
arch: x86
tags: 錯誤, operand size mismatch, 運算元大小, A2022, MOVZX, PTR, 除錯
---
錯誤訊息：error A2022: instruction operands must be the same size
```
mov eax, bx        ; 錯：32 位元 ← 16 位元
movzx eax, bx      ; 對：零延伸
movsx eax, bx      ; 對：符號延伸（有號數）

var BYTE 5
mov eax, var       ; 錯：var 是 BYTE
movzx eax, var     ; 對
```
記憶體運算元沒有型態時（例如 [ebx]）要加 BYTE PTR／WORD PTR／DWORD PTR。

## 不能記憶體到記憶體
id: debug-mem-to-mem
category: 除錯
arch: x86
tags: 錯誤, 記憶體到記憶體, invalid instruction operands, A2070, MOV, 兩個記憶體
---
錯誤訊息：error A2070: invalid instruction operands
x86 的兩個運算元不能同時是記憶體（MOVS 等字串指令例外）：
```
mov var1, var2     ; 錯
mov eax, var2      ; 對：先搬到暫存器
mov var1, eax
```
ADD、SUB、CMP、XCHG 等也一樣。

## 除法錯誤：除以零與商太大
id: debug-divide-error
category: 除錯
arch: x86
tags: 除法錯誤, divide error, 除以零, integer overflow, DIV, IDIV, EDX, CDQ, 當機
---
DIV／IDIV 會在兩種情況產生除法錯誤（程式直接當掉，Windows 顯示 Integer division by zero 或 Integer overflow）：
1. 除數為 0
2. 商放不進目的暫存器（例如 8 位元除法商 > 255）

最常見原因：32 位元除法前忘了處理 EDX。
```
mov eax, 100
mov ecx, 7
div ecx            ; 若 EDX 剛好有殘留值（例如前面 MUL 留下的），被除數就變成巨大的數字 → 溢位
```
修正：無號除法前 XOR EDX, EDX；有號除法前 CDQ。8 位元除法則要設定好 AH（無號用 MOVZX AX, AL 或 MOV AH, 0；有號用 CBW）。

## 堆疊不平衡
id: debug-stack-imbalance
category: 除錯
arch: x86
tags: 堆疊不平衡, stack, PUSH, POP, RET, 當機, access violation, 返回位址
---
症狀：RET 之後跳到奇怪的位址、Access violation、程式莫名其妙重複執行。
原因：副程式中 PUSH 與 POP 次數不一致，RET 取到的不是返回位址。
```
MyProc PROC
    push ebx
    ...
    jz   quit          ; 錯：從這裡跳走就少了 POP
    pop  ebx
quit:
    ret
MyProc ENDP
```
檢查方法：在副程式開頭與 RET 前比較 ESP 是否相同；所有離開路徑都要經過相同的 POP。
另外 cdecl 呼叫後忘了 ADD ESP, n，也會讓堆疊一直往下長。

## 無窮迴圈與 LOOP 陷阱
id: debug-infinite-loop
category: 除錯
arch: x86
tags: 無窮迴圈, LOOP, ECX, 迴圈, 當機, 卡住, JECXZ, 計數器被改
---
常見原因：
1. 進入 LOOP 前 ECX = 0 → 執行 4294967296 次。用 JECXZ 先檢查
2. 迴圈內呼叫的副程式改了 ECX（例如 Irvine 某些程序、或自己寫的副程式）→ 迴圈前後 PUSH/POP ECX
3. 條件跳躍用錯方向或用錯有號／無號，條件永遠成立
4. 迴圈變數沒有更新（忘了 INC/ADD）
5. 8051：DJNZ 的暫存器在迴圈內被改掉；SJMP $ 本身就是故意的無窮迴圈

「推導模擬」頁會在超過最大步數時停止，可以從追蹤表看出哪個值沒有變化。

## 有號與無號跳躍用錯
id: debug-signed-unsigned
category: 除錯
arch: x86
tags: 有號, 無號, JG, JA, JL, JB, 條件跳躍, 比較錯誤, 負數
---
症狀：有負數時比較結果不對。
```
mov eax, -1
cmp eax, 5
ja  bigger         ; 會跳！因為無號看 -1 是 FFFFFFFFh（很大）
jg  bigger         ; 不會跳，有號 -1 < 5
```
規則：資料是有號數（可能為負）就用 JG/JL/JGE/JLE；是無號數（位址、計數、ASCII）就用 JA/JB/JAE/JBE。
同樣地 MUL/IMUL、DIV/IDIV、SHR/SAR、MOVZX/MOVSX 也要依有號或無號選擇。

## 暫存器被副程式破壞
id: debug-register-clobber
category: 除錯
arch: x86
tags: 暫存器保存, 呼叫者保存, 被呼叫者保存, USES, PUSH, POP, 副程式, 值被改
---
症狀：呼叫副程式後，某個暫存器的值莫名改變。
慣例：EAX、ECX、EDX 是「呼叫者保存」（副程式可以隨意改）；EBX、ESI、EDI、EBP 是「被呼叫者保存」（副程式要還原）。
解法：
・副程式開頭 PUSH 會改到的暫存器，RET 前依相反順序 POP
・MASM：MyProc PROC USES ebx esi edi
・呼叫 C 函式庫（printf 等）後，不要期待 EAX、ECX、EDX 保持原值

## 除錯工具與技巧
id: debug-tools
category: 除錯
arch: 通用
tags: 除錯, debugger, Visual Studio, x64dbg, gdb, DOSBox, debug.exe, 中斷點, 單步, 暫存器視窗
---
・Visual Studio：F9 設中斷點、F10 單步、偵錯 → 視窗 → 暫存器／記憶體／反組譯；監看式可以輸入 eax、[esi] 等
・x64dbg／OllyDbg：Windows 反組譯除錯器，適合看執行檔
・gdb：Linux，layout asm、info registers、stepi、x/16xb $esp
・DOSBox + debug.exe：16 位元，指令 r（看暫存器）、t（單步）、d（看記憶體）、u（反組譯）
・MARS（MIPS）、RARS（RISC-V）：內建單步與暫存器視窗
・Keil μVision（8051/ARM）：模擬器可看 SFR、Port 與計時器
・Irvine32 的 DumpRegs、DumpMem 可以在程式中印出狀態
・本程式的「推導模擬」頁：x86 Intel 語法的逐步追蹤，顯示每一步的推導

技巧：先用小資料測試；每完成一小段就執行一次；對照手算的追蹤表找出第一個不一致的地方。

## 效能最佳化小技巧
id: debug-performance
category: 做法
arch: x86
tags: 最佳化, 效能, performance, XOR清零, LEA, 分支, 對齊, 迴圈展開
---
・XOR REG, REG 清零、TEST REG, REG 判斷零（比 CMP REG, 0 短）
・用 LEA 做加法與小常數乘法，用 SHL/SHR 乘除 2 的冪次
・減少分支：CMOVcc、SETcc；把最常發生的情況放在不跳躍的路徑
・迴圈展開（一次處理多個元素）減少迴圈控制成本
・資料對齊（DWORD 對齊 4、QWORD 對齊 8）
・避免部分暫存器相依（寫 AL 後讀 EAX 可能較慢），可用 MOVZX
・現代 CPU 上可讀性比微小的指令技巧更重要，先確定瓶頸再最佳化
