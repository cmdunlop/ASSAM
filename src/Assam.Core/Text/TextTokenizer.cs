using System.Collections.Generic;

namespace Assam.Core.Text
{
    /// <summary>
    /// 中英混合斷詞器：英文/數字以單字為單位（轉小寫），中文使用「單字 + 雙字詞(bigram)」。
    /// 不需要詞典檔，適合在低階電腦上即時運作。
    /// </summary>
    public static class TextTokenizer
    {
        private static readonly HashSet<char> CjkStopChars = new HashSet<char>(
            "的了嗎呢吧啊呀是我你他她它們請問什麼怎樣如何個這那要會能可以一下想把被給和與及或就也都還嘛喔哦".ToCharArray());

        private static readonly HashSet<string> EnglishStopWords = new HashSet<string>
        {
            "a", "an", "the", "is", "are", "was", "be", "what", "how", "why", "to", "of", "in", "on", "for",
            "do", "does", "did", "i", "you", "me", "my", "it", "this", "that", "and", "or", "with", "can",
            "please", "about", "tell", "show", "want", "know", "use", "used", "using"
        };

        /// <summary>斷詞。removeStopWords=true 用於搜尋，false 用於意圖分類（疑問詞是重要特徵）。</summary>
        public static List<string> Tokenize(string text, bool removeStopWords = true)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(text)) return tokens;

            text = TermNormalizer.Normalize(text);
            int n = text.Length;
            int i = 0;
            while (i < n)
            {
                char c = text[i];
                if (TermNormalizer.IsCjk(c))
                {
                    int start = i;
                    while (i < n && TermNormalizer.IsCjk(text[i])) i++;
                    AddCjkRun(text.Substring(start, i - start), tokens, removeStopWords);
                }
                else if (IsWordChar(c) || (IsPrefixChar(c) && i + 1 < n && IsWordChar(text[i + 1]) && (i == 0 || !IsWordChar(text[i - 1]))))
                {
                    int start = i;
                    i++;
                    while (i < n && IsWordChar(text[i])) i++;
                    AddWord(text.Substring(start, i - start).ToLowerInvariant(), tokens, removeStopWords);
                }
                else
                {
                    i++;
                }
            }
            return tokens;
        }

        private static void AddCjkRun(string run, List<string> tokens, bool removeStopWords)
        {
            for (int k = 0; k < run.Length; k++)
            {
                char c = run[k];
                if (!removeStopWords || !CjkStopChars.Contains(c))
                    tokens.Add(c.ToString());

                if (k + 1 < run.Length)
                {
                    char d = run[k + 1];
                    if (removeStopWords && CjkStopChars.Contains(c) && CjkStopChars.Contains(d))
                        continue;
                    tokens.Add(new string(new[] { c, d }));
                }
            }
        }

        private static void AddWord(string word, List<string> tokens, bool removeStopWords)
        {
            if (word.Length == 0) return;
            if (IsPrefixChar(word[0]))
            {
                string bare = word.Substring(1);
                tokens.Add(word);
                if (bare.Length > 0 && (!removeStopWords || !EnglishStopWords.Contains(bare)))
                    tokens.Add(bare);
                return;
            }
            if (removeStopWords && EnglishStopWords.Contains(word)) return;
            tokens.Add(word);
        }

        private static bool IsWordChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
        }

        private static bool IsPrefixChar(char c)
        {
            return c == '.' || c == '$' || c == '%' || c == '@' || c == '#';
        }
    }
}
