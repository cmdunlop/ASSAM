using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Assam.App.Dialogs;
using Assam.Core.Assistant;
using Assam.Core.Search;

namespace Assam.App.Pages
{
    /// <summary>智慧問答：輸入問題或程式碼，得到解答；可回饋、教學、更正問題類型。</summary>
    [DesignerCategory("Code")]
    public sealed class ChatPage : UserControl
    {
        private readonly AsmAssistant _assistant;
        private readonly Action<string> _status;
        private readonly RichTextBox _conversation;
        private readonly TextBox _input;
        private readonly Button _helpful;
        private readonly Button _notHelpful;
        private readonly Button _teach;
        private readonly Label _intentLabel;
        private readonly ComboBox _intentCombo;
        private readonly Button _intentFix;
        private readonly ListBox _hits;
        private readonly RichTextBox _preview;
        private readonly Button _confirm;
        private AssistantReply _last;

        public event Action KnowledgeChanged;

        public ChatPage(AsmAssistant assistant, Action<string> status)
        {
            _assistant = assistant;
            _status = status;

            var split = Ui.Split(Orientation.Vertical, 0.66);
            Controls.Add(split);

            // ---- 左側：對話 ----
            var left = Ui.Rows(100, -1, -1);
            _conversation = Ui.ReadOnlyText();
            _conversation.BackColor = Color.FromArgb(252, 252, 250);
            left.Controls.Add(_conversation, 0, 0);

            var inputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true };
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _input = new TextBox
            {
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                Font = Ui.CodeFont,
                Dock = DockStyle.Fill,
                Height = 96
            };
            _input.KeyDown += OnInputKeyDown;
            inputRow.Controls.Add(_input, 0, 0);
            var sendPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
            sendPanel.Controls.Add(Ui.Button("送出（Ctrl+Enter）", (s, e) => AskQuestion(_input.Text), 150));
            sendPanel.Controls.Add(Ui.Button("清除畫面", (s, e) => _conversation.Clear(), 150));
            inputRow.Controls.Add(sendPanel, 1, 0);
            left.Controls.Add(inputRow, 0, 1);

            var feedback = Ui.Toolbar();
            _helpful = Ui.Button("有幫助", (s, e) => SendFeedback(true));
            _notHelpful = Ui.Button("沒幫助", (s, e) => SendFeedback(false));
            _teach = Ui.Button("教我正確答案…", (s, e) => Teach());
            _intentLabel = Ui.Label("問題類型：-");
            _intentCombo = Ui.Combo(130, AssistantIntentInfo.All.Select(i => (object)new IntentItem(i)).ToArray());
            _intentFix = Ui.Button("更正類型並學習", (s, e) => FixIntent());
            feedback.Controls.AddRange(new Control[] { _helpful, _notHelpful, _teach, _intentLabel, _intentCombo, _intentFix });
            left.Controls.Add(feedback, 0, 2);
            split.Panel1.Controls.Add(left);

            // ---- 右側：相關知識 ----
            var right = Ui.Rows(-1, 40, 60, -1);
            right.Controls.Add(Ui.Label("相關知識（點選可預覽；★ 表示依你的回饋調整過）", true), 0, 0);
            _hits = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
            _hits.SelectedIndexChanged += (s, e) => ShowHit();
            _hits.DoubleClick += (s, e) => ShowHit();
            right.Controls.Add(_hits, 0, 1);
            _preview = Ui.ReadOnlyText();
            right.Controls.Add(_preview, 0, 2);
            _confirm = Ui.Button("這個才是答案（記住）", (s, e) => ConfirmSelected());
            right.Controls.Add(_confirm, 0, 3);
            split.Panel2.Controls.Add(right);

            SetFeedbackEnabled(false);
            AppendSystem("歡迎使用 ASSAM 離線組合語言 AI 助理！\r\n" +
                         "可以問觀念、指令、做法，或在問題下一行貼上程式碼請我解釋、推導執行結果。\r\n" +
                         "例如：「LOOP 指令怎麼用」「-5 的 8 位元二補數」「8051 12MHz 延遲 200 250」。輸入 /help 查看完整說明。\r\n");
        }

        private sealed class IntentItem
        {
            public IntentItem(AssistantIntent intent)
            {
                Intent = intent;
            }

            public AssistantIntent Intent { get; private set; }

            public override string ToString()
            {
                return AssistantIntentInfo.DisplayName(Intent);
            }
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AskQuestion(_input.Text);
            }
        }

        public void AskQuestion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                AppendUser(text.Trim());
                _last = _assistant.Ask(text);
                AppendAssistant(_last);
                _input.Clear();

                _hits.BeginUpdate();
                _hits.Items.Clear();
                foreach (var h in _last.Hits) _hits.Items.Add(h);
                _hits.EndUpdate();
                _preview.Clear();

                _intentLabel.Text = "問題類型：" + AssistantIntentInfo.DisplayName(_last.Intent) + "（" +
                                    (_last.IntentConfidence * 100).ToString("0", CultureInfo.InvariantCulture) + "%）";
                foreach (IntentItem item in _intentCombo.Items)
                    if (item.Intent == _last.Intent) _intentCombo.SelectedItem = item;
                SetFeedbackEnabled(true);
                _status(_assistant.LastSaveError != null ? "記憶儲存失敗：" + _assistant.LastSaveError : "已回答。覺得有用請按「有幫助」，我會記住。");
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void SetFeedbackEnabled(bool enabled)
        {
            _helpful.Enabled = enabled;
            _notHelpful.Enabled = enabled;
            _intentFix.Enabled = enabled;
            _confirm.Enabled = enabled;
        }

        private void AppendUser(string text)
        {
            _conversation.SelectionStart = _conversation.TextLength;
            _conversation.SelectionFont = Ui.BoldFont;
            _conversation.SelectionColor = Color.FromArgb(0, 70, 160);
            _conversation.AppendText("\r\n你：" + text + "\r\n");
        }

        private void AppendAssistant(AssistantReply reply)
        {
            _conversation.SelectionStart = _conversation.TextLength;
            _conversation.SelectionFont = Ui.BoldFont;
            _conversation.SelectionColor = Color.FromArgb(0, 110, 60);
            _conversation.AppendText("ASSAM（" + reply.Title + "）：\r\n");
            _conversation.SelectionFont = Ui.CodeFont;
            _conversation.SelectionColor = Color.Black;
            _conversation.AppendText(reply.Text + "\r\n");
            _conversation.ScrollToCaret();
        }

        private void AppendSystem(string text)
        {
            _conversation.SelectionStart = _conversation.TextLength;
            _conversation.SelectionFont = Ui.UiFont;
            _conversation.SelectionColor = Color.DimGray;
            _conversation.AppendText(text);
        }

        private void ShowHit()
        {
            var hit = _hits.SelectedItem as SearchHit;
            if (hit == null) return;
            _preview.Text = hit.Entry.ToDisplayText() + "\r\n\r\n（相關分數 " + hit.Score.ToString("0.00", CultureInfo.InvariantCulture) +
                            (hit.BoostedByMemory ? "，其中學習記憶加分 " + hit.LearnedScore.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty) + "）";
        }

        private void SendFeedback(bool helpful)
        {
            if (_last == null) return;
            string message = _assistant.Feedback(_last, helpful);
            AppendSystem("【學習】" + message + "\r\n");
            _status(message);
            _helpful.Enabled = _notHelpful.Enabled = false;
        }

        private void ConfirmSelected()
        {
            var hit = _hits.SelectedItem as SearchHit;
            if (_last == null || hit == null)
            {
                Ui.ShowInfo(this, "請先在上方清單選擇一個知識條目。");
                return;
            }
            string message = _assistant.ConfirmEntry(_last, hit.Entry.Id);
            AppendSystem("【學習】" + message + "\r\n");
            _status(message);
        }

        private void Teach()
        {
            string question = _last != null ? (_last.QuestionText ?? _last.Question) : _input.Text;
            using (var dlg = new KnowledgeEditorForm("教我正確答案", question, string.Empty, "通用", Core.Knowledge.KnowledgeEntry.CategoryUser, string.Empty))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var entry = _assistant.Teach(dlg.EntryTitle, dlg.EntryBody, dlg.EntryArch, dlg.EntryCategory, dlg.EntryTags);
                    AppendSystem("【學習】學會了！已新增知識「" + entry.Title + "」，下次問類似問題會優先回答。\r\n");
                    _status("已新增使用者知識：" + entry.Title);
                    var handler = KnowledgeChanged;
                    if (handler != null) handler();
                }
                catch (ArgumentException ex)
                {
                    Ui.ShowError(this, ex.Message);
                }
            }
        }

        private void FixIntent()
        {
            var item = _intentCombo.SelectedItem as IntentItem;
            if (_last == null || item == null) return;
            string text = string.IsNullOrWhiteSpace(_last.QuestionText) ? _last.Question : _last.QuestionText;
            _assistant.CorrectIntent(text, item.Intent);
            string message = "已記住：「" + text + "」屬於「" + item + "」。";
            AppendSystem("【學習】" + message + "\r\n");
            _status(message);
        }
    }
}
