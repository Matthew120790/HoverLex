using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HoverLex
{
    public static class SelfTest
    {
        private static List<string> report = new List<string>();
        private static int failures;
        private static readonly string Base = AppDomain.CurrentDomain.BaseDirectory;
        private static void Check(bool ok, string name)
        {
            report.Add((ok ? "PASS " : "FAIL ") + name);
            if (!ok) failures++;
        }
        public static int Run()
        {
            try
            {
                Check(Words.Clean(" (curiosity), ") == "curiosity", "punctuation cleanup");
                Check(Words.Clean("don\u2019t") == "don't", "typographic apostrophe");
                Check(Words.Clean("hello world") == "" && Words.Clean("你好") == "", "reject phrases and non-English text");
                HoverGate gate = new HoverGate();
                Check(!gate.Observe(new Point(10, 10), true, true, false, 0, 350), "Ctrl starts dwell without an immediate request");
                Check(gate.Observe(new Point(10, 10), true, true, false, 500, 350), "Ctrl over stationary word triggers after dwell");
                gate.Observe(new Point(20, 10), true, true, false, 550, 350);
                Check(!gate.Observe(new Point(20, 10), true, true, false, 800, 350), "dwell delay enforced");
                Check(gate.Observe(new Point(20, 10), true, true, false, 900, 350), "movement followed by dwell triggers");
                Check(!gate.Observe(new Point(20, 10), true, true, false, 950, 350), "no duplicate capture at same position");
                int generation = gate.Generation;
                gate.Observe(new Point(20, 10), false, true, false, 960, 350);
                Check(gate.Generation != generation, "release invalidates in-flight result");
                gate.Observe(new Point(20, 10), true, true, false, 1000, 350);
                gate.Observe(new Point(40, 10), true, true, false, 1020, 350);
                Check(!gate.Observe(new Point(40, 10), true, true, false, 1400, 350, false), "busy worker preserves pending request");
                Check(gate.Observe(new Point(40, 10), true, true, false, 1440, 350, true), "pending request retried after worker finishes");
                Check(!gate.Observe(new Point(50, 10), true, false, false, 2000, 350), "disabled lookup blocked");
                gate.Observe(new Point(50, 10), true, true, false, 2100, 350);
                Check(!gate.Observe(new Point(60, 10), true, true, true, 2500, 350), "popup hover blocked");
                CaptureRegression();
                using (LocalDictionary dictionary = new LocalDictionary(Path.Combine(Base, "Dictionary")))
                {
                    Check(dictionary.Count > 700000, "full ECDICT available: " + dictionary.Count);
                    foreach (string word in new string[] { "curiosity", "Curiosity", "read", "context", "understand", "learning", "mice", "ran", "generates", "studying" })
                    {
                        Entry entry = dictionary.Lookup(word);
                        Check(entry != null && !String.IsNullOrWhiteSpace(entry.Chinese), "dictionary lookup: " + word + (entry == null ? "" : " -> " + entry.Word));
                    }
                    Check(dictionary.Lookup("zzzxxyyunknownword") == null, "unknown word has no false match");
                }
                string dir = Path.Combine(Base, "TestArtifacts", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "words.json");
                var items = new List<SavedWord> { new SavedWord { Word = "curiosity", Meaning = "好奇心", Context = "Curiosity, \"always\".", SavedAt = "2026-10-04" } };
                Storage.WriteJson(path, items); items.Add(new SavedWord { Word = "read", Meaning = "阅读" }); Storage.WriteJson(path, items);
                Check(Storage.LoadWords(path).Count == 2 && File.Exists(path + ".bak"), "saved-word persistence with backup");
                Check(Storage.Csv("a,\"b\"") == "\"a,\"\"b\"\"\"", "CSV quotes escaped");
                string bad = Path.Combine(dir, "bad.json"); File.WriteAllText(bad, "{broken", Encoding.UTF8);
                bool threw = false; try { Storage.LoadWords(bad); } catch { threw = true; }
                Check(threw && File.ReadAllText(bad, Encoding.UTF8) == "{broken", "corrupt vocabulary preserved");
                Preview();
                Check(File.Exists(Path.Combine(Base, "main-preview.png")) && File.Exists(Path.Combine(Base, "popup-preview.png")), "main and popup render");
                RenderRegression();
                TranslationTests.Core(Check);
                LearningTests.Core(Check);
            }
            catch (Exception e) { Check(false, e.ToString()); }
            File.WriteAllLines(Path.Combine(Base, "self-test.txt"), report, Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }
        private static void CaptureRegression()
        {
            HoverGate retry = new HoverGate(); Point p = new Point(20,20);
            retry.Observe(p,true,true,false,0,150);
            Check(retry.Observe(p,true,true,false,150,150),"stationary request starts at configured dwell");
            int generation = retry.Generation;
            retry.Retry(generation,200);
            Check(!retry.Observe(p,true,true,false,600,150),"failed lookup retry waits before restarting");
            Check(retry.Observe(new Point(21,21),true,true,false,650,150),"small cursor jitter still permits a failed lookup retry");
            retry.Retry(generation,700);
            Check(retry.Observe(p,true,true,false,1150,150),"second failed lookup can retry at same position");
            retry.Retry(generation,1200);
            Check(!retry.Observe(p,true,true,false,2000,150),"blank area retries stop after three attempts");
            retry.Observe(new Point(40,20),true,true,false,2100,150);
            retry.Retry(generation,2150);
            Check(retry.Observe(new Point(40,20),true,true,false,2250,150),"stale failure cannot delay lookup on a new word");
            int active = retry.Generation; retry.Observe(p,false,true,false,2300,150); retry.Retry(active,2400);
            Check(!retry.Observe(p,true,true,false,2450,150),"release cancels retries and starts a fresh dwell");
            var boxes = new List<ScreenReader.WordBox> {
                new ScreenReader.WordBox { Text="first", Context="first second", Bounds=new RectangleF(10,10,40,16) },
                new ScreenReader.WordBox { Text="second", Context="first second", Bounds=new RectangleF(58,10,55,16) },
                new ScreenReader.WordBox { Text="below", Context="below", Bounds=new RectangleF(10,38,44,16) }
            };
            CaptureResult edge=ScreenReader.WordAt(boxes,49,29,"test");
            Check(edge != null && edge.Word == "first","pointer near baseline still picks the intended word");
            CaptureResult next=ScreenReader.WordAt(boxes,59,17,"test");
            Check(next != null && next.Word == "second","neighbor word is not stolen by edge tolerance");
            Check(ScreenReader.WordAt(boxes,54,18,"test") == null,"space between words remains unmatched");
            Check(ScreenReader.WordAt(boxes,25,32,"test") == null,"space between text lines remains unmatched");
            Check(ScreenReader.WordAt(boxes,160,90,"test") == null,"blank background is not replaced with nearest text");
            var adjacent = new List<ScreenReader.WordBox> {
                new ScreenReader.WordBox { Text="left", Bounds=new RectangleF(0,0,40,16) },
                new ScreenReader.WordBox { Text="right", Bounds=new RectangleF(44,0,40,16) }
            };
            Check(ScreenReader.WordAt(adjacent,42,8,"test") == null,"equally close words are not guessed");
        }
        public static int RenderTest()
        {
            RenderRegression();
            File.WriteAllLines(Path.Combine(Base,"ui-render-test.txt"),report,Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }
        private static void RenderRegression()
        {
            bool empty = true;
            foreach (RectangleF bounds in new RectangleF[] { RectangleF.Empty,new RectangleF(1,1,-2,30),new RectangleF(1,1,30,0),new RectangleF(0,0,Single.NaN,30) })
                using (var path=Theme.Round(bounds,12)) empty &= path.PointCount==0;
            Check(empty,"invalid rounded rectangles are skipped without GDI+ exceptions");
            using (var path=Theme.Round(new RectangleF(1,1,100,60),0)) Check(path.GetBounds()==new RectangleF(1,1,100,60),"zero corner radius uses a valid rectangle");
            // 排版过程可能暂时给控件零尺寸，直接触发绘制以覆盖这一时刻。
            using (Bitmap image = new Bitmap(640,480))
            using (Graphics graphics = Graphics.FromImage(image))
            using (PaintEventArgs paint = new PaintEventArgs(graphics,new Rectangle(0,0,640,480)))
            {
                Control[] controls = { new Card(), new SoftButton(), new LookupPanel() };
                foreach (Control control in controls) using (control)
                {
                    bool ok = true; string error = "";
                    try {
                        var method = control.GetType().GetMethod(control is Card ? "OnPaintBackground" : "OnPaint",System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        foreach (Size size in new Size[] { Size.Empty,new Size(1,1),new Size(2,2),new Size(3,3),new Size(1,100),new Size(100,1),new Size(100,60) }) {
                            control.Size = size; method.Invoke(control,new object[] { paint });
                        }
                    } catch(Exception e) { ok=false; error=(e.InnerException ?? e).Message; }
                    Check(ok,control.GetType().Name+" paints through zero, tiny and restored sizes "+error);
                }
            }
            try {
                using (MainForm main = new MainForm(true,new List<SavedWord> { new SavedWord { Word="curiosity",Meaning="好奇心" } })) {
                    main.StartPosition=FormStartPosition.Manual; main.Location=new Point(-32000,-32000); main.ShowInTaskbar=false; main.Show();
                    Size normal=main.Size;
                    for(int i=0;i<20;i++) {
                        main.WindowState=FormWindowState.Minimized; Application.DoEvents();
                        main.WindowState=FormWindowState.Normal; main.Size=i%2==0 ? main.MinimumSize : normal;
                        main.PerformLayout(); main.Refresh(); Application.DoEvents();
                        using(Bitmap image=new Bitmap(main.Width,main.Height)) main.DrawToBitmap(image,new Rectangle(Point.Empty,main.Size));
                        ((Button)main.Controls.Find("helpButton",true)[0]).PerformClick(); main.Refresh(); Application.DoEvents();
                    }
                    main.Close();
                }
                Check(true,"20 minimize/restore/resize cycles and help navigation render without exceptions");
            } catch(Exception e) { Check(false,"window repaint cycles: "+e); }
        }

        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle,int command);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle,uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle,int index);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle,out uint pid);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr handle,StringBuilder name,int length);

        private static void PopupIntegration(List<string> lines,Form fixture)
        {
            using (LookupPanel panel = new LookupPanel())
            using (Form cover = new Form { Text="HoverLex overlap test", StartPosition=FormStartPosition.Manual, ShowInTaskbar=false }) {
                panel.SetResult(new CaptureResult { Word="curiosity",Method="测试",Context="Curiosity makes learning easier." },new Entry { Word="curiosity",Chinese="n. 好奇心" });
                Point location = new Point(fixture.Left+40,fixture.Top+40);
                IntPtr foreground = GetForegroundWindow(); panel.ShowAt(location); Application.DoEvents();
                Point sample = panel.PointToScreen(new Point(30,30));
                lines.Add(((GetWindowLong(panel.Handle,-20)&8)!=0 ? "PASS" : "FAIL")+" popup has native topmost state");
                lines.Add((GetAncestor(WindowFromPoint(sample),2)==panel.Handle ? "PASS" : "FAIL")+" lookup panel appears above reading window");
                lines.Add((GetForegroundWindow()==foreground ? "PASS" : "FAIL")+" lookup panel does not steal reading focus");
                cover.Bounds=panel.Bounds; cover.Show(); ShowWindow(cover.Handle,5); cover.Activate(); Application.DoEvents();
                lines.Add((GetAncestor(WindowFromPoint(sample),2)==panel.Handle ? "PASS" : "FAIL")+" activated normal window cannot cover lookup panel");
                Native.SetWindowPos(panel.Handle,new IntPtr(-2),0,0,0,0,0x0013);
                panel.ShowAt(location); Application.DoEvents();
                lines.Add(((GetWindowLong(panel.Handle,-20)&8)!=0 && GetAncestor(WindowFromPoint(sample),2)==panel.Handle ? "PASS" : "FAIL")+" repeated lookup restores topmost order");
                panel.Hide(); panel.ShowAt(location); Application.DoEvents();
                lines.Add((GetAncestor(WindowFromPoint(sample),2)==panel.Handle ? "PASS" : "FAIL")+" hidden lookup panel returns on top");
                panel.Hide(); cover.Close();
            }
        }
        public static void Preview()
        {
            using (MainForm main = new MainForm(true))
            {
                Prepare(main);
                Check(!HasText(main, "截图识字") && !HasText(main, "Ctrl + Alt + O"), "removed image capture controls and help shortcut");
                Check(typeof(ScreenReader).GetMethod("ReadBitmap") == null, "image recognition engine removed");
                Check(MainForm.Probe(0,0,"image",100).Word.Length == 0 && MainForm.Probe(0,0,"ocr",100).Error == "不支持的取词模式", "old image modes no longer capture the screen");
                Check(main.Controls.Find("savedList", true)[0].Height >= 100, "vocabulary panel remains visible at current DPI");
                Control lookup = main.Controls.Find("lookup",true)[0];
                Check(lookup.Bottom <= lookup.Parent.ClientSize.Height && lookup.Height >= 40, "lookup button fits its row at current DPI");
                foreach(string name in new[] { "captureEnabled","inputTranslation","englishCorrection","reviewReminder" }) {
                    Control toggle=main.Controls.Find(name,true)[0];
                    using(Graphics graphics=toggle.CreateGraphics()) {
                        int required=TextRenderer.MeasureText(graphics,toggle.Text,toggle.Font,Size.Empty,TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width+(int)Math.Ceiling(36*graphics.DpiX/96f);
                        Check(toggle.Width>=required,"switch text fits the painted track at current DPI: "+name);
                    }
                }
                using (Bitmap image = new Bitmap(main.Width, main.Height)) { main.DrawToBitmap(image, new Rectangle(Point.Empty, main.Size)); image.Save(Path.Combine(Base, "main-preview.png")); }
                main.Size = main.MinimumSize; Prepare(main);
                Control correction=main.Controls.Find("englishCorrection",true)[0];
                Check(correction.Right<=correction.Parent.ClientSize.Width && correction.Bottom<=correction.Parent.ClientSize.Height,"the independent correction switch fits the minimum window size");
                ((CheckBox)correction).Checked=false;
                using(Bitmap image=new Bitmap(main.Width,main.Height)) { main.DrawToBitmap(image,new Rectangle(Point.Empty,main.Size)); image.Save(Path.Combine(Base,"correction-off-preview.png")); }
                Check(lookup.Bottom <= lookup.Parent.ClientSize.Height && main.Controls.Find("savedList",true)[0].Height >= 100, "critical controls fit the minimum window size");
            }
            using (MainForm main = new MainForm(true,new List<SavedWord> {
                new SavedWord { Word = "curiosity", Meaning = "n. 好奇心；求知欲", SavedAt = "2026-10-04 09:30" },
                new SavedWord { Word = "serendipity", Meaning = "n. 意外发现美好事物的机缘", SavedAt = "2026-10-03 20:16" },
                new SavedWord { Word = "context", Meaning = "n. 上下文；语境", SavedAt = "2026-10-03 18:42" }
            }))
            {
                Prepare(main);
                main.StartPosition = FormStartPosition.Manual; main.Location = new Point(-32000,-32000); main.ShowInTaskbar = false;
                main.Show(); Application.DoEvents();
                using (Bitmap image = new Bitmap(main.Width,main.Height)) { main.DrawToBitmap(image,new Rectangle(Point.Empty,main.Size)); image.Save(Path.Combine(Base,"main-saved-preview.png")); }
                ((Button)main.Controls.Find("helpButton",true)[0]).PerformClick(); Application.DoEvents();
                using (Bitmap image = new Bitmap(main.Width,main.Height)) { main.DrawToBitmap(image,new Rectangle(Point.Empty,main.Size)); image.Save(Path.Combine(Base,"help-preview.png")); }
                main.Close();
            }
            using (LookupPanel popup = new LookupPanel())
            {
                popup.SetResult(new CaptureResult { Word = "curiosity", Method = "直接取词", Context = "Curiosity makes learning easier." }, new Entry { Word = "curiosity", Phonetic = "kj\u028a\u0259ri\u0252s\u026ati", Chinese = "n. 好奇心；求知欲\nn. 奇物；珍品" });
                Prepare(popup);
                using (Bitmap image = new Bitmap(popup.Width, popup.Height)) { popup.DrawToBitmap(image, new Rectangle(Point.Empty, popup.Size)); image.Save(Path.Combine(Base, "popup-preview.png")); }
            }
        }
        private static bool HasText(Control control, string text)
        {
            if (control.Text.Contains(text)) return true;
            foreach (Control child in control.Controls) if (HasText(child, text)) return true;
            return false;
        }
        internal static void Prepare(Control control)
        {
            IntPtr handle = control.Handle;
            ContainerControl container = control as ContainerControl;
            if (container != null) container.PerformAutoScale();
            foreach (Control child in control.Controls) Prepare(child);
            control.PerformLayout();
        }
        public static int Integration()
        {
            var lines = new List<string>(); int exitCode = 1;
            using (Form fixture = new Form { Text = "HoverLex capture test", Size = new Size(700, 390), TopMost = true, StartPosition = FormStartPosition.CenterScreen, AutoScaleMode = AutoScaleMode.None, BackColor = Color.White })
            using (Bitmap pixels = new Bitmap(620, 150))
            using (Graphics graphics = Graphics.FromImage(pixels))
            using (Font font = new Font("Segoe UI", 28, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White); graphics.DrawString("curiosity makes learning easier", font, Brushes.Black, 20, 35);
                TextBox text = new TextBox { Text = "curiosity makes learning easier", Font = font, Location = new Point(20, 20), Size = new Size(620, 50) };
                PictureBox picture = new PictureBox { Image = pixels, SizeMode = PictureBoxSizeMode.Normal, Location = new Point(20, 100), Size = pixels.Size };
                TextBox password = new TextBox { UseSystemPasswordChar = true, Text = "secret", Location = new Point(20, 280), Width = 200 };
                fixture.Controls.AddRange(new Control[] { text, picture, password });
                fixture.Shown += async delegate
                {
                    try
                    {
                        ShowWindow(fixture.Handle,5); fixture.BringToFront(); fixture.Activate();
                        await Task.Delay(400);
                        ShowWindow(fixture.Handle,5); Native.SetWindowPos(fixture.Handle,new IntPtr(-1),0,0,0,0,0x0053); fixture.Activate(); await Task.Delay(100);
                        Point uiaPoint = text.PointToScreen(text.GetPositionFromCharIndex(3)); uiaPoint.Offset(3, 14);
                        IntPtr under=WindowFromPoint(uiaPoint); uint underPid; GetWindowThreadProcessId(under,out underPid);
                        lines.Add("INFO fixture visible="+fixture.Visible+" bounds="+fixture.Bounds+" dpi="+fixture.DeviceDpi+" point="+uiaPoint+" ownHandle="+fixture.Handle+" underHandle="+under+" root="+GetAncestor(under,2)+" underPid="+underPid+" nativeTopmost="+((GetWindowLong(fixture.Handle,-20)&8)!=0));
                        StringBuilder surface=new StringBuilder(256); GetClassName(GetAncestor(under,2),surface,256);
                        if(surface.ToString()=="LockScreenBackstopFrame") { lines.Add("BLOCKED Windows lock screen covers the capture fixture; unlock before live testing"); exitCode=3; return; }
                        CaptureResult uia = await Task.Run(() => MainForm.Probe(uiaPoint.X, uiaPoint.Y, "uia", 2500));
                        lines.Add((uia.Word.Equals("curiosity", StringComparison.OrdinalIgnoreCase) ? "PASS" : "FAIL") + " UIA: " + uia.Word + " " + uia.Error);
                        lines.Add((uia.Context.Contains("learning") ? "PASS" : "FAIL")+" UIA retains sentence context");
                        foreach (int index in new int[] { 0,8,10,16 }) {
                            Point local = text.GetPositionFromCharIndex(index), after = text.GetPositionFromCharIndex(index+1);
                            local.Offset(Math.Max(1,(after.X-local.X)*3/4),14);
                            Point location = text.PointToScreen(local);
                            CaptureResult at = await Task.Run(() => MainForm.Probe(location.X,location.Y,"uia",2500));
                            string expected = index <= 8 ? "curiosity" : index <= 14 ? "makes" : "learning";
                            lines.Add((at.Word.Equals(expected,StringComparison.OrdinalIgnoreCase) ? "PASS" : "FAIL")+" UIA character "+index+": "+at.Word+" "+at.Error);
                        }
                        Point pw = password.PointToScreen(new Point(20, 10));
                        CaptureResult protectedResult = await Task.Run(() => MainForm.Probe(pw.X, pw.Y, "uia", 2500));
                        lines.Add((protectedResult.Method == "密码保护" ? "PASS" : "FAIL") + " password excluded: " + protectedResult.Method);
                        PopupIntegration(lines,fixture);
                        exitCode = lines.Any(l => l.StartsWith("FAIL")) ? 1 : 0;
                    }
                    catch (Exception e) { lines.Add("FAIL " + e); }
                    finally { File.WriteAllLines(Path.Combine(Base, "integration-test.txt"), lines, Encoding.UTF8); fixture.Close(); }
                };
                Application.Run(fixture);
            }
            return exitCode;
        }
    }
}
