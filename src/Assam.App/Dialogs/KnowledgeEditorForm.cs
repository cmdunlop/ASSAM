using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Assam.Core.Knowledge;

namespace Assam.App.Dialogs
{
    /// <summary>新增／編輯知識卡（也用於「教我正確答案」）。</summary>
    [DesignerCategory("Code")]
    public sealed class KnowledgeEditorForm : Form
    {
        private readonly TextBox _title;
        private readonly ComboBox _arch;
        private readonly ComboBox _category;
        private readonly TextBox _tags;
        private readonly TextBox _body;

        public KnowledgeEditorForm(string caption, string title, string body, string arch, string category, string tags)
        {
            Text = caption;
            Font = Ui.UiFont;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(720, 560);
            MinimumSize = new Size(560, 420);
            MinimizeBox = false;
            ShowInTaskbar = false;

            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new Padding(8) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _title = new TextBox { Dock = DockStyle.Fill, Text = title ?? string.Empty };
            _arch = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            _arch.Items.AddRange(new object[] { "通用", "x86", "8051", "ARM", "ARM64", "MIPS", "RISC-V", "AVR" });
            _arch.Text = string.IsNullOrEmpty(arch) ? "通用" : arch;
            _category = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            _category.Items.AddRange(new object[]
            {
                KnowledgeEntry.CategoryUser, KnowledgeEntry.CategoryConcept, KnowledgeEntry.CategoryHowTo,
                KnowledgeEntry.CategoryDerivation, KnowledgeEntry.CategoryExample, KnowledgeEntry.CategoryDebug
            });
            _category.Text = string.IsNullOrEmpty(category) ? KnowledgeEntry.CategoryUser : category;
            _tags = new TextBox { Dock = DockStyle.Fill, Text = tags ?? string.Empty };
            _body = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = true,
                Font = Ui.CodeFont,
                Text = (body ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n")
            };

            AddRow(table, 0, "標題／問題：", _title);
            AddRow(table, 1, "架構：", _arch);
            AddRow(table, 2, "分類：", _category);
            AddRow(table, 3, "標籤（逗號分隔）：", _tags);
            AddRow(table, 4, "內容／答案：", _body);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            var cancel = Ui.Button("取消", null, 90);
            cancel.DialogResult = DialogResult.Cancel;
            var ok = Ui.Button("儲存", (s, e) => OnSave(), 90);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            table.Controls.Add(buttons, 0, 5);
            table.SetColumnSpan(buttons, 2);

            Controls.Add(table);
            AcceptButton = null; // 內容框需要 Enter 換行
            CancelButton = cancel;
        }

        private static void AddRow(TableLayoutPanel table, int row, string label, Control control)
        {
            var l = Ui.Label(label);
            l.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            table.Controls.Add(l, 0, row);
            table.Controls.Add(control, 1, row);
        }

        private void OnSave()
        {
            if (string.IsNullOrWhiteSpace(_title.Text) || string.IsNullOrWhiteSpace(_body.Text))
            {
                Ui.ShowError(this, "標題與內容都必須填寫。");
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        public string EntryTitle
        {
            get { return _title.Text.Trim(); }
        }

        public string EntryBody
        {
            get { return _body.Text.Replace("\r\n", "\n").Trim(); }
        }

        public string EntryArch
        {
            get { return _arch.Text.Trim(); }
        }

        public string EntryCategory
        {
            get { return _category.Text.Trim(); }
        }

        public List<string> EntryTags
        {
            get { return KnowledgeParser.SplitTags(_tags.Text); }
        }
    }
}
