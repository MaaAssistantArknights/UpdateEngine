using System.Text.Json;
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

            var manifest = JsonSerializer.Deserialize<PackageManifest>(
                File.ReadAllBytes(args[1]),
                new JsonSerializerOptions()
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                })
                ?? throw new Exception("invalid manifest");
            var pkgdir = Path.GetFullPath(args[2]);

            var updpkg_name = args[3];

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
                updpkg = await UpdatePackage.CacheAndOpenAsync(f2, manifest, Path.Combine(workdir, "package_cache"), default);
            }

            using (updpkg)
            {
                using var session = new UpdateSession(manifest, pkgdir, workdir);
                session.AddPackage(updpkg);
                session.Commit();
            }

            if (f2 != null)
            {
                Console.WriteLine($"remote file: {f2.IoCount} I/O requests, {f2.BytesTransferred} bytes transferred");
            }
        }
    }
}
