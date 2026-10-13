using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Assam.Core.Derivation;
using Assam.Core.Text;

namespace Assam.Core.Assistant
{
    /// <summary>
    /// 從自然語言問句辨識「數值推導」需求，例如：
    /// 「0x7F 轉二進位」「-5 的 8 位元二補數」「8 位元 7Fh + 1 的旗標」「1234:0010 的實體位址」
    /// 「8051 12MHz 延遲 250 200」「12MHz 計時器 50ms 初值」「11.0592MHz 鮑率 9600」。
    /// </summary>
    public static class NumberRequestParser
    {
        // 注意：.NET 的 \w 也會比對中文字，所以邊界一律用 ASCII 字元類別
        private static readonly Regex NumberToken = new Regex(@"(?<![0-9a-z_$.])(-?(?:0x[0-9a-f]+|0b[01]+|\$[0-9a-f]+|[0-9][0-9a-f]*h|[01]+b|[0-7]+[oq]|[0-9]+(?:\.[0-9]+)?)|'.')(?![0-9a-z_])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BitsRegex = new Regex(@"(?<![0-9])(8|16|32|64)\s*(位元|bit|bits|-bit|位)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NamedRadixRegex = new Regex(@"(十六進位|16進位|hex|二進位|2進位|binary)\s*(?:的|數|值)?\s*([0-9a-f]+)(?![0-9a-z_])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MHzRegex = new Regex(@"(\d+(?:\.\d+)?)\s*mhz", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SegOffRegex = new Regex(@"(?<![0-9a-z_])([0-9a-f]{1,4})h?\s*:\s*([0-9a-f]{1,4})h?(?![0-9a-z_])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BinaryOpRegex = new Regex(@"(-?[0-9a-fx$']+[hbo]?)\s*([+\-])\s*(-?[0-9a-fx$']+[hbo]?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TimeRegex = new Regex(@"(\d+(?:\.\d+)?)\s*(ms|us|μs|微秒|毫秒|s|秒)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public sealed class Result
        {
            public string Title { get; set; }
            public string Text { get; set; }
        }

        public static Result TryHandle(string question)
        {
            if (string.IsNullOrWhiteSpace(question)) return null;
            string q = TermNormalizer.Normalize(question).Trim();
            string lower = q.ToLowerInvariant();
            int bits = ParseBits(lower);

            try
            {
                // 8051 延遲
                if ((lower.Contains("延遲") || lower.Contains("delay")) && (lower.Contains("mhz") || lower.Contains("8051") || lower.Contains("djnz")))
                {
                    double mhz = ParseMHz(lower, 12);
                    var loops = Numbers(RemoveMHz(lower)).Select(s => ToLong(s)).Where(v => v >= 0 && v <= 256 && v != 8051).Select(v => (int)v).ToList();
                    if (loops.Count > 0)
                        return new Result { Title = "8051 延遲時間推導", Text = NumberDerivation.Delay8051(mhz, loops.Take(4).ToList()) };
                }

                // 8051 計時器初值
                if ((lower.Contains("計時器") || lower.Contains("timer") || lower.Contains("th0") || lower.Contains("初值")) && TimeRegex.IsMatch(lower))
                {
                    double mhz = ParseMHz(lower, 12);
                    var t = TimeRegex.Match(lower);
                    double value = double.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture);
                    string unit = t.Groups[2].Value;
                    double us = unit == "ms" || unit == "毫秒" ? value * 1000 : unit == "s" || unit == "秒" ? value * 1000000 : value;
                    int mode = lower.Contains("模式2") || lower.Contains("模式 2") || lower.Contains("mode 2") || lower.Contains("mode2") ? 2
                             : lower.Contains("模式0") || lower.Contains("模式 0") || lower.Contains("mode 0") ? 0 : 1;
                    return new Result { Title = "8051 計時器初值推導", Text = NumberDerivation.Timer8051(mhz, us, mode) };
                }

                // 鮑率
                if (lower.Contains("鮑率") || lower.Contains("baud"))
                {
                    double mhz = ParseMHz(lower, 11.0592);
                    var nums = Numbers(RemoveMHz(lower)).Select(s => ToLong(s)).Where(v => v >= 300).ToList();
                    if (nums.Count > 0)
                        return new Result { Title = "8051 鮑率推導", Text = NumberDerivation.Baud8051(mhz, (int)nums[0], lower.Contains("smod")) };
                }

                // 實體位址
                var so = SegOffRegex.Match(q);
                if (so.Success && (lower.Contains("實體") || lower.Contains("physical") || lower.Contains("位址") || lower.Contains("段") || q.Trim().Length <= so.Length + 2))
                    return new Result { Title = "8086 實體位址推導", Text = NumberDerivation.PhysicalAddress(so.Groups[1].Value, so.Groups[2].Value) };

                // 旗標推導（a + b / a - b）
                var op = BinaryOpRegex.Match(q);
                if (op.Success && (lower.Contains("旗標") || lower.Contains("flag") || lower.Contains("溢位") || lower.Contains("進位") ||
                                   lower.Contains("cf") || lower.Contains("of") || lower.Contains("cmp") || lower.Contains("比較")))
                {
                    long a, b;
                    if (AsmNumber.TryParse(op.Groups[1].Value, out a) && AsmNumber.TryParse(op.Groups[3].Value, out b))
                        return new Result
                        {
                            Title = "旗標推導",
                            Text = NumberDerivation.AddSubFlags(op.Groups[1].Value, op.Groups[3].Value, bits == 0 ? 8 : bits, op.Groups[2].Value == "-" || lower.Contains("cmp") || lower.Contains("比較"))
                        };
                }

                // 移位
                var shiftMatch = Regex.Match(lower, @"\b(shl|sal|shr|sar|rol|ror)\b");
                if (shiftMatch.Success || lower.Contains("左移") || lower.Contains("右移"))
                {
                    string o = shiftMatch.Success ? shiftMatch.Groups[1].Value : lower.Contains("左移") ? "shl" : lower.Contains("算術右移") ? "sar" : "shr";
                    var nums = Numbers(lower.Replace(o, " ")).Where(s => !IsBitsToken(lower, s)).ToList();
                    if (nums.Count >= 1)
                    {
                        int count = nums.Count >= 2 ? (int)ToLong(nums[1]) : 1;
                        return new Result { Title = o.ToUpperInvariant() + " 移位推導", Text = NumberDerivation.Shift(nums[0], o, count, bits == 0 ? 8 : bits) };
                    }
                }

                // 符號延伸 / 零延伸
                if (lower.Contains("延伸") || lower.Contains("movsx") || lower.Contains("movzx") || lower.Contains("extension"))
                {
                    var nums = Numbers(lower).Where(s => !IsBitsToken(lower, s)).ToList();
                    if (nums.Count >= 1)
                    {
                        int from = bits == 0 ? 8 : bits;
                        var allBits = BitsRegex.Matches(lower).Cast<Match>().Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
                        int to = allBits.Count >= 2 ? allBits[1] : from * 2;
                        if (allBits.Count >= 1) from = allBits[0];
                        return new Result { Title = "延伸推導", Text = NumberDerivation.Extend(nums[0], from, Math.Max(to, from * 2 > 64 ? 64 : to)) };
                    }
                }

                // 位元組序
                if (lower.Contains("小端") || lower.Contains("大端") || lower.Contains("endian") || lower.Contains("位元組序"))
                {
                    var nums = Numbers(lower).Where(s => !IsBitsToken(lower, s)).ToList();
                    if (nums.Count >= 1)
                        return new Result { Title = "位元組序推導", Text = NumberDerivation.Endian(nums[0], bits == 0 ? 4 : bits / 8) };
                }

                // 二補數
                if (lower.Contains("補數") || lower.Contains("complement") || lower.Contains("有號") || lower.Contains("負數") || lower.Contains("signed"))
                {
                    var nums = Numbers(lower).Where(s => !IsBitsToken(lower, s)).ToList();
                    if (nums.Count >= 1)
                        return new Result { Title = "二補數推導", Text = NumberDerivation.TwosComplement(nums[0], bits) };
                }

                // 進位轉換
                bool convertWords = lower.Contains("轉") || lower.Contains("換成") || lower.Contains("進位") || lower.Contains("binary") ||
                                    lower.Contains("hex") || lower.Contains("decimal") || lower.Contains("convert") || lower.Contains("等於多少") ||
                                    lower.Contains("是多少") || lower.Contains("ascii");
                var tokens = Numbers(lower).Where(s => !IsBitsToken(lower, s)).ToList();
                var named = NamedRadixRegex.Match(lower);
                string before = named.Success ? lower.Substring(Math.Max(0, named.Index - 2), Math.Min(2, named.Index)) : string.Empty;
                if (named.Success && before.IndexOf('轉') < 0 && before.IndexOf('成') < 0 && before.IndexOf('為') < 0)
                {
                    // 「十六進位 FF」「二進位 1010」：依照文字指定的進位解讀
                    bool hex = named.Groups[1].Value.Contains("16") || named.Groups[1].Value.Contains("十六") || named.Groups[1].Value == "hex";
                    string digits = named.Groups[2].Value;
                    if (hex || Regex.IsMatch(digits, "^[01]+$"))
                        tokens.Insert(0, hex ? (char.IsDigit(digits[0]) ? digits : "0" + digits) + "h" : digits + "b");
                }
                bool onlyNumber = Regex.IsMatch(q.Trim(), @"^(-?(?:0x[0-9a-f]+|0b[01]+|[0-9][0-9a-f]*h|[01]+b|[0-7]+[oq]|\d+)|'.')$", RegexOptions.IgnoreCase);
                if (tokens.Count >= 1 && (convertWords || onlyNumber))
                    return new Result { Title = "進位轉換推導", Text = NumberDerivation.ConvertBase(tokens[0], bits) };
            }
            catch (ArgumentException ex)
            {
                return new Result { Title = "數值推導", Text = "無法推導：" + ex.Message };
            }
            return null;
        }

        private static int ParseBits(string lower)
        {
            var m = BitsRegex.Match(lower);
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        private static bool IsBitsToken(string lower, string token)
        {
            foreach (Match m in BitsRegex.Matches(lower))
                if (m.Groups[1].Value == token) return true;
            return false;
        }

        private static double ParseMHz(string lower, double fallback)
        {
            var m = MHzRegex.Match(lower);
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : fallback;
        }

        private static string RemoveMHz(string lower)
        {
            return MHzRegex.Replace(lower, " ").Replace("8051", " ");
        }

        private static List<string> Numbers(string text)
        {
            var list = new List<string>();
            foreach (Match m in NumberToken.Matches(text)) list.Add(m.Groups[1].Value);
            return list;
        }

        private static long ToLong(string s)
        {
            long v;
            if (AsmNumber.TryParse(s, out v)) return v;
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return (long)d;
            return -1;
        }
    }
}
