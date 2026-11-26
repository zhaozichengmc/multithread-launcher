using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace PclDl
{
    /// <summary>
    /// Windows 网络参数临时调优：先读取当前值，应用吞吐优先的设置，下载结束后按快照还原。
    /// 只动 netsh 的全局项（可读可写可还原），不碰注册表，不改驱动参数。
    /// </summary>
    public static class NetTuner
    {
        private static string _auto = "normal";
        private static string _rss = "enabled";
        private static string _ts = "disabled";
        private static bool _changedAuto, _changedRss, _changedTs;
        private static bool _captured;

        public static bool Applied { get { return _changedAuto || _changedRss || _changedTs; } }

        public static bool IsAdmin
        {
            get
            {
                try
                {
                    using (var id = WindowsIdentity.GetCurrent())
                        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        private static string MarkerPath
        {
            get
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PclDlCache");
                try { System.IO.Directory.CreateDirectory(dir); } catch { }
                return System.IO.Path.Combine(dir, "net_snapshot.txt");
            }
        }

        /// <summary>上次异常退出未还原时，启动时补还原</summary>
        public static void RecoverIfNeeded()
        {
            try
            {
                if (!System.IO.File.Exists(MarkerPath)) return;
                string[] lines = System.IO.File.ReadAllLines(MarkerPath);
                if (lines.Length >= 3)
                {
                    if (IsAdmin)
                    {
                        string o;
                        Exec("int tcp set global autotuninglevel=" + lines[0], out o);
                        Exec("int tcp set global rss=" + lines[1], out o);
                        Exec("int tcp set global timestamps=" + lines[2], out o);
                    }
                }
                System.IO.File.Delete(MarkerPath);
            }
            catch { }
        }

        public static void Apply(Action<string> log)
        {
            if (!IsAdmin)
            {
                if (log != null) log("网络调优：需要管理员权限，已跳过系统级调整（线程缓冲仍会加大）");
                return;
            }

            if (!Capture(log))
            {
                if (log != null) log("网络调优：读取当前设置失败，为保证可还原，本次不做系统级调整");
                return;
            }

            if (_auto != "normal" && Run("int tcp set global autotuninglevel=normal")) _changedAuto = true;
            if (_rss != "enabled" && Run("int tcp set global rss=enabled")) _changedRss = true;
            if (_ts != "disabled" && Run("int tcp set global timestamps=disabled")) _changedTs = true;

            if (Applied)
            {
                try { System.IO.File.WriteAllText(MarkerPath, _auto + "\n" + _rss + "\n" + _ts); } catch { }
            }

            if (log == null) return;
            if (Applied)
            {
                var what = new StringBuilder();
                if (_changedAuto) what.Append("窗口自动调优→normal ");
                if (_changedRss) what.Append("RSS→开启 ");
                if (_changedTs) what.Append("时间戳→关闭 ");
                log("网络调优：已应用（" + what.ToString().Trim() + "），下载结束自动还原");
            }
            else log("网络调优：当前已是吞吐优先设置，无需改动");
        }

        public static void Restore(Action<string> log)
        {
            if (!Applied) return;

            if (!IsAdmin)
            {
                _changedAuto = _changedRss = _changedTs = false;
                return;
            }

            if (_changedAuto) Run("int tcp set global autotuninglevel=" + _auto);
            if (_changedRss) Run("int tcp set global rss=" + _rss);
            if (_changedTs) Run("int tcp set global timestamps=" + _ts);
            _changedAuto = _changedRss = _changedTs = false;

            try { if (System.IO.File.Exists(MarkerPath)) System.IO.File.Delete(MarkerPath); } catch { }

            if (log != null)
                log("网络调优：已还原原设置（autotuning=" + _auto + "，rss=" + _rss + "，timestamps=" + _ts + "）");
        }

        // ---------------- 内部 ----------------

        private static bool Capture(Action<string> log)
        {
            if (_captured) return true;
            string raw;
            if (!ShowGlobal(out raw))
                return false;

            string auto = FindValue(raw, new[] { "自动调优", "autotun" });
            string rss = FindValue(raw, new[] { "接收方缩放", "receive scaling", "rss" });
            string ts = FindValue(raw, new[] { "时间戳", "timestamp" });

            // 三个值必须都认出来才敢改：以前某个值没解析到就退回默认值，
            // 还原时会照着"默认值"写回去，把用户原本的设置改错。
            if (auto == null || rss == null || ts == null)
            {
                if (log != null) log("网络调优：读不到完整的 TCP 全局参数，跳过本次调优（不改动系统设置）");
                return false;
            }

            _auto = NormalizeAuto(auto);
            _rss = NormalizeOnOff(rss);
            _ts = NormalizeOnOff(ts);

            _captured = true;
            if (log != null) log("网络调优：当前 autotuning=" + _auto + "，rss=" + _rss + "，timestamps=" + _ts);
            return true;
        }

        private static bool Run(string args)
        {
            string output;
            return Exec(args, out output) == 0;
        }

        private static bool ShowGlobal(out string output)
        {
            return Exec("int tcp show global", out output) == 0 && !string.IsNullOrEmpty(output);
        }

        private static int Exec(string args, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo("netsh", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                try { psi.StandardOutputEncoding = Encoding.GetEncoding(936); } catch { }
                try { psi.StandardErrorEncoding = Encoding.GetEncoding(936); } catch { }

                using (var p = Process.Start(psi))
                {
                    // 以前是先 ReadToEnd 再 WaitForExit：netsh 卡住时 ReadToEnd 永远不返回，超时形同虚设
                    var sb = new StringBuilder();
                    var gate = new object();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { }
                        output = "netsh 超时未返回";
                        return -1;
                    }
                    try { p.WaitForExit(); } catch { }   // 等异步读取把剩余输出冲干净
                    lock (gate) output = sb.ToString();
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }

        private static string FindValue(string text, string[] keys)
        {
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int idx = line.IndexOf(':');
                if (idx < 0) idx = line.IndexOf('：');
                if (idx < 0) continue;

                string key = line.Substring(0, idx).ToLowerInvariant();
                foreach (var k in keys)
                    if (key.Contains(k.ToLowerInvariant()))
                        return line.Substring(idx + 1).Trim();
            }
            return null;
        }

        private static string NormalizeAuto(string v)
        {
            string s = v.ToLowerInvariant();
            if (s.Contains("高度") || s.Contains("highly")) return "highlyrestricted";
            if (s.Contains("受限") || s.Contains("restrict")) return "restricted";
            if (s.Contains("实验") || s.Contains("experimental")) return "experimental";
            if (s.Contains("正常") || s.Contains("normal")) return "normal";
            if (s.Contains("禁用") || s.Contains("disabled")) return "disabled";
            return "normal";
        }

        private static string NormalizeOnOff(string v)
        {
            string s = v.ToLowerInvariant();
            if (s.Contains("启用") || s.Contains("enabled") || s.Contains("是")) return "enabled";
            return "disabled";
        }
    }
}
