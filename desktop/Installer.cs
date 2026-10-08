using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HoverLex.Desktop
{
    internal static class Installer
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoverLex");
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "HoverLex");
            if (args.Length == 5 && args[0] == "--test-install" && args[1] == "--target" && args[3] == "--desktop")
            {
                try { Install(Path.GetFullPath(args[2]), Path.GetFullPath(args[4]), Path.Combine(Path.GetFullPath(args[2]),"test-start-menu"), delegate { }); return 0; }
                catch (Exception e) { Directory.CreateDirectory(args[2]); File.WriteAllText(Path.Combine(args[2], "install-error.txt"), e.ToString(), Encoding.UTF8); return 1; }
            }
            if (args.Length > 0 && !(args.Length == 2 && args[0] == "--preview"))
            {
                bool silent = false;
                string log = null;
                bool targetOverride = false, desktopOverride = false, menuOverride = false;
                try
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        string flag = args[i].ToUpperInvariant();
                        if (flag == "/VERYSILENT" || flag == "/SILENT" || flag == "/S" || flag == "--SILENT") silent = true;
                        else if (flag == "/SUPPRESSMSGBOXES" || flag == "/NORESTART") { }
                        else if (flag == "--TARGET" || flag == "--DESKTOP" || flag == "--START-MENU" || flag == "--LOG")
                        {
                            if (++i >= args.Length) throw new ArgumentException("参数缺少路径：" + flag);
                            string path = Path.GetFullPath(args[i]);
                            if (flag == "--TARGET") { target = path; targetOverride = true; }
                            else if (flag == "--DESKTOP") { desktop = path; desktopOverride = true; }
                            else if (flag == "--START-MENU") { startMenu = path; menuOverride = true; }
                            else log = path;
                        }
                        else throw new ArgumentException("不支持的安装参数：" + args[i]);
                    }
                    if (!silent) throw new ArgumentException("命令行安装请使用 /VERYSILENT 或 /S");
                    if ((targetOverride || desktopOverride || menuOverride) && !(targetOverride && desktopOverride && menuOverride))
                        throw new ArgumentException("自定义安装必须同时提供 --target、--desktop、--start-menu");
                    Install(target,desktop,startMenu,delegate { });
                    WriteSilentLog(log,new { Success = true, Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3), Target = target, Desktop = desktop, StartMenu = startMenu });
                    return 0;
                }
                catch (Exception error)
                {
                    try { WriteSilentLog(log,new { Success = false, Error = error.Message }); } catch { }
                    return error is ArgumentException ? 2 : 1;
                }
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (WelcomeWindow form = new WelcomeWindow("安装 HoverLex · 鼠标取词", "让每一个陌生单词，都成为阅读的收获。", true))
            {
                Label status = form.Message; status.Text = "正在安装，并创建桌面图标…";
                Button done = form.Done;
                done.Click += delegate { form.Close(); };
                form.Shown += async delegate
                {
                    try
                    {
                        await Task.Run(() => Install(target, desktop, startMenu, message => { if (!form.IsDisposed) form.BeginInvoke(new Action(() => status.Text = message)); }));
                        status.Text = FinishedMessage();
                    }
                    catch (Exception error) { status.Text = "安装未完成：" + error.Message; }
                    finally { done.Enabled = true; }
                };
                if (args.Length == 2 && args[0] == "--preview")
                {
                    status.Text = FinishedMessage(); done.Enabled = true; Prepare(form);
                    using (Bitmap image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(args[1]); }
                    return 0;
                }
                Application.Run(form);
            }
            return 0;
        }
        private static void WriteSilentLog(string path, object value)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            string parent = Path.GetDirectoryName(path); Directory.CreateDirectory(parent);
            UpdateFiles.AtomicJson(path,value);
        }
        private static string FinishedMessage() { return "已为你准备好。\n\n双击桌面的「HoverLex 鼠标取词」开始使用。\n每次打开自动检查更新，生词本会保留。"; }
        private static void Prepare(Control control)
        {
            IntPtr handle = control.Handle;
            foreach (Control child in control.Controls) Prepare(child);
            control.PerformLayout();
        }
        private static void Install(string root, string desktop, string startMenu, Action<string> progress)
        {
            root = Path.GetFullPath(root); desktop = Path.GetFullPath(desktop); startMenu = Path.GetFullPath(startMenu);
            Dictionary<string,string> originalData = SnapshotWords(root);
            if (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length > 0 && !File.Exists(Path.Combine(root, "installation.json")))
                throw new InvalidOperationException("安装目录已有其他文件，请选择空目录；未覆盖任何文件");
            Directory.CreateDirectory(root); UpdateFiles.RejectLink(root);
            UpdateFiles.AtomicJson(Path.Combine(root, "installation.json"), new { Product = "HoverLex", InstalledAt = DateTime.UtcNow.ToString("o") });
            UpdateEngine engine = new UpdateEngine(UpdateFiles.ResourceText("update-public-key"));
            ReleaseInfo release = engine.Verify(UpdateFiles.ResourceText("initial-release"));
            string archive = UpdateFiles.Under(root, "installer-package-" + Guid.NewGuid().ToString("N") + ".zip");
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("initial-package"))
            using (Stream output = new FileStream(archive, FileMode.CreateNew)) resource.CopyTo(output);
            InstalledRelease installed = engine.InstallPackage(root, release, archive, progress);
            string source = UpdateFiles.ResourceText("default-channel");
            if (!File.Exists(Path.Combine(root, "update-channel.json"))) File.WriteAllText(Path.Combine(root, "update-channel.json"), source, new UTF8Encoding(false));
            string versionRoot = UpdateFiles.Under(root, installed.Directory);
            string launcherSource = Path.Combine(versionRoot, "HoverLexLauncher.exe");
            string launcherTarget = Path.Combine(root, "HoverLexLauncher.exe");
            RefreshLauncher(launcherSource, launcherTarget, progress);
            // 更新只切换版本目录，生词存放在固定目录内。
            Directory.CreateDirectory(Path.Combine(root, "UserData"));
            MigrateWords(root);
            progress("正在创建桌面快捷方式…");
            Directory.CreateDirectory(desktop);
            CreateShortcut(desktop, launcherTarget, root, Path.Combine(versionRoot, "HoverLex.exe"));
            Directory.CreateDirectory(startMenu);
            CreateShortcut(startMenu, launcherTarget, root, Path.Combine(versionRoot, "HoverLex.exe"));
            foreach (KeyValuePair<string,string> item in originalData)
                if (!File.Exists(item.Key) || UpdateFiles.Hash(item.Key) != item.Value) throw new InvalidDataException("生词或设置校验失败，原文件内容发生变化：" + Path.GetFileName(item.Key));
        }
        private static Dictionary<string,string> SnapshotWords(string root)
        {
            Dictionary<string,string> hashes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            string directory = Path.Combine(root,"UserData");
            if (Directory.Exists(directory))
            {
                UpdateFiles.RejectLink(directory);
                foreach (string file in Directory.GetFiles(directory))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("个人数据不能是符号链接");
                    hashes.Add(file,UpdateFiles.Hash(file));
                }
            }
            return hashes;
        }
        private static void MigrateWords(string root)
        {
            string original = UpdateFiles.ResourceText("migration-source").Trim();
            if (!Directory.Exists(original)) return;
            foreach (string name in new string[] { "settings.json", "settings.json.bak", "words.json", "words.json.bak" })
            {
                string source = Path.Combine(original, name), destination = Path.Combine(root, "UserData", name);
                if (File.Exists(source) && !File.Exists(destination)) File.Copy(source, destination);
            }
        }
        private static void RefreshLauncher(string source, string target, Action<string> progress)
        {
            if (!File.Exists(target)) { File.Copy(source,target); return; }
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("启动器不能是符号链接");
            if (UpdateFiles.Hash(source) == UpdateFiles.Hash(target)) return;
            if (System.Diagnostics.FileVersionInfo.GetVersionInfo(target).FileDescription != "HoverLex")
                throw new InvalidDataException("启动器位置已有其他文件，未覆盖该文件");
            string staged = target + ".new-" + Guid.NewGuid().ToString("N");
            string backup = target + ".previous-" + Guid.NewGuid().ToString("N");
            File.Copy(source,staged);
            try { File.Replace(staged,target,backup,true); }
            catch (IOException) { progress("启动器正在使用，暂时保留已有启动器。"); }
            // 保留旧启动器备份，不修改个人数据。
        }
        private static void CreateShortcut(string desktop, string launcher, string root, string icon)
        {
            string preferred = Path.Combine(desktop, "HoverLex 鼠标取词.lnk");
            string path = preferred;
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("Windows 快捷方式组件不可用");
            object shell = Activator.CreateInstance(shellType), shortcut = null;
            try
            {
                // 同一启动器的其他快捷方式只更新图标，保留名称、参数和目标。
                foreach(string existingPath in Directory.GetFiles(desktop,"*.lnk")) {
                    object existingLink=shellType.InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[] { existingPath });
                    try {
                        Type existingType=existingLink.GetType();
                        string target=(string)existingType.InvokeMember("TargetPath",BindingFlags.GetProperty,null,existingLink,new object[0]);
                        if(!String.Equals(target,launcher,StringComparison.OrdinalIgnoreCase)) continue;
                        existingType.InvokeMember("IconLocation",BindingFlags.SetProperty,null,existingLink,new object[] { icon+",0" });
                        existingType.InvokeMember("Save",BindingFlags.InvokeMethod,null,existingLink,new object[0]);
                    } finally { Marshal.FinalReleaseComObject(existingLink); }
                }
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type type = shortcut.GetType();
                if (File.Exists(preferred))
                {
                    string existing = (string)type.InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, new object[0]);
                    if (String.Equals(existing, launcher, StringComparison.OrdinalIgnoreCase))
                    {
                        type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { icon + ",0" });
                        type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, new object[0]);
                        return;
                    }
                    Marshal.FinalReleaseComObject(shortcut); shortcut = null;
                    path = Path.Combine(desktop, "HoverLex 鼠标取词 " + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".lnk");
                    shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path }); type = shortcut.GetType();
                }
                type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { launcher });
                type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { root });
                type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "HoverLex 鼠标取词 · 打开时自动检查更新" });
                type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { icon + ",0" });
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, new object[0]);
            }
            finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
        }
    }
}
