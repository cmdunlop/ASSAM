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
        private Dictionary<ulong, FoundString> _stringsByAddress;
        private Dictionary<ulong, PeImport> _importsByIat;
        private Dictionary<ulong, PeExport> _exportsByAddress;

        // 區段查詢索引：把區段表換算成「互不重疊、依位址排序」的片段後二分搜尋。
        // 區段範圍重疊（損毀或刻意構造的檔案）時，重疊處一律屬於區段表中較前面的區段。
        private Segment[] _segments;
        private int _indexedCount = -1;
        private bool _sectionsOverlap;

        /// <summary>一段連續、只屬於一個區段的虛擬位址範圍 [Start, End)。</summary>
        private struct Segment
        {
            public ulong Start;
            public ulong End;
            public PeSection Owner;
        }
        internal LoadedBinary()
        {
            Sections = new List<PeSection>();
            Imports = new List<PeImport>();
            Exports = new List<PeExport>();
            Warnings = new List<string>();
            TlsCallbacks = new List<ulong>();
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

        /// <summary>
        /// TLS 回呼函式的虛擬位址（PE 的 TLS 目錄）。Windows 會在進入點「之前」呼叫它們，
        /// 所以它們也是程式碼的起點（加殼程式、反除錯技巧常放在這裡）。
        /// </summary>
        public List<ulong> TlsCallbacks { get; private set; }

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
            get { return Sections.Where(s => s != null && s.IsExecutable); }
        }

        // ------------------------------------------------------------------
        // 位址換算
        // ------------------------------------------------------------------

        /// <summary>PE 檔頭在記憶體中可對應到檔案的大小（SizeOfHeaders，但不超過檔案大小）。</summary>
        internal uint HeaderMapSize
        {
            get { return IsPe ? (uint)Math.Min((long)SizeOfHeaders, (Data ?? new byte[0]).Length) : 0u; }
        }

        private static ulong SpanOf(PeSection s)
        {
            return Math.Max(s.MemorySize, s.RawSize);
        }

        private static ulong EndOf(PeSection s)
        {
            ulong span = SpanOf(s);
            return s.VirtualAddress > ulong.MaxValue - span ? ulong.MaxValue : s.VirtualAddress + span;
        }

        private void EnsureSectionIndex()
        {
            if (_segments != null && _indexedCount == Sections.Count) return;
            var list = Sections.Where(s => s != null && SpanOf(s) > 0).OrderBy(s => s.VirtualAddress).ToArray();
            bool overlap = false;
            for (int i = 0; i + 1 < list.Length; i++)
            {
                if (list[i + 1].VirtualAddress < EndOf(list[i]))
                {
                    overlap = true;
                    break;
                }
            }
            Segment[] segments;
            if (!overlap)
            {
                segments = new Segment[list.Length];
                for (int i = 0; i < list.Length; i++)
                    segments[i] = new Segment { Start = list[i].VirtualAddress, End = EndOf(list[i]), Owner = list[i] };
            }
            else
            {
                segments = ResolveOverlaps();
            }
            _segments = segments;
            _sectionsOverlap = overlap;
            _indexedCount = Sections.Count;
        }

        /// <summary>依區段表順序加入區段，只保留尚未被前面區段佔用的部分（每次以線性合併維持排序，O(n²) 且 n 有上限）。</summary>
        private Segment[] ResolveOverlaps()
        {
            var segs = new List<Segment>();
            var parts = new List<Segment>();
            foreach (var s in Sections)
            {
                if (s == null || SpanOf(s) == 0) continue;
                ulong end = EndOf(s), cur = s.VirtualAddress;
                parts.Clear();
                foreach (var g in segs)
                {
                    if (g.End <= cur) continue;
                    if (g.Start >= end) break;
                    if (g.Start > cur) parts.Add(new Segment { Start = cur, End = g.Start, Owner = s });
                    cur = g.End;
                    if (cur >= end) break;
                }
                if (cur < end) parts.Add(new Segment { Start = cur, End = end, Owner = s });
                if (parts.Count == 0) continue;
                var merged = new List<Segment>(segs.Count + parts.Count);
                int a = 0, b = 0;
                while (a < segs.Count || b < parts.Count)
                {
                    if (b >= parts.Count || (a < segs.Count && segs[a].Start < parts[b].Start)) merged.Add(segs[a++]);
                    else merged.Add(parts[b++]);
                }
                segs = merged;
            }
            return segs.ToArray();
        }

        /// <summary>載入器修改區段表後呼叫，讓查詢索引重建。</summary>
        internal void InvalidateSectionIndex()
        {
            _segments = null;
            _indexedCount = -1;
        }

        /// <summary>區段範圍是否有重疊（損毀或刻意構造的檔案）。</summary>
        internal bool SectionsOverlap
        {
            get
            {
                EnsureSectionIndex();
                return _sectionsOverlap;
            }
        }

        /// <summary>
        /// 找出 va 所在的區段（以記憶體範圍判斷，含 RawSize 之後補 0 的部分）。segmentEnd 是該區段在此處連續範圍的結尾；
        /// 找不到時 segmentEnd 是下一個區段的起點（沒有則為 ulong.MaxValue），供檔頭區域限制長度。
        /// </summary>
        private PeSection FindSection(ulong va, out ulong segmentEnd)
        {
            EnsureSectionIndex();
            var segs = _segments;
            int lo = 0, hi = segs.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (segs[mid].Start <= va)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            if (found >= 0 && va < segs[found].End)
            {
                segmentEnd = segs[found].End;
                return segs[found].Owner;
            }
            segmentEnd = found + 1 < segs.Length ? segs[found + 1].Start : ulong.MaxValue;
            return null;
        }

        /// <summary>RVA → 檔案位置。檔頭區域也可以換算；RVA 落在未初始化資料（例如 .bss）時傳回 false。</summary>
        public bool TryRvaToOffset(uint rva, out int offset)
        {
            int available;
            return TryMapVa(ImageBase + rva, out offset, out available);
        }

        /// <summary>
        /// 虛擬位址 → 檔案位置與從該處起還有幾個位元組（同一區段內）。
        /// 區段的 RawSize 到 VirtualSize 之間（例如 .bss）在執行時存在、內容是 0，但檔案中沒有對應的位元組，所以傳回 false，
        /// 絕不會對應到其他不相干的檔案位元組。
        /// </summary>
        public bool TryMapVa(ulong va, out int fileOffset, out int available)
        {
            fileOffset = -1;
            available = 0;
            var data = Data ?? new byte[0];
            ulong segmentEnd;
            var s = FindSection(va, out segmentEnd);
            if (s != null)
            {
                ulong delta = va - s.VirtualAddress;
                if (delta >= s.RawSize) return false;
                ulong o = (ulong)s.RawOffset + delta;
                ulong end = Math.Min((ulong)data.Length, (ulong)s.RawOffset + s.RawSize);
                if (o >= end) return false;
                ulong length = Math.Min(end - o, segmentEnd - va);
                fileOffset = (int)o;
                available = (int)Math.Min((ulong)int.MaxValue, length);
                return true;
            }
            if (IsPe)
            {
                ulong rva = va - ImageBase;
                ulong headerSize = HeaderMapSize;
                if (rva < headerSize)
                {
                    fileOffset = (int)rva;
                    available = (int)Math.Min(headerSize - rva, segmentEnd - va);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 依「記憶體中的樣子」讀取：有檔案內容的部分複製檔案位元組，RawSize 之後（.bss）補 0，可以跨越相鄰區段。
        /// 回傳實際讀到的位元組數（遇到不屬於映像的位址就停止）。
        /// </summary>
        internal int ReadImage(ulong va, byte[] buffer, int count)
        {
            var data = Data ?? new byte[0];
            count = Math.Min(count, buffer.Length);
            int done = 0;
            while (done < count)
            {
                ulong cur = va + (ulong)done;
                if (cur < va) break; // 位址繞回
                ulong want = (ulong)(count - done);
                ulong n;
                ulong segmentEnd;
                var s = FindSection(cur, out segmentEnd);
                if (s != null)
                {
                    ulong delta = cur - s.VirtualAddress;
                    ulong fileEnd = Math.Min((ulong)data.Length, (ulong)s.RawOffset + s.RawSize);
                    ulong o = (ulong)s.RawOffset + delta;
                    ulong inSegment = segmentEnd - cur;
                    if (delta < s.RawSize && o < fileEnd)
                    {
                        n = Math.Min(Math.Min(want, fileEnd - o), inSegment);
                        Buffer.BlockCopy(data, (int)o, buffer, done, (int)n);
                    }
                    else
                    {
                        // 未初始化資料：執行時是 0
                        ulong zeroLeft = delta < s.RawSize ? s.RawSize - delta : inSegment;
                        n = Math.Min(Math.Min(want, zeroLeft), inSegment);
                        Array.Clear(buffer, done, (int)n);
                    }
                }
                else
                {
                    ulong rva = cur - ImageBase;
                    ulong headerSize = HeaderMapSize;
                    if (!IsPe || rva >= headerSize) break;
                    n = Math.Min(Math.Min(want, headerSize - rva), segmentEnd - cur);
                    Buffer.BlockCopy(data, (int)rva, buffer, done, (int)n);
                }
                if (n == 0) break;
                done += (int)n;
            }
            return done;
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
            ulong segmentEnd;
            return FindSection(va, out segmentEnd);
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
                var map = new Dictionary<ulong, PeImport>();
                foreach (var i in Imports)
                    if (i != null && !map.ContainsKey(i.IatAddress)) map[i.IatAddress] = i;
                _importsByIat = map;
            }
            PeImport import;
            return _importsByIat.TryGetValue(iatAddress, out import) ? import : null;
        }

        /// <summary>若 va 是匯出函式的位址，回傳該匯出；否則 null。</summary>
        public PeExport ExportAt(ulong address)
        {
            if (_exportsByAddress == null)
            {
                var map = new Dictionary<ulong, PeExport>();
                foreach (var e in Exports.Where(x => x != null && x.ForwardTo == null))
                    if (!map.ContainsKey(e.Address)) map[e.Address] = e;
                _exportsByAddress = map;
            }
            PeExport export;
            return _exportsByAddress.TryGetValue(address, out export) ? export : null;
        }

        /// <summary>若 address 是掃描到的字串開頭，回傳該字串；否則 null（比在 Strings 中逐一搜尋快）。</summary>
        public FoundString StringAt(ulong address)
        {
            if (_stringsByAddress == null)
            {
                var map = new Dictionary<ulong, FoundString>();
                foreach (var s in Strings)
                    if (!map.ContainsKey(s.Address)) map[s.Address] = s;
                _stringsByAddress = map;
            }
            FoundString found;
            return _stringsByAddress.TryGetValue(address, out found) ? found : null;
        }

        /// <summary>讀取以 0 結尾、全部是可列印字元的 ASCII 字串；不是字串時回傳 null。</summary>
        public string ReadCString(ulong va, int max = 256, int minLength = 1)
        {
            if (max <= 0) return null;
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
            if (maxChars <= 0) return null;
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
                if (TlsCallbacks.Count > 0)
                    sb.Append("TLS 回呼：").Append(TlsCallbacks.Count).Append(" 個（會在進入點之前執行）：")
                      .Append(string.Join("、", TlsCallbacks.Take(8).Select(a => "0x" + a.ToString("X", CultureInfo.InvariantCulture)))).Append(TlsCallbacks.Count > 8 ? "…" : string.Empty).Append("\r\n");
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

        /// <summary>最多讀取的區段數（Windows Vista 之後上限是 65535，但正常檔案很少超過 100 個）。</summary>
        internal const int MaxSections = 2048;

        /// <summary>匯入函式總數上限（一般匯入 + 延遲載入）；正常程式很少超過幾千個。</summary>
        internal const int MaxImports = 100000;

        internal const int MaxImportDescriptors = 4096;

        /// <summary>匯出位址表的上限（序號是 16 位元，最多 65536 個）。</summary>
        internal const int MaxExports = 65536;

        internal const int MaxTlsCallbacks = 256;

        /// <summary>匯入／匯出名稱合計最多讀取的字元數（避免構造的檔案用大量長名稱耗盡記憶體）。</summary>
        internal const int NameCharBudget = 4 * 1024 * 1024;

        /// <summary>單一匯入／匯出名稱的長度上限（C++ 修飾名稱可能很長）。</summary>
        internal const int MaxNameLength = 1024;

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
            try
            {
                // DOS 也接受 "ZM" 開頭的 EXE（很少見）；Windows 的 PE 一定是 "MZ"
                if (data.Length >= 2 && ((data[0] == 'M' && data[1] == 'Z') || (data[0] == 'Z' && data[1] == 'M'))) return LoadMz(data, name);
                if (data.Length >= 4 && data[0] == 0x7F && data[1] == 'E' && data[2] == 'L' && data[3] == 'F')
                    throw new BinaryFormatException("這是 Linux 的 ELF 執行檔。目前支援 Windows EXE/DLL（PE）、DOS EXE（MZ）與 COM 檔。");
                if (name.EndsWith(".com", StringComparison.OrdinalIgnoreCase)) return LoadCom(data, name);
                throw new BinaryFormatException("不是可辨識的執行檔（開頭不是 \"MZ\"）。若是沒有檔頭的機器碼，請用「原始機器碼」方式載入。");
            }
            catch (BinaryFormatException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentException || ex is OverflowException ||
                                       ex is InvalidOperationException || ex is NullReferenceException || ex is InvalidCastException)
            {
                // 最後一道防線：解析器本身不應走到這裡；若檔案構造得太奇怪，也只回報格式錯誤而不讓程式當掉
                throw new BinaryFormatException("檔案格式損毀，無法解析（內部錯誤：" + ex.GetType().Name + "）。");
            }
        }

        /// <summary>沒有檔頭的機器碼（例如開機磁區、自己組譯的 .bin）。</summary>
        public static LoadedBinary LoadRaw(byte[] data, string fileName, int bits, ulong baseAddress)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (bits != 16 && bits != 32 && bits != 64) throw new ArgumentException("bits 必須是 16、32 或 64");
            if (data.Length > MaxFileSize) throw new BinaryFormatException("檔案太大（超過 256 MB），無法載入。");
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
            if (data.Length == 0) b.Warnings.Add("檔案是空的，沒有任何指令。");
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

        private static string Hex(ulong value)
        {
            return "0x" + value.ToString("X", CultureInfo.InvariantCulture);
        }

        private static LoadedBinary LoadMz(byte[] data, string name)
        {
            if (data.Length < 0x1C) throw new BinaryFormatException("DOS 檔頭不完整（檔案太小）。");
            if (data.Length >= 0x40 && data[0] == 'M')
            {
                uint lfanew = U32(data, 0x3C);
                // e_lfanew 可以小於 0x40（PE 檔頭與 DOS 檔頭重疊的極小 PE），只要指到 "PE\0\0" 就是 PE
                if (lfanew >= 4 && lfanew <= (uint)data.Length - 4)
                {
                    if (data[lfanew] == 'P' && data[lfanew + 1] == 'E' && data[lfanew + 2] == 0 && data[lfanew + 3] == 0)
                        return LoadPe(data, name, lfanew);
                    if (lfanew >= 0x40 && ((data[lfanew] == 'N' && data[lfanew + 1] == 'E') || (data[lfanew] == 'L' && (data[lfanew + 1] == 'E' || data[lfanew + 1] == 'X'))))
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
            ushort lastPage = U16(data, 2);      // e_cblp：最後一頁（512 位元組）實際使用的位元組數，0 表示整頁
            ushort pages = U16(data, 4);         // e_cp：檔案佔幾頁（含檔頭）
            ushort relocations = U16(data, 6);   // e_crlc
            ushort headerParagraphs = U16(data, 8); // e_cparhdr：檔頭大小（以 16 位元組為單位）
            ushort ss = U16(data, 14);
            ushort sp = U16(data, 16);
            ushort ip = U16(data, 20);
            ushort cs = U16(data, 22);

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
                DosRelocationCount = relocations
            };

            long imageEnd;
            if (pages == 0)
            {
                b.Warnings.Add("檔頭記錄的頁數（e_cp）是 0，改以實際檔案大小為準。");
                imageEnd = data.Length;
            }
            else
            {
                int last = lastPage;
                if (last > 512)
                {
                    b.Warnings.Add("最後一頁的位元組數（e_cblp = " + last + "）大於 512，視為整頁。");
                    last = 0;
                }
                imageEnd = (long)pages * 512 - (last != 0 ? 512 - last : 0);
            }
            if (imageEnd > data.Length)
            {
                b.Warnings.Add("檔頭記錄的大小（" + imageEnd + "）比實際檔案大，已以實際大小為準。");
                imageEnd = data.Length;
            }
            else if (imageEnd < data.Length)
            {
                b.Warnings.Add("檔案結尾有 " + (data.Length - imageEnd) + " 位元組不屬於載入模組（可能是覆蓋資料 overlay），不會反組譯。");
            }

            long headerSize = (long)headerParagraphs * 16;
            if (headerSize > imageEnd)
            {
                b.Warnings.Add("DOS 檔頭大小不合理，沒有可反組譯的程式碼。");
                headerSize = imageEnd;
            }
            b.DosHeaderSize = (int)headerSize;
            uint size = (uint)Math.Max(0, imageEnd - headerSize);
            b.Sections.Add(new PeSection
            {
                Name = "load module", VirtualAddress = 0, Rva = 0, VirtualSize = size, RawOffset = (uint)headerSize, RawSize = size,
                Characteristics = 0xE0000020u
            });
            // CS 是相對於載入模組開頭的段落；CS×16 + IP 超過 1 MB 時在真實模式會繞回
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
            }
            // .NET 的 ReadyToRun（預先編譯）映像在非 Windows 平台會把 Machine 與作業系統代碼做 XOR
            for (int i = 0; i < NativeOsOverrides.Length; i++)
            {
                ushort real = (ushort)(machine ^ NativeOsOverrides[i]);
                if (real == 0x014C || real == 0x8664 || real == 0x01C4 || real == 0xAA64)
                    return MachineText(real, out bits) + "，.NET ReadyToRun（" + NativeOsNames[i] + " 版）";
            }
            return "未知（0x" + machine.ToString("X4", CultureInfo.InvariantCulture) + "）";
        }

        private static readonly ushort[] NativeOsOverrides = { 0x4644, 0xADC4, 0x7B79, 0x1993, 0x1992 };
        private static readonly string[] NativeOsNames = { "macOS", "FreeBSD", "Linux", "NetBSD", "SunOS" };

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
                case 13: return "EFI ROM";
                case 14: return "Xbox";
                case 16: return "Windows 開機應用程式";
                default: return "其他（" + subsystem + "）";
            }
        }

        /// <summary>區段名稱：8 位元組、以 0 結尾；"/數字" 是 COFF 字串表中的長名稱（MinGW 的 .debug_* 區段會用）。</summary>
        private static string SectionName(byte[] data, long sh, long stringTable, long stringTableEnd)
        {
            int len = 0;
            while (len < 8 && data[sh + len] != 0) len++;
            var sb = new StringBuilder(len);
            for (int k = 0; k < len; k++)
            {
                byte c = data[sh + k];
                sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '?');
            }
            string name = sb.ToString();
            if (name.Length >= 2 && name[0] == '/' && stringTable >= 0)
            {
                long offset = 0;
                bool digits = true;
                for (int k = 1; k < name.Length; k++)
                {
                    if (name[k] < '0' || name[k] > '9')
                    {
                        digits = false;
                        break;
                    }
                    offset = offset * 10 + (name[k] - '0');
                }
                if (digits && offset >= 4 && stringTable + offset < stringTableEnd)
                {
                    var full = new StringBuilder();
                    for (long p = stringTable + offset; p < stringTableEnd && data[p] != 0 && full.Length < 64; p++)
                        full.Append(data[p] >= 0x20 && data[p] < 0x7F ? (char)data[p] : '?');
                    if (full.Length > 0) name = full.ToString();
                }
            }
            return name;
        }

        private static LoadedBinary LoadPe(byte[] data, string name, uint peOffset)
        {
            long coff = (long)peOffset + 4;
            ushort machine = U16(data, coff);
            ushort sectionCount = U16(data, coff + 2);
            uint timeStamp = U32(data, coff + 4);
            uint symbolTable = U32(data, coff + 8);
            uint symbolCount = U32(data, coff + 12);
            ushort optionalSize = U16(data, coff + 16);
            ushort characteristics = U16(data, coff + 18);
            long opt = coff + 20;
            ushort magic = U16(data, opt);
            bool pe64;
            if (magic == 0x10B) pe64 = false;
            else if (magic == 0x20B) pe64 = true;
            else throw new BinaryFormatException("PE 選用標頭的格式碼 0x" + magic.ToString("X", CultureInfo.InvariantCulture) + " 不正確（應為 0x10B 或 0x20B）。");

            // 選用標頭固定欄位（到 NumberOfRvaAndSizes 為止）一定要完整，否則無法解析
            long fixedSize = pe64 ? 112 : 96;
            if (opt + fixedSize > data.Length) throw new BinaryFormatException("PE 選用標頭被截斷（檔案太小）。");

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
            if (bits == 32 && pe64)
            {
                bits = 64;
                b.Warnings.Add("Machine 是 i386 但檔頭是 PE32+，以 64 位元反組譯。");
            }
            if (bits == 64 && !pe64)
            {
                bits = 32;
                b.Warnings.Add("Machine 是 AMD64 但檔頭是 PE32，以 32 位元反組譯。");
            }
            b.Bits = bits;
            if (bits == 0) b.Warnings.Add("CPU 架構是 " + b.MachineName + "，本程式只能反組譯 x86/x64 機器碼，以下只顯示檔頭、區段、匯入與匯出。");
            if (timeStamp != 0 && timeStamp != 0xFFFFFFFF)
                b.TimeStamp = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(timeStamp);
            if ((characteristics & 0x0002) == 0)
                b.Warnings.Add("檔頭沒有標示「可執行映像」（IMAGE_FILE_EXECUTABLE_IMAGE），Windows 不會執行它。");
            if (optionalSize < fixedSize)
                b.Warnings.Add("選用標頭大小（SizeOfOptionalHeader = " + optionalSize + "）比標準小，區段表與選用標頭重疊。");

            uint entryRva = U32(data, opt + 16);
            b.ImageBase = pe64 ? U64(data, opt + 24) : U32(data, opt + 28);
            b.SizeOfImage = U32(data, opt + 56);
            b.SizeOfHeaders = U32(data, opt + 60);
            b.Subsystem = U16(data, opt + 68);
            b.SubsystemName = SubsystemText(b.Subsystem);
            b.DllCharacteristics = U16(data, opt + 70);
            if (b.SizeOfHeaders > data.Length)
                b.Warnings.Add("檔頭大小（SizeOfHeaders = " + Hex(b.SizeOfHeaders) + "）比檔案還大，只對應到檔案結尾。");

            // 資料目錄：數量由 NumberOfRvaAndSizes 決定（最多 16 個）。和 Windows 載入器一樣，即使超出 SizeOfOptionalHeader 也照讀。
            long dirCountOffset = opt + (pe64 ? 108 : 92);
            uint dirCountRaw = U32(data, dirCountOffset);
            long dirBase = dirCountOffset + 4;
            int dirCount = (int)Math.Min(dirCountRaw, 16u);
            if (dirCountRaw > 16) b.Warnings.Add("資料目錄數量（NumberOfRvaAndSizes = " + dirCountRaw + "）超過 16，只讀取前 16 個。");
            var dirRva = new uint[16];
            var dirSize = new uint[16];
            for (int i = 0; i < dirCount; i++)
            {
                long at = dirBase + i * 8L;
                if (at + 8 > data.Length)
                {
                    b.Warnings.Add("資料目錄被截斷（檔案太小）。");
                    break;
                }
                dirRva[i] = U32(data, at);
                dirSize[i] = U32(data, at + 4);
            }
            if (dirCount > 0 && dirBase + dirCount * 8L > opt + optionalSize)
                b.Warnings.Add("資料目錄超出選用標頭範圍（與區段表重疊）。");

            ReadSections(b, data, opt + optionalSize, sectionCount, symbolTable, symbolCount);

            b.HasEntryPoint = entryRva != 0;
            b.EntryPoint = b.ImageBase + entryRva;
            AddSyntheticSections(b, entryRva);
            CheckEntryPoint(b);

            // CLR（.NET）
            if (dirRva[14] != 0)
            {
                var clr = new byte[0x48];
                if (dirSize[14] >= 0x48 && b.ReadImage(b.ImageBase + dirRva[14], clr, clr.Length) == clr.Length)
                {
                    b.IsDotNet = true;
                    b.ClrHeaderRva = dirRva[14];
                    b.ClrHeaderSize = dirSize[14];
                }
                else
                {
                    b.Warnings.Add("CLR（.NET）標頭目錄無效或超出檔案，不當作 .NET 組件。");
                }
            }

            var ctx = new PeContext(b, pe64);
            RunSafely(b, "匯入表讀取失敗：", () => ReadImports(ctx, dirRva[1]));
            RunSafely(b, "延遲載入匯入表讀取失敗：", () => ReadDelayImports(ctx, dirRva[13]));
            RunSafely(b, "匯出表讀取失敗：", () => ReadExports(ctx, dirRva[0], dirSize[0]));
            RunSafely(b, "TLS 目錄讀取失敗：", () => ReadTls(ctx, dirRva[9]));
            b.InvalidateSectionIndex();
            return b;
        }

        private static void RunSafely(LoadedBinary b, string prefix, Action action)
        {
            try
            {
                action();
            }
            catch (BinaryFormatException ex)
            {
                b.Warnings.Add(prefix + ex.Message);
            }
        }

        private static void ReadSections(LoadedBinary b, byte[] data, long sectionTable, int sectionCount, uint symbolTable, uint symbolCount)
        {
            // COFF 字串表（長區段名稱用）：緊接在符號表（每個 18 位元組）之後，開頭 4 位元組是字串表大小
            long stringTable = -1, stringTableEnd = -1;
            if (symbolTable != 0)
            {
                long st = (long)symbolTable + (long)symbolCount * 18;
                if (st + 4 <= data.Length)
                {
                    stringTable = st;
                    stringTableEnd = Math.Min(data.Length, st + U32(data, st));
                }
            }

            int count = sectionCount;
            if (count > MaxSections)
            {
                b.Warnings.Add("區段數量 " + count + " 異常，只讀取前 " + MaxSections + " 個。");
                count = MaxSections;
            }
            for (int i = 0; i < count; i++)
            {
                long sh = sectionTable + i * 40L;
                if (sh + 40 > data.Length)
                {
                    b.Warnings.Add("區段表被截斷（只讀到 " + i + " 個區段）。");
                    break;
                }
                string secName = SectionName(data, sh, stringTable, stringTableEnd);
                if (secName.Length == 0) secName = "(無名)";
                uint vsize = U32(data, sh + 8);
                uint va = U32(data, sh + 12);
                uint rawSize = U32(data, sh + 16);
                uint rawPtr = U32(data, sh + 20);
                uint ch = U32(data, sh + 36);

                // VirtualSize 為 0 時，Windows 以 SizeOfRawData 當作記憶體大小
                uint memSize = vsize != 0 ? vsize : rawSize;
                uint fileSize = rawSize;
                if (rawSize != 0 && rawPtr == 0)
                {
                    // PointerToRawData = 0 表示只有未初始化資料，不可把檔頭當成區段內容
                    fileSize = 0;
                }
                else if (rawSize != 0 && rawPtr >= (uint)data.Length)
                {
                    b.Warnings.Add("區段 " + secName + " 的檔案位置（" + Hex(rawPtr) + "）超出檔案結尾，視為沒有內容。");
                    fileSize = 0;
                }
                else if ((ulong)rawPtr + rawSize > (ulong)data.Length)
                {
                    b.Warnings.Add("區段 " + secName + " 的資料超出檔案結尾，已截短。");
                    fileSize = (uint)(data.Length - rawPtr);
                }
                // 檔案對齊造成的尾端填充不屬於區段內容（反組譯時不應把填充的 0 當成指令）
                if (fileSize > memSize) fileSize = memSize;
                if ((ulong)va + memSize > 0x100000000UL)
                    b.Warnings.Add("區段 " + secName + " 的範圍超過 4 GB 映像上限（RVA " + Hex(va) + " + " + Hex(memSize) + "）。");

                b.Sections.Add(new PeSection
                {
                    Name = secName,
                    Rva = va,
                    VirtualAddress = b.ImageBase + va,
                    VirtualSize = memSize,
                    RawOffset = rawPtr,
                    RawSize = fileSize,
                    Characteristics = ch
                });
            }

            // 重疊的區段：Windows 不會載入這種檔案；這裡以區段表中較前面的區段為準
            var sorted = b.Sections.Where(s => s.MemorySize > 0).OrderBy(s => s.Rva).ToList();
            for (int i = 0; i + 1 < sorted.Count; i++)
            {
                if ((ulong)sorted[i].Rva + sorted[i].MemorySize > sorted[i + 1].Rva)
                {
                    b.Warnings.Add("區段 " + sorted[i].Name + " 與 " + sorted[i + 1].Name + " 的位址範圍重疊（檔案可能損毀或被刻意構造），重疊處以區段表中較前面的區段為準。");
                    break;
                }
            }
        }

        /// <summary>沒有區段、或進入點落在檔頭時，補上合成的區段，讓反組譯器仍能處理。</summary>
        private static void AddSyntheticSections(LoadedBinary b, uint entryRva)
        {
            uint headerMap = b.HeaderMapSize;
            if (b.Sections.Count == 0)
            {
                uint fileSize = (uint)b.Data.Length;
                uint raw = b.SizeOfImage != 0 ? Math.Min(fileSize, b.SizeOfImage) : fileSize;
                uint mem = Math.Max(raw, Math.Min(b.SizeOfImage, (uint)MaxFileSize));
                b.Sections.Add(new PeSection
                {
                    Name = "(整個映像)", Rva = 0, VirtualAddress = b.ImageBase, VirtualSize = mem, RawOffset = 0, RawSize = raw,
                    Characteristics = 0xE0000020u
                });
                b.Warnings.Add("這個 PE 沒有區段表，整個檔案被當成一個可讀寫執行的區段（極小化 PE 的技巧）。");
                return;
            }
            if (!b.HasEntryPoint || b.SectionAt(b.EntryPoint) != null || entryRva >= headerMap) return;
            // 進入點在檔頭裡（極小化 PE 常見）：把檔頭當成一個可執行區段，但不覆蓋真正的區段
            uint size = headerMap;
            foreach (var s in b.Sections)
                if (s.MemorySize > 0 && s.Rva > entryRva && s.Rva < size) size = s.Rva;
            b.Sections.Add(new PeSection
            {
                Name = "(檔頭)", Rva = 0, VirtualAddress = b.ImageBase, VirtualSize = size, RawOffset = 0, RawSize = size,
                Characteristics = 0x60000020u
            });
            b.Warnings.Add("進入點位於 PE 檔頭內（不在任何區段中），已把檔頭當成程式碼區段處理。");
        }

        private static void CheckEntryPoint(LoadedBinary b)
        {
            if (!b.HasEntryPoint) return;
            var s = b.SectionAt(b.EntryPoint);
            if (s == null)
            {
                b.Warnings.Add("進入點不在任何區段內（可能是加殼或損毀的檔案）。");
                return;
            }
            if (b.EntryPoint - s.VirtualAddress >= s.RawSize)
                b.Warnings.Add("進入點落在區段 " + s.Name + " 沒有檔案內容的部分（執行時才會被填入，常見於加殼程式）。");
            else if (!s.IsExecutable)
                b.Warnings.Add("進入點所在的區段 " + s.Name + " 沒有標示為可執行。");
        }

        // ------------------------------------------------------------------
        // 匯入／匯出／TLS
        // ------------------------------------------------------------------

        /// <summary>解析 PE 目錄時共用的狀態：依「記憶體映像」讀取、名稱快取與字元預算。</summary>
        private sealed class PeContext
        {
            private readonly byte[] _buf = new byte[8];
            private readonly byte[] _chunk = new byte[64];
            private readonly Dictionary<ulong, string> _names = new Dictionary<ulong, string>();
            private int _budget = NameCharBudget;
            private bool _budgetWarned;

            public PeContext(LoadedBinary binary, bool pe64)
            {
                B = binary;
                Pe64 = pe64;
                PtrSize = pe64 ? 8 : 4;
            }

            public LoadedBinary B { get; private set; }
            public bool Pe64 { get; private set; }
            public int PtrSize { get; private set; }
            public int ImportCount { get; set; }
            public bool ImportCapWarned { get; set; }

            private bool Read(ulong rva, int count)
            {
                if (rva > uint.MaxValue || rva + (ulong)count > 0x100000000UL) return false;
                return B.ReadImage(B.ImageBase + rva, _buf, count) == count;
            }

            public bool TryU16(ulong rva, out ushort value)
            {
                value = 0;
                if (!Read(rva, 2)) return false;
                value = (ushort)(_buf[0] | (_buf[1] << 8));
                return true;
            }

            public bool TryU32(ulong rva, out uint value)
            {
                value = 0;
                if (!Read(rva, 4)) return false;
                value = (uint)(_buf[0] | (_buf[1] << 8) | (_buf[2] << 16) | (_buf[3] << 24));
                return true;
            }

            public bool TryPtr(ulong rva, out ulong value)
            {
                value = 0;
                if (!Read(rva, PtrSize)) return false;
                ulong lo = (uint)(_buf[0] | (_buf[1] << 8) | (_buf[2] << 16) | (_buf[3] << 24));
                if (!Pe64)
                {
                    value = lo;
                    return true;
                }
                ulong hi = (uint)(_buf[4] | (_buf[5] << 8) | (_buf[6] << 16) | (_buf[7] << 24));
                value = lo | (hi << 32);
                return true;
            }

            /// <summary>讀取一整塊（例如匯出位址表）；回傳實際讀到的位元組數。</summary>
            public int ReadBlock(ulong rva, byte[] buffer, int count)
            {
                if (rva > uint.MaxValue) return 0;
                ulong max = 0x100000000UL - rva;
                if ((ulong)count > max) count = (int)max;
                return B.ReadImage(B.ImageBase + rva, buffer, count);
            }

            /// <summary>讀取以 0 結尾的 ASCII 名稱（非可列印字元以 '?' 表示）；讀不到或空字串時回傳 null。</summary>
            public string ReadName(ulong rva, int max)
            {
                string cached;
                if (_names.TryGetValue(rva, out cached)) return cached;
                if (_budget <= 0)
                {
                    if (!_budgetWarned)
                    {
                        _budgetWarned = true;
                        B.Warnings.Add("匯入／匯出名稱總長度超過上限，其餘名稱不再讀取。");
                    }
                    return null;
                }
                var sb = new StringBuilder();
                ulong pos = rva;
                bool done = false;
                while (!done && sb.Length < max && pos <= uint.MaxValue)
                {
                    int want = (int)Math.Min((ulong)_chunk.Length, Math.Min((ulong)(max - sb.Length), 0x100000000UL - pos));
                    int got = B.ReadImage(B.ImageBase + pos, _chunk, want);
                    if (got <= 0) break;
                    for (int k = 0; k < got; k++)
                    {
                        byte c = _chunk[k];
                        if (c == 0)
                        {
                            done = true;
                            break;
                        }
                        sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '?');
                    }
                    if (got < want) break;
                    pos += (ulong)got;
                }
                string result = sb.Length > 0 ? sb.ToString() : null;
                _budget -= sb.Length + 16;
                _names[rva] = result;
                return result;
            }

            /// <summary>增加一個匯入；達到上限時回傳 false。</summary>
            public bool AddImport(PeImport import)
            {
                if (ImportCount >= MaxImports)
                {
                    if (!ImportCapWarned)
                    {
                        ImportCapWarned = true;
                        B.Warnings.Add("匯入函式超過 " + MaxImports + " 個，其餘的不再列出。");
                    }
                    return false;
                }
                B.Imports.Add(import);
                ImportCount++;
                return true;
            }
        }

        private static string UnreadableName(ulong rva)
        {
            return "(RVA " + Hex(rva) + ")";
        }

        /// <summary>
        /// 一般匯入表：IMAGE_IMPORT_DESCRIPTOR（20 位元組）陣列，Name 或 FirstThunk 為 0 時結束（與 Windows 載入器相同）。
        /// 名稱取自 OriginalFirstThunk（匯入名稱表 ILT）；沒有 ILT 時改用 FirstThunk（IAT）。
        /// </summary>
        private static void ReadImports(PeContext c, uint rva)
        {
            if (rva == 0) return;
            var b = c.B;
            int ptr = c.PtrSize;
            ulong ordinalFlag = c.Pe64 ? 0x8000000000000000UL : 0x80000000UL;
            for (int d = 0; ; d++)
            {
                if (d >= MaxImportDescriptors)
                {
                    b.Warnings.Add("匯入描述元超過 " + MaxImportDescriptors + " 個，其餘的不再讀取。");
                    return;
                }
                ulong desc = (ulong)rva + (ulong)d * 20;
                uint ilt, stamp, nameRva, iat;
                if (!c.TryU32(desc, out ilt) || !c.TryU32(desc + 4, out stamp) || !c.TryU32(desc + 12, out nameRva) || !c.TryU32(desc + 16, out iat))
                {
                    b.Warnings.Add("匯入描述元 #" + d + "（RVA " + Hex(desc) + "）不在映像內，匯入表被截斷。");
                    return;
                }
                if (nameRva == 0 || iat == 0) return;
                string dll = c.ReadName(nameRva, 256) ?? "(未知 DLL)";
                ulong thunkTable = ilt != 0 ? ilt : iat;
                if (ilt == 0 && stamp != 0)
                    b.Warnings.Add(dll + " 的匯入已預先繫結（bound）且沒有匯入名稱表，函式名稱可能不正確。");
                for (ulong i = 0; ; i++)
                {
                    ulong value;
                    if (!c.TryPtr(thunkTable + i * (ulong)ptr, out value))
                    {
                        b.Warnings.Add(dll + " 的匯入名稱表超出映像範圍，已截斷。");
                        break;
                    }
                    if (value == 0) break;
                    var import = new PeImport { Dll = dll, IatAddress = b.ImageBase + iat + i * (ulong)ptr };
                    if ((value & ordinalFlag) != 0)
                    {
                        import.Ordinal = (int)(value & 0xFFFF);
                    }
                    else if (value > 0x7FFFFFFF)
                    {
                        import.Name = UnreadableName(value);
                    }
                    else
                    {
                        ushort hint;
                        if (c.TryU16(value, out hint)) import.Ordinal = hint;
                        import.Name = c.ReadName(value + 2, MaxNameLength) ?? UnreadableName(value);
                    }
                    if (!c.AddImport(import)) return;
                }
            }
        }

        /// <summary>
        /// 延遲載入匯入表：ImgDelayDescr（32 位元組）陣列，DllNameRVA 為 0 時結束。
        /// Attributes 第 0 位元為 1 表示欄位是 RVA；為 0 是舊版（VC6）格式，欄位是虛擬位址（VA）。
        /// </summary>
        private static void ReadDelayImports(PeContext c, uint rva)
        {
            if (rva == 0) return;
            var b = c.B;
            int ptr = c.PtrSize;
            ulong ordinalFlag = c.Pe64 ? 0x8000000000000000UL : 0x80000000UL;
            for (int d = 0; ; d++)
            {
                if (d >= MaxImportDescriptors)
                {
                    b.Warnings.Add("延遲載入描述元超過 " + MaxImportDescriptors + " 個，其餘的不再讀取。");
                    return;
                }
                ulong desc = (ulong)rva + (ulong)d * 32;
                uint attributes, nameField, iatField, intField;
                if (!c.TryU32(desc, out attributes) || !c.TryU32(desc + 4, out nameField) || !c.TryU32(desc + 12, out iatField) || !c.TryU32(desc + 16, out intField))
                {
                    b.Warnings.Add("延遲載入描述元 #" + d + " 不在映像內，延遲載入匯入表被截斷。");
                    return;
                }
                if (nameField == 0) return;
                bool rvaBased = (attributes & 1) != 0;
                ulong nameRva = ToRva(b, nameField, rvaBased);
                ulong iatRva = ToRva(b, iatField, rvaBased);
                ulong intRva = ToRva(b, intField, rvaBased);
                string dll = c.ReadName(nameRva, 256) ?? "(未知 DLL)";
                if (intField == 0 || intRva > uint.MaxValue || iatRva > uint.MaxValue)
                {
                    b.Warnings.Add("延遲載入的 " + dll + " 沒有有效的匯入名稱表，無法列出函式。");
                    continue;
                }
                for (ulong i = 0; ; i++)
                {
                    ulong value;
                    if (!c.TryPtr(intRva + i * (ulong)ptr, out value))
                    {
                        b.Warnings.Add("延遲載入的 " + dll + " 匯入名稱表超出映像範圍，已截斷。");
                        break;
                    }
                    if (value == 0) break;
                    var import = new PeImport { Dll = dll, IsDelayLoad = true, IatAddress = b.ImageBase + iatRva + i * (ulong)ptr };
                    if ((value & ordinalFlag) != 0)
                    {
                        import.Ordinal = (int)(value & 0xFFFF);
                    }
                    else
                    {
                        ulong hintName = rvaBased ? value : value - b.ImageBase;
                        if (hintName > 0x7FFFFFFF)
                        {
                            import.Name = UnreadableName(value);
                        }
                        else
                        {
                            ushort hint;
                            if (c.TryU16(hintName, out hint)) import.Ordinal = hint;
                            import.Name = c.ReadName(hintName + 2, MaxNameLength) ?? UnreadableName(hintName);
                        }
                    }
                    if (!c.AddImport(import)) return;
                }
            }
        }

        /// <summary>舊版延遲載入描述元的欄位是 VA，要減去 ImageBase；結果不合理時傳回超過 32 位元的值（讀取會失敗）。</summary>
        private static ulong ToRva(LoadedBinary b, uint value, bool rvaBased)
        {
            if (rvaBased) return value;
            return value >= b.ImageBase ? value - b.ImageBase : ulong.MaxValue;
        }

        /// <summary>
        /// 匯出目錄：位址表（EAT，索引 + OrdinalBase = 序號）、名稱指標表與名稱序號表（名稱 → EAT 索引）。
        /// 位址落在匯出目錄範圍內的是轉送匯出（字串 "DLL.函式"）。同一個函式可以有多個名稱（別名）。
        /// </summary>
        private static void ReadExports(PeContext c, uint rva, uint size)
        {
            if (rva == 0) return;
            var b = c.B;
            var dir = new byte[40];
            if (c.ReadBlock(rva, dir, 40) < 40)
            {
                b.Warnings.Add("匯出目錄（RVA " + Hex(rva) + "）不在映像內。");
                return;
            }
            uint ordinalBase = U32(dir, 16);
            uint functionCountRaw = U32(dir, 20);
            uint nameCountRaw = U32(dir, 24);
            uint functions = U32(dir, 28);
            uint names = U32(dir, 32);
            uint ordinals = U32(dir, 36);

            int functionCount = (int)Math.Min(functionCountRaw, (uint)MaxExports);
            int nameCount = (int)Math.Min(nameCountRaw, (uint)MaxExports);
            if (functionCountRaw > MaxExports) b.Warnings.Add("匯出函式數量（" + functionCountRaw + "）異常，只讀取前 " + MaxExports + " 個。");
            if (nameCountRaw > MaxExports) b.Warnings.Add("匯出名稱數量（" + nameCountRaw + "）異常，只讀取前 " + MaxExports + " 個。");

            var eat = new byte[functionCount * 4];
            int eatBytes = functionCount > 0 ? c.ReadBlock(functions, eat, eat.Length) : 0;
            if (eatBytes < eat.Length)
            {
                b.Warnings.Add("匯出位址表超出映像範圍，已截斷。");
                functionCount = eatBytes / 4;
            }

            // 名稱 → EAT 索引（第一個名稱放 firstName，其餘當作別名）
            var firstName = new string[functionCount];
            var aliases = new List<KeyValuePair<int, string>>();
            if (nameCount > 0)
            {
                var namePtrs = new byte[nameCount * 4];
                var nameOrds = new byte[nameCount * 2];
                int n1 = c.ReadBlock(names, namePtrs, namePtrs.Length) / 4;
                int n2 = c.ReadBlock(ordinals, nameOrds, nameOrds.Length) / 2;
                int usable = Math.Min(n1, n2);
                if (usable < nameCount) b.Warnings.Add("匯出名稱表超出映像範圍，已截斷。");
                int badIndex = 0;
                for (int j = 0; j < usable; j++)
                {
                    int index = nameOrds[j * 2] | (nameOrds[j * 2 + 1] << 8);
                    if (index >= functionCount)
                    {
                        badIndex++;
                        continue;
                    }
                    uint nameRva = U32(namePtrs, j * 4);
                    string exportName = c.ReadName(nameRva, MaxNameLength) ?? UnreadableName(nameRva);
                    if (firstName[index] == null) firstName[index] = exportName;
                    else if (firstName[index] != exportName) aliases.Add(new KeyValuePair<int, string>(index, exportName));
                }
                if (badIndex > 0) b.Warnings.Add("有 " + badIndex + " 個匯出名稱指到不存在的函式（名稱序號超出位址表範圍），已略過。");
            }

            var aliasesByIndex = aliases.ToLookup(a => a.Key, a => a.Value);
            ulong dirEnd = (ulong)rva + size;
            for (int i = 0; i < functionCount; i++)
            {
                uint fn = U32(eat, i * 4);
                if (fn == 0) continue; // 未使用的序號
                long ordinal = (long)ordinalBase + i;
                var export = new PeExport
                {
                    Name = firstName[i],
                    Ordinal = (int)Math.Min(ordinal, int.MaxValue),
                    Rva = fn,
                    Address = b.ImageBase + fn
                };
                if (fn >= rva && fn < dirEnd) export.ForwardTo = c.ReadName(fn, 256) ?? UnreadableName(fn);
                b.Exports.Add(export);
                foreach (var alias in aliasesByIndex[i])
                    b.Exports.Add(new PeExport { Name = alias, Ordinal = export.Ordinal, Rva = fn, Address = export.Address, ForwardTo = export.ForwardTo });
            }
        }

        /// <summary>
        /// TLS 目錄：AddressOfCallBacks（VA）指向以 0 結尾的回呼函式 VA 陣列。
        /// PE32 的欄位是 4 位元組（AddressOfCallBacks 在位移 12），PE32+ 是 8 位元組（在位移 24）。
        /// </summary>
        private static void ReadTls(PeContext c, uint rva)
        {
            if (rva == 0) return;
            var b = c.B;
            ulong callbacksVa;
            if (!c.TryPtr((ulong)rva + (c.Pe64 ? 24u : 12u), out callbacksVa))
            {
                b.Warnings.Add("TLS 目錄（RVA " + Hex(rva) + "）不在映像內。");
                return;
            }
            if (callbacksVa == 0) return;
            ulong tableRva = callbacksVa - b.ImageBase;
            if (callbacksVa < b.ImageBase || tableRva > uint.MaxValue)
            {
                b.Warnings.Add("TLS 回呼表位址 " + Hex(callbacksVa) + " 不在映像內。");
                return;
            }
            for (int i = 0; ; i++)
            {
                if (i >= MaxTlsCallbacks)
                {
                    b.Warnings.Add("TLS 回呼超過 " + MaxTlsCallbacks + " 個，其餘的不再讀取。");
                    break;
                }
                ulong callback;
                if (!c.TryPtr(tableRva + (ulong)i * (ulong)c.PtrSize, out callback)) break;
                if (callback == 0) break;
                if (!b.TlsCallbacks.Contains(callback)) b.TlsCallbacks.Add(callback);
            }
            foreach (var cb in b.TlsCallbacks)
            {
                if (!b.IsExecutable(cb))
                {
                    b.Warnings.Add("TLS 回呼 " + Hex(cb) + " 不在可執行區段內。");
                    break;
                }
            }
        }
    }

    /// <summary>在檔案中找出 ASCII 與 UTF-16LE 字串。</summary>
    public static class StringScanner
    {
        /// <summary>掃描上限（位元組），避免大檔案在低階電腦上跑太久。</summary>
        public const int MaxScanBytes = 32 * 1024 * 1024;

        /// <summary>單一字串最長字元數；更長的會分段回報。</summary>
        private const int MaxRun = 1024;

        private static readonly bool[] AsciiOk = BuildAsciiTable();

        private static bool[] BuildAsciiTable()
        {
            var t = new bool[256];
            for (int i = 0; i < 256; i++) t[i] = LoadedBinary.IsPrintable((byte)i);
            return t;
        }

        public static List<FoundString> Scan(LoadedBinary binary, int minLength, int maxCount)
        {
            var result = new List<FoundString>();
            if (binary == null || binary.Data == null || maxCount <= 0) return result;
            if (minLength < 1) minLength = 1;
            var data = binary.Data;
            long scanned = 0;
            foreach (var s in binary.Sections)
            {
                if (s == null) continue;
                // PE 只掃描非執行區段（字串通常在 .rdata/.data）；DOS/COM/原始檔只有一個區段，全部掃描
                if (binary.IsPe && s.IsExecutable) continue;
                if (s.RawSize == 0 || s.RawOffset >= data.Length) continue;
                int start = (int)s.RawOffset;
                long end = Math.Min((long)data.Length, (long)s.RawOffset + s.RawSize);
                long budget = MaxScanBytes - scanned;
                if (budget <= 0) break;
                if (end - start > budget) end = start + budget;
                scanned += end - start;
                ScanAscii(data, s, start, (int)end, minLength, maxCount, result);
                ScanUtf16(data, s, start, (int)end, minLength, maxCount, result);
                if (result.Count >= maxCount) break;
            }

            // 依位址排序；同一個位址只保留一個（較長的那個）
            result.Sort((a, b) =>
            {
                int cmp = a.Address.CompareTo(b.Address);
                return cmp != 0 ? cmp : b.Text.Length.CompareTo(a.Text.Length);
            });
            var unique = new List<FoundString>(result.Count);
            foreach (var f in result)
                if (unique.Count == 0 || unique[unique.Count - 1].Address != f.Address) unique.Add(f);
            return unique;
        }

        private static ulong AddressOf(PeSection s, int fileOffset)
        {
            return s.VirtualAddress + (ulong)(fileOffset - (long)s.RawOffset);
        }

        private static void ScanAscii(byte[] data, PeSection s, int start, int end, int minLength, int maxCount, List<FoundString> result)
        {
            int i = start;
            while (i < end && result.Count < maxCount)
            {
                if (!AsciiOk[data[i]])
                {
                    i++;
                    continue;
                }
                int limit = (int)Math.Min(end, (long)i + MaxRun);
                int j = i;
                while (j < limit && AsciiOk[data[j]]) j++;
                bool capped = j - i >= MaxRun;
                if (j - i >= minLength && (j >= end || capped || data[j] == 0))
                {
                    string text = Encoding.ASCII.GetString(data, i, j - i);
                    if (text.Trim().Length >= minLength)
                        result.Add(new FoundString { Address = AddressOf(s, i), Text = Escape(text), Encoding = "ASCII", Section = s.Name });
                }
                // 長字串被分段時從下一段繼續；否則跳過結尾的非字串位元組
                i = capped ? j : j + 1;
            }
        }

        private static bool WideOk(int c)
        {
            return (c >= 0x20 && c < 0x7F) || c == '\t' || c == '\r' || c == '\n' ||
                   (c >= 0x4E00 && c <= 0x9FFF) ||   // CJK 統一漢字
                   (c >= 0x3000 && c <= 0x303F) ||   // CJK 標點
                   (c >= 0x3100 && c <= 0x312F) ||   // 注音符號
                   (c >= 0x2010 && c <= 0x2027) ||   // 常用標點（引號、刪節號）
                   (c >= 0xFF00 && c <= 0xFFEF);     // 全形字元
        }

        private static void ScanUtf16(byte[] data, PeSection s, int start, int end, int minLength, int maxCount, List<FoundString> result)
        {
            // UTF-16 字串在記憶體中通常是 2 位元組對齊
            int i = start + ((AddressOf(s, start) & 1) != 0 ? 1 : 0);
            while (i + 1 < end && result.Count < maxCount)
            {
                if (!WideOk(data[i] | (data[i + 1] << 8)))
                {
                    i += 2;
                    continue;
                }
                int j = i, n = 0;
                bool hasNonPrintablePair = false;
                while (j + 1 < end && n < MaxRun)
                {
                    int c = data[j] | (data[j + 1] << 8);
                    if (!WideOk(c)) break;
                    // 兩個位元組都是可列印 ASCII 的「漢字」可能只是 ASCII 文字被誤讀
                    if (c < 0x100 || !AsciiOk[data[j]] || !AsciiOk[data[j + 1]]) hasNonPrintablePair = true;
                    j += 2;
                    n++;
                }
                bool capped = n >= MaxRun;
                bool terminated = j + 1 < end && data[j] == 0 && data[j + 1] == 0;
                if (n >= minLength && hasNonPrintablePair && (terminated || capped || j + 1 >= end))
                {
                    var chars = new char[n];
                    for (int k = 0; k < n; k++) chars[k] = (char)(data[i + 2 * k] | (data[i + 2 * k + 1] << 8));
                    string text = new string(chars);
                    if (text.Trim().Length >= minLength)
                        result.Add(new FoundString { Address = AddressOf(s, i), Text = Escape(text), Encoding = "UTF-16", Section = s.Name });
                }
                i = terminated ? j + 2 : j;
            }
        }

        private static string Escape(string s)
        {
            return s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }
}
