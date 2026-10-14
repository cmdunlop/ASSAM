using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Assam.App.Pages;
using Assam.Core.Assistant;

namespace Assam.App
{
    /// <summary>
    /// 主視窗：七個分頁（智慧問答、程式碼分析、推導模擬、反組譯、數值推導、知識庫、學習記憶）。
    /// 介面全部以程式碼建立（DesignerCategory = Code），在 VS2019 中直接開啟程式碼即可修改。
    /// </summary>
    [DesignerCategory("Code")]
    public sealed class MainForm : Form
    {
        private readonly AsmAssistant _assistant;
        private readonly TabControl _tabs;
        private readonly ToolStripStatusLabel _status;
        private readonly ChatPage _chat;
        private readonly CodePage _code;
        private readonly SimulationPage _simulation;
        private readonly DisassemblyPage _disassembly;
        private readonly NumberPage _numbers;
        private readonly KnowledgePage _knowledge;
        private readonly MemoryPage _memory;

        public MainForm()
        {
            Text = "ASSAM 離線組合語言 AI 助理";
            Font = Ui.UiFont;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 600);
            Size = new Size(1180, 780);
            Icon = SystemIcons.Application;

            Cursor = Cursors.WaitCursor;
            _assistant = AsmAssistant.CreateDefault();
            Cursor = Cursors.Default;

            _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            var statusStrip = new StatusStrip();
            statusStrip.Items.Add(_status);
            statusStrip.Items.Add(new ToolStripStatusLabel("離線模式：不使用網路") { ForeColor = Color.DarkGreen });

            _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            _chat = new ChatPage(_assistant, SetStatus);
            _code = new CodePage(_assistant, SetStatus);
            _simulation = new SimulationPage(_assistant, SetStatus);
            _disassembly = new DisassemblyPage(_assistant, SetStatus);
            _numbers = new NumberPage(SetStatus);
            _knowledge = new KnowledgePage(_assistant, SetStatus);
            _memory = new MemoryPage(_assistant, SetStatus);

            AddTab("智慧問答", _chat);
            AddTab("程式碼分析", _code);
            AddTab("推導模擬（x86）", _simulation);
            AddTab("反組譯（EXE／DLL）", _disassembly);
            AddTab("數值推導", _numbers);
            AddTab("知識庫", _knowledge);
            AddTab("學習記憶", _memory);

            _code.SendToSimulation += code =>
            {
                _simulation.LoadCode(code);
                _tabs.SelectedTab = (TabPage)_simulation.Parent;
            };
            _disassembly.SendToAnalysis += code =>
            {
                _code.LoadCode(code, "反組譯結果");
                _tabs.SelectedTab = (TabPage)_code.Parent;
            };
            _disassembly.SendToSimulation += code =>
            {
                _simulation.LoadCode(code);
                _tabs.SelectedTab = (TabPage)_simulation.Parent;
            };
            _memory.KnowledgeReloaded += () => _knowledge.RefreshTree();
            _chat.KnowledgeChanged += () => _knowledge.RefreshTree();
            _tabs.SelectedIndexChanged += (s, e) =>
            {
                if (_tabs.SelectedTab != null && _tabs.SelectedTab.Controls.Contains(_memory)) _memory.RefreshStats();
            };

            var menu = BuildMenu();
            Controls.Add(_tabs);
            Controls.Add(statusStrip);
            Controls.Add(menu);
            MainMenuStrip = menu;

            SetStatus(_assistant.StartupMessage);
            if (!string.IsNullOrEmpty(_assistant.Memory.LastMessage) && _assistant.Memory.LastMessage.Contains("無法讀取"))
                Shown += (s, e) => Ui.ShowError(this, _assistant.Memory.LastMessage);
        }

        private void AddTab(string title, Control page)
        {
            var tab = new TabPage(title) { Padding = new Padding(4), UseVisualStyleBackColor = true };
            page.Dock = DockStyle.Fill;
            tab.Controls.Add(page);
            _tabs.TabPages.Add(tab);
        }

        private MenuStrip BuildMenu()
        {
            var menu = new MenuStrip { Font = Ui.UiFont };
            var file = new ToolStripMenuItem("檔案(&F)");
            file.DropDownItems.Add("開啟組合語言檔案…", null, (s, e) => OpenCodeFile());
            file.DropDownItems.Add("開啟 EXE／DLL 反組譯…", null, (s, e) =>
            {
                _tabs.SelectedTab = (TabPage)_disassembly.Parent;
                _disassembly.OpenFile();
            });
            file.DropDownItems.Add("開啟學習記憶資料夾", null, (s, e) => OpenFolder(_assistant.Memory.DataDirectory));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("結束(&X)", null, (s, e) => Close());

            var help = new ToolStripMenuItem("說明(&H)");
            help.DropDownItems.Add("使用說明", null, (s, e) =>
            {
                _tabs.SelectedIndex = 0;
                _chat.AskQuestion("/help");
            });
            help.DropDownItems.Add("關於 ASSAM", null, (s, e) => Ui.ShowInfo(this,
                "ASSAM 離線組合語言 AI 助理 v" + AsmAssistant.Version + "\r\n\r\n" +
                "・完全離線：不需要網路，也不會傳送任何資料\r\n" +
                "・本機學習：回饋與教學存在\r\n  " + _assistant.Memory.MemoryFilePath + "\r\n" +
                "・技術：BM25 檢索、單純貝氏分類、線上學習、規則式專家系統、x86 指令模擬、x86／x64 反組譯與 .NET IL 解析\r\n" +
                "・反組譯只讀取檔案做靜態分析，不會執行載入的 EXE／DLL\r\n" +
                "・需求：Windows 7 SP1 以上、.NET Framework 4.7.2、記憶體 1GB 以上即可"));

            menu.Items.Add(file);
            menu.Items.Add(help);
            return menu;
        }

        private void OpenCodeFile()
        {
            using (var dlg = new OpenFileDialog { Filter = "組合語言檔案 (*.asm;*.s;*.S;*.a51;*.inc;*.txt)|*.asm;*.s;*.S;*.a51;*.inc;*.txt|所有檔案 (*.*)|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _code.LoadCode(Ui.ReadTextFile(dlg.FileName), Path.GetFileName(dlg.FileName));
                _tabs.SelectedTab = (TabPage)_code.Parent;
            }
        }

        internal static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("無法開啟資料夾：" + ex.Message, "ASSAM");
            }
        }

        private void SetStatus(string text)
        {
            _status.Text = text ?? string.Empty;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _assistant.Save();
            base.OnFormClosing(e);
        }
    }
}
