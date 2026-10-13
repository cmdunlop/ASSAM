using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Assam.Core.Text;

namespace Assam.Core.Simulation
{
    /// <summary>識別字解析的結果。</summary>
    public sealed class SymbolValue
    {
        public long Value { get; set; }
        /// <summary>顯示在推導中的樣子，例如 "ESI(0x00404000)"。</summary>
        public string Display { get; set; }
        public bool IsRegister { get; set; }
        public bool IsDataLabel { get; set; }
        public bool IsCodeLabel { get; set; }
        /// <summary>資料標籤的元素大小（位元組）。</summary>
        public int ElementSize { get; set; }
        public int Length { get; set; }
    }

    /// <summary>
    /// 組合語言運算式求值：數字、常數、標籤、暫存器、+ - * / MOD SHL SHR AND OR、括號、[ ]、
    /// OFFSET、LENGTHOF、SIZEOF、TYPE、$（目前位址）。
    /// </summary>
    public sealed class ExpressionEvaluator
    {
        private readonly Func<string, SymbolValue> _resolve;
        private List<string> _tokens;
        private int _pos;
        private StringBuilder _display;

        public ExpressionEvaluator(Func<string, SymbolValue> resolve)
        {
            _resolve = resolve;
        }

        public bool UsedRegister { get; private set; }
        public bool UsedDataLabel { get; private set; }
        public bool UsedCodeLabel { get; private set; }
        public bool UsedOffsetOperator { get; private set; }
        /// <summary>第一個用到的資料標籤（用來推斷記憶體運算元的大小）。</summary>
        public SymbolValue FirstDataLabel { get; private set; }
        public string Display { get; private set; }
        public string Error { get; private set; }

        public bool TryEvaluate(string expression, out long value)
        {
            value = 0;
            UsedRegister = UsedDataLabel = UsedCodeLabel = UsedOffsetOperator = false;
            FirstDataLabel = null;
            Error = null;
            _display = new StringBuilder();
            try
            {
                _tokens = Tokenize(expression ?? string.Empty);
                _pos = 0;
                if (_tokens.Count == 0) { Error = "空的運算式"; return false; }
                value = ParseOr();
                if (_pos < _tokens.Count) { Error = "無法理解「" + _tokens[_pos] + "」"; return false; }
                Display = _display.ToString().Trim();
                return true;
            }
            catch (FormatException ex)
            {
                Error = ex.Message;
                return false;
            }
        }

        private static List<string> Tokenize(string s)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '\'' || c == '"')
                {
                    int end = s.IndexOf(c, i + 1);
                    if (end < 0) throw new FormatException("字元常數缺少結尾引號");
                    tokens.Add(s.Substring(i, end - i + 1));
                    i = end + 1;
                    continue;
                }
                if ("+-*/()[]".IndexOf(c) >= 0) { tokens.Add(c.ToString()); i++; continue; }
                if (char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '@' || c == '?' || c == '.')
                {
                    int start = i;
                    i++;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '$' || s[i] == '@' || s[i] == '?' || s[i] == '.')) i++;
                    tokens.Add(s.Substring(start, i - start));
                    continue;
                }
                throw new FormatException("無法理解的字元「" + c + "」");
            }
            return tokens;
        }

        private string Peek()
        {
            return _pos < _tokens.Count ? _tokens[_pos] : null;
        }

        private bool Accept(string t)
        {
            if (_pos < _tokens.Count && string.Equals(_tokens[_pos], t, StringComparison.OrdinalIgnoreCase))
            {
                _pos++;
                return true;
            }
            return false;
        }

        private long ParseOr()
        {
            long v = ParseAdd();
            while (true)
            {
                if (Accept("or")) { _display.Append(" OR "); v |= ParseAdd(); }
                else if (Accept("and")) { _display.Append(" AND "); v &= ParseAdd(); }
                else if (Accept("xor")) { _display.Append(" XOR "); v ^= ParseAdd(); }
                else return v;
            }
        }

        private long ParseAdd()
        {
            long v = ParseMul();
            while (true)
            {
                if (Accept("+")) { _display.Append(" + "); v = unchecked(v + ParseMul()); }
                else if (Accept("-")) { _display.Append(" - "); v = unchecked(v - ParseMul()); }
                else return v;
            }
        }

        private long ParseMul()
        {
            long v = ParseUnary();
            while (true)
            {
                if (Accept("*")) { _display.Append("×"); v = unchecked(v * ParseUnary()); }
                else if (Accept("/"))
                {
                    _display.Append("÷");
                    long d = ParseUnary();
                    if (d == 0) throw new FormatException("運算式中除以 0");
                    v /= d;
                }
                else if (Accept("mod"))
                {
                    _display.Append(" MOD ");
                    long d = ParseUnary();
                    if (d == 0) throw new FormatException("運算式中除以 0");
                    v %= d;
                }
                else if (Accept("shl")) { _display.Append(" SHL "); v = v << (int)ParseUnary(); }
                else if (Accept("shr")) { _display.Append(" SHR "); v = (long)((ulong)v >> (int)ParseUnary()); }
                else return v;
            }
        }

        private long ParseUnary()
        {
            if (Accept("-")) { _display.Append("-"); return unchecked(-ParseUnary()); }
            if (Accept("+")) return ParseUnary();
            if (Accept("not")) { _display.Append("NOT "); return ~ParseUnary(); }
            if (Accept("offset") || Accept("addr"))
            {
                UsedOffsetOperator = true;
                _display.Append("OFFSET ");
                return ParseUnary();
            }
            foreach (var op in new[] { "lengthof", "sizeof", "type", "length", "size" })
            {
                if (!Accept(op)) continue;
                string name = Peek();
                if (name == null) throw new FormatException(op.ToUpperInvariant() + " 後面缺少變數名稱");
                _pos++;
                var sym = _resolve(name);
                if (sym == null || !sym.IsDataLabel) throw new FormatException(op.ToUpperInvariant() + " 需要資料變數，但「" + name + "」不是");
                long v = op == "lengthof" || op == "length" ? sym.Length
                       : op == "sizeof" || op == "size" ? (long)sym.Length * sym.ElementSize
                       : sym.ElementSize;
                _display.Append(op.ToUpperInvariant()).Append(' ').Append(name).Append('(').Append(v).Append(')');
                return v;
            }
            if (Accept("low")) { _display.Append("LOW "); return ParseUnary() & 0xFF; }
            if (Accept("high")) { _display.Append("HIGH "); return (ParseUnary() >> 8) & 0xFF; }
            return ParsePrimary();
        }

        private long ParsePrimary()
        {
            string t = Peek();
            if (t == null) throw new FormatException("運算式不完整");
            if (t == "(" || t == "[")
            {
                _pos++;
                _display.Append('(');
                long v = ParseOr();
                if (!Accept(")") && !Accept("]")) throw new FormatException("括號不成對");
                _display.Append(')');
                return v;
            }
            _pos++;

            long n;
            int radix;
            if ((char.IsDigit(t[0]) || t[0] == '\'' || t[0] == '"') && AsmNumber.TryParse(t, out n, out radix))
            {
                _display.Append(t);
                return n;
            }

            var sym = _resolve(t);
            if (sym == null) throw new FormatException("不認識的符號「" + t + "」");
            if (sym.IsRegister) UsedRegister = true;
            if (sym.IsCodeLabel) UsedCodeLabel = true;
            if (sym.IsDataLabel)
            {
                UsedDataLabel = true;
                if (FirstDataLabel == null) FirstDataLabel = sym;
            }
            _display.Append(sym.Display ?? t);

            // 支援 MASM 的 TYPE 後綴寫法，例如 arr[esi*TYPE arr] 已由 ParseUnary 處理
            return sym.Value;
        }
    }

    internal static class HexText
    {
        public static string H(ulong v, int bits)
        {
            return AsmNumber.Hex(v, bits);
        }

        public static string Dec(ulong v, int bits)
        {
            return AsmNumber.Describe(v, bits);
        }

        public static string Int(long v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }
    }
}
