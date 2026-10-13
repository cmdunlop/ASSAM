using System;
using System.Linq;
using Assam.Core.Analysis;
using Assam.Core.Instructions;
using Assam.Core.Learning;
using Assam.Core.Samples;

namespace Assam.Tests
{
    public static class AnalysisTests
    {
        // ---------------- 行解析 ----------------

        [Test]
        public static void ParseLabelMnemonicOperandsComment()
        {
            var l = AsmLineParser.ParseLine("L1: mov eax, [ebx+4] ; 讀取", 1, AsmArch.X86Intel);
            Assert.Equal("L1", l.Label);
            Assert.Equal("mov", l.Mnemonic);
            Assert.Equal(2, l.Operands.Count);
            Assert.Equal("[ebx+4]", l.Operands[1]);
            Assert.Equal("讀取", l.Comment);
        }

        [Test]
        public static void ParseDataDefinitionWithSemicolonInString()
        {
            var l = AsmLineParser.ParseLine("msg db 'a;b', 0", 1, AsmArch.X86Intel);
            Assert.Equal("msg", l.Label);
            Assert.True(l.LabelIsName);
            Assert.Equal("db", l.Mnemonic);
            Assert.Equal(2, l.Operands.Count);
            Assert.Null(l.Comment);
        }

        [Test]
        public static void ParsePrefixAndSizeKeyword()
        {
            var rep = AsmLineParser.ParseLine("rep movsb", 1, AsmArch.X86Intel);
            Assert.Equal("rep", rep.Prefix);
            Assert.Equal("movsb", rep.Mnemonic);
            var inc = AsmLineParser.ParseLine("inc dword ptr [x]", 1, AsmArch.X86Intel);
            Assert.Equal("inc", inc.Mnemonic, "inc 不應被當成標籤");
            Assert.Null(inc.Label);
        }

        [Test]
        public static void ParseArchSpecificComments()
        {
            var mips = AsmLineParser.ParseLine("lw $t0, 0($t1) # load", 1, AsmArch.Mips);
            Assert.Equal("load", mips.Comment);
            Assert.Equal(2, mips.Operands.Count);
            var arm = AsmLineParser.ParseLine("mov r0, #5 @ set", 1, AsmArch.Arm);
            Assert.Equal("#5", arm.Operands[1]);
            Assert.Equal("set", arm.Comment);
            var push = AsmLineParser.ParseLine("push {r4-r7, lr}", 1, AsmArch.Arm);
            Assert.Equal(1, push.Operands.Count);
        }

        // ---------------- 指令表 ----------------

        [Test]
        public static void InstructionSetsLoaded()
        {
            foreach (var key in new[] { "x86", "arm", "arm64", "mips", "riscv", "i8051", "avr" })
                Assert.NotNull(InstructionCatalog.Get(key), "缺少指令表 " + key);
            var x86 = InstructionCatalog.Get("x86");
            Assert.True(x86.All.Count >= 150, "x86 指令數 " + x86.All.Count);
            Assert.NotNull(x86.Find("MOV"));
            Assert.NotNull(x86.Find("jz"));
            Assert.NotNull(x86.Find("movl"), "AT&T 字尾");
            Assert.NotNull(x86.Find("movzbl"), "AT&T movzbl");
            var arm = InstructionCatalog.Get("arm");
            Assert.Equal("b", arm.Find("bne").PrimaryMnemonic);
            Assert.Equal("bl", arm.Find("bleq").PrimaryMnemonic);
            Assert.Equal("add", arm.Find("addseq").PrimaryMnemonic);
            Assert.Equal("sub", arm.Find("subs").PrimaryMnemonic);
            Assert.NotNull(InstructionCatalog.Get("arm64").Find("b.ne"));
            Assert.NotNull(InstructionCatalog.Get("i8051").Find("djnz"));
        }

        [Test]
        public static void ActionTemplates()
        {
            var add = InstructionCatalog.Get("arm").Find("add");
            Assert.Equal("r0 ← r1 + r2", add.FormatAction(new[] { "r0", "r1", "r2" }));
            Assert.Equal("r0 ← r0 + #1", add.FormatAction(new[] { "r0", "#1" }));
            var mov = InstructionCatalog.Get("x86").Find("mov");
            Assert.Equal("EAX ← 5", mov.FormatAction(new[] { "EAX", "5" }));
        }

        // ---------------- 架構辨識 ----------------

        private static ArchitectureDetector NewDetector(UserMemoryData memory = null)
        {
            var m = memory ?? UserMemoryData.CreateNew();
            return new ArchitectureDetector(() => m);
        }

        [Test]
        public static void DetectSingleSnippets()
        {
            var d = NewDetector();
            Assert.Equal(AsmArch.X86Intel, d.Detect("mov eax, ebx\nadd eax, 5").Best);
            Assert.Equal(AsmArch.X86Att, d.Detect("movl %eax, %ebx\naddl $5, %eax").Best);
            Assert.Equal(AsmArch.Mips, d.Detect("lw $t0, 4($sp)\naddi $t0, $t0, 1").Best);
            Assert.Equal(AsmArch.Arm, d.Detect("ldr r0, [r1, #4]\nadd r0, r0, r2\nbx lr").Best);
            Assert.Equal(AsmArch.Arm64, d.Detect("stp x29, x30, [sp, #-16]!\nmov x29, sp").Best);
            Assert.Equal(AsmArch.I8051, d.Detect("mov a, #30h\ndjnz r7, loop").Best);
            Assert.Equal(AsmArch.Avr, d.Detect("ldi r16, 0xFF\nout PORTB, r16").Best);
            Assert.Equal(AsmArch.RiscV, d.Detect("addi a0, a0, 1\nli a7, 10\necall").Best);
            Assert.Equal(AsmArch.Unknown, d.Detect("這不是程式").Best);
        }

        [Test]
        public static void DetectAllSamples()
        {
            var d = NewDetector();
            foreach (var s in SampleCode.All)
            {
                var r = d.Detect(s.Code);
                Assert.Equal(s.Arch, r.Best, s.Name + "（" + r.Describe() + "）");
                Assert.True(r.Confidence > 0.6, s.Name + " 信心太低：" + r.Confidence);
            }
        }

        [Test]
        public static void DetectorLearnsFromCorrection()
        {
            var memory = UserMemoryData.CreateNew();
            var d = NewDetector(memory);
            const string code = "mov r0, #5\nmov r1, #6";
            var before = d.Detect(code);
            var target = before.Best == AsmArch.I8051 ? AsmArch.Arm : AsmArch.I8051;
            Assert.True(d.Learn(code, target));
            Assert.Equal(target, d.Detect(code).Best);
            Assert.Equal(1, memory.ArchSamples.Count);
            Assert.True(memory.ArchWeightDeltas.Count > 0);
            // 學習一個片段不應該破壞其他架構的判斷
            Assert.Equal(AsmArch.X86Intel, d.Detect("mov eax, ebx\nadd eax, 5").Best);
        }

        // ---------------- 程式解說 ----------------

        private static CodeExplainer NewExplainer()
        {
            return new CodeExplainer(NewDetector());
        }

        private static string Sample(string namePart)
        {
            return SampleCode.All.First(s => s.Name.Contains(namePart)).Code;
        }

        [Test]
        public static void ExplainIrvineSample()
        {
            var e = NewExplainer().Explain(Sample("陣列加總"));
            Assert.Equal(AsmArch.X86Intel, e.Arch);
            string text = e.ToText();
            Assert.Contains("OFFSET", text);
            Assert.Contains("迴圈：第", text);
            Assert.Contains("WriteString", text);
            Assert.Equal(0, e.Warnings.Count, string.Join(" | ", e.Warnings));
            var add = e.Lines.First(l => l.Code.Contains("add  eax, [esi]"));
            Assert.Contains("暫存器間接定址", add.Explanation);
            Assert.Equal("eax ← eax + [esi]", add.Action);
        }

        [Test]
        public static void ExplainWarnsAboutCommonMistakes()
        {
            var e = NewExplainer().Explain(".data\na DWORD 1\nb DWORD 2\n.code\nmov a, b\nmov eax, bx\nmov ebx, 3\ndiv ebx\nexit", AsmArch.X86Intel);
            string all = string.Join("\n", e.Warnings);
            Assert.Contains("記憶體 → 記憶體", all);
            Assert.Contains("大小不一致", all);
            Assert.Contains("EDX", all);
        }

        [Test]
        public static void ExplainDosServices()
        {
            var e = NewExplainer().Explain(Sample("DOS"));
            string text = e.ToText();
            Assert.Contains("AH=09h", text);
            Assert.Contains("'$' 結尾", text);
            Assert.Contains("AH=4Ch", text);
        }

        [Test]
        public static void Explain8051HashHint()
        {
            var e = NewExplainer().Explain("MOV A, 30H\nADD A, #5", AsmArch.I8051);
            Assert.Contains("#30H", string.Join("\n", e.Notes));
        }

        [Test]
        public static void ExplainAttReversesOperands()
        {
            var e = NewExplainer().Explain(Sample("AT&T"));
            Assert.Equal(AsmArch.X86Att, e.Arch);
            var line = e.Lines.First(l => l.Code.Contains("movl  %esp, %ebp"));
            Assert.Equal("%ebp ← %esp", line.Action);
        }

        [Test]
        public static void ExplainMipsSyscall()
        {
            var e = NewExplainer().Explain(Sample("MIPS"));
            Assert.Equal(AsmArch.Mips, e.Arch);
            Assert.Contains("印出整數", e.ToText());
        }

        [Test]
        public static void ExplainArmPostIndex()
        {
            var e = NewExplainer().Explain(Sample("ARM（32"));
            Assert.Contains("後索引", e.ToText());
        }
    }
}
