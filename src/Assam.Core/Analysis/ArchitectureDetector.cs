using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Assam.Core.Instructions;
using Assam.Core.Learning;

namespace Assam.Core.Analysis
{
    public sealed class ArchDetectionResult
    {
        public ArchDetectionResult()
        {
            Ranking = new List<KeyValuePair<AsmArch, double>>();
            Evidence = new List<string>();
        }

        public AsmArch Best { get; set; }
        public double Confidence { get; set; }
        public List<KeyValuePair<AsmArch, double>> Ranking { get; private set; }
        public List<string> Evidence { get; private set; }
        public int InstructionLines { get; set; }

        public string Describe()
        {
            if (Best == AsmArch.Unknown) return "無法判斷架構（找不到可辨識的指令）。";
            var parts = Ranking.Take(3).Where(r => r.Value >= 0.01)
                               .Select(r => AsmArchInfo.ShortName(r.Key) + " " + (r.Value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%");
            string s = "判斷為 " + AsmArchInfo.DisplayName(Best) + "（信心 " +
                       (Confidence * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%）";
            s += "\r\n候選：" + string.Join("、", parts);
            if (Evidence.Count > 0) s += "\r\n主要依據：" + string.Join("；", Evidence);
            return s;
        }
    }

    /// <summary>
    /// 組合語言架構辨識器：以「特徵 × 權重」的線性模型計分（可解釋），
    /// 使用者更正時以 Passive-Aggressive 線上學習法調整權重，結果存在本機記憶中。
    /// </summary>
    public sealed class ArchitectureDetector
    {
        private static readonly Regex IdentifierRegex = new Regex(@"(?<![a-z0-9_.])(?<p>[%$@#=]?)(?<id>[a-z_][a-z0-9_]*)", RegexOptions.Compiled);
        private static readonly Regex HexHRegex = new Regex(@"(?<![a-z0-9_$])[0-9][0-9a-f]*h(?![a-z0-9_])", RegexOptions.Compiled);
        private static readonly Regex Hex0xRegex = new Regex(@"(?<![a-z0-9_])0x[0-9a-f]+", RegexOptions.Compiled);
        private static readonly Regex DollarNumRegex = new Regex(@"\$-?[0-9]", RegexOptions.Compiled);
        private static readonly Regex HashNumRegex = new Regex(@"#\s*-?[0-9$'(]", RegexOptions.Compiled);
        private static readonly Regex OffsetRegRegex = new Regex(@"-?[0-9a-z_]*\(\s*[%$]?[a-z][a-z0-9]*\s*(,[^)]*)?\)", RegexOptions.Compiled);
        private static readonly Regex AvrPointerRegex = new Regex(@"^-?[xyz]\+?$", RegexOptions.Compiled);
        private static readonly Regex ArmShiftRegex = new Regex(@"^(lsl|lsr|asr|ror)\s+#", RegexOptions.Compiled);
        private static readonly Regex BitAddressRegex = new Regex(@"(?<![a-z0-9_])p[0-3]\.[0-7](?![0-9])", RegexOptions.Compiled);

        private static readonly HashSet<string> PlainDirectives = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "db", "dw", "dd", "dq", "dt", "byte", "sbyte", "word", "sword", "dword", "sdword", "qword", "tbyte",
            "real4", "real8", "proc", "endp", "equ", "=", "org", "end", "include", "includelib", "invoke", "segment",
            "ends", "assume", "section", "global", "extern", "externdef", "bits", "times", "resb", "resw", "resd",
            "resq", "exit", "macro", "endm", "struct", "struc", "model", "title", "option", "cseg", "dseg", "using",
            "ds", "bit", "sbit", "data", "code", "xdata", "idata", "area", "export", "preserve8", "thumb", "entry",
            "align", "default", "label", "textequ", "set", "public", "dup"
        };

        private static readonly Dictionary<AsmArch, Dictionary<string, double>> BaseWeights = BuildBaseWeights();
        private static readonly HashSet<string> AllRegisters = new HashSet<string>(
            BaseWeights.Values.SelectMany(d => d.Keys).Where(k => k.StartsWith("r:", StringComparison.Ordinal)).Select(k => k.Substring(2)),
            StringComparer.Ordinal);

        private readonly Func<UserMemoryData> _memory;

        public ArchitectureDetector(Func<UserMemoryData> memory)
        {
            _memory = memory;
        }

        // ------------------------------------------------------------------
        // 特徵擷取
        // ------------------------------------------------------------------

        public sealed class CodeFeatures
        {
            public CodeFeatures()
            {
                Counts = new Dictionary<string, double>(StringComparer.Ordinal);
                Mnemonics = new List<string>();
            }

            public Dictionary<string, double> Counts { get; private set; }
            public List<string> Mnemonics { get; private set; }
            public int FeatureLines { get; set; }

            public void Add(string feature, double amount = 1.0)
            {
                double c;
                Counts.TryGetValue(feature, out c);
                Counts[feature] = c + amount;
            }
        }

        public static CodeFeatures ExtractFeatures(string code)
        {
            var f = new CodeFeatures();
            foreach (var line in AsmLineParser.Parse(code ?? string.Empty, AsmArch.Unknown))
            {
                if (!line.HasMnemonic) continue;
                f.FeatureLines++;
                string m = line.MnemonicLower;
                if (m.StartsWith(".", StringComparison.Ordinal) || PlainDirectives.Contains(m))
                {
                    f.Add("d:" + m);
                }
                else
                {
                    f.Add("m:" + m);
                    f.Mnemonics.Add(m);
                }
                if (!string.IsNullOrEmpty(line.Prefix)) f.Add("m:" + line.Prefix.ToLowerInvariant());

                string ops = (line.OperandText ?? string.Empty).ToLowerInvariant();
                if (ops.Length == 0) continue;
                AnalyzeOperandText(ops, line.Operands, f);
            }
            return f;
        }

        private static void AnalyzeOperandText(string ops, List<string> operands, CodeFeatures f)
        {
            // 字串常數內容不分析
            string text = Regex.Replace(ops, "'[^']*'|\"[^\"]*\"", " ");

            foreach (Match match in IdentifierRegex.Matches(text))
            {
                string p = match.Groups["p"].Value;
                string id = match.Groups["id"].Value;
                switch (p)
                {
                    case "%":
                        f.Add(AllRegisters.Contains(id) ? "p:%reg" : "p:%rel");
                        if (AllRegisters.Contains(id)) f.Add("r:" + id);
                        break;
                    case "$":
                        if (IsMipsRegisterName(id)) { f.Add("p:$reg"); f.Add("r:" + id); }
                        else f.Add("p:$sym");
                        break;
                    case "#":
                        f.Add("p:#imm");
                        break;
                    case "@":
                        if (id == "data") f.Add("p:@data");
                        else if (id == "r0" || id == "r1" || id == "dptr" || id == "a") f.Add("p:@ind");
                        break;
                    case "=":
                        f.Add("p:=lit");
                        break;
                    default:
                        if (AllRegisters.Contains(id)) f.Add("r:" + id);
                        else if (id == "ptr" || id == "offset" || id == "lengthof" || id == "sizeof") f.Add("p:" + (id == "ptr" ? "ptr" : "offset"));
                        else if (id == "dword" || id == "word" || id == "byte" || id == "qword") f.Add("p:sizekw");
                        break;
                }
            }

            foreach (Match unused in HexHRegex.Matches(text)) f.Add("p:hhex");
            foreach (Match unused in Hex0xRegex.Matches(text)) f.Add("p:0x");
            foreach (Match unused in DollarNumRegex.Matches(text)) f.Add("p:$num");
            foreach (Match unused in HashNumRegex.Matches(text)) f.Add("p:#imm");
            foreach (Match unused in OffsetRegRegex.Matches(text)) f.Add("p:off(reg)");
            foreach (Match unused in BitAddressRegex.Matches(text)) f.Add("p:bitaddr");
            if (text.IndexOf('[') >= 0) f.Add("p:[]");
            if (text.IndexOf("]!", StringComparison.Ordinal) >= 0) f.Add("p:writeback");
            if (text.IndexOf('{') >= 0) f.Add("p:reglist");

            foreach (var op in operands)
            {
                string o = op.Trim().ToLowerInvariant();
                if (AvrPointerRegex.IsMatch(o)) f.Add("p:avr_ptr");
                if (ArmShiftRegex.IsMatch(o)) f.Add("p:armshift");
            }
        }

        private static bool IsMipsRegisterName(string id)
        {
            if (id.Length == 0) return false;
            switch (id)
            {
                case "zero": case "at": case "gp": case "sp": case "fp": case "ra": case "k0": case "k1":
                case "v0": case "v1": case "a0": case "a1": case "a2": case "a3":
                    return true;
            }
            if ((id[0] == 't' || id[0] == 's') && id.Length == 2 && char.IsDigit(id[1])) return true;
            return false;
        }

        // ------------------------------------------------------------------
        // 計分
        // ------------------------------------------------------------------

        public ArchDetectionResult Detect(string code)
        {
            var features = ExtractFeatures(code);
            var result = new ArchDetectionResult { InstructionLines = features.Mnemonics.Count };
            if (features.FeatureLines == 0)
            {
                result.Best = AsmArch.Unknown;
                return result;
            }

            var scores = new Dictionary<AsmArch, double>();
            foreach (var arch in AsmArchInfo.All)
                scores[arch] = Score(arch, features);

            double temperature = Math.Max(1.5, 0.35 * features.FeatureLines);
            double max = scores.Values.Max();
            double sum = scores.Values.Sum(s => Math.Exp((s - max) / temperature));
            foreach (var kv in scores.OrderByDescending(k => k.Value))
                result.Ranking.Add(new KeyValuePair<AsmArch, double>(kv.Key, Math.Exp((kv.Value - max) / temperature) / sum));

            var best = result.Ranking[0];
            if (max <= 0.5)
            {
                result.Best = AsmArch.Unknown;
                result.Confidence = 0;
                return result;
            }
            result.Best = best.Key;
            result.Confidence = best.Value;
            result.Evidence.AddRange(ExplainEvidence(best.Key, features));
            return result;
        }

        public double Score(AsmArch arch, CodeFeatures features)
        {
            double score = MnemonicScore(arch, features.Mnemonics);
            Dictionary<string, double> baseW = BaseWeights[arch];
            Dictionary<string, double> delta = GetDeltas(arch, false);
            foreach (var kv in features.Counts)
            {
                double w = 0, d = 0;
                baseW.TryGetValue(kv.Key, out w);
                if (delta != null) delta.TryGetValue(kv.Key, out d);
                score += (w + d) * kv.Value;
            }
            return score;
        }

        private static double MnemonicScore(AsmArch arch, List<string> mnemonics)
        {
            var set = InstructionCatalog.Get(AsmArchInfo.InstructionSetKey(arch));
            if (set == null) return 0;
            double score = 0;
            foreach (var m in mnemonics)
            {
                bool exact = set.IsExact(m);
                bool known = exact || set.IsKnown(m);
                if (arch == AsmArch.X86Intel) score += exact ? 1.0 : known ? -0.3 : -0.7;
                else if (arch == AsmArch.X86Att) score += exact ? 0.8 : known ? 1.2 : -0.7;
                else score += known ? 1.0 : -0.7;
            }
            return score;
        }

        private IEnumerable<string> ExplainEvidence(AsmArch arch, CodeFeatures features)
        {
            var baseW = BaseWeights[arch];
            var delta = GetDeltas(arch, false);
            var contributions = new List<KeyValuePair<string, double>>();
            foreach (var kv in features.Counts)
            {
                double w = 0, d = 0;
                baseW.TryGetValue(kv.Key, out w);
                if (delta != null) delta.TryGetValue(kv.Key, out d);
                double c = (w + d) * kv.Value;
                if (c > 0.4) contributions.Add(new KeyValuePair<string, double>(kv.Key, c));
            }

            var set = InstructionCatalog.Get(AsmArchInfo.InstructionSetKey(arch));
            int known = set == null ? 0 : features.Mnemonics.Count(m => set.IsKnown(m));
            var list = new List<string>();
            if (features.Mnemonics.Count > 0)
                list.Add(known + "/" + features.Mnemonics.Count + " 個指令屬於此架構的指令集");
            foreach (var kv in contributions.OrderByDescending(c => c.Value).Take(5))
                list.Add(FeatureText(kv.Key) + (delta != null && delta.ContainsKey(kv.Key) ? "（含學習記憶）" : string.Empty));
            return list;
        }

        public static string FeatureText(string feature)
        {
            string key = feature.Substring(2);
            switch (feature)
            {
                case "p:%reg": return "暫存器使用 % 前綴（AT&T 語法）";
                case "p:%rel": return "使用 %hi/%lo 等重定位運算子";
                case "p:$reg": return "暫存器使用 $ 前綴（MIPS）";
                case "p:$num": return "立即值使用 $ 前綴";
                case "p:$sym": return "符號使用 $ 前綴";
                case "p:#imm": return "立即值使用 # 前綴";
                case "p:@ind": return "使用 @R0/@DPTR 間接定址（8051）";
                case "p:@data": return "使用 @data（MASM 16 位元）";
                case "p:=lit": return "使用 =常數（ARM LDR 虛擬指令）";
                case "p:ptr": return "使用 PTR 指定運算元大小（MASM）";
                case "p:offset": return "使用 OFFSET/LENGTHOF/SIZEOF 運算子（MASM）";
                case "p:sizekw": return "使用 BYTE/WORD/DWORD 大小關鍵字";
                case "p:hhex": return "十六進位以 h 結尾（例如 21h）";
                case "p:0x": return "十六進位以 0x 開頭";
                case "p:[]": return "使用 [ ] 表示記憶體運算元";
                case "p:writeback": return "使用 ]! 回寫定址（ARM）";
                case "p:reglist": return "使用 { } 暫存器清單（ARM）";
                case "p:off(reg)": return "使用 位移(暫存器) 的記憶體寫法";
                case "p:avr_ptr": return "使用 X/Y/Z 指標暫存器（AVR）";
                case "p:armshift": return "使用桶型移位運算元（ARM）";
                case "p:bitaddr": return "使用 P1.0 這類位元位址（8051）";
            }
            if (feature.StartsWith("r:", StringComparison.Ordinal)) return "暫存器 " + key.ToUpperInvariant();
            if (feature.StartsWith("d:", StringComparison.Ordinal)) return "虛擬指令 " + key.ToUpperInvariant();
            if (feature.StartsWith("m:", StringComparison.Ordinal)) return "指令 " + key.ToUpperInvariant();
            return feature;
        }

        // ------------------------------------------------------------------
        // 學習（Passive-Aggressive 線上學習）
        // ------------------------------------------------------------------

        /// <summary>使用者告訴我們「這段程式其實是 actual 架構」，調整權重直到能正確分類。回傳是否有調整。</summary>
        public bool Learn(string code, AsmArch actual)
        {
            if (actual == AsmArch.Unknown) return false;
            var memory = _memory();
            if (memory == null) return false;

            var features = ExtractFeatures(code);
            if (features.Counts.Count == 0) return false;

            double norm = features.Counts.Values.Sum(v => v * v);
            bool changed = false;
            const double requiredMargin = 3.0;

            for (int iteration = 0; iteration < 10; iteration++)
            {
                double target = Score(actual, features);
                AsmArch rival = AsmArch.Unknown;
                double rivalScore = double.NegativeInfinity;
                foreach (var arch in AsmArchInfo.All)
                {
                    if (arch == actual) continue;
                    double s = Score(arch, features);
                    if (s > rivalScore) { rivalScore = s; rival = arch; }
                }

                double loss = requiredMargin - (target - rivalScore);
                if (loss <= 0) break;

                double tau = Math.Min(5.0, loss / (2 * norm));
                var up = GetDeltas(actual, true);
                var down = GetDeltas(rival, true);
                foreach (var kv in features.Counts)
                {
                    Adjust(up, kv.Key, tau * kv.Value);
                    Adjust(down, kv.Key, -tau * kv.Value);
                }
                changed = true;
            }

            memory.ArchSamples.Add(new LabeledSample
            {
                Text = code.Length > 2000 ? code.Substring(0, 2000) : code,
                Label = actual.ToString(),
                CreatedAt = UserMemoryData.Now()
            });
            return changed;
        }

        private static void Adjust(Dictionary<string, double> map, string feature, double amount)
        {
            double v;
            map.TryGetValue(feature, out v);
            v = Math.Max(-20, Math.Min(20, v + amount));
            if (Math.Abs(v) < 1e-9) map.Remove(feature);
            else map[feature] = v;
        }

        private Dictionary<string, double> GetDeltas(AsmArch arch, bool create)
        {
            var memory = _memory != null ? _memory() : null;
            if (memory == null) return null;
            Dictionary<string, double> map;
            if (memory.ArchWeightDeltas.TryGetValue(arch.ToString(), out map)) return map;
            if (!create) return null;
            map = new Dictionary<string, double>(StringComparer.Ordinal);
            memory.ArchWeightDeltas[arch.ToString()] = map;
            return map;
        }

        // ------------------------------------------------------------------
        // 初始權重（專家知識）
        // ------------------------------------------------------------------

        private static Dictionary<AsmArch, Dictionary<string, double>> BuildBaseWeights()
        {
            var w = new Dictionary<AsmArch, Dictionary<string, double>>();
            foreach (var a in AsmArchInfo.All) w[a] = new Dictionary<string, double>(StringComparer.Ordinal);

            // ---- x86 暫存器 ----
            var x86Regs = new List<string>
            {
                "al", "ah", "bl", "bh", "cl", "ch", "dl", "dh", "ax", "bx", "cx", "dx", "si", "di", "bp",
                "eax", "ebx", "ecx", "edx", "esi", "edi", "esp", "ebp", "rax", "rbx", "rcx", "rdx", "rsi",
                "rdi", "rsp", "rbp", "sil", "dil", "spl", "bpl", "cs", "ds", "es", "ss", "fs", "gs", "eip", "rip"
            };
            for (int i = 8; i <= 15; i++)
            {
                x86Regs.Add("r" + i + "d");
                x86Regs.Add("r" + i + "w");
                x86Regs.Add("r" + i + "b");
            }
            for (int i = 0; i < 16; i++) x86Regs.Add("xmm" + i);
            foreach (var r in x86Regs)
            {
                w[AsmArch.X86Intel]["r:" + r] = 2.0;
                w[AsmArch.X86Att]["r:" + r] = 1.0;
            }
            for (int i = 8; i <= 15; i++)
            {
                w[AsmArch.X86Intel]["r:r" + i] = 0.3;
                w[AsmArch.X86Att]["r:r" + i] = 0.3;
            }
            w[AsmArch.X86Intel]["r:sp"] = 0.3;

            Set(w[AsmArch.X86Intel], "p:ptr", 3, "p:offset", 3, "p:sizekw", 1.5, "p:hhex", 1.2, "p:[]", 0.8, "p:@data", 5,
                "p:%reg", -4, "p:$reg", -4, "p:#imm", -3, "p:off(reg)", -2, "p:@ind", -2, "p:$num", -2, "p:reglist", -3,
                "p:writeback", -3, "p:=lit", -2, "p:bitaddr", -2, "p:avr_ptr", -1,
                "d:.model", 4, "d:.code", 3, "d:.stack", 3, "d:.data", 1, "d:.data?", 2, "d:.386", 4, "d:.486", 4, "d:.586", 3,
                "d:.686", 3, "d:.686p", 3, "d:.const", 2, "d:proc", 3, "d:endp", 3, "d:invoke", 3, "d:include", 1,
                "d:includelib", 3, "d:db", 1.5, "d:dw", 1.5, "d:dd", 2, "d:dq", 1, "d:byte", 2, "d:word", 1, "d:dword", 2,
                "d:sdword", 2, "d:sbyte", 2, "d:qword", 1, "d:section", 1.5, "d:global", 1, "d:extern", 1, "d:segment", 2,
                "d:assume", 3, "d:bits", 2, "d:resb", 2, "d:resd", 2, "d:resw", 2, "d:times", 2, "d:equ", 0.3, "d:exit", 2,
                "d:end", 0.3, "d:dup", 1, "m:rep", 1, "m:repne", 1, "m:repe", 1);

            Set(w[AsmArch.X86Att], "p:%reg", 3, "p:$num", 2.5, "p:$sym", 1.5, "p:off(reg)", 1.5, "p:[]", -3, "p:ptr", -4,
                "p:offset", -3, "p:hhex", -1, "p:#imm", -3, "p:reglist", -3, "p:@ind", -2, "p:sizekw", -1,
                "d:.globl", 1.5, "d:.text", 0.8, "d:.data", 0.5, "d:.section", 0.8, "d:.long", 1.5, "d:.quad", 1,
                "d:.asciz", 1, "d:.string", 1, "d:.type", 1, "d:.size", 1, "d:.intel_syntax", -5, "d:.att_syntax", 5,
                "d:.ascii", 0.5, "d:.byte", 0.3, "d:.word", 0.3, "d:.cfi_startproc", 2, "d:.cfi_endproc", 2);

            // ---- ARM 32 ----
            for (int i = 0; i <= 12; i++) w[AsmArch.Arm]["r:r" + i] = 1.0;
            Set(w[AsmArch.Arm], "r:r13", 1.5, "r:r14", 1.5, "r:r15", 1.5, "r:sp", 0.5, "r:lr", 3, "r:pc", 2, "r:cpsr", 3,
                "r:spsr", 3, "r:apsr", 3, "r:fp", 0.5, "r:ip", 0.3,
                "p:#imm", 1.5, "p:[]", 0.8, "p:reglist", 3, "p:writeback", 2.5, "p:=lit", 3, "p:armshift", 3,
                "p:%reg", -4, "p:$reg", -4, "p:ptr", -4, "p:off(reg)", -2, "p:@ind", -2, "p:hhex", -0.5, "p:offset", -3,
                "d:.global", 0.5, "d:.text", 0.3, "d:.data", 0.3, "d:.word", 0.8, "d:.thumb", 5, "d:.arm", 5,
                "d:.syntax", 4, "d:.thumb_func", 5, "d:.cpu", 2, "d:.fpu", 2, "d:area", 4, "d:export", 2,
                "d:preserve8", 4, "d:thumb", 4, "d:entry", 2, "d:.asciz", 0.5, "d:.align", 0.3, "d:.equ", 0.3,
                "d:.balign", 0.3, "d:.type", 0.3, "d:end", 0.2);

            // ---- ARM64 ----
            for (int i = 0; i <= 30; i++)
            {
                w[AsmArch.Arm64]["r:x" + i] = 2.0;
                w[AsmArch.Arm64]["r:w" + i] = 2.5;
            }
            Set(w[AsmArch.Arm64], "r:xzr", 4, "r:wzr", 4, "r:sp", 0.5, "r:lr", 0.5, "r:fp", 0.3,
                "p:#imm", 1.0, "p:[]", 0.8, "p:writeback", 1.5, "p:=lit", 1, "p:%reg", -4, "p:$reg", -4, "p:ptr", -4,
                "p:off(reg)", -2, "p:@ind", -2, "p:reglist", -2, "p:offset", -3,
                "d:.global", 0.5, "d:.text", 0.3, "d:.data", 0.3, "d:.asciz", 0.5, "d:.quad", 0.5, "d:.arch", 2,
                "d:.xword", 3, "d:.align", 0.3);

            // ---- MIPS ----
            foreach (var r in new[] { "zero", "at", "v0", "v1", "a0", "a1", "a2", "a3", "k0", "k1", "gp", "sp", "fp", "ra" })
                w[AsmArch.Mips]["r:" + r] = 0.3;
            for (int i = 0; i <= 9; i++) w[AsmArch.Mips]["r:t" + i] = 0.3;
            for (int i = 0; i <= 8; i++) w[AsmArch.Mips]["r:s" + i] = 0.3;
            Set(w[AsmArch.Mips], "p:$reg", 4, "p:off(reg)", 2, "p:%rel", 1, "p:#imm", -3, "p:[]", -3, "p:%reg", -4, "p:ptr", -4,
                "p:hhex", -1, "p:offset", -3,
                "d:.data", 0.5, "d:.text", 0.5, "d:.globl", 0.5, "d:.asciiz", 3, "d:.word", 0.8, "d:.space", 0.5,
                "d:.align", 0.3, "d:.ascii", 0.3, "d:.byte", 0.3, "d:.half", 1);

            // ---- RISC-V ----
            foreach (var r in new[] { "zero", "ra", "sp", "gp", "tp", "fp" }) w[AsmArch.RiscV]["r:" + r] = 1.2;
            for (int i = 0; i <= 6; i++) w[AsmArch.RiscV]["r:t" + i] = 1.2;
            for (int i = 0; i <= 11; i++) w[AsmArch.RiscV]["r:s" + i] = 1.2;
            for (int i = 0; i <= 7; i++) w[AsmArch.RiscV]["r:a" + i] = 1.2;
            for (int i = 0; i <= 31; i++) w[AsmArch.RiscV]["r:x" + i] = 1.0;
            Set(w[AsmArch.RiscV], "p:off(reg)", 2, "p:%rel", 2, "p:$reg", -4, "p:#imm", -3, "p:[]", -3, "p:%reg", -3, "p:ptr", -4,
                "p:hhex", -1, "p:offset", -3,
                "d:.data", 0.5, "d:.text", 0.5, "d:.globl", 0.5, "d:.global", 0.3, "d:.asciz", 1.0, "d:.string", 1,
                "d:.word", 0.5, "d:.section", 0.3, "d:.option", 3, "d:.attribute", 2, "d:.dword", 2);

            // ---- 8051 ----
            for (int i = 0; i <= 7; i++) w[AsmArch.I8051]["r:r" + i] = 1.0;
            Set(w[AsmArch.I8051], "r:a", 1.2, "r:acc", 3, "r:b", 0.5, "r:dptr", 4, "r:dpl", 3, "r:dph", 3, "r:psw", 3,
                "r:sp", 0.3, "r:c", 0.6, "r:p0", 3, "r:p1", 3, "r:p2", 3, "r:p3", 3);
            foreach (var r in new[] { "tmod", "tcon", "th0", "tl0", "th1", "tl1", "scon", "sbuf", "ie", "pcon", "tr0", "tr1",
                                      "tf0", "tf1", "ea", "et0", "et1", "ex0", "ex1", "es", "ri", "ti", "it0", "it1", "ie0", "ie1",
                                      "cy", "ov", "ac", "rs0", "rs1", "t2con", "rcap2h", "rcap2l", "th2", "tl2", "ip" })
                w[AsmArch.I8051]["r:" + r] = 3;
            Set(w[AsmArch.I8051], "p:@ind", 4, "p:#imm", 1.5, "p:hhex", 1.0, "p:bitaddr", 4, "p:[]", -3, "p:%reg", -4,
                "p:$reg", -4, "p:ptr", -4, "p:off(reg)", -2, "p:reglist", -3, "p:offset", -3,
                "d:org", 1.5, "d:end", 0.5, "d:equ", 0.5, "d:db", 0.5, "d:dw", 0.3, "d:bit", 2, "d:sbit", 2, "d:cseg", 2,
                "d:dseg", 2, "d:using", 3, "d:ds", 1, "d:data", 1.5, "d:code", 1, "d:xdata", 2, "d:idata", 2, "d:set", 0.5);

            // ---- AVR ----
            for (int i = 0; i <= 31; i++) w[AsmArch.Avr]["r:r" + i] = i >= 16 ? 2.0 : 0.8;
            Set(w[AsmArch.Avr], "r:x", 1.0, "r:y", 1.0, "r:z", 1.0, "r:xl", 3, "r:xh", 3, "r:yl", 3, "r:yh", 3, "r:zl", 3,
                "r:zh", 3, "r:sreg", 3, "r:spl", 3, "r:sph", 3, "r:ramend", 2);
            foreach (var port in "abcdefgh")
            {
                w[AsmArch.Avr]["r:port" + port] = 3;
                w[AsmArch.Avr]["r:ddr" + port] = 3;
                w[AsmArch.Avr]["r:pin" + port] = 3;
            }
            Set(w[AsmArch.Avr], "p:avr_ptr", 3, "p:#imm", -3, "p:[]", -3, "p:%reg", -4, "p:ptr", -4, "p:off(reg)", -2,
                "p:$num", 0.5, "p:0x", 0.3, "p:offset", -3, "p:@ind", -2,
                "d:.include", 1.5, "d:.def", 3, "d:.equ", 0.5, "d:.org", 0.8, "d:.cseg", 3, "d:.dseg", 3, "d:.eseg", 3,
                "d:.device", 3, "d:.db", 1, "d:.dw", 1, "d:.byte", 0.3, "d:.set", 0.5, "d:.macro", 0.3, "d:.endm", 0.3);

            return w;
        }

        private static void Set(Dictionary<string, double> map, params object[] pairs)
        {
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                map[(string)pairs[i]] = Convert.ToDouble(pairs[i + 1], CultureInfo.InvariantCulture);
        }
    }
}
