using System;

namespace Assam.Core.Binary
{
    /// <summary>.NET 組件的 IL（中間語言）反組譯器。只讀取檔案，不會載入或執行組件。</summary>
    public static class CilDisassembler
    {
        public static CilDisassemblyResult Disassemble(LoadedBinary binary, int maxMethods = 5000)
        {
            throw new NotImplementedException();
        }
    }
}
