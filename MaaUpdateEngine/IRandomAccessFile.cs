using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaaUpdateEngine
{
    public interface IRandomAccessFile
    {
        public int ReadAt(long offset, Span<byte> buffer);
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer) => ReadAtAsync(offset, buffer, CancellationToken.None);
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct);
        public async Task CopyToAsync(long offset, long length, Stream destination, CancellationToken ct) {
            var buffer = ArrayPool<byte>.Shared.Rent(80000);
            try {
                long remaining = length;
                while (remaining > 0) {
                    int read = await ReadAtAsync(offset, buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct);
                    if (read == 0) {
                        break;
                    }
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    offset += read;
                    remaining -= read;
                }
            } finally {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
