using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Assam.Core.Analysis;
using Assam.Core.Assistant;
using Assam.Core.Binary;
using Assam.Core.Instructions;

namespace Assam.App.Pages
{
    /// <summary>
    /// 反組譯：載入 EXE／DLL（Windows PE、.NET、DOS MZ/COM），顯示檔頭、區段、匯入匯出、函式的組合語言與 .NET IL。
    /// 只讀取檔案內容做靜態分析，絕不執行載入的程式。
    /// </summary>
    [DesignerCategory("Code")]
    public sealed class DisassemblyPage : UserControl
    {
        private const int MaxTreeNodes = 3000;
        private const int GotoInstructions = 400;
        private const int SectionInstructions = 2000;
        private const int HexDumpBytes = 16 * 1024;
        private const int MaxXrefLines = 2000;

        private readonly AsmAssistant _assistant;
        private readonly Action<string> _status;
        private readonly Disassembler _disassembler = new Disassembler();
        private readonly FlowLayoutPanel _bar;
        private readonly TreeView _tree;
        private readonly TextBox _listing;
        private readonly RichTextBox _info;
        private readonly TextBox _address;
        private readonly TextBox _filter;
        private readonly CheckBox _showBytes;
        private readonly CheckBox _scanUnreached;
        private readonly CheckBox _withCallees;
        private readonly Button _back;
        private readonly Timer _filterTimer;
        private readonly ContextMenuStrip _exportMenu;
        private readonly List<Control> _needsBinary = new List<Control>();
        private readonly Dictionary<int, AsmListingLine> _lineMap = new Dictionary<int, AsmListingLine>();
        private readonly Stack<ulong> _history = new Stack<ulong>();

        private LoadedBinary _binary;
        private DisassemblyResult _result;
        private CilDisassemblyResult _cil;
        private Dictionary<string, ulong> _symbolLookup = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        private bool _busy;
        private bool _suppressSelect;
        private bool _xrefMode;
        private int _lastInfoRow = -1;
        private ulong? _currentAddress;
        private DisassembledFunction _currentFunction;
        private CilMethodListing _currentMethod;
        private Action _refreshView;

        /// <summary>把選取函式的組合語言送到「程式碼分析」頁。</summary>
        public event Action<string> SendToAnalysis;

        /// <summary>把選取函式的組合語言送到「推導模擬」頁。</summary>
        public event Action<string> SendToSimulation;

        /// <summary>左側樹狀清單中，一個可延後展開的群組（函式、字串等數量可能很多）。</summary>
        private sealed class GroupTag
        {
            public string Title;
            public int Total;
            public Func<string, List<TreeNode>> Build;
            public bool Populated;
        }

        private sealed class AnalysisBundle
        {
            public LoadedBinary Binary;
            public DisassemblyResult Result;
            public CilDisassemblyResult Cil;
            public readonly List<string> Errors = new List<string>();
            public TimeSpan Elapsed;
        }

        public DisassemblyPage(AsmAssistant assistant, Action<string> status)
        {
            _assistant = assistant;
            _status = status ?? (s => { });

            var layout = Ui.Rows(-1, 100);
            Controls.Add(layout);

            var bar = _bar = Ui.Toolbar();
            bar.Controls.Add(Ui.Button("開啟 EXE／DLL…", (s, e) => OpenFile()));
            _back = Ui.Button("← 上一步", (s, e) => Guard(GoBack));
            _back.Enabled = false;
            bar.Controls.Add(_back);
            bar.Controls.Add(Ui.Label("位址／名稱："));
            _address = Ui.TextBox(150);
            _address.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                Guard(() => GoTo(_address.Text));
            };
            bar.Controls.Add(_address);
            var go = Ui.Button("前往", (s, e) => Guard(() => GoTo(_address.Text)));
            bar.Controls.Add(go);
            bar.Controls.Add(Ui.Label("篩選："));
            _filter = Ui.TextBox(120);
            bar.Controls.Add(_filter);
            _showBytes = MakeCheckBox("顯示機器碼", true);
            _showBytes.CheckedChanged += (s, e) => Guard(RefreshView);
            bar.Controls.Add(_showBytes);
            _scanUnreached = MakeCheckBox("也掃描沒追蹤到的程式碼", false);
            _scanUnreached.CheckedChanged += (s, e) => Reanalyze();
            bar.Controls.Add(_scanUnreached);
            var toAnalysis = Ui.Button("送到程式碼分析", (s, e) => Send(SendToAnalysis, "程式碼分析"));
            var toSimulation = Ui.Button("送到推導模擬", (s, e) => Send(SendToSimulation, "推導模擬"));
            bar.Controls.Add(toAnalysis);
            bar.Controls.Add(toSimulation);
            _withCallees = MakeCheckBox("連同它呼叫的函式", false);
            bar.Controls.Add(_withCallees);
            var export = Ui.Button("匯出…", null);
            _exportMenu = new ContextMenuStrip { Font = Ui.UiFont };
            _exportMenu.Items.Add("匯出完整反組譯清單（.lst）…", null, (s, e) => ExportListing());
            _exportMenu.Items.Add("匯出選取函式的原始碼（.asm）…", null, (s, e) => ExportFunctionSource());
            export.Click += (s, e) => _exportMenu.Show(export, new Point(0, export.Height));
            bar.Controls.Add(export);
            layout.Controls.Add(bar, 0, 0);
            _needsBinary.AddRange(new Control[] { _address, go, _filter, toAnalysis, toSimulation, export });

            var split = Ui.Split(Orientation.Vertical, 0.27);
            _tree = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                Font = Ui.UiFont,
                ShowNodeToolTips = true
            };
            _tree.BeforeExpand += (s, e) => PopulateGroup(e.Node);
            _tree.AfterSelect += (s, e) =>
            {
                if (!_suppressSelect) Guard(() => ShowNode(e.Node));
            };
            split.Panel1.Controls.Add(_tree);

            var right = Ui.Split(Orientation.Horizontal, 0.68);
            _listing = Ui.CodeBox();
            _listing.ReadOnly = true;
            _listing.BackColor = SystemColors.Window;
            _listing.MouseUp += (s, e) => Guard(ShowCaretInstruction);
            _listing.KeyUp += (s, e) => Guard(ShowCaretInstruction);
            _listing.DoubleClick += (s, e) => Guard(FollowCaret);
            right.Panel1.Controls.Add(_listing);
            _info = Ui.ReadOnlyText(false);
            right.Panel2.Controls.Add(_info);
            split.Panel2.Controls.Add(right);
            layout.Controls.Add(split, 0, 1);

            _filterTimer = new Timer { Interval = 400 };
            _filterTimer.Tick += (s, e) =>
            {
                _filterTimer.Stop();
                ApplyFilter();
            };
            _filter.TextChanged += (s, e) =>
            {
                _filterTimer.Stop();
                _filterTimer.Start();
            };

            foreach (var c in new Control[] { this, _tree, _listing, _info }) EnableFileDrop(c);
            SetBinaryControlsEnabled(false);
            ShowWelcome();
        }

        private static CheckBox MakeCheckBox(string text, bool isChecked)
        {
            return new CheckBox { Text = text, Checked = isChecked, AutoSize = true, Margin = new Padding(8, 7, 3, 3) };
        }

        /// <summary>
        /// 執行介面動作；檔案格式特殊或損毀造成的例外只顯示在說明區，不中斷程式。
        /// </summary>
        private void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _status("顯示時發生錯誤：" + ex.Message);
                SetInfo("【發生錯誤】\r\n" + ex.Message + "\r\n\r\n這通常是檔案格式特殊或損毀造成的，其他部分仍可繼續使用。");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _filterTimer.Dispose();
                _exportMenu.Dispose();
            }
            base.Dispose(disposing);
        }

        // =====================================================================
        // 開檔與背景分析
        // =====================================================================

        /// <summary>顯示「開啟檔案」對話方塊並反組譯選取的檔案。</summary>
        public void OpenFile()
        {
            if (_busy) return;
            using (var dlg = new OpenFileDialog
            {
                Title = "選擇要反組譯的 EXE／DLL（只會讀取，不會執行）",
                Filter = "執行檔與函式庫 (*.exe;*.dll;*.com;*.sys;*.ocx;*.cpl;*.scr)|*.exe;*.dll;*.com;*.sys;*.ocx;*.cpl;*.scr|所有檔案 (*.*)|*.*"
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) OpenPath(dlg.FileName);
            }
        }

        /// <summary>在背景載入並反組譯檔案（不會卡住視窗），完成後更新畫面。</summary>
        public async void OpenPath(string path)
        {
            if (_busy || string.IsNullOrEmpty(path)) return;
            bool scan = _scanUnreached.Checked;
            SetBusy(true, "正在讀取並反組譯 " + Path.GetFileName(path) + "（只讀取，不執行）…");
            try
            {
                var bundle = await Task.Run(() => Analyze(BinaryLoader.Load(path), scan));
                if (IsDisposed) return;
                ApplyBundle(bundle);
            }
            catch (BinaryFormatException ex)
            {
                if (IsDisposed) return;
                _status("無法反組譯 " + Path.GetFileName(path));
                Ui.ShowError(this, "無法反組譯這個檔案：\r\n" + ex.Message);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException)
            {
                if (IsDisposed) return;
                _status("無法讀取 " + Path.GetFileName(path));
                Ui.ShowError(this, "無法讀取檔案：\r\n" + ex.Message);
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _status("反組譯失敗");
                Ui.ShowError(this, "反組譯時發生錯誤：\r\n" + ex.Message);
            }
            finally
            {
                if (!IsDisposed) SetBusy(false, null);
            }
        }

        /// <summary>切換「也掃描沒追蹤到的程式碼」時，對同一個檔案重新反組譯（不重新讀檔）。</summary>
        private async void Reanalyze()
        {
            if (_busy || _binary == null || _binary.Bits == 0) return;
            var binary = _binary;
            bool scan = _scanUnreached.Checked;
            SetBusy(true, "正在重新反組譯…");
            try
            {
                var bundle = await Task.Run(() => Analyze(binary, scan, false));
                if (IsDisposed) return;
                bundle.Cil = _cil;
                ApplyBundle(bundle);
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                Ui.ShowError(this, "反組譯時發生錯誤：\r\n" + ex.Message);
            }
            finally
            {
                if (!IsDisposed) SetBusy(false, null);
            }
        }

        /// <summary>在背景執行緒中執行：反組譯機器碼、.NET IL，並先掃描字串（避免之後卡住介面）。</summary>
        private static AnalysisBundle Analyze(LoadedBinary binary, bool scanUnreached, bool includeCil = true)
        {
            var watch = Stopwatch.StartNew();
            var bundle = new AnalysisBundle { Binary = binary };
            var unused = binary.Strings.Count;
            if (binary.Bits != 0)
            {
                try
                {
                    bundle.Result = new Disassembler().Disassemble(binary, new DisassemblyOptions { ScanUnreachedCode = scanUnreached });
                }
                catch (Exception ex)
                {
                    bundle.Errors.Add("反組譯機器碼時發生錯誤：" + ex.Message);
                }
            }
            if (includeCil && binary.IsDotNet)
            {
                try
                {
                    bundle.Cil = CilDisassembler.Disassemble(binary);
                }
                catch (Exception ex)
                {
                    bundle.Errors.Add("讀取 .NET 中繼資料時發生錯誤：" + ex.Message);
                }
            }
            bundle.Elapsed = watch.Elapsed;
            return bundle;
        }

        private void ApplyBundle(AnalysisBundle bundle)
        {
            _binary = bundle.Binary;
            _result = bundle.Result;
            _cil = bundle.Cil;
            _history.Clear();
            _back.Enabled = false;
            _currentAddress = null;
            _currentFunction = null;
            _currentMethod = null;
            foreach (var e in bundle.Errors) _binary.Warnings.Add(e);
            BuildSymbolLookup();
            BuildTree();

            var sb = new StringBuilder();
            sb.Append("已反組譯 ").Append(_binary.FileName).Append("：").Append(_binary.KindName);
            if (_result != null) sb.Append("，函式 ").Append(_result.Functions.Count).Append(" 個、指令 ").Append(_result.TotalInstructions).Append(" 條");
            if (_cil != null) sb.Append("，.NET 方法 ").Append(_cil.MethodCount).Append(" 個");
            sb.Append("，耗時 ").Append(bundle.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" 秒");
            if (_result != null && _result.Truncated) sb.Append("（檔案太大，只反組譯了一部分）");
            _status(sb.ToString());

            var entry = _result != null && _binary.HasEntryPoint ? _result.FindFunction(_binary.EntryPoint) : null;
            if (entry != null)
            {
                SelectFunctionNode(entry);
                ShowFunction(entry, null);
            }
            else
            {
                _tree.SelectedNode = _tree.Nodes.Count > 0 ? _tree.Nodes[0] : null;
            }
        }

        private void SetBusy(bool busy, string message)
        {
            _busy = busy;
            UseWaitCursor = busy;
            foreach (Control c in _bar.Controls) c.Enabled = !busy;
            _tree.Enabled = !busy;
            if (!busy)
            {
                SetBinaryControlsEnabled(_binary != null);
                _back.Enabled = _history.Count > 0;
            }
            if (message != null) _status(message);
        }

        private void SetBinaryControlsEnabled(bool enabled)
        {
            foreach (var c in _needsBinary) c.Enabled = enabled;
        }

        private void EnableFileDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) =>
            {
                e.Effect = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            c.DragDrop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) OpenPath(files[0]);
            };
        }

        private void BuildSymbolLookup()
        {
            _symbolLookup = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            if (_result != null)
            {
                foreach (var kv in _result.Symbols)
                    if (!_symbolLookup.ContainsKey(kv.Value)) _symbolLookup[kv.Value] = kv.Key;
                foreach (var f in _result.Functions) _symbolLookup[f.Name] = f.Start;
            }
            foreach (var e in _binary.Exports)
                if (e.Name != null && e.ForwardTo == null && !_symbolLookup.ContainsKey(e.Name)) _symbolLookup[e.Name] = e.Address;
        }

        // =====================================================================
        // 左側樹狀清單
        // =====================================================================

        private void BuildTree()
        {
            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                _tree.Nodes.Add(new TreeNode("檔案資訊") { Tag = "info" });
                AddGroup("區段", _binary.Sections.Count, f => _binary.Sections
                    .Where(s => Matches(s.Name, f))
                    .Select(s => new TreeNode(s.Name + "  " + s.AccessText) { Tag = s, ToolTipText = s.ToString() }).ToList());
                if (_binary.Imports.Count > 0) AddGroup("匯入", _binary.Imports.Count, BuildImportNodes);
                if (_binary.Exports.Count > 0) AddGroup("匯出", _binary.Exports.Count, f => Limit(_binary.Exports
                    .Where(e => Matches(e.DisplayName, f))
                    .Select(e => new TreeNode(e.ToString()) { Tag = e })));
                if (_result != null) AddGroup("函式", _result.Functions.Count, f => Limit(_result.Functions
                    .Where(x => Matches(x.Name, f) || Matches(x.Start.ToString("X", CultureInfo.InvariantCulture), f))
                    .Select(FunctionNode)));
                if (_binary.Strings.Count > 0) AddGroup("字串", _binary.Strings.Count, f => Limit(_binary.Strings
                    .Where(s => Matches(s.Text, f))
                    .Select(s => new TreeNode(Shorten(s.Text, 60)) { Tag = s, ToolTipText = s.ToString() })));
                if (_cil != null) AddGroup(".NET 型別", _cil.Types.Count, BuildCilNodes);
                foreach (TreeNode n in _tree.Nodes) UpdateGroupNode(n);
            }
            finally
            {
                _tree.EndUpdate();
            }
        }

        private void AddGroup(string title, int total, Func<string, List<TreeNode>> build)
        {
            var node = new TreeNode(title) { Tag = new GroupTag { Title = title, Total = total, Build = build } };
            _tree.Nodes.Add(node);
        }

        /// <summary>依篩選文字重設群組（尚未展開的群組只放一個佔位節點，展開時才建立，避免大檔案卡住）。</summary>
        private void UpdateGroupNode(TreeNode node)
        {
            var g = node.Tag as GroupTag;
            if (g == null) return;
            bool wasExpanded = node.IsExpanded;
            node.Nodes.Clear();
            g.Populated = false;
            string filter = _filter.Text.Trim();
            if (filter.Length == 0)
            {
                node.Text = g.Title + "（" + g.Total + "）";
                if (g.Total > 0) node.Nodes.Add(new TreeNode("載入中…"));
            }
            else
            {
                var nodes = g.Build(filter);
                int matched = nodes.Sum(n => n.Tag is string ? n.Nodes.Count : n.Tag != null ? 1 : 0);
                node.Text = g.Title + "（" + matched + "／" + g.Total + "）";
                node.Nodes.AddRange(nodes.ToArray());
                g.Populated = true;
            }
            if (wasExpanded || (filter.Length > 0 && node.Nodes.Count > 0)) node.Expand();
        }

        private void PopulateGroup(TreeNode node)
        {
            var g = node.Tag as GroupTag;
            if (g == null || g.Populated) return;
            _tree.BeginUpdate();
            try
            {
                node.Nodes.Clear();
                node.Nodes.AddRange(g.Build(_filter.Text.Trim()).ToArray());
                g.Populated = true;
            }
            finally
            {
                _tree.EndUpdate();
            }
        }

        private void ApplyFilter()
        {
            if (_binary == null) return;
            _tree.BeginUpdate();
            try
            {
                foreach (TreeNode n in _tree.Nodes) UpdateGroupNode(n);
            }
            finally
            {
                _tree.EndUpdate();
            }
        }

        private static bool Matches(string text, string filter)
        {
            return string.IsNullOrEmpty(filter) || (text != null && text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>節點太多時只放前 MaxTreeNodes 個，最後加一個提示節點（不會悄悄截斷）。</summary>
        private static List<TreeNode> Limit(IEnumerable<TreeNode> nodes)
        {
            var list = new List<TreeNode>();
            int extra = 0;
            foreach (var n in nodes)
            {
                if (list.Count < MaxTreeNodes) list.Add(n);
                else extra++;
            }
            if (extra > 0) list.Add(new TreeNode("……還有 " + extra + " 項沒有列出，請用上方「篩選」或「前往」尋找") { ForeColor = Color.Gray });
            return list;
        }

        private static TreeNode FunctionNode(DisassembledFunction f)
        {
            string text = f.Name.StartsWith("sub_", StringComparison.Ordinal) ? f.Name : f.Name + "  0x" + f.Start.ToString("X", CultureInfo.InvariantCulture);
            return new TreeNode(text) { Tag = f, ToolTipText = f.Origin + "，" + f.Lines.Count + " 條指令" };
        }

        private List<TreeNode> BuildImportNodes(string filter)
        {
            var result = new List<TreeNode>();
            foreach (var dll in _binary.Imports.GroupBy(i => i.Dll ?? "?", StringComparer.OrdinalIgnoreCase))
            {
                bool dllMatches = Matches(dll.Key, filter);
                var children = dll.Where(i => dllMatches || Matches(i.DisplayName, filter))
                                  .Select(i => new TreeNode(i.DisplayName + (i.IsDelayLoad ? "（延遲載入）" : string.Empty)) { Tag = i, ToolTipText = LibraryCalls.Describe(i.Name) ?? i.FullName })
                                  .ToArray();
                if (children.Length == 0) continue;
                var node = new TreeNode(dll.Key + "（" + children.Length + "）") { Tag = dll.Key };
                node.Nodes.AddRange(children);
                result.Add(node);
            }
            return result;
        }

        private List<TreeNode> BuildCilNodes(string filter)
        {
            var result = new List<TreeNode>();
            int count = 0;
            foreach (var t in _cil.Types)
            {
                bool typeMatches = Matches(t.FullName, filter);
                var methods = t.Methods.Where(m => typeMatches || Matches(m.Name, filter)).ToList();
                if (!typeMatches && methods.Count == 0) continue;
                if (count >= MaxTreeNodes)
                {
                    result.Add(new TreeNode("……型別太多，請用上方「篩選」縮小範圍") { ForeColor = Color.Gray });
                    break;
                }
                var node = new TreeNode(t.FullName) { Tag = t };
                node.Nodes.AddRange(methods.Select(m => new TreeNode(m.ToString()) { Tag = m }).ToArray());
                result.Add(node);
                count += 1 + methods.Count;
            }
            return result;
        }

        private TreeNode GroupNode(string title)
        {
            foreach (TreeNode n in _tree.Nodes)
            {
                var g = n.Tag as GroupTag;
                if (g != null && g.Title == title) return n;
            }
            return null;
        }

        /// <summary>在樹狀清單中選取函式節點（只同步選取，不重新顯示）。</summary>
        private void SelectFunctionNode(DisassembledFunction f)
        {
            var group = GroupNode("函式");
            if (group == null) return;
            PopulateGroup(group);
            var node = group.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag == f);
            if (node == null) return;
            _suppressSelect = true;
            try
            {
                _tree.SelectedNode = node;
                node.EnsureVisible();
            }
            finally
            {
                _suppressSelect = false;
            }
        }

        private void ShowNode(TreeNode node)
        {
            if (node == null || _binary == null) return;
            object tag = node.Tag;
            if (tag as string == "info") ShowFileInfo();
            else if (tag is PeSection) ShowSection((PeSection)tag);
            else if (tag is PeImport) ShowImport((PeImport)tag);
            else if (tag is PeExport) ShowExport((PeExport)tag);
            else if (tag is DisassembledFunction) ShowFunction((DisassembledFunction)tag, null);
            else if (tag is FoundString) ShowString((FoundString)tag);
            else if (tag is CilTypeListing) ShowCilType((CilTypeListing)tag);
            else if (tag is CilMethodListing) ShowCilMethod((CilMethodListing)tag);
            else if (tag is string) ShowDll((string)tag);
        }

        // =====================================================================
        // 右側：各種檢視
        // =====================================================================

        private void ShowWelcome()
        {
            SetListing("; 尚未開啟檔案。\r\n;\r\n; 按上方「開啟 EXE／DLL…」，或把檔案拖曳到這裡。\r\n" +
                       "; 支援：32／64 位元 Windows 執行檔與 DLL（PE）、.NET 組件（IL）、DOS 的 .EXE（MZ）與 .COM。\r\n" +
                       "; ASSAM 只會讀取檔案內容做靜態分析，不會執行它，也不需要網路。\r\n", null);
            SetInfo("【反組譯是什麼】\r\n" +
                    "把機器碼（CPU 實際執行的位元組）轉回人看得懂的組合語言。編譯器產生的執行檔沒有原本的變數名稱與註解，" +
                    "ASSAM 會自動加上標籤：sub_ 開頭是函式、loc_ 是跳躍目標、str_ 是字串、__imp_ 是從 DLL 匯入的函式。\r\n\r\n" +
                    "【使用方式】\r\n" +
                    "1. 開啟檔案後，左邊會列出檔案資訊、區段、匯入／匯出、函式與字串。\r\n" +
                    "2. 點選函式即可看到它的組合語言；點選某一行會在下方說明這條指令。\r\n" +
                    "3. 在 call／jmp 那一行按兩下可以跳到目標，按「← 上一步」回來。\r\n" +
                    "4. 「送到程式碼分析」會逐行解釋；「送到推導模擬」可以一步一步執行看暫存器變化（外部 API 只會略過）。\r\n\r\n" +
                    "【注意】\r\n請只分析你有權分析的檔案（自己寫的程式、作業、教材範例等）。" +
                    KnowledgeSnippets("反組譯 PE 執行檔 區段 匯入表", 3));
        }

        private void ShowFileInfo()
        {
            ResetView(ShowFileInfo);
            var sb = new StringBuilder();
            sb.Append("; ").Append(_binary.FileName).Append("\r\n");
            foreach (var line in _binary.Describe().Replace("\r\n", "\n").Split('\n')) sb.Append("; ").Append(line).Append("\r\n");
            sb.Append(";\r\n; 區段：\r\n");
            sb.Append(";   名稱       虛擬位址          大小        檔案位移    權限\r\n");
            foreach (var s in _binary.Sections)
            {
                sb.Append(";   ").Append((s.Name ?? string.Empty).PadRight(10)).Append(' ')
                  .Append(("0x" + s.VirtualAddress.ToString("X", CultureInfo.InvariantCulture)).PadRight(18))
                  .Append(("0x" + s.MemorySize.ToString("X", CultureInfo.InvariantCulture)).PadRight(12))
                  .Append(("0x" + s.RawOffset.ToString("X", CultureInfo.InvariantCulture)).PadRight(12))
                  .Append(s.AccessText).Append("\r\n");
            }
            if (_binary.Imports.Count > 0)
            {
                sb.Append(";\r\n; 匯入的 DLL：\r\n");
                foreach (var g in _binary.Imports.GroupBy(i => i.Dll ?? "?", StringComparer.OrdinalIgnoreCase))
                    sb.Append(";   ").Append(g.Key).Append("（").Append(g.Count()).Append(" 個函式）\r\n");
            }
            if (_result != null)
            {
                sb.Append(";\r\n; 反組譯：函式 ").Append(_result.Functions.Count).Append(" 個、指令 ").Append(_result.TotalInstructions).Append(" 條");
                if (_result.Truncated) sb.Append("（已達上限，只反組譯一部分）");
                sb.Append("\r\n");
                foreach (var m in _result.Messages) sb.Append(";   ").Append(m).Append("\r\n");
            }
            else if (_binary.Bits == 0)
            {
                sb.Append(";\r\n; 這個檔案的 CPU 架構（").Append(_binary.MachineName).Append("）目前只能分析檔頭，無法反組譯機器碼（ASSAM 支援 x86／x64）。\r\n");
            }
            if (_cil != null)
            {
                sb.Append(";\r\n; .NET 組件：").Append(_cil.AssemblyName).Append(' ').Append(_cil.AssemblyVersion)
                  .Append("，執行階段 ").Append(_cil.MetadataVersion).Append("，型別 ").Append(_cil.Types.Count).Append(" 個、方法 ").Append(_cil.MethodCount).Append(" 個\r\n");
                if (!string.IsNullOrEmpty(_cil.EntryPoint)) sb.Append(";   進入點：").Append(_cil.EntryPoint).Append("\r\n");
                if (_cil.AssemblyReferences.Count > 0) sb.Append(";   參考的組件：").Append(string.Join("、", _cil.AssemblyReferences)).Append("\r\n");
                foreach (var m in _cil.Messages) sb.Append(";   ").Append(m).Append("\r\n");
            }
            if (_binary.Warnings.Count > 0)
            {
                sb.Append(";\r\n; 警告（檔案可能損毀、被加殼或格式特殊）：\r\n");
                foreach (var w in _binary.Warnings) sb.Append(";   ▲ ").Append(w).Append("\r\n");
            }
            SetListing(sb.ToString(), null);

            var info = new StringBuilder();
            info.Append("【這是什麼檔案】\r\n").Append(KindExplanation()).Append("\r\n\r\n");
            info.Append("【下一步】\r\n");
            if (_result != null && _binary.HasEntryPoint)
                info.Append("・左邊「函式」中的 ").Append(_binary.IsPe ? "entry" : "start").Append(" 是程式開始執行的地方（進入點）。\r\n");
            if (_binary.Exports.Count > 0) info.Append("・「匯出」是這個 DLL 提供給別人呼叫的函式，點選即可看到它的程式碼。\r\n");
            if (_binary.Imports.Count > 0) info.Append("・「匯入」是它向 Windows 或其他 DLL 借用的函式，點選可以找到是哪裡呼叫的。\r\n");
            if (_binary.Strings.Count > 0) info.Append("・「字串」常常是理解程式最快的線索（訊息、檔名、格式字串）。\r\n");
            if (_cil != null) info.Append("・.NET 程式的邏輯在「.NET 型別」中的 IL 程式碼，原生的進入點只是一行跳到 _CorExeMain 的跳板。\r\n");
            info.Append(KnowledgeSnippets(_binary.IsDotNet ? ".NET IL 中繼語言 組件" : _binary.IsPe ? "PE 執行檔 區段 匯入表 進入點" : "DOS COM EXE 程式 int 21h", 3));
            SetInfo(info.ToString());
        }

        private string KindExplanation()
        {
            switch (_binary.Kind)
            {
                case BinaryKind.Pe32:
                case BinaryKind.Pe64:
                    var sb = new StringBuilder();
                    sb.Append(_binary.Kind == BinaryKind.Pe64 ? "64 位元" : "32 位元").Append(" Windows ").Append(_binary.IsDll ? "動態連結函式庫（DLL）" : "執行檔（EXE）")
                      .Append("，使用 PE（Portable Executable）格式。檔案分成數個區段：.text 放程式碼、.data／.rdata 放資料與字串、.idata 或 .rdata 中有匯入表。")
                      .Append("程式載入時會被放到 ImageBase（0x").Append(_binary.ImageBase.ToString("X", CultureInfo.InvariantCulture)).Append("），位址 = ImageBase + RVA。");
                    if (_binary.Kind == BinaryKind.Pe64) sb.Append("x64 程式常用 [rip+位移] 存取全域資料，前四個參數放在 rcx、rdx、r8、r9。");
                    else sb.Append("32 位元程式的參數通常用 push 放到堆疊上（stdcall／cdecl），回傳值放在 eax。");
                    if (_binary.IsDotNet) sb.Append("\r\n這是 .NET 組件：真正的程式是 IL（中繼語言），執行時才由 JIT 編譯成機器碼。");
                    return sb.ToString();
                case BinaryKind.DosMz:
                    return "16 位元 DOS 執行檔（MZ 格式，例如 MASM／TASM 以 .model small 組譯連結出的 .EXE）。位址以「區段:位移」表示，這裡用線性位址（區段×16＋位移，相對於載入模組開頭）顯示。程式多半透過 int 21h 呼叫 DOS 功能。";
                case BinaryKind.DosCom:
                    return "16 位元 DOS .COM 檔：沒有檔頭，整個檔案載入到位移 100h 開始執行（所以原始碼要寫 org 100h），程式、資料、堆疊共用同一個 64KB 區段。";
                default:
                    return "沒有檔頭的原始機器碼。";
            }
        }

        private void ShowSection(PeSection s)
        {
            ResetView(() => ShowSection(s));
            var info = new StringBuilder();
            info.Append("【區段 ").Append(s.Name).Append("】\r\n");
            info.Append("虛擬位址：0x").Append(s.VirtualAddress.ToString("X", CultureInfo.InvariantCulture))
                .Append("（RVA 0x").Append(s.Rva.ToString("X", CultureInfo.InvariantCulture)).Append("）\r\n");
            info.Append("記憶體大小：0x").Append(s.MemorySize.ToString("X", CultureInfo.InvariantCulture))
                .Append("，檔案中大小：0x").Append(s.RawSize.ToString("X", CultureInfo.InvariantCulture))
                .Append("，檔案位移：0x").Append(s.RawOffset.ToString("X", CultureInfo.InvariantCulture)).Append("\r\n");
            info.Append("權限：").Append(s.AccessText).Append("（R 可讀、W 可寫、X 可執行），旗標 0x").Append(s.Characteristics.ToString("X8", CultureInfo.InvariantCulture)).Append("\r\n\r\n");
            info.Append("【用途】\r\n").Append(SectionPurpose(s)).Append("\r\n");
            if (_result != null)
            {
                int functions = _result.Functions.Count(f => s.Contains(f.Start));
                if (functions > 0) info.Append("\r\n這個區段中有 ").Append(functions).Append(" 個已找到的函式。\r\n");
            }
            SetInfo(info.ToString());

            if (s.IsExecutable && _binary.Bits != 0)
            {
                var lines = _disassembler.DisassembleRange(_binary, _result, s.VirtualAddress, SectionInstructions);
                SetListing("; 區段 " + s.Name + " 從頭線性反組譯（最多 " + SectionInstructions + " 條指令；資料夾雜在程式碼中時，這種方式可能解錯，請以左側「函式」為準）\r\n", lines);
                _currentAddress = s.VirtualAddress;
            }
            else
            {
                SetListing(HexDump(s.VirtualAddress, (int)Math.Min(s.MemorySize == 0 ? s.RawSize : s.MemorySize, (uint)HexDumpBytes),
                    "; 區段 " + s.Name + " 的內容（十六進位，最多顯示 " + (HexDumpBytes / 1024) + " KB）\r\n"), null);
            }
        }

        private static string SectionPurpose(PeSection s)
        {
            string n = (s.Name ?? string.Empty).ToLowerInvariant();
            if (n == ".text" || n == "code" || n == ".code" || n == "text") return "程式碼（CPU 執行的機器指令）。";
            if (n == ".data" || n == "data") return "已初始化的全域／靜態變數（可讀寫）。";
            if (n == ".rdata") return "唯讀資料：字串常數、浮點常數、匯入表、例外處理資訊等。";
            if (n == ".bss") return "未初始化的全域變數（檔案中不佔空間，載入時清為 0）。";
            if (n == ".idata") return "匯入表：記錄要從哪些 DLL 匯入哪些函式，載入時 Windows 會把實際位址填進 IAT。";
            if (n == ".edata") return "匯出表：這個 DLL 提供給別人呼叫的函式。";
            if (n == ".rsrc") return "資源：圖示、對話方塊、字串表、版本資訊等。";
            if (n == ".reloc") return "重定位表：模組無法載入到預設位址時，用來修正絕對位址。";
            if (n == ".tls") return "執行緒區域儲存（TLS）資料，可能含有在進入點之前執行的 TLS 回呼函式。";
            if (n == ".pdata") return "x64 例外處理用的函式範圍表（每個函式的起訖位址）。";
            if (n == ".xdata") return "x64 例外處理的展開（unwind）資訊。";
            if (n == ".crt" || n.StartsWith(".crt", StringComparison.Ordinal)) return "C 執行階段的初始化函式指標表。";
            if (n.StartsWith("upx", StringComparison.Ordinal)) return "UPX 加殼留下的區段：真正的程式被壓縮了，靜態反組譯只看得到解壓縮程式。";
            if (n == "image") return "DOS 程式的載入映像（程式、資料都在這裡）。";
            if (s.IsExecutable) return "可執行的程式碼。";
            return s.IsWritable ? "可寫的資料。" : "唯讀資料。";
        }

        private void ShowImport(PeImport import)
        {
            ResetView(() => ShowImport(import));
            var thunks = new HashSet<ulong>();
            if (_result != null)
            {
                foreach (var f in _result.Functions)
                    if (f.Lines.Count == 1 && f.Lines[0].Instruction != null && ReferencesAddress(f.Lines[0].Instruction, import.IatAddress))
                        thunks.Add(f.Start);
            }
            var xrefs = FindXrefs(ins => ReferencesAddress(ins, import.IatAddress) || (ins.BranchTarget.HasValue && thunks.Contains(ins.BranchTarget.Value)));
            ShowXrefs("; 呼叫或讀取匯入函式 " + import.FullName + " 的地方（按兩下可跳過去）\r\n", xrefs);

            var info = new StringBuilder();
            info.Append("【匯入函式】").Append(import.FullName).Append("\r\n");
            string desc = LibraryCalls.Describe(import.Name);
            if (desc != null) info.Append(desc).Append("\r\n");
            info.Append("IAT 槽位：0x").Append(import.IatAddress.ToString("X", CultureInfo.InvariantCulture));
            if (import.IsDelayLoad) info.Append("（延遲載入：第一次呼叫時才載入 DLL）");
            info.Append("\r\n\r\n【原理】\r\n程式不知道 ").Append(import.Dll).Append(" 會被載入到哪裡，所以編譯器產生 call [IAT 槽位]（間接呼叫）。")
                .Append("Windows 載入程式時才把 ").Append(import.DisplayName).Append(" 的實際位址寫進這個槽位。反組譯清單中寫成 [__imp_")
                .Append(import.DisplayName).Append("]。\r\n");
            info.Append("\r\n找到 ").Append(xrefs.Count).Append(" 個引用處。");
            if (_binary.Bits == 32) info.Append("呼叫前用 push 放到堆疊上的值就是參數（由右到左）。");
            else if (_binary.Bits == 64) info.Append("呼叫前放進 rcx、rdx、r8、r9 的值就是前四個參數。");
            info.Append("\r\n");
            info.Append(KnowledgeSnippets(import.DisplayName + " " + (import.Dll ?? string.Empty) + " 匯入 API 呼叫", 2));
            SetInfo(info.ToString());
        }

        private void ShowDll(string dll)
        {
            ResetView(() => ShowDll(dll));
            var imports = _binary.Imports.Where(i => string.Equals(i.Dll, dll, StringComparison.OrdinalIgnoreCase)).ToList();
            var sb = new StringBuilder();
            sb.Append("; 從 ").Append(dll).Append(" 匯入的 ").Append(imports.Count).Append(" 個函式\r\n");
            foreach (var i in imports)
            {
                string desc = LibraryCalls.Describe(i.Name);
                sb.Append("0x").Append(i.IatAddress.ToString("X", CultureInfo.InvariantCulture)).Append("  ").Append(i.DisplayName.PadRight(32));
                if (desc != null) sb.Append("; ").Append(desc);
                sb.Append("\r\n");
            }
            SetListing(sb.ToString(), null);
            SetInfo("【" + dll + "】\r\n" + DllPurpose(dll) + "\r\n\r\n點選左邊的函式名稱，可以找出程式中呼叫它的地方。");
        }

        private static string DllPurpose(string dll)
        {
            string d = dll.ToLowerInvariant();
            if (d.StartsWith("kernel32", StringComparison.Ordinal) || d.StartsWith("kernelbase", StringComparison.Ordinal)) return "Windows 核心 API：檔案、記憶體、行程與執行緒、主控台輸出入。";
            if (d.StartsWith("user32", StringComparison.Ordinal)) return "Windows 使用者介面 API：視窗、訊息、對話方塊（例如 MessageBoxA）。";
            if (d.StartsWith("gdi32", StringComparison.Ordinal)) return "Windows 繪圖 API。";
            if (d.StartsWith("advapi32", StringComparison.Ordinal)) return "進階 API：登錄檔、安全性、服務。";
            if (d.StartsWith("msvcrt", StringComparison.Ordinal) || d.StartsWith("ucrtbase", StringComparison.Ordinal) || d.StartsWith("api-ms-win-crt", StringComparison.Ordinal) || d.StartsWith("vcruntime", StringComparison.Ordinal))
                return "C 執行階段函式庫：printf、strcpy、malloc 等 C 標準函式。";
            if (d.StartsWith("mscoree", StringComparison.Ordinal)) return ".NET 執行階段的啟動器：_CorExeMain／_CorDllMain 會載入 CLR 再執行 IL。";
            if (d.StartsWith("ntdll", StringComparison.Ordinal)) return "Windows 最底層的使用者模式 API（系統呼叫的入口）。";
            if (d.StartsWith("ws2_32", StringComparison.Ordinal) || d.StartsWith("wininet", StringComparison.Ordinal)) return "網路 API（這個程式可能會使用網路）。";
            if (d.StartsWith("shell32", StringComparison.Ordinal)) return "Windows 殼層 API：開啟檔案、資料夾、捷徑等。";
            return "外部 DLL。";
        }

        private void ShowExport(PeExport export)
        {
            if (export.ForwardTo != null)
            {
                ResetView(() => ShowExport(export));
                SetListing("; " + export.DisplayName + " 是轉送匯出：呼叫它其實會呼叫 " + export.ForwardTo + "，這個 DLL 中沒有它的程式碼。\r\n", null);
                SetInfo("【轉送匯出】\r\n" + export.DisplayName + " → " + export.ForwardTo);
                return;
            }
            var f = _result != null ? _result.Functions.FirstOrDefault(x => x.Start == export.Address) ?? _result.FindFunction(export.Address) : null;
            if (f != null)
            {
                ShowFunction(f, export.Address);
                return;
            }
            ShowAddress(export.Address, false);
        }

        private void ShowString(FoundString str)
        {
            ResetView(() => ShowString(str));
            var xrefs = FindXrefs(ins => ReferencesAddress(ins, str.Address));
            ShowXrefs("; 使用字串 0x" + str.Address.ToString("X", CultureInfo.InvariantCulture) + " 的地方（按兩下可跳過去）\r\n", xrefs);
            var info = new StringBuilder();
            info.Append("【字串】").Append(str.Encoding == "UTF-16" ? "UTF-16（寬字元，每個字 2 位元組，常見於 Windows 的 W 版 API）" : "ASCII（每個字 1 位元組，以 0 結尾）").Append("\r\n");
            info.Append("位址：0x").Append(str.Address.ToString("X", CultureInfo.InvariantCulture)).Append("，區段 ").Append(str.Section ?? "?")
                .Append("，長度 ").Append(str.Text.Length).Append(" 字\r\n\r\n");
            info.Append(str.Text).Append("\r\n\r\n");
            info.Append(xrefs.Count > 0
                ? "找到 " + xrefs.Count + " 個引用處：程式把這個位址當成參數（push 位址、mov 暫存器, 位址 或 lea）。"
                : "沒有找到直接引用這個位址的指令（可能經由表格、計算出的位址間接使用，或只是資料）。");
            SetInfo(info.ToString());
        }

        private void ShowCilType(CilTypeListing type)
        {
            ResetView(() => ShowCilType(type));
            var sb = new StringBuilder();
            sb.Append("// 型別 ").Append(type.FullName).Append("，方法 ").Append(type.Methods.Count).Append(" 個\r\n\r\n");
            int lines = 0;
            foreach (var m in type.Methods)
            {
                sb.Append(m.Text).Append("\r\n");
                lines += m.Lines.Count + 3;
                if (lines > 20000)
                {
                    sb.Append("// ……太長，只顯示一部分，請點選單一方法查看。\r\n");
                    break;
                }
            }
            SetListing(sb.ToString(), null);
            SetInfo("【.NET 型別】" + type.FullName + "\r\n點選左邊的方法可以看單一方法的 IL 與說明。" + KnowledgeSnippets(".NET IL 中繼語言 堆疊 指令", 2));
        }

        private void ShowCilMethod(CilMethodListing m)
        {
            ResetView(() => ShowCilMethod(m));
            _currentMethod = m;
            SetListing(m.Text, null);
            var info = new StringBuilder();
            info.Append("【.NET 方法】").Append(m.TypeName).Append("::").Append(m.Signature ?? m.Name).Append("\r\n");
            info.Append("中繼資料權杖：0x").Append(m.MetadataToken.ToString("X8", CultureInfo.InvariantCulture));
            if (m.HasBody)
                info.Append("，RVA 0x").Append(m.Rva.ToString("X", CultureInfo.InvariantCulture)).Append("，IL ").Append(m.CodeSize).Append(" 位元組，.maxstack ")
                    .Append(m.MaxStack).Append("，區域變數 ").Append(m.LocalCount).Append(" 個");
            info.Append("\r\n\r\n【怎麼讀 IL】\r\n");
            info.Append("IL 是「堆疊機」：ldarg／ldloc／ldc／ldstr 把值推到評估堆疊，add／mul 等從堆疊取出運算再推回結果，stloc 存回區域變數，")
                .Append("call／callvirt 呼叫方法（參數從堆疊取出），ret 返回。br／brtrue／brfalse／beq／blt 是跳躍，對應 x86 的 jmp／jcc。\r\n");
            var ops = m.Lines.Select(l => IlOpcode(l)).Where(o => o != null).GroupBy(o => o).OrderByDescending(g => g.Count()).Take(8).Select(g => g.Key).ToList();
            if (ops.Count > 0)
            {
                info.Append("\r\n【這個方法用到的 IL 指令】\r\n");
                foreach (var op in ops) info.Append("・").Append(op).Append("：").Append(IlDescription(op)).Append("\r\n");
            }
            SetInfo(info.ToString());
        }

        private static string IlOpcode(string line)
        {
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0) return null;
            string rest = line.Substring(colon + 2).Trim();
            int sp = rest.IndexOf(' ');
            return sp > 0 ? rest.Substring(0, sp) : rest.Length > 0 ? rest : null;
        }

        private static string IlDescription(string op)
        {
            string o = op.ToLowerInvariant();
            if (o.StartsWith("ldarg", StringComparison.Ordinal)) return "載入參數到堆疊";
            if (o.StartsWith("ldloca", StringComparison.Ordinal)) return "載入區域變數的位址";
            if (o.StartsWith("ldloc", StringComparison.Ordinal)) return "載入區域變數到堆疊";
            if (o.StartsWith("stloc", StringComparison.Ordinal)) return "從堆疊取出值存到區域變數";
            if (o.StartsWith("starg", StringComparison.Ordinal)) return "從堆疊取出值存到參數";
            if (o.StartsWith("ldc.", StringComparison.Ordinal)) return "載入常數到堆疊";
            if (o == "ldstr") return "載入字串常數";
            if (o == "ldnull") return "載入 null";
            if (o.StartsWith("ldfld", StringComparison.Ordinal) || o.StartsWith("ldsfld", StringComparison.Ordinal)) return "讀取欄位";
            if (o.StartsWith("stfld", StringComparison.Ordinal) || o.StartsWith("stsfld", StringComparison.Ordinal)) return "寫入欄位";
            if (o == "call") return "呼叫方法（非虛擬）";
            if (o == "callvirt") return "呼叫虛擬／執行個體方法";
            if (o == "newobj") return "建立物件並呼叫建構函式";
            if (o == "newarr") return "建立陣列";
            if (o == "ret") return "返回（有回傳值時從堆疊取出）";
            if (o == "nop") return "什麼都不做";
            if (o == "pop") return "丟棄堆疊頂端的值";
            if (o == "dup") return "複製堆疊頂端的值";
            if (o.StartsWith("add", StringComparison.Ordinal)) return "加法";
            if (o.StartsWith("sub", StringComparison.Ordinal)) return "減法";
            if (o.StartsWith("mul", StringComparison.Ordinal)) return "乘法";
            if (o.StartsWith("div", StringComparison.Ordinal)) return "除法";
            if (o.StartsWith("rem", StringComparison.Ordinal)) return "取餘數";
            if (o == "and" || o == "or" || o == "xor" || o == "not") return "位元運算";
            if (o == "shl" || o.StartsWith("shr", StringComparison.Ordinal)) return "位移";
            if (o == "ceq" || o.StartsWith("cgt", StringComparison.Ordinal) || o.StartsWith("clt", StringComparison.Ordinal)) return "比較，結果 1 或 0 推回堆疊";
            if (o == "box" || o.StartsWith("unbox", StringComparison.Ordinal)) return "裝箱／拆箱（值型別與物件互轉）";
            if (o == "break") return "除錯中斷點";
            if (o.StartsWith("br", StringComparison.Ordinal) || (o.StartsWith("b", StringComparison.Ordinal) && (o.Contains("eq") || o.Contains("ne") || o.Contains("lt") || o.Contains("gt") || o.Contains("le") || o.Contains("ge"))))
                return "跳躍（條件或無條件）";
            if (o == "switch") return "跳躍表（依索引跳到不同位置）";
            if (o.StartsWith("conv", StringComparison.Ordinal)) return "型別轉換";
            if (o.StartsWith("ldelem", StringComparison.Ordinal)) return "讀取陣列元素";
            if (o.StartsWith("stelem", StringComparison.Ordinal)) return "寫入陣列元素";
            if (o == "ldlen") return "取得陣列長度";
            if (o == "throw") return "擲回例外";
            if (o == "leave" || o == "leave.s") return "離開 try 區塊";
            if (o == "endfinally") return "finally 區塊結束";
            if (o == "castclass" || o == "isinst") return "型別轉換／型別檢查";
            return "（請參考 ECMA-335 規格）";
        }

        // =====================================================================
        // 函式與位址
        // =====================================================================

        private void ShowFunction(DisassembledFunction f, ulong? focus)
        {
            ResetView(() => ShowFunction(f, _currentAddress));
            _currentFunction = f;
            _currentAddress = focus ?? f.Start;
            SetListing(Disassembler.FunctionHeader(_result, f), f.Lines);
            if (focus.HasValue) FocusAddress(focus.Value);
            SetInfo(FunctionInfo(f));
        }

        private string FunctionInfo(DisassembledFunction f)
        {
            var sb = new StringBuilder();
            sb.Append("【函式】").Append(f.Name).Append("（").Append(f.Origin).Append("）\r\n");
            sb.Append("範圍：0x").Append(f.Start.ToString("X", CultureInfo.InvariantCulture)).Append(" – 0x").Append(f.End.ToString("X", CultureInfo.InvariantCulture))
              .Append("，").Append(f.Lines.Count).Append(" 條指令\r\n");
            var callers = _result.Functions.Where(x => x.Callees.Contains(f.Start)).Select(x => x.Name).Take(12).ToList();
            if (callers.Count > 0) sb.Append("被這些函式呼叫：").Append(string.Join("、", callers)).Append("\r\n");
            if (f.Callees.Count > 0)
                sb.Append("呼叫：").Append(string.Join("、", f.Callees.Take(12).Select(a => { var t = _result.Functions.FirstOrDefault(x => x.Start == a); return t != null ? t.Name : "0x" + a.ToString("X", CultureInfo.InvariantCulture); }))).Append("\r\n");

            if (f.ImportsUsed.Count > 0)
            {
                sb.Append("\r\n【使用的外部函式（API）】\r\n");
                foreach (var api in f.ImportsUsed.Take(15))
                {
                    int bang = api.IndexOf('!');
                    string desc = LibraryCalls.Describe(bang >= 0 ? api.Substring(bang + 1) : api);
                    sb.Append("・").Append(api);
                    if (desc != null) sb.Append("：").Append(desc);
                    sb.Append("\r\n");
                }
            }
            if (f.StringsReferenced.Count > 0)
            {
                sb.Append("\r\n【引用的字串】\r\n");
                foreach (var s in f.StringsReferenced.Take(8)) sb.Append("・\"").Append(Shorten(s, 80)).Append("\"\r\n");
            }

            sb.Append("\r\n【結構觀察】\r\n");
            foreach (var note in StructureNotes(f)) sb.Append("・").Append(note).Append("\r\n");

            var set = InstructionCatalog.Get("x86");
            var common = f.Lines.Where(l => l.Instruction != null && l.Instruction.IsValid)
                .GroupBy(l => l.Instruction.Mnemonic).OrderByDescending(g => g.Count()).Take(8).ToList();
            if (set != null && common.Count > 0)
            {
                sb.Append("\r\n【常用指令】\r\n");
                foreach (var g in common)
                {
                    var info = set.Find(g.Key);
                    sb.Append("・").Append(g.Key).Append(" ×").Append(g.Count());
                    if (info != null) sb.Append("：").Append(info.Description);
                    sb.Append("\r\n");
                }
            }
            sb.Append("\r\n提示：點選清單中的一行可看那條指令的說明；在 call／jmp 那一行按兩下可跳到目標。");
            sb.Append(KnowledgeSnippets(f.ImportsUsed.Count > 0 ? "函式 呼叫慣例 堆疊框架 API" : "函式 堆疊框架 迴圈 條件跳躍", 2));
            return sb.ToString();
        }

        /// <summary>從指令型態推測函式結構（堆疊框架、迴圈、分支、呼叫慣例）。</summary>
        private IEnumerable<string> StructureNotes(DisassembledFunction f)
        {
            var notes = new List<string>();
            var ins = f.Lines.Select(l => l.Instruction).Where(i => i != null).ToList();
            if (ins.Count == 0) return notes;
            string first = ins[0].Text, second = ins.Count > 1 ? ins[1].Text : string.Empty;
            if ((first == "push ebp" && second == "mov ebp, esp") || (first == "push rbp" && second == "mov rbp, rsp") || (first == "push bp" && second == "mov bp, sp"))
                notes.Add("開頭是標準堆疊框架（push ebp／mov ebp, esp）：參數在 [ebp+8] 以上，區域變數在 [ebp-…]。");
            else if (first.StartsWith("enter", StringComparison.Ordinal))
                notes.Add("使用 enter 建立堆疊框架。");
            var frame = ins.Take(8).FirstOrDefault(i => i.Mnemonic == "sub" && i.Operands.Count == 2 && i.Operands[0].Kind == OperandKind.Register &&
                                                         (i.Operands[0].Register == "esp" || i.Operands[0].Register == "rsp" || i.Operands[0].Register == "sp") &&
                                                         i.Operands[1].Kind == OperandKind.Immediate);
            if (frame != null) notes.Add("配置 " + frame.Operands[1].Immediate + "（0x" + frame.Operands[1].Immediate.ToString("X", CultureInfo.InvariantCulture) + "）位元組的區域變數／呼叫空間。");
            if (ins.Count == 1 && ins[0].Flow == InstructionFlow.Jump)
                notes.Add("只有一條 jmp：這是跳板（thunk），直接轉到另一個函式。");
            int loops = ins.Count(i => (i.Flow == InstructionFlow.ConditionalJump || i.Flow == InstructionFlow.Jump || i.Mnemonic.StartsWith("loop", StringComparison.Ordinal)) &&
                                       i.BranchTarget.HasValue && i.BranchTarget.Value <= i.Address && i.BranchTarget.Value >= f.Start);
            if (loops > 0) notes.Add("有 " + loops + " 個往回跳的跳躍：通常代表迴圈。");
            int branches = ins.Count(i => i.Flow == InstructionFlow.ConditionalJump);
            if (branches > 0) notes.Add("有 " + branches + " 個條件跳躍（if／while 等判斷）。");
            int calls = ins.Count(i => i.Flow == InstructionFlow.Call);
            if (calls > 0) notes.Add("呼叫其他函式 " + calls + " 次。");
            var rets = ins.Where(i => i.Flow == InstructionFlow.Return).ToList();
            var retN = rets.FirstOrDefault(i => i.Operands.Count == 1 && i.Operands[0].Kind == OperandKind.Immediate);
            if (retN != null)
            {
                int unit = _binary.Bits == 16 ? 2 : 4;
                long bytes = retN.Operands[0].Immediate;
                notes.Add("以 ret " + bytes + " 返回：由被呼叫者清除 " + bytes + " 位元組的參數（stdcall，約 " + (bytes / unit) + " 個參數）。");
            }
            else if (rets.Count > 0 && _binary.Bits == 32)
            {
                notes.Add("以 ret 返回：若有參數，由呼叫者清除堆疊（cdecl）。");
            }
            if (ins.Any(i => i.Mnemonic == "int" && i.Operands.Count == 1 && i.Operands[0].Immediate == 0x21)) notes.Add("使用 int 21h 呼叫 DOS 功能（AH 決定功能號碼）。");
            if (ins.Any(i => i.Mnemonic == "syscall" || i.Mnemonic == "sysenter")) notes.Add("直接使用系統呼叫指令。");
            if (notes.Count == 0) notes.Add("簡短的直線程式碼，沒有迴圈或分支。");
            return notes;
        }

        /// <summary>前往位址：在已知函式中就顯示該函式並選取那一行，否則從該位址線性反組譯或顯示十六進位內容。</summary>
        private void ShowAddress(ulong address, bool pushHistory)
        {
            if (_binary == null) return;
            if (pushHistory && _currentAddress.HasValue && _currentAddress.Value != address)
            {
                _history.Push(_currentAddress.Value);
                _back.Enabled = true;
            }
            var f = _result != null ? _result.FindFunction(address) : null;
            if (f != null)
            {
                SelectFunctionNode(f);
                ShowFunction(f, address);
                _status("0x" + address.ToString("X", CultureInfo.InvariantCulture) + " 在函式 " + f.Name + " 中");
                return;
            }
            var section = _binary.SectionAt(address);
            if (section == null)
            {
                _status("位址 0x" + address.ToString("X", CultureInfo.InvariantCulture) + " 不在任何區段內");
                Ui.ShowInfo(this, "位址 0x" + address.ToString("X", CultureInfo.InvariantCulture) + " 不在這個檔案的任何區段內。\r\n" +
                                  "請確認輸入的是虛擬位址（VA，例如 0x401000），而不是檔案位移。");
                return;
            }
            ResetView(() => ShowAddress(address, false));
            _currentAddress = address;
            if (section.IsExecutable && _binary.Bits != 0)
            {
                var lines = _disassembler.DisassembleRange(_binary, _result, address, GotoInstructions);
                SetListing("; 從 0x" + address.ToString("X", CultureInfo.InvariantCulture) + " 線性反組譯 " + lines.Count + " 條指令（這裡不在已找到的函式中，若是資料可能解出無意義的指令）\r\n", lines);
                SetInfo("【位址 0x" + address.ToString("X", CultureInfo.InvariantCulture) + "】\r\n區段 " + section.Name + "（" + SectionPurpose(section) + "）\r\n" +
                        "這個位址沒有被追蹤到（沒有被呼叫或跳到）。可以勾選「也掃描沒追蹤到的程式碼」讓 ASSAM 找出更多函式。");
            }
            else
            {
                SetListing(HexDump(address, 1024, "; 0x" + address.ToString("X", CultureInfo.InvariantCulture) + " 的資料（區段 " + section.Name + "）\r\n"), null);
                var str = _binary.Strings.FirstOrDefault(s => s.Address == address);
                SetInfo("【資料位址 0x" + address.ToString("X", CultureInfo.InvariantCulture) + "】\r\n區段 " + section.Name + "：" + SectionPurpose(section) +
                        (str != null ? "\r\n\r\n這裡是字串：\"" + str.Text + "\"" : string.Empty));
            }
            _status("已前往 0x" + address.ToString("X", CultureInfo.InvariantCulture));
        }

        private void GoTo(string text)
        {
            if (_binary == null || string.IsNullOrWhiteSpace(text)) return;
            ulong address;
            if (!TryParseAddress(text.Trim(), out address))
            {
                Ui.ShowInfo(this, "找不到「" + text.Trim() + "」。\r\n請輸入十六進位位址（例如 401000、0x401000、401000h，DOS 可用 0000:0100），" +
                                  "或函式／符號名稱（例如 entry、sub_401000、匯出函式名稱）。");
                return;
            }
            ShowAddress(address, true);
        }

        private bool TryParseAddress(string text, out ulong address)
        {
            address = 0;
            if (_symbolLookup.TryGetValue(text, out address)) return true;
            string t = text.Replace("`", string.Empty).Replace("_", string.Empty);
            int colon = t.IndexOf(':');
            if (colon > 0 && _binary.Bits == 16)
            {
                ulong seg, off;
                if (ulong.TryParse(t.Substring(0, colon), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out seg) &&
                    ulong.TryParse(t.Substring(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out off))
                {
                    address = (_binary.Kind == BinaryKind.DosCom ? 0 : seg * 16) + off;
                    return true;
                }
            }
            var m = Regex.Match(text, @"^(?:sub|loc|j|str|data|off|unk|byte|word|dword)_([0-9A-Fa-f]+)$");
            if (m.Success) t = m.Groups[1].Value;
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            else if (t.EndsWith("h", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 1);
            return t.Length > 0 && t.Length <= 16 && ulong.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
        }

        private void GoBack()
        {
            if (_history.Count == 0) return;
            ShowAddress(_history.Pop(), false);
            _back.Enabled = _history.Count > 0;
        }

        /// <summary>在清單中按兩下：跳到 call／jmp 的目標，或引用的字串／資料（在交叉參考清單中則跳到該行）。</summary>
        private void FollowCaret()
        {
            AsmListingLine line;
            if (!_lineMap.TryGetValue(CaretRow(), out line) || line.Instruction == null) return;
            if (_xrefMode)
            {
                ShowAddress(line.Address, true);
                return;
            }
            var ins = line.Instruction;
            if (ins.BranchTarget.HasValue && _binary.SectionAt(ins.BranchTarget.Value) != null)
            {
                ShowAddress(ins.BranchTarget.Value, true);
                return;
            }
            foreach (var op in ins.Operands)
            {
                ulong target;
                if (op.AbsoluteAddress.HasValue) target = op.AbsoluteAddress.Value;
                else if (op.Kind == OperandKind.Immediate) target = _binary.Bits == 64 ? (ulong)op.Immediate : (ulong)(uint)op.Immediate;
                else continue;
                var import = _binary.ImportAt(target);
                if (import != null)
                {
                    PushHistory();
                    ShowImport(import);
                    return;
                }
                var str = _binary.Strings.FirstOrDefault(s => s.Address == target);
                if (str != null)
                {
                    PushHistory();
                    ShowString(str);
                    return;
                }
                if (_binary.SectionAt(target) != null && target > 0xFF)
                {
                    ShowAddress(target, true);
                    return;
                }
            }
        }

        private void PushHistory()
        {
            if (!_currentAddress.HasValue) return;
            _history.Push(_currentAddress.Value);
            _back.Enabled = true;
        }

        private int CaretRow()
        {
            return _listing.GetLineFromCharIndex(_listing.SelectionStart);
        }

        private void ShowCaretInstruction()
        {
            int row = CaretRow();
            if (row == _lastInfoRow) return;
            _lastInfoRow = row;
            AsmListingLine line;
            if (!_lineMap.TryGetValue(row, out line) || line.Instruction == null) return;
            if (!_xrefMode) _currentAddress = line.Address;
            SetInfo(InstructionInfo(line));
        }

        private string InstructionInfo(AsmListingLine line)
        {
            var ins = line.Instruction;
            var sb = new StringBuilder();
            sb.Append("【指令】").Append(line.Code).Append("\r\n");
            sb.Append("位址 0x").Append(line.Address.ToString("X", CultureInfo.InvariantCulture)).Append("，機器碼 ").Append(ins.HexBytes)
              .Append("（").Append(ins.Length).Append(" 位元組）\r\n");
            var f = _result != null ? _result.FindFunction(line.Address) : null;
            if (f != null) sb.Append("所在函式：").Append(f.Name).Append("\r\n");
            if (!ins.IsValid)
            {
                sb.Append("\r\n這幾個位元組不是有效的指令：可能是夾在程式碼中的資料、對齊用的填充，或是線性反組譯從指令中間開始解碼。\r\n");
                return sb.ToString();
            }
            if (!string.IsNullOrEmpty(line.Comment)) sb.Append("註解：").Append(line.Comment).Append("\r\n");

            var set = InstructionCatalog.Get("x86");
            var info = set != null ? set.Find(ins.Mnemonic) : null;
            if (info != null)
            {
                sb.Append("\r\n【").Append(info.MnemonicText).Append("】").Append(info.Description).Append("\r\n");
                if (!string.IsNullOrEmpty(info.Syntax)) sb.Append("語法：").Append(info.Syntax).Append("\r\n");
                if (!string.IsNullOrEmpty(info.Flags)) sb.Append("影響旗標：").Append(info.Flags).Append("\r\n");
            }
            try
            {
                var explained = _assistant.Explainer.Explain(line.ToSourceText(), AsmArch.X86Intel).Lines.FirstOrDefault(l => l.Kind == "指令");
                if (explained != null)
                {
                    sb.Append("\r\n【說明】").Append(explained.Explanation).Append("\r\n");
                    if (!string.IsNullOrEmpty(explained.Action)) sb.Append("動作：").Append(explained.Action).Append("\r\n");
                }
            }
            catch (Exception)
            {
                // 說明只是輔助資訊，解析失敗時略過
            }
            if (ins.BranchTarget.HasValue)
            {
                string name;
                string target = _result != null && _result.Symbols.TryGetValue(ins.BranchTarget.Value, out name) ? name : "0x" + ins.BranchTarget.Value.ToString("X", CultureInfo.InvariantCulture);
                sb.Append("\r\n目標：").Append(target).Append("（按兩下這一行可以跳過去）\r\n");
                if (ins.Flow == InstructionFlow.ConditionalJump) sb.Append("條件成立才跳，否則繼續執行下一行。\r\n");
            }
            if (ins.Prefix != null && ins.Prefix.Contains("lock")) sb.Append("lock 前綴：讓這個讀取-修改-寫入的動作在多核心下不可分割（原子操作）。\r\n");
            if (ins.Prefix != null && ins.Prefix.StartsWith("rep", StringComparison.Ordinal)) sb.Append(ins.Prefix + " 前綴：重複執行字串指令，每次 ecx 減 1，直到 ecx 為 0" + (ins.Prefix != "rep" ? "或旗標條件不成立" : string.Empty) + "。\r\n");
            return sb.ToString();
        }

        // =====================================================================
        // 交叉參考
        // =====================================================================

        private List<AsmListingLine> FindXrefs(Func<DecodedInstruction, bool> predicate)
        {
            var list = new List<AsmListingLine>();
            if (_result == null) return list;
            foreach (var f in _result.Functions)
            {
                foreach (var line in f.Lines)
                {
                    if (line.Instruction == null || !predicate(line.Instruction)) continue;
                    list.Add(line);
                    if (list.Count >= MaxXrefLines) return list;
                }
            }
            return list;
        }

        private static bool ReferencesAddress(DecodedInstruction ins, ulong address)
        {
            foreach (var op in ins.Operands)
            {
                if (op.AbsoluteAddress.HasValue && op.AbsoluteAddress.Value == address) return true;
                if (op.Kind == OperandKind.Immediate && op.Immediate != 0 &&
                    ((ulong)op.Immediate == address || (ulong)(uint)op.Immediate == address)) return true;
            }
            return false;
        }

        private void ShowXrefs(string header, List<AsmListingLine> xrefs)
        {
            var sb = new StringBuilder(header);
            _lineMap.Clear();
            int digits = _result != null ? _result.AddressDigits : 8;
            int row = CountLines(header);
            if (xrefs.Count == 0)
            {
                sb.Append("; （沒有找到）\r\n");
            }
            else
            {
                string lastFunction = null;
                foreach (var line in xrefs)
                {
                    var f = _result.FindFunction(line.Address);
                    string fname = f != null ? f.Name : "?";
                    if (fname != lastFunction)
                    {
                        sb.Append(fname).Append(":\r\n");
                        row++;
                        lastFunction = fname;
                    }
                    _lineMap[row] = line;
                    sb.Append(line.ToListingText(_showBytes.Checked, digits)).Append("\r\n");
                    row++;
                }
                if (xrefs.Count >= MaxXrefLines) sb.Append("; ……超過 ").Append(MaxXrefLines).Append(" 處，只列出前面的部分\r\n");
            }
            _xrefMode = true;
            _listing.Text = sb.ToString();
            _listing.Select(0, 0);
            _listing.ScrollToCaret();
            _lastInfoRow = -1;
        }

        // =====================================================================
        // 傳送與匯出
        // =====================================================================

        /// <summary>目前選取的函式：游標所在行的函式優先，其次是正在顯示的函式。</summary>
        private DisassembledFunction SelectedFunction()
        {
            if (_result == null) return null;
            AsmListingLine line;
            if (_lineMap.TryGetValue(CaretRow(), out line))
            {
                var f = _result.FindFunction(line.Address);
                if (f != null) return f;
            }
            return _currentFunction;
        }

        private void Send(Action<string> handler, string target)
        {
            if (_binary == null) return;
            if (_currentMethod != null && _currentFunction == null)
            {
                Ui.ShowInfo(this, ".NET 的 IL 不是 x86 組合語言，無法送到「" + target + "」。請改選原生（x86／x64）函式。");
                return;
            }
            var f = SelectedFunction();
            if (f == null)
            {
                Ui.ShowInfo(this, "請先在左邊「函式」中選擇一個函式（或在清單中點選某一行）。");
                return;
            }
            string code = _disassembler.FunctionSource(_result, f, _withCallees.Checked);
            if (handler != null) handler(code);
            _status("已把 " + f.Name + (_withCallees.Checked ? "（含呼叫的函式）" : string.Empty) + " 送到「" + target + "」");
        }

        private async void ExportListing()
        {
            if (_binary == null || _busy) return;
            string fileName;
            using (var dlg = new SaveFileDialog
            {
                Title = "匯出完整反組譯清單",
                Filter = "反組譯清單 (*.lst)|*.lst|文字檔 (*.txt)|*.txt|所有檔案 (*.*)|*.*",
                FileName = SafeBaseName() + ".lst"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                fileName = dlg.FileName;
            }
            var binary = _binary;
            var result = _result;
            var cil = _cil;
            bool showBytes = _showBytes.Checked;
            SetBusy(true, "正在匯出 " + Path.GetFileName(fileName) + "…");
            try
            {
                await Task.Run(() => WriteListing(fileName, binary, result, cil, showBytes));
                if (IsDisposed) return;
                _status("已匯出 " + fileName);
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                Ui.ShowError(this, "匯出失敗：" + ex.Message);
            }
            finally
            {
                if (!IsDisposed) SetBusy(false, null);
            }
        }

        private static void WriteListing(string fileName, LoadedBinary binary, DisassemblyResult result, CilDisassemblyResult cil, bool showBytes)
        {
            var disassembler = new Disassembler();
            using (var w = new StreamWriter(fileName, false, new UTF8Encoding(true)))
            {
                w.Write("; ASSAM 反組譯清單：" + binary.FileName + "\r\n");
                foreach (var line in binary.Describe().Replace("\r\n", "\n").Split('\n')) w.Write("; " + line + "\r\n");
                w.Write("\r\n");
                if (result != null)
                {
                    foreach (var f in result.Functions) w.Write(disassembler.ToListing(result, f, showBytes));
                }
                if (cil != null)
                {
                    w.Write("\r\n// ===== .NET IL =====\r\n");
                    foreach (var t in cil.Types)
                        foreach (var m in t.Methods) w.Write(m.Text + "\r\n");
                }
                if (binary.Strings.Count > 0)
                {
                    w.Write("\r\n; ===== 字串 =====\r\n");
                    foreach (var s in binary.Strings) w.Write("; " + s + "\r\n");
                }
            }
        }

        private void ExportFunctionSource()
        {
            if (_binary == null) return;
            var f = SelectedFunction();
            if (f == null)
            {
                Ui.ShowInfo(this, "請先在左邊「函式」中選擇一個函式。");
                return;
            }
            using (var dlg = new SaveFileDialog
            {
                Title = "匯出函式原始碼",
                Filter = "組合語言 (*.asm)|*.asm|所有檔案 (*.*)|*.*",
                FileName = SafeBaseName() + "_" + f.Name + ".asm"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dlg.FileName, _disassembler.FunctionSource(_result, f, _withCallees.Checked), new UTF8Encoding(true));
                    _status("已匯出 " + dlg.FileName);
                }
                catch (Exception ex)
                {
                    Ui.ShowError(this, "匯出失敗：" + ex.Message);
                }
            }
        }

        private string SafeBaseName()
        {
            string name = Path.GetFileNameWithoutExtension(_binary.FileName ?? "output");
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 0 ? name : "output";
        }

        // =====================================================================
        // 共用
        // =====================================================================

        /// <summary>切換到新的檢視：記住如何重畫（切換「顯示機器碼」時用）、清除行對應。</summary>
        private void ResetView(Action refresh)
        {
            _refreshView = refresh;
            _currentFunction = null;
            _currentMethod = null;
            _xrefMode = false;
        }

        private void RefreshView()
        {
            if (_binary == null || _refreshView == null) return;
            var refresh = _refreshView;
            int row = CaretRow();
            refresh();
            if (row > 0) SelectRow(row);
        }

        /// <summary>顯示清單：header 是開頭的註解，lines 是指令（會記住每一行對應哪條指令）。</summary>
        private void SetListing(string header, IList<AsmListingLine> lines)
        {
            _lineMap.Clear();
            _lastInfoRow = -1;
            var sb = new StringBuilder(header ?? string.Empty);
            if (lines != null)
            {
                int row = CountLines(header);
                int digits = _result != null ? _result.AddressDigits : _binary == null ? 8 : _binary.Bits == 64 ? 16 : _binary.Bits == 16 ? 5 : 8;
                bool showBytes = _showBytes.Checked;
                foreach (var line in lines)
                {
                    if (line.Label != null)
                    {
                        sb.Append(line.Label).Append(":\r\n");
                        row++;
                    }
                    _lineMap[row] = line;
                    sb.Append(line.ToListingText(showBytes, digits)).Append("\r\n");
                    row++;
                }
            }
            _listing.Text = sb.ToString();
            _listing.Select(0, 0);
            _listing.ScrollToCaret();
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int n = 0;
            foreach (char c in text) if (c == '\n') n++;
            return n;
        }

        private void FocusAddress(ulong address)
        {
            int best = -1;
            ulong bestAddress = 0;
            foreach (var kv in _lineMap)
            {
                ulong a = kv.Value.Address;
                if (a <= address && (best < 0 || a > bestAddress))
                {
                    best = kv.Key;
                    bestAddress = a;
                }
            }
            if (best >= 0) SelectRow(best);
        }

        private void SelectRow(int row)
        {
            if (row < 0) return;
            int start = _listing.GetFirstCharIndexFromLine(row);
            if (start < 0) return;
            int next = _listing.GetFirstCharIndexFromLine(row + 1);
            int length = next < 0 ? _listing.TextLength - start : Math.Max(0, next - start - 2);
            int top = _listing.GetFirstCharIndexFromLine(Math.Max(0, row - 5));
            _listing.Select(Math.Max(0, top), 0);
            _listing.ScrollToCaret();
            _listing.Select(start, length);
            _lastInfoRow = -1;
        }

        private void SetInfo(string text)
        {
            string t = (text ?? string.Empty).Replace("\r\n", "\n");
            _info.Text = t;
            int index = 0;
            foreach (var line in t.Split('\n'))
            {
                if (line.StartsWith("【", StringComparison.Ordinal))
                {
                    int end = line.IndexOf('】');
                    _info.Select(index, end > 0 ? end + 1 : line.Length);
                    _info.SelectionFont = Ui.BoldFont;
                }
                index += line.Length + 1;
            }
            _info.Select(0, 0);
        }

        private string HexDump(ulong address, int count, string header)
        {
            var sb = new StringBuilder(header);
            var bytes = _binary.ReadVa(address, Math.Max(0, count));
            if (bytes.Length == 0) return sb.Append("; （檔案中沒有這段資料，例如 .bss 這種載入時才清為 0 的區域）\r\n").ToString();
            int digits = _binary.Bits == 64 ? 16 : _binary.Bits == 16 ? 5 : 8;
            for (int i = 0; i < bytes.Length; i += 16)
            {
                sb.Append((address + (ulong)i).ToString("X" + digits, CultureInfo.InvariantCulture)).Append("  ");
                var ascii = new StringBuilder();
                for (int j = 0; j < 16; j++)
                {
                    if (i + j < bytes.Length)
                    {
                        byte b = bytes[i + j];
                        sb.Append(b.ToString("X2", CultureInfo.InvariantCulture)).Append(j == 7 ? "  " : " ");
                        ascii.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                    }
                    else
                    {
                        sb.Append(j == 7 ? "    " : "   ");
                    }
                }
                sb.Append(' ').Append(ascii).Append("\r\n");
            }
            return sb.ToString();
        }

        private string KnowledgeSnippets(string query, int count)
        {
            try
            {
                var hits = _assistant.Search.Search(query, count, "x86");
                if (hits.Count == 0) return string.Empty;
                var sb = new StringBuilder("\r\n\r\n【相關知識】（到「知識庫」頁可看全文）\r\n");
                foreach (var h in hits)
                {
                    string body = Regex.Replace(h.Entry.Body ?? string.Empty, @"[`*#>|]+", string.Empty).Replace("\r", " ").Replace("\n", " ");
                    sb.Append("・").Append(h.Entry.Title).Append("：").Append(Shorten(Regex.Replace(body, @"\s+", " ").Trim(), 90)).Append("\r\n");
                }
                return sb.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }
    }
}
