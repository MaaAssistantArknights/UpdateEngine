using System;
using System.Buffers;
using System.Formats.Tar;
using System.Security.Cryptography;
using Windows.Win32;

namespace MaaUpdateEngine
{
    using static UpdateDataCommon;
    public class UpdateSession : IDisposable
    {
        public record class AddPackageProgress(long BytesToConsume, long BytesConsumed, string? CurrentFile);
        public record class CommitProgress(long NumberOfFilesToUpdate, long NumberOfFilesUpdated, string? CurrentFile);
        private record struct PatchRecord(string FromVersion, PatchFile PatchFile);

        private PackageManifest currentPackageInfo;

        private readonly string workDir;
        private readonly string rootDir;
        private readonly string _pendingRemoveDir;

        private const int TempFileThreshold = 84000;
        private const string PendingRemoveDirName = "pending_remove";
        private bool pendingRemoveDirCreated = false;
        private bool workDirCreated = false;
        private bool disposed = false;

        // path in package -> temp file path
        private Dictionary<string, string?> updatedFileMapping = [];

        public UpdateSession(PackageManifest currentPackage, string packageRootDirectory, string workingDirectoryBase)
        {
            currentPackageInfo = currentPackage;
            rootDir = Path.GetFullPath(packageRootDirectory);

            workingDirectoryBase = Path.GetFullPath(workingDirectoryBase);

            workDir = Path.Combine(workingDirectoryBase, "session_" + Guid.NewGuid().ToString("N"));
            _pendingRemoveDir = Path.Combine(workingDirectoryBase, PendingRemoveDirName);
        }

        /// <summary>
        /// Add an update package to the current session.
        /// </summary>
        /// <param name="updatePackage"></param>
        /// <exception cref="InvalidOperationException"></exception>
        /// <exception cref="InvalidDataException"></exception>
        public void AddPackage(UpdatePackage updatePackage, IProgress<AddPackageProgress>? progress = null)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            long bytes_consumed = 0;

            if (currentPackageInfo == null)
            {
                throw new InvalidOperationException("Current package info is not available");
            }

            var apply_chunks = updatePackage.PackageIndex.Chunks.Where(c => c.GetTargetVersions().Contains(currentPackageInfo.Version)).ToArray();
            var total_bytes = apply_chunks.Sum(c => c.Size);

            progress?.Report(new(total_bytes, bytes_consumed, null));

            var patch_files = new Dictionary<string, List<PatchRecord>>();
            var interested_patch_data = new Dictionary<string, byte[]>();

            // process chunks from the last one
            for (var i = apply_chunks.Length - 1; i >= 0; i--)
            {
                var chunk_meta = apply_chunks[i];
                var chunk_stream = updatePackage.OpenChunk(chunk_meta);

                var (hasher, expect_hash) = ParseHashString(chunk_meta.Hash);

                var actual_hash = hasher.ComputeHash(chunk_stream);

                if (!actual_hash.SequenceEqual(expect_hash))
                {
                    throw new InvalidDataException("Invalid chunk hash");
                }

                chunk_stream.Position = 0;

                var decompress_stream = OpenDecompressionStream(updatePackage.CompressionType, chunk_stream);
                var tf = new TarReader(decompress_stream, true);

                var chunk_manifest_entry = tf.GetNextEntry();
                if (!IsChunkManifestEntry(chunk_manifest_entry))
                {
                    throw new InvalidDataException("Invalid chunk manifest");
                }
                progress?.Report(new(total_bytes, bytes_consumed + chunk_stream.Position, null));

                var chunk_manifest = DeserializeFromEntry<ChunkManifest>(chunk_manifest_entry) ?? throw new InvalidDataException("Invalid chunk manifest");
                progress?.Report(new(total_bytes, bytes_consumed + chunk_stream.Position, null));

                // process removed files
                foreach (var rf in chunk_manifest.RemoveFiles ?? [])
                {
                    updatedFileMapping[rf] = null;
                }

                // process patch files
                var all_patch_data_names_set = new HashSet<string>();
                foreach (var pf in chunk_manifest.PatchFiles ?? [])
                {
                    all_patch_data_names_set.Add(pf.Patch);
                    if (chunk_manifest.PatchBase == currentPackageInfo.Version)
                    {
                        // initialize patch list
                        var list = new List<PatchRecord>();
                        patch_files[pf.File] = list;
                        list.Add(new PatchRecord(currentPackageInfo.Version, pf));
                        interested_patch_data[pf.Patch] = [];
                    }
                    else if (patch_files.TryGetValue(pf.File, out var list))
                    {
                        // append patch list if available
                        if (list[^1].PatchFile.NewVersion == chunk_manifest.PatchBase)
                        {
                            list.Add(new PatchRecord(chunk_manifest.PatchBase, pf));
                            interested_patch_data[pf.Patch] = [];
                        }
                    }
                }

                TarEntry? GetNextEntry2(TarReader reader)
                {
                    try
                    {
                        var entry = reader.GetNextEntry();
                        progress?.Report(new(total_bytes, bytes_consumed + chunk_stream.Position, null));
                        return entry;
                    }
                    catch (EndOfStreamException)
                    {
                        // since we created the tar without EOF marker, this is expected
                        return null;
                    }
                }

                // process replaced files
                TarEntry? file_entry;
                while ((file_entry = GetNextEntry2(tf)) != null)
                {
                    // skip extracting patch files
                    if (interested_patch_data.ContainsKey(file_entry.Name))
                    {
                        var patchdata = new byte[file_entry.Length];
                        var ds = file_entry.DataStream ?? throw new InvalidDataException("Invalid patch data");
                        ds.ReadExactly(patchdata);
                        interested_patch_data[file_entry.Name] = patchdata;
                        total_bytes += patchdata.Length;
                        continue;
                    }
                    else if (all_patch_data_names_set.Contains(file_entry.Name))
                    {
                        continue;
                    }

                    if (file_entry.EntryType == TarEntryType.RegularFile)
                    {
                        var file_path = file_entry.Name;
                        var tmpfile = GetTempFileName();
                        file_entry.ExtractToFile(tmpfile, true);
                        updatedFileMapping[file_path] = tmpfile;
                    }
                    progress?.Report(new(total_bytes, bytes_consumed + chunk_stream.Position, null));
                }

                bytes_consumed += chunk_meta.Size;
            }

            // apply patch series
            foreach (var (file_to_patch, patch_series) in patch_files)
            {
                progress?.Report(new(total_bytes, bytes_consumed, file_to_patch));
                if (patch_series[^1].PatchFile.NewVersion != updatePackage.Manifest.Version)
                {
                    throw new InvalidDataException($"Invalid patch series: file {file_to_patch} is not patched to the target version");
                }

                var patch_src = OpenSourceFile(file_to_patch);
                try
                {
                    if (!patch_src.CanSeek)
                    {
                        throw new InvalidOperationException("Source file must be seekable");
                    }

                    string? patched_file = null;
                    string prev_hash = string.Empty;

                    foreach (var patch in patch_series)
                    {
                        // skip hash check if the source file is already checked
                        if (prev_hash != patch.PatchFile.OldHash)
                        {
                            var (hasher, parsed_hash) = ParseHashString(patch.PatchFile.OldHash);
                            var actual_hash = hasher.ComputeHash(patch_src);
                            if (!actual_hash.SequenceEqual(parsed_hash))
                            {
                                throw new InvalidDataException("Invalid source file hash");
                            }
                        }

                        patch_src.Position = 0;

                        if (patch.PatchFile.PatchType == "copy")
                        {
                            continue;
                        }

                        Stream patch_out;
                        string? patch_out_path;

                        if (patch.PatchFile.NewSize > TempFileThreshold)
                        {
                            (patch_out_path, patch_out) = CreateTempFile();
                        }
                        else
                        {
                            patch_out = new MemoryStream(new byte[patch.PatchFile.NewSize]);
                            patch_out_path = null;
                        }

                        var patch_data = interested_patch_data[patch.PatchFile.Patch];
                        var patch_data_stream = new MemoryStream(patch_data);
                        ApplyBinaryPatch(patch_src, patch_data_stream, patch_out, patch.PatchFile.PatchType);

                        var (hasher2, parsed_hash2) = ParseHashString(patch.PatchFile.NewHash);
                        patch_out.Position = 0;
                        var actual_hash2 = hasher2.ComputeHash(patch_out);
                        if (!actual_hash2.SequenceEqual(parsed_hash2))
                        {
                            throw new InvalidDataException("Invalid patched file hash");
                        }
                        prev_hash = patch.PatchFile.NewHash;

                        patch_src.Dispose();
                        patch_out.Position = 0;
                        patch_src = patch_out;

                        // TODO: track and delete intermediate file
                        var old_input_file = patched_file;
                        patched_file = patch_out_path;

                        if (old_input_file != null)
                        {
                            SafeFileOperation.Unlink(old_input_file);
                        }
                        bytes_consumed += patch_data.Length;
                        progress?.Report(new(total_bytes, bytes_consumed, file_to_patch));
                    }

                    if (patched_file == null)
                    {
                        // patched file is not on disk yet
                        patched_file = GetTempFileName();
                        patch_src.Position = 0;
                        using var fs = File.OpenWrite(patched_file);
                        patch_src.CopyTo(fs);
                    }
                    updatedFileMapping[file_to_patch] = patched_file;
                }
                finally
                {
                    patch_src.Dispose();
                }
            }

            currentPackageInfo = updatePackage.Manifest;
        }

        /// <summary>
        /// Commit the changes in current session.
        /// </summary>
        public void Commit(IProgress<CommitProgress>? progress = null)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var updatedFileCount = 0;
            var total = updatedFileMapping.Count;
            foreach (var (path_in_package, tempfile) in updatedFileMapping)
            {
                progress?.Report(new(total, updatedFileCount, path_in_package));
                var canonical_path = Path.GetFullPath(Path.Combine(rootDir, path_in_package));

                if (tempfile == null)
                {
                    RemoveFileWithFallback(canonical_path);
                }
                else
                {
                    ReplaceFileWithFallback(tempfile, canonical_path);
                }
                updatedFileCount++;
            }
            progress?.Report(new(total, updatedFileCount, null));
        }

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                Directory.Delete(workDir, true);
                updatedFileMapping.Clear();
            }
        }

        private string GetPendingRemoveDir()
        {
            if (!pendingRemoveDirCreated)
            {
                Directory.CreateDirectory(_pendingRemoveDir);
                pendingRemoveDirCreated = true;
            }
            return _pendingRemoveDir;
        }

        private void ReplaceFileWithFallback(string oldPath, string newPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(newPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                SafeFileOperation.Rename(oldPath, newPath);
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                var temp_file = Path.Join(GetPendingRemoveDir(), Path.GetFileName(oldPath) + Random.Shared.Next(0, 65535).ToString("X4"));
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
                {
                    // Windows requires at least one link for memory-mapped file, make it happy
                    PInvoke.CreateHardLink(temp_file, newPath);
                    // now we can use POSIX semantics rename
                    SafeFileOperation.Rename(oldPath, newPath);
                }
                else
                {
                    // no atomic rename
                    SafeFileOperation.Rename(newPath, temp_file);
                    SafeFileOperation.Rename(oldPath, newPath);
                }
            }
        }

        private void RemoveFileWithFallback(string oldPath)
        {
            try
            {
                if (File.Exists(oldPath))
                {
                    SafeFileOperation.Unlink(oldPath);
                }
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                // cannot remove the last link of a memory-mapped file, rename it
                var temp_file = Path.Join(GetPendingRemoveDir(), Path.GetFileName(oldPath) + Random.Shared.Next(0, 65535).ToString("X4"));
                SafeFileOperation.Rename(oldPath, temp_file);
            }
        }

        private Stream OpenSourceFile(string file)
        {
            if (updatedFileMapping.TryGetValue(file, out var updatedFile))
            {
                if (updatedFile == null)
                {
                    throw new FileNotFoundException("File is removed in session", file);
                }
                return File.Open(updatedFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            }
            // TODO: cache small file in memory?
            // TODO: copy file to temp dir for safe operation?
            return File.Open(Path.Combine(rootDir, file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        private void EnsureWorkDir()
        {
            if (!workDirCreated)
            {
                Directory.CreateDirectory(workDir);
                workDirCreated = true;
            }
        }

        private string GetTempFileName()
        {
            EnsureWorkDir();
            return Path.Combine(workDir, Path.GetRandomFileName());
        }

        private (string, FileStream) CreateTempFile()
        {
            var tempFile = GetTempFileName();
            var fs = File.Open(tempFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return (tempFile, fs);
        }

        private static void ApplyBinaryPatch(Stream srcStream, Stream patchStream, Stream destStream, string patchType)
        {
            if (patchType == "bsdiff")
            {
                var patch = BsDiff.BinaryPatch.ReadFrom(patchStream);
                patch.Apply(srcStream, destStream);
            }
            else if (patchType == "zstd")
            {
                using var patcher = new ZstdSharp.Decompressor();
                patcher.SetParameter(ZstdSharp.Unsafe.ZSTD_dParameter.ZSTD_d_windowLogMax, (int)Math.Ceiling(Math.Log2(srcStream.Length)));
                if (srcStream is MemoryStream ms && ms.TryGetBuffer(out var buf))
                {
                    patcher.LoadDictionary(buf.AsSpan());
                }
                else
                {
                    var oldData = ArrayPool<byte>.Shared.Rent((int)srcStream.Length);
                    var oldDataSpan = oldData.AsSpan(0, (int)srcStream.Length);
                    srcStream.ReadExactly(oldDataSpan);
                    patcher.LoadDictionary(oldDataSpan);
                    ArrayPool<byte>.Shared.Return(oldData);
                }
                using var decompstream = new ZstdSharp.DecompressionStream(patchStream, patcher);
                decompstream.CopyTo(destStream);
            }
            else
            {
                throw new NotSupportedException("Unsupported patch type");
            }
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
    }
}