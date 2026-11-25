using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PclDlGui
{
    /// <summary>
    /// 单文件分发时的原生依赖处理：WebView2Loader.dll 不能像托管库那样被 Costura 直接加载，
    /// 所以内嵌一份，启动时释放到临时目录并把该目录塞进 DLL 搜索路径。
    /// </summary>
    internal static class NativeBootstrap
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static string _preparedDir;

        public static void PrepareWebView2()
        {
            try
            {
                // exe 同目录已经有了（完整版压缩包 / 安装版），直接用，不折腾
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                if (File.Exists(Path.Combine(exeDir, "WebView2Loader.dll"))) return;

                string resourceName = FindResource();
                if (resourceName == null) return;

                string dir = Path.Combine(Path.GetTempPath(), "LightspeedWebView2");
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, "WebView2Loader.dll");
                bool needWrite = true;
                if (File.Exists(target))
                {
                    try { needWrite = new FileInfo(target).Length == 0; } catch { }
                }
                if (needWrite)
                {
                    using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                    using (var dst = File.Create(target))
                        src.CopyTo(dst);
                }

                _preparedDir = dir;
                try { SetDllDirectory(dir); } catch { }
                // 双保险：PATH 里也加上，LoadLibrary 会按 PATH 找
                try
                {
                    string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                    if (path.IndexOf(dir, StringComparison.OrdinalIgnoreCase) < 0)
                        Environment.SetEnvironmentVariable("PATH", dir + ";" + path);
                }
                catch { }
            }
            catch { /* 失败就让浏览器降级到系统浏览器，不影响主程序 */ }
        }

        private static string FindResource()
        {
            var asm = Assembly.GetExecutingAssembly();
            foreach (var n in asm.GetManifestResourceNames())
                if (n.EndsWith("WebView2Loader.dll", StringComparison.OrdinalIgnoreCase))
                    return n;
            return null;
        }

        public static string PreparedDir { get { return _preparedDir; } }
    }
}
