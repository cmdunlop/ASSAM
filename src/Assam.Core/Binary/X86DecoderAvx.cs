using System;
using System.Collections.Generic;
using System.Globalization;

namespace Assam.Core.Binary
{
    /// <summary>
    /// x86 解碼器的 AVX／AVX-512 部分：EVEX 標頭、表格驅動的 VEX／EVEX 運算元命名
    /// （zmm/ymm/xmm0-31、k0-k7、{k1}{z} 遮罩、{1toN} 廣播、{rn-sae}/{sae}、disp8*N 壓縮位移、VSIB）。
    /// </summary>
    public sealed partial class X86Decoder
    {
        bool _evex, _evB, _evZ;
        int _evLL, _evAaa, _evRp, _evVp;
        int _disp8N = 1;        // EVEX disp8*N 的 N（非 EVEX 為 1）
        int _vsibBytes, _vsibHi; // VSIB：索引向量位元組數（0 表示一般定址）與 EVEX.V' 擴充

        static readonly string[] CmpPred =
        {
            "eq", "lt", "le", "unord", "neq", "nlt", "nle", "ord", "eq_uq", "nge", "ngt", "false", "neq_oq", "ge", "gt", "true",
            "eq_os", "lt_oq", "le_oq", "unord_s", "neq_us", "nlt_uq", "nle_uq", "ord_s", "eq_us", "nge_uq", "ngt_uq", "false_os", "neq_os", "ge_oq", "gt_oq", "true_us"
        };
        static readonly string[] PCmpPred = { "eq", "lt", "le", null, "neq", "nlt", "nle", null };
        static readonly string[] RcNames = { "{rn-sae}", "{rd-sae}", "{ru-sae}", "{rz-sae}" };

        bool _rex2;

        void ResetAvx()
        {
            _rex2 = false;
            _evex = false; _evB = false; _evZ = false; _evLL = 0; _evAaa = 0; _evRp = 0; _evVp = 0;
            _disp8N = 1; _vsibBytes = 0; _vsibHi = 0; _r4 = 0; _x4 = 0; _b4 = 0;
        }

        static string VecName(int idx, int bytes)
        {
            return (bytes >= 64 ? "zmm" : bytes >= 32 ? "ymm" : "xmm") + (idx & 31).ToString(CultureInfo.InvariantCulture);
        }

        string IndexName(int idx)
        {
            if (_vsibBytes == 0) return AddrReg(idx);
            return VecName(idx + _vsibHi, _vsibBytes);
        }

        static string AvxKw(int bytes)
        {
            switch (bytes)
            {
                case 1: return "byte";
                case 2: return "word";
                case 4: return "dword";
                case 8: return "qword";
                case 16: return "xmmword";
                case 32: return "ymmword";
                case 64: return "zmmword";
            }
            return null;
        }

        static int TypeBytes(char t, int vl)
        {
            switch (t)
            {
                case 'x': return 16;
                case 'y': return 32;
                case 'z': return 64;
                case 'h': return Math.Max(16, vl / 2);
                case 'q': return Math.Max(16, vl / 4);
                case 'o': return Math.Max(16, vl / 8);
            }
            return vl;
        }

        // APX JMPABS（REX2.M0=0 的 A1 + imm64）
        void EmitJmpAbs(DecodedInstruction ins)
        {
            ulong a = U64();
            var d = new DecodedOperand();
            d.Kind = OperandKind.RelativeBranch; d.Immediate = (long)a; d.SizeBits = 64;
            d.AbsoluteAddress = a; d.Text = "0x" + a.ToString("x", CultureInfo.InvariantCulture);
            ins.Mnemonic = "jmpabs";
            ins.Operands.Add(d);
            FinishLen(ins);
            if (_over) { ins.IsValid = false; return; }
            ins.BranchTarget = a;
            ins.Flow = InstructionFlow.Jump;
        }

        // ---------------- EVEX ----------------
        void DecodeEvex(DecodedInstruction ins)
        {
            int p0 = U8(); int p1 = U8(); int p2 = U8();
            if (_over) return;
            int map = p0 & 7;
            if ((p1 & 4) == 0) return;                          // P1 bit2 必為 1
            if ((p0 & 0x08) != 0 && map != 4) return;           // P0 bit3 保留
            if (map == 0 || map == 7) return;
            _vex = true; _evex = true;
            _vexMap = map;
            _vexW = (p1 & 0x80) != 0;
            _vexPP = p1 & 3;
            _evLL = (p2 >> 5) & 3;
            _vexL = _evLL == 0 ? 0 : 1;
            _rex = ((p0 & 0x80) == 0 ? 4 : 0) | ((p0 & 0x40) == 0 ? 2 : 0) | ((p0 & 0x20) == 0 ? 1 : 0) | (_vexW ? 8 : 0);
            _hasRex = true;
            if (Bits == 64) { _evRp = (p0 & 0x10) == 0 ? 16 : 0; _evVp = (p2 & 8) == 0 ? 16 : 0; }
            else _rex &= ~1; // 非 64 位元模式忽略 B
            _vvvv = (~(p1 >> 3)) & 0xF;
            _evB = (p2 & 0x10) != 0;
            _evZ = (p2 & 0x80) != 0;
            _evAaa = p2 & 7;
            _mandatory = _vexPP == 1 ? 0x66 : _vexPP == 2 ? 0xF3 : _vexPP == 3 ? 0xF2 : 0;
            _p66 = _vexPP == 1; _rep = _vexPP == 2; _repne = _vexPP == 3;
            int op = U8();
            if (_over) return;
            ReadModRM(AddrBitsFor());
            if (_over) return;
            if (AvxTableDecode(ins, op)) return;
            // 表中沒有的 EVEX：只保證長度正確
            ReadMemTail(false);
            if (_vexMap == 3 || (_vexMap == 1 && VexMap1HasImm(op))) U8();
            FinishLen(ins);
            if (_over) { ins.IsValid = false; return; }
            ins.Mnemonic = "(bad)"; ins.Operands.Clear(); ins.IsValid = true; ins.Flow = InstructionFlow.Sequential;
        }

        // ---------------- 表格驅動 ----------------
        /// <summary>以 AVX 表解碼（ModRM 已讀）。命中回傳 true（結果可能為無效）；未命中回傳 false。</summary>
        bool AvxTableDecode(DecodedInstruction ins, int op)
        {
            if (_vexMap < 1 || _vexMap > 3) return false;
            bool mem = _mod != 3;
            AvxEnt e = X86AvxTables.FindAnyW(_evex, _vexMap, _vexPP, _vexW ? 1 : 0, op, _regField);
            while (e != null)
            {
                if ((e.Flags & AvxFl.MemOnly) != 0 && !mem) { e = e.Next; continue; }
                if ((e.Flags & AvxFl.RegOnly) != 0 && mem) { e = e.Next; continue; }
                break;
            }
            if (e == null) return false;
            if (!AvxBuild(ins, e, op)) ins.IsValid = false;
            return true;
        }

        void AvxVec(DecodedOperand d, int idx, int bytes)
        {
            SetReg(d, VecName(idx, bytes), bytes * 8);
        }

        void AvxMemOp(DecodedOperand d, int kwBytes, bool bcst, int elem, int fullBytes)
        {
            if (bcst)
            {
                BuildMemCore(d, elem * 8, AvxKw(elem));
                d.Text += "{1to" + (fullBytes / elem).ToString(CultureInfo.InvariantCulture) + "}";
            }
            else BuildMemCore(d, kwBytes * 8, AvxKw(kwBytes));
        }

        bool AvxBuild(DecodedInstruction ins, AvxEnt e, int op)
        {
            bool mem = _mod != 3;
            int fl = e.Flags;
            bool w64 = Bits == 64 && _vexW;
            int vl;
            bool rc = _evex && _evB && !mem;
            if (_evex)
            {
                if (rc)
                {
                    if ((fl & (AvxFl.ER | AvxFl.SAE)) == 0) return false;
                    vl = 64;
                }
                else
                {
                    if (_evLL == 3 && e.Vector) return false;
                    vl = _evLL >= 2 ? 64 : (16 << _evLL);
                }
            }
            else vl = _vexL != 0 ? 32 : 16;
            if ((fl & AvxFl.L1) != 0 && vl < 32) return false;
            if ((fl & AvxFl.L0) != 0 && vl != 16) return false;
            if ((fl & AvxFl.L2) != 0 && vl < 64) return false;
            bool bcst = _evex && _evB && mem;
            if (bcst && e.Tuple != AvxTup.FV && e.Tuple != AvxTup.HV) return false;
            if ((fl & AvxFl.NoVvvv) != 0 && _vvvv != 0) return false;

            int ew = e.WSel >= 0 ? e.WSel : (_vexW ? 1 : 0);
            bool gprW = (fl & AvxFl.GprW) != 0; // 以通用暫存器大小決定的名稱／元素（非 64 位元模式忽略 W）
            if (gprW) ew = w64 ? 1 : 0;
            int elem = e.Elem != 0 ? e.Elem : ((gprW ? w64 : _vexW) ? 8 : 4);
            int memBytes = -1, fullBytes = vl;
            switch (e.Tuple)
            {
                case AvxTup.FV: memBytes = bcst ? elem : vl; fullBytes = vl; break;
                case AvxTup.HV: memBytes = bcst ? elem : vl / 2; fullBytes = vl / 2; break;
                case AvxTup.FVM: memBytes = vl; break;
                case AvxTup.HVM: memBytes = vl / 2; break;
                case AvxTup.QVM: memBytes = vl / 4; break;
                case AvxTup.OVM: memBytes = vl / 8; break;
                case AvxTup.T1S: memBytes = elem; break;
                case AvxTup.T2: memBytes = 2 * elem; break;
                case AvxTup.T4: memBytes = 4 * elem; break;
                case AvxTup.T8: memBytes = 8 * elem; break;
                case AvxTup.M128: memBytes = 16; break;
                case AvxTup.DUP: memBytes = vl == 16 ? 8 : vl; break;
            }
            if (_evex && memBytes > 0) _disp8N = memBytes;

            if (e.Vsib != 0)
            {
                if (!mem || _rmField != 4 || _addrBits == 16) return false;
                _vsibBytes = e.Vsib == 1 ? vl : Math.Max(16, vl / 2);
                _vsibHi = _evex ? _evVp : 0;
            }
            ReadMemTail(false);
            if (_over) return false;

            var ops = new List<DecodedOperand>(4);
            int rmIdx = -1;
            int immIdx = -1; long immVal = 0;
            foreach (string tk in e.Toks)
            {
                var d = new DecodedOperand();
                char k = tk[0];
                char t = tk.Length > 1 ? tk[1] : 'v';
                int kwb = memBytes > 0 ? memBytes : TokMemBytes(tk, vl, w64);
                if ((fl & AvxFl.KwVl) != 0) kwb = vl;
                switch (k)
                {
                    case 'V': AvxVec(d, _regField + (RexR ? 8 : 0) + (_evex ? _evRp : 0), TypeBytes(t, vl)); break;
                    case 'H': AvxVec(d, _vvvv + (_evex ? _evVp : 0), TypeBytes(t, vl)); break;
                    case 'W':
                    case 'U':
                        if (mem) { if (k == 'U') return false; AvxMemOp(d, kwb, bcst, elem, fullBytes); }
                        else AvxVec(d, _rmField + (RexB ? 8 : 0) + (_evex && RexX ? 16 : 0), TypeBytes(t, vl));
                        rmIdx = ops.Count;
                        break;
                    case 'M':
                        if (!mem) return false;
                        AvxMemOp(d, kwb, bcst, elem, fullBytes);
                        rmIdx = ops.Count;
                        break;
                    case 'K':
                        switch (t)
                        {
                            case 'g': SetReg(d, "k" + (_regField & 7).ToString(CultureInfo.InvariantCulture), 64); break;
                            case 'h': SetReg(d, "k" + (_vvvv & 7).ToString(CultureInfo.InvariantCulture), 64); break;
                            case 'r':
                                if (mem) return false;
                                SetReg(d, "k" + _rmField.ToString(CultureInfo.InvariantCulture), 64); rmIdx = ops.Count; break;
                            default:
                                if (mem) AvxMemOp(d, kwb, false, elem, fullBytes);
                                else SetReg(d, "k" + _rmField.ToString(CultureInfo.InvariantCulture), 64);
                                rmIdx = ops.Count; break;
                        }
                        break;
                    case 'G':
                        { int bits = t == 'y' ? (w64 ? 64 : 32) : 32; SetReg(d, GprName(_regField + (RexR ? 8 : 0), bits), bits); }
                        break;
                    case 'B':
                        { int bits = t == 'y' ? (w64 ? 64 : 32) : 32; SetReg(d, GprName(_vvvv & 15, bits), bits); }
                        break;
                    case 'E':
                    case 'R':
                        if (mem)
                        {
                            if (k == 'R') return false;
                            AvxMemOp(d, kwb, false, elem, fullBytes);
                        }
                        else
                        {
                            int bits = t == 'y' ? (w64 ? 64 : 32) : 32;
                            SetReg(d, GprName(_rmField + (RexB ? 8 : 0), bits), bits);
                        }
                        rmIdx = ops.Count;
                        break;
                    case 'I':
                        {
                            int v = U8();
                            d.Kind = OperandKind.Immediate; d.Immediate = v; d.SizeBits = 8;
                            d.Text = "0x" + v.ToString("x", CultureInfo.InvariantCulture);
                            immIdx = ops.Count; immVal = v;
                        }
                        break;
                    case 'L':
                        {
                            int v = U8();
                            int ri = (v >> 4) & 15; if (Bits != 64) ri &= 7;
                            AvxVec(d, ri, TypeBytes(t, vl));
                        }
                        break;
                    default:
                        return false;
                }
                ops.Add(d);
            }
            if (_over) return false;

            string mn = (ew != 0 && e.N1 != null) ? e.N1 : e.N0;
            if (immIdx >= 0)
            {
                if ((fl & AvxFl.Cmp) != 0 && immVal < 32)
                {
                    mn = "vcmp" + CmpPred[(int)immVal] + mn.Substring(4);
                    ops.RemoveAt(immIdx);
                }
                else if ((fl & AvxFl.PCmp) != 0 && immVal < 8 && PCmpPred[(int)immVal] != null)
                {
                    mn = "vpcmp" + PCmpPred[(int)immVal] + mn.Substring(5);
                    ops.RemoveAt(immIdx);
                }
            }
            if (rc)
            {
                // 捨入／SAE 附在最後一個非立即值運算元上（與 objdump 相同）
                int ri = ops.Count - 1;
                if (ri >= 0 && ops[ri].Kind == OperandKind.Immediate) ri--;
                if (ri >= 0) ops[ri].Text += (fl & AvxFl.ER) != 0 ? RcNames[_evLL & 3] : "{sae}";
            }
            if (_evex && ops.Count > 0)
            {
                string sfx = "";
                if (_evAaa != 0) sfx = "{k" + _evAaa.ToString(CultureInfo.InvariantCulture) + "}";
                if (_evZ) sfx += "{z}";
                if (sfx.Length > 0) ops[0].Text += sfx;
            }

            ins.Mnemonic = mn;
            foreach (var o in ops) ins.Operands.Add(o);
            FinishLen(ins);
            if (_over) return false;
            ApplyPrefixes(ins, op, _vexMap, false);
            ins.Flow = InstructionFlow.Sequential;
            return true;
        }

        static int TokMemBytes(string tk, int vl, bool w64)
        {
            char t = tk.Length > 1 ? tk[1] : 'v';
            if (t == '0') return 0;
            if (tk[0] == 'E')
            {
                switch (t) { case 'b': return 1; case 'w': return 2; case 'd': return 4; default: return w64 ? 8 : 4; }
            }
            return TypeBytes(t, vl);
        }
    }
}
