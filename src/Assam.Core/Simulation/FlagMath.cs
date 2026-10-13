using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Assam.Core.Text;

namespace Assam.Core.Simulation
{
    /// <summary>算術運算後旗標（CF OF ZF SF PF AF）的計算與「為什麼」的推導說明。</summary>
    public static class FlagMath
    {
        public sealed class FlagResult
        {
            public FlagResult()
            {
                Explanations = new List<string>();
            }

            public ulong Result { get; set; }
            public bool CF { get; set; }
            public bool OF { get; set; }
            public bool ZF { get; set; }
            public bool SF { get; set; }
            public bool PF { get; set; }
            public bool AF { get; set; }
            public List<string> Explanations { get; private set; }
        }

        public static bool Msb(ulong v, int bits)
        {
            return ((v >> (bits - 1)) & 1UL) != 0;
        }

        /// <summary>PF：結果低 8 位元中 1 的個數為偶數時為 1。</summary>
        public static bool Parity(ulong v)
        {
            return CountOnes((byte)v) % 2 == 0;
        }

        public static int CountOnes(ulong v)
        {
            int c = 0;
            while (v != 0) { c += (int)(v & 1UL); v >>= 1; }
            return c;
        }

        private static string B(bool b)
        {
            return b ? "1" : "0";
        }

        private static string D(BigInteger v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>a + b (+ carryIn)。</summary>
        public static FlagResult Add(ulong a, ulong b, int bits, bool carryIn)
        {
            ulong mask = AsmNumber.Mask(bits);
            a &= mask; b &= mask;
            int cin = carryIn ? 1 : 0;
            BigInteger exactU = new BigInteger(a) + new BigInteger(b) + cin;
            BigInteger exactS = new BigInteger(AsmNumber.ToSigned(a, bits)) + new BigInteger(AsmNumber.ToSigned(b, bits)) + cin;
            ulong res = (ulong)(exactU & new BigInteger(mask));

            var r = new FlagResult { Result = res };
            BigInteger maxU = new BigInteger(mask);
            BigInteger minS = -(BigInteger.One << (bits - 1));
            BigInteger maxS = (BigInteger.One << (bits - 1)) - 1;
            r.CF = exactU > maxU;
            r.OF = exactS < minS || exactS > maxS;
            SetCommon(r, res, bits);
            r.AF = (((a ^ b ^ res) >> 4) & 1UL) != 0;

            string plus = carryIn ? " + 1（CF）" : string.Empty;
            r.Explanations.Add("CF=" + B(r.CF) + "：無號 " + a.ToString(CultureInfo.InvariantCulture) + " + " + b.ToString(CultureInfo.InvariantCulture) + plus + " = " + D(exactU) +
                               (r.CF ? "，超過 " : "，沒有超過 ") + bits + " 位元最大值 " + D(maxU) + (r.CF ? "（產生進位）" : string.Empty));
            r.Explanations.Add("OF=" + B(r.OF) + "：有號 " + AsmNumber.ToSigned(a, bits).ToString(CultureInfo.InvariantCulture) + " + " +
                               AsmNumber.ToSigned(b, bits).ToString(CultureInfo.InvariantCulture) + plus + " = " + D(exactS) +
                               (r.OF ? "，超出" : "，在") + "有號範圍 " + D(minS) + "～" + D(maxS) + (r.OF ? "（有號溢位）" : string.Empty));
            AddCommonExplanations(r, res, bits, "進位");
            return r;
        }

        /// <summary>a - b (- borrowIn)。CMP、SUB、SBB、NEG 共用。</summary>
        public static FlagResult Sub(ulong a, ulong b, int bits, bool borrowIn)
        {
            ulong mask = AsmNumber.Mask(bits);
            a &= mask; b &= mask;
            int bin = borrowIn ? 1 : 0;
            BigInteger exactU = new BigInteger(a) - new BigInteger(b) - bin;
            BigInteger exactS = new BigInteger(AsmNumber.ToSigned(a, bits)) - new BigInteger(AsmNumber.ToSigned(b, bits)) - bin;
            BigInteger modulus = BigInteger.One << bits;
            BigInteger wrapped = ((exactU % modulus) + modulus) % modulus;
            ulong res = (ulong)wrapped;

            var r = new FlagResult { Result = res };
            BigInteger minS = -(BigInteger.One << (bits - 1));
            BigInteger maxS = (BigInteger.One << (bits - 1)) - 1;
            r.CF = exactU < 0;
            r.OF = exactS < minS || exactS > maxS;
            SetCommon(r, res, bits);
            r.AF = (((a ^ b ^ res) >> 4) & 1UL) != 0;

            string minus = borrowIn ? " - 1（CF）" : string.Empty;
            r.Explanations.Add("CF=" + B(r.CF) + "：無號 " + a.ToString(CultureInfo.InvariantCulture) + " - " + b.ToString(CultureInfo.InvariantCulture) + minus + " = " + D(exactU) +
                               (r.CF ? "，結果小於 0 需要借位" : "，不需要借位"));
            r.Explanations.Add("OF=" + B(r.OF) + "：有號 " + AsmNumber.ToSigned(a, bits).ToString(CultureInfo.InvariantCulture) + " - " +
                               AsmNumber.ToSigned(b, bits).ToString(CultureInfo.InvariantCulture) + minus + " = " + D(exactS) +
                               (r.OF ? "，超出" : "，在") + "有號範圍 " + D(minS) + "～" + D(maxS) + (r.OF ? "（有號溢位）" : string.Empty));
            AddCommonExplanations(r, res, bits, "借位");
            return r;
        }

        /// <summary>AND/OR/XOR/TEST：CF=OF=0，依結果設定 ZF SF PF。</summary>
        public static FlagResult Logic(ulong res, int bits)
        {
            var r = new FlagResult { Result = res & AsmNumber.Mask(bits) };
            SetCommon(r, r.Result, bits);
            r.Explanations.Add("CF=0、OF=0：邏輯運算一律清除 CF 與 OF");
            AddCommonExplanations(r, r.Result, bits, null);
            return r;
        }

        private static void SetCommon(FlagResult r, ulong res, int bits)
        {
            r.ZF = res == 0;
            r.SF = Msb(res, bits);
            r.PF = Parity(res);
        }

        private static void AddCommonExplanations(FlagResult r, ulong res, int bits, string carryWord)
        {
            r.Explanations.Add("ZF=" + B(r.ZF) + "：結果" + (r.ZF ? "等於" : "不等於") + " 0");
            r.Explanations.Add("SF=" + B(r.SF) + "：結果最高位元（第 " + (bits - 1) + " 位元）是 " + B(r.SF));
            int ones = CountOnes((byte)res);
            r.Explanations.Add("PF=" + B(r.PF) + "：結果低 8 位元 " + AsmNumber.Binary(res & 0xFF, 8) + " 有 " + ones + " 個 1（" + (ones % 2 == 0 ? "偶數" : "奇數") + "）");
            if (carryWord == "進位")
                r.Explanations.Add("AF=" + B(r.AF) + "：低 4 位元相加" + (r.AF ? "有" : "沒有") + "進位到第 4 位元（BCD 調整用）");
            else if (carryWord != null)
                r.Explanations.Add("AF=" + B(r.AF) + "：低 4 位元相減" + (r.AF ? "需要" : "不需要") + "向第 4 位元借位（BCD 調整用）");
        }
    }
}
