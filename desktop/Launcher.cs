using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HoverLex.Desktop
{
    internal static class Launcher
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string root = AppDomain.CurrentDomain.BaseDirectory;
            if (args.Length >= 3 && args[0] == "--self-test") return UpdaterTests.Run(args[1], args[2]);
            if (args.Length >= 2 && args[0] == "--check-only")
            {
                root = Path.GetFullPath(args[1]);
                try
                {
                    UpdateResult result = Prepare(root, delegate { });
                    UpdateFiles.AtomicJson(Path.Combine(root, "last-update.json"), result);
                    return 0;
                }
                catch (Exception e) { File.WriteAllText(Path.Combine(root, "launcher-error.txt"), e.ToString(), Encoding.UTF8); return 1; }
            }
            using (WelcomeWindow splash = new WelcomeWindow("HoverLex", "读到哪里，查到哪里。", false))
            {
                Label label = splash.Message;
                label.Text = "正在检查最新版本…";
                if (args.Length == 2 && args[0] == "--preview")
                {
                    PreparePreview(splash);
                    using (Bitmap image = new Bitmap(splash.Width, splash.Height)) { splash.DrawToBitmap(image, new Rectangle(Point.Empty, splash.Size)); image.Save(args[1]); }
                    return 0;
                }
                splash.Shown += async delegate
                {
                    try
                    {
                        UpdateResult result = await Task.Run(() => Prepare(root, message => { if (!splash.IsDisposed) splash.BeginInvoke(new Action(() => label.Text = message)); }));
                        StartApp(root, result);
                    }
                    catch (Exception error) { MessageBox.Show("无法打开 HoverLex：" + error.Message, "HoverLex", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                    finally { splash.Close(); }
                };
                Application.Run(splash);
            }
            return 0;
        }
        private static void PreparePreview(Control control)
        {
            IntPtr handle = control.Handle;
            foreach (Control child in control.Controls) PreparePreview(child);
            control.PerformLayout();
        }
        public static UpdateResult Prepare(string root, Action<string> progress)
        {
            UpdateEngine engine = new UpdateEngine(UpdateFiles.ResourceText("update-public-key"));
            UpdateChannel channel;
            try
            {
                string channelPath = Path.Combine(root, "update-channel.json");
                channel = UpdateFiles.Json.Deserialize<UpdateChannel>(File.ReadAllText(channelPath, Encoding.UTF8));
                if (channel == null || String.IsNullOrWhiteSpace(channel.Source)) throw new InvalidDataException("缺少更新来源配置");
            }
            catch (Exception error)
            {
                InstalledRelease existing = engine.ReadCurrent(root);
                if (existing == null) throw;
                return new UpdateResult { Current = existing, Message = "更新配置不可用，继续使用 " + existing.Version + "（" + error.Message + "）" };
            }
            string mutexName;
            using (var sha = System.Security.Cryptography.SHA256.Create()) mutexName = "Local\\HoverLex.Update." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant()))).Replace("-", "");
            using (Mutex mutex = new Mutex(false, mutexName))
            {
                bool locked = false;
                try
                {
                    try { locked = mutex.WaitOne(8000); } catch (AbandonedMutexException) { locked = true; }
                    if (!locked)
                    {
                        InstalledRelease current = engine.ReadCurrent(root);
                        if (current == null) throw new InvalidOperationException("另一个启动程序正在安装，请稍后重试");
                        return new UpdateResult { Current = current, Message = "另一个启动程序正在检查更新，继续使用已有版本" };
                    }
                    UpdateResult result = engine.Check(root, channel.Source, progress);
                    try { UpdateFiles.AtomicJson(Path.Combine(root, "last-update.json"), result); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    return result;
                }
                finally { if (locked) mutex.ReleaseMutex(); }
            }
        }
        private static void StartApp(string root, UpdateResult result)
        {
            try
            {
                using (EventWaitHandle activate = EventWaitHandle.OpenExisting("Local\\HoverLex.Activate")) { activate.Set(); return; }
            }
            catch (WaitHandleCannotBeOpenedException) { }
            string directory = UpdateFiles.Under(root, result.Current.Directory);
            ProcessStartInfo info = new ProcessStartInfo(Path.Combine(directory, "HoverLex.exe")) { UseShellExecute = false, WorkingDirectory = directory };
            info.EnvironmentVariables["HOVERLEX_INSTALL_ROOT"] = Path.GetFullPath(root);
            info.EnvironmentVariables["HOVERLEX_UPDATE_STATUS"] = result.Message;
            Process.Start(info);
        }
    }
}
