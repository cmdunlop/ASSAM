using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Assam.Core.Text;

namespace Assam.Core.Analysis
{
    /// <summary>程式中出現的符號（標籤、變數、常數），供運算元說明使用。</summary>
    public sealed class SymbolTable
    {
        public SymbolTable()
        {
            CodeLabels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            DataLabels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Constants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>標籤 → 行號。</summary>
        public Dictionary<string, int> CodeLabels { get; private set; }
        public Dictionary<string, int> DataLabels { get; private set; }
        public Dictionary<string, string> Constants { get; private set; }
    }

    /// <summary>把單一運算元翻譯成中文說明（暫存器、立即值、定址模式……）。</summary>
    public static class OperandAnalyzer
    {
        private static readonly Regex SizePrefix = new Regex(@"^(byte|word|dword|qword|tbyte|fword|sbyte|sword|sdword|real4|real8)\s*(ptr\b)?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SegmentOverride = new Regex(@"^(cs|ds|es|ss|fs|gs)\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OffsetBase = new Regex(@"^(?<off>[^()]*)\((?<inner>[^()]*)\)$", RegexOptions.Compiled);

        public static string Describe(AsmArch arch, string operand, SymbolTable symbols)
        {
            if (string.IsNullOrWhiteSpace(operand)) return null;
            string o = operand.Trim();
            symbols = symbols ?? new SymbolTable();
            switch (arch)
            {
                case AsmArch.X86Att: return DescribeAtt(o, symbols);
                case AsmArch.Arm:
                case AsmArch.Arm64: return DescribeArm(arch, o, symbols);
                case AsmArch.Mips:
                case AsmArch.RiscV: return DescribeMipsLike(arch, o, symbols);
                case AsmArch.I8051: return Describe8051(o, symbols);
                case AsmArch.Avr: return DescribeAvr(o, symbols);
                default: return DescribeIntel(o, symbols);
            }
        }

        // ---------------- 共用 ----------------

        public static string DescribeNumber(string text)
        {
            long v;
            int radix;
            if (!AsmNumber.TryParse(text, out v, out radix)) return null;
            if (radix == 0)
            {
                string inner = text.Trim().TrimStart('#').Trim();
                return "字元常數 " + inner + "（ASCII " + v.ToString(CultureInfo.InvariantCulture) + " = " + v.ToString("X2", CultureInfo.InvariantCulture) + "h）";
            }
            string dec = v.ToString(CultureInfo.InvariantCulture);
            string hex = v < 0 ? "-0x" + (-v).ToString("X", CultureInfo.InvariantCulture) : "0x" + v.ToString("X", CultureInfo.InvariantCulture);
            if (radix == 10)
                return "立即值 " + dec + (Math.Abs(v) >= 10 ? "（十六進位 " + hex + "）" : string.Empty);
            return "立即值 " + text.Trim().TrimStart('#') + "（" + AsmNumber.RadixName(radix) + "）= 十進位 " + dec;
        }

        private static string DescribeSymbol(string name, SymbolTable symbols, bool dataIsMemory)
        {
            string n = name.Trim();
            if (symbols.CodeLabels.ContainsKey(n)) return "標籤 " + n + "（程式第 " + symbols.CodeLabels[n] + " 行的位置）";
            if (symbols.DataLabels.ContainsKey(n))
                return dataIsMemory ? "變數 " + n + "（記憶體運算元，直接定址：讀寫 " + n + " 位址中的資料）" : "變數 " + n + " 的位址";
            string c;
            if (symbols.Constants.TryGetValue(n, out c)) return "常數 " + n + "（= " + c + "）";
            if (Regex.IsMatch(n, @"^[A-Za-z_.$@?][\w.$@?]*$")) return "符號 " + n + "（標籤、變數或外部定義的常數）";
            return null;
        }

        // ---------------- x86 Intel ----------------

        private static string DescribeIntel(string o, SymbolTable symbols)
        {
            string lower = o.ToLowerInvariant();
            string sizeText = null;
            var m = SizePrefix.Match(o);
            if (m.Success && m.Length < o.Length)
            {
                sizeText = SizeName(m.Groups[1].Value.ToLowerInvariant());
                o = o.Substring(m.Length).Trim();
                lower = o.ToLowerInvariant();
            }

            string segment = null;
            var seg = SegmentOverride.Match(o);
            if (seg.Success && seg.Length < o.Length)
            {
                segment = seg.Groups[1].Value.ToUpperInvariant();
                o = o.Substring(seg.Length).Trim();
                lower = o.ToLowerInvariant();
            }

            string reg = RegisterInfo.Describe(AsmArch.X86Intel, lower);
            if (reg != null) return reg;

            foreach (var op in new[] { "offset", "addr" })
            {
                if (lower.StartsWith(op + " ", StringComparison.Ordinal))
                    return "OFFSET " + o.Substring(op.Length).Trim() + "：取得變數的位址（是一個立即值，不是記憶體內容）";
            }
            if (lower.StartsWith("lengthof ", StringComparison.Ordinal)) return "LENGTHOF：陣列 " + o.Substring(9).Trim() + " 的元素個數";
            if (lower.StartsWith("sizeof ", StringComparison.Ordinal)) return "SIZEOF：陣列 " + o.Substring(7).Trim() + " 佔用的位元組數（元素個數 × 元素大小）";
            if (lower.StartsWith("type ", StringComparison.Ordinal)) return "TYPE：" + o.Substring(5).Trim() + " 每個元素的大小（位元組）";

            if (o.IndexOf('[') >= 0)
            {
                string inner = o.Replace("[", "+").Replace("]", string.Empty).Trim().TrimStart('+');
                string mode = ClassifyAddress(inner, s => RegisterInfo.X86RegisterBits(s) > 0, symbols);
                return "記憶體運算元（" + mode + "），有效位址 = " + inner.Replace("+", " + ").Replace("  ", " ") +
                       (segment != null ? "，使用 " + segment + " 區段" : string.Empty) +
                       (sizeText != null ? "，大小為 " + sizeText : string.Empty);
            }

            string num = DescribeNumber(o);
            if (num != null) return num;
            if (o.Length >= 2 && (o[0] == '\'' || o[0] == '"')) return "字串 " + o;

            if (o.IndexOf('+') > 0 || o.IndexOf('-') > 0)
            {
                string first = Regex.Split(o, @"[+\-]")[0].Trim();
                if (symbols.DataLabels.ContainsKey(first))
                    return "記憶體運算元（直接定址＋位移），位址 = " + o + (sizeText != null ? "，大小為 " + sizeText : string.Empty);
                return "運算式 " + o + "（組譯時計算出的值）";
            }

            string sym = DescribeSymbol(o, symbols, true);
            if (sym != null && sizeText != null) sym += "，大小為 " + sizeText;
            return sym ?? o;
        }

        private static string SizeName(string size)
        {
            switch (size)
            {
                case "byte": return "8 位元（BYTE）";
                case "sbyte": return "8 位元有號（SBYTE）";
                case "word": return "16 位元（WORD）";
                case "sword": return "16 位元有號（SWORD）";
                case "dword": return "32 位元（DWORD）";
                case "sdword": return "32 位元有號（SDWORD）";
                case "real4": return "32 位元浮點數（REAL4）";
                case "fword": return "48 位元（FWORD）";
                case "qword": return "64 位元（QWORD）";
                case "real8": return "64 位元浮點數（REAL8）";
                case "tbyte": return "80 位元（TBYTE）";
                default: return size;
            }
        }

        /// <summary>依位址運算式中的暫存器、比例、位移判斷定址模式名稱。</summary>
        public static string ClassifyAddress(string expression, Func<string, bool> isRegister, SymbolTable symbols)
        {
            var terms = Regex.Split(expression, @"[+\-]").Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            int regs = 0, scaled = 0, disp = 0;
            bool hasLabel = false;
            foreach (var t in terms)
            {
                if (t.IndexOf('*') >= 0)
                {
                    var parts = t.Split('*').Select(p => p.Trim()).ToArray();
                    if (parts.Any(isRegister)) { scaled++; continue; }
                    disp++;
                    continue;
                }
                if (isRegister(t)) regs++;
                else
                {
                    disp++;
                    long v;
                    if (!AsmNumber.TryParse(t, out v)) hasLabel = true;
                }
            }

            if (regs == 0 && scaled == 0) return "直接定址";
            if (scaled > 0) return disp > 0 ? "比例索引定址（基底 + 索引×比例 + 位移）" : "比例索引定址（基底 + 索引×比例）";
            if (regs == 1 && disp == 0) return "暫存器間接定址";
            if (regs == 1) return hasLabel ? "索引定址（變數 + 索引暫存器）" : "基底相對定址（暫存器 + 位移）";
            return disp > 0 ? "基底 + 索引 + 位移定址" : "基底 + 索引定址";
        }

        // ---------------- x86 AT&T ----------------

        private static string DescribeAtt(string o, SymbolTable symbols)
        {
            if (o.StartsWith("*", StringComparison.Ordinal)) return "間接目標：跳到 " + o.Substring(1) + " 所存的位址";
            if (o.StartsWith("%", StringComparison.Ordinal))
                return RegisterInfo.Describe(AsmArch.X86Att, o) ?? "暫存器 " + o;
            if (o.StartsWith("$", StringComparison.Ordinal))
            {
                string v = o.Substring(1);
                long n;
                if (AsmNumber.TryParse(v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? v : v.TrimStart('$'), out n))
                    return "立即值 " + n.ToString(CultureInfo.InvariantCulture) + "（AT&T 以 $ 表示立即值）";
                return "立即值：" + v + " 的位址（$符號 = 取位址）";
            }
            var m = OffsetBase.Match(o);
            if (m.Success)
            {
                string off = m.Groups["off"].Value.Trim();
                var parts = m.Groups["inner"].Value.Split(',').Select(p => p.Trim()).ToArray();
                string baseReg = parts.Length > 0 ? parts[0] : string.Empty;
                string index = parts.Length > 1 ? parts[1] : string.Empty;
                string scale = parts.Length > 2 ? parts[2] : "1";
                var expr = new List<string>();
                if (baseReg.Length > 0) expr.Add(baseReg);
                if (index.Length > 0) expr.Add(index + (scale != "1" ? "×" + scale : string.Empty));
                if (off.Length > 0) expr.Add(off);
                string mode = index.Length > 0 ? (scale != "1" ? "比例索引定址" : "基底 + 索引定址") : off.Length > 0 ? "基底相對定址" : "暫存器間接定址";
                return "記憶體運算元（" + mode + "），有效位址 = " + string.Join(" + ", expr) + "（AT&T 寫法 位移(基底,索引,比例)）";
            }
            return DescribeSymbol(o, symbols, true) ?? o;
        }

        // ---------------- ARM / ARM64 ----------------

        private static string DescribeArm(AsmArch arch, string o, SymbolTable symbols)
        {
            string lower = o.ToLowerInvariant();
            if (lower.StartsWith("{", StringComparison.Ordinal)) return "暫存器清單 " + o + "（一次存取多個暫存器）";
            if (lower.StartsWith("=", StringComparison.Ordinal)) return "=" + o.Substring(1) + "：由組譯器把常數／位址放進常數池再載入";
            if (Regex.IsMatch(lower, @"^(lsl|lsr|asr|ror)\s+#?\w+"))
                return "桶型移位器：先把前一個運算元 " + lower.Substring(0, 3).ToUpperInvariant() + " " + Regex.Replace(o.Substring(3).Trim(), "^#", string.Empty) + " 位元再參與運算";
            if (lower.StartsWith("[", StringComparison.Ordinal))
            {
                bool writeBack = lower.EndsWith("!", StringComparison.Ordinal);
                string inner = lower.TrimEnd('!').Trim().TrimStart('[').TrimEnd(']');
                var parts = inner.Split(',').Select(p => p.Trim()).ToArray();
                string baseReg = parts[0].ToUpperInvariant();
                if (parts.Length == 1) return "記憶體運算元（暫存器間接定址），位址 = " + baseReg;
                string offset = parts[1].TrimStart('#');
                string text = "記憶體運算元（基底 + 位移），位址 = " + baseReg + " + " + offset;
                if (parts.Length > 2) text += "（" + parts[2] + "）";
                if (writeBack) text += "；! 代表存取前先把 " + baseReg + " 更新為新位址（前索引回寫）";
                return text;
            }
            if (lower.StartsWith("#", StringComparison.Ordinal)) return DescribeNumber(o) ?? "立即值 " + o.Substring(1);
            string reg = RegisterInfo.Describe(arch, lower);
            if (reg != null) return reg;
            return DescribeNumber(o) ?? DescribeSymbol(o, symbols, false) ?? o;
        }

        // ---------------- MIPS / RISC-V ----------------

        private static string DescribeMipsLike(AsmArch arch, string o, SymbolTable symbols)
        {
            string lower = o.ToLowerInvariant();
            string reg = RegisterInfo.Describe(arch, lower);
            if (reg != null) return reg;
            var m = OffsetBase.Match(o);
            if (m.Success)
            {
                string off = m.Groups["off"].Value.Trim();
                string baseReg = m.Groups["inner"].Value.Trim();
                if (off.StartsWith("%", StringComparison.Ordinal)) return "記憶體運算元，位址 = " + baseReg + " + " + off + "（重定位）";
                return "記憶體運算元（基底 + 位移定址），位址 = " + baseReg + " + " + (off.Length == 0 ? "0" : off);
            }
            return DescribeNumber(o) ?? DescribeSymbol(o, symbols, false) ?? o;
        }

        // ---------------- 8051 ----------------

        private static string Describe8051(string o, SymbolTable symbols)
        {
            string lower = o.ToLowerInvariant().Replace(" ", string.Empty);
            if (lower.StartsWith("#", StringComparison.Ordinal))
            {
                string inner = o.Trim().Substring(1).Trim();
                string desc = DescribeNumber(inner);
                if (desc != null) return desc + "（# 表示立即值）";
                return "立即值：符號 " + inner + " 的值";
            }
            switch (lower)
            {
                case "@r0":
                case "@r1": return o.ToUpperInvariant() + "：暫存器間接定址（存取位址 = " + o.Substring(1).ToUpperInvariant() + " 內容的內部 RAM）";
                case "@dptr": return "@DPTR：以 DPTR 為位址存取外部記憶體";
                case "@a+dptr": return "@A+DPTR：索引定址，位址 = A + DPTR（查表用，讀程式記憶體）";
                case "@a+pc": return "@A+PC：索引定址，位址 = A + PC";
                case "ab": return "AB：A 與 B 兩個暫存器";
            }
            var bit = Regex.Match(lower, @"^(p[0-3]|acc|psw|b|tcon|scon|ie|ip)\.([0-7])$");
            if (bit.Success) return o.ToUpperInvariant() + "：位元定址（" + bit.Groups[1].Value.ToUpperInvariant() + " 的第 " + bit.Groups[2].Value + " 位元）";
            if (lower.StartsWith("/", StringComparison.Ordinal)) return o + "：取該位元的反相值";
            string reg = RegisterInfo.Describe(AsmArch.I8051, lower);
            if (reg != null) return reg;
            long v;
            if (AsmNumber.TryParse(o, out v))
                return "直接定址：內部 RAM／SFR 位址 " + o + "（" + v.ToString(CultureInfo.InvariantCulture) + "），讀寫的是該位址的內容（不是常數，常數要加 #）";
            if (o == "$") return "$：目前這一行的位址（SJMP $ 代表原地跳躍）";
            return DescribeSymbol(o, symbols, true) ?? o;
        }

        // ---------------- AVR ----------------

        private static string DescribeAvr(string o, SymbolTable symbols)
        {
            string lower = o.ToLowerInvariant().Replace(" ", string.Empty);
            var ptr = Regex.Match(lower, @"^(-?)([xyz])(\+?)(\d*)$");
            if (ptr.Success && (ptr.Groups[1].Value.Length > 0 || ptr.Groups[3].Value.Length > 0 || lower.Length == 1))
            {
                string p = ptr.Groups[2].Value.ToUpperInvariant();
                if (ptr.Groups[1].Value.Length > 0) return "-" + p + "：先把指標 " + p + " 減 1，再存取該位址";
                if (ptr.Groups[3].Value.Length > 0 && ptr.Groups[4].Value.Length > 0) return p + "+" + ptr.Groups[4].Value + "：存取位址 " + p + " + " + ptr.Groups[4].Value + "（指標不變）";
                if (ptr.Groups[3].Value.Length > 0) return p + "+：存取 " + p + " 指向的位址後，" + p + " 加 1";
                return RegisterInfo.Describe(AsmArch.Avr, lower);
            }
            var hl = Regex.Match(lower, @"^(low|high|lo8|hi8)\((.+)\)$");
            if (hl.Success) return "取 " + hl.Groups[2].Value + " 的" + (hl.Groups[1].Value.StartsWith("l", StringComparison.Ordinal) ? "低" : "高") + "位元組";
            string reg = RegisterInfo.Describe(AsmArch.Avr, lower);
            if (reg != null) return reg;
            return DescribeNumber(o) ?? DescribeSymbol(o, symbols, false) ?? o;
        }
    }
}
