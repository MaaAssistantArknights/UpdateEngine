
using System;
using System.Buffers;
using System.Net.Http;
using ZstdSharp.Unsafe;

namespace MaaUpdateEngine
{
    internal class SimpleHttpRandomAccessFile : AbstractRandomAccessFile
    {
        private HttpClient client;
        private Uri url;
        public long Length => contentLength;
        private long contentLength;

        private SimpleHttpRandomAccessFile(HttpClient client, Uri url, long length)
        {
            this.client = client;
            this.url = url;
            contentLength = length;
        }

        public static async Task<SimpleHttpRandomAccessFile> OpenAsync(Uri url)
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, UseCookies = true, AutomaticDecompression = System.Net.DecompressionMethods.All });
            var headResp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
            if (headResp.RequestMessage?.RequestUri != null)
            {
                url = headResp.RequestMessage.RequestUri;
            }
            var supportRangeRequests = false;
            if (headResp.Headers.TryGetValues("accept-ranges", out var accepsRangesValues))
            {
                if (accepsRangesValues.First() == "bytes")
                {
                    supportRangeRequests = true;
                }
            }
            if (!supportRangeRequests)
            {
                throw new InvalidOperationException("This endpoint does not support range requests");
            }
            if (headResp.Content.Headers.ContentLength.HasValue)
            {
                return new SimpleHttpRandomAccessFile(client, url, headResp.Content.Headers.ContentLength.Value);
            }
            throw new InvalidOperationException("This endpoint does not support range requests");
        }

        public static Task<SimpleHttpRandomAccessFile> OpenAsync(string url)
        {
            return OpenAsync(new Uri(url));
        }

        public override int ReadAt(long offset, Span<byte> buffer)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + buffer.Length - 1);
            using var resp = client.Send(req);
            resp.EnsureSuccessStatusCode();
            using var s = resp.Content.ReadAsStream();
            return s.Read(buffer);
        }

        public override async ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, IProgress<long>? progress, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + buffer.Length - 1);
            using var resp = await client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            using var s = await resp.Content.ReadAsStreamAsync(ct);
            return await s.ReadAsync(buffer, ct);
        }

        public override async Task CopyToAsync(long offset, long length, Stream destination, IProgress<long>? progress, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);
            long xferd = 0;
            progress?.Report(xferd);
            using var resp = await client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            using var s = await resp.Content.ReadAsStreamAsync(ct);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            while (!ct.IsCancellationRequested)
            {
                var len = await s.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), ct);
                if (len == 0)
                {
                    break;
                }
                await destination.WriteAsync(buffer.AsMemory(0, len), ct);
                xferd += len;
                progress?.Report(xferd);
            }
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
