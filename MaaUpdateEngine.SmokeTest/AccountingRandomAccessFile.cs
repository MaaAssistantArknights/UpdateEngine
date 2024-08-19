using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaaUpdateEngine
{
    internal class AccountingRandomAccessFile(IRandomAccessFile baseObj) : AbstractRandomAccessFile
    {
        private long ioCount = 0;
        private long bytesXferd = 0;

        public long IoCount => ioCount;
        public long BytesTransferred => bytesXferd;

        public override int ReadAt(long offset, Span<byte> buffer)
        {
            var len = baseObj.ReadAt(offset, buffer);
            Interlocked.Increment(ref ioCount);
            Interlocked.Add(ref bytesXferd, len);
            Thread.Sleep(500);
            return len;
        }

        public override async ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, IProgress<long>? progress, CancellationToken ct)
        {
            var len = await baseObj.ReadAtAsync(offset, buffer, progress, ct);
            Interlocked.Increment(ref ioCount);
            Interlocked.Add(ref bytesXferd, len);
            await Task.Delay(500, ct);
            return len;
        }

        public override async Task CopyToAsync(long offset, long length, Stream destination, IProgress<long>? progress, CancellationToken ct)
        {
            await Task.Delay(1000, ct);
            await baseObj.CopyToAsync(offset, length, destination, progress, ct);
            Interlocked.Increment(ref ioCount);
            Interlocked.Add(ref bytesXferd, length);
        }

    }
}
