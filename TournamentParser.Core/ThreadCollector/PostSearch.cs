using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TournamentParser.Util;

namespace TournamentParser.ThreadCollector
{
    /// <summary>A post found by Smogon's search. A thread hit (<see cref="PostId"/> null) is the first post of the thread.</summary>
    internal sealed record SearchHit(long ThreadId, string ThreadUrl, long? PostId, int Position, DateTime PostDate, string ForumUrl)
    {
        /// <summary>Posts per thread page on Smogon.</summary>
        public const int PostsPerPage = 25;

        /// <summary>The page of the thread the post is on, from its position ("Post #57" is on page 3).</summary>
        public int Page => Math.Max(1, (Position - 1) / PostsPerPage + 1);
    }

    /// <summary>The posts a search found, and the forums it could not cover: they have a day with more posts than a search shows.</summary>
    internal sealed record SearchResult(IReadOnlyList<SearchHit> Hits, IReadOnlySet<long> Uncovered);

    /// <summary>
    /// Finds posts through Smogon's guest search (XenForo), newest first. Smogon cuts a search off after 200 results; a search
    /// that reaches that is repeated for the days before its oldest result, so that the dates page through any number of posts.
    /// </summary>
    internal sealed partial class PostSearch
    {
        /// <summary>The posts the scanner takes replays from: links to Showdown replays (labelled links count as well).</summary>
        public const string ReplayQuery = "replay.pokemonshowdown.com";

        /// <summary>200 results, 10 per page: a search that fills this many pages may have been cut off.</summary>
        public const int MaxResultPages = 20;

        private const int MaxFloodWaits = 3;

        private readonly Func<string, Task<string>> _getPage;
        private readonly Func<TimeSpan, Task> _delay;

        public PostSearch(Func<string, Task<string>> getPage, Func<TimeSpan, Task>? delay = null)
        {
            _getPage = getPage;
            _delay = delay ?? (wait => Task.Delay(wait));
        }

        /// <summary>What a search is limited to: forums with all their subforums, or a single thread.</summary>
        public sealed record Filter(IReadOnlyList<long> NodeIds, long? ThreadId)
        {
            public static Filter Forums(IReadOnlyList<long> nodeIds) => new(nodeIds, null);

            public static Filter Thread(long threadId) => new([], threadId);
        }

        /// <summary>
        /// The search for posts matching <paramref name="query"/>, from <paramref name="since"/> on and before
        /// <paramref name="before"/>, newest first. Smogon reads the dates in its own time zone, so callers keep a day of margin.
        /// </summary>
        public static string Url(string query, Filter filter, DateTime? since, DateTime? before) =>
            Common.SmogonBaseUrl + "/forums/search/1/?q=" + Uri.EscapeDataString(query) + "&t=post&o=date"
            + (since is DateTime start ? "&c[newer_than]=" + start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "")
            + (before is DateTime end ? "&c[older_than]=" + end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "")
            + (filter.ThreadId is long threadId ? "&c[thread]=" + threadId : "")
            + string.Concat(filter.NodeIds.Select((id, index) => $"&c[nodes][{index}]={id}"))
            + (filter.NodeIds.Count > 0 ? "&c[child_nodes]=1" : "");

        /// <summary>
        /// All replay posts in the forums <paramref name="nodeIds"/> and their subforums from <paramref name="since"/> on.
        /// Null if guests cannot search.
        /// </summary>
        public async Task<SearchResult?> SearchForumsAsync(IReadOnlyList<long> nodeIds, DateTime? since)
        {
            var hits = new Dictionary<(long ThreadId, long? PostId), SearchHit>();
            var uncovered = new HashSet<long>();
            if (!await SearchRangeAsync(Filter.Forums(nodeIds), since, null, hits, uncovered).ConfigureAwait(false))
            {
                return null;
            }
            Console.WriteLine($"Searched replay posts{(since != null ? $" since {since:yyyy-MM-dd}" : "")} in {nodeIds.Count} forum(s): {hits.Count} post(s)");
            return new SearchResult(hits.Values.ToList(), uncovered);
        }

        /// <summary>The outcome of <see cref="SearchThreadAsync"/>.</summary>
        public enum ThreadSearchOutcome
        {
            Found,
            /// <summary>
            /// The thread has more replay posts than allowed, so reading its pages is cheaper; or more on a single day than a
            /// search shows.
            /// </summary>
            TooManyResults,
            /// <summary>Guests cannot search.</summary>
            Unavailable,
        }

        /// <summary>
        /// All replay posts of a thread. Gives up after the first result page if the results take more than
        /// <paramref name="maxResultPages"/> pages, so that a search never costs much more than reading the thread.
        /// </summary>
        public async Task<(ThreadSearchOutcome Outcome, IReadOnlyList<SearchHit> Hits)> SearchThreadAsync(long threadId, int maxResultPages)
        {
            var filter = Filter.Thread(threadId);
            var url = Url(ReplayQuery, filter, null, null);
            var html = await GetResultPageAsync(url).ConfigureAwait(false);
            if (html == null)
            {
                return (ThreadSearchOutcome.Unavailable, []);
            }
            if (SmogonPages.GetNumberOfPages(html) > maxResultPages)
            {
                return (ThreadSearchOutcome.TooManyResults, []);
            }

            var hits = new Dictionary<(long ThreadId, long? PostId), SearchHit>();
            var (found, cutOff) = await ReadResultPagesAsync(html, 1).ConfigureAwait(false);
            if (found == null)
            {
                return (ThreadSearchOutcome.Unavailable, []);
            }
            AddHits(hits, found);
            var uncovered = new HashSet<long>();
            if (cutOff && !await SearchOlderAsync(filter, null, found, hits, uncovered).ConfigureAwait(false))
            {
                return (ThreadSearchOutcome.Unavailable, []);
            }
            return uncovered.Count > 0
                ? (ThreadSearchOutcome.TooManyResults, [])
                : (ThreadSearchOutcome.Found, hits.Values.Where(hit => hit.ThreadId == threadId).ToList());
        }

        private static void AddHits(Dictionary<(long, long?), SearchHit> hits, IEnumerable<SearchHit> found)
        {
            foreach (var hit in found)
            {
                hits.TryAdd((hit.ThreadId, hit.PostId), hit);
            }
        }

        /// <summary>
        /// Pages through the results by date. A day with more results than a search shows is split by forum; a single forum
        /// with such a day is <paramref name="uncovered"/>. False if guests cannot search.
        /// </summary>
        private async Task<bool> SearchRangeAsync(Filter filter, DateTime? since, DateTime? before, Dictionary<(long, long?), SearchHit> hits, HashSet<long> uncovered)
        {
            var html = await GetResultPageAsync(Url(ReplayQuery, filter, since, before)).ConfigureAwait(false);
            if (html == null)
            {
                return false;
            }
            var (found, cutOff) = await ReadResultPagesAsync(html, 1).ConfigureAwait(false);
            if (found == null)
            {
                return false;
            }
            AddHits(hits, found);
            return !cutOff || await SearchOlderAsync(filter, since, found, hits, uncovered, before).ConfigureAwait(false);
        }

        /// <summary>A search was cut off: searches again for the days up to its oldest result (results come newest first).</summary>
        private async Task<bool> SearchOlderAsync(Filter filter, DateTime? since, List<SearchHit> found, Dictionary<(long, long?), SearchHit> hits, HashSet<long> uncovered, DateTime? before = null)
        {
            var oldestDay = found.Count > 0 ? found.Min(hit => hit.PostDate).Date.AddDays(1) : (DateTime?)null;
            if (oldestDay != null && (before == null || oldestDay < before))
            {
                Console.WriteLine($"  {hits.Count} replay post(s) so far, searching before {oldestDay:yyyy-MM-dd}");
                return await SearchRangeAsync(filter, since, oldestDay, hits, uncovered).ConfigureAwait(false);
            }
            if (filter.NodeIds.Count > 1)
            {
                var half = filter.NodeIds.Count / 2;
                return await SearchRangeAsync(Filter.Forums(filter.NodeIds.Take(half).ToList()), since, before, hits, uncovered).ConfigureAwait(false)
                    && await SearchRangeAsync(Filter.Forums(filter.NodeIds.Skip(half).ToList()), since, before, hits, uncovered).ConfigureAwait(false);
            }
            var what = filter.ThreadId is long threadId ? "Thread " + threadId : "Forum " + filter.NodeIds[0];
            Console.WriteLine($"{what} has more than {MaxResultPages * 10} replay posts on a single day; its pages are read instead.");
            uncovered.Add(filter.ThreadId ?? filter.NodeIds[0]);
            return true;
        }

        /// <summary>The hits of a search, starting with its first result page; whether the search was cut off.</summary>
        private async Task<(List<SearchHit>? Hits, bool CutOff)> ReadResultPagesAsync(string firstPage, int pagesRead)
        {
            var hits = new List<SearchHit>(SearchResultParser.Parse(firstPage, out var url));
            var pages = pagesRead;
            while (url != null && pages < MaxResultPages)
            {
                var html = await GetResultPageAsync(url).ConfigureAwait(false);
                if (html == null)
                {
                    return (null, false);
                }
                pages++;
                hits.AddRange(SearchResultParser.Parse(html, out url));
            }
            return (hits, pages >= MaxResultPages);
        }

        [GeneratedRegex(@"must wait at least (\d+) seconds")]
        private static partial Regex FloodWaitRegex();

        [GeneratedRegex("<div class=\"blockMessage[^\"]*\">\\s*([^<]+)")]
        private static partial Regex MessageRegex();

        /// <summary>A result page; null (after a message) if Smogon answers with anything else.</summary>
        private async Task<string?> GetResultPageAsync(string url)
        {
            for (var attempt = 0; ; attempt++)
            {
                string html;
                try
                {
                    html = await _getPage(url).ConfigureAwait(false);
                }
                catch (HttpRequestException e)
                {
                    Console.WriteLine($"Search not available ({e.Message}); thread pages are read instead.");
                    return null;
                }
                if (SmogonPages.IsBotCheck(html))
                {
                    throw new SmogonAccessBlockedException(url);
                }
                if (html.Contains("data-template=\"search_results\""))
                {
                    return html;
                }
                // Searches in quick succession
                var flood = FloodWaitRegex().Match(html);
                if (flood.Success && attempt < MaxFloodWaits)
                {
                    await _delay(TimeSpan.FromSeconds(int.Parse(flood.Groups[1].Value) + 1)).ConfigureAwait(false);
                    continue;
                }
                // Such as "The search could not be completed because the search keywords were too short, too long, or too common."
                var message = MessageRegex().Matches(html).Select(match => match.Groups[1].Value.Trim())
                    .FirstOrDefault(text => !text.Contains("JavaScript") && !text.Contains("out of date browser"));
                Console.WriteLine($"Search not available ({message ?? url + " answered without results page"}); thread pages are read instead.");
                return null;
            }
        }
    }

    /// <summary>Reads the results of a XenForo search page ("search_results").</summary>
    internal static partial class SearchResultParser
    {
        private const string ItemStart = "<li class=\"block-row block-row--separated";

        // Profile posts and other content link elsewhere and are skipped. XenForo leaves out the slug of titles without URL-safe characters.
        [GeneratedRegex("<h3 class=\"contentRow-title\">\\s*<a href=\"(/forums/threads/(?:[^\"/]*?\\.)?(\\d+)/)(?:post-(\\d+))?\"[^>]*>", RegexOptions.Singleline)]
        private static partial Regex TitleRegex();

        [GeneratedRegex("<li>Post #([\\d,.]+)</li>")]
        private static partial Regex PositionRegex();

        [GeneratedRegex("<time\\s[^>]*data-timestamp=\"(\\d+)\"")]
        private static partial Regex TimestampRegex();

        // Forums without node ID in their URL (".../forums/gen1/") too
        [GeneratedRegex("Forum: <a href=\"(/forums/forums/[^\"]+/)\"")]
        private static partial Regex ForumRegex();

        [GeneratedRegex("<a href=\"([^\"]+)\" class=\"pageNav-jump pageNav-jump--next\"")]
        private static partial Regex NextPageRegex();

        public static IList<SearchHit> Parse(string html, out string? nextPageUrl)
        {
            var next = NextPageRegex().Match(html);
            nextPageUrl = next.Success ? Common.SmogonBaseUrl + WebUtility.HtmlDecode(next.Groups[1].Value) : null;

            var hits = new List<SearchHit>();
            var start = html.IndexOf(ItemStart);
            while (start != -1)
            {
                var end = html.IndexOf(ItemStart, start + ItemStart.Length);
                var hit = ParseItem(end == -1 ? html[start..] : html[start..end]);
                if (hit != null)
                {
                    hits.Add(hit);
                }
                start = end;
            }
            return hits;
        }

        private static SearchHit? ParseItem(string block)
        {
            var title = TitleRegex().Match(block);
            var timestamp = TimestampRegex().Match(block);
            var forum = ForumRegex().Match(block);
            if (!title.Success || !timestamp.Success || !forum.Success)
            {
                return null;
            }

            long? postId = title.Groups[3].Success ? long.Parse(title.Groups[3].Value) : null;
            var position = 1;
            var positionMatch = PositionRegex().Match(block);
            if (postId != null && positionMatch.Success)
            {
                position = int.Parse(positionMatch.Groups[1].Value.Replace(",", "").Replace(".", ""), CultureInfo.InvariantCulture);
            }

            return new SearchHit(
                ThreadId: long.Parse(title.Groups[2].Value),
                ThreadUrl: Common.SmogonBaseUrl + title.Groups[1].Value,
                PostId: postId,
                Position: position,
                PostDate: DateTimeOffset.FromUnixTimeSeconds(long.Parse(timestamp.Groups[1].Value)).UtcDateTime,
                ForumUrl: Common.SmogonBaseUrl + WebUtility.HtmlDecode(forum.Groups[1].Value));
        }
    }
}
