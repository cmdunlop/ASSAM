using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;
using Assam.Core.Knowledge;

namespace Assam.Core.Learning
{
    /// <summary>
    /// 使用者的「學習記憶」，全部存在本機 UserData\memory.json（JSON 純文字，可備份、可攜帶）。
    /// </summary>
    [DataContract]
    public sealed class UserMemoryData
    {
        public const int CurrentVersion = 1;

        [DataMember(Order = 1)] public int Version { get; set; }
        [DataMember(Order = 2)] public string CreatedAt { get; set; }
        [DataMember(Order = 3)] public string UpdatedAt { get; set; }

        /// <summary>查詢詞 → (知識條目 → 學到的權重)。由「有幫助／沒幫助」回饋累積。</summary>
        [DataMember(Order = 4)] public Dictionary<string, Dictionary<string, double>> TermEntryWeights { get; set; }

        /// <summary>正規化後的整句問題 → 使用者確認的最佳答案條目。</summary>
        [DataMember(Order = 5)] public Dictionary<string, string> QueryAnswers { get; set; }

        /// <summary>使用者親自教的知識。</summary>
        [DataMember(Order = 6)] public List<KnowledgeEntry> TaughtEntries { get; set; }

        /// <summary>架構辨識模型學到的權重修正（架構 → 特徵 → 權重差）。</summary>
        [DataMember(Order = 7)] public Dictionary<string, Dictionary<string, double>> ArchWeightDeltas { get; set; }

        [DataMember(Order = 8)] public List<LabeledSample> ArchSamples { get; set; }

        /// <summary>使用者更正過的意圖（問題類型）樣本，用來再訓練單純貝氏分類器。</summary>
        [DataMember(Order = 9)] public List<LabeledSample> IntentSamples { get; set; }

        [DataMember(Order = 10)] public List<HistoryItem> History { get; set; }

        /// <summary>使用者對指令的個人筆記，例如 "x86:LOOP" → "記得先設定 ECX"。</summary>
        [DataMember(Order = 11)] public Dictionary<string, string> InstructionNotes { get; set; }

        [DataMember(Order = 12)] public Dictionary<string, int> Counters { get; set; }

        public static UserMemoryData CreateNew()
        {
            var data = new UserMemoryData();
            data.EnsureInitialized();
            return data;
        }

        /// <summary>反序列化不會執行建構子，所以載入後要補上空集合。</summary>
        public void EnsureInitialized()
        {
            if (Version == 0) Version = CurrentVersion;
            if (string.IsNullOrEmpty(CreatedAt)) CreatedAt = Now();
            if (TermEntryWeights == null) TermEntryWeights = new Dictionary<string, Dictionary<string, double>>();
            if (QueryAnswers == null) QueryAnswers = new Dictionary<string, string>();
            if (TaughtEntries == null) TaughtEntries = new List<KnowledgeEntry>();
            if (ArchWeightDeltas == null) ArchWeightDeltas = new Dictionary<string, Dictionary<string, double>>();
            if (ArchSamples == null) ArchSamples = new List<LabeledSample>();
            if (IntentSamples == null) IntentSamples = new List<LabeledSample>();
            if (History == null) History = new List<HistoryItem>();
            if (InstructionNotes == null) InstructionNotes = new Dictionary<string, string>();
            if (Counters == null) Counters = new Dictionary<string, int>();
            foreach (var e in TaughtEntries)
                if (e.Tags == null) e.Tags = new List<string>();
        }

        public int GetCounter(string name)
        {
            int v;
            return Counters != null && Counters.TryGetValue(name, out v) ? v : 0;
        }

        public void Increment(string name, int delta = 1)
        {
            Counters[name] = GetCounter(name) + delta;
        }

        public int LearnedTermWeightCount
        {
            get
            {
                int n = 0;
                if (TermEntryWeights != null)
                    foreach (var kv in TermEntryWeights) n += kv.Value.Count;
                return n;
            }
        }

        public static string Now()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }

    [DataContract]
    public sealed class LabeledSample
    {
        [DataMember(Order = 1)] public string Text { get; set; }
        [DataMember(Order = 2)] public string Label { get; set; }
        [DataMember(Order = 3)] public string CreatedAt { get; set; }
    }

    [DataContract]
    public sealed class HistoryItem
    {
        [DataMember(Order = 1)] public string Time { get; set; }
        [DataMember(Order = 2)] public string Question { get; set; }
        [DataMember(Order = 3)] public string Intent { get; set; }
        [DataMember(Order = 4)] public string TopEntryId { get; set; }
        [DataMember(Order = 5)] public string TopTitle { get; set; }
        /// <summary>1 = 有幫助，-1 = 沒幫助，0 = 未回饋。</summary>
        [DataMember(Order = 6)] public int Feedback { get; set; }

        public override string ToString()
        {
            string mark = Feedback > 0 ? "（有幫助）" : Feedback < 0 ? "（沒幫助）" : string.Empty;
            return Time + "  " + Question + mark;
        }
    }
}
