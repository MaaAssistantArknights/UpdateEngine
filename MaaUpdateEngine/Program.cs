using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Formats.Tar;
using System.Diagnostics;
using System.Text.Json;

namespace MaaUpdateEngine
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length != 4)
            {
                Console.Error.WriteLine($"Usage: {Environment.GetCommandLineArgs()[0]} workdir manifest.json package_dir update_package");
                return;
            }

            var workdir = Path.GetFullPath(args[0]);

            var manifest = JsonSerializer.Deserialize<PackageManifest>(
                File.ReadAllBytes(args[1]),
                new JsonSerializerOptions()
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                })
                ?? throw new Exception("invalid manifest");
            var pkgdir = Path.GetFullPath(args[2]);

            var updpkg_name = args[3];
            UpdatePackage updpkg;
            if (updpkg_name.StartsWith("http"))
            {
                updpkg = await UpdatePackage.CacheAndOpenAsync(await HttpRandomAccessFile.OpenAsync(updpkg_name), manifest, Path.Combine(workdir, "package_cache"), default);
            }
            else
            {
                updpkg = await UpdatePackage.OpenAsync(updpkg_name, default);
            }
            using (updpkg)
            {
                using var session = new UpdateSession(manifest, pkgdir, workdir);
                session.AddPackage(updpkg);
                session.Commit();
            }
        }
    }
}
