using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace HoverLex
{
    public static class Native
    {
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern bool IsChild(IntPtr parent,IntPtr child);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr value, string text);
        public static bool Down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
        public static bool CtrlOnly()
        {
            if (!Down(0x11)) return false;
            for (int k = 1; k < 255; k++)
            {
                if (k == 0x11 || k == 0xa2 || k == 0xa3) continue;
                if (Down(k)) return false;
            }
            return true;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            if(args.Length>0 && args[0]=="--input-watch") { InputReader.Watch(); return; }
            if(args.Length>0 && args[0]=="--replace-input") { InputReader.ReplaceCommand(); return; }
            if(args.Length>0 && args[0]=="--selection-probe") {
                InputSnapshot selected=args.Length>4 && args[4]=="copy" ? InputReader.CopyCompatibilitySelection(Int64.Parse(args[1]),Int32.Parse(args[2]),Int32.Parse(args[3])) : InputReader.ReadSelection(Int64.Parse(args[1]),Int32.Parse(args[2]),Int32.Parse(args[3]));
                using(StreamWriter writer=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false))) writer.Write(new JavaScriptSerializer().Serialize(selected));
                return;
            }
            if (args.Length > 0 && args[0] == "--probe")
            {
                CaptureResult result;
                try
                {
                    int x = Int32.Parse(args[1]), y = Int32.Parse(args[2]);
                    result = args[3] == "uia" ? Task.Run(() => ScreenReader.AutomationOnly(x, y)).GetAwaiter().GetResult() : new CaptureResult { Error = "不支持的取词模式" };
                }
                catch (Exception e) { result = new CaptureResult { Error = e.Message }; }
                using (StreamWriter writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
                    writer.Write(new JavaScriptSerializer().Serialize(result));
                return;
            }
            Application.EnableVisualStyles();
            if(args.Length>1 && args[0]=="--capture-diagnostics") {
                using(StreamWriter writer=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false))) writer.Write(CaptureDiagnostics.WindowInfo(Int64.Parse(args[1])));
                return;
            }
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--self-test") { Environment.Exit(SelfTest.Run()); return; }
            if(args.Length>0 && args[0]=="--whatsapp-test") { Environment.Exit(WhatsAppTests.Run(args.Length>1 && args[1]=="--prepared",args.Length>2 && args[2]=="--online")); return; }
            if(args.Length>1 && args[0]=="--whatsapp-readonly-test") { Environment.Exit(WhatsAppTests.Readonly(Int64.Parse(args[1]))); return; }
            if (args.Length > 0 && args[0] == "--render-test") { Environment.Exit(SelfTest.RenderTest()); return; }
            if(args.Length>0 && args[0]=="--learning-test") { Environment.Exit(LearningTests.Run()); return; }
            if(args.Length>0 && args[0]=="--quality-test") { Environment.Exit(QualityTests.Run(args.Length>1 && args[1]=="--online")); return; }
            if(args.Length>0 && args[0]=="--hover-translation-test") { Environment.Exit(HoverTranslationTests.Run(args.Length>1 && args[1]=="--online")); return; }
            if(args.Length>0 && args[0]=="--capture-compatibility-test") { Environment.Exit(CaptureCompatibilityTests.Run(args.Length>1 ? args[1] : "")); return; }
            if(args.Length>0 && args[0]=="--hover-runtime-test") { Environment.Exit(HoverRuntimeTests.Installed()); return; }
            if(args.Length>0 && args[0]=="--hover-controller-test") { Environment.Exit(HoverRuntimeTests.Controller()); return; }
            if(args.Length>0 && args[0]=="--translation-test") { Environment.Exit(TranslationTests.Run(args.Length>1 && args[1]=="--online")); return; }
            if(args.Length>0 && args[0]=="--input-adapter-test") { Environment.Exit(InputAdapterTests.Run()); return; }
            if(args.Length>0 && args[0]=="--wechat-adapter-test") { Environment.Exit(InputAdapterTests.Wechat()); return; }
            if(args.Length>0 && args[0]=="--speech-test") { Environment.Exit(SpeechTests.Run()); return; }
            if(args.Length>0 && args[0]=="--bidirectional-test") { Environment.Exit(BidirectionalTests.Run(args.Length>1 && args[1]=="--online")); return; }
            if(args.Length>0 && args[0]=="--correction-test") { Environment.Exit(CorrectionTests.Run(args.Length>1 && args[1]=="--online")); return; }
            if(args.Length>0 && args[0]=="--correction-recovery-test") { Environment.Exit(CorrectionRecoveryTests.Run(args.Length>1 && args[1]=="--online")); return; }
            if(args.Length>0 && args[0]=="--reliability-test") { Environment.Exit(ReliabilityTests.Run()); return; }
            if(args.Length>0 && args[0]=="--correction-toggle-test") { Environment.Exit(CorrectionTests.RunToggle(args.Length>1 && args[1]=="--online")); return; }
            if(args.Length>0 && args[0]=="--correction-toggle-ui-test") { Environment.Exit(TranslationTests.RunCorrectionToggle()); return; }
            if(args.Length>0 && args[0]=="--selection-test") { Environment.Exit(SelectionTests.Run(args.Length>1 && args[1]=="--online")); return; }
            if(args.Length>0 && args[0]=="--selection-adapter-test") { Environment.Exit(SelectionAdapterTests.Run(args.Length>1 ? args[1] : "")); return; }
            if(args.Length>1 && args[0]=="--wechat-selection-test") { Environment.Exit(WechatSelectionTests.Run(args[1],args.Length>2 && args[2]=="--online")); return; }
            if(args.Length>0 && args[0]=="--wechat-prepared-test") { Environment.Exit(WechatSelectionTests.Prepared()); return; }
            if (args.Length > 0 && args[0] == "--preview") { SelfTest.Preview(); return; }
            if (args.Length > 0 && args[0] == "--integration-test") { Environment.Exit(SelfTest.Integration()); return; }
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\HoverLex.Windows.Portable", out created))
            {
                if (!created)
                {
                    try { using (EventWaitHandle activate = EventWaitHandle.OpenExisting("Local\\HoverLex.Activate")) activate.Set(); }
                    catch { MessageBox.Show("HoverLex 已经运行，可从右下角托盘打开。", "HoverLex"); }
                    return;
                }
                try
                {
                    using (EventWaitHandle activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\HoverLex.Activate"))
                    using (MainForm form = new MainForm())
                    {
                        RegisteredWaitHandle listener = ThreadPool.RegisterWaitForSingleObject(activate, delegate { if (form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke(new Action(form.OpenMain)); }, null, Timeout.Infinite, false);
                        try { Application.Run(form); } finally { listener.Unregister(null); }
                    }
                }
                catch (Exception e) { MessageBox.Show("启动失败：" + e.Message, "HoverLex"); }
            }
        }
    }

    public sealed class LookupPanel : Form
    {
        internal Action<string> FocusTrace;
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
        private Label heading, phonetic, source, context;
        private ListBox meanings;
        private Button speak, save, close;
        private FlowLayoutPanel actions;
        private Entry entry;
        private CaptureResult capture;
        public Func<CaptureResult, Entry, string, bool> SaveWord;
        public Action<string> SpeakWord;
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; p.ClassStyle |= 0x00020000; return p; }
        }
        public LookupPanel()
        {
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); ClientSize = new Size(420, 370);
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            BackColor = Color.White; Padding = new Padding(24); Font = Theme.Font(10);
            heading = Theme.Label("curiosity", 25, Theme.Ink); heading.Font = new Font("Segoe UI", 25, FontStyle.Bold);
            phonetic = Theme.Label("", 10, Theme.Muted);
            source = Theme.Label("", 9, Theme.Green);
            source.AutoSize=false; source.AutoEllipsis=true;
            meanings = new MeaningList { BorderStyle = BorderStyle.None, Font = Theme.Font(10), ForeColor = Theme.Ink, BackColor = Color.White, Height = 116, IntegralHeight = false, HorizontalScrollbar = false };
            context = Theme.Label("", 9, Theme.Muted); context.AutoSize = false; context.Height = 54; context.Padding = new Padding(10,8,10,4); context.BackColor = Theme.Background;
            speak = Theme.Button("发音", false); save = Theme.Button("收藏词义", true); close = Theme.Button("关闭", false);
            actions = new FlowLayoutPanel { AutoSize = false, WrapContents = false, Margin = new Padding(0) };
            speak.Height = save.Height = close.Height = 38;
            actions.Controls.AddRange(new Control[] { speak, save, close });
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            Control[] controls = { heading, phonetic, source, meanings, context, actions };
            for (int i = 0; i < controls.Length; i++) { controls[i].Dock = DockStyle.Fill; layout.Controls.Add(controls[i], 0, i); }
            Controls.Add(layout);
            AutoScaleDimensions = new SizeF(96, 96); PerformAutoScale();
            close.Click += delegate { Hide(); };
            speak.Click += delegate { if (SpeakWord != null && capture != null) SpeakWord(capture.Word); };
            save.Click += delegate
            {
                if (SaveWord == null || entry == null) return;
                string chosen = meanings.SelectedItem as string;
                if (SaveWord(capture, entry, chosen ?? entry.Chinese)) save.Text = "已收藏";
            };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width <= 1 || ClientSize.Height <= 1) return;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (System.Drawing.Drawing2D.GraphicsPath path = Theme.Round(new RectangleF(0,0,Width-1,Height-1),16*e.Graphics.DpiX/96f))
            using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawPath(p,path);
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width < 2 || Height < 2) return;
            float scale; using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            using (System.Drawing.Drawing2D.GraphicsPath path = Theme.Round(new RectangleF(0,0,Width,Height),16*scale))
            { Region previous = Region; Region = new Region(path); if (previous != null) previous.Dispose(); }
        }
        public void SetResult(CaptureResult result, Entry found)
        {
            capture = result; entry = found; heading.Text = result.Word;
            phonetic.Text = found == null ? "" : (String.IsNullOrWhiteSpace(found.Phonetic) ? "" : "/" + found.Phonetic + "/");
            source.Text = result.Method + (found != null && !found.Word.Equals(result.Word, StringComparison.OrdinalIgnoreCase) ? "  ·  原形 " + found.Word : "") + "  ·  离线词典";
            meanings.Items.Clear();
            string meaning = found == null ? "本地词典未收录此词，可在主窗口检查拼写。" : (String.IsNullOrWhiteSpace(found.Chinese) ? found.English : found.Chinese);
            foreach (string line in meaning.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)) meanings.Items.Add(line.Trim());
            if (meanings.Items.Count > 0) meanings.SelectedIndex = 0;
            context.Text = String.IsNullOrWhiteSpace(result.Context) ? "选择符合原文的词义，再点收藏。" : "原文  " + result.Context.Replace("\r", " ").Replace("\n", " ");
            save.Enabled = found != null; save.Text = "收藏词义"; speak.Enabled = result.Word.Length>0;
        }
        public void SetFailure(string message)
        {
            capture=null; entry=null; heading.Text="未读到英文"; phonetic.Text=""; source.Text="界面取词";
            meanings.Items.Clear(); meanings.Items.Add(message);
            context.Text="指向正文中的英文并按住 Ctrl；也可选中文字后轻按 Ctrl 翻译。";
            save.Enabled=false; speak.Enabled=false; save.Text="收藏词义";
        }
        public void SetProviderState(string caption,string error)
        {
            source.Text=caption;
            if(!String.IsNullOrWhiteSpace(error)) context.Text=error;
        }
        public void ShowAt(Point point)
        {
            IntPtr focus=GetFocus(),foreground=GetForegroundWindow();
            bool restore=focus!=IntPtr.Zero && (focus==foreground || Native.IsChild(foreground,focus));
            Rectangle area = Screen.FromPoint(point).WorkingArea;
            Location = new Point(Math.Max(area.Left, Math.Min(point.X + 20, area.Right - Width)), Math.Max(area.Top, Math.Min(point.Y + 24, area.Bottom - Height)));
            Show();
            KeepOnTop();
            if(FocusTrace!=null) FocusTrace("before="+focus+" foreground="+foreground+" current="+GetFocus()+" currentForeground="+GetForegroundWindow()+" restore="+restore);
            // Form.Show 会选择卡片控件；仅恢复本线程原先的阅读焦点。
            if(foreground!=IntPtr.Zero && GetForegroundWindow()==Handle) SetForegroundWindow(foreground);
            if(restore && GetForegroundWindow()==foreground && (GetFocus()==Handle || Native.IsChild(Handle,GetFocus()))) SetFocus(focus);
            KeepOnTop();
            if(FocusTrace!=null) FocusTrace("after="+GetFocus());
        }
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) KeepOnTop();
        }
        private void KeepOnTop()
        {
            if (!IsHandleCreated || !Visible) return;
            // 每次显示都重新置顶，同时保留阅读窗口的键盘焦点。
            if(!TopMost) TopMost = true;
            Native.SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x0001 | 0x0002 | 0x0010 | 0x0040 | 0x0200);
        }
    }

    public sealed partial class MainForm : Form
    {
        private readonly string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        private readonly string userDir;
        private readonly Settings settings;
        private LocalDictionary dictionary;
        private readonly HoverGate gate = new HoverGate();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly LookupPanel popup = new LookupPanel();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private NotifyIcon tray;
        private Label status, count, savedCount;
        private EmptyWords emptyState;
        private PictureBox brandMark;
        private ImageList rowImages;
        private TextBox search;
        private SearchTranslation searchTranslation;
        private CheckBox enabled;
        private CheckBox inputTranslation;
        private CheckBox autoReplace;
        private CheckBox englishCorrection;
        private Label translationStatus;
        private Button recoverInput;
        private readonly ToolTip helpTips=new ToolTip { InitialDelay=450,ReshowDelay=100,AutoPopDelay=10000 };
        private TranslationController translation;
        private SelectionTranslationController selectionTranslation;
        private NumericUpDown delay;
        private List<SavedWord> saved;
        private ListView savedList;
        private SpeechSynthesizer speech;
        private bool busy, quitting, previewMode;
        private long hideAt;
        private List<int> registered = new List<int>();
        private bool resourcesDisposed;
        public MainForm() : this(false) { }
        public MainForm(bool preview) : this(preview,null) { }
        internal MainForm(bool preview, List<SavedWord> previewWords)
        {
            previewMode = preview;
            string installedRoot = Environment.GetEnvironmentVariable("HOVERLEX_INSTALL_ROOT");
            userDir = Path.Combine(String.IsNullOrWhiteSpace(installedRoot) ? baseDir : Path.GetFullPath(installedRoot), "UserData");
            if (!preview) Directory.CreateDirectory(userDir);
            settings = preview ? new Settings() : Settings.Load(Path.Combine(userDir, "settings.json"));
            try { saved = preview ? (previewWords ?? new List<SavedWord>()) : Storage.LoadWords(Path.Combine(userDir, "words.json")); }
            catch (Exception e) { throw new InvalidDataException("生词本读取失败，原文件已保留：" + e.Message); }
            Vocabulary.EnsureIds(saved);
            if(!preview) TranslationPreferences.Configure(settings.TranslationProvider,settings.DeepSeekModel,ApiKeyStore.Load(userDir));
            Text = "HoverLex " + AppVersion.Current + " · 鼠标取词"; Size = new Size(840, 640); MinimumSize = new Size(760, 580);
            StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            BackColor = Theme.Background; Font = Theme.Font(10); Icon = Theme.AppIcon();
            BuildLayout();
            AutoScaleDimensions = new SizeF(96, 96); PerformAutoScale();
            float dpi = CurrentAutoScaleDimensions.Width / 96f;
            rowImages.ImageSize = new Size(1,(int)(44*dpi));
            using (Bitmap spacer = new Bitmap(1,(int)(44*dpi))) rowImages.Images.Add(spacer);
            ClientSize = new Size((int)(960 * dpi), (int)(800 * dpi));
            MinimumSize = new Size((int)(880 * dpi), (int)(760 * dpi));
            popup.SaveWord = SaveWord; popup.SpeakWord = Speak;
            popup.VisibleChanged+=delegate { if(!popup.Visible) CancelHoverLookup(); };
            searchTranslation=new SearchTranslation(search,message=> { if(!IsDisposed) status.Text=message; },word=>dictionary==null ? null : dictionary.Lookup(word),word=>ShowEntry(word,"手动查词",""));
            searchTranslation.EnglishCorrection=settings.EnglishCorrection;
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!quitting && !preview) { e.Cancel = true; searchTranslation.Dismiss(); Hide(); popup.Hide(); }
            };
            if (!preview)
            {
                translation=new TranslationController(SetInputStatus);
                translation.ExcludeFocus=focus=>focus==Handle.ToInt64() || Native.IsChild(Handle,new IntPtr(focus));
                if(search.IsHandleCreated) translation.ExcludedFocusHandle=search.Handle.ToInt64();
                translation.AutoReplace=settings.AutoReplaceTranslation;
                translation.EnglishCorrection=settings.EnglishCorrection;
                selectionTranslation=new SelectionTranslationController(message=> { if(!IsDisposed) status.Text=message; });
                selectionTranslation.PointLookup=LookupTappedPoint;
                selectionTranslation.EnglishCorrection=settings.EnglishCorrection;
                selectionTranslation.Enabled=settings.Enabled;
                if(settings.InputTranslation && !translation.SetEnabled(true)) inputTranslation.Checked=false;
                BuildTray();
                reviewTimer.Interval=60000; reviewTimer.Tick+=delegate { RefreshWords(); CheckReviewReminder(); }; reviewTimer.Start();
                timer.Interval = 40; timer.Tick += OnTick; timer.Start();
                Shown += async delegate { await LoadDictionary(); CheckReviewReminder(); if(settings.EnglishCorrection && settings.TranslationProvider!="deepseek") await EnglishProofreader.WarmupAsync(); };
            }
            else { status.Text = "就绪 · 鼠标指向单词，按住 Ctrl"; count.Text = "770,611 条离线词条"; Controls.Find("lookup", true)[0].Enabled = true; }
        }
        private void BuildLayout()
        {
            TableLayoutPanel shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176)); shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Panel sidebar = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar, Padding = new Padding(18, 28, 18, 24), Margin = new Padding(0) };
            TableLayoutPanel side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
            side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach (float height in new float[] { 48, 28, 42, 50, 50 }) side.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            side.RowStyles.Add(new RowStyle(SizeType.Percent,100)); side.RowStyles.Add(new RowStyle(SizeType.Absolute,58));
            TableLayoutPanel identity = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            identity.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,39)); identity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); identity.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            brandMark = new PictureBox { Image = Theme.Mark(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(32,32), Anchor = AnchorStyles.Left, Margin = new Padding(0) };
            Label brand = Theme.Label("HoverLex",15,Color.White); brand.Font = new Font("Segoe UI",15,FontStyle.Bold); brand.AutoSize = false; brand.Dock = DockStyle.Fill; brand.TextAlign = ContentAlignment.MiddleLeft; brand.Margin = new Padding(0);
            identity.Controls.Add(brandMark,0,0); identity.Controls.Add(brand,1,0); side.Controls.Add(identity,0,0);
            side.Controls.Add(Theme.Label("读到哪里，查到哪里",8.5f,Theme.SideMuted),0,1);
            Label navigation = Theme.Label("阅读工具",8,Theme.SideMuted); navigation.Padding = new Padding(10,16,0,0); side.Controls.Add(navigation,0,2);
            Button library = Theme.Button("查词与生词本",true); ((SoftButton)library).Navigation = true; library.BackColor = Theme.SideActive; library.Dock = DockStyle.Top; library.Margin = new Padding(0,0,0,6); side.Controls.Add(library,0,3);
            Button helpButton = Theme.Button("使用指南",false); ((SoftButton)helpButton).Navigation = true; helpButton.BackColor = Theme.Sidebar; helpButton.ForeColor = Theme.SideMuted; helpButton.Name = "helpButton"; helpButton.Dock = DockStyle.Top; helpButton.Margin = new Padding(0); side.Controls.Add(helpButton,0,4);
            Label footer = Theme.Label("离线词典 · 本机取词\nHoverLex  " + AppVersion.Current,8,Theme.SideMuted); footer.Dock = DockStyle.Fill; footer.TextAlign = ContentAlignment.BottomLeft; side.Controls.Add(footer,0,6);
            sidebar.Controls.Add(side); shell.Controls.Add(sidebar,0,0);
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(30,28,30,16), ColumnCount = 1, RowCount = 6, Margin = new Padding(0) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,76)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,68)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,110)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,134)); root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,32));
            FlowLayoutPanel title = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            Label heading = Theme.Label("查词，读懂每一个词。",20,Theme.Ink); heading.Font = Theme.Font(20,FontStyle.Bold); heading.Margin = new Padding(0,0,0,6);
            title.Controls.Add(heading); title.Controls.Add(Theme.Label("随手查词，收藏值得记住的表达。",9,Theme.Muted)); root.Controls.Add(title,0,0);
            TableLayoutPanel searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0,0,0,14) };
            searchRow.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,94)); searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,108));
            Card input = new Card { Dock = DockStyle.Fill, Padding = new Padding(16,12,12,8), Margin = new Padding(0,0,10,0) };
            search = new TextBox { Name = "searchInput", Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI",12), ForeColor = Theme.Ink, AccessibleName = "输入中文或英文，支持单词和句子",MaxLength=2000 };
            search.HandleCreated += delegate { Native.SendMessage(search.Handle,0x1501,new IntPtr(1),"输入中文或英文，支持单词和句子…"); if(translation!=null) translation.ExcludedFocusHandle=search.Handle.ToInt64(); };
            input.Controls.Add(search);
            Button lookup = Theme.Button("翻译 / 查词",true); lookup.Dock = DockStyle.Fill; lookup.Name = "lookup";
            Button demo = Theme.Button("体验取词",false); demo.Dock = DockStyle.Fill; demo.Margin = new Padding(0);
            searchRow.Controls.Add(input,0,0); searchRow.Controls.Add(lookup,1,0); searchRow.Controls.Add(demo,2,0);
            lookup.Click += delegate { ManualLookup(); }; search.KeyDown += delegate(object sender,KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ManualLookup(); } };
            demo.Click += delegate { ShowDemo(); }; root.Controls.Add(searchRow,0,1);
            Card guide = new Card { Dock = DockStyle.Fill, BackColor = Theme.Tint, Padding = new Padding(18,12,18,10), Margin = new Padding(0,0,0,18) };
            TableLayoutPanel guideFlow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            guideFlow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); guideFlow.RowStyles.Add(new RowStyle(SizeType.Absolute,24)); guideFlow.RowStyles.Add(new RowStyle(SizeType.Absolute,22)); guideFlow.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            Label action = Theme.Label("Ctrl + 鼠标停留   ·   即刻查看释义",10,Theme.Green); action.Font = Theme.Font(10,FontStyle.Bold); guideFlow.Controls.Add(action,0,0);
            guideFlow.Controls.Add(Theme.Label("按住 Ctrl 查词；选中句子，轻按 Ctrl：英文译中文，中文译英文。",8.5f,Theme.Muted),0,1);
            FlowLayoutPanel switches = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0,2,0,0) };
            enabled = new SwitchBox { Name="captureEnabled",Text = "启用取词", Checked = settings.Enabled, AutoSize = true, ForeColor = Theme.Green, Margin = new Padding(0,0,16,0), Font = Theme.Font(9) };
            delay = new NumericUpDown { Minimum = 150, Maximum = 1200, Increment = 50, Value = settings.Delay, Width = 65, Font = Theme.Font(9), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0,0,5,0) };
            switches.Controls.AddRange(new Control[] { enabled,Theme.Label("停留",9,Theme.Muted),delay,Theme.Label("毫秒",9,Theme.Muted) });
            guideFlow.Controls.Add(switches,0,2); guide.Controls.Add(guideFlow); root.Controls.Add(guide,0,2);
            enabled.CheckedChanged += delegate { UpdateSettings(); }; delay.ValueChanged += delegate { UpdateSettings(); };
            Card translationCard=new Card { Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(18,10,18,8),Margin=new Padding(0,0,0,14) };
            TableLayoutPanel translationLayout=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=4,Margin=new Padding(0) };
            translationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); translationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,98));
            foreach(float rowHeight in new float[] { 28,24,26,28 }) translationLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,rowHeight));
            inputTranslation=new SwitchBox { Name="inputTranslation",Text="输入助手",AccessibleName="输入助手总开关",Checked=settings.InputTranslation,AutoSize=true,Font=Theme.Font(10,FontStyle.Bold),ForeColor=Theme.Green,Margin=new Padding(0) };
            englishCorrection=new SwitchBox { Name="englishCorrection",Text="英文纠错",AccessibleName="英文纠错开关",Checked=settings.EnglishCorrection,AutoSize=true,Font=Theme.Font(10,FontStyle.Bold),ForeColor=Theme.Green,Margin=new Padding(20,0,0,0) };
            FlowLayoutPanel translationSwitches=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false,Margin=new Padding(0) };
            translationSwitches.Controls.AddRange(new Control[] { inputTranslation,englishCorrection });
            FlowLayoutPanel translationModes=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false,Margin=new Padding(0) };
            autoReplace=new CheckBox { Name="autoReplaceTranslation",Text="自动替换输入",Checked=settings.AutoReplaceTranslation,AutoSize=true,Font=Theme.Font(8.5f),ForeColor=Theme.Ink,Margin=new Padding(0,0,12,0) };
            onlineNote=Theme.Label(settings.TranslationProvider=="deepseek" ? "DeepSeek 翻译与纠错" : "MyMemory 翻译 · 本机纠错",8.5f,Theme.Muted); onlineNote.Margin=new Padding(0,1,0,0);
            translationModes.Controls.AddRange(new Control[] { autoReplace,onlineNote });
            translationStatus=Theme.Label("输入助手已关闭 · 手动查词与翻译仍可用",8.5f,Theme.Muted); translationStatus.Name="inputStatus"; translationStatus.AccessibleName="输入助手状态"; translationStatus.AutoSize=false; translationStatus.AutoEllipsis=true; translationStatus.Dock=DockStyle.Fill;
            Button translationDemo=Theme.Button("翻译练习",false); translationDemo.Dock=DockStyle.Fill; translationDemo.Margin=new Padding(6,0,0,0); translationDemo.Click+=delegate { ShowTranslationDemo(); };
            translationLayout.Controls.Add(translationSwitches,0,0); translationLayout.Controls.Add(translationModes,0,1); translationLayout.Controls.Add(translationStatus,0,2);
            Button translationSettings=Theme.Button("翻译设置",false); translationSettings.Name="translationSettings"; translationSettings.Dock=DockStyle.Fill; translationSettings.Font=Theme.Font(9); translationSettings.Margin=new Padding(6,2,0,0); translationSettings.Click+=delegate { ConfigureTranslation(); };
            translationLayout.Controls.Add(translationSettings,1,2);
            recoverInput=Theme.Button("恢复检测",false); recoverInput.Name="recoverInput"; recoverInput.Dock=DockStyle.Fill; recoverInput.Font=Theme.Font(8.5f); recoverInput.Margin=new Padding(6,2,0,0); recoverInput.Enabled=settings.InputTranslation;
            recoverInput.Click+=delegate { if(translation!=null) translation.Recover(); }; translationLayout.Controls.Add(recoverInput,1,3);
            Label translationHint=Theme.Label("开启后：中文转英文，英文只纠错 · 默认 Tab 确认替换",8.5f,Theme.Muted); translationHint.Dock=DockStyle.Fill; translationLayout.Controls.Add(translationHint,0,3);
            helpTips.SetToolTip(inputTranslation,"自动检测当前输入框的中文和英文。Ctrl + Alt + T 开启或关闭；关闭后手动翻译仍可使用。");
            helpTips.SetToolTip(englishCorrection,"控制英文语法与拼写检查。输入框内自动检查需要同时开启“输入助手”。");
            helpTips.SetToolTip(autoReplace,"开启后，结果会直接替换输入框内容，但不会发送消息。关闭时由你按 Tab 或点击“替换”确认。");
            helpTips.SetToolTip(recoverInput,"重新启动输入检测，不改变生词、设置或当前输入文字。");
            translationLayout.Controls.Add(translationDemo,1,0); translationLayout.SetRowSpan(translationDemo,2); translationCard.Controls.Add(translationLayout); root.Controls.Add(translationCard,0,3);
            inputTranslation.CheckedChanged+=delegate { UpdateTranslation(); };
            englishCorrection.CheckedChanged+=delegate { UpdateEnglishCorrection(); };
            autoReplace.CheckedChanged+=delegate { if(translation==null || previewMode) return; translation.AutoReplace=autoReplace.Checked; settings.AutoReplaceTranslation=autoReplace.Checked; try { settings.Save(Path.Combine(userDir,"settings.json")); } catch(Exception error) { translationStatus.Text="设置保存失败："+error.Message; } };
            Card notebook = new Card { Dock = DockStyle.Fill, Padding = new Padding(20,18,20,14), Margin = new Padding(0) };
            TableLayoutPanel notebookLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            notebookLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); notebookLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,42)); notebookLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,78)); notebookLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            TableLayoutPanel toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));
            savedCount = Theme.Label("我的生词本",12,Theme.Ink); savedCount.Font = Theme.Font(12,FontStyle.Bold); savedCount.Dock = DockStyle.Fill; savedCount.TextAlign = ContentAlignment.MiddleLeft;
            Button export = Theme.Button("导出 CSV",false); export.Font = Theme.Font(9); export.Dock = DockStyle.Fill; export.Margin = new Padding(0,0,0,8); export.Click += delegate { ExportWords(); };
            toolbar.Controls.Add(savedCount,0,0); toolbar.Controls.Add(export,1,0); notebookLayout.Controls.Add(toolbar,0,0);
            Control learningTools=BuildLearningTools(); notebookLayout.Controls.Add(learningTools,0,1);
            Panel body = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0) };
            savedList = new ListView { Name = "savedList", Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.None, ForeColor = Theme.Ink, Font = Theme.Font(10), OwnerDraw = true };
            savedList.Columns.Add("单词",145); savedList.Columns.Add("收藏的释义",330); savedList.Columns.Add("复习状态",135); savedList.ShowItemToolTips=true;
            rowImages = new ImageList { ImageSize = new Size(1,36), ColorDepth = ColorDepth.Depth32Bit }; savedList.SmallImageList = rowImages;
            savedList.DrawColumnHeader += delegate(object sender,DrawListViewColumnHeaderEventArgs e) { using (Brush b = new SolidBrush(Theme.Background)) e.Graphics.FillRectangle(b,e.Bounds); using (Font f = Theme.Font(8.5f)) TextRenderer.DrawText(e.Graphics,e.Header.Text,f,Rectangle.Inflate(e.Bounds,-12,0),Theme.Muted,TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); };
            savedList.DrawItem += delegate(object sender,DrawListViewItemEventArgs e) { if (savedList.View != View.Details) e.DrawDefault = true; };
            savedList.DrawSubItem += delegate(object sender,DrawListViewSubItemEventArgs e) {
                Color row = e.Item.Selected ? Theme.Tint : e.ItemIndex % 2 == 0 ? Color.White : Color.FromArgb(250,251,249);
                using (Brush b = new SolidBrush(row)) e.Graphics.FillRectangle(b,e.Bounds);
                using (Font f = e.ColumnIndex == 0 ? new Font("Segoe UI",11,FontStyle.Bold) : Theme.Font(e.ColumnIndex == 2 ? 8.5f : 9.5f))
                    TextRenderer.DrawText(e.Graphics,e.SubItem.Text,f,Rectangle.Inflate(e.Bounds,-12,0),e.ColumnIndex == 0 ? Theme.Green : e.ColumnIndex == 2 ? Theme.Muted : Theme.Ink,TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawLine(p,e.Bounds.Left,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1);
            };
            savedList.Resize += delegate { float scale; using (Graphics g = savedList.CreateGraphics()) scale = g.DpiX / 96f; int available = savedList.ClientSize.Width; savedList.Columns[0].Width = (int)(122*scale); savedList.Columns[2].Width = (int)(160*scale); savedList.Columns[1].Width = Math.Max(80,available-savedList.Columns[0].Width-savedList.Columns[2].Width-8); };
            savedList.DoubleClick += delegate { if(savedList.SelectedItems.Count>0) { SavedWord selected=savedList.SelectedItems[0].Tag as SavedWord; if(selected!=null) ShowEntry(selected.Word,"生词本",selected.Context); } };
            emptyState = new EmptyWords { Dock = DockStyle.Fill };
            Panel helpText = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Visible = false };
            TableLayoutPanel shortcuts = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 10, Padding = new Padding(0,12,0,0) };
            shortcuts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,190)); shortcuts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            string[] keys = { "Ctrl + 鼠标停留", "选中句子 + Ctrl", "Ctrl + Alt + T", "Ctrl + Alt + H", "Ctrl + Alt + D", "Ctrl + Alt + Q", "Esc" };
            string[] descriptions = { "在单词旁查看释义", "轻按 Ctrl：英文译中文，中文译英文", "开启 / 关闭输入助手", "暂停 / 恢复取词和划词翻译", "打开主窗口", "退出软件", "收起取词和翻译浮窗" };
            for (int i=0;i<keys.Length;i++) {
                shortcuts.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
                Label key = Theme.Label(keys[i],9,Theme.Green); key.Font = new Font("Segoe UI",9,FontStyle.Bold); key.Dock = DockStyle.Fill; key.TextAlign = ContentAlignment.MiddleLeft;
                Label description = Theme.Label(descriptions[i],9,Theme.Ink); description.Dock = DockStyle.Fill; description.TextAlign = ContentAlignment.MiddleLeft;
                shortcuts.Controls.Add(key,0,i); shortcuts.Controls.Add(description,1,i);
            }
            shortcuts.RowStyles.Add(new RowStyle(SizeType.Absolute,20)); shortcuts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label guideText = Theme.Label("关闭窗口后，软件继续在托盘运行，并可提醒复习。\n免费模式：本机查词与纠错，MyMemory 在线翻译。\nDeepSeek：翻译、纠错及取词的单词和附近原文会联网处理。",8.5f,Theme.Muted); guideText.Dock = DockStyle.Fill; shortcuts.Controls.Add(guideText,0,9); shortcuts.SetColumnSpan(guideText,2); helpText.Controls.Add(shortcuts);
            body.Controls.Add(savedList); body.Controls.Add(emptyState); body.Controls.Add(helpText);
            library.Click += delegate { showingHelp=false; helpText.Visible = false; learningTools.Visible=true; export.Visible = true; savedList.Visible = true; RefreshWords(); library.BackColor = Theme.SideActive; library.ForeColor = Color.White; helpButton.BackColor = Theme.Sidebar; helpButton.ForeColor = Theme.SideMuted; };
            helpButton.Click += delegate { showingHelp=true; savedList.Visible = emptyState.Visible = false; learningTools.Visible=false; helpText.Visible = true; helpText.BringToFront(); savedCount.Text = "使用指南"; export.Visible = false; helpButton.BackColor = Theme.SideActive; helpButton.ForeColor = Color.White; library.BackColor = Theme.Sidebar; library.ForeColor = Theme.SideMuted; };
            notebookLayout.Controls.Add(body,0,2); notebook.Controls.Add(notebookLayout); root.Controls.Add(notebook,0,4);
            TableLayoutPanel bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0,10,0,0), Margin = new Padding(0) };
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,65)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,35));
            status = Theme.Label("正在准备词典…",8,Theme.Green); count = Theme.Label("",8,Theme.Muted); count.TextAlign = ContentAlignment.TopRight;
            status.AutoEllipsis = true; status.Dock = count.Dock = DockStyle.Fill; bottom.Controls.Add(status,0,0); bottom.Controls.Add(count,1,0); root.Controls.Add(bottom,0,5);
            shell.Controls.Add(root,1,0); Controls.Add(shell); RefreshWords();
        }
        private void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开 HoverLex", null, delegate { OpenMain(); });
            menu.Items.Add("暂停 / 恢复取词", null, delegate { enabled.Checked = !enabled.Checked; });
            ToolStripMenuItem translationToggle=new ToolStripMenuItem("输入助手（翻译与纠错）") { Checked=inputTranslation.Checked };
            translationToggle.Click+=delegate { inputTranslation.Checked=!inputTranslation.Checked; };
            inputTranslation.CheckedChanged+=delegate { translationToggle.Checked=inputTranslation.Checked; }; menu.Items.Add(translationToggle);
            menu.Items.Add("退出", null, delegate { Quit(); });
            tray = new NotifyIcon { Icon = Icon, Text = "HoverLex · Ctrl + 鼠标取词", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += delegate { OpenMain(); };
            tray.BalloonTipClicked+=delegate { OpenMain(); StartReview(); };
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (previewMode) return;
            foreach (int id in new int[] { 1, 2, 3, 5 })
            {
                uint key = id == 1 ? (uint)Keys.H : id == 2 ? (uint)Keys.D : id == 3 ? (uint)Keys.Q : (uint)Keys.T;
                if (Native.RegisterHotKey(Handle, id, 0x4003, key)) registered.Add(id);
            }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312)
            {
                int id = m.WParam.ToInt32();
                if (id == 1) enabled.Checked = !enabled.Checked;
                if (id == 2) OpenMain();
                if (id == 3) Quit();
                if (id == 5) inputTranslation.Checked=!inputTranslation.Checked;
            }
            base.WndProc(ref m);
        }
        private async Task LoadDictionary()
        {
            try
            {
                dictionary = await Task.Run(() => new LocalDictionary(Path.Combine(baseDir, "Dictionary")));
                if (IsDisposed) { dictionary.Dispose(); return; }
                count.Text = dictionary.Count.ToString("N0") + " 条离线词条";
                Controls.Find("lookup", true)[0].Enabled = true;
                SetReadyStatus();
                string updateMessage = Environment.GetEnvironmentVariable("HOVERLEX_UPDATE_STATUS");
                if (!String.IsNullOrWhiteSpace(updateMessage)) status.Text = updateMessage;
                if (registered.Count < 5) status.Text = "部分快捷键被占用，可使用托盘菜单";
            }
            catch (Exception e) { status.Text = "词典未就绪"; MessageBox.Show("词典加载失败：" + e.Message + "\n请保留 Dictionary 文件夹，或运行 scripts/prepare_dictionary.py 重建。", "HoverLex"); }
        }
        private void SetReadyStatus() { status.Text = settings.Enabled ? "就绪 · 鼠标指向单词，按住 Ctrl" : "已暂停 · Ctrl + Alt + H 恢复"; }
        private void SetInputStatus(string message) {
            if(IsDisposed || resourcesDisposed) return;
            translationStatus.Text=message; helpTips.SetToolTip(translationStatus,message);
            if(tray!=null) { string text="HoverLex · "+message.Replace("\r"," ").Replace("\n"," "); tray.Text=text.Length>63 ? text.Substring(0,63) : text; }
        }
        private void UpdateTranslation()
        {
            if(previewMode || translation==null) return;
            bool requested=inputTranslation.Checked;
            if(!translation.SetEnabled(requested) && requested) { inputTranslation.Checked=false; return; }
            settings.InputTranslation=inputTranslation.Checked;
            recoverInput.Enabled=settings.InputTranslation;
            try { settings.Save(Path.Combine(userDir,"settings.json")); } catch(Exception error) { translationStatus.Text="设置保存失败："+error.Message; }
        }
        private void UpdateEnglishCorrection()
        {
            if(previewMode || searchTranslation==null) return;
            settings.EnglishCorrection=englishCorrection.Checked;
            DismissHoverLookup();
            searchTranslation.EnglishCorrection=settings.EnglishCorrection;
            if(translation!=null) translation.EnglishCorrection=settings.EnglishCorrection;
            if(selectionTranslation!=null) selectionTranslation.EnglishCorrection=settings.EnglishCorrection;
            if(translation==null || !translation.Enabled) SetInputStatus(settings.EnglishCorrection ? "英文纠错已开启 · 开启输入助手后自动检查" : "英文纠错已关闭 · 手动翻译仍可使用");
            try { settings.Save(Path.Combine(userDir,"settings.json")); } catch(Exception error) { translationStatus.Text="设置保存失败："+error.Message; }
        }
        private void ShowTranslationDemo()
        {
            Form demo=new Form { Text="输入翻译练习",Size=new Size(660,290),StartPosition=FormStartPosition.CenterParent,BackColor=Theme.Background,Font=Theme.Font(11),Padding=new Padding(24) };
            Label hint=Theme.Label("开启“输入助手”后，在这里输入中文或英文。\n可试：I has an apple.　出现建议后按 Tab 替换。",10,Theme.Green); hint.Dock=DockStyle.Top; hint.Height=50; hint.AutoSize=false;
            TextBox editor=new TextBox { Multiline=true,Dock=DockStyle.Fill,Font=Theme.Font(12),AccessibleName="中文输入翻译练习",ScrollBars=ScrollBars.Vertical };
            demo.Controls.Add(editor); demo.Controls.Add(hint); demo.Shown+=delegate { editor.Focus(); }; demo.Show(this);
        }
        private void UpdateSettings()
        {
            if (previewMode) return;
            settings.Enabled = enabled.Checked; settings.Delay = (int)delay.Value;
            DismissHoverLookup();
            if(selectionTranslation!=null) selectionTranslation.Enabled=settings.Enabled;
            try { settings.Save(Path.Combine(userDir, "settings.json")); SetReadyStatus(); }
            catch (Exception e) { status.Text = "设置保存失败：" + e.Message; }
        }
        private async void OnTick(object sender, EventArgs e)
        {
            Point point = Cursor.Position;
            bool ctrlOnly=Native.CtrlOnly();
            if (Native.Down((int)Keys.Escape)) { DismissHoverLookup(); if(selectionTranslation!=null) selectionTranslation.Dismiss(); return; }
            // WhatsApp 聊天气泡没有悬停文字接口；已有拖选时保留选区读取，等待松开 Ctrl。
            if(selectionTranslation!=null && selectionTranslation.WaitForWhatsAppSelection) { DismissHoverLookup(); return; }
            if(!ctrlOnly && selectionTranslation!=null && selectionTranslation.SuppressHover) { DismissHoverLookup(); return; }
            if (popup.Visible && clock.ElapsedMilliseconds > hideAt && !popup.Bounds.Contains(point)) popup.Hide();
            bool blocked = popup.Visible && popup.Bounds.Contains(point);
            if(hoverRequest!=null && (!popup.Visible || (hoverAnchor.HasValue && !blocked && (Math.Abs(point.X-hoverAnchor.Value.X)>3 || Math.Abs(point.Y-hoverAnchor.Value.Y)>3)))) {
                CancelHoverLookup();
                if(popup.Visible) popup.SetProviderState("DeepSeek 请求已取消",null);
            }
            if (!gate.Observe(point, ctrlOnly, settings.Enabled && dictionary != null, blocked, clock.ElapsedMilliseconds, settings.Delay, !busy)) return;
            // 达到停留时间即为单词查询，松键后不再追加选区翻译。
            if(selectionTranslation!=null) selectionTranslation.Dismiss();
            bool whatsapp=WhatsAppWindows.Host(ScreenReader.WindowFromPoint(point))!=IntPtr.Zero;
            long focus=InputReader.SelectionFocusWindow().ToInt64(),revision=selectionTranslation==null ? 0 : selectionTranslation.GestureRevision;
            int generation = gate.Generation;
            busy = true; CancelHoverLookup(); popup.Hide(); status.Text = "正在取词…";
            try
            {
                CaptureResult result = await Task.Run(() => Probe(point.X, point.Y, "uia", 2800));
                bool released=whatsapp && !Native.Down(0x11) && selectionTranslation!=null && revision==selectionTranslation.GestureRevision &&
                    focus==InputReader.SelectionFocusWindow().ToInt64() && Math.Abs(Cursor.Position.X-point.X)<=3 && Math.Abs(Cursor.Position.Y-point.Y)<=3;
                if ((!released && (generation!=gate.Generation || !Native.CtrlOnly())) || !settings.Enabled || IsDisposed) return;
                if (!String.IsNullOrWhiteSpace(result.Word))
                {
                    status.Text = result.Method + " · " + result.Word;
                    ShowLookup(result,point,true);
                }
                else {
                    status.Text = result.Error;
                    if (result.Method != "密码保护") {
                        popup.SetFailure(result.Error); popup.ShowAt(point); hideAt=clock.ElapsedMilliseconds+6000;
                        gate.Retry(generation,clock.ElapsedMilliseconds);
                    }
                }
            }
            catch (Exception error) { if (!IsDisposed) { status.Text = "取词失败：" + error.Message; gate.Retry(generation,clock.ElapsedMilliseconds); } }
            finally { busy = false; }
        }
        public static CaptureResult Probe(int x, int y, string mode, int timeout)
        {
            if (mode != "uia") return new CaptureResult { Error = "不支持的取词模式" };
            ProcessStartInfo info = new ProcessStartInfo(Application.ExecutablePath, "--probe " + x + " " + y + " " + mode)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (Process process = Process.Start(info))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeout))
                {
                    try { process.Kill(); process.WaitForExit(1000); } catch { }
                    return new CaptureResult { Error = "界面文字读取超时" };
                }
                string json = output.GetAwaiter().GetResult();
                if (String.IsNullOrWhiteSpace(json)) return new CaptureResult { Error = "取词进程未返回结果：" + error.GetAwaiter().GetResult() };
                return new JavaScriptSerializer().Deserialize<CaptureResult>(json);
            }
        }
        protected override bool ProcessCmdKey(ref Message message,Keys keyData) { if(keyData==Keys.Tab && searchTranslation!=null && searchTranslation.HandleTab()) return true; return base.ProcessCmdKey(ref message,keyData); }
        private async void ManualLookup()
        {
            if(previewMode) { status.Text="输入中文或英文，按回车翻译或查词"; return; }
            await searchTranslation.QueryAsync();
        }
        private void ShowEntry(string word, string method, string context)
        {
            if (dictionary == null) { status.Text = "词典正在加载，请稍候"; return; }
            gate.Reset();
            ShowLookup(new CaptureResult { Word = word, Method = method, Context = context },new Point(Right - popup.Width - 20, Top + 140),false);
        }
        private void ShowDemo()
        {
            Form demo = new Form { Text = "取词练习 · Ctrl + 鼠标", Size = new Size(720, 310), StartPosition = FormStartPosition.CenterParent, BackColor = Color.White, Font = Theme.Font(11), Padding = new Padding(30) };
            Label instruction = Theme.Label("按住 Ctrl 查词；选中句子，轻按 Ctrl 中英互译。", 11, Theme.Green); instruction.Dock = DockStyle.Top;
            TextBox passage = new TextBox { ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new Font("Segoe UI", 23), Dock = DockStyle.Fill, Text = "Curiosity makes learning easier.\r\nRead a little every day.\r\nUnderstand each word in context.", TabStop = false };
            demo.Controls.Add(passage); demo.Controls.Add(instruction); demo.Show(this);
        }
        private void Speak(string word)
        {
            try
            {
                if (speech == null)
                {
                    speech = new SpeechSynthesizer();
                    var voice = speech.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en");
                    if (voice == null) { speech.Dispose(); speech = null; status.Text = "请在 Windows 语音设置中安装英语语音"; return; }
                    speech.SelectVoice(voice.VoiceInfo.Name);
                }
                speech.SpeakAsyncCancelAll(); speech.SpeakAsync(word);
            }
            catch (Exception e) { status.Text = "发音失败：" + e.Message; }
        }
        private bool SaveWord(CaptureResult capture, Entry entry, string meaning)
        {
            SavedWord item = new SavedWord { Id=Guid.NewGuid().ToString("N"),Word = capture.Word, Lemma = entry.Word, Meaning = meaning, Context = capture.Context, SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
            if (saved.Any(w => w.Word.Equals(item.Word, StringComparison.OrdinalIgnoreCase) && w.Meaning == meaning && w.Context == item.Context)) { status.Text = "这个词义和原文已收藏"; return true; }
            var updated = new List<SavedWord>(saved); updated.Insert(0, item);
            if(CommitWords(updated)) { status.Text="已收藏 "+capture.Word; return true; }
            return false;
        }
        private void RefreshWords()
        {
            HashSet<string> selectedIds=new HashSet<string>(savedList.SelectedItems.Cast<ListViewItem>().Where(item=>item.Tag is SavedWord).Select(item=>((SavedWord)item.Tag).Id));
            int top=savedList.IsHandleCreated && savedList.Items.Count>0 && savedList.TopItem!=null ? savedList.TopItem.Index : 0;
            savedList.BeginUpdate();
            try {
            savedList.Items.Clear();
            if(wordSearch!=null && wordFilter!=null) foreach(SavedWord word in Vocabulary.Find(saved,wordSearch.Text,wordFilter.SelectedIndex,DateTimeOffset.UtcNow)) savedList.Items.Add(new ListViewItem(new string[] { word.Word,word.Meaning,Vocabulary.Status(word,DateTimeOffset.UtcNow) }) { Tag=word,Selected=selectedIds.Contains(word.Id),ToolTipText="原文："+(word.Context ?? "")+"\n收藏："+word.SavedAt });
            if(savedList.IsHandleCreated && savedList.Items.Count>0) savedList.TopItem=savedList.Items[Math.Min(top,savedList.Items.Count-1)];
            } finally { savedList.EndUpdate(); }
            emptyState.Visible = !showingHelp && saved.Count == 0; if(emptyState.Visible) emptyState.BringToFront();
            RefreshReviewCount();
        }
        private void ExportWords()
        {
            if (saved.Count == 0) { status.Text = "先收藏一个单词再导出"; return; }
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "CSV 表格 (*.csv)|*.csv", FileName = "HoverLex-生词本.csv", OverwritePrompt = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (StreamWriter writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true)))
                    {
                        writer.WriteLine("单词,原形,选中的词义,原文,收藏时间,状态,复习次数,下次复习");
                        foreach (SavedWord word in saved) writer.WriteLine(String.Join(",", new string[] { Storage.Csv(word.Word), Storage.Csv(word.Lemma), Storage.Csv(word.Meaning), Storage.Csv(word.Context), Storage.Csv(word.SavedAt),Storage.Csv(word.Archived ? "已归档" : "学习中"),Storage.Csv(word.ReviewCount.ToString()),Storage.Csv(word.NextReviewAt) }));
                    }
                    status.Text = "生词本已导出";
                }
                catch (Exception e) { MessageBox.Show("导出失败：" + e.Message, "HoverLex"); }
            }
        }
        public void OpenMain() { Show(); WindowState = FormWindowState.Normal; Activate(); search.Focus(); }
        private void Quit() { quitting = true; timer.Stop(); CancelHoverLookup(); gate.Reset(); if(translation!=null) translation.Dispose(); popup.Close(); Close(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed)
            {
                resourcesDisposed=true;
                helpTips.Dispose();
                CancelHoverLookup(); hoverTranslation.Clear();
                IntPtr window=IsHandleCreated ? Handle : IntPtr.Zero;
                if(window!=IntPtr.Zero) foreach (int id in registered) Native.UnregisterHotKey(window, id);
                registered.Clear();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (brandMark != null && brandMark.Image != null) brandMark.Image.Dispose();
                if (rowImages != null) rowImages.Dispose();
                if(translation!=null) translation.Dispose();
                if(searchTranslation!=null) searchTranslation.Dispose();
                if(selectionTranslation!=null) selectionTranslation.Dispose();
                timer.Dispose(); popup.Dispose(); if (speech != null) speech.Dispose(); if (dictionary != null) dictionary.Dispose();
                reviewTimer.Dispose(); if(!previewMode) EnglishProofreader.StopWorker();
            }
            base.Dispose(disposing);
        }
    }
}
