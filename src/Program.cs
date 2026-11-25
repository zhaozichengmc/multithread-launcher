using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PclDlGui
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // 兜底：任何未处理异常都写日志 + 弹窗，不再静默闪退
            NativeBootstrap.PrepareWebView2();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ReportCrash(e.Exception, "UI 线程");
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportCrash(e.ExceptionObject as Exception, "后台线程");

            bool created;
            using (var mutex = new Mutex(true, "PCLDownloader.SingleInstance.v2", out created))
            {
                if (!created) return;   // 已有实例在运行，静默退出

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    Application.Run(new MainForm(args));
                }
                catch (Exception ex)
                {
                    ReportCrash(ex, "启动");
                }
            }
        }

        /// <summary>异常写入 %APPDATA%\PCLDownloader\crash.log，并提示日志位置</summary>
        public static void ReportCrash(Exception ex, string where)
        {
            string logPath = "";
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCLDownloader");
                Directory.CreateDirectory(dir);
                logPath = Path.Combine(dir, "crash.log");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + where);
                sb.AppendLine("版本：" + UpdateChecker.CurrentVersion + "    系统：" + Environment.OSVersion + "    " + (Environment.Is64BitProcess ? "x64" : "x86"));
                sb.AppendLine(ex == null ? "（无异常对象）" : ex.ToString());
                sb.AppendLine(new string('-', 60));
                File.AppendAllText(logPath, sb.ToString(), System.Text.Encoding.UTF8);
            }
            catch { }

            try
            {
                string msg = "程序遇到错误：" + (ex == null ? "未知错误" : ex.Message);
                if (ex is FileNotFoundException || ex is TypeLoadException)
                    msg += "\r\n\r\n像是缺运行库文件（Newtonsoft.Json.dll / WebView2 的 dll）。\r\n请用完整版压缩包，exe 和同目录 dll 要放在一起，别单独拷 exe。";
                msg += "\r\n\r\n日志：" + (logPath.Length > 0 ? logPath : "（写入失败）");
                MessageBox.Show(msg, "光速下载器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
