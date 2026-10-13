using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Assam.Core.Knowledge
{
    /// <summary>知識庫中的一張「知識卡」。</summary>
    [DataContract]
    public sealed class KnowledgeEntry
    {
        public const string CategoryConcept = "概念";
        public const string CategoryInstruction = "指令";
        public const string CategoryHowTo = "做法";
        public const string CategoryDerivation = "推導";
        public const string CategoryExample = "範例";
        public const string CategoryDebug = "除錯";
        public const string CategoryUser = "使用者教學";

        public KnowledgeEntry()
        {
            Tags = new List<string>();
        }

        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public string Title { get; set; }
        [DataMember(Order = 3)] public string Category { get; set; }
        [DataMember(Order = 4)] public string Arch { get; set; }
        [DataMember(Order = 5)] public List<string> Tags { get; set; }
        [DataMember(Order = 6)] public string Body { get; set; }
        [DataMember(Order = 7)] public string Source { get; set; }
        [DataMember(Order = 8)] public bool IsUserEntry { get; set; }
        [DataMember(Order = 9)] public string CreatedAt { get; set; }

        public string TagText
        {
            get { return Tags == null ? string.Empty : string.Join(", ", Tags); }
        }

        public string ToDisplayText()
        {
            var header = "【" + Title + "】";
            var meta = "分類：" + (Category ?? "-") + "　架構：" + (Arch ?? "通用");
            if (Tags != null && Tags.Count > 0) meta += "　標籤：" + TagText;
            if (!string.IsNullOrEmpty(Source)) meta += "　來源：" + Source;
            return header + "\r\n" + meta + "\r\n\r\n" + (Body ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        public override string ToString()
        {
            return Title ?? Id ?? base.ToString();
        }
    }
}
