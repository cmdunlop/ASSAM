using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Assam.Core.Simulation;
using Assam.Core.Text;

namespace Assam.Core.Derivation
{
    /// <summary>
    /// 數值推導工具：每個函式都回傳「一步一步」的計算過程（像手寫作業一樣），
    /// 用來學習進位轉換、二補數、旗標、實體位址、移位、8051 延遲／計時器／鮑率等計算。
    /// </summary>
    public static class NumberDerivation
    {
        private static string N(long v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }

        private static string N(ulong v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }

        private static string N(double v, string format = "0.###")
        {
            return v.ToString(format, CultureInfo.InvariantCulture);
        }

        public static long ParseValue(string text)
        {
            long v;
            if (!AsmNumber.TryParse(text, out v)) throw new ArgumentException("看不懂數值「" + text + "」。可用格式：255、-5、0x1F、1Fh、1010b、'A'。");
            return v;
        }

        /// <summary>選擇能容納數值的最小位元數（8/16/32/64）。</summary>
        public static int FitBits(long value)
        {
            if (value >= -128 && value <= 255) return 8;
            if (value >= -32768 && value <= 65535) return 16;
            if (value >= int.MinValue && value <= uint.MaxValue) return 32;
            return 64;
        }

        // =====================================================================
        // 進位轉換
        // =====================================================================

        public static string ConvertBase(string input, int bits = 0)
        {
            long value;
            int radix;
            if (!AsmNumber.TryParse(input, out value, out radix))
                throw new ArgumentException("看不懂數值「" + input + "」。可用格式：255、0x1F、1Fh、1010b、17o、'A'。");
            if (value < 0) return TwosComplement(input, bits == 0 ? FitBits(value) : bits);

            ulong u = (ulong)value;
            if (bits == 0) bits = FitBits(value);
            var sb = new StringBuilder();
            sb.Append("輸入：").Append(input.Trim()).Append("（").Append(AsmNumber.RadixName(radix)).Append("）\r\n\r\n");
            int step = 1;

            if (radix == 0)
            {
                sb.Append("【步驟 ").Append(step++).Append("】字元轉成 ASCII 碼\r\n");
                sb.Append("  ").Append(input.Trim()).Append(" 的 ASCII 碼 = ").Append(N(u)).Append("\r\n\r\n");
            }
            else if (radix != 10)
            {
                sb.Append("【步驟 ").Append(step++).Append("】").Append(AsmNumber.RadixName(radix)).Append(" → 十進位（位權展開：每一位 × 基數的次方）\r\n");
                sb.Append(PositionalExpansion(input, radix, u));
                sb.Append("\r\n");
            }

            if (radix != 2)
            {
                sb.Append("【步驟 ").Append(step++).Append("】十進位 → 二進位（除 2 取餘數，由下往上讀）\r\n");
                if (u <= 65535)
                {
                    ulong x = u;
                    if (x == 0) sb.Append("  0 的二進位就是 0\r\n");
                    while (x > 0)
                    {
                        sb.Append("  ").Append(N(x).PadLeft(6)).Append(" ÷ 2 = ").Append(N(x / 2).PadLeft(6)).Append(" … 餘 ").Append(x % 2).Append("\r\n");
                        x /= 2;
                    }
                    sb.Append("  由下往上讀餘數 → ").Append(Convert.ToString(value, 2)).Append("\r\n\r\n");
                }
                else
                {
                    sb.Append("  數值較大，改用「先轉十六進位，再把每個十六進位數字換成 4 個位元」的方法。\r\n");
                    sb.Append(DivisionTable(u, 16));
                    sb.Append("\r\n");
                }
            }

            string bin = AsmNumber.Binary(u, bits);
            sb.Append("【步驟 ").Append(step++).Append("】二進位 → 十六進位（從右邊開始每 4 位元一組）\r\n");
            sb.Append("  ").Append(bin).Append("\r\n  ");
            var groups = bin.Split(' ');
            sb.Append(string.Join("    ", groups.Select(g => Convert.ToInt32(g, 2).ToString("X", CultureInfo.InvariantCulture)))).Append("\r\n");
            sb.Append("  → ").Append(u.ToString("X", CultureInfo.InvariantCulture)).Append("h\r\n\r\n");

            sb.Append("【步驟 ").Append(step++).Append("】二進位 → 八進位（從右邊開始每 3 位元一組）\r\n");
            string raw = Convert.ToString(value, 2);
            while (raw.Length % 3 != 0) raw = "0" + raw;
            var octGroups = Enumerable.Range(0, raw.Length / 3).Select(i => raw.Substring(i * 3, 3)).ToList();
            sb.Append("  ").Append(string.Join(" ", octGroups)).Append("\r\n");
            sb.Append("  ").Append(string.Join("   ", octGroups.Select(g => Convert.ToInt32(g, 2).ToString(CultureInfo.InvariantCulture)))).Append("\r\n");
            sb.Append("  → ").Append(Convert.ToString(value, 8)).Append("o\r\n\r\n");

            sb.Append("【結果】\r\n");
            sb.Append("  十進位   ").Append(N(u)).Append("\r\n");
            sb.Append("  十六進位 ").Append(u.ToString("X", CultureInfo.InvariantCulture)).Append("h（0x").Append(u.ToString("X", CultureInfo.InvariantCulture)).Append("）\r\n");
            sb.Append("  二進位   ").Append(Convert.ToString(value, 2)).Append("b\r\n");
            sb.Append("  八進位   ").Append(Convert.ToString(value, 8)).Append("o\r\n");
            if (u <= AsmNumber.Mask(bits))
            {
                long signed = AsmNumber.ToSigned(u, bits);
                sb.Append("  以 ").Append(bits).Append(" 位元表示：").Append(AsmNumber.Binary(u, bits)).Append("，無號 = ").Append(N(u)).Append("，有號 = ").Append(N(signed));
                if (signed < 0) sb.Append("（最高位元是 1，視為有號數時是負數）");
                sb.Append("\r\n");
            }
            if (u >= 32 && u < 127) sb.Append("  ASCII 字元：'").Append((char)u).Append("'\r\n");
            return sb.ToString();
        }

        private static string PositionalExpansion(string input, int radix, ulong value)
        {
            string digits = input.Trim().ToLowerInvariant().Replace("_", string.Empty);
            if (digits.StartsWith("0x", StringComparison.Ordinal) || digits.StartsWith("0b", StringComparison.Ordinal)) digits = digits.Substring(2);
            else if (digits.StartsWith("$", StringComparison.Ordinal)) digits = digits.Substring(1);
            else if (digits.EndsWith("h", StringComparison.Ordinal) || digits.EndsWith("b", StringComparison.Ordinal) ||
                     digits.EndsWith("o", StringComparison.Ordinal) || digits.EndsWith("q", StringComparison.Ordinal))
                digits = digits.Substring(0, digits.Length - 1);
            digits = digits.TrimStart('0');
            if (digits.Length == 0) return "  全部是 0，所以值為 0\r\n";
            if (digits.Length > 16)
                return "  位數太多，直接計算結果 = " + N(value) + "\r\n";

            var terms = new List<string>();
            var values = new List<string>();
            for (int i = 0; i < digits.Length; i++)
            {
                int power = digits.Length - 1 - i;
                int d = Convert.ToInt32(digits[i].ToString(), 16);
                string dText = d >= 10 ? char.ToUpperInvariant(digits[i]) + "(" + d + ")" : d.ToString(CultureInfo.InvariantCulture);
                terms.Add(dText + "×" + radix + "^" + power);
                if (d != 0) values.Add(N((ulong)d * Pow((ulong)radix, power)));
            }
            var sb = new StringBuilder();
            sb.Append("  ").Append(string.Join(" + ", terms)).Append("\r\n");
            sb.Append("  = ").Append(values.Count == 0 ? "0" : string.Join(" + ", values)).Append("\r\n");
            sb.Append("  = ").Append(N(value)).Append("\r\n");
            return sb.ToString();
        }

        private static ulong Pow(ulong b, int e)
        {
            ulong r = 1;
            for (int i = 0; i < e; i++) r *= b;
            return r;
        }

        private static string DivisionTable(ulong value, ulong divisor)
        {
            var sb = new StringBuilder();
            ulong x = value;
            var digits = new List<string>();
            while (x > 0)
            {
                ulong q = x / divisor, r = x % divisor;
                string rText = r.ToString("X", CultureInfo.InvariantCulture);
                sb.Append("  ").Append(N(x)).Append(" ÷ ").Append(divisor).Append(" = ").Append(N(q)).Append(" … 餘 ").Append(N(r));
                if (r >= 10) sb.Append("（").Append(rText).Append("）");
                sb.Append("\r\n");
                digits.Add(rText);
                x = q;
            }
            digits.Reverse();
            sb.Append("  由下往上讀 → ").Append(string.Join(string.Empty, digits)).Append("h\r\n");
            return sb.ToString();
        }

        // =====================================================================
        // 二補數
        // =====================================================================

        public static string TwosComplement(string input, int bits)
        {
            long value;
            int radix;
            if (!AsmNumber.TryParse(input, out value, out radix)) throw new ArgumentException("看不懂數值「" + input + "」");
            if (bits <= 0) bits = FitBits(value);
            if (bits > 64) bits = 64;
            long min = bits >= 64 ? long.MinValue : -(1L << (bits - 1));
            ulong maxU = AsmNumber.Mask(bits);

            var sb = new StringBuilder();
            sb.Append(bits).Append(" 位元有號數範圍：").Append(N(min)).Append(" ～ ").Append(bits >= 64 ? N(long.MaxValue) : N((1L << (bits - 1)) - 1));
            sb.Append("；無號數範圍：0 ～ ").Append(N(maxU)).Append("\r\n\r\n");

            if (value < 0)
            {
                if (value < min) throw new ArgumentException(N(value) + " 超出 " + bits + " 位元有號數範圍，請改用更多位元。");
                ulong mag = (ulong)(-value);
                ulong inverted = ~mag & maxU;
                ulong result = (inverted + 1) & maxU;
                sb.Append("求 ").Append(N(value)).Append(" 的 ").Append(bits).Append(" 位元二補數：\r\n");
                sb.Append("【步驟 1】寫出絕對值 ").Append(N(mag)).Append(" 的二進位：").Append(AsmNumber.Binary(mag, bits)).Append("\r\n");
                sb.Append("【步驟 2】每個位元反相（一補數）：  ").Append(AsmNumber.Binary(inverted, bits)).Append("\r\n");
                sb.Append("【步驟 3】再加 1：                  ").Append(AsmNumber.Binary(result, bits)).Append("\r\n\r\n");
                sb.Append("【結果】").Append(N(value)).Append(" = ").Append(AsmNumber.Binary(result, bits)).Append("b = ")
                  .Append(AsmNumber.Hex(result, bits)).Append("（當成無號數看是 ").Append(N(result)).Append("）\r\n");
                if (bits < 64)
                    sb.Append("【驗算】2^").Append(bits).Append(" - ").Append(N(mag)).Append(" = ").Append(N((maxU - mag) + 1)).Append("，正好等於無號值 ").Append(N(result)).Append(" ✓\r\n");
                sb.Append("【快速法】從右邊找到第一個 1，保留它和右邊的 0，左邊其餘位元全部反相。\r\n");
                return sb.ToString();
            }

            ulong u = (ulong)value;
            if (u > maxU) throw new ArgumentException(input + " 超出 " + bits + " 位元能表示的範圍。");
            bool negative = FlagMath.Msb(u, bits);
            sb.Append("把 ").Append(input.Trim()).Append(" = ").Append(AsmNumber.Binary(u, bits)).Append(" 視為 ").Append(bits).Append(" 位元有號數：\r\n");
            if (!negative)
            {
                sb.Append("  最高位元（第 ").Append(bits - 1).Append(" 位元）= 0 → 正數，值就是 ").Append(N(u)).Append("\r\n");
                ulong neg = (~u + 1) & maxU;
                sb.Append("\r\n若要求它的相反數 -").Append(N(u)).Append("：\r\n");
                sb.Append("  反相：").Append(AsmNumber.Binary(~u & maxU, bits)).Append("\r\n");
                sb.Append("  加 1：").Append(AsmNumber.Binary(neg, bits)).Append(" = ").Append(AsmNumber.Hex(neg, bits)).Append("\r\n");
                return sb.ToString();
            }

            ulong inv = ~u & maxU;
            ulong magnitude = (inv + 1) & maxU;
            long signedValue = AsmNumber.ToSigned(u, bits);
            sb.Append("【步驟 1】最高位元（第 ").Append(bits - 1).Append(" 位元）= 1 → 這是負數\r\n");
            sb.Append("【步驟 2】求它的大小：反相 ").Append(AsmNumber.Binary(inv, bits)).Append("\r\n");
            sb.Append("【步驟 3】再加 1：      ").Append(AsmNumber.Binary(magnitude, bits)).Append(" = ").Append(N(magnitude == 0 ? (ulong)1 << (bits - 1) : magnitude)).Append("\r\n");
            sb.Append("【結果】").Append(input.Trim()).Append(" 當作有號數 = ").Append(N(signedValue)).Append("（當作無號數 = ").Append(N(u)).Append("）\r\n");
            if (bits < 64) sb.Append("【另一種算法】無號值 - 2^").Append(bits).Append(" = ").Append(N(u)).Append(" - ").Append(N(maxU + 1)).Append(" = ").Append(N(signedValue)).Append("\r\n");
            return sb.ToString();
        }

        // =====================================================================
        // 加減法旗標推導
        // =====================================================================

        public static string AddSubFlags(string aText, string bText, int bits, bool subtract)
        {
            if (bits != 8 && bits != 16 && bits != 32 && bits != 64) bits = 8;
            ulong mask = AsmNumber.Mask(bits);
            ulong a = unchecked((ulong)ParseValue(aText)) & mask;
            ulong b = unchecked((ulong)ParseValue(bText)) & mask;
            var f = subtract ? FlagMath.Sub(a, b, bits, false) : FlagMath.Add(a, b, bits, false);

            var sb = new StringBuilder();
            sb.Append(bits).Append(" 位元 ").Append(subtract ? "SUB／CMP" : "ADD").Append("：").Append(AsmNumber.Hex(a, bits)).Append(subtract ? " - " : " + ").Append(AsmNumber.Hex(b, bits)).Append("\r\n\r\n");

            if (bits <= 32)
            {
                string pad = "      ";
                if (!subtract)
                {
                    ulong carries = 0;
                    bool c = false;
                    for (int i = 0; i < bits; i++)
                    {
                        bool ai = ((a >> i) & 1) != 0, bi = ((b >> i) & 1) != 0;
                        bool cout = (ai && bi) || (c && (ai ^ bi));
                        if (cout && i + 1 < bits) carries |= 1UL << (i + 1);
                        c = cout;
                    }
                    sb.Append("進位  ").Append(AsmNumber.Binary(carries, bits)).Append(c ? "  （最高位元還有進位 → 進到第 " + bits + " 位元）" : string.Empty).Append("\r\n");
                }
                sb.Append(pad).Append(AsmNumber.Binary(a, bits)).Append("   ").Append(AsmNumber.Describe(a, bits)).Append("\r\n");
                sb.Append(subtract ? "  -   " : "  +   ").Append(AsmNumber.Binary(b, bits)).Append("   ").Append(AsmNumber.Describe(b, bits)).Append("\r\n");
                sb.Append("  ").Append(new string('-', AsmNumber.Binary(a, bits).Length + 4)).Append("\r\n");
                sb.Append(pad).Append(AsmNumber.Binary(f.Result, bits)).Append("   ").Append(AsmNumber.Describe(f.Result, bits)).Append("\r\n\r\n");
            }
            else
            {
                sb.Append("結果 = ").Append(AsmNumber.Describe(f.Result, bits)).Append("\r\n\r\n");
            }

            if (subtract)
            {
                ulong comp = (~b + 1) & mask;
                sb.Append("（CPU 的做法：A - B = A + (B 的二補數) = ").Append(AsmNumber.Hex(a, bits)).Append(" + ").Append(AsmNumber.Hex(comp, bits))
                  .Append("；x86 的 CF 在減法代表「借位」，剛好和加二補數時的進位相反）\r\n\r\n");
            }

            sb.Append("【旗標推導】\r\n");
            foreach (var e in f.Explanations) sb.Append("  ").Append(e).Append("\r\n");
            sb.Append("\r\n【重點】CF 看「無號數」有沒有超出範圍；OF 看「有號數」有沒有超出範圍。\r\n");
            if (!subtract && f.OF)
                sb.Append("  這裡兩個同號數相加，結果卻變號（").Append(FlagMath.Msb(a, bits) ? "負 + 負 = 正" : "正 + 正 = 負").Append("），所以 OF=1。\r\n");
            if (subtract && f.OF)
                sb.Append("  這裡異號數相減，結果的符號和被減數不同，所以 OF=1。\r\n");
            if (subtract)
            {
                sb.Append("\r\n若接著做條件跳躍：");
                sb.Append("JE ").Append(f.ZF ? "會跳" : "不跳").Append("、");
                sb.Append("JA（無號大於）").Append(!f.CF && !f.ZF ? "會跳" : "不跳").Append("、");
                sb.Append("JB（無號小於）").Append(f.CF ? "會跳" : "不跳").Append("、");
                sb.Append("JG（有號大於）").Append(!f.ZF && f.SF == f.OF ? "會跳" : "不跳").Append("、");
                sb.Append("JL（有號小於）").Append(f.SF != f.OF ? "會跳" : "不跳").Append("\r\n");
            }
            return sb.ToString();
        }

        // =====================================================================
        // 8086 實體位址
        // =====================================================================

        public static string PhysicalAddress(string segmentText, string offsetText)
        {
            ulong seg = (ulong)ParseHexDefault(segmentText) & 0xFFFF;
            ulong off = (ulong)ParseHexDefault(offsetText) & 0xFFFF;
            ulong shifted = seg << 4;
            ulong phys = (shifted + off) & 0xFFFFF;
            var sb = new StringBuilder();
            sb.Append("8086 真實模式：實體位址 = 段位址 × 16 + 偏移位址（20 位元）\r\n\r\n");
            sb.Append("  段位址 ").Append(seg.ToString("X4", CultureInfo.InvariantCulture)).Append("h × 10h = ").Append(shifted.ToString("X5", CultureInfo.InvariantCulture)).Append("h（十六進位後面補一個 0，等於左移 4 位元）\r\n");
            sb.Append("  ").Append(shifted.ToString("X5", CultureInfo.InvariantCulture)).Append("h + ").Append(off.ToString("X4", CultureInfo.InvariantCulture)).Append("h = ")
              .Append(((shifted + off)).ToString("X5", CultureInfo.InvariantCulture)).Append("h\r\n");
            if (shifted + off > 0xFFFFF) sb.Append("  超過 20 位元，8086 會繞回（A20 環繞）→ ").Append(phys.ToString("X5", CultureInfo.InvariantCulture)).Append("h\r\n");
            sb.Append("\r\n【結果】").Append(seg.ToString("X4", CultureInfo.InvariantCulture)).Append(":").Append(off.ToString("X4", CultureInfo.InvariantCulture))
              .Append(" → 實體位址 ").Append(phys.ToString("X5", CultureInfo.InvariantCulture)).Append("h（十進位 ").Append(N(phys)).Append("）\r\n");
            sb.Append("\r\n補充：不同的「段:偏移」可能指到同一個實體位址，例如 ")
              .Append(((phys >> 4) & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture)).Append(":").Append((phys & 0xF).ToString("X4", CultureInfo.InvariantCulture))
              .Append(" 也等於 ").Append(phys.ToString("X5", CultureInfo.InvariantCulture)).Append("h。\r\n");
            return sb.ToString();
        }

        /// <summary>位址類輸入預設視為十六進位（例如 1234:0010）。</summary>
        private static long ParseHexDefault(string text)
        {
            string t = text.Trim();
            long v;
            if (t.EndsWith("h", StringComparison.OrdinalIgnoreCase) || t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ParseValue(t);
            if (AsmNumber.TryParse(t + "h", out v)) return v;
            return ParseValue(t);
        }

        // =====================================================================
        // 移位與旋轉
        // =====================================================================

        public static string Shift(string valueText, string op, int count, int bits)
        {
            if (bits != 8 && bits != 16 && bits != 32) bits = 8;
            ulong mask = AsmNumber.Mask(bits);
            ulong v = unchecked((ulong)ParseValue(valueText)) & mask;
            op = (op ?? "SHL").Trim().ToUpperInvariant();
            if (count < 0 || count > 31) throw new ArgumentException("移位次數要在 0～31 之間");

            var sb = new StringBuilder();
            sb.Append(op).Append(" ").Append(AsmNumber.Hex(v, bits)).Append(", ").Append(count).Append("（").Append(bits).Append(" 位元）\r\n\r\n");
            sb.Append("  開始      ").Append(AsmNumber.Binary(v, bits)).Append("  = ").Append(AsmNumber.Describe(v, bits)).Append("\r\n");
            bool cf = false;
            ulong r = v;
            for (int i = 1; i <= count; i++)
            {
                switch (op)
                {
                    case "SHL":
                    case "SAL":
                        cf = FlagMath.Msb(r, bits);
                        r = (r << 1) & mask;
                        break;
                    case "SHR":
                        cf = (r & 1) != 0;
                        r >>= 1;
                        break;
                    case "SAR":
                        cf = (r & 1) != 0;
                        r = (r >> 1) | (r & (1UL << (bits - 1)));
                        break;
                    case "ROL":
                        cf = FlagMath.Msb(r, bits);
                        r = ((r << 1) | (cf ? 1UL : 0UL)) & mask;
                        break;
                    case "ROR":
                        cf = (r & 1) != 0;
                        r = (r >> 1) | (cf ? 1UL << (bits - 1) : 0UL);
                        break;
                    default:
                        throw new ArgumentException("支援的運算：SHL、SAL、SHR、SAR、ROL、ROR");
                }
                if (count <= 16 || i == count)
                    sb.Append("  第 ").Append(i.ToString(CultureInfo.InvariantCulture).PadLeft(2)).Append(" 次  ").Append(AsmNumber.Binary(r, bits)).Append("  CF=").Append(cf ? 1 : 0).Append("\r\n");
            }
            sb.Append("\r\n【結果】").Append(AsmNumber.Describe(r, bits)).Append("，CF=").Append(cf ? 1 : 0).Append("\r\n");
            switch (op)
            {
                case "SHL":
                case "SAL":
                    sb.Append("說明：左移 ").Append(count).Append(" 位等於乘以 2^").Append(count).Append(" = ").Append(1L << count).Append("（超出 ").Append(bits).Append(" 位元的部分會被丟掉）\r\n");
                    break;
                case "SHR":
                    sb.Append("說明：邏輯右移 ").Append(count).Append(" 位等於無號數除以 2^").Append(count).Append("：").Append(N(v)).Append(" ÷ ").Append(1L << count).Append(" = ").Append(N(r)).Append("（捨去餘數）\r\n");
                    break;
                case "SAR":
                    sb.Append("說明：算術右移會保留符號位元，等於有號數除以 2^").Append(count).Append(" 並向下取整：").Append(N(AsmNumber.ToSigned(v, bits))).Append(" → ").Append(N(AsmNumber.ToSigned(r, bits))).Append("\r\n");
                    break;
                default:
                    sb.Append("說明：旋轉不會遺失位元，移出去的位元從另一端繞回來。\r\n");
                    break;
            }
            return sb.ToString();
        }

        // =====================================================================
        // 零延伸 / 符號延伸
        // =====================================================================

        public static string Extend(string valueText, int fromBits, int toBits)
        {
            if (fromBits >= toBits) throw new ArgumentException("目的位元數必須大於來源位元數");
            ulong v = unchecked((ulong)ParseValue(valueText)) & AsmNumber.Mask(fromBits);
            ulong zero = v;
            ulong sign = unchecked((ulong)AsmNumber.ToSigned(v, fromBits)) & AsmNumber.Mask(toBits);
            bool msb = FlagMath.Msb(v, fromBits);
            var sb = new StringBuilder();
            sb.Append("來源 ").Append(fromBits).Append(" 位元：").Append(AsmNumber.Binary(v, fromBits)).Append(" = ").Append(AsmNumber.Describe(v, fromBits)).Append("\r\n\r\n");
            sb.Append("【零延伸 MOVZX】前面全部補 0（適用無號數）\r\n  ").Append(AsmNumber.Binary(zero, toBits)).Append(" = ").Append(AsmNumber.Describe(zero, toBits)).Append("\r\n\r\n");
            sb.Append("【符號延伸 MOVSX／CBW／CWDE／CDQ】前面補上符號位元 ").Append(msb ? "1" : "0").Append("（適用有號數）\r\n  ")
              .Append(AsmNumber.Binary(sign, toBits)).Append(" = ").Append(AsmNumber.Describe(sign, toBits)).Append("\r\n\r\n");
            sb.Append(msb ? "因為最高位元是 1，兩種延伸結果不同：無號看是 " + N(v) + "，有號看是 " + N(AsmNumber.ToSigned(v, fromBits)) + "。\r\n"
                          : "因為最高位元是 0，兩種延伸結果相同。\r\n");
            return sb.ToString();
        }

        // =====================================================================
        // 位元組序
        // =====================================================================

        public static string Endian(string valueText, int bytes)
        {
            if (bytes != 2 && bytes != 4 && bytes != 8) bytes = 4;
            ulong v = unchecked((ulong)ParseValue(valueText)) & AsmNumber.Mask(bytes * 8);
            var sb = new StringBuilder();
            sb.Append("數值 ").Append(AsmNumber.Hex(v, bytes * 8)).Append("（").Append(bytes).Append(" 個位元組）存到記憶體位址 X 開始：\r\n\r\n");
            sb.Append("位址        小端序(x86、ARM 預設)   大端序(網路、Motorola)\r\n");
            for (int i = 0; i < bytes; i++)
            {
                ulong little = (v >> (8 * i)) & 0xFF;
                ulong big = (v >> (8 * (bytes - 1 - i))) & 0xFF;
                sb.Append("X+").Append(i).Append("         ").Append(little.ToString("X2", CultureInfo.InvariantCulture)).Append("                      ")
                  .Append(big.ToString("X2", CultureInfo.InvariantCulture)).Append("\r\n");
            }
            sb.Append("\r\n小端序：低位元組放在低位址（所以在記憶體傾印中看起來是「反過來」的）。\r\n");
            sb.Append("例：MASM 的 val DWORD ").Append(v.ToString("X", CultureInfo.InvariantCulture)).Append("h 在記憶體中依序是 ");
            var parts = new List<string>();
            for (int i = 0; i < bytes; i++) parts.Add(((v >> (8 * i)) & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
            sb.Append(string.Join(" ", parts)).Append("。\r\n");
            return sb.ToString();
        }

        // =====================================================================
        // 8051：延遲副程式、計時器初值、鮑率
        // =====================================================================

        /// <summary>
        /// 巢狀 DJNZ 延遲副程式的時間推導。loops 由外到內，例如 {250, 200} 代表
        /// MOV R6,#250 / D1: MOV R7,#200 / D2: DJNZ R7,D2 / DJNZ R6,D1 / RET。
        /// </summary>
        public static string Delay8051(double crystalMHz, IList<int> loops, bool includeCall = true)
        {
            if (crystalMHz <= 0) throw new ArgumentException("石英振盪器頻率必須大於 0");
            if (loops == null || loops.Count == 0 || loops.Count > 4) throw new ArgumentException("請提供 1～4 層迴圈次數，例如 250,200");
            var counts = loops.Select(n => n == 0 ? 256 : n).ToList();
            if (counts.Any(n => n < 1 || n > 256)) throw new ArgumentException("DJNZ 的次數要在 1～256 之間（0 代表 256 次）");

            double cycleUs = 12.0 / crystalMHz;
            string[] regs = { "R4", "R5", "R6", "R7" };
            int start = 4 - counts.Count;
            var sb = new StringBuilder();
            sb.Append("石英振盪器 ").Append(N(crystalMHz)).Append(" MHz，8051 一個機器週期 = 12 個時脈 = 12 ÷ ").Append(N(crystalMHz)).Append(" = ").Append(N(cycleUs, "0.######")).Append(" μs\r\n\r\n");
            sb.Append("對應的延遲副程式：\r\n");
            sb.Append("  DELAY:  MOV  ").Append(regs[start]).Append(", #").Append(loops[0]).Append("      ; 1 週期\r\n");
            for (int i = 1; i < counts.Count; i++)
                sb.Append("  D").Append(i).Append(":     MOV  ").Append(regs[start + i]).Append(", #").Append(loops[i]).Append("      ; 1 週期\r\n");
            sb.Append("  D").Append(counts.Count).Append(":     DJNZ ").Append(regs[start + counts.Count - 1]).Append(", D").Append(counts.Count).Append("        ; 2 週期\r\n");
            for (int i = counts.Count - 2; i >= 0; i--)
                sb.Append("          DJNZ ").Append(regs[start + i]).Append(", D").Append(i + 1).Append("        ; 2 週期\r\n");
            sb.Append("          RET                ; 2 週期\r\n\r\n");

            sb.Append("【推導】由最內層往外算：\r\n");
            long inner = 2L * counts[counts.Count - 1];
            sb.Append("  最內層：DJNZ 執行 ").Append(counts[counts.Count - 1]).Append(" 次 × 2 週期 = ").Append(inner).Append(" 週期\r\n");
            long current = inner;
            for (int i = counts.Count - 2; i >= 0; i--)
            {
                long per = 1 + current + 2;
                long total = counts[i] * per;
                sb.Append("  第 ").Append(i + 1).Append(" 層：每一圈 = MOV(1) + 內層(").Append(current).Append(") + DJNZ(2) = ").Append(per)
                  .Append("，共 ").Append(counts[i]).Append(" 圈 = ").Append(total).Append(" 週期\r\n");
                current = total;
            }
            long cycles = 1 + current + 2;
            sb.Append("  加上第一個 MOV(1) 與 RET(2)：1 + ").Append(current).Append(" + 2 = ").Append(cycles).Append(" 週期\r\n");
            if (includeCall)
            {
                cycles += 2;
                sb.Append("  再加上呼叫它的 ACALL/LCALL(2)：").Append(cycles).Append(" 週期\r\n");
            }
            double us = cycles * cycleUs;
            sb.Append("\r\n【結果】").Append(cycles).Append(" 週期 × ").Append(N(cycleUs, "0.######")).Append(" μs ≈ ").Append(N(us, "0.##")).Append(" μs ≈ ")
              .Append(N(us / 1000.0, "0.###")).Append(" ms\r\n");
            sb.Append("估算法：延遲 ≈ 2 × ").Append(string.Join(" × ", counts)).Append(" × 機器週期 = ").Append(N(2.0 * counts.Aggregate(1L, (a, b) => a * b) * cycleUs / 1000.0, "0.###")).Append(" ms（忽略 MOV 與 RET）\r\n");
            return sb.ToString();
        }

        /// <summary>8051 計時器初值推導（模式 0、1、2）。</summary>
        public static string Timer8051(double crystalMHz, double microseconds, int mode)
        {
            if (crystalMHz <= 0 || microseconds <= 0) throw new ArgumentException("頻率與時間都必須大於 0");
            if (mode < 0 || mode > 2) throw new ArgumentException("模式請用 0、1 或 2");
            double cycleUs = 12.0 / crystalMHz;
            double exactCount = microseconds / cycleUs;
            long count = (long)Math.Round(exactCount);
            long max = mode == 0 ? 8192 : mode == 1 ? 65536 : 256;
            var sb = new StringBuilder();
            sb.Append("機器週期 = 12 ÷ ").Append(N(crystalMHz)).Append(" MHz = ").Append(N(cycleUs, "0.######")).Append(" μs\r\n");
            sb.Append("需要計數 = ").Append(N(microseconds)).Append(" μs ÷ ").Append(N(cycleUs, "0.######")).Append(" μs = ").Append(N(exactCount, "0.##"));
            if (Math.Abs(exactCount - count) > 0.001) sb.Append(" ≈ ").Append(count);
            sb.Append(" 次\r\n");
            sb.Append("模式 ").Append(mode).Append(mode == 0 ? "（13 位元，最多 8192）" : mode == 1 ? "（16 位元，最多 65536）" : "（8 位元自動重載，最多 256）").Append("\r\n\r\n");

            if (count > max)
            {
                long times = (long)Math.Ceiling((double)count / max);
                long per = (long)Math.Round((double)count / times);
                sb.Append("計數 ").Append(count).Append(" 超過模式 ").Append(mode).Append(" 的上限 ").Append(max).Append("，必須讓計時器溢位多次。\r\n");
                sb.Append("例如分成 ").Append(times).Append(" 次，每次計數 ").Append(per).Append("，以下計算每次的初值：\r\n\r\n");
                count = per;
            }

            long init = max - count;
            sb.Append("初值 = ").Append(max).Append(" - ").Append(count).Append(" = ").Append(init).Append(" = ").Append(init.ToString("X4", CultureInfo.InvariantCulture)).Append("h\r\n");
            if (mode == 1)
            {
                sb.Append("TH = 高位元組 = ").Append((init >> 8).ToString("X2", CultureInfo.InvariantCulture)).Append("h，TL = 低位元組 = ").Append((init & 0xFF).ToString("X2", CultureInfo.InvariantCulture)).Append("h\r\n");
                sb.Append("（也可以寫成 MOV TH0, #HIGH(").Append(max).Append("-").Append(count).Append(")／MOV TL0, #LOW(").Append(max).Append("-").Append(count).Append(")）\r\n");
                sb.Append("\r\n參考程式：\r\n  MOV TMOD, #01h      ; Timer0 模式 1\r\n  MOV TH0, #").Append((init >> 8).ToString("X2", CultureInfo.InvariantCulture)).Append("h\r\n  MOV TL0, #")
                  .Append((init & 0xFF).ToString("X2", CultureInfo.InvariantCulture)).Append("h\r\n  SETB TR0            ; 啟動\r\n  JNB TF0, $          ; 等待溢位\r\n  CLR TR0\r\n  CLR TF0\r\n");
            }
            else if (mode == 2)
            {
                sb.Append("TH = TL = ").Append(init.ToString("X2", CultureInfo.InvariantCulture)).Append("h（TH 存重載值，溢位時自動把 TH 載入 TL）\r\n");
            }
            else
            {
                sb.Append("模式 0：TH = 初值的高 8 位元 = ").Append((init >> 5).ToString("X2", CultureInfo.InvariantCulture)).Append("h，TL = 低 5 位元 = ")
                  .Append((init & 0x1F).ToString("X2", CultureInfo.InvariantCulture)).Append("h\r\n");
            }
            return sb.ToString();
        }

        /// <summary>8051 序列埠鮑率：Timer1 模式 2，TH1 = 256 - (2^SMOD × f) / (384 × baud)。</summary>
        public static string Baud8051(double crystalMHz, int baud, bool smod)
        {
            if (crystalMHz <= 0 || baud <= 0) throw new ArgumentException("頻率與鮑率都必須大於 0");
            double f = crystalMHz * 1000000.0;
            double k = smod ? 2 : 1;
            double divisor = f * k / (384.0 * baud);
            long rounded = (long)Math.Round(divisor);
            long th1 = 256 - rounded;
            double actual = f * k / (384.0 * rounded);
            double error = (actual - baud) / baud * 100.0;
            var sb = new StringBuilder();
            sb.Append("公式（Timer1 模式 2）：TH1 = 256 - (2^SMOD × 石英頻率) ÷ (384 × 鮑率)\r\n\r\n");
            sb.Append("  (").Append(smod ? "2" : "1").Append(" × ").Append(N(f, "0")).Append(") ÷ (384 × ").Append(baud).Append(") = ").Append(N(divisor, "0.###"));
            if (Math.Abs(divisor - rounded) > 0.001) sb.Append(" ≈ ").Append(rounded);
            sb.Append("\r\n  TH1 = 256 - ").Append(rounded).Append(" = ").Append(th1).Append(" = ").Append(th1 >= 0 && th1 < 256 ? th1.ToString("X2", CultureInfo.InvariantCulture) + "h" : "（超出範圍）").Append("\r\n\r\n");
            sb.Append("實際鮑率 = ").Append(N(actual, "0.##")).Append("，誤差 ").Append(N(error, "0.##")).Append("%\r\n");
            if (Math.Abs(error) > 2.5) sb.Append("▲ 誤差超過 2.5%，通訊可能出錯。建議改用 11.0592 MHz 石英（可整除常見鮑率）。\r\n");
            sb.Append("\r\n參考程式：\r\n  MOV TMOD, #20h      ; Timer1 模式 2\r\n  MOV TH1, #").Append(th1 >= 0 && th1 < 256 ? th1.ToString("X2", CultureInfo.InvariantCulture) : "??").Append("h\r\n  MOV SCON, #50h      ; 模式 1，允許接收\r\n  SETB TR1\r\n");
            return sb.ToString();
        }
    }
}
