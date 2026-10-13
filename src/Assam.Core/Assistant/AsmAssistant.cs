using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Assam.Core.Analysis;
using Assam.Core.Knowledge;
using Assam.Core.Learning;
using Assam.Core.Search;
using Assam.Core.Simulation;
using Assam.Core.Text;

namespace Assam.Core.Assistant
{
    /// <summary>一次問答的結果。</summary>
    public sealed class AssistantReply
    {
        public AssistantReply()
        {
            Hits = new List<SearchHit>();
        }

        public string Question { get; set; }
        /// <summary>用來分類／搜尋的文字（去掉程式碼後的問題）。</summary>
        public string QuestionText { get; set; }
        public string Code { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
        public AssistantIntent Intent { get; set; }
        public double IntentConfidence { get; set; }
        public List<SearchHit> Hits { get; private set; }
        public CodeExplanation Explanation { get; set; }
        public SimulationResult Simulation { get; set; }
        public ArchDetectionResult Detection { get; set; }
        public HistoryItem History { get; set; }

        public SearchHit TopHit
        {
            get { return Hits.Count > 0 ? Hits[0] : null; }
        }

        public bool CanGiveFeedback
        {
            get { return Hits.Count > 0; }
        }
    }

    /// <summary>
    /// 離線組合語言 AI 助理的總入口：知識檢索、程式碼解說、架構辨識、執行推導、數值推導，
    /// 以及「從回饋中學習」並把記憶存在本機。
    /// </summary>
    public sealed class AsmAssistant
    {
        public const string Version = "1.0.0";

        private static readonly string[] SimulateWords =
        {
            "執行後", "執行結果", "結果", "多少", "推導", "追蹤", "模擬", "最後", "輸出什麼", "跑完", "trace", "simulate", "值是", "等於"
        };

        private static readonly string[] DetectWords =
        {
            "什麼架構", "哪種", "哪一種", "哪個cpu", "哪個 cpu", "什麼語言", "什麼組合語言", "辨識", "which arch", "架構嗎", "語法嗎"
        };

        public AsmAssistant(string dataDirectory)
        {
            Memory = new MemoryStore(dataDirectory);
            Memory.Load();
            Knowledge = new KnowledgeBase();
            Search = new SearchEngine(Knowledge, () => Memory.Data);
            Detector = new ArchitectureDetector(() => Memory.Data);
            Explainer = new CodeExplainer(Detector);
            Simulator = new X86Simulator();
            Intents = new IntentClassifier(() => Memory.Data);
            ReloadKnowledge();
        }

        public static AsmAssistant CreateDefault()
        {
            return new AsmAssistant(MemoryStore.ResolveDefaultDirectory(AppDomain.CurrentDomain.BaseDirectory));
        }

        public MemoryStore Memory { get; private set; }
        public KnowledgeBase Knowledge { get; private set; }
        public SearchEngine Search { get; private set; }
        public ArchitectureDetector Detector { get; private set; }
        public CodeExplainer Explainer { get; private set; }
        public X86Simulator Simulator { get; private set; }
        public IntentClassifier Intents { get; private set; }
        public string LastSaveError { get; private set; }

        public string StartupMessage
        {
            get
            {
                var d = Memory.Data;
                return "知識庫 " + Knowledge.Count + " 筆（含使用者知識 " + Knowledge.Entries.Count(e => e.IsUserEntry) + " 筆）；" +
                       "學習記憶：教學 " + d.TaughtEntries.Count + " 筆、回饋權重 " + d.LearnedTermWeightCount + " 項、架構更正 " + d.ArchSamples.Count + " 次。" +
                       (Knowledge.LoadErrors.Count > 0 ? " 載入警告：" + string.Join("；", Knowledge.LoadErrors) : string.Empty);
            }
        }

        /// <summary>重新載入全部知識（內建 + UserData\Knowledge\*.md + 使用者教學）。</summary>
        public void ReloadKnowledge()
        {
            var kb = new KnowledgeBase();
            kb.LoadBuiltIn();
            kb.LoadDirectory(Memory.KnowledgeDirectory);
            foreach (var e in Memory.Data.TaughtEntries)
            {
                e.IsUserEntry = true;
                kb.AddOrUpdate(e);
            }
            Knowledge = kb;
            Search = new SearchEngine(Knowledge, () => Memory.Data);
            Search.Rebuild();
        }

        public bool Save()
        {
            try
            {
                Memory.Save();
                LastSaveError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastSaveError = ex.Message;
                return false;
            }
        }

        // =====================================================================
        // 問答
        // =====================================================================

        public AssistantReply Ask(string input)
        {
            var reply = new AssistantReply { Question = (input ?? string.Empty).Trim() };
            if (reply.Question.Length == 0)
            {
                reply.Intent = AssistantIntent.Help;
                reply.Title = "使用說明";
                reply.Text = HelpText;
                return reply;
            }

            if (reply.Question.StartsWith("/", StringComparison.Ordinal))
            {
                HandleCommand(reply);
                Record(reply);
                return reply;
            }

            string question, code;
            CodeExtractor.Split(reply.Question, out question, out code);
            reply.QuestionText = question;
            reply.Code = code;

            if (code.Length > 0) AnswerWithCode(reply, question, code);
            else AnswerQuestion(reply, question);

            Memory.Data.Increment("questions");
            Record(reply);
            return reply;
        }

        private void AnswerWithCode(AssistantReply reply, string question, string code)
        {
            string lower = (question ?? string.Empty).ToLowerInvariant();
            AssistantIntent intent = AssistantIntent.ExplainCode;
            double confidence = 0.6;
            if (lower.Length > 0)
            {
                var prediction = Intents.Predict(question);
                var best = prediction[0];
                if (best.Key == AssistantIntent.Simulate || best.Key == AssistantIntent.DetectArch || best.Key == AssistantIntent.ExplainCode)
                {
                    intent = best.Key;
                    confidence = best.Value;
                }
                if (SimulateWords.Any(w => lower.Contains(w))) { intent = AssistantIntent.Simulate; confidence = Math.Max(confidence, 0.8); }
                if (DetectWords.Any(w => lower.Contains(w))) { intent = AssistantIntent.DetectArch; confidence = Math.Max(confidence, 0.8); }
            }
            reply.Intent = intent;
            reply.IntentConfidence = confidence;

            var detection = Detector.Detect(code);
            reply.Detection = detection;
            var arch = detection.Best;

            if (intent == AssistantIntent.DetectArch)
            {
                reply.Title = "架構辨識";
                var sb = new StringBuilder(detection.Describe()).Append("\r\n\r\n");
                sb.Append("如果判斷錯了，請到「程式碼分析」頁選擇正確架構並按「更正並學習」，我會記住這次的修正。\r\n");
                AppendRelated(sb, reply, AsmArchInfo.DisplayName(arch) + " 架構 暫存器", AsmArchInfo.KnowledgeArch(arch));
                reply.Text = sb.ToString();
                return;
            }

            if (intent == AssistantIntent.Simulate && (arch == AsmArch.X86Intel || arch == AsmArch.Unknown))
            {
                var options = new SimulationOptions { MaxSteps = 1000 };
                foreach (var kv in SimulationOptions.ParseRegisterAssignments(question, null)) options.InitialRegisters[kv.Key] = kv.Value;
                var sim = Simulator.Run(code, options);
                reply.Simulation = sim;
                reply.Title = "執行推導";
                var sb = new StringBuilder();
                string asked = AskedRegisterAnswer(question, sim);
                if (asked != null) sb.Append("【答案】").Append(asked).Append("\r\n\r\n");
                sb.Append(sim.ToText(80));
                AppendRelated(sb, reply, string.Join(" ", ExplainMnemonics(code, arch).Take(6)), "x86");
                reply.Text = sb.ToString();
                return;
            }

            var explanation = Explainer.Explain(code, AsmArch.Unknown, detection);
            reply.Explanation = explanation;
            reply.Title = "程式碼解說";
            var text = new StringBuilder();
            if (intent == AssistantIntent.Simulate)
                text.Append("（推導模擬器目前支援 x86 Intel 語法；").Append(AsmArchInfo.DisplayName(arch)).Append(" 先提供逐行說明與動作虛擬碼。）\r\n\r\n");
            text.Append(explanation.ToText(200));
            AppendRelated(text, reply, AsmArchInfo.KnowledgeArch(arch) + " " + string.Join(" ", explanation.Mnemonics.Take(6)), AsmArchInfo.KnowledgeArch(arch));
            reply.Text = text.ToString();
        }

        private IEnumerable<string> ExplainMnemonics(string code, AsmArch arch)
        {
            return ArchitectureDetector.ExtractFeatures(code).Mnemonics.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string AskedRegisterAnswer(string question, SimulationResult sim)
        {
            if (string.IsNullOrEmpty(question)) return null;
            var answers = new List<string>();
            foreach (var kv in sim.FinalRegisters)
            {
                if (question.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ulong v;
                    string hex = kv.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? kv.Value.Substring(2) : kv.Value;
                    if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
                        answers.Add(kv.Key + " = " + kv.Value + "（十進位 " + v.ToString(CultureInfo.InvariantCulture) + "）");
                    else answers.Add(kv.Key + " = " + kv.Value);
                }
            }
            if (answers.Count == 0) return null;
            return "執行完畢後 " + string.Join("、", answers);
        }

        private void AnswerQuestion(AssistantReply reply, string question)
        {
            reply.QuestionText = question;
            var number = NumberRequestParser.TryHandle(question);
            var prediction = Intents.Predict(question);
            var best = prediction[0];
            reply.Intent = best.Key;
            reply.IntentConfidence = best.Value;

            if (number != null)
            {
                reply.Intent = AssistantIntent.NumberTool;
                reply.IntentConfidence = Math.Max(best.Key == AssistantIntent.NumberTool ? best.Value : 0, 0.9);
                reply.Title = number.Title;
                var sb = new StringBuilder(number.Text);
                AppendRelated(sb, reply, question, null);
                reply.Text = sb.ToString();
                return;
            }

            bool shortText = question.Length <= 12;
            if (best.Key == AssistantIntent.Greeting && shortText && best.Value > 0.5)
            {
                reply.Title = "你好";
                reply.Text = "你好！我是 ASSAM 離線組合語言助理。可以問我指令、觀念、做法，或貼上程式碼請我解釋、推導執行結果。輸入 /help 看更多用法。";
                return;
            }
            if (best.Key == AssistantIntent.Help && best.Value > 0.5 && shortText)
            {
                reply.Title = "使用說明";
                reply.Text = HelpText;
                return;
            }

            reply.Title = "知識查詢";
            var hits = Search.Search(question, 8);
            reply.Hits.AddRange(hits);
            var text = new StringBuilder();
            if ((best.Key == AssistantIntent.ExplainCode || best.Key == AssistantIntent.Simulate || best.Key == AssistantIntent.DetectArch) && best.Value > 0.6)
                text.Append("（看起來你想分析程式碼，但我沒有看到程式。請把組合語言程式碼貼在問題的下一行，或使用「程式碼分析」「推導模擬」頁。）\r\n\r\n");

            if (hits.Count == 0)
            {
                text.Append("抱歉，知識庫中找不到相關資料。\r\n");
                text.Append("・試試換個說法或加上指令名稱（例如 LOOP、DJNZ、LDR）\r\n");
                text.Append("・如果你知道答案，可以按「教我正確答案」把它存進我的記憶，下次就會回答了。");
                reply.Text = text.ToString();
                return;
            }

            var top = hits[0];
            if (top.TextScore < 2.0 && !top.BoostedByMemory)
                text.Append("（我不太確定這是你要的答案，請看看下面的其他相關主題。）\r\n\r\n");
            if (top.BoostedByMemory)
                text.Append("（★ 依照你過去的回饋調整了排名）\r\n\r\n");
            text.Append(top.Entry.ToDisplayText());

            string note = InstructionNoteForEntry(top.Entry);
            if (note != null) text.Append("\r\n\r\n【你的筆記】").Append(note);

            if (hits.Count > 1)
            {
                text.Append("\r\n\r\n【其他相關主題】\r\n");
                foreach (var h in hits.Skip(1).Take(6)) text.Append("・").Append(h).Append("\r\n");
            }
            reply.Text = text.ToString().TrimEnd();
        }

        private void AppendRelated(StringBuilder sb, AssistantReply reply, string query, string arch)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            var hits = Search.Search(query, 5, arch);
            if (hits.Count == 0) return;
            reply.Hits.AddRange(hits.Where(h => reply.Hits.All(x => x.Entry.Id != h.Entry.Id)));
            sb.Append("\r\n【相關知識】（可在右側清單點選查看）\r\n");
            foreach (var h in hits) sb.Append("・").Append(h.Entry.Title).Append("\r\n");
        }

        private void Record(AssistantReply reply)
        {
            var top = reply.TopHit;
            var item = new HistoryItem
            {
                Time = UserMemoryData.Now(),
                Question = reply.Question.Length > 200 ? reply.Question.Substring(0, 200) + "…" : reply.Question,
                Intent = AssistantIntentInfo.DisplayName(reply.Intent),
                TopEntryId = top != null ? top.Entry.Id : null,
                TopTitle = top != null ? top.Entry.Title : reply.Title
            };
            reply.History = item;
            Memory.AddHistory(item);
            Save();
        }

        // =====================================================================
        // 學習
        // =====================================================================

        /// <summary>對回答按「有幫助／沒幫助」。回傳要顯示給使用者的訊息。</summary>
        public string Feedback(AssistantReply reply, bool helpful)
        {
            if (reply == null) return "沒有可以回饋的回答。";
            if (reply.History != null) reply.History.Feedback = helpful ? 1 : -1;
            Memory.Data.Increment(helpful ? "feedback_positive" : "feedback_negative");
            var top = reply.TopHit;
            string message;
            if (top != null && reply.Intent != AssistantIntent.ExplainCode && reply.Intent != AssistantIntent.Simulate)
            {
                SearchEngine.Learn(Memory.Data, reply.QuestionText ?? reply.Question, top.Entry.Id, helpful);
                message = helpful
                    ? "謝謝！已記住：類似的問題會優先回答「" + top.Entry.Title + "」。"
                    : "了解，已降低「" + top.Entry.Title + "」對這個問題的排名。可以從右側清單點選正確的主題並按「這個才是答案」，或按「教我正確答案」。";
            }
            else
            {
                message = helpful ? "謝謝你的回饋！" : "收到。如果是架構判斷錯誤，請到「程式碼分析」頁更正；也可以按「教我正確答案」。";
            }
            Save();
            return message;
        }

        /// <summary>使用者指定「這個條目才是正確答案」。</summary>
        public string ConfirmEntry(AssistantReply reply, string entryId)
        {
            var entry = Knowledge.Get(entryId);
            if (entry == null) return "找不到這個知識條目。";
            string q = reply != null ? (reply.QuestionText ?? reply.Question) : string.Empty;
            if (string.IsNullOrWhiteSpace(q)) return "沒有對應的問題可以學習。";
            var top = reply.TopHit;
            if (top != null && !string.Equals(top.Entry.Id, entryId, StringComparison.OrdinalIgnoreCase))
                SearchEngine.Learn(Memory.Data, q, top.Entry.Id, false);
            SearchEngine.Learn(Memory.Data, q, entryId, true);
            SearchEngine.Learn(Memory.Data, q, entryId, true);
            if (reply.History != null) reply.History.Feedback = 1;
            Memory.Data.Increment("feedback_positive");
            Save();
            return "已記住：「" + q + "」的答案是「" + entry.Title + "」。";
        }

        /// <summary>教我新知識：問題 + 答案存成知識卡，並與問題中的詞建立強關聯。</summary>
        public KnowledgeEntry Teach(string question, string answer, string arch = null, string category = null, IEnumerable<string> tags = null)
        {
            if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("請輸入問題或標題");
            if (string.IsNullOrWhiteSpace(answer)) throw new ArgumentException("請輸入答案內容");
            string title = question.Trim();
            if (title.Length > 80) title = title.Substring(0, 80) + "…";
            var entry = new KnowledgeEntry
            {
                Id = "user-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 4),
                Title = title,
                Body = answer.Trim(),
                Arch = string.IsNullOrWhiteSpace(arch) ? "通用" : arch.Trim(),
                Category = string.IsNullOrWhiteSpace(category) ? KnowledgeEntry.CategoryUser : category.Trim(),
                Tags = tags != null ? tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct().ToList() : new List<string>(),
                Source = "使用者教學",
                IsUserEntry = true,
                CreatedAt = UserMemoryData.Now()
            };
            Memory.Data.TaughtEntries.Add(entry);
            Knowledge.AddOrUpdate(entry);
            SearchEngine.Learn(Memory.Data, question, entry.Id, true);
            SearchEngine.Learn(Memory.Data, question, entry.Id, true);
            Memory.Data.Increment("taught");
            Save();
            return entry;
        }

        /// <summary>新增或修改使用者知識（知識庫頁使用）。</summary>
        public void SaveUserEntry(KnowledgeEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            entry.IsUserEntry = true;
            if (string.IsNullOrEmpty(entry.Id))
                entry.Id = "user-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            if (string.IsNullOrEmpty(entry.CreatedAt)) entry.CreatedAt = UserMemoryData.Now();
            if (string.IsNullOrEmpty(entry.Source)) entry.Source = "使用者教學";
            int idx = Memory.Data.TaughtEntries.FindIndex(e => string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) Memory.Data.TaughtEntries[idx] = entry;
            else Memory.Data.TaughtEntries.Add(entry);
            Knowledge.AddOrUpdate(entry);
            Save();
        }

        public bool DeleteUserEntry(string id)
        {
            int removed = Memory.Data.TaughtEntries.RemoveAll(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            Knowledge.Remove(id);
            foreach (var map in Memory.Data.TermEntryWeights.Values) map.Remove(id);
            foreach (var key in Memory.Data.QueryAnswers.Where(kv => string.Equals(kv.Value, id, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
                Memory.Data.QueryAnswers.Remove(key);
            Save();
            return true;
        }

        public bool CorrectArchitecture(string code, AsmArch actual)
        {
            bool changed = Detector.Learn(code, actual);
            Memory.Data.Increment("arch_corrections");
            Save();
            return changed;
        }

        public void CorrectIntent(string text, AssistantIntent actual)
        {
            Intents.Learn(text, actual);
            Memory.Data.Increment("intent_corrections");
            Save();
        }

        public void SaveInstructionNote(string archKey, string mnemonic, string note)
        {
            string key = archKey + ":" + mnemonic.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(note)) Memory.Data.InstructionNotes.Remove(key);
            else Memory.Data.InstructionNotes[key] = note.Trim();
            Save();
        }

        public string GetInstructionNote(string archKey, string mnemonic)
        {
            string note;
            return Memory.Data.InstructionNotes.TryGetValue(archKey + ":" + mnemonic.ToUpperInvariant(), out note) ? note : null;
        }

        /// <summary>指令卡的 Id 格式為 ins-{架構}-{助憶碼}。</summary>
        public static bool TryParseInstructionEntryId(string id, out string archKey, out string mnemonic)
        {
            archKey = mnemonic = null;
            if (string.IsNullOrEmpty(id) || !id.StartsWith("ins-", StringComparison.Ordinal)) return false;
            int dash = id.IndexOf('-', 4);
            if (dash < 0) return false;
            archKey = id.Substring(4, dash - 4);
            mnemonic = id.Substring(dash + 1).Replace('_', '.');
            return true;
        }

        private string InstructionNoteForEntry(KnowledgeEntry entry)
        {
            string arch, mn;
            if (!TryParseInstructionEntryId(entry.Id, out arch, out mn)) return null;
            return GetInstructionNote(arch, mn);
        }

        public string StatsText()
        {
            var d = Memory.Data;
            var sb = new StringBuilder();
            sb.Append("記憶檔位置：").Append(Memory.MemoryFilePath).Append("\r\n");
            sb.Append("自訂知識資料夾：").Append(Memory.KnowledgeDirectory).Append("（放入 .md 知識檔後按「重新載入知識庫」）\r\n");
            sb.Append("建立時間：").Append(d.CreatedAt).Append("　最後更新：").Append(d.UpdatedAt ?? "-").Append("\r\n\r\n");
            sb.Append("知識庫總數：").Append(Knowledge.Count).Append(" 筆（指令卡 ").Append(Knowledge.Entries.Count(e => e.Category == KnowledgeEntry.CategoryInstruction))
              .Append("、使用者知識 ").Append(Knowledge.Entries.Count(e => e.IsUserEntry)).Append("）\r\n");
            sb.Append("問過的問題：").Append(d.GetCounter("questions")).Append(" 次\r\n");
            sb.Append("回饋：有幫助 ").Append(d.GetCounter("feedback_positive")).Append(" 次、沒幫助 ").Append(d.GetCounter("feedback_negative")).Append(" 次\r\n");
            sb.Append("教我的知識：").Append(d.TaughtEntries.Count).Append(" 筆\r\n");
            sb.Append("學到的「詞 → 答案」權重：").Append(d.LearnedTermWeightCount).Append(" 項；記住的整句答案：").Append(d.QueryAnswers.Count).Append(" 句\r\n");
            sb.Append("架構辨識更正：").Append(d.ArchSamples.Count).Append(" 次（調整了 ").Append(d.ArchWeightDeltas.Values.Sum(m => m.Count)).Append(" 個特徵權重）\r\n");
            sb.Append("問題類型更正：").Append(d.IntentSamples.Count).Append(" 次\r\n");
            sb.Append("指令筆記：").Append(d.InstructionNotes.Count).Append(" 則\r\n");
            sb.Append("歷史紀錄：").Append(d.History.Count).Append(" 筆（最多保留 ").Append(MemoryStore.MaxHistory).Append(" 筆）\r\n");
            return sb.ToString();
        }

        // =====================================================================
        // 指令（/help 等）
        // =====================================================================

        public static readonly string HelpText =
            "ASSAM 離線組合語言 AI 助理 v" + Version + "（完全離線，不連網）\r\n\r\n" +
            "【可以這樣問】\r\n" +
            "・觀念／指令：什麼是二補數？LOOP 指令怎麼用？8051 的 DJNZ？ARM 的 LDR？\r\n" +
            "・做法：怎麼找陣列最大值？如何印出十進位數字？跑馬燈怎麼寫？\r\n" +
            "・解釋程式：在問題下一行貼上程式碼，例如：\r\n" +
            "      幫我解釋\r\n      mov ecx, 5\r\n      L1: add eax, ecx\r\n      loop L1\r\n" +
            "・推導結果：在問題中寫「執行後 EAX 是多少」再貼上 x86 程式碼（可加初值，例如 EAX=5）\r\n" +
            "・辨識架構：「這是什麼組合語言？」＋程式碼\r\n" +
            "・數值推導：0x7F 轉二進位、-5 的 8 位元二補數、8 位元 7Fh + 1 的旗標、1234:0010 實體位址、\r\n" +
            "            8051 12MHz 延遲 250 200、12MHz 計時器 50ms 初值、11.0592MHz 鮑率 9600、SHR 0B4h 2\r\n\r\n" +
            "【快速指令】\r\n" +
            "・/help 說明　/stats 學習統計　/teach 問題 => 答案　/explain 程式碼　/sim 程式碼　/detect 程式碼\r\n\r\n" +
            "【學習記憶】\r\n" +
            "・按「有幫助／沒幫助」我會調整答案排名；按「教我正確答案」可以新增知識。\r\n" +
            "・在程式碼分析頁更正架構、在問答頁更正問題類型，我都會記住。\r\n" +
            "・所有記憶都存在本機 UserData\\memory.json，可以匯出備份或帶到其他電腦。";

        private void HandleCommand(AssistantReply reply)
        {
            string text = reply.Question.Substring(1);
            int space = text.IndexOfAny(new[] { ' ', '\n', '\r', '\t' });
            string cmd = (space < 0 ? text : text.Substring(0, space)).ToLowerInvariant();
            string arg = space < 0 ? string.Empty : text.Substring(space + 1).Trim();
            reply.QuestionText = arg;

            switch (cmd)
            {
                case "help":
                case "?":
                    reply.Intent = AssistantIntent.Help;
                    reply.Title = "使用說明";
                    reply.Text = HelpText;
                    return;
                case "stats":
                    reply.Intent = AssistantIntent.Help;
                    reply.Title = "學習統計";
                    reply.Text = StatsText();
                    return;
                case "teach":
                    {
                        reply.Intent = AssistantIntent.Help;
                        reply.Title = "教學";
                        int sep = arg.IndexOf("=>", StringComparison.Ordinal);
                        if (sep < 0) { reply.Text = "格式：/teach 問題 => 答案"; return; }
                        try
                        {
                            var e = Teach(arg.Substring(0, sep), arg.Substring(sep + 2));
                            reply.Text = "學會了！已新增知識「" + e.Title + "」。";
                        }
                        catch (ArgumentException ex)
                        {
                            reply.Text = ex.Message;
                        }
                        return;
                    }
                case "explain":
                    AnswerWithCode(reply, "解釋", arg);
                    return;
                case "sim":
                case "trace":
                    AnswerWithCode(reply, "推導執行結果", arg);
                    return;
                case "detect":
                    AnswerWithCode(reply, "這是什麼架構", arg);
                    return;
                default:
                    reply.Intent = AssistantIntent.Help;
                    reply.Title = "未知指令";
                    reply.Text = "不認識的指令 /" + cmd + "，輸入 /help 查看可用的指令。";
                    return;
            }
        }
    }
}
