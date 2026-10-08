using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace HoverLex
{
    public static class Theme
    {
        public static readonly Color Background = Color.FromArgb(247, 248, 245);
        public static readonly Color Ink = Color.FromArgb(29, 48, 42);
        public static readonly Color Muted = Color.FromArgb(113, 128, 119);
        public static readonly Color Green = Color.FromArgb(33, 106, 77);
        public static readonly Color Line = Color.FromArgb(228, 234, 226);
        public static readonly Color Tint = Color.FromArgb(232, 242, 235);
        public static readonly Color Sidebar = Color.FromArgb(24, 62, 51);
        public static readonly Color SideMuted = Color.FromArgb(161, 188, 174);
        public static readonly Color SideActive = Color.FromArgb(47, 88, 71);
        public static Font Font(float size, FontStyle style = FontStyle.Regular) { return new Font("Microsoft YaHei UI", size, style); }
        public static Label Label(string text, float size, Color color)
        {
            return new Label { Text = text, AutoSize = true, Font = Font(size), ForeColor = color, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 8) };
        }
        public static Button Button(string text, bool primary)
        {
            return new SoftButton { Text = text, Height = 42, Width = 108, Font = Font(9.5f), Cursor = Cursors.Hand, BackColor = primary ? Green : Color.White, ForeColor = primary ? Color.White : Ink, Margin = new Padding(0, 0, 10, 0), FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
        }
        public static Icon AppIcon()
        {
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("hoverlex-icon"))
            {
                if (source != null) using (Icon icon = new Icon(source, 64, 64)) return (Icon)icon.Clone();
            }
            return (Icon)SystemIcons.Application.Clone();
        }
        public static Image Mark()
        {
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("hoverlex-mark"))
            {
                if (source != null) using (Image image = Image.FromStream(source)) return new Bitmap(image);
            }
            using (Icon icon = AppIcon()) return icon.ToBitmap();
        }
        public static GraphicsPath Round(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            // 最小化和排版时可能出现零/负尺寸，GDI+ 不能绘制这种圆弧。
            if (rect.Width <= 0 || rect.Height <= 0 ||
                !Finite(rect.X) || !Finite(rect.Y) || !Finite(rect.Width) || !Finite(rect.Height) ||
                !Finite(rect.Right) || !Finite(rect.Bottom)) return path;
            float d = Math.Min(Finite(radius) ? Math.Max(0,radius) : 0,Math.Min(rect.Width,rect.Height)/2) * 2;
            if (d <= 0) { path.AddRectangle(rect); return path; }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90); path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
        }
        private static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
    }
    public sealed class SoftButton : Button
    {
        private bool hovered;
        public bool Navigation { get; set; }
        public bool PreserveInputFocus { get; set; }
        public SoftButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); FlatAppearance.BorderSize = 0; }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void WndProc(ref Message message)
        {
            if(PreserveInputFocus) {
                if(message.Msg==0x0021) { message.Result=new IntPtr(3); return; }
                if(message.Msg==0x0201 || message.Msg==0x0203 || message.Msg==0x0202) {
                    int packed=message.LParam.ToInt32(); Point point=new Point((short)(packed&0xffff),(short)((packed>>16)&0xffff));
                    MouseEventArgs mouse=new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0);
                    if(message.Msg==0x0202) { bool click=Capture && ClientRectangle.Contains(point); Capture=false; OnMouseUp(mouse); if(click && Enabled) OnClick(EventArgs.Empty); }
                    else if(Enabled) { Capture=true; OnMouseDown(mouse); }
                    return;
                }
            }
            base.WndProc(ref message);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            float scale = e.Graphics.DpiX / 96f; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? Theme.Background : Parent.BackColor);
            if (ClientSize.Width <= 3 || ClientSize.Height <= 3) return;
            Color fill = !Enabled ? Theme.Line : hovered ? ControlPaint.Light(BackColor, .10f) : BackColor;
            using (GraphicsPath path = Theme.Round(new RectangleF(1, 1, Width - 3, Height - 3), 8 * scale))
            using (Brush brush = new SolidBrush(fill))
            using (Pen border = new Pen(Navigation || BackColor == Theme.Green ? fill : Theme.Line)) { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(border, path); }
            Rectangle text = Navigation ? Rectangle.Inflate(ClientRectangle, -(int)(14 * scale), 0) : ClientRectangle;
            TextRenderer.DrawText(e.Graphics, Text, Font, text, Enabled ? ForeColor : Theme.Muted, (Navigation ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -7, -7));
        }
    }
    public sealed class SwitchBox : CheckBox
    {
        public SwitchBox() { SetStyle(ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); BackColor = Color.Transparent; Cursor = Cursors.Hand; }
        public override Size GetPreferredSize(Size proposedSize)
        {
            using(Graphics graphics=CreateGraphics()) {
                float scale = graphics.DpiX / 96f;
                Size text = TextRenderer.MeasureText(graphics, Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                return new Size(text.Width + (int)Math.Ceiling(42 * scale), Math.Max(text.Height + (int)Math.Ceiling(4 * scale),(int)Math.Ceiling(24 * scale)));
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            base.OnPaintBackground(e); float s = e.Graphics.DpiX / 96f; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF track = new RectangleF(1,(Height-16*s)/2,28*s,16*s);
            using (GraphicsPath path = Theme.Round(track,8*s))
            using (Brush fill = new SolidBrush(Enabled && Checked ? Theme.Green : Color.FromArgb(188,202,190))) e.Graphics.FillPath(fill,path);
            using (Brush knob = new SolidBrush(Color.White)) e.Graphics.FillEllipse(knob,track.X+(Checked ? 14 : 2)*s,track.Y+2*s,12*s,12*s);
            TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle((int)Math.Ceiling(36*s),0,Math.Max(0,Width-(int)Math.Ceiling(36*s)),Height),Enabled ? ForeColor : Theme.Muted,TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-1,-1));
        }
    }
    public sealed class Card : Panel
    {
        public Card() { BackColor = Color.White; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Theme.Background : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (ClientSize.Width <= 3 || ClientSize.Height <= 3) return;
            using (GraphicsPath path = Theme.Round(new RectangleF(1, 1, Width - 3, Height - 3), 12 * e.Graphics.DpiX / 96f))
            using (Brush brush = new SolidBrush(BackColor))
            using (Pen border = new Pen(Theme.Line)) { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(border, path); }
        }
    }
    public sealed class EmptyWords : Control
    {
        public EmptyWords() { BackColor = Color.White; SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Graphics g = e.Graphics; float s = g.DpiX / 96f; g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = Width / 2f, cy = Height / 2f - 32 * s;
            using (Brush tint = new SolidBrush(Theme.Tint)) g.FillEllipse(tint, cx - 34 * s, cy - 34 * s, 68 * s, 68 * s);
            using (Pen p = new Pen(Theme.Green, 2.3f * s))
            {
                g.DrawLines(p, new PointF[] { new PointF(cx - 19*s,cy - 13*s),new PointF(cx - 5*s,cy - 10*s),new PointF(cx,cy - 6*s),new PointF(cx + 5*s,cy - 10*s),new PointF(cx + 19*s,cy - 13*s),new PointF(cx + 19*s,cy + 14*s),new PointF(cx + 5*s,cy + 17*s),new PointF(cx,cy + 21*s),new PointF(cx - 5*s,cy + 17*s),new PointF(cx - 19*s,cy + 14*s),new PointF(cx - 19*s,cy - 13*s) });
                g.DrawLine(p, cx,cy - 6*s,cx,cy + 21*s);
            }
            using (Font title = Theme.Font(12, FontStyle.Bold)) TextRenderer.DrawText(g, "把读过的好词，留在这里", title, new Rectangle(0,(int)(cy+43*s),Width,(int)(29*s)), Theme.Ink, TextFormatFlags.HorizontalCenter);
            using (Font sub = Theme.Font(9)) TextRenderer.DrawText(g, "查词后选择一个词义，点击「收藏词义」", sub, new Rectangle(0,(int)(cy+77*s),Width,(int)(25*s)), Theme.Muted, TextFormatFlags.HorizontalCenter);
        }
    }
    public sealed class MeaningList : ListBox
    {
        public MeaningList() { DrawMode = DrawMode.OwnerDrawVariable; BorderStyle = BorderStyle.None; IntegralHeight = false; BackColor = Color.White; ForeColor = Theme.Ink; }
        protected override void OnMeasureItem(MeasureItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            Size text = TextRenderer.MeasureText(Convert.ToString(Items[e.Index]), Font, new Size(Math.Max(30, ClientSize.Width - 24), 2000), TextFormatFlags.WordBreak);
            e.ItemHeight = Math.Max((int)(40 * e.Graphics.DpiX / 96f), text.Height + (int)(16 * e.Graphics.DpiX / 96f));
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (Brush brush = new SolidBrush(selected ? Theme.Tint : Color.White)) e.Graphics.FillRectangle(brush, e.Bounds);
            int gap = (int)(10 * e.Graphics.DpiX / 96f);
            Rectangle rect = Rectangle.Inflate(e.Bounds, -gap, -gap / 2);
            TextRenderer.DrawText(e.Graphics, Convert.ToString(Items[e.Index]), Font, rect, selected ? Theme.Green : Theme.Ink, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }
    }
    public sealed class WelcomeWindow : Form
    {
        public readonly Label Message;
        public readonly Button Done;
        private readonly PictureBox mark;
        public WelcomeWindow(string title, string description, bool installer)
        {
            Text = title; ClientSize = new Size(480, installer ? 325 : 245); StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; BackColor = Theme.Background; Font = Theme.Font(10); Icon = Theme.AppIcon();
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96,96);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(32,24,32,24), ColumnCount = 2, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,76)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,72)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,36)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
            mark = new PictureBox { Image = Theme.Mark(), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Margin = new Padding(0,0,12,8) };
            Label name = Theme.Label("HoverLex",24,Theme.Ink); name.Font = Theme.Font(24,FontStyle.Bold); name.Dock = DockStyle.Fill; name.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(mark,0,0); layout.Controls.Add(name,1,0);
            Label subtitle = Theme.Label(description,10,Theme.Muted); subtitle.Dock = DockStyle.Fill; layout.Controls.Add(subtitle,0,1); layout.SetColumnSpan(subtitle,2);
            Message = Theme.Label("正在准备…",10,Theme.Green); Message.AutoSize = false; Message.Dock = DockStyle.Fill; Message.Padding = new Padding(0,10,0,0); layout.Controls.Add(Message,0,2); layout.SetColumnSpan(Message,2);
            Done = Theme.Button("完成",true); Done.Dock = DockStyle.Fill; Done.Visible = installer; Done.Enabled = false;
            if (installer) { layout.Controls.Add(Done,0,3); layout.SetColumnSpan(Done,2); }
            else { ProgressBar bar = new ProgressBar { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Top, Height = 5, Margin = new Padding(0,14,0,0) }; layout.Controls.Add(bar,0,3); layout.SetColumnSpan(bar,2); }
            Controls.Add(layout); AutoScaleDimensions = new SizeF(96,96); PerformAutoScale();
            float scale = CurrentAutoScaleDimensions.Width / 96f; ClientSize = new Size((int)(480*scale),(int)((installer?325:245)*scale));
        }
        protected override void Dispose(bool disposing) { if (disposing && mark.Image != null) { mark.Image.Dispose(); mark.Image = null; } base.Dispose(disposing); }
    }
}
