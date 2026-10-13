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
    }
}
