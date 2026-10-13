using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Assam.Core.Analysis;
using Assam.Core.Text;

namespace Assam.Core.Simulation
{
    /// <summary>
    /// x86 / x64 指令模擬器（Intel 語法，MASM/NASM 常用寫法）。
    /// 每執行一行就記錄暫存器、旗標、記憶體的變化與「推導過程」，方便學習與手算對照。
    /// 純軟體模擬，不會真的執行機器碼，也不需要網路。
    /// </summary>
    public sealed class X86Simulator
    {
        public SimulationResult Run(string code, SimulationOptions options = null)
        {
            return new Machine(code, options ?? new SimulationOptions()).Run();
        }

        /// <summary>模擬器支援的指令清單（給使用者參考）。</summary>
        public static readonly string[] SupportedInstructions =
        {
            "MOV", "MOVZX", "MOVSX", "MOVSXD", "LEA", "XCHG", "PUSH", "POP", "PUSHAD", "POPAD", "PUSHA", "POPA", "PUSHFD", "POPFD",
            "ADD", "ADC", "SUB", "SBB", "INC", "DEC", "NEG", "CMP", "MUL", "IMUL", "DIV", "IDIV",
            "AND", "OR", "XOR", "NOT", "TEST", "SHL", "SAL", "SHR", "SAR", "ROL", "ROR", "RCL", "RCR", "BSWAP",
            "CBW", "CWDE", "CDQE", "CWD", "CDQ", "CQO", "JMP", "Jcc（JE/JNE/JG/JL/JA/JB…）", "JCXZ", "JECXZ",
            "LOOP", "LOOPE", "LOOPNE", "CALL", "RET", "ENTER", "LEAVE", "SETcc", "CMOVcc",
            "MOVSB/W/D", "STOSB/W/D", "LODSB/W/D", "SCASB/W/D", "CMPSB/W/D", "REP/REPE/REPNE",
            "CLC", "STC", "CMC", "CLD", "STD", "NOP", "HLT", "INT 21h/10h/16h/80h", "SYSCALL",
            "Irvine32：WriteString/WriteDec/WriteInt/WriteHex/WriteBin/WriteChar/Crlf/DumpRegs/ReadInt/ReadDec/ReadChar/ReadString"
        };

        // =====================================================================
        // 內部：機器狀態與執行
        // =====================================================================

        private sealed class RegRef
        {
            public string Name;
            public int Index;
            public int Bits;
            public int Shift;
            public bool IsSegment;
        }

        private sealed class DataSymbol
        {
            public string Name;
            public ulong Address;
            public int ElementSize;
            public int Count;
        }

        private sealed class Instr
        {
            public AsmLine Line;
            public string Mn;
            public List<string> Ops;
            public string Prefix;
        }

        private enum OpKind { Register, Immediate, Memory }

        private sealed class Operand
        {
            public OpKind Kind;
            public RegRef Reg;
            public ulong Imm;
            public ulong Address;
            public int Bits;
            public string Text;
            public string AddressText;
        }

        // 注意：靜態欄位依宣告順序初始化，SegmentNames 必須在 BuildRegisters() 之前
        private static readonly string[] SegmentNames = { "es", "cs", "ss", "ds", "fs", "gs" };
        private static readonly Dictionary<string, RegRef> Registers = BuildRegisters();
        private static readonly Regex SizePrefixRegex = new Regex(@"^(byte|sbyte|word|sword|dword|sdword|qword|real4|real8)\s+(ptr\s+)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SegOverrideRegex = new Regex(@"^(cs|ds|es|ss|fs|gs)\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DupRegex = new Regex(@"^(.+?)\s+dup\s*\((.*)\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static Dictionary<string, RegRef> BuildRegisters()
        {
            var map = new Dictionary<string, RegRef>(StringComparer.OrdinalIgnoreCase);
            string[] r64 = { "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi" };
            string[] r32 = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" };
            string[] r16 = { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di" };
            string[] r8 = { "al", "cl", "dl", "bl", "spl", "bpl", "sil", "dil" };
            string[] r8h = { "ah", "ch", "dh", "bh" };
            for (int i = 0; i < 8; i++)
            {
                map[r64[i]] = new RegRef { Name = r64[i], Index = i, Bits = 64 };
                map[r32[i]] = new RegRef { Name = r32[i], Index = i, Bits = 32 };
                map[r16[i]] = new RegRef { Name = r16[i], Index = i, Bits = 16 };
                map[r8[i]] = new RegRef { Name = r8[i], Index = i, Bits = 8 };
            }
            for (int i = 0; i < 4; i++) map[r8h[i]] = new RegRef { Name = r8h[i], Index = i, Bits = 8, Shift = 8 };
            for (int i = 8; i < 16; i++)
            {
                string n = "r" + i.ToString(CultureInfo.InvariantCulture);
                map[n] = new RegRef { Name = n, Index = i, Bits = 64 };
                map[n + "d"] = new RegRef { Name = n + "d", Index = i, Bits = 32 };
                map[n + "w"] = new RegRef { Name = n + "w", Index = i, Bits = 16 };
                map[n + "b"] = new RegRef { Name = n + "b", Index = i, Bits = 8 };
                map[n + "l"] = new RegRef { Name = n + "l", Index = i, Bits = 8 };
            }
            for (int i = 0; i < SegmentNames.Length; i++)
                map[SegmentNames[i]] = new RegRef { Name = SegmentNames[i], Index = i, Bits = 16, IsSegment = true };
            return map;
        }

        private sealed class Machine
        {
            private const int MaxOutput = 100000;
            private const int MaxRepeat = 1000000;

            private readonly string _code;
            private readonly SimulationOptions _options;
            private readonly SimulationResult _result = new SimulationResult();

            private readonly ulong[] _r = new ulong[16];
            private readonly ushort[] _seg = new ushort[6];
            private bool _cf, _pf, _af, _zf, _sf, _of, _df;
            private readonly Dictionary<ulong, byte> _mem = new Dictionary<ulong, byte>();

            private readonly List<Instr> _instrs = new List<Instr>();
            private readonly Dictionary<string, int> _codeLabels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, DataSymbol> _data = new Dictionary<string, DataSymbol>(StringComparer.OrdinalIgnoreCase);
            private readonly List<DataSymbol> _dataOrder = new List<DataSymbol>();
            private readonly Dictionary<string, long> _constants = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            private int _bits = 32;
            private bool _nasm;
            private ulong _dataBase;
            private ulong _dataPtr;
            private ulong _codeBase;
            private ulong _initialSp;
            private int _ip;
            private bool _halt;
            private string _stopReason;
            private readonly StringBuilder _output = new StringBuilder();
            private readonly List<string> _derivation = new List<string>();
            private readonly List<string> _memChanges = new List<string>();
            private int _inputPos;
            private uint _random = 12345;

            public Machine(string code, SimulationOptions options)
            {
                _code = code ?? string.Empty;
                _options = options;
            }

            private int AddrBits
            {
                get { return _bits; }
            }

            private string SpName
            {
                get { return _bits == 64 ? "RSP" : _bits == 16 ? "SP" : "ESP"; }
            }

            // -----------------------------------------------------------------
            // 載入程式
            // -----------------------------------------------------------------

            public SimulationResult Run()
            {
                try
                {
                    Load();
                }
                catch (SimulationException ex)
                {
                    _result.Error = ex.Message;
                    _result.StopReason = "載入程式失敗";
                    Finish();
                    return _result;
                }

                int step = 0;
                while (true)
                {
                    if (_ip < 0 || _ip >= _instrs.Count)
                    {
                        _stopReason = _instrs.Count == 0 ? "沒有可執行的指令" : "執行到程式最後一行";
                        break;
                    }
                    if (step >= _options.MaxSteps)
                    {
                        _stopReason = "已達最大步數 " + _options.MaxSteps + "（可能是無窮迴圈，或請調高步數上限）";
                        _result.Messages.Add("執行超過 " + _options.MaxSteps + " 步而停止。");
                        break;
                    }

                    var ins = _instrs[_ip];
                    var before = SnapshotRegisters();
                    var flagsBefore = FlagsText();
                    ushort[] segBefore = (ushort[])_seg.Clone();
                    _derivation.Clear();
                    _memChanges.Clear();
                    int next = _ip + 1;
                    string error = null;
                    try
                    {
                        Execute(ins, ref next);
                    }
                    catch (SimulationException ex)
                    {
                        error = ex.Message;
                    }

                    step++;
                    var trace = new TraceStep
                    {
                        Step = step,
                        LineNumber = ins.Line.LineNumber,
                        Source = ins.Line.CodeText,
                        Derivation = string.Join("\r\n", _derivation),
                        Flags = FlagsText()
                    };
                    trace.Changes = DescribeChanges(before, flagsBefore, segBefore);
                    trace.Registers.AddRange(DisplayRegisters());
                    _result.Steps.Add(trace);

                    if (error != null)
                    {
                        _result.Error = "第 " + ins.Line.LineNumber + " 行：" + error;
                        _stopReason = "發生錯誤而停止";
                        trace.Derivation = (trace.Derivation.Length > 0 ? trace.Derivation + "\r\n" : string.Empty) + "錯誤：" + error;
                        break;
                    }
                    if (_halt) break;
                    _ip = next;
                }

                Finish();
                return _result;
            }

            private void Finish()
            {
                _result.Bits = _bits;
                _result.StopReason = _result.StopReason ?? _stopReason;
                _result.Output = _output.ToString();
                _result.FinalFlags = FlagsText();
                _result.FinalRegisters.AddRange(DisplayRegisters());
                foreach (var d in _dataOrder.Take(50)) _result.DataDump.Add(DumpSymbol(d));
            }

            private void Load()
            {
                var lines = AsmLineParser.Parse(_code, AsmArch.X86Intel);
                DetectMode(lines);

                _dataBase = _bits == 64 ? 0x0000000140004000UL : _bits == 16 ? 0x0000UL : 0x00404000UL;
                _codeBase = _bits == 64 ? 0x0000000140001000UL : _bits == 16 ? 0x0100UL : 0x00401000UL;
                _initialSp = _bits == 64 ? 0x000000000014FF00UL : _bits == 16 ? 0xFFFEUL : 0x0019FF00UL;
                _dataPtr = _dataBase;
                _r[4] = _initialSp;
                if (_bits == 16) { _seg[1] = 0x0A00; _seg[2] = 0x0C00; }

                string entry = null;
                var pendingLabels = new List<string>();

                foreach (var line in lines)
                {
                    if (!line.HasMnemonic)
                    {
                        if (!string.IsNullOrEmpty(line.Label)) pendingLabels.Add(line.Label);
                        continue;
                    }
                    string mn = line.MnemonicLower;

                    if (DirectiveInfo.IsDataDefinition(mn) && mn != "ds")
                    {
                        DefineData(line);
                        continue;
                    }
                    if (mn == "equ" || mn == "=" || (mn == "textequ"))
                    {
                        if (string.IsNullOrEmpty(line.Label)) continue;
                        DefineConstant(line.Label, line.OperandText);
                        continue;
                    }
                    if (mn == "proc" && line.LabelIsName)
                    {
                        pendingLabels.Add(line.Label);
                        continue;
                    }
                    if (mn == "end")
                    {
                        if (line.Operands.Count > 0) entry = line.Operands[0].Trim();
                        continue;
                    }
                    if (mn == "endp" || mn.StartsWith(".", StringComparison.Ordinal) && mn != ".exit" ||
                        IsIgnoredDirective(mn))
                    {
                        if (!string.IsNullOrEmpty(line.Label) && !line.LabelIsName) pendingLabels.Add(line.Label);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(line.Label) && !line.LabelIsName) pendingLabels.Add(line.Label);
                    foreach (var l in pendingLabels) _codeLabels[l] = _instrs.Count;
                    pendingLabels.Clear();
                    _instrs.Add(new Instr
                    {
                        Line = line,
                        Mn = mn == ".exit" ? "exit" : mn,
                        Ops = new List<string>(line.Operands),
                        Prefix = line.Prefix == null ? null : line.Prefix.ToLowerInvariant()
                    });
                }
                foreach (var l in pendingLabels) _codeLabels[l] = _instrs.Count;

                if (entry == null)
                {
                    foreach (var candidate in new[] { "_start", "start", "main", "_main" })
                        if (_codeLabels.ContainsKey(candidate) && _codeLabels[candidate] == 0) entry = candidate;
                }
                _ip = 0;
                if (entry != null)
                {
                    int idx;
                    if (_codeLabels.TryGetValue(entry, out idx)) _ip = idx;
                    else _result.Messages.Add("找不到進入點「" + entry + "」，從第一行指令開始執行。");
                }

                foreach (var kv in _options.InitialRegisters)
                {
                    RegRef reg;
                    if (Registers.TryGetValue(kv.Key, out reg)) WriteReg(reg, kv.Value);
                    else _result.Messages.Add("初始值中的「" + kv.Key + "」不是暫存器，已忽略。");
                }

                string mode = _bits + " 位元模式";
                _result.Messages.Add("使用 " + mode + "、" + (_nasm ? "NASM" : "MASM") + " 語法規則；資料區從 " + AsmNumber.Hex(_dataBase, AddrBits) +
                                     " 開始，" + SpName + " 初始值為 " + AsmNumber.Hex(_initialSp, AddrBits) + "（模擬位址）。");
            }

            private static bool IsIgnoredDirective(string mn)
            {
                switch (mn)
                {
                    case "include": case "includelib": case "option": case "title": case "segment": case "ends":
                    case "assume": case "section": case "global": case "extern": case "externdef": case "bits":
                    case "default": case "org": case "align": case "public": case "model": case "proto": case "page":
                    case "subttl": case "comment": case "cpu":
                        return true;
                }
                return false;
            }

            private void DetectMode(List<AsmLine> lines)
            {
                bool uses64 = false, dos = false, flat = false;
                int explicitBits = 0;
                foreach (var l in lines)
                {
                    if (!l.HasMnemonic) continue;
                    string mn = l.MnemonicLower;
                    string ops = (l.OperandText ?? string.Empty).ToLowerInvariant();
                    if (mn == "section" || mn == "global" || mn == "resb" || mn == "resw" || mn == "resd" || mn == "resq" || mn == "bits" || mn == "times")
                        _nasm = true;
                    if (mn == "bits")
                    {
                        int b;
                        if (int.TryParse(ops.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out b)) explicitBits = b;
                    }
                    if (mn == ".model") { if (ops.Contains("flat")) flat = true; else dos = true; }
                    if (ops.Contains("@data")) dos = true;
                    if (mn == "org" && ops.Trim().StartsWith("100h", StringComparison.Ordinal)) dos = true;
                    foreach (Match m in Regex.Matches(ops, @"[a-z][a-z0-9]*"))
                    {
                        int bits = RegisterInfo.X86RegisterBits(m.Value);
                        if (bits == 64) uses64 = true;
                    }
                    if (mn == "syscall") uses64 = true;
                }
                if (explicitBits == 16 || explicitBits == 32 || explicitBits == 64) _bits = explicitBits;
                else if (uses64) _bits = 64;
                else if (dos && !flat) _bits = 16;
                else _bits = 32;
            }

            private void DefineData(AsmLine line)
            {
                string mn = line.MnemonicLower;
                int size;
                switch (mn)
                {
                    case "db": case "byte": case "sbyte": case "resb": size = 1; break;
                    case "dw": case "word": case "sword": case "resw": size = 2; break;
                    case "dd": case "dword": case "sdword": case "real4": case "resd": size = 4; break;
                    case "dq": case "qword": case "real8": case "resq": size = 8; break;
                    case "dt": case "tbyte": case "real10": size = 10; break;
                    default:
                        _result.Messages.Add("第 " + line.LineNumber + " 行：模擬器不處理資料定義 " + line.Mnemonic + "，已略過。");
                        return;
                }

                var symbol = new DataSymbol { Name = line.Label, Address = _dataPtr, ElementSize = size };
                if (!string.IsNullOrEmpty(line.Label))
                {
                    _data[line.Label] = symbol;
                    _dataOrder.Add(symbol);
                }

                if (mn.StartsWith("res", StringComparison.Ordinal))
                {
                    long n = Evaluate(line.OperandText, line.LineNumber);
                    symbol.Count = (int)n;
                    _dataPtr += (ulong)(n * size);
                    return;
                }

                var bytes = new List<byte>();
                int count = 0;
                foreach (var item in AsmLineParser.SplitOperands(line.OperandText))
                    count += EmitDataItem(item, size, bytes, line.LineNumber);
                symbol.Count = count;
                foreach (var b in bytes) _mem[_dataPtr++] = b;
            }

            private int EmitDataItem(string item, int size, List<byte> bytes, int lineNumber)
            {
                string t = item.Trim();
                if (t == "?")
                {
                    for (int i = 0; i < size; i++) bytes.Add(0);
                    return 1;
                }
                if (t.Length >= 2 && (t[0] == '\'' || t[0] == '"') && t[t.Length - 1] == t[0] && (size == 1 || t.Length > 2 + size))
                {
                    string s = t.Substring(1, t.Length - 2);
                    foreach (char c in s)
                    {
                        bytes.Add(c < 256 ? (byte)c : (byte)'?');
                        for (int i = 1; i < size; i++) bytes.Add(0);
                    }
                    return s.Length;
                }
                var dup = DupRegex.Match(t);
                if (dup.Success)
                {
                    long times = Evaluate(dup.Groups[1].Value, lineNumber);
                    if (times < 0 || times > 1000000) throw new SimulationException("第 " + lineNumber + " 行：DUP 次數不合理（" + times + "）");
                    int total = 0;
                    var inner = new List<byte>();
                    int innerCount = 0;
                    foreach (var part in AsmLineParser.SplitOperands(dup.Groups[2].Value))
                        innerCount += EmitDataItem(part, size, inner, lineNumber);
                    for (long i = 0; i < times; i++)
                    {
                        bytes.AddRange(inner);
                        total += innerCount;
                    }
                    return total;
                }
                // $ 在資料定義中代表目前位址（尚未加上本行已輸出的位元組）
                ulong saved = _dataPtr;
                _dataPtr = saved + (ulong)bytes.Count;
                long v = Evaluate(t, lineNumber);
                _dataPtr = saved;
                for (int i = 0; i < size; i++) bytes.Add(i < 8 ? (byte)((ulong)v >> (8 * i)) : (byte)0);
                return 1;
            }

            private void DefineConstant(string name, string expression)
            {
                var eval = new ExpressionEvaluator(ResolveForLoad);
                long v;
                if (eval.TryEvaluate(expression, out v)) _constants[name] = v;
                else _result.Messages.Add("常數 " + name + " = " + expression + " 無法計算（" + eval.Error + "），已略過。");
            }

            private long Evaluate(string expression, int lineNumber)
            {
                var eval = new ExpressionEvaluator(ResolveForLoad);
                long v;
                if (!eval.TryEvaluate(expression, out v))
                    throw new SimulationException("第 " + lineNumber + " 行：無法計算「" + expression + "」（" + eval.Error + "）");
                return v;
            }

            private SymbolValue ResolveForLoad(string name)
            {
                if (name == "$") return new SymbolValue { Value = (long)_dataPtr, Display = "$(" + AsmNumber.Hex(_dataPtr, AddrBits) + ")" };
                long c;
                if (_constants.TryGetValue(name, out c)) return new SymbolValue { Value = c, Display = name };
                DataSymbol d;
                if (_data.TryGetValue(name, out d))
                    return new SymbolValue { Value = (long)d.Address, IsDataLabel = true, ElementSize = d.ElementSize, Length = d.Count, Display = name };
                if (name.Equals("@data", StringComparison.OrdinalIgnoreCase)) return new SymbolValue { Value = 0x0B00, Display = "@data" };
                return null;
            }

            // -----------------------------------------------------------------
            // 暫存器、記憶體
            // -----------------------------------------------------------------

            private ulong ReadReg(RegRef r)
            {
                if (r.IsSegment) return _seg[r.Index];
                return (_r[r.Index] >> r.Shift) & AsmNumber.Mask(r.Bits);
            }

            private void WriteReg(RegRef r, ulong value)
            {
                if (r.IsSegment) { _seg[r.Index] = (ushort)value; return; }
                switch (r.Bits)
                {
                    case 64: _r[r.Index] = value; break;
                    case 32: _r[r.Index] = value & 0xFFFFFFFFUL; break;
                    case 16: _r[r.Index] = (_r[r.Index] & ~0xFFFFUL) | (value & 0xFFFFUL); break;
                    default:
                        ulong mask = 0xFFUL << r.Shift;
                        _r[r.Index] = (_r[r.Index] & ~mask) | ((value & 0xFFUL) << r.Shift);
                        break;
                }
            }

            private RegRef Reg(int index, int bits)
            {
                switch (bits)
                {
                    case 64: return Registers[new[] { "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi" }[index]];
                    case 32: return Registers[new[] { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" }[index]];
                    case 16: return Registers[new[] { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di" }[index]];
                    default: return Registers[new[] { "al", "cl", "dl", "bl", "spl", "bpl", "sil", "dil" }[index]];
                }
            }

            private ulong ReadMem(ulong address, int bits)
            {
                ulong v = 0;
                for (int i = 0; i < bits / 8; i++)
                {
                    byte b;
                    _mem.TryGetValue(address + (ulong)i, out b);
                    v |= (ulong)b << (8 * i);
                }
                return v;
            }

            private void WriteMem(ulong address, int bits, ulong value)
            {
                ulong old = ReadMem(address, bits);
                for (int i = 0; i < bits / 8; i++)
                    _mem[address + (ulong)i] = (byte)(value >> (8 * i));
                _memChanges.Add("[" + AsmNumber.Hex(address, AddrBits) + "]（" + SizeWord(bits) + "）：" + AsmNumber.Hex(old, bits) + " → " + AsmNumber.Hex(value, bits));
            }

            private static string SizeWord(int bits)
            {
                switch (bits)
                {
                    case 8: return "BYTE";
                    case 16: return "WORD";
                    case 32: return "DWORD";
                    case 64: return "QWORD";
                    default: return bits + " 位元";
                }
            }

            private string ReadCString(ulong address, char terminator, int max = 4096)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < max; i++)
                {
                    byte b;
                    _mem.TryGetValue(address + (ulong)i, out b);
                    if ((char)b == terminator) break;
                    sb.Append((char)b);
                }
                return sb.ToString();
            }

            private void Output(string text)
            {
                if (_output.Length > MaxOutput) return;
                _output.Append(text);
            }

            // -----------------------------------------------------------------
            // 運算元解析
            // -----------------------------------------------------------------

            private SymbolValue ResolveAtRuntime(string name)
            {
                RegRef reg;
                if (Registers.TryGetValue(name, out reg))
                {
                    ulong v = ReadReg(reg);
                    return new SymbolValue { Value = (long)v, IsRegister = true, Display = name.ToUpperInvariant() + "(" + AsmNumber.Hex(v, reg.Bits) + ")" };
                }
                long c;
                if (_constants.TryGetValue(name, out c)) return new SymbolValue { Value = c, Display = name + "(" + c + ")" };
                DataSymbol d;
                if (_data.TryGetValue(name, out d))
                    return new SymbolValue
                    {
                        Value = (long)d.Address, IsDataLabel = true, ElementSize = d.ElementSize, Length = d.Count,
                        Display = name + "(" + AsmNumber.Hex(d.Address, AddrBits) + ")"
                    };
                int idx;
                if (_codeLabels.TryGetValue(name, out idx))
                    return new SymbolValue { Value = (long)CodeAddress(idx), IsCodeLabel = true, Display = name };
                if (name.Equals("@data", StringComparison.OrdinalIgnoreCase)) return new SymbolValue { Value = 0x0B00, Display = "@data(0x0B00)" };
                if (name == "$") return new SymbolValue { Value = (long)CodeAddress(_ip), Display = "$" };
                return null;
            }

            private ulong CodeAddress(int index)
            {
                return _codeBase + (ulong)index * 4UL;
            }

            private int SizeKeywordBits(string keyword)
            {
                switch (keyword.ToLowerInvariant())
                {
                    case "byte": case "sbyte": return 8;
                    case "word": case "sword": return 16;
                    case "dword": case "sdword": case "real4": return 32;
                    case "qword": case "real8": return 64;
                }
                return 0;
            }

            private Operand ParseOperand(string text, int sizeHint)
            {
                string t = text.Trim();
                int explicitBits = 0;
                var size = SizePrefixRegex.Match(t);
                if (size.Success && size.Length < t.Length)
                {
                    explicitBits = SizeKeywordBits(size.Groups[1].Value);
                    t = t.Substring(size.Length).Trim();
                }
                var seg = SegOverrideRegex.Match(t);
                if (seg.Success && seg.Length < t.Length) t = t.Substring(seg.Length).Trim();

                RegRef reg;
                if (Registers.TryGetValue(t, out reg))
                    return new Operand { Kind = OpKind.Register, Reg = reg, Bits = reg.Bits, Text = t };

                bool bracket = t.IndexOf('[') >= 0;
                string expr = bracket ? t.Replace("[", "+(").Replace("]", ")") : t;
                if (expr.StartsWith("+", StringComparison.Ordinal)) expr = expr.Substring(1);

                var eval = new ExpressionEvaluator(ResolveAtRuntime);
                long value;
                if (!eval.TryEvaluate(expr, out value))
                    throw new SimulationException("無法解析運算元「" + text.Trim() + "」：" + eval.Error);

                bool memory = bracket || (!_nasm && eval.UsedDataLabel && !eval.UsedOffsetOperator && !eval.UsedRegister);
                if (!memory)
                    return new Operand { Kind = OpKind.Immediate, Imm = unchecked((ulong)value), Bits = explicitBits > 0 ? explicitBits : sizeHint, Text = t };

                int bits = explicitBits;
                if (bits == 0 && eval.FirstDataLabel != null && eval.FirstDataLabel.ElementSize <= 8)
                    bits = eval.FirstDataLabel.ElementSize * 8;
                if (bits == 0) bits = sizeHint;
                ulong address = unchecked((ulong)value) & AsmNumber.Mask(AddrBits);
                return new Operand
                {
                    Kind = OpKind.Memory,
                    Address = address,
                    Bits = bits,
                    Text = t,
                    AddressText = eval.Display + " = " + AsmNumber.Hex(address, AddrBits)
                };
            }

            private ulong Get(Operand op)
            {
                switch (op.Kind)
                {
                    case OpKind.Register: return ReadReg(op.Reg);
                    case OpKind.Immediate: return op.Imm & AsmNumber.Mask(op.Bits > 0 ? op.Bits : 64);
                    default:
                        RequireSize(op);
                        return ReadMem(op.Address, op.Bits);
                }
            }

            private void Set(Operand op, ulong value)
            {
                switch (op.Kind)
                {
                    case OpKind.Register: WriteReg(op.Reg, value); break;
                    case OpKind.Memory:
                        RequireSize(op);
                        WriteMem(op.Address, op.Bits, value & AsmNumber.Mask(op.Bits));
                        break;
                    default: throw new SimulationException("目的運算元不能是立即值「" + op.Text + "」");
                }
            }

            private static void RequireSize(Operand op)
            {
                if (op.Bits == 0)
                    throw new SimulationException("無法判斷「" + op.Text + "」的大小，請加上 BYTE PTR／WORD PTR／DWORD PTR");
            }

            /// <summary>解析雙運算元指令（dest, src），並檢查大小。</summary>
            private void ParseTwo(Instr ins, out Operand dst, out Operand src, bool allowSizeMismatch = false)
            {
                if (ins.Ops.Count != 2) throw new SimulationException(ins.Mn.ToUpperInvariant() + " 需要 2 個運算元");
                var first = ParseOperand(ins.Ops[0], 0);
                var second = ParseOperand(ins.Ops[1], first.Kind == OpKind.Immediate ? 0 : first.Bits);
                if (first.Bits == 0 && second.Kind != OpKind.Immediate) first = ParseOperand(ins.Ops[0], second.Bits);
                dst = first;
                src = second;
                if (dst.Kind == OpKind.Memory && src.Kind == OpKind.Memory)
                    throw new SimulationException("x86 不允許兩個運算元都是記憶體");
                if (src.Kind == OpKind.Immediate) src.Bits = dst.Bits;
                if (!allowSizeMismatch && dst.Bits != 0 && src.Bits != 0 && dst.Bits != src.Bits)
                    throw new SimulationException("運算元大小不一致：" + dst.Text + "（" + dst.Bits + " 位元）與 " + src.Text + "（" + src.Bits + " 位元）");
                RequireSize(dst);
            }

            private Operand ParseOne(Instr ins, int sizeHint = 0)
            {
                if (ins.Ops.Count < 1) throw new SimulationException(ins.Mn.ToUpperInvariant() + " 需要運算元");
                var op = ParseOperand(ins.Ops[0], sizeHint);
                return op;
            }

            private string Show(Operand op)
            {
                string name = op.Kind == OpKind.Register ? op.Reg.Name.ToUpperInvariant()
                            : op.Kind == OpKind.Memory ? "[" + AsmNumber.Hex(op.Address, AddrBits) + "]"
                            : op.Text;
                return name;
            }

            private void NoteAddress(Operand op)
            {
                if (op.Kind == OpKind.Memory && op.AddressText != null)
                    _derivation.Add("有效位址：" + op.AddressText);
            }

            // -----------------------------------------------------------------
            // 旗標
            // -----------------------------------------------------------------

            private void ApplyFlags(FlagMath.FlagResult f, bool updateCarry, bool explain)
            {
                if (updateCarry) _cf = f.CF;
                _of = f.OF; _zf = f.ZF; _sf = f.SF; _pf = f.PF; _af = f.AF;
                if (!explain) return;
                foreach (var e in f.Explanations)
                {
                    if (!updateCarry && e.StartsWith("CF=", StringComparison.Ordinal)) continue;
                    _derivation.Add("　" + e);
                }
                if (!updateCarry) _derivation.Add("　CF 不變（INC/DEC 不影響 CF）");
            }

            private void SetLogicFlags(ulong res, int bits)
            {
                var f = FlagMath.Logic(res, bits);
                _cf = false; _of = false; _af = false;
                _zf = f.ZF; _sf = f.SF; _pf = f.PF;
                _derivation.Add("　旗標：CF=0 OF=0 ZF=" + B(_zf) + " SF=" + B(_sf) + " PF=" + B(_pf));
            }

            private void SetSzp(ulong res, int bits)
            {
                res &= AsmNumber.Mask(bits);
                _zf = res == 0;
                _sf = FlagMath.Msb(res, bits);
                _pf = FlagMath.Parity(res);
            }

            private static string B(bool b)
            {
                return b ? "1" : "0";
            }

            private string FlagsText()
            {
                return "CF=" + B(_cf) + " ZF=" + B(_zf) + " SF=" + B(_sf) + " OF=" + B(_of) + " PF=" + B(_pf) + " AF=" + B(_af) + " DF=" + B(_df);
            }

            private ulong PackFlags()
            {
                ulong f = 0x2;
                if (_cf) f |= 1UL;
                if (_pf) f |= 1UL << 2;
                if (_af) f |= 1UL << 4;
                if (_zf) f |= 1UL << 6;
                if (_sf) f |= 1UL << 7;
                if (_df) f |= 1UL << 10;
                if (_of) f |= 1UL << 11;
                return f;
            }

            private void UnpackFlags(ulong f)
            {
                _cf = (f & 1UL) != 0;
                _pf = (f & (1UL << 2)) != 0;
                _af = (f & (1UL << 4)) != 0;
                _zf = (f & (1UL << 6)) != 0;
                _sf = (f & (1UL << 7)) != 0;
                _df = (f & (1UL << 10)) != 0;
                _of = (f & (1UL << 11)) != 0;
            }

            /// <summary>條件碼求值並說明，例如 L → SF≠OF。</summary>
            private bool Condition(string cc, out string text)
            {
                bool r;
                string rule;
                switch (cc)
                {
                    case "e": case "z": r = _zf; rule = "ZF=1（相等／為 0），目前 ZF=" + B(_zf); break;
                    case "ne": case "nz": r = !_zf; rule = "ZF=0（不相等），目前 ZF=" + B(_zf); break;
                    case "a": case "nbe": r = !_cf && !_zf; rule = "CF=0 且 ZF=0（無號大於），目前 CF=" + B(_cf) + "、ZF=" + B(_zf); break;
                    case "ae": case "nb": case "nc": r = !_cf; rule = "CF=0（無號大於等於），目前 CF=" + B(_cf); break;
                    case "b": case "nae": case "c": r = _cf; rule = "CF=1（無號小於），目前 CF=" + B(_cf); break;
                    case "be": case "na": r = _cf || _zf; rule = "CF=1 或 ZF=1（無號小於等於），目前 CF=" + B(_cf) + "、ZF=" + B(_zf); break;
                    case "g": case "nle": r = !_zf && _sf == _of; rule = "ZF=0 且 SF=OF（有號大於），目前 ZF=" + B(_zf) + "、SF=" + B(_sf) + "、OF=" + B(_of); break;
                    case "ge": case "nl": r = _sf == _of; rule = "SF=OF（有號大於等於），目前 SF=" + B(_sf) + "、OF=" + B(_of); break;
                    case "l": case "nge": r = _sf != _of; rule = "SF≠OF（有號小於），目前 SF=" + B(_sf) + "、OF=" + B(_of); break;
                    case "le": case "ng": r = _zf || _sf != _of; rule = "ZF=1 或 SF≠OF（有號小於等於），目前 ZF=" + B(_zf) + "、SF=" + B(_sf) + "、OF=" + B(_of); break;
                    case "s": r = _sf; rule = "SF=1（負數），目前 SF=" + B(_sf); break;
                    case "ns": r = !_sf; rule = "SF=0，目前 SF=" + B(_sf); break;
                    case "o": r = _of; rule = "OF=1（溢位），目前 OF=" + B(_of); break;
                    case "no": r = !_of; rule = "OF=0，目前 OF=" + B(_of); break;
                    case "p": case "pe": r = _pf; rule = "PF=1，目前 PF=" + B(_pf); break;
                    case "np": case "po": r = !_pf; rule = "PF=0，目前 PF=" + B(_pf); break;
                    default: throw new SimulationException("不認識的條件碼「" + cc + "」");
                }
                text = "條件 " + rule + " → " + (r ? "成立" : "不成立");
                return r;
            }

            private static readonly HashSet<string> ConditionCodes = new HashSet<string>
            {
                "e", "z", "ne", "nz", "a", "nbe", "ae", "nb", "nc", "b", "nae", "c", "be", "na", "g", "nle",
                "ge", "nl", "l", "nge", "le", "ng", "s", "ns", "o", "no", "p", "pe", "np", "po"
            };

            // -----------------------------------------------------------------
            // 指令執行
            // -----------------------------------------------------------------

            private void Execute(Instr ins, ref int next)
            {
                string mn = ins.Mn;

                if (ins.Prefix != null && IsStringInstruction(mn)) { ExecuteRep(ins); return; }
                if (IsStringInstruction(mn) && ins.Ops.Count == 0) { StringStep(mn, true); return; }

                switch (mn)
                {
                    case "mov": Mov(ins); return;
                    case "movzx": case "movsx": case "movsxd": MovExtend(ins); return;
                    case "lea": Lea(ins); return;
                    case "xchg": Xchg(ins); return;
                    case "push": Push(ins); return;
                    case "pop": Pop(ins); return;
                    case "pushad": case "pusha": PushAll(mn == "pusha" ? 16 : 32); return;
                    case "popad": case "popa": PopAll(mn == "popa" ? 16 : 32); return;
                    case "pushfd": case "pushf": case "pushfq": PushValue(PackFlags(), _bits == 64 ? 64 : mn == "pushf" ? 16 : 32, "EFLAGS"); return;
                    case "popfd": case "popf": case "popfq": UnpackFlags(PopValue(_bits == 64 ? 64 : mn == "popf" ? 16 : 32)); _derivation.Add("從堆疊取回旗標"); return;
                    case "add": case "adc": case "sub": case "sbb": case "cmp": Arith(ins); return;
                    case "inc": case "dec": IncDec(ins); return;
                    case "neg": Neg(ins); return;
                    case "not": Not(ins); return;
                    case "and": case "or": case "xor": case "test": Logic(ins); return;
                    case "shl": case "sal": case "shr": case "sar": case "rol": case "ror": case "rcl": case "rcr": Shift(ins); return;
                    case "mul": case "imul": Multiply(ins); return;
                    case "div": case "idiv": Divide(ins); return;
                    case "cbw": Extend("al", "ax"); return;
                    case "cwde": Extend("ax", "eax"); return;
                    case "cdqe": Extend("eax", "rax"); return;
                    case "cwd": ExtendPair("ax", "dx"); return;
                    case "cdq": ExtendPair("eax", "edx"); return;
                    case "cqo": ExtendPair("rax", "rdx"); return;
                    case "bswap": Bswap(ins); return;
                    case "jmp": Jump(ins, ref next, true, "無條件跳躍"); return;
                    case "jcxz": JumpIfZero(ins, ref next, "cx"); return;
                    case "jecxz": JumpIfZero(ins, ref next, "ecx"); return;
                    case "jrcxz": JumpIfZero(ins, ref next, "rcx"); return;
                    case "loop": case "loope": case "loopz": case "loopne": case "loopnz": Loop(ins, ref next); return;
                    case "call": Call(ins, ref next); return;
                    case "invoke": Invoke(ins, ref next); return;
                    case "ret": case "retn": case "retf": Ret(ins, ref next); return;
                    case "leave": Leave(); return;
                    case "enter": Enter(ins); return;
                    case "nop": _derivation.Add("不做任何事"); return;
                    case "hlt": Halt("HLT：CPU 停止"); return;
                    case "exit": Halt("exit：程式結束"); return;
                    case "clc": _cf = false; _derivation.Add("CF ← 0"); return;
                    case "stc": _cf = true; _derivation.Add("CF ← 1"); return;
                    case "cmc": _cf = !_cf; _derivation.Add("CF ← NOT CF = " + B(_cf)); return;
                    case "cld": _df = false; _derivation.Add("DF ← 0（字串指令往高位址前進）"); return;
                    case "std": _df = true; _derivation.Add("DF ← 1（字串指令往低位址前進）"); return;
                    case "cli": case "sti": _derivation.Add("中斷旗標（模擬器不處理硬體中斷）"); return;
                    case "int": Interrupt(ins); return;
                    case "syscall": Syscall(); return;
                }

                if (mn.Length > 1 && mn[0] == 'j' && ConditionCodes.Contains(mn.Substring(1)))
                {
                    string text;
                    bool taken = Condition(mn.Substring(1), out text);
                    Jump(ins, ref next, taken, mn.ToUpperInvariant() + " " + text);
                    return;
                }
                if (mn.StartsWith("set", StringComparison.Ordinal) && ConditionCodes.Contains(mn.Substring(3)))
                {
                    string text;
                    bool r = Condition(mn.Substring(3), out text);
                    var dst = ParseOne(ins, 8);
                    Set(dst, r ? 1UL : 0UL);
                    _derivation.Add(text + "，所以 " + Show(dst) + " ← " + (r ? "1" : "0"));
                    return;
                }
                if (mn.StartsWith("cmov", StringComparison.Ordinal) && ConditionCodes.Contains(mn.Substring(4)))
                {
                    string text;
                    bool r = Condition(mn.Substring(4), out text);
                    Operand dst, src;
                    ParseTwo(ins, out dst, out src);
                    if (r) Set(dst, Get(src));
                    _derivation.Add(text + (r ? "，所以 " + Show(dst) + " ← " + AsmNumber.Hex(Get(src), dst.Bits) : "，不搬移"));
                    return;
                }

                throw new SimulationException("模擬器目前不支援「" + ins.Mn.ToUpperInvariant() + "」指令（可在「程式碼分析」頁查看它的說明）");
            }

            private void Halt(string reason)
            {
                _halt = true;
                _stopReason = reason;
                _derivation.Add(reason);
            }

            // ---------------- 資料搬移 ----------------

            private void Mov(Instr ins)
            {
                Operand dst, src;
                ParseTwo(ins, out dst, out src);
                if (dst.Kind == OpKind.Register && dst.Reg.IsSegment && src.Kind == OpKind.Immediate)
                    throw new SimulationException("不能把立即值直接放進區段暫存器（要先放到 AX）");
                NoteAddress(dst);
                NoteAddress(src);
                ulong v = Get(src) & AsmNumber.Mask(dst.Bits);
                Set(dst, v);
                _derivation.Add(Show(dst) + " ← " + DescribeSource(src) + " = " + AsmNumber.Describe(v, dst.Bits));
            }

            private string DescribeSource(Operand src)
            {
                switch (src.Kind)
                {
                    case OpKind.Register: return src.Reg.Name.ToUpperInvariant();
                    case OpKind.Memory: return "記憶體[" + AsmNumber.Hex(src.Address, AddrBits) + "]";
                    default: return src.Text;
                }
            }

            private void MovExtend(Instr ins)
            {
                if (ins.Ops.Count != 2) throw new SimulationException(ins.Mn.ToUpperInvariant() + " 需要 2 個運算元");
                var dst = ParseOperand(ins.Ops[0], 0);
                var src = ParseOperand(ins.Ops[1], 0);
                if (dst.Kind != OpKind.Register) throw new SimulationException(ins.Mn.ToUpperInvariant() + " 的目的必須是暫存器");
                if (src.Bits == 0) throw new SimulationException("無法判斷來源大小，請加上 BYTE PTR 或 WORD PTR");
                if (src.Bits >= dst.Bits) throw new SimulationException("來源（" + src.Bits + " 位元）必須比目的（" + dst.Bits + " 位元）小");
                NoteAddress(src);
                ulong v = Get(src);
                ulong result;
                if (ins.Mn == "movzx")
                {
                    result = v;
                    _derivation.Add("零延伸：" + AsmNumber.Binary(v, src.Bits) + " 前面補 0 → " + AsmNumber.Hex(result, dst.Bits));
                }
                else
                {
                    result = unchecked((ulong)AsmNumber.ToSigned(v, src.Bits)) & AsmNumber.Mask(dst.Bits);
                    _derivation.Add("符號延伸：最高位元為 " + (FlagMath.Msb(v, src.Bits) ? "1，前面補 1" : "0，前面補 0") + " → " + AsmNumber.Describe(result, dst.Bits));
                }
                Set(dst, result);
                _derivation.Add(Show(dst) + " ← " + AsmNumber.Describe(result, dst.Bits));
            }

            private void Lea(Instr ins)
            {
                if (ins.Ops.Count != 2) throw new SimulationException("LEA 需要 2 個運算元");
                var dst = ParseOperand(ins.Ops[0], 0);
                var src = ParseOperand(ins.Ops[1], dst.Bits);
                if (dst.Kind != OpKind.Register) throw new SimulationException("LEA 的目的必須是暫存器");
                if (src.Kind != OpKind.Memory)
                {
                    if (src.Kind == OpKind.Immediate && _nasm) src = new Operand { Kind = OpKind.Memory, Address = src.Imm, Bits = dst.Bits, AddressText = src.Text + " = " + AsmNumber.Hex(src.Imm, AddrBits) };
                    else throw new SimulationException("LEA 的來源必須是記憶體運算元，例如 [EBX+4]");
                }
                ulong ea = src.Address & AsmNumber.Mask(dst.Bits);
                Set(dst, ea);
                _derivation.Add("有效位址：" + src.AddressText + "（LEA 只計算位址，不讀取記憶體）");
                _derivation.Add(Show(dst) + " ← " + AsmNumber.Describe(ea, dst.Bits));
            }

            private void Xchg(Instr ins)
            {
                Operand a, b;
                ParseTwo(ins, out a, out b);
                ulong va = Get(a), vb = Get(b);
                Set(a, vb);
                Set(b, va);
                _derivation.Add(Show(a) + " ↔ " + Show(b) + "：" + Show(a) + " = " + AsmNumber.Hex(vb, a.Bits) + "、" + Show(b) + " = " + AsmNumber.Hex(va, b.Bits));
            }

            private void Extend(string from, string to)
            {
                var f = Registers[from];
                var t = Registers[to];
                ulong v = ReadReg(f);
                ulong r = unchecked((ulong)AsmNumber.ToSigned(v, f.Bits)) & AsmNumber.Mask(t.Bits);
                WriteReg(t, r);
                _derivation.Add(to.ToUpperInvariant() + " ← 符號延伸(" + from.ToUpperInvariant() + " = " + AsmNumber.Hex(v, f.Bits) + ") = " + AsmNumber.Describe(r, t.Bits));
            }

            private void ExtendPair(string low, string high)
            {
                var l = Registers[low];
                var h = Registers[high];
                ulong v = ReadReg(l);
                bool negative = FlagMath.Msb(v, l.Bits);
                ulong hv = negative ? AsmNumber.Mask(h.Bits) : 0;
                WriteReg(h, hv);
                _derivation.Add(low.ToUpperInvariant() + " = " + AsmNumber.Describe(v, l.Bits) + "，最高位元為 " + (negative ? "1" : "0") +
                                "，所以 " + high.ToUpperInvariant() + " ← " + AsmNumber.Hex(hv, h.Bits) + "（" + high.ToUpperInvariant() + ":" + low.ToUpperInvariant() + " 成為 " + (l.Bits * 2) + " 位元有號數）");
            }

            private void Bswap(Instr ins)
            {
                var op = ParseOne(ins);
                if (op.Kind != OpKind.Register || op.Bits < 32) throw new SimulationException("BSWAP 需要 32 或 64 位元暫存器");
                ulong v = Get(op), r = 0;
                int n = op.Bits / 8;
                for (int i = 0; i < n; i++) r |= ((v >> (8 * i)) & 0xFF) << (8 * (n - 1 - i));
                Set(op, r);
                _derivation.Add(Show(op) + "：" + AsmNumber.Hex(v, op.Bits) + " 反轉位元組順序 → " + AsmNumber.Hex(r, op.Bits));
            }

            // ---------------- 堆疊 ----------------

            private RegRef StackReg
            {
                get { return Registers[_bits == 64 ? "rsp" : _bits == 16 ? "sp" : "esp"]; }
            }

            private void PushValue(ulong value, int bits, string what)
            {
                var sp = StackReg;
                ulong old = ReadReg(sp);
                ulong nsp = (old - (ulong)(bits / 8)) & AsmNumber.Mask(sp.Bits);
                WriteReg(sp, nsp);
                WriteMem(nsp, bits, value & AsmNumber.Mask(bits));
                _derivation.Add(SpName + " = " + AsmNumber.Hex(old, sp.Bits) + " - " + (bits / 8) + " = " + AsmNumber.Hex(nsp, sp.Bits) +
                                "；記憶體[" + AsmNumber.Hex(nsp, sp.Bits) + "] ← " + what + " = " + AsmNumber.Hex(value, bits));
            }

            private ulong PopValue(int bits)
            {
                var sp = StackReg;
                ulong old = ReadReg(sp);
                ulong v = ReadMem(old, bits);
                ulong nsp = (old + (ulong)(bits / 8)) & AsmNumber.Mask(sp.Bits);
                WriteReg(sp, nsp);
                _derivation.Add("讀取記憶體[" + AsmNumber.Hex(old, sp.Bits) + "] = " + AsmNumber.Hex(v, bits) + "；" + SpName + " = " +
                                AsmNumber.Hex(old, sp.Bits) + " + " + (bits / 8) + " = " + AsmNumber.Hex(nsp, sp.Bits));
                return v;
            }

            private int DefaultPushBits(Operand op)
            {
                if (op.Kind == OpKind.Immediate) return _bits;
                if (op.Bits == 0) return _bits;
                if (_bits == 64 && op.Bits == 32) throw new SimulationException("x64 模式不能 PUSH 32 位元運算元");
                return op.Bits;
            }

            private void Push(Instr ins)
            {
                var op = ParseOne(ins, _bits);
                int bits = DefaultPushBits(op);
                ulong v = op.Kind == OpKind.Immediate ? unchecked((ulong)AsmNumber.ToSigned(op.Imm, 32)) & AsmNumber.Mask(bits) : Get(op);
                NoteAddress(op);
                PushValue(v, bits, Show(op));
            }

            private void Pop(Instr ins)
            {
                var op = ParseOne(ins, _bits);
                if (op.Kind == OpKind.Immediate) throw new SimulationException("POP 的目的不能是立即值");
                int bits = op.Bits == 0 ? _bits : op.Bits;
                if (op.Bits == 0) op.Bits = bits;
                ulong v = PopValue(bits);
                Set(op, v);
                _derivation.Add(Show(op) + " ← " + AsmNumber.Describe(v, bits));
            }

            private void PushAll(int bits)
            {
                if (_bits == 64) throw new SimulationException("x64 模式沒有 PUSHAD／PUSHA");
                ulong originalSp = ReadReg(StackReg);
                int[] order = { 0, 1, 2, 3, 4, 5, 6, 7 };
                foreach (int i in order)
                {
                    var r = Reg(i, bits);
                    ulong v = i == 4 ? originalSp & AsmNumber.Mask(bits) : ReadReg(r);
                    PushValue(v, bits, r.Name.ToUpperInvariant());
                }
            }

            private void PopAll(int bits)
            {
                if (_bits == 64) throw new SimulationException("x64 模式沒有 POPAD／POPA");
                for (int i = 7; i >= 0; i--)
                {
                    ulong v = PopValue(bits);
                    if (i == 4) { _derivation.Add("（略過存起來的 " + (bits == 32 ? "ESP" : "SP") + "）"); continue; }
                    WriteReg(Reg(i, bits), v);
                }
            }

            // ---------------- 算術 ----------------

            private void Arith(Instr ins)
            {
                Operand dst, src;
                ParseTwo(ins, out dst, out src);
                NoteAddress(dst);
                NoteAddress(src);
                ulong a = Get(dst);
                ulong b = Get(src) & AsmNumber.Mask(dst.Bits);
                if (src.Kind == OpKind.Immediate && dst.Bits == 64) b = unchecked((ulong)AsmNumber.ToSigned(src.Imm, 32));
                bool carry = (ins.Mn == "adc" || ins.Mn == "sbb") && _cf;
                bool isAdd = ins.Mn == "add" || ins.Mn == "adc";
                var f = isAdd ? FlagMath.Add(a, b, dst.Bits, carry) : FlagMath.Sub(a, b, dst.Bits, carry);

                string sign = isAdd ? " + " : " - ";
                string carryText = carry ? (isAdd ? " + CF(1)" : " - CF(1)") : (ins.Mn == "adc" || ins.Mn == "sbb" ? (isAdd ? " + CF(0)" : " - CF(0)") : string.Empty);
                string line = AsmNumber.Hex(a, dst.Bits) + sign + AsmNumber.Hex(b, dst.Bits) + carryText + " = " + AsmNumber.Describe(f.Result, dst.Bits) +
                              "（十進位：" + a.ToString(CultureInfo.InvariantCulture) + sign + b.ToString(CultureInfo.InvariantCulture) + carryText + "）";
                if (ins.Mn == "cmp")
                    _derivation.Add("比較 " + Show(dst) + " 與 " + DescribeSource(src) + "：計算 " + line + "，結果不存回，只設定旗標");
                else
                {
                    Set(dst, f.Result);
                    _derivation.Add(Show(dst) + " ← " + line);
                }
                ApplyFlags(f, true, true);
            }

            private void IncDec(Instr ins)
            {
                var op = ParseOne(ins);
                RequireSize(op);
                NoteAddress(op);
                ulong a = Get(op);
                var f = ins.Mn == "inc" ? FlagMath.Add(a, 1, op.Bits, false) : FlagMath.Sub(a, 1, op.Bits, false);
                Set(op, f.Result);
                _derivation.Add(Show(op) + " ← " + AsmNumber.Hex(a, op.Bits) + (ins.Mn == "inc" ? " + 1" : " - 1") + " = " + AsmNumber.Describe(f.Result, op.Bits));
                ApplyFlags(f, false, true);
            }

            private void Neg(Instr ins)
            {
                var op = ParseOne(ins);
                RequireSize(op);
                ulong a = Get(op);
                var f = FlagMath.Sub(0, a, op.Bits, false);
                Set(op, f.Result);
                _derivation.Add(Show(op) + " ← 0 - " + AsmNumber.Hex(a, op.Bits) + " = " + AsmNumber.Describe(f.Result, op.Bits) + "（取二補數：反相後加 1）");
                ApplyFlags(f, true, true);
            }

            private void Not(Instr ins)
            {
                var op = ParseOne(ins);
                RequireSize(op);
                ulong a = Get(op);
                ulong r = ~a & AsmNumber.Mask(op.Bits);
                Set(op, r);
                _derivation.Add(Show(op) + " ← NOT " + AsmNumber.Hex(a, op.Bits) + " = " + AsmNumber.Hex(r, op.Bits) + "（每個位元反相，不影響旗標）");
                if (op.Bits <= 32)
                    _derivation.Add("　" + AsmNumber.Binary(a, op.Bits) + " → " + AsmNumber.Binary(r, op.Bits));
            }

            private void Logic(Instr ins)
            {
                Operand dst, src;
                ParseTwo(ins, out dst, out src);
                NoteAddress(dst);
                NoteAddress(src);
                ulong a = Get(dst), b = Get(src) & AsmNumber.Mask(dst.Bits);
                ulong r;
                string op;
                switch (ins.Mn)
                {
                    case "and": case "test": r = a & b; op = "AND"; break;
                    case "or": r = a | b; op = "OR"; break;
                    default: r = a ^ b; op = "XOR"; break;
                }
                r &= AsmNumber.Mask(dst.Bits);
                if (ins.Mn != "test") Set(dst, r);
                string head = ins.Mn == "test" ? "測試 " : Show(dst) + " ← ";
                _derivation.Add(head + AsmNumber.Hex(a, dst.Bits) + " " + op + " " + AsmNumber.Hex(b, dst.Bits) + " = " + AsmNumber.Hex(r, dst.Bits) +
                                (ins.Mn == "test" ? "（結果不存回）" : string.Empty));
                if (dst.Bits <= 16)
                {
                    _derivation.Add("　  " + AsmNumber.Binary(a, dst.Bits));
                    _derivation.Add("　" + op.PadRight(4).Substring(0, 3) + " " + AsmNumber.Binary(b, dst.Bits));
                    _derivation.Add("　  = " + AsmNumber.Binary(r, dst.Bits));
                }
                if (ins.Mn == "xor" && dst.Kind == OpKind.Register && src.Kind == OpKind.Register && dst.Reg.Name == src.Reg.Name)
                    _derivation.Add("　自己 XOR 自己一定是 0（清零技巧）");
                SetLogicFlags(r, dst.Bits);
            }

            private void Shift(Instr ins)
            {
                if (ins.Ops.Count < 1) throw new SimulationException(ins.Mn.ToUpperInvariant() + " 需要運算元");
                var dst = ParseOperand(ins.Ops[0], 0);
                RequireSize(dst);
                int count = 1;
                string countText = "1";
                if (ins.Ops.Count >= 2)
                {
                    var c = ParseOperand(ins.Ops[1], 8);
                    if (c.Kind == OpKind.Register && c.Reg.Name != "cl") throw new SimulationException("移位次數只能是立即值或 CL");
                    count = (int)(Get(c) & 0xFF);
                    countText = c.Kind == OpKind.Register ? "CL(" + count + ")" : count.ToString(CultureInfo.InvariantCulture);
                }
                int bits = dst.Bits;
                count &= bits == 64 ? 0x3F : 0x1F;
                NoteAddress(dst);
                ulong v = Get(dst);
                ulong mask = AsmNumber.Mask(bits);
                if (count == 0)
                {
                    _derivation.Add("移位次數為 0，什麼都不改變");
                    return;
                }

                ulong r;
                bool cf;
                string desc;
                switch (ins.Mn)
                {
                    case "shl":
                    case "sal":
                        r = count >= 64 ? 0 : (v << count) & mask;
                        cf = count <= bits && ((v >> (bits - count)) & 1UL) != 0;
                        if (count == 1) _of = FlagMath.Msb(r, bits) ^ cf;
                        desc = "左移 " + countText + " 位元（右邊補 0），相當於乘以 2^" + count;
                        break;
                    case "shr":
                        r = count >= 64 ? 0 : v >> count;
                        cf = ((v >> (count - 1)) & 1UL) != 0;
                        if (count == 1) _of = FlagMath.Msb(v, bits);
                        desc = "邏輯右移 " + countText + " 位元（左邊補 0），相當於無號數除以 2^" + count;
                        break;
                    case "sar":
                        {
                            long s = AsmNumber.ToSigned(v, bits);
                            r = unchecked((ulong)(s >> Math.Min(count, 63))) & mask;
                            cf = count - 1 < bits ? ((v >> (count - 1)) & 1UL) != 0 : FlagMath.Msb(v, bits);
                            if (count == 1) _of = false;
                            desc = "算術右移 " + countText + " 位元（左邊補符號位元 " + (FlagMath.Msb(v, bits) ? "1" : "0") + "），相當於有號數除以 2^" + count + "（向下取整）";
                            break;
                        }
                    case "rol":
                        {
                            int c = count % bits;
                            r = c == 0 ? v : ((v << c) | (v >> (bits - c))) & mask;
                            cf = (r & 1UL) != 0;
                            if (count == 1) _of = FlagMath.Msb(r, bits) ^ cf;
                            desc = "循環左移 " + countText + " 位元（移出的高位元繞回低位元）";
                            break;
                        }
                    case "ror":
                        {
                            int c = count % bits;
                            r = c == 0 ? v : ((v >> c) | (v << (bits - c))) & mask;
                            cf = FlagMath.Msb(r, bits);
                            if (count == 1) _of = FlagMath.Msb(r, bits) ^ (((r >> (bits - 2)) & 1UL) != 0);
                            desc = "循環右移 " + countText + " 位元（移出的低位元繞回高位元）";
                            break;
                        }
                    default:
                        {
                            // RCL / RCR：CF 參與旋轉，逐位元模擬
                            r = v;
                            cf = _cf;
                            int c = count % (bits + 1);
                            for (int i = 0; i < c; i++)
                            {
                                if (ins.Mn == "rcl")
                                {
                                    bool outBit = FlagMath.Msb(r, bits);
                                    r = ((r << 1) | (cf ? 1UL : 0UL)) & mask;
                                    cf = outBit;
                                }
                                else
                                {
                                    bool outBit = (r & 1UL) != 0;
                                    r = (r >> 1) | (cf ? 1UL << (bits - 1) : 0UL);
                                    cf = outBit;
                                }
                            }
                            if (count == 1) _of = ins.Mn == "rcl" ? FlagMath.Msb(r, bits) ^ cf : FlagMath.Msb(r, bits) ^ (((r >> (bits - 2)) & 1UL) != 0);
                            desc = (ins.Mn == "rcl" ? "帶進位循環左移 " : "帶進位循環右移 ") + countText + " 位元（CF 也參與旋轉）";
                            break;
                        }
                }

                Set(dst, r);
                _cf = cf;
                if (ins.Mn == "shl" || ins.Mn == "sal" || ins.Mn == "shr" || ins.Mn == "sar") SetSzp(r, bits);
                _derivation.Add(Show(dst) + " = " + AsmNumber.Hex(v, bits) + " " + desc + " → " + AsmNumber.Describe(r, bits));
                if (bits <= 32)
                {
                    _derivation.Add("　移位前 " + AsmNumber.Binary(v, bits));
                    _derivation.Add("　移位後 " + AsmNumber.Binary(r, bits));
                }
                _derivation.Add("　CF=" + B(_cf) + "（最後移出／繞過的位元）" + (count == 1 ? "，OF=" + B(_of) : "（移位多於 1 次時 OF 未定義）"));
            }

            private void Multiply(Instr ins)
            {
                if (ins.Mn == "imul" && ins.Ops.Count >= 2)
                {
                    // 雙／三運算元 IMUL
                    var dst = ParseOperand(ins.Ops[0], 0);
                    if (dst.Kind != OpKind.Register) throw new SimulationException("IMUL 的目的必須是暫存器");
                    Operand a, b;
                    if (ins.Ops.Count == 2) { a = dst; b = ParseOperand(ins.Ops[1], dst.Bits); }
                    else { a = ParseOperand(ins.Ops[1], dst.Bits); b = ParseOperand(ins.Ops[2], dst.Bits); }
                    NoteAddress(a);
                    NoteAddress(b);
                    long va = AsmNumber.ToSigned(Get(a), dst.Bits);
                    long vb = b.Kind == OpKind.Immediate ? AsmNumber.ToSigned(b.Imm, Math.Min(32, dst.Bits)) : AsmNumber.ToSigned(Get(b), dst.Bits);
                    BigInteger exact = new BigInteger(va) * new BigInteger(vb);
                    ulong truncated = (ulong)(exact & new BigInteger(AsmNumber.Mask(dst.Bits)));
                    bool overflow = new BigInteger(AsmNumber.ToSigned(truncated, dst.Bits)) != exact;
                    Set(dst, truncated);
                    _cf = _of = overflow;
                    _derivation.Add(Show(dst) + " ← " + va + " × " + vb + " = " + exact.ToString(CultureInfo.InvariantCulture) + " → 取低 " + dst.Bits + " 位元 = " + AsmNumber.Describe(truncated, dst.Bits));
                    _derivation.Add("　CF=OF=" + B(overflow) + (overflow ? "（乘積放不進 " + dst.Bits + " 位元，被截斷）" : "（乘積完整放進目的暫存器）"));
                    return;
                }

                var src = ParseOne(ins);
                RequireSize(src);
                NoteAddress(src);
                int bits = src.Bits;
                ulong m = Get(src);
                RegRef lowReg = Reg(0, bits);
                ulong a0 = ReadReg(lowReg);
                BigInteger product = ins.Mn == "mul"
                    ? new BigInteger(a0) * new BigInteger(m)
                    : new BigInteger(AsmNumber.ToSigned(a0, bits)) * new BigInteger(AsmNumber.ToSigned(m, bits));
                BigInteger modulus = BigInteger.One << (bits * 2);
                BigInteger wrapped = ((product % modulus) + modulus) % modulus;
                ulong low = (ulong)(wrapped & new BigInteger(AsmNumber.Mask(bits)));
                ulong high = (ulong)((wrapped >> bits) & new BigInteger(AsmNumber.Mask(bits)));

                string where;
                if (bits == 8)
                {
                    WriteReg(Registers["ax"], (high << 8) | low);
                    where = "AX = " + AsmNumber.Hex((high << 8) | low, 16) + "（AH = " + AsmNumber.Hex(high, 8) + "、AL = " + AsmNumber.Hex(low, 8) + "）";
                }
                else
                {
                    RegRef hiReg = Reg(2, bits);
                    WriteReg(lowReg, low);
                    WriteReg(hiReg, high);
                    where = hiReg.Name.ToUpperInvariant() + ":" + lowReg.Name.ToUpperInvariant() + "，" + hiReg.Name.ToUpperInvariant() + " = " + AsmNumber.Hex(high, bits) +
                            "、" + lowReg.Name.ToUpperInvariant() + " = " + AsmNumber.Hex(low, bits);
                }
                bool upperUsed = ins.Mn == "mul" ? high != 0 : new BigInteger(AsmNumber.ToSigned(low, bits)) != product;
                _cf = _of = upperUsed;
                string lowName = bits == 8 ? "AL" : lowReg.Name.ToUpperInvariant();
                string av = ins.Mn == "mul" ? a0.ToString(CultureInfo.InvariantCulture) : AsmNumber.ToSigned(a0, bits).ToString(CultureInfo.InvariantCulture);
                string mv = ins.Mn == "mul" ? m.ToString(CultureInfo.InvariantCulture) : AsmNumber.ToSigned(m, bits).ToString(CultureInfo.InvariantCulture);
                _derivation.Add((ins.Mn == "mul" ? "無號" : "有號") + "乘法：" + lowName + "(" + av + ") × " + Show(src) + "(" + mv + ") = " + product.ToString(CultureInfo.InvariantCulture));
                _derivation.Add("　結果是 " + (bits * 2) + " 位元，放在 " + where);
                _derivation.Add("　CF=OF=" + B(upperUsed) + (upperUsed ? "（高半部有用到）" : "（高半部只是延伸，乘積放得進 " + bits + " 位元）"));
            }

            private void Divide(Instr ins)
            {
                var src = ParseOne(ins);
                RequireSize(src);
                NoteAddress(src);
                int bits = src.Bits;
                ulong divisor = Get(src);
                bool signed = ins.Mn == "idiv";
                if (divisor == 0) throw new SimulationException("除以 0！CPU 會產生除法錯誤（#DE），程式當掉");

                BigInteger dividend;
                string dividendText;
                if (bits == 8)
                {
                    ulong ax = ReadReg(Registers["ax"]);
                    dividend = signed ? new BigInteger(AsmNumber.ToSigned(ax, 16)) : new BigInteger(ax);
                    dividendText = "AX = " + AsmNumber.Hex(ax, 16);
                }
                else
                {
                    ulong lo = ReadReg(Reg(0, bits));
                    ulong hi = ReadReg(Reg(2, bits));
                    BigInteger u = (new BigInteger(hi) << bits) | new BigInteger(lo);
                    if (signed && FlagMath.Msb(hi, bits)) u -= BigInteger.One << (bits * 2);
                    dividend = u;
                    dividendText = Reg(2, bits).Name.ToUpperInvariant() + ":" + Reg(0, bits).Name.ToUpperInvariant() + " = " + AsmNumber.Hex(hi, bits) + ":" + AsmNumber.Hex(lo, bits);
                }

                BigInteger d = signed ? new BigInteger(AsmNumber.ToSigned(divisor, bits)) : new BigInteger(divisor);
                BigInteger remainder;
                BigInteger quotient = BigInteger.DivRem(dividend, d, out remainder);

                BigInteger maxQ = signed ? (BigInteger.One << (bits - 1)) - 1 : (BigInteger.One << bits) - 1;
                BigInteger minQ = signed ? -(BigInteger.One << (bits - 1)) : BigInteger.Zero;
                _derivation.Add("被除數 " + dividendText + "（十進位 " + dividend.ToString(CultureInfo.InvariantCulture) + "），除數 " + Show(src) + " = " + d.ToString(CultureInfo.InvariantCulture));
                if (quotient > maxQ || quotient < minQ)
                    throw new SimulationException("除法溢位！商 " + quotient.ToString(CultureInfo.InvariantCulture) + " 放不進 " + bits + " 位元（常見原因：除法前沒有把 " +
                                                  (bits == 8 ? "AH" : Reg(2, bits).Name.ToUpperInvariant()) + " 清 0 或用 CDQ 做符號延伸）");

                ulong q = (ulong)(quotient & new BigInteger(AsmNumber.Mask(bits)));
                ulong r = (ulong)(remainder & new BigInteger(AsmNumber.Mask(bits)));
                if (bits == 8)
                {
                    WriteReg(Registers["al"], q);
                    WriteReg(Registers["ah"], r);
                    _derivation.Add("商 " + quotient.ToString(CultureInfo.InvariantCulture) + " → AL，餘數 " + remainder.ToString(CultureInfo.InvariantCulture) + " → AH");
                }
                else
                {
                    WriteReg(Reg(0, bits), q);
                    WriteReg(Reg(2, bits), r);
                    _derivation.Add("商 " + quotient.ToString(CultureInfo.InvariantCulture) + " → " + Reg(0, bits).Name.ToUpperInvariant() +
                                    "，餘數 " + remainder.ToString(CultureInfo.InvariantCulture) + " → " + Reg(2, bits).Name.ToUpperInvariant());
                }
                _derivation.Add("　驗算：" + quotient.ToString(CultureInfo.InvariantCulture) + " × " + d.ToString(CultureInfo.InvariantCulture) + " + " +
                                remainder.ToString(CultureInfo.InvariantCulture) + " = " + dividend.ToString(CultureInfo.InvariantCulture) + "（DIV 後旗標未定義）");
            }

            // ---------------- 流程控制 ----------------

            private int ResolveTarget(Instr ins)
            {
                if (ins.Ops.Count < 1) throw new SimulationException(ins.Mn.ToUpperInvariant() + " 需要跳躍目標");
                string target = ins.Ops[0].Trim();
                target = Regex.Replace(target, @"^(short|near|far)\s+(ptr\s+)?", string.Empty, RegexOptions.IgnoreCase);
                int idx;
                if (_codeLabels.TryGetValue(target, out idx)) return idx;
                if (target == "$") return _ip;
                if (Regex.IsMatch(target, @"^[A-Za-z_.@?][\w.@?]*$") && !Registers.ContainsKey(target) && !_data.ContainsKey(target))
                    throw new SimulationException("找不到標籤或副程式「" + target + "」");
                // 間接跳躍：JMP EAX、JMP [table+...]
                var op = ParseOperand(target, _bits);
                ulong address = op.Kind == OpKind.Immediate ? op.Imm : Get(op);
                int index = AddressToIndex(address);
                if (index < 0) throw new SimulationException("跳躍目標「" + target + "」不是程式中的標籤");
                return index;
            }

            private int AddressToIndex(ulong address)
            {
                if (address < _codeBase) return -1;
                ulong diff = address - _codeBase;
                if (diff % 4 != 0) return -1;
                ulong idx = diff / 4;
                if (idx > (ulong)_instrs.Count) return -1;
                return (int)idx;
            }

            private string LineOf(int index)
            {
                if (index >= 0 && index < _instrs.Count) return "第 " + _instrs[index].Line.LineNumber + " 行";
                return "程式結尾";
            }

            private void Jump(Instr ins, ref int next, bool taken, string reason)
            {
                int target = ResolveTarget(ins);
                if (taken)
                {
                    next = target;
                    _derivation.Add(reason + " → 跳到「" + ins.Ops[0].Trim() + "」（" + LineOf(target) + "）");
                }
                else
                {
                    _derivation.Add(reason + " → 不跳，繼續執行下一行");
                }
            }

            private void JumpIfZero(Instr ins, ref int next, string reg)
            {
                ulong v = ReadReg(Registers[reg]);
                Jump(ins, ref next, v == 0, reg.ToUpperInvariant() + " = " + v + (v == 0 ? "，等於 0" : "，不等於 0"));
            }

            private void Loop(Instr ins, ref int next)
            {
                var counter = Registers[_bits == 64 ? "rcx" : _bits == 16 ? "cx" : "ecx"];
                ulong old = ReadReg(counter);
                ulong v = (old - 1) & AsmNumber.Mask(counter.Bits);
                WriteReg(counter, v);
                string name = counter.Name.ToUpperInvariant();
                string calc = name + " = " + old + " - 1 = " + v;
                if (old == 0) calc += "（從 0 減 1 變成最大值，迴圈會跑非常多次！）";
                bool taken = v != 0;
                string cond = "；" + name + (taken ? " ≠ 0" : " = 0");
                if (ins.Mn == "loope" || ins.Mn == "loopz") { taken = taken && _zf; cond += "，ZF=" + B(_zf); }
                if (ins.Mn == "loopne" || ins.Mn == "loopnz") { taken = taken && !_zf; cond += "，ZF=" + B(_zf); }
                Jump(ins, ref next, taken, calc + cond);
            }

            private void Call(Instr ins, ref int next)
            {
                if (ins.Ops.Count < 1) throw new SimulationException("CALL 需要目標");
                string target = ins.Ops[0].Trim();
                if (CallLibrary(target)) return;
                int idx = ResolveTarget(ins);
                PushValue(CodeAddress(_ip + 1), _bits, "返回位址 " + AsmNumber.Hex(CodeAddress(_ip + 1), _bits) + "（" + LineOf(_ip + 1) + "）");
                next = idx;
                _derivation.Add("跳到副程式「" + target + "」（" + LineOf(idx) + "）");
            }

            private void Invoke(Instr ins, ref int next)
            {
                if (ins.Ops.Count < 1) throw new SimulationException("INVOKE 需要程序名稱");
                string target = ins.Ops[0].Trim();
                // INVOKE 參數由右到左推入堆疊（stdcall），這裡只支援函式庫與 ExitProcess
                if (ins.Ops.Count > 1 && !LibraryName(target).Equals("ExitProcess", StringComparison.OrdinalIgnoreCase))
                    _result.Messages.Add("第 " + ins.Line.LineNumber + " 行：INVOKE 的參數沒有推入堆疊（模擬器簡化處理）。");
                if (CallLibrary(target)) return;
                var fake = new Instr { Line = ins.Line, Mn = "call", Ops = new List<string> { target } };
                Call(fake, ref next);
            }

            private void Ret(Instr ins, ref int next)
            {
                var sp = StackReg;
                if (ReadReg(sp) >= _initialSp)
                {
                    Halt("主程式執行 RET 返回作業系統，程式結束");
                    return;
                }
                ulong address = PopValue(_bits);
                if (ins.Ops.Count > 0)
                {
                    long n = 0;
                    var op = ParseOperand(ins.Ops[0], 16);
                    n = (long)op.Imm;
                    ulong old = ReadReg(sp);
                    WriteReg(sp, old + (ulong)n);
                    _derivation.Add("RET " + n + "：再把 " + SpName + " 加 " + n + "（清除呼叫時推入的參數）");
                }
                int idx = AddressToIndex(address);
                if (idx < 0) throw new SimulationException("RET 取到的返回位址 " + AsmNumber.Hex(address, _bits) + " 不正確（堆疊不平衡：PUSH 和 POP 次數不一致？）");
                next = idx;
                _derivation.Add("返回到 " + LineOf(idx));
            }

            private void Leave()
            {
                var sp = StackReg;
                var bp = Registers[_bits == 64 ? "rbp" : _bits == 16 ? "bp" : "ebp"];
                ulong v = ReadReg(bp);
                WriteReg(sp, v);
                _derivation.Add(SpName + " ← " + bp.Name.ToUpperInvariant() + " = " + AsmNumber.Hex(v, sp.Bits));
                WriteReg(bp, PopValue(_bits));
                _derivation.Add(bp.Name.ToUpperInvariant() + " ← POP（還原呼叫者的堆疊框架）");
            }

            private void Enter(Instr ins)
            {
                if (ins.Ops.Count < 1) throw new SimulationException("ENTER 需要運算元");
                var size = ParseOperand(ins.Ops[0], 16);
                var bp = Registers[_bits == 64 ? "rbp" : _bits == 16 ? "bp" : "ebp"];
                PushValue(ReadReg(bp), _bits, bp.Name.ToUpperInvariant());
                ulong sp = ReadReg(StackReg);
                WriteReg(bp, sp);
                WriteReg(StackReg, sp - size.Imm);
                _derivation.Add(bp.Name.ToUpperInvariant() + " ← " + SpName + "；" + SpName + " ← " + SpName + " - " + size.Imm + "（保留區域變數空間）");
            }

            // ---------------- 字串指令 ----------------

            private static bool IsStringInstruction(string mn)
            {
                if (mn.Length != 5) return false;
                string head = mn.Substring(0, 4);
                char tail = mn[4];
                return (head == "movs" || head == "stos" || head == "lods" || head == "scas" || head == "cmps") && "bwdq".IndexOf(tail) >= 0;
            }

            private int StringBits(string mn)
            {
                switch (mn[4])
                {
                    case 'b': return 8;
                    case 'w': return 16;
                    case 'd': return 32;
                    default: return 64;
                }
            }

            private void StringStep(string mn, bool explain)
            {
                int bits = StringBits(mn);
                ulong step = (ulong)(bits / 8);
                var si = Registers[_bits == 64 ? "rsi" : _bits == 16 ? "si" : "esi"];
                var di = Registers[_bits == 64 ? "rdi" : _bits == 16 ? "di" : "edi"];
                var acc = Reg(0, bits);
                ulong s = ReadReg(si), d = ReadReg(di);
                string head = mn.Substring(0, 4);
                switch (head)
                {
                    case "movs":
                        {
                            ulong v = ReadMem(s, bits);
                            WriteMem(d, bits, v);
                            if (explain) _derivation.Add("記憶體[" + AsmNumber.Hex(d, _bits) + "] ← 記憶體[" + AsmNumber.Hex(s, _bits) + "] = " + AsmNumber.Hex(v, bits));
                            Advance(si, step);
                            Advance(di, step);
                            break;
                        }
                    case "stos":
                        {
                            ulong v = ReadReg(acc);
                            WriteMem(d, bits, v);
                            if (explain) _derivation.Add("記憶體[" + AsmNumber.Hex(d, _bits) + "] ← " + acc.Name.ToUpperInvariant() + " = " + AsmNumber.Hex(v, bits));
                            Advance(di, step);
                            break;
                        }
                    case "lods":
                        {
                            ulong v = ReadMem(s, bits);
                            WriteReg(acc, v);
                            if (explain) _derivation.Add(acc.Name.ToUpperInvariant() + " ← 記憶體[" + AsmNumber.Hex(s, _bits) + "] = " + AsmNumber.Hex(v, bits));
                            Advance(si, step);
                            break;
                        }
                    case "scas":
                        {
                            ulong a = ReadReg(acc), m = ReadMem(d, bits);
                            var f = FlagMath.Sub(a, m, bits, false);
                            ApplyFlags(f, true, false);
                            if (explain) _derivation.Add("比較 " + acc.Name.ToUpperInvariant() + "(" + AsmNumber.Hex(a, bits) + ") 與 記憶體[" + AsmNumber.Hex(d, _bits) + "](" + AsmNumber.Hex(m, bits) + ")，ZF=" + B(_zf));
                            Advance(di, step);
                            break;
                        }
                    default:
                        {
                            ulong a = ReadMem(s, bits), b = ReadMem(d, bits);
                            var f = FlagMath.Sub(a, b, bits, false);
                            ApplyFlags(f, true, false);
                            if (explain) _derivation.Add("比較 記憶體[" + AsmNumber.Hex(s, _bits) + "](" + AsmNumber.Hex(a, bits) + ") 與 記憶體[" + AsmNumber.Hex(d, _bits) + "](" + AsmNumber.Hex(b, bits) + ")，ZF=" + B(_zf));
                            Advance(si, step);
                            Advance(di, step);
                            break;
                        }
                }
            }

            private void Advance(RegRef reg, ulong step)
            {
                ulong v = ReadReg(reg);
                WriteReg(reg, _df ? v - step : v + step);
            }

            private void ExecuteRep(Instr ins)
            {
                var counter = Registers[_bits == 64 ? "rcx" : _bits == 16 ? "cx" : "ecx"];
                string mn = ins.Mn;
                bool compare = mn.StartsWith("scas", StringComparison.Ordinal) || mn.StartsWith("cmps", StringComparison.Ordinal);
                int iterations = 0;
                ulong start = ReadReg(counter);
                while (ReadReg(counter) != 0)
                {
                    if (iterations >= MaxRepeat) throw new SimulationException("REP 重複次數過多（超過 " + MaxRepeat + " 次）");
                    StringStep(mn, iterations < 3);
                    WriteReg(counter, ReadReg(counter) - 1);
                    iterations++;
                    if (compare)
                    {
                        if ((ins.Prefix == "repe" || ins.Prefix == "repz" || ins.Prefix == "rep") && !_zf) break;
                        if ((ins.Prefix == "repne" || ins.Prefix == "repnz") && _zf) break;
                    }
                }
                if (iterations > 3) _derivation.Add("……（共重複 " + iterations + " 次）");
                _derivation.Add(ins.Prefix.ToUpperInvariant() + " " + mn.ToUpperInvariant() + "：" + counter.Name.ToUpperInvariant() + " 從 " + start + " 變成 " + ReadReg(counter) +
                                "，共執行 " + iterations + " 次" + (compare ? "（ZF=" + B(_zf) + "）" : string.Empty));
            }

            // ---------------- 中斷、系統呼叫、函式庫 ----------------

            private void Interrupt(Instr ins)
            {
                var op = ParseOne(ins, 8);
                ulong n = op.Imm;
                if (op.Kind != OpKind.Immediate) throw new SimulationException("INT 需要中斷號碼");
                ulong ah = ReadReg(Registers["ah"]);
                switch (n)
                {
                    case 0x21:
                        switch (ah)
                        {
                            case 0x02:
                                {
                                    char c = (char)ReadReg(Registers["dl"]);
                                    Output(c.ToString());
                                    _derivation.Add("INT 21h／AH=02h：輸出 DL 的字元 '" + Printable(c) + "'");
                                    return;
                                }
                            case 0x09:
                                {
                                    ulong address = ReadReg(Registers[_bits == 16 ? "dx" : "edx"]);
                                    string s = ReadCString(address, '$');
                                    Output(s);
                                    _derivation.Add("INT 21h／AH=09h：從 DS:DX = " + AsmNumber.Hex(address, 16) + " 輸出字串直到 '$'：" + Escape(s));
                                    return;
                                }
                            case 0x01:
                            case 0x07:
                            case 0x08:
                                {
                                    char c = NextInputChar();
                                    WriteReg(Registers["al"], c);
                                    if (ah == 0x01) Output(c.ToString());
                                    _derivation.Add("INT 21h／AH=" + ah.ToString("X2", CultureInfo.InvariantCulture) + "h：讀取鍵盤字元 → AL = '" + Printable(c) + "'(" + (int)c + ")");
                                    return;
                                }
                            case 0x4C:
                                Halt("INT 21h／AH=4Ch：結束程式，結束碼 AL = " + ReadReg(Registers["al"]));
                                return;
                            default:
                                _derivation.Add("INT 21h／AH=" + ah.ToString("X2", CultureInfo.InvariantCulture) + "h：" + ServiceNames.Dos((long)ah) + "（模擬器未實作，已略過）");
                                _result.Messages.Add("第 " + ins.Line.LineNumber + " 行：DOS 功能 " + ah.ToString("X2", CultureInfo.InvariantCulture) + "h 未模擬。");
                                return;
                        }
                    case 0x10:
                        if (ah == 0x0E)
                        {
                            char c = (char)ReadReg(Registers["al"]);
                            Output(c.ToString());
                            _derivation.Add("INT 10h／AH=0Eh：輸出 AL 的字元 '" + Printable(c) + "'");
                        }
                        else _derivation.Add("INT 10h／AH=" + ah.ToString("X2", CultureInfo.InvariantCulture) + "h：" + ServiceNames.Bios10((long)ah) + "（模擬器只記錄，不改變畫面）");
                        return;
                    case 0x16:
                        {
                            char c = NextInputChar();
                            WriteReg(Registers["al"], c);
                            _derivation.Add("INT 16h：讀取按鍵 → AL = '" + Printable(c) + "'");
                            return;
                        }
                    case 0x80:
                        {
                            ulong eax = ReadReg(Registers["eax"]);
                            if (eax == 1) { Halt("Linux exit 系統呼叫，結束碼 EBX = " + ReadReg(Registers["ebx"])); return; }
                            if (eax == 4)
                            {
                                ulong buf = ReadReg(Registers["ecx"]);
                                ulong len = ReadReg(Registers["edx"]);
                                string s = ReadBytes(buf, len);
                                Output(s);
                                WriteReg(Registers["eax"], len);
                                _derivation.Add("Linux write：從 ECX = " + AsmNumber.Hex(buf, 32) + " 輸出 EDX = " + len + " 個位元組：" + Escape(s));
                                return;
                            }
                            _derivation.Add("Linux 系統呼叫 " + eax + "：" + ServiceNames.Linux32((long)eax) + "（模擬器未實作）");
                            return;
                        }
                    case 3:
                        _derivation.Add("INT 3：除錯中斷點（模擬器繼續執行）");
                        return;
                    default:
                        _derivation.Add("INT " + n.ToString("X", CultureInfo.InvariantCulture) + "h：模擬器未實作，已略過");
                        _result.Messages.Add("第 " + ins.Line.LineNumber + " 行：INT " + n.ToString("X", CultureInfo.InvariantCulture) + "h 未模擬。");
                        return;
                }
            }

            private void Syscall()
            {
                ulong rax = ReadReg(Registers["rax"]);
                switch (rax)
                {
                    case 1:
                        {
                            ulong buf = ReadReg(Registers["rsi"]);
                            ulong len = ReadReg(Registers["rdx"]);
                            string s = ReadBytes(buf, len);
                            Output(s);
                            WriteReg(Registers["rax"], len);
                            _derivation.Add("Linux write：從 RSI = " + AsmNumber.Hex(buf, 64) + " 輸出 RDX = " + len + " 個位元組：" + Escape(s));
                            return;
                        }
                    case 60:
                    case 231:
                        Halt("Linux exit 系統呼叫，結束碼 RDI = " + ReadReg(Registers["rdi"]));
                        return;
                    default:
                        _derivation.Add("Linux 系統呼叫 " + rax + "：" + ServiceNames.Linux64((long)rax) + "（模擬器未實作）");
                        return;
                }
            }

            private static string LibraryName(string target)
            {
                string n = target.Trim().TrimStart('_');
                int at = n.IndexOf('@');
                if (at > 0) n = n.Substring(0, at);
                return n;
            }

            private bool CallLibrary(string target)
            {
                string name = LibraryName(target);
                if (_codeLabels.ContainsKey(target)) return false; // 使用者自己定義的同名程序優先
                ulong eax = ReadReg(Registers["eax"]);
                switch (name.ToLowerInvariant())
                {
                    case "writestring":
                        {
                            ulong edx = ReadReg(Registers[_bits == 64 ? "rdx" : "edx"]);
                            string s = ReadCString(edx, '\0');
                            Output(s);
                            _derivation.Add("WriteString：輸出 EDX = " + AsmNumber.Hex(edx, _bits) + " 指向的字串：" + Escape(s));
                            return true;
                        }
                    case "writedec":
                        Output(((uint)eax).ToString(CultureInfo.InvariantCulture));
                        _derivation.Add("WriteDec：以無號十進位輸出 EAX = " + (uint)eax);
                        return true;
                    case "writeint":
                        {
                            int v = unchecked((int)(uint)eax);
                            string s = (v >= 0 ? "+" : string.Empty) + v.ToString(CultureInfo.InvariantCulture);
                            Output(s);
                            _derivation.Add("WriteInt：以有號十進位輸出 EAX = " + s);
                            return true;
                        }
                    case "writehex":
                        {
                            string s = ((uint)eax).ToString("X8", CultureInfo.InvariantCulture);
                            Output(s);
                            _derivation.Add("WriteHex：以十六進位輸出 EAX = " + s);
                            return true;
                        }
                    case "writebin":
                        {
                            string s = AsmNumber.Binary((uint)eax, 32);
                            Output(s);
                            _derivation.Add("WriteBin：以二進位輸出 EAX = " + s);
                            return true;
                        }
                    case "writechar":
                        {
                            char c = (char)(eax & 0xFF);
                            Output(c.ToString());
                            _derivation.Add("WriteChar：輸出 AL 的字元 '" + Printable(c) + "'");
                            return true;
                        }
                    case "crlf":
                        Output("\r\n");
                        _derivation.Add("Crlf：換行");
                        return true;
                    case "dumpregs":
                        {
                            string dump = "\r\n  EAX=" + Hex8(0) + "  EBX=" + Hex8(3) + "  ECX=" + Hex8(1) + "  EDX=" + Hex8(2) +
                                          "\r\n  ESI=" + Hex8(6) + "  EDI=" + Hex8(7) + "  EBP=" + Hex8(5) + "  ESP=" + Hex8(4) +
                                          "\r\n  CF=" + B(_cf) + "  SF=" + B(_sf) + "  ZF=" + B(_zf) + "  OF=" + B(_of) + "  AF=" + B(_af) + "  PF=" + B(_pf) + "\r\n";
                            Output(dump);
                            _derivation.Add("DumpRegs：輸出所有暫存器與旗標");
                            return true;
                        }
                    case "readint":
                    case "readdec":
                        {
                            long v = NextInputNumber();
                            WriteReg(Registers["eax"], unchecked((ulong)v) & 0xFFFFFFFFUL);
                            _derivation.Add(name + "：從輸入讀到 " + v + " → EAX");
                            return true;
                        }
                    case "readchar":
                        {
                            char c = NextInputChar();
                            WriteReg(Registers["al"], c);
                            _derivation.Add("ReadChar：讀取字元 '" + Printable(c) + "' → AL");
                            return true;
                        }
                    case "readstring":
                        {
                            ulong edx = ReadReg(Registers["edx"]);
                            ulong max = ReadReg(Registers["ecx"]);
                            string s = NextInputLine();
                            if ((ulong)s.Length >= max && max > 0) s = s.Substring(0, (int)max - 1);
                            for (int i = 0; i < s.Length; i++) WriteMem(edx + (ulong)i, 8, s[i] < 256 ? s[i] : '?');
                            WriteMem(edx + (ulong)s.Length, 8, 0);
                            WriteReg(Registers["eax"], (ulong)s.Length);
                            _derivation.Add("ReadString：讀入「" + s + "」存到 EDX 指向的緩衝區，EAX = 長度 " + s.Length);
                            return true;
                        }
                    case "strlength":
                        {
                            ulong edx = ReadReg(Registers["edx"]);
                            string s = ReadCString(edx, '\0');
                            WriteReg(Registers["eax"], (ulong)s.Length);
                            _derivation.Add("StrLength：字串長度 " + s.Length + " → EAX");
                            return true;
                        }
                    case "randomrange":
                        {
                            _random = _random * 1103515245u + 12345u;
                            ulong range = eax == 0 ? 1 : eax & 0xFFFFFFFFUL;
                            ulong v = (_random >> 8) % range;
                            WriteReg(Registers["eax"], v);
                            _derivation.Add("RandomRange：產生 0～" + (range - 1) + " 的亂數 → EAX = " + v + "（模擬器使用固定種子，每次結果相同）");
                            return true;
                        }
                    case "random32":
                        _random = _random * 1103515245u + 12345u;
                        WriteReg(Registers["eax"], _random);
                        _derivation.Add("Random32：EAX = " + _random);
                        return true;
                    case "clrscr":
                    case "randomize":
                    case "settextcolor":
                    case "delay":
                    case "waitmsg":
                    case "gotoxy":
                    case "getmseconds":
                        _derivation.Add(name + "：模擬器不處理畫面／時間，已略過");
                        return true;
                    case "exitprocess":
                        Halt("ExitProcess：程式結束");
                        return true;
                    case "printf":
                    case "scanf":
                    case "puts":
                    case "putchar":
                    case "getchar":
                        _derivation.Add("C 函式 " + name + "：模擬器不支援，已略過");
                        _result.Messages.Add("C 函式庫 " + name + " 沒有模擬，輸出可能不完整。");
                        return true;
                }
                return false;
            }

            private string Hex8(int index)
            {
                return ((uint)_r[index]).ToString("X8", CultureInfo.InvariantCulture);
            }

            private string ReadBytes(ulong address, ulong length)
            {
                var sb = new StringBuilder();
                ulong n = Math.Min(length, 65536UL);
                for (ulong i = 0; i < n; i++)
                {
                    byte b;
                    _mem.TryGetValue(address + i, out b);
                    sb.Append((char)b);
                }
                return sb.ToString();
            }

            private char NextInputChar()
            {
                string input = _options.Input ?? string.Empty;
                if (_inputPos < input.Length) return input[_inputPos++];
                _result.Messages.Add("程式要求鍵盤輸入，但「模擬輸入」已用完，以 0 代替。");
                return '\0';
            }

            private string NextInputLine()
            {
                string input = _options.Input ?? string.Empty;
                if (_inputPos >= input.Length)
                {
                    _result.Messages.Add("程式要求輸入字串，但「模擬輸入」已用完。");
                    return string.Empty;
                }
                int end = input.IndexOf('\n', _inputPos);
                string line = end < 0 ? input.Substring(_inputPos) : input.Substring(_inputPos, end - _inputPos);
                _inputPos = end < 0 ? input.Length : end + 1;
                return line.TrimEnd('\r');
            }

            private long NextInputNumber()
            {
                string input = _options.Input ?? string.Empty;
                while (_inputPos < input.Length && (char.IsWhiteSpace(input[_inputPos]) || input[_inputPos] == ',')) _inputPos++;
                int start = _inputPos;
                while (_inputPos < input.Length && !char.IsWhiteSpace(input[_inputPos]) && input[_inputPos] != ',') _inputPos++;
                string token = input.Substring(start, _inputPos - start);
                long v;
                if (token.Length > 0 && AsmNumber.TryParse(token, out v)) return v;
                _result.Messages.Add("程式要求輸入數字，但「模擬輸入」沒有可用的數字，以 0 代替。");
                return 0;
            }

            private static string Printable(char c)
            {
                if (c == '\r') return "\\r";
                if (c == '\n') return "\\n";
                if (c == '\t') return "\\t";
                if (c < 32) return "\\x" + ((int)c).ToString("X2", CultureInfo.InvariantCulture);
                return c.ToString();
            }

            private static string Escape(string s)
            {
                var sb = new StringBuilder("\"");
                foreach (char c in s.Length > 200 ? s.Substring(0, 200) + "…" : s) sb.Append(Printable(c));
                return sb.Append('"').ToString();
            }

            // -----------------------------------------------------------------
            // 顯示
            // -----------------------------------------------------------------

            private string[] DisplayNames()
            {
                if (_bits == 64) return new[] { "rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15" };
                if (_bits == 16) return new[] { "ax", "bx", "cx", "dx", "si", "di", "bp", "sp", "ds", "es" };
                return new[] { "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp" };
            }

            private Dictionary<string, ulong> SnapshotRegisters()
            {
                var map = new Dictionary<string, ulong>(StringComparer.Ordinal);
                foreach (var n in DisplayNames()) map[n] = ReadReg(Registers[n]);
                return map;
            }

            private IEnumerable<KeyValuePair<string, string>> DisplayRegisters()
            {
                foreach (var n in DisplayNames())
                {
                    var r = Registers[n];
                    yield return new KeyValuePair<string, string>(n.ToUpperInvariant(), AsmNumber.Hex(ReadReg(r), r.Bits));
                }
            }

            private string DescribeChanges(Dictionary<string, ulong> before, string flagsBefore, ushort[] segBefore)
            {
                var parts = new List<string>();
                foreach (var kv in before)
                {
                    var r = Registers[kv.Key];
                    ulong now = ReadReg(r);
                    if (now != kv.Value)
                        parts.Add(kv.Key.ToUpperInvariant() + "：" + AsmNumber.Hex(kv.Value, r.Bits) + " → " + AsmNumber.Hex(now, r.Bits));
                }
                for (int i = 0; i < _seg.Length; i++)
                {
                    string n = SegmentNames[i];
                    if (_seg[i] != segBefore[i] && !before.ContainsKey(n))
                        parts.Add(n.ToUpperInvariant() + "：" + AsmNumber.Hex(segBefore[i], 16) + " → " + AsmNumber.Hex(_seg[i], 16));
                }
                string flagsNow = FlagsText();
                if (flagsNow != flagsBefore)
                {
                    var b = flagsBefore.Split(' ');
                    var a = flagsNow.Split(' ');
                    for (int i = 0; i < a.Length && i < b.Length; i++)
                        if (a[i] != b[i]) parts.Add(b[i] + "→" + a[i].Substring(a[i].IndexOf('=') + 1));
                }
                if (_memChanges.Count > 8)
                {
                    parts.AddRange(_memChanges.Take(6));
                    parts.Add("……記憶體共改變 " + _memChanges.Count + " 處");
                }
                else parts.AddRange(_memChanges);
                return parts.Count == 0 ? "（沒有改變）" : string.Join("；", parts);
            }

            private string DumpSymbol(DataSymbol d)
            {
                int bits = Math.Min(64, d.ElementSize * 8);
                int shown = Math.Min(d.Count, 16);
                var values = new List<string>();
                for (int i = 0; i < shown; i++)
                {
                    ulong v = ReadMem(d.Address + (ulong)(i * d.ElementSize), bits);
                    values.Add(d.ElementSize == 1 ? v.ToString(CultureInfo.InvariantCulture) : AsmNumber.ToSigned(v, bits).ToString(CultureInfo.InvariantCulture));
                }
                string text = d.Name + "（" + AsmNumber.Hex(d.Address, AddrBits) + "，" + SizeWord(bits) + "×" + d.Count + "）= " + string.Join(", ", values) + (d.Count > shown ? ", …" : string.Empty);
                if (d.ElementSize == 1 && d.Count > 1)
                {
                    var chars = new StringBuilder();
                    for (int i = 0; i < Math.Min(d.Count, 80); i++)
                    {
                        byte b = (byte)ReadMem(d.Address + (ulong)i, 8);
                        chars.Append(b >= 32 && b < 127 ? (char)b : '.');
                    }
                    text += "　文字：\"" + chars + "\"";
                }
                return text;
            }
        }
    }
}
