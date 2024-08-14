using Microsoft.Win32.SafeHandles;

namespace MaaUpdateEngine;

internal class LocalRandomAccessFile : IRandomAccessFile, IDisposable
{
    public FileStream FileStream { get; }
    private SafeFileHandle fd;
    private bool leaveOpen;

    public string Name => FileStream.Name;

    public LocalRandomAccessFile(FileStream fileStream, bool leaveOpen = true)
    {
        FileStream = fileStream;
        fd = fileStream.SafeFileHandle;
        this.leaveOpen = leaveOpen;
    }

    public int ReadAt(long offset, Span<byte> buffer) => RandomAccess.Read(fd, buffer, offset);

    public ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct) => RandomAccess.ReadAsync(fd, buffer, offset, ct);

    public void Dispose()
    {
        if (!leaveOpen)
        {
            FileStream.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    ~LocalRandomAccessFile()
    {
        Dispose();
    }
}
