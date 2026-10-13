using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Assam.Core.Analysis;
using Assam.Core.Assistant;
using Assam.Core.Samples;
using Assam.Core.Simulation;

namespace Assam.App.Pages
{
    /// <summary>推導模擬：逐步執行 x86 程式，每一步顯示暫存器、旗標、記憶體的變化與推導過程。</summary>
    [DesignerCategory("Code")]
    public sealed class SimulationPage : UserControl
    {
        private readonly AsmAssistant _assistant;
        private readonly Action<string> _status;
        private readonly TextBox _code;
        private readonly TextBox _initial;
        private readonly TextBox _input;
        private readonly NumericUpDown _maxSteps;
        private readonly ComboBox _samples;
        private readonly DataGridView _trace;
        private readonly RichTextBox _detail;
        private readonly ListView _registers;
        private readonly TextBox _output;
        private SimulationResult _result;

        public SimulationPage(AsmAssistant assistant, Action<string> status)
        {
            _assistant = assistant;
            _status = status;

            var layout = Ui.Rows(-1, 100);
            Controls.Add(layout);

            var bar = Ui.Toolbar();
            bar.Controls.Add(Ui.Label("初始暫存器："));
            _initial = Ui.TextBox(170);
            bar.Controls.Add(_initial);
            bar.Controls.Add(Ui.Label("模擬輸入："));
            _input = Ui.TextBox(110);
            bar.Controls.Add(_input);
            bar.Controls.Add(Ui.Label("最大步數："));
            _maxSteps = new NumericUpDown { Minimum = 10, Maximum = 200000, Value = 2000, Increment = 500, Width = 80, Margin = new Padding(3, 4, 3, 3) };
            bar.Controls.Add(_maxSteps);
            bar.Controls.Add(Ui.Button("執行推導", (s, e) => Run()));
            _samples = Ui.Combo(250, new object[] { "（x86 範例）" }.Concat(SampleCode.All.Where(x => x.Arch == AsmArch.X86Intel).Cast<object>()).ToArray());
            _samples.SelectedIndexChanged += (s, e) =>
            {
                var sample = _samples.SelectedItem as SampleProgram;
                if (sample != null) LoadCode(sample.Code);
            };
            bar.Controls.Add(_samples);
            var tips = new ToolTip();
            tips.SetToolTip(_initial, "例如：EAX=5, EBX=0x10");
            tips.SetToolTip(_input, "ReadInt、ReadChar、INT 21h/01h 讀取的內容，例如：12 30");
            layout.Controls.Add(bar, 0, 0);

            var split = Ui.Split(Orientation.Horizontal, 0.36);
            _code = Ui.CodeBox();
            _code.Text = SampleCode.All[0].Code.Replace("\n", "\r\n");
            split.Panel1.Controls.Add(_code);

            var bottom = Ui.Split(Orientation.Vertical, 0.55);
            _trace = Ui.Grid();
            _trace.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            _trace.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            _trace.Columns.Add(Ui.Column("步驟", 50));
            _trace.Columns.Add(Ui.Column("行", 40));
            _trace.Columns.Add(Ui.Column("指令", 170));
            _trace.Columns.Add(Ui.Column("變化", 300, true));
            _trace.SelectionChanged += (s, e) => ShowStep();
            bottom.Panel1.Controls.Add(_trace);

            var right = Ui.Rows(-1, 45, -1, 35, -1, 20);
            right.Controls.Add(Ui.Label("推導過程（點選左側步驟）", true), 0, 0);
            _detail = Ui.ReadOnlyText();
            right.Controls.Add(_detail, 0, 1);
            right.Controls.Add(Ui.Label("暫存器（該步驟執行後）", true), 0, 2);
            _registers = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, Font = Ui.CodeFont, GridLines = true };
            _registers.Columns.Add("暫存器", 80);
            _registers.Columns.Add("十六進位", 170);
            _registers.Columns.Add("十進位（無號／有號）", 230);
            right.Controls.Add(_registers, 0, 3);
            right.Controls.Add(Ui.Label("程式輸出與訊息", true), 0, 4);
            _output = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = Ui.CodeFont, Dock = DockStyle.Fill, WordWrap = false };
            right.Controls.Add(_output, 0, 5);
            bottom.Panel2.Controls.Add(right);
            split.Panel2.Controls.Add(bottom);
            layout.Controls.Add(split, 0, 1);
        }

        public void LoadCode(string code)
        {
            _code.Text = (code ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n");
            Run();
        }

        private void Run()
        {
            if (string.IsNullOrWhiteSpace(_code.Text)) return;
            var errors = new List<string>();
            var options = new SimulationOptions { MaxSteps = (int)_maxSteps.Value, Input = _input.Text.Replace("\r\n", "\n") };
            foreach (var kv in SimulationOptions.ParseRegisterAssignments(_initial.Text, errors)) options.InitialRegisters[kv.Key] = kv.Value;
            if (errors.Count > 0) Ui.ShowError(this, string.Join("\r\n", errors));

            var detection = _assistant.Detector.Detect(_code.Text);
            if (detection.Best != AsmArch.X86Intel && detection.Best != AsmArch.Unknown && detection.Confidence > 0.8)
            {
                var answer = MessageBox.Show(this, "這段程式看起來是 " + AsmArchInfo.DisplayName(detection.Best) + "。\r\n推導模擬器只支援 x86 Intel 語法，仍要嘗試執行嗎？\r\n（其他架構可到「程式碼分析」頁看逐行說明）",
                                             "ASSAM", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
            }

            Cursor = Cursors.WaitCursor;
            try
            {
                _result = _assistant.Simulator.Run(_code.Text, options);
                _trace.SuspendLayout();
                _trace.Rows.Clear();
                foreach (var step in _result.Steps)
                {
                    int row = _trace.Rows.Add(step.Step, step.LineNumber, step.Source, step.Changes);
                    if (step.Derivation.Contains("錯誤：")) _trace.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(255, 225, 225);
                    else if (step.Derivation.Contains("→ 跳到")) _trace.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(235, 245, 255);
                }
                _trace.ResumeLayout();

                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(_result.Output))
                    sb.Append("【程式輸出】\r\n").Append(_result.Output.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n\r\n");
                sb.Append("【結束原因】").Append(_result.StopReason).Append("\r\n");
                if (_result.Error != null) sb.Append("【錯誤】").Append(_result.Error).Append("\r\n");
                sb.Append("【最後的旗標】").Append(_result.FinalFlags).Append("\r\n");
                foreach (var d in _result.DataDump) sb.Append("【資料】").Append(d).Append("\r\n");
                foreach (var m in _result.Messages) sb.Append("・").Append(m).Append("\r\n");
                _output.Text = sb.ToString();

                if (_trace.Rows.Count > 0)
                {
                    int last = _trace.Rows.Count - 1;
                    _trace.ClearSelection();
                    _trace.Rows[last].Selected = true;
                    _trace.FirstDisplayedScrollingRowIndex = Math.Max(0, last - 5);
                }
                else
                {
                    ShowRegisters(_result.FinalRegisters);
                }
                _status("推導完成：共 " + _result.Steps.Count + " 步（" + _result.Bits + " 位元模式）。" + (_result.Error != null ? "發生錯誤：" + _result.Error : string.Empty));
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void ShowStep()
        {
            if (_result == null || _trace.SelectedRows.Count == 0) return;
            int index = _trace.SelectedRows[0].Index;
            if (index < 0 || index >= _result.Steps.Count) return;
            var step = _result.Steps[index];
            var sb = new StringBuilder();
            sb.Append("步驟 ").Append(step.Step).Append("（第 ").Append(step.LineNumber).Append(" 行）：").Append(step.Source).Append("\r\n\r\n");
            sb.Append(step.Derivation).Append("\r\n\r\n");
            sb.Append("變化：").Append(step.Changes).Append("\r\n");
            sb.Append("旗標：").Append(step.Flags).Append("\r\n");
            _detail.Text = sb.ToString();
            ShowRegisters(step.Registers);
            Ui.SelectLine(_code, step.LineNumber);
        }

        private void ShowRegisters(IEnumerable<KeyValuePair<string, string>> registers)
        {
            _registers.BeginUpdate();
            _registers.Items.Clear();
            foreach (var kv in registers)
            {
                string hex = kv.Value.StartsWith("0x", StringComparison.Ordinal) ? kv.Value.Substring(2) : kv.Value;
                ulong v;
                string dec = string.Empty;
                if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
                {
                    int bits = hex.Length * 4;
                    long signed = Core.Text.AsmNumber.ToSigned(v, bits);
                    dec = v.ToString(CultureInfo.InvariantCulture) + (signed < 0 ? " ／ " + signed.ToString(CultureInfo.InvariantCulture) : string.Empty);
                }
                _registers.Items.Add(new ListViewItem(new[] { kv.Key, kv.Value, dec }));
            }
            _registers.EndUpdate();
        }
    }
}
