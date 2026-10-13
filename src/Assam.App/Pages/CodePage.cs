using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Assam.Core.Analysis;
using Assam.Core.Assistant;
using Assam.Core.Samples;

namespace Assam.App.Pages
{
    /// <summary>程式碼分析：自動辨識架構、逐行解釋、找出常見錯誤，並可更正架構讓辨識器學習。</summary>
    [DesignerCategory("Code")]
    public sealed class CodePage : UserControl
    {
        private readonly AsmAssistant _assistant;
        private readonly Action<string> _status;
        private readonly ComboBox _arch;
        private readonly ComboBox _samples;
        private readonly TextBox _code;
        private readonly DataGridView _grid;
        private readonly RichTextBox _summary;
        private CodeExplanation _last;

        public event Action<string> SendToSimulation;

        internal sealed class ArchItem
        {
            public ArchItem(AsmArch arch)
            {
                Arch = arch;
            }

            public AsmArch Arch { get; private set; }

            public override string ToString()
            {
                return Arch == AsmArch.Unknown ? "自動偵測" : AsmArchInfo.DisplayName(Arch);
            }
        }

        public CodePage(AsmAssistant assistant, Action<string> status)
        {
            _assistant = assistant;
            _status = status;

            var layout = Ui.Rows(-1, 100);
            Controls.Add(layout);

            var bar = Ui.Toolbar();
            bar.Controls.Add(Ui.Label("架構："));
            _arch = Ui.Combo(260, new object[] { new ArchItem(AsmArch.Unknown) }.Concat(AsmArchInfo.All.Select(a => (object)new ArchItem(a))).ToArray());
            bar.Controls.Add(_arch);
            bar.Controls.Add(Ui.Button("分析／逐行解釋", (s, e) => Analyze()));
            bar.Controls.Add(Ui.Button("更正架構並學習", (s, e) => CorrectArch()));
            bar.Controls.Add(Ui.Label("　範例："));
            _samples = Ui.Combo(260, new object[] { "（選擇範例程式）" }.Concat(SampleCode.All.Cast<object>()).ToArray());
            _samples.SelectedIndexChanged += (s, e) => LoadSample();
            bar.Controls.Add(_samples);
            bar.Controls.Add(Ui.Button("開啟檔案…", (s, e) => OpenFile()));
            bar.Controls.Add(Ui.Button("送到推導模擬", (s, e) =>
            {
                var handler = SendToSimulation;
                if (handler != null) handler(_code.Text);
            }));
            layout.Controls.Add(bar, 0, 0);

            var split = Ui.Split(Orientation.Horizontal, 0.38);
            _code = Ui.CodeBox();
            _code.Text = SampleCode.All[2].Code.Replace("\n", "\r\n");
            split.Panel1.Controls.Add(_code);

            var bottom = Ui.Split(Orientation.Vertical, 0.64);
            _grid = Ui.Grid();
            _grid.Columns.Add(Ui.Column("行", 44));
            _grid.Columns.Add(Ui.Column("類型", 70));
            _grid.Columns.Add(Ui.Column("程式碼", 220));
            _grid.Columns.Add(Ui.Column("說明", 360, true));
            _grid.Columns.Add(Ui.Column("動作（虛擬碼）", 180));
            _grid.SelectionChanged += (s, e) => HighlightLine();
            bottom.Panel1.Controls.Add(_grid);
            _summary = Ui.ReadOnlyText();
            bottom.Panel2.Controls.Add(_summary);
            split.Panel2.Controls.Add(bottom);
            layout.Controls.Add(split, 0, 1);
            Analyze();
        }

        public void LoadCode(string code, string source = null)
        {
            _code.Text = (code ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n");
            _arch.SelectedIndex = 0;
            Analyze();
            if (source != null) _status("已載入 " + source);
        }

        private void LoadSample()
        {
            var sample = _samples.SelectedItem as SampleProgram;
            if (sample == null) return;
            LoadCode(sample.Code, "範例：" + sample.Name);
        }

        private void OpenFile()
        {
            using (var dlg = new OpenFileDialog { Filter = "組合語言檔案 (*.asm;*.s;*.S;*.a51;*.inc;*.txt)|*.asm;*.s;*.S;*.a51;*.inc;*.txt|所有檔案 (*.*)|*.*" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    LoadCode(Ui.ReadTextFile(dlg.FileName), Path.GetFileName(dlg.FileName));
            }
        }

        private void Analyze()
        {
            if (string.IsNullOrWhiteSpace(_code.Text))
            {
                Ui.ShowInfo(this, "請先輸入或貼上組合語言程式碼。");
                return;
            }
            Cursor = Cursors.WaitCursor;
            try
            {
                var arch = ((ArchItem)_arch.SelectedItem).Arch;
                _last = _assistant.Explainer.Explain(_code.Text, arch);

                _grid.SuspendLayout();
                _grid.Rows.Clear();
                foreach (var line in _last.Lines.Where(l => l.Kind != "空白"))
                {
                    int row = _grid.Rows.Add(line.LineNumber, line.Kind, line.Code.Trim(), line.Explanation, line.Action ?? string.Empty);
                    if (line.Kind == "未知") _grid.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(255, 238, 220);
                    else if (line.Kind == "註解") _grid.Rows[row].DefaultCellStyle.ForeColor = Color.Gray;
                    else if (line.Kind != "指令") _grid.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(240, 245, 255);
                }
                _grid.ResumeLayout();

                var sb = new StringBuilder();
                sb.Append("【架構判斷】\r\n");
                sb.Append(_last.ArchSpecifiedByUser ? AsmArchInfo.DisplayName(_last.Arch) + "（手動指定）" : _last.Detection.Describe()).Append("\r\n\r\n");
                sb.Append("【程式結構】\r\n");
                foreach (var n in _last.Notes) sb.Append("・").Append(n).Append("\r\n");
                if (_last.Warnings.Count > 0)
                {
                    sb.Append("\r\n【注意事項】\r\n");
                    foreach (var w in _last.Warnings) sb.Append("▲ ").Append(w).Append("\r\n");
                }
                var hits = _assistant.Search.Search(AsmArchInfo.KnowledgeArch(_last.Arch) + " " + string.Join(" ", _last.Mnemonics.Take(6)), 6, AsmArchInfo.KnowledgeArch(_last.Arch));
                if (hits.Count > 0)
                {
                    sb.Append("\r\n【相關知識】（可到「知識庫」頁搜尋標題）\r\n");
                    foreach (var h in hits) sb.Append("・").Append(h.Entry.Title).Append("\r\n");
                }
                if (_last.Arch != AsmArch.X86Intel)
                    sb.Append("\r\n（推導模擬器支援 x86 Intel 語法；其他架構請參考逐行說明的「動作」欄手動追蹤。）\r\n");
                _summary.Text = sb.ToString();
                _status("分析完成：" + AsmArchInfo.DisplayName(_last.Arch) + "，" + _last.InstructionCount + " 行指令，" + _last.Warnings.Count + " 個注意事項。");
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void CorrectArch()
        {
            var item = (ArchItem)_arch.SelectedItem;
            if (item.Arch == AsmArch.Unknown)
            {
                Ui.ShowInfo(this, "請先在「架構」下拉選單選擇正確的架構，再按「更正架構並學習」。");
                return;
            }
            if (string.IsNullOrWhiteSpace(_code.Text)) return;
            var before = _assistant.Detector.Detect(_code.Text);
            _assistant.CorrectArchitecture(_code.Text, item.Arch);
            var after = _assistant.Detector.Detect(_code.Text);
            string message = "已學習：這段程式是 " + AsmArchInfo.DisplayName(item.Arch) + "。（原本判斷 " + AsmArchInfo.ShortName(before.Best) +
                             "，現在判斷 " + AsmArchInfo.ShortName(after.Best) + "）";
            _status(message);
            Ui.ShowInfo(this, message + "\r\n\r\n學習結果已存到本機記憶，下次遇到類似程式會判斷得更準。");
            _arch.SelectedIndex = 0;
            Analyze();
        }

        private void HighlightLine()
        {
            if (_grid.SelectedRows.Count == 0) return;
            var value = _grid.SelectedRows[0].Cells[0].Value;
            if (value is int) Ui.SelectLine(_code, (int)value);
        }
    }
}
