using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Assam.Core.Binary
{
    /// <summary>
    /// 反組譯器：從進入點、TLS 回呼與匯出函式開始追蹤執行流程（遞迴下降），找出函式並產生符號化的組合語言清單。
    /// 只讀取檔案內容，不會執行任何程式碼。
    /// </summary>
    public sealed class Disassembler
    {
        /// <summary>呼叫後不會返回的函式（遇到時停止往下追蹤，避免把下一個函式誤判成同一個函式）。</summary>
        private static readonly HashSet<string> NoReturnFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ExitProcess", "ExitThread", "FreeLibraryAndExitThread", "exit", "_exit", "_Exit", "abort", "quick_exit",
            "__stack_chk_fail", "_amsg_exit", "__report_gsfailure", "_invalid_parameter_noinfo_noreturn", "FatalExit",
            "FatalAppExitA", "FatalAppExitW", "RaiseFailFastException", "longjmp", "_longjmp", "_CxxThrowException",
            "__cxa_throw", "__cxa_rethrow", "_assert", "__assert_fail", "_wassert", "__fastfail", "std::terminate", "terminate"
        };

        private const int MaxJumpTableEntries = 256;
        private const int MaxSourceFunctions = 50;

        // =====================================================================
        // 反組譯整個檔案
        // =====================================================================

        private sealed class Seed
        {
            public ulong Start;
            public string Name;
            public string Origin;
        }

        private sealed class Context
        {
            public LoadedBinary Binary;
            public X86Decoder Decoder;
            public DisassemblyOptions Options;
            public DisassemblyResult Result;
            public readonly Dictionary<ulong, DecodedInstruction> Decoded = new Dictionary<ulong, DecodedInstruction>();
            public readonly Dictionary<ulong, ulong> Owner = new Dictionary<ulong, ulong>();
            public readonly Dictionary<ulong, Seed> Seeds = new Dictionary<ulong, Seed>();
            public readonly Queue<ulong> Pending = new Queue<ulong>();
            public readonly HashSet<ulong> BranchTargets = new HashSet<ulong>();
            public readonly Dictionary<ulong, List<ulong>> CalleesOf = new Dictionary<ulong, List<ulong>>();
            public readonly Dictionary<PeSection, byte[]> SectionBytes = new Dictionary<PeSection, byte[]>();
            public readonly Dictionary<ulong, bool> NoReturnCache = new Dictionary<ulong, bool>();
        }

        /// <summary>反組譯整個檔案。binary.Bits 為 0（非 x86）時回傳空結果並在 Messages 說明。</summary>
        public DisassemblyResult Disassemble(LoadedBinary binary, DisassemblyOptions options = null)
        {
            if (binary == null) throw new ArgumentNullException("binary");
            var result = new DisassemblyResult { Binary = binary };
            if (binary.Bits == 0)
            {
                result.Messages.Add("這個檔案的機器碼不是 x86／x64（" + binary.MachineName + "），無法反組譯，只能查看檔頭、區段與匯入匯出。");
                return result;
            }

            var c = new Context
            {
                Binary = binary,
                Decoder = new X86Decoder(binary.Bits),
                Options = options ?? new DisassemblyOptions(),
                Result = result
            };

            // 起點：進入點、TLS 回呼、匯出函式
            if (binary.HasEntryPoint && IsCode(c, binary.EntryPoint))
                AddSeed(c, binary.EntryPoint, binary.IsPe ? "entry" : "start", binary.IsPe ? "進入點" : "進入點（CS:IP）");
            foreach (var tls in TlsCallbacksOf(binary))
                if (IsCode(c, tls)) AddSeed(c, tls, null, "TLS 回呼");
            foreach (var e in binary.Exports.Where(x => x.ForwardTo == null).OrderBy(x => x.Address))
                if (IsCode(c, e.Address))
                    AddSeed(c, e.Address, e.Name != null ? SafeName(e.Name, null) : "ordinal_" + e.Ordinal.ToString(CultureInfo.InvariantCulture), "匯出函式");

            Explore(c);

            if (c.Options.ScanUnreachedCode && !LimitReached(c)) ScanGaps(c);

            BuildFunctions(c);

            if (binary.IsDotNet)
                result.Messages.Add(".NET 組件的機器碼只有一個跳到 mscoree.dll!_CorExeMain／_CorDllMain 的啟動程式；真正的程式內容是 IL，請看「.NET 型別與方法」。");
            if (result.Truncated)
                result.Messages.Add("已達反組譯上限（" + c.Options.MaxInstructions + " 條指令／" + c.Options.MaxFunctions + " 個函式），只顯示前面的部分。");
            result.Messages.Add("找到 " + result.Functions.Count + " 個函式、" + result.TotalInstructions + " 條指令。");
            return result;
        }

        /// <summary>TLS 回呼（若載入器有提供 TlsCallbacks 屬性）。</summary>
        private static IEnumerable<ulong> TlsCallbacksOf(LoadedBinary binary)
        {
            var prop = typeof(LoadedBinary).GetProperty("TlsCallbacks");
            if (prop == null) return Enumerable.Empty<ulong>();
            var value = prop.GetValue(binary, null) as IEnumerable<ulong>;
            return value ?? Enumerable.Empty<ulong>();
        }

        private static bool LimitReached(Context c)
        {
            return c.Result.TotalInstructions >= c.Options.MaxInstructions || c.Seeds.Count >= c.Options.MaxFunctions;
        }

        private static bool IsCode(Context c, ulong address)
        {
            var s = c.Binary.SectionAt(address);
            return s != null && s.IsExecutable && address - s.VirtualAddress < s.RawSize;
        }

        private static void AddSeed(Context c, ulong start, string name, string origin)
        {
            Seed existing;
            if (c.Seeds.TryGetValue(start, out existing))
            {
                if (existing.Name == null && name != null) existing.Name = name;
                return;
            }
            if (c.Seeds.Count >= c.Options.MaxFunctions)
            {
                c.Result.Truncated = true;
                return;
            }
            c.Seeds[start] = new Seed { Start = start, Name = name, Origin = origin };
            c.Pending.Enqueue(start);
        }

        private static byte[] BytesOf(Context c, PeSection s)
        {
            byte[] bytes;
            if (c.SectionBytes.TryGetValue(s, out bytes)) return bytes;
            int start = (int)Math.Min(s.RawOffset, (uint)c.Binary.Data.Length);
            int length = (int)Math.Min((long)s.RawSize, c.Binary.Data.Length - (long)start);
            bytes = new byte[Math.Max(0, length)];
            if (length > 0) Buffer.BlockCopy(c.Binary.Data, start, bytes, 0, length);
            c.SectionBytes[s] = bytes;
            return bytes;
        }

        private static DecodedInstruction DecodeAt(Context c, ulong address)
        {
            var s = c.Binary.SectionAt(address);
            if (s == null || !s.IsExecutable) return null;
            var bytes = BytesOf(c, s);
            ulong delta = address - s.VirtualAddress;
            if (delta >= (ulong)bytes.Length) return null;
            return c.Decoder.Decode(bytes, (int)delta, address);
        }

        private static void Explore(Context c)
        {
            while (c.Pending.Count > 0)
            {
                if (c.Result.TotalInstructions >= c.Options.MaxInstructions)
                {
                    c.Result.Truncated = true;
                    break;
                }
                ExploreFunction(c, c.Pending.Dequeue());
            }
        }

        private static void ExploreFunction(Context c, ulong start)
        {
            var work = new Stack<ulong>();
            work.Push(start);
            while (work.Count > 0)
            {
                ulong address = work.Pop();
                bool first = true;
                while (true)
                {
                    if (c.Result.TotalInstructions >= c.Options.MaxInstructions)
                    {
                        c.Result.Truncated = true;
                        return;
                    }
                    if (c.Decoded.ContainsKey(address)) break;
                    if (!(first && address == start) && c.Seeds.ContainsKey(address) && address != start) break; // 落入另一個函式
                    first = false;

                    var ins = DecodeAt(c, address);
                    if (ins == null) break;
                    c.Decoded[address] = ins;
                    c.Owner[address] = start;
                    c.Result.TotalInstructions++;
                    if (!ins.IsValid) break;

                    ulong next = address + (ulong)ins.Length;
                    bool stop = false;
                    switch (ins.Flow)
                    {
                        case InstructionFlow.Sequential:
                            break;
                        case InstructionFlow.Call:
                            if (ins.BranchTarget.HasValue)
                            {
                                ulong t = ins.BranchTarget.Value;
                                if (IsCode(c, t))
                                {
                                    AddSeed(c, t, null, "被呼叫");
                                    AddCallee(c, start, t);
                                    if (IsNoReturnTarget(c, t)) stop = true;
                                }
                            }
                            else if (IsNoReturnImport(ImportOf(c, ins))) stop = true;
                            break;
                        case InstructionFlow.Jump:
                            stop = true;
                            if (ins.BranchTarget.HasValue)
                            {
                                ulong t = ins.BranchTarget.Value;
                                if (!IsCode(c, t)) break;
                                if (t != start && c.Seeds.ContainsKey(t))
                                {
                                    AddCallee(c, start, t); // 尾呼叫（jmp 到另一個函式）
                                    break;
                                }
                                c.BranchTargets.Add(t);
                                work.Push(t);
                            }
                            else
                            {
                                foreach (ulong t in JumpTableTargets(c, ins))
                                {
                                    c.BranchTargets.Add(t);
                                    work.Push(t);
                                }
                            }
                            break;
                        case InstructionFlow.ConditionalJump:
                            if (ins.BranchTarget.HasValue && IsCode(c, ins.BranchTarget.Value))
                            {
                                c.BranchTargets.Add(ins.BranchTarget.Value);
                                work.Push(ins.BranchTarget.Value);
                            }
                            break;
                        default:
                            stop = true; // Return、Stop、Invalid
                            break;
                    }
                    if (stop) break;
                    address = next;
                }
            }
        }

        private static void AddCallee(Context c, ulong function, ulong callee)
        {
            List<ulong> list;
            if (!c.CalleesOf.TryGetValue(function, out list))
            {
                list = new List<ulong>();
                c.CalleesOf[function] = list;
            }
            if (!list.Contains(callee)) list.Add(callee);
        }

        /// <summary>指令的記憶體運算元若指到匯入位址表（IAT），回傳那個匯入函式。</summary>
        private static PeImport ImportOf(Context c, DecodedInstruction ins)
        {
            foreach (var op in ins.Operands)
            {
                if (op.Kind == OperandKind.Memory && op.AbsoluteAddress.HasValue && IsPlainSegment(op))
                {
                    var imp = c.Binary.ImportAt(op.AbsoluteAddress.Value);
                    if (imp != null) return imp;
                }
            }
            return null;
        }

        private static bool IsPlainSegment(DecodedOperand op)
        {
            return string.IsNullOrEmpty(op.Segment) || op.Segment == "ds";
        }

        private static bool IsNoReturnImport(PeImport import)
        {
            return import != null && import.Name != null && NoReturnFunctions.Contains(StripDecoration(import.Name));
        }

        /// <summary>被呼叫的位址如果是「jmp [IAT]」跳板且目標是不會返回的函式（例如 exit），呼叫後就不會回來。</summary>
        private static bool IsNoReturnTarget(Context c, ulong target)
        {
            bool cached;
            if (c.NoReturnCache.TryGetValue(target, out cached)) return cached;
            bool result = false;
            var ins = DecodeAt(c, target);
            if (ins != null && ins.IsValid && ins.Flow == InstructionFlow.Jump && !ins.BranchTarget.HasValue)
                result = IsNoReturnImport(ImportOf(c, ins));
            c.NoReturnCache[target] = result;
            return result;
        }

        private static string StripDecoration(string name)
        {
            string n = name.TrimStart('_');
            int at = n.IndexOf('@');
            if (at > 0) n = n.Substring(0, at);
            return n.Length == 0 ? name : (name.StartsWith("_", StringComparison.Ordinal) && NoReturnFunctions.Contains(name) ? name : n);
        }

        /// <summary>switch 跳躍表：jmp dword ptr [reg*4+table]（32 位元）或 jmp qword ptr [reg*8+table]。</summary>
        private static IEnumerable<ulong> JumpTableTargets(Context c, DecodedInstruction ins)
        {
            var result = new List<ulong>();
            if (ins.Operands.Count != 1) return result;
            var op = ins.Operands[0];
            int ptr = c.Binary.Bits == 64 ? 8 : 4;
            if (op.Kind != OperandKind.Memory || op.BaseRegister != null || op.IndexRegister == null || op.Scale != ptr || !op.HasDisplacement)
                return result;
            ulong table = c.Binary.Bits == 64 ? (ulong)op.Displacement : (ulong)(uint)op.Displacement;
            var section = c.Binary.SectionAt(ins.Address);
            for (int i = 0; i < MaxJumpTableEntries; i++)
            {
                var bytes = c.Binary.ReadVa(table + (ulong)(i * ptr), ptr);
                if (bytes.Length < ptr) break;
                ulong target = ptr == 8 ? BitConverter.ToUInt64(bytes, 0) : BitConverter.ToUInt32(bytes, 0);
                if (section == null || !section.Contains(target) || !IsCode(c, target)) break;
                result.Add(target);
            }
            return result;
        }

        /// <summary>線性掃描程式區段中還沒被追蹤到的部分（跳過 int3/nop/0 填充），每段當成一個新函式。</summary>
        private static void ScanGaps(Context c)
        {
            foreach (var s in c.Binary.CodeSections.ToList())
            {
                var bytes = BytesOf(c, s);
                long i = 0;
                while (i < bytes.Length && !LimitReached(c))
                {
                    ulong va = s.VirtualAddress + (ulong)i;
                    DecodedInstruction known;
                    if (c.Decoded.TryGetValue(va, out known))
                    {
                        i += Math.Max(1, known.Length);
                        continue;
                    }
                    byte b = bytes[i];
                    if (b == 0xCC || b == 0x90 || b == 0x00)
                    {
                        i++;
                        continue;
                    }
                    AddSeed(c, va, null, "線性掃描");
                    Explore(c);
                    if (!c.Decoded.ContainsKey(va)) i++;
                }
            }
        }

        // =====================================================================
        // 建立函式與符號
        // =====================================================================

        private static void BuildFunctions(Context c)
        {
            var r = c.Result;
            var b = c.Binary;
            SplitFallThroughFunctions(c);
            var byOwner = new Dictionary<ulong, List<DecodedInstruction>>();
            foreach (var kv in c.Decoded)
            {
                ulong owner;
                if (!c.Owner.TryGetValue(kv.Key, out owner)) continue;
                List<DecodedInstruction> list;
                if (!byOwner.TryGetValue(owner, out list))
                {
                    list = new List<DecodedInstruction>();
                    byOwner[owner] = list;
                }
                list.Add(kv.Value);
            }

            // 函式名稱
            var names = new Dictionary<ulong, string>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var seed in c.Seeds.Values.OrderBy(s => s.Start))
            {
                if (!byOwner.ContainsKey(seed.Start)) continue;
                string name = seed.Name;
                var ins = byOwner[seed.Start];
                if (name == null && ins.Count == 1 && ins[0].Flow == InstructionFlow.Jump && !ins[0].BranchTarget.HasValue)
                {
                    var imp = ImportOf(c, ins[0]);
                    if (imp != null)
                    {
                        name = "j_" + SafeName(imp.DisplayName, null);
                        seed.Origin = "匯入跳板（" + imp.FullName + "）";
                        r.ImportThunks[seed.Start] = imp;
                    }
                }
                if (name == null) name = "sub_" + Hex(b, seed.Start);
                string unique = name;
                for (int n = 2; used.Contains(unique); n++) unique = name + "_" + n.ToString(CultureInfo.InvariantCulture);
                used.Add(unique);
                names[seed.Start] = unique;
                r.Symbols[seed.Start] = unique;
            }
            foreach (ulong t in c.BranchTargets)
                if (!r.Symbols.ContainsKey(t)) r.Symbols[t] = "loc_" + Hex(b, t);
            foreach (var imp in b.Imports)
                if (!r.Symbols.ContainsKey(imp.IatAddress)) r.Symbols[imp.IatAddress] = "__imp_" + ImportSymbol(imp);

            foreach (var kv in byOwner.OrderBy(k => k.Key))
            {
                Seed seed;
                c.Seeds.TryGetValue(kv.Key, out seed);
                var f = new DisassembledFunction
                {
                    Start = kv.Key,
                    Name = names.ContainsKey(kv.Key) ? names[kv.Key] : "sub_" + Hex(b, kv.Key),
                    Origin = seed != null ? seed.Origin : "被呼叫"
                };
                var instructions = kv.Value.OrderBy(i => i.Address).ToList();
                foreach (var ins in instructions)
                {
                    var line = MakeLine(r, ins, f);
                    if (ins.Address != f.Start)
                    {
                        string label;
                        if (r.Symbols.TryGetValue(ins.Address, out label)) line.Label = label;
                    }
                    else line.Label = f.Name;
                    f.Lines.Add(line);
                }
                var last = instructions[instructions.Count - 1];
                f.End = last.Address + (ulong)Math.Max(1, last.Length);
                List<ulong> callees;
                if (c.CalleesOf.TryGetValue(kv.Key, out callees)) f.Callees.AddRange(callees);
                r.Functions.Add(f);
            }
        }

        /// <summary>
        /// 先追蹤的函式可能一路「流入」後來才發現的被呼叫位址（例如前一個函式結尾呼叫了不會返回的函式），
        /// 這時從那個位址起連續的指令改歸新函式，讓每個被呼叫的位址都是獨立函式。
        /// </summary>
        private static void SplitFallThroughFunctions(Context c)
        {
            foreach (var seed in c.Seeds.Values.OrderBy(s => s.Start))
            {
                ulong owner;
                if (!c.Owner.TryGetValue(seed.Start, out owner) || owner == seed.Start) continue;
                ulong a = seed.Start;
                DecodedInstruction ins;
                ulong current;
                while (c.Decoded.TryGetValue(a, out ins) && c.Owner.TryGetValue(a, out current) && current == owner)
                {
                    if (a != seed.Start && c.Seeds.ContainsKey(a)) break;
                    c.Owner[a] = seed.Start;
                    a += (ulong)Math.Max(1, ins.Length);
                }
            }
        }

        private static string Hex(LoadedBinary b, ulong address)
        {
            int digits = b.Bits == 16 ? 4 : 8;
            return address.ToString("X" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        /// <summary>把名稱轉成組合語言可用的識別字（保留 C++ 修飾名稱常見的 ? @ $）。</summary>
        private static string SafeName(string name, string fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback ?? "noname";
            var sb = new StringBuilder();
            foreach (char ch in name)
            {
                if (ch < 128 && (char.IsLetterOrDigit(ch) || ch == '_' || ch == '?' || ch == '@' || ch == '$' || ch == '.')) sb.Append(ch);
                else sb.Append('_');
            }
            string s = sb.ToString();
            if (s.Length == 0) return fallback ?? "noname";
            if (char.IsDigit(s[0])) s = "_" + s;
            return s;
        }

        private static string ImportSymbol(PeImport imp)
        {
            if (imp.Name != null) return SafeName(imp.Name, "noname");
            return SafeName(imp.Dll, "dll") + "_" + imp.Ordinal.ToString(CultureInfo.InvariantCulture);
        }

        // =====================================================================
        // 符號化
        // =====================================================================

        /// <summary>收集原始碼輸出需要的資料定義（字串、全域變數）。</summary>
        private sealed class SourceData
        {
            public readonly SortedDictionary<ulong, string> Definitions = new SortedDictionary<ulong, string>();
            public readonly Dictionary<ulong, string> Labels = new Dictionary<ulong, string>();
        }

        private static AsmListingLine MakeLine(DisassemblyResult r, DecodedInstruction ins, DisassembledFunction f)
        {
            var comments = new List<string>();
            string operands = FormatOperands(r, ins, comments, f, null);
            return new AsmListingLine
            {
                Address = ins.Address,
                Instruction = ins,
                Mnemonic = string.IsNullOrEmpty(ins.Prefix) ? ins.Mnemonic : ins.Prefix + " " + ins.Mnemonic,
                Operands = operands,
                Comment = comments.Count > 0 ? string.Join("；", comments.Distinct()) : null
            };
        }

        /// <summary>產生符號化的運算元文字。data 不為 null 時是原始碼模式（字串與全域變數改用標籤，並記錄資料定義）。</summary>
        private static string FormatOperands(DisassemblyResult r, DecodedInstruction ins, List<string> comments, DisassembledFunction f, SourceData data)
        {
            var b = r.Binary;
            var parts = new List<string>();
            foreach (var op in ins.Operands)
            {
                string text = op.Text ?? string.Empty;
                switch (op.Kind)
                {
                    case OperandKind.RelativeBranch:
                        if (op.AbsoluteAddress.HasValue)
                        {
                            // 呼叫匯入跳板（jmp [__imp_X]）等於呼叫匯入函式：記到函式的 API 清單；原始碼模式直接寫成 [__imp_X]，不需要附上跳板
                            PeImport thunkImport;
                            if (r.ImportThunks.TryGetValue(op.AbsoluteAddress.Value, out thunkImport))
                            {
                                comments.Add(thunkImport.FullName);
                                if (f != null && !f.ImportsUsed.Contains(thunkImport.FullName)) f.ImportsUsed.Add(thunkImport.FullName);
                                if (data != null)
                                {
                                    text = (b.Bits == 64 ? "qword" : "dword") + " ptr [__imp_" + ImportSymbol(thunkImport) + "]";
                                    break;
                                }
                            }
                            string sym;
                            if (r.Symbols.TryGetValue(op.AbsoluteAddress.Value, out sym)) text = sym;
                        }
                        break;
                    case OperandKind.Memory:
                        if (op.AbsoluteAddress.HasValue && IsPlainSegment(op))
                            text = SymbolizeMemory(r, ins, op, text, comments, f, data);
                        break;
                    case OperandKind.Immediate:
                        text = SymbolizeImmediate(r, ins, op, text, comments, f, data);
                        break;
                }
                parts.Add(text);
            }
            return string.Join(", ", parts);
        }

        private static string ReplaceBracket(string text, string inner)
        {
            int open = text.IndexOf('[');
            int close = text.LastIndexOf(']');
            if (open < 0 || close < open) return text;
            return text.Substring(0, open + 1) + inner + text.Substring(close);
        }

        private static string SymbolizeMemory(DisassemblyResult r, DecodedInstruction ins, DecodedOperand op, string text, List<string> comments,
                                              DisassembledFunction f, SourceData data)
        {
            var b = r.Binary;
            ulong address = op.AbsoluteAddress.Value;
            var imp = b.ImportAt(address);
            if (imp != null)
            {
                comments.Add(imp.FullName + (imp.IsDelayLoad ? "（延遲載入）" : string.Empty));
                if (f != null && !f.ImportsUsed.Contains(imp.FullName)) f.ImportsUsed.Add(imp.FullName);
                return ReplaceBracket(text, "__imp_" + ImportSymbol(imp));
            }

            string sym;
            if (r.Symbols.TryGetValue(address, out sym) && b.IsExecutable(address))
                return ReplaceBracket(text, sym);

            string encoding;
            string s = TryReadString(b, address, out encoding);
            if (s != null)
            {
                comments.Add((encoding == "UTF-16" ? "L" : string.Empty) + "\"" + Shorten(s) + "\"");
                if (f != null && !f.StringsReferenced.Contains(s)) f.StringsReferenced.Add(s);
                if (data != null) return ReplaceBracket(text, DefineString(b, data, address, s, encoding));
            }
            else if (op.BaseRegister == null && op.IndexRegister == null && !b.IsExecutable(address))
            {
                if (data != null) return ReplaceBracket(text, DefineData(b, data, address, op.SizeBits));
                if (op.RipRelative) comments.Add("0x" + address.ToString("X", CultureInfo.InvariantCulture));
                else comments.Add("全域變數");
            }
            if (op.RipRelative && data == null && s != null) comments.Insert(comments.Count - 1, "0x" + address.ToString("X", CultureInfo.InvariantCulture));
            return text;
        }

        private static string SymbolizeImmediate(DisassemblyResult r, DecodedInstruction ins, DecodedOperand op, string text, List<string> comments,
                                                 DisassembledFunction f, SourceData data)
        {
            var b = r.Binary;
            if (b.Bits == 16)
            {
                // DOS：只有「mov dx/si/di/bx, imm」或 push imm 可能是字串位址
                if (op.SizeBits != 16) return text;
                string m = ins.Mnemonic;
                bool candidate = m == "push" || (m == "mov" && ins.Operands.Count == 2 && ins.Operands[0].Kind == OperandKind.Register &&
                                                 (ins.Operands[0].Register == "dx" || ins.Operands[0].Register == "si" || ins.Operands[0].Register == "di" || ins.Operands[0].Register == "bx"));
                if (!candidate) return text;
            }
            else if (op.SizeBits < 32) return text;

            ulong value = b.Bits == 64 ? (ulong)op.Immediate : b.Bits == 16 ? (ulong)(ushort)op.Immediate : (ulong)(uint)op.Immediate;
            if (value == 0 && b.Bits != 16) return text;
            if (b.SectionAt(value) == null) return text;

            string sym;
            if (b.IsExecutable(value) && r.Symbols.TryGetValue(value, out sym))
            {
                comments.Add("位址 " + sym);
                return data != null ? "offset " + sym : text;
            }
            string encoding;
            string s = TryReadString(b, value, out encoding);
            if (s == null) return text;
            comments.Add((encoding == "UTF-16" ? "L" : string.Empty) + "\"" + Shorten(s) + "\"");
            if (f != null && !f.StringsReferenced.Contains(s)) f.StringsReferenced.Add(s);
            return data != null ? "offset " + DefineString(b, data, value, s, encoding) : text;
        }

        /// <summary>讀取位址上的字串：C 字串（0 結尾）、UTF-16 字串，16 位元時也接受 DOS 的 '$' 結尾字串。</summary>
        private static string TryReadString(LoadedBinary b, ulong address, out string encoding)
        {
            encoding = "ASCII";
            if (b.IsPe && b.IsExecutable(address)) return null;
            string s = b.ReadCString(address, 512, 2);
            if (s != null) return s;
            if (b.Bits == 16)
            {
                var bytes = b.ReadVa(address, 256);
                var sb = new StringBuilder();
                foreach (var x in bytes)
                {
                    if (x == (byte)'$') return sb.Length >= 2 ? sb.Append('$').ToString() : null;
                    if (!LoadedBinary.IsPrintable(x)) return null;
                    sb.Append((char)x);
                }
                return null;
            }
            s = b.ReadWideString(address, 256, 2);
            if (s != null) encoding = "UTF-16";
            return s;
        }

        private static string Shorten(string s)
        {
            string t = s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return t.Length > 60 ? t.Substring(0, 57) + "..." : t;
        }

        private static string DefineString(LoadedBinary b, SourceData data, ulong address, string s, string encoding)
        {
            string label;
            if (data.Labels.TryGetValue(address, out label)) return label;
            label = "str_" + Hex(b, address);
            data.Labels[address] = label;
            var sb = new StringBuilder();
            if (encoding == "UTF-16")
            {
                // 寬字元字串：每個字一個 word（dw "ab" 在 MASM 會把兩個字元塞進同一個 word，所以逐字寫出）
                var chars = s.Select(ch => ch >= 0x20 && ch < 0x7F && ch != '\'' ? "'" + ch + "'" : "0x" + ((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                data.Definitions[address] = label + " dw " + string.Join(", ", chars.Concat(new[] { "0" })) + "        ; L\"" + Shorten(s) + "\"";
                return label;
            }
            sb.Append(label).Append(" db ");
            var parts = new List<string>();
            var run = new StringBuilder();
            foreach (char ch in s)
            {
                if (ch >= 0x20 && ch < 0x7F && ch != '"')
                {
                    run.Append(ch);
                    continue;
                }
                if (run.Length > 0) { parts.Add("\"" + run + "\""); run.Clear(); }
                parts.Add("0x" + ((int)ch).ToString("X2", CultureInfo.InvariantCulture));
            }
            if (run.Length > 0) parts.Add("\"" + run + "\"");
            if (!s.EndsWith("$", StringComparison.Ordinal) || b.Bits != 16) parts.Add("0");
            sb.Append(string.Join(", ", parts));
            data.Definitions[address] = sb.ToString();
            return label;
        }

        private static string DefineData(LoadedBinary b, SourceData data, ulong address, int sizeBits)
        {
            string label;
            if (data.Labels.TryGetValue(address, out label)) return label;
            label = "data_" + Hex(b, address);
            data.Labels[address] = label;
            int size = sizeBits == 8 ? 1 : sizeBits == 16 ? 2 : sizeBits == 64 ? 8 : 4;
            var bytes = b.ReadVa(address, size);
            ulong value = 0;
            for (int i = 0; i < bytes.Length; i++) value |= (ulong)bytes[i] << (8 * i);
            string directive = size == 1 ? "db" : size == 2 ? "dw" : size == 8 ? "dq" : "dd";
            data.Definitions[address] = label + " " + directive + " 0x" + value.ToString("X", CultureInfo.InvariantCulture) +
                                        (bytes.Length == 0 ? "        ; 未初始化資料（.bss），執行時為 0" : "        ; 全域變數的初始值");
            return label;
        }

        // =====================================================================
        // 前往位址（線性反組譯）
        // =====================================================================

        /// <summary>從指定位址線性反組譯最多 maxInstructions 條指令（「前往位址」功能）。context 用來符號化，可為 null。</summary>
        public List<AsmListingLine> DisassembleRange(LoadedBinary binary, DisassemblyResult context, ulong address, int maxInstructions)
        {
            var lines = new List<AsmListingLine>();
            if (binary == null || binary.Bits == 0) return lines;
            var r = context ?? new DisassemblyResult { Binary = binary };
            if (r.Binary == null) r.Binary = binary;
            var section = binary.SectionAt(address);
            if (section == null) return lines;
            var c = new Context { Binary = binary, Decoder = new X86Decoder(binary.Bits), Options = new DisassemblyOptions(), Result = r };
            var bytes = BytesOf(c, section);
            ulong offset = address - section.VirtualAddress;
            int count = Math.Max(1, Math.Min(maxInstructions, 100000));
            while (lines.Count < count && offset < (ulong)bytes.Length)
            {
                var ins = c.Decoder.Decode(bytes, (int)offset, section.VirtualAddress + offset);
                var line = MakeLine(r, ins, null);
                string label;
                if (r.Symbols.TryGetValue(ins.Address, out label)) line.Label = label;
                lines.Add(line);
                offset += (ulong)Math.Max(1, ins.Length);
            }
            return lines;
        }

        // =====================================================================
        // 輸出
        // =====================================================================

        /// <summary>產生函式的清單文字（含位址；showBytes 決定是否顯示機器碼）。function 為 null 時輸出全部函式（最多 20000 行）。</summary>
        public string ToListing(DisassemblyResult result, DisassembledFunction function, bool showBytes)
        {
            if (result == null) return string.Empty;
            var sb = new StringBuilder();
            int digits = result.AddressDigits;
            int lines = 0;
            var functions = function != null ? new List<DisassembledFunction> { function } : result.Functions;
            foreach (var f in functions)
            {
                AppendFunctionHeader(sb, result, f);
                foreach (var line in f.Lines)
                {
                    if (line.Label != null) sb.Append(line.Label).Append(":\r\n");
                    sb.Append(line.ToListingText(showBytes, digits)).Append("\r\n");
                    if (++lines >= 20000 && function == null)
                    {
                        sb.Append("; ……清單太長，只顯示前 20000 行。請在左側選擇單一函式查看。\r\n");
                        return sb.ToString();
                    }
                }
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>把線性反組譯的結果轉成清單文字。</summary>
        public static string LinesToListing(IEnumerable<AsmListingLine> lines, bool showBytes, int addressDigits)
        {
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                if (line.Label != null) sb.Append(line.Label).Append(":\r\n");
                sb.Append(line.ToListingText(showBytes, addressDigits)).Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>函式清單的標頭註解（名稱、範圍、呼叫的函式、使用的 API、字串）。</summary>
        public static string FunctionHeader(DisassemblyResult result, DisassembledFunction f)
        {
            var sb = new StringBuilder();
            if (result != null && f != null) AppendFunctionHeader(sb, result, f);
            return sb.ToString();
        }

        private static void AppendFunctionHeader(StringBuilder sb, DisassemblyResult result, DisassembledFunction f)
        {
            sb.Append("; ==============================================================\r\n");
            sb.Append("; ").Append(f.Name).Append("（").Append(f.Origin).Append("）  0x").Append(f.Start.ToString("X", CultureInfo.InvariantCulture))
              .Append(" - 0x").Append(f.End.ToString("X", CultureInfo.InvariantCulture)).Append("，").Append(f.Lines.Count).Append(" 條指令\r\n");
            if (f.Callees.Count > 0)
                sb.Append("; 呼叫：").Append(string.Join(", ", f.Callees.Select(a => SymbolOrHex(result, a)))).Append("\r\n");
            if (f.ImportsUsed.Count > 0)
                sb.Append("; 使用的 API：").Append(string.Join(", ", f.ImportsUsed)).Append("\r\n");
            if (f.StringsReferenced.Count > 0)
                sb.Append("; 字串：").Append(string.Join(", ", f.StringsReferenced.Take(8).Select(s => "\"" + Shorten(s) + "\""))).Append("\r\n");
            sb.Append("; ==============================================================\r\n");
        }

        private static string SymbolOrHex(DisassemblyResult r, ulong address)
        {
            string s;
            return r.Symbols.TryGetValue(address, out s) ? s : "0x" + address.ToString("X", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 產生可以貼到「程式碼分析」與「推導模擬」的組合語言原始碼（Intel 語法、標籤獨立一行、沒有位址）。
        /// 字串與全域變數會變成 .data 區的定義（str_xxx、data_xxx），匯入函式寫成 [__imp_名稱]。
        /// includeCallees 為 true 時，一併附上它呼叫的檔案內函式（遞迴，最多 50 個）。
        /// </summary>
        public string FunctionSource(DisassemblyResult result, DisassembledFunction function, bool includeCallees)
        {
            if (result == null || function == null) return string.Empty;
            var b = result.Binary;
            var functions = new List<DisassembledFunction> { function };
            if (includeCallees)
            {
                var queue = new Queue<DisassembledFunction>();
                queue.Enqueue(function);
                var seen = new HashSet<ulong> { function.Start };
                while (queue.Count > 0 && functions.Count < MaxSourceFunctions)
                {
                    var f = queue.Dequeue();
                    foreach (ulong callee in f.Callees)
                    {
                        if (!seen.Add(callee)) continue;
                        var target = result.Functions.FirstOrDefault(x => x.Start == callee);
                        if (target == null) continue;
                        functions.Add(target);
                        queue.Enqueue(target);
                        if (functions.Count >= MaxSourceFunctions) break;
                    }
                }
            }

            var data = new SourceData();
            var code = new StringBuilder();
            foreach (var f in functions)
            {
                code.Append("; ---- ").Append(f.Name).Append("（").Append(f.Origin).Append("，0x").Append(f.Start.ToString("X", CultureInfo.InvariantCulture)).Append("）----\r\n");
                foreach (var line in f.Lines)
                {
                    if (line.Label != null) code.Append(line.Label).Append(":\r\n");
                    var comments = new List<string>();
                    string operands = FormatOperands(result, line.Instruction, comments, null, data);
                    string text = "    " + line.Mnemonic + (operands.Length > 0 ? " " + operands : string.Empty);
                    if (comments.Count > 0) text = text.PadRight(44) + " ; " + string.Join("；", comments.Distinct());
                    code.Append(text).Append("\r\n");
                }
                code.Append("\r\n");
            }

            var sb = new StringBuilder();
            sb.Append("; 由 ASSAM 反組譯 ").Append(b.FileName).Append(" 產生：函式 ").Append(function.Name).Append("（0x")
              .Append(function.Start.ToString("X", CultureInfo.InvariantCulture)).Append("）\r\n");
            sb.Append("; 位址已換成標籤：sub_/loc_ 為程式位置，str_ 為字串，data_ 為全域變數，[__imp_名稱] 為透過匯入表呼叫的外部函式。\r\n");
            if (b.Kind == BinaryKind.DosCom) sb.Append("        org 100h\r\n");
            else if (b.Kind == BinaryKind.DosMz) sb.Append(".model small\r\n");
            if (data.Definitions.Count > 0)
            {
                sb.Append(".data\r\n");
                foreach (var d in data.Definitions.Values) sb.Append(d).Append("\r\n");
            }
            sb.Append(".code\r\n");
            sb.Append(code);
            return sb.ToString();
        }
    }
}
