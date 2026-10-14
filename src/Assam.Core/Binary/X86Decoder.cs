using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Assam.Core.Binary
{
    /// <summary>
    /// x86／x64 機器碼解碼器（16、32、64 位元模式），輸出 Intel 語法（格式與 objdump -M intel 一致，小寫）。
    /// 純軟體表格解碼，不需要網路或任何外部函式庫。絕不丟出例外、絕不讀出緩衝區範圍。
    /// </summary>
    public sealed class X86Decoder
    {
        public X86Decoder(int bits)
        {
            if (bits != 16 && bits != 32 && bits != 64) throw new ArgumentException("bits 必須是 16、32 或 64", "bits");
            Bits = bits;
        }

        public int Bits { get; private set; }

        // ---- 暫存器名稱表 ----
        static readonly string[] Reg8NoRex = { "al", "cl", "dl", "bl", "ah", "ch", "dh", "bh" };
        static readonly string[] Reg8Rex = { "al", "cl", "dl", "bl", "spl", "bpl", "sil", "dil", "r8b", "r9b", "r10b", "r11b", "r12b", "r13b", "r14b", "r15b" };
        static readonly string[] Reg16 = { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di", "r8w", "r9w", "r10w", "r11w", "r12w", "r13w", "r14w", "r15w" };
        static readonly string[] Reg32 = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi", "r8d", "r9d", "r10d", "r11d", "r12d", "r13d", "r14d", "r15d" };
        static readonly string[] Reg64 = { "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15" };
        static readonly string[] SegNames = { "es", "cs", "ss", "ds", "fs", "gs" };
        static readonly string[] Addr16Base = { "bx", "bx", "bp", "bp", "si", "di", "bp", "bx" };
        static readonly string[] Addr16Index = { "si", "di", "si", "di", null, null, null, null };

        // ---- 解碼狀態（重複使用以減少配置）----
        byte[] _code;
        int _start, _limit, _pos;
        bool _over;
        bool _p66, _p67, _lock, _repne, _rep;
        int _seg;           // -1 或 0..5
        int _rex;           // 0 無；否則 0x40..0x4F
        bool _hasRex;
        int _mandatory;     // 0 無、0x66、0xF2、0xF3

        // ModRM / 記憶體描述
        bool _hasModRM;
        int _mod, _regField, _rmField;
        bool _rmIsMem;
        // 記憶體：
        string _mBase, _mIndex;
        int _mScale;
        long _mDisp;
        bool _mHasDisp, _mRip, _mAbs, _mSibEiz;
        int _addrBits;

        // VEX
        bool _vex;
        int _vvvv, _vexL, _vexPP, _vexMap;
        bool _vexW;

        public DecodedInstruction Decode(byte[] code, int offset, ulong address)
        {
            var ins = new DecodedInstruction();
            ins.Address = address;
            if (code == null || offset < 0 || offset >= code.Length)
                return MakeBadEmpty(ins);
            _code = code; _start = offset; _pos = offset;
            _limit = offset + 15; if (_limit > code.Length) _limit = code.Length;
            _over = false; _p66 = _p67 = _lock = _repne = _rep = false; _seg = -1; _rex = 0; _hasRex = false; _mandatory = 0;
            _hasModRM = false; _rmIsMem = false; _vex = false; _vexW = false; _vvvv = 0; _vexL = 0; _vexPP = 0; _vexMap = 0;
            _relOperand = null; _vexImmRead = false; _opRegIdx = 0; _fwaitMerge = false; _segConsumed = false; _orphanRex = -1;
            try
            {
                DecodeCore(ins);
            }
            catch
            {
                return MakeBad(ins);
            }
            if (_over || !ins.IsValid)
                return MakeBad(ins);
            return ins;
        }

        DecodedInstruction MakeBadEmpty(DecodedInstruction ins)
        {
            ins.IsValid = false; ins.Length = 1; ins.Mnemonic = "(bad)"; ins.Flow = InstructionFlow.Invalid;
            ins.Bytes = new byte[0];
            return ins;
        }

        DecodedInstruction MakeBad(DecodedInstruction ins)
        {
            ins.Operands.Clear();
            ins.IsValid = false; ins.Length = 1; ins.Mnemonic = "(bad)"; ins.Flow = InstructionFlow.Invalid;
            ins.Prefix = null; ins.BranchTarget = null;
            ins.Bytes = new byte[] { _code[_start] };
            return ins;
        }

        // ---------------- 位元組讀取 ----------------
        byte U8()
        {
            if (_pos >= _limit) { _over = true; return 0; }
            return _code[_pos++];
        }
        byte Peek()
        {
            if (_pos >= _limit) return 0;
            return _code[_pos];
        }
        ushort U16() { int a = U8(); int b = U8(); return (ushort)(a | (b << 8)); }
        uint U32() { uint a = U8(); uint b = U8(); uint c = U8(); uint d = U8(); return a | (b << 8) | (c << 16) | (d << 24); }
        ulong U64() { ulong lo = U32(); ulong hi = U32(); return lo | (hi << 32); }

        // ---------------- 主解碼 ----------------
        void DecodeCore(DecodedInstruction ins)
        {
            ReadPrefixes();
            if (_orphanRex >= 0) { EmitOrphanRex(ins); return; }
            int op = U8();
            OpEntry e;
            bool twoByte = false;
            int map = 0; // 0=1byte,1=0F,2=0F38,3=0F3A

            if (Bits == 64 && op == 0xD5)
            {
                // REX2（APX）：D5 + payload，之後是 1 位元組或 0F map 的 opcode
                int payload = U8();
                _hasRex = true;
                _rex = 0x40 | (payload & 0x0F); // W R X B（低 4 位元）
                bool m0 = (payload & 0x80) != 0;
                op = U8();
                if (m0) { twoByte = true; map = 1; e = PickMap(X86OpcodeTables.TwoByte, X86OpcodeTables.TwoByte66, X86OpcodeTables.TwoByteF3, X86OpcodeTables.TwoByteF2, op); }
                else e = X86OpcodeTables.OneByte[op];
            }
            else if (op == 0x0F)
            {
                twoByte = true;
                int op2 = U8();
                if (op2 == 0x38) { map = 2; op = U8(); e = PickMap(X86OpcodeTables.Three38, X86OpcodeTables.Three38_66, null, X86OpcodeTables.Three38_F2, op); }
                else if (op2 == 0x3A) { map = 3; op = U8(); e = PickMap(X86OpcodeTables.Three3A, X86OpcodeTables.Three3A_66, null, null, op); }
                else if (op2 == 0x0F) { Decode3DNow(ins); return; }
                else { map = 1; op = op2; e = PickMap(X86OpcodeTables.TwoByte, X86OpcodeTables.TwoByte66, X86OpcodeTables.TwoByteF3, X86OpcodeTables.TwoByteF2, op); }
            }
            else if ((op == 0xC4 || op == 0xC5) && (Bits == 64 || (Peek() & 0xC0) == 0xC0))
            {
                DecodeVex(ins, op);
                return;
            }
            else if (op == 0x62 && Bits == 64)
            {
                DecodeEvex(ins);
                return;
            }
            else
            {
                e = X86OpcodeTables.OneByte[op];
            }

            // 特例：某些 opcode 在 64 位元下意義不同
            if (!twoByte)
            {
                if (op == 0x63 && Bits == 64) { e = MkLocal("movsxd", new Opr(Am.G, Ty.V), new Opr(Am.E, Ty.D)); }
                if (op == 0xE3) { int ab = AddrBitsFor(); e.Mn = ab == 16 ? "jcxz" : ab == 32 ? "jecxz" : "jrcxz"; }
                if ((op >= 0x90 && op <= 0x97)) { DecodeXchgNop(ins, op); return; }
                if (op == 0x98) { ins.Mnemonic = Opsize0(e) == 16 ? "cbw" : Opsize0(e) == 32 ? "cwde" : "cdqe"; FinishSimple(ins); return; }
                if (op == 0x99) { ins.Mnemonic = Opsize0(e) == 16 ? "cwd" : Opsize0(e) == 32 ? "cdq" : "cqo"; FinishSimple(ins); return; }
                if (op == 0x9B && IsFwaitMerge()) { int nb = U8(); _fwaitMerge = true; DecodeX87(ins, nb); return; }
                if (op == 0xD7) { EmitXlat(ins); return; }
                if (op >= 0xD8 && op <= 0xDF) { DecodeX87(ins, op); return; }
            }

            // endbr64/endbr32（F3 0F 1E FA/FB）
            if (map == 1 && op == 0x1E && _rep)
            {
                int m = Peek();
                if (m == 0xFA || m == 0xFB)
                {
                    U8();
                    ins.Mnemonic = m == 0xFA ? "endbr64" : "endbr32";
                    FinishLen(ins);
                    if (_over) { ins.IsValid = false; return; }
                    ins.IsValid = true; ins.Flow = InstructionFlow.Sequential;
                    return;
                }
            }

            if (e.Grp >= 0 && e.Mn == null && !e.IsInherit)
            {
                // 群組：先讀 ModRM
                int grp = e.Grp;
                ReadModRM(AddrBitsFor());
                e = ResolveGroup(ins, grp, op, map);
                if (e.Mn == null) { ins.IsValid = false; ins.Mnemonic = "(bad)"; ins.Flow = InstructionFlow.Invalid; FinishLen(ins); return; }
            }
            else
            {
                if (e.IsInherit) e = X86OpcodeTables.Bad;
                if (NeedsModRM(e)) ReadModRM(AddrBitsFor());
            }

            if (e.Mn == null) { ins.IsValid = false; ins.Mnemonic = "(bad)"; ins.Flow = InstructionFlow.Invalid; FinishLen(ins); return; }
            if (e.Mn == "*3dnow*") { Decode3DNow(ins); return; }

            // 無效（64 位元）
            if (Bits == 64 && (e.Flags & X86OpcodeTables.F_I64) != 0)
            { ins.IsValid = false; ins.Mnemonic = "(bad)"; ins.Flow = InstructionFlow.Invalid; FinishLen(ins); return; }

            EmitEntry(ins, e, op, map);
        }

        int AddrBitsFor()
        {
            if (Bits == 64) return _p67 ? 32 : 64;
            if (Bits == 32) return _p67 ? 16 : 32;
            return _p67 ? 32 : 16;
        }

        int _orphanRex;

        static bool IsPrefixByte(int b)
        {
            switch (b)
            {
                case 0x66: case 0x67: case 0xF0: case 0xF2: case 0xF3:
                case 0x2E: case 0x36: case 0x3E: case 0x26: case 0x64: case 0x65:
                    return true;
            }
            return b >= 0x40 && b <= 0x4F;
        }

        void ReadPrefixes()
        {
            while (true)
            {
                int b = Peek();
                if (_pos >= _limit) break;
                if (Bits == 64 && b >= 0x40 && b <= 0x4F)
                {
                    int nxt = (_pos + 1 < _limit) ? _code[_pos + 1] : -1;
                    if (nxt >= 0 && IsPrefixByte(nxt)) { _orphanRex = b; _pos++; return; }
                    _rex = b; _hasRex = true; _pos++; continue;
                }
                switch (b)
                {
                    case 0x66: _p66 = true; _mandatory = _mandatory == 0 ? 0x66 : _mandatory; _rex = 0; _hasRex = false; _pos++; continue;
                    case 0x67: _p67 = true; _rex = 0; _hasRex = false; _pos++; continue;
                    case 0xF0: _lock = true; _rex = 0; _hasRex = false; _pos++; continue;
                    case 0xF2: _repne = true; _mandatory = 0xF2; _rex = 0; _hasRex = false; _pos++; continue;
                    case 0xF3: _rep = true; _mandatory = 0xF3; _rex = 0; _hasRex = false; _pos++; continue;
                    case 0x2E: _seg = 1; _rex = 0; _hasRex = false; _pos++; continue;
                    case 0x36: _seg = 2; _rex = 0; _hasRex = false; _pos++; continue;
                    case 0x3E: _seg = 3; _rex = 0; _hasRex = false; _pos++; continue;
                    case 0x26: _seg = 0; _rex = 0; _hasRex = false; _pos++; continue;
                    case 0x64: _seg = 4; _rex = 0; _hasRex = false; _pos++; continue;
                    case 0x65: _seg = 5; _rex = 0; _hasRex = false; _pos++; continue;
                }
                break;
            }
            // 66 若實際是強制前綴（SSE），由 PickMap 決定；此處先設 _mandatory。
            if (_mandatory == 0x66 && (_rep || _repne)) _mandatory = _rep ? 0xF3 : 0xF2;
        }

        bool RexW { get { return (_rex & 8) != 0; } }
        bool RexR { get { return (_rex & 4) != 0; } }
        bool RexX { get { return (_rex & 2) != 0; } }
        bool RexB { get { return (_rex & 1) != 0; } }

        OpEntry PickMap(OpEntry[] baseT, OpEntry[] t66, OpEntry[] tF3, OpEntry[] tF2, int op)
        {
            // 依強制前綴優先序 F3 > F2 > 66 選擇
            if (_rep && tF3 != null && !tF3[op].IsInherit) return tF3[op];
            if (_repne && tF2 != null && !tF2[op].IsInherit) return tF2[op];
            if (_p66 && t66 != null && !t66[op].IsInherit) return t66[op];
            return baseT[op];
        }

        bool NeedsModRM(OpEntry e)
        {
            return UsesModRM(e.A) || UsesModRM(e.B) || UsesModRM(e.C) || UsesModRM(e.D);
        }
        static bool UsesModRM(Opr o)
        {
            switch (o.M)
            {
                case Am.E: case Am.G: case Am.M: case Am.R: case Am.Seg: case Am.Cr: case Am.Dr:
                case Am.V: case Am.W: case Am.U: case Am.P: case Am.N: case Am.Q:
                    return true;
            }
            return false;
        }
        bool RmForcedReg(OpEntry e)
        {
            return IsRegForced(e.A) || IsRegForced(e.B) || IsRegForced(e.C) || IsRegForced(e.D);
        }
        static bool IsRegForced(Opr o) { return o.M == Am.R || o.M == Am.U || o.M == Am.N || o.M == Am.Cr || o.M == Am.Dr; }

        // ---------------- ModRM / SIB / 位移 ----------------
        void ReadModRM(int addrBits)
        {
            if (_hasModRM) return;
            _hasModRM = true;
            _addrBits = addrBits;
            int m = U8();
            _mod = m >> 6; _regField = (m >> 3) & 7; _rmField = m & 7;
            _mBase = null; _mIndex = null; _mScale = 1; _mDisp = 0; _mHasDisp = false;
            _mRip = false; _mAbs = false; _mSibEiz = false; _rmIsMem = (_mod != 3);
        }

        // 在 EmitEntry 得知 rmForcedReg 後才決定是否讀 SIB/位移
        void ReadMemTail(bool rmForcedReg)
        {
            if (_mod == 3 || rmForcedReg) { _rmIsMem = false; return; }
            _rmIsMem = true;
            if (_addrBits == 16)
            {
                _mScale = 1;
                if (_mod == 0 && _rmField == 6) { _mAbs = true; _mDisp = (short)U16(); _mHasDisp = true; }
                else
                {
                    _mBase = Addr16Base[_rmField];
                    _mIndex = Addr16Index[_rmField];
                    if (_mod == 1) { _mDisp = (sbyte)U8(); _mHasDisp = true; }
                    else if (_mod == 2) { _mDisp = (short)U16(); _mHasDisp = true; }
                }
                return;
            }
            // 32 / 64 位元定址
            if (_rmField == 4)
            {
                int sib = U8();
                int scale = 1 << (sib >> 6);
                int indexF = (sib >> 3) & 7;
                int baseF = sib & 7;
                int indexReg = indexF + (RexX ? 8 : 0);
                bool noIndex = (indexF == 4 && !RexX);
                _mScale = scale;
                if (baseF == 5 && _mod == 0)
                {
                    // 無基底，disp32
                    _mDisp = (int)U32(); _mHasDisp = true;
                    if (noIndex)
                    {
                        if (_addrBits == 64) { _mAbs = true; }
                        else { _mAbs = true; _mSibEiz = true; } // 32 位元顯示 eiz
                    }
                    else
                    {
                        _mIndex = AddrReg(indexReg);
                        _mAbs = false; _mBase = null;
                    }
                }
                else
                {
                    _mBase = AddrReg(baseF + (RexB ? 8 : 0));
                    if (!noIndex) _mIndex = AddrReg(indexReg);
                    else
                    {
                        // eiz 規則
                        if (!(baseF == 4 && (sib >> 6) == 0)) _mSibEiz = true;
                    }
                    if (_mod == 1) { _mDisp = (sbyte)U8(); _mHasDisp = true; }
                    else if (_mod == 2) { _mDisp = (int)U32(); _mHasDisp = true; }
                }
            }
            else
            {
                if (_rmField == 5 && _mod == 0)
                {
                    _mDisp = (int)U32(); _mHasDisp = true;
                    if (_addrBits == 64) { _mRip = true; }
                    else { _mAbs = true; }
                }
                else
                {
                    _mBase = AddrReg(_rmField + (RexB ? 8 : 0));
                    if (_mod == 1) { _mDisp = (sbyte)U8(); _mHasDisp = true; }
                    else if (_mod == 2) { _mDisp = (int)U32(); _mHasDisp = true; }
                }
            }
        }

        string AddrReg(int idx)
        {
            if (_addrBits == 64) return Reg64[idx & 15];
            if (_addrBits == 32) return Reg32[idx & 15];
            return Reg16[idx & 15];
        }

        // ---------------- 群組解析 ----------------
        OpEntry MkLocal(string mn, Opr a, Opr b) { OpEntry e; e.Mn = mn; e.A = a; e.B = b; e.C = default(Opr); e.D = default(Opr); e.Flags = 0; e.Grp = -1; return e; }
        OpEntry MkLocal(string mn, Opr a, Opr b, Opr c) { var e = MkLocal(mn, a, b); e.C = c; return e; }

        OpEntry ResolveGroup(DecodedInstruction ins, int grp, int op, int map)
        {
            var gt = X86OpcodeTables.Groups[grp];
            int reg = _regField;
            // 需依 opcode 決定運算元的群組
            if (grp == X86OpcodeTables.G1)
            {
                string mn = gt[reg].Mn;
                if (op == 0x80 || op == 0x82) return MkLocal(mn, new Opr(Am.E, Ty.B), new Opr(Am.I, Ty.B));
                if (op == 0x81) return MkLocal(mn, new Opr(Am.E, Ty.V), new Opr(Am.I, Ty.Z));
                return MkLocal(mn, new Opr(Am.E, Ty.V), new Opr(Am.I, Ty.Bs)); // 0x83
            }
            if (grp == X86OpcodeTables.G2)
            {
                string mn = gt[reg].Mn;
                Opr dst = (op == 0xC0 || op == 0xD0 || op == 0xD2) ? new Opr(Am.E, Ty.B) : new Opr(Am.E, Ty.V);
                Opr src;
                if (op == 0xC0 || op == 0xC1) src = new Opr(Am.I, Ty.B);
                else if (op == 0xD0 || op == 0xD1) src = new Opr(Am.One, Ty.B);
                else src = new Opr(Am.RCL, Ty.B);
                return MkLocal(mn, dst, src);
            }
            if (grp == X86OpcodeTables.G11b) { if (reg != 0) return X86OpcodeTables.Bad; return MkLocal("mov", new Opr(Am.E, Ty.B), new Opr(Am.I, Ty.B)); }
            if (grp == X86OpcodeTables.G11v) { if (reg != 0) return X86OpcodeTables.Bad; return MkLocal("mov", new Opr(Am.E, Ty.V), new Opr(Am.I, Ty.Z)); }
            if (grp == X86OpcodeTables.G9)
            {
                var ge = gt[reg];
                if (ge.Mn == "*g9_6*" || ge.Mn == "*g9_7*")
                    return ResolveG9Special(reg);
                if (reg == 1 && RexW) return MkLocal("cmpxchg16b", new Opr(Am.M, Ty.Dq), default(Opr));
                return ge;
            }
            if (grp == X86OpcodeTables.G7)
            {
                if (_mod == 3) return ResolveG7Reg(_rmField, reg);
            }
            var r = gt[reg];
            // 群組 12/13/14 的 66 強制前綴形式（XMM）：覆寫運算元為 U(Dq)
            if ((grp == X86OpcodeTables.G12 || grp == X86OpcodeTables.G13 || grp == X86OpcodeTables.G14) && _p66 && r.Mn != null)
            {
                // psrldq/pslldq (G14 reg3/7) 已是 U；其他把 N→U
                if (r.A.M == Am.N) r.A = new Opr(Am.U, Ty.Dq);
                if (grp == X86OpcodeTables.G14 && (reg == 3)) r = MkLocal("psrldq", new Opr(Am.U, Ty.Dq), new Opr(Am.I, Ty.B));
                if (grp == X86OpcodeTables.G14 && (reg == 7)) r = MkLocal("pslldq", new Opr(Am.U, Ty.Dq), new Opr(Am.I, Ty.B));
            }
            return r;
        }

        OpEntry ResolveG9Special(int reg)
        {
            // 0F C7 /6,/7 依強制前綴：vmxon/rdrand/rdseed 等
            if (reg == 6)
            {
                if (_p66) return MkLocal("vmclear", new Opr(Am.M, Ty.Q), default(Opr));
                if (_rep) return MkLocal("vmxon", new Opr(Am.M, Ty.Q), default(Opr));
                if (_mod == 3) return MkLocal("rdrand", new Opr(Am.E, Ty.V), default(Opr));
                return MkLocal("vmptrld", new Opr(Am.M, Ty.Q), default(Opr));
            }
            // reg==7
            if (_mod == 3) { if (_rep) return MkLocal("rdpid", new Opr(Am.R, Ty.V), default(Opr)); return MkLocal("rdseed", new Opr(Am.E, Ty.V), default(Opr)); }
            return MkLocal("rdseed", new Opr(Am.E, Ty.V), default(Opr));
        }

        OpEntry ResolveG7Reg(int rm, int reg)
        {
            // 0F 01 的 mod==3 形式
            switch (reg)
            {
                case 1:
                    if (rm == 0) return MkLocal("monitor", default(Opr), default(Opr));
                    if (rm == 1) return MkLocal("mwait", default(Opr), default(Opr));
                    if (rm == 2) return MkLocal("clac", default(Opr), default(Opr));
                    if (rm == 3) return MkLocal("stac", default(Opr), default(Opr));
                    break;
                case 2:
                    if (rm == 0) return MkLocal("xgetbv", default(Opr), default(Opr));
                    if (rm == 1) return MkLocal("xsetbv", default(Opr), default(Opr));
                    break;
                case 3:
                    if (rm == 0) return MkLocal("vmrun", default(Opr), default(Opr));
                    if (rm == 1) return MkLocal("vmmcall", default(Opr), default(Opr));
                    if (rm == 2) return MkLocal("vmload", default(Opr), default(Opr));
                    if (rm == 3) return MkLocal("vmsave", default(Opr), default(Opr));
                    if (rm == 4) return MkLocal("stgi", default(Opr), default(Opr));
                    if (rm == 5) return MkLocal("clgi", default(Opr), default(Opr));
                    if (rm == 6) return MkLocal("skinit", default(Opr), default(Opr));
                    if (rm == 7) return MkLocal("invlpga", default(Opr), default(Opr));
                    break;
                case 7:
                    if (rm == 0) return MkLocal("swapgs", default(Opr), default(Opr));
                    if (rm == 1) return MkLocal("rdtscp", default(Opr), default(Opr));
                    break;
                case 4:
                    return MkLocal("smsw", new Opr(Am.E, Ty.V), default(Opr));
                case 6:
                    return MkLocal("lmsw", new Opr(Am.E, Ty.W), default(Opr));
            }
            return X86OpcodeTables.Bad;
        }

        // ---------------- 一般指令輸出 ----------------
        int Opsize0(OpEntry e) { return ResolveOpsize(e); }

        int ResolveOpsize(OpEntry e)
        {
            if (Bits == 64)
            {
                if ((e.Flags & X86OpcodeTables.F_O64) != 0) return 64;
                if ((e.Flags & X86OpcodeTables.F_D64) != 0) return _p66 ? 16 : 64;
                if (RexW) return 64;
                return _p66 ? 16 : 32;
            }
            if (Bits == 32) return _p66 ? 16 : 32;
            return _p66 ? 32 : 16;
        }

        void EmitEntry(DecodedInstruction ins, OpEntry e, int op, int map)
        {
            int opsize = ResolveOpsize(e);
            _opRegIdx = (op & 7) + (RexB ? 8 : 0);
            // 讀取記憶體尾段（SIB/位移）
            if (_hasModRM) ReadMemTail(RmForcedReg(e));

            string mn = e.Mn;
            if ((e.Flags & X86OpcodeTables.F_DQNAME) != 0 && RexW && mn != null && mn.EndsWith("d", StringComparison.Ordinal))
                mn = mn.Substring(0, mn.Length - 1) + "q";
            if (map == 0 && mn == "mov")
            {
                if (op >= 0xB8 && op <= 0xBF && opsize == 64) mn = "movabs";
                else if (op >= 0xA0 && op <= 0xA3 && AddrBitsFor() == 64) mn = "movabs";
            }

            // 字串指令：短格式，無運算元
            if (IsStringOp(op, map))
            {
                EmitString(ins, op, opsize);
                return;
            }

            // movlps/movhps 的暫存器形式 → movhlps/movlhps
            if (map == 1 && !_p66 && !_rep && !_repne && _mod == 3)
            {
                if (op == 0x12) mn = "movhlps";
                else if (op == 0x16) mn = "movlhps";
            }
            // mov r/m,Sreg（8C）：記憶體運算元固定為 word
            if (map == 0 && op == 0x8C && _rmIsMem) e.A = new Opr(Am.E, Ty.W);
            // arpl（63，非 64 位元）：reg 為運算元大小；r/m 暫存器時為運算元大小、記憶體時為 word
            if (map == 0 && op == 0x63 && Bits != 64)
            {
                e.B = new Opr(Am.G, Ty.V);
                e.A = _rmIsMem ? new Opr(Am.E, Ty.W) : new Opr(Am.E, Ty.V);
            }

            var ops = new List<DecodedOperand>(4);
            bool ok = true;
            AddOperand(ops, e.A, opsize, ref ok);
            AddOperand(ops, e.B, opsize, ref ok);
            AddOperand(ops, e.C, opsize, ref ok);
            AddOperand(ops, e.D, opsize, ref ok);

            // cmpps/cmppd/cmpss/cmpsd 的 imm8（0..7）→ 偽助憶碼
            if (map == 1 && op == 0xC2 && ops.Count == 3 && ops[2].Kind == OperandKind.Immediate && ops[2].Immediate >= 0 && ops[2].Immediate <= 7)
            {
                string[] pred = { "eq", "lt", "le", "unord", "neq", "nlt", "nle", "ord" };
                string suf = mn.Substring(mn.Length - 2);
                mn = "cmp" + pred[(int)ops[2].Immediate] + suf;
                ops.RemoveAt(2);
            }

            ins.Mnemonic = mn;
            foreach (var o in ops) ins.Operands.Add(o);
            if (!ok) { MarkBad(ins); return; }

            FinishLen(ins);
            ApplyPrefixes(ins, op, map, false);
            SetFlowAndTargets(ins, op, map);
        }

        void FinishSimple(DecodedInstruction ins)
        {
            FinishLen(ins);
            ApplyPrefixes(ins, -1, 0, false);
            ins.IsValid = true;
            ins.Flow = ClassifyFlow(ins.Mnemonic);
        }

        void FinishLen(DecodedInstruction ins)
        {
            int len = _pos - _start;
            if (_over || len <= 0 || len > 15) { ins.IsValid = false; return; }
            ins.Length = len;
            var bytes = new byte[len];
            Buffer.BlockCopy(_code, _start, bytes, 0, len);
            ins.Bytes = bytes;
            ins.Address = ins.Address;
            ins.IsValid = ins.Mnemonic != null && ins.Mnemonic != "(bad)";
            // 修正 RIP 相對與相對分支的絕對位址
            ulong next = ins.Address + (ulong)len;
            foreach (var o in ins.Operands)
            {
                if (o.RipRelative)
                    o.AbsoluteAddress = WrapAddr(next + (ulong)o.Displacement);
            }
        }

        void MarkBad(DecodedInstruction ins)
        {
            ins.Operands.Clear();
            ins.IsValid = false; ins.Mnemonic = "(bad)"; ins.Flow = InstructionFlow.Invalid;
            ins.Length = 1; ins.Bytes = new byte[] { _code[_start] }; ins.Prefix = null; ins.BranchTarget = null;
        }

        bool _segConsumed;

        void ApplyPrefixes(DecodedInstruction ins, int op, int map, bool sse)
        {
            // 未被記憶體運算元使用的區段覆寫，以獨立前綴呈現（objdump 行為）
            string seg = (_seg >= 0 && !_segConsumed) ? SegNames[_seg] : null;
            if (seg == null && !_lock) { ins.Prefix = null; return; }
            var sb = new StringBuilder();
            if (seg != null) sb.Append(seg);
            if (_lock) { if (sb.Length > 0) sb.Append(' '); sb.Append("lock"); }
            ins.Prefix = sb.ToString();
        }

        // ---------------- 運算元 ----------------
        void AddOperand(List<DecodedOperand> ops, Opr o, int opsize, ref bool ok)
        {
            if (o.M == Am.None) return;
            var d = new DecodedOperand();
            switch (o.M)
            {
                case Am.E:
                case Am.Q:
                case Am.W:
                    if (_rmIsMem) { BuildMem(d, o, opsize); }
                    else { BuildRmReg(d, o, opsize); }
                    break;
                case Am.M:
                    if (!_rmIsMem) { ok = false; return; }
                    BuildMem(d, o, opsize);
                    break;
                case Am.R:
                case Am.U:
                case Am.N:
                    BuildRmReg(d, o, opsize);
                    break;
                case Am.G:
                    SetReg(d, GprName(_regField + (RexR ? 8 : 0), TySize(o.T, opsize)), TySize(o.T, opsize));
                    break;
                case Am.V:
                    SetXmm(d, _regField + (RexR ? 8 : 0), o.T);
                    break;
                case Am.P:
                    SetReg(d, "mm" + (_regField & 7).ToString(CultureInfo.InvariantCulture), 64);
                    break;
                case Am.Seg:
                    { int s = _regField & 7; if (s > 5) { ok = false; return; } SetReg(d, SegNames[s], 16); }
                    break;
                case Am.Cr:
                    SetReg(d, "cr" + (_regField + (RexR ? 8 : 0)).ToString(CultureInfo.InvariantCulture), Bits == 64 ? 64 : 32);
                    break;
                case Am.Dr:
                    SetReg(d, "dr" + (_regField + (RexR ? 8 : 0)).ToString(CultureInfo.InvariantCulture), Bits == 64 ? 64 : 32);
                    break;
                case Am.OpReg:
                    {
                        int idx = _opRegIdx;
                        SetReg(d, GprName(idx, TySize(o.T, opsize)), TySize(o.T, opsize));
                    }
                    break;
                case Am.Acc:
                    SetReg(d, GprName(0, TySize(o.T, opsize)), TySize(o.T, opsize));
                    break;
                case Am.RCL: SetReg(d, "cl", 8); break;
                case Am.RDX: SetReg(d, "dx", 16); break;
                case Am.RES: SetReg(d, "es", 16); break;
                case Am.RCS: SetReg(d, "cs", 16); break;
                case Am.RSS: SetReg(d, "ss", 16); break;
                case Am.RDS: SetReg(d, "ds", 16); break;
                case Am.RFS: SetReg(d, "fs", 16); break;
                case Am.RGS: SetReg(d, "gs", 16); break;
                case Am.One:
                    d.Kind = OperandKind.Immediate; d.Immediate = 1; d.SizeBits = 8; d.Text = "1";
                    break;
                case Am.I:
                    BuildImm(d, o, opsize);
                    break;
                case Am.J:
                    BuildRel(d, o, opsize);
                    break;
                case Am.O:
                    BuildMoffs(d, o, opsize);
                    break;
                case Am.Ap:
                    BuildFarPtr(d, o, opsize);
                    break;
                case Am.St:
                    SetReg(d, "st(" + (_rmField & 7).ToString(CultureInfo.InvariantCulture) + ")", 80);
                    break;
                case Am.St0:
                    SetReg(d, "st(0)", 80);
                    break;
                case Am.H:
                    SetXmm(d, _vvvv, o.T);
                    break;
                default:
                    ok = false; return;
            }
            ops.Add(d);
        }

        int _opRegIdx;

        void SetReg(DecodedOperand d, string name, int bits)
        {
            d.Kind = OperandKind.Register; d.Register = name; d.Text = name; d.SizeBits = bits;
        }
        void SetXmm(DecodedOperand d, int idx, Ty t)
        {
            bool ymm = (t == Ty.Qq) || (_vex && _vexL == 1 && t == Ty.Dq);
            string name = (ymm ? "ymm" : "xmm") + (idx & 31).ToString(CultureInfo.InvariantCulture);
            SetReg(d, name, ymm ? 256 : 128);
        }

        void BuildRmReg(DecodedOperand d, Opr o, int opsize)
        {
            int idx = _rmField + (RexB ? 8 : 0);
            switch (o.M)
            {
                case Am.W: case Am.U: SetXmm(d, idx, o.T); return;
                case Am.N: SetReg(d, "mm" + (_rmField & 7).ToString(CultureInfo.InvariantCulture), 64); return;
                case Am.Q: SetReg(d, "mm" + (_rmField & 7).ToString(CultureInfo.InvariantCulture), 64); return;
            }
            if (o.T == Ty.Dq) { SetXmm(d, idx, Ty.Dq); return; }
            int bits = (o.M == Am.R && o.T == Ty.D) ? (Bits == 64 ? 64 : 32) : TySize(o.T, opsize);
            SetReg(d, GprName(idx, bits), bits);
        }

        void BuildMem(DecodedOperand d, Opr o, int opsize)
        {
            d.Kind = OperandKind.Memory;
            int bits = TySize(o.T, opsize);
            d.SizeBits = bits;
            d.Scale = _mScale;
            d.BaseRegister = _mBase;
            d.IndexRegister = _mIndex;
            d.Displacement = _mDisp;
            d.HasDisplacement = _mHasDisp;
            d.RipRelative = _mRip;
            int segOp = (Bits == 64 && _seg >= 0 && _seg <= 3) ? -1 : _seg; // 64 位元 es/cs/ss/ds 以前綴呈現，不進運算元
            if (segOp >= 0) { d.Segment = SegNames[segOp]; _segConsumed = true; }

            var sb = new StringBuilder();
            string kw = SizeKeyword(o.T, opsize);
            if (kw != null) { sb.Append(kw); sb.Append(" ptr "); }
            if (segOp >= 0) { sb.Append(SegNames[segOp]); sb.Append(':'); }

            if (_mRip)
            {
                sb.Append("[rip+0x");
                sb.Append(((ulong)_mDisp).ToString("x", CultureInfo.InvariantCulture));
                sb.Append(']');
            }
            else if (_mAbs && !_mSibEiz)
            {
                // 絕對位址：[0x...]
                ulong a = AbsMask(_mDisp);
                sb.Append('[').Append("0x").Append(a.ToString("x", CultureInfo.InvariantCulture)).Append(']');
                d.AbsoluteAddress = a;
                d.BaseRegister = null; d.IndexRegister = null;
            }
            else
            {
                sb.Append('[');
                bool first = true;
                if (_mBase != null) { sb.Append(_mBase); first = false; }
                if (_mIndex != null)
                {
                    if (!first) sb.Append('+');
                    sb.Append(_mIndex);
                    if (_addrBits != 16) sb.Append('*').Append(_mScale.ToString(CultureInfo.InvariantCulture));
                    first = false;
                }
                else if (_mSibEiz)
                {
                    if (!first) sb.Append('+');
                    sb.Append(_addrBits == 64 ? "riz" : "eiz").Append('*').Append(_mScale.ToString(CultureInfo.InvariantCulture));
                    first = false;
                }
                if (_mHasDisp)
                {
                    if (first) { sb.Append("0x").Append(AbsMask(_mDisp).ToString("x", CultureInfo.InvariantCulture)); d.AbsoluteAddress = AbsMask(_mDisp); }
                    else AppendDisp(sb, _mDisp, true);
                }
                else if (first)
                {
                    sb.Append("0x0");
                }
                sb.Append(']');
            }
            d.Text = sb.ToString();
        }

        void AppendDisp(StringBuilder sb, long disp, bool signed)
        {
            if (disp < 0) sb.Append("-0x").Append(((ulong)(-disp)).ToString("x", CultureInfo.InvariantCulture));
            else sb.Append("+0x").Append(((ulong)disp).ToString("x", CultureInfo.InvariantCulture));
        }

        ulong AbsMask(long v)
        {
            if (_addrBits == 64) return (ulong)v;
            if (_addrBits == 32) return (uint)v;
            return (ushort)v;
        }

        ulong WrapAddr(ulong a)
        {
            if (Bits == 32) return a & 0xFFFFFFFFUL;
            if (Bits == 16) return a & 0xFFFFUL;
            return a;
        }

        void BuildImm(DecodedOperand d, Opr o, int opsize)
        {
            long val;
            d.Kind = OperandKind.Immediate;
            switch (o.T)
            {
                case Ty.B: val = (sbyte)U8(); d.Immediate = val; d.SizeBits = 8; d.Text = ImmText((byte)val, 8); return;
                case Ty.W: val = (short)U16(); d.Immediate = val; d.SizeBits = 16; d.Text = ImmText((ulong)val, 16); return;
                case Ty.Bs:
                    val = (sbyte)U8(); d.Immediate = val; d.SizeBits = opsize;
                    d.Text = ImmText((ulong)val, opsize); d.Kind = OperandKind.Immediate; return;
                case Ty.Z:
                    if (opsize == 16) { val = (short)U16(); } else { val = (int)U32(); }
                    d.Immediate = val; d.SizeBits = opsize; d.Text = ImmText((ulong)val, opsize); d.Kind = OperandKind.Immediate; return;
                case Ty.V:
                default:
                    if (opsize == 16) { val = (short)U16(); }
                    else if (opsize == 32) { val = (int)U32(); }
                    else { val = (long)U64(); }
                    d.Immediate = val; d.SizeBits = opsize; d.Text = ImmText((ulong)val, opsize); d.Kind = OperandKind.Immediate; return;
            }
        }

        string ImmText(ulong v, int bits)
        {
            ulong m;
            if (bits >= 64) m = ulong.MaxValue; else m = (1UL << bits) - 1;
            return "0x" + (v & m).ToString("x", CultureInfo.InvariantCulture);
        }

        void BuildRel(DecodedOperand d, Opr o, int opsize)
        {
            long rel;
            if (o.T == Ty.B) rel = (sbyte)U8();
            else { if (opsize == 16) rel = (short)U16(); else rel = (int)U32(); }
            d.Kind = OperandKind.RelativeBranch;
            d.Immediate = rel;
            d.SizeBits = opsize;
            // 目標位址於 FinishLen 之後計算；此處先記錄 rel 於 Displacement 暫用
            d.Displacement = rel;
            d.HasDisplacement = false;
            d.RipRelative = false;
            _relOperand = d;
        }
        DecodedOperand _relOperand;

        void BuildMoffs(DecodedOperand d, Opr o, int opsize)
        {
            int ab = AddrBitsFor();
            ulong a;
            if (ab == 16) a = U16();
            else if (ab == 32) a = U32();
            else a = U64();
            d.Kind = OperandKind.Memory;
            d.SizeBits = TySize(o.T, opsize);
            d.Displacement = (long)a;
            d.HasDisplacement = true;
            d.AbsoluteAddress = a;
            int segOp = (Bits == 64 && _seg >= 0 && _seg <= 3) ? -1 : _seg;
            if (segOp >= 0) { d.Segment = SegNames[segOp]; _segConsumed = true; }
            var sb = new StringBuilder();
            if (segOp >= 0) sb.Append(SegNames[segOp]).Append(':');
            sb.Append("[0x").Append(a.ToString("x", CultureInfo.InvariantCulture)).Append(']');
            d.Text = sb.ToString();
        }

        void BuildFarPtr(DecodedOperand d, Opr o, int opsize)
        {
            ulong off;
            if (opsize == 16) off = U16(); else off = U32();
            int seg = U16();
            d.Kind = OperandKind.FarPointer;
            d.Text = "0x" + ((ulong)seg).ToString("x", CultureInfo.InvariantCulture) + ":0x" + off.ToString("x", CultureInfo.InvariantCulture);
            d.Immediate = (long)off;
        }

        // ---------------- 尺寸與名稱 ----------------
        int TySize(Ty t, int opsize)
        {
            switch (t)
            {
                case Ty.B: return 8;
                case Ty.W: return 16;
                case Ty.D: return 32;
                case Ty.Q: return 64;
                case Ty.V: return opsize;
                case Ty.Z: return opsize == 16 ? 16 : 32;
                case Ty.P: return opsize + 16;
                case Ty.Dq: return 128;
                case Ty.Qq: return 256;
                case Ty.T80: return 80;
                case Ty.A: return opsize * 2;
                case Ty.Y: return RexW ? 64 : 32;
                case Ty.S6: return 0;
                default: return opsize;
            }
        }

        string SizeKeyword(Ty t, int opsize)
        {
            int bits = TySize(t, opsize);
            if (t == Ty.P)
            {
                if (opsize == 16) return "dword";
                if (opsize == 32) return "fword";
                return "tbyte";
            }
            if (t == Ty.S6 || t == Ty.None) return null;
            switch (bits)
            {
                case 8: return "byte";
                case 16: return "word";
                case 32: return "dword";
                case 64: return "qword";
                case 80: return "tbyte";
                case 128: return "xmmword";
                case 256: return "ymmword";
                default: return null;
            }
        }

        string GprName(int idx, int bits)
        {
            idx &= 15;
            switch (bits)
            {
                case 8: return (_hasRex ? Reg8Rex[idx] : Reg8NoRex[idx & 7]);
                case 16: return Reg16[idx];
                case 32: return Reg32[idx];
                case 64: return Reg64[idx];
                default: return Reg32[idx];
            }
        }

        // ---------------- 流程分類 ----------------
        void SetFlowAndTargets(DecodedInstruction ins, int op, int map)
        {
            ins.Flow = ClassifyFlow(ins.Mnemonic);
            if (_relOperand != null && ins.IsValid)
            {
                ulong target = WrapAddr(ins.Address + (ulong)ins.Length + (ulong)_relOperand.Immediate);
                _relOperand.AbsoluteAddress = target;
                _relOperand.Text = "0x" + target.ToString("x", CultureInfo.InvariantCulture);
                ins.BranchTarget = target;
            }
            _relOperand = null;
        }

        InstructionFlow ClassifyFlow(string mn)
        {
            if (mn == null || mn == "(bad)") return InstructionFlow.Invalid;
            if (mn == "jmp" || mn == "jmpf") return InstructionFlow.Jump;
            if (mn == "call" || mn == "callf") return InstructionFlow.Call;
            if (mn[0] == 'j') return InstructionFlow.ConditionalJump; // jcc/jecxz/jrcxz
            if (mn == "loop" || mn == "loope" || mn == "loopne") return InstructionFlow.ConditionalJump;
            if (mn == "ret" || mn == "retf" || mn == "iret" || mn == "iretd" || mn == "iretq" || mn == "sysret") return InstructionFlow.Return;
            if (mn == "hlt" || mn == "ud2" || mn == "ud0" || mn == "ud1" || mn == "int3") return InstructionFlow.Stop;
            return InstructionFlow.Sequential;
        }

        // ---------------- 字串指令 ----------------
        bool IsStringOp(int op, int map)
        {
            if (map != 0) return false;
            switch (op)
            {
                case 0x6C: case 0x6D: case 0x6E: case 0x6F:
                case 0xA4: case 0xA5: case 0xA6: case 0xA7:
                case 0xAA: case 0xAB: case 0xAC: case 0xAD: case 0xAE: case 0xAF:
                    return true;
            }
            return false;
        }

        void EmitString(DecodedInstruction ins, int op, int opsize)
        {
            string baseName;
            char suffix = opsize == 16 ? 'w' : opsize == 32 ? 'd' : 'q';
            bool isByte = false;
            switch (op)
            {
                case 0x6C: baseName = "ins"; isByte = true; break;
                case 0x6D: baseName = "ins"; break;
                case 0x6E: baseName = "outs"; isByte = true; break;
                case 0x6F: baseName = "outs"; break;
                case 0xA4: baseName = "movs"; isByte = true; break;
                case 0xA5: baseName = "movs"; break;
                case 0xA6: baseName = "cmps"; isByte = true; break;
                case 0xA7: baseName = "cmps"; break;
                case 0xAA: baseName = "stos"; isByte = true; break;
                case 0xAB: baseName = "stos"; break;
                case 0xAC: baseName = "lods"; isByte = true; break;
                case 0xAD: baseName = "lods"; break;
                case 0xAE: baseName = "scas"; isByte = true; break;
                default: baseName = "scas"; break;
            }
            ins.Mnemonic = baseName + (isByte ? "b" : suffix.ToString());
            FinishLen(ins);
            // rep 前綴
            string pre = null;
            bool cmpScas = (baseName == "cmps" || baseName == "scas");
            if (_rep) pre = cmpScas ? "repe" : "rep";
            else if (_repne) pre = cmpScas ? "repne" : "repne";
            if (_lock) pre = pre == null ? "lock" : "lock " + pre;
            ins.Prefix = pre;
            ins.IsValid = true;
            ins.Flow = InstructionFlow.Sequential;
        }

        // ---------------- xchg / nop ----------------
        void DecodeXchgNop(DecodedInstruction ins, int op)
        {
            int reg = (op & 7) + (RexB ? 8 : 0);
            int opsize = Bits == 64 ? (RexW ? 64 : (_p66 ? 16 : 32)) : (_p66 ? (Bits == 16 ? 32 : 16) : (Bits == 16 ? 16 : 32));
            if (op == 0x90 && reg == 0)
            {
                if (_rep) { ins.Mnemonic = "pause"; FinishLen(ins); ins.Prefix = null; ins.IsValid = true; ins.Flow = InstructionFlow.Sequential; return; }
                if (_p66) { ins.Mnemonic = "xchg"; AddReg(ins, "ax"); AddReg(ins, "ax"); FinishLen(ins); ApplyPrefixes(ins, op, 0, false); ins.IsValid = true; ins.Flow = InstructionFlow.Sequential; return; }
                ins.Mnemonic = "nop"; FinishLen(ins); ApplyPrefixes(ins, op, 0, false); ins.IsValid = true; ins.Flow = InstructionFlow.Sequential; return;
            }
            ins.Mnemonic = "xchg";
            AddReg(ins, GprName(reg, opsize));
            AddReg(ins, GprName(0, opsize));
            FinishLen(ins); ApplyPrefixes(ins, op, 0, false); ins.IsValid = true; ins.Flow = InstructionFlow.Sequential;
        }

        void AddReg(DecodedInstruction ins, string name)
        {
            var d = new DecodedOperand(); SetReg(d, name, 0); ins.Operands.Add(d);
        }

        // ---------------- x87 ----------------
        void DecodeX87(DecodedInstruction ins, int op)
        {
            ReadModRM(AddrBitsFor());
            X87Decode(ins, op);
        }

        // ---------------- 3DNow! ----------------
        void Decode3DNow(DecodedInstruction ins)
        {
            if (!_hasModRM) ReadModRM(AddrBitsFor());
            ReadMemTail(false);
            int imm = U8();
            ins.Mnemonic = ThreeDNowName(imm);
            var ops = new List<DecodedOperand>(2);
            var dreg = new DecodedOperand(); SetReg(dreg, "mm" + (_regField & 7).ToString(CultureInfo.InvariantCulture), 64); ops.Add(dreg);
            var src = new DecodedOperand();
            if (_rmIsMem) BuildMem(src, new Opr(Am.Q, Ty.Q), 32); else SetReg(src, "mm" + (_rmField & 7).ToString(CultureInfo.InvariantCulture), 64);
            ops.Add(src);
            foreach (var o in ops) ins.Operands.Add(o);
            FinishLen(ins);
            ApplyPrefixes(ins, -1, 0, false);
            ins.IsValid = ins.Mnemonic != "(bad)";
            ins.Flow = ins.IsValid ? InstructionFlow.Sequential : InstructionFlow.Invalid;
        }

        string ThreeDNowName(int imm)
        {
            switch (imm)
            {
                case 0x0C: return "pi2fw"; case 0x0D: return "pi2fd";
                case 0x1C: return "pf2iw"; case 0x1D: return "pf2id";
                case 0x8A: return "pfnacc"; case 0x8E: return "pfpnacc";
                case 0x90: return "pfcmpge"; case 0x94: return "pfmin"; case 0x96: return "pfrcp"; case 0x97: return "pfrsqrt";
                case 0x9A: return "pfsub"; case 0x9E: return "pfadd";
                case 0xA0: return "pfcmpgt"; case 0xA4: return "pfmax"; case 0xA6: return "pfrcpit1"; case 0xA7: return "pfrsqit1";
                case 0xAA: return "pfsubr"; case 0xAE: return "pfacc";
                case 0xB0: return "pfcmpeq"; case 0xB4: return "pfmul"; case 0xB6: return "pfrcpit2"; case 0xB7: return "pmulhrw";
                case 0xBB: return "pswapd"; case 0xBF: return "pavgusb";
                default: return "(bad)";
            }
        }

        // ---------------- VEX ----------------
        void DecodeVex(DecodedInstruction ins, int c)
        {
            _vex = true;
            if (c == 0xC5)
            {
                int b1 = U8();
                _vexMap = 1;
                _vvvv = (~(b1 >> 3)) & 0xF;
                _vexL = (b1 >> 2) & 1;
                _vexPP = b1 & 3;
                _rex = ((b1 & 0x80) == 0 ? 4 : 0); // R inverted
                _hasRex = true;
            }
            else
            {
                int b1 = U8(); int b2 = U8();
                _vexMap = b1 & 0x1F;
                _vexW = (b2 & 0x80) != 0;
                _vvvv = (~(b2 >> 3)) & 0xF;
                _vexL = (b2 >> 2) & 1;
                _vexPP = b2 & 3;
                int r = (b1 & 0x80) == 0 ? 4 : 0;
                int x = (b1 & 0x40) == 0 ? 2 : 0;
                int bb = (b1 & 0x20) == 0 ? 1 : 0;
                _rex = r | x | bb | (_vexW ? 8 : 0);
                _hasRex = true;
            }
            if (Bits != 64) { _rex &= ~(2 | 1); } // X/B 在 32 位元無效
            _mandatory = _vexPP == 1 ? 0x66 : _vexPP == 2 ? 0xF3 : _vexPP == 3 ? 0xF2 : 0;
            _p66 = _vexPP == 1; _rep = _vexPP == 2; _repne = _vexPP == 3;

            int op = U8();
            VexEmit(ins, op);
        }

        void DecodeEvex(DecodedInstruction ins)
        {
            int p0 = U8(); int p1 = U8(); int p2 = U8();
            _vex = true;
            _vexMap = p0 & 0x07;
            _vexW = (p1 & 0x80) != 0;
            _vexPP = p1 & 3;
            int ll = (p2 >> 5) & 3;
            _vexL = ll == 0 ? 0 : 1;
            int r = (p0 & 0x80) == 0 ? 4 : 0;
            int x = (p0 & 0x40) == 0 ? 2 : 0;
            int bb = (p0 & 0x20) == 0 ? 1 : 0;
            _rex = r | x | bb | (_vexW ? 8 : 0); _hasRex = true;
            _vvvv = (~(p1 >> 3)) & 0xF;
            _mandatory = _vexPP == 1 ? 0x66 : _vexPP == 2 ? 0xF3 : _vexPP == 3 ? 0xF2 : 0;
            _p66 = _vexPP == 1; _rep = _vexPP == 2; _repne = _vexPP == 3;
            int op = U8();
            VexEmit(ins, op, true);
        }

        void VexEmit(DecodedInstruction ins, int op, bool evex = false)
        {
            OpEntry baseE;
            bool imm8 = false;
            if (_vexMap == 1) { baseE = PickMap(X86OpcodeTables.TwoByte, X86OpcodeTables.TwoByte66, X86OpcodeTables.TwoByteF3, X86OpcodeTables.TwoByteF2, op); imm8 = VexMap1HasImm(op); }
            else if (_vexMap == 2) { baseE = PickMap(X86OpcodeTables.Three38, X86OpcodeTables.Three38_66, null, X86OpcodeTables.Three38_F2, op); imm8 = false; }
            else if (_vexMap == 3) { baseE = PickMap(X86OpcodeTables.Three3A, X86OpcodeTables.Three3A_66, null, null, op); imm8 = true; }
            else baseE = X86OpcodeTables.Bad;

            if (baseE.IsInherit) baseE = X86OpcodeTables.Bad;
            var ex = VexExtra(_vexMap, op);
            if (ex.Mn != null) baseE = ex;
            // 無 ModRM 的 VEX：vzeroupper/vzeroall（0F 77）
            if (_vexMap == 1 && op == 0x77)
            {
                FinishLen(ins);
                if (_over) { ins.IsValid = false; return; }
                ins.Mnemonic = _vexL == 1 ? "vzeroall" : "vzeroupper";
                ins.IsValid = true; ins.Flow = InstructionFlow.Sequential;
                return;
            }
            // 讀 ModRM + 尾段（VEX 一律有 ModRM）
            ReadModRM(AddrBitsFor());
            ReadMemTail(RmForcedReg(baseE));

            string mn = baseE.Mn;
            if (mn == null || mn.StartsWith("*", StringComparison.Ordinal) || mn.StartsWith("(", StringComparison.Ordinal))
                mn = null;

            // 長度：ModRM/SIB/位移已讀，imm8 補讀
            // 建立運算元文字（盡量比對 objdump；未知者仍保證長度）
            List<DecodedOperand> ops = VexOperands(baseE, mn);
            if (imm8 || HasImmOperand(baseE)) { /* imm 已在 VexOperands 讀取或此處 */ }

            // 確保 imm8 被讀取（若運算元未涵蓋）
            if ((imm8) && !_vexImmRead) U8();

            FinishLen(ins);
            if (_over) { ins.IsValid = false; return; }
            if (mn == null)
            {
                // 無法命名的 VEX/EVEX：保留正確長度，名稱以 (bad) 表示（長度比對仍正確）。
                ins.Mnemonic = "(bad)"; ins.Operands.Clear(); ins.IsValid = true; ins.Flow = InstructionFlow.Sequential;
                return;
            }
            string vmn = VexRename("v" + mn, op);
            ins.Mnemonic = vmn;
            foreach (var o in ops) ins.Operands.Add(o);
            ins.IsValid = true;
            ins.Flow = ClassifyFlow(ins.Mnemonic);
        }

        bool _vexImmRead;

        bool HasImmOperand(OpEntry e)
        {
            return e.A.M == Am.I || e.B.M == Am.I || e.C.M == Am.I || e.D.M == Am.I;
        }

        bool VexMap1HasImm(int op)
        {
            switch (op) { case 0x70: case 0x71: case 0x72: case 0x73: case 0xC2: case 0xC4: case 0xC5: case 0xC6: return true; }
            return false;
        }

        string VexRename(string vmn, int op)
        {
            // vmovdqa/vmovdqu 等保持；movd→vmovd。zeroupper/zeroall 特例處理在別處。
            return vmn;
        }

        List<DecodedOperand> VexOperands(OpEntry e, string mn)
        {
            _vexImmRead = false;
            var ops = new List<DecodedOperand>(4);
            if (mn == null)
            {
                // 仍需讀取可能的 imm（由呼叫端處理）
                return ops;
            }
            // 依基底運算元形狀重建（插入 vvvv 當第二運算元，若為三運算元型）
            // 簡化：常見型態 (V, H, W) 或 (V, W) 或 (V, W, Ib)
            bool ok = true;
            // 收集非 imm 運算元
            var outs = new List<DecodedOperand>(4);
            bool threeOp = NeedsVvvv(e, mn);
            // 第一運算元
            if (!e.A.IsNone) { var d0 = new DecodedOperand(); BuildVexOperand(d0, e.A, ref ok); outs.Add(d0); }
            if (threeOp) { var dh = new DecodedOperand(); SetXmm(dh, _vvvv, _vexL == 1 ? Ty.Qq : Ty.Dq); outs.Add(dh); }
            if (!e.B.IsNone) { var d1 = new DecodedOperand(); BuildVexOperand(d1, e.B, ref ok); outs.Add(d1); }
            if (!e.C.IsNone) { var d2 = new DecodedOperand(); BuildVexOperand(d2, e.C, ref ok); outs.Add(d2); }
            if (!ok) return ops;
            return outs;
        }

        OpEntry VexExtra(int map, int op)
        {
            if (map == 2 && _p66)
            {
                switch (op)
                {
                    case 0x18: return MkLocal("broadcastss", new Opr(Am.V, Ty.Dq), new Opr(Am.W, Ty.D));
                    case 0x19: return MkLocal("broadcastsd", new Opr(Am.V, Ty.Dq), new Opr(Am.W, Ty.Q));
                    case 0x58: return MkLocal("pbroadcastd", new Opr(Am.V, Ty.Dq), new Opr(Am.W, Ty.D));
                    case 0x59: return MkLocal("pbroadcastq", new Opr(Am.V, Ty.Dq), new Opr(Am.W, Ty.Q));
                    case 0x78: return MkLocal("pbroadcastb", new Opr(Am.V, Ty.Dq), new Opr(Am.W, Ty.B));
                    case 0x79: return MkLocal("pbroadcastw", new Opr(Am.V, Ty.Dq), new Opr(Am.W, Ty.W));
                }
            }
            return X86OpcodeTables.Bad;
        }

        bool NeedsVvvv(OpEntry e, string mn)
        {
            // 單運算元移動類（vmovups/vmovdqa/vmovaps load/store, vmovd/vmovq, vzeroupper, cvt*）不使用 vvvv
            if (mn.StartsWith("mov", StringComparison.Ordinal)) return false;
            if (mn.StartsWith("broadcast", StringComparison.Ordinal) || mn.StartsWith("pbroadcast", StringComparison.Ordinal)) return false;
            if (mn.StartsWith("cvt", StringComparison.Ordinal)) return false;
            if (mn.StartsWith("ucomi", StringComparison.Ordinal) || mn.StartsWith("comi", StringComparison.Ordinal)) return false;
            if (mn.StartsWith("ptest", StringComparison.Ordinal)) return false;
            if (mn.StartsWith("pmov", StringComparison.Ordinal)) return false;
            if (mn == "lddqu" || mn.StartsWith("sqrt", StringComparison.Ordinal) || mn.StartsWith("rcp", StringComparison.Ordinal) || mn.StartsWith("rsqrt", StringComparison.Ordinal)) return false;
            if (mn.StartsWith("pshuf", StringComparison.Ordinal) && !mn.Contains("b")) return false;
            // 其餘的算術/邏輯/比較多為三運算元
            if (e.B.M == Am.W || e.B.M == Am.E || e.B.M == Am.Q) return true;
            return false;
        }

        void BuildVexOperand(DecodedOperand d, Opr o, ref bool ok)
        {
            switch (o.M)
            {
                case Am.V: SetXmm(d, _regField + (RexR ? 8 : 0), _vexL == 1 && o.T == Ty.Dq ? Ty.Qq : o.T); break;
                case Am.W:
                case Am.U:
                    if (_rmIsMem && o.M == Am.W) BuildMem(d, new Opr(o.M, _vexL == 1 && o.T == Ty.Dq ? Ty.Qq : o.T), 128);
                    else SetXmm(d, _rmField + (RexB ? 8 : 0), _vexL == 1 && o.T == Ty.Dq ? Ty.Qq : o.T);
                    break;
                case Am.G: SetReg(d, GprName(_regField + (RexR ? 8 : 0), TySize(o.T, 32)), TySize(o.T, 32)); break;
                case Am.E:
                    if (_rmIsMem) BuildMem(d, o, 32); else SetReg(d, GprName(_rmField + (RexB ? 8 : 0), TySize(o.T, 32)), TySize(o.T, 32));
                    break;
                case Am.I: BuildImm(d, o, 32); _vexImmRead = true; break;
                case Am.M: BuildMem(d, o, 128); break;
                default: ok = false; break;
            }
        }

        void EmitOrphanRex(DecodedInstruction ins)
        {
            // objdump 把後面接著另一個前綴的 REX，單獨當成 1 位元組的 "rex.XX"
            int r = _orphanRex;
            var sb = new StringBuilder("rex");
            string suf = ((r & 8) != 0 ? "w" : "") + ((r & 4) != 0 ? "r" : "") + ((r & 2) != 0 ? "x" : "") + ((r & 1) != 0 ? "b" : "");
            if (suf.Length > 0) sb.Append('.').Append(suf);
            ins.Mnemonic = sb.ToString();
            FinishLen(ins);
            ins.Prefix = null;
            ins.IsValid = true;
            ins.Flow = InstructionFlow.Sequential;
        }

        void EmitXlat(DecodedInstruction ins)
        {
            int ab = AddrBitsFor();
            string b = ab == 64 ? "rbx" : ab == 32 ? "ebx" : "bx";
            string seg = _seg >= 0 ? SegNames[_seg] : "ds";
            ins.Mnemonic = "xlat";
            var d = new DecodedOperand();
            d.Kind = OperandKind.Memory; d.SizeBits = 8; d.BaseRegister = b; d.Segment = seg; d.Scale = 1;
            d.Text = "byte ptr " + seg + ":[" + b + "]";
            ins.Operands.Add(d);
            FinishLen(ins);
            ins.Prefix = _lock ? "lock" : null;
            ins.IsValid = true;
            ins.Flow = InstructionFlow.Sequential;
        }

        // ---------------- x87 ----------------
        bool _fwaitMerge;

        bool IsFwaitMerge()
        {
            // _pos 指向 9B 之後的位元組
            if (_pos >= _limit) return false;
            int nb = _code[_pos];
            int modrm = (_pos + 1 < _limit) ? _code[_pos + 1] : -1;
            if (modrm < 0) return false;
            int mod = modrm >> 6; int reg = (modrm >> 3) & 7;
            if (nb == 0xDB) return modrm == 0xE2 || modrm == 0xE3;
            if (nb == 0xDF) return modrm == 0xE0;
            if (nb == 0xD9) return mod != 3 && (reg == 6 || reg == 7);
            if (nb == 0xDD) return mod != 3 && (reg == 6 || reg == 7);
            return false;
        }

        void AddStr(DecodedInstruction ins, string text, int bits, bool isReg)
        {
            var d = new DecodedOperand();
            if (isReg) { d.Kind = OperandKind.Register; d.Register = text; }
            d.Text = text; d.SizeBits = bits;
            ins.Operands.Add(d);
        }

        void X87Decode(DecodedInstruction ins, int op)
        {
            int set = op - 0xD8;
            if (_mod != 3)
            {
                ReadMemTail(false);
                X87Mem(ins, op);
                if (_fwaitMerge && ins.Mnemonic != null && ins.Mnemonic.StartsWith("fn", StringComparison.Ordinal)) ins.Mnemonic = "f" + ins.Mnemonic.Substring(2);
                FinishLen(ins);
                ins.Prefix = _lock ? "lock" : null;
                ins.IsValid = ins.Mnemonic != null && ins.Mnemonic != "(bad)";
                ins.Flow = ins.IsValid ? InstructionFlow.Sequential : InstructionFlow.Invalid;
                return;
            }
            X87Reg(ins, op, _regField, _rmField);
            if (_fwaitMerge && ins.Mnemonic != null && ins.Mnemonic.StartsWith("fn", StringComparison.Ordinal)) ins.Mnemonic = "f" + ins.Mnemonic.Substring(2);
            FinishLen(ins);
            ins.Prefix = null;
            ins.IsValid = ins.Mnemonic != null && ins.Mnemonic != "(bad)";
            ins.Flow = ins.IsValid ? InstructionFlow.Sequential : InstructionFlow.Invalid;
        }

        void X87Mem(DecodedInstruction ins, int op)
        {
            int reg = _regField;
            string mn = null; Ty t = Ty.D;
            switch (op)
            {
                case 0xD8:
                    { string[] m = { "fadd", "fmul", "fcom", "fcomp", "fsub", "fsubr", "fdiv", "fdivr" }; mn = m[reg]; t = Ty.D; }
                    break;
                case 0xD9:
                    switch (reg) { case 0: mn = "fld"; t = Ty.D; break; case 2: mn = "fst"; t = Ty.D; break; case 3: mn = "fstp"; t = Ty.D; break; case 4: mn = "fldenv"; t = Ty.None; break; case 5: mn = "fldcw"; t = Ty.W; break; case 6: mn = "fnstenv"; t = Ty.None; break; case 7: mn = "fnstcw"; t = Ty.W; break; default: mn = null; break; }
                    break;
                case 0xDA:
                    { string[] m = { "fiadd", "fimul", "ficom", "ficomp", "fisub", "fisubr", "fidiv", "fidivr" }; mn = m[reg]; t = Ty.D; }
                    break;
                case 0xDB:
                    switch (reg) { case 0: mn = "fild"; t = Ty.D; break; case 1: mn = "fisttp"; t = Ty.D; break; case 2: mn = "fist"; t = Ty.D; break; case 3: mn = "fistp"; t = Ty.D; break; case 5: mn = "fld"; t = Ty.T80; break; case 7: mn = "fstp"; t = Ty.T80; break; default: mn = null; break; }
                    break;
                case 0xDC:
                    { string[] m = { "fadd", "fmul", "fcom", "fcomp", "fsub", "fsubr", "fdiv", "fdivr" }; mn = m[reg]; t = Ty.Q; }
                    break;
                case 0xDD:
                    switch (reg) { case 0: mn = "fld"; t = Ty.Q; break; case 1: mn = "fisttp"; t = Ty.Q; break; case 2: mn = "fst"; t = Ty.Q; break; case 3: mn = "fstp"; t = Ty.Q; break; case 4: mn = "frstor"; t = Ty.None; break; case 6: mn = "fnsave"; t = Ty.None; break; case 7: mn = "fnstsw"; t = Ty.W; break; default: mn = null; break; }
                    break;
                case 0xDE:
                    { string[] m = { "fiadd", "fimul", "ficom", "ficomp", "fisub", "fisubr", "fidiv", "fidivr" }; mn = m[reg]; t = Ty.W; }
                    break;
                default: // 0xDF
                    switch (reg) { case 0: mn = "fild"; t = Ty.W; break; case 1: mn = "fisttp"; t = Ty.W; break; case 2: mn = "fist"; t = Ty.W; break; case 3: mn = "fistp"; t = Ty.W; break; case 4: mn = "fbld"; t = Ty.T80; break; case 5: mn = "fild"; t = Ty.Q; break; case 6: mn = "fbstp"; t = Ty.T80; break; case 7: mn = "fistp"; t = Ty.Q; break; default: mn = null; break; }
                    break;
            }
            if (mn == null) { ins.Mnemonic = "(bad)"; return; }
            ins.Mnemonic = mn;
            var d = new DecodedOperand();
            BuildMem(d, new Opr(Am.M, t), 32);
            ins.Operands.Add(d);
        }

        void X87Reg(DecodedInstruction ins, int op, int reg, int rm)
        {
            string sti = "st(" + rm.ToString(CultureInfo.InvariantCulture) + ")";
            switch (op)
            {
                case 0xD8:
                    { string[] m = { "fadd", "fmul", "fcom", "fcomp", "fsub", "fsubr", "fdiv", "fdivr" }; ins.Mnemonic = m[reg];
                      if (reg == 2 || reg == 3) { AddStr(ins, sti, 80, true); } else { AddStr(ins, "st", 80, true); AddStr(ins, sti, 80, true); } }
                    return;
                case 0xD9:
                    X87D9Reg(ins, reg, rm, sti); return;
                case 0xDA:
                    X87DAReg(ins, reg, rm, sti); return;
                case 0xDB:
                    X87DBReg(ins, reg, rm, sti); return;
                case 0xDC:
                    { string[] m = { "fadd", "fmul", null, null, "fsub", "fsubr", "fdiv", "fdivr" };
                      // 注意 DC 的 sub/subr、div/divr 相對 D8 互換
                      string[] mm = { "fadd", "fmul", "fcom", "fcomp", "fsubr", "fsub", "fdivr", "fdiv" };
                      ins.Mnemonic = mm[reg];
                      if (reg == 2 || reg == 3) { AddStr(ins, sti, 80, true); } else { AddStr(ins, sti, 80, true); AddStr(ins, "st", 80, true); } }
                    return;
                case 0xDD:
                    { string[] m = { "ffree", null, "fst", "fstp", "fucom", "fucomp", null, null }; string mn = m[reg];
                      if (mn == null) { ins.Mnemonic = "(bad)"; return; } ins.Mnemonic = mn;
                      if (reg == 4) { AddStr(ins, sti, 80, true); } // fucom st(i)
                      else AddStr(ins, sti, 80, true); }
                    return;
                case 0xDE:
                    X87DEReg(ins, reg, rm, sti); return;
                default: // 0xDF
                    X87DFReg(ins, reg, rm, sti); return;
            }
        }

        void X87D9Reg(DecodedInstruction ins, int reg, int rm, string sti)
        {
            if (reg == 0) { ins.Mnemonic = "fld"; AddStr(ins, sti, 80, true); return; }
            if (reg == 1) { ins.Mnemonic = "fxch"; AddStr(ins, sti, 80, true); return; }
            int b = (reg << 3) | rm;
            string mn;
            switch (b)
            {
                case 0x10: mn = "fnop"; break;
                case 0x20: mn = "fchs"; break; case 0x21: mn = "fabs"; break; case 0x24: mn = "ftst"; break; case 0x25: mn = "fxam"; break;
                case 0x28: mn = "fld1"; break; case 0x29: mn = "fldl2t"; break; case 0x2A: mn = "fldl2e"; break; case 0x2B: mn = "fldpi"; break;
                case 0x2C: mn = "fldlg2"; break; case 0x2D: mn = "fldln2"; break; case 0x2E: mn = "fldz"; break;
                case 0x30: mn = "f2xm1"; break; case 0x31: mn = "fyl2x"; break; case 0x32: mn = "fptan"; break; case 0x33: mn = "fpatan"; break;
                case 0x34: mn = "fxtract"; break; case 0x35: mn = "fprem1"; break; case 0x36: mn = "fdecstp"; break; case 0x37: mn = "fincstp"; break;
                case 0x38: mn = "fprem"; break; case 0x39: mn = "fyl2xp1"; break; case 0x3A: mn = "fsqrt"; break; case 0x3B: mn = "fsincos"; break;
                case 0x3C: mn = "frndint"; break; case 0x3D: mn = "fscale"; break; case 0x3E: mn = "fsin"; break; case 0x3F: mn = "fcos"; break;
                default: mn = null; break;
            }
            ins.Mnemonic = mn ?? "(bad)";
        }

        void X87DAReg(DecodedInstruction ins, int reg, int rm, string sti)
        {
            int b = (reg << 3) | rm;
            if (b == 0x29) { ins.Mnemonic = "fucompp"; return; }
            string[] m = { "fcmovb", "fcmove", "fcmovbe", "fcmovu", null, null, null, null };
            if (reg < 4 && m[reg] != null) { ins.Mnemonic = m[reg]; AddStr(ins, "st", 80, true); AddStr(ins, sti, 80, true); return; }
            ins.Mnemonic = "(bad)";
        }

        void X87DBReg(DecodedInstruction ins, int reg, int rm, string sti)
        {
            int b = (reg << 3) | rm;
            if (b == 0x22) { ins.Mnemonic = "fnclex"; return; }
            if (b == 0x23) { ins.Mnemonic = "fninit"; return; }
            string[] m = { "fcmovnb", "fcmovne", "fcmovnbe", "fcmovnu", null, "fucomi", "fcomi", null };
            if (m[reg] != null)
            {
                ins.Mnemonic = m[reg];
                AddStr(ins, "st", 80, true); AddStr(ins, sti, 80, true); return;
            }
            ins.Mnemonic = "(bad)";
        }

        void X87DEReg(DecodedInstruction ins, int reg, int rm, string sti)
        {
            int b = (reg << 3) | rm;
            if (b == 0x19) { ins.Mnemonic = "fcompp"; return; }
            string[] mm = { "faddp", "fmulp", null, null, "fsubrp", "fsubp", "fdivrp", "fdivp" };
            string mn = mm[reg];
            if (mn == null) { ins.Mnemonic = "(bad)"; return; }
            ins.Mnemonic = mn;
            AddStr(ins, sti, 80, true); AddStr(ins, "st", 80, true);
        }

        void X87DFReg(DecodedInstruction ins, int reg, int rm, string sti)
        {
            int b = (reg << 3) | rm;
            if (b == 0x20) { ins.Mnemonic = "fnstsw"; AddStr(ins, "ax", 16, true); return; }
            if (reg == 5) { ins.Mnemonic = "fucomip"; AddStr(ins, "st", 80, true); AddStr(ins, sti, 80, true); return; }
            if (reg == 6) { ins.Mnemonic = "fcomip"; AddStr(ins, "st", 80, true); AddStr(ins, sti, 80, true); return; }
            if (reg == 0) { ins.Mnemonic = "ffreep"; AddStr(ins, sti, 80, true); return; }
            ins.Mnemonic = "(bad)";
        }
    }
}
