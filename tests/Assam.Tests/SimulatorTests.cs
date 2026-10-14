using System;
using System.Globalization;
using System.Linq;
using Assam.Core.Samples;
using Assam.Core.Simulation;

namespace Assam.Tests
{
    public static class SimulatorTests
    {
        private static SimulationResult Run(string code, SimulationOptions options = null)
        {
            return new X86Simulator().Run(code, options);
        }

        private static ulong Reg(SimulationResult r, string name)
        {
            var kv = r.FinalRegisters.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase));
            if (kv.Key == null) throw new AssertionException("沒有暫存器 " + name + "；可用：" + string.Join(",", r.FinalRegisters.Select(x => x.Key)));
            return ulong.Parse(kv.Value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static string Sample(string namePart)
        {
            return SampleCode.All.First(s => s.Name.Contains(namePart)).Code;
        }

        [Test]
        public static void MovAdd()
        {
            var r = Run("mov eax, 5\nadd eax, 3");
            Assert.Null(r.Error);
            Assert.Equal(8UL, Reg(r, "EAX"));
            Assert.Equal(2, r.Steps.Count);
            Assert.Contains("0x00000005 + 0x00000003", r.Steps[1].Derivation);
        }

        [Test]
        public static void OverflowFlags8Bit()
        {
            var r = Run("mov al, 7Fh\nadd al, 1");
            Assert.Contains("OF=1", r.FinalFlags);
            Assert.Contains("SF=1", r.FinalFlags);
            Assert.Contains("CF=0", r.FinalFlags);
            Assert.Contains("OF=1：有號 127 + 1 = 128", r.Steps[1].Derivation);
        }

        [Test]
        public static void SubBorrow()
        {
            var r = Run("mov ecx, 10\nsub ecx, 12");
            Assert.Equal(0xFFFFFFFEUL, Reg(r, "ECX"));
            Assert.Contains("CF=1", r.FinalFlags);
        }

        [Test]
        public static void LoopSumSample()
        {
            var r = Run(Sample("LOOP 計算"));
            Assert.Null(r.Error);
            Assert.Equal(55UL, Reg(r, "EAX"));
            Assert.Equal(0UL, Reg(r, "ECX"));
            Assert.True(r.Steps.Any(s => s.Derivation.Contains("跳到「L1」")), "應有跳回 L1 的推導");
        }

        [Test]
        public static void DivideQuotientRemainder()
        {
            var r = Run("mov eax, 100\nxor edx, edx\nmov ebx, 7\ndiv ebx");
            Assert.Equal(14UL, Reg(r, "EAX"));
            Assert.Equal(2UL, Reg(r, "EDX"));
            Assert.Contains("驗算：14 × 7 + 2 = 100", r.Steps.Last().Derivation);
        }

        [Test]
        public static void DivideByZeroIsError()
        {
            var r = Run("mov eax, 1\nxor ebx, ebx\ndiv ebx");
            Assert.NotNull(r.Error);
            Assert.Contains("除以 0", r.Error);
        }

        [Test]
        public static void DivideOverflowIsError()
        {
            var r = Run("mov edx, 1\nmov eax, 0\nmov ecx, 1\ndiv ecx");
            Assert.NotNull(r.Error);
            Assert.Contains("除法溢位", r.Error);
        }

        [Test]
        public static void SignedIdiv()
        {
            var r = Run("mov eax, -100\ncdq\nmov ecx, 7\nidiv ecx");
            Assert.Equal(unchecked((ulong)(uint)-14), Reg(r, "EAX"));
            Assert.Equal(unchecked((ulong)(uint)-2), Reg(r, "EDX"));
        }

        [Test]
        public static void MulHighPart()
        {
            var r = Run("mov eax, 10000h\nmov ebx, 10000h\nmul ebx");
            Assert.Equal(1UL, Reg(r, "EDX"));
            Assert.Equal(0UL, Reg(r, "EAX"));
            Assert.Contains("CF=1", r.FinalFlags);
        }

        [Test]
        public static void ImulThreeOperand()
        {
            var r = Run("mov ebx, -3\nimul eax, ebx, 7");
            Assert.Equal(0xFFFFFFEBUL, Reg(r, "EAX"));
        }

        [Test]
        public static void SarKeepsSign()
        {
            var r = Run("mov al, 0B2h\nsar al, 1");
            Assert.Equal(0xD9UL, Reg(r, "EAX") & 0xFF);
        }

        [Test]
        public static void ShlCarry()
        {
            var r = Run("mov al, 0B2h\nshl al, 1");
            Assert.Equal(0x64UL, Reg(r, "EAX") & 0xFF);
            Assert.Contains("CF=1", r.FinalFlags);
        }

        [Test]
        public static void PushPopOrder()
        {
            var r = Run("push 1\npush 2\npop eax\npop ebx");
            Assert.Equal(2UL, Reg(r, "EAX"));
            Assert.Equal(1UL, Reg(r, "EBX"));
            Assert.Equal(0x0019FF00UL, Reg(r, "ESP"));
        }

        [Test]
        public static void SignedVsUnsignedJumps()
        {
            const string body = "mov eax, -1\ncmp eax, 5\n{0} big\nmov ebx, 1\njmp done\nbig: mov ebx, 2\ndone: nop";
            Assert.Equal(1UL, Reg(Run(string.Format(body, "jg")), "EBX"), "JG：-1 > 5 不成立");
            Assert.Equal(2UL, Reg(Run(string.Format(body, "ja")), "EBX"), "JA：0xFFFFFFFF > 5 成立");
        }

        [Test]
        public static void LeaArithmetic()
        {
            var r = Run("mov ebx, 10\nlea eax, [ebx+ebx*4]");
            Assert.Equal(50UL, Reg(r, "EAX"));
            Assert.Contains("有效位址", r.Steps[1].Derivation);
        }

        [Test]
        public static void PtrAndLittleEndian()
        {
            var r = Run(".data\nval DWORD 12345678h\n.code\nmov al, BYTE PTR val\nmov ax, WORD PTR val+2");
            Assert.Null(r.Error);
            Assert.Equal(0x1234UL, Reg(r, "EAX"));
            Assert.Equal(0x78UL, (ulong)r.Steps[0].Registers.Where(k => k.Key == "EAX").Select(k => ulong.Parse(k.Value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).First());
        }

        [Test]
        public static void IrvineArraySumOutput()
        {
            var r = Run(Sample("陣列加總"));
            Assert.Null(r.Error);
            Assert.Equal(150UL, Reg(r, "EAX"));
            Assert.Equal("Sum = 150\r\n", r.Output);
            Assert.Contains("exit", r.StopReason);
        }

        [Test]
        public static void ArrayMaxSigned()
        {
            var r = Run(Sample("找陣列最大值"));
            Assert.Null(r.Error);
            Assert.Equal(25UL, Reg(r, "EAX"));
        }

        [Test]
        public static void RecursiveFactorial()
        {
            var r = Run(Sample("遞迴階乘"));
            Assert.Null(r.Error);
            Assert.Equal(120UL, Reg(r, "EAX"));
            Assert.Contains("RET", r.StopReason);
            Assert.Equal(0x0019FF00UL, Reg(r, "ESP"));
        }

        [Test]
        public static void StringLengthRepneScasb()
        {
            var r = Run(Sample("字串長度"));
            Assert.Null(r.Error);
            Assert.Equal(8UL, Reg(r, "ECX"));
        }

        [Test]
        public static void Dos16BitHello()
        {
            var r = Run(Sample("DOS"));
            Assert.Null(r.Error);
            Assert.Equal(16, r.Bits);
            Assert.Equal("Hello, DOS!\r\n", r.Output);
            Assert.Contains("4Ch", r.StopReason);
        }

        [Test]
        public static void NasmLinuxHello()
        {
            var r = Run(Sample("NASM（Linux 32"));
            Assert.Null(r.Error);
            Assert.Equal("Hello, Linux!\n", r.Output);
        }

        [Test]
        public static void Nasm64Syscall()
        {
            var r = Run("section .data\nmsg db 'Hi', 10\nlen equ $ - msg\nsection .text\nglobal _start\n_start:\nmov rax, 1\nmov rdi, 1\nmov rsi, msg\nmov rdx, len\nsyscall\nmov rax, 60\nxor rdi, rdi\nsyscall");
            Assert.Null(r.Error);
            Assert.Equal(64, r.Bits);
            Assert.Equal("Hi\n", r.Output);
        }

        [Test]
        public static void SixtyFourBitShift()
        {
            var r = Run("mov rax, 1\nshl rax, 40");
            Assert.Equal(1UL << 40, Reg(r, "RAX"));
        }

        [Test]
        public static void RepMovsbCopies()
        {
            var r = Run(".data\nsrc BYTE 'ABCD'\ndst BYTE 4 DUP(0)\n.code\ncld\nmov esi, OFFSET src\nmov edi, OFFSET dst\nmov ecx, 4\nrep movsb\nmov eax, DWORD PTR dst");
            Assert.Null(r.Error);
            Assert.Equal(0x44434241UL, Reg(r, "EAX"));
        }

        [Test]
        public static void InfiniteLoopStops()
        {
            var r = Run("L: jmp L", new SimulationOptions { MaxSteps = 50 });
            Assert.Equal(50, r.Steps.Count);
            Assert.Contains("最大步數", r.StopReason);
        }

        [Test]
        public static void UnknownLabelError()
        {
            var r = Run("jmp nowhere");
            Assert.NotNull(r.Error);
            Assert.Contains("找不到", r.Error);
        }

        [Test]
        public static void MemoryToMemoryRejected()
        {
            var r = Run(".data\na DWORD 1\nb DWORD 2\n.code\nmov a, b");
            Assert.NotNull(r.Error);
            Assert.Contains("記憶體", r.Error);
        }

        [Test]
        public static void InitialRegisters()
        {
            var errors = new System.Collections.Generic.List<string>();
            var options = new SimulationOptions();
            foreach (var kv in SimulationOptions.ParseRegisterAssignments("EAX=5, ebx = 0x10", errors)) options.InitialRegisters[kv.Key] = kv.Value;
            var r = Run("add eax, ebx", options);
            Assert.Equal(0, errors.Count);
            Assert.Equal(21UL, Reg(r, "EAX"));
        }

        [Test]
        public static void ReadIntFromInput()
        {
            var r = Run("call ReadInt\nmov ebx, eax\ncall ReadInt\nadd eax, ebx\ncall WriteDec", new SimulationOptions { Input = "12 30" });
            Assert.Equal("42", r.Output);
        }

        [Test]
        public static void BasicSampleRunsWithoutError()
        {
            var r = Run(Sample("基本算術"));
            Assert.Null(r.Error);
            Assert.Equal(56UL, Reg(r, "EAX"));
            Assert.Equal(2UL, Reg(r, "EDX"));
        }

        [Test]
        public static void UnsupportedInstructionMessage()
        {
            var r = Run("fld st0");
            Assert.NotNull(r.Error);
            Assert.Contains("不支援", r.Error);
        }

        // ---------------- 反組譯原始碼：匯入表、外部函式 ----------------

        [Test]
        public static void ImportCallRunsSimulatedLibrary()
        {
            var r = Run(".data\nstr_404000 db \"Hi\", 0\n.code\npush offset str_404000\ncall dword ptr [__imp_puts]\nadd esp, 4\npush 3\ncall dword ptr [__imp_ExitProcess]\nmov eax, 99");
            Assert.Null(r.Error);
            Assert.Equal("Hi\r\n", r.Output);
            Assert.Contains("ExitProcess", r.StopReason);
            Assert.Contains("結束碼 3", r.StopReason);
            Assert.False(Reg(r, "EAX") == 99UL, "ExitProcess 之後不應繼續執行");
        }

        [Test]
        public static void UnsimulatedApiReturnsZeroWithoutTouchingStack()
        {
            var r = Run("mov eax, 5\ncall dword ptr [__imp_GetTickCount]\nmov ebx, eax");
            Assert.Null(r.Error);
            Assert.Equal(0UL, Reg(r, "EAX"));
            Assert.Equal(0UL, Reg(r, "EBX"));
            Assert.Equal(0x0019FF00UL, Reg(r, "ESP"), "外部函式不應推入返回位址");
            Assert.True(r.Messages.Any(m => m.Contains("外部函式 GetTickCount 未模擬，假設回傳 0")), string.Join(" | ", r.Messages));
            Assert.Equal(3, r.Steps.Count);
        }

        [Test]
        public static void QwordImportCallIn64BitMode()
        {
            var r = Run(".data\nstr_140004000 db \"x64!\", 0\n.code\nsub rsp, 0x28\nlea rcx, [str_140004000]\ncall qword ptr [__imp_puts]\n" +
                        "mov rax, 7\ncall qword ptr [__imp_GetCurrentProcessId]\nadd rsp, 0x28\nret");
            Assert.Null(r.Error);
            Assert.Equal(64, r.Bits);
            Assert.Equal("x64!\r\n", r.Output);
            Assert.Equal(0UL, Reg(r, "RAX"), "未模擬的 API 回傳 RAX = 0");
            Assert.Contains("RET", r.StopReason);
        }

        [Test]
        public static void StdcallImportCleansArgumentsLikeMinGw()
        {
            // mathlib32.dll 的 ShowMessage（MinGW -O2）：呼叫 stdcall 的 MessageBoxA 之後用 sub esp, 0x10 補回
            string code = ".data\nstr_64C84044 db \"ASSAM\", 0\nstr_64C8404A db \"Hi from DLL\", 0\n.code\nShowMessage:\n" +
                          "    sub esp, 0x1c\n    mov dword ptr [esp+0xc], 0x0\n    mov dword ptr [esp+0x8], offset str_64C84044\n" +
                          "    mov dword ptr [esp+0x4], offset str_64C8404A\n    mov dword ptr [esp], 0x0\n" +
                          "    call dword ptr [__imp_MessageBoxA]         ; USER32.dll!MessageBoxA\n" +
                          "    sub esp, 0x10\n    add esp, 0x1c\n    ret";
            var r = Run(code);
            Assert.Null(r.Error);
            Assert.Contains("Hi from DLL", r.Output);
            Assert.Contains("ASSAM", r.Output);
            Assert.Equal(1UL, Reg(r, "EAX"), "MessageBoxA 模擬按下確定（IDOK = 1）");
            Assert.Contains("RET 16", string.Join("\n", r.Steps.Select(s => s.Derivation)));
            Assert.Contains("RET", r.StopReason);
        }

        [Test]
        public static void ImportThunkJumpActsLikeCallAndRet()
        {
            var r = Run(".data\nmsg db \"Hello\", 0\n.code\nmain:\npush offset msg\ncall j_puts\nadd esp, 4\nmov ebx, 1\nret\n" +
                        "j_puts:\njmp dword ptr [__imp_puts]");
            Assert.Null(r.Error);
            Assert.Equal("Hello\r\n", r.Output);
            Assert.Equal(1UL, Reg(r, "EBX"), "跳板返回後應回到 call 的下一行");
            Assert.Equal(0x0019FF00UL, Reg(r, "ESP"));
        }

        [Test]
        public static void TailJumpToImportAtTopLevelEndsProgram()
        {
            var r = Run(".data\nmsg db \"bye\", 0\n.code\nmain:\nmov eax, 1\njmp dword ptr [__imp_GetLastError]\nmov eax, 2");
            Assert.Null(r.Error);
            Assert.Equal(0UL, Reg(r, "EAX"));
            Assert.Contains("返回作業系統", r.StopReason);
        }

        [Test]
        public static void TargetNameFormsAreNormalized()
        {
            string code = ".data\nfmt db \"%d+%d=%d\", 0Ah, 0\n.code\n" +
                          "push 5\npush 3\npush 2\npush offset fmt\ncall _printf\nadd esp, 16\n" +
                          "push 'A'\ncall [__imp_putchar]\nadd esp, 4\n" +
                          "push 'B'\ncall __imp__putchar\nadd esp, 4\n" +
                          "push 1\npush 2\ncall Foo@8\n" +
                          "push 0\ncall ExitProcess@4";
            var r = Run(code);
            Assert.Null(r.Error);
            Assert.Equal("2+3=5\r\nAB", r.Output);
            Assert.True(r.Messages.Any(m => m.Contains("外部函式 Foo 未模擬")), string.Join(" | ", r.Messages));
            Assert.Contains("ExitProcess", r.StopReason);
            // Foo@8 是 stdcall：返回時清掉 8 位元組，最後只剩 push 0 的 4 位元組
            Assert.Equal(0x0019FEFCUL, Reg(r, "ESP"));
        }

        [Test]
        public static void PrintfFormatting32Bit()
        {
            string code = ".data\nfmt db \"[%5d|%-4s|%c|%x|%08X|%u|%%|%s]\", 0\ns1 db \"ab\", 0\n.code\n" +
                          "push 0\npush 4294967295\npush 0BEEFh\npush 255\npush 'Q'\npush offset s1\npush -42\npush offset fmt\n" +
                          "call dword ptr [__imp_printf]\nadd esp, 32";
            var r = Run(code);
            Assert.Null(r.Error);
            Assert.Equal("[  -42|ab  |Q|ff|0000BEEF|4294967295|%|(null)]", r.Output);
            Assert.Equal((ulong)r.Output.Length, Reg(r, "EAX"), "printf 回傳輸出的字元數");
        }

        [Test]
        public static void PrintfUsesRegistersIn64BitMode()
        {
            // Windows x64：格式字串在 RCX，接著 RDX、R8、R9，第 5 個以後在 [RSP+20h]、[RSP+28h]
            string code = ".data\nfmt db \"%d %s %d %d %lld\", 0\nname db \"x\", 0\n.code\nsub rsp, 0x38\n" +
                          "lea rcx, [fmt]\nmov edx, 1\nlea r8, [name]\nmov r9d, 3\n" +
                          "mov dword ptr [rsp+0x20], 4\nmov qword ptr [rsp+0x28], -3\ncall qword ptr [__imp_printf]\nadd rsp, 0x38";
            var r = Run(code);
            Assert.Null(r.Error);
            Assert.Equal("1 x 3 4 -3", r.Output);
        }

        [Test]
        public static void IndirectCallThroughRegisterLoadedFromIat()
        {
            var r = Run("mov esi, dword ptr [__imp_putchar]\npush 'Z'\ncall esi\nadd esp, 4\nmov edi, dword ptr [__imp_GetTickCount]\ncall edi");
            Assert.Null(r.Error);
            Assert.Equal("Z", r.Output);
            Assert.True(r.Messages.Any(m => m.Contains("GetTickCount")), string.Join(" | ", r.Messages));
        }

        [Test]
        public static void MissingDisassembledSubroutineIsSkipped()
        {
            var r = Run("mov eax, 9\ncall sub_401560\nmov ebx, 1");
            Assert.Null(r.Error);
            Assert.Equal(0UL, Reg(r, "EAX"));
            Assert.Equal(1UL, Reg(r, "EBX"));
            Assert.True(r.Messages.Any(m => m.Contains("sub_401560")), string.Join(" | ", r.Messages));
        }

        [Test]
        public static void CallThroughForeignFunctionPointerIsSkipped()
        {
            var r = Run(".data\nfp dd 12345678h\ntable dd 0, 0\n.code\nmov eax, 5\ncall dword ptr [fp]\nmov ebx, 1");
            Assert.Null(r.Error);
            Assert.Equal(0UL, Reg(r, "EAX"));
            Assert.Equal(1UL, Reg(r, "EBX"));
            Assert.True(r.Messages.Any(m => m.Contains("函式指標")), string.Join(" | ", r.Messages));

            // 跳躍表（方括號內有暫存器）不能當成函式呼叫，仍然要回報錯誤
            var bad = Run(".data\ntable dd 0, 0\n.code\nmov eax, 1\njmp dword ptr [table+eax*4]");
            Assert.NotNull(bad.Error);
        }

        [Test]
        public static void MissingDisassembledJumpTargetHasFriendlyError()
        {
            var r = Run("jmp loc_401578");
            Assert.NotNull(r.Error);
            Assert.Contains("loc_401578", r.Error);
            Assert.Contains("反組譯", r.Error);
        }

        [Test]
        public static void ExitFunctionsHalt()
        {
            foreach (var target in new[] { "dword ptr [__imp_exit]", "_exit", "dword ptr [__imp__exit]", "abort" })
            {
                var r = Run("push 4\ncall " + target + "\nmov ecx, 1");
                Assert.Null(r.Error, target);
                Assert.Equal(0UL, Reg(r, "ECX"), target + " 之後不應繼續執行");
                Assert.Equal(2, r.Steps.Count, target);
            }
        }

        [Test]
        public static void InvokeExternalApiReadsOperands()
        {
            string code = ".386\n.model flat, stdcall\n.data\nmsg BYTE \"Hello\", 0\ncap BYTE \"Title\", 0\nhOut DWORD ?\nwritten DWORD ?\n.code\nmain PROC\n" +
                          "    INVOKE MessageBoxA, NULL, ADDR msg, ADDR cap, MB_OK\n" +
                          "    INVOKE GetStdHandle, STD_OUTPUT_HANDLE\n    mov hOut, eax\n" +
                          "    INVOKE WriteConsoleA, hOut, ADDR msg, 5, ADDR written, 0\n" +
                          "    mov ebx, written\n    INVOKE ExitProcess, 0\nmain ENDP\nEND main";
            var r = Run(code);
            Assert.Null(r.Error);
            Assert.Contains("Title：Hello", r.Output);
            Assert.Contains("Hello", r.Output.Substring(r.Output.IndexOf('\n') + 1));
            Assert.Equal(5UL, Reg(r, "EBX"), "WriteConsoleA 應寫入實際輸出的字元數");
            Assert.Equal(0x0019FF00UL, Reg(r, "ESP"), "INVOKE 外部函式不應改變堆疊");
            Assert.False(r.Messages.Any(m => m.Contains("INVOKE 的參數沒有推入堆疊")), "外部函式的參數直接從運算元讀取");
        }

        [Test]
        public static void UserProcedureWinsOverLibraryName()
        {
            var r = Run("main:\ncall puts\nret\nputs:\nmov eax, 7\nret");
            Assert.Null(r.Error);
            Assert.Equal(7UL, Reg(r, "EAX"));
            Assert.Equal(string.Empty, r.Output);
        }

        [Test]
        public static void WeirdCallTargetsDoNotCrash()
        {
            foreach (var target in new[] { "dword ptr [__imp_]", "[", "dword ptr [", "__imp_@", "@@8", "j_", "qword ptr [rip+__imp_puts]", "far ptr x@4", "[__imp_WS2_32.dll_115]" })
            {
                var r = Run("call " + target + "\njmp " + target);
                Assert.True(r.Steps.Count >= 1, target);
            }
        }
    }
}
