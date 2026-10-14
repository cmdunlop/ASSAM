using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Assam.Core.Binary
{
    /// <summary>
    /// 讀取 .NET 組件的中繼資料（ECMA-335 第 II 部分）：CLR 標頭、中繼資料根（"BSJB"）、
    /// 資料表串流（#~ 或未壓縮的 #-）、#Strings、#US、#Blob、#GUID 堆積。
    /// 只讀取位元組，不會載入組件；每次讀取都先檢查範圍，格式錯誤時記錄訊息並盡量讀出部分結果，不會丟出例外。
    /// </summary>
    internal sealed class ClrMetadataReader
    {
        // ------------------------------------------------------------------
        // 資料表編號（ECMA-335 II.22）
        // ------------------------------------------------------------------
        public const int TableModule = 0x00;
        public const int TableTypeRef = 0x01;
        public const int TableTypeDef = 0x02;
        public const int TableFieldPtr = 0x03;
        public const int TableField = 0x04;
        public const int TableMethodPtr = 0x05;
        public const int TableMethodDef = 0x06;
        public const int TableParamPtr = 0x07;
        public const int TableParam = 0x08;
        public const int TableMemberRef = 0x0A;
        public const int TableStandAloneSig = 0x11;
        public const int TableEvent = 0x14;
        public const int TableProperty = 0x17;
        public const int TableModuleRef = 0x1A;
        public const int TableTypeSpec = 0x1B;
        public const int TableAssembly = 0x20;
        public const int TableAssemblyRef = 0x23;
        public const int TableFile = 0x26;
        public const int TableExportedType = 0x27;
        public const int TableNestedClass = 0x29;
        public const int TableGenericParam = 0x2A;
        public const int TableMethodSpec = 0x2B;

        /// <summary>#US（使用者字串）的語彙基元（token）類別，例如 ldstr 的 0x70xxxxxx。</summary>
        public const int TokenUserString = 0x70;

        /// <summary>已知結構的最後一個資料表（GenericParamConstraint）。</summary>
        public const int LastKnownTable = 0x2C;

        // ------------------------------------------------------------------
        // 欄位種類編碼
        // ------------------------------------------------------------------
        private const int Fixed1 = 0x101;
        private const int Fixed2 = 0x102;
        private const int Fixed4 = 0x104;
        private const int StringIdx = 0x200;
        private const int GuidIdx = 0x201;
        private const int BlobIdx = 0x202;
        private const int SimpleBase = 0x300;
        private const int CodedBase = 0x400;

        // 編碼索引（coded index）種類（II.24.2.6）
        public const int CodedTypeDefOrRef = 0;
        public const int CodedHasConstant = 1;
        public const int CodedHasCustomAttribute = 2;
        public const int CodedHasFieldMarshal = 3;
        public const int CodedHasDeclSecurity = 4;
        public const int CodedMemberRefParent = 5;
        public const int CodedHasSemantics = 6;
        public const int CodedMethodDefOrRef = 7;
        public const int CodedMemberForwarded = 8;
        public const int CodedImplementation = 9;
        public const int CodedCustomAttributeType = 10;
        public const int CodedResolutionScope = 11;
        public const int CodedTypeOrMethodDef = 12;

        /// <summary>每種編碼索引可以指向的資料表（-1 表示保留未用）。</summary>
        private static readonly int[][] CodedTables =
        {
            new[] { 0x02, 0x01, 0x1B },
            new[] { 0x04, 0x08, 0x17 },
            new[] { 0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x00, 0x0E, 0x17, 0x14, 0x11, 0x1A, 0x1B, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2A, 0x2C, 0x2B },
            new[] { 0x04, 0x08 },
            new[] { 0x02, 0x06, 0x20 },
            new[] { 0x02, 0x01, 0x1A, 0x06, 0x1B },
            new[] { 0x14, 0x17 },
            new[] { 0x06, 0x0A },
            new[] { 0x04, 0x06 },
            new[] { 0x26, 0x23, 0x27 },
            new[] { -1, -1, 0x06, 0x0A, -1 },
            new[] { 0x00, 0x1A, 0x23, 0x01 },
            new[] { 0x02, 0x06 }
        };

        /// <summary>每種編碼索引的標記位元數。</summary>
        private static readonly int[] CodedTagBits = { 2, 2, 5, 1, 2, 3, 1, 1, 1, 2, 3, 2, 1 };

        private static int T(int table)
        {
            return SimpleBase + table;
        }

        private static int C(int coded)
        {
            return CodedBase + coded;
        }

        /// <summary>資料表 0x00–0x2C 的欄位結構（決定每列大小，所以全部都要正確，即使用不到）。</summary>
        private static readonly int[][] Schema =
        {
            /* 0x00 Module */ new[] { Fixed2, StringIdx, GuidIdx, GuidIdx, GuidIdx },
            /* 0x01 TypeRef */ new[] { C(CodedResolutionScope), StringIdx, StringIdx },
            /* 0x02 TypeDef */ new[] { Fixed4, StringIdx, StringIdx, C(CodedTypeDefOrRef), T(0x04), T(0x06) },
            /* 0x03 FieldPtr */ new[] { T(0x04) },
            /* 0x04 Field */ new[] { Fixed2, StringIdx, BlobIdx },
            /* 0x05 MethodPtr */ new[] { T(0x06) },
            /* 0x06 MethodDef */ new[] { Fixed4, Fixed2, Fixed2, StringIdx, BlobIdx, T(0x08) },
            /* 0x07 ParamPtr */ new[] { T(0x08) },
            /* 0x08 Param */ new[] { Fixed2, Fixed2, StringIdx },
            /* 0x09 InterfaceImpl */ new[] { T(0x02), C(CodedTypeDefOrRef) },
            /* 0x0A MemberRef */ new[] { C(CodedMemberRefParent), StringIdx, BlobIdx },
            /* 0x0B Constant */ new[] { Fixed1, Fixed1, C(CodedHasConstant), BlobIdx },
            /* 0x0C CustomAttribute */ new[] { C(CodedHasCustomAttribute), C(CodedCustomAttributeType), BlobIdx },
            /* 0x0D FieldMarshal */ new[] { C(CodedHasFieldMarshal), BlobIdx },
            /* 0x0E DeclSecurity */ new[] { Fixed2, C(CodedHasDeclSecurity), BlobIdx },
            /* 0x0F ClassLayout */ new[] { Fixed2, Fixed4, T(0x02) },
            /* 0x10 FieldLayout */ new[] { Fixed4, T(0x04) },
            /* 0x11 StandAloneSig */ new[] { BlobIdx },
            /* 0x12 EventMap */ new[] { T(0x02), T(0x14) },
            /* 0x13 EventPtr */ new[] { T(0x14) },
            /* 0x14 Event */ new[] { Fixed2, StringIdx, C(CodedTypeDefOrRef) },
            /* 0x15 PropertyMap */ new[] { T(0x02), T(0x17) },
            /* 0x16 PropertyPtr */ new[] { T(0x17) },
            /* 0x17 Property */ new[] { Fixed2, StringIdx, BlobIdx },
            /* 0x18 MethodSemantics */ new[] { Fixed2, T(0x06), C(CodedHasSemantics) },
            /* 0x19 MethodImpl */ new[] { T(0x02), C(CodedMethodDefOrRef), C(CodedMethodDefOrRef) },
            /* 0x1A ModuleRef */ new[] { StringIdx },
            /* 0x1B TypeSpec */ new[] { BlobIdx },
            /* 0x1C ImplMap */ new[] { Fixed2, C(CodedMemberForwarded), StringIdx, T(0x1A) },
            /* 0x1D FieldRVA */ new[] { Fixed4, T(0x04) },
            /* 0x1E EncLog */ new[] { Fixed4, Fixed4 },
            /* 0x1F EncMap */ new[] { Fixed4 },
            /* 0x20 Assembly */ new[] { Fixed4, Fixed2, Fixed2, Fixed2, Fixed2, Fixed4, BlobIdx, StringIdx, StringIdx },
            /* 0x21 AssemblyProcessor */ new[] { Fixed4 },
            /* 0x22 AssemblyOS */ new[] { Fixed4, Fixed4, Fixed4 },
            /* 0x23 AssemblyRef */ new[] { Fixed2, Fixed2, Fixed2, Fixed2, Fixed4, BlobIdx, StringIdx, StringIdx, BlobIdx },
            /* 0x24 AssemblyRefProcessor */ new[] { Fixed4, T(0x23) },
            /* 0x25 AssemblyRefOS */ new[] { Fixed4, Fixed4, Fixed4, T(0x23) },
            /* 0x26 File */ new[] { Fixed4, StringIdx, BlobIdx },
            /* 0x27 ExportedType */ new[] { Fixed4, Fixed4, StringIdx, StringIdx, C(CodedImplementation) },
            /* 0x28 ManifestResource */ new[] { Fixed4, Fixed4, StringIdx, C(CodedImplementation) },
            /* 0x29 NestedClass */ new[] { T(0x02), T(0x02) },
            /* 0x2A GenericParam */ new[] { Fixed2, Fixed2, C(CodedTypeOrMethodDef), StringIdx },
            /* 0x2B MethodSpec */ new[] { C(CodedMethodDefOrRef), BlobIdx },
            /* 0x2C GenericParamConstraint */ new[] { T(0x2A), C(CodedTypeDefOrRef) }
        };

        // 常用欄位的位置（對應上面的 Schema）
        public const int TypeRefScope = 0, TypeRefName = 1, TypeRefNamespace = 2;
        public const int TypeDefFlags = 0, TypeDefName = 1, TypeDefNamespace = 2, TypeDefExtends = 3, TypeDefFieldList = 4, TypeDefMethodList = 5;
        public const int FieldFlags = 0, FieldName = 1, FieldSignature = 2;
        public const int MethodRva = 0, MethodImplFlags = 1, MethodFlags = 2, MethodName = 3, MethodSignature = 4, MethodParamList = 5;
        public const int MemberRefParent = 0, MemberRefName = 1, MemberRefSignature = 2;
        public const int StandAloneSigBlob = 0;
        public const int ModuleRefName = 0;
        public const int TypeSpecBlob = 0;
        public const int AssemblyMajor = 1, AssemblyMinor = 2, AssemblyBuild = 3, AssemblyRevision = 4, AssemblyName = 7;
        public const int AssemblyRefMajor = 0, AssemblyRefMinor = 1, AssemblyRefBuild = 2, AssemblyRefRevision = 3, AssemblyRefName = 6;
        public const int FileName = 1;
        public const int NestedClassNested = 0, NestedClassEnclosing = 1;
        public const int GenericParamNumber = 0, GenericParamOwner = 2, GenericParamName = 3;
        public const int MethodSpecMethod = 0, MethodSpecInstantiation = 1;

        /// <summary>訊息數量上限（格式極度損毀時避免訊息無限增加）。</summary>
        private const int MaxMessages = 50;

        private readonly byte[] _data;
        private readonly List<string> _messages;
        private readonly HashSet<string> _messageSet = new HashSet<string>(StringComparer.Ordinal);

        private int _stringsOffset, _stringsSize;
        private int _usOffset, _usSize;
        private int _blobOffset, _blobSize;
        private int _guidOffset, _guidSize;

        private readonly long[] _declaredRows = new long[64];
        private readonly int[] _rows = new int[64];
        private readonly int[] _rowSize = new int[64];
        private readonly long[] _tableOffset = new long[64];
        private readonly int[][] _colOffset = new int[LastKnownTable + 1][];
        private readonly int[][] _colSize = new int[LastKnownTable + 1][];
        private int _stringIndexSize = 2, _guidIndexSize = 2, _blobIndexSize = 2;
        private bool _forceLargeIndexes;

        private readonly Dictionary<uint, string> _stringCache = new Dictionary<uint, string>();

        private ClrMetadataReader(byte[] data, List<string> messages)
        {
            _data = data;
            _messages = messages;
        }

        public byte[] Data
        {
            get { return _data; }
        }

        public ushort RuntimeMajorVersion { get; private set; }
        public ushort RuntimeMinorVersion { get; private set; }
        public uint MetadataRva { get; private set; }
        public uint MetadataSize { get; private set; }

        /// <summary>CLR 標頭旗標，例如 0x01 = ILONLY、0x10 = 原生進入點。</summary>
        public uint ClrFlags { get; private set; }

        /// <summary>進入點的中繼資料語彙基元（通常是 0x06xxxxxx 的 MethodDef）；旗標 0x10 時是原生程式碼的 RVA。</summary>
        public uint EntryPointToken { get; private set; }

        /// <summary>中繼資料版本字串，例如 "v4.0.30319"。</summary>
        public string MetadataVersion { get; private set; }

        /// <summary>是否成功讀到資料表串流。</summary>
        public bool HasTables { get; private set; }

        /// <summary>資料表串流是不是未壓縮的 "#-"（編譯中或編輯後繼續的格式）。</summary>
        public bool IsUncompressedTables { get; private set; }

        public bool HasNativeEntryPoint
        {
            get { return (ClrFlags & 0x10) != 0; }
        }

        // ------------------------------------------------------------------
        // 開啟
        // ------------------------------------------------------------------

        /// <summary>讀取 CLR 標頭與中繼資料；完全無法讀取時回傳 null（原因寫在 messages）。</summary>
        public static ClrMetadataReader Open(LoadedBinary binary, List<string> messages)
        {
            if (binary == null || binary.Data == null) return null;
            var reader = new ClrMetadataReader(binary.Data, messages);
            return reader.Initialize(binary) ? reader : null;
        }

        private bool Initialize(LoadedBinary binary)
        {
            int clr;
            if (!binary.TryRvaToOffset(binary.ClrHeaderRva, out clr) || !InData(clr, 0x48))
            {
                AddMessage("CLR 標頭（RVA 0x" + Hex(binary.ClrHeaderRva) + "）不在檔案範圍內，無法讀取 .NET 中繼資料。");
                return false;
            }
            RuntimeMajorVersion = U16(clr + 4);
            RuntimeMinorVersion = U16(clr + 6);
            MetadataRva = U32(clr + 8);
            MetadataSize = U32(clr + 12);
            ClrFlags = U32(clr + 16);
            EntryPointToken = U32(clr + 20);

            int root;
            if (MetadataRva == 0 || !binary.TryRvaToOffset(MetadataRva, out root) || !InData(root, 20))
            {
                AddMessage("中繼資料（RVA 0x" + Hex(MetadataRva) + "）不在檔案範圍內。");
                return false;
            }
            if (U32(root) != 0x424A5342u)
            {
                AddMessage("中繼資料開頭不是 \"BSJB\" 簽章，檔案可能已損毀或被加殼／混淆。");
                return false;
            }
            int rootEnd = MetadataSize > 0 && (long)root + MetadataSize <= _data.Length ? root + (int)MetadataSize : _data.Length;
            if (MetadataSize == 0 || (long)root + MetadataSize > _data.Length)
                AddMessage("CLR 標頭記錄的中繼資料大小（" + MetadataSize + "）不合理，改以檔案結尾為界。");

            uint versionLength = U32(root + 12);
            if (versionLength > 255 || !InData(root + 16, (int)versionLength))
            {
                AddMessage("中繼資料版本字串長度 " + versionLength + " 不合理。");
                return false;
            }
            MetadataVersion = ReadAscii(root + 16, (int)versionLength);
            int p = root + 16 + (int)((versionLength + 3) & ~3u);
            if (!InData(p, 4))
            {
                AddMessage("中繼資料根被截斷，找不到串流標頭。");
                return false;
            }
            int streamCount = U16(p + 2);
            p += 4;
            if (streamCount > 32)
            {
                AddMessage("串流數量 " + streamCount + " 不合理，只讀取前 32 個。");
                streamCount = 32;
            }

            int tablesOffset = -1, tablesSize = 0;
            bool hasJtd = false;
            for (int i = 0; i < streamCount; i++)
            {
                if (!InData(p, 8))
                {
                    AddMessage("串流標頭被截斷。");
                    break;
                }
                uint offset = U32(p);
                uint size = U32(p + 4);
                int nameStart = p + 8;
                int nameLength = 0;
                while (nameLength < 32 && nameStart + nameLength < _data.Length && _data[nameStart + nameLength] != 0) nameLength++;
                string name = ReadAscii(nameStart, nameLength);
                p = nameStart + ((nameLength + 4) & ~3);

                long start = (long)root + offset;
                if (offset >= (uint)_data.Length || start >= _data.Length)
                {
                    AddMessage("串流 " + name + " 的位置超出檔案範圍，已略過。");
                    continue;
                }
                long end = start + size;
                if (end > _data.Length)
                {
                    AddMessage("串流 " + name + " 超出檔案結尾，已截短。");
                    end = _data.Length;
                }
                else if (end > rootEnd && rootEnd < _data.Length)
                {
                    AddMessage("串流 " + name + " 超出中繼資料範圍。");
                }
                int s = (int)start;
                int len = (int)(end - start);
                switch (name)
                {
                    case "#~":
                    case "#-":
                        if (tablesOffset < 0)
                        {
                            tablesOffset = s;
                            tablesSize = len;
                            IsUncompressedTables = name == "#-";
                        }
                        break;
                    case "#Strings":
                        if (_stringsSize == 0) { _stringsOffset = s; _stringsSize = len; }
                        break;
                    case "#US":
                        if (_usSize == 0) { _usOffset = s; _usSize = len; }
                        break;
                    case "#Blob":
                        if (_blobSize == 0) { _blobOffset = s; _blobSize = len; }
                        break;
                    case "#GUID":
                        if (_guidSize == 0) { _guidOffset = s; _guidSize = len; }
                        break;
                    case "#JTD":
                        hasJtd = true;
                        break;
                }
            }
            _forceLargeIndexes = hasJtd;

            if (tablesOffset < 0)
            {
                AddMessage("找不到中繼資料資料表串流（#~），無法列出型別與方法。");
                return true;
            }
            ReadTableStream(tablesOffset, tablesSize);
            return true;
        }

        private void ReadTableStream(int start, int size)
        {
            int end = start + size;
            if (size < 24)
            {
                AddMessage("資料表串流太小（" + size + " 位元組）。");
                return;
            }
            byte heapSizes = _data[start + 6];
            ulong valid = U64(start + 8);
            int p = start + 24;
            for (int t = 0; t < 64; t++)
            {
                if ((valid & (1UL << t)) == 0) continue;
                if (p + 4 > end)
                {
                    AddMessage("資料表列數陣列被截斷。");
                    valid &= (1UL << t) - 1;
                    break;
                }
                _declaredRows[t] = U32(p);
                p += 4;
            }
            // 部分混淆器會在列數之後多放 4 個位元組（HeapSizes 的 0x40 位元），CLR 也接受
            if ((heapSizes & 0x40) != 0) p += 4;

            _stringIndexSize = (heapSizes & 0x01) != 0 || _forceLargeIndexes ? 4 : 2;
            _guidIndexSize = (heapSizes & 0x02) != 0 || _forceLargeIndexes ? 4 : 2;
            _blobIndexSize = (heapSizes & 0x04) != 0 || _forceLargeIndexes ? 4 : 2;

            for (int t = 0; t <= LastKnownTable; t++)
            {
                var columns = Schema[t];
                var offsets = new int[columns.Length];
                var sizes = new int[columns.Length];
                int rowSize = 0;
                for (int c = 0; c < columns.Length; c++)
                {
                    offsets[c] = rowSize;
                    sizes[c] = ColumnSize(columns[c]);
                    rowSize += sizes[c];
                }
                _colOffset[t] = offsets;
                _colSize[t] = sizes;
                _rowSize[t] = rowSize;
            }

            long cursor = p;
            for (int t = 0; t < 64; t++)
            {
                if ((valid & (1UL << t)) == 0 || _declaredRows[t] == 0) continue;
                if (t > LastKnownTable)
                {
                    AddMessage("資料表串流含有未知的資料表 0x" + t.ToString("X2", CultureInfo.InvariantCulture) + "，之後的資料表無法定位（已讀取的部分仍可使用）。");
                    break;
                }
                _tableOffset[t] = cursor;
                long bytes = _declaredRows[t] * _rowSize[t];
                long available = Math.Max(0L, end - cursor);
                long rows = _declaredRows[t];
                if (bytes > available)
                {
                    rows = available / _rowSize[t];
                    AddMessage("資料表 0x" + t.ToString("X2", CultureInfo.InvariantCulture) + " 宣告 " + _declaredRows[t] + " 列，但資料只夠 " + rows + " 列，已截短。");
                }
                // 語彙基元的列號只有 24 位元
                _rows[t] = (int)Math.Min(rows, 0xFFFFFFL);
                cursor += bytes;
            }
            HasTables = true;
        }

        private int ColumnSize(int kind)
        {
            if (kind == Fixed1) return 1;
            if (kind == Fixed2) return 2;
            if (kind == Fixed4) return 4;
            if (kind == StringIdx) return _stringIndexSize;
            if (kind == GuidIdx) return _guidIndexSize;
            if (kind == BlobIdx) return _blobIndexSize;
            if (_forceLargeIndexes) return 4;
            if (kind >= CodedBase)
            {
                int coded = kind - CodedBase;
                long limit = 1L << (16 - CodedTagBits[coded]);
                foreach (int table in CodedTables[coded])
                    if (table >= 0 && _declaredRows[table] >= limit) return 4;
                return 2;
            }
            return _declaredRows[kind - SimpleBase] > 0xFFFF ? 4 : 2;
        }

        // ------------------------------------------------------------------
        // 資料表存取
        // ------------------------------------------------------------------

        /// <summary>資料表實際可讀的列數（資料被截斷時比宣告的少）。</summary>
        public int RowCount(int table)
        {
            return table >= 0 && table < 64 ? _rows[table] : 0;
        }

        /// <summary>讀取第 row 列（從 1 開始）的第 column 欄；超出範圍時回傳 0。</summary>
        public uint GetColumn(int table, int row, int column)
        {
            if (table < 0 || table > LastKnownTable || row < 1 || row > _rows[table]) return 0;
            var offsets = _colOffset[table];
            if (offsets == null || column < 0 || column >= offsets.Length) return 0;
            long o = _tableOffset[table] + (long)(row - 1) * _rowSize[table] + offsets[column];
            int size = _colSize[table][column];
            if (o < 0 || o + size > _data.Length) return 0;
            int i = (int)o;
            switch (size)
            {
                case 1: return _data[i];
                case 2: return (uint)(_data[i] | (_data[i + 1] << 8));
                default: return (uint)(_data[i] | (_data[i + 1] << 8) | (_data[i + 2] << 16) | (_data[i + 3] << 24));
            }
        }

        /// <summary>把編碼索引欄位解成語彙基元（資料表 &lt;&lt; 24 | 列號）；空值或無效時回傳 0。</summary>
        public int GetCodedToken(int table, int row, int column, int codedKind)
        {
            return DecodeCodedIndex(GetColumn(table, row, column), codedKind);
        }

        public static int DecodeCodedIndex(uint value, int codedKind)
        {
            if (codedKind < 0 || codedKind >= CodedTables.Length) return 0;
            int bits = CodedTagBits[codedKind];
            uint tag = value & ((1u << bits) - 1);
            uint row = value >> bits;
            var tables = CodedTables[codedKind];
            if (tag >= tables.Length || tables[tag] < 0 || row == 0 || row > 0xFFFFFF) return 0;
            return (tables[tag] << 24) | (int)row;
        }

        public string GetStringColumn(int table, int row, int column)
        {
            return GetString(GetColumn(table, row, column));
        }

        // ------------------------------------------------------------------
        // 堆積
        // ------------------------------------------------------------------

        /// <summary>#Strings 堆積中的 UTF-8 字串；索引無效時回傳空字串。</summary>
        public string GetString(uint index)
        {
            if (index == 0 || index >= (uint)_stringsSize) return string.Empty;
            string cached;
            if (_stringCache.TryGetValue(index, out cached)) return cached;
            int start = _stringsOffset + (int)index;
            int end = _stringsOffset + _stringsSize;
            int i = start;
            // 名稱長度上限 4096 位元組，避免沒有結尾 0 的損毀資料
            while (i < end && _data[i] != 0 && i - start < 4096) i++;
            string s = Encoding.UTF8.GetString(_data, start, i - start);
            if (_stringCache.Count < 200000) _stringCache[index] = s;
            return s;
        }

        /// <summary>#US 堆積中的使用者字串（ldstr 的字串）；無效時回傳 null。</summary>
        public string GetUserString(uint index, int maxChars)
        {
            if (index == 0 || index >= (uint)_usSize) return index == 0 && _usSize > 0 ? string.Empty : null;
            int p = _usOffset + (int)index;
            int end = _usOffset + _usSize;
            int length;
            if (!TryReadCompressed(_data, ref p, end, out length)) return null;
            if (length > end - p) return null;
            int chars = length / 2;
            if (chars > maxChars) chars = maxChars;
            var sb = new StringBuilder(chars);
            for (int i = 0; i < chars; i++) sb.Append((char)(_data[p + i * 2] | (_data[p + i * 2 + 1] << 8)));
            return sb.ToString();
        }

        /// <summary>#US 堆積中某字串的字元數（不讀內容）；無效時回傳 -1。</summary>
        public int GetUserStringLength(uint index)
        {
            if (index == 0) return 0;
            if (index >= (uint)_usSize) return -1;
            int p = _usOffset + (int)index;
            int end = _usOffset + _usSize;
            int length;
            if (!TryReadCompressed(_data, ref p, end, out length) || length > end - p) return -1;
            return length / 2;
        }

        /// <summary>#Blob 堆積中的資料位置；無效時回傳 false。</summary>
        public bool TryGetBlob(uint index, out int offset, out int length)
        {
            offset = 0;
            length = 0;
            if (index >= (uint)_blobSize || _blobSize == 0) return false;
            int p = _blobOffset + (int)index;
            int end = _blobOffset + _blobSize;
            int len;
            if (!TryReadCompressed(_data, ref p, end, out len)) return false;
            if (len > end - p) len = end - p;
            offset = p;
            length = len;
            return true;
        }

        /// <summary>開啟一個 Blob 供簽章解碼；無效時回傳的游標是空的（讀取會失敗）。</summary>
        public ClrBlobCursor OpenBlob(uint index)
        {
            int offset, length;
            if (!TryGetBlob(index, out offset, out length)) return new ClrBlobCursor(_data, 0, 0);
            return new ClrBlobCursor(_data, offset, offset + length);
        }

        public Guid GetGuid(uint index)
        {
            if (index == 0) return Guid.Empty;
            long o = _guidOffset + (long)(index - 1) * 16;
            if (o + 16 > _guidOffset + (long)_guidSize || o + 16 > _data.Length) return Guid.Empty;
            var bytes = new byte[16];
            Buffer.BlockCopy(_data, (int)o, bytes, 0, 16);
            return new Guid(bytes);
        }

        // ------------------------------------------------------------------
        // 共用工具
        // ------------------------------------------------------------------

        /// <summary>讀取 ECMA-335 壓縮無號整數（1、2 或 4 位元組）。</summary>
        public static bool TryReadCompressed(byte[] data, ref int p, int end, out int value)
        {
            value = 0;
            if (p >= end || p >= data.Length || p < 0) return false;
            byte b = data[p];
            if ((b & 0x80) == 0)
            {
                value = b;
                p += 1;
                return true;
            }
            if ((b & 0xC0) == 0x80)
            {
                if (p + 2 > end || p + 2 > data.Length) return false;
                value = ((b & 0x3F) << 8) | data[p + 1];
                p += 2;
                return true;
            }
            if ((b & 0xE0) == 0xC0)
            {
                if (p + 4 > end || p + 4 > data.Length) return false;
                value = ((b & 0x1F) << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3];
                p += 4;
                return true;
            }
            return false;
        }

        public void AddMessage(string message)
        {
            if (_messages == null || _messages.Count >= MaxMessages) return;
            if (_messageSet.Add(message)) _messages.Add(message);
        }

        private bool InData(long offset, int count)
        {
            return offset >= 0 && count >= 0 && offset + count <= _data.Length;
        }

        private ushort U16(int o)
        {
            return InData(o, 2) ? (ushort)(_data[o] | (_data[o + 1] << 8)) : (ushort)0;
        }

        private uint U32(int o)
        {
            return InData(o, 4) ? (uint)(_data[o] | (_data[o + 1] << 8) | (_data[o + 2] << 16) | (_data[o + 3] << 24)) : 0u;
        }

        private ulong U64(int o)
        {
            return U32(o) | ((ulong)U32(o + 4) << 32);
        }

        private string ReadAscii(int offset, int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count && offset + i < _data.Length; i++)
            {
                byte b = _data[offset + i];
                if (b == 0) break;
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '?');
            }
            return sb.ToString();
        }

        private static string Hex(uint value)
        {
            return value.ToString("X", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Blob（簽章）讀取游標：所有讀取都檢查邊界，失敗時設定 Failed 而不丟例外。</summary>
    internal struct ClrBlobCursor
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _pos;
        private bool _failed;

        public ClrBlobCursor(byte[] data, int start, int end)
        {
            _data = data;
            _pos = start;
            _end = data == null ? start : Math.Min(end, data.Length);
            _failed = false;
        }

        public bool Failed
        {
            get { return _failed; }
        }

        public int Remaining
        {
            get { return Math.Max(0, _end - _pos); }
        }

        public void Fail()
        {
            _failed = true;
        }

        public bool TryPeek(out byte value)
        {
            value = 0;
            if (_failed || _data == null || _pos >= _end) return false;
            value = _data[_pos];
            return true;
        }

        public byte ReadByte()
        {
            if (_failed || _data == null || _pos >= _end)
            {
                _failed = true;
                return 0;
            }
            return _data[_pos++];
        }

        /// <summary>壓縮無號整數；失敗時回傳 0 並設定 Failed。</summary>
        public int ReadCompressed()
        {
            int value;
            if (_failed || _data == null || !ClrMetadataReader.TryReadCompressed(_data, ref _pos, _end, out value))
            {
                _failed = true;
                return 0;
            }
            return value;
        }

        /// <summary>壓縮有號整數（陣列下界用，II.23.2）。</summary>
        public int ReadCompressedSigned()
        {
            if (_failed || _data == null || _pos >= _end)
            {
                _failed = true;
                return 0;
            }
            byte first = _data[_pos];
            int raw = ReadCompressed();
            if (_failed) return 0;
            bool negative = (raw & 1) != 0;
            raw >>= 1;
            if (!negative) return raw;
            if ((first & 0x80) == 0) return raw - 0x40;
            if ((first & 0xC0) == 0x80) return raw - 0x2000;
            return raw - 0x10000000;
        }

        /// <summary>TypeDefOrRefOrSpecEncoded（II.23.2.8）→ 語彙基元；失敗時回傳 0。</summary>
        public int ReadTypeDefOrRefToken()
        {
            int coded = ReadCompressed();
            if (_failed) return 0;
            int token = ClrMetadataReader.DecodeCodedIndex((uint)coded, ClrMetadataReader.CodedTypeDefOrRef);
            if (token == 0) _failed = true;
            return token;
        }
    }
}
