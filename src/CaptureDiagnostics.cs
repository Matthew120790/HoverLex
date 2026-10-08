using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Web.Script.Serialization;

namespace HoverLex
{
    internal static class CaptureDiagnostics
    {
        private delegate bool Child(IntPtr window,IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window,Child callback,IntPtr state);
        public static string WindowInfo(long handle)
        {
            IntPtr window=new IntPtr(handle); string process=ScreenReader.WindowProcess(window);
            var report=new Dictionary<string,object> { {"process",process},{"class",ScreenReader.WindowClass(window)},{"handle",handle} };
            List<object> children=new List<object>();
            EnumChildWindows(window,(child,state)=> { if(children.Count<40) children.Add(new { handle=child.ToInt64(),kind=ScreenReader.WindowClass(child) }); return true; },IntPtr.Zero);
            report["childWindows"]=children;
            report["msaa"]=ScreenReader.AccessibleMetadata(window);
            try {
                var root=AutomationElement.FromHandle(window); report["bounds"]=root.Current.BoundingRectangle.ToString();
                report["offscreen"]=root.Current.IsOffscreen;
                var nodes=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty,true));
                report["textPatterns"]=nodes.Count; List<object> text=new List<object>();
                foreach(AutomationElement node in nodes) { if(text.Count>=12) break; text.Add(new { kind=node.Current.ClassName,bounds=node.Current.BoundingRectangle.ToString(),offscreen=node.Current.IsOffscreen }); }
                report["textNodes"]=text;
            } catch(Exception error) { report["uiaError"]=error.GetType().Name; }
            if(process.Equals("wps",StringComparison.OrdinalIgnoreCase) || process.Equals("WINWORD",StringComparison.OrdinalIgnoreCase)) {
                object office=null;
                try { office=ScreenReader.OfficeWindow(window,process); report["officeAttached"]=office!=null; }
                catch(Exception error) { report["officeError"]=error.GetType().Name; }
                finally { if(office!=null && Marshal.IsComObject(office)) Marshal.ReleaseComObject(office); }
                foreach(string program in new[] { "kwps.Application","wps.Application" }) {
                    object app=null,active=null;
                    try { app=Marshal.GetActiveObject(program); active=app.GetType().InvokeMember("ActiveWindow",BindingFlags.GetProperty,null,app,null); report[program]=active.GetType().InvokeMember("Hwnd",BindingFlags.GetProperty,null,active,null); }
                    catch(Exception error) { report[program]=error.GetType().Name; }
                    finally { if(active!=null && Marshal.IsComObject(active)) Marshal.ReleaseComObject(active); if(app!=null && Marshal.IsComObject(app)) Marshal.ReleaseComObject(app); }
                }
            }
            return new JavaScriptSerializer().Serialize(report);
        }
    }
}
