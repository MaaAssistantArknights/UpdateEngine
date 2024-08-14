using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaaUpdateEngine
{
    internal class AccountingRandomAccessFile(IRandomAccessFile baseObj) : IRandomAccessFile
    {
        private long ioCount = 0;
        private long bytesXferd = 0;

        public long IoCount => ioCount;
        public long BytesTransferred => bytesXferd;

        public int ReadAt(long offset, Span<byte> buffer)
        {
            var len = baseObj.ReadAt(offset, buffer);
            Interlocked.Increment(ref ioCount);
            Interlocked.Add(ref bytesXferd, len);
            return len;
        }

        public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            var len = await baseObj.ReadAtAsync(offset, buffer, ct);
            Interlocked.Increment(ref ioCount);
            Interlocked.Add(ref bytesXferd, len);
            return len;
        }

        async Task IRandomAccessFile.CopyToAsync(long offset, long length, Stream destination, CancellationToken ct)
        {
            await baseObj.CopyToAsync(offset, length, destination, ct);
            Interlocked.Increment(ref ioCount);
            Interlocked.Add(ref bytesXferd, length);
        }

    }
}
