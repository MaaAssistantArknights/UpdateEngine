
using System;
using System.Buffers;

namespace MaaUpdateEngine
{
    internal class HttpRandomAccessFile : IRandomAccessFile
    {
        const int PageSize = 65536;

        private HttpClient client;
        private Uri url;
        public long Length => contentLength;
        private long contentLength;
        private long pageCount;

        private SortedDictionary<long, Memory<byte>> pageCache = new SortedDictionary<long, Memory<byte>>();


        int getCount = 0;
        long bytesTransferred = 0;

        private HttpRandomAccessFile(HttpClient client, Uri url, long length)
        {
            this.client = client;
            this.url = url;
            contentLength = length;
            pageCount = (length + PageSize - 1) / PageSize;
        }

        public static async Task<HttpRandomAccessFile> OpenAsync(Uri url)
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
                return new HttpRandomAccessFile(client, url, headResp.Content.Headers.ContentLength.Value);
            }
            throw new InvalidOperationException("This endpoint does not support range requests");
        }

        public static Task<HttpRandomAccessFile> OpenAsync(string url)
        {
            return OpenAsync(new Uri(url));
        }

        private IEnumerable<(long begin, long end)> GetPagesToFetch(long offset, long length) {
            if (offset < 0 || offset > contentLength)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset is outside the bounds of the file.");
            }

            long startPage = offset / PageSize;
            long endPage = (offset + length - 1) / PageSize;
            List<long> pagesToFetch = new List<long>(capacity: (int)(endPage - startPage + 1));

            // Determine which pages need to be fetched
            for (long page = startPage; page <= endPage; page++)
            {
                if (!pageCache.ContainsKey(page))
                {
                    pagesToFetch.Add(page);
                }
            }


            if (pagesToFetch.Count > 0)
            {
                List<(long begin, long end)> segmentsToFetch = new();

                long currentBlockStart = pagesToFetch[0];
                long lastPage = currentBlockStart;

                for (int i = 1; i <= pagesToFetch.Count; i++)
                {
                    if (i == pagesToFetch.Count || pagesToFetch[i] != lastPage + 1)
                    {
                        // We've reached the end of a contiguous block or the end of the list
                        segmentsToFetch.Add((currentBlockStart, lastPage));

                        if (i < pagesToFetch.Count)
                        {
                            // Start a new block
                            currentBlockStart = pagesToFetch[i];
                        }
                    }
                    if (i < pagesToFetch.Count)
                    {
                        lastPage = pagesToFetch[i];
                    }
                }
                return segmentsToFetch;
            }
            else
            {
                return Enumerable.Empty<(long, long)>();
            }
        }

        private void CachePages(long startPage, long endPage, Memory<byte> buffer)
        {
            // Cache the fetched pages
            for (long page = startPage; page <= endPage; page++)
            {
                var offset = (int)((page - startPage) * PageSize);
                var length = Math.Min(PageSize, buffer.Length - offset);
                var pageMem = buffer.Slice(offset, length);
                pageCache[page] = pageMem;
            }
        }

        private async Task EnsureRangeAsync(long offset, long length, CancellationToken ct)
        {
            foreach (var (begin, end) in GetPagesToFetch(offset, length))
            {
                await FetchAndCachePagesAsync(begin, end, ct);
                ct.ThrowIfCancellationRequested();
            }
        }

        private void EnsureRange(long offset, long length)
        {
            foreach (var (begin, end) in GetPagesToFetch(offset, length))
            {
                FetchAndCachePages(begin, end);
            }
        }

        private (long offset, long length) GetFetchOffsetLength(long startPage, long endPage)
        {
            var offset = startPage * PageSize;
            long startOffset = startPage * PageSize;
            long endOffset = (endPage + 1) * PageSize - 1;
            if (endOffset >= contentLength)
            {
                endOffset = contentLength - 1;
            }
            var fetchLength = endOffset - startOffset + 1;
            return (offset, fetchLength);
        }

        private async Task FetchAndCachePagesAsync(long startPage, long endPage, CancellationToken ct)
        {
            var (offset, length) = GetFetchOffsetLength(startPage, endPage);

            var fetch = new RangeFetch(client, url, offset, length);
            using var resp = await fetch.ExecuteAsync(ct);
            getCount++;
            
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            byte[] buffer = new byte[length];
            bytesTransferred += await stream.ReadAsync(buffer, ct);

            CachePages(startPage, endPage, buffer);
        }

        private void FetchAndCachePages(long startPage, long endPage)
        {
            var (offset, length) = GetFetchOffsetLength(startPage, endPage);

            var fetch = new RangeFetch(client, url, offset, length);
            using var resp = fetch.Execute();
            getCount++;

            using var stream = resp.Content.ReadAsStream();
            byte[] buffer = new byte[length];
            bytesTransferred += stream.Read(buffer);

            CachePages(startPage, endPage, buffer);
        }

        private int CopyCache(long offset, Span<byte> buffer)
        {
            int bytesRead = 0;
            while (bytesRead < buffer.Length && offset + bytesRead < contentLength)
            {
                long page = (offset + bytesRead) / PageSize;
                int pageOffset = (int)((offset + bytesRead) % PageSize);
                var cachedPage = pageCache[page];
                int bytesToCopy = Math.Min(cachedPage.Length - pageOffset, buffer.Length - bytesRead);
                cachedPage.Span.Slice(pageOffset, bytesToCopy).CopyTo(buffer.Slice(bytesRead));
                bytesRead += bytesToCopy;
            }

            return bytesRead;
        }

        public int ReadAt(long offset, Span<byte> buffer)
        {
            EnsureRange(offset, buffer.Length);
            return CopyCache(offset, buffer);
        }

        public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            await EnsureRangeAsync(offset, buffer.Length, ct);
            return CopyCache(offset, buffer.Span);
        }

        public (int RequestCount, long BytesTransferred) GetStatistics()
        {
            return (getCount, bytesTransferred);
        }

        public async Task CopyToAsync(long offset, long length, Stream destination, CancellationToken ct)
        {
            await EnsureRangeAsync(offset, length, ct);
            ct.ThrowIfCancellationRequested();

            const int bufferSize = 262144;
            var buf = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                long remaining = length;
                while (remaining > 0)
                {
                    var copylen = (int)Math.Min(remaining, bufferSize);
                    CopyCache(offset, buf.AsSpan(0, copylen));
                    await destination.WriteAsync(buf.AsMemory(0, copylen), ct);
                    offset += copylen;
                    remaining -= copylen;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
    }
}
