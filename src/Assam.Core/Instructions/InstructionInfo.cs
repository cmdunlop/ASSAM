using System;
using System.Collections.Generic;
using System.Globalization;

namespace Assam.Core.Instructions
{
    /// <summary>一個指令（或一組同義助憶碼，例如 JE/JZ）的說明資料。</summary>
    public sealed class InstructionInfo
    {
        public string ArchKey { get; set; }
        public string[] Mnemonics { get; set; }
        public string Category { get; set; }
        public string Flags { get; set; }
        public string Syntax { get; set; }
        public string Description { get; set; }

        /// <summary>
        /// 動作樣板，例如 "{0} ← {0} + {1}"。可依運算元數量提供多個版本："2:{0} ← {0} + {1} || 3:{0} ← {1} + {2}"。
        /// </summary>
        public string ActionTemplate { get; set; }

        public string PrimaryMnemonic
        {
            get { return Mnemonics != null && Mnemonics.Length > 0 ? Mnemonics[0] : string.Empty; }
        }

        public string MnemonicText
        {
            get { return Mnemonics == null ? string.Empty : string.Join(" / ", Mnemonics); }
        }

        /// <summary>依運算元產生「動作」虛擬碼；沒有樣板時回傳 null。</summary>
        public string FormatAction(IList<string> operands)
        {
            if (string.IsNullOrWhiteSpace(ActionTemplate)) return null;
            int count = operands == null ? 0 : operands.Count;

            string chosen = null;
            string fallback = null;
            foreach (var part in ActionTemplate.Split(new[] { "||" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                int colon = p.IndexOf(':');
                int n;
                if (colon > 0 && colon <= 2 && int.TryParse(p.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                {
                    if (n == count) { chosen = p.Substring(colon + 1).Trim(); break; }
                }
                else if (fallback == null)
                {
                    fallback = p;
                }
            }
            string template = chosen ?? fallback;
            if (template == null) return null;

            string result = template;
            for (int i = 0; i < 4; i++)
            {
                string token = "{" + i.ToString(CultureInfo.InvariantCulture) + "}";
                if (result.IndexOf(token, StringComparison.Ordinal) < 0) continue;
                if (i >= count) return null; // 樣板需要的運算元不存在
                result = result.Replace(token, operands[i]);
            }
            return result;
        }
    }
}
