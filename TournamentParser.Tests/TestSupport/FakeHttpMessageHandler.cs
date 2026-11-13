#nullable enable
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
        private readonly Dictionary<string, HttpStatusCode> _statuses = new();
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

        /// <summary>Answers URLs without mapping; null means 404.</summary>
        public System.Func<string, string?>? Fallback { get; set; }

        /// <summary>Called with the URL of each request before it is answered, so tests can change the responses mid-run.</summary>
        public System.Action<string>? BeforeResponse { get; set; }

        public void Map(string url, string content)
        {
            _responses[url] = content;
            _statuses.Remove(url);
        }

        /// <summary>Answers the URL with an empty response of the given status code.</summary>
        public void MapStatus(string url, HttpStatusCode statusCode) => _statuses[url] = statusCode;

        public void ClearRequests()
        {
            lock (_lock)
            {
                _requests.Clear();
            }
        }

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
            BeforeResponse?.Invoke(url);
            if (_statuses.TryGetValue(url, out var statusCode))
            {
                return Task.FromResult(new HttpResponseMessage(statusCode));
            }
            if (_responses.TryGetValue(url, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content),
                });
            }
            if (Fallback?.Invoke(url) is string generated)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(generated),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
