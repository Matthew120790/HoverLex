using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace HoverLex
{
    public sealed class CaptureResult
    {
        public string Word = "";
        public string Context = "";
        public string Method = "";
        public string Error = "";
    }

    public static partial class ScreenReader
    {
        public sealed class WordBox
        {
            public string Text, Context;
            public RectangleF Bounds;
        }
        private static readonly Regex Token = new Regex(@"[A-Za-z]+(?:[-'\u2019][A-Za-z]+)*",RegexOptions.Compiled);

        public static CaptureResult WordAt(IEnumerable<WordBox> words, double x, double y, string method)
        {
            WordBox best = null; double bestScore = Double.MaxValue; bool ambiguous = false;
            foreach (WordBox word in words) {
                RectangleF r = word.Bounds;
                if (r.Width <= 0 || r.Height <= 0 || Words.Clean(word.Text).Length == 0) continue;
                double dx = Math.Max(0,Math.Max(r.Left-x,x-r.Right)), dy = Math.Max(0,Math.Max(r.Top-y,y-r.Bottom));
                double horizontal = Math.Min(3,Math.Max(1.5,r.Height*.16)), vertical = Math.Min(6,Math.Max(2,r.Height*.28));
                if (dx > horizontal || dy > vertical) continue;
                double score = dx*dx + dy*dy*1.5;
                if (score + .1 < bestScore) { best = word; bestScore = score; ambiguous = false; }
                else if (best != null && Math.Abs(score-bestScore) <= .1 && !String.Equals(best.Text,word.Text,StringComparison.OrdinalIgnoreCase)) ambiguous = true;
            }
            if (best == null || ambiguous) return null;
            return new CaptureResult { Word = Words.Clean(best.Text), Context = best.Context ?? "", Method = method };
        }
        public static CaptureResult AutomationOnly(int x, int y)
        {
            bool protectedNative;
            CaptureResult native=ReadNativePoint(x,y,out protectedNative);
            if(protectedNative) return new CaptureResult { Method="密码保护",Error="密码输入框不取词" };
            if(native!=null) return native;
            bool password;
            CaptureResult result = ReadAutomation(x, y, out password);
            if (password) return new CaptureResult { Method = "密码保护", Error = "密码输入框不取词" };
            if(result!=null) return result;
            result=ReadWhatsAppPoint(x,y,out password);
            if(password) return new CaptureResult { Method="密码保护",Error="密码输入框不取词" };
            if(result!=null) return result;
            result=ReadAccessiblePoint(x,y,out password);
            if(password) return new CaptureResult { Method="密码保护",Error="密码输入框不取词" };
            return result ?? new CaptureResult { Method="文字接口",Error=PointFailure(x,y) };
        }

        private static CaptureResult ReadAutomation(int x, int y, out bool password)
        {
            password = false;
            try
            {
                AutomationElement element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
                List<AutomationElement> parents = new List<AutomationElement>();
                for (int depth = 0; element != null && depth < 32; depth++) {
                    if (element.Current.IsPassword) { password = true; return null; }
                    parents.Add(element);
                    if(element.Current.ControlType==ControlType.Window) break;
                    element = TreeWalker.RawViewWalker.GetParent(element);
                }
                foreach (AutomationElement target in parents)
                {
                    object value;
                    if (target.TryGetCurrentPattern(TextPattern.Pattern, out value))
                    {
                        try { CaptureResult found = ReadText((TextPattern)value,x,y); if (found != null) return found; }
                        catch (Exception error) { if(CaptureTrace!=null) CaptureTrace("UIA text "+target.Current.ClassName+": "+error.GetType().Name); }
                    }
                    // 无文本范围的按钮、标签只接受单个完整单词。
                    string label = target.Current.Name ?? "";
                    string single = Words.Clean(label);
                    ControlType kind = target.Current.ControlType;
                    bool labelControl = kind == ControlType.Text || kind == ControlType.Button || kind == ControlType.Hyperlink || kind == ControlType.MenuItem || kind == ControlType.ListItem;
                    if (labelControl && single.Length > 0 && label.Trim().Split(new char[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length == 1)
                        return new CaptureResult { Word = single, Context = label, Method = "界面文字" };
                }
                // 终端有时只命中外层窗口；在同一窗口内找实际的文字控件。
                IntPtr root=GetAncestor(WindowFromPoint(new Point(x,y)),2);
                string process=WindowProcess(root),rootKind=WindowClass(root);
                if(process.Equals("WindowsTerminal",StringComparison.OrdinalIgnoreCase) || rootKind=="ConsoleWindowClass") {
                    AutomationElement window=AutomationElement.FromHandle(root);
                    var nodes=window.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty,true));
                    int checkedNodes=0;
                    foreach(AutomationElement node in nodes) {
                        if(++checkedNodes>32) break;
                        if(node.Current.IsPassword) { password=true; return null; }
                        if(!node.Current.BoundingRectangle.Contains(new System.Windows.Point(x,y))) continue;
                        object pattern;
                        if(!node.TryGetCurrentPattern(TextPattern.Pattern,out pattern)) continue;
                        try { CaptureResult found=ReadText((TextPattern)pattern,x,y); if(found!=null) return found; } catch { }
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        internal static CaptureResult ReadText(TextPattern pattern, int x, int y)
        {
            TextPatternRange anchor = pattern.RangeFromPoint(new System.Windows.Point(x,y));
            List<WordBox> boxes = new List<WordBox>();
            // RangeFromPoint 是最近插入点，单词右半边可能落到下一个词。
            foreach (int offset in new int[] { 0,-1,1 }) {
                try {
                    TextPatternRange range = anchor.Clone();
                    if (offset != 0) range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start,TextUnit.Character,offset);
                    range.MoveEndpointByRange(TextPatternRangeEndpoint.End,range,TextPatternRangeEndpoint.Start);
                    range.ExpandToEnclosingUnit(TextUnit.Word);
                    AddTextBoxes(boxes,range);
                } catch { }
            }
            CaptureResult found = WordAt(boxes,x,y,"直接取词");
            if (found != null) {
                try {
                    TextPatternRange context = anchor.Clone();
                    context.MoveEndpointByUnit(TextPatternRangeEndpoint.Start,TextUnit.Character,-120);
                    context.MoveEndpointByUnit(TextPatternRangeEndpoint.End,TextUnit.Character,120);
                    found.Context = context.GetText(280).Trim();
                } catch (Exception) { }
                return found;
            }
            TextPatternRange nearby = anchor.Clone();
            try {
                nearby.MoveEndpointByUnit(TextPatternRangeEndpoint.Start,TextUnit.Character,-64);
                nearby.MoveEndpointByUnit(TextPatternRangeEndpoint.End,TextUnit.Character,64);
            } catch { nearby=anchor.Clone(); nearby.ExpandToEnclosingUnit(TextUnit.Line); }
            AddTextBoxes(boxes,nearby);
            found=WordAt(boxes,x,y,"兼容文字范围");
            if(found!=null) return found;
            // 终端可能只支持行范围，仍逐词核对实际文字坐标。
            TextPatternRange line=anchor.Clone(); line.ExpandToEnclosingUnit(TextUnit.Line);
            AddTextBoxes(boxes,line);
            return WordAt(boxes,x,y,"终端 / 行取词");
        }

        private static void AddTextBoxes(List<WordBox> boxes, TextPatternRange range)
        {
            string text = range.GetText(160);
            foreach (Match match in Token.Matches(text)) {
                TextPatternRange word = range.Clone();
                word.MoveEndpointByRange(TextPatternRangeEndpoint.End,word,TextPatternRangeEndpoint.Start);
                if (word.MoveEndpointByUnit(TextPatternRangeEndpoint.Start,TextUnit.Character,match.Index) != match.Index) continue;
                word.MoveEndpointByRange(TextPatternRangeEndpoint.End,word,TextPatternRangeEndpoint.Start);
                if (word.MoveEndpointByUnit(TextPatternRangeEndpoint.End,TextUnit.Character,match.Length) != match.Length) continue;
                string actual = Words.Clean(word.GetText(80));
                if (!String.Equals(actual,Words.Clean(match.Value),StringComparison.OrdinalIgnoreCase)) continue;
                foreach (System.Windows.Rect r in word.GetBoundingRectangles())
                    boxes.Add(new WordBox { Text = actual, Context = text.Trim(), Bounds = new RectangleF((float)r.X,(float)r.Y,(float)r.Width,(float)r.Height) });
            }
        }

    }
}
