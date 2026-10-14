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
            if (arch == AsmArch.X86Intel || arch == AsmArch.X86Att)
            {
                string last = line.Operands.Count > 0 ? line.Operands[line.Operands.Count - 1].Trim().TrimStart('*') : string.Empty;
                string note = DescribeDisassemblerTarget(last);
                if (note != null) return note;
            }
            return null;
        }

        /// <summary>sub_401560、loc_401578、j_printf 這類反組譯器產生、但不在這段程式中的名稱。</summary>
        private static string DescribeDisassemblerTarget(string target)
        {
            if (string.IsNullOrEmpty(target) || LibraryCalls.IsImportReference(target) || !LibraryCalls.IsDisassemblerSymbol(target)) return null;
            string address = AddressOfSymbol(target);
            if (target.StartsWith("sub_", StringComparison.Ordinal))
                return "目標「" + target + "」是反組譯器替位址 " + address + " 的函式取的名稱，它的內容不在這段程式碼中。";
            if (target.StartsWith("j_", StringComparison.Ordinal))
            {
                string api = LibraryCalls.Normalize(target.Substring(2));
                string desc = LibraryCalls.Describe(target);
                return "目標「" + target + "」是匯入跳板（裡面只有一行 jmp [__imp_" + api + "]），等於呼叫外部函式 " + api +
                       (desc != null ? "（" + desc + "）" : string.Empty) + "。";
            }
            return "目標「" + target + "」是反組譯器產生的位置標籤（位址 " + address + "），不在這段程式碼中。";
        }

        private static string AddressOfSymbol(string symbol)
        {
            int us = symbol.IndexOf('_');
            if (us < 0) return symbol;
            string hex = symbol.Substring(us + 1);
            int us2 = hex.IndexOf('_');
            if (us2 > 0) hex = hex.Substring(0, us2);
            return "0x" + hex.ToUpperInvariant();
        }

        /// <summary>呼叫目標的顯示名稱：[__imp_X] → X（透過匯入表呼叫；說明），已知函式附上說明。</summary>
        private static string DescribeCallForSummary(string target, SymbolTable symbols)
        {
            if (symbols.CodeLabels.ContainsKey(target)) return target;
            if (LibraryCalls.IsImportReference(target))
            {
                string name = LibraryCalls.Normalize(target) ?? target;
                string desc = LibraryCalls.Describe(target) ?? LibraryCalls.DescribeOrdinalImport(target);
                return name + "（透過匯入表呼叫的外部函式" + (desc != null ? "；" + desc : string.Empty) + "）";
            }
            string lib = LibraryCalls.Describe(target);
            if (lib != null) return target + "（" + lib + "）";
            if (RegisterInfo.X86RegisterBits(target.TrimStart('%')) > 0) return target + "（間接呼叫：位址存在暫存器中）";
            if (target.IndexOf('[') >= 0) return target + "（間接呼叫：位址存在記憶體中）";
            return target;
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
                bool isX86 = arch == AsmArch.X86Intel || arch == AsmArch.X86Att;
                if (isX86 && l.MnemonicLower == "invoke" && l.Operands.Count > 0)
                {
                    string proc = l.Operands[0].Trim();
                    if (calls.Count < 40 && !calls.Contains(proc, StringComparer.OrdinalIgnoreCase)) calls.Add(proc);
                    continue;
                }
                var info = set.Find(l.MnemonicLower);
                if (info == null || l.Operands.Count == 0) continue;
                string target = l.Operands[l.Operands.Count - 1].Trim();
                if (isX86) target = target.TrimStart('*');
                string primary = info.PrimaryMnemonic.ToLowerInvariant();
                // RET n、ENTER n, 0 的運算元不是呼叫目標
                if (primary == "ret" || primary == "retn" || primary == "retf" || primary == "reti" || primary == "enter" || primary == "leave") continue;
                // 匯入跳板 jmp dword ptr [__imp_X] 也等於呼叫外部函式
                bool importJump = isX86 && primary == "jmp" && LibraryCalls.IsImportReference(target);
                if (info.Category != "副程式" && !importJump) continue;
                if (calls.Count < 40 && !calls.Contains(target, StringComparer.OrdinalIgnoreCase)) calls.Add(target);
            }
            if (calls.Count > 0)
            {
                var described = calls.Select(c => DescribeCallForSummary(c, symbols)).Distinct(StringComparer.OrdinalIgnoreCase);
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
            var externalTargets = new List<string>();
            for (int i = 0; i < code.Count; i++)
            {
                var l = code[i];
                string mn = l.MnemonicLower;
                var info = set != null ? set.Find(mn) : null;

                // 跳躍到未定義的標籤
                if (info != null && IsBranch(info) && l.Operands.Count > 0)
                {
                    string target = l.Operands[l.Operands.Count - 1].Trim();
                    if ((arch == AsmArch.X86Intel || arch == AsmArch.X86Att) && !symbols.CodeLabels.ContainsKey(target) &&
                        LibraryCalls.IsDisassemblerSymbol(target.TrimStart('*')))
                    {
                        // sub_／loc_／j_／__imp_ 是反組譯器產生的名稱，只是不在這段程式中，不算錯誤
                        if (externalTargets.Count < 12 && !externalTargets.Contains(target, StringComparer.OrdinalIgnoreCase)) externalTargets.Add(target);
                    }
                    else if (Regex.IsMatch(target, @"^[A-Za-z_.@?][\w.@?]*$") && !symbols.CodeLabels.ContainsKey(target) &&
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

            if (externalTargets.Count > 0)
                result.Notes.Add("反組譯器產生的名稱（定義不在這段程式中，屬正常現象）：" + string.Join("、", externalTargets) +
                                 "；sub_ 是函式、loc_ 是跳躍位置、j_ 是匯入跳板，後面的數字是十六進位位址。");

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
                                                                l.MnemonicLower == "int" || l.MnemonicLower == "syscall" ||
                                                                l.MnemonicLower == "retn" || l.MnemonicLower == "retf" || IsEndingCall(l, symbols)));
                if (fullProgram && !hasExit)
                    result.Warnings.Add("看不到結束程式的指令（exit、INVOKE ExitProcess、INT 21h/4Ch 或系統呼叫），程式執行完最後一行後會跑進未知的記憶體。");
            }
        }

        /// <summary>
        /// 會結束程式或結束這個函式的呼叫：call／jmp 到 exit、_exit、abort、ExitProcess（含 [__imp_X] 寫法），
        /// 或尾端跳躍到其他函式（jmp [__imp_X]、jmp sub_xxx）。
        /// </summary>
        private static bool IsEndingCall(AsmLine l, SymbolTable symbols)
        {
            string mn = l.MnemonicLower;
            if ((mn != "call" && mn != "jmp" && mn != "invoke") || l.Operands.Count == 0) return false;
            string target = l.Operands[0].Trim().TrimStart('*');
            string name = LibraryCalls.Normalize(target);
            if (name != null && (name.Equals("ExitProcess", StringComparison.OrdinalIgnoreCase) || name == "exit" || name == "_exit" || name == "abort"))
                return true;
            return mn == "jmp" && !symbols.CodeLabels.ContainsKey(target) &&
                   (LibraryCalls.IsImportReference(target) || target.StartsWith("sub_", StringComparison.Ordinal) || target.StartsWith("j_", StringComparison.Ordinal));
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
                if (!line.HasMnemonic) return;
                if (arch == AsmArch.X86Intel || arch == AsmArch.X86Att) ObserveImportPointer(arch, line);
                if (line.Operands.Count < 2) return;
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
                if ((arch == AsmArch.X86Intel || arch == AsmArch.X86Att) && (mn == "call" || mn == "invoke" || mn == "jmp") && line.Operands.Count > 0)
                    return DescribeCallTarget(arch, line, symbols, mn == "jmp");
                return null;
            }

            /// <summary>說明 CALL／JMP 的目標：匯入表間接呼叫、函式庫、存在暫存器中的 API 位址、函式指標。</summary>
            private string DescribeCallTarget(AsmArch arch, AsmLine line, SymbolTable symbols, bool jump)
            {
                string raw = line.Operands[0].Trim();
                string target = raw.TrimStart('*');
                if (symbols.CodeLabels.ContainsKey(target)) return null;
                if (LibraryCalls.IsImportReference(target))
                {
                    string name = LibraryCalls.Normalize(target) ?? target;
                    string desc = LibraryCalls.Describe(target) ?? LibraryCalls.DescribeOrdinalImport(target);
                    string how = jump
                        ? "匯入跳板：從匯入位址表（IAT）的槽位讀出 " + name + " 的真實位址後直接跳過去（等於呼叫它，返回時回到原本的呼叫者）"
                        : "透過匯入位址表（IAT）間接呼叫外部函式 " + name + "：方括號裡是 IAT 槽位，Windows 載入程式時會把 " + name +
                          " 的真實位址填進去，CALL 先讀出這個位址再跳過去";
                    return "【外部函式】" + how + (desc != null ? "。" + name + "：" + desc : string.Empty) + "。";
                }

                string reg = target.TrimStart('%').ToLowerInvariant();
                string api;
                if (_importInRegister.TryGetValue(CanonicalRegister(reg), out api))
                {
                    string desc = LibraryCalls.Describe(api);
                    return "【外部函式】" + reg.ToUpperInvariant() + " 之前載入了 IAT 槽位 __imp_" + api + " 的內容，所以這行是" + (jump ? "跳到" : "呼叫") +
                           "外部函式 " + api + (desc != null ? "：" + desc : string.Empty) + "。";
                }
                bool isRegister = RegisterInfo.X86RegisterBits(reg) > 0;
                bool isMemory = target.IndexOf('[') >= 0 || (arch == AsmArch.X86Att && target.IndexOf('(') >= 0);
                if (jump)
                {
                    if (isMemory) return "間接跳躍：先從記憶體讀出目標位址再跳過去（常見於 switch 跳躍表）。";
                    if (isRegister) return "間接跳躍：跳到 " + reg.ToUpperInvariant() + " 中存放的位址。";
                    return null; // 一般標籤交給 DescribeJumpTarget
                }
                if (!LibraryCalls.IsDisassemblerSymbol(target))
                {
                    string lib = LibraryCalls.Describe(target);
                    if (lib != null) return "【函式庫】" + raw + "：" + lib + "。";
                }
                if (isRegister) return "間接呼叫：目標位址存在 " + reg.ToUpperInvariant() + " 中（函式指標、虛擬函式或執行時才取得的 API 位址）。";
                if (isMemory) return "間接呼叫：先從記憶體讀出函式位址再呼叫（函式指標或虛擬函式表）。";
                int bytes = LibraryCalls.StdcallArgumentBytes(target);
                if (bytes >= 0 && Regex.IsMatch(target, @"@\d+$"))
                    return "名稱後面的 @" + bytes + " 是 stdcall 名稱修飾：函式有 " + bytes + " 位元組參數（" + bytes / 4 + " 個 4 位元組參數），返回時用 RET " + bytes + " 自行清除。";
                return null;
            }

            // 暫存器 → 從 IAT 槽位載入的 API 名稱，例如 mov esi, dword ptr [__imp_printf] 之後 call esi
            private readonly Dictionary<string, string> _importInRegister = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            private void ObserveImportPointer(AsmArch arch, AsmLine line)
            {
                string mn = line.MnemonicLower;
                if (mn == "call")
                {
                    // 呼叫會改變揮發性暫存器
                    foreach (var r in new[] { "ax", "cx", "dx", "r8", "r9", "r10", "r11" }) _importInRegister.Remove(r);
                    return;
                }
                if (line.Operands.Count == 0 || mn.StartsWith("j", StringComparison.Ordinal) || mn == "push" || mn == "pushl" || mn == "pushq" ||
                    mn == "cmp" || mn == "test" || mn.StartsWith("cmp", StringComparison.Ordinal) || mn.StartsWith("test", StringComparison.Ordinal))
                    return;
                bool att = arch == AsmArch.X86Att;
                string dst = (att ? line.Operands[line.Operands.Count - 1] : line.Operands[0]).Trim().TrimStart('%').ToLowerInvariant();
                if (RegisterInfo.X86RegisterBits(dst) <= 0) return;
                string key = CanonicalRegister(dst);
                string src = line.Operands.Count >= 2 ? (att ? line.Operands[0] : line.Operands[line.Operands.Count - 1]) : null;
                if ((mn == "mov" || mn == "movl" || mn == "movq") && src != null && LibraryCalls.IsImportReference(src))
                {
                    string api = LibraryCalls.Normalize(src);
                    if (api != null) { _importInRegister[key] = api; return; }
                }
                _importInRegister.Remove(key);
            }

            /// <summary>eax／rax → ax、esi／rsi → si，讓 32 與 64 位元名稱對應到同一個暫存器。</summary>
            private static string CanonicalRegister(string reg)
            {
                if (reg.Length == 3 && (reg[0] == 'e' || reg[0] == 'r') && char.IsLetter(reg[1]) && char.IsLetter(reg[2])) return reg.Substring(1);
                return reg;
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

    /// <summary>
    /// 常見函式庫（Irvine32、C 執行階段、Win32 API、編譯器輔助函式）的說明與名稱正規化。
    /// 可以接受反組譯器的寫法，例如 "dword ptr [__imp_ExitProcess]"、"KERNEL32.dll!ExitProcess"、"_MessageBoxA@16"、"j_printf"。
    /// </summary>
    public static class LibraryCalls
    {
        private sealed class ApiEntry
        {
            public string Name;
            public string Description;
            /// <summary>32 位元 stdcall 的參數個數（被呼叫者清除堆疊）；-1 表示 cdecl 或不清楚。</summary>
            public int StdcallArgs = -1;
        }

        private const string Irvine = "Irvine32：";
        private const string Kernel32 = "Win32 API（kernel32）：";
        private const string User32 = "Win32 API（user32）：";
        private const string Crt = "C 函式庫：";
        private const string CrtStartup = "C 執行階段（啟動程式內部使用）：";
        private const string Helper = "編譯器輔助函式：";

        private static readonly Dictionary<string, ApiEntry> Map = BuildMap();

        private static Dictionary<string, ApiEntry> BuildMap()
        {
            var map = new Dictionary<string, ApiEntry>(StringComparer.OrdinalIgnoreCase);
            Action<string, string> add = (name, desc) => map[name] = new ApiEntry { Name = name, Description = desc };
            Action<string, int, string> api = (name, args, desc) => map[name] = new ApiEntry { Name = name, Description = desc, StdcallArgs = args };
            // 有 A（ANSI）與 W（Unicode）兩種版本的 Win32 API：一次登錄三個名稱
            Action<string, int, string> aw = (baseName, args, desc) =>
            {
                map[baseName] = new ApiEntry { Name = baseName, Description = desc, StdcallArgs = args };
                map[baseName + "A"] = new ApiEntry { Name = baseName + "A", Description = desc + "（A 結尾：ANSI 字串版本）", StdcallArgs = args };
                map[baseName + "W"] = new ApiEntry { Name = baseName + "W", Description = desc + "（W 結尾：Unicode 版本，字串為 UTF-16 寬字元）", StdcallArgs = args };
            };

            // ---------------- Irvine32 ----------------
            add("WriteString", Irvine + "顯示 EDX 指向、以 0 結尾的字串");
            add("WriteDec", Irvine + "以十進位顯示 EAX（無號）");
            add("WriteInt", Irvine + "以十進位顯示 EAX（有號，帶 +/- 號）");
            add("WriteHex", Irvine + "以 8 位數十六進位顯示 EAX");
            add("WriteHexB", Irvine + "以十六進位顯示 EAX（EBX 指定位元組數）");
            add("WriteBin", Irvine + "以二進位顯示 EAX");
            add("WriteChar", Irvine + "顯示 AL 中的字元");
            add("Crlf", Irvine + "換行");
            add("ReadInt", Irvine + "讀取有號整數到 EAX");
            add("ReadDec", Irvine + "讀取無號整數到 EAX");
            add("ReadHex", Irvine + "讀取十六進位整數到 EAX");
            add("ReadChar", Irvine + "讀取一個字元到 AL（不回顯）");
            add("ReadString", Irvine + "讀取字串到 EDX 指向的緩衝區（ECX = 最大長度，EAX 傳回長度）");
            add("DumpRegs", Irvine + "顯示所有暫存器與旗標");
            add("DumpMem", Irvine + "顯示記憶體內容（ESI=位址、ECX=個數、EBX=單位大小）");
            add("Clrscr", Irvine + "清除螢幕");
            add("Delay", Irvine + "暫停 EAX 毫秒");
            add("Randomize", Irvine + "設定亂數種子");
            add("RandomRange", Irvine + "產生 0 ～ EAX-1 的亂數放在 EAX");
            add("Random32", Irvine + "產生 32 位元亂數到 EAX");
            add("SetTextColor", Irvine + "設定文字顏色（EAX）");
            add("Gotoxy", Irvine + "移動游標（DH=列、DL=行）");
            add("GetMseconds", Irvine + "取得午夜以來的毫秒數到 EAX");
            add("WaitMsg", Irvine + "顯示「按任意鍵繼續」並等待");
            add("StrLength", Irvine + "計算 EDX 指向字串的長度到 EAX");

            // ---------------- kernel32 ----------------
            api("ExitProcess", 1, Kernel32 + "結束整個程式（行程），參數是結束碼，不會返回");
            api("GetStdHandle", 1, Kernel32 + "取得標準輸入／輸出／錯誤的代碼（-10 輸入、-11 輸出、-12 錯誤），回傳 HANDLE");
            aw("WriteConsole", 5, Kernel32 + "把字串寫到主控台視窗（參數：主控台代碼、字串位址、字元數、實際寫出數的位址、保留 0）");
            api("WriteFile", 5, Kernel32 + "寫入檔案或裝置（參數：代碼、緩衝區、位元組數、實際寫入數的位址、OVERLAPPED），成功回傳非 0");
            api("ReadFile", 5, Kernel32 + "從檔案或裝置讀取資料（參數：代碼、緩衝區、要讀的位元組數、實際讀到數的位址、OVERLAPPED）");
            aw("ReadConsole", 5, Kernel32 + "從主控台讀取鍵盤輸入的字元");
            aw("CreateFile", 7, Kernel32 + "開啟或建立檔案（也可開啟裝置），回傳檔案代碼；失敗回傳 INVALID_HANDLE_VALUE（-1）");
            api("CloseHandle", 1, Kernel32 + "關閉檔案、執行緒等核心物件的代碼");
            aw("GetModuleHandle", 1, Kernel32 + "取得已載入模組（EXE／DLL）的基底位址；參數為 NULL 時取得程式自己的 ImageBase");
            api("GetProcAddress", 2, Kernel32 + "依名稱或序號取得 DLL 中函式的位址（常與 LoadLibrary 搭配，在執行時才找 API）");
            aw("LoadLibrary", 1, Kernel32 + "載入 DLL 並回傳模組代碼（就是 DLL 的基底位址）");
            api("FreeLibrary", 1, Kernel32 + "卸載 LoadLibrary 載入的 DLL");
            api("VirtualAlloc", 4, Kernel32 + "以頁（4KB）為單位配置虛擬記憶體，可指定存取權限（例如可讀寫、可執行）");
            api("VirtualFree", 3, Kernel32 + "釋放 VirtualAlloc 配置的記憶體");
            api("VirtualProtect", 4, Kernel32 + "修改記憶體頁的存取權限（例如改成可寫或可執行），並傳回舊權限");
            api("VirtualQuery", 3, Kernel32 + "查詢某個位址所在記憶體區域的狀態、大小與權限");
            api("HeapAlloc", 3, Kernel32 + "從堆積配置記憶體並回傳指標（malloc 的底層常用它）");
            api("HeapFree", 3, Kernel32 + "釋放 HeapAlloc 配置的記憶體");
            api("GetProcessHeap", 0, Kernel32 + "取得程式預設堆積的代碼");
            api("GetLastError", 0, Kernel32 + "取得最近一次 API 失敗的錯誤碼");
            api("SetLastError", 1, Kernel32 + "設定目前執行緒的錯誤碼");
            api("Sleep", 1, Kernel32 + "讓目前執行緒暫停指定的毫秒數");
            api("GetTickCount", 0, Kernel32 + "取得開機至今的毫秒數（常用於計時或當亂數種子）");
            api("QueryPerformanceCounter", 1, Kernel32 + "讀取高精度計時器的計數值（寫到參數指向的 64 位元變數）");
            aw("GetCommandLine", 0, Kernel32 + "取得整個命令列字串的位址");
            api("GetSystemTimeAsFileTime", 1, Kernel32 + "取得目前的 UTC 時間（FILETIME：從 1601 年起的 100 奈秒數）；CRT 啟動時用它產生安全 cookie");
            api("GetCurrentProcessId", 0, Kernel32 + "取得目前程式（行程）的 ID");
            api("GetCurrentThreadId", 0, Kernel32 + "取得目前執行緒的 ID");
            api("CreateThread", 6, Kernel32 + "建立新的執行緒，從指定的函式開始執行");
            api("ExitThread", 1, Kernel32 + "結束目前的執行緒");
            api("InitializeCriticalSection", 1, Kernel32 + "初始化臨界區段（多執行緒互斥用的鎖）");
            api("EnterCriticalSection", 1, Kernel32 + "進入臨界區段（取得鎖，其他執行緒要等待）");
            api("LeaveCriticalSection", 1, Kernel32 + "離開臨界區段（釋放鎖）");
            api("DeleteCriticalSection", 1, Kernel32 + "刪除臨界區段，釋放它使用的資源");
            api("TlsGetValue", 1, Kernel32 + "讀取執行緒區域儲存（TLS）槽位的值");
            api("SetUnhandledExceptionFilter", 1, Kernel32 + "設定「未處理例外」的處理函式（程式當掉前最後的處理）");
            api("IsDebuggerPresent", 0, Kernel32 + "檢查程式是否正在被除錯器執行（非 0 表示是；常見的反除錯技巧）");
            api("MultiByteToWideChar", 6, Kernel32 + "把多位元組字串（ANSI／UTF-8）轉成 UTF-16 寬字元字串");
            api("WideCharToMultiByte", 8, Kernel32 + "把 UTF-16 寬字元字串轉成多位元組字串（ANSI／UTF-8）");
            api("IsDBCSLeadByteEx", 2, Kernel32 + "判斷位元組是否為雙位元組字元（例如 Big5）的前導位元組");
            aw("GetStartupInfo", 1, Kernel32 + "取得程式啟動時的視窗與標準代碼設定");

            // ---------------- user32 ----------------
            aw("MessageBox", 4, User32 + "顯示訊息方塊（參數：父視窗、內容字串、標題字串、按鈕樣式），回傳按下的按鈕（IDOK = 1）");
            add("wsprintfA", User32 + "格式化字串到緩衝區（類似 sprintf，cdecl 可變參數）（A 結尾：ANSI 字串版本）");
            add("wsprintfW", User32 + "格式化字串到緩衝區（類似 sprintf，cdecl 可變參數）（W 結尾：Unicode 版本，字串為 UTF-16 寬字元）");

            // ---------------- C 執行階段（msvcrt／ucrt） ----------------
            add("printf", Crt + "格式化輸出到螢幕（第一個參數是格式字串，例如 \"%d\"）");
            add("puts", Crt + "輸出字串並自動換行");
            add("putchar", Crt + "輸出一個字元");
            add("getchar", Crt + "從鍵盤讀取一個字元");
            add("gets", Crt + "讀取一行字串（不檢查長度，不安全）");
            add("fgets", Crt + "從檔案讀取一行（最多 n-1 個字元）");
            add("scanf", Crt + "格式化輸入（參數是變數的位址）");
            add("sprintf", Crt + "格式化輸出到字串緩衝區");
            add("fprintf", Crt + "格式化輸出到檔案（stdout／stderr 也是檔案）");
            add("vfprintf", Crt + "格式化輸出到檔案（參數以 va_list 傳入）");
            add("fputc", Crt + "輸出一個字元到檔案");
            add("fwrite", Crt + "把一塊記憶體寫到檔案（參數：位址、元素大小、個數、檔案）");
            add("fputs", Crt + "輸出字串到檔案（不自動換行）");
            add("fopen", Crt + "開啟檔案，回傳 FILE 指標");
            add("fclose", Crt + "關閉檔案");
            add("malloc", Crt + "配置記憶體（堆積），回傳位址");
            add("calloc", Crt + "配置 n × size 位元組並清為 0");
            add("realloc", Crt + "改變已配置記憶體的大小（可能搬到新位址）");
            add("free", Crt + "釋放 malloc／calloc／realloc 配置的記憶體");
            add("memcpy", Crt + "複製 n 個位元組（來源與目的不可重疊）");
            add("memset", Crt + "把一塊記憶體的每個位元組設成同一個值");
            add("memmove", Crt + "複製 n 個位元組（允許重疊）");
            add("memcmp", Crt + "比較兩塊記憶體的前 n 個位元組");
            add("strlen", Crt + "計算字串長度（不含結尾的 0）");
            add("wcslen", Crt + "計算寬字元（UTF-16）字串長度");
            add("strcpy", Crt + "複製字串（包含結尾的 0，不檢查長度）");
            add("strncpy", Crt + "最多複製 n 個字元的字串");
            add("strcmp", Crt + "比較兩個字串（相等回傳 0）");
            add("strncmp", Crt + "比較兩個字串的前 n 個字元");
            add("strcat", Crt + "把字串接到另一個字串後面");
            add("strchr", Crt + "在字串中找某個字元，回傳它的位址");
            add("strrchr", Crt + "在字串中從後面找某個字元");
            add("strstr", Crt + "在字串中找子字串");
            add("strerror", Crt + "取得錯誤碼對應的說明字串");
            add("atoi", Crt + "把字串轉成整數");
            add("rand", Crt + "產生虛擬亂數");
            add("srand", Crt + "設定亂數種子");
            add("time", Crt + "取得目前時間（1970 年起的秒數）");
            add("toupper", Crt + "把字元轉成大寫");
            add("tolower", Crt + "把字元轉成小寫");
            add("abort", Crt + "立即異常終止程式");
            add("exit", Crt + "執行清理工作後結束程式（參數是結束碼）");
            add("_exit", Crt + "不做清理，直接結束程式");
            add("signal", Crt + "設定訊號（例如 SIGSEGV、SIGINT）的處理函式");
            add("setlocale", Crt + "設定地區（語系）設定");
            add("localeconv", Crt + "取得目前地區的數字格式設定");
            add("_errno", Crt + "取得 errno 變數的位址（每個執行緒各有一份）");
            add("__getmainargs", CrtStartup + "取得 argc、argv、envp，交給 main（MinGW／舊版 msvcrt）");
            add("__set_app_type", CrtStartup + "告訴 C 執行階段這是主控台（1）還是視窗（2）程式");
            add("_initterm", CrtStartup + "依序呼叫一張函式指標表中的初始化函式（全域建構函式）");
            add("_cexit", CrtStartup + "執行結束前的清理（atexit 註冊的函式、關閉檔案），但不結束程式");
            add("_amsg_exit", CrtStartup + "顯示執行階段錯誤訊息後結束程式");
            add("__p__fmode", CrtStartup + "取得 _fmode（檔案預設文字／二進位模式）變數的位址");
            add("__p__commode", CrtStartup + "取得 _commode（檔案預設認可模式）變數的位址");
            add("_onexit", CrtStartup + "註冊程式結束時要呼叫的函式（atexit 的底層）");
            add("__acrt_iob_func", CrtStartup + "取得 stdin／stdout／stderr 的 FILE 指標（UCRT）");
            add("__iob_func", CrtStartup + "取得 stdin／stdout／stderr 的 FILE 陣列（舊版 msvcrt）");
            add("_iob", CrtStartup + "stdin／stdout／stderr 的 FILE 陣列（資料，不是函式）");
            add("__stdio_common_vfprintf", CrtStartup + "UCRT 的 printf 系列共用實作（printf 會被編譯成呼叫它）");
            add("__setusermatherr", CrtStartup + "設定數學函式錯誤的處理函式");
            add("__initenv", CrtStartup + "環境變數表（資料，不是函式）");
            add("_lock", CrtStartup + "取得 C 執行階段內部的鎖");
            add("_unlock", CrtStartup + "釋放 C 執行階段內部的鎖");
            add("__C_specific_handler", CrtStartup + "x64 結構化例外處理（__try／__except）的處理常式");

            // ---------------- 編譯器輔助函式 ----------------
            add("__security_check_cookie", Helper + "檢查堆疊上的安全 cookie 是否被改寫（MSVC /GS），被改寫就立即終止程式");
            add("__stack_chk_fail", Helper + "GCC 堆疊保護：發現堆疊 canary 被改寫時呼叫，會中止程式");
            add("__chkstk", Helper + "大型區域變數的堆疊探測（每 4KB 碰一次，讓堆疊正常擴張）；32 位元 MSVC 版本會直接把 ESP 減掉 EAX");
            add("___chkstk_ms", Helper + "MinGW 的堆疊探測（只探測 EAX 位元組，呼叫者之後自己 sub esp, eax）");
            add("_chkstk", Helper + "大型區域變數的堆疊探測（同 __chkstk）");
            add("_alloca_probe", Helper + "大型區域變數的堆疊探測（同 __chkstk）");
            add("__main", Helper + "MinGW 的 main 開頭會呼叫它，執行全域建構函式等初始化");
            add("_alloca", Helper + "在堆疊上動態配置記憶體（函式返回時自動釋放）");
            return map;
        }

        private static readonly Regex SizePrefix = new Regex(@"^(byte|word|dword|fword|qword|tbyte|xmmword|near|far|short)\s+(ptr\s+)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SegmentPrefix = new Regex(@"^(cs|ds|es|ss|fs|gs)\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OrdinalImport = new Regex(@"^(?<dll>[\w.]+?\.dll)_(?<ord>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DisassemblerSymbol = new Regex(@"^(sub|loc|locret|j)_[0-9A-Fa-f]{1,16}(_\d+)?$", RegexOptions.Compiled);
        private static readonly Regex StdcallSuffix = new Regex(@"@(\d{1,4})$", RegexOptions.Compiled);

        /// <summary>
        /// 去掉反組譯器與組譯器加上的修飾，取出函式名稱：
        /// "dword ptr [__imp_ExitProcess]" → "ExitProcess"、"KERNEL32.dll!ExitProcess" → "ExitProcess"、"_MessageBoxA@16" → "MessageBoxA"、
        /// "__imp__printf" → "printf"、"j_printf" → "printf"。已知的函式回傳標準寫法；未知的回傳去掉修飾後的名稱；無法取出名稱時回傳 null。
        /// </summary>
        public static string Normalize(string name)
        {
            string bare = StripDecoration(name);
            if (string.IsNullOrEmpty(bare)) return null;
            var e = Lookup(bare);
            return e != null ? e.Name : bare;
        }

        /// <summary>函式說明；不認得時回傳 null。</summary>
        public static string Describe(string name)
        {
            string bare = StripDecoration(name);
            if (string.IsNullOrEmpty(bare)) return null;
            var e = Lookup(bare);
            return e != null ? e.Description : null;
        }

        /// <summary>是否為「透過匯入表（IAT）」的寫法，例如 [__imp_X]、__imp__X@4。</summary>
        public static bool IsImportReference(string operand)
        {
            return !string.IsNullOrEmpty(operand) && operand.IndexOf("__imp_", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>是否為反組譯器自動產生的程式位置名稱（sub_401560、loc_401578、j_printf、__imp_X）。</summary>
        public static bool IsDisassemblerSymbol(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.Trim();
            if (n.StartsWith("__imp_", StringComparison.OrdinalIgnoreCase)) return true;
            if (DisassemblerSymbol.IsMatch(n)) return true;
            return n.Length > 2 && n.StartsWith("j_", StringComparison.Ordinal);
        }

        /// <summary>
        /// 32 位元 stdcall 函式返回時會清掉的參數位元組數（ret n 的 n）：名稱帶 @N 時就是 N，否則查已知的 Win32 API；
        /// cdecl 或不清楚時回傳 -1。
        /// </summary>
        public static int StdcallArgumentBytes(string name)
        {
            string cleaned = StripWrapper(name);
            if (string.IsNullOrEmpty(cleaned)) return -1;
            var m = StdcallSuffix.Match(cleaned);
            int n;
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n % 4 == 0)
                return n;
            var e = Lookup(StripDecoration(name));
            return e != null && e.StdcallArgs >= 0 ? e.StdcallArgs * 4 : -1;
        }

        /// <summary>以序號匯入的符號（例如 __imp_WS2_32.dll_115）的說明；不是這種寫法時回傳 null。</summary>
        public static string DescribeOrdinalImport(string name)
        {
            string bare = StripDecoration(name);
            if (string.IsNullOrEmpty(bare)) return null;
            var m = OrdinalImport.Match(bare);
            if (!m.Success) return null;
            return "以序號 " + m.Groups["ord"].Value + " 匯入的 " + m.Groups["dll"].Value + " 函式（沒有名稱，要對照該 DLL 的匯出表才知道是哪個函式）";
        }

        /// <summary>去掉大小、區段、方括號、DLL 限定詞，只留下其中的符號名稱。</summary>
        private static string StripWrapper(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string t = text.Trim();
            if (t.Length > 512) t = t.Substring(0, 512);
            if (t.StartsWith("offset ", StringComparison.OrdinalIgnoreCase)) t = t.Substring(7).Trim();
            for (int i = 0; i < 3; i++)
            {
                var m = SizePrefix.Match(t);
                if (m.Success && m.Length < t.Length) t = t.Substring(m.Length).Trim();
                var s = SegmentPrefix.Match(t);
                if (s.Success && s.Length < t.Length) t = t.Substring(s.Length).Trim();
            }
            int open = t.IndexOf('[');
            if (open >= 0)
            {
                int close = t.IndexOf(']', open + 1);
                t = (close > open ? t.Substring(open + 1, close - open - 1) : t.Substring(open + 1)).Trim();
                var s = SegmentPrefix.Match(t);
                if (s.Success && s.Length < t.Length) t = t.Substring(s.Length).Trim();
                // [rip+__imp_X]：只取符號部分
                int plus = t.LastIndexOf('+');
                if (plus >= 0 && t.IndexOf("__imp_", StringComparison.OrdinalIgnoreCase) > plus) t = t.Substring(plus + 1).Trim();
            }
            int bang = t.LastIndexOf('!');
            if (bang >= 0 && bang < t.Length - 1) t = t.Substring(bang + 1).Trim();
            // 只保留符號字元（去掉後面的空白、註解、括號等）
            int end = 0;
            while (end < t.Length && IsSymbolChar(t[end])) end++;
            return t.Substring(0, end);
        }

        private static bool IsSymbolChar(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                   c == '_' || c == '@' || c == '$' || c == '?' || c == '.';
        }

        private static string StripDecoration(string text)
        {
            string t = StripWrapper(text);
            if (string.IsNullOrEmpty(t)) return null;
            int imp = t.IndexOf("__imp_", StringComparison.OrdinalIgnoreCase);
            if (imp >= 0) t = t.Substring(imp + 6);
            // stdcall／fastcall 修飾：_Func@8、@Func@8
            t = t.TrimStart('@');
            int at = t.IndexOf('@');
            if (at > 0) t = t.Substring(0, at);
            return t.Length > 0 ? t : null;
        }

        private static ApiEntry Lookup(string bare)
        {
            if (string.IsNullOrEmpty(bare)) return null;
            ApiEntry e;
            // 依序嘗試原名稱、去掉一個前置底線、再去掉一個……（_printf → printf；_exit、__getmainargs 本來就有底線，原名稱就找得到）
            string cand = bare;
            for (int i = 0; i < 4; i++)
            {
                if (Map.TryGetValue(cand, out e)) return e;
                if (cand.Length < 2 || cand[0] != '_') break;
                cand = cand.Substring(1);
            }
            // 反組譯器替匯入跳板取的名稱：j_printf
            if (bare.Length > 2 && bare.StartsWith("j_", StringComparison.Ordinal) && Map.TryGetValue(bare.Substring(2), out e)) return e;
            return null;
        }
    }
}
