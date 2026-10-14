using System;
using System.ComponentModel;
using System.Windows.Forms;
using Assam.Core.Assistant;

namespace Assam.App.Pages
{
    /// <summary>反組譯：載入 EXE／DLL（Windows PE、.NET、DOS MZ/COM），顯示檔頭、區段、匯入匯出、函式的組合語言與 .NET IL。</summary>
    [DesignerCategory("Code")]
    public sealed class DisassemblyPage : UserControl
    {
        public DisassemblyPage(AsmAssistant assistant, Action<string> status)
        {
        }

        /// <summary>把選取函式的組合語言送到「程式碼分析」頁。</summary>
        public event Action<string> SendToAnalysis;

        /// <summary>把選取函式的組合語言送到「推導模擬」頁。</summary>
        public event Action<string> SendToSimulation;
    }
}
