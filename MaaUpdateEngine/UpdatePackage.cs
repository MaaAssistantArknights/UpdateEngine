using System.Buffers;
using System.Buffers.Binary;
using System.Formats.Tar;
using System.Text;

using MaaUpdateEngine.Streams;

namespace MaaUpdateEngine
{
    using static UpdateDataCommon;

    public record class UpdatePackage : IDisposable
    {
        /// <summary>
        /// Represents the progress of a package transfer.
        /// </summary>
        /// <param name="BytesTransferred"></param>
        /// <param name="TotalBytes">Total bytes to transfer. A negative value indicates that the number total bytes are unknown.</param>
        public readonly record struct TransferProgress(long BytesTransferred, long TotalBytes);

        private const int FirstSegmentSize = 65536;

        private const int MagicGzipLength = 29;
        private const int MagicZstdLength = 16;
        private static ReadOnlySpan<byte> MagicGzipBegin => [0x1F, 0x8B, 0x08, 0x10, 0x4d, 0x55, 0x45, 0x31, 0x00, 0xFF];
        private static ReadOnlySpan<byte> MagicGzipEnd => [0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        private static ReadOnlySpan<byte> MagicZstd => [0x5a, 0x2a, 0x4d, 0x18, 0x08, 0x00, 0x00, 0x00, 0x4d, 0x55, 0x45, 0x31];

        internal ISubStreamFactory SubStreamFactory { get; }
        private bool leaveOpen;
        public PackageManifest Manifest { get; }

        internal DeltaPackageManifest PackageIndex { get; }

        internal long ChunksOffset { get; }

        internal PackageCompressionType CompressionType { get; }

        internal UpdatePackage(ISubStreamFactory fileStream, PackageManifest manifest, DeltaPackageManifest packageIndex, long chunksOffset, PackageCompressionType compressionType, bool leaveOpen = true)
        {
            SubStreamFactory = fileStream;
            Manifest = manifest;
            PackageIndex = packageIndex;
            ChunksOffset = chunksOffset;
            CompressionType = compressionType;
            this.leaveOpen = leaveOpen;
        }

        public static async Task<UpdatePackage> OpenAsync(string path, CancellationToken ct)
        {
            var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var file = new LocalRandomAccessFile(fileStream);
            return await InternalLoadPackageAsync(file, null, null, null, ct);
        }

        public static async Task<UpdatePackage> CacheAndOpenAsync(IRandomAccessFile file, PackageManifest currentPackage, string cachePath, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
        {
            return await InternalLoadPackageAsync(file, cachePath, currentPackage, progress, ct);
        }

        public void Close()
        {
            if (!leaveOpen)
            {
                (SubStreamFactory as IDisposable)?.Dispose();
            }
        }

        public void Dispose()
        {
            Close();
            GC.SuppressFinalize(this);
        }

        ~UpdatePackage()
        {
            Dispose();
        }

        internal Stream OpenChunk(Chunk chunk)
        {
            return SubStreamFactory.CreateSubStream(chunk.Offset + ChunksOffset, chunk.Size);
        }

        private static (PackageCompressionType, int headerLength, int manifestLength) ParsePackageHeader(ReadOnlySpan<byte> header)
        {
            if (header.Length >= MagicGzipLength && header.StartsWith(MagicGzipBegin) && header[..MagicGzipLength].EndsWith(MagicGzipEnd))
            {
                var lengthHex = header.Slice(MagicGzipBegin.Length, 8);
                var length = Convert.ToInt32(Encoding.ASCII.GetString(lengthHex), 16);
                return (PackageCompressionType.Gzip, MagicGzipLength, length);
            }
            if (header.Slice(0, 12).SequenceEqual(MagicZstd))
            {
                var lengthBinary = header.Slice(12, 4);
                var length = BitConverter.ToInt32(lengthBinary);
                if (!BitConverter.IsLittleEndian)
                {
                    length = BinaryPrimitives.ReverseEndianness(length);
                }
                return (PackageCompressionType.Zstandard, MagicZstdLength, length);
            }
            throw new InvalidOperationException("Invalid header");
        }

        private static async Task<UpdatePackage> InternalLoadPackageAsync(IRandomAccessFile file, string? cachePackagePath, PackageManifest? referencePackageManifest, IProgress<TransferProgress>? progress, CancellationToken ct)
        {
            if (cachePackagePath != null && referencePackageManifest == null)
            {
                throw new ArgumentNullException(nameof(referencePackageManifest));
            }

            long xferd = 0, total = -1;

            void report_progress()
            {
                progress?.Report(new TransferProgress(xferd, total));
            }

            var subprogress = new SimpleProgress<long>(delta =>
            {
                progress?.Report(new TransferProgress(xferd + delta, total));
            });

            var buf = ArrayPool<byte>.Shared.Rent(FirstSegmentSize);

            report_progress();
            var peek_len = await file.ReadAtAsync(0, buf.AsMemory(0, FirstSegmentSize), subprogress, ct);
            xferd += peek_len;
            report_progress();

            var peek_buf = buf.AsMemory(0, peek_len);

            var (comp, header_len, manifest_len) = ParsePackageHeader(peek_buf.Span);

            var sans_header = peek_buf.Slice(header_len);

            if (sans_header.Length < manifest_len)
            {
                // read more data
                if (buf.Length < header_len + manifest_len)
                {
                    // realloc buffer
                    var buf2 = ArrayPool<byte>.Shared.Rent(header_len + manifest_len);
                    Array.Copy(buf, buf2, peek_len);
                    ArrayPool<byte>.Shared.Return(buf);
                    buf = buf2;
                }
                var extra_len = await file.ReadAtAsync(peek_len, buf.AsMemory()[peek_len..(header_len + manifest_len)], subprogress, ct);
                xferd += extra_len;
                report_progress();
                if (peek_len + extra_len >= header_len + manifest_len)
                {
                    peek_len += extra_len;
                    sans_header = buf.AsMemory(header_len, manifest_len);
                    peek_buf = buf.AsMemory(0, peek_len);
                }
                else
                {
                    throw new InvalidDataException("Invalid package header");
                }
            }

            var manifest_chunk = sans_header.Slice(0, manifest_len);
            var tr = new TarReader(OpenDecompressionStream(comp, new ModernMemoryStream(manifest_chunk)));
            var entry = tr.GetNextEntry();
            if (!IsPackageManifestEntry(entry))
            {
                throw new InvalidDataException("Invalid manifest chunk");
            }

            var package_manifest = DeserializeFromEntry<PackageManifest>(entry) ?? throw new InvalidDataException("Invalid package manifest");

            if (referencePackageManifest != null)
            {
                if (package_manifest.Name != referencePackageManifest.Name)
                {
                    throw new InvalidDataException("package name mismatch");
                }

                if (package_manifest.Variant != referencePackageManifest.Variant)
                {
                    throw new InvalidDataException("package variant mismatch");
                }

                // if (package_manifest.Version == validatePackageManifest.Version)
                // {
                //     throw new InvalidDataException("no need to udpate");
                // }
            }

            entry = tr.GetNextEntry();
            if (entry == null)
            {
                // TODO: no index - full package
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

            ArrayPool<byte>.Shared.Return(buf);

            var chunks_offset = header_len + manifest_len;

            ISubStreamFactory factory;
            if (cachePackagePath == null && file is LocalRandomAccessFile lrf)
            {
                factory = new RandomAccessSubStreamFactory(lrf.FileStream, false, false);
            }
            else
            {
                var apply_chunks = delta_manifest.Chunks.Where(c => c.GetTargetVersions().Contains(referencePackageManifest!.Version)).ToArray();
                // maximum position relative to the first chunk
                var last_chunk_end = chunks_offset + apply_chunks.Select(c => c.Offset + c.Size).Max();

                var filename = cachePackagePath!;
                var dir = Path.GetDirectoryName(filename);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                Stream s;
                if (cachePackagePath != null)
                {
                    var fs = File.Open(cachePackagePath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                    fs.SetLength(0);
                    factory = new RandomAccessSubStreamFactory(fs, true, true);
                    s = fs;
                }
                else
                {
                    var ms = new MemoryStream(checked((int)last_chunk_end));
                    factory = new MemorySubStreamFactory(ms, true);
                    s = ms;
                }
                // avoid duplicate read from source file
                await s.WriteAsync(peek_buf, ct);
                if (last_chunk_end > peek_buf.Length)
                {
                    total = last_chunk_end;
                    var remaining = last_chunk_end - peek_buf.Length;
                    // read the rest of the file
                    report_progress();
                    await file.CopyToAsync(peek_len, remaining, s, subprogress, ct);
                    xferd += remaining;
                    report_progress();
                }
                s.Position = 0;
            }

            return new UpdatePackage(factory, package_manifest, delta_manifest, chunks_offset, comp);
        }

        private class SimpleProgress<T>(Action<T> action) : IProgress<T>
        {
            public void Report(T value)
            {
                action(value);
            }
        }

    }

}
