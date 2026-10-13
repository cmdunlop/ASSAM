using System;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using Assam.Core.Assistant;

namespace Assam.App.Pages
{
    /// <summary>學習記憶：查看學到了什麼、歷史紀錄，以及備份、匯入、清除記憶。</summary>
    [DesignerCategory("Code")]
    public sealed class MemoryPage : UserControl
    {
        private readonly AsmAssistant _assistant;
        private readonly Action<string> _status;
        private readonly RichTextBox _stats;
        private readonly ListBox _history;

        public event Action KnowledgeReloaded;

        public MemoryPage(AsmAssistant assistant, Action<string> status)
        {
            _assistant = assistant;
            _status = status;

            var layout = Ui.Rows(-1, 45, -1, 55);
            Controls.Add(layout);

            var bar = Ui.Toolbar();
            bar.Controls.Add(Ui.Button("重新整理", (s, e) => RefreshStats()));
            bar.Controls.Add(Ui.Button("開啟資料夾", (s, e) => MainForm.OpenFolder(_assistant.Memory.DataDirectory)));
            bar.Controls.Add(Ui.Button("匯出記憶（備份）…", (s, e) => Export()));
            bar.Controls.Add(Ui.Button("匯入記憶…", (s, e) => Import()));
            bar.Controls.Add(Ui.Button("重新載入知識庫", (s, e) => Reload()));
            bar.Controls.Add(Ui.Button("清除所有學習記憶…", (s, e) => Reset()));
            layout.Controls.Add(bar, 0, 0);

            _stats = Ui.ReadOnlyText(false);
            layout.Controls.Add(_stats, 0, 1);
            layout.Controls.Add(Ui.Label("最近的問題（新的在上面）", true), 0, 2);
            _history = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
            layout.Controls.Add(_history, 0, 3);
            RefreshStats();
        }

        public void RefreshStats()
        {
            _stats.Text = _assistant.StatsText() +
                          "\r\n說明：\r\n" +
                          "・「有幫助／沒幫助」會調整「問題中的詞 → 知識條目」的權重（相關回饋學習）\r\n" +
                          "・「這個才是答案」會記住整句問題對應的答案\r\n" +
                          "・「教我正確答案」新增知識卡，並與問題建立強關聯\r\n" +
                          "・「更正架構」用線上學習調整架構辨識模型；「更正類型」重新訓練單純貝氏分類器\r\n" +
                          "・記憶檔是 JSON 純文字，可以複製到其他電腦的 UserData 資料夾繼續使用";
            _history.BeginUpdate();
            _history.Items.Clear();
            foreach (var h in _assistant.Memory.Data.History.AsEnumerable().Reverse()) _history.Items.Add(h);
            _history.EndUpdate();
        }

        private void Export()
        {
            using (var dlg = new SaveFileDialog { Filter = "ASSAM 記憶檔 (*.json)|*.json", FileName = "assam_memory_" + DateTime.Now.ToString("yyyyMMdd") + ".json" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _assistant.Memory.ExportTo(dlg.FileName);
                _status("已匯出記憶到 " + dlg.FileName);
            }
        }

        private void Import()
        {
            using (var dlg = new OpenFileDialog { Filter = "ASSAM 記憶檔 (*.json)|*.json" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (MessageBox.Show(this, "匯入會取代目前的學習記憶（目前的記憶會先自動備份）。確定嗎？", "ASSAM", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                try
                {
                    _assistant.Memory.ImportFrom(dlg.FileName);
                    Reload();
                    _status("已匯入記憶：" + dlg.FileName);
                }
                catch (Exception ex)
                {
                    Ui.ShowError(this, "匯入失敗，檔案格式不正確：" + ex.Message);
                }
            }
        }

        private void Reload()
        {
            _assistant.ReloadKnowledge();
            RefreshStats();
            var handler = KnowledgeReloaded;
            if (handler != null) handler();
            _status(_assistant.StartupMessage);
        }

        private void Reset()
        {
            if (MessageBox.Show(this, "確定要清除所有學習記憶（教學、回饋、更正、筆記、歷史）嗎？\r\n目前的記憶會先備份到資料夾中。", "ASSAM",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            string backup = _assistant.Memory.Reset();
            Reload();
            Ui.ShowInfo(this, "已清除學習記憶。" + (backup != null ? "\r\n備份檔：" + backup : string.Empty));
        }
    }
}
