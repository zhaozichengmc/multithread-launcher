using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PclDlGui
{
    /// <summary>
    /// 圆角按钮：自绘 + 抗锯齿，带悬停 / 按下反馈
    /// </summary>
    public class RoundButton : Button
    {
        public int CornerRadius = 8;

        /// <summary>基准字号（pt）。0 = 按按钮高度自动取</summary>
        public float BaseFontPt = 0;
        /// <summary>文字与边框的最小留白（px）</summary>
        public int TextPadding = 10;

        private Font _fitFont;
        private string _fitKey;

        public Color NormalBack = Color.FromArgb(248, 249, 251);
        public Color HoverBack = Color.FromArgb(238, 243, 250);
        public Color PressBack = Color.FromArgb(226, 235, 247);
        public Color DisabledBack = Color.FromArgb(243, 243, 243);

        public Color NormalBorder = Color.FromArgb(214, 217, 222);
        public Color HoverBorder = Color.FromArgb(0, 120, 212);
        public Color DisabledBorder = Color.FromArgb(226, 226, 226);

        public Color TextColor = Color.FromArgb(26, 26, 26);
        public Color DisabledTextColor = Color.FromArgb(166, 166, 166);

        private bool _hover;
        private bool _down;

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = new Font("Microsoft YaHei UI", 9F);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _down = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (mevent.Button == MouseButtons.Left) { _down = true; Invalidate(); }
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            _down = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            _hover = false; _down = false;
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // 先铺父容器底色，避免圆角外露出黑边
            Color parent = Parent != null ? Parent.BackColor : SystemColors.Control;
            using (var bg = new SolidBrush(parent))
                g.FillRectangle(bg, ClientRectangle);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);

            Color back, border, txt;
            if (!Enabled)
            {
                back = DisabledBack; border = DisabledBorder; txt = DisabledTextColor;
            }
            else if (_down)
            {
                back = PressBack; border = HoverBorder; txt = TextColor;
            }
            else if (_hover)
            {
                back = HoverBack; border = HoverBorder; txt = TextColor;
            }
            else
            {
                back = NormalBack; border = NormalBorder; txt = TextColor;
            }

            using (var path = RoundRect(r, CornerRadius))
            {
                using (var fill = new SolidBrush(back))
                    g.FillPath(fill, path);
                using (var pen = new Pen(border, 1f))
                    g.DrawPath(pen, path);
            }

            // 字号：按高度尽量放大，再按宽度收窄，保证不越界
            Font f = GetFittedFont(g);
            const TextFormatFlags measureFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            Size ts = TextRenderer.MeasureText(g, Text, f, new Size(int.MaxValue, int.MaxValue), measureFlags);

            int tx = (Width - ts.Width) / 2;
            int ty = (Height - ts.Height) / 2;
            if (ty < 0) ty = 0;
            TextRenderer.DrawText(g, Text, f, new Rectangle(tx, ty, ts.Width, ts.Height), txt,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        private Font GetFittedFont(IDeviceContext dc)
        {
            string family = Font.FontFamily.Name;
            string key = family + "|" + Text + "|" + Width + "|" + Height + "|" + BaseFontPt;
            if (_fitKey == key && _fitFont != null) return _fitFont;

            if (_fitFont != null) { _fitFont.Dispose(); _fitFont = null; }

            float pt = BaseFontPt > 0
                ? BaseFontPt
                : (Height >= 32 ? 12.5f : (Height >= 27 ? 11f : 10f));

            const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            Font f = new Font(family, pt, FontStyle.Regular, GraphicsUnit.Point);
            int maxW = Math.Max(20, Width - TextPadding * 2);
            while (true)
            {
                Size ts = TextRenderer.MeasureText(dc, Text, f, new Size(int.MaxValue, int.MaxValue), flags);
                if (ts.Width <= maxW || pt <= 6.5f) break;
                pt -= 0.5f;
                f.Dispose();
                f = new Font(family, pt, FontStyle.Regular, GraphicsUnit.Point);
            }

            _fitFont = f;
            _fitKey = key;
            return f;
        }

        protected override void OnResize(EventArgs e)
        {
            _fitKey = null;
            base.OnResize(e);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            _fitKey = null;
            base.OnTextChanged(e);
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = Math.Max(1, radius * 2);
            var path = new GraphicsPath();
            if (radius <= 1)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
