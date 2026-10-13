using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Assam.Core.Instructions;
using Assam.Core.Text;

namespace Assam.Core.Analysis
{
    public sealed class ExplainedLine
    {
        public int LineNumber { get; set; }
        public string Code { get; set; }
        public string Kind { get; set; }
        public string Explanation { get; set; }
        /// <summary>虛擬碼形式的動作，例如 "EAX ← EAX + 1"。</summary>
        public string Action { get; set; }
    }

    public sealed class CodeExplanation
    {
        public CodeExplanation()
        {
            Lines = new List<ExplainedLine>();
            Notes = new List<string>();
            Warnings = new List<string>();
            Mnemonics = new List<string>();
            Registers = new List<string>();
        }

        public AsmArch Arch { get; set; }
        public ArchDetectionResult Detection { get; set; }
        public bool ArchSpecifiedByUser { get; set; }
        public List<ExplainedLine> Lines { get; private set; }
        public List<string> Notes { get; private set; }
        public List<string> Warnings { get; private set; }
        /// <summary>程式中用到的指令（不重複，依出現次數排序）。</summary>
        public List<string> Mnemonics { get; private set; }
        public List<string> Registers { get; private set; }
        public int InstructionCount { get; set; }

        public string ToText(int maxLines = 300)
        {
            var sb = new StringBuilder();
            sb.Append("【架構】").Append(ArchSpecifiedByUser ? AsmArchInfo.DisplayName(Arch) + "（使用者指定）" : Detection != null ? Detection.Describe() : AsmArchInfo.DisplayName(Arch)).Append("\r\n\r\n");
            sb.Append("【逐行說明】\r\n");
            int shown = 0;
            foreach (var line in Lines)
            {
                if (line.Kind == "空白") continue;
                if (shown++ >= maxLines)
                {
                    sb.Append("……（其餘省略，請在「程式碼分析」頁查看完整內容）\r\n");
                    break;
                }
                sb.Append(line.LineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append(" | ").Append(line.Code).Append("\r\n");
                sb.Append("    → ").Append(line.Explanation).Append("\r\n");
                if (!string.IsNullOrEmpty(line.Action)) sb.Append("    動作：").Append(line.Action).Append("\r\n");
            }
            if (Notes.Count > 0)
            {
                sb.Append("\r\n【程式結構】\r\n");
                foreach (var n in Notes) sb.Append("・").Append(n).Append("\r\n");
            }
            if (Warnings.Count > 0)
            {
                sb.Append("\r\n【注意事項】\r\n");
                foreach (var w in Warnings) sb.Append("▲ ").Append(w).Append("\r\n");
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 程式碼解說器：辨識架構 → 逐行解釋（指令說明、運算元定址模式、虛擬碼動作）→ 分析結構（迴圈、副程式、系統呼叫）→ 找出常見錯誤。
    /// </summary>
    public sealed class CodeExplainer
    {
        private readonly ArchitectureDetector _detector;

        public CodeExplainer(ArchitectureDetector detector)
        {
            _detector = detector;
        }

        /// <summary>解釋程式碼。arch 為 Unknown 時自動辨識；若已經辨識過，可傳入 detection 避免重算。</summary>
        public CodeExplanation Explain(string code, AsmArch arch = AsmArch.Unknown, ArchDetectionResult detection = null)
        {
            var result = new CodeExplanation();
            if (detection != null && (arch == AsmArch.Unknown || arch == detection.Best))
            {
                result.Detection = detection;
                arch = detection.Best;
            }
            else if (arch == AsmArch.Unknown)
            {
                result.Detection = _detector.Detect(code);
                arch = result.Detection.Best;
            }
            else
            {
                result.ArchSpecifiedByUser = true;
            }
            if (arch == AsmArch.Unknown) arch = AsmArch.X86Intel;
            result.Arch = arch;

            var lines = AsmLineParser.Parse(code ?? string.Empty, arch);
            var set = InstructionCatalog.Get(AsmArchInfo.InstructionSetKey(arch));
            var symbols = BuildSymbols(lines, set);
            var mnemonicCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var registers = new List<string>();
            var tracker = new ValueTracker();

            foreach (var line in lines)
            {
                var item = new ExplainedLine { LineNumber = line.LineNumber, Code = line.Raw.TrimEnd() };
                result.Lines.Add(item);

                if (line.IsBlank) { item.Kind = "空白"; item.Explanation = string.Empty; continue; }
                if (line.IsCommentOnly) { item.Kind = "註解"; item.Explanation = "註解：" + line.Comment; continue; }

                var parts = new List<string>();
                if (!string.IsNullOrEmpty(line.Label) && !line.LabelIsName)
                    parts.Add("標籤「" + line.Label + "」：標記這個位置，可作為跳躍或呼叫的目標");

                if (!line.HasMnemonic)
                {
                    item.Kind = "標籤";
                    item.Explanation = string.Join("；", parts);
                    continue;
                }

                string mn = line.MnemonicLower;
                string directive = DirectiveInfo.Explain(mn, line, arch);
                InstructionInfo info = set != null ? set.Find(mn) : null;

                if (directive != null && (info == null || DirectiveInfo.PreferDirective(mn, line)))
                {
                    item.Kind = DirectiveInfo.IsDataDefinition(mn) ? "資料定義" : "虛擬指令";
                    parts.Add(directive);
                }
                else if (info != null)
                {
                    item.Kind = "指令";
                    result.InstructionCount++;
                    int c;
                    mnemonicCount.TryGetValue(info.PrimaryMnemonic, out c);
                    mnemonicCount[info.PrimaryMnemonic] = c + 1;
                    parts.Add(DescribeInstruction(arch, line, info, symbols, tracker, item));
                    CollectRegisters(arch, line, registers);
                }
                else
                {
                    item.Kind = "未知";
                    parts.Add("無法辨識的指令或巨集「" + line.Mnemonic + "」（可能是巨集、其他架構的指令或拼字錯誤）");
                }

                if (!string.IsNullOrEmpty(line.Comment)) parts.Add("原註解：" + line.Comment);
                item.Explanation = string.Join("；", parts.Where(p => !string.IsNullOrEmpty(p)));
                tracker.Observe(arch, line);
            }

            result.Mnemonics.AddRange(mnemonicCount.OrderByDescending(kv => kv.Value).Select(kv => kv.Key.ToUpperInvariant()));
            result.Registers.AddRange(registers.Distinct(StringComparer.OrdinalIgnoreCase));
            AnalyzeStructure(result, lines, arch, set, symbols, mnemonicCount);
            CheckCommonMistakes(result, lines, arch, set, symbols);
            return result;
        }

        private static SymbolTable BuildSymbols(List<AsmLine> lines, InstructionSet set)
        {
            var symbols = new SymbolTable();
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line.Label)) continue;
                string mn = line.MnemonicLower ?? string.Empty;
                if (line.LabelIsName)
                {
                    if (mn == "equ" || mn == "=" || mn == "textequ" || mn == "set" || mn == "bit" || mn == "sbit" || mn == "data")
                        symbols.Constants[line.Label] = line.OperandText;
                    else if (mn == "proc" || mn == "label")
                        symbols.CodeLabels[line.Label] = line.LineNumber;
                    else if (DirectiveInfo.IsDataDefinition(mn))
                        symbols.DataLabels[line.Label] = line.LineNumber;
                }
                else if (DirectiveInfo.IsDataDefinition(mn))
                {
                    symbols.DataLabels[line.Label] = line.LineNumber;
                }
                else
                {
                    symbols.CodeLabels[line.Label] = line.LineNumber;
                }
            }
            return symbols;
        }

        private static string DescribeInstruction(AsmArch arch, AsmLine line, InstructionInfo info, SymbolTable symbols,
                                                  ValueTracker tracker, ExplainedLine item)
        {
            string mnUpper = line.Mnemonic.ToUpperInvariant();
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(line.Prefix))
                sb.Append(line.Prefix.ToUpperInvariant()).Append(" 前綴：重複執行後面的字串指令（每次 ECX 減 1，16 位元模式為 CX）。");
            sb.Append(mnUpper).Append("（").Append(info.Category).Append("）：").Append(FirstSentence(info.Description));

            var operands = new List<string>(line.Operands);
            if (operands.Count > 0)
            {
                var descs = new List<string>();
                foreach (var op in operands)
                {
                    string d = OperandAnalyzer.Describe(arch, op, symbols);
                    if (!string.IsNullOrEmpty(d) && !string.Equals(d, op, StringComparison.Ordinal))
                        descs.Add(op + " → " + d);
                }
                if (descs.Count > 0) sb.Append("　運算元：").Append(string.Join("；", descs));
            }

            // AT&T 語法：來源在前、目的在後，套用樣板前反轉成 Intel 順序
            var forTemplate = new List<string>(operands);
            if (arch == AsmArch.X86Att && forTemplate.Count >= 2) forTemplate.Reverse();
            string action = info.FormatAction(forTemplate);
            string mn = line.MnemonicLower;

            // 常見技巧
            if ((mn == "xor" || mn == "sub" || mn == "eor") && operands.Count == 2 &&
                string.Equals(operands[0].Trim(), operands[1].Trim(), StringComparison.OrdinalIgnoreCase))
            {
                action = Clean(operands[0]) + " ← 0";
                sb.Append("　技巧：自己和自己 ").Append(mnUpper).Append(" 會得到 0，是把暫存器清零的常見寫法。");
            }
            if ((mn == "test" || mn == "and" || mn == "or") && operands.Count == 2 &&
                string.Equals(operands[0].Trim(), operands[1].Trim(), StringComparison.OrdinalIgnoreCase) && mn != "and")
                sb.Append("　技巧：用自己測試自己，可判斷是否為 0（ZF）或負數（SF）。");
            if (arch == AsmArch.I8051 && (mn == "sjmp" || mn == "jmp" || mn == "ajmp" || mn == "ljmp") && operands.Count == 1 && operands[0].Trim() == "$")
                sb.Append("　跳到自己：程式停在這裡的無窮迴圈。");
            if ((arch == AsmArch.Arm || arch == AsmArch.Arm64) && (mn == "bx" || mn == "br") && operands.Count == 1 &&
                (operands[0].Trim().ToLowerInvariant() == "lr" || operands[0].Trim().ToLowerInvariant() == "x30"))
                sb.Append("　這行等於「從副程式返回」。");
            if (arch == AsmArch.Mips && mn == "jr" && operands.Count == 1 && operands[0].Trim().ToLowerInvariant() == "$ra")
                sb.Append("　這行等於「從函式返回」。");
            if (arch == AsmArch.Arm && (mn == "ldr" || mn == "str" || mn == "ldrb" || mn == "strb") && operands.Count == 3)
                sb.Append("　後索引：先用 ").Append(operands[1].Trim()).Append(" 存取記憶體，之後再把基底暫存器加上 ").Append(operands[2].Trim().TrimStart('#')).Append("。");

            string service = tracker.DescribeService(arch, line, symbols);
            if (service != null) sb.Append("　").Append(service);

            string jumpNote = DescribeJumpTarget(arch, line, info, symbols);
            if (jumpNote != null) sb.Append("　").Append(jumpNote);

            item.Action = action;
            return sb.ToString();
        }

        private static string DescribeJumpTarget(AsmArch arch, AsmLine line, InstructionInfo info, SymbolTable symbols)
        {
            if (!IsBranch(info)) return null;
            foreach (var op in line.Operands)
            {
                string target = op.Trim();
                int targetLine;
                if (symbols.CodeLabels.TryGetValue(target, out targetLine))
                {
                    if (targetLine <= line.LineNumber)
                        return "目標「" + target + "」在第 " + targetLine + " 行（往回跳，形成迴圈）。";
                    return "目標「" + target + "」在第 " + targetLine + " 行。";
                }
            }
            return null;
        }

        internal static bool IsBranch(InstructionInfo info)
        {
            if (info == null) return false;
            return info.Category == "流程控制" || info.Category == "條件跳躍" || info.Category == "迴圈" || info.Category == "副程式";
        }

        private static string FirstSentence(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int idx = text.IndexOf('。');
            if (idx > 0 && idx < text.Length - 1 && idx < 90) return text.Substring(0, idx + 1);
            return text;
        }

        private static string Clean(string op)
        {
            return op.Trim().TrimStart('%', '$');
        }

        private static readonly Regex WordRegex = new Regex(@"[%$]?[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

        private static void CollectRegisters(AsmArch arch, AsmLine line, List<string> registers)
        {
            foreach (var op in line.Operands)
            {
                foreach (Match m in WordRegex.Matches(op))
                {
                    string w = m.Value;
                    string d = RegisterInfo.Describe(arch, w.ToLowerInvariant());
                    if (d == null) continue;
                    if (arch == AsmArch.I8051 && (w.Equals("c", StringComparison.OrdinalIgnoreCase))) continue;
                    registers.Add(arch == AsmArch.Mips || arch == AsmArch.RiscV ? w : w.TrimStart('%').ToUpperInvariant());
                }
            }
        }

        // ------------------------------------------------------------------
        // 結構分析
        // ------------------------------------------------------------------

        private static void AnalyzeStructure(CodeExplanation result, List<AsmLine> lines, AsmArch arch, InstructionSet set,
                                             SymbolTable symbols, Dictionary<string, int> mnemonicCount)
        {
            int dataDefs = lines.Count(l => l.HasMnemonic && DirectiveInfo.IsDataDefinition(l.MnemonicLower));
            result.Notes.Add("共 " + result.InstructionCount + " 行指令、" + symbols.CodeLabels.Count + " 個標籤／程序、" + dataDefs + " 個資料定義。");
            if (result.Mnemonics.Count > 0)
            {
                var top = mnemonicCount.OrderByDescending(kv => kv.Value).Take(12)
                                       .Select(kv => kv.Key.ToUpperInvariant() + "×" + kv.Value);
                result.Notes.Add("使用的指令：" + string.Join("、", top));
            }
            if (result.Registers.Count > 0)
                result.Notes.Add("使用的暫存器：" + string.Join("、", result.Registers.Take(20)));

            var procs = lines.Where(l => l.LabelIsName && l.MnemonicLower == "proc").Select(l => l.Label).ToList();
            if (procs.Count > 0) result.Notes.Add("程序（PROC）：" + string.Join("、", procs));

            var calls = new List<string>();
            foreach (var l in lines)
            {
                if (!l.HasMnemonic || set == null) continue;
                var info = set.Find(l.MnemonicLower);
                if (info == null || info.Category != "副程式" || l.Operands.Count == 0) continue;
                string target = l.Operands[l.Operands.Count - 1].Trim();
                if (!calls.Contains(target, StringComparer.OrdinalIgnoreCase)) calls.Add(target);
            }
            if (calls.Count > 0)
            {
                var described = calls.Select(c =>
                {
                    string lib = symbols.CodeLabels.ContainsKey(c) ? null : LibraryCalls.Describe(c);
                    return lib != null ? c + "（" + lib + "）" : c;
                });
                result.Notes.Add("呼叫的副程式：" + string.Join("、", described));
            }

            foreach (var l in lines)
            {
                if (!l.HasMnemonic || set == null) continue;
                var info = set.Find(l.MnemonicLower);
                if (!IsBranch(info) || info.Category == "副程式") continue;
                foreach (var op in l.Operands)
                {
                    int target;
                    if (symbols.CodeLabels.TryGetValue(op.Trim(), out target) && target <= l.LineNumber)
                    {
                        result.Notes.Add("迴圈：第 " + target + "～" + l.LineNumber + " 行（第 " + l.LineNumber + " 行的 " +
                                         l.Mnemonic.ToUpperInvariant() + " 跳回標籤「" + op.Trim() + "」）");
                        break;
                    }
                }
            }

            foreach (var item in result.Lines)
            {
                if (item.Explanation != null && item.Explanation.Contains("【系統服務】"))
                {
                    int idx = item.Explanation.IndexOf("【系統服務】", StringComparison.Ordinal);
                    result.Notes.Add("第 " + item.LineNumber + " 行：" + item.Explanation.Substring(idx + 6).TrimEnd('。') );
                }
            }
        }

        // ------------------------------------------------------------------
        // 常見錯誤檢查
        // ------------------------------------------------------------------

        private static void CheckCommonMistakes(CodeExplanation result, List<AsmLine> lines, AsmArch arch, InstructionSet set, SymbolTable symbols)
        {
            var code = lines.Where(l => l.HasMnemonic).ToList();
            for (int i = 0; i < code.Count; i++)
            {
                var l = code[i];
                string mn = l.MnemonicLower;
                var info = set != null ? set.Find(mn) : null;

                // 跳躍到未定義的標籤
                if (info != null && IsBranch(info) && l.Operands.Count > 0)
                {
                    string target = l.Operands[l.Operands.Count - 1].Trim();
                    if (Regex.IsMatch(target, @"^[A-Za-z_.@?][\w.@?]*$") && !symbols.CodeLabels.ContainsKey(target) &&
                        RegisterInfo.Describe(arch, target.ToLowerInvariant()) == null && LibraryCalls.Describe(target) == null &&
                        target != "$" && info.Category != "副程式")
                        result.Warnings.Add("第 " + l.LineNumber + " 行：跳躍目標「" + target + "」在這段程式中找不到定義。");
                }

                if (arch == AsmArch.X86Intel)
                {
                    if (mn == "mov" && l.Operands.Count == 2)
                    {
                        bool m0 = IsIntelMemory(l.Operands[0], symbols);
                        bool m1 = IsIntelMemory(l.Operands[1], symbols);
                        if (m0 && m1) result.Warnings.Add("第 " + l.LineNumber + " 行：x86 不允許「記憶體 → 記憶體」的 MOV，請先搬到暫存器再存回。");

                        int b0 = RegisterInfo.X86RegisterBits(l.Operands[0]);
                        int b1 = RegisterInfo.X86RegisterBits(l.Operands[1]);
                        if (b0 > 0 && b1 > 0 && b0 != b1 && b0 != 128 && b1 != 128)
                            result.Warnings.Add("第 " + l.LineNumber + " 行：運算元大小不一致（" + l.Operands[0].Trim().ToUpperInvariant() + " 是 " + b0 + " 位元、" +
                                                l.Operands[1].Trim().ToUpperInvariant() + " 是 " + b1 + " 位元），可改用 MOVZX／MOVSX。");

                        string dst = l.Operands[0].Trim().ToLowerInvariant();
                        long imm;
                        if ((dst == "ds" || dst == "es" || dst == "ss" || dst == "cs") && AsmNumber.TryParse(l.Operands[1], out imm))
                            result.Warnings.Add("第 " + l.LineNumber + " 行：不能把立即值直接放進區段暫存器，要先放到 AX 再 MOV " + dst.ToUpperInvariant() + ", AX。");
                        if (dst == "cs") result.Warnings.Add("第 " + l.LineNumber + " 行：CS 不能用 MOV 修改。");
                    }

                    if ((mn == "div" || mn == "idiv") && l.Operands.Count == 1)
                    {
                        int bits = RegisterInfo.X86RegisterBits(l.Operands[0]);
                        string hi = bits == 16 ? "dx" : bits == 64 ? "rdx" : "edx";
                        if (bits != 8 && !PrecededBySetup(code, i, hi, mn == "idiv"))
                            result.Warnings.Add("第 " + l.LineNumber + " 行：" + mn.ToUpperInvariant() + " 會把 " + hi.ToUpperInvariant() + ":" +
                                                (bits == 16 ? "AX" : bits == 64 ? "RAX" : "EAX") + " 當成被除數，前面沒有看到清除或設定 " + hi.ToUpperInvariant() +
                                                "（無號用 XOR " + hi.ToUpperInvariant() + "," + hi.ToUpperInvariant() + "，有號用 " + (bits == 16 ? "CWD" : bits == 64 ? "CQO" : "CDQ") + "），可能造成除法溢位。");
                    }

                    if (mn == "loop" && !code.Take(i).Any(p => p.Operands.Count > 0 && Regex.IsMatch(p.Operands[0].Trim(), "^(e|r)?cx$", RegexOptions.IgnoreCase)))
                        result.Warnings.Add("第 " + l.LineNumber + " 行：LOOP 以 ECX 當計數器，前面沒有看到設定 ECX；若 ECX=0 會執行 2^32 次。");
                }

                if (arch == AsmArch.I8051 && (mn == "mov" || mn == "add" || mn == "addc" || mn == "subb" || mn == "anl" || mn == "orl") &&
                    l.Operands.Count == 2 && l.Operands[0].Trim().Equals("a", StringComparison.OrdinalIgnoreCase))
                {
                    long v;
                    string src = l.Operands[1].Trim();
                    if (!src.StartsWith("#", StringComparison.Ordinal) && AsmNumber.TryParse(src, out v))
                        result.Notes.Add("第 " + l.LineNumber + " 行：「" + l.Mnemonic.ToUpperInvariant() + " A, " + src + "」沒有 #，代表讀取 RAM 位址 " + src +
                                         " 的內容；若要使用常數 " + src + " 請寫 #" + src + "。");
                }

                if (arch == AsmArch.Mips && (mn == "lw" || mn == "sw") && l.Operands.Count == 2)
                {
                    var m = Regex.Match(l.Operands[1], @"^\s*(-?\d+)\s*\(");
                    int off;
                    if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out off) && off % 4 != 0)
                        result.Warnings.Add("第 " + l.LineNumber + " 行：" + mn + " 的位移 " + off + " 不是 4 的倍數，若基底位址對齊則會產生位址未對齊例外。");
                }
            }

            if (arch == AsmArch.X86Intel)
            {
                // PROC 內 PUSH/POP 次數比較
                var stack = new List<AsmLine>();
                string currentProc = null;
                int pushes = 0, pops = 0;
                foreach (var l in code)
                {
                    string mn = l.MnemonicLower;
                    if (l.LabelIsName && mn == "proc") { currentProc = l.Label; pushes = pops = 0; continue; }
                    if (l.LabelIsName && mn == "endp")
                    {
                        if (currentProc != null && pushes != pops)
                            result.Warnings.Add("程序 " + currentProc + "：PUSH " + pushes + " 次、POP " + pops + " 次，堆疊可能不平衡（RET 會取到錯誤的返回位址）。");
                        currentProc = null;
                        continue;
                    }
                    if (mn == "push") pushes++;
                    if (mn == "pop") pops++;
                }

                bool fullProgram = lines.Any(l => l.HasMnemonic && (l.MnemonicLower == ".code" || l.MnemonicLower == "section"));
                bool hasExit = lines.Any(l => l.HasMnemonic && (l.MnemonicLower == "exit" || l.MnemonicLower == "ret" || l.MnemonicLower == "hlt" ||
                                                                (l.MnemonicLower == "invoke" && l.OperandText.IndexOf("exitprocess", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                                                (l.MnemonicLower == "call" && l.OperandText.IndexOf("exitprocess", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                                                l.MnemonicLower == "int" || l.MnemonicLower == "syscall"));
                if (fullProgram && !hasExit)
                    result.Warnings.Add("看不到結束程式的指令（exit、INVOKE ExitProcess、INT 21h/4Ch 或系統呼叫），程式執行完最後一行後會跑進未知的記憶體。");
            }
        }

        private static bool IsIntelMemory(string operand, SymbolTable symbols)
        {
            string o = operand.Trim();
            if (o.IndexOf('[') >= 0) return true;
            string lower = o.ToLowerInvariant();
            if (lower.StartsWith("offset", StringComparison.Ordinal)) return false;
            o = Regex.Replace(o, @"^(byte|word|dword|qword)\s+ptr\s+", string.Empty, RegexOptions.IgnoreCase);
            string first = Regex.Split(o, @"[+\-]")[0].Trim();
            return symbols.DataLabels.ContainsKey(first);
        }

        private static bool PrecededBySetup(List<AsmLine> code, int index, string hiReg, bool signed)
        {
            for (int j = index - 1; j >= 0 && j >= index - 6; j--)
            {
                var p = code[j];
                string mn = p.MnemonicLower;
                if (mn == "cdq" || mn == "cwd" || mn == "cqo") return true;
                if (p.Operands.Count > 0 && p.Operands[0].Trim().Equals(hiReg, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // 追蹤 AH / EAX / $v0 等的值，用來說明 INT 21h、syscall 的功能
        // ------------------------------------------------------------------

        private sealed class ValueTracker
        {
            private readonly Dictionary<string, long> _values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            public void Observe(AsmArch arch, AsmLine line)
            {
                if (!line.HasMnemonic || line.Operands.Count < 2) return;
                string mn = line.MnemonicLower;
                bool isMove = mn == "mov" || mn == "li" || mn == "movl" || mn == "movq" || mn == "ldi" || mn == "addi" && line.Operands.Count == 3 && IsZero(line.Operands[1]);
                if (!isMove) return;

                string dst, src;
                if (arch == AsmArch.X86Att) { dst = line.Operands[1]; src = line.Operands[0]; }
                else { dst = line.Operands[0]; src = line.Operands[line.Operands.Count - 1]; }
                dst = dst.Trim().TrimStart('%', '$').ToLowerInvariant();
                src = src.Trim().TrimStart('$', '#');
                long v;
                if (!AsmNumber.TryParse(src, out v)) { _values.Remove(dst); return; }
                _values[dst] = v;
                if (dst == "ax") { _values["ah"] = (v >> 8) & 0xFF; _values["al"] = v & 0xFF; }
                if (dst == "eax" || dst == "rax") { _values["eax"] = v; _values["rax"] = v; _values["ah"] = (v >> 8) & 0xFF; }
            }

            private static bool IsZero(string op)
            {
                string o = op.Trim().ToLowerInvariant();
                return o == "zero" || o == "x0" || o == "$zero" || o == "$0";
            }

            private bool TryGet(string reg, out long v)
            {
                return _values.TryGetValue(reg, out v);
            }

            public string DescribeService(AsmArch arch, AsmLine line, SymbolTable symbols)
            {
                string mn = line.MnemonicLower;
                long v;
                string op = line.Operands.Count > 0 ? line.Operands[0].Trim().TrimStart('$') : string.Empty;
                long intNo;
                if ((arch == AsmArch.X86Intel || arch == AsmArch.X86Att) && mn == "int" && AsmNumber.TryParse(op, out intNo))
                {
                    if (intNo == 0x21)
                        return TryGet("ah", out v) ? "【系統服務】DOS INT 21h，AH=" + v.ToString("X2", CultureInfo.InvariantCulture) + "h：" + ServiceNames.Dos(v) + "。" : "【系統服務】DOS 功能呼叫（功能號碼放在 AH）。";
                    if (intNo == 0x10)
                        return TryGet("ah", out v) ? "【系統服務】BIOS INT 10h，AH=" + v.ToString("X2", CultureInfo.InvariantCulture) + "h：" + ServiceNames.Bios10(v) + "。" : "【系統服務】BIOS 顯示服務（功能號碼放在 AH）。";
                    if (intNo == 0x16) return "【系統服務】BIOS 鍵盤服務 INT 16h（AH=00h 讀取按鍵到 AL）。";
                    if (intNo == 0x80)
                        return TryGet("eax", out v) ? "【系統服務】Linux 32 位元系統呼叫，EAX=" + v + "：" + ServiceNames.Linux32(v) + "。" : "【系統服務】Linux 32 位元系統呼叫（號碼放在 EAX）。";
                    if (intNo == 3) return "【系統服務】INT 3：除錯中斷點。";
                }
                if ((arch == AsmArch.X86Intel || arch == AsmArch.X86Att) && mn == "syscall")
                    return TryGet("rax", out v) || TryGet("eax", out v) ? "【系統服務】Linux x64 系統呼叫，RAX=" + v + "：" + ServiceNames.Linux64(v) + "。" : "【系統服務】Linux x64 系統呼叫（號碼放在 RAX）。";
                if (arch == AsmArch.Mips && mn == "syscall")
                    return TryGet("v0", out v) ? "【系統服務】MARS/SPIM 服務 $v0=" + v + "：" + ServiceNames.Mars(v) + "。" : "【系統服務】系統呼叫（服務號碼放在 $v0）。";
                if (arch == AsmArch.RiscV && mn == "ecall")
                    return TryGet("a7", out v) ? "【系統服務】ecall，a7=" + v + "：" + ServiceNames.RiscV(v) + "。" : "【系統服務】系統呼叫（號碼放在 a7）。";
                if (arch == AsmArch.Arm && (mn == "svc" || mn == "swi"))
                    return TryGet("r7", out v) ? "【系統服務】Linux ARM 系統呼叫，R7=" + v + "：" + ServiceNames.Linux32(v) + "。" : "【系統服務】系統呼叫（Linux 號碼放在 R7）。";
                if (arch == AsmArch.Arm64 && mn == "svc")
                    return TryGet("x8", out v) ? "【系統服務】Linux ARM64 系統呼叫，X8=" + v + "：" + ServiceNames.LinuxGeneric(v) + "。" : "【系統服務】系統呼叫（號碼放在 X8）。";
                if ((arch == AsmArch.X86Intel || arch == AsmArch.X86Att) && (mn == "call" || mn == "invoke") && line.Operands.Count > 0 &&
                    !symbols.CodeLabels.ContainsKey(line.Operands[0].Trim()))
                {
                    string lib = LibraryCalls.Describe(line.Operands[0].Trim().TrimStart('_'));
                    if (lib != null) return "【函式庫】" + line.Operands[0].Trim() + "：" + lib + "。";
                }
                return null;
            }
        }
    }

    /// <summary>常見系統服務號碼的意義。</summary>
    public static class ServiceNames
    {
        public static string Dos(long ah)
        {
            switch (ah)
            {
                case 0x01: return "從鍵盤讀取一個字元（有回顯），結果放在 AL";
                case 0x02: return "顯示 DL 中的字元";
                case 0x06: return "直接主控台輸入／輸出";
                case 0x07: return "讀取字元（無回顯）";
                case 0x08: return "讀取字元（無回顯，可偵測 Ctrl+C）";
                case 0x09: return "顯示 DS:DX 指向的字串，字串必須以 '$' 結尾";
                case 0x0A: return "讀取一整行輸入到 DS:DX 的緩衝區";
                case 0x2A: return "取得系統日期";
                case 0x2C: return "取得系統時間";
                case 0x3C: return "建立檔案";
                case 0x3D: return "開啟檔案";
                case 0x3E: return "關閉檔案";
                case 0x3F: return "讀取檔案";
                case 0x40: return "寫入檔案";
                case 0x4C: return "結束程式並回到 DOS（AL = 結束碼）";
                default: return "DOS 功能 " + ah.ToString("X2", CultureInfo.InvariantCulture) + "h";
            }
        }

        public static string Bios10(long ah)
        {
            switch (ah)
            {
                case 0x00: return "設定顯示模式（AL = 模式）";
                case 0x02: return "設定游標位置（DH = 列、DL = 行）";
                case 0x03: return "讀取游標位置";
                case 0x06: return "向上捲動視窗（可用於清除螢幕）";
                case 0x09: return "在游標處寫入字元與屬性";
                case 0x0E: return "電傳打字模式輸出 AL 中的字元";
                default: return "BIOS 顯示功能 " + ah.ToString("X2", CultureInfo.InvariantCulture) + "h";
            }
        }

        public static string Linux32(long eax)
        {
            switch (eax)
            {
                case 1: return "exit 結束程式（EBX = 結束碼）";
                case 3: return "read 讀取（EBX=檔案代碼、ECX=緩衝區、EDX=長度）";
                case 4: return "write 寫出（EBX=檔案代碼 1 為螢幕、ECX=字串位址、EDX=長度）";
                case 5: return "open 開啟檔案";
                case 6: return "close 關閉檔案";
                default: return "系統呼叫 " + eax;
            }
        }

        public static string Linux64(long rax)
        {
            switch (rax)
            {
                case 0: return "read 讀取（RDI=檔案代碼、RSI=緩衝區、RDX=長度）";
                case 1: return "write 寫出（RDI=檔案代碼 1 為螢幕、RSI=字串位址、RDX=長度）";
                case 2: return "open 開啟檔案";
                case 3: return "close 關閉檔案";
                case 60: return "exit 結束程式（RDI = 結束碼）";
                case 231: return "exit_group 結束整個行程";
                default: return "系統呼叫 " + rax;
            }
        }

        public static string LinuxGeneric(long n)
        {
            switch (n)
            {
                case 63: return "read 讀取";
                case 64: return "write 寫出（X0=檔案代碼、X1=緩衝區、X2=長度）";
                case 93: return "exit 結束程式（X0 = 結束碼）";
                default: return "系統呼叫 " + n;
            }
        }

        public static string Mars(long v0)
        {
            switch (v0)
            {
                case 1: return "印出整數（$a0）";
                case 4: return "印出字串（$a0 = 字串位址，以 0 結尾）";
                case 5: return "讀取整數到 $v0";
                case 8: return "讀取字串（$a0 = 緩衝區、$a1 = 長度）";
                case 9: return "配置記憶體（sbrk）";
                case 10: return "結束程式";
                case 11: return "印出字元（$a0）";
                case 12: return "讀取字元到 $v0";
                case 17: return "結束程式並傳回結束碼（$a0）";
                default: return "服務 " + v0;
            }
        }

        public static string RiscV(long a7)
        {
            switch (a7)
            {
                case 1: return "（RARS）印出整數 a0";
                case 4: return "（RARS）印出字串 a0";
                case 5: return "（RARS）讀取整數到 a0";
                case 10: return "（RARS）結束程式";
                case 11: return "（RARS）印出字元 a0";
                case 64: return "（Linux）write 寫出";
                case 93: return "（Linux）exit 結束程式";
                default: return "系統呼叫 " + a7;
            }
        }
    }

    /// <summary>常見函式庫（Irvine32、C 執行階段、Win32）的說明。</summary>
    public static class LibraryCalls
    {
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "WriteString", "Irvine32：顯示 EDX 指向、以 0 結尾的字串" },
            { "WriteDec", "Irvine32：以十進位顯示 EAX（無號）" },
            { "WriteInt", "Irvine32：以十進位顯示 EAX（有號，帶 +/- 號）" },
            { "WriteHex", "Irvine32：以 8 位數十六進位顯示 EAX" },
            { "WriteHexB", "Irvine32：以十六進位顯示 EAX（EBX 指定位元組數）" },
            { "WriteBin", "Irvine32：以二進位顯示 EAX" },
            { "WriteChar", "Irvine32：顯示 AL 中的字元" },
            { "Crlf", "Irvine32：換行" },
            { "ReadInt", "Irvine32：讀取有號整數到 EAX" },
            { "ReadDec", "Irvine32：讀取無號整數到 EAX" },
            { "ReadHex", "Irvine32：讀取十六進位整數到 EAX" },
            { "ReadChar", "Irvine32：讀取一個字元到 AL（不回顯）" },
            { "ReadString", "Irvine32：讀取字串到 EDX 指向的緩衝區（ECX = 最大長度，EAX 傳回長度）" },
            { "DumpRegs", "Irvine32：顯示所有暫存器與旗標" },
            { "DumpMem", "Irvine32：顯示記憶體內容（ESI=位址、ECX=個數、EBX=單位大小）" },
            { "Clrscr", "Irvine32：清除螢幕" },
            { "Delay", "Irvine32：暫停 EAX 毫秒" },
            { "Randomize", "Irvine32：設定亂數種子" },
            { "RandomRange", "Irvine32：產生 0 ～ EAX-1 的亂數放在 EAX" },
            { "Random32", "Irvine32：產生 32 位元亂數到 EAX" },
            { "SetTextColor", "Irvine32：設定文字顏色（EAX）" },
            { "Gotoxy", "Irvine32：移動游標（DH=列、DL=行）" },
            { "GetMseconds", "Irvine32：取得午夜以來的毫秒數到 EAX" },
            { "WaitMsg", "Irvine32：顯示「按任意鍵繼續」並等待" },
            { "StrLength", "Irvine32：計算 EDX 指向字串的長度到 EAX" },
            { "ExitProcess", "Win32 API：結束程式" },
            { "MessageBoxA", "Win32 API：顯示訊息方塊" },
            { "printf", "C 函式庫：格式化輸出" },
            { "scanf", "C 函式庫：格式化輸入" },
            { "puts", "C 函式庫：輸出字串並換行" },
            { "putchar", "C 函式庫：輸出一個字元" },
            { "getchar", "C 函式庫：讀取一個字元" },
            { "malloc", "C 函式庫：配置記憶體" },
            { "free", "C 函式庫：釋放記憶體" },
            { "exit", "C 函式庫：結束程式" }
        };

        public static string Describe(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string n = name.Trim().TrimStart('_');
            int at = n.IndexOf('@');
            if (at > 0) n = n.Substring(0, at);
            string s;
            return Map.TryGetValue(n, out s) ? s : null;
        }
    }
}
