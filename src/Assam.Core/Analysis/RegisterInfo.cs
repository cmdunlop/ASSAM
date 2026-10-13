using System;
using System.Collections.Generic;
using System.Globalization;

namespace Assam.Core.Analysis
{
    /// <summary>各架構暫存器的中文說明。</summary>
    public static class RegisterInfo
    {
        private static readonly Dictionary<string, string> X86Roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "a", "累加器（算術運算、乘除法、函式回傳值）" },
            { "b", "基底暫存器（常用來存位址）" },
            { "c", "計數暫存器（LOOP、REP 的次數，CL 為移位次數）" },
            { "d", "資料暫存器（乘除法的高位部分、I/O 埠號）" },
            { "si", "來源索引（字串指令的來源位址）" },
            { "di", "目的索引（字串指令的目的位址）" },
            { "sp", "堆疊指標（指向堆疊頂端）" },
            { "bp", "基底指標（存取參數與區域變數的堆疊框架）" }
        };

        /// <summary>x86 暫存器：回傳位元數，不是暫存器則回傳 0。</summary>
        public static int X86RegisterBits(string name)
        {
            string r = (name ?? string.Empty).Trim().ToLowerInvariant().TrimStart('%');
            switch (r)
            {
                case "rax": case "rbx": case "rcx": case "rdx": case "rsi": case "rdi": case "rsp": case "rbp": case "rip":
                    return 64;
                case "eax": case "ebx": case "ecx": case "edx": case "esi": case "edi": case "esp": case "ebp": case "eip":
                    return 32;
                case "ax": case "bx": case "cx": case "dx": case "si": case "di": case "sp": case "bp": case "ip":
                case "cs": case "ds": case "es": case "ss": case "fs": case "gs":
                    return 16;
                case "al": case "ah": case "bl": case "bh": case "cl": case "ch": case "dl": case "dh":
                case "sil": case "dil": case "spl": case "bpl":
                    return 8;
            }
            if (r.Length >= 2 && r[0] == 'r' && char.IsDigit(r[1]))
            {
                string digits = string.Empty;
                int i = 1;
                while (i < r.Length && char.IsDigit(r[i])) digits += r[i++];
                int n;
                if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 8 || n > 15) return 0;
                string suffix = r.Substring(i);
                switch (suffix)
                {
                    case "": return 64;
                    case "d": return 32;
                    case "w": return 16;
                    case "b": case "l": return 8;
                }
                return 0;
            }
            if (r.StartsWith("xmm", StringComparison.Ordinal)) return 128;
            return 0;
        }

        public static string Describe(AsmArch arch, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string r = name.Trim().ToLowerInvariant();
            switch (arch)
            {
                case AsmArch.X86Intel:
                case AsmArch.X86Att:
                case AsmArch.Unknown:
                    return DescribeX86(r.TrimStart('%'));
                case AsmArch.Arm:
                    return DescribeArm(r);
                case AsmArch.Arm64:
                    return DescribeArm64(r);
                case AsmArch.Mips:
                    return DescribeMips(r);
                case AsmArch.RiscV:
                    return DescribeRiscV(r);
                case AsmArch.I8051:
                    return Describe8051(r);
                case AsmArch.Avr:
                    return DescribeAvr(r);
            }
            return null;
        }

        private static string DescribeX86(string r)
        {
            int bits = X86RegisterBits(r);
            if (bits == 0) return null;
            string upper = r.ToUpperInvariant();
            if (bits == 128) return upper + "：128 位元 SSE 暫存器（浮點／向量運算）";

            switch (r)
            {
                case "cs": return "CS：程式碼區段暫存器";
                case "ds": return "DS：資料區段暫存器";
                case "ss": return "SS：堆疊區段暫存器";
                case "es": return "ES：額外區段暫存器（字串指令的目的區段）";
                case "fs":
                case "gs": return upper + "：額外區段暫存器（Windows 用於 TEB、Linux 用於執行緒區域儲存）";
                case "ip":
                case "eip":
                case "rip": return upper + "：指令指標（下一個要執行的指令位址）";
            }

            string role = null;
            string key = r;
            if (key.StartsWith("r", StringComparison.Ordinal) && key.Length >= 2 && char.IsDigit(key[1]))
                role = "x64 新增的通用暫存器";
            else
            {
                if (key.StartsWith("e", StringComparison.Ordinal) || (key.StartsWith("r", StringComparison.Ordinal) && key.Length == 3)) key = key.Substring(1);
                if (key.Length == 2 && (key[1] == 'x' || key[1] == 'l' || key[1] == 'h') && "abcd".IndexOf(key[0]) >= 0) key = key.Substring(0, 1);
                if (key == "sil") key = "si";
                if (key == "dil") key = "di";
                if (key == "spl") key = "sp";
                if (key == "bpl") key = "bp";
                X86Roles.TryGetValue(key, out role);
            }

            string part = string.Empty;
            if (r.Length == 2 && r[1] == 'h' && "abcd".IndexOf(r[0]) >= 0) part = "，是 " + char.ToUpperInvariant(r[0]) + "X 的高 8 位元";
            else if (r.Length == 2 && r[1] == 'l' && "abcd".IndexOf(r[0]) >= 0) part = "，是 " + char.ToUpperInvariant(r[0]) + "X 的低 8 位元";
            return upper + "：" + bits.ToString(CultureInfo.InvariantCulture) + " 位元暫存器" + (role != null ? "，" + role : string.Empty) + part;
        }

        private static string DescribeArm(string r)
        {
            switch (r)
            {
                case "sp": case "r13": return r.ToUpperInvariant() + "：堆疊指標（SP，R13）";
                case "lr": case "r14": return r.ToUpperInvariant() + "：連結暫存器（LR，R14，存放副程式的返回位址）";
                case "pc": case "r15": return r.ToUpperInvariant() + "：程式計數器（PC，R15）";
                case "fp": case "r11": return r.ToUpperInvariant() + "：框架指標（FP，R11，被呼叫者需保存）";
                case "ip": case "r12": return r.ToUpperInvariant() + "：程序內暫用暫存器（IP，R12）";
                case "cpsr": return "CPSR：目前程式狀態暫存器（N Z C V 旗標與模式）";
                case "apsr": return "APSR：應用程式狀態暫存器（N Z C V 旗標）";
                case "spsr": return "SPSR：已保存的程式狀態暫存器（例外處理時使用）";
            }
            int n;
            if (r.Length >= 2 && r[0] == 'r' && int.TryParse(r.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0 && n <= 15)
            {
                if (n <= 3) return r.ToUpperInvariant() + "：32 位元暫存器（AAPCS：傳遞參數與回傳值）";
                return r.ToUpperInvariant() + "：32 位元暫存器（AAPCS：變數暫存器，被呼叫者需保存）";
            }
            return null;
        }

        private static string DescribeArm64(string r)
        {
            switch (r)
            {
                case "sp": return "SP：堆疊指標（必須 16 位元組對齊）";
                case "xzr": return "XZR：64 位元零暫存器（讀取恆為 0，寫入會被丟棄）";
                case "wzr": return "WZR：32 位元零暫存器";
                case "lr": return "LR（X30）：連結暫存器，存放返回位址";
                case "fp": return "FP（X29）：框架指標";
            }
            int n;
            if (r.Length >= 2 && (r[0] == 'x' || r[0] == 'w') && int.TryParse(r.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0 && n <= 30)
            {
                string size = r[0] == 'x' ? "64 位元暫存器" : "32 位元暫存器（X" + n + " 的低 32 位元）";
                string role;
                if (n <= 7) role = "傳遞參數與回傳值";
                else if (n == 8) role = "間接結果／Linux 系統呼叫號碼";
                else if (n <= 15) role = "暫存（呼叫者保存）";
                else if (n <= 17) role = "程序內呼叫暫存（IP0/IP1）";
                else if (n == 18) role = "平台保留";
                else if (n <= 28) role = "被呼叫者保存";
                else if (n == 29) role = "框架指標 FP";
                else role = "連結暫存器 LR（返回位址）";
                return r.ToUpperInvariant() + "：" + size + "，" + role;
            }
            return null;
        }

        private static readonly string[] MipsNames =
        {
            "zero", "at", "v0", "v1", "a0", "a1", "a2", "a3", "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7",
            "s0", "s1", "s2", "s3", "s4", "s5", "s6", "s7", "t8", "t9", "k0", "k1", "gp", "sp", "fp", "ra"
        };

        private static string DescribeMips(string r)
        {
            string name = r.TrimStart('$');
            int n;
            if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0 && n < 32)
                name = MipsNames[n];
            string role;
            switch (name)
            {
                case "zero": role = "恆為 0"; break;
                case "at": role = "組譯器保留（虛擬指令展開用）"; break;
                case "v0": case "v1": role = "函式回傳值；syscall 時 $v0 放服務號碼"; break;
                case "a0": case "a1": case "a2": case "a3": role = "函式參數／syscall 參數"; break;
                case "k0": case "k1": role = "作業系統核心保留"; break;
                case "gp": role = "全域指標"; break;
                case "sp": role = "堆疊指標"; break;
                case "fp": case "s8": role = "框架指標"; break;
                case "ra": role = "返回位址（jal 會自動寫入）"; break;
                default:
                    if (name.Length == 2 && name[0] == 't' && char.IsDigit(name[1])) role = "暫存暫存器（呼叫函式後可能被改變）";
                    else if (name.Length == 2 && name[0] == 's' && char.IsDigit(name[1])) role = "保存暫存器（被呼叫的函式需保留其值）";
                    else return null;
                    break;
            }
            int index = Array.IndexOf(MipsNames, name);
            return "$" + name + (index >= 0 ? "（$" + index + "）" : string.Empty) + "：" + role;
        }

        private static string DescribeRiscV(string r)
        {
            string[] abi =
            {
                "zero", "ra", "sp", "gp", "tp", "t0", "t1", "t2", "s0", "s1", "a0", "a1", "a2", "a3", "a4", "a5",
                "a6", "a7", "s2", "s3", "s4", "s5", "s6", "s7", "s8", "s9", "s10", "s11", "t3", "t4", "t5", "t6"
            };
            string name = r;
            int n;
            if (name == "fp") name = "s0";
            if (name.Length >= 2 && name[0] == 'x' && int.TryParse(name.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0 && n < 32)
                name = abi[n];
            int index = Array.IndexOf(abi, name);
            if (index < 0) return null;
            string role;
            if (name == "zero") role = "恆為 0";
            else if (name == "ra") role = "返回位址";
            else if (name == "sp") role = "堆疊指標";
            else if (name == "gp") role = "全域指標";
            else if (name == "tp") role = "執行緒指標";
            else if (name == "s0") role = "保存暫存器／框架指標 fp";
            else if (name == "a0" || name == "a1") role = "函式參數與回傳值";
            else if (name[0] == 'a') role = "函式參數（a7 也用來放系統呼叫號碼）";
            else if (name[0] == 's') role = "保存暫存器（被呼叫者保存）";
            else role = "暫存暫存器（呼叫者保存）";
            return r + "（x" + index + "）：" + role;
        }

        private static readonly Dictionary<string, string> I8051Sfr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "a", "A：累加器（大部分算術、邏輯運算的對象）" },
            { "acc", "ACC：累加器（以 SFR 名稱表示，位址 E0h）" },
            { "b", "B：B 暫存器（MUL AB、DIV AB 使用）" },
            { "dptr", "DPTR：16 位元資料指標（存取外部記憶體或查表）" },
            { "dpl", "DPL：DPTR 的低位元組" },
            { "dph", "DPH：DPTR 的高位元組" },
            { "psw", "PSW：程式狀態字組（CY AC F0 RS1 RS0 OV P）" },
            { "sp", "SP：堆疊指標（重置後為 07h）" },
            { "c", "C：進位旗標 CY（位元運算的累加器）" },
            { "cy", "CY：進位旗標" },
            { "ov", "OV：溢位旗標" },
            { "p0", "P0：I/O 連接埠 0（開汲極，通常需要提升電阻）" },
            { "p1", "P1：I/O 連接埠 1" },
            { "p2", "P2：I/O 連接埠 2" },
            { "p3", "P3：I/O 連接埠 3（兼具中斷、計時器、序列埠等第二功能）" },
            { "tmod", "TMOD：計時器模式暫存器（設定 Timer0/1 的模式）" },
            { "tcon", "TCON：計時器控制暫存器（TR0/TR1 啟動、TF0/TF1 溢位旗標）" },
            { "th0", "TH0：計時器 0 的高位元組" },
            { "tl0", "TL0：計時器 0 的低位元組" },
            { "th1", "TH1：計時器 1 的高位元組（序列埠鮑率常用）" },
            { "tl1", "TL1：計時器 1 的低位元組" },
            { "tr0", "TR0：計時器 0 啟動位元" },
            { "tr1", "TR1：計時器 1 啟動位元" },
            { "tf0", "TF0：計時器 0 溢位旗標" },
            { "tf1", "TF1：計時器 1 溢位旗標" },
            { "scon", "SCON：序列埠控制暫存器" },
            { "sbuf", "SBUF：序列埠資料緩衝暫存器" },
            { "ri", "RI：序列埠接收完成旗標" },
            { "ti", "TI：序列埠傳送完成旗標" },
            { "ie", "IE：中斷致能暫存器" },
            { "ip", "IP：中斷優先權暫存器" },
            { "ea", "EA：總中斷致能位元" },
            { "et0", "ET0：計時器 0 中斷致能" },
            { "et1", "ET1：計時器 1 中斷致能" },
            { "ex0", "EX0：外部中斷 0 致能" },
            { "ex1", "EX1：外部中斷 1 致能" },
            { "es", "ES：序列埠中斷致能" },
            { "it0", "IT0：外部中斷 0 觸發方式（1=負緣觸發）" },
            { "it1", "IT1：外部中斷 1 觸發方式" },
            { "pcon", "PCON：電源控制暫存器（SMOD 位元可倍增鮑率）" }
        };

        private static string Describe8051(string r)
        {
            string s;
            if (I8051Sfr.TryGetValue(r, out s)) return s;
            if (r.Length == 2 && r[0] == 'r' && r[1] >= '0' && r[1] <= '7')
                return r.ToUpperInvariant() + "：工作暫存器（位於目前選擇的暫存器庫）";
            return null;
        }

        private static string DescribeAvr(string r)
        {
            switch (r)
            {
                case "x": return "X：16 位元指標暫存器（R27:R26）";
                case "y": return "Y：16 位元指標暫存器（R29:R28）";
                case "z": return "Z：16 位元指標暫存器（R31:R30，也用於 LPM 讀取 Flash）";
                case "sreg": return "SREG：狀態暫存器（I T H S V N Z C 旗標）";
                case "spl": return "SPL：堆疊指標低位元組";
                case "sph": return "SPH：堆疊指標高位元組";
                case "xl": case "xh": case "yl": case "yh": case "zl": case "zh":
                    return r.ToUpperInvariant() + "：指標暫存器 " + char.ToUpperInvariant(r[0]) + " 的" + (r[1] == 'l' ? "低" : "高") + "位元組";
            }
            if (r.StartsWith("port", StringComparison.Ordinal) && r.Length == 5) return r.ToUpperInvariant() + "：連接埠 " + char.ToUpperInvariant(r[4]) + " 的輸出暫存器";
            if (r.StartsWith("ddr", StringComparison.Ordinal) && r.Length == 4) return r.ToUpperInvariant() + "：連接埠 " + char.ToUpperInvariant(r[3]) + " 的方向暫存器（1 = 輸出）";
            if (r.StartsWith("pin", StringComparison.Ordinal) && r.Length == 4) return r.ToUpperInvariant() + "：連接埠 " + char.ToUpperInvariant(r[3]) + " 的輸入腳位狀態";
            int n;
            if (r.Length >= 2 && r[0] == 'r' && int.TryParse(r.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0 && n <= 31)
            {
                if (n >= 26) return r.ToUpperInvariant() + "：通用暫存器（同時是指標 " + (n < 28 ? "X" : n < 30 ? "Y" : "Z") + " 的一部分）";
                if (n >= 16) return r.ToUpperInvariant() + "：通用暫存器（可用於 LDI、SUBI 等立即值指令）";
                return r.ToUpperInvariant() + "：通用暫存器（不能用於 LDI 等立即值指令）";
            }
            return null;
        }
    }
}
