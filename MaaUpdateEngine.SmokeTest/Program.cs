using System.Text.Json.Nodes;
using MaaUpdateEngine;

namespace MaaUpdateEngine.SmokeTest
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length != 4)
            {
                Console.Error.WriteLine($"Usage: {Environment.GetCommandLineArgs()[0]} workdir manifest.json package_dir update_package");
                Console.Error.WriteLine("  If update_package is a http/https/file URI, I/O accounting will be enabled");
                return;
            }

            var workdir = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(workdir);

            var manifest_doc = JsonNode.Parse(File.ReadAllBytes(args[1])) ?? throw new Exception("invalid manifest");

            var manifest = new PackageManifest
            {
                Name = manifest_doc["name"]?.GetValue<string>() ?? throw new Exception("missing name"),
                Version = manifest_doc["version"]?.GetValue<string>() ?? throw new Exception("missing version"),
                Variant = manifest_doc["variant"]?.GetValue<string>()
            };

            var pkgdir = Path.GetFullPath(args[2]);

            var updpkg_name = args[3];
            var progress_lock = new object();

            AccountingRandomAccessFile? f2 = null;
            UpdatePackage updpkg;
            if (File.Exists(updpkg_name))
            {
                updpkg = await UpdatePackage.OpenAsync(updpkg_name, default);
            }
            else
            {
                IRandomAccessFile f;
                if (updpkg_name.StartsWith("http"))
                {
                    f = await SimpleHttpRandomAccessFile.OpenAsync(updpkg_name);
                }
                else if (updpkg_name.StartsWith("file://"))
                {
                    var fs = File.OpenRead(updpkg_name.Substring(7));
                    f = new LocalRandomAccessFile(fs, false);
                }
                else
                {
                    throw new Exception("invalid update package name");
                }
                f2 = new AccountingRandomAccessFile(f);
                UpdatePackage.TransferProgress old_progress = default;
                var fetch_progress = new SyncProgress<UpdatePackage.TransferProgress>(x =>
                {
                    if (x == old_progress)
                    {
                        return;
                    }
                    old_progress = x;
                    Console.Write($"\rprogress: {x.BytesTransferred} / {x.TotalBytes} bytes transferred");
                    if (x.BytesTransferred == x.TotalBytes)
                    {
                        Console.WriteLine();
                    }
                });
                updpkg = await UpdatePackage.CacheAndOpenAsync(f2, manifest, Path.Combine(workdir, "package_cache"), fetch_progress, CancellationToken.None);
            }

            var old_add_progress = default(UpdateSession.AddPackageProgress);
            var add_progress = new SyncProgress<UpdateSession.AddPackageProgress>(x =>
            {
                if (x == old_add_progress)
                {
                    return;
                }
                old_add_progress = x;
                Console.WriteLine($"add package: [{(float)x.BytesConsumed/x.BytesToConsume:###%} {x.BytesConsumed}/{x.BytesToConsume}] {x.CurrentFile}");
                if (x.BytesConsumed == x.BytesToConsume && x.CurrentFile == null)
                {
                    Console.WriteLine();
                }
            });

            var commit_progress = new SyncProgress<UpdateSession.CommitProgress>(x =>
            {
                Console.WriteLine($"commit: [{x.NumberOfFilesUpdated}/{x.NumberOfFilesToUpdate}] {x.CurrentFile}");
                if (x.NumberOfFilesToUpdate == x.NumberOfFilesUpdated && x.CurrentFile == null)
                {
                    Console.WriteLine();
                }
            });

            using (updpkg)
            {
                using var session = new UpdateSession(manifest, pkgdir, workdir);
                session.AddPackage(updpkg, add_progress);
                session.Commit(commit_progress);
            }

            if (f2 != null)
            {
                Console.WriteLine($"remote file: {f2.IoCount} I/O requests, {f2.BytesTransferred} bytes transferred");
            }
        }

        private class SyncProgress<T>(Action<T> action) : IProgress<T>
        {
            public void Report(T value)
            {
                action(value);
            }
        }
    }


}
