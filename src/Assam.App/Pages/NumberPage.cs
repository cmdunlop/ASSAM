using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Assam.Core.Derivation;

namespace Assam.App.Pages
{
    /// <summary>數值推導：進位轉換、二補數、旗標、實體位址、移位、延伸、位元組序、8051 延遲／計時器／鮑率。</summary>
    [DesignerCategory("Code")]
    public sealed class NumberPage : UserControl
    {
        private readonly Action<string> _status;
        private readonly RichTextBox _output;

        public NumberPage(Action<string> status)
        {
            _status = status;
            var split = Ui.Split(Orientation.Vertical, 0.42);
            Controls.Add(split);

            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(4)
            };
            split.Panel1.Controls.Add(panel);
            _output = Ui.ReadOnlyText();
            _output.WordWrap = false;
            split.Panel2.Controls.Add(_output);

            // 進位轉換
            var cvValue = Ui.TextBox(130, "0x2F");
            var cvBits = BitsCombo(true);
            panel.Controls.Add(Group("進位轉換（二／八／十／十六進位）", cvValue, Ui.Label("位元："), cvBits,
                Ui.Button("推導", (s, e) => Show("進位轉換", () => NumberDerivation.ConvertBase(cvValue.Text, Bits(cvBits))))));

            // 二補數
            var tcValue = Ui.TextBox(130, "-5");
            var tcBits = BitsCombo(false);
            panel.Controls.Add(Group("二補數（負數表示／有號解讀）", tcValue, Ui.Label("位元："), tcBits,
                Ui.Button("推導", (s, e) => Show("二補數", () => NumberDerivation.TwosComplement(tcValue.Text, Bits(tcBits))))));

            // 旗標
            var fa = Ui.TextBox(70, "7Fh");
            var fop = Ui.Combo(50, "+", "-");
            var fb = Ui.TextBox(70, "1");
            var fBits = BitsCombo(false);
            panel.Controls.Add(Group("加減法旗標（CF OF ZF SF PF AF）", fa, fop, fb, Ui.Label("位元："), fBits,
                Ui.Button("推導", (s, e) => Show("旗標", () => NumberDerivation.AddSubFlags(fa.Text, fb.Text, Bits(fBits), (string)fop.SelectedItem == "-")))));

            // 實體位址
            var seg = Ui.TextBox(70, "1234");
            var off = Ui.TextBox(70, "0010");
            panel.Controls.Add(Group("8086 實體位址（段:偏移，十六進位）", Ui.Label("段"), seg, Ui.Label("偏移"), off,
                Ui.Button("推導", (s, e) => Show("實體位址", () => NumberDerivation.PhysicalAddress(seg.Text, off.Text)))));

            // 移位
            var shValue = Ui.TextBox(70, "0B2h");
            var shOp = Ui.Combo(70, "SHL", "SHR", "SAR", "ROL", "ROR");
            var shCount = new NumericUpDown { Minimum = 0, Maximum = 31, Value = 1, Width = 50, Margin = new Padding(3, 4, 3, 3) };
            var shBits = Ui.Combo(60, "8", "16", "32");
            panel.Controls.Add(Group("移位／旋轉", shValue, shOp, Ui.Label("次數"), shCount, shBits,
                Ui.Button("推導", (s, e) => Show("移位", () => NumberDerivation.Shift(shValue.Text, (string)shOp.SelectedItem, (int)shCount.Value, int.Parse((string)shBits.SelectedItem, CultureInfo.InvariantCulture))))));

            // 延伸
            var exValue = Ui.TextBox(70, "0F0h");
            var exFrom = Ui.Combo(55, "8", "16", "32");
            var exTo = Ui.Combo(55, "16", "32", "64");
            panel.Controls.Add(Group("零延伸／符號延伸（MOVZX／MOVSX）", exValue, Ui.Label("從"), exFrom, Ui.Label("到"), exTo,
                Ui.Button("推導", (s, e) => Show("延伸", () => NumberDerivation.Extend(exValue.Text, int.Parse((string)exFrom.SelectedItem, CultureInfo.InvariantCulture), int.Parse((string)exTo.SelectedItem, CultureInfo.InvariantCulture))))));

            // 位元組序
            var enValue = Ui.TextBox(110, "12345678h");
            var enBytes = Ui.Combo(55, "4", "2", "8");
            panel.Controls.Add(Group("位元組序（小端／大端）", enValue, Ui.Label("位元組"), enBytes,
                Ui.Button("推導", (s, e) => Show("位元組序", () => NumberDerivation.Endian(enValue.Text, int.Parse((string)enBytes.SelectedItem, CultureInfo.InvariantCulture))))));

            // 8051 延遲
            var dlMhz = Ui.TextBox(60, "12");
            var dlLoops = Ui.TextBox(90, "200,250");
            var dlCall = new CheckBox { Text = "含 ACALL", Checked = true, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            panel.Controls.Add(Group("8051 延遲副程式（DJNZ 巢狀迴圈，由外到內）", Ui.Label("MHz"), dlMhz, Ui.Label("次數"), dlLoops, dlCall,
                Ui.Button("推導", (s, e) => Show("8051 延遲", () => NumberDerivation.Delay8051(ParseDouble(dlMhz.Text), ParseLoops(dlLoops.Text), dlCall.Checked)))));

            // 8051 計時器
            var tmMhz = Ui.TextBox(60, "12");
            var tmUs = Ui.TextBox(80, "50000");
            var tmMode = Ui.Combo(70, "模式 1", "模式 2", "模式 0");
            panel.Controls.Add(Group("8051 計時器初值（時間單位 μs）", Ui.Label("MHz"), tmMhz, Ui.Label("μs"), tmUs, tmMode,
                Ui.Button("推導", (s, e) => Show("8051 計時器", () => NumberDerivation.Timer8051(ParseDouble(tmMhz.Text), ParseDouble(tmUs.Text), tmMode.SelectedIndex == 0 ? 1 : tmMode.SelectedIndex == 1 ? 2 : 0)))));

            // 8051 鮑率
            var bdMhz = Ui.TextBox(70, "11.0592");
            var bdBaud = Ui.TextBox(70, "9600");
            var bdSmod = new CheckBox { Text = "SMOD=1", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            panel.Controls.Add(Group("8051 序列埠鮑率（Timer1 模式 2）", Ui.Label("MHz"), bdMhz, Ui.Label("鮑率"), bdBaud, bdSmod,
                Ui.Button("推導", (s, e) => Show("8051 鮑率", () => NumberDerivation.Baud8051(ParseDouble(bdMhz.Text), (int)ParseDouble(bdBaud.Text), bdSmod.Checked)))));

            _output.Text = "在左側輸入數值後按「推導」，這裡會列出一步一步的計算過程。\r\n\r\n" +
                           "數值格式：十進位 255、-5；十六進位 0x1F、1Fh、0FFh；二進位 1010b、0b1010；八進位 17o；字元 'A'。\r\n" +
                           "也可以直接在「智慧問答」輸入，例如「0x7F 轉二進位」「8 位元 7Fh + 1 的旗標」。";
        }

        /// <summary>一個工具群組：標題 + 一列輸入控制項（高度依內容計算，不使用 AutoSize 以避免版面撐大）。</summary>
        private static GroupBox Group(string title, params Control[] controls)
        {
            const int width = 455;
            var flow = new FlowLayoutPanel
            {
                WrapContents = true,
                Padding = new Padding(0),
                Margin = new Padding(0),
                Location = new System.Drawing.Point(8, 20),
                Width = width - 16
            };
            flow.Controls.AddRange(controls);
            int rowHeight = controls.Max(c => c.GetPreferredSize(System.Drawing.Size.Empty).Height + c.Margin.Vertical);
            int rowWidth = controls.Sum(c => c.GetPreferredSize(System.Drawing.Size.Empty).Width + c.Margin.Horizontal);
            int rows = Math.Max(1, (int)Math.Ceiling(rowWidth / (double)(width - 24)));
            flow.Height = rows * rowHeight + 4;
            var g = new GroupBox
            {
                Text = title,
                Width = width,
                Height = flow.Height + 30,
                Margin = new Padding(3, 3, 3, 6)
            };
            g.Controls.Add(flow);
            return g;
        }

        private static ComboBox BitsCombo(bool includeAuto)
        {
            return includeAuto ? Ui.Combo(70, "自動", "8", "16", "32", "64") : Ui.Combo(60, "8", "16", "32", "64");
        }

        private static int Bits(ComboBox combo)
        {
            int v;
            return int.TryParse((string)combo.SelectedItem, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static double ParseDouble(string text)
        {
            double v;
            if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                throw new ArgumentException("看不懂數值「" + text + "」");
            return v;
        }

        private static int[] ParseLoops(string text)
        {
            return text.Split(new[] { ',', ' ', '，', '、' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(t => (int)ParseDouble(t)).ToArray();
        }

        private void Show(string title, Func<string> derive)
        {
            try
            {
                _output.Text = "【" + title + "】\r\n\r\n" + derive();
                _status(title + "推導完成。");
            }
            catch (ArgumentException ex)
            {
                _output.Text = "無法推導：" + ex.Message;
            }
            catch (FormatException ex)
            {
                _output.Text = "格式錯誤：" + ex.Message;
            }
        }
    }
}
