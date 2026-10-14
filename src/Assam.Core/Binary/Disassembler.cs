using System;
using System.Collections.Generic;

namespace Assam.Core.Binary
{
    /// <summary>
    /// 反組譯器：從進入點與匯出函式開始追蹤（遞迴下降），找出函式並產生符號化的組合語言清單。
    /// </summary>
    public sealed class Disassembler
    {
        /// <summary>反組譯整個檔案。binary.Bits 為 0（非 x86）時回傳空結果並在 Messages 說明。</summary>
        public DisassemblyResult Disassemble(LoadedBinary binary, DisassemblyOptions options = null)
        {
            throw new NotImplementedException();
        }

        /// <summary>從指定位址線性反組譯最多 maxInstructions 條指令（「前往位址」功能）。context 用來符號化，可為 null。</summary>
        public List<AsmListingLine> DisassembleRange(LoadedBinary binary, DisassemblyResult context, ulong address, int maxInstructions)
        {
            throw new NotImplementedException();
        }

        /// <summary>產生函式的清單文字（含位址；showBytes 決定是否顯示機器碼）。function 為 null 時輸出全部函式。</summary>
        public string ToListing(DisassemblyResult result, DisassembledFunction function, bool showBytes)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// 產生可以貼到「程式碼分析」與「推導模擬」的組合語言原始碼（Intel 語法、標籤獨立一行、沒有位址）。
        /// includeCallees 為 true 時，一併附上它呼叫的檔案內函式（遞迴，最多 50 個）。
        /// </summary>
        public string FunctionSource(DisassemblyResult result, DisassembledFunction function, bool includeCallees)
        {
            throw new NotImplementedException();
        }
    }
}
