using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PclDlGui
{
    /// <summary>
    /// 内置浏览器（WebView2 / Edge 内核）。
    /// 网页里点下载时拦截下来，把真实 URL + 来源页 + 登录 Cookie 交给主窗口的下载队列。
    /// 若系统缺 WebView2 运行时，自动降级成“用系统浏览器打开”，不影响主程序。
    /// </summary>
    public class BrowserForm : Form
    {
        private readonly Action<string, string, string> _onDownload;
        private WebView2 _web;
        private TextBox _txtAddr;
        private Label _labStatus;
        private Button _btnSystem;
        private bool _ready;
        private string _pendingUrl;

        public BrowserForm(Action<string, string, string> onDownload)
        {
            _onDownload = onDownload;
            Text = "内置浏览器 · 点下载自动进队列";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(1080, 740);
            MinimumSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterParent;

            Panel bar = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Color.FromArgb(248, 249, 251) };
            Controls.Add(bar);

            var btnBack = new Button { Text = "←", Left = 8, Top = 8, Width = 34, Height = 26, FlatStyle = FlatStyle.Flat };
            var btnFwd = new Button { Text = "→", Left = 46, Top = 8, Width = 34, Height = 26, FlatStyle = FlatStyle.Flat };
            var btnReload = new Button { Text = "刷新", Left = 84, Top = 8, Width = 52, Height = 26, FlatStyle = FlatStyle.Flat };
            _txtAddr = new TextBox { Left = 144, Top = 10, Width = 640, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            var btnGo = new Button { Text = "打开", Left = 792, Top = 8, Width = 58, Height = 26, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var btnGrab = new Button { Text = "下载此页", Left = 856, Top = 8, Width = 84, Height = 26, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _btnSystem = new Button { Text = "系统浏览器", Left = 946, Top = 8, Width = 96, Height = 26, Anchor = AnchorStyles.Top | AnchorStyles.Right, Visible = false };
            bar.Controls.Add(btnBack); bar.Controls.Add(btnFwd); bar.Controls.Add(btnReload);
            bar.Controls.Add(_txtAddr); bar.Controls.Add(btnGo); bar.Controls.Add(btnGrab); bar.Controls.Add(_btnSystem);

            _labStatus = new Label
            {
                Dock = DockStyle.Bottom, Height = 24, Text = "正在启动浏览器内核…",
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray, Padding = new Padding(8, 0, 0, 0)
            };
            Controls.Add(_labStatus);

            try
            {
                _web = new WebView2 { Dock = DockStyle.Fill };
                Controls.Add(_web);
                _web.BringToFront();
            }
            catch (Exception ex)
            {
                _labStatus.Text = "浏览器内核加载失败：" + ex.Message;
                _btnSystem.Visible = true;
            }

            btnBack.Click += (s, e) => { try { if (_web.CoreWebView2.CanGoBack) _web.CoreWebView2.GoBack(); } catch { } };
            btnFwd.Click += (s, e) => { try { if (_web.CoreWebView2.CanGoForward) _web.CoreWebView2.GoForward(); } catch { } };
            btnReload.Click += (s, e) => { try { _web.CoreWebView2.Reload(); } catch { } };
            btnGo.Click += (s, e) => Navigate();
            _txtAddr.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    Navigate();
                }
            };
            _txtAddr.KeyPress += (s, e) =>
            {
                if (e.KeyChar == (char)Keys.Return)
                {
                    e.Handled = true;
                    Navigate();
                }
            };
            btnGrab.Click += (s, e) => GrabCurrent();
            _btnSystem.Click += (s, e) => OpenExternal(CurrentUrl());

            Load += async (s, e) => await InitAsync();
        }

        private async Task InitAsync()
        {
            if (_web == null) return;
            try
            {
                string dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PCLDownloader", "WebView2");
                Directory.CreateDirectory(dataDir);

                var env = await CoreWebView2Environment.CreateAsync(null, dataDir, null);
                await _web.EnsureCoreWebView2Async(env);

                var core = _web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = true;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.DownloadStarting += Core_DownloadStarting;
                core.NewWindowRequested += (s, ev) => { ev.Handled = true; try { core.Navigate(ev.Uri); } catch { } };
                core.NavigationCompleted += (s, ev) =>
                {
                    try { _txtAddr.Text = _web.Source != null ? _web.Source.ToString() : ""; } catch { }
                    _labStatus.Text = ev.IsSuccess ? "就绪：网页里的下载会自动进队列" : "打开失败：" + ev.WebErrorStatus;
                };
                core.SourceChanged += (s, ev) =>
                {
                    try { _txtAddr.Text = _web.Source != null ? _web.Source.ToString() : ""; } catch { }
                };

                _ready = true;
                _labStatus.Text = "就绪：网页里的下载会自动进队列";
                if (!string.IsNullOrEmpty(_pendingUrl))
                {
                    string pending = _pendingUrl;
                    _pendingUrl = null;
                    TryNavigate(pending);
                }
                else
                {
                    TryNavigate("https://www.bing.com/");
                }
            }
            catch (Exception ex)
            {
                _ready = false;
                _labStatus.Text = "浏览器内核不可用：" + ex.Message + "。可装 WebView2 运行库，或点“系统浏览器”打开。";
                _btnSystem.Visible = true;
            }
        }

        private string CurrentUrl()
        {
            try { return _web != null && _web.Source != null ? _web.Source.ToString() : _txtAddr.Text.Trim(); }
            catch { return _txtAddr.Text.Trim(); }
        }

        private void Navigate()
        {
            string s = _txtAddr.Text.Trim();
            if (s.Length == 0) return;
            if (!s.Contains("://")) s = "https://" + s;
            TryNavigate(s);
        }

        private void TryNavigate(string url)
        {
            if (!_ready || _web == null || _web.CoreWebView2 == null)
            {
                _pendingUrl = url;
                _labStatus.Text = "浏览器内核还在启动，地址已记下，就绪后自动打开…";
                return;
            }
            try
            {
                _pendingUrl = null;
                _labStatus.Text = "正在打开：" + url;
                _web.Source = new Uri(url);
            }
            catch (Exception ex)
            {
                _labStatus.Text = "地址无效：" + ex.Message + "（要带 http:// 或 https://）";
            }
        }

        private void OpenExternal(string url)
        {
            try { Process.Start(url); }
            catch (Exception ex) { _labStatus.Text = "打开系统浏览器失败：" + ex.Message; }
        }

        /// <summary>把当前页面地址当成下载链接入队（有些站点是“页面即文件”的直链）</summary>
        private async void GrabCurrent()
        {
            string url = CurrentUrl();
            if (string.IsNullOrEmpty(url)) return;
            string referer = "";
            string cookie = "";
            try
            {
                if (_ready && _web.CoreWebView2 != null)
                {
                    referer = _web.Source != null ? _web.Source.ToString() : "";
                    var list = await _web.CoreWebView2.CookieManager.GetCookiesAsync(url);
                    var sb = new StringBuilder();
                    foreach (var c in list) sb.Append(c.Name).Append('=').Append(c.Value).Append("; ");
                    cookie = sb.ToString().Trim();
                }
            }
            catch { }
            _onDownload(url, referer, cookie);
            _labStatus.Text = "已加入队列：" + url;
        }

        /// <summary>
        /// WebView2 的下载事件：取消内核自带下载，改由多线程引擎接手。
        /// Cookie 用 CookieManager 按该 URL 取，登录态站点也能下。
        /// </summary>
        private async void Core_DownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            string uri = "";
            try { uri = e.DownloadOperation.Uri; } catch { }
            e.Cancel = true;
            if (string.IsNullOrEmpty(uri)) { _labStatus.Text = "拦截到一个下载，但拿不到地址"; return; }

            string referer = "";
            string cookie = "";
            try
            {
                referer = _web.Source != null ? _web.Source.ToString() : "";
                var list = await _web.CoreWebView2.CookieManager.GetCookiesAsync(uri);
                var sb = new StringBuilder();
                foreach (var c in list) sb.Append(c.Name).Append('=').Append(c.Value).Append("; ");
                cookie = sb.ToString().Trim();
            }
            catch { }

            BeginInvoke((Action)(() =>
            {
                try
                {
                    _onDownload(uri, referer, cookie);
                    _labStatus.Text = "已拦截并加入队列：" + uri;
                }
                catch (Exception ex) { _labStatus.Text = "加入队列失败：" + ex.Message; }
            }));
        }
    }
}
