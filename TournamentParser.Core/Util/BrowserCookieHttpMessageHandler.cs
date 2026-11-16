using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentParser.Util
{
    /// <summary>
    /// Sends requests as plain HTTP. When the answer is a bot check, the page is loaded in the browser instead, which passes
    /// it, and from then on the plain requests carry the cookies the browser got. Smogon's check sets its cookie for 7 days,
    /// so the browser loads one page per run. If the bot check comes back twice in a row for a part of the site (such as
    /// /forums/threads/) although the plain requests carry the browser's cookies, that part is loaded in the browser for the
    /// rest of the run.
    /// </summary>
    public sealed class BrowserCookieHttpMessageHandler : HttpMessageHandler
    {
        private const int RejectionsBeforeBrowserOnly = 2;

        private readonly HttpMessageInvoker _plain;
        private readonly HttpMessageInvoker _browser;
        private readonly Func<Uri, CancellationToken, Task<IReadOnlyList<Cookie>>> _browserCookies;
        private readonly Func<string, bool> _isBotCheck;
        private readonly CookieContainer _cookies = new();
        private readonly ConcurrentDictionary<string, int> _rejections = new();
        private volatile bool _haveBrowserCookies;
        private int _browserRequests;

        public BrowserCookieHttpMessageHandler() : this(new PlaywrightHttpMessageHandler())
        {
        }

        public BrowserCookieHttpMessageHandler(PlaywrightHttpMessageHandler browser)
            : this(CreatePlainHandler(), browser, browser.GetCookiesAsync, SmogonPages.IsBotCheck)
        {
        }

        internal BrowserCookieHttpMessageHandler(HttpMessageHandler plain, HttpMessageHandler browser,
            Func<Uri, CancellationToken, Task<IReadOnlyList<Cookie>>> browserCookies, Func<string, bool> isBotCheck)
        {
            _plain = new HttpMessageInvoker(plain, disposeHandler: true);
            _browser = new HttpMessageInvoker(browser, disposeHandler: true);
            _browserCookies = browserCookies;
            _isBotCheck = isBotCheck;
        }

        /// <summary>Requests the browser loaded, such as the first bot check of a run.</summary>
        public int BrowserRequests => _browserRequests;

        private static SocketsHttpHandler CreatePlainHandler()
        {
            var handler = PoliteHttpMessageHandler.CreatePlainHttpHandler();
            // Cookies are kept here, so that the browser's can be added.
            handler.UseCookies = false;
            return handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("The request has no URI.");
            var part = PartOf(uri);
            if (_rejections.GetValueOrDefault(part) < RejectionsBeforeBrowserOnly)
            {
                var response = await SendPlainAsync(request, uri, cancellationToken).ConfigureAwait(false);
                if (!await IsBotCheckAsync(response, cancellationToken).ConfigureAwait(false))
                {
                    _rejections.TryRemove(part, out _);
                    return response;
                }
                response.Dispose();
                if (_haveBrowserCookies && _rejections.AddOrUpdate(part, 1, (_, count) => count + 1) == RejectionsBeforeBrowserOnly)
                {
                    Console.WriteLine($"The bot check comes back for {uri.Host}/{part} even with the browser's cookies; these pages are loaded in the browser from now on.");
                }
            }

            Interlocked.Increment(ref _browserRequests);
            var browserResponse = await _browser.SendAsync(Copy(request, uri), cancellationToken).ConfigureAwait(false);
            foreach (var cookie in await _browserCookies(uri, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    _cookies.Add(uri, cookie);
                }
                catch (CookieException)
                {
                    // Not for this host
                }
            }
            _haveBrowserCookies = true;
            return browserResponse;
        }

        /// <summary>The first two path segments ("forums/threads"): pages that share them share a bot check.</summary>
        private static string PartOf(Uri uri) => string.Join('/', uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(2));

        private async Task<HttpResponseMessage> SendPlainAsync(HttpRequestMessage request, Uri uri, CancellationToken cancellationToken)
        {
            var plainRequest = Copy(request, uri);
            var cookieHeader = _cookies.GetCookieHeader(uri);
            if (cookieHeader.Length > 0)
            {
                plainRequest.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }
            var response = await _plain.SendAsync(plainRequest, cancellationToken).ConfigureAwait(false);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    try
                    {
                        _cookies.SetCookies(uri, setCookie);
                    }
                    catch (CookieException)
                    {
                        // A cookie the container cannot take; the browser would ignore it as well.
                    }
                }
            }
            return response;
        }

        private static HttpRequestMessage Copy(HttpRequestMessage request, Uri uri)
        {
            var copy = new HttpRequestMessage(request.Method, uri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers.Where(header => !header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
            {
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return copy;
        }

        /// <summary>Reads the body into memory (the caller can still read it) and checks it for the bot check.</summary>
        private async Task<bool> IsBotCheckAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            return _isBotCheck(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _plain.Dispose();
                // Closes the browser
                _browser.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
