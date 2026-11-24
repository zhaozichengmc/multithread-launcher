using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PclDlGui
{
    /// <summary>三种登录方式：离线 / 第三方（authlib-injector 皮肤站）/ 微软正版</summary>
    public class AccountForm : Form
    {
        private RadioButton rbOffline, rbThird, rbMs;
        private Panel panOffline, panThird, panMs;
        private TextBox txtName, txtServer, txtUser, txtPass;
        private Label labMsCode, labMsTip, labThirdState;
        private RoundButton btnMsStart, btnThirdLogin;
        private CancellationTokenSource _msCts;
        public Account Result;
        private bool _saved;

        public AccountForm(Account cur)
        {
            Text = "登录";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(520, 300);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            rbOffline = new RadioButton { Text = "离线登录", Left = 16, Top = 14, Width = 100 };
            rbThird = new RadioButton { Text = "第三方登录（皮肤站）", Left = 124, Top = 14, Width = 180 };
            rbMs = new RadioButton { Text = "微软正版", Left = 312, Top = 14, Width = 100 };
            rbOffline.CheckedChanged += (s, e) => Switch();
            rbThird.CheckedChanged += (s, e) => Switch();
            rbMs.CheckedChanged += (s, e) => Switch();
            Controls.Add(rbOffline); Controls.Add(rbThird); Controls.Add(rbMs);

            // 离线
            panOffline = new Panel { Left = 8, Top = 44, Width = 504, Height = 200 };
            panOffline.Controls.Add(new Label { Text = "游戏内昵称", Left = 8, Top = 10, Width = 90 });
            txtName = new TextBox { Left = 100, Top = 7, Width = 240, Text = (cur != null && cur.Type == "offline") ? cur.Name : "Steve" };
            panOffline.Controls.Add(txtName);
            panOffline.Controls.Add(new Label
            {
                Text = "离线模式不需要密码，UUID 按昵称本地生成；\n只适合自己玩或局域网，不能进正版服务器。",
                Left = 8, Top = 44, Width = 470, Height = 44, ForeColor = Color.DimGray
            });
            Controls.Add(panOffline);

            // 第三方
            panThird = new Panel { Left = 8, Top = 44, Width = 504, Height = 200, Visible = false };
            panThird.Controls.Add(new Label { Text = "皮肤站地址", Left = 8, Top = 10, Width = 90 });
            txtServer = new TextBox { Left = 100, Top = 7, Width = 380, Text = (cur != null && cur.Type == "third") ? cur.Server : "https://littleskin.cn/api/yggdrasil" };
            panThird.Controls.Add(txtServer);
            panThird.Controls.Add(new Label { Text = "账号（邮箱）", Left = 8, Top = 42, Width = 90 });
            txtUser = new TextBox { Left = 100, Top = 39, Width = 240 };
            panThird.Controls.Add(txtUser);
            panThird.Controls.Add(new Label { Text = "密码", Left = 8, Top = 74, Width = 90 });
            txtPass = new TextBox { Left = 100, Top = 71, Width = 240, UseSystemPasswordChar = true };
            panThird.Controls.Add(txtPass);
            btnThirdLogin = new RoundButton { Text = "登录", Left = 356, Top = 70, Width = 96, Height = 28 };
            btnThirdLogin.NormalBack = Color.FromArgb(0, 120, 212);
            btnThirdLogin.HoverBack = Color.FromArgb(0, 132, 226);
            btnThirdLogin.PressBack = Color.FromArgb(0, 104, 190);
            btnThirdLogin.TextColor = Color.White;
            btnThirdLogin.Click += (s, e) => ThirdLogin();
            panThird.Controls.Add(btnThirdLogin);
            labThirdState = new Label { Left = 8, Top = 112, Width = 480, Height = 60, ForeColor = Color.DimGray, Text = "支持 LittleSkin、Blessing Skin 等任意 authlib 皮肤站" };
            panThird.Controls.Add(labThirdState);
            Controls.Add(panThird);

            // 微软
            panMs = new Panel { Left = 8, Top = 44, Width = 504, Height = 200, Visible = false };
            btnMsStart = new RoundButton { Text = "获取登录代码", Left = 8, Top = 8, Width = 132, Height = 30 };
            btnMsStart.Click += (s, e) => MsStart();
            panMs.Controls.Add(btnMsStart);
            labMsCode = new Label { Left = 8, Top = 50, Width = 480, Height = 30, Font = new Font("Consolas", 15F, FontStyle.Bold), Text = "" };
            panMs.Controls.Add(labMsCode);
            labMsTip = new Label
            {
                Left = 8, Top = 86, Width = 480, Height = 90, ForeColor = Color.DimGray,
                Text = "点按钮获取代码后，打开 https://microsoft.com/link 输入代码并登录微软账户。\n登录成功后本窗口会自动完成，之后启动无需再登录。"
            };
            panMs.Controls.Add(labMsTip);
            Controls.Add(panMs);

            var ok = new RoundButton { Text = "确定", Left = 300, Top = 256, Width = 96, Height = 30 };
            ok.NormalBack = Color.FromArgb(0, 120, 212); ok.HoverBack = Color.FromArgb(0, 132, 226);
            ok.PressBack = Color.FromArgb(0, 104, 190); ok.TextColor = Color.White;
            ok.Click += (s, e) => Confirm();
            var cancel = new RoundButton { Text = "取消", Left = 408, Top = 256, Width = 96, Height = 30 };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(ok); Controls.Add(cancel);

            if (cur != null && cur.Type == "third") rbThird.Checked = true;
            else if (cur != null && cur.Type == "ms") rbMs.Checked = true;
            else rbOffline.Checked = true;
            Switch();
            FormClosing += (s, e) => { if (_msCts != null) _msCts.Cancel(); };
        }

        private void Switch()
        {
            panOffline.Visible = rbOffline.Checked;
            panThird.Visible = rbThird.Checked;
            panMs.Visible = rbMs.Checked;
        }

        private void ThirdLogin()
        {
            string server = txtServer.Text.Trim();
            string user = txtUser.Text.Trim();
            string pass = txtPass.Text;
            if (user.Length == 0 || pass.Length == 0) { labThirdState.Text = "请填写账号和密码"; return; }
            btnThirdLogin.Enabled = false;
            labThirdState.Text = "正在登录…";
            var th = new Thread(() =>
            {
                try
                {
                    var acc = Auth.ThirdLogin(server, user, pass, s => { });
                    BeginInvoke((Action)(() =>
                    {
                        Result = acc;
                        AccountStore.Save(acc);
                        labThirdState.Text = "登录成功：" + acc.Name + "（可直接点确定）";
                        btnThirdLogin.Enabled = true;
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke((Action)(() => { labThirdState.Text = ex.Message; btnThirdLogin.Enabled = true; }));
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void MsStart()
        {
            btnMsStart.Enabled = false;
            labMsCode.Text = "正在获取…";
            _msCts = new CancellationTokenSource();
            var cts = _msCts;
            var th = new Thread(() =>
            {
                try
                {
                    var code = Auth.MsStart();
                    BeginInvoke((Action)(() =>
                    {
                        labMsCode.Text = code.UserCode;
                        try { Clipboard.SetText(code.UserCode); } catch { }
                        labMsTip.Text = "打开 " + code.VerificationUri + " 输入上面的代码（已复制到剪贴板）\n等待授权中…";
                        try { Process.Start(code.VerificationUri); } catch { }
                    }));
                    var acc = Auth.MsPoll(code, cts.Token, s => { });
                    BeginInvoke((Action)(() =>
                    {
                        Result = acc;
                        AccountStore.Save(acc);
                        labMsTip.Text = "登录成功：" + acc.Name + "，可以直接启动游戏了。";
                        labMsCode.Text = acc.Name;
                        btnMsStart.Enabled = true;
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke((Action)(() =>
                    {
                        labMsTip.Text = ex.Message;
                        labMsCode.Text = "";
                        btnMsStart.Enabled = true;
                    }));
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void Confirm()
        {
            if (rbOffline.Checked)
            {
                string name = txtName.Text.Trim();
                if (name.Length < 1 || name.Length > 16)
                {
                    MessageBox.Show(this, "昵称需 1~16 个字符", "提示");
                    return;
                }
                Result = Auth.Offline(name);
                AccountStore.Save(Result);
            }
            else if (Result == null)
            {
                MessageBox.Show(this, rbMs.Checked ? "请先完成微软登录" : "请先点“登录”完成皮肤站登录", "提示");
                return;
            }
            _saved = true;
            DialogResult = DialogResult.OK;
            Close();
        }

        public bool Saved { get { return _saved; } }
    }
}
