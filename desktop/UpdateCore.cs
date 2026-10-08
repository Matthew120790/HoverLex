using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HoverLex.Desktop
{
    public sealed class ReleaseInfo
    {
        public string Version;
        public long Revision;
        public string Package;
        public long Size;
        public string Sha256;
        public string AppSha256;
    }
    public sealed class SignedRelease { public string Payload; public string Signature; }
    public sealed class InstalledRelease
    {
        public string Version;
        public long Revision;
        public string Sha256;
        public string AppSha256;
        public string Directory;
    }
    public sealed class UpdateChannel { public string Source; }
    public sealed class UpdateResult
    {
        public bool Updated;
        public string Message;
        public InstalledRelease Current;
    }
    public static class UpdateFiles
    {
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        public static string Under(string root, string relative)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("路径必须位于软件目录内");
            string result = Path.GetFullPath(Path.Combine(fullRoot, relative));
            if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径越出了软件目录");
            return result;
        }
        public static string Hash(string path)
        {
            using (Stream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public static void AtomicJson(string path, object value)
        {
            string temporary = path + ".new-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, Json.Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        public static string ResourceText(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) throw new InvalidDataException("缺少资源：" + name);
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }
        public static string SafeName(string name)
        {
            string normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith("HoverLex/", StringComparison.Ordinal)) throw new InvalidDataException("更新包目录无效");
            string relative = normalized.Substring(9);
            foreach (string part in relative.Split('/'))
                if (part == "." || part == ".." || part.TrimEnd(' ', '.') != part || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("更新包包含不安全的文件路径");
            if (relative.StartsWith("UserData/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("versions/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包不能修改生词或已有版本");
            if (!Allowed(relative)) throw new InvalidDataException("更新包包含未知文件：" + relative);
            return relative.Replace('/', Path.DirectorySeparatorChar);
        }
        private static bool Allowed(string name)
        {
            if (name == "HoverLex.exe" || name == "HoverLex.exe.config" || name == "HoverLexLauncher.exe" || name == "HoverLexLauncher.exe.config" || name == "EnglishProofreader.zip" || name == "README.md" || name == "THIRD-PARTY-NOTICES.md" || name == "LICENSE" || name == "update-channel.json") return true;
            return name == "Dictionary/dictionary.idx" || name == "Dictionary/dictionary.dat" || name == "Dictionary/ECDICT-LICENSE.txt" || name == "Dictionary/source.json";
        }
        public static void RejectLink(string directory)
        {
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("软件目录不能是符号链接或目录联接");
        }
    }

    public sealed class UpdateEngine
    {
        private const long MaxPackage = 160L * 1024 * 1024;
        private readonly string publicKey;
        public UpdateEngine(string key) { publicKey = key; }
        public ReleaseInfo Verify(string envelope)
        {
            if (envelope.Length > 256 * 1024) throw new InvalidDataException("更新清单过大");
            SignedRelease signed = UpdateFiles.Json.Deserialize<SignedRelease>(envelope.TrimStart('\ufeff', ' ', '\r', '\n', '\t'));
            if (signed == null || signed.Payload == null || signed.Signature == null) throw new InvalidDataException("更新清单缺少签名");
            byte[] payload = Convert.FromBase64String(signed.Payload), signature = Convert.FromBase64String(signed.Signature);
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            using (SHA256 sha = SHA256.Create())
            {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(publicKey);
                if (!rsa.VerifyData(payload, sha, signature)) throw new InvalidDataException("更新清单签名无效");
            }
            ReleaseInfo release = UpdateFiles.Json.Deserialize<ReleaseInfo>(Encoding.UTF8.GetString(payload).TrimStart('\ufeff'));
            Version parsed;
            if (release == null || !Regex.IsMatch(release.Version ?? "", @"^\d+\.\d+\.\d+$") || !Version.TryParse(release.Version, out parsed) || release.Revision <= 0 || release.Size <= 0 || release.Size > MaxPackage || !Hex(release.Sha256) || !Hex(release.AppSha256) || String.IsNullOrWhiteSpace(release.Package))
                throw new InvalidDataException("更新清单字段无效");
            return release;
        }
        private static bool Hex(string value) { return Regex.IsMatch(value ?? "", "^[a-f0-9]{64}$"); }
        public static bool IsNewer(ReleaseInfo incoming, InstalledRelease current)
        {
            if (current == null) return true;
            int order = new Version(incoming.Version).CompareTo(new Version(current.Version));
            return (order > 0 || (order == 0 && incoming.Revision > current.Revision)) && incoming.Sha256 != current.Sha256;
        }
        public InstalledRelease ReadCurrent(string root)
        {
            foreach (string name in new string[] { "current.json", "current.json.bak" })
            {
                try
                {
                    InstalledRelease state = UpdateFiles.Json.Deserialize<InstalledRelease>(File.ReadAllText(Path.Combine(root, name), Encoding.UTF8));
                    Version version;
                    if (state == null || !Version.TryParse(state.Version, out version) || state.Revision <= 0 || !state.Directory.StartsWith("versions/", StringComparison.Ordinal) || !Hex(state.AppSha256)) continue;
                    string directory = UpdateFiles.Under(root, state.Directory);
                    UpdateFiles.RejectLink(root); UpdateFiles.RejectLink(Path.Combine(root, "versions")); UpdateFiles.RejectLink(directory);
                    string executable = Path.Combine(directory, "HoverLex.exe");
                    if (File.Exists(executable) && File.Exists(Path.Combine(directory, "Dictionary", "dictionary.idx")) && File.Exists(Path.Combine(directory, "Dictionary", "dictionary.dat")) && UpdateFiles.Hash(executable) == state.AppSha256) return state;
                }
                catch { }
            }
            return null;
        }
        public UpdateResult Check(string root, string source, Action<string> progress)
        {
            InstalledRelease current = ReadCurrent(root);
            try
            {
                progress("正在检查最新版本…");
                string envelope = Encoding.UTF8.GetString(ReadSource(source, 256 * 1024));
                ReleaseInfo release = Verify(envelope);
                if (!IsNewer(release, current)) return new UpdateResult { Current = current, Message = "已是最新版本 " + (current == null ? "" : current.Version) };
                progress("正在下载并验证新版 " + release.Version + "…");
                string packageSource = Resolve(source, release.Package);
                string cache = UpdateFiles.Under(root, "downloads"); Directory.CreateDirectory(cache); UpdateFiles.RejectLink(cache);
                string archive = Path.Combine(cache, "update-" + Guid.NewGuid().ToString("N") + ".zip");
                CopySource(packageSource, archive, release.Size);
                InstalledRelease installed = InstallPackage(root, release, archive, progress);
                return new UpdateResult { Updated = true, Current = installed, Message = "已自动更新到 " + installed.Version };
            }
            catch (Exception error)
            {
                if (current == null) throw new InvalidOperationException("没有可用的已安装版本：" + error.Message, error);
                return new UpdateResult { Current = current, Message = "更新检查失败，继续使用 " + current.Version + "（" + error.Message + "）" };
            }
        }
        public InstalledRelease InstallPackage(string root, ReleaseInfo release, string archive, Action<string> progress)
        {
            root = Path.GetFullPath(root); Directory.CreateDirectory(root); UpdateFiles.RejectLink(root);
            if (new FileInfo(archive).Length != release.Size || UpdateFiles.Hash(archive) != release.Sha256) throw new InvalidDataException("更新包校验失败，旧版本未改动");
            InstalledRelease previous = ReadCurrent(root);
            if (previous != null && !IsNewer(release, previous)) return previous;
            string versions = UpdateFiles.Under(root, "versions"); Directory.CreateDirectory(versions); UpdateFiles.RejectLink(versions);
            string folderName = release.Version + "-" + release.Revision + "-" + release.Sha256.Substring(0, 12);
            string final = UpdateFiles.Under(versions, folderName);
            if (Directory.Exists(final)) throw new InvalidDataException("版本目录已经存在，未覆盖原有文件");
            string stage = UpdateFiles.Under(versions, "staging-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            progress("正在安装新版，生词本保持原位…");
            long total = 0;
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (ZipArchive zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count > 100) throw new InvalidDataException("更新包文件过多");
                foreach (ZipArchiveEntry item in zip.Entries)
                {
                    if (item.FullName.EndsWith("/")) continue;
                    string relative = UpdateFiles.SafeName(item.FullName);
                    if (!names.Add(relative)) throw new InvalidDataException("更新包包含重复文件");
                    total += item.Length;
                    if (item.Length < 0 || total > 240L * 1024 * 1024) throw new InvalidDataException("更新包解压体积超出限制");
                    string target = UpdateFiles.Under(stage, relative); Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (Stream input = item.Open())
                    using (Stream output = new FileStream(target, FileMode.CreateNew)) CopyBounded(input, output, item.Length);
                }
            }
            string app = Path.Combine(stage, "HoverLex.exe");
            if (!File.Exists(app) || !File.Exists(Path.Combine(stage, "Dictionary", "dictionary.dat")) || !File.Exists(Path.Combine(stage, "Dictionary", "dictionary.idx")) || UpdateFiles.Hash(app) != release.AppSha256)
                throw new InvalidDataException("更新包缺少必需文件或程序校验失败");
            string actualVersion = FileVersionInfo.GetVersionInfo(app).FileVersion;
            if (actualVersion != release.Version + ".0") throw new InvalidDataException("更新包中的程序版本与清单不一致");
            UpdateFiles.RejectLink(stage);
            // 两个路径都已限定在 versions 内；完整验证后才切换版本指针。
            Directory.Move(stage, final);
            InstalledRelease state = new InstalledRelease { Version = release.Version, Revision = release.Revision, Sha256 = release.Sha256, AppSha256 = release.AppSha256, Directory = "versions/" + folderName };
            UpdateFiles.AtomicJson(Path.Combine(root, "current.json"), state);
            return state;
        }
        public static string Resolve(string source, string package)
        {
            Uri sourceUri;
            if (Uri.TryCreate(source, UriKind.Absolute, out sourceUri) && sourceUri.Scheme == "https")
            {
                Uri target = new Uri(sourceUri, package);
                if (target.Scheme != "https") throw new InvalidDataException("网络更新仅支持 HTTPS");
                return target.AbsoluteUri;
            }
            if (!Path.IsPathRooted(source)) throw new InvalidDataException("本机更新清单必须使用完整路径");
            if (Path.IsPathRooted(package)) return Path.GetFullPath(package);
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), package));
        }
        private static Stream OpenSource(string source)
        {
            Uri uri;
            if (Path.IsPathRooted(source) && !source.StartsWith("\\\\")) return File.OpenRead(source);
            if (!Uri.TryCreate(source, UriKind.Absolute, out uri) || uri.Scheme != "https") throw new InvalidDataException("更新来源须为本机文件或 HTTPS 地址");
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri); request.Timeout = 6000; request.ReadWriteTimeout = 6000;
            request.UserAgent = "HoverLex-Updater/0.2"; request.MaximumAutomaticRedirections = 5;
            HttpWebResponse response = (HttpWebResponse)request.GetResponse();
            if (response.ResponseUri.Scheme != "https") { response.Close(); throw new InvalidDataException("更新地址跳转到了非 HTTPS 链接"); }
            return new ResponseStream(response);
        }
        private static byte[] ReadSource(string source, long maximum)
        {
            using (Stream input = OpenSource(source))
            using (MemoryStream data = new MemoryStream())
            {
                Stopwatch clock = Stopwatch.StartNew(); byte[] buffer = new byte[8192]; int count; long total = 0;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    total += count; if (total > maximum || clock.ElapsedMilliseconds > 12000) throw new InvalidDataException("更新清单过大或读取超时");
                    data.Write(buffer, 0, count);
                }
                return data.ToArray();
            }
        }
        private static void CopySource(string source, string target, long size)
        {
            using (Stream input = OpenSource(source))
            using (Stream output = new FileStream(target, FileMode.CreateNew)) CopyBounded(input, output, size);
        }
        private static void CopyBounded(Stream input, Stream output, long expected)
        {
            Stopwatch clock = Stopwatch.StartNew(); byte[] buffer = new byte[65536]; int count; long total = 0;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                total += count;
                if (total > expected || clock.ElapsedMilliseconds > 30000) throw new InvalidDataException("更新包体积不符或传输超时");
                output.Write(buffer, 0, count);
            }
            if (total != expected) throw new InvalidDataException("更新包下载不完整");
        }
        private sealed class ResponseStream : Stream
        {
            private readonly HttpWebResponse response;
            private readonly Stream inner;
            public ResponseStream(HttpWebResponse value) { response = value; inner = response.GetResponseStream(); }
            public override bool CanRead { get { return inner.CanRead; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return inner.Length; } }
            public override long Position { get { return inner.Position; } set { throw new NotSupportedException(); } }
            public override int Read(byte[] buffer, int offset, int count) { return inner.Read(buffer, offset, count); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); response.Close(); } base.Dispose(disposing); }
        }
    }
}
