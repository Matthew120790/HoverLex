using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace HoverLex.Desktop
{
    public static class UpdaterTests
    {
        private static List<string> lines = new List<string>();
        private static int failed;
        private static void Check(bool condition, string name) { lines.Add((condition ? "PASS " : "FAIL ") + name); if (!condition) failed++; }
        private static bool Throws(Action action) { try { action(); return false; } catch { return true; } }
        private static ReleaseInfo Clone(ReleaseInfo source)
        {
            return UpdateFiles.Json.Deserialize<ReleaseInfo>(UpdateFiles.Json.Serialize(source));
        }
        private static string Sign(ReleaseInfo release, string privateKey)
        {
            byte[] payload = Encoding.UTF8.GetBytes(UpdateFiles.Json.Serialize(release));
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            using (SHA256 sha = SHA256.Create())
            {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(privateKey);
                return UpdateFiles.Json.Serialize(new SignedRelease { Payload = Convert.ToBase64String(payload), Signature = Convert.ToBase64String(rsa.SignData(payload, sha)) });
            }
        }
        public static int Run(string testRoot, string privateKeyPath)
        {
            testRoot = Path.GetFullPath(testRoot); Directory.CreateDirectory(testRoot);
            string run = UpdateFiles.Under(testRoot, "run-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
            try
            {
                string privateKey = File.ReadAllText(privateKeyPath, Encoding.UTF8);
                UpdateChannel channel = UpdateFiles.Json.Deserialize<UpdateChannel>(UpdateFiles.ResourceText("default-channel"));
                string originalEnvelope = File.ReadAllText(channel.Source, Encoding.UTF8);
                UpdateEngine engine = new UpdateEngine(UpdateFiles.ResourceText("update-public-key"));
                ReleaseInfo initial = engine.Verify(originalEnvelope);
                string originalZip = UpdateEngine.Resolve(channel.Source, initial.Package);
                string expectedVersion = new Version(System.Diagnostics.FileVersionInfo.GetVersionInfo(System.Reflection.Assembly.GetExecutingAssembly().Location).FileVersion).ToString(3);
                Check(initial.Version == expectedVersion, "release signature and version verified");
                Check(engine.Verify("\ufeff" + originalEnvelope).Sha256 == initial.Sha256, "UTF-8 BOM accepted in signed envelope");
                SignedRelease changed = UpdateFiles.Json.Deserialize<SignedRelease>(originalEnvelope);
                changed.Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"Version\":\"9.9.9\"}"));
                Check(Throws(() => engine.Verify(UpdateFiles.Json.Serialize(changed))), "tampered manifest rejected");
                Check(Throws(() => engine.Verify("{}")), "unsigned manifest rejected");
                Check(UpdateEngine.IsNewer(new ReleaseInfo { Version = "0.10.0", Revision = 2, Sha256 = "b" }, new InstalledRelease { Version = "0.9.0", Revision = 1, Sha256 = "a" }), "numeric version ordering");
                Check(!UpdateEngine.IsNewer(new ReleaseInfo { Version = "0.1.0", Revision = Int64.MaxValue, Sha256 = "b" }, new InstalledRelease { Version = "0.2.0", Revision = 1, Sha256 = "a" }), "downgrades rejected");
                Check(UpdateFiles.SafeName("HoverLex/EnglishProofreader.zip")=="EnglishProofreader.zip", "the signed English proofreading component is accepted");
                foreach (string unsafeName in new string[] { "HoverLex/../../escape.txt", "HoverLex/UserData/words.json", "HoverLex/C:/escape.txt", "HoverLex/README.md:stream", "HoverLex/README.md.", "elsewhere/HoverLex.exe" })
                    Check(Throws(() => UpdateFiles.SafeName(unsafeName)), "unsafe archive path rejected: " + unsafeName);
                string install = Path.Combine(run, "installed");
                InstalledRelease state = engine.InstallPackage(install, initial, originalZip, delegate { });
                Check(state.Version == initial.Version && engine.ReadCurrent(install) != null, "first installation activates verified release");
                string words = Path.Combine(install, "UserData", "words.json"); Directory.CreateDirectory(Path.GetDirectoryName(words));
                File.WriteAllText(words, "[{\"Word\":\"curiosity\",\"Meaning\":\"keep me\"}]", Encoding.UTF8);
                string preserved = File.ReadAllText(words, Encoding.UTF8);
                string feed = Path.Combine(run, "latest.json"); File.WriteAllText(feed, originalEnvelope, Encoding.UTF8);
                ReleaseInfo localInitial = Clone(initial); localInitial.Package = originalZip;
                File.WriteAllText(feed, Sign(localInitial, privateKey), Encoding.UTF8);
                UpdateResult same = engine.Check(install, feed, delegate { });
                Check(!same.Updated && same.Current.Revision == initial.Revision, "same release is not reinstalled");
                string rebuilt = Path.Combine(run, "rebuilt.zip");
                using (ZipArchive input = ZipFile.OpenRead(originalZip))
                using (ZipArchive output = ZipFile.Open(rebuilt, ZipArchiveMode.Create))
                {
                    foreach (ZipArchiveEntry item in input.Entries)
                    {
                        ZipArchiveEntry copy = output.CreateEntry(item.FullName, CompressionLevel.Fastest);
                        using (Stream destination = copy.Open())
                        {
                            using (Stream source = item.Open()) source.CopyTo(destination);
                            if (item.FullName == "HoverLex/README.md") { byte[] marker = Encoding.UTF8.GetBytes("\nUpdate test build.\n"); destination.Write(marker, 0, marker.Length); }
                        }
                    }
                }
                ReleaseInfo next = Clone(initial); next.Revision++; next.Package = rebuilt; next.Size = new FileInfo(rebuilt).Length; next.Sha256 = UpdateFiles.Hash(rebuilt);
                File.WriteAllText(feed, Sign(next, privateKey), Encoding.UTF8);
                UpdateResult update = engine.Check(install, feed, delegate { });
                Check(update.Updated && update.Current.Sha256 == next.Sha256, "new build downloaded, verified, and activated: " + update.Message);
                Check(File.Exists(Path.Combine(install, "current.json.bak")) && Directory.Exists(UpdateFiles.Under(install, state.Directory)), "previous version and pointer backup retained");
                Check(File.ReadAllText(words, Encoding.UTF8) == preserved, "vocabulary preserved across updates");
                string pointer = File.ReadAllText(Path.Combine(install, "current.json"), Encoding.UTF8);
                ReleaseInfo corrupt = Clone(next); corrupt.Revision++; corrupt.Sha256 = new string('0', 64);
                File.WriteAllText(feed, Sign(corrupt, privateKey), Encoding.UTF8);
                UpdateResult rejected = engine.Check(install, feed, delegate { });
                Check(!rejected.Updated && rejected.Current.Sha256 == next.Sha256 && File.ReadAllText(Path.Combine(install, "current.json"), Encoding.UTF8) == pointer, "bad package hash leaves active version unchanged");
                string truncated = Path.Combine(run, "truncated.zip"); File.WriteAllBytes(truncated, new byte[] { 1, 2, 3 });
                ReleaseInfo shortRelease = Clone(next); shortRelease.Revision++; shortRelease.Package = truncated;
                File.WriteAllText(feed, Sign(shortRelease, privateKey), Encoding.UTF8);
                Check(!engine.Check(install, feed, delegate { }).Updated, "truncated download falls back to old version");
                UpdateResult offline = engine.Check(install, Path.Combine(run, "missing-feed.json"), delegate { });
                Check(offline.Current.Sha256 == next.Sha256 && !offline.Updated, "missing update source still opens installed version");
                string evil = Path.Combine(run, "evil.zip");
                using (ZipArchive zip = ZipFile.Open(evil, ZipArchiveMode.Create))
                using (Stream writer = zip.CreateEntry("HoverLex/../../escape.txt").Open()) writer.WriteByte(1);
                ReleaseInfo evilRelease = Clone(next); evilRelease.Revision++; evilRelease.Package = evil; evilRelease.Size = new FileInfo(evil).Length; evilRelease.Sha256 = UpdateFiles.Hash(evil);
                File.WriteAllText(feed, Sign(evilRelease, privateKey), Encoding.UTF8);
                UpdateResult evilResult = engine.Check(install, feed, delegate { });
                Check(!evilResult.Updated && File.ReadAllText(Path.Combine(install, "current.json"), Encoding.UTF8) == pointer && !File.Exists(Path.Combine(install, "escape.txt")), "path traversal never changes active release");
                File.WriteAllText(Path.Combine(install, "current.json"), "{broken", Encoding.UTF8);
                Check(engine.ReadCurrent(install).Sha256 == initial.Sha256, "damaged pointer falls back to valid previous version");
                File.WriteAllText(Path.Combine(install, "current.json"), pointer, Encoding.UTF8);
                Check(File.ReadAllText(words, Encoding.UTF8) == preserved, "failed updates do not modify vocabulary");
            }
            catch (Exception error) { Check(false, error.ToString()); }
            File.WriteAllLines(Path.Combine(testRoot, "updater-tests.txt"), lines, Encoding.UTF8);
            return failed == 0 ? 0 : 1;
        }
    }
}
