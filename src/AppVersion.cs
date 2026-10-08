using System.Diagnostics;
using System.Reflection;

namespace HoverLex
{
    public static class AppVersion
    {
        public static string Current
        {
            get
            {
                string version = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion;
                return string.IsNullOrEmpty(version) ? "开发版" : new System.Version(version).ToString(3);
            }
        }
    }
}
