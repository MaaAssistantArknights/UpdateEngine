
using System.Buffers;

namespace MaaUpdateEngine;

/// <summary>
/// Provide base implementation for <see cref="IRandomAccessFile"/>.
/// </summary>
public abstract class AbstractRandomAccessFile : IRandomAccessFile
{
    public abstract int ReadAt(long offset, Span<byte> buffer);
    public virtual ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer) => ReadAtAsync(offset, buffer, null, CancellationToken.None);
    public virtual ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct) => ReadAtAsync(offset, buffer, null, ct);
    public abstract ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, IProgress<long>? progress, CancellationToken ct);
    public virtual async Task CopyToAsync(long offset, long length, Stream destination, CancellationToken ct) => await CopyToAsync(offset, length, destination, null, ct);
    public virtual async Task CopyToAsync(long offset, long length, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(80000);
        try
        {
            long xferd = 0;
            long remaining = length;
            while (remaining > 0)
            {
                int read = await ReadAtAsync(offset, buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct);
                if (read == 0)
                {
                    break;
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                xferd += read;
                progress?.Report(xferd);
                offset += read;
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
