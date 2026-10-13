using System;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace Assam.App
{
    /// <summary>介面共用的字型與控制項工廠（全部用程式碼建立，不需要設計工具檔）。</summary>
    internal static class Ui
    {
        private static Font _uiFont;
        private static Font _boldFont;
        private static Font _codeFont;
        private static Font _titleFont;

        public static Font UiFont
        {
            get { return _uiFont ?? (_uiFont = new Font(PickFont("Microsoft JhengHei UI", "Microsoft JhengHei", "PMingLiU", "Segoe UI"), 9.75f)); }
        }

        public static Font BoldFont
        {
            get { return _boldFont ?? (_boldFont = new Font(UiFont, FontStyle.Bold)); }
        }

        public static Font TitleFont
        {
            get { return _titleFont ?? (_titleFont = new Font(UiFont.FontFamily, 11f, FontStyle.Bold)); }
        }

        /// <summary>程式碼用等寬字型（中文會由 Windows 自動以其他字型補字）。</summary>
        public static Font CodeFont
        {
            get { return _codeFont ?? (_codeFont = new Font(PickFont("Consolas", "Lucida Console", "Courier New"), 10f)); }
        }

        private static string PickFont(params string[] names)
        {
            using (var installed = new InstalledFontCollection())
            {
                var families = installed.Families.Select(f => f.Name).ToList();
                foreach (var n in names)
                    if (families.Any(f => string.Equals(f, n, StringComparison.OrdinalIgnoreCase))) return n;
            }
            return SystemFonts.MessageBoxFont.FontFamily.Name;
        }

        public static Button Button(string text, EventHandler onClick, int minWidth = 0)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(6, 2, 6, 2),
                Margin = new Padding(3),
                UseVisualStyleBackColor = true
            };
            if (minWidth > 0) b.MinimumSize = new Size(minWidth, 0);
            if (onClick != null) b.Click += onClick;
            return b;
        }

        public static Label Label(string text, bool bold = false)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Margin = new Padding(3, 8, 3, 3),
                Font = bold ? BoldFont : UiFont
            };
        }

        public static TextBox TextBox(int width, string text = "")
        {
            return new TextBox { Width = width, Text = text, Margin = new Padding(3, 4, 3, 3) };
        }

        public static TextBox CodeBox()
        {
            return new TextBox
            {
                Multiline = true,
                AcceptsTab = true,
                AcceptsReturn = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Font = CodeFont,
                Dock = DockStyle.Fill,
                MaxLength = 0,
                HideSelection = false
            };
        }

        public static RichTextBox ReadOnlyText(bool code = true)
        {
            return new RichTextBox
            {
                ReadOnly = true,
                BackColor = SystemColors.Window,
                Font = code ? CodeFont : UiFont,
                Dock = DockStyle.Fill,
                WordWrap = true,
                DetectUrls = false,
                HideSelection = false
            };
        }

        public static ComboBox Combo(int width, params object[] items)
        {
            var c = new ComboBox { Width = width, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 4, 3, 3) };
            c.Items.AddRange(items);
            if (c.Items.Count > 0) c.SelectedIndex = 0;
            return c;
        }

        public static FlowLayoutPanel Toolbar()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                Padding = new Padding(2)
            };
        }

        /// <summary>建立一欄多列的 TableLayoutPanel；rows 中的 -1 表示自動高度，其餘表示百分比。</summary>
        public static TableLayoutPanel Rows(params float[] rows)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows.Length, Margin = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var r in rows)
                t.RowStyles.Add(r < 0 ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Percent, r));
            return t;
        }

        /// <summary>Vertical = 左右分割，Horizontal = 上下分割；ratio 為第一個面板所佔比例。</summary>
        public static SplitContainer Split(Orientation orientation, double ratio)
        {
            var s = new SplitContainer { Dock = DockStyle.Fill, Orientation = orientation, SplitterWidth = 6 };
            ApplySplitter(s, ratio);
            return s;
        }

        /// <summary>SplitContainer 要在有實際大小之後才能設定 SplitterDistance。</summary>
        public static void ApplySplitter(SplitContainer s, double ratio)
        {
            EventHandler apply = null;
            apply = (sender, e) =>
            {
                int total = s.Orientation == Orientation.Vertical ? s.Width : s.Height;
                if (total <= 0) return;
                int d = (int)(total * ratio);
                d = Math.Max(s.Panel1MinSize, Math.Min(total - s.Panel2MinSize - s.SplitterWidth, d));
                if (d > 0) s.SplitterDistance = d;
                s.SizeChanged -= apply;
            };
            s.SizeChanged += apply;
        }

        public static DataGridView Grid()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells
            };
            g.DefaultCellStyle.Font = CodeFont;
            g.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            g.ColumnHeadersDefaultCellStyle.Font = BoldFont;
            return g;
        }

        public static DataGridViewTextBoxColumn Column(string header, int width, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (fill)
            {
                c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                c.MinimumWidth = 80;
            }
            return c;
        }

        /// <summary>讀取文字檔：先嘗試 UTF-8（含無 BOM），失敗再用系統預設編碼（例如 Big5）。</summary>
        public static string ReadTextFile(string path)
        {
            byte[] bytes = System.IO.File.ReadAllBytes(path);
            try
            {
                var utf8 = new System.Text.UTF8Encoding(false, true);
                string text = utf8.GetString(bytes);
                return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
            }
            catch (System.Text.DecoderFallbackException)
            {
                return System.Text.Encoding.Default.GetString(bytes);
            }
        }

        public static void ShowError(IWin32Window owner, string message)
        {
            MessageBox.Show(owner, message, "ASSAM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public static void ShowInfo(IWin32Window owner, string message)
        {
            MessageBox.Show(owner, message, "ASSAM", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>選取多行文字框中的第 lineNumber 行（從 1 開始）。</summary>
        public static void SelectLine(TextBox box, int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > box.Lines.Length) return;
            int start = box.GetFirstCharIndexFromLine(lineNumber - 1);
            if (start < 0) return;
            box.Select(start, box.Lines[lineNumber - 1].Length);
            box.ScrollToCaret();
        }
    }
}
