using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using PclDl;

namespace PclDlGui
{
    public class MainForm : Form
    {
        private TextBox txtUrl, txtPath, txtLog;
        private NumericUpDown numThreads, numParallel, numLimit;
        private CheckBox chkDns, chkNet, chkClip;
        private RoundButton btnAdd, btnPaste, btnBrowse, btnOpenDir;
        private RoundButton btnStartAll, btnPauseAll, btnCancelAll, btnRetry, btnClear, btnSettings;
        private RoundButton btnOpenFile, btnExport, btnBrowser, btnMc;
        private ListView lv;
        private ComboBox cboAfter;
        private Label labOverall;

        private readonly List<TaskItem> _tasks = new List<TaskItem>();
        private AppSettings _settings;
        private int _active;
        private bool _allPaused;
        private bool _afterDone = true;
        private string _lastClip;
        private readonly Dictionary<int, Icon> _icons = new Dictionary<int, Icon>();
        private float _zoom = 1f;
        private BrowserForm _browser;
        private McForm _mcForm;
        private int _trayPercent = -1;

        private NotifyIcon _tray;
        private Timer _timer;
        private bool _exiting;

        public MainForm(string[] args)
        {
            Text = "光速下载器";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(980, 730);
            MinimumSize = new Size(900, 660);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;

            _settings = AppSettings.Load();
            NetTuner.RecoverIfNeeded();

            BuildUi();
            ApplyZoom(_settings.Zoom > 0 ? _settings.Zoom : SystemDpi() / 96f);
            BuildTray();
            LoadQueue();

            if (args != null)
                foreach (var a in args)
                    if (a.StartsWith("http")) AddTask(a);

            Shown += (s, e) =>
            {
                var wa = Screen.FromControl(this).WorkingArea;
                int nx = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
                int ny = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
                if (nx != Left || ny != Top) Location = new Point(nx, ny);
            };
            DpiChanged += (s, e) =>
            {
                if (_settings != null && _settings.Zoom <= 0.01f)
                    ApplyZoom(DeviceDpi / 96f);
            };

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();

            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized && (_active > 0 || _allPaused)) HideToTray();
            };
            FormClosing += (s, e) =>
            {
                if (!_exiting && (_active > 0 || _allPaused))
                {
                    e.Cancel = true;
                    HideToTray();
                    return;
                }
                SaveQueue();
                NetTuner.Restore(null);
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                DestroyIconHandle();
            };
        }

        // ================= 界面 =================

        private void BuildUi()
        {
            int x = 16, w = ClientSize.Width - 32;

            Controls.Add(new Label { Text = "下载地址（每行一个，可批量粘贴 / 拖入链接）", Left = x, Top = 13, Width = 500, Height = 20 });

            txtUrl = new TextBox
            {
                Left = x, Top = 30, Width = w - 190, Height = 56,
                Multiline = true, ScrollBars = ScrollBars.Vertical,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(txtUrl);

            btnAdd = Btn("添加到队列", x + w - 176, 30, 176, 26, true);
            btnAdd.Click += (s, e) => AddFromBox();
            btnPaste = Btn("粘贴并添加", x + w - 176, 60, 176, 26);
            btnPaste.Click += (s, e) => PasteAndAdd();

            Controls.Add(new Label { Text = "保存到（文件夹）", Left = x, Top = 97, Width = 200, Height = 20 });

            txtPath = new TextBox
            {
                Left = x, Top = 112, Width = w - 190,
                Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Downloads"),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(txtPath);
            btnBrowse = Btn("浏览…", x + w - 176, 111, 84, 24);
            btnBrowse.Click += (s, e) => BrowseDir();
            btnOpenDir = Btn("打开目录", x + w - 88, 111, 88, 24);
            btnOpenDir.Click += (s, e) => OpenDir();

            Controls.Add(Lab("线程数", x, 148, 52));
            numThreads = Num(x + 56, 145, 58, 1, 256, 16);
            Controls.Add(Lab("并发任务", x + 120, 148, 60));
            numParallel = Num(x + 184, 145, 52, 1, 8, 2);
            Controls.Add(Lab("限速", x + 244, 148, 32));
            numLimit = Num(x + 282, 145, 64, 0, 1048576, 0);
            Controls.Add(new Label { Text = "KB/s（0=不限）", Left = x + 350, Top = 148, Width = 100, Height = 18, ForeColor = Color.Gray });

            chkDns = new CheckBox { Text = "自动优选 DNS", Left = x + 460, Top = 145, Width = 116, Height = 20, Checked = true };
            chkNet = new CheckBox { Text = "网络调优", Left = x + 578, Top = 145, Width = 86, Height = 20, Checked = true };
            chkClip = new CheckBox { Text = "监听剪贴板链接", Left = x + 668, Top = 145, Width = 140, Height = 20 };
            Controls.Add(chkDns); Controls.Add(chkNet); Controls.Add(chkClip);

            btnStartAll = Btn("开始全部", x, 178, 104, 32, true);
            btnStartAll.Click += (s, e) => StartAll();
            btnPauseAll = Btn("暂停全部", x + 112, 178, 96, 32);
            btnPauseAll.Click += (s, e) => TogglePauseAll();
            btnCancelAll = Btn("取消全部", x + 216, 178, 92, 32);
            btnCancelAll.Click += (s, e) => CancelAll();
            btnRetry = Btn("重试失败", x + 316, 178, 92, 32);
            btnRetry.Click += (s, e) => RetryFailed();
            btnClear = Btn("清空已完成", x + 416, 178, 100, 32);
            btnClear.Click += (s, e) => ClearFinished();
            btnSettings = Btn("设置…", x + 524, 178, 84, 32);
            btnSettings.Click += (s, e) => OpenSettings();
            btnBrowser = Btn("浏览网页", x + 616, 178, 96, 32, true);
            btnBrowser.Click += (s, e) => OpenBrowser();
            btnMc = Btn("下载 MC", x + 720, 178, 96, 32);
            btnMc.Click += (s, e) => OpenMc();

            lv = new ListView
            {
                Left = x, Top = 220, Width = w, Height = 252,
                View = View.Details, FullRowSelect = true, GridLines = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                MultiSelect = false
            };
            lv.Columns.Add("文件名", 360);
            lv.Columns.Add("大小", 100);
            lv.Columns.Add("进度", 80, HorizontalAlignment.Right);
            lv.Columns.Add("速度", 110, HorizontalAlignment.Right);
            lv.Columns.Add("状态", 160);
            lv.Columns.Add("线程", 70);
            lv.ContextMenuStrip = BuildTaskMenu();
            lv.DoubleClick += (s, e) => OpenSelected();
            Controls.Add(lv);

            Controls.Add(new Label { Text = "日志", Left = x, Top = 481, Width = 100, Height = 18, ForeColor = Color.Gray });

            txtLog = new TextBox
            {
                Left = x, Top = 496, Width = w, Height = 150,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 250, 250),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            Controls.Add(txtLog);

            Controls.Add(new Label { Text = "完成后", Left = x, Top = 664, Width = 48, Height = 20 });
            cboAfter = new ComboBox
            {
                Left = x + 52, Top = 660, Width = 140, DropDownStyle = ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            cboAfter.Items.AddRange(new object[] { "不操作", "打开文件夹", "睡眠", "关机（60 秒）" });
            cboAfter.SelectedIndex = 0;
            Controls.Add(cboAfter);

            labOverall = new Label { Left = x + 204, Top = 664, Width = 500, Height = 20, Text = "队列 0 个", ForeColor = Color.Gray };
            Controls.Add(labOverall);

            btnOpenFile = Btn("打开文件", x + w - 190, 658, 90, 28);
            btnOpenFile.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnOpenFile.Enabled = false;
            btnOpenFile.Click += (s, e) => OpenSelected();
            btnExport = Btn("导出日志", x + w - 92, 658, 92, 28);
            btnExport.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnExport.Click += (s, e) => ExportLog();

            numLimit.ValueChanged += (s, e) => ApplyLimit();
            numParallel.ValueChanged += (s, e) => { if (_active > 0) RunScheduler(); };

            DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.Text)) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += (s, e) =>
            {
                string t = (string)e.Data.GetData(DataFormats.Text);
                if (t != null) AddMany(t);
            };
        }

        /// <summary>当前系统 DPI（PerMonitorV2 进程返回主屏 DPI）</summary>
        private static float SystemDpi()
        {
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                    return g.DpiX > 0 ? g.DpiX : 96f;
            }
            catch { return 96f; }
        }

        /// <summary>
        /// 缩放界面：0.75~2.0 之间；若窗体超出屏幕工作区（小屏 + 高 DPI），自动往下收，
        /// 保证标题栏、底部按钮和日志框都在屏幕内。
        /// </summary>
        private void ApplyZoom(float z)
        {
            if (z <= 0) z = SystemDpi() / 96f;
            if (z > 2f) z = 2f;
            var wa = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : new Rectangle(0, 0, 1280, 800);
            while (z > 0.75f && (980f * z + 18f > wa.Width || 730f * z + 42f > wa.Height))
                z -= 0.05f;
            if (z < 0.75f) z = 0.75f;
            float ratio = z / _zoom;
            if (Math.Abs(ratio - 1f) > 0.001f)
                Scale(new SizeF(ratio, ratio));
            _zoom = z;
            MinimumSize = new Size((int)(900 * z), (int)(660 * z));

            // Scale() 只缩放控件矩形，字体和列表列宽要单独跟着走，否则高 DPI 下字会显小
            Font = new Font("Microsoft YaHei UI", 9f * z);
            if (lv != null && lv.Columns.Count == 6)
            {
                int[] baseW = { 360, 100, 80, 110, 160, 70 };
                for (int i = 0; i < 6; i++) lv.Columns[i].Width = (int)(baseW[i] * z);
            }
        }

        private static Label Lab(string t, int x, int y, int w)
        {
            return new Label { Text = t, Left = x, Top = y, Width = w, Height = 20 };
        }

        private NumericUpDown Num(int x, int y, int w, int min, int max, int val)
        {
            return new NumericUpDown { Left = x, Top = y, Width = w, Minimum = min, Maximum = max, Value = val };
        }

        private RoundButton Btn(string text, int x, int y, int w, int h, bool primary = false)
        {
            var b = new RoundButton { Text = text, Left = x, Top = y, Width = w, Height = h };
            if (primary) StylePrimary(b);
            Controls.Add(b);
            return b;
        }

        private static void StylePrimary(RoundButton b)
        {
            b.NormalBack = Color.FromArgb(0, 120, 212);
            b.HoverBack = Color.FromArgb(16, 110, 190);
            b.PressBack = Color.FromArgb(0, 90, 158);
            b.DisabledBack = Color.FromArgb(199, 223, 245);
            b.NormalBorder = Color.FromArgb(0, 120, 212);
            b.HoverBorder = Color.FromArgb(0, 90, 158);
            b.DisabledBorder = Color.FromArgb(199, 223, 245);
            b.TextColor = Color.White;
            b.DisabledTextColor = Color.FromArgb(108, 143, 175);
        }

        private ContextMenuStrip BuildTaskMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("暂停 / 继续", null, (s, e) => ToggleSelected());
            m.Items.Add("取消", null, (s, e) => CancelSelected());
            m.Items.Add("重试", null, (s, e) => RetrySelected());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("打开文件", null, (s, e) => OpenSelected());
            m.Items.Add("打开所在文件夹", null, (s, e) => OpenSelectedDir());
            m.Items.Add("复制链接", null, (s, e) => CopySelectedUrl());
            m.Items.Add("设置 SHA-256…", null, (s, e) => SetSelectedSha());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("移除任务", null, (s, e) => RemoveSelected());
            return m;
        }

        // ================= 托盘 =================

        private void BuildTray()
        {
            _tray = new NotifyIcon { Icon = TrayIcon(0), Text = "光速下载器", Visible = true };
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主界面", null, (s, e) => ShowFromTray());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("暂停 / 继续全部", null, (s, e) => TogglePauseAll());
            menu.Items.Add("取消全部", null, (s, e) => CancelAll());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => { _exiting = true; CancelAll(); Close(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => ShowFromTray();
        }

        private Icon TrayIcon(int percent)
        {
            Icon cached;
            if (_icons.TryGetValue(percent, out cached)) return cached;

            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var brush = new SolidBrush(Color.FromArgb(0, 120, 212)))
                        g.FillEllipse(brush, 2, 2, 28, 28);
                    if (percent > 0)
                    {
                        using (var brush = new SolidBrush(Color.White))
                            g.FillPie(brush, 4, 4, 24, 24, -90, (float)(percent * 3.6));
                    }
                    using (var pen = new Pen(Color.FromArgb(0, 90, 158), 2f))
                    {
                        g.DrawLine(pen, 16, 9, 16, 19);
                        g.DrawLine(pen, 11, 15, 16, 20);
                        g.DrawLine(pen, 21, 15, 16, 20);
                    }
                }
                var icon = Icon.FromHandle(bmp.GetHicon());
                _icons[percent] = icon;
                return icon;
            }
        }

        private void DestroyIconHandle()
        {
            foreach (var ic in _icons.Values)
            {
                try { ic.Dispose(); } catch { }
            }
            _icons.Clear();
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr h);

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
        }

        private void ShowFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        // ================= 队列 =================

        private void AddFromBox()
        {
            AddMany(txtUrl.Text);
            txtUrl.Clear();
        }

        private void PasteAndAdd()
        {
            try
            {
                string s = Clipboard.GetText();
                if (!string.IsNullOrEmpty(s)) { AddMany(s); txtUrl.Clear(); }
            }
            catch { }
        }

        private void AddMany(string text)
        {
            if (text == null) return;
            int added = 0;
            foreach (var raw in text.Split('\n'))
            {
                string u = raw.Trim().Trim('"');
                if (!u.StartsWith("http://") && !u.StartsWith("https://")) continue;
                if (AddTask(u)) added++;
            }
            if (added > 0)
            {
                Log("已加入 " + added + " 个任务");
                SaveQueue();
                UpdateSummary();
            }
        }

        private bool AddTask(string url)
        {
            foreach (var t in _tasks)
                if (t.Url == url && t.State != DlState.Finished) return false;

            string dir = txtPath.Text.Trim();
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Downloads");
            try { Directory.CreateDirectory(dir); } catch { }

            string name = GuessName(url);
            if (string.IsNullOrEmpty(name)) name = "download.bin";
            string path = UniquePath(Path.Combine(dir, name));

            AddTaskItem(url, path, (int)numThreads.Value);
            return true;
        }

        private void AddTaskItem(string url, string path, int threads)
        {
            var t = new TaskItem { Url = url, Path = path, Threads = threads };
            var row = new ListViewItem(t.Name);
            row.SubItems.Add("—");
            row.SubItems.Add("0%");
            row.SubItems.Add("—");
            row.SubItems.Add("等待开始");
            row.SubItems.Add("—");
            row.Tag = t;
            t.Row = row;
            lv.Items.Add(row);
            _tasks.Add(t);
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; i < 999; i++)
            {
                string p = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(p)) return p;
            }
            return path;
        }

        private static string GuessName(string url)
        {
            try
            {
                var uri = new Uri(url.Trim());
                return Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            }
            catch { return ""; }
        }

        private void BrowseDir()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择保存目录";
                dlg.SelectedPath = txtPath.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) txtPath.Text = dlg.SelectedPath;
            }
        }

        private void OpenDir()
        {
            try
            {
                string d = txtPath.Text.Trim();
                if (Directory.Exists(d)) Process.Start("explorer.exe", "\"" + d + "\"");
            }
            catch { }
        }

        private void StartAll()
        {
            ApplyLimit();
            if (chkNet.Checked) NetTuner.Apply(m => Ui(() => Log(m)));

            _afterDone = false;
            _allPaused = false;
            btnPauseAll.Text = "暂停全部";

            foreach (var t in _tasks)
            {
                if (t.Running) continue;
                if (t.State == DlState.Finished) continue;
                t.State = DlState.Idle;
                t.Message = null;
                t.ResetProgress();
                UpdateRow(t);
            }
            RunScheduler();
        }

        private void ApplyLimit()
        {
            long kb = (long)numLimit.Value;
            SpeedLimiter.Global.SetLimit(kb > 0 ? kb * 1024 : 0);
        }

        private void RunScheduler()
        {
            if (_allPaused) { UpdateSummary(); return; }
            int max = (int)numParallel.Value;

            foreach (var t in _tasks)
            {
                if (_active >= max) break;
                if (t.Running || t.State == DlState.Finished) continue;
                if (t.State != DlState.Idle && t.State != DlState.Failed && t.State != DlState.Cancelled) continue;
                StartTask(t);
            }
            UpdateSummary();
        }

        private void StartTask(TaskItem t)
        {
            t.Running = true;
            t.State = DlState.Probing;
            t.Message = "正在探测…";

            var dl = new MultiThreadDownloader(t.Url, t.Path, t.Threads, chkDns.Checked)
            {
                Proxy = _settings.BuildProxy(),
                ExpectedSha256 = string.IsNullOrEmpty(t.Sha256) ? null : t.Sha256,
                AlwaysHash = _settings.AlwaysHash
            };
            t.Dl = dl;

            var headers = _settings.BuildHeaders();
            foreach (var kv in headers) dl.CustomHeaders[kv.Key] = kv.Value;
            if (!string.IsNullOrEmpty(t.Referer)) dl.CustomHeaders["Referer"] = t.Referer;
            if (!string.IsNullOrEmpty(t.Cookie)) dl.CustomHeaders["Cookie"] = t.Cookie;
            if (_settings.Mirrors.Count > 0) dl.Mirrors.AddRange(_settings.Mirrors);
            if (t.Mirrors.Count > 0) dl.Mirrors.AddRange(t.Mirrors);

            dl.OnLog += msg => Ui(() => Log("[" + t.Name + "] " + msg));
            dl.OnProgress += p => Ui(() => OnTaskProgress(t, p));

            _active++;
            UpdateRow(t);
            UpdateButtons();
            dl.Start();
        }

        private void OnTaskProgress(TaskItem t, DlProgress p)
        {
            t.State = p.State;
            t.Done = p.Done;
            t.Total = p.Total;
            t.Speed = p.Speed;
            if (p.Speed > t.PeakSpeed) t.PeakSpeed = p.Speed;
            t.ActiveThreads = p.ActiveThreads;
            t.Message = p.Message;
            UpdateRow(t);

            if (p.State == DlState.Finished || p.State == DlState.Failed || p.State == DlState.Cancelled)
            {
                if (t.Running)
                {
                    t.Running = false;
                    _active--;
                }
                if (p.State == DlState.Finished)
                {
                    t.Row.SubItems[2].Text = "100%";
                    try { t.Row.SubItems[1].Text = MultiThreadDownloader.FormatSize(new FileInfo(t.Path).Length); } catch { }
                    t.Dl = null;
                }
                UpdateRow(t);
                UpdateButtons();
                RunScheduler();
                CheckAllDone();
            }
            UpdateSummary();
        }

        private void CheckAllDone()
        {
            if (_active > 0 || _afterDone) return;
            bool anyPending = false, anyFinished = false;
            foreach (var t in _tasks)
            {
                if (t.Running || t.State == DlState.Idle || t.State == DlState.Probing || t.State == DlState.Downloading)
                    anyPending = true;
                if (t.State == DlState.Finished) anyFinished = true;
            }
            if (anyPending) return;

            _afterDone = true;
            if (chkNet.Checked) NetTuner.Restore(m => Ui(() => Log(m)));
            Log("全部任务结束");

            if (!anyFinished) return;
            switch (cboAfter.SelectedIndex)
            {
                case 1: OpenDir(); break;
                case 2: Run("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"); break;
                case 3: Run("shutdown.exe", "/s /t 60"); Log("60 秒后关机，可执行 shutdown /a 取消"); break;
            }
        }

        private static void Run(string exe, string args)
        {
            try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true }); } catch { }
        }

        private void TogglePauseAll()
        {
            _allPaused = !_allPaused;
            btnPauseAll.Text = _allPaused ? "继续全部" : "暂停全部";

            foreach (var t in _tasks)
            {
                if (t.Dl == null) continue;
                if (_allPaused)
                {
                    if (t.Dl.State == DlState.Downloading) t.Dl.Pause();
                }
                else if (t.Dl.State == DlState.Paused) t.Dl.Resume();
            }
            if (!_allPaused) RunScheduler();
            UpdateSummary();
        }

        private void CancelAll()
        {
            foreach (var t in _tasks)
                if (t.Dl != null) t.Dl.Cancel();
            _allPaused = false;
            btnPauseAll.Text = "暂停全部";
        }

        private void RetryFailed()
        {
            foreach (var t in _tasks)
            {
                if (t.Running) continue;
                if (t.State == DlState.Failed || t.State == DlState.Cancelled)
                {
                    t.State = DlState.Idle;
                    t.ResetProgress();
                    UpdateRow(t);
                }
            }
            RunScheduler();
        }

        private void ClearFinished()
        {
            for (int i = _tasks.Count - 1; i >= 0; i--)
            {
                if (_tasks[i].State == DlState.Finished && !_tasks[i].Running)
                {
                    lv.Items.Remove(_tasks[i].Row);
                    _tasks.RemoveAt(i);
                }
            }
            SaveQueue();
            UpdateSummary();
        }

        private TaskItem Selected
        {
            get
            {
                if (lv.SelectedItems.Count == 0) return null;
                return lv.SelectedItems[0].Tag as TaskItem;
            }
        }

        private void ToggleSelected()
        {
            var t = Selected; if (t == null || t.Dl == null) return;
            if (t.Dl.State == DlState.Paused) t.Dl.Resume();
            else if (t.Dl.State == DlState.Downloading) t.Dl.Pause();
        }

        private void CancelSelected()
        {
            var t = Selected; if (t == null || t.Dl == null) return;
            t.Dl.Cancel();
        }

        private void RetrySelected()
        {
            var t = Selected; if (t == null || t.Running) return;
            t.State = DlState.Idle;
            t.ResetProgress();
            UpdateRow(t);
            RunScheduler();
        }

        private void RemoveSelected()
        {
            var t = Selected; if (t == null) return;
            if (t.Running) { t.Dl.Cancel(); return; }
            lv.Items.Remove(t.Row);
            _tasks.Remove(t);
            SaveQueue();
            UpdateSummary();
        }

        private void OpenSelected()
        {
            var t = Selected; if (t == null) return;
            try { if (File.Exists(t.Path)) Process.Start("explorer.exe", "/select,\"" + t.Path + "\""); } catch { }
        }

        private void OpenSelectedDir()
        {
            var t = Selected; if (t == null) return;
            try
            {
                string d = Path.GetDirectoryName(t.Path);
                if (Directory.Exists(d)) Process.Start("explorer.exe", "\"" + d + "\"");
            }
            catch { }
        }

        private void CopySelectedUrl()
        {
            var t = Selected; if (t == null) return;
            try { Clipboard.SetText(t.Url); } catch { }
        }

        private void SetSelectedSha()
        {
            var t = Selected; if (t == null) return;
            string v = InputDialog.Ask(this, "SHA-256 校验", "填写期望的 SHA-256（留空则不校验）：", t.Sha256);
            if (v == null) return;
            t.Sha256 = v.Trim();
            Log(t.Name + (string.IsNullOrEmpty(t.Sha256) ? "：已关闭校验" : "：已设置校验值"));
            SaveQueue();
        }

        // ================= 内置浏览器 =================

        private void OpenBrowser()
        {
            if (_browser != null && !_browser.IsDisposed && _browser.Visible)
            {
                _browser.Activate();
                return;
            }
            _browser = new BrowserForm((url, referer, cookie) => AddTaskFromBrowser(url, referer, cookie));
            _browser.FormClosed += (s, e) => { _browser = null; };
            _browser.Show(this);
        }

        private void OpenMc()
        {
            if (_mcForm != null && !_mcForm.IsDisposed && _mcForm.Visible)
            {
                _mcForm.Activate();
                return;
            }
            _mcForm = new McForm();
            _mcForm.FormClosed += (s, e) => { _mcForm = null; };
            _mcForm.Show(this);
        }

        private void AddTaskFromBrowser(string url, string referer, string cookie)
        {
            if (string.IsNullOrEmpty(url)) return;
            foreach (var t in _tasks)
                if (t.Url == url && t.State != DlState.Finished) { Log("浏览器：已在队列中 " + url); return; }

            string dir = txtPath.Text.Trim();
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Downloads");
            try { Directory.CreateDirectory(dir); } catch { }

            string name = GuessName(url);
            if (string.IsNullOrEmpty(name)) name = "download.bin";
            string path = UniquePath(Path.Combine(dir, name));

            AddTaskItem(url, path, (int)numThreads.Value);
            var item = _tasks[_tasks.Count - 1];
            item.Referer = referer;
            item.Cookie = cookie;
            SaveQueue();
            UpdateSummary();
            Log("浏览器拦截下载：" + name + (string.IsNullOrEmpty(cookie) ? "" : "（已带登录 Cookie）"));
        }

        private void OpenSettings()
        {
            using (var f = new SettingsForm(_settings))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    _settings = AppSettings.Load();
                    if (Math.Abs(_settings.Zoom - _zoom) > 0.001f)
                    {
                        ApplyZoom(_settings.Zoom);
                        Log("界面缩放已切换为 " + (int)Math.Round(_settings.Zoom * 100) + "%");
                    }
                    if (!string.IsNullOrEmpty(AppSettings.LastWriteError))
                        Log("设置保存失败：" + AppSettings.LastWriteError);
                    else
                        Log("设置已保存");
                }
            }
        }

        private void ExportLog()
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "文本文件 (*.txt)|*.txt";
                dlg.FileName = "下载日志_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".txt";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    try { File.WriteAllText(dlg.FileName, txtLog.Text, System.Text.Encoding.UTF8); } catch { }
                }
            }
        }

        // ================= 刷新 =================

        private void UpdateRow(TaskItem t)
        {
            if (t.Row == null) return;
            t.Row.SubItems[0].Text = t.Name;
            t.Row.SubItems[1].Text = t.SizeText;
            t.Row.SubItems[2].Text = t.Total > 0 ? t.Percent + "%" : "—";
            t.Row.SubItems[3].Text = t.Speed > 0 ? MultiThreadDownloader.FormatSize(t.Speed) + "/s" : "—";
            t.Row.SubItems[4].Text = StatusText(t);
            t.Row.SubItems[5].Text = t.ActiveThreads > 0 ? t.ActiveThreads + "/" + t.Threads : t.Threads + " 线";
        }

        private static string StatusText(TaskItem t)
        {
            switch (t.State)
            {
                case DlState.Idle: return "等待开始";
                case DlState.Probing: return "探测中…";
                case DlState.Downloading: return "下载中 " + t.Percent + "%";
                case DlState.Paused: return "已暂停 " + t.Percent + "%";
                case DlState.Merging: return "合并中…";
                case DlState.Finished: return "已完成";
                case DlState.Failed: return "失败：" + (t.Message ?? "未知错误");
                case DlState.Cancelled: return "已取消";
                default: return t.State.ToString();
            }
        }

        private void UpdateButtons()
        {
            bool anyActive = false;
            foreach (var t in _tasks) if (t.Running) anyActive = true;
            btnPauseAll.Enabled = anyActive;
            btnCancelAll.Enabled = anyActive;
            btnStartAll.Enabled = !_allPaused;
            btnOpenFile.Enabled = Selected != null;
        }

        private void UpdateSummary()
        {
            int running = 0;
            long speed = 0, done = 0, total = 0;
            foreach (var t in _tasks)
            {
                if (t.Running) running++;
                speed += t.Speed;
                done += t.Done;
                total += t.Total;
            }
            string pct = total > 0 ? " " + (done * 100 / total) + "%" : "";
            labOverall.Text = string.Format("队列 {0} 个 · 运行 {1} · 总速度 {2}/s{3}",
                _tasks.Count, running, MultiThreadDownloader.FormatSize(speed), pct);

            int tp = total > 0 ? (int)(done * 100 / total) : 0;
            if (tp != _trayPercent)
            {
                _trayPercent = tp;
                try
                {
                    _tray.Icon = TrayIcon(tp);
                    string tip = running > 0
                        ? ("下载中 " + tp + "%  " + MultiThreadDownloader.FormatSize(speed) + "/s")
                        : ("队列 " + _tasks.Count + " 个");
                    if (tip.Length > 60) tip = tip.Substring(0, 60);
                    _tray.Text = tip;
                }
                catch { }
            }
        }

        private void Tick()
        {
            if (chkClip.Checked) CheckClipboard();
            UpdateSummary();
            UpdateButtons();
        }

        private void CheckClipboard()
        {
            try
            {
                if (!Clipboard.ContainsText()) return;
                string s = Clipboard.GetText().Trim();
                if (s == _lastClip) return;
                _lastClip = s;
                if (s.StartsWith("http://") || s.StartsWith("https://"))
                {
                    AddMany(s);
                    Log("剪贴板捕获到链接，已加入队列");
                }
            }
            catch { }
        }

        private void Log(string msg)
        {
            if (IsDisposed) return;
            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine);
        }

        private void Ui(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }

        // ================= 队列持久化 =================

        private static string QueueFile
        {
            get { return Path.Combine(AppSettings.Dir, "tasks.tsv"); }
        }

        private void SaveQueue()
        {
            try
            {
                var lines = new List<string>();
                foreach (var t in _tasks)
                    lines.Add(t.Url + "\t" + t.Path + "\t" + t.Threads + "\t" + (t.Sha256 ?? "") + "\t" + (t.Referer ?? ""));
                AppSettings.SaveLines(QueueFile, lines);
            }
            catch (Exception ex) { Log("队列保存失败：" + ex.Message); }
        }

        private void LoadQueue()
        {
            try
            {
                if (!File.Exists(QueueFile)) { UpdateSummary(); return; }
                foreach (var line in AppSettings.ReadLines(QueueFile))
                {
                    var p = line.Split('\t');
                    if (p.Length < 2) continue;
                    int th = 16;
                    if (p.Length > 2) int.TryParse(p[2], out th);
                    if (th < 1 || th > 256) th = 16;
                    AddTaskItem(p[0], p[1], th);
                    if (p.Length > 3 && p[3].Length == 64) _tasks[_tasks.Count - 1].Sha256 = p[3];
                    if (p.Length > 4 && p[4].Length > 0) _tasks[_tasks.Count - 1].Referer = p[4];
                }
                Log("已恢复上次的 " + _tasks.Count + " 个任务（未下完的会重新开始）");
            }
            catch { }
            UpdateSummary();
        }
    }
}
