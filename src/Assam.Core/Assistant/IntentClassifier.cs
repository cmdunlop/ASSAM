using System;
using System.Collections.Generic;
using System.Linq;
using Assam.Core.Learning;
using Assam.Core.Text;

namespace Assam.Core.Assistant
{
    /// <summary>使用者問題的類型。</summary>
    public enum AssistantIntent
    {
        Search,
        ExplainCode,
        Simulate,
        DetectArch,
        NumberTool,
        Help,
        Greeting
    }

    public static class AssistantIntentInfo
    {
        public static string DisplayName(AssistantIntent intent)
        {
            switch (intent)
            {
                case AssistantIntent.Search: return "查詢知識／做法";
                case AssistantIntent.ExplainCode: return "解釋程式碼";
                case AssistantIntent.Simulate: return "推導執行結果";
                case AssistantIntent.DetectArch: return "辨識架構";
                case AssistantIntent.NumberTool: return "數值推導";
                case AssistantIntent.Help: return "使用說明";
                case AssistantIntent.Greeting: return "打招呼";
                default: return intent.ToString();
            }
        }

        public static readonly AssistantIntent[] All =
        {
            AssistantIntent.Search, AssistantIntent.ExplainCode, AssistantIntent.Simulate, AssistantIntent.DetectArch,
            AssistantIntent.NumberTool, AssistantIntent.Help, AssistantIntent.Greeting
        };
    }

    /// <summary>
    /// 問題類型分類器：單純貝氏模型，先用內建例句訓練，再加上使用者更正過的例句（權重較高）。
    /// </summary>
    public sealed class IntentClassifier
    {
        private static readonly Dictionary<AssistantIntent, string[]> Seeds = new Dictionary<AssistantIntent, string[]>
        {
            {
                AssistantIntent.Search, new[]
                {
                    "什麼是暫存器", "MOV 指令是什麼意思", "如何寫迴圈", "LOOP 指令怎麼用", "解釋堆疊的原理", "怎麼印出字串",
                    "旗標有哪些", "CF 和 OF 的差別", "定址模式有哪些", "怎麼呼叫副程式", "如何在 VS2019 組譯 MASM",
                    "8051 的計時器怎麼設定", "ARM 的 LDR 用法", "MIPS 的 syscall 有哪些", "怎麼交換兩個變數", "陣列加總的做法",
                    "如何找最大值", "字串反轉怎麼寫", "大小寫轉換", "什麼是二補數", "小端序是什麼", "呼叫慣例", "x64 的參數怎麼傳",
                    "how to write a loop", "what is the stack", "explain addressing modes", "jg 和 ja 有什麼不同",
                    "DIV 指令要注意什麼", "除法溢位怎麼辦", "常見的錯誤", "跑馬燈怎麼寫", "LED 閃爍程式", "中斷向量",
                    "教我氣泡排序", "費氏數列", "遞迴階乘", "怎麼讀取鍵盤輸入", "Irvine32 有哪些函式", "INT 21h 功能表",
                    "有效位址是什麼", "LEA 和 MOV 的差別", "條件跳躍", "堆疊框架", "巨集怎麼寫"
                }
            },
            {
                AssistantIntent.ExplainCode, new[]
                {
                    "幫我解釋這段程式", "這段程式碼在做什麼", "逐行說明", "請解釋以下程式碼", "這段組合語言是什麼意思",
                    "看不懂這段程式", "幫我分析程式", "註解這段程式", "explain this code", "what does this code do",
                    "這個程式的功能", "說明這段程式碼的流程", "這幾行是什麼意思", "幫我看這段程式有沒有錯", "程式哪裡有問題"
                }
            },
            {
                AssistantIntent.Simulate, new[]
                {
                    "執行後 EAX 是多少", "推導執行結果", "最後暫存器的值", "追蹤每一步", "模擬執行", "執行結果是什麼",
                    "AX 最後等於多少", "幫我推導", "一步一步追蹤", "跑完之後 ECX 的值", "trace this code", "simulate",
                    "最後旗標是多少", "輸出什麼", "結果是多少", "列出每一步暫存器的變化", "手算過程"
                }
            },
            {
                AssistantIntent.DetectArch, new[]
                {
                    "這是什麼組合語言", "這是哪一種架構", "判斷是 x86 還是 ARM", "這段是 8051 嗎", "辨識語法",
                    "這是 MASM 還是 NASM", "which architecture is this", "這是 AT&T 語法嗎", "哪個 CPU 的程式"
                }
            },
            {
                AssistantIntent.NumberTool, new[]
                {
                    "0x7F 轉二進位", "255 的十六進位", "二補數 -5", "8 位元 7Fh + 1 的旗標", "1234:0010 的實體位址",
                    "十進位轉二進位", "-12 的二補數", "10110b 轉十進位", "12MHz 延遲時間", "計時器初值", "鮑率 9600",
                    "符號延伸", "左移三位", "convert 0x1F to binary", "小端序 12345678h"
                }
            },
            {
                AssistantIntent.Help, new[]
                {
                    "你會什麼", "help", "使用說明", "有哪些功能", "怎麼使用你", "可以問什麼", "指令清單", "說明書", "幫助"
                }
            },
            {
                AssistantIntent.Greeting, new[]
                {
                    "你好", "哈囉", "hi", "hello", "謝謝", "感謝", "早安", "晚安", "掰掰", "thanks", "嗨"
                }
            }
        };

        private readonly Func<UserMemoryData> _memory;
        private NaiveBayesClassifier _model;
        private int _trainedUserSamples = -1;

        public IntentClassifier(Func<UserMemoryData> memory)
        {
            _memory = memory;
        }

        private void EnsureTrained()
        {
            var memory = _memory != null ? _memory() : null;
            int userCount = memory != null ? memory.IntentSamples.Count : 0;
            if (_model != null && userCount == _trainedUserSamples) return;

            var model = new NaiveBayesClassifier { Alpha = 0.5 };
            foreach (var kv in Seeds)
                foreach (var s in kv.Value)
                    model.Train(Features(s), kv.Key.ToString(), 1.0);
            if (memory != null)
            {
                foreach (var sample in memory.IntentSamples)
                    model.Train(Features(sample.Text), sample.Label, 3.0);
            }
            _model = model;
            _trainedUserSamples = userCount;
        }

        public static List<string> Features(string text)
        {
            var tokens = TextTokenizer.Tokenize(text, false);
            return tokens;
        }

        public List<KeyValuePair<AssistantIntent, double>> Predict(string text)
        {
            EnsureTrained();
            var result = new List<KeyValuePair<AssistantIntent, double>>();
            foreach (var kv in _model.Predict(Features(text)))
            {
                AssistantIntent intent;
                if (Enum.TryParse(kv.Key, out intent)) result.Add(new KeyValuePair<AssistantIntent, double>(intent, kv.Value));
            }
            if (result.Count == 0) result.Add(new KeyValuePair<AssistantIntent, double>(AssistantIntent.Search, 1.0));
            return result;
        }

        /// <summary>使用者更正問題類型：存成樣本，下次預測就會參考。</summary>
        public void Learn(string text, AssistantIntent actual)
        {
            var memory = _memory();
            if (memory == null || string.IsNullOrWhiteSpace(text)) return;
            memory.IntentSamples.Add(new LabeledSample { Text = text.Length > 300 ? text.Substring(0, 300) : text, Label = actual.ToString(), CreatedAt = UserMemoryData.Now() });
            _model = null;
        }
    }
}
