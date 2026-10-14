using System;
using System.Collections.Generic;
using System.Globalization;

namespace Assam.Core.Binary
{
    /// <summary>AVX／AVX2／AVX-512（VEX、EVEX）指令表的一筆規格。</summary>
    internal sealed class AvxEnt
    {
        public string N0, N1;       // W0／W1 的助憶碼（N1 為 null 表示相同）
        public string Ops;          // 運算元代碼，空白分隔（例如 "Vv Hv Wv"）
        public string[] Toks;       // Ops 拆開後的代碼
        public bool Vector;         // 含隨 VL 變動的向量運算元（非純純量）
        public int Tuple;           // AvxTup.*
        public int Elem;            // 明確指定的元素位元組數（0 = 依 EVEX.W 決定 4／8）
        public int Flags;           // AvxFl.*
        public int WSel = -1;       // 此規格對應的 EVEX/VEX.W（-1＝兩者皆可，名稱／元素大小依實際 W）
        public int Vsib;            // 0 無；1＝索引向量與 VL 同寬；2＝索引向量為 VL 的一半
        public AvxEnt Next;         // 同一 opcode 的其他變體（依 mod 區分）
    }

    internal static class AvxTup
    {
        public const int None = 0, FV = 1, HV = 2, FVM = 3, HVM = 4, QVM = 5, OVM = 6,
            T1S = 7, T2 = 8, T4 = 9, T8 = 10, M128 = 11, DUP = 12;
    }

    internal static class AvxFl
    {
        public const int ER = 1, SAE = 2, MemOnly = 4, RegOnly = 8, L1 = 16, L2 = 32, L0 = 4096, GprW = 8192,
            Cmp = 64, PCmp = 128, KwVl = 256, Vex = 512, Evex = 1024, NoVvvv = 2048;
    }

    /// <summary>
    /// AVX 系列指令表（EVEX 完整命名；VEX 補足 AVX2／FMA／BMI 等）。以文字規格在靜態建構時一次建好。
    /// 規格格式：「map pp op[/reg] W 名稱[/W1名稱] | 運算元 | 元組與旗標」。
    /// </summary>
    internal static class X86AvxTables
    {
        static readonly Dictionary<int, AvxEnt> EvexT = new Dictionary<int, AvxEnt>();
        static readonly Dictionary<int, AvxEnt> VexT = new Dictionary<int, AvxEnt>();

        static int Key(int reg, int map, int pp, int w, int op)
        {
            return ((((reg + 1) * 8 + map) * 4 + pp) * 2 + w) * 256 + op;
        }

        /// <summary>查表：先找指定 reg 的群組項，再找一般項。</summary>
        public static AvxEnt Find(bool evex, int map, int pp, int w, int op, int reg)
        {
            var t = evex ? EvexT : VexT;
            AvxEnt e;
            if (t.TryGetValue(Key(reg, map, pp, w, op), out e)) return e;
            if (t.TryGetValue(Key(-1, map, pp, w, op), out e)) return e;
            return null;
        }

        /// <summary>找不到時，改用相反的 W（objdump 對 W 不符的 EVEX 仍以同名解碼）。</summary>
        public static AvxEnt FindAnyW(bool evex, int map, int pp, int w, int op, int reg)
        {
            AvxEnt e = Find(evex, map, pp, w, op, reg);
            if (e == null) e = Find(evex, map, pp, 1 - w, op, reg);
            return e;
        }

        static void Add(string line)
        {
            string[] part = line.Split('|');
            string[] h = part[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var e = new AvxEnt();
            int map = int.Parse(h[0], CultureInfo.InvariantCulture);
            int pp = h[1] == "-" ? 0 : h[1] == "66" ? 1 : h[1] == "F3" ? 2 : 3;
            int reg = -1;
            string os = h[2];
            int sl = os.IndexOf('/');
            if (sl >= 0) { reg = int.Parse(os.Substring(sl + 1), CultureInfo.InvariantCulture); os = os.Substring(0, sl); }
            int op = int.Parse(os, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string w = h[3];
            string nm = h[4];
            int ns = nm.IndexOf('/');
            if (ns >= 0) { e.N0 = nm.Substring(0, ns); e.N1 = nm.Substring(ns + 1); } else { e.N0 = nm; e.N1 = null; }
            e.Ops = part[1].Trim();
            string[] f = part.Length > 2 ? part[2].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries) : new string[0];
            int flags = 0;
            foreach (string s in f)
            {
                switch (s)
                {
                    case "FV": e.Tuple = AvxTup.FV; break;
                    case "HV": e.Tuple = AvxTup.HV; break;
                    case "FVM": e.Tuple = AvxTup.FVM; break;
                    case "HVM": e.Tuple = AvxTup.HVM; break;
                    case "QVM": e.Tuple = AvxTup.QVM; break;
                    case "OVM": e.Tuple = AvxTup.OVM; break;
                    case "T1S": e.Tuple = AvxTup.T1S; break;
                    case "T2": e.Tuple = AvxTup.T2; break;
                    case "T4": e.Tuple = AvxTup.T4; break;
                    case "T8": e.Tuple = AvxTup.T8; break;
                    case "M128": e.Tuple = AvxTup.M128; break;
                    case "DUP": e.Tuple = AvxTup.DUP; break;
                    case "e1": e.Elem = 1; break;
                    case "e2": e.Elem = 2; break;
                    case "e4": e.Elem = 4; break;
                    case "e8": e.Elem = 8; break;
                    case "er": flags |= AvxFl.ER; break;
                    case "sae": flags |= AvxFl.SAE; break;
                    case "m": flags |= AvxFl.MemOnly; break;
                    case "r": flags |= AvxFl.RegOnly; break;
                    case "l1": flags |= AvxFl.L1; break;
                    case "l2": flags |= AvxFl.L2; break;
                    case "l0": flags |= AvxFl.L0; break;
                    case "cmp": flags |= AvxFl.Cmp; break;
                    case "pcmp": flags |= AvxFl.PCmp; break;
                    case "kwvl": flags |= AvxFl.KwVl; break;
                    case "vex": flags |= AvxFl.Vex; break;
                    case "both": flags |= AvxFl.Vex | AvxFl.Evex; break;
                    case "nov": flags |= AvxFl.NoVvvv; break;
                    case "vs1": e.Vsib = 1; break;
                    case "vs2": e.Vsib = 2; break;
                    default: throw new InvalidOperationException("AVX 表未知旗標：" + s + " @ " + line);
                }
            }
            if ((flags & (AvxFl.Vex | AvxFl.Evex)) == 0) flags |= AvxFl.Evex;
            // 沒有 vvvv 運算元的規格，解碼時需檢查 vvvv 必為 0
            e.Toks = e.Ops.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool hasV = false, vec = false;
            foreach (string tk in e.Toks)
            {
                if (tk[0] == 'H' || tk[0] == 'B' || tk == "Kh") hasV = true;
                if ("VHWU".IndexOf(tk[0]) >= 0 && tk.Length > 1 && "vhqo".IndexOf(tk[1]) >= 0) vec = true;
            }
            if (!hasV && e.Vsib == 0) flags |= AvxFl.NoVvvv;
            e.Vector = vec;
            if (Array.IndexOf(e.Toks, "Ey") >= 0) flags |= AvxFl.GprW;
            e.Flags = flags;
            for (int ww = 0; ww < 2; ww++)
            {
                if (w != "x" && int.Parse(w, CultureInfo.InvariantCulture) != ww) continue;
                var copy = ww == 0 || w != "x" ? e : Clone(e);
                copy.WSel = w == "x" ? -1 : ww;
                if ((flags & AvxFl.Evex) != 0) Put(EvexT, Key(reg, map, pp, ww, op), copy);
                if ((flags & AvxFl.Vex) != 0) Put(VexT, Key(reg, map, pp, ww, op), (flags & AvxFl.Evex) != 0 ? Clone(copy) : copy);
            }
        }

        static AvxEnt Clone(AvxEnt e)
        {
            return new AvxEnt { N0 = e.N0, N1 = e.N1, Ops = e.Ops, Toks = e.Toks, Vector = e.Vector, Tuple = e.Tuple, Elem = e.Elem, Flags = e.Flags, Vsib = e.Vsib, WSel = e.WSel };
        }

        static void Put(Dictionary<int, AvxEnt> t, int key, AvxEnt e)
        {
            AvxEnt cur;
            if (!t.TryGetValue(key, out cur)) { t[key] = e; return; }
            while (cur.Next != null) cur = cur.Next;
            if (cur == e) return;
            cur.Next = e;
        }

        static X86AvxTables()
        {
            foreach (string l in Lines) Add(l);
            AddFma();
            AddPmov();
            AddCmpXadd();
        }

        static void AddFma()
        {
            string[] sets = { "132", "213", "231" };
            int[] bases = { 0x90, 0xA0, 0xB0 };
            for (int s = 0; s < 3; s++)
            {
                // 偏移 6..F：6 fmaddsub、7 fmsubadd、8 fmadd(p)、9 fmadd(s)、A fmsub(p)、B fmsub(s)、C fnmadd(p)、D fnmadd(s)、E fnmsub(p)、F fnmsub(s)
                string[] nm = { "fmaddsub", "fmsubadd", "fmadd", "fmadd", "fmsub", "fmsub", "fnmadd", "fnmadd", "fnmsub", "fnmsub" };
                for (int k = 0; k < 10; k++)
                {
                    int op = bases[s] + 6 + k;
                    bool scalar = k >= 2 && ((k & 1) == 1);
                    string n = "v" + nm[k] + sets[s];
                    string hx = op.ToString("X2", CultureInfo.InvariantCulture);
                    if (scalar)
                    {
                        Add("2 66 " + hx + " x " + n + "ss/" + n + "sd | Vx Hx Wx | T1S er both");
                    }
                    else
                    {
                        Add("2 66 " + hx + " x " + n + "ps/" + n + "pd | Vv Hv Wv | FV er both");
                    }
                }
            }
        }

        static void AddCmpXadd()
        {
            string[] cc = { "o", "no", "b", "nb", "z", "nz", "be", "nbe", "s", "ns", "p", "np", "l", "nl", "le", "nle" };
            for (int i = 0; i < 16; i++)
                Add("2 66 " + (0xE0 + i).ToString("X2", CultureInfo.InvariantCulture) + " x cmp" + cc[i] + "xadd | M Gy By | T1S vex m l0");
        }

        static void AddPmov()
        {
            // 下轉換：F3 0F38 10..15（us）、20..25（s）、30..35（無飽和）
            string[] sfx = { "wb", "db", "qb", "dw", "qw", "qd" };
            string[] tup = { "HVM", "QVM", "OVM", "HVM", "QVM", "HVM" };
            string[] dty = { "Wh", "Wq", "Wo", "Wh", "Wq", "Wh" };
            string[] pre = { "vpmovus", "vpmovs", "vpmov" };
            int[] bs = { 0x10, 0x20, 0x30 };
            for (int g = 0; g < 3; g++)
                for (int k = 0; k < 6; k++)
                    Add("2 F3 " + (bs[g] + k).ToString("X2", CultureInfo.InvariantCulture) + " 0 " + pre[g] + sfx[k] + " | " + dty[k] + " Vv | " + tup[k]);
            // 擴展：66 0F38 20..25（sx）、30..35（zx）
            string[] ext = { "bw", "bd", "bq", "wd", "wq", "dq" };
            string[] etup = { "HVM", "QVM", "OVM", "HVM", "QVM", "HVM" };
            string[] ety = { "Wh", "Wq", "Wo", "Wh", "Wq", "Wh" };
            for (int k = 0; k < 6; k++)
            {
                string w = k == 5 ? "0" : "x";
                Add("2 66 " + (0x20 + k).ToString("X2", CultureInfo.InvariantCulture) + " " + w + " vpmovsx" + ext[k] + " | Vv " + ety[k].Substring(0, 2) + " | " + etup[k] + " both");
                Add("2 66 " + (0x30 + k).ToString("X2", CultureInfo.InvariantCulture) + " " + w + " vpmovzx" + ext[k] + " | Vv " + ety[k].Substring(0, 2) + " | " + etup[k] + " both");
            }
        }

        // 規格列表。運算元代碼：
        //  V*=reg 向量、H*=vvvv 向量、W*=r/m 向量或記憶體、U*=r/m 向量（僅暫存器）、M=僅記憶體；
        //  向量尺寸字母：v=VL、x=128、y=256、z=512、h=VL/2、q=VL/4、o=VL/8；
        //  Kg=reg 欄遮罩暫存器、Kh=vvvv 遮罩、Kr=r/m 遮罩（僅暫存器）、Km=r/m 遮罩或記憶體；
        //  Gy／Gd=reg 欄通用暫存器（依 W 為 32/64／固定 32）、Ey／Ed／Eb／Ew=r/m 通用暫存器或記憶體、
        //  Ry／Rd=r/m 通用暫存器（僅暫存器）、Ib=imm8、Lv／Lx=imm8[7:4] 暫存器。
        static readonly string[] Lines =
        {
            // ===== map 1（0F）=====
            "1 - 10 0 vmovups | Vv Wv | FVM both",
            "1 66 10 1 vmovupd | Vv Wv | FVM both",
            "1 F3 10 0 vmovss | Vx Hx Ux | r both",
            "1 F3 10 0 vmovss | Vx M | T1S e4 m both",
            "1 F2 10 1 vmovsd | Vx Hx Ux | r both",
            "1 F2 10 1 vmovsd | Vx M | T1S e8 m both",
            "1 - 11 0 vmovups | Wv Vv | FVM both",
            "1 66 11 1 vmovupd | Wv Vv | FVM both",
            "1 F3 11 0 vmovss | Ux Hx Vx | r both",
            "1 F3 11 0 vmovss | M Vx | T1S e4 m both",
            "1 F2 11 1 vmovsd | Ux Hx Vx | r both",
            "1 F2 11 1 vmovsd | M Vx | T1S e8 m both",
            "1 - 12 0 vmovlps | Vx Hx M | T2 e4 m both",
            "1 - 12 0 vmovhlps | Vx Hx Ux | r both",
            "1 66 12 1 vmovlpd | Vx Hx M | T1S e8 m both",
            "1 F3 12 0 vmovsldup | Vv Wv | FVM both",
            "1 F2 12 1 vmovddup | Vv Wv | DUP both",
            "1 - 13 0 vmovlps | M Vx | T2 e4 m both",
            "1 66 13 1 vmovlpd | M Vx | T1S e8 m both",
            "1 - 14 0 vunpcklps | Vv Hv Wv | FV both",
            "1 66 14 1 vunpcklpd | Vv Hv Wv | FV both",
            "1 - 15 0 vunpckhps | Vv Hv Wv | FV both",
            "1 66 15 1 vunpckhpd | Vv Hv Wv | FV both",
            "1 - 16 0 vmovhps | Vx Hx M | T2 e4 m both",
            "1 - 16 0 vmovlhps | Vx Hx Ux | r both",
            "1 66 16 1 vmovhpd | Vx Hx M | T1S e8 m both",
            "1 F3 16 0 vmovshdup | Vv Wv | FVM both",
            "1 - 17 0 vmovhps | M Vx | T2 e4 m both",
            "1 66 17 1 vmovhpd | M Vx | T1S e8 m both",
            "1 - 28 0 vmovaps | Vv Wv | FVM both",
            "1 66 28 1 vmovapd | Vv Wv | FVM both",
            "1 - 29 0 vmovaps | Wv Vv | FVM both",
            "1 66 29 1 vmovapd | Wv Vv | FVM both",
            "1 F3 2A x vcvtsi2ss | Vx Hx Ey | T1S er both",
            "1 F2 2A x vcvtsi2sd | Vx Hx Ey | T1S er both",
            "1 - 2B 0 vmovntps | M Vv | FVM both",
            "1 66 2B 1 vmovntpd | M Vv | FVM both",
            "1 F3 2C x vcvttss2si | Gy Wx | T1S e4 sae both",
            "1 F2 2C x vcvttsd2si | Gy Wx | T1S e8 sae both",
            "1 F3 2D x vcvtss2si | Gy Wx | T1S e4 er both",
            "1 F2 2D x vcvtsd2si | Gy Wx | T1S e8 er both",
            "1 - 2E 0 vucomiss | Vx Wx | T1S e4 sae both",
            "1 66 2E 1 vucomisd | Vx Wx | T1S e8 sae both",
            "1 - 2F 0 vcomiss | Vx Wx | T1S e4 sae both",
            "1 66 2F 1 vcomisd | Vx Wx | T1S e8 sae both",
            "1 - 51 0 vsqrtps | Vv Wv | FV er both",
            "1 66 51 1 vsqrtpd | Vv Wv | FV er both",
            "1 F3 51 0 vsqrtss | Vx Hx Wx | T1S e4 er both",
            "1 F2 51 1 vsqrtsd | Vx Hx Wx | T1S e8 er both",
            "1 - 54 0 vandps | Vv Hv Wv | FV both",
            "1 66 54 1 vandpd | Vv Hv Wv | FV both",
            "1 - 55 0 vandnps | Vv Hv Wv | FV both",
            "1 66 55 1 vandnpd | Vv Hv Wv | FV both",
            "1 - 56 0 vorps | Vv Hv Wv | FV both",
            "1 66 56 1 vorpd | Vv Hv Wv | FV both",
            "1 - 57 0 vxorps | Vv Hv Wv | FV both",
            "1 66 57 1 vxorpd | Vv Hv Wv | FV both",
            "1 - 58 0 vaddps | Vv Hv Wv | FV er both",
            "1 66 58 1 vaddpd | Vv Hv Wv | FV er both",
            "1 F3 58 0 vaddss | Vx Hx Wx | T1S e4 er both",
            "1 F2 58 1 vaddsd | Vx Hx Wx | T1S e8 er both",
            "1 - 59 0 vmulps | Vv Hv Wv | FV er both",
            "1 66 59 1 vmulpd | Vv Hv Wv | FV er both",
            "1 F3 59 0 vmulss | Vx Hx Wx | T1S e4 er both",
            "1 F2 59 1 vmulsd | Vx Hx Wx | T1S e8 er both",
            "1 - 5A 0 vcvtps2pd | Vv Wh | HV sae both",
            "1 66 5A 1 vcvtpd2ps | Vh Wv | FV er",
            "1 F3 5A 0 vcvtss2sd | Vx Hx Wx | T1S e4 sae both",
            "1 F2 5A 1 vcvtsd2ss | Vx Hx Wx | T1S e8 er both",
            "1 - 5B 0 vcvtdq2ps | Vv Wv | FV er",
            "1 - 5B 1 vcvtqq2ps | Vh Wv | FV er",
            "1 66 5B 0 vcvtps2dq | Vv Wv | FV er",
            "1 F3 5B 0 vcvttps2dq | Vv Wv | FV sae",
            "1 - 5C 0 vsubps | Vv Hv Wv | FV er both",
            "1 66 5C 1 vsubpd | Vv Hv Wv | FV er both",
            "1 F3 5C 0 vsubss | Vx Hx Wx | T1S e4 er both",
            "1 F2 5C 1 vsubsd | Vx Hx Wx | T1S e8 er both",
            "1 - 5D 0 vminps | Vv Hv Wv | FV sae both",
            "1 66 5D 1 vminpd | Vv Hv Wv | FV sae both",
            "1 F3 5D 0 vminss | Vx Hx Wx | T1S e4 sae both",
            "1 F2 5D 1 vminsd | Vx Hx Wx | T1S e8 sae both",
            "1 - 5E 0 vdivps | Vv Hv Wv | FV er both",
            "1 66 5E 1 vdivpd | Vv Hv Wv | FV er both",
            "1 F3 5E 0 vdivss | Vx Hx Wx | T1S e4 er both",
            "1 F2 5E 1 vdivsd | Vx Hx Wx | T1S e8 er both",
            "1 - 5F 0 vmaxps | Vv Hv Wv | FV sae both",
            "1 66 5F 1 vmaxpd | Vv Hv Wv | FV sae both",
            "1 F3 5F 0 vmaxss | Vx Hx Wx | T1S e4 sae both",
            "1 F2 5F 1 vmaxsd | Vx Hx Wx | T1S e8 sae both",
            "1 66 60 x vpunpcklbw | Vv Hv Wv | FVM both",
            "1 66 61 x vpunpcklwd | Vv Hv Wv | FVM both",
            "1 66 62 0 vpunpckldq | Vv Hv Wv | FV both",
            "1 66 63 x vpacksswb | Vv Hv Wv | FVM both",
            "1 66 64 x vpcmpgtb | Kg Hv Wv | FVM",
            "1 66 65 x vpcmpgtw | Kg Hv Wv | FVM",
            "1 66 66 0 vpcmpgtd | Kg Hv Wv | FV",
            "1 66 67 x vpackuswb | Vv Hv Wv | FVM both",
            "1 66 68 x vpunpckhbw | Vv Hv Wv | FVM both",
            "1 66 69 x vpunpckhwd | Vv Hv Wv | FVM both",
            "1 66 6A 0 vpunpckhdq | Vv Hv Wv | FV both",
            "1 66 6B 0 vpackssdw | Vv Hv Wv | FV both",
            "1 66 6C 1 vpunpcklqdq | Vv Hv Wv | FV both",
            "1 66 6D 1 vpunpckhqdq | Vv Hv Wv | FV both",
            "1 66 6E x vmovd/vmovq | Vx Ey | T1S both",
            "1 66 6F x vmovdqa32/vmovdqa64 | Vv Wv | FVM",
            "1 F3 6F x vmovdqu32/vmovdqu64 | Vv Wv | FVM",
            "1 F2 6F x vmovdqu8/vmovdqu16 | Vv Wv | FVM",
            "1 66 70 0 vpshufd | Vv Wv Ib | FV both",
            "1 F3 70 x vpshufhw | Vv Wv Ib | FVM both",
            "1 F2 70 x vpshuflw | Vv Wv Ib | FVM both",
            "1 66 71/2 x vpsrlw | Hv Wv Ib | FVM both",
            "1 66 71/4 x vpsraw | Hv Wv Ib | FVM both",
            "1 66 71/6 x vpsllw | Hv Wv Ib | FVM both",
            "1 66 72/0 x vprord/vprorq | Hv Wv Ib | FV",
            "1 66 72/1 x vprold/vprolq | Hv Wv Ib | FV",
            "1 66 72/2 0 vpsrld | Hv Wv Ib | FV both",
            "1 66 72/4 x vpsrad/vpsraq | Hv Wv Ib | FV",
            "1 66 72/6 0 vpslld | Hv Wv Ib | FV both",
            "1 66 73/2 1 vpsrlq | Hv Wv Ib | FV both",
            "1 66 73/3 x vpsrldq | Hv Wv Ib | FVM both",
            "1 66 73/6 1 vpsllq | Hv Wv Ib | FV both",
            "1 66 73/7 x vpslldq | Hv Wv Ib | FVM both",
            "1 66 74 x vpcmpeqb | Kg Hv Wv | FVM",
            "1 66 75 x vpcmpeqw | Kg Hv Wv | FVM",
            "1 66 76 0 vpcmpeqd | Kg Hv Wv | FV",
            "1 - 78 0 vcvttps2udq | Vv Wv | FV sae",
            "1 - 78 1 vcvttpd2udq | Vh Wv | FV sae",
            "1 66 78 0 vcvttps2uqq | Vv Wh | HV sae",
            "1 66 78 1 vcvttpd2uqq | Vv Wv | FV sae",
            "1 F3 78 x vcvttss2usi | Gy Wx | T1S e4 sae",
            "1 F2 78 x vcvttsd2usi | Gy Wx | T1S e8 sae",
            "1 - 79 0 vcvtps2udq | Vv Wv | FV er",
            "1 - 79 1 vcvtpd2udq | Vh Wv | FV er",
            "1 66 79 0 vcvtps2uqq | Vv Wh | HV er",
            "1 66 79 1 vcvtpd2uqq | Vv Wv | FV er",
            "1 F3 79 x vcvtss2usi | Gy Wx | T1S e4 er",
            "1 F2 79 x vcvtsd2usi | Gy Wx | T1S e8 er",
            "1 66 7A 0 vcvttps2qq | Vv Wh | HV sae",
            "1 66 7A 1 vcvttpd2qq | Vv Wv | FV sae",
            "1 F3 7A 0 vcvtudq2pd | Vv Wh | HV",
            "1 F3 7A 1 vcvtuqq2pd | Vv Wv | FV er",
            "1 F2 7A 0 vcvtudq2ps | Vv Wv | FV er",
            "1 F2 7A 1 vcvtuqq2ps | Vh Wv | FV er",
            "1 66 7B 0 vcvtps2qq | Vv Wh | HV er",
            "1 66 7B 1 vcvtpd2qq | Vv Wv | FV er",
            "1 F3 7B x vcvtusi2ss | Vx Hx Ey | T1S er",
            "1 F2 7B x vcvtusi2sd | Vx Hx Ey | T1S er",
            "1 66 7E x vmovd/vmovq | Ey Vx | T1S both",
            "1 F3 7E 1 vmovq | Vx Wx | T1S e8 both",
            "1 66 7F x vmovdqa32/vmovdqa64 | Wv Vv | FVM",
            "1 F3 7F x vmovdqu32/vmovdqu64 | Wv Vv | FVM",
            "1 F2 7F x vmovdqu8/vmovdqu16 | Wv Vv | FVM",
            "1 - C2 0 vcmpps | Kg Hv Wv Ib | FV sae cmp",
            "1 66 C2 1 vcmppd | Kg Hv Wv Ib | FV sae cmp",
            "1 F3 C2 0 vcmpss | Kg Hx Wx Ib | T1S e4 sae cmp",
            "1 F2 C2 1 vcmpsd | Kg Hx Wx Ib | T1S e8 sae cmp",
            "1 66 C4 x vpinsrw | Vx Hx Ew Ib | T1S e2 both",
            "1 66 C5 x vpextrw | Gd Ux Ib | r both",
            "1 - C6 0 vshufps | Vv Hv Wv Ib | FV both",
            "1 66 C6 1 vshufpd | Vv Hv Wv Ib | FV both",
            "1 66 D1 x vpsrlw | Vv Hv Wx | M128 both",
            "1 66 D2 0 vpsrld | Vv Hv Wx | M128 both",
            "1 66 D3 1 vpsrlq | Vv Hv Wx | M128 both",
            "1 66 D4 1 vpaddq | Vv Hv Wv | FV both",
            "1 66 D5 x vpmullw | Vv Hv Wv | FVM both",
            "1 66 D6 1 vmovq | Wx Vx | T1S e8 both",
            "1 66 D8 x vpsubusb | Vv Hv Wv | FVM both",
            "1 66 D9 x vpsubusw | Vv Hv Wv | FVM both",
            "1 66 DA x vpminub | Vv Hv Wv | FVM both",
            "1 66 DB x vpandd/vpandq | Vv Hv Wv | FV",
            "1 66 DC x vpaddusb | Vv Hv Wv | FVM both",
            "1 66 DD x vpaddusw | Vv Hv Wv | FVM both",
            "1 66 DE x vpmaxub | Vv Hv Wv | FVM both",
            "1 66 DF x vpandnd/vpandnq | Vv Hv Wv | FV",
            "1 66 E0 x vpavgb | Vv Hv Wv | FVM both",
            "1 66 E1 x vpsraw | Vv Hv Wx | M128 both",
            "1 66 E2 x vpsrad/vpsraq | Vv Hv Wx | M128",
            "1 66 E3 x vpavgw | Vv Hv Wv | FVM both",
            "1 66 E4 x vpmulhuw | Vv Hv Wv | FVM both",
            "1 66 E5 x vpmulhw | Vv Hv Wv | FVM both",
            "1 66 E6 1 vcvttpd2dq | Vh Wv | FV sae",
            "1 F3 E6 0 vcvtdq2pd | Vv Wh | HV both",
            "1 F3 E6 1 vcvtqq2pd | Vv Wv | FV er",
            "1 F2 E6 1 vcvtpd2dq | Vh Wv | FV er",
            "1 66 E7 0 vmovntdq | M Vv | FVM both",
            "1 66 E8 x vpsubsb | Vv Hv Wv | FVM both",
            "1 66 E9 x vpsubsw | Vv Hv Wv | FVM both",
            "1 66 EA x vpminsw | Vv Hv Wv | FVM both",
            "1 66 EB x vpord/vporq | Vv Hv Wv | FV",
            "1 66 EC x vpaddsb | Vv Hv Wv | FVM both",
            "1 66 ED x vpaddsw | Vv Hv Wv | FVM both",
            "1 66 EE x vpmaxsw | Vv Hv Wv | FVM both",
            "1 66 EF x vpxord/vpxorq | Vv Hv Wv | FV",
            "1 66 F1 x vpsllw | Vv Hv Wx | M128 both",
            "1 66 F2 0 vpslld | Vv Hv Wx | M128 both",
            "1 66 F3 1 vpsllq | Vv Hv Wx | M128 both",
            "1 66 F4 1 vpmuludq | Vv Hv Wv | FV both",
            "1 66 F5 x vpmaddwd | Vv Hv Wv | FVM both",
            "1 66 F6 x vpsadbw | Vv Hv Wv | FVM both",
            "1 66 F8 x vpsubb | Vv Hv Wv | FVM both",
            "1 66 F9 x vpsubw | Vv Hv Wv | FVM both",
            "1 66 FA 0 vpsubd | Vv Hv Wv | FV both",
            "1 66 FB 1 vpsubq | Vv Hv Wv | FV both",
            "1 66 FC x vpaddb | Vv Hv Wv | FVM both",
            "1 66 FD x vpaddw | Vv Hv Wv | FVM both",
            "1 66 FE 0 vpaddd | Vv Hv Wv | FV both",
            // ----- 遮罩暫存器運算（VEX）-----
            "1 - 41 0 kandw | Kg Kh Kr | vex l1",
            "1 66 41 0 kandb | Kg Kh Kr | vex l1",
            "1 - 41 1 kandq | Kg Kh Kr | vex l1",
            "1 66 41 1 kandd | Kg Kh Kr | vex l1",
            "1 - 42 0 kandnw | Kg Kh Kr | vex l1",
            "1 66 42 0 kandnb | Kg Kh Kr | vex l1",
            "1 - 42 1 kandnq | Kg Kh Kr | vex l1",
            "1 66 42 1 kandnd | Kg Kh Kr | vex l1",
            "1 - 44 0 knotw | Kg Kr | vex l0",
            "1 66 44 0 knotb | Kg Kr | vex l0",
            "1 - 44 1 knotq | Kg Kr | vex l0",
            "1 66 44 1 knotd | Kg Kr | vex l0",
            "1 - 45 0 korw | Kg Kh Kr | vex l1",
            "1 66 45 0 korb | Kg Kh Kr | vex l1",
            "1 - 45 1 korq | Kg Kh Kr | vex l1",
            "1 66 45 1 kord | Kg Kh Kr | vex l1",
            "1 - 46 0 kxnorw | Kg Kh Kr | vex l1",
            "1 66 46 0 kxnorb | Kg Kh Kr | vex l1",
            "1 - 46 1 kxnorq | Kg Kh Kr | vex l1",
            "1 66 46 1 kxnord | Kg Kh Kr | vex l1",
            "1 - 47 0 kxorw | Kg Kh Kr | vex l1",
            "1 66 47 0 kxorb | Kg Kh Kr | vex l1",
            "1 - 47 1 kxorq | Kg Kh Kr | vex l1",
            "1 66 47 1 kxord | Kg Kh Kr | vex l1",
            "1 - 4A 0 kaddw | Kg Kh Kr | vex l1",
            "1 66 4A 0 kaddb | Kg Kh Kr | vex l1",
            "1 - 4A 1 kaddq | Kg Kh Kr | vex l1",
            "1 66 4A 1 kaddd | Kg Kh Kr | vex l1",
            "1 - 4B 0 kunpckwd | Kg Kh Kr | vex l1",
            "1 66 4B 0 kunpckbw | Kg Kh Kr | vex l1",
            "1 - 4B 1 kunpckdq | Kg Kh Kr | vex l1",
            "1 - 90 0 kmovw | Kg Km | T1S e2 vex l0",
            "1 66 90 0 kmovb | Kg Km | T1S e1 vex l0",
            "1 - 90 1 kmovq | Kg Km | T1S e8 vex l0",
            "1 66 90 1 kmovd | Kg Km | T1S e4 vex l0",
            "1 - 91 0 kmovw | M Kg | T1S e2 m vex l0",
            "1 66 91 0 kmovb | M Kg | T1S e1 m vex l0",
            "1 - 91 1 kmovq | M Kg | T1S e8 m vex l0",
            "1 66 91 1 kmovd | M Kg | T1S e4 m vex l0",
            "1 - 92 0 kmovw | Kg Rd | r vex l0",
            "1 66 92 0 kmovb | Kg Rd | r vex l0",
            "1 F2 92 0 kmovd | Kg Rd | r vex l0",
            "1 F2 92 1 kmovq | Kg Ry | r vex l0",
            "1 - 93 0 kmovw | Gd Kr | r vex l0",
            "1 66 93 0 kmovb | Gd Kr | r vex l0",
            "1 F2 93 0 kmovd | Gd Kr | r vex l0",
            "1 F2 93 1 kmovq | Gy Kr | r vex l0",
            "1 - 98 0 kortestw | Kg Kr | vex",
            "1 66 98 0 kortestb | Kg Kr | vex",
            "1 - 98 1 kortestq | Kg Kr | vex",
            "1 66 98 1 kortestd | Kg Kr | vex",
            "1 - 99 0 ktestw | Kg Kr | vex",
            "1 66 99 0 ktestb | Kg Kr | vex",
            "1 - 99 1 ktestq | Kg Kr | vex",
            "1 66 99 1 ktestd | Kg Kr | vex",

            // ===== map 2（0F38）=====
            "2 66 00 x vpshufb | Vv Hv Wv | FVM",
            "2 66 04 x vpmaddubsw | Vv Hv Wv | FVM",
            "2 66 0B x vpmulhrsw | Vv Hv Wv | FVM",
            "2 66 0C 0 vpermilps | Vv Hv Wv | FV",
            "2 66 0D 1 vpermilpd | Vv Hv Wv | FV",
            "2 66 10 1 vpsrlvw | Vv Hv Wv | FVM",
            "2 66 11 1 vpsravw | Vv Hv Wv | FVM",
            "2 66 12 1 vpsllvw | Vv Hv Wv | FVM",
            "2 66 13 0 vcvtph2ps | Vv Wh | HVM sae both",
            "2 66 14 x vprorvd/vprorvq | Vv Hv Wv | FV",
            "2 66 15 x vprolvd/vprolvq | Vv Hv Wv | FV",
            "2 66 16 x vpermps/vpermpd | Vv Hv Wv | FV l1",
            "2 66 18 0 vbroadcastss | Vv Wx | T1S e4 both",
            "2 66 19 0 vbroadcastf32x2 | Vv Wx | T2 e4 l1",
            "2 66 19 1 vbroadcastsd | Vv Wx | T1S e8 l1 both",
            "2 66 1A 0 vbroadcastf32x4 | Vv M | T4 e4 m l1",
            "2 66 1A 1 vbroadcastf64x2 | Vv M | T2 e8 m l1",
            "2 66 1B 0 vbroadcastf32x8 | Vv M | T8 e4 m l2",
            "2 66 1B 1 vbroadcastf64x4 | Vv M | T4 e8 m l2",
            "2 66 1C x vpabsb | Vv Wv | FVM both",
            "2 66 1D x vpabsw | Vv Wv | FVM both",
            "2 66 1E 0 vpabsd | Vv Wv | FV both",
            "2 66 1F 1 vpabsq | Vv Wv | FV",
            "2 66 26 x vptestmb/vptestmw | Kg Hv Wv | FVM",
            "2 F3 26 x vptestnmb/vptestnmw | Kg Hv Wv | FVM",
            "2 66 27 x vptestmd/vptestmq | Kg Hv Wv | FV",
            "2 F3 27 x vptestnmd/vptestnmq | Kg Hv Wv | FV",
            "2 66 28 1 vpmuldq | Vv Hv Wv | FV",
            "2 F3 28 x vpmovm2b/vpmovm2w | Vv Kr | r",
            "2 66 29 1 vpcmpeqq | Kg Hv Wv | FV",
            "2 F3 29 x vpmovb2m/vpmovw2m | Kg Uv | r",
            "2 66 2A 0 vmovntdqa | Vv M | FVM m both",
            "2 F3 2A 1 vpbroadcastmb2q | Vv Kr | r",
            "2 66 2B 0 vpackusdw | Vv Hv Wv | FV",
            "2 66 2C x vscalefps/vscalefpd | Vv Hv Wv | FV er",
            "2 66 2D x vscalefss/vscalefsd | Vx Hx Wx | T1S er",
            "2 66 36 x vpermd/vpermq | Vv Hv Wv | FV l1",
            "2 66 37 1 vpcmpgtq | Kg Hv Wv | FV",
            "2 66 38 x vpminsb | Vv Hv Wv | FVM",
            "2 F3 38 x vpmovm2d/vpmovm2q | Vv Kr | r",
            "2 66 39 x vpminsd/vpminsq | Vv Hv Wv | FV",
            "2 F3 39 x vpmovd2m/vpmovq2m | Kg Uv | r",
            "2 66 3A x vpminuw | Vv Hv Wv | FVM",
            "2 F3 3A 0 vpbroadcastmw2d | Vv Kr | r",
            "2 66 3B x vpminud/vpminuq | Vv Hv Wv | FV",
            "2 66 3C x vpmaxsb | Vv Hv Wv | FVM",
            "2 66 3D x vpmaxsd/vpmaxsq | Vv Hv Wv | FV",
            "2 66 3E x vpmaxuw | Vv Hv Wv | FVM",
            "2 66 3F x vpmaxud/vpmaxuq | Vv Hv Wv | FV",
            "2 66 40 x vpmulld/vpmullq | Vv Hv Wv | FV",
            "2 66 42 x vgetexpps/vgetexppd | Vv Wv | FV sae",
            "2 66 43 x vgetexpss/vgetexpsd | Vx Hx Wx | T1S sae",
            "2 66 44 x vplzcntd/vplzcntq | Vv Wv | FV",
            "2 66 45 x vpsrlvd/vpsrlvq | Vv Hv Wv | FV",
            "2 66 46 x vpsravd/vpsravq | Vv Hv Wv | FV",
            "2 66 47 x vpsllvd/vpsllvq | Vv Hv Wv | FV",
            "2 66 4C x vrcp14ps/vrcp14pd | Vv Wv | FV",
            "2 66 4D x vrcp14ss/vrcp14sd | Vx Hx Wx | T1S",
            "2 66 4E x vrsqrt14ps/vrsqrt14pd | Vv Wv | FV",
            "2 66 4F x vrsqrt14ss/vrsqrt14sd | Vx Hx Wx | T1S",
            "2 66 50 0 vpdpbusd | Vv Hv Wv | FV",
            "2 66 51 0 vpdpbusds | Vv Hv Wv | FV",
            "2 66 52 0 vpdpwssd | Vv Hv Wv | FV",
            "2 66 53 0 vpdpwssds | Vv Hv Wv | FV",
            "2 F3 72 0 vcvtneps2bf16 | Vh Wv | FV",
            "2 F2 72 0 vcvtne2ps2bf16 | Vv Hv Wv | FV",
            "2 F3 52 0 vdpbf16ps | Vv Hv Wv | FV",
            "2 F2 50 0 vpdpbssd | Vv Hv Wv | FV both",
            "2 F2 51 0 vpdpbssds | Vv Hv Wv | FV both",
            "2 F3 50 0 vpdpbsud | Vv Hv Wv | FV both",
            "2 F3 51 0 vpdpbsuds | Vv Hv Wv | FV both",
            "2 - 50 0 vpdpbuud | Vv Hv Wv | FV both",
            "2 - 51 0 vpdpbuuds | Vv Hv Wv | FV both",
            "2 66 54 x vpopcntb/vpopcntw | Vv Wv | FVM",
            "2 66 55 x vpopcntd/vpopcntq | Vv Wv | FV",
            "2 66 58 0 vpbroadcastd | Vv Wx | T1S e4 both",
            "2 66 59 0 vbroadcasti32x2 | Vv Wx | T2 e4",
            "2 66 59 1 vpbroadcastq | Vv Wx | T1S e8 both",
            "2 66 5A 0 vbroadcasti32x4 | Vv M | T4 e4 m l1",
            "2 66 5A 1 vbroadcasti64x2 | Vv M | T2 e8 m l1",
            "2 66 5B 0 vbroadcasti32x8 | Vv M | T8 e4 m l2",
            "2 66 5B 1 vbroadcasti64x4 | Vv M | T4 e8 m l2",
            "2 66 62 0 vpexpandb | Vv Wv | T1S e1 kwvl",
            "2 66 62 1 vpexpandw | Vv Wv | T1S e2 kwvl",
            "2 66 63 0 vpcompressb | Wv Vv | T1S e1 kwvl",
            "2 66 63 1 vpcompressw | Wv Vv | T1S e2 kwvl",
            "2 66 64 x vpblendmd/vpblendmq | Vv Hv Wv | FV",
            "2 66 65 x vblendmps/vblendmpd | Vv Hv Wv | FV",
            "2 66 66 x vpblendmb/vpblendmw | Vv Hv Wv | FVM",
            "2 66 70 1 vpshldvw | Vv Hv Wv | FVM",
            "2 66 71 x vpshldvd/vpshldvq | Vv Hv Wv | FV",
            "2 66 72 1 vpshrdvw | Vv Hv Wv | FVM",
            "2 66 73 x vpshrdvd/vpshrdvq | Vv Hv Wv | FV",
            "2 66 75 x vpermi2b/vpermi2w | Vv Hv Wv | FVM",
            "2 66 76 x vpermi2d/vpermi2q | Vv Hv Wv | FV",
            "2 66 77 x vpermi2ps/vpermi2pd | Vv Hv Wv | FV",
            "2 66 78 0 vpbroadcastb | Vv Wx | T1S e1 both",
            "2 66 79 0 vpbroadcastw | Vv Wx | T1S e2 both",
            "2 66 7A 0 vpbroadcastb | Vv Rd | r",
            "2 66 7B 0 vpbroadcastw | Vv Rd | r",
            "2 66 7C 0 vpbroadcastd | Vv Rd | r",
            "2 66 7C 1 vpbroadcastq | Vv Ry | r",
            "2 66 7D x vpermt2b/vpermt2w | Vv Hv Wv | FVM",
            "2 66 7E x vpermt2d/vpermt2q | Vv Hv Wv | FV",
            "2 66 7F x vpermt2ps/vpermt2pd | Vv Hv Wv | FV",
            "2 66 83 1 vpmultishiftqb | Vv Hv Wv | FV",
            "2 66 88 x vexpandps/vexpandpd | Vv Wv | T1S kwvl",
            "2 66 89 x vpexpandd/vpexpandq | Vv Wv | T1S kwvl",
            "2 66 8A x vcompressps/vcompresspd | Wv Vv | T1S kwvl",
            "2 66 8B x vpcompressd/vpcompressq | Wv Vv | T1S kwvl",
            "2 66 8D x vpermb/vpermw | Vv Hv Wv | FVM",
            "2 66 8F 0 vpshufbitqmb | Kg Hv Wv | FVM",
            // 蒐集與散佈
            "2 66 90 0 vpgatherdd | Vv M | T1S vs1 m e4",
            "2 66 90 1 vpgatherdq | Vv M | T1S vs2 m e8",
            "2 66 91 0 vpgatherqd | Vh M | T1S vs1 m e4",
            "2 66 91 1 vpgatherqq | Vv M | T1S vs1 m e8",
            "2 66 92 0 vgatherdps | Vv M | T1S vs1 m e4",
            "2 66 92 1 vgatherdpd | Vv M | T1S vs2 m e8",
            "2 66 93 0 vgatherqps | Vh M | T1S vs1 m e4",
            "2 66 93 1 vgatherqpd | Vv M | T1S vs1 m e8",
            "2 66 A0 0 vpscatterdd | M Vv | T1S vs1 m e4",
            "2 66 A0 1 vpscatterdq | M Vv | T1S vs2 m e8",
            "2 66 A1 0 vpscatterqd | M Vh | T1S vs1 m e4",
            "2 66 A1 1 vpscatterqq | M Vv | T1S vs1 m e8",
            "2 66 A2 0 vscatterdps | M Vv | T1S vs1 m e4",
            "2 66 A2 1 vscatterdpd | M Vv | T1S vs2 m e8",
            "2 66 A3 0 vscatterqps | M Vh | T1S vs1 m e4",
            "2 66 A3 1 vscatterqpd | M Vv | T1S vs1 m e8",
            "2 66 B4 1 vpmadd52luq | Vv Hv Wv | FV",
            "2 66 B5 1 vpmadd52huq | Vv Hv Wv | FV",
            "2 66 C4 x vpconflictd/vpconflictq | Vv Wv | FV",
            "2 66 C8 x vexp2ps/vexp2pd | Vv Wv | FV sae",
            "2 66 CA x vrcp28ps/vrcp28pd | Vv Wv | FV sae",
            "2 66 CB x vrcp28ss/vrcp28sd | Vx Hx Wx | T1S sae",
            "2 66 CC x vrsqrt28ps/vrsqrt28pd | Vv Wv | FV sae",
            "2 66 CD x vrsqrt28ss/vrsqrt28sd | Vx Hx Wx | T1S sae",
            "2 66 CF 0 vgf2p8mulb | Vv Hv Wv | FVM both",
            "2 66 DC x vaesenc | Vv Hv Wv | FVM both",
            "2 66 DD x vaesenclast | Vv Hv Wv | FVM both",
            "2 66 DE x vaesdec | Vv Hv Wv | FVM both",
            "2 66 DF x vaesdeclast | Vv Hv Wv | FVM both",

            // ===== map 3（0F3A）=====
            "3 66 00 1 vpermq | Vv Wv Ib | FV l1 both",
            "3 66 01 1 vpermpd | Vv Wv Ib | FV l1 both",
            "3 66 03 x valignd/valignq | Vv Hv Wv Ib | FV",
            "3 66 04 0 vpermilps | Vv Wv Ib | FV both",
            "3 66 05 1 vpermilpd | Vv Wv Ib | FV both",
            "3 66 08 0 vrndscaleps | Vv Wv Ib | FV sae",
            "3 66 09 1 vrndscalepd | Vv Wv Ib | FV sae",
            "3 66 0A 0 vrndscaless | Vx Hx Wx Ib | T1S e4 sae",
            "3 66 0B 1 vrndscalesd | Vx Hx Wx Ib | T1S e8 sae",
            "3 66 0F x vpalignr | Vv Hv Wv Ib | FVM",
            "3 66 14 x vpextrb | Eb Vx Ib | T1S e1 both",
            "3 66 15 x vpextrw | Ew Vx Ib | T1S e2 both",
            "3 66 16 x vpextrd/vpextrq | Ey Vx Ib | T1S both",
            "3 66 17 x vextractps | Ed Vx Ib | T1S e4 both",
            "3 66 18 0 vinsertf32x4 | Vv Hv Wx Ib | T4 e4 l1",
            "3 66 18 1 vinsertf64x2 | Vv Hv Wx Ib | T2 e8 l1",
            "3 66 19 0 vextractf32x4 | Wx Vv Ib | T4 e4 l1",
            "3 66 19 1 vextractf64x2 | Wx Vv Ib | T2 e8 l1",
            "3 66 1A 0 vinsertf32x8 | Vv Hv Wy Ib | T8 e4 l2",
            "3 66 1A 1 vinsertf64x4 | Vv Hv Wy Ib | T4 e8 l2",
            "3 66 1B 0 vextractf32x8 | Wy Vv Ib | T8 e4 l2",
            "3 66 1B 1 vextractf64x4 | Wy Vv Ib | T4 e8 l2",
            "3 66 1D 0 vcvtps2ph | Wh Vv Ib | HVM sae both",
            "3 66 1E x vpcmpud/vpcmpuq | Kg Hv Wv Ib | FV pcmp",
            "3 66 1F x vpcmpd/vpcmpq | Kg Hv Wv Ib | FV pcmp",
            "3 66 20 x vpinsrb | Vx Hx Eb Ib | T1S e1 both",
            "3 66 21 0 vinsertps | Vx Hx Wx Ib | T1S e4 both",
            "3 66 22 x vpinsrd/vpinsrq | Vx Hx Ey Ib | T1S both",
            "3 66 23 x vshuff32x4/vshuff64x2 | Vv Hv Wv Ib | FV l1",
            "3 66 25 x vpternlogd/vpternlogq | Vv Hv Wv Ib | FV",
            "3 66 26 x vgetmantps/vgetmantpd | Vv Wv Ib | FV sae",
            "3 66 27 x vgetmantss/vgetmantsd | Vx Hx Wx Ib | T1S sae",
            "3 66 38 0 vinserti32x4 | Vv Hv Wx Ib | T4 e4 l1",
            "3 66 38 1 vinserti64x2 | Vv Hv Wx Ib | T2 e8 l1",
            "3 66 39 0 vextracti32x4 | Wx Vv Ib | T4 e4 l1",
            "3 66 39 1 vextracti64x2 | Wx Vv Ib | T2 e8 l1",
            "3 66 3A 0 vinserti32x8 | Vv Hv Wy Ib | T8 e4 l2",
            "3 66 3A 1 vinserti64x4 | Vv Hv Wy Ib | T4 e8 l2",
            "3 66 3B 0 vextracti32x8 | Wy Vv Ib | T8 e4 l2",
            "3 66 3B 1 vextracti64x4 | Wy Vv Ib | T4 e8 l2",
            "3 66 3E x vpcmpub/vpcmpuw | Kg Hv Wv Ib | FVM pcmp",
            "3 66 3F x vpcmpb/vpcmpw | Kg Hv Wv Ib | FVM pcmp",
            "3 66 42 0 vdbpsadbw | Vv Hv Wv Ib | FVM",
            "3 66 43 x vshufi32x4/vshufi64x2 | Vv Hv Wv Ib | FV l1",
            "3 66 44 x vpclmulqdq | Vv Hv Wv Ib | FVM both",
            "3 66 50 x vrangeps/vrangepd | Vv Hv Wv Ib | FV sae",
            "3 66 51 x vrangess/vrangesd | Vx Hx Wx Ib | T1S sae",
            "3 66 54 x vfixupimmps/vfixupimmpd | Vv Hv Wv Ib | FV sae",
            "3 66 55 x vfixupimmss/vfixupimmsd | Vx Hx Wx Ib | T1S sae",
            "3 66 56 x vreduceps/vreducepd | Vv Wv Ib | FV sae",
            "3 66 57 x vreducess/vreducesd | Vx Hx Wx Ib | T1S sae",
            "3 66 66 x vfpclassps/vfpclasspd | Kg Wv Ib | FV",
            "3 66 67 x vfpclassss/vfpclasssd | Kg Wx Ib | T1S",
            "3 66 70 1 vpshldw | Vv Hv Wv Ib | FVM",
            "3 66 71 x vpshldd/vpshldq | Vv Hv Wv Ib | FV",
            "3 66 72 1 vpshrdw | Vv Hv Wv Ib | FVM",
            "3 66 73 x vpshrdd/vpshrdq | Vv Hv Wv Ib | FV",
            "3 66 CE 1 vgf2p8affineqb | Vv Hv Wv Ib | FV both",
            "3 66 CF 1 vgf2p8affineinvqb | Vv Hv Wv Ib | FV both",
            "3 66 30 0 kshiftrb | Kg Kr Ib | vex l0",
            "3 66 30 1 kshiftrw | Kg Kr Ib | vex l0",
            "3 66 31 0 kshiftrd | Kg Kr Ib | vex l0",
            "3 66 31 1 kshiftrq | Kg Kr Ib | vex l0",
            "3 66 32 0 kshiftlb | Kg Kr Ib | vex l0",
            "3 66 32 1 kshiftlw | Kg Kr Ib | vex l0",
            "3 66 33 0 kshiftld | Kg Kr Ib | vex l0",
            "3 66 33 1 kshiftlq | Kg Kr Ib | vex l0",

            // ===== VEX 專用（AVX／AVX2／BMI 補強）=====
            "1 - C2 0 vcmpps | Vv Hv Wv Ib | vex cmp",
            "1 66 C2 0 vcmppd | Vv Hv Wv Ib | vex cmp",
            "1 F3 C2 0 vcmpss | Vx Hx Wx Ib | T1S e4 vex cmp",
            "1 F2 C2 0 vcmpsd | Vx Hx Wx Ib | T1S e8 vex cmp",
            "1 66 72/4 0 vpsrad | Hv Wv Ib | vex",
            "1 66 E2 0 vpsrad | Vv Hv Wx | vex",
            "1 66 DB 0 vpand | Vv Hv Wv | vex",
            "1 66 DF 0 vpandn | Vv Hv Wv | vex",
            "1 66 EB 0 vpor | Vv Hv Wv | vex",
            "1 66 EF 0 vpxor | Vv Hv Wv | vex",
            "1 F3 53 0 vrcpss | Vx Hx Wx | T1S e4 vex",
            "1 F3 52 0 vrsqrtss | Vx Hx Wx | T1S e4 vex",
            "3 66 0A 0 vroundss | Vx Hx Wx Ib | T1S e4 vex",
            "3 66 0B 0 vroundsd | Vx Hx Wx Ib | T1S e8 vex",
            "1 66 5A 0 vcvtpd2ps | Vh Wv | vex",
            "1 66 E6 0 vcvttpd2dq | Vh Wv | vex",
            "1 F2 E6 0 vcvtpd2dq | Vh Wv | vex",
            "1 - 50 0 vmovmskps | Gy Uv | vex r",
            "1 66 50 0 vmovmskpd | Gy Uv | vex r",
            "1 66 D7 x vpmovmskb | Gy Uv | vex r",
            "1 F2 F0 0 vlddqu | Vv M0 | vex m",
            "2 66 DB 0 vaesimc | Vx Wx | vex",
            "3 66 DF 0 vaeskeygenassist | Vx Wx Ib | vex",
            "2 66 41 0 vphminposuw | Vx Wx | vex",
            "3 66 08 0 vroundps | Vv Wv Ib | vex",
            "3 66 09 0 vroundpd | Vv Wv Ib | vex",
            "3 66 60 0 vpcmpestrm | Vx Wx Ib | vex",
            "3 66 60 1 vpcmpestrmq | Vx Wx Ib | vex",
            "3 66 61 0 vpcmpestri | Vx Wx Ib | vex",
            "3 66 61 1 vpcmpestriq | Vx Wx Ib | vex",
            "3 66 62 x vpcmpistrm | Vx Wx Ib | vex",
            "3 66 63 x vpcmpistri | Vx Wx Ib | vex",
            "3 66 06 0 vperm2f128 | Vy Hy Wy Ib | vex l1",
            "3 66 46 0 vperm2i128 | Vy Hy Wy Ib | vex l1",
            "3 66 18 0 vinsertf128 | Vy Hy Wx Ib | vex l1",
            "3 66 38 0 vinserti128 | Vy Hy Wx Ib | vex l1",
            "3 66 19 0 vextractf128 | Wx Vy Ib | vex l1",
            "3 66 39 0 vextracti128 | Wx Vy Ib | vex l1",
            "3 66 02 0 vpblendd | Vv Hv Wv Ib | vex",
            "3 66 4A 0 vblendvps | Vv Hv Wv Lv | vex",
            "3 66 4B 0 vblendvpd | Vv Hv Wv Lv | vex",
            "3 66 4C 0 vpblendvb | Vv Hv Wv Lv | vex",
            "3 F2 F0 x rorx | Gy Ey Ib | vex l0",
            "2 66 0C 0 vpermilps | Vv Hv Wv | vex",
            "2 66 0D 0 vpermilpd | Vv Hv Wv | vex",
            "2 66 0E 0 vtestps | Vv Wv | vex",
            "2 66 0F 0 vtestpd | Vv Wv | vex",
            "2 66 16 0 vpermps | Vy Hy Wy | vex l1",
            "2 66 36 0 vpermd | Vy Hy Wy | vex l1",
            "2 66 1A 0 vbroadcastf128 | Vy Mx | vex m l1",
            "2 66 5A 0 vbroadcasti128 | Vy Mx | vex m l1",
            "2 66 2C 0 vmaskmovps | Vv Hv M | vex m",
            "2 66 2D 0 vmaskmovpd | Vv Hv M | vex m",
            "2 66 2E 0 vmaskmovps | M Hv Vv | vex m",
            "2 66 2F 0 vmaskmovpd | M Hv Vv | vex m",
            "2 66 8C x vpmaskmovd/vpmaskmovq | Vv Hv M | vex m",
            "2 66 8E x vpmaskmovd/vpmaskmovq | M Hv Vv | vex m",
            "2 66 45 x vpsrlvd/vpsrlvq | Vv Hv Wv | vex",
            "2 66 46 0 vpsravd | Vv Hv Wv | vex",
            "2 66 47 x vpsllvd/vpsllvq | Vv Hv Wv | vex",
            "2 66 90 0 vpgatherdd | Vv M Hv | T1S vs1 m e4 vex",
            "2 66 90 1 vpgatherdq | Vv M Hv | T1S vs2 m e8 vex",
            "2 66 91 0 vpgatherqd | Vh M Hh | T1S vs1 m e4 vex",
            "2 66 91 1 vpgatherqq | Vv M Hv | T1S vs1 m e8 vex",
            "2 66 92 0 vgatherdps | Vv M Hv | T1S vs1 m e4 vex",
            "2 66 92 1 vgatherdpd | Vv M Hv | T1S vs2 m e8 vex",
            "2 66 93 0 vgatherqps | Vh M Hh | T1S vs1 m e4 vex",
            "2 66 93 1 vgatherqpd | Vv M Hv | T1S vs1 m e8 vex",
            "2 - F2 x andn | Gy By Ey | vex l0",
            "2 - F3/1 x blsr | By Ey | vex l0",
            "2 - F3/2 x blsmsk | By Ey | vex l0",
            "2 - F3/3 x blsi | By Ey | vex l0",
            "2 - F5 x bzhi | Gy Ey By | vex l0",
            "2 F3 F5 x pext | Gy By Ey | vex l0",
            "2 F2 F5 x pdep | Gy By Ey | vex l0",
            "2 F2 F6 x mulx | Gy By Ey | vex l0",
            "2 - F7 x bextr | Gy Ey By | vex l0",
            "2 66 F7 x shlx | Gy Ey By | vex l0",
            "2 F3 F7 x sarx | Gy Ey By | vex l0",
            "2 F2 F7 x shrx | Gy Ey By | vex l0",
        };
    }
}
