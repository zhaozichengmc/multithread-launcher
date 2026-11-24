using System;
using System.Threading;
using System.Windows.Forms;

namespace PclDlGui
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "PCLDownloader.SingleInstance.v2", out created))
            {
                if (!created) return;   // 已有实例在运行，静默退出

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(args));
            }
        }
    }
}
