using System;
using System.Threading;
using System.Windows.Forms;

namespace Assam.App
{
    internal static class Program
    {
        /// <summary>ASSAM 離線組合語言 AI 助理的進入點。</summary>
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                MessageBox.Show("發生未預期的錯誤：\r\n" + e.ExceptionObject, "ASSAM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Run(new MainForm());
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            MessageBox.Show("發生錯誤（程式會繼續執行）：\r\n" + e.Exception.Message + "\r\n\r\n" + e.Exception.StackTrace,
                            "ASSAM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
