using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentParser.Tests.TestSupport
{
    /// <summary>
    /// Serves canned string responses by exact URL and records every request,
    /// so scanner/collector behavior can be pinned without network access.
    /// </summary>
    public class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new();
        private readonly object _lock = new();
        private readonly List<string> _requests = new();

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_lock)
                {
                    return _requests.ToArray();
                }
            }
        }

        public void Map(string url, string content) => _responses[url] = content;

        /// <summary>Maps the same content under both http:// and https:// forms of the URL.</summary>
        public void MapBothSchemes(string urlWithoutScheme, string content)
        {
            Map("http://" + urlWithoutScheme, content);
            Map("https://" + urlWithoutScheme, content);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (_lock)
            {
                _requests.Add(url);
            }
            if (_responses.TryGetValue(url, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
