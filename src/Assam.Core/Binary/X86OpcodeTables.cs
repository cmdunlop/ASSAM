using System;

namespace Assam.Core.Binary
{
    /// <summary>運算元的定址方式（Intel 附錄 A 的字母編碼）。</summary>
    internal enum Am : byte
    {
        None = 0,
        E,      // ModRM r/m：一般暫存器或記憶體
        G,      // ModRM reg：一般暫存器
        M,      // ModRM r/m：只能是記憶體
        I,      // 立即值
        J,      // 相對位移（跳躍目標）
        O,      // moffs：直接位址（無 ModRM）
        R,      // ModRM r/m：只能是暫存器（mod==3）
        Seg,    // ModRM reg：區段暫存器
        Cr,     // 控制暫存器
        Dr,     // 除錯暫存器
        V,      // ModRM reg：XMM/YMM
        W,      // ModRM r/m：XMM/YMM 或記憶體
        U,      // ModRM r/m：XMM/YMM（mod==3）
        P,      // ModRM reg：MMX
        N,      // ModRM r/m：MMX（mod==3）
        Q,      // ModRM r/m：MMX 或記憶體
        StrX,   // ds:[rSI]（字串來源）
        StrY,   // es:[rDI]（字串目的）
        Ap,     // 遠程直接指標 ptr16:16/16:32
        OpReg,  // 暫存器編碼在 opcode 低 3 位元（+REX.B）
        Acc,    // 累加器 al/ax/eax/rax（依運算元大小）
        One,    // 常數 1（移位）
        RCL,    // 固定 cl
        RDX,    // 固定 dx（in/out）
        RES, RCS, RSS, RDS, RFS, RGS, // 固定區段暫存器（push/pop）
        St,     // x87 ST(i)（ModRM r/m）
        St0,    // x87 ST(0)
        H,      // VEX.vvvv 指定的 XMM/YMM
        VexL    // VEX imm8[7:4] 指定的 XMM/YMM
    }

    /// <summary>運算元大小／型別。</summary>
    internal enum Ty : byte
    {
        None = 0,
        B,      // 8 位元
        W,      // 16 位元
        D,      // 32 位元
        Q,      // 64 位元
        V,      // 運算元大小（16/32/64）
        Z,      // 16 或 32（不會是 64）
        P,      // 遠程指標（運算元大小 + 16）
        Dq,     // 128 位元（xmmword）
        Qq,     // 256 位元（ymmword）
        T80,    // 80 位元（tbyte）
        S6,     // 6 位元組虛擬描述符（lgdt…）
        A,      // bound（2×運算元大小）
        Y,      // dword，REX.W 時為 qword（Intel 的 'y'）
        Bs      // 立即值 imm8，符號延伸到運算元大小（顯示用運算元大小）
    }

    /// <summary>一個運算元規格。</summary>
    internal struct Opr
    {
        public Am M;
        public Ty T;
        public Opr(Am m, Ty t) { M = m; T = t; }
        public bool IsNone { get { return M == Am.None; } }
    }

    /// <summary>一條 opcode 的解碼規格。</summary>
    internal struct OpEntry
    {
        public string Mn;       // 助憶碼；null 且 Grp&lt;0 表示 (bad)
        public Opr A, B, C, D;  // 最多四個運算元
        public byte Flags;      // 旗標位元
        public sbyte Grp;       // &gt;=0：群組；-1：一般；-2：前綴變體「未覆寫（繼承）」

        public bool IsInherit { get { return Grp == -2; } }
    }

    /// <summary>x86 指令解碼表（由 X86Decoder 使用），在靜態建構時一次建好。</summary>
    internal static class X86OpcodeTables
    {
        public const byte F_D64 = 0x01;     // 64 位元模式下預設運算元大小 64
        public const byte F_I64 = 0x02;     // 64 位元模式下無效
        public const byte F_O64 = 0x04;     // 強制 64 位元運算元
        public const byte F_IMM8 = 0x08;    // 額外帶一個 imm8（長度用）
        public const byte F_DQNAME = 0x10;  // REX.W 時助憶碼尾端 d → q

        public static readonly OpEntry Bad = Mk(null);
        public static readonly OpEntry Inh = MkInh();

        public static readonly OpEntry[] OneByte = new OpEntry[256];
        public static readonly OpEntry[] TwoByte = new OpEntry[256];
        public static readonly OpEntry[] TwoByte66 = new OpEntry[256];
        public static readonly OpEntry[] TwoByteF3 = new OpEntry[256];
        public static readonly OpEntry[] TwoByteF2 = new OpEntry[256];
        public static readonly OpEntry[] Three38 = new OpEntry[256];
        public static readonly OpEntry[] Three38_66 = new OpEntry[256];
        public static readonly OpEntry[] Three38_F2 = new OpEntry[256];
        public static readonly OpEntry[] Three3A = new OpEntry[256];
        public static readonly OpEntry[] Three3A_66 = new OpEntry[256];

        public static readonly OpEntry[][] Groups = new OpEntry[40][];

        public const int G1 = 0, G1A = 1, G2 = 2, G3b = 3, G3v = 4, G4 = 5, G5 = 6,
            G6 = 7, G7 = 8, G8 = 9, G9 = 10, G11b = 11, G11v = 12, G12 = 13, G13 = 14,
            G14 = 15, G15 = 16, G16 = 17, GP = 18;

        static Opr E(Ty t) { return new Opr(Am.E, t); }
        static Opr G(Ty t) { return new Opr(Am.G, t); }
        static Opr M(Ty t) { return new Opr(Am.M, t); }
        static Opr I(Ty t) { return new Opr(Am.I, t); }
        static Opr J(Ty t) { return new Opr(Am.J, t); }
        static Opr O(Ty t) { return new Opr(Am.O, t); }
        static Opr R(Ty t) { return new Opr(Am.R, t); }
        static Opr V(Ty t) { return new Opr(Am.V, t); }
        static Opr W(Ty t) { return new Opr(Am.W, t); }
        static Opr U(Ty t) { return new Opr(Am.U, t); }
        static Opr P(Ty t) { return new Opr(Am.P, t); }
        static Opr N(Ty t) { return new Opr(Am.N, t); }
        static Opr Q(Ty t) { return new Opr(Am.Q, t); }
        static Opr Reg(Ty t) { return new Opr(Am.OpReg, t); }
        static readonly Opr Seg = new Opr(Am.Seg, Ty.W);
        static readonly Opr Cr = new Opr(Am.Cr, Ty.D);
        static readonly Opr Dr = new Opr(Am.Dr, Ty.D);
        static readonly Opr AL = new Opr(Am.Acc, Ty.B);
        static readonly Opr eAX = new Opr(Am.Acc, Ty.V);
        static readonly Opr eAXz = new Opr(Am.Acc, Ty.Z);
        static readonly Opr CL = new Opr(Am.RCL, Ty.B);
        static readonly Opr DX = new Opr(Am.RDX, Ty.W);
        static readonly Opr One = new Opr(Am.One, Ty.B);
        static readonly Opr Xb = new Opr(Am.StrX, Ty.B);
        static readonly Opr Xv = new Opr(Am.StrX, Ty.V);
        static readonly Opr Yb = new Opr(Am.StrY, Ty.B);
        static readonly Opr Yv = new Opr(Am.StrY, Ty.V);
        static readonly Opr St0 = new Opr(Am.St0, Ty.T80);
        static readonly Opr Sti = new Opr(Am.St, Ty.T80);
        static readonly Opr Ap = new Opr(Am.Ap, Ty.P);
        static readonly Opr None = new Opr(Am.None, Ty.None);
        // SSE 常用運算元
        static readonly Opr Vx = new Opr(Am.V, Ty.Dq);
        static readonly Opr Wx = new Opr(Am.W, Ty.Dq);
        static readonly Opr Wd = new Opr(Am.W, Ty.D);
        static readonly Opr Wq = new Opr(Am.W, Ty.Q);
        static readonly Opr Ux = new Opr(Am.U, Ty.Dq);
        static readonly Opr Pq = new Opr(Am.P, Ty.Q);
        static readonly Opr Qq = new Opr(Am.Q, Ty.Q);
        static readonly Opr Nq = new Opr(Am.N, Ty.Q);
        static readonly Opr Gd = new Opr(Am.G, Ty.D);
        static readonly Opr Gy = new Opr(Am.G, Ty.Y);
        static readonly Opr Ey = new Opr(Am.E, Ty.Y);

        static OpEntry Mk(string mn) { OpEntry e; e.Mn = mn; e.A = None; e.B = None; e.C = None; e.D = None; e.Flags = 0; e.Grp = -1; return e; }
        static OpEntry Mk(string mn, Opr a) { var e = Mk(mn); e.A = a; return e; }
        static OpEntry Mk(string mn, Opr a, Opr b) { var e = Mk(mn, a); e.B = b; return e; }
        static OpEntry Mk(string mn, Opr a, Opr b, Opr c) { var e = Mk(mn, a, b); e.C = c; return e; }
        static OpEntry Mk(string mn, Opr a, Opr b, Opr c, Opr d) { var e = Mk(mn, a, b, c); e.D = d; return e; }
        static OpEntry MkInh() { var e = Mk(null); e.Grp = -2; return e; }
        static OpEntry Fl(OpEntry e, byte f) { e.Flags |= f; return e; }
        static OpEntry Grp(int g) { var e = Mk(null); e.Grp = (sbyte)g; return e; }

        static X86OpcodeTables()
        {
            for (int i = 0; i < 256; i++)
            {
                OneByte[i] = Bad;
                TwoByte[i] = Bad; TwoByte66[i] = Inh; TwoByteF3[i] = Inh; TwoByteF2[i] = Inh;
                Three38[i] = Mk("(bad)", Vx, Wx); Three38_66[i] = Inh; Three38_F2[i] = Inh;
                Three3A[i] = Fl(Mk("(bad)", Vx, Wx, I(Ty.B)), F_IMM8); Three3A_66[i] = Inh;
            }
            BuildOneByte();
            BuildGroups();
            BuildTwoByte();
            BuildSse();
            BuildThree38();
            BuildThree3A();
        }

        static void BuildOneByte()
        {
            var t = OneByte;
            string[] arith = { "add", "or", "adc", "sbb", "and", "sub", "xor", "cmp" };
            for (int k = 0; k < 8; k++)
            {
                int b = k * 8;
                t[b + 0] = Mk(arith[k], E(Ty.B), G(Ty.B));
                t[b + 1] = Mk(arith[k], E(Ty.V), G(Ty.V));
                t[b + 2] = Mk(arith[k], G(Ty.B), E(Ty.B));
                t[b + 3] = Mk(arith[k], G(Ty.V), E(Ty.V));
                t[b + 4] = Mk(arith[k], AL, I(Ty.B));
                t[b + 5] = Mk(arith[k], eAX, I(Ty.Z));
            }
            t[0x06] = Fl(Mk("push", new Opr(Am.RES, Ty.W)), F_I64 | F_D64);
            t[0x07] = Fl(Mk("pop", new Opr(Am.RES, Ty.W)), F_I64 | F_D64);
            t[0x0E] = Fl(Mk("push", new Opr(Am.RCS, Ty.W)), F_I64 | F_D64);
            t[0x16] = Fl(Mk("push", new Opr(Am.RSS, Ty.W)), F_I64 | F_D64);
            t[0x17] = Fl(Mk("pop", new Opr(Am.RSS, Ty.W)), F_I64 | F_D64);
            t[0x1E] = Fl(Mk("push", new Opr(Am.RDS, Ty.W)), F_I64 | F_D64);
            t[0x1F] = Fl(Mk("pop", new Opr(Am.RDS, Ty.W)), F_I64 | F_D64);
            t[0x27] = Fl(Mk("daa"), F_I64);
            t[0x2F] = Fl(Mk("das"), F_I64);
            t[0x37] = Fl(Mk("aaa"), F_I64);
            t[0x3F] = Fl(Mk("aas"), F_I64);
            for (int r = 0; r < 8; r++) t[0x40 + r] = Fl(Mk("inc", Reg(Ty.V)), F_I64);
            for (int r = 0; r < 8; r++) t[0x48 + r] = Fl(Mk("dec", Reg(Ty.V)), F_I64);
            for (int r = 0; r < 8; r++) t[0x50 + r] = Fl(Mk("push", Reg(Ty.V)), F_D64);
            for (int r = 0; r < 8; r++) t[0x58 + r] = Fl(Mk("pop", Reg(Ty.V)), F_D64);
            t[0x60] = Fl(Mk("pusha"), F_I64 | F_D64);
            t[0x61] = Fl(Mk("popa"), F_I64 | F_D64);
            t[0x62] = Fl(Mk("bound", G(Ty.V), M(Ty.A)), F_I64);
            t[0x63] = Fl(Mk("arpl", E(Ty.W), G(Ty.W)), F_I64);
            t[0x68] = Fl(Mk("push", I(Ty.Z)), F_D64);
            t[0x69] = Mk("imul", G(Ty.V), E(Ty.V), I(Ty.Z));
            t[0x6A] = Fl(Mk("push", I(Ty.Bs)), F_D64);
            t[0x6B] = Mk("imul", G(Ty.V), E(Ty.V), I(Ty.Bs));
            t[0x6C] = Mk("insb", Yb, DX);
            t[0x6D] = Mk("insd", Yv, DX);
            t[0x6E] = Mk("outsb", DX, Xb);
            t[0x6F] = Mk("outsd", DX, Xv);
            string[] cc = { "o", "no", "b", "ae", "e", "ne", "be", "a", "s", "ns", "p", "np", "l", "ge", "le", "g" };
            for (int c = 0; c < 16; c++) t[0x70 + c] = Fl(Mk("j" + cc[c], J(Ty.B)), F_D64);
            t[0x80] = Grp(G1); t[0x81] = Grp(G1); t[0x82] = Fl(Grp(G1), F_I64); t[0x83] = Grp(G1);
            t[0x84] = Mk("test", E(Ty.B), G(Ty.B));
            t[0x85] = Mk("test", E(Ty.V), G(Ty.V));
            t[0x86] = Mk("xchg", E(Ty.B), G(Ty.B));
            t[0x87] = Mk("xchg", E(Ty.V), G(Ty.V));
            t[0x88] = Mk("mov", E(Ty.B), G(Ty.B));
            t[0x89] = Mk("mov", E(Ty.V), G(Ty.V));
            t[0x8A] = Mk("mov", G(Ty.B), E(Ty.B));
            t[0x8B] = Mk("mov", G(Ty.V), E(Ty.V));
            t[0x8C] = Mk("mov", E(Ty.V), Seg);
            t[0x8D] = Mk("lea", G(Ty.V), M(Ty.None));
            t[0x8E] = Mk("mov", Seg, E(Ty.W));
            t[0x8F] = Fl(Grp(G1A), F_D64);
            t[0x90] = Mk("nop");
            for (int r = 1; r < 8; r++) t[0x90 + r] = Mk("xchg", Reg(Ty.V), eAX);
            t[0x98] = Mk("cbw"); t[0x99] = Mk("cwd");
            t[0x9A] = Fl(Mk("call", Ap), F_I64);
            t[0x9B] = Mk("fwait");
            t[0x9C] = Fl(Mk("pushf"), F_D64);
            t[0x9D] = Fl(Mk("popf"), F_D64);
            t[0x9E] = Mk("sahf"); t[0x9F] = Mk("lahf");
            t[0xA0] = Mk("mov", AL, O(Ty.B));
            t[0xA1] = Mk("mov", eAX, O(Ty.V));
            t[0xA2] = Mk("mov", O(Ty.B), AL);
            t[0xA3] = Mk("mov", O(Ty.V), eAX);
            t[0xA4] = Mk("movsb", Yb, Xb);
            t[0xA5] = Mk("movsd", Yv, Xv);
            t[0xA6] = Mk("cmpsb", Xb, Yb);
            t[0xA7] = Mk("cmpsd", Xv, Yv);
            t[0xA8] = Mk("test", AL, I(Ty.B));
            t[0xA9] = Mk("test", eAX, I(Ty.Z));
            t[0xAA] = Mk("stosb", Yb, AL);
            t[0xAB] = Mk("stosd", Yv, eAX);
            t[0xAC] = Mk("lodsb", AL, Xb);
            t[0xAD] = Mk("lodsd", eAX, Xv);
            t[0xAE] = Mk("scasb", AL, Yb);
            t[0xAF] = Mk("scasd", eAX, Yv);
            for (int r = 0; r < 8; r++) t[0xB0 + r] = Mk("mov", Reg(Ty.B), I(Ty.B));
            for (int r = 0; r < 8; r++) t[0xB8 + r] = Mk("mov", Reg(Ty.V), I(Ty.V));
            t[0xC0] = Grp(G2); t[0xC1] = Grp(G2);
            t[0xC2] = Fl(Mk("ret", I(Ty.W)), F_D64);
            t[0xC3] = Fl(Mk("ret"), F_D64);
            t[0xC4] = Fl(Mk("les", G(Ty.V), M(Ty.P)), F_I64);
            t[0xC5] = Fl(Mk("lds", G(Ty.V), M(Ty.P)), F_I64);
            t[0xC6] = Grp(G11b); t[0xC7] = Grp(G11v);
            t[0xC8] = Mk("enter", I(Ty.W), I(Ty.B));
            t[0xC9] = Fl(Mk("leave"), F_D64);
            t[0xCA] = Mk("retf", I(Ty.W)); t[0xCB] = Mk("retf");
            t[0xCC] = Mk("int3"); t[0xCD] = Mk("int", I(Ty.B));
            t[0xCE] = Fl(Mk("into"), F_I64); t[0xCF] = Mk("iret");
            t[0xD0] = Grp(G2); t[0xD1] = Grp(G2); t[0xD2] = Grp(G2); t[0xD3] = Grp(G2);
            t[0xD4] = Fl(Mk("aam", I(Ty.B)), F_I64);
            t[0xD5] = Fl(Mk("aad", I(Ty.B)), F_I64);
            t[0xD6] = Fl(Mk("(bad)"), F_I64);
            t[0xD7] = Mk("xlatb");
            for (int i = 0xD8; i <= 0xDF; i++) t[i] = Mk("*x87*");
            t[0xE0] = Fl(Mk("loopne", J(Ty.B)), F_D64);
            t[0xE1] = Fl(Mk("loope", J(Ty.B)), F_D64);
            t[0xE2] = Fl(Mk("loop", J(Ty.B)), F_D64);
            t[0xE3] = Fl(Mk("jecxz", J(Ty.B)), F_D64);
            t[0xE4] = Mk("in", AL, I(Ty.B));
            t[0xE5] = Mk("in", eAXz, I(Ty.B));
            t[0xE6] = Mk("out", I(Ty.B), AL);
            t[0xE7] = Mk("out", I(Ty.B), eAXz);
            t[0xE8] = Fl(Mk("call", J(Ty.Z)), F_D64);
            t[0xE9] = Fl(Mk("jmp", J(Ty.Z)), F_D64);
            t[0xEA] = Fl(Mk("jmp", Ap), F_I64);
            t[0xEB] = Fl(Mk("jmp", J(Ty.B)), F_D64);
            t[0xEC] = Mk("in", AL, DX);
            t[0xED] = Mk("in", eAXz, DX);
            t[0xEE] = Mk("out", DX, AL);
            t[0xEF] = Mk("out", DX, eAXz);
            t[0xF1] = Mk("int1");
            t[0xF4] = Mk("hlt"); t[0xF5] = Mk("cmc");
            t[0xF6] = Grp(G3b); t[0xF7] = Grp(G3v);
            t[0xF8] = Mk("clc"); t[0xF9] = Mk("stc"); t[0xFA] = Mk("cli"); t[0xFB] = Mk("sti");
            t[0xFC] = Mk("cld"); t[0xFD] = Mk("std");
            t[0xFE] = Grp(G4); t[0xFF] = Grp(G5);
        }

        static void BuildGroups()
        {
            string[] g1 = { "add", "or", "adc", "sbb", "and", "sub", "xor", "cmp" };
            Groups[G1] = new OpEntry[8];
            for (int r = 0; r < 8; r++) Groups[G1][r] = Mk(g1[r]);
            Groups[G1A] = new OpEntry[8];
            Groups[G1A][0] = Fl(Mk("pop", E(Ty.V)), F_D64);
            for (int r = 1; r < 8; r++) Groups[G1A][r] = Bad;
            string[] g2 = { "rol", "ror", "rcl", "rcr", "shl", "shr", "shl", "sar" };
            Groups[G2] = new OpEntry[8];
            for (int r = 0; r < 8; r++) Groups[G2][r] = Mk(g2[r]);
            Groups[G3b] = new OpEntry[8];
            Groups[G3b][0] = Mk("test", E(Ty.B), I(Ty.B));
            Groups[G3b][1] = Mk("test", E(Ty.B), I(Ty.B));
            Groups[G3b][2] = Mk("not", E(Ty.B));
            Groups[G3b][3] = Mk("neg", E(Ty.B));
            Groups[G3b][4] = Mk("mul", E(Ty.B));
            Groups[G3b][5] = Mk("imul", E(Ty.B));
            Groups[G3b][6] = Mk("div", E(Ty.B));
            Groups[G3b][7] = Mk("idiv", E(Ty.B));
            Groups[G3v] = new OpEntry[8];
            Groups[G3v][0] = Mk("test", E(Ty.V), I(Ty.Z));
            Groups[G3v][1] = Mk("test", E(Ty.V), I(Ty.Z));
            Groups[G3v][2] = Mk("not", E(Ty.V));
            Groups[G3v][3] = Mk("neg", E(Ty.V));
            Groups[G3v][4] = Mk("mul", E(Ty.V));
            Groups[G3v][5] = Mk("imul", E(Ty.V));
            Groups[G3v][6] = Mk("div", E(Ty.V));
            Groups[G3v][7] = Mk("idiv", E(Ty.V));
            Groups[G4] = new OpEntry[8];
            Groups[G4][0] = Mk("inc", E(Ty.B));
            Groups[G4][1] = Mk("dec", E(Ty.B));
            for (int r = 2; r < 8; r++) Groups[G4][r] = Bad;
            Groups[G5] = new OpEntry[8];
            Groups[G5][0] = Mk("inc", E(Ty.V));
            Groups[G5][1] = Mk("dec", E(Ty.V));
            Groups[G5][2] = Fl(Mk("call", E(Ty.V)), F_D64 | F_O64);
            Groups[G5][3] = Mk("call", M(Ty.P));
            Groups[G5][4] = Fl(Mk("jmp", E(Ty.V)), F_D64 | F_O64);
            Groups[G5][5] = Mk("jmp", M(Ty.P));
            Groups[G5][6] = Fl(Mk("push", E(Ty.V)), F_D64);
            Groups[G5][7] = Bad;
            Groups[G11b] = new OpEntry[8];
            for (int r = 0; r < 8; r++) Groups[G11b][r] = Bad;
            Groups[G11b][0] = Mk("mov", E(Ty.B), I(Ty.B));
            Groups[G11v] = new OpEntry[8];
            for (int r = 0; r < 8; r++) Groups[G11v][r] = Bad;
            Groups[G11v][0] = Mk("mov", E(Ty.V), I(Ty.Z));
            Groups[G6] = new OpEntry[8];
            Groups[G6][0] = Mk("sldt", E(Ty.V));
            Groups[G6][1] = Mk("str", E(Ty.V));
            Groups[G6][2] = Mk("lldt", E(Ty.W));
            Groups[G6][3] = Mk("ltr", E(Ty.W));
            Groups[G6][4] = Mk("verr", E(Ty.W));
            Groups[G6][5] = Mk("verw", E(Ty.W));
            Groups[G6][6] = Bad; Groups[G6][7] = Bad;
            Groups[G7] = new OpEntry[8];
            Groups[G7][0] = Mk("sgdt", M(Ty.S6));
            Groups[G7][1] = Mk("sidt", M(Ty.S6));
            Groups[G7][2] = Mk("lgdt", M(Ty.S6));
            Groups[G7][3] = Mk("lidt", M(Ty.S6));
            Groups[G7][4] = Mk("smsw", E(Ty.V));
            Groups[G7][5] = Mk("rstorssp", M(Ty.Q));
            Groups[G7][6] = Mk("lmsw", E(Ty.W));
            Groups[G7][7] = Mk("invlpg", M(Ty.None));
            Groups[G8] = new OpEntry[8];
            for (int r = 0; r < 4; r++) Groups[G8][r] = Bad;
            Groups[G8][4] = Mk("bt", E(Ty.V), I(Ty.B));
            Groups[G8][5] = Mk("bts", E(Ty.V), I(Ty.B));
            Groups[G8][6] = Mk("btr", E(Ty.V), I(Ty.B));
            Groups[G8][7] = Mk("btc", E(Ty.V), I(Ty.B));
            Groups[G9] = new OpEntry[8];
            for (int r = 0; r < 8; r++) Groups[G9][r] = Bad;
            Groups[G9][1] = Mk("cmpxchg8b", M(Ty.Q));
            Groups[G9][6] = Mk("*g9_6*");
            Groups[G9][7] = Mk("*g9_7*");
            Groups[G12] = new OpEntry[8];
            Groups[G13] = new OpEntry[8];
            Groups[G14] = new OpEntry[8];
            for (int r = 0; r < 8; r++) { Groups[G12][r] = Bad; Groups[G13][r] = Bad; Groups[G14][r] = Bad; }
            Groups[G12][2] = Mk("psrlw", Nq, I(Ty.B));
            Groups[G12][4] = Mk("psraw", Nq, I(Ty.B));
            Groups[G12][6] = Mk("psllw", Nq, I(Ty.B));
            Groups[G13][2] = Mk("psrld", Nq, I(Ty.B));
            Groups[G13][4] = Mk("psrad", Nq, I(Ty.B));
            Groups[G13][6] = Mk("pslld", Nq, I(Ty.B));
            Groups[G14][2] = Mk("psrlq", Nq, I(Ty.B));
            Groups[G14][6] = Mk("psllq", Nq, I(Ty.B));
            Groups[G15] = new OpEntry[8];
            Groups[G15][0] = Mk("fxsave", M(Ty.None));
            Groups[G15][1] = Mk("fxrstor", M(Ty.None));
            Groups[G15][2] = Mk("ldmxcsr", M(Ty.D));
            Groups[G15][3] = Mk("stmxcsr", M(Ty.D));
            Groups[G15][4] = Mk("xsave", M(Ty.None));
            Groups[G15][5] = Mk("xrstor", M(Ty.None));
            Groups[G15][6] = Mk("xsaveopt", M(Ty.None));
            Groups[G15][7] = Mk("clflush", M(Ty.B));
            Groups[G16] = new OpEntry[8];
            Groups[G16][0] = Mk("prefetchnta", M(Ty.B));
            Groups[G16][1] = Mk("prefetcht0", M(Ty.B));
            Groups[G16][2] = Mk("prefetcht1", M(Ty.B));
            Groups[G16][3] = Mk("prefetcht2", M(Ty.B));
            for (int r = 4; r < 8; r++) Groups[G16][r] = Mk("nop", E(Ty.V));
            Groups[GP] = new OpEntry[8];
            for (int r = 0; r < 8; r++) Groups[GP][r] = Mk("nop", E(Ty.V));
            Groups[GP][1] = Mk("prefetchw", M(Ty.B));
        }

        static void BuildTwoByte()
        {
            var t = TwoByte;
            t[0x00] = Grp(G6);
            t[0x01] = Grp(G7);
            t[0x02] = Mk("lar", G(Ty.V), E(Ty.W));
            t[0x03] = Mk("lsl", G(Ty.V), E(Ty.W));
            t[0x05] = Mk("syscall");
            t[0x06] = Mk("clts");
            t[0x07] = Mk("sysret");
            t[0x08] = Mk("invd");
            t[0x09] = Mk("wbinvd");
            t[0x0B] = Mk("ud2");
            t[0x0D] = Grp(GP);
            t[0x0E] = Mk("femms");
            t[0x0F] = Mk("*3dnow*");
            t[0x18] = Grp(G16);
            for (int i = 0x19; i <= 0x1F; i++) t[i] = Mk("nop", E(Ty.V));
            t[0x20] = Mk("mov", R(Ty.D), Cr);
            t[0x21] = Mk("mov", R(Ty.D), Dr);
            t[0x22] = Mk("mov", Cr, R(Ty.D));
            t[0x23] = Mk("mov", Dr, R(Ty.D));
            t[0x30] = Mk("wrmsr"); t[0x31] = Mk("rdtsc"); t[0x32] = Mk("rdmsr"); t[0x33] = Mk("rdpmc");
            t[0x34] = Mk("sysenter"); t[0x35] = Mk("sysexit");
            t[0x37] = Mk("getsec");
            string[] cc = { "o", "no", "b", "ae", "e", "ne", "be", "a", "s", "ns", "p", "np", "l", "ge", "le", "g" };
            for (int c = 0; c < 16; c++) t[0x40 + c] = Mk("cmov" + cc[c], G(Ty.V), E(Ty.V));
            for (int c = 0; c < 16; c++) t[0x80 + c] = Fl(Mk("j" + cc[c], J(Ty.Z)), F_D64);
            for (int c = 0; c < 16; c++) t[0x90 + c] = Mk("set" + cc[c], E(Ty.B));
            t[0xA0] = Fl(Mk("push", new Opr(Am.RFS, Ty.W)), F_D64);
            t[0xA1] = Fl(Mk("pop", new Opr(Am.RFS, Ty.W)), F_D64);
            t[0xA2] = Mk("cpuid");
            t[0xA3] = Mk("bt", E(Ty.V), G(Ty.V));
            t[0xA4] = Mk("shld", E(Ty.V), G(Ty.V), I(Ty.B));
            t[0xA5] = Mk("shld", E(Ty.V), G(Ty.V), CL);
            t[0xA8] = Fl(Mk("push", new Opr(Am.RGS, Ty.W)), F_D64);
            t[0xA9] = Fl(Mk("pop", new Opr(Am.RGS, Ty.W)), F_D64);
            t[0xAA] = Mk("rsm");
            t[0xAB] = Mk("bts", E(Ty.V), G(Ty.V));
            t[0xAC] = Mk("shrd", E(Ty.V), G(Ty.V), I(Ty.B));
            t[0xAD] = Mk("shrd", E(Ty.V), G(Ty.V), CL);
            t[0xAE] = Grp(G15);
            t[0xAF] = Mk("imul", G(Ty.V), E(Ty.V));
            t[0xB0] = Mk("cmpxchg", E(Ty.B), G(Ty.B));
            t[0xB1] = Mk("cmpxchg", E(Ty.V), G(Ty.V));
            t[0xB2] = Mk("lss", G(Ty.V), M(Ty.P));
            t[0xB3] = Mk("btr", E(Ty.V), G(Ty.V));
            t[0xB4] = Mk("lfs", G(Ty.V), M(Ty.P));
            t[0xB5] = Mk("lgs", G(Ty.V), M(Ty.P));
            t[0xB6] = Mk("movzx", G(Ty.V), E(Ty.B));
            t[0xB7] = Mk("movzx", G(Ty.V), E(Ty.W));
            t[0xB8] = Mk("ud1", G(Ty.V), E(Ty.V));
            t[0xB9] = Mk("ud1", G(Ty.V), E(Ty.V));
            t[0xBA] = Grp(G8);
            t[0xBB] = Mk("btc", E(Ty.V), G(Ty.V));
            t[0xBC] = Mk("bsf", G(Ty.V), E(Ty.V));
            t[0xBD] = Mk("bsr", G(Ty.V), E(Ty.V));
            t[0xBE] = Mk("movsx", G(Ty.V), E(Ty.B));
            t[0xBF] = Mk("movsx", G(Ty.V), E(Ty.W));
            t[0xC0] = Mk("xadd", E(Ty.B), G(Ty.B));
            t[0xC1] = Mk("xadd", E(Ty.V), G(Ty.V));
            t[0xC3] = Mk("movnti", M(Ty.V), G(Ty.V));
            t[0xC7] = Grp(G9);
            for (int r = 0; r < 8; r++) t[0xC8 + r] = Mk("bswap", Reg(Ty.V));
            t[0xFF] = Mk("ud0", G(Ty.V), E(Ty.V));
        }

        static void BuildSse()
        {
            var t = TwoByte; var p6 = TwoByte66; var f3 = TwoByteF3; var f2 = TwoByteF2;
            // 10/11 mov*
            t[0x10] = Mk("movups", Vx, Wx); p6[0x10] = Mk("movupd", Vx, Wx); f3[0x10] = Mk("movss", Vx, Wd); f2[0x10] = Mk("movsd", Vx, Wq);
            t[0x11] = Mk("movups", Wx, Vx); p6[0x11] = Mk("movupd", Wx, Vx); f3[0x11] = Mk("movss", Wd, Vx); f2[0x11] = Mk("movsd", Wq, Vx);
            t[0x12] = Mk("movlps", Vx, Wq); p6[0x12] = Mk("movlpd", Vx, Wq); f3[0x12] = Mk("movsldup", Vx, Wx); f2[0x12] = Mk("movddup", Vx, Wq);
            t[0x13] = Mk("movlps", Wq, Vx); p6[0x13] = Mk("movlpd", Wq, Vx);
            t[0x14] = Mk("unpcklps", Vx, Wx); p6[0x14] = Mk("unpcklpd", Vx, Wx);
            t[0x15] = Mk("unpckhps", Vx, Wx); p6[0x15] = Mk("unpckhpd", Vx, Wx);
            t[0x16] = Mk("movhps", Vx, Wq); p6[0x16] = Mk("movhpd", Vx, Wq); f3[0x16] = Mk("movshdup", Vx, Wx);
            t[0x17] = Mk("movhps", Wq, Vx); p6[0x17] = Mk("movhpd", Wq, Vx);
            // 28/29 movaps
            t[0x28] = Mk("movaps", Vx, Wx); p6[0x28] = Mk("movapd", Vx, Wx);
            t[0x29] = Mk("movaps", Wx, Vx); p6[0x29] = Mk("movapd", Wx, Vx);
            // 2A cvt...2...
            t[0x2A] = Mk("cvtpi2ps", Vx, Qq); p6[0x2A] = Mk("cvtpi2pd", Vx, Qq);
            f3[0x2A] = Mk("cvtsi2ss", Vx, Ey); f2[0x2A] = Mk("cvtsi2sd", Vx, Ey);
            t[0x2B] = Mk("movntps", Wx, Vx); p6[0x2B] = Mk("movntpd", Wx, Vx);
            t[0x2C] = Mk("cvttps2pi", Pq, Wq); p6[0x2C] = Mk("cvttpd2pi", Pq, Wx);
            f3[0x2C] = Mk("cvttss2si", Gy, Wd); f2[0x2C] = Mk("cvttsd2si", Gy, Wq);
            t[0x2D] = Mk("cvtps2pi", Pq, Wq); p6[0x2D] = Mk("cvtpd2pi", Pq, Wx);
            f3[0x2D] = Mk("cvtss2si", Gy, Wd); f2[0x2D] = Mk("cvtsd2si", Gy, Wq);
            t[0x2E] = Mk("ucomiss", Vx, Wd); p6[0x2E] = Mk("ucomisd", Vx, Wq);
            t[0x2F] = Mk("comiss", Vx, Wd); p6[0x2F] = Mk("comisd", Vx, Wq);
            // 50
            t[0x50] = Mk("movmskps", Gd, Ux); p6[0x50] = Mk("movmskpd", Gd, Ux);
            t[0x51] = Mk("sqrtps", Vx, Wx); p6[0x51] = Mk("sqrtpd", Vx, Wx); f3[0x51] = Mk("sqrtss", Vx, Wd); f2[0x51] = Mk("sqrtsd", Vx, Wq);
            t[0x52] = Mk("rsqrtps", Vx, Wx); f3[0x52] = Mk("rsqrtss", Vx, Wd);
            t[0x53] = Mk("rcpps", Vx, Wx); f3[0x53] = Mk("rcpss", Vx, Wd);
            t[0x54] = Mk("andps", Vx, Wx); p6[0x54] = Mk("andpd", Vx, Wx);
            t[0x55] = Mk("andnps", Vx, Wx); p6[0x55] = Mk("andnpd", Vx, Wx);
            t[0x56] = Mk("orps", Vx, Wx); p6[0x56] = Mk("orpd", Vx, Wx);
            t[0x57] = Mk("xorps", Vx, Wx); p6[0x57] = Mk("xorpd", Vx, Wx);
            t[0x58] = Mk("addps", Vx, Wx); p6[0x58] = Mk("addpd", Vx, Wx); f3[0x58] = Mk("addss", Vx, Wd); f2[0x58] = Mk("addsd", Vx, Wq);
            t[0x59] = Mk("mulps", Vx, Wx); p6[0x59] = Mk("mulpd", Vx, Wx); f3[0x59] = Mk("mulss", Vx, Wd); f2[0x59] = Mk("mulsd", Vx, Wq);
            t[0x5A] = Mk("cvtps2pd", Vx, Wq); p6[0x5A] = Mk("cvtpd2ps", Vx, Wx); f3[0x5A] = Mk("cvtss2sd", Vx, Wd); f2[0x5A] = Mk("cvtsd2ss", Vx, Wq);
            t[0x5B] = Mk("cvtdq2ps", Vx, Wx); p6[0x5B] = Mk("cvtps2dq", Vx, Wx); f3[0x5B] = Mk("cvttps2dq", Vx, Wx);
            t[0x5C] = Mk("subps", Vx, Wx); p6[0x5C] = Mk("subpd", Vx, Wx); f3[0x5C] = Mk("subss", Vx, Wd); f2[0x5C] = Mk("subsd", Vx, Wq);
            t[0x5D] = Mk("minps", Vx, Wx); p6[0x5D] = Mk("minpd", Vx, Wx); f3[0x5D] = Mk("minss", Vx, Wd); f2[0x5D] = Mk("minsd", Vx, Wq);
            t[0x5E] = Mk("divps", Vx, Wx); p6[0x5E] = Mk("divpd", Vx, Wx); f3[0x5E] = Mk("divss", Vx, Wd); f2[0x5E] = Mk("divsd", Vx, Wq);
            t[0x5F] = Mk("maxps", Vx, Wx); p6[0x5F] = Mk("maxpd", Vx, Wx); f3[0x5F] = Mk("maxss", Vx, Wd); f2[0x5F] = Mk("maxsd", Vx, Wq);
            // 60-6F：MMX（基底）與 66（XMM）
            string[] p6x = { "punpcklbw", "punpcklwd", "punpckldq", "packsswb", "pcmpgtb", "pcmpgtw", "pcmpgtd", "packuswb",
                             "punpckhbw", "punpckhwd", "punpckhdq", "packssdw", "punpcklqdq", "punpckhqdq", null, null };
            for (int k = 0; k < 12; k++) { t[0x60 + k] = Mk(p6x[k], Pq, Qq); p6[0x60 + k] = Mk(p6x[k], Vx, Wx); }
            p6[0x6C] = Mk("punpcklqdq", Vx, Wx); p6[0x6D] = Mk("punpckhqdq", Vx, Wx);
            t[0x6C] = Bad; t[0x6D] = Bad;
            t[0x6E] = Fl(Mk("movd", Pq, Ey), F_DQNAME); p6[0x6E] = Fl(Mk("movd", Vx, Ey), F_DQNAME);
            t[0x6F] = Mk("movq", Pq, Qq); p6[0x6F] = Mk("movdqa", Vx, Wx); f3[0x6F] = Mk("movdqu", Vx, Wx);
            t[0x70] = Fl(Mk("pshufw", Pq, Qq, I(Ty.B)), F_IMM8); p6[0x70] = Fl(Mk("pshufd", Vx, Wx, I(Ty.B)), F_IMM8);
            f3[0x70] = Fl(Mk("pshufhw", Vx, Wx, I(Ty.B)), F_IMM8); f2[0x70] = Fl(Mk("pshuflw", Vx, Wx, I(Ty.B)), F_IMM8);
            t[0x71] = Fl(Grp(G12), F_IMM8); t[0x72] = Fl(Grp(G13), F_IMM8); t[0x73] = Fl(Grp(G14), F_IMM8);
            // 群組 12/13/14 的 66 形式（XMM）
            p6[0x71] = Fl(Grp(G12), F_IMM8); p6[0x72] = Fl(Grp(G13), F_IMM8); p6[0x73] = Fl(Grp(G14), F_IMM8);
            t[0x74] = Mk("pcmpeqb", Pq, Qq); p6[0x74] = Mk("pcmpeqb", Vx, Wx);
            t[0x75] = Mk("pcmpeqw", Pq, Qq); p6[0x75] = Mk("pcmpeqw", Vx, Wx);
            t[0x76] = Mk("pcmpeqd", Pq, Qq); p6[0x76] = Mk("pcmpeqd", Vx, Wx);
            t[0x77] = Mk("emms");
            p6[0x7C] = Mk("haddpd", Vx, Wx); f2[0x7C] = Mk("haddps", Vx, Wx);
            p6[0x7D] = Mk("hsubpd", Vx, Wx); f2[0x7D] = Mk("hsubps", Vx, Wx);
            t[0x7E] = Fl(Mk("movd", Ey, Pq), F_DQNAME); p6[0x7E] = Fl(Mk("movd", Ey, Vx), F_DQNAME); f3[0x7E] = Mk("movq", Vx, Wq);
            t[0x7F] = Mk("movq", Qq, Pq); p6[0x7F] = Mk("movdqa", Wx, Vx); f3[0x7F] = Mk("movdqu", Wx, Vx);
            t[0xC2] = Fl(Mk("cmpps", Vx, Wx, I(Ty.B)), F_IMM8); p6[0xC2] = Fl(Mk("cmppd", Vx, Wx, I(Ty.B)), F_IMM8);
            f3[0xC2] = Fl(Mk("cmpss", Vx, Wd, I(Ty.B)), F_IMM8); f2[0xC2] = Fl(Mk("cmpsd", Vx, Wq, I(Ty.B)), F_IMM8);
            t[0xC4] = Fl(Mk("pinsrw", Pq, new Opr(Am.E, Ty.W), I(Ty.B)), F_IMM8); p6[0xC4] = Fl(Mk("pinsrw", Vx, new Opr(Am.E, Ty.W), I(Ty.B)), F_IMM8);
            t[0xC5] = Fl(Mk("pextrw", Gd, Nq, I(Ty.B)), F_IMM8); p6[0xC5] = Fl(Mk("pextrw", Gd, Ux, I(Ty.B)), F_IMM8);
            t[0xC6] = Fl(Mk("shufps", Vx, Wx, I(Ty.B)), F_IMM8); p6[0xC6] = Fl(Mk("shufpd", Vx, Wx, I(Ty.B)), F_IMM8);
            p6[0xD0] = Mk("addsubpd", Vx, Wx); f2[0xD0] = Mk("addsubps", Vx, Wx);
            string[] dblk = { "psrlw", "psrld", "psrlq", "paddq", "pmullw", null, null, "psubusb",
                              "psubusw", "pminub", "pand", "paddusb", "paddusw", "pmaxub", "pandn" };
            for (int k = 0; k < 15; k++) { if (dblk[k] != null) { t[0xD1 + k] = Mk(dblk[k], Pq, Qq); p6[0xD1 + k] = Mk(dblk[k], Vx, Wx); } }
            p6[0xD6] = Mk("movq", Wq, Vx); f3[0xD6] = Mk("movq2dq", Vx, Nq); f2[0xD6] = Mk("movdq2q", Pq, Ux);
            t[0xD7] = Mk("pmovmskb", Gd, Nq); p6[0xD7] = Mk("pmovmskb", Gd, Ux);
            string[] eblk = { "pavgb", "psraw", "psrad", "pavgw", "pmulhuw", "pmulhw", null, null,
                              "psubsb", "psubsw", "pminsw", "por", "paddsb", "paddsw", "pmaxsw", "pxor" };
            for (int k = 0; k < 16; k++) { if (eblk[k] != null) { t[0xE0 + k] = Mk(eblk[k], Pq, Qq); p6[0xE0 + k] = Mk(eblk[k], Vx, Wx); } }
            p6[0xE6] = Mk("cvttpd2dq", Vx, Wx); f3[0xE6] = Mk("cvtdq2pd", Vx, Wq); f2[0xE6] = Mk("cvtpd2dq", Vx, Wx);
            t[0xE7] = Mk("movntq", M(Ty.Q), Pq); p6[0xE7] = Mk("movntdq", Mk_mem(), Vx);
            string[] fblk = { "psllw", "pslld", "psllq", "pmuludq", "pmaddwd", "psadbw", null, "psubb",
                              "psubw", "psubd", "psubq", "paddb", "paddw", "paddd" };
            for (int k = 0; k < 14; k++) { if (fblk[k] != null) { t[0xF1 + k] = Mk(fblk[k], Pq, Qq); p6[0xF1 + k] = Mk(fblk[k], Vx, Wx); } }
            t[0xF7] = Mk("maskmovq", Pq, Nq); p6[0xF7] = Mk("maskmovdqu", Vx, Ux);
            // F3 字首：popcnt/tzcnt/lzcnt
            f3[0xB8] = Mk("popcnt", G(Ty.V), E(Ty.V));
            f3[0xBC] = Mk("tzcnt", G(Ty.V), E(Ty.V));
            f3[0xBD] = Mk("lzcnt", G(Ty.V), E(Ty.V));
        }

        static Opr Mk_mem() { return new Opr(Am.M, Ty.Dq); }

        static void BuildThree38()
        {
            var t = Three38; var p6 = Three38_66; var f2 = Three38_F2;
            // SSSE3：MMX（基底）＋ 66（XMM）
            string[] s3 = { "pshufb", "phaddw", "phaddd", "phaddsw", "pmaddubsw", "phsubw", "phsubd", "phsubsw",
                            "psignb", "psignw", "psignd", "pmulhrsw", null, null, null, null };
            for (int k = 0; k < 12; k++) { if (s3[k] != null) { t[k] = Mk(s3[k], Pq, Qq); p6[k] = Mk(s3[k], Vx, Wx); } }
            t[0x1C] = Mk("pabsb", Pq, Qq); p6[0x1C] = Mk("pabsb", Vx, Wx);
            t[0x1D] = Mk("pabsw", Pq, Qq); p6[0x1D] = Mk("pabsw", Vx, Wx);
            t[0x1E] = Mk("pabsd", Pq, Qq); p6[0x1E] = Mk("pabsd", Vx, Wx);
            // SSE4.1（全部 66）
            p6[0x10] = Mk("pblendvb", Vx, Wx);
            p6[0x14] = Mk("blendvps", Vx, Wx);
            p6[0x15] = Mk("blendvpd", Vx, Wx);
            p6[0x17] = Mk("ptest", Vx, Wx);
            p6[0x20] = Mk("pmovsxbw", Vx, Wq); p6[0x21] = Mk("pmovsxbd", Vx, Wd); p6[0x22] = Mk("pmovsxbq", Vx, new Opr(Am.W, Ty.W));
            p6[0x23] = Mk("pmovsxwd", Vx, Wq); p6[0x24] = Mk("pmovsxwq", Vx, Wd); p6[0x25] = Mk("pmovsxdq", Vx, Wq);
            p6[0x28] = Mk("pmuldq", Vx, Wx); p6[0x29] = Mk("pcmpeqq", Vx, Wx); p6[0x2A] = Mk("movntdqa", Vx, Mk_mem()); p6[0x2B] = Mk("packusdw", Vx, Wx);
            p6[0x30] = Mk("pmovzxbw", Vx, Wq); p6[0x31] = Mk("pmovzxbd", Vx, Wd); p6[0x32] = Mk("pmovzxbq", Vx, new Opr(Am.W, Ty.W));
            p6[0x33] = Mk("pmovzxwd", Vx, Wq); p6[0x34] = Mk("pmovzxwq", Vx, Wd); p6[0x35] = Mk("pmovzxdq", Vx, Wq);
            p6[0x37] = Mk("pcmpgtq", Vx, Wx);
            p6[0x38] = Mk("pminsb", Vx, Wx); p6[0x39] = Mk("pminsd", Vx, Wx); p6[0x3A] = Mk("pminuw", Vx, Wx); p6[0x3B] = Mk("pminud", Vx, Wx);
            p6[0x3C] = Mk("pmaxsb", Vx, Wx); p6[0x3D] = Mk("pmaxsd", Vx, Wx); p6[0x3E] = Mk("pmaxuw", Vx, Wx); p6[0x3F] = Mk("pmaxud", Vx, Wx);
            p6[0x40] = Mk("pmulld", Vx, Wx); p6[0x41] = Mk("phminposuw", Vx, Wx);
            p6[0xDB] = Mk("aesimc", Vx, Wx); p6[0xDC] = Mk("aesenc", Vx, Wx); p6[0xDD] = Mk("aesenclast", Vx, Wx);
            p6[0xDE] = Mk("aesdec", Vx, Wx); p6[0xDF] = Mk("aesdeclast", Vx, Wx);
            // movbe / crc32
            t[0xF0] = Mk("movbe", G(Ty.V), M(Ty.V)); f2[0xF0] = Mk("crc32", Gd, E(Ty.B));
            t[0xF1] = Mk("movbe", M(Ty.V), G(Ty.V)); f2[0xF1] = Mk("crc32", Gd, E(Ty.V));
            p6[0xF0] = Mk("movbe", G(Ty.W), M(Ty.W)); p6[0xF1] = Mk("movbe", M(Ty.W), G(Ty.W));
        }

        static void BuildThree3A()
        {
            var t = Three3A; var p6 = Three3A_66;
            p6[0x08] = Fl(Mk("roundps", Vx, Wx, I(Ty.B)), F_IMM8);
            p6[0x09] = Fl(Mk("roundpd", Vx, Wx, I(Ty.B)), F_IMM8);
            p6[0x0A] = Fl(Mk("roundss", Vx, Wd, I(Ty.B)), F_IMM8);
            p6[0x0B] = Fl(Mk("roundsd", Vx, Wq, I(Ty.B)), F_IMM8);
            p6[0x0C] = Fl(Mk("blendps", Vx, Wx, I(Ty.B)), F_IMM8);
            p6[0x0D] = Fl(Mk("blendpd", Vx, Wx, I(Ty.B)), F_IMM8);
            p6[0x0E] = Fl(Mk("pblendw", Vx, Wx, I(Ty.B)), F_IMM8);
            t[0x0F] = Fl(Mk("palignr", Pq, Qq, I(Ty.B)), F_IMM8); p6[0x0F] = Fl(Mk("palignr", Vx, Wx, I(Ty.B)), F_IMM8);
            p6[0x14] = Fl(Mk("pextrb", E(Ty.B), Vx, I(Ty.B)), F_IMM8);
            p6[0x15] = Fl(Mk("pextrw", E(Ty.W), Vx, I(Ty.B)), F_IMM8);
            p6[0x16] = Fl(Mk("pextrd", Ey, Vx, I(Ty.B)), F_IMM8 | F_DQNAME);
            p6[0x17] = Fl(Mk("extractps", Ey, Vx, I(Ty.B)), F_IMM8);
            p6[0x20] = Fl(Mk("pinsrb", Vx, E(Ty.B), I(Ty.B)), F_IMM8);
            p6[0x21] = Fl(Mk("insertps", Vx, Wd, I(Ty.B)), F_IMM8);
            p6[0x22] = Fl(Mk("pinsrd", Vx, Ey, I(Ty.B)), F_IMM8 | F_DQNAME);
            p6[0x40] = Fl(Mk("dpps", Vx, Wx, I(Ty.B)), F_IMM8);
            p6[0x41] = Fl(Mk("dppd", Vx, Wx, I(Ty.B)), F_IMM8);
            p6[0x42] = Fl(Mk("mpsadbw", Vx, Wx, I(Ty.B)), F_IMM8);
            p6[0x44] = Fl(Mk("pclmulqdq", Vx, Wx, I(Ty.B)), F_IMM8);
            p6[0x60] = Fl(Mk("pcmpestrm", Vx, Wx, I(Ty.B)), F_IMM8);
            p6[0x61] = Fl(Mk("pcmpestri", Vx, Wx, I(Ty.B)), F_IMM8);
            p6[0x62] = Fl(Mk("pcmpistrm", Vx, Wx, I(Ty.B)), F_IMM8);
            p6[0x63] = Fl(Mk("pcmpistri", Vx, Wx, I(Ty.B)), F_IMM8);
            p6[0xDF] = Fl(Mk("aeskeygenassist", Vx, Wx, I(Ty.B)), F_IMM8);
        }
    }
}
