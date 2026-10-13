using System;
using System.Collections.Generic;

namespace Assam.Core.Analysis
{
    /// <summary>組譯器虛擬指令（directive）的說明，例如 .data、PROC、DB、EQU、ORG。</summary>
    public static class DirectiveInfo
    {
        private static readonly HashSet<string> DataDefinitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "db", "dw", "dd", "dq", "dt", "df", "byte", "sbyte", "word", "sword", "dword", "sdword", "fword", "qword",
            "tbyte", "real4", "real8", "real10", "resb", "resw", "resd", "resq", "ds", ".byte", ".word", ".long", ".int",
            ".quad", ".short", ".half", ".dword", ".xword", ".hword", ".ascii", ".asciz", ".asciiz", ".string", ".space",
            ".skip", ".zero", ".db", ".dw", ".float", ".double", ".fill"
        };

        private static readonly Dictionary<string, string> Texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".model", "虛擬指令：設定記憶體模式（small 為 16 位元 DOS 程式常用、flat 為 32 位元 Windows 程式）與呼叫慣例" },
            { ".stack", "虛擬指令：設定堆疊大小" },
            { ".data", "虛擬指令：資料區段開始，以下宣告變數" },
            { ".data?", "虛擬指令：未初始化資料區段開始（不佔執行檔空間）" },
            { ".const", "虛擬指令：唯讀常數資料區段開始" },
            { ".code", "虛擬指令：程式碼區段開始，以下是要執行的指令" },
            { ".386", "虛擬指令：啟用 80386 以上的 32 位元指令" },
            { ".486", "虛擬指令：啟用 80486 以上的指令" },
            { ".586", "虛擬指令：啟用 Pentium 以上的指令" },
            { ".686", "虛擬指令：啟用 Pentium Pro 以上的指令" },
            { ".686p", "虛擬指令：啟用 Pentium Pro 以上的指令（含特權指令）" },
            { "proc", "虛擬指令：副程式（程序）開始" },
            { "endp", "虛擬指令：副程式（程序）結束" },
            { "end", "虛擬指令：原始碼結束；END 後面的標籤是程式進入點" },
            { "include", "虛擬指令：引入其他原始檔（例如 Irvine32.inc 提供常用的輸入輸出程序）" },
            { "includelib", "虛擬指令：連結時加入指定的函式庫" },
            { "invoke", "MASM 高階呼叫：自動把參數由右到左推入堆疊再 CALL" },
            { "equ", "虛擬指令：定義常數（名稱代表一個值，組譯時直接替換，不佔記憶體）" },
            { "=", "虛擬指令：定義可重新指定的數值常數" },
            { "textequ", "虛擬指令：定義文字替換常數" },
            { "org", "虛擬指令：設定接下來程式或資料的起始位址（8051 常用 ORG 0000h，COM 程式用 ORG 100h）" },
            { "segment", "虛擬指令：區段開始" },
            { "ends", "虛擬指令：區段或結構結束" },
            { "assume", "虛擬指令：告訴組譯器各區段暫存器對應哪個區段" },
            { "section", "虛擬指令（NASM）：切換區段（.text 程式、.data 資料、.bss 未初始化資料）" },
            { "global", "虛擬指令：宣告全域符號，讓連結器看得到（例如 _start、main）" },
            { ".globl", "虛擬指令：宣告全域符號，讓連結器看得到" },
            { ".global", "虛擬指令：宣告全域符號，讓連結器看得到" },
            { "extern", "虛擬指令：宣告外部定義的符號（例如 printf）" },
            { "externdef", "虛擬指令：宣告外部符號" },
            { "public", "虛擬指令：把符號公開給其他模組" },
            { "bits", "虛擬指令（NASM）：指定 16／32／64 位元程式碼" },
            { "default", "虛擬指令（NASM）：設定預設定址方式（例如 default rel）" },
            { "times", "虛擬指令（NASM）：把後面的內容重複指定次數" },
            { "align", "虛擬指令：讓下一個資料或指令的位址對齊" },
            { "macro", "虛擬指令：巨集定義開始" },
            { "endm", "虛擬指令：巨集定義結束" },
            { "struct", "虛擬指令：結構定義開始" },
            { "struc", "虛擬指令：結構定義開始" },
            { "label", "虛擬指令：在目前位置定義指定型態的標籤" },
            { "option", "虛擬指令：設定組譯器選項" },
            { "title", "虛擬指令：程式標題（只影響列表檔）" },
            { "exit", "Irvine32 巨集：結束程式（等同 INVOKE ExitProcess, 0）" },
            { ".text", "虛擬指令：程式碼區段開始" },
            { ".section", "虛擬指令：切換到指定區段" },
            { ".bss", "虛擬指令：未初始化資料區段開始" },
            { ".rodata", "虛擬指令：唯讀資料區段開始" },
            { ".align", "虛擬指令：位址對齊" },
            { ".balign", "虛擬指令：以位元組為單位的位址對齊" },
            { ".equ", "虛擬指令：定義常數" },
            { ".set", "虛擬指令：定義常數" },
            { ".include", "虛擬指令：引入其他檔案（AVR 常用 .include \"m328pdef.inc\"）" },
            { ".def", "虛擬指令（AVR）：替暫存器取別名，例如 .def temp = r16" },
            { ".cseg", "虛擬指令（AVR）：程式記憶體（Flash）區段" },
            { ".dseg", "虛擬指令（AVR）：資料記憶體（SRAM）區段" },
            { ".eseg", "虛擬指令（AVR）：EEPROM 區段" },
            { ".org", "虛擬指令：設定起始位址（AVR 中斷向量常用 .org 0x0000）" },
            { ".device", "虛擬指令（AVR）：指定晶片型號" },
            { ".macro", "虛擬指令：巨集定義開始" },
            { ".endm", "虛擬指令：巨集定義結束" },
            { ".thumb", "虛擬指令（ARM）：以下使用 Thumb 指令集" },
            { ".arm", "虛擬指令（ARM）：以下使用 ARM 指令集" },
            { ".syntax", "虛擬指令（ARM）：設定語法（unified 為 UAL 統一語法）" },
            { ".thumb_func", "虛擬指令（ARM）：標記下一個函式為 Thumb 函式" },
            { ".cpu", "虛擬指令：指定 CPU 型號" },
            { ".fpu", "虛擬指令：指定浮點運算單元" },
            { ".type", "虛擬指令：設定符號型態（除錯／連結資訊，可忽略）" },
            { ".size", "虛擬指令：設定符號大小（除錯／連結資訊，可忽略）" },
            { ".intel_syntax", "虛擬指令（GAS）：改用 Intel 語法" },
            { ".att_syntax", "虛擬指令（GAS）：使用 AT&T 語法" },
            { ".option", "虛擬指令（RISC-V）：組譯器選項" },
            { "area", "虛擬指令（ARM Keil）：定義程式或資料區域" },
            { "export", "虛擬指令（ARM Keil）：匯出符號" },
            { "preserve8", "虛擬指令（ARM Keil）：宣告保持 8 位元組堆疊對齊" },
            { "thumb", "虛擬指令（ARM Keil）：使用 Thumb 指令" },
            { "entry", "虛擬指令（ARM Keil）：程式進入點" },
            { "bit", "虛擬指令（8051）：替位元位址取名稱，例如 LED BIT P1.0" },
            { "sbit", "虛擬指令（8051）：定義特殊功能暫存器中的位元" },
            { "data", "虛擬指令（8051）：替內部 RAM 位址取名稱" },
            { "xdata", "虛擬指令（8051）：替外部 RAM 位址取名稱" },
            { "idata", "虛擬指令（8051）：替間接定址 RAM 位址取名稱" },
            { "code", "虛擬指令（8051）：替程式記憶體位址取名稱" },
            { "cseg", "虛擬指令（8051）：程式區段" },
            { "dseg", "虛擬指令（8051）：資料區段" },
            { "using", "虛擬指令（8051）：指定使用的暫存器庫" },
            { "set", "虛擬指令：定義可重新指定的常數" },
            { "dup", "DUP：重複資料" }
        };

        public static bool IsDataDefinition(string mnemonic)
        {
            return !string.IsNullOrEmpty(mnemonic) && DataDefinitions.Contains(mnemonic);
        }

        /// <summary>同時是虛擬指令與指令時，以「名稱 + 虛擬指令」的寫法判斷為虛擬指令。</summary>
        public static bool PreferDirective(string mnemonic, AsmLine line)
        {
            return line.LabelIsName || (mnemonic != null && mnemonic.StartsWith(".", StringComparison.Ordinal));
        }

        public static string Explain(string mnemonic, AsmLine line, AsmArch arch)
        {
            if (string.IsNullOrEmpty(mnemonic)) return null;
            string m = mnemonic.ToLowerInvariant();
            string name = line.LabelIsName ? line.Label : null;

            if (IsDataDefinition(m)) return ExplainData(m, line, name);

            string text;
            if (Texts.TryGetValue(m, out text))
            {
                if (m == "proc" && name != null) return "副程式「" + name + "」開始（" + name + " PROC）";
                if (m == "endp" && name != null) return "副程式「" + name + "」結束";
                if ((m == "equ" || m == "=" || m == "set" || m == "textequ") && name != null)
                    return "定義常數 " + name + " = " + line.OperandText + "（組譯時替換，不佔記憶體）";
                if ((m == "bit" || m == "sbit" || m == "data" || m == "xdata" || m == "idata" || m == "code") && name != null)
                    return "（8051）把 " + line.OperandText + " 取名為 " + name;
                if (m == "end" && line.Operands.Count > 0) return "原始碼結束，程式從「" + line.Operands[0] + "」開始執行";
                if (m == "org" || m == ".org") return text + "；這裡設為 " + line.OperandText;
                if (m == "section" && line.Operands.Count > 0) return "切換到 " + line.Operands[0] + " 區段（" + SectionName(line.Operands[0]) + "）";
                if (m == ".section" && line.Operands.Count > 0) return "切換到 " + line.Operands[0] + " 區段（" + SectionName(line.Operands[0]) + "）";
                if (m == "invoke" && line.Operands.Count > 0)
                {
                    string lib = LibraryCalls.Describe(line.Operands[0]);
                    return text + "：呼叫 " + line.Operands[0] + (lib != null ? "（" + lib + "）" : string.Empty);
                }
                return text;
            }
            if (m.StartsWith(".cfi", StringComparison.Ordinal)) return "除錯用的呼叫框架資訊（編譯器產生，可忽略）";
            if (m.StartsWith(".", StringComparison.Ordinal)) return "組譯器虛擬指令 " + mnemonic;
            return null;
        }

        private static string SectionName(string section)
        {
            string s = section.Trim().ToLowerInvariant();
            if (s.StartsWith(".text", StringComparison.Ordinal)) return "程式碼";
            if (s.StartsWith(".data", StringComparison.Ordinal)) return "已初始化資料";
            if (s.StartsWith(".bss", StringComparison.Ordinal)) return "未初始化資料";
            if (s.StartsWith(".rodata", StringComparison.Ordinal)) return "唯讀資料";
            return "自訂區段";
        }

        private static string ExplainData(string m, AsmLine line, string name)
        {
            string size;
            switch (m)
            {
                case "db": case "byte": case ".byte": case ".db": size = "位元組（8 位元）"; break;
                case "sbyte": size = "有號位元組（8 位元）"; break;
                case "dw": case "word": case ".word": case ".short": case ".half": case ".hword": case ".dw": size = m == ".word" ? "字組（ARM/MIPS/RISC-V 為 32 位元，x86 GAS 為 16 位元）" : "字組（16 位元）"; break;
                case "sword": size = "有號字組（16 位元）"; break;
                case "dd": case "dword": case ".long": case ".int": case ".dword": size = m == ".dword" ? "雙字組（RISC-V 為 64 位元）" : "雙字組（32 位元）"; break;
                case "sdword": size = "有號雙字組（32 位元）"; break;
                case "real4": case ".float": size = "單精度浮點數（32 位元）"; break;
                case "dq": case "qword": case ".quad": case ".xword": size = "四字組（64 位元）"; break;
                case "real8": case ".double": size = "倍精度浮點數（64 位元）"; break;
                case "dt": case "tbyte": case "real10": size = "10 位元組（80 位元）"; break;
                case "resb": return "保留 " + line.OperandText + " 個未初始化的位元組" + (name != null ? "，名稱為 " + name : string.Empty);
                case "resw": return "保留 " + line.OperandText + " 個未初始化的字組（16 位元）" + (name != null ? "，名稱為 " + name : string.Empty);
                case "resd": return "保留 " + line.OperandText + " 個未初始化的雙字組（32 位元）" + (name != null ? "，名稱為 " + name : string.Empty);
                case "resq": return "保留 " + line.OperandText + " 個未初始化的四字組（64 位元）" + (name != null ? "，名稱為 " + name : string.Empty);
                case "ds": return "保留 " + line.OperandText + " 個位元組的空間" + (name != null ? "，名稱為 " + name : string.Empty);
                case ".space": case ".skip": case ".zero": case ".fill": return "保留 " + line.OperandText + " 個位元組的空間" + (name != null ? "，名稱為 " + name : string.Empty);
                case ".ascii": return "定義字串 " + line.OperandText + "（結尾不會自動補 0）" + (name != null ? "，名稱為 " + name : string.Empty);
                case ".asciz": case ".asciiz": case ".string": return "定義字串 " + line.OperandText + "（結尾自動補 0）" + (name != null ? "，名稱為 " + name : string.Empty);
                default: size = "資料"; break;
            }

            string label = name ?? line.Label;
            string text = "定義" + size + "資料" + (label != null ? "「" + label + "」" : string.Empty) + "，初始值：" + (line.OperandText.Length > 0 ? line.OperandText : "（無）");
            string ops = line.OperandText;
            if (ops.IndexOf("dup", StringComparison.OrdinalIgnoreCase) >= 0) text += "；DUP 代表重複，例如 10 DUP(0) 是 10 個 0";
            if (ops.Trim() == "?") text += "；? 代表不指定初始值";
            if (ops.IndexOf("'$'", StringComparison.Ordinal) >= 0 || ops.TrimEnd().EndsWith("$'", StringComparison.Ordinal) || ops.TrimEnd().EndsWith("$\"", StringComparison.Ordinal))
                text += "；字串以 '$' 結尾，供 DOS INT 21h／AH=09h 顯示";
            else if (Contains0Terminator(ops)) text += "；最後的 0 是字串結尾標記（以 0 結尾的字串）";
            if (ops.IndexOf("0dh", StringComparison.OrdinalIgnoreCase) >= 0 || ops.IndexOf("13,", StringComparison.Ordinal) >= 0)
                text += "；0Dh、0Ah（13、10）是換行字元 CR、LF";
            return text;
        }

        private static bool Contains0Terminator(string ops)
        {
            string t = ops.Replace(" ", string.Empty);
            return (t.IndexOf('\'') >= 0 || t.IndexOf('"') >= 0) && (t.EndsWith(",0", StringComparison.Ordinal) || t.EndsWith(",0h", StringComparison.OrdinalIgnoreCase));
        }
    }
}
