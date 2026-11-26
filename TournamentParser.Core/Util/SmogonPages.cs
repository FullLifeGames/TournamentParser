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
        /// <summary>
        /// Start of a post until 2026-11-13. <see cref="ThreadScanner.SmogonThreadScanner"/> keeps these lines when it
        /// stores a page, so pages stored before then carry this form.
        /// </summary>
        private const string PostStartUntil20261113 = "<article class=\"message message--post js-post js-inlineModContainer";

        /// <summary>Start of a post since 2026-11-14 (followed by js-inlineModContainer until 2026-11-23).</summary>
        private const string PostStart = "<article class=\"message js-post";

        /// <summary>
        /// Whether the line opens a post that the scanner counts, in either markup. The first post of an article thread
        /// ("message message--article js-post") never counted.
        /// </summary>
        public static bool IsPostStart(string line) => line.Contains(PostStart) || line.Contains(PostStartUntil20261113);

        // Any post in either markup, the first post of an article thread included.
        [GeneratedRegex("<article class=\"message [^\"]*\\bjs-post\\b")]
        private static partial Regex AnyPostStartRegex();

        /// <summary>Whether a page or a stored page contains a post.</summary>
        public static bool ContainsPost(string html) => AnyPostStartRegex().IsMatch(html);

        public const string LockedMarker = "blockStatus-message--locked";

        public const string TitleMarker = "<h1 class=\"p-title-value\">";

        /// <summary>Page 1 is requested without suffix, since ".../page-1" answers with a redirect (one extra request).</summary>
        public static string PageUrl(string baseUrl, int page) => page <= 1 ? baseUrl : baseUrl + "page-" + page;

        /// <summary>
        /// Smogon's proof-of-work page ("Checking your browser...", cookie xf_smogon_gate, since 2026-11-24 forum_gate). Real
        /// forum pages always carry a XenForo template name, so a post that merely mentions the gate does not count.
        /// </summary>
        public static bool IsBotCheck(string html) =>
            !HasTemplate(html) && (html.Contains("xf_smogon_gate") || html.Contains("forum_gate=") || html.Contains("Checking your browser"));

        /// <summary>
        /// Whether the page names the XenForo template (any one if null): data-template="forum_list" until 2026-11-23, since
        /// then only the class template-forum_list on the &lt;html&gt; tag.
        /// </summary>
        public static bool HasTemplate(string html, string? template = null)
        {
            var name = template == null ? "[^\"\\s]+" : Regex.Escape(template);
            return Regex.IsMatch(html, "<html\\b[^>]*\\sclass=\"(?:[^\"]*\\s)?template-" + name + "[\"\\s]")
                || Regex.IsMatch(html, "data-template=\"" + name + "\"");
        }

        /// <summary>Forum index and forum listing pages carry the XenForo template name.</summary>
        public static void EnsureTemplate(string html, string url, string template)
        {
            if (IsBotCheck(html))
            {
                throw new SmogonAccessBlockedException(url);
            }
            if (!HasTemplate(html, template))
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
            if (!ContainsPost(html))
            {
                throw new UnexpectedPageException(url, "the page contains no posts");
            }
        }

        /// <summary>
        /// Whether a page stored by version 2.x holds real thread content. 2.x also stored bot checks and error pages,
        /// which leave an empty page behind.
        /// </summary>
        public static bool IsUsableStoredPage(string? storedPage) => storedPage != null && ContainsPost(storedPage);

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

        [GeneratedRegex("class=\"pageNav-page pageNav-page--current[^\"]*\"[^>]*>\\s*<a[^>]*>\\s*(\\d+)\\s*</a>")]
        private static partial Regex CurrentPageRegex();

        /// <summary>The page shown, from the page navigation (a post link like ".../post-123" lands on the page of the post).</summary>
        public static int GetCurrentPage(string html)
        {
            var current = CurrentPageRegex().Match(html);
            return current.Success && int.TryParse(current.Groups[1].Value, out var page) ? page : 1;
        }

        [GeneratedRegex("\\.(\\d+)/?$")]
        private static partial Regex NodeIdRegex();

        /// <summary>The last path segment of a forum URL (".../forums/tournaments.34/" is "tournaments.34", ".../forums/gen1/" is "gen1").</summary>
        public static string GetForumKey(string forumUrl)
        {
            var path = forumUrl.TrimEnd('/');
            return path[(path.LastIndexOf('/') + 1)..];
        }

        /// <summary>The node ID of a forum URL (".../forums/tournaments.34/" is 34), null if there is none (".../forums/gen1/").</summary>
        public static long? GetForumId(string forumUrl)
        {
            var id = NodeIdRegex().Match(forumUrl);
            return id.Success ? long.Parse(id.Groups[1].Value) : null;
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

        // Until 2026-11-25 the <time> tag carried data-timestamp; since 2026-11-26 a <dynamic-time timestamp> inside it does.
        [GeneratedRegex("\\G<time\\b[^>]*?(?:\\sdata-timestamp=\"(\\d+)\"|>\\s*<dynamic-time\\b[^>]*?\\stimestamp=\"(\\d+)\")")]
        private static partial Regex TimeTagTimestampRegex();

        /// <summary>The Unix time (seconds) of the &lt;time&gt; tag that starts at <paramref name="index"/>; null if it shows none.</summary>
        public static long? GetTimestamp(string html, int index)
        {
            var match = TimeTagTimestampRegex().Match(html, index);
            return match.Success ? long.Parse(match.Groups[match.Groups[1].Success ? 1 : 2].Value) : null;
        }
    }
}
