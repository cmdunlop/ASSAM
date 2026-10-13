using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Assam.Core.Analysis;
using Assam.Core.Assistant;
using Assam.Core.Knowledge;
using Assam.Core.Learning;
using Assam.Core.Search;

namespace Assam.Tests
{
    public static class KnowledgeAndAssistantTests
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "assam-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static AsmAssistant NewAssistant(string dir = null)
        {
            return new AsmAssistant(dir ?? NewTempDir());
        }

        // ---------------- 知識庫 ----------------

        [Test]
        public static void BuiltInKnowledgeIsValid()
        {
            var kb = new KnowledgeBase();
            kb.LoadBuiltIn();
            Assert.True(kb.Count > 400, "知識數量 " + kb.Count);
            var builtin = kb.Entries.Where(e => e.Source != null && e.Source.StartsWith("內建:", StringComparison.Ordinal)).ToList();
            Assert.True(builtin.Count >= 100, "內建知識卡 " + builtin.Count);
            foreach (var e in builtin)
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Title), e.Id + " 沒有標題");
                Assert.True(e.Body != null && e.Body.Length > 30, e.Id + " 內文太短");
                Assert.False(e.Id.Contains("-0") && e.Id.StartsWith("內建", StringComparison.Ordinal), e.Id + " 缺少 id");
                Assert.True(e.Tags.Count > 0, e.Id + " 沒有標籤");
            }
            Assert.Equal(kb.Entries.Count, kb.Entries.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "id 不可重複");
        }

        [Test]
        public static void ParserKeepsHeadingsInsideCodeFence()
        {
            string md = "## A\nid: a\n---\n```\n## not a heading\n```\ntext\n## B\nid: b\n---\nbody b";
            var list = KnowledgeParser.Parse(md, "t");
            Assert.Equal(2, list.Count);
            Assert.Contains("## not a heading", list[0].Body);
            Assert.Equal("body b", list[1].Body);
            var round = KnowledgeParser.Parse(KnowledgeParser.Serialize(list), "t2");
            Assert.Equal(2, round.Count);
            Assert.Equal("a", round[0].Id);
        }

        // ---------------- 搜尋 ----------------

        private static SearchEngine NewSearch(UserMemoryData memory)
        {
            var kb = new KnowledgeBase();
            kb.LoadBuiltIn();
            return new SearchEngine(kb, () => memory);
        }

        [Test]
        public static void SearchFindsRelevantTopics()
        {
            var s = NewSearch(UserMemoryData.CreateNew());
            Assert.Equal("x86-loop", s.Search("LOOP 指令怎麼用")[0].Entry.Id);
            Assert.Contains("二補數", s.Search("什麼是二補數")[0].Entry.Title);
            Assert.Equal("8051", s.Search("8051 延遲副程式怎麼算")[0].Entry.Arch);
            Assert.Equal("x86-registers", s.Search("寄存器是什么")[0].Entry.Id, "簡體字也要能查到");
            Assert.Equal("howto-array-max", s.Search("怎麼找陣列最大值")[0].Entry.Id);
            Assert.Equal("ins-i8051-djnz", s.Search("DJNZ")[0].Entry.Id);
        }

        [Test]
        public static void SearchLearnsFromFeedback()
        {
            var memory = UserMemoryData.CreateNew();
            var s = NewSearch(memory);
            const string q = "迴圈要注意什麼";
            var hits = s.Search(q);
            var wanted = hits[3].Entry.Id;
            SearchEngine.Learn(memory, q, wanted, true);
            Assert.Equal(wanted, s.Search(q)[0].Entry.Id, "回饋後應排第一");
            Assert.True(s.Search(q)[0].BoostedByMemory);
            SearchEngine.Learn(memory, q, wanted, false);
            SearchEngine.Learn(memory, q, wanted, false);
            Assert.False(s.Search(q)[0].Entry.Id == wanted, "負回饋後應降低排名");
        }

        // ---------------- 助理整合 ----------------

        [Test]
        public static void AskKnowledgeQuestion()
        {
            var a = NewAssistant();
            var r = a.Ask("什麼是堆疊");
            Assert.Equal(AssistantIntent.Search, r.Intent);
            Assert.True(r.Hits.Count > 0);
            Assert.Contains("堆疊", r.Text);
        }

        [Test]
        public static void AskExplainCode()
        {
            var a = NewAssistant();
            var r = a.Ask("幫我解釋\nmov ecx, 5\nL1: add eax, ecx\nloop L1");
            Assert.Equal(AssistantIntent.ExplainCode, r.Intent);
            Assert.NotNull(r.Explanation);
            Assert.Contains("【逐行說明】", r.Text);
            Assert.Contains("迴圈", r.Text);
        }

        [Test]
        public static void AskSimulate()
        {
            var a = NewAssistant();
            var r = a.Ask("執行後 EAX 是多少？\nmov eax, 5\nadd eax, 3");
            Assert.Equal(AssistantIntent.Simulate, r.Intent);
            Assert.NotNull(r.Simulation);
            Assert.Contains("EAX = 0x00000008（十進位 8）", r.Text);
        }

        [Test]
        public static void AskEmbeddedInstruction()
        {
            var a = NewAssistant();
            var r = a.Ask("mov eax, [ebx+4] 是什麼意思");
            Assert.NotNull(r.Explanation, "應把問句中的指令當成程式解釋");
            Assert.Contains("基底相對定址", r.Text);
        }

        [Test]
        public static void AskNumberTools()
        {
            var a = NewAssistant();
            var r = a.Ask("0x7F 轉二進位");
            Assert.Equal(AssistantIntent.NumberTool, r.Intent);
            Assert.Contains("0111 1111", r.Text);
            Assert.Contains("1111 1011", a.Ask("-5 的 8 位元二補數").Text);
        }

        [Test]
        public static void AskDetect()
        {
            var a = NewAssistant();
            var r = a.Ask("這是什麼組合語言？\nlw $t0, 0($sp)\naddi $sp, $sp, 4");
            Assert.Equal(AssistantIntent.DetectArch, r.Intent);
            Assert.Contains("MIPS", r.Text);
        }

        [Test]
        public static void GreetingAndCommands()
        {
            var a = NewAssistant();
            Assert.Equal(AssistantIntent.Greeting, a.Ask("你好").Intent);
            Assert.Contains("記憶檔位置", a.Ask("/stats").Text);
            Assert.Contains("/teach", a.Ask("/help").Text);
        }

        [Test]
        public static void TeachAndRememberAcrossRestart()
        {
            string dir = NewTempDir();
            var a = NewAssistant(dir);
            var entry = a.Teach("學校實驗室的組譯器版本", "實驗室電腦使用 MASM 14 與 Irvine32 函式庫。", "x86");
            var r = a.Ask("實驗室的組譯器版本是什麼");
            Assert.Equal(entry.Id, r.TopHit.Entry.Id);

            var again = NewAssistant(dir);
            Assert.Equal(1, again.Memory.Data.TaughtEntries.Count);
            Assert.Equal(entry.Id, again.Ask("實驗室組譯器版本").TopHit.Entry.Id, "重新啟動後仍記得");
            Assert.True(File.Exists(Path.Combine(dir, MemoryStore.MemoryFileName)));
        }

        [Test]
        public static void FeedbackIsSavedToDisk()
        {
            string dir = NewTempDir();
            var a = NewAssistant(dir);
            var r = a.Ask("迴圈怎麼寫");
            var other = r.Hits[2];
            a.ConfirmEntry(r, other.Entry.Id);
            var b = NewAssistant(dir);
            Assert.Equal(other.Entry.Id, b.Ask("迴圈怎麼寫").TopHit.Entry.Id);
            Assert.True(b.Memory.Data.GetCounter("feedback_positive") >= 1);
        }

        [Test]
        public static void IntentCorrectionIsLearned()
        {
            var a = NewAssistant();
            const string text = "給我看功能列表吧";
            a.CorrectIntent(text, AssistantIntent.Help);
            a.CorrectIntent(text, AssistantIntent.Help);
            Assert.Equal(AssistantIntent.Help, a.Intents.Predict(text)[0].Key);
        }

        [Test]
        public static void UserKnowledgeFolderIsLoaded()
        {
            string dir = NewTempDir();
            Directory.CreateDirectory(Path.Combine(dir, "Knowledge"));
            File.WriteAllText(Path.Combine(dir, "Knowledge", "my.md"), "## 我的 Z80 筆記\nid: my-z80\narch: Z80\ntags: Z80, LD\n---\nZ80 的 LD A, B 是把 B 複製到 A。");
            var a = NewAssistant(dir);
            Assert.NotNull(a.Knowledge.Get("my-z80"));
            Assert.Equal("my-z80", a.Ask("Z80 的 LD 指令").TopHit.Entry.Id);
        }

        [Test]
        public static void CorruptMemoryFileRecovers()
        {
            string dir = NewTempDir();
            File.WriteAllText(Path.Combine(dir, MemoryStore.MemoryFileName), "{ 這不是 JSON");
            var a = NewAssistant(dir);
            Assert.Equal(0, a.Memory.Data.TaughtEntries.Count);
            Assert.Contains("無法讀取", a.Memory.LastMessage);
            Assert.True(Directory.GetFiles(dir, "memory.broken-*.json").Length == 1);
        }

        [Test]
        public static void MemoryJsonRoundTrip()
        {
            var data = UserMemoryData.CreateNew();
            data.TermEntryWeights["迴圈"] = new System.Collections.Generic.Dictionary<string, double> { { "x86-loop", 2.0 } };
            data.QueryAnswers["a b"] = "x86-loop";
            data.TaughtEntries.Add(new KnowledgeEntry { Id = "u1", Title = "標題", Body = "內容", Tags = { "t" } });
            data.InstructionNotes["x86:LOOP"] = "先設定 ECX";
            var copy = MemoryStore.Deserialize(MemoryStore.Serialize(data));
            Assert.Equal(2.0, copy.TermEntryWeights["迴圈"]["x86-loop"]);
            Assert.Equal("x86-loop", copy.QueryAnswers["a b"]);
            Assert.Equal("內容", copy.TaughtEntries[0].Body);
            Assert.Equal("先設定 ECX", copy.InstructionNotes["x86:LOOP"]);
            string json = System.Text.Encoding.UTF8.GetString(MemoryStore.Serialize(data));
            Assert.Contains("\"迴圈\"", json, "JSON 應保留中文、可讀");
        }

        [Test]
        public static void CorrectArchitecturePersists()
        {
            string dir = NewTempDir();
            var a = NewAssistant(dir);
            const string code = "mov r0, #5\nmov r1, #6";
            var target = a.Detector.Detect(code).Best == AsmArch.I8051 ? AsmArch.Arm : AsmArch.I8051;
            a.CorrectArchitecture(code, target);
            Assert.Equal(target, NewAssistant(dir).Detector.Detect(code).Best);
        }

        // ---------------- 離線與效能 ----------------

        [Test]
        public static void CoreHasNoNetworkDependency()
        {
            var refs = typeof(AsmAssistant).Assembly.GetReferencedAssemblies().Select(r => r.Name).ToList();
            Assert.False(refs.Any(n => n.StartsWith("System.Net", StringComparison.OrdinalIgnoreCase)), "核心不可引用網路組件：" + string.Join(",", refs));
            Assert.False(refs.Any(n => n.StartsWith("System.Web", StringComparison.OrdinalIgnoreCase)));
        }

        [Test]
        public static void StartupIsFast()
        {
            var sw = Stopwatch.StartNew();
            var a = NewAssistant();
            long startup = sw.ElapsedMilliseconds;
            sw.Restart();
            for (int i = 0; i < 20; i++) a.Search.Search("迴圈 暫存器 旗標 " + i);
            long search = sw.ElapsedMilliseconds;
            Console.WriteLine("        啟動 " + startup + " ms，20 次搜尋 " + search + " ms，知識 " + a.Knowledge.Count + " 筆");
            Assert.True(startup < 5000, "啟動太慢：" + startup + " ms");
            Assert.True(search < 3000, "搜尋太慢：" + search + " ms");
        }
    }
}
