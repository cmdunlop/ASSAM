using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Assam.Core.Binary
{
    public enum OperandKind
    {
        None,
        Register,
        Immediate,
        Memory,
        /// <summary>相對跳躍／呼叫的目標（已換算成絕對位址）。</summary>
        RelativeBranch,
        /// <summary>遠程指標 seg:offset（JMP FAR/CALL FAR ptr16:16/32）。</summary>
        FarPointer
    }

    /// <summary>指令對執行流程的影響（反組譯器用來追蹤函式與跳躍）。</summary>
    public enum InstructionFlow
    {
        /// <summary>執行完接著下一行。</summary>
        Sequential,
        /// <summary>無條件跳躍（JMP）。</summary>
        Jump,
        /// <summary>條件跳躍（Jcc、LOOP、JECXZ…），可能跳也可能繼續。</summary>
        ConditionalJump,
        /// <summary>呼叫副程式（CALL），返回後繼續下一行。</summary>
        Call,
        /// <summary>返回（RET/IRET）。</summary>
        Return,
        /// <summary>不會繼續往下執行（HLT、UD2、INT3 填充等）。</summary>
        Stop,
        /// <summary>無法解碼的位元組。</summary>
        Invalid
    }

    /// <summary>一個解碼後的運算元。</summary>
    public sealed class DecodedOperand
    {
        public OperandKind Kind { get; set; }

        /// <summary>Intel 語法文字（小寫），格式與 objdump -M intel 相同，例如 "dword ptr [ebp-0x4]"、"0x10"、"eax"。</summary>
        public string Text { get; set; }

        /// <summary>運算元大小（位元）；0 表示不適用。</summary>
        public int SizeBits { get; set; }

        /// <summary>Kind = Register 時的暫存器名稱（小寫）。</summary>
        public string Register { get; set; }

        /// <summary>Kind = Immediate 時的值（依運算元大小做過符號延伸）。</summary>
        public long Immediate { get; set; }

        // Kind = Memory 的組成
        public string Segment { get; set; }
        public string BaseRegister { get; set; }
        public string IndexRegister { get; set; }
        public int Scale { get; set; }
        public long Displacement { get; set; }
        public bool HasDisplacement { get; set; }
        public bool RipRelative { get; set; }

        /// <summary>
        /// 能直接算出的絕對位址：RIP 相對定址的目標、沒有基底與索引暫存器的位移（[0x403000]）、
        /// 相對跳躍／呼叫的目標。其他情況為 null。反組譯器用它來標註匯入函式、字串、全域變數。
        /// </summary>
        public ulong? AbsoluteAddress { get; set; }

        public override string ToString()
        {
            return Text;
        }
    }

    /// <summary>一條解碼後的 x86/x64 指令。</summary>
    public sealed class DecodedInstruction
    {
        public DecodedInstruction()
        {
            Operands = new List<DecodedOperand>();
            Bytes = new byte[0];
        }

        public ulong Address { get; set; }
        public int Length { get; set; }
        public byte[] Bytes { get; set; }

        /// <summary>"rep"、"repe"、"repne"、"lock"（或其組合，例如 "lock"）；沒有時為 null。</summary>
        public string Prefix { get; set; }

        /// <summary>小寫助憶碼，例如 "mov"；無法解碼時為 "(bad)"。</summary>
        public string Mnemonic { get; set; }

        public List<DecodedOperand> Operands { get; private set; }
        public bool IsValid { get; set; }
        public InstructionFlow Flow { get; set; }

        /// <summary>相對跳躍／呼叫／LOOP 的目標位址；間接跳躍（jmp eax、call [mem]）為 null。</summary>
        public ulong? BranchTarget { get; set; }

        public string OperandText
        {
            get { return string.Join(", ", Operands.Select(o => o.Text)); }
        }

        /// <summary>完整的 Intel 語法文字，例如 "rep movsb"、"mov eax, dword ptr [ebp-0x4]"。</summary>
        public string Text
        {
            get
            {
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(Prefix)) sb.Append(Prefix).Append(' ');
                sb.Append(Mnemonic);
                if (Operands.Count > 0) sb.Append(' ').Append(OperandText);
                return sb.ToString();
            }
        }

        /// <summary>機器碼的十六進位，例如 "55 8b ec"。</summary>
        public string HexBytes
        {
            get { return string.Join(" ", Bytes.Select(b => b.ToString("x2"))); }
        }

        public override string ToString()
        {
            return Address.ToString("x") + ": " + Text;
        }
    }
}
