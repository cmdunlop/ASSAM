using System;
using System.Globalization;
using System.Text;

namespace Assam.Core.Text
{
    /// <summary>
    /// 解析組合語言常見的數字寫法：
    /// 十進位(10, -5)、十六進位(0x1F, 1Fh, 0FFh, $FF)、二進位(1010b, 0b1010)、八進位(17o, 17q)、字元('A')。
    /// </summary>
    public static class AsmNumber
    {
        public static bool TryParse(string text, out long value)
        {
            int radix;
            return TryParse(text, out value, out radix);
        }

        public static bool TryParse(string text, out long value, out int radix)
        {
            value = 0;
            radix = 10;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string s = text.Trim().Replace("_", "");
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1).Trim();
            if (s.Length == 0) return false;

            // 字元常數
            if (s.Length >= 3 && (s[0] == '\'' || s[0] == '"') && s[s.Length - 1] == s[0])
            {
                string inner = s.Substring(1, s.Length - 2);
                if (inner.Length == 0 || inner.Length > 8) return false;
                ulong acc = 0;
                foreach (char c in inner) acc = (acc << 8) | (byte)c;
                value = unchecked((long)acc);
                radix = 0;
                return true;
            }

            bool negative = false;
            if (s[0] == '-' || s[0] == '+')
            {
                negative = s[0] == '-';
                s = s.Substring(1).Trim();
                if (s.Length == 0) return false;
            }

            string digits;
            string lower = s.ToLowerInvariant();
            if (lower.StartsWith("0x", StringComparison.Ordinal)) { radix = 16; digits = lower.Substring(2); }
            else if (lower.StartsWith("$", StringComparison.Ordinal) && lower.Length > 1) { radix = 16; digits = lower.Substring(1); }
            else if (lower.StartsWith("0b", StringComparison.Ordinal) && lower.Length > 2 && IsAll(lower.Substring(2), "01")) { radix = 2; digits = lower.Substring(2); }
            else if (lower.EndsWith("h", StringComparison.Ordinal) && char.IsDigit(lower[0])) { radix = 16; digits = lower.Substring(0, lower.Length - 1); }
            else if (lower.EndsWith("b", StringComparison.Ordinal) && lower.Length > 1 && IsAll(lower.Substring(0, lower.Length - 1), "01")) { radix = 2; digits = lower.Substring(0, lower.Length - 1); }
            else if ((lower.EndsWith("o", StringComparison.Ordinal) || lower.EndsWith("q", StringComparison.Ordinal)) && lower.Length > 1 && IsAll(lower.Substring(0, lower.Length - 1), "01234567")) { radix = 8; digits = lower.Substring(0, lower.Length - 1); }
            else if (lower.EndsWith("d", StringComparison.Ordinal) && lower.Length > 1 && IsAll(lower.Substring(0, lower.Length - 1), "0123456789")) { radix = 10; digits = lower.Substring(0, lower.Length - 1); }
            else { radix = 10; digits = lower; }

            if (digits.Length == 0) return false;

            ulong result;
            if (!TryParseDigits(digits, radix, out result)) return false;

            if (negative)
            {
                if (result > (ulong)long.MaxValue + 1UL) return false;
                value = unchecked(-(long)result);
            }
            else
            {
                value = unchecked((long)result);
            }
            return true;
        }

        private static bool TryParseDigits(string digits, int radix, out ulong result)
        {
            result = 0;
            foreach (char ch in digits)
            {
                int d;
                if (ch >= '0' && ch <= '9') d = ch - '0';
                else if (ch >= 'a' && ch <= 'f') d = ch - 'a' + 10;
                else return false;
                if (d >= radix) return false;
                if (result > (ulong.MaxValue - (ulong)d) / (ulong)radix) return false; // 超過 64 位元
                result = result * (ulong)radix + (ulong)d;
            }
            return true;
        }

        private static bool IsAll(string s, string allowed)
        {
            if (s.Length == 0) return false;
            foreach (char c in s)
                if (allowed.IndexOf(c) < 0) return false;
            return true;
        }

        public static string RadixName(int radix)
        {
            switch (radix)
            {
                case 2: return "二進位";
                case 8: return "八進位";
                case 16: return "十六進位";
                case 0: return "字元";
                default: return "十進位";
            }
        }

        public static ulong Mask(int bits)
        {
            return bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1UL;
        }

        /// <summary>將值依位元數格式化成 0x 開頭的十六進位（補齊位數）。</summary>
        public static string Hex(ulong value, int bits)
        {
            int digits = Math.Max(1, (bits + 3) / 4);
            return "0x" + (value & Mask(bits)).ToString("X" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        /// <summary>二進位字串，每 4 位元以空白分組。</summary>
        public static string Binary(ulong value, int bits, bool group = true)
        {
            var sb = new StringBuilder();
            for (int i = bits - 1; i >= 0; i--)
            {
                sb.Append(((value >> i) & 1UL) != 0 ? '1' : '0');
                if (group && i > 0 && i % 4 == 0) sb.Append(' ');
            }
            return sb.ToString();
        }

        public static long ToSigned(ulong value, int bits)
        {
            value &= Mask(bits);
            if (bits >= 64) return unchecked((long)value);
            ulong sign = 1UL << (bits - 1);
            if ((value & sign) != 0) return unchecked((long)(value | ~Mask(bits)));
            return (long)value;
        }

        /// <summary>同時顯示十六進位、無號十進位（以及不同時的有號十進位）。</summary>
        public static string Describe(ulong value, int bits)
        {
            value &= Mask(bits);
            long signed = ToSigned(value, bits);
            string s = Hex(value, bits) + " (" + value.ToString(CultureInfo.InvariantCulture);
            if (signed < 0) s += "，有號為 " + signed.ToString(CultureInfo.InvariantCulture);
            return s + ")";
        }
    }
}
