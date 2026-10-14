using System;
using System.IO;
using System.Linq;
using Assam.Core.Binary;

namespace Assam.Tests
{
    public static class BinaryLoaderTests
    {
        /// <summary>測試資料檔的完整路徑（建置時從 TestData 複製到輸出資料夾）。</summary>
        public static string TestFile(string name)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", name);
            if (!File.Exists(path)) throw new AssertionException("找不到測試檔 " + path);
            return path;
        }

        [Test]
        public static void LoadPe32Exe()
        {
            var b = BinaryLoader.Load(TestFile("hello32.exe"));
            Assert.Equal(BinaryKind.Pe32, b.Kind);
            Assert.Equal(32, b.Bits);
            Assert.False(b.IsDll);
            Assert.False(b.IsDotNet);
            Assert.True(b.HasEntryPoint);
            Assert.True(b.IsExecutable(b.EntryPoint), "進入點應在可執行區段");
            Assert.NotNull(b.Sections.FirstOrDefault(s => s.Name == ".text"));
            Assert.True(b.Imports.Any(i => i.Name == "strcpy"), string.Join(",", b.Imports.Select(i => i.Name)));
            Assert.True(b.Imports.Any(i => i.Dll.Equals("KERNEL32.dll", StringComparison.OrdinalIgnoreCase)));
            Assert.True(b.Strings.Any(s => s.Text == "Hello, ASSAM!"), "應找到字串");
            var import = b.Imports.First();
            Assert.Equal(import, b.ImportAt(import.IatAddress));
            Assert.Contains("Windows 主控台", b.Describe());
        }

        [Test]
        public static void LoadPe64Exe()
        {
            var b = BinaryLoader.Load(TestFile("hello64.exe"));
            Assert.Equal(BinaryKind.Pe64, b.Kind);
            Assert.Equal(64, b.Bits);
            Assert.True(b.ImageBase >= 0x140000000UL || b.ImageBase == 0x400000UL, "ImageBase 0x" + b.ImageBase.ToString("X"));
            Assert.True(b.IsExecutable(b.EntryPoint));
            Assert.True(b.Imports.Count > 5);
        }

        [Test]
        public static void LoadDllExports()
        {
            foreach (var name in new[] { "mathlib32.dll", "mathlib64.dll" })
            {
                var b = BinaryLoader.Load(TestFile(name));
                Assert.True(b.IsDll, name);
                var names = b.Exports.Select(e => e.Name).ToList();
                Assert.True(names.Contains("Multiply"), name + "：" + string.Join(",", names));
                Assert.True(names.Any(n => n != null && n.StartsWith("AddNumbers", StringComparison.Ordinal)), name);
                var mul = b.Exports.First(e => e.Name == "Multiply");
                Assert.True(b.IsExecutable(mul.Address), "匯出函式應在程式區段");
                Assert.Equal(mul, b.ExportAt(mul.Address));
                Assert.True(b.Imports.Any(i => i.Name == "MessageBoxA"), name);
            }
        }

        [Test]
        public static void LoadDotNetAssembly()
        {
            var b = BinaryLoader.Load(TestFile("hello_cs.exe"));
            Assert.True(b.IsDotNet);
            Assert.True(b.ClrHeaderRva != 0);
            Assert.True(b.Imports.Any(i => i.Name == "_CorExeMain"));
            Assert.Contains(".NET", b.Describe());
        }

        [Test]
        public static void LoadDosCom()
        {
            var b = BinaryLoader.Load(TestFile("dos_hello.com"));
            Assert.Equal(BinaryKind.DosCom, b.Kind);
            Assert.Equal(16, b.Bits);
            Assert.Equal(0x100UL, b.EntryPoint);
            Assert.Equal((byte)0xB4, b.ReadVa(0x100, 1)[0]);
            Assert.True(b.Strings.Any(s => s.Text.Contains("Hello, DOS!")), "COM 檔的字串和程式碼混在一起，只要求包含");
        }

        [Test]
        public static void LoadDosMz()
        {
            var b = BinaryLoader.Load(TestFile("dos_hello_mz.exe"));
            Assert.Equal(BinaryKind.DosMz, b.Kind);
            Assert.Equal(16, b.Bits);
            Assert.Equal(32, b.DosHeaderSize);
            Assert.Equal(6UL, b.EntryPoint);
            Assert.Equal((byte)0x8C, b.ReadVa(b.EntryPoint, 1)[0]);
        }

        [Test]
        public static void RejectsNonExecutable()
        {
            Assert.Throws<BinaryFormatException>(() => BinaryLoader.Load(new byte[] { 1, 2, 3, 4, 5 }, "x.bin"));
            Assert.Throws<BinaryFormatException>(() => BinaryLoader.Load(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1 }, "a.out"));
        }

        [Test]
        public static void TruncatedAndCorruptFilesDoNotCrash()
        {
            var original = File.ReadAllBytes(TestFile("mathlib32.dll"));
            var rng = new Random(1234);
            int loaded = 0, rejected = 0;
            for (int round = 0; round < 300; round++)
            {
                byte[] data;
                if (round % 3 == 0)
                {
                    data = new byte[rng.Next(2, original.Length)];
                    Buffer.BlockCopy(original, 0, data, 0, data.Length);
                }
                else
                {
                    data = (byte[])original.Clone();
                    for (int k = 0; k < 20; k++) data[rng.Next(0, Math.Min(data.Length, 1024))] = (byte)rng.Next(256);
                    data[0] = (byte)'M';
                    data[1] = (byte)'Z';
                }
                try
                {
                    var b = BinaryLoader.Load(data, "fuzz.dll");
                    b.Describe();
                    var unused = b.Strings.Count;
                    foreach (var s in b.Sections) b.ReadVa(s.VirtualAddress, 64);
                    loaded++;
                }
                catch (BinaryFormatException)
                {
                    rejected++;
                }
            }
            Assert.Equal(300, loaded + rejected);
        }

        [Test]
        public static void RawLoadAndMapping()
        {
            var b = BinaryLoader.LoadRaw(new byte[] { 0x90, 0xC3 }, "x.bin", 32, 0x1000);
            Assert.Equal(BinaryKind.Raw, b.Kind);
            Assert.Equal(2, b.ReadVa(0x1000, 10).Length);
            Assert.Equal(0, b.ReadVa(0x2000, 10).Length);
            Assert.True(b.IsExecutable(0x1001));
        }
    }
}
