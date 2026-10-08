using System;
using System.Drawing;
using System.Windows.Forms;

namespace HoverLex
{
    internal sealed class InputFeedback : Form
    {
        private readonly Label message;
        private readonly Timer expiry=new Timer();
        internal string Message { get { return message.Text; } }
        internal bool IsShown { get { return IsHandleCreated && IsWindowVisible(Handle); } }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var p=base.CreateParams; p.ExStyle|=0x08000088; return p; } }
        internal InputFeedback()
        {
            AutoScaleMode=AutoScaleMode.Dpi; AutoScaleDimensions=new SizeF(96,96);
            ClientSize=new Size(300,38); FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false;
            BackColor=Theme.Tint; Padding=new Padding(12,6,12,6);
            message=new Label { Dock=DockStyle.Fill,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Theme.Green,Font=Theme.Font(9),AccessibleName="输入助手状态" };
            Controls.Add(message); expiry.Tick+=delegate { Hide(); };
        }
        internal void Display(InputSnapshot source,string text,int milliseconds=0)
        {
            if(IsDisposed) return;
            expiry.Stop(); message.Text=text;
            Rectangle area=Screen.FromPoint(new Point(source.X,source.Y)).WorkingArea;
            Location=new Point(Math.Max(area.Left,Math.Min(source.X+12,area.Right-Width)),Math.Max(area.Top,Math.Min(source.Y+Math.Min(source.Height,60)+12,area.Bottom-Height)));
            IntPtr label=message.Handle; PerformLayout();
            Native.SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x0053);
            if(milliseconds>0) { expiry.Interval=milliseconds; expiry.Start(); }
        }
        public new void Hide() { expiry.Stop(); if(IsHandleCreated) Native.SetWindowPos(Handle,IntPtr.Zero,0,0,0,0,0x0097); base.Hide(); }
        protected override void Dispose(bool disposing) { if(disposing) expiry.Dispose(); base.Dispose(disposing); }
    }

    internal sealed class InputRecovery
    {
        private int failures;
        private long healthySince=-1;
        internal long RetryAt { get; private set; }
        internal int Failures { get { return failures; } }
        internal void Failed(long now) { failures=Math.Min(4,failures+1); healthySince=-1; RetryAt=now+(failures<=3 ? 1000L<<(failures-1) : 30000L); }
        internal void Healthy(long now) { if(healthySince<0) healthySince=now; if(now-healthySince>=5000) failures=0; }
        internal bool Due(long now,bool interacted) { return interacted || now>=RetryAt; }
        internal void Reset() { failures=0; healthySince=-1; RetryAt=0; }
    }
}
