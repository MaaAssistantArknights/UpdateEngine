using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace MaaUpdateEngine;

internal class SafeFileOperationWin32 : ISafeFileOperation
{
    [SupportedOSPlatform("windows10.0.14393")]
    public static unsafe void Rename(string source, string destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (source.Length == 0 || destination.Length == 0)
        {
            throw new ArgumentException("source and destination must not be empty");
        }

        if (destination.Length > 32767)
        {
            throw new PathTooLongException("destination path is too long");
        }
        const uint FILE_RENAME_REPLACE_IF_EXISTS = 0x00000001;
        const uint FILE_RENAME_POSIX_SEMANTICS = 0x00000002;

        using var fd = OpenFileForDelete(source);

        var buffer = new byte[sizeof(FILE_RENAME_INFO) + destination.Length * 2];

        ref var rename = ref MemoryMarshal.AsRef<FILE_RENAME_INFO>(buffer);
        rename.Anonymous.Flags = FILE_RENAME_REPLACE_IF_EXISTS | FILE_RENAME_POSIX_SEMANTICS;
        rename.RootDirectory = HANDLE.Null;
        rename.FileNameLength = (uint)(destination.Length * 2);
        destination.AsSpan().CopyTo(MemoryMarshal.CreateSpan(ref rename.FileName[0], destination.Length));

        fixed (byte* p = buffer)
        {
            if (!PInvoke.SetFileInformationByHandle(fd, FILE_INFO_BY_HANDLE_CLASS.FileRenameInfoEx, p, (uint)buffer.Length))
            {
                throw new IOException(Marshal.GetLastPInvokeErrorMessage(), Marshal.GetHRForLastWin32Error());
            }
        }

    }

    [SupportedOSPlatform("windows10.0.14393")]
    void ISafeFileOperation.Rename(string source, string destination) => Rename(source, destination);

    [SupportedOSPlatform("windows10.0.14393")]
    public static unsafe void Unlink(string path)
    {
        using var fd = OpenFileForDelete(path);
        var unlink = new FILE_DISPOSITION_INFO_EX
        {
            Flags = FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_DELETE |
                FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_POSIX_SEMANTICS |
                FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE
        };
        if (!PInvoke.SetFileInformationByHandle(fd, FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfoEx, &unlink, (uint)sizeof(FILE_DISPOSITION_INFO_EX)))
        {
            throw new IOException(Marshal.GetLastPInvokeErrorMessage(), Marshal.GetHRForLastWin32Error());
        }
    }

    [SupportedOSPlatform("windows10.0.14393")]
    void ISafeFileOperation.Unlink(string path) => Unlink(path);

    [SupportedOSPlatform("windows10.0.14393")]
    private static SafeFileHandle OpenFileForDelete(string path)
    {
        var fd = PInvoke.CreateFile(path, (uint)(FILE_ACCESS_RIGHTS.SYNCHRONIZE | FILE_ACCESS_RIGHTS.DELETE), FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT, null);
        if (fd.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            if (err == (int)WIN32_ERROR.ERROR_FILE_NOT_FOUND)
            {
                throw new FileNotFoundException(Marshal.GetLastPInvokeErrorMessage(), path);
            }
            else
            {
                throw new IOException(Marshal.GetLastPInvokeErrorMessage(), Marshal.GetHRForLastWin32Error());
            }
        }
        return fd;
    }

    public static bool IsSupported { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393);
}