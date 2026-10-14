using System;

namespace Assam.Core.Binary
{
    /// <summary>
    /// x86／x64 機器碼解碼器（16、32、64 位元模式），輸出 Intel 語法（格式與 objdump -M intel 一致，小寫）。
    /// 純軟體表格解碼，不需要網路或任何外部函式庫。
    /// </summary>
    public sealed class X86Decoder
    {
        public X86Decoder(int bits)
        {
            if (bits != 16 && bits != 32 && bits != 64) throw new ArgumentException("bits 必須是 16、32 或 64", "bits");
            Bits = bits;
        }

        public int Bits { get; private set; }

        /// <summary>
        /// 從 code[offset] 解碼一條指令（address 是該指令的虛擬位址）。不會丟出例外：
        /// 無法解碼時回傳 IsValid = false、Length = 1、Mnemonic = "(bad)"、Flow = Invalid。
        /// </summary>
        public DecodedInstruction Decode(byte[] code, int offset, ulong address)
        {
            throw new NotImplementedException();
        }
    }
}
