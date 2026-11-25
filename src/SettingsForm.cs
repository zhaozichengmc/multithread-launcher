using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using PclDl;

namespace PclDlGui
{
    /// <summary>全局设置（代理、请求头、镜像、校验），存到 %APPDATA%\\PCLDownloader\\settings.tsv</summary>
    public class AppSettings
    {
        public bool ProxyEnabled;
        public string ProxyHost = "";
        public int ProxyPort = 1080;
        public bool Socks5;
        public string ProxyUser = "";
        public string ProxyPass = "";

        public string Referer = "";
        public string Cookie = "";
        public string UserAgent = "";

        public List<string> Mirrors = new List<string>();
        public bool AlwaysHash;
        public float Zoom = 0f;   // 0 = 跟随系统 DPI
                public static string LastWriteError = "";

        public static string Dir
        {
            get
            {
                string d = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCLDownloader");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        private static string FilePath { get { return System.IO.Path.Combine(Dir, "settings.tsv"); } }

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('\t');
                    if (i < 0) continue;
                    string k = line.Substring(0, i), v = line.Substring(i + 1);
                    switch (k)
                    {
                        case "proxy": s.ProxyEnabled = v == "1"; break;
                        case "proxyhost": s.ProxyHost = v; break;
                        case "proxyport":
                            int pp = 0;
                            if (int.TryParse(v, out pp) && pp > 0 && pp < 65536) s.ProxyPort = pp;
                            break;
                        case "socks5": s.Socks5 = v == "1"; break;
                        case "proxyuser": s.ProxyUser = v; break;
                        case "proxypass": s.ProxyPass = v; break;
                        case "referer": s.Referer = v; break;
                        case "cookie": s.Cookie = v; break;
                        case "ua": s.UserAgent = v; break;
                        case "hash": s.AlwaysHash = v == "1"; break;
                        case "mirror": if (v.Length > 0) s.Mirrors.Add(v); break;
                        case "zoom": float z = 1f; if (float.TryParse(v, out z) && z >= 0f && z <= 2f) s.Zoom = z; break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                var lines = new List<string>
                {
                    "proxy\t" + (ProxyEnabled ? "1" : "0"),
                    "proxyhost\t" + ProxyHost,
                    "proxyport\t" + ProxyPort,
                    "socks5\t" + (Socks5 ? "1" : "0"),
                    "proxyuser\t" + ProxyUser,
                    "proxypass\t" + ProxyPass,
                    "referer\t" + Referer,
                    "cookie\t" + Cookie,
                    "ua\t" + UserAgent,
                    "hash\t" + (AlwaysHash ? "1" : "0")
                };
                foreach (var m in Mirrors) lines.Add("mirror\t" + m);
                lines.Add("zoom\t" + Zoom.ToString("0.00"));
                LastWriteError = "";
                SaveLines(FilePath, lines);
            }
            catch (Exception ex) { LastWriteError = ex.Message; }
        }

        /// <summary>UTF-8 + 先写临时文件再原子替换，避免写一半断电/占用导致设置丢失</summary>
        public static void SaveLines(string path, List<string> lines)
        {
            string tmp = path + ".tmp";
            var enc = new System.Text.UTF8Encoding(false);
            File.WriteAllLines(tmp, lines.ToArray(), enc);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static List<string> ReadLines(string path)
        {
            var enc = System.Text.Encoding.UTF8;
            return new List<string>(File.ReadAllLines(path, enc));
        }

        public ProxyConfig BuildProxy()
        {
            if (!ProxyEnabled || string.IsNullOrEmpty(ProxyHost)) return null;
            return new ProxyConfig
            {
                Host = ProxyHost.Trim(),
                Port = ProxyPort,
                Socks5 = Socks5,
                User = string.IsNullOrEmpty(ProxyUser) ? null : ProxyUser,
                Pass = ProxyPass
            };
        }

        public Dictionary<string, string> BuildHeaders()
        {
            var h = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(Referer)) h["Referer"] = Referer;
            if (!string.IsNullOrEmpty(Cookie)) h["Cookie"] = Cookie;
            if (!string.IsNullOrEmpty(UserAgent)) h["User-Agent"] = UserAgent;
            return h;
        }
    }

    /// <summary>设置窗口</summary>
    public class SettingsForm : Form
    {
        private CheckBox chkProxy, chkSocks, chkHash;
        private TextBox tHost, tPort, tUser, tPass, tRef, tCookie, tUa, tMirror;
        private ComboBox cboZoom;

        public AppSettings Settings { get; private set; }

        public SettingsForm(AppSettings src)
        {
            Settings = src;
            Text = "设置";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(560, 462);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            int y = 14;
            chkProxy = new CheckBox { Text = "使用代理", Left = 16, Top = y, Width = 120, Checked = src.ProxyEnabled };
            Controls.Add(chkProxy);
            chkSocks = new CheckBox { Text = "SOCKS5（不勾选为 HTTP）", Left = 150, Top = y, Width = 220, Checked = src.Socks5 };
            Controls.Add(chkSocks);
            y += 28;

            Controls.Add(Label("地址", 16, y + 3)); tHost = Box(src.ProxyHost, 70, y, 220); Controls.Add(tHost);
            Controls.Add(Label("端口", 300, y + 3)); tPort = Box(src.ProxyPort.ToString(), 340, y, 80); Controls.Add(tPort);
            y += 30;
            Controls.Add(Label("用户名", 16, y + 3)); tUser = Box(src.ProxyUser, 70, y, 160); Controls.Add(tUser);
            Controls.Add(Label("密码", 246, y + 3)); tPass = Box(src.ProxyPass, 290, y, 160); Controls.Add(tPass);
            tPass.UseSystemPasswordChar = true;
            y += 36;

            Controls.Add(Label("Referer", 16, y + 3)); tRef = Box(src.Referer, 86, y, 450); Controls.Add(tRef);
            y += 28;
            Controls.Add(Label("Cookie", 16, y + 3)); tCookie = Box(src.Cookie, 86, y, 450); Controls.Add(tCookie);
            y += 28;
            Controls.Add(Label("User-Agent", 16, y + 3)); tUa = Box(src.UserAgent, 110, y, 426); Controls.Add(tUa);
            y += 34;

            Controls.Add(Label("备用镜像（每行一个 URL，主源失败自动换）", 16, y));
            y += 20;
            tMirror = new TextBox
            {
                Left = 16, Top = y, Width = 520, Height = 110,
                Multiline = true, ScrollBars = ScrollBars.Vertical,
                Text = string.Join("\r\n", src.Mirrors.ToArray())
            };
            Controls.Add(tMirror);
            y += 118;

            Controls.Add(Label("界面缩放", 16, y + 4));
            cboZoom = new ComboBox { Left = 86, Top = y, Width = 92, DropDownStyle = ComboBoxStyle.DropDownList };
            cboZoom.Items.AddRange(new object[] { "自动（跟随系统 DPI）", "100%", "110%", "125%", "150%" });
            cboZoom.SelectedIndex = src.Zoom <= 0.01f ? 0
                : (src.Zoom >= 1.45f ? 4 : (src.Zoom >= 1.2f ? 3 : (src.Zoom >= 1.05f ? 2 : 1)));
            Controls.Add(cboZoom);
            Controls.Add(new Label { Text = "（立即生效，重启后保留）", Left = 186, Top = y + 4, Width = 220, Height = 20, ForeColor = Color.Gray });
            y += 32;

            chkHash = new CheckBox { Text = "下载完成后总是计算 SHA-256（大文件会多花几秒）", Left = 16, Top = y, Width = 420, Checked = src.AlwaysHash };
            Controls.Add(chkHash);
            y += 30;

            var ok = new Button { Text = "确定", Left = 330, Top = y, Width = 96, Height = 30, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Left = 440, Top = y, Width = 96, Height = 30, DialogResult = DialogResult.Cancel };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;

            ok.Click += (s, e) => Collect();
        }

        private static Label Label(string t, int x, int y)
        {
            return new Label { Text = t, Left = x, Top = y, Width = 100, Height = 20, AutoSize = false };
        }

        private static TextBox Box(string v, int x, int y, int w)
        {
            return new TextBox { Text = v ?? "", Left = x, Top = y, Width = w };
        }

        private void Collect()
        {
            Settings.ProxyEnabled = chkProxy.Checked;
            Settings.Socks5 = chkSocks.Checked;
            Settings.ProxyHost = tHost.Text.Trim();
            int p = 1080;
            try { p = int.Parse(tPort.Text.Trim()); } catch { }
            Settings.ProxyPort = p;
            Settings.ProxyUser = tUser.Text.Trim();
            Settings.ProxyPass = tPass.Text;
            Settings.Referer = tRef.Text.Trim();
            Settings.Cookie = tCookie.Text.Trim();
            Settings.UserAgent = tUa.Text.Trim();
            Settings.AlwaysHash = chkHash.Checked;
            Settings.Zoom = new[] { 0f, 1f, 1.1f, 1.25f, 1.5f }[Math.Max(0, cboZoom.SelectedIndex)];
            Settings.Mirrors.Clear();
            foreach (var line in tMirror.Text.Split('\n'))
            {
                string u = line.Trim();
                if (u.StartsWith("http")) Settings.Mirrors.Add(u);
            }
            Settings.Save();
        }
    }

    /// <summary>简单输入框</summary>
    public static class InputDialog
    {
        public static string Ask(IWin32Window owner, string title, string label, string value)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.Font = new Font("Microsoft YaHei UI", 9F);
                f.AutoScaleMode = AutoScaleMode.Dpi;
                f.AutoScaleDimensions = new SizeF(96F, 96F);
                f.ClientSize = new Size(440, 130);
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false;
                f.StartPosition = FormStartPosition.CenterParent;

                f.Controls.Add(new Label { Text = label, Left = 14, Top = 14, Width = 410, Height = 34 });
                var box = new TextBox { Text = value ?? "", Left = 14, Top = 52, Width = 410 };
                f.Controls.Add(box);

                var ok = new Button { Text = "确定", Left = 230, Top = 88, Width = 90, Height = 28, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "取消", Left = 334, Top = 88, Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
                f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
            }
        }
    }
}
