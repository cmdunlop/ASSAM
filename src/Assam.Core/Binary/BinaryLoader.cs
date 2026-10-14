using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Assam.Core.Binary
{
    /// <summary>
    /// 已載入（只讀取、絕不執行）的執行檔：PE32/PE32+（Windows EXE/DLL，含 .NET 組件）、DOS MZ、DOS COM、原始機器碼。
    /// 提供虛擬位址與檔案位置的換算、匯入／匯出表、字串等資訊，給反組譯器使用。
    /// </summary>
    public sealed class LoadedBinary
    {
        private List<FoundString> _strings;
        private Dictionary<ulong, PeImport> _importsByIat;
        private Dictionary<ulong, PeExport> _exportsByAddress;

        internal LoadedBinary()
        {
            Sections = new List<PeSection>();
            Imports = new List<PeImport>();
            Exports = new List<PeExport>();
            Warnings = new List<string>();
            Data = new byte[0];
        }

        public string FileName { get; internal set; }
        public byte[] Data { get; internal set; }
        public BinaryKind Kind { get; internal set; }

        /// <summary>反組譯用的位元模式：16、32 或 64；0 表示機器碼不是 x86（例如 ARM 的 PE），不能用 x86 反組譯。</summary>
        public int Bits { get; internal set; }

        public ushort Machine { get; internal set; }
        public string MachineName { get; internal set; }
        public ulong ImageBase { get; internal set; }
        /// <summary>程式進入點的虛擬位址；沒有進入點時為 0（例如純資源 DLL）。</summary>
        public ulong EntryPoint { get; internal set; }
        public bool HasEntryPoint { get; internal set; }
        public bool IsDll { get; internal set; }
        /// <summary>是否為 .NET 組件（有 CLR 標頭，內容是 IL 中間語言）。</summary>
        public bool IsDotNet { get; internal set; }
        public uint ClrHeaderRva { get; internal set; }
        public uint ClrHeaderSize { get; internal set; }
        public uint SizeOfHeaders { get; internal set; }
        public uint SizeOfImage { get; internal set; }
        public ushort Subsystem { get; internal set; }
        public string SubsystemName { get; internal set; }
        public ushort Characteristics { get; internal set; }
        public ushort DllCharacteristics { get; internal set; }
        public DateTime? TimeStamp { get; internal set; }
        public string LinkerVersion { get; internal set; }

        // DOS MZ 資訊
        public ushort DosInitialCs { get; internal set; }
        public ushort DosInitialIp { get; internal set; }
        public ushort DosInitialSs { get; internal set; }
        public ushort DosInitialSp { get; internal set; }
        public int DosHeaderSize { get; internal set; }
        public int DosRelocationCount { get; internal set; }

        public List<PeSection> Sections { get; private set; }
        public List<PeImport> Imports { get; private set; }
        public List<PeExport> Exports { get; private set; }
        public List<string> Warnings { get; private set; }

        public bool IsPe
        {
            get { return Kind == BinaryKind.Pe32 || Kind == BinaryKind.Pe64; }
        }

        public string KindName
        {
            get
            {
                switch (Kind)
                {
                    case BinaryKind.Pe32: return IsDotNet ? ".NET 組件（PE32）" : "Windows PE32（32 位元）";
                    case BinaryKind.Pe64: return IsDotNet ? ".NET 組件（PE32+）" : "Windows PE32+（64 位元）";
                    case BinaryKind.DosMz: return "DOS MZ 執行檔（16 位元）";
                    case BinaryKind.DosCom: return "DOS COM 檔（16 位元）";
                    default: return "原始機器碼";
                }
            }
        }

        /// <summary>可執行的區段（反組譯的範圍）。</summary>
        public IEnumerable<PeSection> CodeSections
        {
            get { return Sections.Where(s => s.IsExecutable); }
        }

        // ------------------------------------------------------------------
        // 位址換算
        // ------------------------------------------------------------------

        /// <summary>RVA → 檔案位置。檔頭區域也可以換算。</summary>
        public bool TryRvaToOffset(uint rva, out int offset)
        {
            offset = -1;
            if (!IsPe)
            {
                ulong va = ImageBase + rva;
                int available;
                return TryMapVa(va, out offset, out available);
            }
            if (rva < SizeOfHeaders && rva < Data.Length)
            {
                offset = (int)rva;
                return true;
            }
            foreach (var s in Sections)
            {
                if (rva >= s.Rva && (ulong)rva - s.Rva < s.RawSize)
                {
                    ulong o = (ulong)s.RawOffset + (rva - s.Rva);
                    if (o < (ulong)Data.Length)
                    {
                        offset = (int)o;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>虛擬位址 → 檔案位置與從該處起還有幾個位元組（同一區段內）。</summary>
        public bool TryMapVa(ulong va, out int fileOffset, out int available)
        {
            fileOffset = -1;
            available = 0;
            foreach (var s in Sections)
            {
                if (va < s.VirtualAddress) continue;
                ulong delta = va - s.VirtualAddress;
                if (delta >= s.RawSize) continue;
                ulong o = s.RawOffset + delta;
                if (o >= (ulong)Data.Length) continue;
                fileOffset = (int)o;
                ulong end = Math.Min((ulong)Data.Length, (ulong)s.RawOffset + s.RawSize);
                available = (int)Math.Min(int.MaxValue, end - o);
                return available > 0;
            }
            if (IsPe && va >= ImageBase && va - ImageBase < SizeOfHeaders && va - ImageBase < (ulong)Data.Length)
            {
                fileOffset = (int)(va - ImageBase);
                available = (int)(Math.Min(SizeOfHeaders, (uint)Data.Length) - (uint)fileOffset);
                return available > 0;
            }
            return false;
        }

        /// <summary>讀取從 va 開始最多 count 個位元組（遇到區段結尾會變短；位址無效時回傳空陣列，不會丟例外）。</summary>
        public byte[] ReadVa(ulong va, int count)
        {
            int offset, available;
            if (count <= 0 || !TryMapVa(va, out offset, out available)) return new byte[0];
            int n = Math.Min(count, available);
            var buffer = new byte[n];
            Buffer.BlockCopy(Data, offset, buffer, 0, n);
            return buffer;
        }

        public PeSection SectionAt(ulong va)
        {
            return Sections.FirstOrDefault(s => s.Contains(va));
        }

        public bool IsExecutable(ulong va)
        {
            var s = SectionAt(va);
            return s != null && s.IsExecutable;
        }

        /// <summary>若 va 是某個匯入函式的 IAT 槽位，回傳該匯入；否則 null。</summary>
        public PeImport ImportAt(ulong iatAddress)
        {
            if (_importsByIat == null)
            {
                _importsByIat = new Dictionary<ulong, PeImport>();
                foreach (var i in Imports)
                    if (!_importsByIat.ContainsKey(i.IatAddress)) _importsByIat[i.IatAddress] = i;
            }
            PeImport import;
            return _importsByIat.TryGetValue(iatAddress, out import) ? import : null;
        }

        /// <summary>若 va 是匯出函式的位址，回傳該匯出；否則 null。</summary>
        public PeExport ExportAt(ulong address)
        {
            if (_exportsByAddress == null)
            {
                _exportsByAddress = new Dictionary<ulong, PeExport>();
                foreach (var e in Exports.Where(x => x.ForwardTo == null))
                    if (!_exportsByAddress.ContainsKey(e.Address)) _exportsByAddress[e.Address] = e;
            }
            PeExport export;
            return _exportsByAddress.TryGetValue(address, out export) ? export : null;
        }

        /// <summary>讀取以 0 結尾、全部是可列印字元的 ASCII 字串；不是字串時回傳 null。</summary>
        public string ReadCString(ulong va, int max = 256, int minLength = 1)
        {
            var bytes = ReadVa(va, max + 1);
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                if (b == 0) return sb.Length >= minLength ? sb.ToString() : null;
                if (!IsPrintable(b)) return null;
                sb.Append((char)b);
            }
            return null;
        }

        /// <summary>讀取以 0 結尾的 UTF-16LE 字串；不是字串時回傳 null。</summary>
        public string ReadWideString(ulong va, int maxChars = 256, int minLength = 1)
        {
            var bytes = ReadVa(va, maxChars * 2 + 2);
            var sb = new StringBuilder();
            for (int i = 0; i + 1 < bytes.Length; i += 2)
            {
                char c = (char)(bytes[i] | (bytes[i + 1] << 8));
                if (c == 0) return sb.Length >= minLength ? sb.ToString() : null;
                if (c < 0x20 && c != '\t' && c != '\r' && c != '\n') return null;
                if (char.IsSurrogate(c) || c == 0xFFFF) return null;
                sb.Append(c);
            }
            return null;
        }

        internal static bool IsPrintable(byte b)
        {
            return (b >= 0x20 && b < 0x7F) || b == 9 || b == 10 || b == 13;
        }

        /// <summary>掃描非執行區段（與 COM/MZ 的整個映像）中的字串（第一次呼叫時才掃描）。</summary>
        public List<FoundString> Strings
        {
            get
            {
                if (_strings == null) _strings = StringScanner.Scan(this, 5, 20000);
                return _strings;
            }
        }

        // ------------------------------------------------------------------
        // 說明
        // ------------------------------------------------------------------

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("檔案：").Append(FileName).Append("（").Append(Data.Length.ToString("N0", CultureInfo.InvariantCulture)).Append(" 位元組）\r\n");
            sb.Append("格式：").Append(KindName);
            if (IsPe) sb.Append(IsDll ? "，DLL（動態連結函式庫）" : "，EXE（執行檔）");
            sb.Append("\r\n");
            if (IsPe)
            {
                sb.Append("CPU：").Append(MachineName).Append("（Machine = 0x").Append(Machine.ToString("X4", CultureInfo.InvariantCulture)).Append("）\r\n");
                if (SubsystemName != null) sb.Append("子系統：").Append(SubsystemName).Append("\r\n");
                if (TimeStamp.HasValue) sb.Append("連結時間（檔頭記錄）：").Append(TimeStamp.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC\r\n");
                if (LinkerVersion != null) sb.Append("連結器版本：").Append(LinkerVersion).Append("\r\n");
                sb.Append("ImageBase（預設載入位址）：0x").Append(ImageBase.ToString("X", CultureInfo.InvariantCulture)).Append("\r\n");
                sb.Append("進入點：").Append(HasEntryPoint ? "0x" + EntryPoint.ToString("X", CultureInfo.InvariantCulture) + "（RVA 0x" + (EntryPoint - ImageBase).ToString("X", CultureInfo.InvariantCulture) + "）" : "無").Append("\r\n");
                if ((DllCharacteristics & 0x0040) != 0) sb.Append("ASLR：開啟（實際載入位址可能和 ImageBase 不同）\r\n");
                if ((DllCharacteristics & 0x0100) != 0) sb.Append("DEP/NX：相容\r\n");
                sb.Append("區段：").Append(Sections.Count).Append(" 個；匯入函式：").Append(Imports.Count).Append(" 個（來自 ")
                  .Append(Imports.Select(i => i.Dll).Distinct(StringComparer.OrdinalIgnoreCase).Count()).Append(" 個 DLL）；匯出函式：").Append(Exports.Count).Append(" 個\r\n");
                if (IsDotNet)
                    sb.Append(".NET：這是 .NET 組件，程式主體是 IL 中間語言（執行時才由 JIT 轉成機器碼），請看「.NET 方法」的 IL 反組譯。\r\n");
            }
            else if (Kind == BinaryKind.DosMz)
            {
                sb.Append("DOS 檔頭大小：").Append(DosHeaderSize).Append(" 位元組；重定位項目：").Append(DosRelocationCount).Append(" 個\r\n");
                sb.Append("初始 CS:IP = ").Append(DosInitialCs.ToString("X4", CultureInfo.InvariantCulture)).Append(":").Append(DosInitialIp.ToString("X4", CultureInfo.InvariantCulture))
                  .Append("，SS:SP = ").Append(DosInitialSs.ToString("X4", CultureInfo.InvariantCulture)).Append(":").Append(DosInitialSp.ToString("X4", CultureInfo.InvariantCulture)).Append("\r\n");
                sb.Append("位址以「載入模組開頭 = 0」的線性位址表示；進入點 = CS×16 + IP = 0x").Append(EntryPoint.ToString("X", CultureInfo.InvariantCulture)).Append("\r\n");
            }
            else if (Kind == BinaryKind.DosCom)
            {
                sb.Append("COM 檔沒有檔頭，整個檔案載入到 CS:0100h 後從第一個位元組開始執行。\r\n");
            }
            if (Bits > 0) sb.Append("反組譯模式：").Append(Bits).Append(" 位元 x86\r\n");
            else sb.Append("反組譯模式：不支援（機器碼不是 x86），只顯示檔頭資訊。\r\n");
            foreach (var w in Warnings) sb.Append("▲ ").Append(w).Append("\r\n");
            return sb.ToString();
        }
    }

    /// <summary>辨識並解析執行檔。只讀取位元組，不會執行或載入任何程式碼。</summary>
    public static class BinaryLoader
    {
        /// <summary>可接受的最大檔案（避免低階電腦記憶體不足）。</summary>
        public const int MaxFileSize = 256 * 1024 * 1024;

        public static LoadedBinary Load(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new BinaryFormatException("找不到檔案：" + path);
            if (info.Length > MaxFileSize) throw new BinaryFormatException("檔案太大（超過 256 MB），無法載入。");
            return Load(File.ReadAllBytes(path), info.Name);
        }

        public static LoadedBinary Load(byte[] data, string fileName)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (data.Length > MaxFileSize) throw new BinaryFormatException("檔案太大（超過 256 MB），無法載入。");
            string name = fileName ?? "(未命名)";
            if (data.Length >= 2 && data[0] == 'M' && data[1] == 'Z') return LoadMz(data, name);
            if (data.Length >= 4 && data[0] == 0x7F && data[1] == 'E' && data[2] == 'L' && data[3] == 'F')
                throw new BinaryFormatException("這是 Linux 的 ELF 執行檔。目前支援 Windows EXE/DLL（PE）、DOS EXE（MZ）與 COM 檔。");
            if (name.EndsWith(".com", StringComparison.OrdinalIgnoreCase)) return LoadCom(data, name);
            throw new BinaryFormatException("不是可辨識的執行檔（開頭不是 \"MZ\"）。若是沒有檔頭的機器碼，請用「原始機器碼」方式載入。");
        }

        /// <summary>沒有檔頭的機器碼（例如開機磁區、自己組譯的 .bin）。</summary>
        public static LoadedBinary LoadRaw(byte[] data, string fileName, int bits, ulong baseAddress)
        {
            if (bits != 16 && bits != 32 && bits != 64) throw new ArgumentException("bits 必須是 16、32 或 64");
            var b = new LoadedBinary { Data = data, FileName = fileName ?? "(原始機器碼)", Kind = BinaryKind.Raw, Bits = bits, ImageBase = baseAddress };
            b.Sections.Add(new PeSection
            {
                Name = "raw", VirtualAddress = baseAddress, Rva = 0, VirtualSize = (uint)data.Length, RawOffset = 0,
                RawSize = (uint)data.Length, Characteristics = 0x60000020u
            });
            b.EntryPoint = baseAddress;
            b.HasEntryPoint = data.Length > 0;
            b.MachineName = "x86（" + bits + " 位元）";
            return b;
        }

        private static LoadedBinary LoadCom(byte[] data, string name)
        {
            if (data.Length > 0xFF00) throw new BinaryFormatException("COM 檔最大只能 65280 位元組，這個檔案太大。");
            var b = LoadRaw(data, name, 16, 0x100);
            b.Kind = BinaryKind.DosCom;
            b.Sections[0].Name = "com";
            b.MachineName = "Intel 8086（16 位元）";
            return b;
        }

        // ------------------------------------------------------------------
        // DOS MZ / PE
        // ------------------------------------------------------------------

        private static ushort U16(byte[] d, long o)
        {
            if (o < 0 || o + 2 > d.Length) throw new BinaryFormatException("檔頭被截斷（位置 0x" + o.ToString("X", CultureInfo.InvariantCulture) + "）。");
            return (ushort)(d[o] | (d[o + 1] << 8));
        }

        private static uint U32(byte[] d, long o)
        {
            if (o < 0 || o + 4 > d.Length) throw new BinaryFormatException("檔頭被截斷（位置 0x" + o.ToString("X", CultureInfo.InvariantCulture) + "）。");
            return (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
        }

        private static ulong U64(byte[] d, long o)
        {
            return U32(d, o) | ((ulong)U32(d, o + 4) << 32);
        }

        private static LoadedBinary LoadMz(byte[] data, string name)
        {
            if (data.Length < 0x1C) throw new BinaryFormatException("DOS 檔頭不完整（檔案太小）。");
            if (data.Length >= 0x40)
            {
                uint lfanew = U32(data, 0x3C);
                if (lfanew >= 0x40 && lfanew <= (uint)data.Length - 4)
                {
                    if (data[lfanew] == 'P' && data[lfanew + 1] == 'E' && data[lfanew + 2] == 0 && data[lfanew + 3] == 0)
                        return LoadPe(data, name, lfanew);
                    if ((data[lfanew] == 'N' && data[lfanew + 1] == 'E') || (data[lfanew] == 'L' && (data[lfanew + 1] == 'E' || data[lfanew + 1] == 'X')))
                    {
                        var dos = LoadDos(data, name);
                        dos.Warnings.Add("這是 16 位元 Windows／OS/2 的 " + (char)data[lfanew] + (char)data[lfanew + 1] +
                                         " 格式；只反組譯其中的 DOS 部分（通常是顯示「需要 Windows」的小程式）。");
                        return dos;
                    }
                }
            }
            return LoadDos(data, name);
        }

        private static LoadedBinary LoadDos(byte[] data, string name)
        {
            ushort lastPage = U16(data, 2);
            ushort pages = U16(data, 4);
            ushort relocations = U16(data, 6);
            ushort headerParagraphs = U16(data, 8);
            ushort ss = U16(data, 14);
            ushort sp = U16(data, 16);
            ushort ip = U16(data, 20);
            ushort cs = U16(data, 22);

            int headerSize = headerParagraphs * 16;
            long imageEnd = pages == 0 ? data.Length : (long)pages * 512 - (lastPage != 0 ? 512 - lastPage : 0);
            var b = new LoadedBinary
            {
                Data = data,
                FileName = name,
                Kind = BinaryKind.DosMz,
                Bits = 16,
                MachineName = "Intel 8086（16 位元）",
                DosInitialCs = cs,
                DosInitialIp = ip,
                DosInitialSs = ss,
                DosInitialSp = sp,
                DosHeaderSize = headerSize,
                DosRelocationCount = relocations
            };
            if (imageEnd > data.Length)
            {
                b.Warnings.Add("檔頭記錄的大小（" + imageEnd + "）比實際檔案大，已以實際大小為準。");
                imageEnd = data.Length;
            }
            if (headerSize > imageEnd)
            {
                b.Warnings.Add("DOS 檔頭大小不合理，沒有可反組譯的程式碼。");
                headerSize = (int)Math.Min(imageEnd, data.Length);
            }
            uint size = (uint)Math.Max(0, imageEnd - headerSize);
            b.Sections.Add(new PeSection
            {
                Name = "load module", VirtualAddress = 0, Rva = 0, VirtualSize = size, RawOffset = (uint)headerSize, RawSize = size,
                Characteristics = 0xE0000020u
            });
            b.EntryPoint = (ulong)((cs * 16 + ip) & 0xFFFFF);
            b.HasEntryPoint = b.EntryPoint < size;
            if (!b.HasEntryPoint) b.Warnings.Add("進入點 CS:IP 指到載入模組範圍外。");
            return b;
        }

        private static string MachineText(ushort machine, out int bits)
        {
            bits = 0;
            switch (machine)
            {
                case 0x014C: bits = 32; return "Intel 386（x86，32 位元）";
                case 0x8664: bits = 64; return "AMD64（x64，64 位元）";
                case 0x01C0: return "ARM（32 位元）";
                case 0x01C4: return "ARM Thumb-2（ARMv7）";
                case 0xAA64: return "ARM64（AArch64）";
                case 0x0200: return "Intel Itanium（IA-64）";
                case 0x0000: return "未指定（Unknown）";
                default: return "未知（0x" + machine.ToString("X4", CultureInfo.InvariantCulture) + "）";
            }
        }

        private static string SubsystemText(ushort subsystem)
        {
            switch (subsystem)
            {
                case 1: return "Native（驅動程式／系統）";
                case 2: return "Windows GUI（視窗程式）";
                case 3: return "Windows 主控台（Console）";
                case 7: return "POSIX 主控台";
                case 9: return "Windows CE GUI";
                case 10: return "EFI 應用程式";
                case 11: return "EFI 開機服務驅動";
                case 12: return "EFI 執行階段驅動";
                case 14: return "Xbox";
                case 16: return "Windows 開機應用程式";
                default: return "其他（" + subsystem + "）";
            }
        }

        private static LoadedBinary LoadPe(byte[] data, string name, uint peOffset)
        {
            long coff = peOffset + 4;
            ushort machine = U16(data, coff);
            ushort sectionCount = U16(data, coff + 2);
            uint timeStamp = U32(data, coff + 4);
            ushort optionalSize = U16(data, coff + 16);
            ushort characteristics = U16(data, coff + 18);
            long opt = coff + 20;
            ushort magic = U16(data, opt);
            bool pe64;
            if (magic == 0x10B) pe64 = false;
            else if (magic == 0x20B) pe64 = true;
            else throw new BinaryFormatException("PE 選用標頭的格式碼 0x" + magic.ToString("X", CultureInfo.InvariantCulture) + " 不正確（應為 0x10B 或 0x20B）。");

            int bits;
            var b = new LoadedBinary
            {
                Data = data,
                FileName = name,
                Kind = pe64 ? BinaryKind.Pe64 : BinaryKind.Pe32,
                Machine = machine,
                MachineName = MachineText(machine, out bits),
                Characteristics = characteristics,
                IsDll = (characteristics & 0x2000) != 0,
                LinkerVersion = data[opt + 2].ToString(CultureInfo.InvariantCulture) + "." + data[opt + 3].ToString(CultureInfo.InvariantCulture)
            };
            if (bits == 32 && pe64) { bits = 64; b.Warnings.Add("Machine 是 i386 但檔頭是 PE32+，以 64 位元反組譯。"); }
            if (bits == 64 && !pe64) { bits = 32; b.Warnings.Add("Machine 是 AMD64 但檔頭是 PE32，以 32 位元反組譯。"); }
            b.Bits = bits;
            if (bits == 0) b.Warnings.Add("CPU 架構是 " + b.MachineName + "，本程式只能反組譯 x86/x64 機器碼，以下只顯示檔頭、區段、匯入與匯出。");
            if (timeStamp != 0 && timeStamp != 0xFFFFFFFF)
                b.TimeStamp = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(timeStamp);

            uint entryRva = U32(data, opt + 16);
            b.ImageBase = pe64 ? U64(data, opt + 24) : U32(data, opt + 28);
            b.SizeOfImage = U32(data, opt + 56);
            b.SizeOfHeaders = U32(data, opt + 60);
            b.Subsystem = U16(data, opt + 68);
            b.SubsystemName = SubsystemText(b.Subsystem);
            b.DllCharacteristics = U16(data, opt + 70);
            long dirCountOffset = opt + (pe64 ? 108 : 92);
            uint dirCount = optionalSize >= (pe64 ? 112 : 96) ? U32(data, dirCountOffset) : 0;
            long dirBase = dirCountOffset + 4;
            dirCount = Math.Min(dirCount, 16u);
            var dirRva = new uint[16];
            var dirSize = new uint[16];
            for (int i = 0; i < dirCount; i++)
            {
                if (dirBase + i * 8 + 8 > opt + optionalSize) break;
                dirRva[i] = U32(data, dirBase + i * 8);
                dirSize[i] = U32(data, dirBase + i * 8 + 4);
            }

            // 區段表
            long sectionTable = opt + optionalSize;
            if (sectionCount > 96) b.Warnings.Add("區段數量 " + sectionCount + " 異常，只讀取前 96 個。");
            for (int i = 0; i < Math.Min((int)sectionCount, 96); i++)
            {
                long sh = sectionTable + i * 40;
                if (sh + 40 > data.Length) { b.Warnings.Add("區段表被截斷。"); break; }
                var nameBytes = new byte[8];
                Buffer.BlockCopy(data, (int)sh, nameBytes, 0, 8);
                string secName = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                uint vsize = U32(data, sh + 8);
                uint va = U32(data, sh + 12);
                uint rawSize = U32(data, sh + 16);
                uint rawPtr = U32(data, sh + 20);
                uint ch = U32(data, sh + 36);
                if (rawPtr >= data.Length) { rawSize = 0; }
                else if ((ulong)rawPtr + rawSize > (ulong)data.Length)
                {
                    b.Warnings.Add("區段 " + secName + " 的資料超出檔案結尾，已截短。");
                    rawSize = (uint)(data.Length - rawPtr);
                }
                // 檔案對齊造成的尾端填充不屬於區段內容（反組譯時不應把填充的 0 當成指令）
                if (vsize != 0 && rawSize > vsize) rawSize = vsize;
                b.Sections.Add(new PeSection
                {
                    Name = secName.Length == 0 ? "(無名)" : secName,
                    Rva = va,
                    VirtualAddress = b.ImageBase + va,
                    VirtualSize = vsize,
                    RawOffset = rawPtr,
                    RawSize = rawSize,
                    Characteristics = ch
                });
            }

            b.HasEntryPoint = entryRva != 0;
            b.EntryPoint = b.ImageBase + entryRva;
            if (b.HasEntryPoint && b.SectionAt(b.EntryPoint) == null) b.Warnings.Add("進入點不在任何區段內（可能是加殼或損毀的檔案）。");

            // CLR（.NET）
            if (dirRva[14] != 0 && dirSize[14] >= 0x48)
            {
                b.IsDotNet = true;
                b.ClrHeaderRva = dirRva[14];
                b.ClrHeaderSize = dirSize[14];
            }

            try
            {
                ReadImports(b, dirRva[1], dirSize[1], pe64);
            }
            catch (BinaryFormatException ex)
            {
                b.Warnings.Add("匯入表讀取失敗：" + ex.Message);
            }
            try
            {
                ReadDelayImports(b, dirRva[13], dirSize[13], pe64);
            }
            catch (BinaryFormatException ex)
            {
                b.Warnings.Add("延遲載入匯入表讀取失敗：" + ex.Message);
            }
            try
            {
                ReadExports(b, dirRva[0], dirSize[0]);
            }
            catch (BinaryFormatException ex)
            {
                b.Warnings.Add("匯出表讀取失敗：" + ex.Message);
            }
            return b;
        }

        private static string ReadAsciiAtRva(LoadedBinary b, uint rva, int max)
        {
            int offset;
            if (!b.TryRvaToOffset(rva, out offset)) return null;
            var sb = new StringBuilder();
            for (int i = offset; i < b.Data.Length && sb.Length < max; i++)
            {
                byte c = b.Data[i];
                if (c == 0) return sb.ToString();
                sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '?');
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static void ReadImports(LoadedBinary b, uint rva, uint size, bool pe64)
        {
            if (rva == 0) return;
            int ptr = pe64 ? 8 : 4;
            int total = 0;
            for (int d = 0; d < 4096; d++)
            {
                int desc;
                if (!b.TryRvaToOffset(rva + (uint)(d * 20), out desc) || desc + 20 > b.Data.Length) break;
                uint ilt = U32(b.Data, desc);
                uint nameRva = U32(b.Data, desc + 12);
                uint iat = U32(b.Data, desc + 16);
                if (ilt == 0 && nameRva == 0 && iat == 0) break;
                string dll = ReadAsciiAtRva(b, nameRva, 256) ?? "(未知 DLL)";
                uint thunkTable = ilt != 0 ? ilt : iat;
                for (int i = 0; i < 65536 && total < 200000; i++)
                {
                    int t;
                    if (!b.TryRvaToOffset(thunkTable + (uint)(i * ptr), out t) || t + ptr > b.Data.Length) break;
                    ulong value = pe64 ? U64(b.Data, t) : U32(b.Data, t);
                    if (value == 0) break;
                    var import = new PeImport { Dll = dll, IatAddress = b.ImageBase + iat + (ulong)(i * ptr) };
                    bool byOrdinal = pe64 ? (value & 0x8000000000000000UL) != 0 : (value & 0x80000000UL) != 0;
                    if (byOrdinal) import.Ordinal = (int)(value & 0xFFFF);
                    else
                    {
                        uint hintName = (uint)(value & 0x7FFFFFFF);
                        int h;
                        if (b.TryRvaToOffset(hintName, out h) && h + 2 <= b.Data.Length) import.Ordinal = U16(b.Data, h);
                        import.Name = ReadAsciiAtRva(b, hintName + 2, 512) ?? ("(RVA 0x" + hintName.ToString("X", CultureInfo.InvariantCulture) + ")");
                    }
                    b.Imports.Add(import);
                    total++;
                }
            }
        }

        private static void ReadDelayImports(LoadedBinary b, uint rva, uint size, bool pe64)
        {
            if (rva == 0) return;
            int ptr = pe64 ? 8 : 4;
            for (int d = 0; d < 1024; d++)
            {
                int desc;
                if (!b.TryRvaToOffset(rva + (uint)(d * 32), out desc) || desc + 32 > b.Data.Length) break;
                uint attributes = U32(b.Data, desc);
                uint nameRva = U32(b.Data, desc + 4);
                uint iat = U32(b.Data, desc + 12);
                uint names = U32(b.Data, desc + 16);
                if (nameRva == 0 && iat == 0) break;
                bool rvaBased = (attributes & 1) != 0;
                Func<uint, uint> toRva = v => rvaBased ? v : (uint)(v - (uint)b.ImageBase);
                string dll = ReadAsciiAtRva(b, toRva(nameRva), 256) ?? "(未知 DLL)";
                for (int i = 0; i < 65536; i++)
                {
                    int t;
                    if (!b.TryRvaToOffset(toRva(names) + (uint)(i * ptr), out t) || t + ptr > b.Data.Length) break;
                    ulong value = pe64 ? U64(b.Data, t) : U32(b.Data, t);
                    if (value == 0) break;
                    var import = new PeImport { Dll = dll, IsDelayLoad = true, IatAddress = b.ImageBase + toRva(iat) + (ulong)(i * ptr) };
                    bool byOrdinal = pe64 ? (value & 0x8000000000000000UL) != 0 : (value & 0x80000000UL) != 0;
                    if (byOrdinal) import.Ordinal = (int)(value & 0xFFFF);
                    else import.Name = ReadAsciiAtRva(b, toRva((uint)value) + 2, 512);
                    b.Imports.Add(import);
                }
            }
        }

        private static void ReadExports(LoadedBinary b, uint rva, uint size)
        {
            if (rva == 0) return;
            int dir;
            if (!b.TryRvaToOffset(rva, out dir) || dir + 40 > b.Data.Length) return;
            uint ordinalBase = U32(b.Data, dir + 16);
            uint functionCount = Math.Min(U32(b.Data, dir + 20), 65536u);
            uint nameCount = Math.Min(U32(b.Data, dir + 24), 65536u);
            uint functions = U32(b.Data, dir + 28);
            uint names = U32(b.Data, dir + 32);
            uint ordinals = U32(b.Data, dir + 36);

            var nameByIndex = new Dictionary<uint, string>();
            for (uint i = 0; i < nameCount; i++)
            {
                int n, o;
                if (!b.TryRvaToOffset(names + i * 4, out n) || n + 4 > b.Data.Length) break;
                if (!b.TryRvaToOffset(ordinals + i * 2, out o) || o + 2 > b.Data.Length) break;
                uint index = U16(b.Data, o);
                string name = ReadAsciiAtRva(b, U32(b.Data, n), 512);
                if (name != null && !nameByIndex.ContainsKey(index)) nameByIndex[index] = name;
            }
            for (uint i = 0; i < functionCount; i++)
            {
                int f;
                if (!b.TryRvaToOffset(functions + i * 4, out f) || f + 4 > b.Data.Length) break;
                uint fn = U32(b.Data, f);
                if (fn == 0) continue;
                string name;
                nameByIndex.TryGetValue(i, out name);
                var export = new PeExport { Name = name, Ordinal = (int)(ordinalBase + i), Rva = fn, Address = b.ImageBase + fn };
                if (fn >= rva && fn < rva + size) export.ForwardTo = ReadAsciiAtRva(b, fn, 512);
                b.Exports.Add(export);
            }
        }
    }

    /// <summary>在檔案中找出 ASCII 與 UTF-16LE 字串。</summary>
    public static class StringScanner
    {
        /// <summary>掃描上限（位元組），避免大檔案在低階電腦上跑太久。</summary>
        public const int MaxScanBytes = 32 * 1024 * 1024;

        public static List<FoundString> Scan(LoadedBinary binary, int minLength, int maxCount)
        {
            var result = new List<FoundString>();
            int scanned = 0;
            foreach (var s in binary.Sections)
            {
                // PE 只掃描非執行區段（字串通常在 .rdata/.data）；DOS/COM/原始檔只有一個區段，全部掃描
                if (binary.IsPe && s.IsExecutable) continue;
                if (s.RawSize == 0 || s.RawOffset >= binary.Data.Length) continue;
                int start = (int)s.RawOffset;
                int end = (int)Math.Min((long)binary.Data.Length, (long)s.RawOffset + s.RawSize);
                if (scanned + (end - start) > MaxScanBytes) end = start + Math.Max(0, MaxScanBytes - scanned);
                scanned += end - start;
                ScanAscii(binary, s, start, end, minLength, maxCount, result);
                ScanUtf16(binary, s, start, end, minLength, maxCount, result);
                if (result.Count >= maxCount || scanned >= MaxScanBytes) break;
            }
            result.Sort((a, b) => a.Address.CompareTo(b.Address));
            return result;
        }

        private static void ScanAscii(LoadedBinary binary, PeSection s, int start, int end, int minLength, int maxCount, List<FoundString> result)
        {
            var data = binary.Data;
            int i = start;
            while (i < end && result.Count < maxCount)
            {
                int j = i;
                while (j < end && LoadedBinary.IsPrintable(data[j]) && j - i < 1024) j++;
                if (j - i >= minLength && (j >= end || data[j] == 0 || j - i >= 1024))
                {
                    string text = Encoding.ASCII.GetString(data, i, j - i);
                    if (text.Trim().Length >= minLength)
                        result.Add(new FoundString { Address = s.VirtualAddress + (ulong)(i - (int)s.RawOffset), Text = Escape(text), Encoding = "ASCII", Section = s.Name });
                }
                i = j + 1;
            }
        }

        private static void ScanUtf16(LoadedBinary binary, PeSection s, int start, int end, int minLength, int maxCount, List<FoundString> result)
        {
            var data = binary.Data;
            int i = start + ((start & 1) != 0 ? 1 : 0);
            while (i + 1 < end && result.Count < maxCount)
            {
                int j = i;
                var sb = new StringBuilder();
                while (j + 1 < end && sb.Length < 1024)
                {
                    char c = (char)(data[j] | (data[j + 1] << 8));
                    bool ok = (c >= 0x20 && c < 0x7F) || c == '\t' || c == '\r' || c == '\n' || (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3000 && c <= 0x303F) || (c >= 0xFF00 && c <= 0xFFEF);
                    if (!ok) break;
                    sb.Append(c);
                    j += 2;
                }
                if (sb.Length >= minLength && j + 1 < end && data[j] == 0 && data[j + 1] == 0 && sb.ToString().Trim().Length >= minLength)
                {
                    result.Add(new FoundString { Address = s.VirtualAddress + (ulong)(i - (int)s.RawOffset), Text = Escape(sb.ToString()), Encoding = "UTF-16", Section = s.Name });
                    i = j + 2;
                }
                else
                {
                    i = Math.Max(j, i + 2);
                }
            }
        }

        private static string Escape(string s)
        {
            return s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }
}
