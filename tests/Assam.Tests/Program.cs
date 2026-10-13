using System;
using System.Text;

namespace Assam.Tests
{
    /// <summary>
    /// 測試程式：在 VS2019 中把 Assam.Tests 設為起始專案後按 Ctrl+F5 執行，
    /// 或在命令列執行 Assam.Tests.exe [篩選字串]。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (Exception)
            {
                // 某些主控台不支援變更編碼，忽略即可
            }
            Console.WriteLine("ASSAM 單元測試");
            Console.WriteLine(new string('=', 40));
            int code = TestRunner.RunAll(args.Length > 0 ? args[0] : null);
            if (Environment.UserInteractive && args.Length == 0 && !Console.IsInputRedirected && Environment.GetEnvironmentVariable("ASSAM_NO_PAUSE") == null)
            {
                Console.WriteLine("按任意鍵結束…");
                try { Console.ReadKey(); } catch (InvalidOperationException) { }
            }
            return code;
        }
    }
}
