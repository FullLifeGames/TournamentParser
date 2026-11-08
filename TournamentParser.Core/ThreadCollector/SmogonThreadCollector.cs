using Microsoft.Extensions.Caching.Distributed;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using TournamentParser.Data;
using TournamentParser.ThreadScanner;
using TournamentParser.Util;

namespace TournamentParser.ThreadCollector
{
    /// <summary>
    /// Finds the threads of the scanned forums and keeps their pages in the crawl state. With a persistent cache, a run only
    /// reads what changed since the last run:
    /// <list type="bullet">
    /// <item>A forum whose latest post (as the parent forum lists it) is unchanged is not requested at all, nor are its subforums.</item>
    /// <item>A forum listing is read up to the first thread without new posts (listings are sorted by latest post).</item>
    /// <item>Of a thread with a new post, a lock or a new title, page 1 (the tournament post) and the pages from the last known page on are read.</item>
    /// </list>
    /// See <see cref="CollectorOptions"/> for the exceptions to this.
    /// </summary>
    public class SmogonThreadCollector : IThreadCollector
    {
        public const string ForumIndexUrl = Common.SmogonBaseUrl + "/forums/";
        public const string PsTournamentsUrl = Common.SmogonBaseUrl + "/forums/forums/ps-tournaments.698/";

        private const int MaxUnexpectedThreadPagesInARow = 5;

        private readonly CrawlStateStore _store;
        private readonly CollectorOptions _options;

        // A collector instance is one run: forums and threads are handled once, even if several calls reach them.
        private readonly HashSet<string> _visitedForums = [];
        private readonly HashSet<long> _returnedThreads = [];
        private string? _forumIndex;
        private int _unexpectedThreadPagesInARow;

        public SmogonThreadCollector() : this((IDistributedCache?)null)
        {
        }

        public SmogonThreadCollector(IDistributedCache? cache, CollectorOptions? options = null)
            : this(new CrawlStateStore(cache ?? new ProcessLocalCache()), options)
        {
        }

        internal SmogonThreadCollector(CrawlStateStore store, CollectorOptions? options)
        {
            _store = store;
            _options = options ?? new CollectorOptions();
        }

        /// <summary>Requests sent by this collector (retries inside the HTTP handler not counted).</summary>
        public int RequestCount { get; private set; }

        private HttpClient Http => _options.HttpClient ?? Common.HttpClient;

        public Task<IDictionary<string, List<string>>> GetThreadsForForums() =>
            GetGeneralThreadsForForums("Tournaments", new Dictionary<string, string>
            {
                { "PS! Tournaments", PsTournamentsUrl },
            });

        public async Task<IDictionary<string, List<string>>> GetNonTourThreadsForForums()
        {
            var threadsForForums = new Dictionary<string, List<string>>();
            foreach (var forumName in new[] { "Smogon Metagames", "Ruins of Alph", "Battle Stadium Singles" })
            {
                foreach (var forum in await GetGeneralThreadsForForums(forumName).ConfigureAwait(false))
                {
                    threadsForForums[forum.Key] = forum.Value;
                }
            }
            return threadsForForums;
        }

        /// <summary>
        /// Brings the forum named <paramref name="filter"/> on the forum index, the forums in <paramref name="additionals"/>
        /// (name to URL) and all their subforums up to date. Returns forum URL to thread URLs of every thread with stored pages.
        /// </summary>
        public async Task<IDictionary<string, List<string>>> GetGeneralThreadsForForums(
            string filter, IDictionary<string, string>? additionals = null)
        {
            var index = await GetForumIndexAsync().ConfigureAwait(false);
            var nodes = ForumPageParser.ParseNodes(index);
            var subNodeLinks = ForumPageParser.ParseSubNodeLinks(index);

            var roots = new List<ForumNode>();
            var root = nodes.FirstOrDefault(node => node.Title == filter) ?? subNodeLinks.FirstOrDefault(node => node.Title == filter);
            if (root == null)
            {
                // Without the forum, its threads would silently drop out of the output.
                throw new UnexpectedPageException(ForumIndexUrl, $"the forum '{filter}' is not on the forum index");
            }
            roots.Add(root);
            foreach (var additional in additionals ?? new Dictionary<string, string>())
            {
                roots.Add(nodes.FirstOrDefault(node => node.Url == additional.Value)
                    ?? new ForumNode { Title = additional.Key, Url = additional.Value });
            }

            var forums = new List<string>();
            foreach (var forum in roots)
            {
                await VisitForumAsync(forum.Url, forum.LatestPostTimestamp, forums).ConfigureAwait(false);
            }
            return GetStoredThreads(forums);
        }

        private async Task<string> GetForumIndexAsync()
        {
            if (_forumIndex == null)
            {
                var index = await GetPageAsync(ForumIndexUrl).ConfigureAwait(false);
                SmogonPages.EnsureTemplate(index, ForumIndexUrl, "forum_list");
                _forumIndex = index;
            }
            return _forumIndex;
        }

        /// <summary>
        /// Brings a forum and its subforums up to date and adds their URLs to <paramref name="forums"/>.
        /// Returns false if a thread in the subtree could not be read, so the subtree must not be skipped next run.
        /// </summary>
        private async Task<bool> VisitForumAsync(string url, long nodeTimestamp, List<string> forums)
        {
            if (!_visitedForums.Add(url))
            {
                return true;
            }
            forums.Add(url);

            var forum = _store.GetForum(url) ?? new ForumCrawlState();
            var incremental = forum.Complete && !_options.FullRefresh;
            var recentSince = DateTimeOffset.UtcNow.Subtract(_options.RecentActivityWindow).ToUnixTimeSeconds();

            if (incremental && nodeTimestamp > 0 && nodeTimestamp == forum.NodeTimestamp && nodeTimestamp < recentSince)
            {
                // Neither this forum nor any of its subforums got a post since the last run.
                foreach (var subForum in forum.SubForums)
                {
                    AddStoredSubtree(subForum, forums);
                }
                return true;
            }

            // Listings are sorted by latest post. Once a non-sticky thread is not newer than the last run, every thread
            // after it is unchanged too; only threads with recent posts are still looked at for locks and title changes.
            var oldWatermark = forum.Watermark;
            var newWatermark = oldWatermark;
            var listed = new List<ThreadListItem>();
            IList<ForumNode> subForums = [];
            var pages = 1;
            var listingPages = 0;
            var walkedAllPages = true;
            for (var page = 1; page <= pages; page++)
            {
                var pageUrl = SmogonPages.PageUrl(url, page);
                var site = await GetPageAsync(pageUrl).ConfigureAwait(false);
                SmogonPages.EnsureTemplate(site, pageUrl, "forum_view");
                listingPages++;
                if (page == 1)
                {
                    pages = SmogonPages.GetNumberOfPages(site);
                    subForums = ForumPageParser.ParseNodes(site);
                }

                var reachedKnownThreads = false;
                var oldestOnPage = long.MaxValue;
                foreach (var item in ForumPageParser.ParseThreads(site))
                {
                    listed.Add(item);
                    newWatermark = Math.Max(newWatermark, item.LatestPostTimestamp);
                    if (!item.Sticky)
                    {
                        oldestOnPage = Math.Min(oldestOnPage, item.LatestPostTimestamp);
                        reachedKnownThreads |= incremental && item.LatestPostTimestamp <= oldWatermark;
                    }
                }
                if (reachedKnownThreads && oldestOnPage < recentSince)
                {
                    walkedAllPages = page == pages;
                    break;
                }
            }

            var seen = new HashSet<long>();
            var toUpdate = new List<ThreadListItem>();
            foreach (var item in listed)
            {
                // The same thread can show up twice if the listing shifted while paging.
                if (seen.Add(item.ThreadId))
                {
                    forum.Threads[item.ThreadId] = item.Url;
                    toUpdate.Add(item);
                }
            }
            toUpdate.AddRange(forum.RetryThreads.Values.Where(item => !seen.Contains(item.ThreadId)));

            var updatedThreads = 0;
            foreach (var item in toUpdate)
            {
                var result = await UpdateThreadAsync(item, url).ConfigureAwait(false);
                if (result == ThreadUpdate.Updated)
                {
                    updatedThreads++;
                }
                if (result == ThreadUpdate.Failed)
                {
                    forum.RetryThreads[item.ThreadId] = item;
                }
                else
                {
                    forum.RetryThreads.Remove(item.ThreadId);
                }
            }

            if (walkedAllPages)
            {
                // Threads missing from a complete listing were deleted or moved away.
                foreach (var threadId in forum.Threads.Keys.Where(id => !seen.Contains(id)).ToList())
                {
                    forum.Threads.Remove(threadId);
                    forum.RetryThreads.Remove(threadId);
                }
            }

            forum.Watermark = newWatermark;
            forum.Complete = true;
            forum.LastCrawl = DateTime.UtcNow;
            _store.SetForum(url, forum);
            Console.WriteLine($"{url}: {listingPages} listing page(s) read, {updatedThreads} thread(s) updated, {forum.Threads.Count} thread(s) known");

            var clean = forum.RetryThreads.Count == 0;
            foreach (var subForum in subForums)
            {
                clean &= await VisitForumAsync(subForum.Url, subForum.LatestPostTimestamp, forums).ConfigureAwait(false);
            }

            // Only now is the whole subtree up to date; until then the next run looks at it again.
            forum.SubForums = subForums.Select(subForum => subForum.Url).ToList();
            forum.NodeTimestamp = clean ? nodeTimestamp : 0;
            _store.SetForum(url, forum);
            return clean;
        }

        private void AddStoredSubtree(string url, List<string> forums)
        {
            if (!_visitedForums.Add(url))
            {
                return;
            }
            forums.Add(url);
            foreach (var subForum in _store.GetForum(url)?.SubForums ?? [])
            {
                AddStoredSubtree(subForum, forums);
            }
        }

        /// <summary>Forum URL to the URLs of its threads with stored pages; each thread appears once per run.</summary>
        private IDictionary<string, List<string>> GetStoredThreads(IEnumerable<string> forums)
        {
            var threadsForForums = new Dictionary<string, List<string>>();
            foreach (var url in forums)
            {
                var forum = _store.GetForum(url);
                if (forum == null)
                {
                    continue;
                }
                var threadUrls = new List<string>();
                var movedAway = false;
                foreach (var threadId in forum.Threads.Keys.OrderBy(id => id).ToList())
                {
                    var state = _store.GetThread(threadId);
                    if (state == null || !state.HasContent)
                    {
                        continue;
                    }
                    if (state.ForumUrl.Length > 0 && state.ForumUrl != url)
                    {
                        // Moved to another forum, which lists it from now on
                        forum.Threads.Remove(threadId);
                        movedAway = true;
                        continue;
                    }
                    if (_returnedThreads.Add(threadId))
                    {
                        threadUrls.Add(state.Url);
                    }
                }
                if (movedAway)
                {
                    _store.SetForum(url, forum);
                }
                threadsForForums[url] = threadUrls;
            }
            return threadsForForums;
        }

        private enum ThreadUpdate
        {
            Unchanged,
            Updated,
            /// <summary>The thread is gone or not accessible (404, 403, 410).</summary>
            Gone,
            /// <summary>The thread could not be read and is tried again next run.</summary>
            Failed,
        }

        /// <summary>Brings the stored state of a listed thread up to date.</summary>
        private async Task<ThreadUpdate> UpdateThreadAsync(ThreadListItem item, string forumUrl)
        {
            var state = _store.GetThread(item.ThreadId);
            var migrated = false;
            if (state == null)
            {
                state = MigrateLegacyState(item);
                migrated = state != null;
            }

            var changed = _options.FullRefresh
                || state == null
                || state.NumberOfPages == 0
                || state.LatestPostTimestamp != item.LatestPostTimestamp
                || (state.Locked.HasValue && state.Locked.Value != item.Locked)
                || (state.Title != null && state.Title != item.Title)
                || state.MissingPages.Any();
            if (!changed)
            {
                if (migrated || state!.ForumUrl != forumUrl || state.Url != item.Url || state.Title == null || state.Locked == null)
                {
                    state!.ForumUrl = forumUrl;
                    state.Url = item.Url;
                    state.Title = item.Title;
                    state.Locked = item.Locked;
                    _store.SetThread(item.ThreadId, state);
                }
                return ThreadUpdate.Unchanged;
            }

            var listedLastPage = Math.Max(1, item.LastPage);
            var pagesToRead = new SortedSet<int>();
            if (_options.FullRefresh || state == null || state.NumberOfPages == 0)
            {
                for (var page = 1; page <= listedLastPage; page++)
                {
                    pagesToRead.Add(page);
                }
            }
            else
            {
                // Page 1 holds the tournament post, which hosts edit (winners in bold, replays) without bumping the thread.
                pagesToRead.Add(1);
                pagesToRead.UnionWith(state.MissingPages.Where(page => page <= listedLastPage));
                for (var page = Math.Min(state.NumberOfPages, listedLastPage); page <= listedLastPage; page++)
                {
                    pagesToRead.Add(page);
                }
            }
            var queue = pagesToRead.ToList();

            Console.WriteLine($"Reading {item.Url} (page {FormatPages(queue)} of {listedLastPage})");

            var numberOfPages = listedLastPage;
            var readPages = new Dictionary<int, string>();
            for (var i = 0; i < queue.Count; i++)
            {
                var page = queue[i];
                if (page > numberOfPages)
                {
                    break;
                }

                var pageUrl = SmogonPages.PageUrl(item.Url, page);
                string site;
                try
                {
                    site = await GetPageAsync(pageUrl).ConfigureAwait(false);
                }
                catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                {
                    Console.WriteLine($"Skipping {pageUrl}: HTTP {(int)e.StatusCode!}");
                    return ThreadUpdate.Gone;
                }
                catch (HttpRequestException e)
                {
                    Console.WriteLine($"Skipping {pageUrl} until the next run: {e.Message}");
                    return ThreadUpdate.Failed;
                }
                try
                {
                    SmogonPages.EnsureThreadPage(site, pageUrl);
                    _unexpectedThreadPagesInARow = 0;
                }
                catch (UnexpectedPageException e)
                {
                    // A single odd thread is skipped and retried next run; many in a row mean the markup changed, and the crawl stops.
                    if (++_unexpectedThreadPagesInARow >= MaxUnexpectedThreadPagesInARow)
                    {
                        throw;
                    }
                    Console.WriteLine("Skipping thread until the next run: " + e.Message);
                    return ThreadUpdate.Failed;
                }

                numberOfPages = SmogonPages.GetNumberOfPages(site);
                if (page > numberOfPages)
                {
                    // Posts were deleted and the requested page no longer exists; Smogon served the last page instead.
                    // Read the real last page next (unless it was read already); the loop then stops at the missing pages.
                    if (!queue.Contains(numberOfPages))
                    {
                        queue.Insert(i + 1, numberOfPages);
                    }
                    continue;
                }
                if (i == queue.Count - 1 && numberOfPages > page)
                {
                    // More pages than the listing showed
                    for (var extra = page + 1; extra <= numberOfPages; extra++)
                    {
                        queue.Add(extra);
                    }
                }

                try
                {
                    readPages[page] = SmogonThreadScanner.ExtractImportantLines(item.Url, page, site);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Skipping {item.Url} until the next run, page {page} could not be parsed: {e.Message}");
                    return ThreadUpdate.Failed;
                }
            }

            state ??= new ThreadCrawlState();
            foreach (var (page, importantLines) in readPages)
            {
                _store.SetPage(item.ThreadId, page, importantLines);
                state.StoredPages.Add(page);
                state.LegacyPages.Remove(page);
            }
            foreach (var stalePage in state.StoredPages.Where(page => page > numberOfPages).ToList())
            {
                _store.RemovePage(item.ThreadId, stalePage);
                state.StoredPages.Remove(stalePage);
            }
            state.LegacyPages.RemoveWhere(page => page > numberOfPages);
            if (state.LegacyPages.Count == 0)
            {
                state.LegacyKey = null;
            }
            state.NumberOfPages = numberOfPages;
            state.LatestPostTimestamp = item.LatestPostTimestamp;
            state.Title = item.Title;
            state.Locked = item.Locked;
            state.Url = item.Url;
            state.ForumUrl = forumUrl;
            _store.SetThread(item.ThreadId, state);
            return ThreadUpdate.Updated;
        }

        /// <summary>
        /// Takes over the entry of a version before 3.0 without changing it. Pages holding real thread content are used from
        /// there; empty pages (2.x also stored bot checks) count as missing. If the stored pages already cover the latest post,
        /// the thread counts as unchanged and is not read at all; otherwise page 1 and the pages from its last known page on are read.
        /// </summary>
        private ThreadCrawlState? MigrateLegacyState(ThreadListItem item)
        {
            // Until July 2026, 2.x used http:// URLs, which are also its cache keys; a database can hold both.
            var keys = new List<string> { item.Url };
            if (item.Url.StartsWith("https://"))
            {
                keys.Add("http://" + item.Url["https://".Length..]);
            }

            ThreadCrawlState? best = null;
            foreach (var key in keys)
            {
                var candidate = MigrateLegacyEntry(key, item);
                if (candidate == null)
                {
                    continue;
                }
                var covers = candidate.LatestPostTimestamp != 0;
                var bestCovers = best?.LatestPostTimestamp is > 0;
                if (best == null
                    || (covers && !bestCovers)
                    || (covers == bestCovers && candidate.LegacyPages.Count > best.LegacyPages.Count))
                {
                    best = candidate;
                }
            }
            return best;
        }

        private ThreadCrawlState? MigrateLegacyEntry(string key, ThreadListItem item)
        {
            var legacy = _store.GetLegacyThread(key);
            if (legacy?.CollectedLinks == null || legacy.CollectedLinks.Count == 0)
            {
                return null;
            }

            var state = new ThreadCrawlState
            {
                Url = item.Url,
                LegacyKey = key,
                NumberOfPages = legacy.CollectedLinks.Count,
            };
            string? titleLine = null;
            var locked = false;
            for (var page = 1; page <= legacy.CollectedLinks.Count; page++)
            {
                var content = legacy.CollectedLinks[page - 1];
                if (!SmogonPages.IsUsableStoredPage(content))
                {
                    continue;
                }
                state.LegacyPages.Add(page);
                foreach (var line in Common.EnumerateLines(content))
                {
                    if (line.Contains(SmogonPages.TitleMarker))
                    {
                        titleLine = line;
                    }
                    locked |= line.Contains(SmogonPages.LockedMarker);
                }
            }
            if (state.LegacyPages.Count == 0)
            {
                return null;
            }
            state.Title = titleLine != null ? SmogonPages.ExtractThreadTitle(titleLine) : null;
            state.Locked = locked;

            if (legacy.CollectedLinks.Count == item.LastPage
                && !state.MissingPages.Any()
                && LegacyCoversLatestPost(legacy.LastPost, item))
            {
                state.LatestPostTimestamp = item.LatestPostTimestamp;
            }
            return state;
        }

        /// <summary>Whether a 2.x entry was written after the latest post that the listing shows.</summary>
        internal static bool LegacyCoversLatestPost(DateTime legacyLastPost, ThreadListItem item)
        {
            if (legacyLastPost.Kind != DateTimeKind.Unspecified)
            {
                // Before July 2026 the post dates could not be read, and 2.x stored the time of the scan (DateTime.Now) instead.
                return legacyLastPost.ToUniversalTime() >= DateTimeOffset.FromUnixTimeSeconds(item.LatestPostTimestamp).UtcDateTime;
            }
            // The date of the last post as Smogon displays it, to the minute.
            return DateTime.TryParseExact(item.LatestPostDisplayDate.Replace("at ", ""), "MMM d, yyyy h:mm tt",
                    CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var latestPost)
                && legacyLastPost >= latestPost;
        }

        private async Task<string> GetPageAsync(string url)
        {
            if (_options.MaxRequests > 0 && RequestCount >= _options.MaxRequests)
            {
                throw new RequestBudgetExhaustedException(_options.MaxRequests);
            }
            RequestCount++;
            return await Http.GetStringAsync(url).ConfigureAwait(false);
        }

        private static string FormatPages(IList<int> pages)
        {
            var parts = new List<string>();
            for (var i = 0; i < pages.Count; i++)
            {
                var start = pages[i];
                while (i + 1 < pages.Count && pages[i + 1] == pages[i] + 1)
                {
                    i++;
                }
                parts.Add(start == pages[i] ? start.ToString() : start + "-" + pages[i]);
            }
            return string.Join(", ", parts);
        }
    }
}
