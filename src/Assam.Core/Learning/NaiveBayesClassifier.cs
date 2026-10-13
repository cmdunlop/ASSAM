using System;
using System.Collections.Generic;
using System.Linq;

namespace Assam.Core.Learning
{
    /// <summary>
    /// 多項式單純貝氏分類器（Multinomial Naive Bayes）。
    /// 計算量極小，在任何電腦上都能即時訓練與預測；新增樣本即可「學習」。
    /// </summary>
    public sealed class NaiveBayesClassifier
    {
        private readonly Dictionary<string, Dictionary<string, double>> _tokenCounts =
            new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _classTotals = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _classDocs = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly HashSet<string> _vocabulary = new HashSet<string>(StringComparer.Ordinal);
        private double _totalDocs;

        public double Alpha { get; set; }

        public NaiveBayesClassifier()
        {
            Alpha = 1.0;
        }

        public IEnumerable<string> Labels
        {
            get { return _classDocs.Keys; }
        }

        public void Train(IEnumerable<string> tokens, string label, double weight = 1.0)
        {
            if (string.IsNullOrEmpty(label) || tokens == null) return;
            Dictionary<string, double> counts;
            if (!_tokenCounts.TryGetValue(label, out counts))
            {
                counts = new Dictionary<string, double>(StringComparer.Ordinal);
                _tokenCounts[label] = counts;
                _classTotals[label] = 0;
                _classDocs[label] = 0;
            }

            foreach (var t in tokens)
            {
                double c;
                counts.TryGetValue(t, out c);
                counts[t] = c + weight;
                _classTotals[label] += weight;
                _vocabulary.Add(t);
            }
            _classDocs[label] += weight;
            _totalDocs += weight;
        }

        /// <summary>回傳各類別的後驗機率（由高到低排序）。</summary>
        public List<KeyValuePair<string, double>> Predict(IEnumerable<string> tokens)
        {
            var result = new List<KeyValuePair<string, double>>();
            if (_totalDocs <= 0) return result;
            var list = tokens == null ? new List<string>() : tokens.ToList();
            double vocab = Math.Max(1, _vocabulary.Count);

            var logScores = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var label in _classDocs.Keys)
            {
                double score = Math.Log(_classDocs[label] / _totalDocs);
                var counts = _tokenCounts[label];
                double denom = _classTotals[label] + Alpha * vocab;
                foreach (var t in list)
                {
                    if (!_vocabulary.Contains(t)) continue; // 沒見過的詞不提供資訊
                    double c;
                    counts.TryGetValue(t, out c);
                    score += Math.Log((c + Alpha) / denom);
                }
                logScores[label] = score;
            }

            double max = logScores.Values.Max();
            double sum = logScores.Values.Sum(v => Math.Exp(v - max));
            foreach (var kv in logScores)
                result.Add(new KeyValuePair<string, double>(kv.Key, Math.Exp(kv.Value - max) / sum));
            return result.OrderByDescending(kv => kv.Value).ToList();
        }
    }
}
