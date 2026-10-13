using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Assam.Core.Instructions;

namespace Assam.Core.Analysis
{
    /// <summary>一行組合語言拆解後的結果。</summary>
    public sealed class AsmLine
    {
        public AsmLine()
        {
            Operands = new List<string>();
        }

        public int LineNumber { get; set; }
        public string Raw { get; set; }
        public string Label { get; set; }
        /// <summary>REP/LOCK 等前綴。</summary>
        public string Prefix { get; set; }
        public string Mnemonic { get; set; }
        public string OperandText { get; set; }
        public List<string> Operands { get; private set; }
        public string Comment { get; set; }
        /// <summary>標籤是 "name db ..."、"main PROC" 這種不帶冒號的名稱。</summary>
        public bool LabelIsName { get; set; }

        public string MnemonicLower
        {
            get { return Mnemonic == null ? null : Mnemonic.ToLowerInvariant(); }
        }

        public bool HasMnemonic
        {
            get { return !string.IsNullOrEmpty(Mnemonic); }
        }

        public bool IsBlank
        {
            get { return !HasMnemonic && string.IsNullOrEmpty(Label) && string.IsNullOrEmpty(Comment); }
        }

        public bool IsCommentOnly
        {
            get { return !HasMnemonic && string.IsNullOrEmpty(Label) && !string.IsNullOrEmpty(Comment); }
        }

        /// <summary>去掉註解後的程式碼部分。</summary>
        public string CodeText
        {
            get
            {
                string s = string.Empty;
                if (!string.IsNullOrEmpty(Label)) s = LabelIsName ? Label + " " : Label + ": ";
                if (!string.IsNullOrEmpty(Prefix)) s += Prefix + " ";
                if (HasMnemonic) s += Mnemonic;
                if (!string.IsNullOrEmpty(OperandText)) s += " " + OperandText;
                return s.Trim();
            }
        }
    }

    /// <summary>通用的行解析器，可依架構決定註解符號。</summary>
    public static class AsmLineParser
    {
        private static readonly Regex LabelRegex = new Regex(@"^\s*([A-Za-z_.$@?][\w.$@?]*|\d+)\s*:(?!:)", RegexOptions.Compiled);

        /// <summary>出現在名稱後面的資料定義/虛擬指令（例如 "msg db 'Hi',0"、"main PROC"、"N EQU 10"）。</summary>
        public static readonly HashSet<string> NamedDirectives = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "db", "dw", "dd", "dq", "dt", "df", "dp", "byte", "sbyte", "word", "sword", "dword", "sdword", "fword",
            "qword", "tbyte", "real4", "real8", "real10", "equ", "=", "proc", "endp", "macro", "endm", "struct",
            "struc", "ends", "segment", "label", "textequ", "resb", "resw", "resd", "resq", "rest", "ds", "dbit",
            "bit", "sbit", "data", "xdata", "idata", "set", "union", "record", "typedef"
        };

        /// <summary>前面一定是名稱的虛擬指令（不會出現在指令後面）。</summary>
        private static readonly HashSet<string> AlwaysNamed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "db", "dw", "dd", "dq", "dt", "df", "dp", "real4", "real8", "real10", "equ", "=", "proc", "endp", "macro",
            "endm", "struct", "struc", "ends", "segment", "label", "textequ", "resb", "resw", "resd", "resq", "rest",
            "dbit", "sbit", "union", "record", "typedef"
        };

        /// <summary>同時是運算元大小關鍵字（inc dword ptr [x]），只有在第一個字不是 x86 指令時才是資料定義。</summary>
        private static readonly HashSet<string> SizeKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "byte", "sbyte", "word", "sword", "dword", "sdword", "fword", "qword", "tbyte"
        };

        private static readonly HashSet<string> Prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "rep", "repe", "repz", "repne", "repnz", "lock"
        };

        public static List<AsmLine> Parse(string code, AsmArch arch)
        {
            var result = new List<AsmLine>();
            if (code == null) return result;
            var lines = code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
                result.Add(ParseLine(lines[i], i + 1, arch));
            return result;
        }

        public static AsmLine ParseLine(string raw, int lineNumber, AsmArch arch)
        {
            var line = new AsmLine { LineNumber = lineNumber, Raw = raw ?? string.Empty };
            string comment;
            string code = StripComment(line.Raw, arch, out comment).Trim();
            line.Comment = comment;
            if (code.Length == 0) return line;

            // 標籤（可能有多個，例如 "a: b: mov ..."，只取第一個，其餘視為程式）
            var m = LabelRegex.Match(code);
            if (m.Success)
            {
                line.Label = m.Groups[1].Value;
                code = code.Substring(m.Length).Trim();
                if (code.Length == 0) return line;
            }

            string first, rest;
            SplitFirstToken(code, out first, out rest);

            if (string.IsNullOrEmpty(line.Label) && rest.Length > 0)
            {
                string second, afterSecond;
                SplitFirstToken(rest, out second, out afterSecond);
                bool isName = AlwaysNamed.Contains(second) ||
                              (SizeKeywords.Contains(second) && !IsX86Instruction(first)) ||
                              (NamedDirectives.Contains(second) && !SizeKeywords.Contains(second) && !IsKnownInstruction(first));
                if (isName)
                {
                    line.Label = first;
                    line.LabelIsName = true;
                    first = second;
                    rest = afterSecond;
                }
            }

            if (Prefixes.Contains(first) && rest.Length > 0)
            {
                line.Prefix = first;
                SplitFirstToken(rest, out first, out rest);
            }

            line.Mnemonic = first;
            line.OperandText = rest.Trim();
            line.Operands.AddRange(SplitOperands(line.OperandText));
            return line;
        }

        private static bool IsKnownInstruction(string token)
        {
            foreach (var set in InstructionCatalog.Sets.Values)
            {
                if (set.IsKnown(token)) return true;
            }
            return false;
        }

        private static bool IsX86Instruction(string token)
        {
            var x86 = InstructionCatalog.Get("x86");
            return x86 != null && x86.IsKnown(token);
        }

        private static void SplitFirstToken(string text, out string first, out string rest)
        {
            text = text.Trim();
            int i = 0;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            first = text.Substring(0, i);
            rest = i < text.Length ? text.Substring(i).Trim() : string.Empty;
        }

        /// <summary>以逗號分隔運算元（忽略括號、中括號、大括號與字串內的逗號）。</summary>
        public static List<string> SplitOperands(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            int depth = 0;
            char quote = '\0';
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '\'' || c == '"') { quote = c; continue; }
                if (c == '[' || c == '(' || c == '{') depth++;
                else if (c == ']' || c == ')' || c == '}') depth = Math.Max(0, depth - 1);
                else if (c == ',' && depth == 0)
                {
                    result.Add(text.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            result.Add(text.Substring(start).Trim());
            return result.Where(s => s.Length > 0).ToList();
        }

        /// <summary>依架構移除註解並回傳註解內容。</summary>
        public static string StripComment(string raw, AsmArch arch, out string comment)
        {
            comment = null;
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            char quote = '\0';
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '\'' || c == '"')
                {
                    // AT&T/GAS 的 'c 字元寫法不一定成對，只有在後面還有相同引號時才視為字串
                    if (raw.IndexOf(c, i + 1) > i) quote = c;
                    continue;
                }
                int len = CommentStartLength(raw, i, arch);
                if (len > 0)
                {
                    comment = raw.Substring(i + len).Trim();
                    return raw.Substring(0, i);
                }
            }
            return raw;
        }

        private static int CommentStartLength(string s, int i, AsmArch arch)
        {
            char c = s[i];
            char next = i + 1 < s.Length ? s[i + 1] : '\0';
            bool atStart = s.Substring(0, i).Trim().Length == 0;

            if (c == '/' && next == '/') return 2;
            if (c == ';')
                return 1;

            if (c == '#')
            {
                switch (arch)
                {
                    case AsmArch.Mips:
                    case AsmArch.RiscV:
                    case AsmArch.X86Att:
                        return 1;
                    case AsmArch.Arm:
                    case AsmArch.Arm64:
                    case AsmArch.I8051:
                        return 0;
                    default:
                        if (atStart || next == ' ' || next == '\t' || next == '\0' || next == '#') return 1;
                        return 0;
                }
            }

            if (c == '@')
            {
                if (arch == AsmArch.Arm) return 1;
                if (arch == AsmArch.Unknown && (next == ' ' || next == '\t' || next == '\0') && (atStart || (i > 0 && char.IsWhiteSpace(s[i - 1]))))
                    return 1;
            }
            return 0;
        }
    }
}
