using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Assam.Core.Binary
{
    public sealed class DisassemblyOptions
    {
        public DisassemblyOptions()
        {
            MaxInstructions = 200000;
            MaxFunctions = 20000;
        }

        /// <summary>整個檔案最多解碼幾條指令（避免大檔案在低階電腦上卡住）。</summary>
        public int MaxInstructions { get; set; }

        public int MaxFunctions { get; set; }

        /// <summary>除了從進入點／匯出追蹤之外，也把程式區段中沒被追蹤到的部分以線性方式掃描（找出更多函式）。</summary>
        public bool ScanUnreachedCode { get; set; }
    }

    /// <summary>反組譯清單中的一行。</summary>
    public sealed class AsmListingLine
    {
        public ulong Address { get; set; }

        /// <summary>這一行之前要顯示的標籤（例如 "sub_401000"、"loc_401020"、"main"），沒有則為 null。</summary>
        public string Label { get; set; }

        public DecodedInstruction Instruction { get; set; }

        /// <summary>助憶碼（含前綴），例如 "rep movsb"。</summary>
        public string Mnemonic { get; set; }

        /// <summary>符號化後的運算元，例如 "dword ptr [__imp_ExitProcess]"、"loc_401020"、"0x403000"。</summary>
        public string Operands { get; set; }

        /// <summary>註解（不含分號），例如 "KERNEL32.dll!ExitProcess"、"\"Hello\""、"迴圈"。</summary>
        public string Comment { get; set; }

        public string HexBytes
        {
            get { return Instruction != null ? Instruction.HexBytes : string.Empty; }
        }

        public string Code
        {
            get { return string.IsNullOrEmpty(Operands) ? Mnemonic : Mnemonic + " " + Operands; }
        }

        /// <summary>清單格式：位址、機器碼、指令、註解。</summary>
        public string ToListingText(bool showBytes, int addressDigits)
        {
            var sb = new StringBuilder();
            sb.Append(Address.ToString("X" + addressDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)).Append("  ");
            if (showBytes)
            {
                string hex = HexBytes;
                if (hex.Length > 23) hex = hex.Substring(0, 20) + "...";
                sb.Append(hex.PadRight(24));
            }
            sb.Append(Code);
            if (!string.IsNullOrEmpty(Comment))
            {
                int pad = Math.Max(1, 40 - Code.Length);
                sb.Append(' ', pad).Append("; ").Append(Comment);
            }
            return sb.ToString();
        }

        /// <summary>原始碼格式（給程式碼分析與推導模擬用）：不含位址與機器碼。</summary>
        public string ToSourceText()
        {
            string code = "    " + Code;
            if (!string.IsNullOrEmpty(Comment)) code = code.PadRight(44) + " ; " + Comment;
            return code;
        }
    }

    /// <summary>反組譯找到的一個函式。</summary>
    public sealed class DisassembledFunction
    {
        public DisassembledFunction()
        {
            Lines = new List<AsmListingLine>();
            Callees = new List<ulong>();
            ImportsUsed = new List<string>();
            StringsReferenced = new List<string>();
        }

        public ulong Start { get; set; }

        /// <summary>函式最後一條指令之後的位址。</summary>
        public ulong End { get; set; }

        /// <summary>名稱：匯出名、"entry"（進入點）、或 "sub_401000"。</summary>
        public string Name { get; set; }

        /// <summary>來源說明："進入點"、"匯出函式"、"被呼叫"、"線性掃描"、"使用者指定"。</summary>
        public string Origin { get; set; }

        public List<AsmListingLine> Lines { get; private set; }

        /// <summary>這個函式呼叫的其他（檔案內）函式起始位址。</summary>
        public List<ulong> Callees { get; private set; }

        /// <summary>這個函式呼叫的匯入函式，例如 "KERNEL32.dll!ExitProcess"。</summary>
        public List<string> ImportsUsed { get; private set; }

        /// <summary>這個函式引用到的字串內容。</summary>
        public List<string> StringsReferenced { get; private set; }

        public int InstructionCount
        {
            get { return Lines.Count; }
        }

        public override string ToString()
        {
            return Name + "  (0x" + Start.ToString("X", CultureInfo.InvariantCulture) + "，" + Lines.Count + " 行)";
        }
    }

    /// <summary>整個檔案的反組譯結果。</summary>
    public sealed class DisassemblyResult
    {
        public DisassemblyResult()
        {
            Functions = new List<DisassembledFunction>();
            Symbols = new Dictionary<ulong, string>();
            Messages = new List<string>();
        }

        public LoadedBinary Binary { get; set; }

        /// <summary>依起始位址排序的函式。</summary>
        public List<DisassembledFunction> Functions { get; private set; }

        /// <summary>位址 → 名稱（函式、標籤、匯入槽位、匯出、字串）。</summary>
        public Dictionary<ulong, string> Symbols { get; private set; }

        public List<string> Messages { get; private set; }
        public int TotalInstructions { get; set; }

        /// <summary>是否因為達到上限而沒有全部反組譯。</summary>
        public bool Truncated { get; set; }

        /// <summary>位址欄的十六進位位數（16 位元 4 位、32 位元 8 位、64 位元 16 位）。</summary>
        public int AddressDigits
        {
            get
            {
                if (Binary == null) return 8;
                return Binary.Bits == 64 ? 16 : Binary.Bits == 16 ? 5 : 8;
            }
        }

        public DisassembledFunction FindFunction(ulong address)
        {
            return Functions.FirstOrDefault(f => address >= f.Start && address < Math.Max(f.End, f.Start + 1));
        }
    }
}
