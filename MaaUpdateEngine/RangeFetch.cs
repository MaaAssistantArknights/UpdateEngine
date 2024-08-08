using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaaUpdateEngine
{
    internal struct RangeFetch(HttpClient httpClient, Uri uri, long offset, long length)
    {
        private HttpRequestMessage PrepareFetchRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);
            return req;
        }

        private void ValidateResponse(HttpResponseMessage response) {
            response.EnsureSuccessStatusCode();
        }

        public async Task<HttpResponseMessage> ExecuteAsync(CancellationToken ct)
        {
            var req = PrepareFetchRequest();
            var resp = await httpClient.SendAsync(req, ct);
            ValidateResponse(resp);
            return resp;
        }

        public HttpResponseMessage Execute()
        {
            var req = PrepareFetchRequest();
            var resp = httpClient.Send(req);
            ValidateResponse(resp);
            return resp;
        }
    }
}
