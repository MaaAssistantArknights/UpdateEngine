using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaaUpdateEngine
{
    /// <summary>
    /// Represents a file that can be read from at arbitrary offsets.
    /// </summary>
    /// <remarks>
    /// Use <see cref="AbstractRandomAccessFile"/> as a base class to ease implementation of this interface.
    /// </remarks>
    public interface IRandomAccessFile
    {
        public int ReadAt(long offset, Span<byte> buffer);
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer);
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct);
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, IProgress<long>? progress, CancellationToken ct);
        public Task CopyToAsync(long offset, long length, Stream destination, CancellationToken ct);
        public Task CopyToAsync(long offset, long length, Stream destination, IProgress<long>? progress, CancellationToken ct);
    }
}
