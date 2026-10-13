using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Assam.App.Dialogs;
using Assam.Core.Assistant;
using Assam.Core.Knowledge;

namespace Assam.App.Pages
{
    /// <summary>知識庫：瀏覽／搜尋全部知識，新增、編輯、刪除自己的知識，並替指令寫筆記。</summary>
    [DesignerCategory("Code")]
    public sealed class KnowledgePage : UserControl
    {
        private readonly AsmAssistant _assistant;
        private readonly Action<string> _status;
        private readonly TextBox _search;
        private readonly ComboBox _archFilter;
        private readonly TreeView _tree;
        private readonly RichTextBox _content;
        private readonly TextBox _note;
        private readonly Button _saveNote;
        private KnowledgeEntry _current;

        public KnowledgePage(AsmAssistant assistant, Action<string> status)
        {
            _assistant = assistant;
            _status = status;

            var split = Ui.Split(Orientation.Vertical, 0.36);
            Controls.Add(split);

            // ---- 左側：搜尋與樹狀清單 ----
            var left = Ui.Rows(-1, 100);
            var bar = Ui.Toolbar();
            _search = Ui.TextBox(170);
            _search.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; RefreshTree(); }
            };
            _archFilter = Ui.Combo(90, "全部架構");
            bar.Controls.Add(_search);
            bar.Controls.Add(Ui.Button("搜尋", (s, e) => RefreshTree()));
            bar.Controls.Add(_archFilter);
            _archFilter.SelectedIndexChanged += (s, e) => RefreshTree();
            left.Controls.Add(bar, 0, 0);
            _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, Font = Ui.UiFont };
            _tree.AfterSelect += (s, e) => ShowEntry(e.Node.Tag as KnowledgeEntry);
            left.Controls.Add(_tree, 0, 1);
            split.Panel1.Controls.Add(left);

            // ---- 右側：內容與編輯 ----
            var right = Ui.Rows(-1, 100, -1);
            var actions = Ui.Toolbar();
            actions.Controls.Add(Ui.Button("新增知識…", (s, e) => AddEntry()));
            actions.Controls.Add(Ui.Button("編輯…", (s, e) => EditEntry()));
            actions.Controls.Add(Ui.Button("刪除", (s, e) => DeleteEntry()));
            actions.Controls.Add(Ui.Button("匯出我的知識（.md）…", (s, e) => ExportUserEntries()));
            actions.Controls.Add(Ui.Button("開啟自訂知識資料夾", (s, e) => MainForm.OpenFolder(_assistant.Memory.KnowledgeDirectory)));
            right.Controls.Add(actions, 0, 0);
            _content = Ui.ReadOnlyText();
            right.Controls.Add(_content, 0, 1);

            var noteBox = new GroupBox { Text = "我的指令筆記（選擇指令卡後可寫，問答時會一起顯示）", Dock = DockStyle.Fill, Height = 104, Padding = new Padding(6) };
            var noteRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            noteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            noteRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _note = new TextBox { Multiline = true, Height = 54, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
            _saveNote = Ui.Button("儲存筆記", (s, e) => SaveNote());
            noteRow.Controls.Add(_note, 0, 0);
            noteRow.Controls.Add(_saveNote, 1, 0);
            noteBox.Controls.Add(noteRow);
            right.Controls.Add(noteBox, 0, 2);
            split.Panel2.Controls.Add(right);

            RefreshArchFilter();
            RefreshTree();
            ShowEntry(null);
        }

        private void RefreshArchFilter()
        {
            string current = _archFilter.SelectedItem as string;
            _archFilter.Items.Clear();
            _archFilter.Items.Add("全部架構");
            foreach (var a in _assistant.Knowledge.Architectures()) _archFilter.Items.Add(a);
            _archFilter.SelectedItem = current != null && _archFilter.Items.Contains(current) ? current : "全部架構";
        }

        public void RefreshTree()
        {
            string arch = _archFilter.SelectedItem as string;
            bool allArch = arch == null || arch == "全部架構";
            string query = _search.Text.Trim();

            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            if (query.Length > 0)
            {
                var hits = _assistant.Search.Search(query, 40, allArch ? null : arch)
                                            .Where(h => allArch || string.Equals(h.Entry.Arch, arch, StringComparison.OrdinalIgnoreCase)).ToList();
                var root = _tree.Nodes.Add("搜尋「" + query + "」：" + hits.Count + " 筆");
                foreach (var h in hits) root.Nodes.Add(Node(h.Entry, h.ToString()));
                root.Expand();
            }
            else
            {
                var groups = _assistant.Knowledge.Entries
                    .Where(e => allArch || string.Equals(e.Arch, arch, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(e => e.IsUserEntry ? "★ 我的知識" : e.Arch ?? "通用")
                    .OrderBy(g => g.Key.StartsWith("★", StringComparison.Ordinal) ? "0" : g.Key == "通用" ? "1" : "2" + g.Key, StringComparer.OrdinalIgnoreCase);
                foreach (var g in groups)
                {
                    var archNode = _tree.Nodes.Add(g.Key + "（" + g.Count() + "）");
                    foreach (var c in g.GroupBy(e => e.Category ?? "其他").OrderBy(c => CategoryOrder(c.Key)))
                    {
                        var catNode = archNode.Nodes.Add(c.Key + "（" + c.Count() + "）");
                        foreach (var e in c.OrderBy(e => e.Title, StringComparer.CurrentCulture)) catNode.Nodes.Add(Node(e, e.Title));
                    }
                    if (!allArch || g.Key.StartsWith("★", StringComparison.Ordinal)) archNode.Expand();
                }
            }
            _tree.EndUpdate();
            _status("知識庫共 " + _assistant.Knowledge.Count + " 筆。");
        }

        private static int CategoryOrder(string category)
        {
            string[] order = { KnowledgeEntry.CategoryUser, KnowledgeEntry.CategoryConcept, KnowledgeEntry.CategoryHowTo, KnowledgeEntry.CategoryDerivation,
                               KnowledgeEntry.CategoryExample, KnowledgeEntry.CategoryDebug, KnowledgeEntry.CategoryInstruction };
            int i = Array.IndexOf(order, category);
            return i < 0 ? 99 : i;
        }

        private static TreeNode Node(KnowledgeEntry e, string text)
        {
            return new TreeNode(text) { Tag = e, ForeColor = e.IsUserEntry ? Color.DarkGreen : SystemColors.WindowText };
        }

        private void ShowEntry(KnowledgeEntry entry)
        {
            _current = entry;
            if (entry == null)
            {
                _content.Text = "從左側選擇一個知識條目，或輸入關鍵字搜尋。\r\n\r\n" +
                                "・綠色的是你自己新增或教我的知識，可以編輯與刪除\r\n" +
                                "・把 .md 知識檔放到「自訂知識資料夾」後，到「學習記憶」頁按「重新載入知識庫」即可加入\r\n" +
                                "・檔案格式請參考匯出的 .md 檔或 README";
                _note.Enabled = _saveNote.Enabled = false;
                _note.Text = string.Empty;
                return;
            }
            _content.Text = entry.ToDisplayText() + "\r\n\r\n（Id：" + entry.Id + "）";
            string archKey, mnemonic;
            bool isInstruction = AsmAssistant.TryParseInstructionEntryId(entry.Id, out archKey, out mnemonic);
            _note.Enabled = _saveNote.Enabled = isInstruction;
            _note.Text = isInstruction ? _assistant.GetInstructionNote(archKey, mnemonic) ?? string.Empty : string.Empty;
        }

        private bool IsEditable(KnowledgeEntry e)
        {
            return e != null && _assistant.Memory.Data.TaughtEntries.Any(t => string.Equals(t.Id, e.Id, StringComparison.OrdinalIgnoreCase));
        }

        private void AddEntry()
        {
            using (var dlg = new KnowledgeEditorForm("新增知識", string.Empty, string.Empty, "通用", KnowledgeEntry.CategoryConcept, string.Empty))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var entry = new KnowledgeEntry
                {
                    Title = dlg.EntryTitle,
                    Body = dlg.EntryBody,
                    Arch = dlg.EntryArch,
                    Category = dlg.EntryCategory,
                    Tags = dlg.EntryTags
                };
                _assistant.SaveUserEntry(entry);
                AfterChange(entry, "已新增知識：" + entry.Title);
            }
        }

        private void EditEntry()
        {
            var e = _current;
            if (e == null) { Ui.ShowInfo(this, "請先選擇一個知識條目。"); return; }
            bool editable = IsEditable(e);
            if (!editable && MessageBox.Show(this, "這是內建（或資料夾中的）知識，不能直接修改。\r\n要建立一份你自己的副本再編輯嗎？", "ASSAM",
                                             MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            using (var dlg = new KnowledgeEditorForm(editable ? "編輯知識" : "建立副本", e.Title, e.Body, e.Arch, editable ? e.Category : KnowledgeEntry.CategoryUser, e.TagText))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var entry = new KnowledgeEntry
                {
                    Id = editable ? e.Id : null,
                    CreatedAt = editable ? e.CreatedAt : null,
                    Title = dlg.EntryTitle,
                    Body = dlg.EntryBody,
                    Arch = dlg.EntryArch,
                    Category = dlg.EntryCategory,
                    Tags = dlg.EntryTags
                };
                _assistant.SaveUserEntry(entry);
                AfterChange(entry, (editable ? "已更新：" : "已建立副本：") + entry.Title);
            }
        }

        private void DeleteEntry()
        {
            var e = _current;
            if (e == null) return;
            if (!IsEditable(e))
            {
                Ui.ShowInfo(this, e.IsUserEntry ? "這筆知識來自自訂知識資料夾中的檔案，請直接編輯或刪除該 .md 檔。" : "內建知識不能刪除。");
                return;
            }
            if (MessageBox.Show(this, "確定要刪除「" + e.Title + "」嗎？", "ASSAM", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _assistant.DeleteUserEntry(e.Id);
            AfterChange(null, "已刪除：" + e.Title);
        }

        private void AfterChange(KnowledgeEntry select, string message)
        {
            RefreshArchFilter();
            RefreshTree();
            ShowEntry(select);
            _status(message);
        }

        private void ExportUserEntries()
        {
            var entries = _assistant.Memory.Data.TaughtEntries;
            if (entries.Count == 0) { Ui.ShowInfo(this, "目前還沒有你自己的知識。"); return; }
            using (var dlg = new SaveFileDialog { Filter = "知識檔 (*.md)|*.md", FileName = "my_knowledge.md" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(dlg.FileName, KnowledgeParser.Serialize(entries), new UTF8Encoding(true));
                _status("已匯出 " + entries.Count + " 筆知識到 " + dlg.FileName);
            }
        }

        private void SaveNote()
        {
            string archKey, mnemonic;
            if (_current == null || !AsmAssistant.TryParseInstructionEntryId(_current.Id, out archKey, out mnemonic)) return;
            _assistant.SaveInstructionNote(archKey, mnemonic, _note.Text);
            _status("已儲存 " + mnemonic.ToUpperInvariant() + " 的筆記。");
        }
    }
}
