using System;
using System.Collections.Generic;
using System.Linq;
using Assam.Core.Knowledge;
using Assam.Core.Learning;
using System.Text.RegularExpressions;
using Assam.Core.Analysis;
using Assam.Core.Instructions;
using Assam.Core.Text;

namespace Assam.Core.Search
{
    public sealed class SearchHit
    {
        public KnowledgeEntry Entry { get; set; }
        public double Score { get; set; }
        public double TextScore { get; set; }
        public double LearnedScore { get; set; }

        /// <summary>是否因為使用者過去的回饋而提升排名。</summary>
        public bool BoostedByMemory
        {
            get { return LearnedScore > 0.05; }
        }

        public override string ToString()
        {
            return (BoostedByMemory ? "★ " : string.Empty) + Entry.Title + "（" + Entry.Arch + "／" + Entry.Category + "）";
        }
    }

    /// <summary>
    /// 本機全文檢索：BM25 排名 + 同義詞擴展 + 架構偏好 + 使用者回饋學到的權重（相關回饋學習）。
    /// </summary>
    public sealed class SearchEngine
    {
        private const double K1 = 1.2;
        private const double B = 0.75;
        private const double TitleWeight = 3.0;
        private const double TagWeight = 2.0;

        private readonly KnowledgeBase _kb;
        private readonly Func<UserMemoryData> _memory;
        private int _indexedVersion = -1;

        private List<Dictionary<string, double>> _docTerms = new List<Dictionary<string, double>>();
        private List<double> _docLengths = new List<double>();
        private List<KnowledgeEntry> _docs = new List<KnowledgeEntry>();
        /// <summary>指令卡的助憶碼（小寫），用於「直接問指令名稱」時加分。</summary>
        private List<HashSet<string>> _docMnemonics = new List<HashSet<string>>();
        private Dictionary<string, int> _docFreq = new Dictionary<string, int>(StringComparer.Ordinal);
        private double _avgLength = 1;

        public SearchEngine(KnowledgeBase kb, Func<UserMemoryData> memory)
        {
            _kb = kb;
            _memory = memory;
        }

        public void RebuildIfNeeded()
        {
            if (_indexedVersion == _kb.Version) return;
            Rebuild();
        }

        public void Rebuild()
        {
            var docTerms = new List<Dictionary<string, double>>();
            var lengths = new List<double>();
            var docs = new List<KnowledgeEntry>();
            var mnemonics = new List<HashSet<string>>();
            var df = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var e in _kb.Entries)
            {
                var tf = new Dictionary<string, double>(StringComparer.Ordinal);
                AddTokens(tf, e.Title, TitleWeight);
                if (e.Tags != null) AddTokens(tf, string.Join(" ", e.Tags), TagWeight);
                AddTokens(tf, e.Arch + " " + e.Category, 1.0);
                AddTokens(tf, e.Body, 1.0);

                double len = tf.Values.Sum();
                docTerms.Add(tf);
                lengths.Add(len);
                docs.Add(e);
                mnemonics.Add(MnemonicsOf(e));
                foreach (var t in tf.Keys)
                {
                    int c;
                    df.TryGetValue(t, out c);
                    df[t] = c + 1;
                }
            }

            _docTerms = docTerms;
            _docLengths = lengths;
            _docs = docs;
            _docMnemonics = mnemonics;
            _docFreq = df;
            _avgLength = lengths.Count == 0 ? 1 : Math.Max(1, lengths.Average());
            _indexedVersion = _kb.Version;
        }

        /// <summary>
        /// 條目「主打」的指令：指令卡取其助憶碼標籤；其他條目取標題中出現的指令名稱（例如「LOOP 指令」）。
        /// </summary>
        private static HashSet<string> MnemonicsOf(KnowledgeEntry e)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (e.Category == KnowledgeEntry.CategoryInstruction)
            {
                if (e.Tags != null)
                    foreach (var tag in e.Tags)
                        if (tag.Length > 0 && tag.All(ch => ch < 128 && (char.IsLetterOrDigit(ch) || ch == '.'))) set.Add(tag.ToLowerInvariant());
                return set;
            }
            foreach (Match m in TitleWordRegex.Matches(e.Title ?? string.Empty))
            {
                string w = m.Value.ToLowerInvariant();
                if (InstructionCatalog.Sets.Values.Any(s => s.IsExact(w))) set.Add(w);
            }
            return set;
        }

        private static readonly Regex TitleWordRegex = new Regex("[A-Za-z][A-Za-z0-9]*", RegexOptions.Compiled);

        private static void AddTokens(Dictionary<string, double> tf, string text, double weight)
        {
            foreach (var t in TextTokenizer.Tokenize(text))
            {
                double c;
                tf.TryGetValue(t, out c);
                tf[t] = c + weight;
            }
        }

        /// <summary>把問題轉成「詞 → 權重」：原詞 1.0，同義詞 0.5。</summary>
        public static Dictionary<string, double> BuildQuery(string query)
        {
            var weights = new Dictionary<string, double>(StringComparer.Ordinal);
            string normalized = TermNormalizer.Normalize(query ?? string.Empty);
            var tokens = TextTokenizer.Tokenize(normalized);
            foreach (var t in tokens)
                weights[t] = 1.0;

            var tokenSet = new HashSet<string>(tokens, StringComparer.Ordinal);
            foreach (var syn in TermNormalizer.ExpandSynonyms(normalized, tokenSet))
            {
                foreach (var t in TextTokenizer.Tokenize(syn))
                {
                    if (!weights.ContainsKey(t)) weights[t] = 0.5;
                }
            }
            return weights;
        }

        /// <summary>整句問題的正規化鍵（用於「這個問題上次的正確答案」記憶）。</summary>
        public static string QueryKey(string query)
        {
            var tokens = TextTokenizer.Tokenize(query ?? string.Empty);
            return string.Join(" ", tokens.Distinct().OrderBy(t => t, StringComparer.Ordinal));
        }

        public List<SearchHit> Search(string query, int top = 8, string preferredArch = null)
        {
            RebuildIfNeeded();
            var hits = new List<SearchHit>();
            var q = BuildQuery(query);
            if (q.Count == 0 || _docs.Count == 0) return hits;

            var memory = _memory != null ? _memory() : null;
            var archs = AsmArchInfo.MentionedKnowledgeArchs(query);
            if (!string.IsNullOrEmpty(preferredArch) && !archs.Contains(preferredArch)) archs.Add(preferredArch);

            string remembered = null;
            if (memory != null) memory.QueryAnswers.TryGetValue(QueryKey(query), out remembered);

            double n = _docs.Count;
            for (int i = 0; i < _docs.Count; i++)
            {
                var tf = _docTerms[i];
                double score = 0;
                foreach (var kv in q)
                {
                    double f;
                    if (!tf.TryGetValue(kv.Key, out f)) continue;
                    int df = _docFreq[kv.Key];
                    double idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                    double norm = f * (K1 + 1) / (f + K1 * (1 - B + B * _docLengths[i] / _avgLength));
                    score += kv.Value * idf * norm;
                }

                // 問題中直接出現指令名稱（例如 DJNZ、LDR）時，讓該指令卡加分
                foreach (var kv in q)
                    if (kv.Value >= 1.0 && _docMnemonics[i].Contains(kv.Key)) score += 2.5;

                var entry = _docs[i];
                double learned = 0;
                if (memory != null)
                {
                    foreach (var kv in q)
                    {
                        Dictionary<string, double> map;
                        double w;
                        if (memory.TermEntryWeights.TryGetValue(kv.Key, out map) && map.TryGetValue(entry.Id, out w))
                            learned += kv.Value * w;
                    }
                    learned *= 0.6;
                    if (remembered != null && string.Equals(remembered, entry.Id, StringComparison.OrdinalIgnoreCase))
                        learned += 25;
                }

                if (score <= 0 && learned <= 0) continue;

                double total = score;
                if (archs.Count > 0)
                {
                    string a = entry.Arch ?? "通用";
                    if (archs.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase))) total *= 1.5;
                    else if (!string.Equals(a, "通用", StringComparison.Ordinal)) total *= 0.6;
                }
                total += learned;
                if (total <= 0) continue;

                hits.Add(new SearchHit { Entry = entry, Score = total, TextScore = score, LearnedScore = learned });
            }

            return hits.OrderByDescending(h => h.Score).Take(top).ToList();
        }

        /// <summary>
        /// 回饋學習：有幫助 → 提高「問題中的詞」與該條目的關聯權重，並記住整句問題的答案；
        /// 沒幫助 → 降低權重，並清除對應的整句記憶。
        /// </summary>
        public static void Learn(UserMemoryData memory, string query, string entryId, bool helpful)
        {
            if (memory == null || string.IsNullOrEmpty(entryId)) return;
            var q = BuildQuery(query);
            double delta = helpful ? 1.0 : -1.0;
            foreach (var kv in q)
            {
                if (kv.Value < 1.0) continue; // 只學原始問題中的詞
                Dictionary<string, double> map;
                if (!memory.TermEntryWeights.TryGetValue(kv.Key, out map))
                {
                    map = new Dictionary<string, double>(StringComparer.Ordinal);
                    memory.TermEntryWeights[kv.Key] = map;
                }
                double w;
                map.TryGetValue(entryId, out w);
                w = Math.Max(-5, Math.Min(10, w + delta));
                if (Math.Abs(w) < 1e-9) map.Remove(entryId);
                else map[entryId] = w;
                if (map.Count == 0) memory.TermEntryWeights.Remove(kv.Key);
            }

            string key = QueryKey(query);
            if (key.Length == 0) return;
            if (helpful) memory.QueryAnswers[key] = entryId;
            else
            {
                string current;
                if (memory.QueryAnswers.TryGetValue(key, out current) &&
                    string.Equals(current, entryId, StringComparison.OrdinalIgnoreCase))
                    memory.QueryAnswers.Remove(key);
            }
        }
    }
}
