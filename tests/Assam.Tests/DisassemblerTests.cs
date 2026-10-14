using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Assam.Core.Analysis;
using Assam.Core.Binary;
using Assam.Core.Learning;
using Assam.Core.Simulation;

namespace Assam.Tests
{
    /// <summary>整個反組譯流程（載入 → 遞迴下降 → 符號化 → 清單／原始碼）的端到端測試，使用 TestData 中的真實執行檔。</summary>
    public static class DisassemblerTests
    {
        private static DisassemblyResult Disassemble(string file, DisassemblyOptions options = null)
        {
            var binary = BinaryLoader.Load(BinaryLoaderTests.TestFile(file));
            return new Disassembler().Disassemble(binary, options);
        }

        private static string AllText(DisassemblyResult r)
        {
            return new Disassembler().ToListing(r, null, true);
        }

        [Test]
        public static void Pe32EntryFunctionsAndSymbols()
        {
            var r = Disassemble("hello32.exe");
            var b = r.Binary;
            Assert.True(r.Functions.Count >= 5, "函式數 " + r.Functions.Count);
            Assert.True(r.TotalInstructions > 50, "指令數 " + r.TotalInstructions);
            var entry = r.Functions.FirstOrDefault(f => f.Start == b.EntryPoint);
            Assert.NotNull(entry, "應有進入點函式");
            Assert.Equal("entry", entry.Name);
            Assert.Equal(entry, r.FindFunction(b.EntryPoint));
            Assert.True(r.Functions.All(f => f.Lines.Count > 0 && f.End > f.Start), "每個函式都要有指令");
            Assert.True(r.Functions.Select(f => f.Name).Distinct().Count() == r.Functions.Count, "函式名稱不可重複");
            for (int i = 1; i < r.Functions.Count; i++) Assert.True(r.Functions[i - 1].Start < r.Functions[i].Start, "函式要依位址排序");

            string text = AllText(r);
            Assert.Contains("__imp_", text);
            Assert.Contains("sub_", text);
            // MinGW 透過 jmp [__imp_strcpy] 跳板呼叫 strcpy：呼叫者的 API 清單要記到它
            Assert.True(r.Functions.Any(f => f.ImportsUsed.Contains("msvcrt.dll!strcpy")), "應有函式使用 strcpy：" +
                        string.Join(",", r.Functions.SelectMany(f => f.ImportsUsed).Distinct()));
            Assert.True(r.Functions.Any(f => f.StringsReferenced.Any(s => s.Contains("%s %d"))), "應找到 printf 的格式字串");
            Assert.True(r.Functions.Any(f => f.Name.StartsWith("j_", StringComparison.Ordinal)), "應辨識出匯入跳板");
        }

        [Test]
        public static void Pe64RipRelativeIsSymbolized()
        {
            var r = Disassemble("hello64.exe");
            Assert.Equal(64, r.Binary.Bits);
            Assert.Equal(16, r.AddressDigits);
            Assert.True(r.Functions.Count >= 5);
            Assert.NotNull(r.Functions.FirstOrDefault(f => f.Name == "entry"));
            string text = AllText(r);
            Assert.Contains("__imp_", text);
            Assert.DoesNotContain("[rip+0x", text.Split('\n').FirstOrDefault(l => l.Contains("__imp_")) ?? string.Empty);
            Assert.True(r.Functions.Any(f => f.StringsReferenced.Any(s => s.Contains("%s %d"))), "x64 以 lea rcx, [rip+…] 傳入格式字串，也要找得到");
        }

        [Test]
        public static void DllExportsAreNamedFunctions()
        {
            foreach (var name in new[] { "mathlib32.dll", "mathlib64.dll" })
            {
                var r = Disassemble(name);
                var names = r.Functions.Select(f => f.Name).ToList();
                Assert.True(names.Contains("Multiply"), name + "：" + string.Join(",", names.Take(40)));
                Assert.True(names.Contains("Factorial"), name);
                Assert.True(names.Any(n => n.StartsWith("AddNumbers", StringComparison.Ordinal)), name);
                var show = r.Functions.First(f => f.Name == "ShowMessage");
                Assert.True(show.ImportsUsed.Any(i => i.EndsWith("!MessageBoxA", StringComparison.Ordinal)), name + " ShowMessage 應呼叫 MessageBoxA：" + string.Join(",", show.ImportsUsed));
                Assert.True(show.StringsReferenced.Contains("Hi from DLL"), name + " ShowMessage 的字串：" + string.Join(",", show.StringsReferenced));
                var mul = r.Functions.First(f => f.Name == "Multiply");
                Assert.True(mul.Lines.Any(l => l.Mnemonic == "imul"), name + " Multiply 應有 imul");
                Assert.Equal("ret", mul.Lines.Last().Mnemonic);
                Assert.Equal("匯出函式", mul.Origin);
            }
        }

        [Test]
        public static void RecursiveCallIsTracked()
        {
            var r = Disassemble("mathlib32.dll");
            var fact = r.Functions.First(f => f.Name == "Factorial");
            // -O2 可能把遞迴改成迴圈；兩種都可以，但至少要有乘法
            Assert.True(fact.Lines.Any(l => l.Mnemonic == "imul" || l.Mnemonic == "mul"), "Factorial 應有乘法");
        }

        [Test]
        public static void DosComListingAndSource()
        {
            var r = Disassemble("dos_hello.com");
            Assert.Equal(5, r.AddressDigits);
            var start = r.Functions.First(f => f.Start == 0x100);
            Assert.Equal("start", start.Name);
            Assert.Equal("mov", start.Lines[0].Mnemonic);
            Assert.True(start.Lines.Any(l => l.Mnemonic == "int"), "應有 int 21h");
            Assert.True(start.StringsReferenced.Any(s => s.Contains("Hello, DOS!")), "應辨識 DOS 的 $ 結尾字串：" + string.Join(",", start.StringsReferenced));

            string source = new Disassembler().FunctionSource(r, start, false);
            Assert.Contains("org 100h", source);
            Assert.Contains("Hello, DOS!", source);
            Assert.DoesNotContain("0x10c", source.ToLowerInvariant().Replace("org 100h", string.Empty));
        }

        [Test]
        public static void DosComSourceRunsInSimulator()
        {
            var r = Disassemble("dos_hello.com");
            var start = r.Functions.First(f => f.Start == 0x100);
            string source = new Disassembler().FunctionSource(r, start, false);
            var sim = new X86Simulator().Run(source, new SimulationOptions { MaxSteps = 100 });
            Assert.True(sim.Error == null, "模擬錯誤：" + sim.Error + "\n" + source);
            Assert.Contains("Hello, DOS!", sim.Output ?? string.Empty, "模擬輸出應有字串\n" + source);
        }

        [Test]
        public static void DosMzEntry()
        {
            var r = Disassemble("dos_hello_mz.exe");
            var start = r.FindFunction(r.Binary.EntryPoint);
            Assert.NotNull(start);
            Assert.Equal("mov", start.Lines[0].Mnemonic);
            Assert.Contains("cs", start.Lines[0].Operands);
            Assert.True(start.Lines.Count(l => l.Mnemonic == "int") == 2, "兩次 int 21h");
        }

        [Test]
        public static void FunctionSourceIsAnalyzable()
        {
            var r = Disassemble("mathlib32.dll");
            var show = r.Functions.First(f => f.Name == "ShowMessage");
            string source = new Disassembler().FunctionSource(r, show, true);
            Assert.Contains("ShowMessage:", source);
            Assert.Contains("__imp_MessageBoxA", source);
            Assert.Contains("Hi from DLL", source);
            Assert.Contains(".data", source);
            Assert.Contains(".code", source);

            var memory = UserMemoryData.CreateNew();
            var explainer = new CodeExplainer(new ArchitectureDetector(() => memory));
            var explained = explainer.Explain(source);
            Assert.True(explained.Arch == AsmArch.X86Intel, "應辨識為 x86 Intel 語法：" + explained.Arch);
            Assert.True(explained.InstructionCount >= show.Lines.Count, "每條指令都應被解析");
        }

        [Test]
        public static void SimpleFunctionSourceRunsInSimulator()
        {
            var r = Disassemble("mathlib32.dll");
            var mul = r.Functions.First(f => f.Name == "Multiply");
            string source = new Disassembler().FunctionSource(r, mul, false);
            var sim = new X86Simulator().Run(source, new SimulationOptions { MaxSteps = 50 });
            Assert.True(sim.Error == null, "模擬錯誤：" + sim.Error + "\n" + source);
            Assert.True(sim.Steps.Count >= 2, "至少執行兩步");
        }

        [Test]
        public static void ShowMessageSourceRunsInSimulatorWithApiStub()
        {
            var r = Disassemble("mathlib32.dll");
            var show = r.Functions.First(f => f.Name == "ShowMessage");
            string source = new Disassembler().FunctionSource(r, show, false);
            var sim = new X86Simulator().Run(source, new SimulationOptions { MaxSteps = 100 });
            Assert.True(sim.Error == null, "外部 API 應被略過而不是錯誤：" + sim.Error + "\n" + source);
        }

        [Test]
        public static void DisassembleRangeAndListing()
        {
            var r = Disassemble("hello32.exe");
            var d = new Disassembler();
            var lines = d.DisassembleRange(r.Binary, r, r.Binary.EntryPoint, 10);
            Assert.Equal(10, lines.Count);
            Assert.Equal(r.Binary.EntryPoint, lines[0].Address);
            Assert.Equal("entry", lines[0].Label);
            for (int i = 1; i < lines.Count; i++) Assert.Equal(lines[i - 1].Address + (ulong)lines[i - 1].Instruction.Length, lines[i].Address);
            string text = Disassembler.LinesToListing(lines, true, r.AddressDigits);
            Assert.Contains(r.Binary.EntryPoint.ToString("X8"), text);
            Assert.Equal(0, d.DisassembleRange(r.Binary, r, 0x10, 10).Count);
            Assert.Equal(0, d.DisassembleRange(r.Binary, null, ulong.MaxValue, 10).Count);
        }

        [Test]
        public static void ListingFormat()
        {
            var r = Disassemble("mathlib32.dll");
            var d = new Disassembler();
            var mul = r.Functions.First(f => f.Name == "Multiply");
            string withBytes = d.ToListing(r, mul, true);
            string noBytes = d.ToListing(r, mul, false);
            Assert.Contains("Multiply", withBytes);
            Assert.True(withBytes.Length > noBytes.Length, "顯示機器碼時應較長");
            Assert.Contains(mul.Lines[0].HexBytes, withBytes);
            Assert.DoesNotContain(mul.Lines[0].HexBytes + " ", noBytes);
            Assert.Contains("Multiply", Disassembler.FunctionHeader(r, mul));
        }

        [Test]
        public static void ScanUnreachedFindsMoreOrEqual()
        {
            var normal = Disassemble("hello32.exe");
            var scanned = Disassemble("hello32.exe", new DisassemblyOptions { ScanUnreachedCode = true });
            Assert.True(scanned.Functions.Count >= normal.Functions.Count, normal.Functions.Count + " → " + scanned.Functions.Count);
            Assert.True(scanned.TotalInstructions >= normal.TotalInstructions);
        }

        [Test]
        public static void LimitsAreRespected()
        {
            var r = Disassemble("hello64.exe", new DisassemblyOptions { MaxInstructions = 30, ScanUnreachedCode = true });
            Assert.True(r.TotalInstructions <= 30 + 16, "指令數 " + r.TotalInstructions);
            Assert.True(r.Truncated, "達到上限時要標記 Truncated");
            Assert.True(r.Messages.Count > 0, "要告訴使用者被截斷");
        }

        [Test]
        public static void DotNetNativeStub()
        {
            var r = Disassemble("hello_cs.exe");
            var entry = r.FindFunction(r.Binary.EntryPoint);
            Assert.NotNull(entry);
            Assert.Equal("jmp", entry.Lines[0].Mnemonic);
            Assert.Contains("_CorExeMain", entry.Lines[0].Operands + entry.Lines[0].Comment);
        }

        [Test]
        public static void RawCodeAllOpcodesDoNotCrash()
        {
            // 每一種第一個位元組（含前綴組合）都不能讓反組譯器當掉
            foreach (int bits in new[] { 16, 32, 64 })
            {
                var data = new List<byte>();
                for (int op = 0; op < 256; op++) data.AddRange(new byte[] { (byte)op, 0x05, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60 });
                var b = BinaryLoader.LoadRaw(data.ToArray(), "all.bin", bits, 0x1000);
                var r = new Disassembler().Disassemble(b, new DisassemblyOptions { ScanUnreachedCode = true });
                Assert.True(r.TotalInstructions > 0, bits + " 位元");
                new Disassembler().ToListing(r, null, true);
                foreach (var f in r.Functions.Take(20)) new Disassembler().FunctionSource(r, f, true);
            }
        }

        [Test]
        public static void CorruptBinariesDoNotCrashPipeline()
        {
            var original = File.ReadAllBytes(BinaryLoaderTests.TestFile("hello32.exe"));
            var rng = new Random(4321);
            int done = 0;
            for (int round = 0; round < 60; round++)
            {
                var data = (byte[])original.Clone();
                // 破壞 .text 與檔頭中的隨機位元組
                for (int k = 0; k < 40; k++) data[rng.Next(0, Math.Min(data.Length, 0x2000))] = (byte)rng.Next(256);
                data[0] = (byte)'M';
                data[1] = (byte)'Z';
                LoadedBinary b;
                try
                {
                    b = BinaryLoader.Load(data, "fuzz.exe");
                }
                catch (BinaryFormatException)
                {
                    continue;
                }
                var r = new Disassembler().Disassemble(b, new DisassemblyOptions { MaxInstructions = 20000, ScanUnreachedCode = round % 2 == 0 });
                new Disassembler().ToListing(r, null, true);
                foreach (var f in r.Functions.Take(5)) new Disassembler().FunctionSource(r, f, true);
                done++;
            }
            Assert.True(done > 0, "至少要有一些檔案能載入");
        }
    }
}
