using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Accessibility;

namespace HoverLex
{
    public static partial class ScreenReader
    {
        internal static Action<string> CaptureTrace;
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder name,int size);
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr window,int index);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window,ref Point point);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window,ref Point point);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageTimeoutW")] private static extern IntPtr SendPointMessage(IntPtr window,uint message,IntPtr first,IntPtr second,uint flags,uint timeout,out IntPtr result);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageTimeoutW")] private static extern IntPtr ReadPointText(IntPtr window,uint message,IntPtr first,StringBuilder text,uint flags,uint timeout,out IntPtr result);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window,IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
        [DllImport("gdi32.dll",CharSet=CharSet.Unicode)] private static extern bool GetTextExtentPoint32(IntPtr dc,string text,int count,out Size size);
        [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromPoint(Point point,[MarshalAs(UnmanagedType.Interface)] out IAccessible accessible,[MarshalAs(UnmanagedType.Struct)] out object child);
        [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr window,uint id,ref Guid iid,[MarshalAs(UnmanagedType.Interface)] out object accessible);
        internal static string WindowClass(IntPtr window) { StringBuilder text=new StringBuilder(256); GetClassName(window,text,256); return text.ToString(); }
        internal static string WindowProcess(IntPtr window)
        {
            uint pid; GetWindowThreadProcessId(window,out pid);
            try { using(Process process=Process.GetProcessById((int)pid)) return process.ProcessName; } catch { return ""; }
        }
        private static object GetCom(object value,string name,params object[] args) { return value.GetType().InvokeMember(name,BindingFlags.GetProperty,null,value,args); }
        private static object CallCom(object value,string name,params object[] args) { return value.GetType().InvokeMember(name,BindingFlags.InvokeMethod,null,value,args); }
        private static void ReleaseCom(object value) { if(value!=null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        internal static CaptureResult WordFromOffset(string text,int index,RectangleF glyph,int x,int y,string method)
        {
            if(text==null || index<0 || index>=text.Length || glyph.Width<=0 || glyph.Height<=0 || !glyph.Contains(x,y)) return null;
            foreach(Match match in Token.Matches(text)) if(index>=match.Index && index<match.Index+match.Length) {
                if(match.Length>80) return null;
                return new CaptureResult { Word=Words.Clean(match.Value),Context=text.Trim(),Method=method };
            }
            return null;
        }
        private static CaptureResult ReadNativePoint(int x,int y,out bool password)
        {
            password=false; IntPtr window=WindowFromPoint(new Point(x,y));
            string kind=WindowClass(window),process=WindowProcess(window);
            bool edit=kind.Equals("Edit",StringComparison.OrdinalIgnoreCase) || kind.IndexOf(".EDIT.",StringComparison.OrdinalIgnoreCase)>=0;
            if(edit) {
                IntPtr value;
                if((GetWindowLong(window,-16)&0x20)!=0 || (SendPointMessage(window,0x00d2,IntPtr.Zero,IntPtr.Zero,2,150,out value)!=IntPtr.Zero && value!=IntPtr.Zero)) { password=true; return null; }
                try { return ReadEditPoint(window,x,y); } catch { return null; }
            }
            if(process.Equals("wps",StringComparison.OrdinalIgnoreCase) || process.Equals("WINWORD",StringComparison.OrdinalIgnoreCase)) return ReadOfficePoint(window,x,y,process);
            return null;
        }
        private static CaptureResult ReadEditPoint(IntPtr window,int x,int y)
        {
            Point local=new Point(x,y); if(!ScreenToClient(window,ref local)) return null;
            if(local.X<0 || local.Y<0 || local.X>32767 || local.Y>32767) return null;
            IntPtr value;
            if(SendPointMessage(window,0x000e,IntPtr.Zero,IntPtr.Zero,2,150,out value)==IntPtr.Zero || value.ToInt64()<1 || value.ToInt64()>60000) return null;
            StringBuilder buffer=new StringBuilder((int)value.ToInt64()+1);
            if(ReadPointText(window,0x000d,new IntPtr(buffer.Capacity),buffer,2,150,out value)==IntPtr.Zero) return null;
            string text=buffer.ToString();
            if(SendPointMessage(window,0x00d7,IntPtr.Zero,new IntPtr((local.Y<<16)|(local.X&0xffff)),2,150,out value)==IntPtr.Zero || value.ToInt64()==-1) return null;
            int offset=(int)(value.ToInt64()&0xffff);
            IntPtr dc=GetDC(window),previous=IntPtr.Zero;
            if(dc==IntPtr.Zero) return null;
            try {
                IntPtr font; if(SendPointMessage(window,0x0031,IntPtr.Zero,IntPtr.Zero,2,100,out font)!=IntPtr.Zero && font!=IntPtr.Zero) previous=SelectObject(dc,font);
                for(int delta=-1;delta<=1;delta++) {
                    int index=offset+delta; if(index<0 || index>=text.Length) continue;
                    if(SendPointMessage(window,0x00d6,new IntPtr(index),IntPtr.Zero,2,150,out value)==IntPtr.Zero || value.ToInt64()==-1) continue;
                    Point start=new Point((short)(value.ToInt64()&0xffff),(short)((value.ToInt64()>>16)&0xffff)); Size size;
                    if(!GetTextExtentPoint32(dc,text.Substring(index,1),1,out size) || !ClientToScreen(window,ref start)) continue;
                    int begin=Math.Max(0,index-120),end=Math.Min(text.Length,index+160);
                    while(begin>0 && begin<index && (Char.IsLetter(text[begin-1]) || text[begin-1]=='\'' || text[begin-1]=='-')) begin++;
                    CaptureResult result=WordFromOffset(text.Substring(begin,end-begin),index-begin,new RectangleF(start.X,start.Y,size.Width,size.Height),x,y,"原生文本取词");
                    if(result!=null) return result;
                }
            } finally { if(previous!=IntPtr.Zero) SelectObject(dc,previous); ReleaseDC(window,dc); }
            return null;
        }
        private static bool OfficeMatches(object window,IntPtr root)
        {
            object document=null;
            try { IntPtr handle=new IntPtr(Convert.ToInt64(GetCom(window,"Hwnd"))); if(handle==IntPtr.Zero || GetAncestor(handle,2)!=root) return false; document=GetCom(window,"Document"); return document!=null; }
            catch { return false; }
            finally { ReleaseCom(document); }
        }
        internal static object OfficeWindow(IntPtr target,string process)
        {
            Guid dispatch=new Guid("00020400-0000-0000-C000-000000000046");
            IntPtr root=GetAncestor(target,2);
            for(IntPtr current=target;current!=IntPtr.Zero;current=GetAncestor(current,1)) {
                object native=null;
                try {
                    if(AccessibleObjectFromWindow(current,0xfffffff0,ref dispatch,out native)==0 && native!=null) {
                        if(OfficeMatches(native,root)) return native;
                        object window=null;
                        try { window=GetCom(native,"ActiveWindow"); if(OfficeMatches(window,root)) return window; } finally { if(window!=null && !OfficeMatches(window,root)) ReleaseCom(window); }
                    }
                } catch { }
                finally { if(native!=null && !OfficeMatches(native,root)) ReleaseCom(native); }
                if(current==root) break;
            }
            string[] programs=process.Equals("wps",StringComparison.OrdinalIgnoreCase) ? new[] { "kwps.Application","wps.Application" } : new[] { "Word.Application" };
            foreach(string program in programs) {
                object app=null,window=null;
                try { app=Marshal.GetActiveObject(program); window=GetCom(app,"ActiveWindow"); if(OfficeMatches(window,root)) return window; }
                catch { }
                finally { if(window!=null && !OfficeMatches(window,root)) ReleaseCom(window); ReleaseCom(app); }
            }
            return null;
        }
        internal static CaptureResult ReadOfficePoint(IntPtr target,int x,int y,string process)
        {
            object window=null,range=null,word=null,context=null;
            try {
                window=OfficeWindow(target,process); if(window==null) { if(CaptureTrace!=null) CaptureTrace("office window unavailable"); return null; }
                range=CallCom(window,"RangeFromPoint",x,y); if(range==null) return null;
                word=GetCom(range,"Duplicate"); CallCom(word,"Expand",2);
                string text=Convert.ToString(GetCom(word,"Text")); string clean=Words.Clean(text);
                if(clean.Length==0 || !Regex.IsMatch(text.Trim(),@"^[A-Za-z]+(?:[-'\u2019][A-Za-z]+)*[.,;:!?]*$")) return null;
                object[] args={0,0,0,0,word}; ParameterModifier refs=new ParameterModifier(5); for(int i=0;i<4;i++) refs[i]=true;
                window.GetType().InvokeMember("GetPoint",BindingFlags.InvokeMethod,null,window,args,new[] { refs },CultureInfo.InvariantCulture,null);
                RectangleF bounds=new RectangleF(Convert.ToInt32(args[0]),Convert.ToInt32(args[1]),Convert.ToInt32(args[2]),Convert.ToInt32(args[3]));
                if(!bounds.Contains(x,y)) { if(CaptureTrace!=null) CaptureTrace("office range outside pointer: "+bounds); return null; }
                context=GetCom(word,"Duplicate"); int start=Convert.ToInt32(GetCom(word,"Start")),end=Convert.ToInt32(GetCom(word,"End"));
                CallCom(context,"SetRange",Math.Max(0,start-120),end);
                try { CallCom(context,"MoveEnd",1,120); } catch { }
                return new CaptureResult { Word=clean,Context=Convert.ToString(GetCom(context,"Text")).Trim(),Method=process.Equals("wps",StringComparison.OrdinalIgnoreCase) ? "WPS 文档取词" : "Word 文档取词" };
            } catch(Exception error) { if(CaptureTrace!=null) CaptureTrace("office read "+error.GetType().Name+": "+error.Message); return null; }
            finally { ReleaseCom(context); ReleaseCom(word); ReleaseCom(range); ReleaseCom(window); }
        }
        internal static string PointFailure(int x,int y)
        {
            if(WhatsAppWindows.Host(WindowFromPoint(new Point(x,y)))!=IntPtr.Zero) return "WhatsApp 当前聊天区域不支持悬停取词。请拖选或双击英文，再轻按并松开 Ctrl 翻译。";
            string process=WindowProcess(WindowFromPoint(new Point(x,y)));
            if(process.Equals("Weixin",StringComparison.OrdinalIgnoreCase) || process.Equals("WeChat",StringComparison.OrdinalIgnoreCase)) return "微信当前区域未提供可定位的文字。可先选中英文，再轻按 Ctrl 翻译。";
            if(process.Equals("wps",StringComparison.OrdinalIgnoreCase)) return "WPS 当前区域无法定位文字。请确认指向文档正文；图片或扫描件不支持取词。";
            return "没有读到鼠标处的英文。请指向可选择的文字；管理员窗口需保持相同权限。";
        }
        private static CaptureResult ReadAccessiblePoint(int x,int y,out bool password)
        {
            password=false; IAccessible accessible=null; object child=null;
            List<object> owned=new List<object>();
            try {
                if(AccessibleObjectFromPoint(new Point(x,y),out accessible,out child)!=0 || accessible==null) return null;
                owned.Add(accessible);
                IAccessible target=accessible; object id=child ?? 0;
                if(id is int && (int)id!=0) {
                    object nested=null; try { nested=accessible.get_accChild(id); } catch { }
                    if(nested is IAccessible) { target=(IAccessible)nested; id=0; owned.Add(nested); }
                }
                for(IAccessible ancestor=target;ancestor!=null;) {
                    if((Convert.ToInt32(ancestor.get_accState(ancestor==target ? id : 0))&0x20000000)!=0) { password=true; return null; }
                    if(owned.Count>=32) break;
                    ancestor=ancestor.accParent as IAccessible;
                    if(ancestor!=null) { if(owned.Contains(ancestor)) break; owned.Add(ancestor); }
                }
                CaptureResult result=ReadAccessibleText(target,x,y);
                if(result!=null) return result;
                // 只接受有准确边框的独立单词，不猜测整段消息的字体布局。
                int role=Convert.ToInt32(target.get_accRole(id));
                if(role!=0x29 && role!=0x2b && role!=0x1e && role!=0x0c) return null;
                string label=target.get_accName(id) ?? "";
                if(!Regex.IsMatch(label.Trim(),@"^[A-Za-z]+(?:[-'\u2019][A-Za-z]+)*$")) return null;
                int left,top,width,height; target.accLocation(out left,out top,out width,out height,id);
                return WordAt(new[] { new WordBox { Text=label,Context=label,Bounds=new RectangleF(left,top,width,height) } },x,y,"辅助接口取词");
            } catch { return null; }
            finally { foreach(object value in owned) ReleaseCom(value); }
        }
        internal static object AccessibleMetadata(IntPtr window)
        {
            object value=null;
            try {
                Guid iid=new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");
                int code=AccessibleObjectFromWindow(window,0xfffffffc,ref iid,out value);
                IAccessible acc=value as IAccessible;
                if(acc==null) return new { code=code,children=-1,text=false };
                return new { code=code,children=acc.accChildCount,text=value is AccessibleText };
            } catch(Exception error) { return new { error=error.GetType().Name }; }
            finally { ReleaseCom(value); }
        }
        private static CaptureResult ReadAccessibleText(object accessible,int x,int y)
        {
            AccessibleText text=accessible as AccessibleText; object serviceObject=null; IntPtr pointer=IntPtr.Zero;
            try {
                if(text==null) {
                    AccessibleServices services=accessible as AccessibleServices;
                    if(services==null) return null;
                    Guid service=new Guid("618736e0-3c3d-11cf-810c-00aa00389b71"),iid=typeof(AccessibleText).GUID;
                    if(services.QueryService(ref service,ref iid,out pointer)!=0 || pointer==IntPtr.Zero) return null;
                    serviceObject=Marshal.GetTypedObjectForIUnknown(pointer,typeof(AccessibleText)); text=serviceObject as AccessibleText;
                }
                int count,offset;
                if(text==null || text.CharacterCount(out count)!=0 || count<1 || text.OffsetAtPoint(x,y,0,out offset)!=0 || offset<0 || offset>=count) return null;
                int start=Math.Max(0,offset-120),end=Math.Min(count,offset+160); string nearby;
                if(text.GetText(start,end,out nearby)!=0 || nearby==null || nearby.Length>end-start) return null;
                foreach(int delta in new[] {0,-1,1}) {
                    int index=offset+delta,left,top,width,height;
                    if(index<start || index>=end || text.CharacterExtents(index,0,out left,out top,out width,out height)!=0) continue;
                    CaptureResult result=WordFromOffset(nearby,index-start,new RectangleF(left,top,width,height),x,y,"兼容文本取词");
                    if(result!=null) return result;
                }
            } catch { }
            finally { ReleaseCom(serviceObject); if(pointer!=IntPtr.Zero) Marshal.Release(pointer); }
            return null;
        }
    }
    [ComImport,Guid("6d5140c1-7436-11ce-8034-00aa006009fa"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface AccessibleServices
    {
        [PreserveSig] int QueryService(ref Guid service,ref Guid iid,out IntPtr value);
    }
    // IAccessible2 文本接口的固定方法顺序；只调用读取方法。
    [ComImport,Guid("24FD2FFB-3AAD-4a08-8335-A3AD89C0FB4B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface AccessibleText
    {
        [PreserveSig] int AddSelection(int start,int end);
        [PreserveSig] int Attributes(int offset,out int start,out int end,[MarshalAs(UnmanagedType.BStr)] out string attributes);
        [PreserveSig] int CaretOffset(out int offset);
        [PreserveSig] int CharacterExtents(int offset,int coordinates,out int x,out int y,out int width,out int height);
        [PreserveSig] int SelectionCount(out int count);
        [PreserveSig] int OffsetAtPoint(int x,int y,int coordinates,out int offset);
        [PreserveSig] int Selection(int index,out int start,out int end);
        [PreserveSig] int GetText(int start,int end,[MarshalAs(UnmanagedType.BStr)] out string text);
        [PreserveSig] int TextBefore(int offset,int boundary,out int start,out int end,[MarshalAs(UnmanagedType.BStr)] out string text);
        [PreserveSig] int TextAfter(int offset,int boundary,out int start,out int end,[MarshalAs(UnmanagedType.BStr)] out string text);
        [PreserveSig] int TextAt(int offset,int boundary,out int start,out int end,[MarshalAs(UnmanagedType.BStr)] out string text);
        [PreserveSig] int RemoveSelection(int index);
        [PreserveSig] int SetCaret(int offset);
        [PreserveSig] int SetSelection(int index,int start,int end);
        [PreserveSig] int CharacterCount(out int count);
    }
}
