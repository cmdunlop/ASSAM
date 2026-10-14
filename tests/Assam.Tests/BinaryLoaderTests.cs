using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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

        // ==================================================================
        // 真實檔案
        // ==================================================================

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
            // MinGW 的執行檔有 TLS 目錄與兩個回呼（tls_callback_0、__dyn_tls_init）
            Assert.Equal(2, b.TlsCallbacks.Count);
            Assert.True(b.TlsCallbacks.All(b.IsExecutable), "TLS 回呼應在程式區段");
            Assert.Contains("TLS 回呼", b.Describe());
            Assert.Equal(0, b.Warnings.Count, string.Join(" | ", b.Warnings));
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
            Assert.Equal(2, b.TlsCallbacks.Count);
            Assert.True(b.TlsCallbacks.All(b.IsExecutable));
            Assert.Equal(0, b.Warnings.Count, string.Join(" | ", b.Warnings));
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
                Assert.Equal(5, b.Exports.Count, name);
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

        /// <summary>clang-cl + lld-link 產生的 DLL：延遲載入、序號匯入、轉送匯出、只有序號的匯出、TLS 回呼（與 llvm-readobj 的輸出一致）。</summary>
        [Test]
        public static void LoadLinkerFeatureDlls()
        {
            foreach (var name in new[] { "pefeatures32.dll", "pefeatures64.dll" })
            {
                var b = BinaryLoader.Load(TestFile(name));
                Assert.True(b.IsDll, name);
                Assert.Equal(0, b.Warnings.Count, name + "：" + string.Join(" | ", b.Warnings));

                var sleep = b.Imports.Single(i => i.Name == "Sleep");
                Assert.Equal("kernel32.dll", sleep.Dll);
                Assert.False(sleep.IsDelayLoad);

                var ord = b.Imports.Single(i => i.Dll == "ordlib.dll");
                Assert.Null(ord.Name, "以序號匯入沒有名稱");
                Assert.Equal(7, ord.Ordinal);
                Assert.Equal("ordlib.dll!#7", ord.FullName);

                var delay = b.Imports.Single(i => i.IsDelayLoad);
                Assert.Equal("user32.dll", delay.Dll);
                Assert.Equal("MessageBoxA", delay.Name);
                Assert.Equal(delay, b.ImportAt(delay.IatAddress));
                Assert.Equal(b.ImageBase + 0x3008, delay.IatAddress, name);

                var fwd = b.Exports.Single(e => e.Name == "FwdSleep");
                Assert.Equal("kernel32.Sleep", fwd.ForwardTo);
                Assert.Null(b.ExportAt(fwd.Address), "轉送匯出不是程式碼");

                var hidden = b.Exports.Single(e => e.Ordinal == 20);
                Assert.Null(hidden.Name, "NONAME 匯出只有序號");
                Assert.True(b.IsExecutable(hidden.Address));

                var add = b.Exports.Single(e => e.Name == "AddTwo");
                var alias = b.Exports.Single(e => e.Name == "AliasAdd");
                Assert.Equal(add.Address, alias.Address);
                Assert.Equal(21, add.Ordinal);
                Assert.Equal(5, b.Exports.Count);

                Assert.Equal(2, b.TlsCallbacks.Count, name);
                Assert.True(b.TlsCallbacks.All(b.IsExecutable), name);
                Assert.True(b.TlsCallbacks[0] != b.TlsCallbacks[1]);
            }
        }

        [Test]
        public static void RejectsNonExecutable()
        {
            Assert.Throws<BinaryFormatException>(() => BinaryLoader.Load(new byte[] { 1, 2, 3, 4, 5 }, "x.bin"));
            Assert.Throws<BinaryFormatException>(() => BinaryLoader.Load(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1 }, "a.out"));
            Assert.Throws<BinaryFormatException>(() => BinaryLoader.Load(new byte[] { (byte)'M', (byte)'Z', 0 }, "tiny.exe"));
            Assert.Throws<BinaryFormatException>(() => BinaryLoader.Load(new byte[0], "empty.exe"));
            Assert.Throws<BinaryFormatException>(() => BinaryLoader.Load(new byte[0x10000], "big.com"));
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
            Assert.Throws<ArgumentNullException>(() => BinaryLoader.LoadRaw(null, "x", 32, 0));
        }

        // ==================================================================
        // 自建的 PE（每個邊界情況一個）
        // ==================================================================

        /// <summary>
        /// 測試用的極簡 PE 產生器：先在「虛擬映像」中以 RVA 寫入資料，Build() 時再依區段表排成檔案。
        /// 所有欄位都可以改成不合理的值，用來測試載入器的防呆。
        /// </summary>
        private sealed class PeBuilder
        {
            public sealed class Sec
            {
                public string Name;
                public uint Rva;
                public uint VirtualSize;
                public byte[] Data = new byte[0];
                public uint Characteristics;
                public uint? RawOffsetOverride;
                public uint? RawSizeOverride;
                public uint RawOffset;
                public uint RawSize;
            }

            public bool Pe64;
            public ushort? Machine;
            public ulong ImageBase = 0x400000;
            public uint EntryRva = 0x1000;
            public uint SizeOfHeaders = 0x200;
            public ushort Characteristics = 0x0102;
            public uint NumberOfRvaAndSizes = 16;
            public ushort? OptionalHeaderSize;
            public uint LfaNew = 0x40;
            public byte[] Trailer = new byte[0];
            public readonly uint[] DirRva = new uint[16];
            public readonly uint[] DirSize = new uint[16];
            public readonly List<Sec> Sections = new List<Sec>();

            public PeBuilder(bool pe64)
            {
                Pe64 = pe64;
                if (pe64) ImageBase = 0x140000000UL;
            }

            public ulong Va(uint rva)
            {
                return ImageBase + rva;
            }

            public Sec Add(string name, uint rva, uint virtualSize, int rawLength, uint characteristics)
            {
                var s = new Sec { Name = name, Rva = rva, VirtualSize = virtualSize, Data = new byte[rawLength], Characteristics = characteristics };
                Sections.Add(s);
                return s;
            }

            public Sec AddCode(uint rva = 0x1000)
            {
                var s = Add(".text", rva, 0x1000, 0x200, 0x60000020);
                for (int i = 0; i < s.Data.Length; i++) s.Data[i] = 0xC3;
                return s;
            }

            public Sec AddData(uint rva = 0x2000, int size = 0x1000, string name = ".rdata")
            {
                return Add(name, rva, (uint)size, size, 0x40000040);
            }

            private Sec Find(uint rva, int length)
            {
                foreach (var s in Sections)
                    if (rva >= s.Rva && (long)rva - s.Rva + length <= s.Data.Length) return s;
                throw new InvalidOperationException("測試錯誤：RVA 0x" + rva.ToString("X") + " 不在任何區段資料內");
            }

            public void U16(uint rva, uint value)
            {
                var s = Find(rva, 2);
                int o = (int)(rva - s.Rva);
                s.Data[o] = (byte)value;
                s.Data[o + 1] = (byte)(value >> 8);
            }

            public void U32(uint rva, uint value)
            {
                var s = Find(rva, 4);
                Put32(s.Data, (int)(rva - s.Rva), value);
            }

            public void U64(uint rva, ulong value)
            {
                U32(rva, (uint)value);
                U32(rva + 4, (uint)(value >> 32));
            }

            public void Ptr(uint rva, ulong value)
            {
                if (Pe64) U64(rva, value);
                else U32(rva, (uint)value);
            }

            public void Str(uint rva, string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text + "\0");
                var s = Find(rva, bytes.Length);
                Buffer.BlockCopy(bytes, 0, s.Data, (int)(rva - s.Rva), bytes.Length);
            }

            /// <summary>寫入 Hint/Name（2 位元組 hint + 名稱）。</summary>
            public void HintName(uint rva, ushort hint, string name)
            {
                U16(rva, hint);
                Str(rva + 2, name);
            }

            public static void Put32(byte[] d, int o, uint v)
            {
                d[o] = (byte)v;
                d[o + 1] = (byte)(v >> 8);
                d[o + 2] = (byte)(v >> 16);
                d[o + 3] = (byte)(v >> 24);
            }

            private static void Put16(byte[] d, int o, uint v)
            {
                d[o] = (byte)v;
                d[o + 1] = (byte)(v >> 8);
            }

            public byte[] Build()
            {
                int fixedSize = Pe64 ? 112 : 96;
                int optSize = OptionalHeaderSize ?? (fixedSize + 16 * 8);
                int dirBytes = (int)Math.Min(NumberOfRvaAndSizes, 16u) * 8;
                long sectionTable = LfaNew + 24 + optSize;
                long headerEnd = Math.Max(Math.Max(sectionTable + Sections.Count * 40, LfaNew + 24 + fixedSize + dirBytes), 0x40);
                uint fileAlign = 0x200;
                uint pos = (uint)((Math.Max(headerEnd, Math.Min(SizeOfHeaders, 0x10000u)) + fileAlign - 1) / fileAlign * fileAlign);
                foreach (var s in Sections)
                {
                    s.RawSize = s.RawSizeOverride ?? (uint)((s.Data.Length + fileAlign - 1) / fileAlign * fileAlign);
                    if (s.RawOffsetOverride.HasValue)
                    {
                        s.RawOffset = s.RawOffsetOverride.Value;
                    }
                    else
                    {
                        s.RawOffset = s.Data.Length == 0 ? 0 : pos;
                        if (s.Data.Length > 0) pos += (uint)((s.Data.Length + fileAlign - 1) / fileAlign * fileAlign);
                    }
                }
                long fileSize = Math.Max(pos, headerEnd);
                foreach (var s in Sections)
                    if (!s.RawOffsetOverride.HasValue && s.Data.Length > 0) fileSize = Math.Max(fileSize, s.RawOffset + s.Data.Length);
                var d = new byte[fileSize + Trailer.Length];
                Buffer.BlockCopy(Trailer, 0, d, (int)fileSize, Trailer.Length);
                d[0] = (byte)'M';
                d[1] = (byte)'Z';
                int pe = (int)LfaNew;
                d[pe] = (byte)'P';
                d[pe + 1] = (byte)'E';
                d[pe + 2] = 0;
                d[pe + 3] = 0;
                int coff = pe + 4;
                Put16(d, coff, Machine ?? (Pe64 ? 0x8664u : 0x14Cu));
                Put16(d, coff + 2, (uint)Sections.Count);
                Put32(d, coff + 4, 0x5F000000);
                Put16(d, coff + 16, (uint)optSize);
                Put16(d, coff + 18, Characteristics);
                int opt = coff + 20;
                Put16(d, opt, Pe64 ? 0x20Bu : 0x10Bu);
                d[opt + 2] = 14;
                d[opt + 3] = 0;
                Put32(d, opt + 16, EntryRva);
                if (Pe64)
                {
                    Put32(d, opt + 24, (uint)ImageBase);
                    Put32(d, opt + 28, (uint)(ImageBase >> 32));
                }
                else
                {
                    Put32(d, opt + 28, (uint)ImageBase);
                }
                Put32(d, opt + 32, 0x1000);
                Put32(d, opt + 36, fileAlign);
                uint image = 0x1000;
                foreach (var s in Sections) image = Math.Max(image, s.Rva + Math.Max(s.VirtualSize, (uint)s.Data.Length));
                Put32(d, opt + 56, (image + 0xFFF) & ~0xFFFu);
                Put32(d, opt + 60, SizeOfHeaders);
                Put16(d, opt + 68, 3);
                int dirCountAt = opt + (Pe64 ? 108 : 92);
                Put32(d, dirCountAt, NumberOfRvaAndSizes);
                for (int i = 0; i < Math.Min(NumberOfRvaAndSizes, 16u); i++)
                {
                    int at = dirCountAt + 4 + i * 8;
                    if (at + 8 > sectionTable && i >= (optSize - fixedSize) / 8) continue; // 與區段表重疊的部分由區段表決定
                    Put32(d, at, DirRva[i]);
                    Put32(d, at + 4, DirSize[i]);
                }
                for (int i = 0; i < Sections.Count; i++)
                {
                    var s = Sections[i];
                    int sh = (int)sectionTable + i * 40;
                    var name = Encoding.ASCII.GetBytes(s.Name);
                    Buffer.BlockCopy(name, 0, d, sh, Math.Min(8, name.Length));
                    Put32(d, sh + 8, s.VirtualSize);
                    Put32(d, sh + 12, s.Rva);
                    Put32(d, sh + 16, s.RawSize);
                    Put32(d, sh + 20, s.RawOffset);
                    Put32(d, sh + 36, s.Characteristics);
                    if (s.Data.Length > 0 && s.RawOffset < d.Length)
                        Buffer.BlockCopy(s.Data, 0, d, (int)s.RawOffset, (int)Math.Min(s.Data.Length, d.Length - s.RawOffset));
                }
                // 最後才寫 e_lfanew：極小 PE 的選用標頭會蓋過 DOS 檔頭的 0x3C（真實的極小 PE 也是讓兩者共用）
                Put32(d, 0x3C, LfaNew);
                return d;
            }

            public LoadedBinary Load()
            {
                return BinaryLoader.Load(Build(), Pe64 ? "crafted64.dll" : "crafted32.exe");
            }
        }

        /// <summary>PE32 與 PE32+ 都跑一次。</summary>
        private static void BothFormats(Action<bool> test)
        {
            test(false);
            test(true);
        }

        private static string WarningsOf(LoadedBinary b)
        {
            return string.Join(" | ", b.Warnings);
        }

        [Test]
        public static void CraftedImportsWithHintsOrdinalsAndIat()
        {
            BothFormats(pe64 =>
            {
                var p = new PeBuilder(pe64);
                p.AddCode();
                p.AddData();
                int ptr = pe64 ? 8 : 4;
                ulong ordinalFlag = pe64 ? 0x8000000000000000UL : 0x80000000UL;
                // 描述元：KERNEL32（有 ILT）、WS2_32（沒有 ILT，只有 IAT）、空白結尾
                p.DirRva[1] = 0x2000;
                p.DirSize[1] = 60;
                p.U32(0x2000, 0x2100);      // OriginalFirstThunk
                p.U32(0x2000 + 12, 0x2400); // Name
                p.U32(0x2000 + 16, 0x2200); // FirstThunk
                p.U32(0x2014, 0);
                p.U32(0x2014 + 12, 0x2410);
                p.U32(0x2014 + 16, 0x2300);
                p.Str(0x2400, "KERNEL32.dll");
                p.Str(0x2410, "WS2_32.dll");
                p.HintName(0x2500, 0x123, "ExitProcess");
                p.HintName(0x2520, 0x45, "GetTickCount");
                p.Ptr(0x2100, 0x2500);
                p.Ptr(0x2100 + (uint)ptr, ordinalFlag | 0x99);
                p.Ptr(0x2100 + 2u * (uint)ptr, 0x2520);
                // IAT 在檔案中是相同內容（尚未繫結）
                p.Ptr(0x2200, 0x2500);
                p.Ptr(0x2200 + (uint)ptr, ordinalFlag | 0x99);
                p.Ptr(0x2200 + 2u * (uint)ptr, 0x2520);
                p.Ptr(0x2300, ordinalFlag | 115);
                var b = p.Load();
                Assert.Equal(4, b.Imports.Count, string.Join(",", b.Imports));
                var exit = b.Imports[0];
                Assert.Equal("KERNEL32.dll!ExitProcess", exit.FullName);
                Assert.Equal(0x123, exit.Ordinal);
                Assert.Equal(p.Va(0x2200), exit.IatAddress);
                Assert.Equal("KERNEL32.dll!#153", b.Imports[1].FullName);
                Assert.Equal(p.Va(0x2200 + (uint)ptr), b.Imports[1].IatAddress);
                Assert.Equal("GetTickCount", b.Imports[2].Name);
                Assert.Equal(p.Va(0x2200 + 2u * (uint)ptr), b.Imports[2].IatAddress);
                Assert.Equal("WS2_32.dll!#115", b.Imports[3].FullName);
                Assert.Equal(p.Va(0x2300), b.Imports[3].IatAddress);
                Assert.Equal(b.Imports[2], b.ImportAt(p.Va(0x2200 + 2u * (uint)ptr)));
                Assert.Equal(0, b.Warnings.Count, WarningsOf(b));
            });
        }

        /// <summary>PE32+ 的序號旗標是第 63 位元；第 31 位元為 1 只是一個（不合理的）大 RVA，不可當成序號。</summary>
        [Test]
        public static void CraftedPe64OrdinalFlagIsBit63()
        {
            var p = new PeBuilder(true);
            p.AddCode();
            p.AddData();
            p.DirRva[1] = 0x2000;
            p.U32(0x2000, 0x2100);
            p.U32(0x2000 + 12, 0x2400);
            p.U32(0x2000 + 16, 0x2200);
            p.Str(0x2400, "a.dll");
            p.HintName(0x2500, 1, "WrongIfMasked");
            p.U64(0x2100, 0x80002500UL);
            p.U64(0x2108, 0x8000000000000005UL);
            p.U64(0x2200, 1); // IAT 內容不影響（有 ILT）
            var b = p.Load();
            Assert.Equal(2, b.Imports.Count, string.Join(",", b.Imports));
            Assert.Equal("(RVA 0x80002500)", b.Imports[0].Name);
            Assert.Null(b.Imports[1].Name);
            Assert.Equal(5, b.Imports[1].Ordinal);
        }

        [Test]
        public static void CraftedDelayImportsRvaAndVaBased()
        {
            BothFormats(pe64 =>
            {
                var p = new PeBuilder(pe64);
                p.AddCode();
                p.AddData();
                p.Add(".data", 0x3000, 0x1000, 0x200, 0xC0000040);
                int ptr = pe64 ? 8 : 4;
                ulong ordinalFlag = pe64 ? 0x8000000000000000UL : 0x80000000UL;
                p.DirRva[13] = 0x2000;
                p.DirSize[13] = 96;
                // 新版：Attributes = 1，欄位是 RVA
                p.U32(0x2000, 1);
                p.U32(0x2004, 0x2400);
                p.U32(0x2008, 0x3000);
                p.U32(0x200C, 0x3010);
                p.U32(0x2010, 0x2100);
                p.Str(0x2400, "USER32.dll");
                p.HintName(0x2500, 0x20, "MessageBoxW");
                p.Ptr(0x2100, 0x2500);
                p.Ptr(0x2100 + (uint)ptr, ordinalFlag | 42);
                // 舊版（VC6）：Attributes = 0，欄位是 VA（PE32+ 也以 32 位元欄位儲存，這裡只取低 32 位元）
                uint vaBase = (uint)p.ImageBase;
                p.U32(0x2020, 0);
                p.U32(0x2024, vaBase + 0x2410);
                p.U32(0x2028, vaBase + 0x3008);
                p.U32(0x202C, vaBase + 0x3080);
                p.U32(0x2030, vaBase + 0x2180);
                p.Str(0x2410, "OLDSTYLE.dll");
                p.HintName(0x2520, 0, "OldFunc");
                p.Ptr(0x2180, (pe64 ? p.ImageBase : vaBase) + 0x2520);
                var b = p.Load();
                var delays = b.Imports.Where(i => i.IsDelayLoad).ToList();
                if (!pe64)
                {
                    Assert.Equal(3, delays.Count, string.Join(",", b.Imports) + " / " + WarningsOf(b));
                    Assert.Equal("OLDSTYLE.dll!OldFunc", delays[2].FullName);
                    Assert.Equal(p.Va(0x3080), delays[2].IatAddress);
                }
                Assert.Equal("USER32.dll!MessageBoxW", delays[0].FullName);
                Assert.Equal(0x20, delays[0].Ordinal);
                Assert.Equal(p.Va(0x3010), delays[0].IatAddress);
                Assert.Equal("USER32.dll!#42", delays[1].FullName);
                Assert.Equal(p.Va(0x3010 + (uint)ptr), delays[1].IatAddress);
                Assert.Contains("延遲載入", delays[0].ToString());
            });
        }

        [Test]
        public static void CraftedExportsWithAliasesForwardersAndBadNameIndexes()
        {
            BothFormats(pe64 =>
            {
                var p = new PeBuilder(pe64);
                p.AddCode();
                p.AddData();
                p.DirRva[0] = 0x2000;
                p.DirSize[0] = 0x400;
                p.U32(0x2000 + 12, 0x2300);
                p.Str(0x2300, "crafted.dll");
                p.U32(0x2000 + 16, 10);     // OrdinalBase
                p.U32(0x2000 + 20, 4);      // NumberOfFunctions
                p.U32(0x2000 + 24, 5);      // NumberOfNames（比函式多）
                p.U32(0x2000 + 28, 0x2040); // AddressOfFunctions
                p.U32(0x2000 + 32, 0x2060); // AddressOfNames
                p.U32(0x2000 + 36, 0x2080); // AddressOfNameOrdinals
                p.U32(0x2040, 0x1010);      // #10
                p.U32(0x2044, 0);           // #11 未使用
                p.U32(0x2048, 0x2200);      // #12 轉送（位在匯出目錄範圍內）
                p.U32(0x204C, 0x1020);      // #13 沒有名稱
                p.Str(0x2200, "NTDLL.RtlAllocateHeap");
                string[] names = { "First", "FirstAlias", "Forwarded", "Ghost", "Unused" };
                ushort[] index = { 0, 0, 2, 9, 1 };
                for (int i = 0; i < names.Length; i++)
                {
                    p.U32(0x2060 + (uint)i * 4, 0x2100 + (uint)i * 0x10);
                    p.Str(0x2100 + (uint)i * 0x10, names[i]);
                    p.U16(0x2080 + (uint)i * 2, index[i]);
                }
                var b = p.Load();
                var text = string.Join(",", b.Exports);
                Assert.Equal(4, b.Exports.Count, text);
                var first = b.Exports.Single(e => e.Name == "First");
                var alias = b.Exports.Single(e => e.Name == "FirstAlias");
                Assert.Equal(10, first.Ordinal);
                Assert.Equal(p.Va(0x1010), first.Address);
                Assert.Equal(first.Address, alias.Address);
                Assert.Equal(10, alias.Ordinal);
                Assert.Equal(first, b.ExportAt(p.Va(0x1010)));
                var fwd = b.Exports.Single(e => e.Name == "Forwarded");
                Assert.Equal("NTDLL.RtlAllocateHeap", fwd.ForwardTo);
                Assert.Equal(12, fwd.Ordinal);
                var noName = b.Exports.Single(e => e.Ordinal == 13);
                Assert.Null(noName.Name);
                Assert.Equal("#13", noName.DisplayName);
                Assert.False(b.Exports.Any(e => e.Name == "Ghost" || e.Name == "Unused" || e.Ordinal == 11), text);
                Assert.Contains("名稱序號超出", WarningsOf(b));
            });
        }

        [Test]
        public static void CraftedTlsCallbacks()
        {
            BothFormats(pe64 =>
            {
                var p = new PeBuilder(pe64);
                p.AddCode();
                p.AddData();
                p.DirRva[9] = 0x2000;
                p.DirSize[9] = pe64 ? 40u : 24u;
                uint callbacksField = pe64 ? 24u : 12u;
                p.Ptr(0x2000 + callbacksField, p.Va(0x2100));
                p.Ptr(0x2100, p.Va(0x1040));
                p.Ptr(0x2100 + (pe64 ? 8u : 4u), p.Va(0x1080));
                p.Ptr(0x2100 + (pe64 ? 16u : 8u), p.Va(0x1040)); // 重複的只算一次
                var b = p.Load();
                Assert.Equal(2, b.TlsCallbacks.Count, WarningsOf(b));
                Assert.Equal(p.Va(0x1040), b.TlsCallbacks[0]);
                Assert.Equal(p.Va(0x1080), b.TlsCallbacks[1]);
                Assert.Equal(0, b.Warnings.Count, WarningsOf(b));

                // 回呼表指到映像外、回呼指到資料區：不當掉，只警告
                p.Ptr(0x2000 + callbacksField, 0x10);
                b = p.Load();
                Assert.Equal(0, b.TlsCallbacks.Count);
                Assert.Contains("TLS 回呼表位址", WarningsOf(b));
                p.Ptr(0x2000 + callbacksField, p.Va(0x2100));
                p.Ptr(0x2100, p.Va(0x2800));
                b = p.Load();
                Assert.Contains("不在可執行區段", WarningsOf(b));
            });
        }

        /// <summary>RawSize 到 VirtualSize 之間（.bss）在執行時是 0，不可對應到其他檔案位元組；位在其中的匯入名稱讀成空的。</summary>
        [Test]
        public static void CraftedBssTailDoesNotMapToFileBytes()
        {
            var p = new PeBuilder(false);
            p.AddCode();
            var data = p.Add(".data", 0x2000, 0x3000, 0x200, 0xC0000040);
            var after = p.Add(".rdata", 0x5000, 0x200, 0x200, 0x40000040);
            for (int i = 0; i < data.Data.Length; i++) data.Data[i] = 0x11;
            for (int i = 0; i < after.Data.Length; i++) after.Data[i] = 0x22;
            // 匯入：DLL 名稱與 ILT 都在 .bss 部分
            p.DirRva[1] = 0x5000;
            p.U32(0x5000, 0x2800);
            p.U32(0x5000 + 12, 0x2900);
            p.U32(0x5000 + 16, 0x2800);
            p.U32(0x5014, 0);
            p.U32(0x5014 + 12, 0);
            p.U32(0x5014 + 16, 0);
            var b = p.Load();
            var sec = b.SectionAt(p.Va(0x2300));
            Assert.NotNull(sec);
            Assert.Equal(".data", sec.Name);
            Assert.Equal(0x200u, sec.RawSize);
            Assert.Equal(0x3000u, sec.MemorySize);
            int offset, available;
            Assert.True(b.TryMapVa(p.Va(0x21FF), out offset, out available));
            Assert.Equal(1, available);
            Assert.Equal((byte)0x11, b.Data[offset]);
            Assert.False(b.TryMapVa(p.Va(0x2200), out offset, out available), ".bss 不可對應到任何檔案位元組");
            Assert.False(b.TryRvaToOffset(0x2F00, out offset));
            Assert.Equal(0, b.ReadVa(p.Va(0x2400), 16).Length);
            Assert.Equal(1, b.ReadVa(p.Va(0x21FF), 16).Length);
            Assert.Null(b.ReadCString(p.Va(0x2900)));
            Assert.Equal(0, b.Imports.Count, "ILT 在 .bss（全是 0）→ 沒有匯入：" + string.Join(",", b.Imports));
        }

        /// <summary>SizeOfHeaders 比第一個區段的 RVA 還大（甚至比檔案大）：區段優先，檔頭只對應到真正屬於檔頭的部分。</summary>
        [Test]
        public static void CraftedSizeOfHeadersLargerThanFirstSection()
        {
            var p = new PeBuilder(false);
            p.SizeOfHeaders = 0x20000;
            var text = p.Add(".text", 0x1000, 0x200, 0x200, 0x60000020);
            for (int i = 0; i < text.Data.Length; i++) text.Data[i] = 0x90;
            p.Trailer = new byte[0x2000];
            var b = p.Load();
            Assert.True(b.Data.Length > 0x1000, "檔案要比 0x1000 大，舊版才會把 RVA 0x1000 當成檔頭");
            Assert.Equal((byte)0x90, b.ReadVa(p.Va(0x1000), 1)[0]);
            int offset;
            Assert.True(b.TryRvaToOffset(0x1000, out offset));
            Assert.Equal((int)text.RawOffset, offset);
            int available;
            Assert.True(b.TryMapVa(p.Va(0x800), out offset, out available));
            Assert.Equal(0x800, offset);
            Assert.Equal(0x800, available, "檔頭區域不可延伸到區段裡");
            Assert.Equal((byte)'M', b.ReadVa(p.ImageBase, 1)[0]);
            Assert.True(b.TryMapVa(p.Va(0x1200), out offset, out available), "區段之後、SizeOfHeaders 之內仍算檔頭");
            Assert.Equal(0x1200, offset);
            Assert.Contains("SizeOfHeaders", WarningsOf(b));
        }

        [Test]
        public static void CraftedSectionSizeEdgeCases()
        {
            var p = new PeBuilder(false);
            p.AddCode();
            var vzero = p.Add(".vzero", 0x2000, 0, 0x200, 0x40000040);
            vzero.Data[0x1FF] = 0x5A;
            var beyond = p.Add(".beyond", 0x3000, 0x1000, 0x200, 0x40000040);
            beyond.RawOffsetOverride = 0x100000;
            var partial = p.Add(".partial", 0x4000, 0x1000, 0, 0x40000040);
            partial.RawSizeOverride = 0x800;
            var rawZeroPtr = p.Add(".noptr", 0x5000, 0x100, 0, 0x40000040);
            rawZeroPtr.RawSizeOverride = 0x200;
            rawZeroPtr.RawOffsetOverride = 0;
            var empty = p.Add(".empty", 0x6000, 0, 0, 0x40000040);
            var bytes = p.Build();
            // .partial 的資料從檔案結尾前 0x100 開始
            int partialOffset = bytes.Length - 0x100;
            PeBuilder.Put32(bytes, 0x40 + 24 + 96 + 128 + 3 * 40 + 20, (uint)partialOffset);
            var b = BinaryLoader.Load(bytes, "edge.exe");
            var s = b.Sections.Single(x => x.Name == ".vzero");
            Assert.Equal(0x200u, s.MemorySize, "VirtualSize 為 0 時用 SizeOfRawData");
            Assert.Equal((byte)0x5A, b.ReadVa(p.Va(0x21FF), 1)[0]);
            Assert.Null(b.SectionAt(p.Va(0x2200)));
            s = b.Sections.Single(x => x.Name == ".beyond");
            Assert.Equal(0u, s.RawSize);
            Assert.NotNull(b.SectionAt(p.Va(0x3000)));
            Assert.Equal(0, b.ReadVa(p.Va(0x3000), 4).Length);
            s = b.Sections.Single(x => x.Name == ".partial");
            Assert.Equal(0x100u, s.RawSize, "超出檔案的部分要截掉");
            Assert.Equal(0x100, b.ReadVa(p.Va(0x4000), 0x1000).Length);
            s = b.Sections.Single(x => x.Name == ".noptr");
            Assert.Equal(0u, s.RawSize, "PointerToRawData = 0 表示沒有檔案內容，不可讀到檔頭");
            Assert.Equal(0, b.ReadVa(p.Va(0x5000), 4).Length);
            Assert.Null(b.SectionAt(p.Va(0x6000)));
            Assert.Contains(".beyond", WarningsOf(b));
            Assert.Contains(".partial", WarningsOf(b));
        }

        [Test]
        public static void CraftedOverlappingSectionsFirstWins()
        {
            var p = new PeBuilder(false);
            var a = p.Add(".a", 0x2000, 0x1000, 0x200, 0x60000020);
            var big = p.Add(".big", 0x1000, 0x4000, 0x200, 0x40000040);
            for (int i = 0; i < a.Data.Length; i++) a.Data[i] = 0xAA;
            for (int i = 0; i < big.Data.Length; i++) big.Data[i] = 0xBB;
            p.EntryRva = 0x2000;
            var b = p.Load();
            Assert.Contains("重疊", WarningsOf(b));
            Assert.Equal(".a", b.SectionAt(p.Va(0x2000)).Name);
            Assert.Equal(".a", b.SectionAt(p.Va(0x2FFF)).Name);
            Assert.Equal(".big", b.SectionAt(p.Va(0x1000)).Name);
            Assert.Equal(".big", b.SectionAt(p.Va(0x3000)).Name);
            Assert.Equal((byte)0xAA, b.ReadVa(p.Va(0x2000), 1)[0]);
            Assert.Equal((byte)0xBB, b.ReadVa(p.Va(0x1000), 1)[0]);
            Assert.Equal(0x200, b.ReadVa(p.Va(0x1000), 0x2000).Length, ".big 只有 0x200 位元組的檔案內容");
            Assert.True(b.IsExecutable(b.EntryPoint));
        }

        [Test]
        public static void CraftedOverlapClampsAtHigherPrioritySection()
        {
            var p = new PeBuilder(false);
            var a = p.Add(".a", 0x1100, 0x100, 0x100, 0x60000020);
            var big = p.Add(".big", 0x1000, 0x1000, 0x400, 0x40000040);
            for (int i = 0; i < a.Data.Length; i++) a.Data[i] = 0xAA;
            for (int i = 0; i < big.Data.Length; i++) big.Data[i] = 0xBB;
            var b = p.Load();
            var bytes = b.ReadVa(p.Va(0x1000), 0x400);
            Assert.Equal(0x100, bytes.Length, "不可越過優先權較高的 .a 起點");
            Assert.True(bytes.All(x => x == 0xBB));
            Assert.Equal((byte)0xAA, b.ReadVa(p.Va(0x1100), 1)[0]);
            Assert.Equal((byte)0xBB, b.ReadVa(p.Va(0x1200), 1)[0]);
        }

        [Test]
        public static void CraftedNoSectionsAndEntryInHeader()
        {
            // 沒有區段：整個檔案當成一個可執行區段
            var p = new PeBuilder(false);
            p.EntryRva = 0x180;
            p.SizeOfHeaders = 0x200;
            p.Trailer = new byte[0x100];
            var bytes = p.Build();
            bytes[0x180] = 0xC3;
            var b = BinaryLoader.Load(bytes, "nosec.exe");
            Assert.Equal(1, b.Sections.Count);
            Assert.True(b.IsExecutable(b.EntryPoint));
            Assert.Equal((byte)0xC3, b.ReadVa(b.EntryPoint, 1)[0]);
            Assert.Contains("沒有區段表", WarningsOf(b));

            // 有區段，但進入點在檔頭裡
            p = new PeBuilder(true);
            p.AddCode(0x1000);
            p.EntryRva = 0x180;
            b = p.Load();
            Assert.True(b.IsExecutable(b.EntryPoint), WarningsOf(b));
            Assert.Equal(".text", b.SectionAt(p.Va(0x1000)).Name, "真正的區段不受影響");
            Assert.Contains("檔頭", WarningsOf(b));
        }

        [Test]
        public static void CraftedOptionalHeaderAndDirectoryCountOddities()
        {
            BothFormats(pe64 =>
            {
                // 選用標頭只有固定部分（沒有資料目錄空間），但 NumberOfRvaAndSizes = 16：區段表與資料目錄重疊
                var p = new PeBuilder(pe64);
                p.OptionalHeaderSize = (ushort)(pe64 ? 112 : 96);
                p.AddCode();
                p.AddData();
                var b = p.Load();
                Assert.Equal(2, b.Sections.Count);
                Assert.Contains("資料目錄超出選用標頭", WarningsOf(b));

                // NumberOfRvaAndSizes = 0：即使目錄有值也不讀
                p = new PeBuilder(pe64);
                p.AddCode();
                p.AddData();
                p.NumberOfRvaAndSizes = 0;
                p.DirRva[1] = 0x2000;
                p.U32(0x2000 + 12, 0x2400);
                p.U32(0x2000 + 16, 0x2100);
                p.Str(0x2400, "x.dll");
                p.U32(0x2100, 0x80000001);
                b = p.Load();
                Assert.Equal(0, b.Imports.Count);

                // NumberOfRvaAndSizes 很大：只讀 16 個
                p.NumberOfRvaAndSizes = 0xFFFFFFFF;
                b = p.Load();
                Assert.Contains("超過 16", WarningsOf(b));
                Assert.Equal(1, b.Imports.Count);

                // 選用標頭被截斷 → 拒絕
                var bytes = new PeBuilder(pe64).Build();
                var cut = new byte[0x40 + 24 + 40];
                Buffer.BlockCopy(bytes, 0, cut, 0, cut.Length);
                Assert.Throws<BinaryFormatException>(() => BinaryLoader.Load(cut, "cut.exe"));
            });
        }

        [Test]
        public static void CraftedOutOfRangeRvasDoNotCrash()
        {
            BothFormats(pe64 =>
            {
                uint[] bad = { 0xFFFFFFFF, 0xFFFFFFF0, 0xFFFFFFEC, 0x7FFFFFFF, 0x80000000, 0x10, 0x1FF, 0x1FFC };
                foreach (uint rva in bad)
                {
                    var p = new PeBuilder(pe64);
                    p.AddCode();
                    p.AddData();
                    p.EntryRva = rva;
                    for (int d = 0; d < 16; d++)
                    {
                        p.DirRva[d] = rva;
                        p.DirSize[d] = 0xFFFFFFFF;
                    }
                    var b = p.Load();
                    b.Describe();
                    Assert.True(b.Imports.Count < 1000);
                }

                // 匯入表的各個 RVA 指到映像外或 .bss
                var q = new PeBuilder(pe64);
                q.AddCode();
                q.AddData();
                q.DirRva[1] = 0x2000;
                q.U32(0x2000, 0x7FFF0000);       // ILT 在映像外
                q.U32(0x2000 + 12, 0xFFFFFFF0);  // 名稱在映像外
                q.U32(0x2000 + 16, 0x2100);
                q.U32(0x2014, 0x2100);
                q.U32(0x2014 + 12, 0x2400);
                q.U32(0x2014 + 16, 0x2100);
                q.Str(0x2400, "b.dll");
                q.Ptr(0x2100, 0x7FFFFFF0);         // Hint/Name 在映像外
                var r = q.Load();
                Assert.Equal(1, r.Imports.Count, string.Join(",", r.Imports));
                Assert.Equal("b.dll", r.Imports[0].Dll);
                Assert.Equal("(RVA 0x7FFFFFF0)", r.Imports[0].Name);
                Assert.Contains("(未知 DLL)", WarningsOf(r));
            });
        }

        [Test]
        public static void CraftedHugeCountsAreCappedAndFast()
        {
            var sw = Stopwatch.StartNew();
            // 5000 個匯入描述元都指到同一個名稱表：10 項時受描述元上限（4096）限制；30 項時受匯入總數上限（100000）限制
            foreach (uint thunks in new uint[] { 10, 30 })
            {
                var p = new PeBuilder(false);
                p.AddCode();
                p.AddData(0x2000, 0x20000);
                p.DirRva[1] = 0x2000;
                for (uint d = 0; d < 5000; d++)
                {
                    uint at = 0x2000 + d * 20;
                    p.U32(at, 0x1F000);
                    p.U32(at + 12, 0x1F800);
                    p.U32(at + 16, 0x1F000);
                }
                p.Str(0x1F800, "many.dll");
                p.HintName(0x1FA00, 0, "SameName");
                for (uint i = 0; i < thunks; i++) p.U32(0x1F000 + i * 4, 0x1FA00);
                var b = p.Load();
                if (thunks == 10)
                {
                    Assert.Equal(40960, b.Imports.Count);
                    Assert.Contains("匯入描述元超過", WarningsOf(b));
                }
                else
                {
                    Assert.Equal(100000, b.Imports.Count);
                    Assert.Contains("匯入函式超過", WarningsOf(b));
                }
                Assert.True(ReferenceEquals(b.Imports[0].Name, b.Imports[1].Name), "相同 RVA 的名稱要共用同一個字串（節省記憶體）");
            }

            // 匯出數量是 0xFFFFFFFF
            var e = new PeBuilder(true);
            e.AddCode();
            e.AddData();
            e.DirRva[0] = 0x2000;
            e.DirSize[0] = 40;
            e.U32(0x2000 + 20, 0xFFFFFFFF);
            e.U32(0x2000 + 24, 0xFFFFFFFF);
            e.U32(0x2000 + 28, 0x1000);
            e.U32(0x2000 + 32, 0x1000);
            e.U32(0x2000 + 36, 0x1000);
            var eb = e.Load();
            Assert.Contains("匯出函式數量", WarningsOf(eb));
            Assert.True(eb.Exports.Count <= 65536);

            // 區段數量 65535（區段表被截斷）
            var bytes = new PeBuilder(false).Build();
            bytes[0x40 + 6] = 0xFF;
            bytes[0x40 + 7] = 0xFF;
            var sb = BinaryLoader.Load(bytes, "sections.exe");
            Assert.Contains("區段", WarningsOf(sb));
            Assert.True(sw.ElapsedMilliseconds < 5000, "耗時 " + sw.ElapsedMilliseconds + " ms");
        }

        [Test]
        public static void CraftedLongSectionNameFromStringTable()
        {
            var p = new PeBuilder(false);
            p.AddCode();
            p.Add("/4", 0x2000, 0x100, 0x200, 0x42000040);
            p.Add("/999", 0x3000, 0x100, 0x200, 0x42000040);
            var strtab = new byte[4 + 13];
            PeBuilder.Put32(strtab, 0, (uint)strtab.Length);
            Encoding.ASCII.GetBytes(".debug_info\0").CopyTo(strtab, 4);
            p.Trailer = strtab;
            var bytes = p.Build();
            PeBuilder.Put32(bytes, 0x40 + 4 + 8, (uint)(bytes.Length - strtab.Length)); // PointerToSymbolTable（0 個符號）
            var b = BinaryLoader.Load(bytes, "longname.exe");
            Assert.Equal(".debug_info", b.Sections[1].Name);
            Assert.Equal("/999", b.Sections[2].Name, "超出字串表的保持原樣");
        }

        [Test]
        public static void CraftedLfanewAndSignatureVariants()
        {
            // e_lfanew 指到檔案外或亂指 → 當成 DOS 程式
            var bytes = new PeBuilder(false).Build();
            foreach (uint lfanew in new uint[] { 0, 2, 0x3C, 0xFFFFFFFF, (uint)bytes.Length - 2, (uint)bytes.Length, 0x80000000 })
            {
                var copy = (byte[])bytes.Clone();
                PeBuilder.Put32(copy, 0x3C, lfanew);
                var b = BinaryLoader.Load(copy, "dos.exe");
                Assert.Equal(BinaryKind.DosMz, b.Kind);
            }

            // e_lfanew 很小（PE 檔頭與 DOS 檔頭重疊的極小 PE）
            var p = new PeBuilder(false);
            p.LfaNew = 4;
            p.AddCode();
            var tiny = BinaryLoader.Load(p.Build(), "tiny.exe");
            Assert.Equal(BinaryKind.Pe32, tiny.Kind);

            // "ZM" 也是 DOS 的 EXE 簽章
            var zm = new byte[64];
            zm[0] = (byte)'Z';
            zm[1] = (byte)'M';
            zm[4] = 1;
            zm[8] = 2;
            Assert.Equal(BinaryKind.DosMz, BinaryLoader.Load(zm, "zm.exe").Kind);

            // .NET ReadyToRun（Linux）：Machine = AMD64 ^ 0x7B79
            var r2r = new PeBuilder(true);
            r2r.Machine = 0x8664 ^ 0x7B79;
            r2r.AddCode();
            var rb = r2r.Load();
            Assert.Equal(64, rb.Bits);
            Assert.Contains("Linux", rb.MachineName);

            // ARM64：載入但不反組譯
            var arm = new PeBuilder(true);
            arm.Machine = 0xAA64;
            arm.AddCode();
            Assert.Equal(0, arm.Load().Bits);
        }

        [Test]
        public static void DosMzHeaderEdgeCases()
        {
            // e_cp = 2、e_cblp = 0x10 → 載入模組到 528 位元組；檔頭 2 段（32 位元組）；檔案 600 位元組 → 尾端 72 位元組是 overlay
            var d = new byte[600];
            d[0] = (byte)'M';
            d[1] = (byte)'Z';
            d[2] = 0x10;
            d[4] = 2;
            d[8] = 2;
            d[20] = 0x10; // IP
            var b = BinaryLoader.Load(d, "a.exe");
            Assert.Equal(496u, b.Sections[0].RawSize);
            Assert.Equal(32u, b.Sections[0].RawOffset);
            Assert.Equal(0x10UL, b.EntryPoint);
            Assert.Contains("overlay", WarningsOf(b));

            // e_cblp > 512、e_cp = 0、檔頭比檔案大、CS:IP 指到外面：都只警告
            d[2] = 0xFF;
            d[3] = 0x7F;
            b = BinaryLoader.Load(d, "b.exe");
            Assert.Contains("e_cblp", WarningsOf(b));
            d[4] = 0;
            b = BinaryLoader.Load(d, "c.exe");
            Assert.Contains("e_cp", WarningsOf(b));
            d[8] = 0xFF;
            d[9] = 0xFF;
            d[22] = 0xFF;
            b = BinaryLoader.Load(d, "d.exe");
            Assert.False(b.HasEntryPoint);
            Assert.Equal(0u, b.Sections[0].RawSize);
            b.Describe();
        }

        // ==================================================================
        // 字串掃描
        // ==================================================================

        [Test]
        public static void StringScannerAsciiUtf16AndNoDuplicates()
        {
            var data = new List<byte>();
            Action<string> ascii = s => { data.AddRange(Encoding.ASCII.GetBytes(s)); data.Add(0); };
            Action<string> wide = s =>
            {
                // 編譯器會把寬字串對齊並以 0 填充（緊接在 ASCII 字串後面、沒有填充時，前面的 ASCII 尾巴可能被併入）
                while ((data.Count & 3) != 0) data.Add(0);
                data.AddRange(Encoding.Unicode.GetBytes(s));
                data.Add(0);
                data.Add(0);
            };
            ascii("Hello, ASSAM!");
            ascii("abc");                 // 太短
            wide("組合語言程式");          // 中文 UTF-16
            wide("Wide string");
            ascii("abcdefghijklmnopqrst");  // 小寫 ASCII：兩兩一組看起來像「漢字」，不可被當成 UTF-16
            data.Add(0);
            var longText = new string('x', 1500) + "END";
            ascii(longText);
            var b = BinaryLoader.LoadRaw(data.ToArray(), "s.bin", 16, 0x1000);
            var strings = b.Strings;
            var texts = strings.Select(s => s.Encoding + ":" + s.Text).ToList();
            var all = string.Join(" | ", texts);
            Assert.True(texts.Contains("ASCII:Hello, ASSAM!"), all);
            Assert.True(texts.Contains("UTF-16:組合語言程式"), all);
            Assert.True(texts.Contains("UTF-16:Wide string"), all);
            Assert.True(texts.Contains("ASCII:abcdefghijklmnopqrst"), all);
            Assert.False(texts.Any(t => t.EndsWith(":abc", StringComparison.Ordinal)), all);
            Assert.False(strings.Any(s => s.Encoding == "UTF-16" && s.Text.Any(c => c >= 0x4E00) && s.Text != "組合語言程式"), "ASCII 被誤認成漢字：" + all);
            Assert.Equal(strings.Count, strings.Select(s => s.Address).Distinct().Count(), "同一個位址不可重複");
            var pieces = strings.Where(s => s.Text.Contains("x")).OrderBy(s => s.Address).ToList();
            Assert.Equal(longText.Length, pieces.Sum(s => s.Text.Length), "長字串分段時不可遺漏字元");
            for (int i = 1; i < strings.Count; i++) Assert.True(strings[i - 1].Address < strings[i].Address, "要依位址排序");
            var hello = strings.First(s => s.Text == "Hello, ASSAM!");
            Assert.Equal(hello, b.StringAt(hello.Address));
            Assert.Null(b.StringAt(0xDEAD));
        }

        [Test]
        public static void StringScannerIsFastOnLargeInput()
        {
            // 30 MB 的雜訊，每 64 KB 放一個字串：字串很少，所以整個檔案都要掃過，必須在合理時間內完成
            var rng = new Random(7);
            var data = new byte[30 * 1024 * 1024];
            rng.NextBytes(data);
            int planted = 0;
            for (int at = 0; at + 64 < data.Length; at += 0x10000)
            {
                data[at] = 0;
                var text = Encoding.ASCII.GetBytes("String number " + planted + "\0");
                Buffer.BlockCopy(text, 0, data, at + 1, text.Length);
                planted++;
            }
            var b = BinaryLoader.LoadRaw(data, "big.bin", 32, 0);
            var sw = Stopwatch.StartNew();
            var strings = b.Strings;
            long ms = sw.ElapsedMilliseconds;
            Assert.True(strings.Count >= planted && strings.Count <= 20000, "數量 " + strings.Count);
            Assert.Equal(planted, strings.Count(s => s.Text.StartsWith("String number ", StringComparison.Ordinal)));
            Assert.True(ms < 5000, "掃描 30 MB 耗時 " + ms + " ms");
            Assert.Equal(strings.Count, strings.Select(s => s.Address).Distinct().Count());

            // 數量上限：全是短字串時在上限停止
            var dense = new byte[4 * 1024 * 1024];
            for (int i = 0; i < dense.Length; i++) dense[i] = (byte)(i % 8 == 7 ? 0 : 0x41 + (i % 8));
            var many = StringScanner.Scan(BinaryLoader.LoadRaw(dense, "dense.bin", 32, 0), 5, 20000);
            Assert.Equal(20000, many.Count);
        }

        // ==================================================================
        // 模糊測試
        // ==================================================================

        /// <summary>對所有測試檔做確定性的突變（位元翻轉、截斷、亂改 e_lfanew／資料目錄／區段表），只允許丟出 BinaryFormatException。</summary>
        [Test]
        public static void FuzzAllTestDataDeterministically()
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData");
            var files = Directory.GetFiles(dir).Where(f => !f.EndsWith(".com", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.True(files.Count >= 6, "測試檔數量 " + files.Count);
            var rng = new Random(20261014);
            int total = 0, loaded = 0;
            var sw = Stopwatch.StartNew();
            foreach (var file in files)
            {
                var original = File.ReadAllBytes(file);
                for (int round = 0; round < 300; round++)
                {
                    var data = Mutate(original, rng, round);
                    total++;
                    try
                    {
                        var b = BinaryLoader.Load(data, Path.GetFileName(file));
                        Exercise(b, rng);
                        loaded++;
                    }
                    catch (BinaryFormatException ex)
                    {
                        Assert.DoesNotContain("內部錯誤", ex.Message, "解析器不應依賴最後防線：" + Path.GetFileName(file) + " 第 " + round + " 輪");
                    }
                }
            }
            Assert.True(total >= 2000, "突變數 " + total);
            Assert.True(loaded > total / 4, "大部分突變仍應可載入：" + loaded + "/" + total);
            Assert.True(sw.ElapsedMilliseconds < 15000, "耗時 " + sw.ElapsedMilliseconds + " ms");
        }

        private static byte[] Mutate(byte[] original, Random rng, int round)
        {
            var data = (byte[])original.Clone();
            uint lfanew = data.Length >= 0x40 ? BitConverter.ToUInt32(data, 0x3C) : 0;
            bool isPe = lfanew > 0 && (long)lfanew + 0x60 < data.Length && data[lfanew] == 'P' && data[lfanew + 1] == 'E';
            int optOffset = (int)lfanew + 24;
            switch (round % 8)
            {
                case 0: // 截斷
                    Array.Resize(ref data, rng.Next(0, data.Length));
                    break;
                case 1: // 檔頭附近的位元翻轉
                    for (int k = 0; k < 1 + rng.Next(16); k++)
                    {
                        int at = rng.Next(Math.Min(data.Length, 1024));
                        data[at] ^= (byte)(1 << rng.Next(8));
                    }
                    break;
                case 2: // 整個檔案的位元翻轉
                    for (int k = 0; k < 1 + rng.Next(64); k++)
                    {
                        int at = rng.Next(data.Length);
                        data[at] ^= (byte)(1 << rng.Next(8));
                    }
                    break;
                case 3: // 亂改 e_lfanew
                    if (data.Length >= 0x40) PeBuilder.Put32(data, 0x3C, rng.Next(4) == 0 ? (uint)rng.Next() * 2u : (uint)rng.Next(data.Length + 8));
                    break;
                case 4: // 亂改資料目錄
                    if (isPe)
                    {
                        bool pe64 = BitConverter.ToUInt16(data, optOffset) == 0x20B;
                        int dirs = optOffset + (pe64 ? 112 : 96);
                        for (int k = 0; k < 1 + rng.Next(4); k++)
                        {
                            int at = dirs + rng.Next(16) * 8 + (rng.Next(2) * 4);
                            if (at + 4 <= data.Length) PeBuilder.Put32(data, at, RandomValue(rng, data.Length));
                        }
                    }
                    break;
                case 5: // 亂改區段表欄位
                    if (isPe)
                    {
                        int sections = optOffset + BitConverter.ToUInt16(data, (int)lfanew + 20);
                        int count = BitConverter.ToUInt16(data, (int)lfanew + 6);
                        for (int k = 0; k < 1 + rng.Next(4) && count > 0; k++)
                        {
                            int at = sections + rng.Next(count) * 40 + 8 + rng.Next(4) * 4;
                            if (at + 4 <= data.Length && at >= 0) PeBuilder.Put32(data, at, RandomValue(rng, data.Length));
                        }
                    }
                    break;
                case 6: // 亂改 COFF／選用標頭的數值欄位
                    if (isPe)
                    {
                        int[] fields = { 6, 20, 24 + 16, 24 + 56, 24 + 60, 24 + 92, 24 + 108 };
                        int at = (int)lfanew + fields[rng.Next(fields.Length)];
                        if (at + 4 <= data.Length) PeBuilder.Put32(data, at, RandomValue(rng, data.Length));
                    }
                    break;
                default: // 區塊覆寫
                    {
                        int at = rng.Next(data.Length);
                        int len = Math.Min(data.Length - at, rng.Next(1, 64));
                        byte fill = (byte)(rng.Next(3) == 0 ? 0xFF : rng.Next(256));
                        for (int k = 0; k < len; k++) data[at + k] = fill;
                    }
                    break;
            }
            return data;
        }

        private static uint RandomValue(Random rng, int fileLength)
        {
            switch (rng.Next(6))
            {
                case 0: return 0;
                case 1: return 0xFFFFFFFF;
                case 2: return (uint)rng.Next(fileLength + 1);
                case 3: return (uint)rng.Next(0x10000);
                case 4: return 0x80000000u | (uint)rng.Next();
                default: return (uint)rng.Next() ^ ((uint)rng.Next() << 1);
            }
        }

        /// <summary>呼叫 LoadedBinary 的各種公開成員，確認不會丟例外。</summary>
        private static void Exercise(LoadedBinary b, Random rng)
        {
            b.Describe();
            var strings = b.Strings;
            if (strings.Count > 0) b.StringAt(strings[0].Address);
            foreach (var s in b.Sections.Take(64))
            {
                b.ReadVa(s.VirtualAddress, 64);
                b.ReadVa(s.VirtualAddress + s.MemorySize - 1, 64);
                b.IsExecutable(s.VirtualAddress);
                s.ToString();
            }
            if (b.HasEntryPoint) b.ReadVa(b.EntryPoint, 16);
            foreach (var i in b.Imports.Take(64))
            {
                b.ImportAt(i.IatAddress);
                i.ToString();
            }
            foreach (var e in b.Exports.Take(64))
            {
                b.ExportAt(e.Address);
                e.ToString();
            }
            foreach (var t in b.TlsCallbacks) b.ReadVa(t, 16);
            for (int k = 0; k < 8; k++)
            {
                uint rva = (uint)rng.Next() ^ ((uint)rng.Next() << 1);
                int offset, available;
                b.TryRvaToOffset(rva, out offset);
                if (b.TryMapVa(b.ImageBase + rva, out offset, out available))
                    Assert.True(offset >= 0 && available > 0 && (long)offset + available <= b.Data.Length, "TryMapVa 範圍超出檔案");
                b.ReadCString(b.ImageBase + rva);
                b.ReadWideString(b.ImageBase + rva);
            }
        }
    }
}
