using System;
using System.Net;
using System.Text.RegularExpressions;

namespace TournamentParser.Util
{
    /// <summary>
    /// Smogon answered with a bot check instead of the requested content.
    /// The crawl stops so that the stored state and the output are not overwritten with empty results.
    /// </summary>
    public class SmogonAccessBlockedException : Exception
    {
        public string Url { get; }

        public SmogonAccessBlockedException(string url)
            : base($"Smogon returned a bot check instead of the page {url}. The crawl was stopped; the output files were left untouched.")
        {
            Url = url;
        }
    }

    /// <summary>
    /// A page did not look like the expected forum page (changed markup, error page, ...).
    /// The crawl stops so that the stored state and the output are not overwritten with empty results.
    /// </summary>
    public class UnexpectedPageException : Exception
    {
        public string Url { get; }

        public UnexpectedPageException(string url, string reason)
            : base($"Unexpected content at {url}: {reason}. The crawl was stopped; the output files were left untouched.")
        {
            Url = url;
        }
    }

    /// <summary>The crawl reached <c>CollectorOptions.MaxRequests</c>; the next run continues where this one stopped.</summary>
    public class RequestBudgetExhaustedException : Exception
    {
        public RequestBudgetExhaustedException(int maxRequests)
            : base($"The request budget of {maxRequests} requests is used up. The crawl was stopped; the next run continues from here.")
        {
        }
    }

    internal static partial class SmogonPages
    {
        /// <summary>Start of every post; <see cref="ThreadScanner.SmogonThreadScanner"/> keeps these lines when it stores a page.</summary>
        public const string PostMarker = "<article class=\"message message--";

        public const string LockedMarker = "blockStatus-message--locked";

        public const string TitleMarker = "<h1 class=\"p-title-value\">";

        /// <summary>Page 1 is requested without suffix, since ".../page-1" answers with a redirect (one extra request).</summary>
        public static string PageUrl(string baseUrl, int page) => page <= 1 ? baseUrl : baseUrl + "page-" + page;

        /// <summary>
        /// Smogon's proof-of-work page ("Checking your browser...", cookie xf_smogon_gate). Real forum pages always carry a
        /// XenForo template name, so a post that merely mentions the gate does not count.
        /// </summary>
        public static bool IsBotCheck(string html) =>
            !html.Contains("data-template=\"") && (html.Contains("xf_smogon_gate") || html.Contains("Checking your browser"));

        /// <summary>Forum index and forum listing pages carry the XenForo template name.</summary>
        public static void EnsureTemplate(string html, string url, string template)
        {
            if (IsBotCheck(html))
            {
                throw new SmogonAccessBlockedException(url);
            }
            if (!html.Contains("data-template=\"" + template + "\""))
            {
                throw new UnexpectedPageException(url, "expected the XenForo template '" + template + "'");
            }
        }

        public static void EnsureThreadPage(string html, string url)
        {
            if (IsBotCheck(html))
            {
                throw new SmogonAccessBlockedException(url);
            }
            if (!html.Contains(PostMarker))
            {
                throw new UnexpectedPageException(url, "the page contains no posts");
            }
        }

        /// <summary>
        /// Whether a page stored by version 2.x holds real thread content. 2.x also stored bot checks and error pages,
        /// which leave an empty page behind.
        /// </summary>
        public static bool IsUsableStoredPage(string? storedPage) => storedPage != null && storedPage.Contains(PostMarker);

        [GeneratedRegex("class=\"pageNav-page[^\"]*\"[^>]*>\\s*<a[^>]*>\\s*(\\d+)\\s*</a>")]
        private static partial Regex PageNavPageRegex();

        public static int GetNumberOfPages(string html)
        {
            var pages = 1;
            if (!html.Contains("<nav class=\"pageNavWrapper"))
            {
                return pages;
            }
            foreach (Match match in PageNavPageRegex().Matches(html))
            {
                if (int.TryParse(match.Groups[1].Value, out var page) && page > pages)
                {
                    pages = page;
                }
            }
            return pages;
        }

        // XenForo leaves out the slug (".../threads/123/") when a title has no URL-safe characters.
        [GeneratedRegex("/threads/(?:[^/]*\\.)?(\\d+)/?$")]
        private static partial Regex ThreadIdRegex();

        public static bool TryGetThreadId(string threadUrl, out long threadId)
        {
            var match = ThreadIdRegex().Match(threadUrl);
            threadId = 0;
            return match.Success && long.TryParse(match.Groups[1].Value, out threadId);
        }

        /// <summary>
        /// The thread title of a line containing <see cref="TitleMarker"/>, cut out the way the scanner does it
        /// (text after any prefix label), HTML-decoded so that it compares to the title of a forum listing.
        /// </summary>
        public static string ExtractThreadTitle(string titleLine)
        {
            var title = titleLine[..titleLine.LastIndexOf('<')];
            title = title[(title.LastIndexOf('>') + 1)..];
            return WebUtility.HtmlDecode(title).Trim();
        }
    }
}
