
using System;
using System.Buffers;
using System.Net.Http;
using ZstdSharp.Unsafe;

namespace MaaUpdateEngine
{
    internal class SimpleHttpRandomAccessFile : IRandomAccessFile
    {
        const int PageSize = 65536;

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

        public int ReadAt(long offset, Span<byte> buffer)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + buffer.Length - 1);
            using var resp = client.Send(req);
            resp.EnsureSuccessStatusCode();
            using var s = resp.Content.ReadAsStream();
            return s.Read(buffer);
        }

        public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + buffer.Length - 1);
            using var resp = await client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            using var s = await resp.Content.ReadAsStreamAsync(ct);
            return await s.ReadAsync(buffer, ct);
        }

        async Task IRandomAccessFile.CopyToAsync(long offset, long length, Stream destination, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);
            using var resp = await client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            using var s = await resp.Content.ReadAsStreamAsync(ct);
            await s.CopyToAsync(destination, ct);
        }
    }
}
