#nullable enable
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TournamentParser.Util;

namespace TournamentParser.Tests
{
    /// <summary>Plain requests that call the browser only for a bot check, against fake plain and browser handlers.</summary>
    [TestFixture]
    public class BrowserCookieHttpMessageHandlerTests
    {
        private const string BotCheck = "<html><body><p id=\"m\">Checking your browser...</p><script>document.cookie = \"xf_smogon_gate=1\"</script></body></html>";
        private const string ThreadUrl = "https://www.smogon.com/forums/threads/tour.";
        private const string ForumUrl = "https://www.smogon.com/forums/forums/tournaments.34/";

        private sealed class FakeHandler(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
        {
            public List<string> Requests { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var cookie = request.Headers.TryGetValues("Cookie", out var values) ? " (" + string.Join("; ", values) + ")" : "";
                Requests.Add(request.RequestUri + cookie);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(request)) });
            }
        }

        private static bool HasGateCookie(HttpRequestMessage request) =>
            request.Headers.TryGetValues("Cookie", out var values) && values.Any(value => value.Contains("xf_smogon_gate=42"));

        private static Task<IReadOnlyList<Cookie>> BrowserCookies(Uri uri, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Cookie>>([new Cookie("xf_smogon_gate", "42", "/", "www.smogon.com")]);

        private static (HttpClient Client, FakeHandler Plain, FakeHandler Browser, BrowserCookieHttpMessageHandler Handler) Create(Func<HttpRequestMessage, string> plainAnswer)
        {
            var plain = new FakeHandler(plainAnswer);
            var browser = new FakeHandler(_ => "<html>page from the browser</html>");
            var handler = new BrowserCookieHttpMessageHandler(plain, browser, BrowserCookies, SmogonPages.IsBotCheck);
            return (new HttpClient(handler), plain, browser, handler);
        }

        [Test]
        public async Task PageWithoutBotCheck_DoesNotStartTheBrowser()
        {
            var (client, plain, browser, _) = Create(_ => "<html>listing</html>");

            Assert.That(await client.GetStringAsync(ForumUrl), Is.EqualTo("<html>listing</html>"));
            Assert.That(plain.Requests, Is.EqualTo(new[] { ForumUrl }));
            Assert.That(browser.Requests, Is.Empty);
        }

        [Test]
        public async Task BotCheck_IsPassedInTheBrowser_AndItsCookieIsSentFromThenOn()
        {
            var (client, plain, browser, handler) = Create(request => HasGateCookie(request) ? "<html>thread</html>" : BotCheck);

            Assert.That(await client.GetStringAsync(ThreadUrl + "1/"), Is.EqualTo("<html>page from the browser</html>"));
            Assert.That(await client.GetStringAsync(ThreadUrl + "2/"), Is.EqualTo("<html>thread</html>"));
            Assert.That(await client.GetStringAsync(ForumUrl), Is.EqualTo("<html>thread</html>"));

            Assert.That(plain.Requests, Is.EqualTo(new[] { ThreadUrl + "1/", ThreadUrl + "2/ (xf_smogon_gate=42)", ForumUrl + " (xf_smogon_gate=42)" }));
            Assert.That(browser.Requests, Is.EqualTo(new[] { ThreadUrl + "1/" }));
            Assert.That(handler.BrowserRequests, Is.EqualTo(1));
        }

        [Test]
        public async Task BotCheckDespiteTheCookie_TwiceInARow_SendsThatPartOfTheSiteToTheBrowser()
        {
            // The cookie does not help for threads (bound to the browser, say); listings need none.
            var (client, plain, browser, _) = Create(request => request.RequestUri!.AbsolutePath.StartsWith("/forums/threads/") ? BotCheck : "<html>listing</html>");

            for (var i = 1; i <= 4; i++)
            {
                Assert.That(await client.GetStringAsync(ThreadUrl + i + "/"), Is.EqualTo("<html>page from the browser</html>"));
            }
            Assert.That(await client.GetStringAsync(ForumUrl), Is.EqualTo("<html>listing</html>"));

            Assert.That(plain.Requests.Count(url => url.StartsWith(ThreadUrl)), Is.EqualTo(3), "thread 4 goes to the browser right away");
            Assert.That(browser.Requests, Has.Count.EqualTo(4));
            Assert.That(plain.Requests.Last(), Does.StartWith(ForumUrl));
        }

        [Test]
        public async Task CookiesSetByPlainResponses_AreSentBack()
        {
            var plain = new FakeHandlerWithCookie();
            var handler = new BrowserCookieHttpMessageHandler(plain, new FakeHandler(_ => ""), BrowserCookies, SmogonPages.IsBotCheck);
            using var client = new HttpClient(handler);

            await client.GetStringAsync(ForumUrl);
            await client.GetStringAsync(ForumUrl);

            Assert.That(plain.Cookies, Is.EqualTo(new[] { "", "xf_csrf=abc" }));
        }

        private sealed class FakeHandlerWithCookie : HttpMessageHandler
        {
            public List<string> Cookies { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Cookies.Add(request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : "");
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>listing</html>") };
                response.Headers.Add("Set-Cookie", "xf_csrf=abc; path=/; secure");
                return Task.FromResult(response);
            }
        }
    }
}
