using Assam.Core.Assistant;
using Assam.Core.Derivation;
using Assam.Core.Text;

namespace Assam.Tests
{
    public static class DerivationTests
    {
        [Test]
        public static void ParseNumberFormats()
        {
            long v;
            Assert.True(AsmNumber.TryParse("0x1F", out v)); Assert.Equal(31L, v);
            Assert.True(AsmNumber.TryParse("1Fh", out v)); Assert.Equal(31L, v);
            Assert.True(AsmNumber.TryParse("0FFh", out v)); Assert.Equal(255L, v);
            Assert.True(AsmNumber.TryParse("0Bh", out v)); Assert.Equal(11L, v);
            Assert.True(AsmNumber.TryParse("1010b", out v)); Assert.Equal(10L, v);
            Assert.True(AsmNumber.TryParse("0b1010", out v)); Assert.Equal(10L, v);
            Assert.True(AsmNumber.TryParse("17o", out v)); Assert.Equal(15L, v);
            Assert.True(AsmNumber.TryParse("'A'", out v)); Assert.Equal(65L, v);
            Assert.True(AsmNumber.TryParse("-5", out v)); Assert.Equal(-5L, v);
            Assert.True(AsmNumber.TryParse("$FF", out v)); Assert.Equal(255L, v);
            Assert.True(AsmNumber.TryParse("#30H", out v)); Assert.Equal(48L, v);
            Assert.False(AsmNumber.TryParse("FFh", out v), "字母開頭的 FFh 在 MASM 中是符號，不是數字");
            Assert.False(AsmNumber.TryParse("eax", out v));
        }

        [Test]
        public static void ConvertHexToAll()
        {
            string s = NumberDerivation.ConvertBase("0x2F");
            Assert.Contains("= 47", s);
            Assert.Contains("101111", s);
            Assert.Contains("57o", s);
        }

        [Test]
        public static void ConvertDecimalShowsDivisionSteps()
        {
            string s = NumberDerivation.ConvertBase("45");
            Assert.Contains("45 ÷ 2 =", s);
            Assert.Contains("101101", s);
            Assert.Contains("2Dh", s);
        }

        [Test]
        public static void TwosComplementNegative()
        {
            string s = NumberDerivation.TwosComplement("-5", 8);
            Assert.Contains("1111 1011", s);
            Assert.Contains("0xFB", s);
            Assert.Contains("251", s);
        }

        [Test]
        public static void TwosComplementInterpretPattern()
        {
            string s = NumberDerivation.TwosComplement("0B5h", 8);
            Assert.Contains("= -75", s);
        }

        [Test]
        public static void AddFlags()
        {
            string s = NumberDerivation.AddSubFlags("9Ch", "85h", 8, false);
            Assert.Contains("CF=1", s);
            Assert.Contains("OF=1", s);
            Assert.Contains("PF=1", s);
            Assert.Contains("AF=1", s);
            Assert.Contains("0011 1000", s, "進位列");
        }

        [Test]
        public static void SubFlagsAndJumps()
        {
            string s = NumberDerivation.AddSubFlags("80h", "1", 8, true);
            Assert.Contains("OF=1", s);
            Assert.Contains("JL（有號小於）會跳", s);
        }

        [Test]
        public static void PhysicalAddress()
        {
            Assert.Contains("12350h", NumberDerivation.PhysicalAddress("1234", "0010"));
        }

        [Test]
        public static void Delay8051()
        {
            string s = NumberDerivation.Delay8051(12, new[] { 200, 250 }, false);
            Assert.Contains("100603 週期", s);
        }

        [Test]
        public static void Timer8051Mode1()
        {
            string s = NumberDerivation.Timer8051(12, 50000, 1);
            Assert.Contains("3CB0h", s);
            Assert.Contains("TH = 高位元組 = 3Ch", s);
        }

        [Test]
        public static void Baud8051()
        {
            Assert.Contains("FDh", NumberDerivation.Baud8051(11.0592, 9600, false));
        }

        [Test]
        public static void ShiftSar()
        {
            string s = NumberDerivation.Shift("0B2h", "SAR", 1, 8);
            Assert.Contains("0xD9", s);
        }

        [Test]
        public static void ParserRecognizesRequests()
        {
            Assert.Equal("進位轉換推導", NumberRequestParser.TryHandle("0x7F轉二進位").Title);
            Assert.Equal("二補數推導", NumberRequestParser.TryHandle("-5 的 8 位元二補數").Title);
            Assert.Equal("旗標推導", NumberRequestParser.TryHandle("8 位元 7Fh + 1 的旗標").Title);
            Assert.Equal("8086 實體位址推導", NumberRequestParser.TryHandle("1234:0010 的實體位址").Title);
            Assert.Equal("8051 延遲時間推導", NumberRequestParser.TryHandle("8051 12MHz 延遲 200 250").Title);
            Assert.Equal("8051 計時器初值推導", NumberRequestParser.TryHandle("12MHz 計時器 50ms 初值").Title);
            Assert.Equal("8051 鮑率推導", NumberRequestParser.TryHandle("11.0592MHz 鮑率 9600").Title);
            Assert.Contains("= 255", NumberRequestParser.TryHandle("十六進位 FF 轉十進位").Text);
            Assert.Null(NumberRequestParser.TryHandle("什麼是堆疊"));
        }
    }
}
