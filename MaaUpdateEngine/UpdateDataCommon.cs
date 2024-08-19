using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;

namespace MaaUpdateEngine;

internal enum PackageCompressionType
{
    Gzip,
    Zstandard
}

internal static class UpdateDataCommon
{
    private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        TypeInfoResolver = SourceGenerationContext.Default
    };

    public static Stream OpenDecompressionStream(PackageCompressionType compressionType, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return compressionType switch
        {
            PackageCompressionType.Gzip => new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true),
            PackageCompressionType.Zstandard => new ZstdSharp.DecompressionStream(stream, leaveOpen: true),
            _ => throw new InvalidOperationException("Invalid compression type")
        };
    }

    public static bool IsPackageManifestEntry([NotNullWhen(true)] TarEntry? entry)
    {
        return entry != null &&
               entry.EntryType == TarEntryType.RegularFile &&
               entry.Name.StartsWith(".maa_update/packages/", StringComparison.Ordinal) &&
               entry.Name.EndsWith("/manifest.json", StringComparison.Ordinal);
    }

    public static bool IsDeltaPackageManifestEntry([NotNullWhen(true)] TarEntry? entry)
    {
        return entry != null &&
               entry.EntryType == TarEntryType.RegularFile &&
               entry.Name.StartsWith(".maa_update/delta/", StringComparison.Ordinal) &&
               entry.Name.EndsWith("/delta_manifest.json", StringComparison.Ordinal);
    }

    public static bool IsChunkManifestEntry([NotNullWhen(true)] TarEntry? entry)
    {
        return entry != null &&
               entry.EntryType == TarEntryType.RegularFile &&
               entry.Name.StartsWith(".maa_update/delta/", StringComparison.Ordinal) &&
               entry.Name.EndsWith("/chunk_manifest.json", StringComparison.Ordinal);
    }

    public static T? DeserializeFromEntry<T>(TarEntry entry)
    {
        var stream = entry.DataStream;
        if (stream == null)
        {
            throw new InvalidDataException("Invalid manifest chunk");
        }
        return JsonSerializer.Deserialize<T>(stream, jsonOptions);
    }
}
