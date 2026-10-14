using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace Assam.Core.Binary
{
    /// <summary>.NET 組件的 IL（中間語言）反組譯器。只讀取檔案，不會載入或執行組件。</summary>
    public static class CilDisassembler
    {
        public static CilDisassemblyResult Disassemble(LoadedBinary binary, int maxMethods = 5000)
        {
            var result = new CilDisassemblyResult();
            try
            {
                if (binary == null || binary.Data == null)
                {
                    result.Messages.Add("沒有載入任何檔案。");
                    return result;
                }
                if (!binary.IsDotNet)
                {
                    result.Messages.Add("這個檔案不是 .NET 組件（沒有 CLR 標頭），裡面沒有 IL 中間語言可以反組譯；請改看 x86 組合語言的反組譯結果。");
                    return result;
                }
                var metadata = ClrMetadataReader.Open(binary, result.Messages);
                if (metadata == null)
                {
                    result.Messages.Add("無法讀取 .NET 中繼資料，沒有 IL 可以反組譯（檔案可能已損毀、被加殼或混淆）。");
                    return result;
                }
                new CilWorker(binary, metadata, result, maxMethods).Run();
            }
            catch (Exception ex)
            {
                // 最後一道防線：任何意外都不能讓呼叫端當掉，已完成的部分照樣回傳
                result.Messages.Add("反組譯 IL 時發生未預期的錯誤（" + ex.GetType().Name + "：" + ex.Message + "），以下只列出已完成的部分。");
            }
            return result;
        }
    }

    /// <summary>實際執行一次 IL 反組譯的工作物件。</summary>
    internal sealed class CilWorker
    {
        /// <summary>單一方法最多解碼的 IL 位元組數（損毀的 CodeSize 不會讓程式卡住）。</summary>
        private const int MaxMethodBodyBytes = 1024 * 1024;

        /// <summary>最多列出的型別數。</summary>
        private const int MaxTypes = 100000;

        /// <summary>最多列出的組件參考數。</summary>
        private const int MaxAssemblyRefs = 10000;

        /// <summary>例外處理子句的上限（每個方法）。</summary>
        private const int MaxClauses = 4096;

        /// <summary>.locals 註解最多列出幾個區域變數。</summary>
        private const int MaxLocalsShown = 64;

        private readonly LoadedBinary _binary;
        private readonly byte[] _data;
        private readonly ClrMetadataReader _md;
        private readonly CilDisassemblyResult _result;
        private readonly int _maxMethods;
        private readonly CilFormatter _f;
        private readonly StringBuilder _sb = new StringBuilder(128);
        private long _lineBudget;
        private bool _budgetMessage;

        // 有問題的方法只統計數量並記住第一個例子，最後彙整成一則訊息（避免訊息洗版）
        private int _failedMethods, _badRvaMethods, _badHeaderMethods;
        private string _failedExample, _badRvaExample, _badHeaderExample;

        public CilWorker(LoadedBinary binary, ClrMetadataReader md, CilDisassemblyResult result, int maxMethods)
        {
            _binary = binary;
            _data = binary.Data;
            _md = md;
            _result = result;
            _maxMethods = Math.Max(0, maxMethods);
            // 總行數上限隨方法上限放大（預設 5000 個方法約 100 萬行），避免低階電腦記憶體不足
            _lineBudget = Math.Max(200000L, Math.Min((long)_maxMethods * 200L, 100000000L));
            _f = new CilFormatter(md);
        }

        public void Run()
        {
            _result.MetadataVersion = _md.MetadataVersion;
            if (!_md.HasTables) return;
            try
            {
                ReadAssemblyInfo();
                ReadEntryPoint();
                ReadTypes();
            }
            finally
            {
                if (_badRvaMethods > 0)
                    _result.Messages.Add("有 " + _badRvaMethods + " 個方法主體的 RVA 不在檔案範圍內（例如 " + _badRvaExample + "）。");
                if (_badHeaderMethods > 0)
                    _result.Messages.Add("有 " + _badHeaderMethods + " 個方法主體標頭格式不正確（例如 " + _badHeaderExample + "）。");
                if (_failedMethods > 0)
                    _result.Messages.Add("有 " + _failedMethods + " 個方法無法完整解碼（例如 " + _failedExample + "），結果可能不完整。");
            }
        }

        private static void Count(ref int counter, ref string example, CilMethodListing m)
        {
            counter++;
            if (example == null) example = m.TypeName + "::" + m.Name;
        }

        // ------------------------------------------------------------------
        // 組件資訊
        // ------------------------------------------------------------------

        private void ReadAssemblyInfo()
        {
            const int asm = ClrMetadataReader.TableAssembly;
            if (_md.RowCount(asm) > 0)
            {
                _result.AssemblyName = _md.GetStringColumn(asm, 1, ClrMetadataReader.AssemblyName);
                _result.AssemblyVersion = Version(asm, 1, ClrMetadataReader.AssemblyMajor);
            }
            else
            {
                _result.AssemblyName = _md.GetStringColumn(ClrMetadataReader.TableModule, 1, 1);
                _result.Messages.Add("這是 .NET 模組（沒有組件資訊表，例如 .netmodule），只列出其中的型別與方法。");
            }

            const int asmRef = ClrMetadataReader.TableAssemblyRef;
            int refs = _md.RowCount(asmRef);
            if (refs > MaxAssemblyRefs)
            {
                _result.Messages.Add("組件參考有 " + refs + " 個，只列出前 " + MaxAssemblyRefs + " 個。");
                refs = MaxAssemblyRefs;
            }
            for (int i = 1; i <= refs; i++)
            {
                string name = _md.GetStringColumn(asmRef, i, ClrMetadataReader.AssemblyRefName);
                _result.AssemblyReferences.Add((name.Length == 0 ? "(無名)" : name) + " " + Version(asmRef, i, ClrMetadataReader.AssemblyRefMajor));
            }
        }

        private string Version(int table, int row, int firstColumn)
        {
            return _md.GetColumn(table, row, firstColumn).ToString(CultureInfo.InvariantCulture) + "." +
                   _md.GetColumn(table, row, firstColumn + 1).ToString(CultureInfo.InvariantCulture) + "." +
                   _md.GetColumn(table, row, firstColumn + 2).ToString(CultureInfo.InvariantCulture) + "." +
                   _md.GetColumn(table, row, firstColumn + 3).ToString(CultureInfo.InvariantCulture);
        }

        private void ReadEntryPoint()
        {
            uint token = _md.EntryPointToken;
            if (token == 0) return;
            if (_md.HasNativeEntryPoint)
            {
                _result.Messages.Add("進入點是原生機器碼（RVA 0x" + token.ToString("X", CultureInfo.InvariantCulture) + "），不是 IL 方法。");
                return;
            }
            int table = (int)(token >> 24);
            int row = (int)(token & 0xFFFFFF);
            if (table == ClrMetadataReader.TableMethodDef && row >= 1 && row <= _md.RowCount(table))
            {
                _result.EntryPoint = _f.MethodDefFullName(row);
            }
            else if (table == ClrMetadataReader.TableFile && row >= 1 && row <= _md.RowCount(table))
            {
                _result.Messages.Add("進入點在另一個模組檔案「" + _md.GetStringColumn(table, row, ClrMetadataReader.FileName) + "」中。");
            }
            else
            {
                _result.Messages.Add("CLR 標頭記錄的進入點語彙基元 0x" + token.ToString("X8", CultureInfo.InvariantCulture) + " 無效。");
            }
        }

        // ------------------------------------------------------------------
        // 型別與方法
        // ------------------------------------------------------------------

        private void ReadTypes()
        {
            const int typeDef = ClrMetadataReader.TableTypeDef;
            const int methodDef = ClrMetadataReader.TableMethodDef;
            int typeCount = _md.RowCount(typeDef);
            if (typeCount > MaxTypes)
            {
                _result.Messages.Add("型別有 " + typeCount + " 個，只列出前 " + MaxTypes + " 個。");
                typeCount = MaxTypes;
            }
            int methodRows = _md.RowCount(methodDef);
            for (int t = 1; t <= typeCount; t++)
            {
                int start = _f.MethodListStart(t);
                int end = _f.MethodListEnd(t);
                string ns, name;
                _f.GetTypeDefParts(t, out ns, out name);
                // <Module> 是放全域函式的虛擬型別，沒有方法時不列出
                if (t == 1 && end <= start && name == "<Module>") continue;
                var type = new CilTypeListing { Namespace = ns, Name = name };
                _result.Types.Add(type);
                string typeName = type.FullName;
                for (int i = start; i < end; i++)
                {
                    int row = _f.MethodListEntry(i);
                    if (row < 1 || row > methodRows) continue;
                    if (_result.MethodCount >= _maxMethods)
                    {
                        _result.Truncated = true;
                        _result.Messages.Add("方法數量超過上限（" + _maxMethods + " 個），只反組譯前 " + _maxMethods + " 個方法。");
                        return;
                    }
                    type.Methods.Add(ReadMethod(typeName, row));
                    _result.MethodCount++;
                }
            }
        }

        private CilMethodListing ReadMethod(string typeName, int row)
        {
            const int methodDef = ClrMetadataReader.TableMethodDef;
            var m = new CilMethodListing
            {
                TypeName = typeName,
                Name = _md.GetStringColumn(methodDef, row, ClrMetadataReader.MethodName),
                Rva = _md.GetColumn(methodDef, row, ClrMetadataReader.MethodRva),
                MetadataToken = (methodDef << 24) | row
            };
            m.HasBody = m.Rva != 0;
            try
            {
                m.Signature = _f.MethodDefSignature(row);
                if (m.HasBody) DecodeBody(m, _md.GetColumn(methodDef, row, ClrMetadataReader.MethodImplFlags));
            }
            catch (Exception ex)
            {
                // 單一方法失敗不影響其他方法
                m.Lines.Add("// 解碼這個方法時發生錯誤：" + ex.Message);
                Count(ref _failedMethods, ref _failedExample, m);
            }
            return m;
        }

        private void DecodeBody(CilMethodListing m, uint implFlags)
        {
            uint codeType = implFlags & 0x3;
            if (codeType == 1)
            {
                m.Lines.Add("// 這是原生（native）方法：RVA 指向機器碼而不是 IL，請用 x86 反組譯檢視 RVA 0x" + m.Rva.ToString("X", CultureInfo.InvariantCulture) + "。");
                return;
            }
            if (codeType == 3)
            {
                m.Lines.Add("// 這是由執行階段（runtime）提供實作的方法，沒有 IL 主體。");
                return;
            }
            int offset;
            if (!_binary.TryRvaToOffset(m.Rva, out offset) || offset < 0 || offset >= _data.Length)
            {
                m.Lines.Add("// 方法主體的 RVA 0x" + m.Rva.ToString("X", CultureInfo.InvariantCulture) + " 不在檔案範圍內。");
                Count(ref _badRvaMethods, ref _badRvaExample, m);
                return;
            }

            byte first = _data[offset];
            int codeStart;
            long declaredSize;
            uint localSig = 0;
            bool moreSections = false, initLocals = false;
            if ((first & 0x3) == 0x2)
            {
                // tiny 標頭：1 個位元組，高 6 位元是程式碼大小，.maxstack 固定為 8
                declaredSize = first >> 2;
                m.MaxStack = 8;
                codeStart = offset + 1;
            }
            else if ((first & 0x3) == 0x3)
            {
                // fat 標頭：12 個位元組
                if (offset + 12 > _data.Length)
                {
                    m.Lines.Add("// 方法主體的 fat 標頭被截斷。");
                    return;
                }
                int flags = U16(offset);
                int headerSize = (flags >> 12) * 4;
                if (headerSize < 12) headerSize = 12;
                m.MaxStack = U16(offset + 2);
                declaredSize = U32(offset + 4);
                localSig = U32(offset + 8);
                moreSections = (flags & 0x08) != 0;
                initLocals = (flags & 0x10) != 0;
                codeStart = offset + headerSize;
            }
            else
            {
                m.Lines.Add("// 方法主體標頭 0x" + first.ToString("X2", CultureInfo.InvariantCulture) + " 格式不正確（不是 tiny 或 fat 格式）。");
                Count(ref _badHeaderMethods, ref _badHeaderExample, m);
                return;
            }

            long available = Math.Max(0L, (long)_data.Length - codeStart);
            int codeSize = (int)Math.Min(declaredSize, available);
            if (declaredSize > available)
            {
                m.Lines.Add("// 程式碼大小 " + declaredSize + " 超出檔案結尾，只解碼檔案中有的 " + codeSize + " 個位元組。");
                moreSections = false;
            }
            m.CodeSize = codeSize;

            if (localSig != 0) DecodeLocals(m, localSig, initLocals);

            int decodeSize = Math.Min(codeSize, MaxMethodBodyBytes);
            DecodeInstructions(m, codeStart, decodeSize);
            if (decodeSize < codeSize)
                m.Lines.Add("// …方法太大，只解碼前 " + decodeSize + " 個位元組。");

            if (moreSections) DecodeExceptionSections(m, codeStart + (long)codeSize);
        }

        private void DecodeLocals(CilMethodListing m, uint token, bool initLocals)
        {
            List<string> types;
            int declared;
            if (!_f.TryReadLocals(token, out types, out declared))
            {
                m.Lines.Add("// .locals：區域變數簽章 0x" + token.ToString("X8", CultureInfo.InvariantCulture) + " 無法解碼。");
                return;
            }
            m.LocalCount = declared;
            if (declared == 0) return;
            _sb.Clear();
            _sb.Append("// .locals ").Append(initLocals ? "init " : string.Empty).Append('(');
            int shown = Math.Min(types.Count, MaxLocalsShown);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) _sb.Append(", ");
                _sb.Append(types[i]).Append(" V_").Append(i.ToString(CultureInfo.InvariantCulture));
            }
            if (declared > shown) _sb.Append(", …共 ").Append(declared.ToString(CultureInfo.InvariantCulture)).Append(" 個");
            _sb.Append(')');
            m.Lines.Add(_sb.ToString());
        }

        private void DecodeInstructions(CilMethodListing m, int codeStart, int codeSize)
        {
            int pos = 0;
            while (pos < codeSize)
            {
                if (_lineBudget <= 0)
                {
                    m.Lines.Add("// …已達總行數上限，其餘指令省略。");
                    if (!_budgetMessage)
                    {
                        _budgetMessage = true;
                        _result.Truncated = true;
                        _result.Messages.Add("IL 指令總數超過上限，部分方法只顯示開頭（請減少一次反組譯的方法數）。");
                    }
                    return;
                }
                _lineBudget--;

                int start = pos;
                int b = _data[codeStart + pos++];
                CilOpInfo op;
                if (b == 0xFE)
                {
                    if (pos >= codeSize)
                    {
                        m.Lines.Add(Prefix(start) + ".byte 0xFE // 運算碼不完整（方法主體被截斷）");
                        return;
                    }
                    int b2 = _data[codeStart + pos++];
                    op = CilOpCodeTable.TwoByte(b2);
                    if (op == null)
                    {
                        m.Lines.Add(Prefix(start) + ".byte 0xFE, 0x" + b2.ToString("X2", CultureInfo.InvariantCulture) + " // 未知的 IL 運算碼");
                        continue;
                    }
                }
                else
                {
                    op = CilOpCodeTable.OneByte(b);
                    if (op == null)
                    {
                        m.Lines.Add(Prefix(start) + ".byte 0x" + b.ToString("X2", CultureInfo.InvariantCulture) + " // 未知的 IL 運算碼");
                        continue;
                    }
                }

                int size = op.OperandSize;
                if (size < 0)
                {
                    // switch：4 位元組的目標數量 N，接著 N 個 4 位元組的相對位移
                    if (pos + 4 > codeSize)
                    {
                        m.Lines.Add(Prefix(start) + op.Name + " // 運算元不完整（方法主體被截斷）");
                        return;
                    }
                    uint count = U32(codeStart + pos);
                    pos += 4;
                    if (count > (uint)((codeSize - pos) / 4))
                    {
                        m.Lines.Add(Prefix(start) + op.Name + " // 跳躍表有 " + count + " 個目標，超出方法主體");
                        return;
                    }
                    long baseOffset = pos + 4L * count;
                    _sb.Clear();
                    _sb.Append(Prefix(start)).Append(op.Name).Append(" (");
                    for (int i = 0; i < count; i++)
                    {
                        if (i > 0) _sb.Append(", ");
                        AppendLabel(_sb, baseOffset + (int)U32(codeStart + pos));
                        pos += 4;
                    }
                    _sb.Append(')');
                    m.Lines.Add(_sb.ToString());
                    continue;
                }
                if (pos + size > codeSize)
                {
                    m.Lines.Add(Prefix(start) + op.Name + " // 運算元不完整（方法主體被截斷）");
                    return;
                }
                string operand = FormatOperand(op, codeStart + pos, pos + size);
                pos += size;
                m.Lines.Add(operand == null ? Prefix(start) + op.Name : Prefix(start) + op.Name + " " + operand);
            }
        }

        /// <summary>格式化運算元；at 是運算元在檔案中的位置，next 是下一個指令的 IL 位移。</summary>
        private string FormatOperand(CilOpInfo op, int at, int next)
        {
            switch (op.Operand)
            {
                case OperandType.InlineNone:
                    return null;
                case OperandType.ShortInlineBrTarget:
                    return Label(next + (sbyte)_data[at]);
                case OperandType.InlineBrTarget:
                    return Label(next + (long)(int)U32(at));
                case OperandType.ShortInlineI:
                    // ldc.i4.s 是有號數；unaligned. 與 no. 是無號數
                    return op.Name == "ldc.i4.s"
                        ? ((sbyte)_data[at]).ToString(CultureInfo.InvariantCulture)
                        : _data[at].ToString(CultureInfo.InvariantCulture);
                case OperandType.ShortInlineVar:
                    return _data[at].ToString(CultureInfo.InvariantCulture);
                case OperandType.InlineVar:
                    return U16(at).ToString(CultureInfo.InvariantCulture);
                case OperandType.InlineI:
                    return ((int)U32(at)).ToString(CultureInfo.InvariantCulture);
                case OperandType.InlineI8:
                    return ((long)(U32(at) | ((ulong)U32(at + 4) << 32))).ToString(CultureInfo.InvariantCulture);
                case OperandType.ShortInlineR:
                    return FormatFloat(BitConverter.ToSingle(_data, at));
                case OperandType.InlineR:
                    return FormatDouble(BitConverter.ToDouble(_data, at));
                case OperandType.InlineString:
                    return _f.UserStringLiteral((int)U32(at));
                case OperandType.InlineMethod:
                    return _f.MethodToken((int)U32(at));
                case OperandType.InlineField:
                    return _f.FieldToken((int)U32(at));
                case OperandType.InlineType:
                    return _f.TypeToken((int)U32(at));
                case OperandType.InlineTok:
                    return _f.TokenOperand((int)U32(at));
                case OperandType.InlineSig:
                    return _f.StandAloneSignature((int)U32(at));
                default:
                    return CilFormatter.TokenText((int)U32(at));
            }
        }

        private void DecodeExceptionSections(CilMethodListing m, long codeEnd)
        {
            long p = (codeEnd + 3) & ~3L;
            int clauses = 0;
            for (int section = 0; section < 64; section++)
            {
                if (p < 0 || p + 4 > _data.Length) break;
                int kind = _data[p];
                bool fat = (kind & 0x40) != 0;
                int size = fat ? (_data[p + 1] | (_data[p + 2] << 8) | (_data[p + 3] << 16)) : _data[p + 1];
                if (size < 4) break;
                if ((kind & 0x01) != 0)
                {
                    int clauseSize = fat ? 24 : 12;
                    int count = (size - 4) / clauseSize;
                    for (int i = 0; i < count && clauses < MaxClauses; i++, clauses++)
                    {
                        long c = p + 4 + (long)i * clauseSize;
                        if (c + clauseSize > _data.Length) break;
                        int at = (int)c;
                        uint flags, tryOffset, tryLength, handlerOffset, handlerLength;
                        if (fat)
                        {
                            flags = U32(at);
                            tryOffset = U32(at + 4);
                            tryLength = U32(at + 8);
                            handlerOffset = U32(at + 12);
                            handlerLength = U32(at + 16);
                        }
                        else
                        {
                            flags = U16(at);
                            tryOffset = U16(at + 2);
                            tryLength = _data[at + 4];
                            handlerOffset = U16(at + 5);
                            handlerLength = _data[at + 7];
                        }
                        uint classOrFilter = U32(at + clauseSize - 4);
                        m.Lines.Add(FormatClause(flags, tryOffset, tryLength, handlerOffset, handlerLength, classOrFilter));
                    }
                }
                if ((kind & 0x80) == 0) break;
                p = (p + size + 3) & ~3L;
            }
        }

        private string FormatClause(uint flags, uint tryOffset, uint tryLength, uint handlerOffset, uint handlerLength, uint classOrFilter)
        {
            _sb.Clear();
            _sb.Append("// 例外處理：.try ");
            AppendLabel(_sb, tryOffset);
            _sb.Append(" 到 ");
            AppendLabel(_sb, (long)tryOffset + tryLength);
            switch (flags & 0x7)
            {
                case 0:
                    _sb.Append("，catch ").Append(_f.TypeToken((int)classOrFilter));
                    break;
                case 1:
                    _sb.Append("，filter ");
                    AppendLabel(_sb, classOrFilter);
                    break;
                case 2:
                    _sb.Append("，finally");
                    break;
                case 4:
                    _sb.Append("，fault");
                    break;
                default:
                    _sb.Append("，未知的子句種類 0x").Append(flags.ToString("X", CultureInfo.InvariantCulture));
                    break;
            }
            _sb.Append("，處理常式 ");
            AppendLabel(_sb, handlerOffset);
            _sb.Append(" 到 ");
            AppendLabel(_sb, (long)handlerOffset + handlerLength);
            return _sb.ToString();
        }

        // ------------------------------------------------------------------
        // 小工具
        // ------------------------------------------------------------------

        private static string Prefix(int offset)
        {
            return "IL_" + offset.ToString("x4", CultureInfo.InvariantCulture) + ": ";
        }

        private static string Label(long target)
        {
            var sb = new StringBuilder(8);
            AppendLabel(sb, target);
            return sb.ToString();
        }

        private static void AppendLabel(StringBuilder sb, long target)
        {
            sb.Append("IL_");
            if (target < 0) sb.Append('-').Append((-target).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(target.ToString("x4", CultureInfo.InvariantCulture));
        }

        private static string FormatFloat(float value)
        {
            if (float.IsNaN(value)) return "NaN";
            if (float.IsPositiveInfinity(value)) return "Infinity";
            if (float.IsNegativeInfinity(value)) return "-Infinity";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string FormatDouble(double value)
        {
            if (double.IsNaN(value)) return "NaN";
            if (double.IsPositiveInfinity(value)) return "Infinity";
            if (double.IsNegativeInfinity(value)) return "-Infinity";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private ushort U16(int o)
        {
            return o >= 0 && o + 2 <= _data.Length ? (ushort)(_data[o] | (_data[o + 1] << 8)) : (ushort)0;
        }

        private uint U32(int o)
        {
            return o >= 0 && o + 4 <= _data.Length ? (uint)(_data[o] | (_data[o + 1] << 8) | (_data[o + 2] << 16) | (_data[o + 3] << 24)) : 0u;
        }
    }

    // ----------------------------------------------------------------------
    // 運算碼表
    // ----------------------------------------------------------------------

    /// <summary>一個 IL 運算碼的名稱與運算元格式。</summary>
    internal sealed class CilOpInfo
    {
        public CilOpInfo(string name, OperandType operand)
        {
            Name = name;
            Operand = operand;
            switch (operand)
            {
                case OperandType.InlineNone:
                    OperandSize = 0;
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    OperandSize = 1;
                    break;
                case OperandType.InlineVar:
                    OperandSize = 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    OperandSize = 8;
                    break;
                case OperandType.InlineSwitch:
                    OperandSize = -1;
                    break;
                default:
                    OperandSize = 4;
                    break;
            }
        }

        public string Name { get; private set; }
        public OperandType Operand { get; private set; }

        /// <summary>運算元位元組數；-1 表示 switch 的可變長度跳躍表。</summary>
        public int OperandSize { get; private set; }
    }

    /// <summary>從 System.Reflection.Emit.OpCodes 以反射建立的 IL 運算碼表（.NET Framework 內建，不需額外檔案）。</summary>
    internal static class CilOpCodeTable
    {
        private static readonly CilOpInfo[] OneByteOps = new CilOpInfo[256];
        private static readonly CilOpInfo[] TwoByteOps = new CilOpInfo[256];

        static CilOpCodeTable()
        {
            try
            {
                foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.FieldType != typeof(OpCode)) continue;
                    var code = (OpCode)field.GetValue(null);
                    // prefix1～prefix7、prefixref 是保留的內部編碼，不是真正的指令
                    if (code.OpCodeType == OpCodeType.Nternal) continue;
                    int value = (ushort)code.Value;
                    var info = new CilOpInfo(code.Name, code.OperandType);
                    if (code.Size == 1 && value < 0x100)
                    {
                        if (OneByteOps[value] == null) OneByteOps[value] = info;
                    }
                    else if (code.Size == 2 && (value >> 8) == 0xFE)
                    {
                        if (TwoByteOps[value & 0xFF] == null) TwoByteOps[value & 0xFF] = info;
                    }
                }
            }
            catch (Exception)
            {
                // 反射失敗時表格保持空白，所有運算碼都會顯示為未知，但不會當掉
            }
            // OpCodes 沒有定義 "no."（ECMA-335 III.2.2，0xFE 0x19）
            if (TwoByteOps[0x19] == null) TwoByteOps[0x19] = new CilOpInfo("no.", OperandType.ShortInlineI);
        }

        public static CilOpInfo OneByte(int code)
        {
            return code >= 0 && code < 256 ? OneByteOps[code] : null;
        }

        public static CilOpInfo TwoByte(int code)
        {
            return code >= 0 && code < 256 ? TwoByteOps[code] : null;
        }
    }

    // ----------------------------------------------------------------------
    // 名稱與簽章
    // ----------------------------------------------------------------------

    /// <summary>把中繼資料語彙基元與簽章 Blob 轉成可讀文字（ILAsm 風格），結果會快取。</summary>
    internal sealed class CilFormatter
    {
        /// <summary>簽章巢狀深度上限（避免損毀資料造成無限遞迴或堆疊溢位）。</summary>
        private const int MaxDepth = 48;

        /// <summary>單一名稱的長度上限（避免惡意的型別規格讓字串無限變長）。</summary>
        private const int MaxNameLength = 2048;

        /// <summary>ldstr 字串最多顯示的字元數。</summary>
        private const int MaxStringChars = 8192;

        private readonly ClrMetadataReader _md;
        private readonly string[] _typeDefNames;
        private readonly string[] _typeRefNames;
        private readonly int[] _enclosing;
        private readonly int[] _methodOwner;
        private readonly int[] _fieldOwner;
        private readonly int[] _methodStart;
        private readonly int[] _methodEnd;
        private readonly bool _methodPtr;
        private readonly Dictionary<int, string> _typeSpecNames = new Dictionary<int, string>();
        private readonly HashSet<int> _typeSpecBusy = new HashSet<int>();
        private readonly Dictionary<int, string> _memberNames = new Dictionary<int, string>();
        private Dictionary<int, List<KeyValuePair<int, string>>> _methodGenericParams;

        public CilFormatter(ClrMetadataReader md)
        {
            _md = md;
            int typeCount = md.RowCount(ClrMetadataReader.TableTypeDef);
            _typeDefNames = new string[typeCount + 1];
            _typeRefNames = new string[md.RowCount(ClrMetadataReader.TableTypeRef) + 1];
            _enclosing = new int[typeCount + 1];
            _methodStart = new int[typeCount + 1];
            _methodEnd = new int[typeCount + 1];
            _methodOwner = new int[md.RowCount(ClrMetadataReader.TableMethodDef) + 1];
            _fieldOwner = new int[md.RowCount(ClrMetadataReader.TableField) + 1];
            _methodPtr = md.RowCount(ClrMetadataReader.TableMethodPtr) > 0;

            int nested = md.RowCount(ClrMetadataReader.TableNestedClass);
            for (int i = 1; i <= nested; i++)
            {
                uint inner = md.GetColumn(ClrMetadataReader.TableNestedClass, i, ClrMetadataReader.NestedClassNested);
                uint outer = md.GetColumn(ClrMetadataReader.TableNestedClass, i, ClrMetadataReader.NestedClassEnclosing);
                if (inner >= 1 && inner <= typeCount && outer >= 1 && outer <= typeCount && inner != outer) _enclosing[inner] = (int)outer;
            }

            BuildRanges(ClrMetadataReader.TypeDefMethodList, ClrMetadataReader.TableMethodPtr, _methodStart, _methodEnd, _methodOwner,
                        _methodPtr ? md.RowCount(ClrMetadataReader.TableMethodPtr) : md.RowCount(ClrMetadataReader.TableMethodDef));
            var fieldStart = new int[typeCount + 1];
            var fieldEnd = new int[typeCount + 1];
            bool fieldPtr = md.RowCount(ClrMetadataReader.TableFieldPtr) > 0;
            BuildRanges(ClrMetadataReader.TypeDefFieldList, ClrMetadataReader.TableFieldPtr, fieldStart, fieldEnd, _fieldOwner,
                        fieldPtr ? md.RowCount(ClrMetadataReader.TableFieldPtr) : md.RowCount(ClrMetadataReader.TableField));
        }

        /// <summary>依 TypeDef 的 MethodList/FieldList 欄位算出每個型別擁有的範圍（範圍不重疊、遞增）。</summary>
        private void BuildRanges(int listColumn, int ptrTable, int[] starts, int[] ends, int[] owners, int listCount)
        {
            int typeCount = starts.Length - 1;
            bool usePtr = _md.RowCount(ptrTable) > 0;
            int floor = 1;
            uint limit = (uint)listCount + 1;
            for (int t = 1; t <= typeCount; t++)
            {
                int start = (int)Math.Min(_md.GetColumn(ClrMetadataReader.TableTypeDef, t, listColumn), limit);
                if (start < floor) start = floor;
                int next = t < typeCount ? (int)Math.Min(_md.GetColumn(ClrMetadataReader.TableTypeDef, t + 1, listColumn), limit) : listCount + 1;
                int end = Math.Max(start, next);
                starts[t] = start;
                ends[t] = end;
                floor = end;
                for (int i = start; i < end; i++)
                {
                    int row = usePtr ? (int)_md.GetColumn(ptrTable, i, 0) : i;
                    if (row >= 1 && row < owners.Length && owners[row] == 0) owners[row] = t;
                }
            }
        }

        public int MethodListStart(int typeRow)
        {
            return typeRow >= 1 && typeRow < _methodStart.Length ? _methodStart[typeRow] : 0;
        }

        public int MethodListEnd(int typeRow)
        {
            return typeRow >= 1 && typeRow < _methodEnd.Length ? _methodEnd[typeRow] : 0;
        }

        /// <summary>方法清單的第 index 項對應的 MethodDef 列（有 MethodPtr 間接表時要轉換）。</summary>
        public int MethodListEntry(int index)
        {
            return _methodPtr ? (int)_md.GetColumn(ClrMetadataReader.TableMethodPtr, index, 0) : index;
        }

        public static string TokenText(int token)
        {
            return "token 0x" + token.ToString("X8", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------
        // 型別名稱
        // ------------------------------------------------------------------

        /// <summary>型別的命名空間與名稱（巢狀型別的名稱是 "Outer/Inner"，命名空間取最外層型別的）。</summary>
        public void GetTypeDefParts(int row, out string ns, out string name)
        {
            ns = string.Empty;
            name = string.Empty;
            if (row < 1 || row >= _typeDefNames.Length) return;
            var chain = new List<int>();
            int current = row;
            while (current >= 1 && chain.Count < MaxDepth)
            {
                chain.Add(current);
                int outer = _enclosing[current];
                if (outer == 0 || chain.Contains(outer)) break;
                current = outer;
            }
            const int typeDef = ClrMetadataReader.TableTypeDef;
            var sb = new StringBuilder();
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                string n = _md.GetStringColumn(typeDef, chain[i], ClrMetadataReader.TypeDefName);
                string space = _md.GetStringColumn(typeDef, chain[i], ClrMetadataReader.TypeDefNamespace);
                if (i == chain.Count - 1)
                {
                    ns = space;
                    sb.Append(n);
                }
                else
                {
                    sb.Append('/');
                    if (space.Length > 0) sb.Append(space).Append('.');
                    sb.Append(n);
                }
            }
            name = sb.ToString();
        }

        public string TypeDefName(int row)
        {
            if (row < 1 || row >= _typeDefNames.Length) return TokenText((ClrMetadataReader.TableTypeDef << 24) | row);
            string cached = _typeDefNames[row];
            if (cached != null) return cached;
            string ns, name;
            GetTypeDefParts(row, out ns, out name);
            cached = ns.Length > 0 ? ns + "." + name : name;
            _typeDefNames[row] = cached;
            return cached;
        }

        private string TypeRefName(int row, int depth)
        {
            if (row < 1 || row >= _typeRefNames.Length) return TokenText((ClrMetadataReader.TableTypeRef << 24) | row);
            string cached = _typeRefNames[row];
            if (cached != null) return cached;
            const int typeRef = ClrMetadataReader.TableTypeRef;
            string name = _md.GetStringColumn(typeRef, row, ClrMetadataReader.TypeRefName);
            string ns = _md.GetStringColumn(typeRef, row, ClrMetadataReader.TypeRefNamespace);
            string local = ns.Length > 0 ? ns + "." + name : name;
            int scope = _md.GetCodedToken(typeRef, row, ClrMetadataReader.TypeRefScope, ClrMetadataReader.CodedResolutionScope);
            int scopeTable = (scope >> 24) & 0xFF;
            int scopeRow = scope & 0xFFFFFF;
            string result;
            switch (scopeTable)
            {
                case ClrMetadataReader.TableAssemblyRef:
                    result = "[" + _md.GetStringColumn(scopeTable, scopeRow, ClrMetadataReader.AssemblyRefName) + "]" + local;
                    break;
                case ClrMetadataReader.TableModuleRef:
                    result = "[.module " + _md.GetStringColumn(scopeTable, scopeRow, ClrMetadataReader.ModuleRefName) + "]" + local;
                    break;
                case ClrMetadataReader.TableTypeRef:
                    if (scope == 0 || scopeRow == row || depth >= MaxDepth) result = "?/" + local;
                    else result = TypeRefName(scopeRow, depth + 1) + "/" + local;
                    break;
                default:
                    result = local;
                    break;
            }
            _typeRefNames[row] = result;
            return result;
        }

        private string TypeSpecName(int row, int depth)
        {
            string cached;
            if (_typeSpecNames.TryGetValue(row, out cached)) return cached;
            if (row < 1 || row > _md.RowCount(ClrMetadataReader.TableTypeSpec)) return TokenText((ClrMetadataReader.TableTypeSpec << 24) | row);
            if (depth >= MaxDepth || !_typeSpecBusy.Add(row)) return "???";
            try
            {
                var c = _md.OpenBlob(_md.GetColumn(ClrMetadataReader.TableTypeSpec, row, ClrMetadataReader.TypeSpecBlob));
                string s = ReadType(ref c, depth + 1);
                _typeSpecNames[row] = s;
                return s;
            }
            finally
            {
                _typeSpecBusy.Remove(row);
            }
        }

        /// <summary>TypeDef、TypeRef 或 TypeSpec 語彙基元 → 型別名稱。</summary>
        public string TypeToken(int token)
        {
            return TypeToken(token, 0);
        }

        private string TypeToken(int token, int depth)
        {
            int table = (token >> 24) & 0xFF;
            int row = token & 0xFFFFFF;
            switch (table)
            {
                case ClrMetadataReader.TableTypeDef:
                    if (row >= 1 && row < _typeDefNames.Length) return TypeDefName(row);
                    break;
                case ClrMetadataReader.TableTypeRef:
                    if (row >= 1 && row < _typeRefNames.Length) return TypeRefName(row, depth);
                    break;
                case ClrMetadataReader.TableTypeSpec:
                    if (row >= 1 && row <= _md.RowCount(table)) return TypeSpecName(row, depth);
                    break;
            }
            return TokenText(token);
        }

        // ------------------------------------------------------------------
        // 簽章（II.23.2）
        // ------------------------------------------------------------------

        /// <summary>讀取一個型別（含修飾詞、指標、陣列、泛型）並轉成文字。</summary>
        private string ReadType(ref ClrBlobCursor c, int depth)
        {
            if (depth > MaxDepth)
            {
                c.Fail();
                return "???";
            }
            byte et = c.ReadByte();
            if (c.Failed) return "???";
            switch (et)
            {
                case 0x01: return "void";
                case 0x02: return "bool";
                case 0x03: return "char";
                case 0x04: return "int8";
                case 0x05: return "uint8";
                case 0x06: return "int16";
                case 0x07: return "uint16";
                case 0x08: return "int32";
                case 0x09: return "uint32";
                case 0x0A: return "int64";
                case 0x0B: return "uint64";
                case 0x0C: return "float32";
                case 0x0D: return "float64";
                case 0x0E: return "string";
                case 0x16: return "typedref";
                case 0x18: return "native int";
                case 0x19: return "native uint";
                case 0x1C: return "object";
                case 0x0F: return ReadType(ref c, depth + 1) + "*";
                case 0x10: return ReadType(ref c, depth + 1) + "&";
                case 0x1D: return ReadType(ref c, depth + 1) + "[]";
                case 0x45: return ReadType(ref c, depth + 1) + " pinned";
                case 0x11:
                case 0x12:
                {
                    int token = c.ReadTypeDefOrRefToken();
                    return c.Failed ? "???" : TypeToken(token, depth + 1);
                }
                case 0x13:
                {
                    int n = c.ReadCompressed();
                    return "!" + n.ToString(CultureInfo.InvariantCulture);
                }
                case 0x1E:
                {
                    int n = c.ReadCompressed();
                    return "!!" + n.ToString(CultureInfo.InvariantCulture);
                }
                case 0x14:
                    return ReadArray(ref c, depth);
                case 0x15:
                    return ReadGenericInstance(ref c, depth);
                case 0x1B:
                {
                    var sig = ReadMethodSig(ref c, depth + 1);
                    var sb = new StringBuilder("method ");
                    AppendCallingConvention(sb, sig.CallingConvention, true);
                    sb.Append(sig.ReturnType).Append(" *(");
                    AppendParameters(sb, sig);
                    return sb.Append(')').ToString();
                }
                case 0x1F:
                case 0x20:
                {
                    int token = c.ReadTypeDefOrRefToken();
                    if (c.Failed) return "???";
                    string modifier = TypeToken(token, depth + 1);
                    string inner = ReadType(ref c, depth + 1);
                    return inner + (et == 0x1F ? " modreq(" : " modopt(") + modifier + ")";
                }
                default:
                    c.Fail();
                    return "???";
            }
        }

        private string ReadArray(ref ClrBlobCursor c, int depth)
        {
            string element = ReadType(ref c, depth + 1);
            int rank = c.ReadCompressed();
            if (c.Failed || rank > 64)
            {
                c.Fail();
                return element + "[?]";
            }
            int sizeCount = c.ReadCompressed();
            if (c.Failed || sizeCount > rank || sizeCount > c.Remaining)
            {
                c.Fail();
                return element + "[?]";
            }
            var sizes = new int[sizeCount];
            for (int i = 0; i < sizeCount; i++) sizes[i] = c.ReadCompressed();
            int boundCount = c.ReadCompressed();
            if (c.Failed || boundCount > rank || boundCount > c.Remaining)
            {
                c.Fail();
                return element + "[?]";
            }
            var bounds = new int[boundCount];
            for (int i = 0; i < boundCount; i++) bounds[i] = c.ReadCompressedSigned();
            var sb = new StringBuilder(element);
            sb.Append('[');
            for (int i = 0; i < rank; i++)
            {
                if (i > 0) sb.Append(',');
                if (i < boundCount)
                {
                    sb.Append(bounds[i].ToString(CultureInfo.InvariantCulture)).Append("...");
                    if (i < sizeCount) sb.Append(((long)bounds[i] + sizes[i] - 1).ToString(CultureInfo.InvariantCulture));
                }
                else if (i < sizeCount)
                {
                    sb.Append(sizes[i].ToString(CultureInfo.InvariantCulture));
                }
            }
            return sb.Append(']').ToString();
        }

        private string ReadGenericInstance(ref ClrBlobCursor c, int depth)
        {
            byte kind = c.ReadByte();
            if (kind != 0x11 && kind != 0x12) c.Fail();
            int token = c.ReadTypeDefOrRefToken();
            int count = c.ReadCompressed();
            if (c.Failed || count > c.Remaining)
            {
                c.Fail();
                return "???";
            }
            var sb = new StringBuilder(TypeToken(token, depth + 1));
            sb.Append('<');
            for (int i = 0; i < count; i++)
            {
                string arg = ReadType(ref c, depth + 1);
                if (sb.Length < MaxNameLength)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(arg);
                }
                else if (i == count - 1)
                {
                    sb.Append(",…");
                }
                if (c.Failed) break;
            }
            return sb.Append('>').ToString();
        }

        /// <summary>方法簽章的解碼結果。</summary>
        private sealed class MethodSig
        {
            public MethodSig()
            {
                ReturnType = "???";
                Parameters = new List<string>();
                Sentinel = -1;
            }

            public byte CallingConvention;
            public int GenericCount;
            public string ReturnType;
            public List<string> Parameters;
            public int Sentinel;
            public bool Failed;
        }

        private MethodSig ReadMethodSig(ref ClrBlobCursor c, int depth)
        {
            var sig = new MethodSig();
            sig.CallingConvention = c.ReadByte();
            if ((sig.CallingConvention & 0x10) != 0) sig.GenericCount = c.ReadCompressed();
            int count = c.ReadCompressed();
            if (c.Failed)
            {
                sig.Failed = true;
                return sig;
            }
            // 每個參數至少佔 1 個位元組，數量不可能超過剩下的位元組數
            if (count > c.Remaining)
            {
                count = c.Remaining;
                sig.Failed = true;
            }
            sig.ReturnType = ReadType(ref c, depth + 1);
            for (int i = 0; i < count && !c.Failed; i++)
            {
                byte next;
                if (c.TryPeek(out next) && next == 0x41)
                {
                    c.ReadByte();
                    if (sig.Sentinel < 0) sig.Sentinel = i;
                }
                sig.Parameters.Add(ReadType(ref c, depth + 1));
            }
            if (c.Failed) sig.Failed = true;
            return sig;
        }

        private static void AppendCallingConvention(StringBuilder sb, byte callingConvention, bool includeInstance)
        {
            if (includeInstance && (callingConvention & 0x20) != 0) sb.Append("instance ");
            if ((callingConvention & 0x40) != 0) sb.Append("explicit ");
            switch (callingConvention & 0x0F)
            {
                case 1: sb.Append("unmanaged cdecl "); break;
                case 2: sb.Append("unmanaged stdcall "); break;
                case 3: sb.Append("unmanaged thiscall "); break;
                case 4: sb.Append("unmanaged fastcall "); break;
                case 5: sb.Append("vararg "); break;
                // .NET 5 起的函式指標：呼叫慣例由 modopt 決定（例如 CallConvCdecl）
                case 9: sb.Append("unmanaged "); break;
            }
        }

        private static void AppendParameters(StringBuilder sb, MethodSig sig)
        {
            int start = sb.Length;
            for (int i = 0; i < sig.Parameters.Count; i++)
            {
                if (sb.Length - start > MaxNameLength)
                {
                    sb.Append(", …");
                    break;
                }
                if (i > 0) sb.Append(", ");
                if (i == sig.Sentinel) sb.Append("..., ");
                sb.Append(sig.Parameters[i]);
            }
            if (sig.Sentinel >= sig.Parameters.Count && sig.Sentinel >= 0) sb.Append(sig.Parameters.Count > 0 ? ", ..." : "...");
        }

        // ------------------------------------------------------------------
        // 方法與欄位參考
        // ------------------------------------------------------------------

        /// <summary>方法清單用的可讀簽章，例如 "static int32 Main(string[])"。</summary>
        public string MethodDefSignature(int row)
        {
            const int methodDef = ClrMetadataReader.TableMethodDef;
            uint flags = _md.GetColumn(methodDef, row, ClrMetadataReader.MethodFlags);
            string name = _md.GetStringColumn(methodDef, row, ClrMetadataReader.MethodName);
            var c = _md.OpenBlob(_md.GetColumn(methodDef, row, ClrMetadataReader.MethodSignature));
            var sig = ReadMethodSig(ref c, 0);
            var sb = new StringBuilder();
            if ((flags & 0x0010) != 0) sb.Append("static ");
            AppendCallingConvention(sb, sig.CallingConvention, false);
            sb.Append(sig.ReturnType).Append(' ').Append(name);
            if (sig.GenericCount > 0)
            {
                var names = MethodGenericParameterNames(row);
                sb.Append('<');
                for (int i = 0; i < sig.GenericCount && i < 256; i++)
                {
                    if (i > 0) sb.Append(", ");
                    string n = null;
                    if (names != null)
                        foreach (var pair in names)
                            if (pair.Key == i) { n = pair.Value; break; }
                    sb.Append(string.IsNullOrEmpty(n) ? "!!" + i.ToString(CultureInfo.InvariantCulture) : n);
                }
                sb.Append('>');
            }
            sb.Append('(');
            AppendParameters(sb, sig);
            sb.Append(')');
            if (sig.Failed) sb.Append(" /* 簽章無法完整解碼 */");
            return sb.ToString();
        }

        private List<KeyValuePair<int, string>> MethodGenericParameterNames(int methodRow)
        {
            if (_methodGenericParams == null)
            {
                _methodGenericParams = new Dictionary<int, List<KeyValuePair<int, string>>>();
                const int gp = ClrMetadataReader.TableGenericParam;
                int rows = _md.RowCount(gp);
                for (int i = 1; i <= rows; i++)
                {
                    int owner = _md.GetCodedToken(gp, i, ClrMetadataReader.GenericParamOwner, ClrMetadataReader.CodedTypeOrMethodDef);
                    if ((owner >> 24) != ClrMetadataReader.TableMethodDef) continue;
                    int ownerRow = owner & 0xFFFFFF;
                    List<KeyValuePair<int, string>> list;
                    if (!_methodGenericParams.TryGetValue(ownerRow, out list))
                    {
                        list = new List<KeyValuePair<int, string>>();
                        _methodGenericParams[ownerRow] = list;
                    }
                    if (list.Count < 256)
                        list.Add(new KeyValuePair<int, string>((int)_md.GetColumn(gp, i, ClrMetadataReader.GenericParamNumber),
                                                               _md.GetStringColumn(gp, i, ClrMetadataReader.GenericParamName)));
                }
            }
            List<KeyValuePair<int, string>> result;
            return _methodGenericParams.TryGetValue(methodRow, out result) ? result : null;
        }

        /// <summary>"Namespace.Type::Method"（進入點顯示用）。</summary>
        public string MethodDefFullName(int row)
        {
            string name = _md.GetStringColumn(ClrMetadataReader.TableMethodDef, row, ClrMetadataReader.MethodName);
            int owner = row >= 1 && row < _methodOwner.Length ? _methodOwner[row] : 0;
            return owner != 0 ? TypeDefName(owner) + "::" + name : name;
        }

        /// <summary>MethodDef、MemberRef 或 MethodSpec 語彙基元 → "回傳型別 [組件]型別::方法(參數型別)"。</summary>
        public string MethodToken(int token)
        {
            string cached;
            if (_memberNames.TryGetValue(token, out cached)) return cached;
            int table = (token >> 24) & 0xFF;
            int row = token & 0xFFFFFF;
            string s = null;
            if (row >= 1 && row <= _md.RowCount(table))
            {
                switch (table)
                {
                    case ClrMetadataReader.TableMethodDef:
                        s = FormatMethodDef(row, null);
                        break;
                    case ClrMetadataReader.TableMemberRef:
                        s = FormatMemberRef(row, null);
                        break;
                    case ClrMetadataReader.TableMethodSpec:
                        s = FormatMethodSpec(row);
                        break;
                }
            }
            if (s == null) s = TokenText(token);
            if (_memberNames.Count < 500000) _memberNames[token] = s;
            return s;
        }

        /// <summary>Field 或 MemberRef 語彙基元 → "型別 [組件]型別::欄位"。</summary>
        public string FieldToken(int token)
        {
            string cached;
            if (_memberNames.TryGetValue(token, out cached)) return cached;
            int table = (token >> 24) & 0xFF;
            int row = token & 0xFFFFFF;
            string s = null;
            if (row >= 1 && row <= _md.RowCount(table))
            {
                if (table == ClrMetadataReader.TableField)
                {
                    const int field = ClrMetadataReader.TableField;
                    int owner = row < _fieldOwner.Length ? _fieldOwner[row] : 0;
                    var c = _md.OpenBlob(_md.GetColumn(field, row, ClrMetadataReader.FieldSignature));
                    s = FormatField(ref c, OwnerName(owner), _md.GetStringColumn(field, row, ClrMetadataReader.FieldName));
                }
                else if (table == ClrMetadataReader.TableMemberRef)
                {
                    s = FormatMemberRef(row, null);
                }
            }
            if (s == null) s = TokenText(token);
            if (_memberNames.Count < 500000) _memberNames[token] = s;
            return s;
        }

        /// <summary>ldtoken 的運算元：型別、"field ..." 或 "method ..."。</summary>
        public string TokenOperand(int token)
        {
            int table = (token >> 24) & 0xFF;
            switch (table)
            {
                case ClrMetadataReader.TableTypeDef:
                case ClrMetadataReader.TableTypeRef:
                case ClrMetadataReader.TableTypeSpec:
                    return TypeToken(token);
                case ClrMetadataReader.TableField:
                    return "field " + FieldToken(token);
                case ClrMetadataReader.TableMethodDef:
                case ClrMetadataReader.TableMethodSpec:
                    return "method " + MethodToken(token);
                case ClrMetadataReader.TableMemberRef:
                    return (IsFieldMemberRef(token & 0xFFFFFF) ? "field " : "method ") + MethodToken(token);
            }
            return TokenText(token);
        }

        /// <summary>calli 的獨立簽章（StandAloneSig）→ "int32(int32, int32)"。</summary>
        public string StandAloneSignature(int token)
        {
            int table = (token >> 24) & 0xFF;
            int row = token & 0xFFFFFF;
            if (table != ClrMetadataReader.TableStandAloneSig || row < 1 || row > _md.RowCount(table)) return TokenText(token);
            var c = _md.OpenBlob(_md.GetColumn(table, row, ClrMetadataReader.StandAloneSigBlob));
            byte first;
            if (!c.TryPeek(out first) || ((first & 0x0F) > 5 && (first & 0x0F) != 9)) return TokenText(token);
            var sig = ReadMethodSig(ref c, 0);
            var sb = new StringBuilder();
            AppendCallingConvention(sb, sig.CallingConvention, true);
            sb.Append(sig.ReturnType).Append('(');
            AppendParameters(sb, sig);
            return sb.Append(')').ToString();
        }

        /// <summary>區域變數簽章（LocalVarSig）；declaredCount 是簽章宣告的數量。</summary>
        public bool TryReadLocals(uint token, out List<string> types, out int declaredCount)
        {
            types = new List<string>();
            declaredCount = 0;
            int table = (int)(token >> 24);
            int row = (int)(token & 0xFFFFFF);
            if (table != ClrMetadataReader.TableStandAloneSig || row < 1 || row > _md.RowCount(table)) return false;
            var c = _md.OpenBlob(_md.GetColumn(table, row, ClrMetadataReader.StandAloneSigBlob));
            if (c.ReadByte() != 0x07) return false;
            int count = c.ReadCompressed();
            if (c.Failed || count > 0xFFFF) return false;
            declaredCount = count;
            for (int i = 0; i < count && !c.Failed; i++)
            {
                string type = ReadType(ref c, 0);
                if (types.Count < 256) types.Add(type);
            }
            return true;
        }

        /// <summary>ldstr 的字串：從 #US 堆積讀出並加上引號與跳脫字元。</summary>
        public string UserStringLiteral(int token)
        {
            if (((token >> 24) & 0xFF) != ClrMetadataReader.TokenUserString) return TokenText(token);
            uint index = (uint)token & 0xFFFFFF;
            string s = _md.GetUserString(index, MaxStringChars);
            if (s == null) return TokenText(token);
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\0': sb.Append("\\0"); break;
                    default:
                        if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        {
                            sb.Append(ch).Append(s[i + 1]);
                            i++;
                        }
                        else if (ch < 0x20 || (ch >= 0x7F && ch < 0xA0) || char.IsSurrogate(ch) || ch == 0x2028 || ch == 0x2029 || ch >= 0xFFFE)
                        {
                            sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(ch);
                        }
                        break;
                }
            }
            sb.Append('"');
            int total = _md.GetUserStringLength(index);
            if (total > s.Length) sb.Append(" /* …共 ").Append(total.ToString(CultureInfo.InvariantCulture)).Append(" 個字元 */");
            return sb.ToString();
        }

        private bool IsFieldMemberRef(int row)
        {
            var c = _md.OpenBlob(_md.GetColumn(ClrMetadataReader.TableMemberRef, row, ClrMetadataReader.MemberRefSignature));
            byte first;
            return c.TryPeek(out first) && (first & 0x0F) == 0x06;
        }

        /// <summary>型別名稱；全域成員（屬於 &lt;Module&gt;）回傳 null，不顯示 "型別::"。</summary>
        private string OwnerName(int typeRow)
        {
            if (typeRow < 1) return null;
            if (typeRow == 1 && _md.GetStringColumn(ClrMetadataReader.TableTypeDef, 1, ClrMetadataReader.TypeDefName) == "<Module>") return null;
            return TypeDefName(typeRow);
        }

        private string FormatField(ref ClrBlobCursor c, string owner, string name)
        {
            byte first = c.ReadByte();
            string type = (first & 0x0F) == 0x06 && !c.Failed ? ReadType(ref c, 0) : "???";
            return type + " " + (owner != null ? owner + "::" : string.Empty) + name;
        }

        private string FormatMethodDef(int row, string genericArguments)
        {
            const int methodDef = ClrMetadataReader.TableMethodDef;
            int owner = row < _methodOwner.Length ? _methodOwner[row] : 0;
            var c = _md.OpenBlob(_md.GetColumn(methodDef, row, ClrMetadataReader.MethodSignature));
            var sig = ReadMethodSig(ref c, 0);
            return FormatMethodReference(sig, OwnerName(owner), _md.GetStringColumn(methodDef, row, ClrMetadataReader.MethodName), genericArguments);
        }

        private string FormatMemberRef(int row, string genericArguments)
        {
            const int memberRef = ClrMetadataReader.TableMemberRef;
            int parent = _md.GetCodedToken(memberRef, row, ClrMetadataReader.MemberRefParent, ClrMetadataReader.CodedMemberRefParent);
            string owner = MemberParentName(parent);
            string name = _md.GetStringColumn(memberRef, row, ClrMetadataReader.MemberRefName);
            var c = _md.OpenBlob(_md.GetColumn(memberRef, row, ClrMetadataReader.MemberRefSignature));
            byte first;
            if (c.TryPeek(out first) && (first & 0x0F) == 0x06) return FormatField(ref c, owner, name);
            var sig = ReadMethodSig(ref c, 0);
            return FormatMethodReference(sig, owner, name, genericArguments);
        }

        private string MemberParentName(int parent)
        {
            int table = (parent >> 24) & 0xFF;
            int row = parent & 0xFFFFFF;
            switch (table)
            {
                case ClrMetadataReader.TableTypeDef:
                    return OwnerName(row);
                case ClrMetadataReader.TableTypeRef:
                case ClrMetadataReader.TableTypeSpec:
                    return TypeToken(parent);
                case ClrMetadataReader.TableModuleRef:
                    return "[.module " + _md.GetStringColumn(table, row, ClrMetadataReader.ModuleRefName) + "]";
                case ClrMetadataReader.TableMethodDef:
                    // vararg 呼叫點：父項是方法定義本身
                    return OwnerName(row < _methodOwner.Length ? _methodOwner[row] : 0);
            }
            return parent == 0 ? null : TokenText(parent);
        }

        private string FormatMethodSpec(int row)
        {
            const int methodSpec = ClrMetadataReader.TableMethodSpec;
            int method = _md.GetCodedToken(methodSpec, row, ClrMetadataReader.MethodSpecMethod, ClrMetadataReader.CodedMethodDefOrRef);
            var c = _md.OpenBlob(_md.GetColumn(methodSpec, row, ClrMetadataReader.MethodSpecInstantiation));
            var sb = new StringBuilder("<");
            if (c.ReadByte() == 0x0A)
            {
                int count = c.ReadCompressed();
                if (count > c.Remaining) count = c.Remaining;
                for (int i = 0; i < count && !c.Failed; i++)
                {
                    string arg = ReadType(ref c, 0);
                    if (sb.Length > MaxNameLength) continue;
                    if (i > 0) sb.Append(',');
                    sb.Append(arg);
                }
            }
            else
            {
                sb.Append("???");
            }
            string arguments = sb.Append('>').ToString();
            int table = (method >> 24) & 0xFF;
            int target = method & 0xFFFFFF;
            if (target >= 1 && target <= _md.RowCount(table))
            {
                if (table == ClrMetadataReader.TableMethodDef) return FormatMethodDef(target, arguments);
                if (table == ClrMetadataReader.TableMemberRef) return FormatMemberRef(target, arguments);
            }
            return TokenText(method) + arguments;
        }

        private static string FormatMethodReference(MethodSig sig, string owner, string name, string genericArguments)
        {
            var sb = new StringBuilder();
            AppendCallingConvention(sb, sig.CallingConvention, true);
            sb.Append(sig.ReturnType).Append(' ');
            if (owner != null) sb.Append(owner).Append("::");
            sb.Append(name);
            if (genericArguments != null) sb.Append(genericArguments);
            sb.Append('(');
            AppendParameters(sb, sig);
            return sb.Append(')').ToString();
        }
    }
}
