using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace PclDlGui
{
    /// <summary>GitHub 发布页上的一个版本</summary>
    public class ReleaseInfo
    {
        public string Tag;
        public string Name;
        public string Body;
        public string HtmlUrl;
        public string ExeUrl;      // 该版本里最适合直接替换的单文件 exe
        public string ExeName;
        public long ExeSize;
        public bool IsNewer;
    }

    /// <summary>
    /// 在线更新：直接读 GitHub Releases（api.github.com，被墙时走 ghproxy 镜像）拿最新版本号与附件，
    /// 对比本地版本后由用户决定是否下载替换。
    /// </summary>
    public static class UpdateChecker
    {
        public const string CurrentVersion = "2.2";
        public const string Owner = "zhaozichengmc";
        public const string Repo = "multithread-launcher";
        public const string ReleasesPage = "https://github.com/" + Owner + "/" + Repo + "/releases";

        private static readonly string[] ApiTemplates =
        {
            "https://api.github.com/repos/{0}/{1}/releases/latest",
            "https://gh-proxy.com/https://api.github.com/repos/{0}/{1}/releases/latest",
            "https://ghproxy.net/https://api.github.com/repos/{0}/{1}/releases/latest"
        };

        public static string ApiUrl { get { return string.Format(ApiTemplates[0], Owner, Repo); } }

        /// <summary>联网取最新发布；全部地址都失败时返回 null（不抛异常）</summary>
        public static ReleaseInfo FetchLatest(int timeoutMs = 6000)
        {
            foreach (var tpl in ApiTemplates)
            {
                try
                {
                    var info = Fetch(string.Format(tpl, Owner, Repo), timeoutMs);
                    if (info != null) return info;
                }
                catch { }
            }
            return null;
        }

        private static ReleaseInfo Fetch(string url, int timeoutMs)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "LightspeedDownloader/" + CurrentVersion;
            req.Accept = "application/vnd.github+json";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var json = JObject.Parse(sr.ReadToEnd());
                var info = new ReleaseInfo
                {
                    Tag = Str(json["tag_name"]),
                    Name = Str(json["name"]),
                    Body = Str(json["body"]),
                    HtmlUrl = Str(json["html_url"]),
                };
                if (!string.IsNullOrEmpty(info.Tag))
                    info.IsNewer = IsNewer(info.Tag, CurrentVersion);

                var assets = json["assets"] as JArray;
                if (assets != null)
                {
                    // 优先单文件版（V2.0 起每版都挂一个 ...-V<版本>.exe）
                    foreach (var a in assets)
                    {
                        string n = Str(a["name"]);
                        if (n != null && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            info.ExeName = n;
                            info.ExeUrl = Str(a["browser_download_url"]);
                            info.ExeSize = a["size"] != null ? a["size"].Value<long>() : 0;
                            break;
                        }
                    }
                }
                return info;
            }
        }

        private static string Str(JToken t)
        {
            return t == null || t.Type == JTokenType.Null ? null : t.ToString();
        }

        /// <summary>比较 "V2.1" / "2.1.3" 与本地 "2.0"，远端更高返回 true</summary>
        public static bool IsNewer(string remote, string local)
        {
            var a = ParseVersion(remote);
            var b = ParseVersion(local);
            int len = Math.Max(a.Count, b.Count);
            for (int i = 0; i < len; i++)
            {
                int x = i < a.Count ? a[i] : 0;
                int y = i < b.Count ? b[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        public static List<int> ParseVersion(string s)
        {
            var list = new List<int>();
            if (string.IsNullOrEmpty(s)) return list;
            s = s.Trim().TrimStart('v', 'V');
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c >= '0' && c <= '9') sb.Append(c);
                else if (sb.Length > 0)
                {
                    int v; if (int.TryParse(sb.ToString(), out v)) list.Add(v);
                    sb.Length = 0;
                    if (c == '.') continue;
                    break;
                }
            }
            if (sb.Length > 0)
            {
                int v; if (int.TryParse(sb.ToString(), out v)) list.Add(v);
            }
            return list;
        }

        /// <summary>下载更新包到临时目录，返回本地文件路径（带超时，并校验真的是 exe）</summary>
        public static string DownloadExe(ReleaseInfo info, Action<int> progress)
        {
            string dir = Path.Combine(Path.GetTempPath(), "LightspeedUpdate");
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, info.ExeName ?? "LightspeedDownloader.exe");

            string[] tries = { info.ExeUrl, "https://gh-proxy.com/" + info.ExeUrl, "https://ghproxy.net/" + info.ExeUrl };
            Exception last = null;
            foreach (var url in tries)
            {
                if (string.IsNullOrEmpty(url)) continue;
                string tmp = dest + ".download";
                try
                {
                    DownloadTo(url, tmp, info.ExeSize, progress);
                    ValidateExe(tmp, info.ExeSize);
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(tmp, dest);
                    return dest;
                }
                catch (Exception e)
                {
                    last = e;
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }
            throw last ?? new Exception("没有可用的下载地址");
        }

        /// <summary>带超时的下载：代理挂起或返回错误页时不会永远卡在“正在下载”</summary>
        private static void DownloadTo(string url, string path, long expectSize, Action<int> progress)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "LightspeedDownloader/" + CurrentVersion;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 20000;
            req.AllowAutoRedirect = true;

            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                long total = resp.ContentLength;
                if (expectSize > 0) total = expectSize;
                using (var s = resp.GetResponseStream())
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buf = new byte[8192];
                    long got = 0;
                    int lastPct = -1;
                    while (true)
                    {
                        int n = s.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        fs.Write(buf, 0, n);
                        got += n;
                        if (progress != null && total > 0)
                        {
                            int pct = (int)Math.Min(99, got * 100 / total);
                            if (pct != lastPct) { lastPct = pct; progress(pct); }
                        }
                    }
                }
            }
        }

        /// <summary>防止把 429 的 JSON / 代理 HTML 错误页当成更新包覆盖掉自己</summary>
        private static void ValidateExe(string path, long expectSize)
        {
            var fi = new FileInfo(path);
            if (fi.Length < 100000)
                throw new Exception("下载到的不是程序文件（只有 " + fi.Length + " 字节，可能是代理返回的错误页）");
            if (expectSize > 0 && fi.Length != expectSize)
                throw new Exception("大小不符：期望 " + expectSize + "，实际 " + fi.Length);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] head = new byte[2];
                if (fs.Read(head, 0, 2) != 2 || head[0] != (byte)'M' || head[1] != (byte)'Z')
                    throw new Exception("下载到的不是有效的 exe（文件头不对）");
            }
        }

        /// <summary>
        /// 用下载好的 exe 替换自己并重启：写一个批处理，等本进程退出后覆盖、启动、自删。
        /// </summary>
        public static void ApplyAndRestart(string newExePath)
        {
            string self = Application.ExecutablePath;
            string bat = Path.Combine(Path.GetTempPath(), "LightspeedUpdate", "apply.bat");
            Directory.CreateDirectory(Path.GetDirectoryName(bat));

            string script =
                "@echo off\r\n" +
                "setlocal enabledelayedexpansion\r\n" +
                "chcp 65001 >nul\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                "set /a n=0\r\n" +
                ":retry\r\n" +
                "set /a n+=1\r\n" +
                "copy /y \"" + newExePath + "\" \"" + self + "\" >nul 2>nul\r\n" +
                "if errorlevel 1 (\r\n" +
                "  if !n! lss 60 ( timeout /t 1 /nobreak >nul & goto retry )\r\n" +
                ")\r\n" +
                "start \"\" \"" + self + "\"\r\n" +
                "timeout /t 1 /nobreak >nul\r\n" +
                "del /f /q \"" + bat + "\"\r\n";

            File.WriteAllText(bat, script, new UTF8Encoding(true));
            var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + bat + "\"\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }

        public static void OpenReleasesPage()
        {
            try { Process.Start(ReleasesPage); } catch { }
        }
    }

    /// <summary>更新提示窗口：版本号、更新说明、两个动作按钮</summary>
    public class UpdateForm : Form
    {
        private ReleaseInfo _info;
        private Label _labStatus;
        private ProgressBar _bar;
        private RoundButton _btnDownload, _btnPage, _btnLater;
        private string _downloaded;
        private bool _ready;

        /// <summary>窗口关了以后后台线程还会回调，直接 BeginInvoke 会抛异常把整个程序带走</summary>
        private void Ui(Action a)
        {
            if (a == null) return;
            try
            {
                if (IsDisposed || Disposing) return;
                if (InvokeRequired)
                {
                    if (IsHandleCreated) BeginInvoke(a);
                }
                else a();
            }
            catch { }
        }

        private void DoRestart()
        {
            try
            {
                UpdateChecker.ApplyAndRestart(_downloaded);
                Application.Exit();
            }
            catch (Exception ex) { MessageBox.Show("替换失败：" + ex.Message); }
        }

        public UpdateForm(ReleaseInfo info, bool hasUpdate)
        {
            _info = info;
            Text = hasUpdate ? "发现新版本 " + info.Tag : "已是最新版本";
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(560, 380);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;

            var head = new Label
            {
                Left = 18, Top = 14, Width = 520, Height = 24,
                Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
                Text = hasUpdate
                    ? "当前 " + UpdateChecker.CurrentVersion + "  →  最新 " + info.Tag
                    : "当前 " + UpdateChecker.CurrentVersion + "  已是最新版本"
            };
            Controls.Add(head);

            var body = new TextBox
            {
                Left = 18, Top = 48, Width = 524, Height = 232,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 250, 250),
                Text = (info.Name ?? info.Tag) + Environment.NewLine + Environment.NewLine +
                       (string.IsNullOrEmpty(info.Body) ? "（该版本没有写更新说明）" : info.Body.Replace("**", ""))
            };
            Controls.Add(body);

            _labStatus = new Label { Left = 18, Top = 290, Width = 524, Height = 20, ForeColor = Color.Gray, Text = hasUpdate ? "更新包：" + (info.ExeName ?? "未附带 exe") + (info.ExeSize > 0 ? "（" + (info.ExeSize / 1024) + " KB）" : "") : "" };
            Controls.Add(_labStatus);

            _bar = new ProgressBar { Left = 18, Top = 312, Width = 524, Height = 10, Visible = hasUpdate };
            Controls.Add(_bar);

            _btnDownload = new RoundButton { Text = "下载并更新", Left = 18, Top = 334, Width = 150, Height = 32, Visible = hasUpdate };
            _btnDownload.NormalBack = Color.FromArgb(0, 120, 212);
            _btnDownload.HoverBack = Color.FromArgb(16, 110, 190);
            _btnDownload.PressBack = Color.FromArgb(0, 90, 158);
            _btnDownload.DisabledBack = Color.FromArgb(199, 223, 245);
            _btnDownload.NormalBorder = Color.FromArgb(0, 120, 212);
            _btnDownload.HoverBorder = Color.FromArgb(0, 90, 158);
            _btnDownload.DisabledBorder = Color.FromArgb(199, 223, 245);
            _btnDownload.TextColor = Color.White;
            _btnDownload.DisabledTextColor = Color.FromArgb(108, 143, 175);
            _btnDownload.Click += (s, e) =>
            {
                if (_ready) { DoRestart(); return; }
                StartDownload();
            };
            Controls.Add(_btnDownload);

            _btnPage = new RoundButton { Text = "打开发布页", Left = 178, Top = 334, Width = 140, Height = 32 };
            _btnPage.Click += (s, e) => UpdateChecker.OpenReleasesPage();
            Controls.Add(_btnPage);

            _btnLater = new RoundButton { Text = hasUpdate ? "稍后再说" : "关闭", Left = 328, Top = 334, Width = 110, Height = 32 };
            _btnLater.Click += (s, e) => Close();
            Controls.Add(_btnLater);
        }

        private void StartDownload()
        {
            if (_info == null || string.IsNullOrEmpty(_info.ExeUrl))
            {
                UpdateChecker.OpenReleasesPage();
                _labStatus.Text = "该版本没有直接的 exe 附件，已帮你打开下载页";
                return;
            }
            _btnDownload.Enabled = false;
            _labStatus.Text = "正在下载更新包…";

            var th = new Thread(() =>
            {
                try
                {
                    _downloaded = UpdateChecker.DownloadExe(_info, p => Ui(() =>
                    {
                        if (_bar == null) return;
                        _bar.Value = Math.Min(100, Math.Max(0, p));
                        _labStatus.Text = "正在下载更新包… " + p + "%";
                    }));
                    Ui(() =>
                    {
                        _bar.Value = 100;
                        _labStatus.Text = "下载完成，点「立即重启」完成替换";
                        _btnDownload.Text = "立即重启";
                        _btnDownload.Enabled = true;
                        _ready = true;
                    });
                }
                catch (Exception ex)
                {
                    Ui(() =>
                    {
                        _btnDownload.Enabled = true;
                        _labStatus.Text = "下载失败：" + ex.Message + "（可打开发布页手动下载）";
                    });
                }
            });
            th.IsBackground = true;
            th.Start();
        }
    }
}
