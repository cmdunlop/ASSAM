using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Assam.Core.Knowledge
{
    /// <summary>
    /// 知識檔格式（.md，UTF-8）：
    /// <code>
    /// ## 標題
    /// id: x86-mov
    /// category: 概念        (概念/指令/做法/推導/範例/除錯)
    /// arch: x86             (x86/8051/ARM/MIPS/RISC-V/AVR/通用)
    /// tags: 標籤1, 標籤2
    /// ---
    /// 內文……（直到下一個以 "## " 開頭的行；``` 程式碼區塊內的 ## 不會被當成新條目）
    /// </code>
    /// </summary>
    public static class KnowledgeParser
    {
        public static List<KnowledgeEntry> Parse(string text, string source)
        {
            var result = new List<KnowledgeEntry>();
            if (string.IsNullOrEmpty(text)) return result;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            KnowledgeEntry current = null;
            bool inMeta = false;
            bool inFence = false;
            var body = new StringBuilder();
            int index = 0;

            Action flush = () =>
            {
                if (current == null) return;
                current.Body = body.ToString().Trim('\n', ' ');
                if (string.IsNullOrEmpty(current.Id))
                    current.Id = MakeId(source, index);
                if (string.IsNullOrEmpty(current.Category)) current.Category = KnowledgeEntry.CategoryConcept;
                if (string.IsNullOrEmpty(current.Arch)) current.Arch = "通用";
                result.Add(current);
                current = null;
                body.Clear();
            };

            foreach (var rawLine in lines)
            {
                string line = rawLine.TrimEnd();
                if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
                {
                    flush();
                    index++;
                    current = new KnowledgeEntry { Title = line.Substring(3).Trim(), Source = source };
                    inMeta = true;
                    continue;
                }
                if (current == null) continue; // 檔案開頭的說明文字

                if (inMeta)
                {
                    if (line.Trim() == "---") { inMeta = false; continue; }
                    if (line.Trim().Length == 0) continue;
                    int colon = line.IndexOf(':');
                    if (colon > 0 && TryApplyMeta(current, line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim()))
                        continue;
                    // 沒有 --- 分隔時，第一行非中繼資料就視為內文開始
                    inMeta = false;
                }

                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
                body.Append(rawLine.TrimEnd()).Append('\n');
            }
            flush();
            return result;
        }

        private static bool TryApplyMeta(KnowledgeEntry entry, string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "id":
                case "編號":
                    entry.Id = value;
                    return true;
                case "category":
                case "類型":
                case "分類":
                    entry.Category = value;
                    return true;
                case "arch":
                case "架構":
                    entry.Arch = value;
                    return true;
                case "tags":
                case "標籤":
                    entry.Tags = SplitTags(value);
                    return true;
                default:
                    return false;
            }
        }

        public static List<string> SplitTags(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new List<string>();
            return value.Split(new[] { ',', '，', '、', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => t.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
        }

        private static string MakeId(string source, int index)
        {
            string baseName = string.IsNullOrEmpty(source) ? "entry" : source;
            var sb = new StringBuilder();
            foreach (char c in baseName)
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
            return sb.ToString().Trim('-') + "-" + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>把條目寫回相同格式（匯出用）。</summary>
        public static string Serialize(IEnumerable<KnowledgeEntry> entries)
        {
            var sb = new StringBuilder();
            sb.Append("# ASSAM 知識檔（可直接編輯，放到 UserData\\Knowledge 資料夾會自動載入）\n\n");
            foreach (var e in entries)
            {
                sb.Append("## ").Append(e.Title).Append('\n');
                sb.Append("id: ").Append(e.Id).Append('\n');
                sb.Append("category: ").Append(e.Category).Append('\n');
                sb.Append("arch: ").Append(e.Arch).Append('\n');
                sb.Append("tags: ").Append(e.TagText).Append('\n');
                sb.Append("---\n");
                sb.Append((e.Body ?? string.Empty).Replace("\r\n", "\n")).Append("\n\n");
            }
            return sb.ToString();
        }
    }
}
