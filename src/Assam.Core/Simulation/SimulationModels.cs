using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Assam.Core.Text;

namespace Assam.Core.Simulation
{
    public sealed class SimulationOptions
    {
        public SimulationOptions()
        {
            MaxSteps = 2000;
            InitialRegisters = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            Input = string.Empty;
        }

        /// <summary>最多執行幾個步驟（避免無窮迴圈）。</summary>
        public int MaxSteps { get; set; }

        /// <summary>執行前預先設定的暫存器值，例如 EAX=5。</summary>
        public Dictionary<string, ulong> InitialRegisters { get; private set; }

        /// <summary>模擬鍵盤輸入（ReadInt、ReadChar、INT 21h/01h 會從這裡讀）。</summary>
        public string Input { get; set; }

        private static readonly Regex AssignRegex = new Regex(@"([A-Za-z][A-Za-z0-9]*)\s*[=:]\s*(-?[0-9A-Za-z'$]+)", RegexOptions.Compiled);

        /// <summary>解析「EAX=5, EBX=0x10; ECX = 3」這類的初始值設定。</summary>
        public static Dictionary<string, ulong> ParseRegisterAssignments(string text, List<string> errors)
        {
            var map = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return map;
            foreach (Match m in AssignRegex.Matches(text))
            {
                long v;
                if (AsmNumber.TryParse(m.Groups[2].Value, out v)) map[m.Groups[1].Value] = unchecked((ulong)v);
                else if (errors != null) errors.Add("無法解析 " + m.Value);
            }
            return map;
        }
    }

    public sealed class TraceStep
    {
        public TraceStep()
        {
            Registers = new List<KeyValuePair<string, string>>();
        }

        public int Step { get; set; }
        public int LineNumber { get; set; }
        public string Source { get; set; }
        /// <summary>這一步改變了什麼（暫存器、旗標、記憶體）。</summary>
        public string Changes { get; set; }
        /// <summary>這一步是怎麼算出來的。</summary>
        public string Derivation { get; set; }
        public List<KeyValuePair<string, string>> Registers { get; private set; }
        public string Flags { get; set; }
    }

    public sealed class SimulationResult
    {
        public SimulationResult()
        {
            Steps = new List<TraceStep>();
            Messages = new List<string>();
            FinalRegisters = new List<KeyValuePair<string, string>>();
            DataDump = new List<string>();
        }

        public List<TraceStep> Steps { get; private set; }
        public string Output { get; set; }
        public List<string> Messages { get; private set; }
        public string Error { get; set; }
        public string StopReason { get; set; }
        public int Bits { get; set; }
        public List<KeyValuePair<string, string>> FinalRegisters { get; private set; }
        public string FinalFlags { get; set; }
        public List<string> DataDump { get; private set; }

        public bool Success
        {
            get { return Error == null; }
        }

        public string ToText(int maxSteps = 60)
        {
            var sb = new StringBuilder();
            sb.Append("【模擬模式】").Append(Bits).Append(" 位元 x86（Intel 語法）\r\n");
            sb.Append("【執行追蹤】共 ").Append(Steps.Count).Append(" 步\r\n");
            int shown = 0;
            foreach (var s in Steps)
            {
                if (shown++ >= maxSteps)
                {
                    sb.Append("……（之後還有 ").Append(Steps.Count - maxSteps).Append(" 步，請到「推導模擬」頁查看完整追蹤）\r\n");
                    break;
                }
                sb.Append("步驟 ").Append(s.Step.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append("（第 ").Append(s.LineNumber).Append(" 行）").Append(s.Source).Append("\r\n");
                if (!string.IsNullOrEmpty(s.Derivation))
                    foreach (var line in s.Derivation.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                        sb.Append("      推導：").Append(line).Append("\r\n");
                if (!string.IsNullOrEmpty(s.Changes)) sb.Append("      變化：").Append(s.Changes).Append("\r\n");
            }
            sb.Append("\r\n【結束原因】").Append(StopReason ?? "-").Append("\r\n");
            if (Error != null) sb.Append("【錯誤】").Append(Error).Append("\r\n");
            sb.Append("【最後的暫存器】").Append(string.Join("  ", FinalRegisters.Select(kv => kv.Key + "=" + kv.Value))).Append("\r\n");
            sb.Append("【最後的旗標】").Append(FinalFlags).Append("\r\n");
            if (DataDump.Count > 0)
            {
                sb.Append("【資料區】\r\n");
                foreach (var d in DataDump) sb.Append("  ").Append(d).Append("\r\n");
            }
            if (!string.IsNullOrEmpty(Output)) sb.Append("【程式輸出】\r\n").Append(Output).Append("\r\n");
            if (Messages.Count > 0)
            {
                sb.Append("【模擬器訊息】\r\n");
                foreach (var m in Messages) sb.Append("・").Append(m).Append("\r\n");
            }
            return sb.ToString();
        }
    }

    /// <summary>模擬中發生的錯誤（例如除以 0、不支援的指令）。</summary>
    public sealed class SimulationException : Exception
    {
        public SimulationException(string message) : base(message)
        {
        }
    }
}
