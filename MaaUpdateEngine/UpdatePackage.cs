using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MaaUpdateEngine
{
    internal class UpdatePackage
    {
        enum CompressionType
        {
            Gzip,
            Zstandard
        }
        private const int FirstSegmentSize = 65536;

        private const int MagicGzipLength = 29;
        private const int MagicZstdLength = 16;
        private static ReadOnlySpan<byte> MagicGzipBegin => [0x1F, 0x8B, 0x08, 0x10, 0x4d, 0x55, 0x45, 0x31, 0x00, 0xFF];
        private static ReadOnlySpan<byte> MagicGzipEnd => [0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        private static ReadOnlySpan<byte> MagicZstd => [0x5a, 0x2a, 0x4d, 0x18, 0x08, 0x00, 0x00, 0x00, 0x4d, 0x55, 0x45, 0x31];

        private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        private static (CompressionType, int headerLength, int manifestLength) ParsePackageHeader(ReadOnlySpan<byte> header)
        {
            if (header.Length >= MagicGzipLength && header.StartsWith(MagicGzipBegin) && header[..MagicGzipLength].EndsWith(MagicGzipEnd))
            {
                var lengthHex = header.Slice(MagicGzipBegin.Length, 8);
                var length = Convert.ToInt32(Encoding.ASCII.GetString(lengthHex), 16);
                return (CompressionType.Gzip, MagicGzipLength, length);
            }
            if (header.Slice(0, 12).SequenceEqual(MagicZstd))
            {
                var lengthBinary = header.Slice(12, 4);
                var length = BitConverter.ToInt32(lengthBinary);
                if (!BitConverter.IsLittleEndian)
                {
                    length = BinaryPrimitives.ReverseEndianness(length);
                }
                return (CompressionType.Zstandard, MagicZstdLength, length);
            }
            throw new InvalidOperationException("Invalid header");
        }

        private static bool IsPackageManifestEntry([NotNullWhen(true)] TarEntry? entry)
        {
            return entry != null &&
                   entry.EntryType == TarEntryType.RegularFile &&
                   entry.Name.StartsWith(".maa_update/packages/", StringComparison.Ordinal) &&
                   entry.Name.EndsWith("/manifest.json", StringComparison.Ordinal);
        }

        private static bool IsDeltaPackageManifestEntry([NotNullWhen(true)] TarEntry? entry)
        {
            return entry != null &&
                   entry.EntryType == TarEntryType.RegularFile &&
                   entry.Name.StartsWith(".maa_update/delta/", StringComparison.Ordinal) &&
                   entry.Name.EndsWith("/manifest.json", StringComparison.Ordinal);
        }

        private static bool IsChunkManifestEntry([NotNullWhen(true)] TarEntry? entry)
        {
            return entry != null &&
                   entry.EntryType == TarEntryType.RegularFile &&
                   entry.Name.StartsWith(".maa_update/delta/", StringComparison.Ordinal) &&
                   entry.Name.EndsWith("/chunk_manifest.json", StringComparison.Ordinal);
        }

        private static Stream GetDecompressor(CompressionType compressionType, Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            return compressionType switch
            {
                CompressionType.Gzip => new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true),
                CompressionType.Zstandard => new ZstdSharp.DecompressionStream(stream, leaveOpen: true),
                _ => throw new InvalidOperationException("Invalid compression type")
            };
        }

        private static (HashAlgorithm, byte[]) ParseHashString(string hashStr)
        {
            if (hashStr.StartsWith("sha256:"))
            {
                var hasher = SHA256.Create();
                var hash = Convert.FromHexString(hashStr[7..]);
                if (hash.Length != hasher.HashSize / 8)
                {
                    throw new InvalidOperationException("Invalid hash length");
                }
                return (hasher, hash);
            }
            throw new NotSupportedException();
        }

        public static async Task ApplyPackage(IRandomAccessFile file, PackageManifest currentPackage, CancellationToken ct)
        {
            var buf = ArrayPool<byte>.Shared.Rent(FirstSegmentSize);
            var len = await file.ReadAtAsync(0, buf, ct);
            var valid_buf = buf.AsMemory(0, len);

            var (comp, header_len, manifest_len) = ParsePackageHeader(valid_buf.Span);

            var sans_header = valid_buf.Slice(header_len);

            if (sans_header.Length < manifest_len)
            {
                // TODO: read more data
                throw new InvalidOperationException("Invalid header");
            }

            var manifest_chunk = sans_header.Slice(0, manifest_len);
            var tr = new TarReader(GetDecompressor(comp, new ModernMemoryStream(manifest_chunk)));
            var entry = tr.GetNextEntry();
            if (!IsPackageManifestEntry(entry))
            {
                throw new InvalidDataException("Invalid manifest chunk");
            }

            static T? DeserializeFromEntry<T>(TarEntry entry)
            {
                var stream = entry.DataStream;
                if (stream == null)
                {
                    throw new InvalidDataException("Invalid manifest chunk");
                }
                return JsonSerializer.Deserialize<T>(stream, jsonOptions);
            }

            var package_manifest = DeserializeFromEntry<PackageManifest>(entry);

            if (package_manifest == null)
            {
                throw new InvalidDataException("Invalid package manifest");
            }

            if (package_manifest.Name != currentPackage.Name)
            {
                throw new InvalidDataException("package name mismatch");
            }

            if (package_manifest.Variant != currentPackage.Variant)
            {
                throw new InvalidDataException("package variant mismatch");
            }

            if (package_manifest.Version == currentPackage.Version)
            {
                //throw new InvalidDataException("package version mismatch");
                return;
            }

            entry = tr.GetNextEntry();
            if (entry == null)
            {
                // TODO: not delta package
                throw new NotImplementedException();
            }
            if (!IsDeltaPackageManifestEntry(entry))
            {
                throw new InvalidDataException("Invalid manifest chunk");
            }
            var delta_manifest = DeserializeFromEntry<DeltaPackageManifest>(entry);

            if (delta_manifest == null)
            {
                throw new InvalidDataException("Invalid delta package manifest");
            }

            var apply_chunks = delta_manifest.Chunks.Where(c => c.Target.EnumerateArray().Select(x=>x.GetString()).Contains(currentPackage.Version)).ToArray();


            var chunks_offset = header_len + manifest_len;
            // TODO: apply chunks

            // maximum position relative to the first chunk
            var maxpos = apply_chunks.Select(c => c.Offset + c.Size).Max();

            using var cache_file = File.OpenWrite($"cache/{currentPackage.Name}_{currentPackage.Version}.tmp");
            var fd = cache_file.SafeFileHandle;

            await file.CopyToAsync(chunks_offset, maxpos, cache_file, ct);

            var chunk_streams = apply_chunks.Select(c => new RandomAccessSubStream(fd, c.Offset, c.Size, false)).ToArray();

            var patch_files = new Dictionary<string, List<(string fromVersion, PatchFile patchFile)>>();
            

            for (var i = apply_chunks.Length - 1; i >= 0; i--)
            {
                var chunk_meta = apply_chunks[i];
                var chunk_stream = chunk_streams[i];

                var (hasher, expect_hash) = ParseHashString(chunk_meta.Hash);

                var actual_hash = hasher.ComputeHash(new RandomAccessSubStream(fd, chunk_meta.Offset, chunk_meta.Size, false));

                if (!actual_hash.SequenceEqual(expect_hash))
                {
                    throw new InvalidDataException("Invalid chunk hash");
                }

                chunk_stream.Position = 0;

                var tf = new TarReader(chunk_stream, true);
                var chunk_manifest_entry = tf.GetNextEntry();
                if (!IsChunkManifestEntry(chunk_manifest_entry))
                {
                    throw new InvalidDataException("Invalid chunk manifest");
                }

                var chunk_manifest = DeserializeFromEntry<ChunkManifest>(chunk_manifest_entry);

                if (chunk_manifest == null)
                {
                    throw new InvalidDataException("Invalid chunk manifest");
                }

                // process removed files
                foreach (var rf in chunk_manifest.RemoveFiles ?? Array.Empty<string>())
                {
                    // TODO: safe file operation
                    File.Delete(rf);
                }

                // process patch files
                var patch_data_names_set = new HashSet<string>();
                foreach (var pf in chunk_manifest.PatchFiles ?? Array.Empty<PatchFile>())
                {
                    patch_data_names_set.Add(pf.Patch);
                    if (chunk_manifest.PatchBase == currentPackage.Version)
                    {
                        // initialize patch list
                        var list = new List<(string, PatchFile)>();
                        patch_files[pf.File] = list;
                        list.Add((currentPackage.Version, pf));
                    }
                    else
                    {
                        if (patch_files.TryGetValue(pf.File, out var list))
                        {
                            // append patch list if available
                            if (list[^1].patchFile.NewVersion == chunk_manifest.PatchBase)
                            {
                                list.Add((chunk_manifest.PatchBase, pf));
                            }
                        }
                    }
                }

                // process replaced files
                TarEntry? file_entry;
                while ((file_entry = tf.GetNextEntry()) != null)
                {
                    // skip extracting patch files
                    if (patch_data_names_set.Contains(file_entry.Name))
                    {
                        continue;
                    }
                    if (file_entry.EntryType == TarEntryType.RegularFile)
                    {
                        // TODO: safe file operation
                        var file_path = file_entry.Name;
                        await file_entry.ExtractToFileAsync(file_path, true, ct);
                    }
                }
            }

            // TODO: apply patch series
            foreach (var (file_to_patch, patch_series) in patch_files)
            {
                throw new NotImplementedException();

            }

        }
    }
}
