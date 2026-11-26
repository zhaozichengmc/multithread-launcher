using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using PclDl;

namespace PclDlGui
{
    public class McVersionInfo
    {
        public string Id;
        public string Type;
        public string Url;
        public string Time;
        public string TypeText
        {
            get
            {
                if (Type == "release") return "正式版";
                if (Type == "snapshot") return "快照";
                if (Type == "old_beta") return "Beta 远古";
                if (Type == "old_alpha") return "Alpha 远古";
                return Type;
            }
        }
    }

    /// <summary>
    /// MC 原版下载窗口：版本清单 → 客户端 jar / 支持库 / 资源文件 / 日志配置，
    /// 全部按 PCL2 的 BMCLAPI 镜像规则走，下完直接是标准 .minecraft 目录，PCL2/HMCL 能认。
    /// </summary>
    public class McForm : Form
    {
        private const string Bmcl = "https://bmclapi2.bangbang93.com";
        private const string ManifestUrl = Bmcl + "/mc/game/version_manifest.json";
        private const string ManifestUrlOfficial = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

        private ComboBox cboType, cboSort;
        private ListView lv;
        private TextBox txtDir, txtLog;
        private ProgressBar bar;
        private Label labStatus;
        private RoundButton btnStart, btnCancel, btnBrowse, btnOpen, btnRefresh;
        private RoundButton btnLogin, btnJava, btnLaunch;
        private Label labAccount;
        private TextBox txtJava;
        private ComboBox cboMem;
        private Account _account = new Account();
        private List<McVersionInfo> _versions = new List<McVersionInfo>();
        private McInstaller _installer;
        private Thread _worker;

        public McForm()
        {
            Text = "下载 Minecraft 原版";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(840, 770);
            MinimumSize = new Size(760, 700);
            StartPosition = FormStartPosition.CenterParent;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Controls.Add(new Label { Text = "版本类型", Left = 16, Top = 14, Width = 60, Height = 20 });
            cboType = new ComboBox { Left = 80, Top = 11, Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
            cboType.Items.AddRange(new object[] { "全部", "正式版", "快照", "远古版本（Alpha/Beta）" });
            cboType.SelectedIndex = 0;
            cboType.SelectedIndexChanged += (s, e) => FillList();
            Controls.Add(cboType);

            cboSort = new ComboBox { Left = 316, Top = 11, Width = 118, DropDownStyle = ComboBoxStyle.DropDownList };
            cboSort.Items.AddRange(new object[] { "最新在前", "最早在前" });
            cboSort.SelectedIndex = 0;
            cboSort.SelectedIndexChanged += (s, e) => FillList();
            Controls.Add(cboSort);

            btnRefresh = new RoundButton { Text = "刷新清单", Left = 200, Top = 10, Width = 96, Height = 26 };
            btnRefresh.Click += (s, e) => LoadManifest();
            Controls.Add(btnRefresh);

            lv = new ListView
            {
                Left = 16, Top = 46, Width = 808, Height = 240,
                View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            lv.Columns.Add("版本", 320);
            lv.Columns.Add("类型", 120);
            lv.Columns.Add("发布时间", 200);
            lv.DoubleClick += (s, e) => StartInstall();
            Controls.Add(lv);

            Controls.Add(new Label { Text = "游戏目录", Left = 16, Top = 300, Width = 60, Height = 20 });
            txtDir = new TextBox
            {
                Left = 80, Top = 297, Width = 620, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft")
            };
            Controls.Add(txtDir);
            btnBrowse = new RoundButton { Text = "浏览…", Left = 708, Top = 295, Width = 56, Height = 26, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnBrowse.Click += (s, e) => BrowseDir();
            btnOpen = new RoundButton { Text = "打开", Left = 768, Top = 295, Width = 56, Height = 26, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnOpen.Click += (s, e) => OpenDir();
            Controls.Add(btnBrowse); Controls.Add(btnOpen);

            bar = new ProgressBar { Left = 16, Top = 334, Width = 620, Height = 18, Maximum = 1000, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(bar);
            labStatus = new Label { Left = 16, Top = 358, Width = 808, Height = 20, Text = "正在读取版本清单…", ForeColor = Color.DimGray, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(labStatus);

            btnStart = new RoundButton { Text = "下载此版本", Left = 16, Top = 386, Width = 132, Height = 32, Enabled = false };
            StylePrimary(btnStart);
            btnStart.Click += (s, e) => StartInstall();
            btnCancel = new RoundButton { Text = "取消", Left = 156, Top = 386, Width = 88, Height = 32, Enabled = false };
            btnCancel.Click += (s, e) => CancelInstall();
            Controls.Add(btnStart); Controls.Add(btnCancel);

            txtLog = new TextBox
            {
                Left = 16, Top = 532, Width = 808, Height = 200,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 250, 250),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(txtLog);

            labAccount = new Label { Left = 16, Top = 426, Width = 590, Height = 22, Text = "账号：未登录", Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(labAccount);
            btnLogin = new RoundButton { Text = "登录…", Left = 620, Top = 423, Width = 84, Height = 28, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnLogin.Click += (s, e) => OpenAccount();
            Controls.Add(btnLogin);

            Controls.Add(new Label { Text = "Java", Left = 16, Top = 458, Width = 50, Height = 22 });
            txtJava = new TextBox { Left = 70, Top = 455, Width = 556, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(txtJava);
            btnJava = new RoundButton { Text = "浏览…", Left = 632, Top = 453, Width = 56, Height = 26, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnJava.Click += (s, e) => PickJava();
            Controls.Add(btnJava);
            Controls.Add(new Label { Text = "内存", Left = 698, Top = 458, Width = 40, Height = 22, Anchor = AnchorStyles.Top | AnchorStyles.Right });
            cboMem = new ComboBox { Left = 738, Top = 455, Width = 86, DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            cboMem.Items.AddRange(new object[] { "1024 MB", "2048 MB", "3072 MB", "4096 MB", "6144 MB", "8192 MB" });
            cboMem.SelectedIndex = 1;
            Controls.Add(cboMem);

            btnLaunch = new RoundButton { Text = "启动游戏", Left = 16, Top = 490, Width = 132, Height = 32 };
            StylePrimary(btnLaunch);
            btnLaunch.Click += (s, e) => StartLaunch();
            Controls.Add(btnLaunch);
            Controls.Add(new Label { Left = 160, Top = 498, Width = 660, Height = 20, ForeColor = Color.DimGray, Text = "启动前会自动检测 Java；正版账号首次登录后无需重复登录", Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right });

            Load += (s, e) => { LoadManifest(); LoadPrefs(); };
            lv.SelectedIndexChanged += (s, e) => UpdateLaunchButton();
            FormClosing += (s, e) =>
            {
                if (_busy && _installer != null && !_installer.Cancel)
                {
                    e.Cancel = true;
                    _closeAfter = true;
                    _installer.Cancel = true;
                    labStatus.Text = "正在取消下载，完成后自动关闭…";
                }
            };
        }

        private static void StylePrimary(RoundButton b)
        {
            b.NormalBack = Color.FromArgb(0, 120, 212);
            b.HoverBack = Color.FromArgb(0, 132, 226);
            b.PressBack = Color.FromArgb(0, 104, 190);
            b.NormalBorder = Color.FromArgb(0, 104, 190);
            b.HoverBorder = Color.FromArgb(0, 120, 212);
            b.TextColor = Color.White;
        }

        /// <summary>关窗后后台线程还会回调，直接 BeginInvoke 会抛异常把整个程序带走</summary>
        private void Ui(Action a)
        {
            if (a == null || IsDisposed || Disposing) return;
            try
            {
                if (InvokeRequired) { if (IsHandleCreated) BeginInvoke(a); }
                else a();
            }
            catch { }
        }

        private void Log(string s)
        {
            if (IsDisposed || Disposing) return;
            Ui(() =>
            {
                try
                {
                    txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine);
                    txtLog.SelectionStart = txtLog.TextLength;
                    txtLog.ScrollToCaret();
                }
                catch { }
            });
        }

        private void SetStatus(string s, int perMille = -1)
        {
            if (IsDisposed || Disposing) return;
            Ui(() =>
            {
                try
                {
                    labStatus.Text = s;
                    if (perMille >= 0) bar.Value = Math.Max(0, Math.Min(1000, perMille));
                }
                catch { }
            });
        }

        private void BrowseDir()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择 .minecraft 目录";
                if (Directory.Exists(txtDir.Text)) dlg.SelectedPath = txtDir.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) txtDir.Text = dlg.SelectedPath;
            }
        }

        private void OpenDir()
        {
            try
            {
                string d = txtDir.Text.Trim();
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                System.Diagnostics.Process.Start(d);
            }
            catch (Exception ex) { Log("打开目录失败：" + ex.Message); }
        }

        private void LoadManifest()
        {
            if (_worker != null && _worker.IsAlive) return;
            btnStart.Enabled = false;
            SetStatus("正在读取版本清单…", 0);
            _worker = new Thread(() =>
            {
                try
                {
                    string json = null;
                    try { json = McNet.Get(ManifestUrl, 2); } catch { }
                    if (string.IsNullOrEmpty(json)) json = McNet.Get(ManifestUrlOfficial, 3);
                    var arr = (JArray)JObject.Parse(json)["versions"];
                    var list = new List<McVersionInfo>();
                    foreach (var t in arr)
                        list.Add(new McVersionInfo
                        {
                            Id = (string)t["id"],
                            Type = (string)t["type"],
                            Url = (string)t["url"],
                            Time = ((string)t["releaseTime"] ?? "").Replace('T', ' ').Replace("+00:00", "")
                        });
                    Ui(() =>
                    {
                        if (IsDisposed || Disposing) return;
                        _versions = list;
                        FillList();
                        SetStatus("清单已加载：" + list.Count + " 个版本", 0);
                        btnStart.Enabled = lv.SelectedItems.Count > 0;
                    });
                }
                catch (Exception ex)
                {
                    Ui(() =>
                    {
                        SetStatus("读取清单失败：" + ex.Message, 0);
                        Log("读取清单失败：" + ex.Message + "（可点“刷新清单”重试）");
                    });
                }
            });
            _worker.IsBackground = true;
            _worker.Start();
        }

        private void FillList()
        {
            lv.Items.Clear();
            int mode = cboType.SelectedIndex;
            int n = 0;
            var src = new List<McVersionInfo>(_versions);
            if (cboSort != null && cboSort.SelectedIndex == 1) src.Reverse();
            foreach (var v in src)
            {
                bool ok;
                if (mode == 0) ok = true;
                else if (mode == 1) ok = v.Type == "release";
                else if (mode == 2) ok = v.Type == "snapshot";
                else ok = v.Type == "old_alpha" || v.Type == "old_beta";
                if (!ok) continue;
                var item = new ListViewItem(new[] { v.Id, v.TypeText, v.Time });
                item.Tag = v;
                lv.Items.Add(item);
                n++;
                if (n >= 2000) break;
            }
            lv.SelectedItems.Clear();
            if (lv.Items.Count > 0) lv.Items[0].Selected = true;
            btnStart.Enabled = !_busy && lv.SelectedItems.Count > 0;
        }

        private volatile bool _busy;
        private bool _closeAfter;
        private Thread _instThread;

        private void StartInstall()
        {
            if (_busy) return;
            if (lv.SelectedItems.Count == 0) { Log("先选一个版本"); return; }
            var v = lv.SelectedItems[0].Tag as McVersionInfo;
            string dir = txtDir.Text.Trim();
            if (string.IsNullOrEmpty(dir)) { Log("先填游戏目录"); return; }

            _busy = true;
            btnStart.Enabled = false;
            btnCancel.Enabled = true;
            btnRefresh.Enabled = false;

            _installer = new McInstaller(v.Id, dir, v.Url, ManifestUrl);
            _installer.OnLog = (s) => Log(s);
            _installer.OnProgress = (p, s) => SetStatus(s, p);

            _instThread = new Thread(() =>
            {
                try
                {
                    _installer.Run();
                    Ui(() =>
                    {
                        if (IsDisposed || Disposing) return;
                        SetStatus("完成：" + v.Id + " 已下载到 " + dir, 1000);
                        Log("完成。用 PCL2 / HMCL 选择该目录即可启动。");
                    });
                }
                catch (Exception ex)
                {
                    Ui(() =>
                    {
                        if (IsDisposed || Disposing) return;
                        SetStatus("失败：" + ex.Message, 0);
                        Log("失败：" + ex.Message);
                    });
                }
                finally
                {
                    // 关窗后 IsHandleCreated 为 false，这里一定不能再硬 BeginInvoke（以前就是闪退点）
                    Ui(() =>
                    {
                        if (IsDisposed || Disposing) return;
                        _busy = false;
                        if (btnStart != null) btnStart.Enabled = lv.SelectedItems.Count > 0;
                        if (btnCancel != null) btnCancel.Enabled = false;
                        if (btnRefresh != null) btnRefresh.Enabled = true;
                        if (btnStart != null) btnStart.Text = "下载此版本";
                        if (_closeAfter) Close();
                    });
                }
            });
            _instThread.IsBackground = true;
            _instThread.Start();
        }

        private void CancelInstall()
        {
            if (_installer != null) _installer.Cancel = true;
            Log("已请求取消，正在结束当前下载…");
        }
        private void LoadPrefs()
        {
            try
            {
                _account = AccountStore.Load();
                UpdateAccountLabel();
                string f = PrefFile();
                if (File.Exists(f))
                {
                    foreach (var line in File.ReadAllLines(f))
                    {
                        int p = line.IndexOf('=');
                        if (p < 0) continue;
                        string k = line.Substring(0, p), v = line.Substring(p + 1);
                        if (k == "java") txtJava.Text = v;
                        else if (k == "mem") cboMem.Text = v;
                    }
                }
                if (string.IsNullOrEmpty(txtJava.Text)) txtJava.Text = JavaFinder.Find(8) ?? "";
            }
            catch { }
        }

        private static string PrefFile()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCLDownloader");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "launch.tsv");
        }

        private void SavePrefs()
        {
            try { File.WriteAllText(PrefFile(), "java=" + txtJava.Text + "\r\nmem=" + cboMem.Text, new UTF8Encoding(false)); } catch { }
        }

        private void UpdateAccountLabel()
        {
            if (_account == null || string.IsNullOrEmpty(_account.Name))
                labAccount.Text = "账号：未登录（离线也需要填昵称）";
            else if (_account.Type == "ms") labAccount.Text = "账号：微软正版 · " + _account.Name;
            else if (_account.Type == "third") labAccount.Text = "账号：第三方 · " + _account.Name + " · " + _account.Server;
            else labAccount.Text = "账号：离线 · " + _account.Name;
        }

        private void OpenAccount()
        {
            using (var f = new AccountForm(AccountStore.Load()))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    _account = AccountStore.Load();
                    UpdateAccountLabel();
                    Log("账号已切换：" + _account.Name);
                }
            }
        }

        private void PickJava()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "java.exe|java.exe|所有文件|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    txtJava.Text = dlg.FileName;
                    Log("Java：" + dlg.FileName + "（Java " + JavaFinder.Version(dlg.FileName) + "）");
                    SavePrefs();
                }
            }
        }

        private McVersionInfo SelectedVersion()
        {
            return lv.SelectedItems.Count == 0 ? null : (lv.SelectedItems[0].Tag as McVersionInfo);
        }

        private void UpdateLaunchButton()
        {
            var v = SelectedVersion();
            bool installed = false;
            if (v != null)
            {
                string jar = Path.Combine(txtDir.Text.Trim(), "versions", v.Id, v.Id + ".jar");
                installed = File.Exists(jar);
            }
            btnLaunch.Text = installed ? "启动游戏" : "启动（未安装）";
            btnLaunch.Enabled = !_busy;
        }

        private void StartLaunch()
        {
            var v = SelectedVersion();
            if (v == null) { Log("先选一个版本"); return; }
            string dir = txtDir.Text.Trim();
            string jar = Path.Combine(dir, "versions", v.Id, v.Id + ".jar");
            if (!File.Exists(jar)) { Log("该版本还没下载完，先点“下载此版本”"); return; }

            _account = AccountStore.Load();
            if (string.IsNullOrEmpty(_account.Name))
            {
                OpenAccount();
                _account = AccountStore.Load();
                if (string.IsNullOrEmpty(_account.Name)) return;
            }

            int needJava = 8;
            try
            {
                string vj = Path.Combine(dir, "versions", v.Id, v.Id + ".json");
                if (File.Exists(vj))
                {
                    var jo = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(vj));
                    needJava = (int?)(jo["javaVersion"]?["majorVersion"]) ?? 8;
                }
            }
            catch { }

            string java = txtJava.Text.Trim();
            int have = (!string.IsNullOrEmpty(java) && File.Exists(java)) ? JavaFinder.Version(java) : 0;
            if (have < needJava)
            {
                string better = JavaFinder.Find(needJava);
                if (!string.IsNullOrEmpty(better)) { java = better; txtJava.Text = better; have = JavaFinder.Version(better); Log("自动选用 Java " + have + "：" + better); }
            }
            if (string.IsNullOrEmpty(java) || !File.Exists(java)) java = JavaFinder.Find(8) ?? "";
            if (string.IsNullOrEmpty(java) || !File.Exists(java)) { Log("没找到 Java。请安装 Java 17/21，或用“浏览”指定 java.exe"); return; }

            int mem = 2048;
            try { mem = int.Parse(cboMem.Text.Replace("MB", "").Trim()); } catch { }
            SavePrefs();

            btnLaunch.Enabled = false;
            var acc = _account;
            var ver = v;
            var th = new Thread(() =>
            {
                try
                {
                    if (acc.Type == "ms")
                    {
                        try { acc = Auth.MsRefresh(acc, s => Log(s)); AccountStore.Save(acc); _account = acc; }
                        catch (Exception ex) { Log("正版令牌刷新失败：" + ex.Message + "，请重新登录"); Ui(() => OpenAccount()); btnLaunch.Enabled = true; return; }
                    }
                    else if (acc.Type == "third")
                    {
                        try { if (!Auth.ThirdValidate(acc)) { acc = Auth.ThirdRefresh(acc); AccountStore.Save(acc); _account = acc; } }
                        catch (Exception ex) { Log("第三方令牌失效：" + ex.Message + "，请重新登录"); Ui(() => OpenAccount()); btnLaunch.Enabled = true; return; }
                    }
                    if (needJava <= 8 && have >= 9)
                Log("提示：该老版本建议用 Java 8，当前 Java " + have + " 可能启动失败");
            var p = Launcher.Launch(ver.Id, dir, acc, java, mem, s => Log(s));
                    Ui(() => { Log("游戏进程已启动（PID " + p.Id + "）"); btnLaunch.Enabled = true; });
                }
                catch (Exception ex)
                {
                    Ui(() => { Log("启动失败：" + ex.Message); btnLaunch.Enabled = true; });
                }
            });
            th.IsBackground = true;
            th.Start();
        }

    }

    /// <summary>下载执行器：客户端 jar / 支持库 / 资源 / 日志配置，带镜像改写与断点跳过</summary>
    public class McInstaller
    {
        private const string Bmcl = "https://bmclapi2.bangbang93.com";

        public string Id, Dir, VersionUrl, ManifestUrl;
        public volatile bool Cancel;
        public Action<string> OnLog;
        public Action<int, string> OnProgress;

        private static DateTime _mirrorDownUntil = DateTime.MinValue;
        private static void MirrorFailed() { _mirrorDownUntil = DateTime.Now.AddMinutes(10); }
        private static bool MirrorDown { get { return DateTime.Now < _mirrorDownUntil; } }
        private static string Pick(string url, string fallback, int attempt)
        {
            if (string.IsNullOrEmpty(fallback)) return url;
            if (MirrorDown) return fallback;
            return (attempt % 2 == 1) ? fallback : url;
        }

        public McInstaller(string id, string dir, string versionUrl, string manifestUrl)
        {
            Id = id; Dir = dir; VersionUrl = versionUrl; ManifestUrl = manifestUrl;
        }

        private void Log(string s) { var h = OnLog; if (h != null) h(s); }
        private void Prog(int perMille, string s) { var h = OnProgress; if (h != null) h(perMille, s); }
        private void CheckCancel() { if (Cancel) throw new OperationCanceledException("已取消"); }

        public void Run()
        {
            Directory.CreateDirectory(Dir);
            string versionsDir = Path.Combine(Dir, "versions", Id);
            string assetsDir = Path.Combine(Dir, "assets");
            string libDir = Path.Combine(Dir, "libraries");
            Directory.CreateDirectory(versionsDir);
            Directory.CreateDirectory(Path.Combine(versionsDir, "natives"));
            Directory.CreateDirectory(assetsDir);
            Directory.CreateDirectory(libDir);

            Prog(20, "读取版本信息…");
            string json = null;
            if (!MirrorDown) { try { json = McNet.Get(Bmcl + "/version/" + Id + "/json", 2); } catch { MirrorFailed(); } }
            if (string.IsNullOrEmpty(json)) json = McNet.Get(VersionUrl, 3);

            string verJsonPath = Path.Combine(versionsDir, Id + ".json");
            File.WriteAllText(verJsonPath, json, new UTF8Encoding(false));
            JObject root = JObject.Parse(json);

            // ---------- 客户端 jar ----------
            Prog(60, "下载客户端 core jar…");
            string jarPath = Path.Combine(versionsDir, Id + ".jar");
            long jarSize = L(root["downloads"]?["client"]?["size"]);
            string jarOfficial = (string)(root["downloads"]?["client"]?["url"] ?? "");
            DownloadWithEngine(Bmcl + "/version/" + Id + "/client", jarOfficial, jarPath, jarSize,
                (string)root["downloads"]?["client"]?["sha1"]);
            Log("客户端 jar 完成：" + new FileInfo(jarPath).Length / 1048576 + " MB");

            // ---------- 支持库 ----------
            var libs = root["libraries"] as JArray;
            var libFiles = new List<LibFile>();
            var nativeFiles = new List<LibFile>();
            if (libs != null)
            {
                foreach (var lib in libs)
                {
                    if (!Allowed(lib)) continue;
                    var art = lib["downloads"]?["artifact"];
                    if (art != null)
                    {
                        string ap = (string)art["path"] ?? "";
                        bool isNative = ap.IndexOf("natives-windows", StringComparison.OrdinalIgnoreCase) >= 0;
                        libFiles.Add(new LibFile
                        {
                            Url = MirrorLib((string)art["url"]),
                            Fallback = (string)art["url"],
                            Path = Path.Combine(libDir, ap.Replace('/', Path.DirectorySeparatorChar)),
                            Size = L(art["size"]),
                            IsNative = isNative
                        });
                    }

                    string nativesKey = null;
                    var natives = lib["natives"];
                    if (natives != null)
                    {
                        if (natives["windows"] != null && ((string)natives["windows"]).Contains("${arch}"))
                            nativesKey = ((string)natives["windows"]).Replace("${arch}", "64");
                        else if (natives["windows"] != null) nativesKey = (string)natives["windows"];
                    }
                    if (nativesKey != null)
                    {
                        var cls = lib["downloads"]?["classifiers"]?[nativesKey];
                        if (cls != null)
                            nativeFiles.Add(new LibFile
                            {
                                Url = MirrorLib((string)cls["url"]),
                                Fallback = (string)cls["url"],
                                Path = Path.Combine(versionsDir, "natives", Id + "." + Path.GetFileName((string)cls["path"] ?? "native.jar")),
                                Size = L(cls["size"]),
                                IsNative = true
                            });
                    }
                }
            }

            Prog(100, "下载支持库（" + libFiles.Count + " 个）…");
            for (int i = 0; i < libFiles.Count; i++)
            {
                CheckCancel();
                var f = libFiles[i];
                DownloadWithEngine(f.Url, f.Fallback, f.Path, f.Size);
                if (f.IsNative)
                {
                    try { ExtractNatives(f.Path, Path.Combine(versionsDir, "natives")); }
                    catch (Exception ex) { Log("解压 natives 失败：" + ex.Message); }
                }
                Prog(100 + (int)(i * 200.0 / Math.Max(1, libFiles.Count)), "支持库 " + (i + 1) + "/" + libFiles.Count);
            }
            Log("支持库完成：" + libFiles.Count + " 个");

            // natives 解压
            string nativesDir = Path.Combine(versionsDir, "natives");
            Directory.CreateDirectory(nativesDir);
            foreach (var f in nativeFiles)
            {
                CheckCancel();
                DownloadWithEngine(f.Url, f.Fallback, f.Path, f.Size);
                try { ExtractNatives(f.Path, nativesDir); } catch (Exception ex) { Log("解压 natives 失败：" + ex.Message); }
            }

            // ---------- 资源索引 ----------
            var assetIndex = root["assetIndex"];
            var assetObjects = new List<AssetFile>();
            if (assetIndex != null)
            {
                string idxId = (string)assetIndex["id"] ?? Id;
                string idxOfficial = (string)assetIndex["url"];
                string idxUrl = MirrorAsset(idxOfficial);
                string idxPath = Path.Combine(assetsDir, "indexes", idxId + ".json");
                Prog(320, "下载资源索引…");
                DownloadSmall(idxUrl, idxOfficial, idxPath, L(assetIndex["size"]));

                JObject idx = JObject.Parse(File.ReadAllText(idxPath));
                string legacyTarget = null;
                bool mapToRes = idx["map_to_resources"] != null && idx["map_to_resources"].Type == JTokenType.Boolean && (bool)idx["map_to_resources"];
                bool virtualMode = idx["virtual"] != null && idx["virtual"].Type == JTokenType.Boolean && (bool)idx["virtual"];
                if (mapToRes) legacyTarget = Path.Combine(Dir, "resources");
                else if (virtualMode) legacyTarget = Path.Combine(assetsDir, "virtual", idxId);
                if (legacyTarget != null) Directory.CreateDirectory(legacyTarget);
                var objects = idx["objects"] as JObject;
                if (objects != null)
                {
                    foreach (var kv in objects)
                    {
                        var o = kv.Value;
                        string hash = (string)o["hash"];
                        long size = L(o["size"]);
                        if (string.IsNullOrEmpty(hash)) continue;
                        assetObjects.Add(new AssetFile
                        {
                            Url = Bmcl + "/assets/" + hash.Substring(0, 2) + "/" + hash,
                            Fallback = "https://resources.download.minecraft.net/" + hash.Substring(0, 2) + "/" + hash,
                            Path = Path.Combine(assetsDir, "objects", hash.Substring(0, 2), hash),
                            Size = size,
                            Sha1 = hash,
                            Name = kv.Key,
                            LegacyDir = legacyTarget
                        });
                    }
                }
                Log("资源清单：" + assetObjects.Count + " 个文件（缺失的才下）"
                    + (legacyTarget != null ? "，老版本资源映射到 " + Path.GetFileName(legacyTarget) : ""));
            }

            // ---------- 资源文件（8 并发） ----------
            int doneAssets = 0, failed = 0;
            int totalAssets = assetObjects.Count;
            using (var sem = new SemaphoreSlim(8))
            {
                var tasks = new List<Task>();
                foreach (var a in assetObjects)
                {
                    CheckCancel();
                    sem.Wait();
                    var file = a;
                    tasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            if (!Cancel)
                            {
                                DownloadSmall(file.Url, file.Fallback, file.Path, file.Size, true, file.Sha1);
                                if (!string.IsNullOrEmpty(file.LegacyDir))
                                {
                                    string dst = Path.Combine(file.LegacyDir, file.Name.Replace('/', Path.DirectorySeparatorChar));
                                    if (!File.Exists(dst) || new FileInfo(dst).Length != file.Size || Sha1Of(dst) != file.Sha1)
                                    {
                                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                                        File.Copy(file.Path, dst, true);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (!Cancel) { Interlocked.Increment(ref failed); Log("资源失败 " + Path.GetFileName(file.Path) + "：" + ex.Message); }
                        }
                        finally
                        {
                            int n = Interlocked.Increment(ref doneAssets);
                            sem.Release();
                            if (n % 20 == 0 || n == totalAssets)
                                Prog(320 + (int)(n * 620.0 / Math.Max(1, totalAssets)), "资源文件 " + n + "/" + totalAssets + "（失败 " + failed + "）");
                        }
                    }));
                }
                Task.WaitAll(tasks.ToArray());
            }
            Log("资源文件完成：" + doneAssets + "/" + totalAssets + "，失败 " + failed);
            if (failed > 0)
                throw new Exception("有 " + failed + " 个资源文件没下成功，游戏会缺材质/音效。再点一次“下载此版本”会自动补下（已下好的不重下）");

            // ---------- 日志配置 ----------
            try
            {
                var logCfg = root["logging"]?["client"]?["file"];
                if (logCfg != null)
                {
                    string lid = (string)logCfg["id"] ?? (Id + "-log4j.xml");
                    string lofficial = (string)logCfg["url"];
                    string lpath = Path.Combine(assetsDir, "log_configs", lid);
                    DownloadSmall(MirrorAsset(lofficial), lofficial, lpath, -1);
                    Log("日志配置：" + lid);
                }
            }
            catch (Exception ex) { Log("日志配置跳过：" + ex.Message); }

            CheckCancel();
            Prog(1000, "全部完成");
            Log("完成：versions\\" + Id + "，jar + 库 + 资源均已就绪");
        }

        private static long L(JToken t) { return t == null ? -1 : (long)t; }

        private class LibFile { public string Url, Fallback, Path; public long Size; public bool IsNative; }
        private class AssetFile { public string Url, Fallback, Path, Name, LegacyDir, Sha1; public long Size; }

        private static bool Allowed(JToken lib)
        {
            var rules = lib["rules"] as JArray;
            if (rules == null) return true;
            bool allow = false;
            foreach (var r in rules)
            {
                bool match = true;
                var os = r["os"];
                if (os != null)
                {
                    string name = (string)os["name"];
                    if (!string.IsNullOrEmpty(name) && name != "windows") match = false;
                    string arch = (string)os["arch"];
                    if (!string.IsNullOrEmpty(arch))
                    {
                        // 目标是 Win64：x86 专属库跳过，arm64 跳过
                        if (arch == "x86" || arch == "arm64") match = false;
                    }
                }
                if (match) allow = (string)r["action"] == "allow";
            }
            return allow;
        }

        private static string MirrorAsset(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            return url
                .Replace("https://piston-data.mojang.com", Bmcl + "/assets")
                .Replace("https://piston-meta.mojang.com", Bmcl + "/assets")
                .Replace("https://resources.download.minecraft.net", Bmcl + "/assets")
                .Replace("http://resources.download.minecraft.net", Bmcl + "/assets");
        }

        private static string MirrorLib(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            return url
                .Replace("https://libraries.minecraft.net", Bmcl + "/maven")
                .Replace("https://piston-data.mojang.com", Bmcl + "/maven")
                .Replace("https://piston-meta.mojang.com", Bmcl + "/maven");
        }

        private bool DownloadWithEngine(string url, string fallback, string path, long size, string sha1 = null)
        {
            CheckCancel();
            if (File.Exists(path))
            {
                long len = new FileInfo(path).Length;
                // jar 这类大文件光比大小不够，镜像返回同长坏包时会一直"装好了但启动不了"
                if ((size <= 0 || len == size) && (string.IsNullOrEmpty(sha1) || Sha1Of(path) == sha1.ToLowerInvariant()))
                    return true;
                try { File.Delete(path); } catch { }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            Exception last = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                CheckCancel();
                var use = Pick(url, fallback, attempt);
                var dl = new MultiThreadDownloader(use, path, 8, false);
                dl.OnLog += s => { if (s.Contains("失败")) Log("  " + s); };
                dl.Start();
                while (dl.State == DlState.Idle || dl.State == DlState.Probing ||
                       dl.State == DlState.Downloading || dl.State == DlState.Merging)
                {
                    if (Cancel) { dl.Cancel(); break; }
                    Thread.Sleep(80);
                }
                if (dl.State == DlState.Finished)
                {
                    if (!string.IsNullOrEmpty(sha1) && Sha1Of(path) != sha1.ToLowerInvariant())
                    {
                        try { File.Delete(path); } catch { }
                        last = new Exception("下载到的文件 SHA-1 不对（镜像可能给了错包）");
                        MirrorFailed();
                    }
                    else return true;
                }
                else { if (use == url) MirrorFailed(); last = new Exception(dl.State == DlState.Cancelled ? "已取消" : "下载失败：" + url); }
                if (Cancel) break;
                Thread.Sleep(1000 * (attempt + 1));
            }
            throw last ?? new Exception("下载失败：" + url);
        }

        /// <summary>小文件直连下载（资源、索引），大小一致就跳过；给了 sha1 还要校验哈希</summary>
        private bool DownloadSmall(string url, string fallback, string path, long size, bool quiet = false, string sha1 = null)
        {
            CheckCancel();
            if (File.Exists(path))
            {
                bool ok = size <= 0 || new FileInfo(path).Length == size;
                if (ok && !string.IsNullOrEmpty(sha1)) ok = Sha1Of(path) == sha1.ToLowerInvariant();
                if (ok) return true;
                try { File.Delete(path); } catch { }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            Exception last = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                CheckCancel();
                string tmp = path + ".tmp";
                try
                {
                    string use = Pick(url, fallback, attempt);
                    // WebClient 没有超时：镜像站卡住时会永远停在"正在下载资源"。改用手动请求，带读写超时
                    FetchToFile(use, tmp);
                    long len = new FileInfo(tmp).Length;
                    if (size > 0 && len != size) throw new Exception("大小不符（期望 " + size + "，实际 " + len + "）");
                    // assets 按 SHA-1 命名，只比大小会放过内容错的镜像响应
                    if (!string.IsNullOrEmpty(sha1) && Sha1Of(tmp) != sha1.ToLowerInvariant())
                        throw new Exception("哈希不符（期望 " + sha1.Substring(0, 8) + "…）");
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                    return true;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (ex.Message.Contains("503") || ex.Message.Contains("timed out")) MirrorFailed();
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    Thread.Sleep(500 * (attempt + 1));
                }
            }
            throw last ?? new Exception("下载失败：" + url);
        }

        internal static void FetchToFile(string url, string path)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "PCLDownloader/2.3";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 20000;
            req.AllowAutoRedirect = true;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buf = new byte[81920];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
            }
        }

        internal static string Sha1Of(string path)
        {
            try
            {
                using (var sha = SHA1.Create())
                using (var fs = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    var sb = new StringBuilder(hash.Length);
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return ""; }
        }

        private static void ExtractNatives(string zipPath, string targetDir)
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.FullName.EndsWith("/")) continue;
                    if (!e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    string outPath = Path.Combine(targetDir, Path.GetFileName(e.FullName));
                    e.ExtractToFile(outPath, true);
                }
            }
        }
    }

    internal static class McNet
    {
        public static string Get(string url, int retries)
        {
            Exception last = null;
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    // WebClient 无超时，镜像站挂住时会永远转圈
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.UserAgent = "PCLDownloader/2.3";
                    req.Timeout = 10000;
                    req.ReadWriteTimeout = 15000;
                    req.AllowAutoRedirect = true;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        return sr.ReadToEnd();
                }
                catch (Exception ex) { last = ex; Thread.Sleep(500 * (i + 1)); }
            }
            throw last;
        }
    }
}
