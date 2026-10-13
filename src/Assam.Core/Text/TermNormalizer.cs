using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Assam.Core.Text
{
    /// <summary>
    /// 文字正規化：全形轉半形、簡體常用詞轉繁體、同義詞擴展。
    /// 全部在本機以字典完成，不需要任何網路服務。
    /// </summary>
    public static class TermNormalizer
    {
        // 片語層級（先比對較長的詞）
        private static readonly KeyValuePair<string, string>[] PhraseMap = BuildPhraseMap(new[]
        {
            "汇编语言", "組合語言", "汇编器", "組譯器", "汇编", "組譯", "寄存器", "暫存器", "内存", "記憶體",
            "存储器", "記憶體", "标志寄存器", "旗標暫存器", "标志位", "旗標", "标志", "旗標", "循环", "迴圈",
            "堆栈", "堆疊", "栈", "堆疊", "中断", "中斷", "跳转", "跳躍", "数组", "陣列", "字符串", "字串",
            "溢出", "溢位", "进位", "進位", "补码", "補數", "寻址", "定址", "物理地址", "實體位址",
            "有效地址", "有效位址", "地址", "位址", "子程序", "副程式", "函数", "函式", "变量", "變數",
            "调用", "呼叫", "十六进制", "十六進位", "二进制", "二進位", "十进制", "十進位", "八进制", "八進位",
            "进制", "進位", "计数器", "計數器", "定时器", "計時器", "计时器", "計時器", "延时", "延遲",
            "单片机", "單晶片", "链接器", "連結器", "链接", "連結", "宏", "巨集", "调试", "除錯",
            "无符号", "無號", "有符号", "有號", "操作数", "運算元", "立即数", "立即值", "指针", "指標",
            "推导", "推導", "执行", "執行", "结果", "結果", "怎么", "怎麼", "什么", "什麼", "为什么", "為什麼",
            "屏幕", "螢幕", "键盘", "鍵盤", "程序计数器", "程式計數器", "反汇编", "反組譯", "数码管", "七段顯示器",
            "波特率", "鮑率", "串口", "序列埠", "端口", "連接埠", "软件", "軟體", "硬件", "硬體", "信息", "資訊",
            "默认", "預設", "文件", "檔案", "代码", "程式碼", "源代码", "原始碼", "字节", "位元組", "比特", "位元",
            "位运算", "位元運算", "逻辑运算", "邏輯運算", "移位", "移位", "递归", "遞迴", "排序", "排序"
        });

        // 單字層級（常見簡體字 → 繁體字）
        private static readonly Dictionary<char, char> CharMap = BuildCharMap(
            "们們这這个個为為么麼吗嗎数數码碼编編译譯换換转轉门門内內标標环環栈棧汇匯断斷组組进進补補寻尋" +
            "变變调調计計时時单單链鏈键鍵显顯较較条條无無号號签簽执執结結导導样樣问問题題说說读讀写寫输輸" +
            "处處机機语語实實际際应應点點运運术術逻邏辑輯页頁区區间間递遞归歸获獲设設开開关關灯燈闪閃烁爍" +
            "钟鐘频頻传傳发發储儲载載态態状狀测測试試错錯误誤帮幫还還两兩余餘负負长長尽盡带帶线線复複杂雜" +
            "简簡现現范範围圍节節约約后後当當从從对對将將与與并並会會来來过過动動几幾头頭体體义義类類参參" +
            "层層级級缓緩冲衝屏屏储儲构構创創删刪选選项項编編辑輯载載页頁视視窗窗务務压壓缩縮");

        private static readonly string[][] SynonymGroups =
        {
            new[] { "暫存器", "register", "reg" },
            new[] { "旗標", "flag", "flags", "eflags", "psw", "cpsr", "sreg" },
            new[] { "迴圈", "loop", "重複", "djnz" },
            new[] { "堆疊", "stack", "push", "pop" },
            new[] { "副程式", "子程式", "程序", "函式", "函數", "procedure", "proc", "function", "subroutine" },
            new[] { "中斷", "interrupt" },
            new[] { "跳躍", "jump", "jmp", "分支", "branch" },
            new[] { "比較", "compare", "cmp" },
            new[] { "乘法", "相乘", "multiply", "mul", "imul" },
            new[] { "除法", "相除", "divide", "div", "idiv", "餘數" },
            new[] { "移位", "位移", "shift", "shl", "shr", "sar", "sal" },
            new[] { "旋轉", "rotate", "rol", "ror", "rcl", "rcr" },
            new[] { "二補數", "2補數", "補數", "complement", "負數" },
            new[] { "十六進位", "16進位", "hex", "hexadecimal" },
            new[] { "二進位", "2進位", "binary" },
            new[] { "十進位", "decimal" },
            new[] { "定址", "addressing" },
            new[] { "記憶體", "memory" },
            new[] { "字串", "string" },
            new[] { "陣列", "array" },
            new[] { "溢位", "overflow" },
            new[] { "進位", "carry" },
            new[] { "組譯器", "assembler", "masm", "nasm" },
            new[] { "連結器", "linker" },
            new[] { "巨集", "macro" },
            new[] { "輸出", "印出", "顯示", "print", "output" },
            new[] { "輸入", "讀取", "鍵盤", "input" },
            new[] { "計時器", "timer" },
            new[] { "延遲", "delay" },
            new[] { "位元組", "byte" },
            new[] { "堆疊框架", "stack frame", "ebp" },
            new[] { "呼叫慣例", "calling convention", "cdecl", "stdcall", "fastcall" },
            new[] { "排序", "sort", "bubble" },
            new[] { "最大值", "max", "maximum" },
            new[] { "最小值", "min", "minimum" },
            new[] { "總和", "加總", "累加", "sum" },
            new[] { "交換", "swap", "xchg" },
            new[] { "除錯", "偵錯", "debug", "debugger" },
            new[] { "系統呼叫", "syscall", "system call" },
            new[] { "實體位址", "physical address" },
            new[] { "有效位址", "effective address", "lea" },
            new[] { "區段", "segment" },
            new[] { "清零", "歸零", "zero" },
            new[] { "階乘", "factorial" },
            new[] { "費氏", "fibonacci" },
            new[] { "遞迴", "recursion", "recursive" },
            new[] { "條件跳躍", "conditional jump", "jcc" },
            new[] { "有號", "signed" },
            new[] { "無號", "unsigned" },
            new[] { "小端", "little endian", "endian", "位元組序" },
            new[] { "單晶片", "8051", "mcs-51" },
            new[] { "跑馬燈", "led" },
            new[] { "七段顯示器", "7-segment", "seven segment" },
            new[] { "序列埠", "uart", "serial" },
            new[] { "鮑率", "baud" },
        };

        private static KeyValuePair<string, string>[] BuildPhraseMap(string[] pairs)
        {
            var list = new List<KeyValuePair<string, string>>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                if (pairs[i] != pairs[i + 1])
                    list.Add(new KeyValuePair<string, string>(pairs[i], pairs[i + 1]));
            }
            return list.OrderByDescending(p => p.Key.Length).ToArray();
        }

        private static Dictionary<char, char> BuildCharMap(string pairs)
        {
            var map = new Dictionary<char, char>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                if (pairs[i] != pairs[i + 1])
                    map[pairs[i]] = pairs[i + 1];
            }
            return map;
        }

        /// <summary>全形轉半形 + 簡轉繁（只處理常用詞），結果用於索引與查詢。</summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c >= '！' && c <= '～') sb.Append((char)(c - 0xFEE0));
                else if (c == '　') sb.Append(' ');
                else sb.Append(c);
            }

            string s = sb.ToString();
            if (!ContainsCjk(s)) return s;

            foreach (var pair in PhraseMap)
            {
                if (s.IndexOf(pair.Key, StringComparison.Ordinal) >= 0)
                    s = s.Replace(pair.Key, pair.Value);
            }

            sb.Clear();
            foreach (char c in s)
            {
                char t;
                sb.Append(CharMap.TryGetValue(c, out t) ? t : c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 同義詞擴展：回傳查詢中出現的詞所屬群組的其他成員（用較低權重加入查詢）。
        /// </summary>
        public static List<string> ExpandSynonyms(string normalizedQuery, ICollection<string> queryTokens)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(normalizedQuery)) return result;
            string lower = normalizedQuery.ToLowerInvariant();

            foreach (var group in SynonymGroups)
            {
                bool hit = false;
                foreach (var member in group)
                {
                    if (IsAsciiWord(member))
                    {
                        if (queryTokens.Contains(member)) { hit = true; break; }
                    }
                    else if (lower.IndexOf(member, StringComparison.Ordinal) >= 0)
                    {
                        hit = true; break;
                    }
                }
                if (!hit) continue;
                foreach (var member in group)
                {
                    if (lower.IndexOf(member, StringComparison.Ordinal) < 0)
                        result.Add(member);
                }
            }
            return result;
        }

        public static bool ContainsCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (IsCjk(c)) return true;
            return false;
        }

        public static bool IsCjk(char c)
        {
            return (c >= '一' && c <= '鿿') || (c >= '㐀' && c <= '䶿') || (c >= '豈' && c <= '﫿');
        }

        private static bool IsAsciiWord(string s)
        {
            foreach (char c in s)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
                    return false;
            }
            return true;
        }
    }
}
