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
    /// <item>
    /// Only threads with pairings in posts 1 or 2 (and replay threads) can yield matches after page 1; of all other threads only
    /// page 1 is read.
    /// </item>
    /// <item>
    /// Smogon's search finds the replay posts since the last run (<see cref="CollectorOptions.UseSearch"/>). A thread with new
    /// posts is read only if the search finds new replay posts in it, or its title or lock changes: then page 1 (the tournament
    /// post), and of a thread with pairings the new pages with replay posts. Replay threads are read on every new post.
    /// </item>
    /// </list>
    /// See <see cref="CollectorOptions"/> for the exceptions to this.
    /// </summary>
    public class SmogonThreadCollector : IThreadCollector
    {
        public const string ForumIndexUrl = Common.SmogonBaseUrl + "/forums/";
        public const string PsTournamentsUrl = Common.SmogonBaseUrl + "/forums/forums/ps-tournaments.698/";

        private const int MaxUnexpectedThreadPagesInARow = 5;

        /// <summary>
        /// A thread with more pages to read than this, that the search of the run does not cover, first asks the search which of
        /// them hold replay posts. Tournament threads have replays on most pages, so for a few pages the search costs more than it saves.
        /// </summary>
        private const int DirectReadLimit = 5;

        /// <summary>
        /// Posts are assumed to be in Smogon's search index this long after they were made. A thread whose latest post is
        /// younger than this when the search starts is read as if there were no search.
        /// </summary>
        private static readonly TimeSpan SearchIndexDelay = TimeSpan.FromMinutes(15);

        private readonly CrawlStateStore _store;
        private readonly CollectorOptions _options;
        private readonly PostSearch _postSearch;

        // A collector instance is one run: forums and threads are handled once, even if several calls reach them.
        private readonly HashSet<string> _visitedForums = [];
        private readonly HashSet<long> _returnedThreads = [];
        private string? _forumIndex;
        private int _unexpectedThreadPagesInARow;
        private bool _searchUnavailable;

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
            _postSearch = new PostSearch(GetPageAsync);
        }

        /// <summary>Requests sent by this collector (retries inside the HTTP handler not counted).</summary>
        public int RequestCount { get; private set; }

        private HttpClient Http => _options.HttpClient ?? Common.HttpClient;

        private bool SearchAllowed => _options.UseSearch && !_options.FullRefresh && !_searchUnavailable;

        public Task<IDictionary<string, List<string>>> GetThreadsForForums() =>
            CollectAsync(["Tournaments"], new Dictionary<string, string>
            {
                { "PS! Tournaments", PsTournamentsUrl },
            });

        public Task<IDictionary<string, List<string>>> GetNonTourThreadsForForums() =>
            CollectAsync(["Smogon Metagames", "Ruins of Alph", "Battle Stadium Singles"], null);

        /// <summary>
        /// Brings the forum named <paramref name="filter"/> on the forum index, the forums in <paramref name="additionals"/>
        /// (name to URL) and all their subforums up to date. Returns forum URL to thread URLs of every thread with stored pages.
        /// </summary>
        public Task<IDictionary<string, List<string>>> GetGeneralThreadsForForums(
            string filter, IDictionary<string, string>? additionals = null) =>
            CollectAsync([filter], additionals);

        private async Task<IDictionary<string, List<string>>> CollectAsync(IEnumerable<string> names, IDictionary<string, string>? additionals)
        {
            var started = DateTime.UtcNow;
            var index = await GetForumIndexAsync().ConfigureAwait(false);
            var nodes = ForumPageParser.ParseNodes(index);
            var subNodeLinks = ForumPageParser.ParseSubNodeLinks(index);

            var roots = new List<ForumNode>();
            foreach (var name in names)
            {
                var root = nodes.FirstOrDefault(node => node.Title == name) ?? subNodeLinks.FirstOrDefault(node => node.Title == name);
                // Without the forum, its threads would silently drop out of the output.
                roots.Add(root ?? throw new UnexpectedPageException(ForumIndexUrl, $"the forum '{name}' is not on the forum index"));
            }
            foreach (var additional in additionals ?? new Dictionary<string, string>())
            {
                roots.Add(nodes.FirstOrDefault(node => node.Url == additional.Value)
                    ?? new ForumNode { Title = additional.Key, Url = additional.Value });
            }

            // Searched before the listings are walked: a post the listing shows is then either found or newer than the search.
            var search = await SearchSinceLastRunsAsync(roots).ConfigureAwait(false);

            var forums = new List<string>();
            foreach (var root in roots)
            {
                var covered = search != null && search.Covers(root.Url);
                await VisitForumAsync(root.Url, root.LatestPostTimestamp, forums, covered ? search : null, covered ? search!.StartedAt : started).ConfigureAwait(false);
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

        /// <summary>The replay posts a search found, and the forums (with their subforums) it covers from its start back to the last run.</summary>
        private sealed record ForumSearch(IReadOnlySet<long> CoveredNodes, DateTime StartedAt, IReadOnlyDictionary<long, List<SearchHit>> HitsByThread)
        {
            /// <summary>Posts up to this time (Unix seconds) are in the search results.</summary>
            public long IndexedUntilTimestamp => new DateTimeOffset(StartedAt - SearchIndexDelay, TimeSpan.Zero).ToUnixTimeSeconds();

            public bool Covers(string forumUrl) => SmogonPages.GetForumId(forumUrl) is long nodeId && CoveredNodes.Contains(nodeId);

            public IReadOnlyList<SearchHit> HitsOf(long threadId) => HitsByThread.TryGetValue(threadId, out var hits) ? hits : [];
        }

        /// <summary>
        /// One search for the replay posts of all <paramref name="roots"/> that were brought up to date before and changed since,
        /// from the oldest of their last runs on (a day earlier: Smogon reads the dates in its own time zone). Null if there are
        /// none, or no search.
        /// </summary>
        private async Task<ForumSearch?> SearchSinceLastRunsAsync(IReadOnlyList<ForumNode> roots)
        {
            if (!SearchAllowed)
            {
                return null;
            }
            var searchable = new List<(long NodeId, DateTime CoveredUntil)>();
            foreach (var root in roots)
            {
                var forum = _store.GetForum(root.Url);
                // A forum without posts since the last run is not walked, so it needs no search either.
                if (forum?.CoveredUntil is DateTime coveredUntil && !IsUnchanged(forum, root.LatestPostTimestamp)
                    && !_visitedForums.Contains(root.Url) && SmogonPages.GetForumId(root.Url) is long nodeId)
                {
                    searchable.Add((nodeId, coveredUntil));
                }
            }
            if (searchable.Count == 0)
            {
                return null;
            }

            var startedAt = DateTime.UtcNow;
            var nodeIds = searchable.Select(forum => forum.NodeId).Distinct().ToList();
            var lastRun = searchable.Min(forum => forum.CoveredUntil);
            // Result pages are read back to the last run (an hour of margin for clock differences); the date filter is the
            // day before, as Smogon reads it in its own time zone.
            var result = await _postSearch.SearchForumsAsync(nodeIds, lastRun.Date.AddDays(-1), lastRun - TimeSpan.FromHours(1)).ConfigureAwait(false);
            if (result == null)
            {
                _searchUnavailable = true;
                return null;
            }
            return new ForumSearch(
                nodeIds.Where(nodeId => !result.Uncovered.Contains(nodeId)).ToHashSet(),
                startedAt,
                result.Hits.GroupBy(hit => hit.ThreadId).ToDictionary(group => group.Key, group => group.ToList()));
        }

        /// <summary>
        /// Brings a forum and its subforums up to date and adds their URLs to <paramref name="forums"/>.
        /// Returns false if a thread in the subtree could not be read, so the subtree must not be skipped next run.
        /// </summary>
        /// <param name="search">The replay search of the run, if it covers the forum.</param>
        /// <param name="coveredUntil">Posts up to this time are covered once the subtree is done.</param>
        private async Task<bool> VisitForumAsync(string url, long nodeTimestamp, List<string> forums, ForumSearch? search, DateTime coveredUntil)
        {
            if (!_visitedForums.Add(url))
            {
                return true;
            }
            forums.Add(url);

            var forum = _store.GetForum(url) ?? new ForumCrawlState();
            var incremental = forum.Complete && !_options.FullRefresh;
            var recentSince = DateTimeOffset.UtcNow.Subtract(_options.RecentActivityWindow).ToUnixTimeSeconds();

            if (IsUnchanged(forum, nodeTimestamp))
            {
                foreach (var subForum in forum.SubForums)
                {
                    AddStoredSubtree(subForum, forums);
                }
                forum.CoveredUntil = coveredUntil;
                _store.SetForum(url, forum);
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
            var openThreadsLatestPost = 0L;
            foreach (var item in toUpdate)
            {
                var result = await UpdateThreadAsync(item, url, search, retry: forum.RetryThreads.ContainsKey(item.ThreadId)).ConfigureAwait(false);
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
                // The listing shows every thread with a post since recentSince, so this covers all threads of the forum that
                // may still be locked or renamed without a new post.
                if (!item.Locked && item.LatestPostTimestamp >= recentSince && _store.GetThread(item.ThreadId) is ThreadCrawlState thread
                    && thread.NeedsPagesAfterFirst(thread.Url))
                {
                    openThreadsLatestPost = Math.Max(openThreadsLatestPost, item.LatestPostTimestamp);
                }
            }
            forum.OpenThreadsLatestPost = openThreadsLatestPost;

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
                clean &= await VisitForumAsync(subForum.Url, subForum.LatestPostTimestamp, forums, search, coveredUntil).ConfigureAwait(false);
            }

            // Only now is the whole subtree up to date; until then the next run looks at it again.
            forum.SubForums = subForums.Select(subForum => subForum.Url).ToList();
            forum.NodeTimestamp = clean ? nodeTimestamp : 0;
            // Threads that failed are tried again next run without relying on the search.
            forum.CoveredUntil = coveredUntil;
            _store.SetForum(url, forum);
            return clean;
        }

        /// <summary>
        /// Neither the forum nor any of its subforums got a post since the last run (<paramref name="nodeTimestamp"/> is the latest
        /// post the parent forum shows), and none of their recently active threads with pairings is still open: those could be
        /// locked or renamed without a new post. Other threads yield no matches from a lock or new title.
        /// </summary>
        private bool IsUnchanged(ForumCrawlState forum, long nodeTimestamp)
        {
            if (!forum.Complete || _options.FullRefresh || nodeTimestamp <= 0 || nodeTimestamp != forum.NodeTimestamp)
            {
                return false;
            }
            var recentSince = DateTimeOffset.UtcNow.Subtract(_options.RecentActivityWindow).ToUnixTimeSeconds();
            return nodeTimestamp < recentSince || !HasOpenThreadsSince(forum, recentSince, []);
        }

        private bool HasOpenThreadsSince(ForumCrawlState forum, long since, HashSet<string> visited)
        {
            if (forum.OpenThreadsLatestPost is not long latestPost || latestPost >= since)
            {
                return true;
            }
            foreach (var subForumUrl in forum.SubForums)
            {
                if (visited.Add(subForumUrl) && (_store.GetForum(subForumUrl) is not ForumCrawlState subForum || HasOpenThreadsSince(subForum, since, visited)))
                {
                    return true;
                }
            }
            return false;
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

        private static long ToTimestamp(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        /// <summary>Brings the stored state of a listed thread up to date.</summary>
        /// <param name="search">The replay search of the run, if it covers the thread's forum.</param>
        /// <param name="retry">The thread failed in an earlier run, so the search may not reach back to its unread posts.</param>
        private async Task<ThreadUpdate> UpdateThreadAsync(ThreadListItem item, string forumUrl, ForumSearch? search, bool retry)
        {
            var state = _store.GetThread(item.ThreadId);
            var migrated = false;
            if (state == null)
            {
                state = MigrateLegacyState(item);
                migrated = state != null;
            }

            var titleOrLockChanged = state != null
                && ((state.Locked.HasValue && state.Locked.Value != item.Locked) || (state.Title != null && state.Title != item.Title));
            var changed = _options.FullRefresh
                || state == null
                || state.NumberOfPages == 0
                || state.LatestPostTimestamp != item.LatestPostTimestamp
                || titleOrLockChanged
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
            var newThread = _options.FullRefresh || state == null || state.NumberOfPages == 0;
            // The search found every replay post that was in its index. A thread that failed before may have older unread posts.
            var covered = search != null && !retry && !newThread && item.LatestPostTimestamp < search.IndexedUntilTimestamp;
            var knownUntil = state?.LatestPostTimestamp ?? 0;
            var newHits = (search?.HitsOf(item.ThreadId) ?? []).Where(hit => ToTimestamp(hit.PostDate) > knownUntil).ToList();

            if (covered && newHits.Count == 0 && !titleOrLockChanged && !state!.MissingPages.Any()
                && !SmogonThreadScanner.IsReplayThread(item.Url) && listedLastPage >= state.NumberOfPages)
            {
                // The new posts hold no replays, so they yield no matches: pairings are only taken from posts 1 and 2. Nothing
                // suggests that page 1 changed either. A host who adds pairings or bolds winners there is seen at the next replay
                // post, the lock or the new title. (A thread that got shorter is read: its replay posts moved.)
                for (var page = 2; page <= listedLastPage && state.NeedsPagesAfterFirst(item.Url); page++)
                {
                    if (!state.StoredPages.Contains(page) && !state.LegacyPages.Contains(page))
                    {
                        state.SkippedPages.Add(page);
                    }
                }
                state.LatestPostTimestamp = item.LatestPostTimestamp;
                state.NumberOfPages = listedLastPage;
                state.Url = item.Url;
                state.ForumUrl = forumUrl;
                state.Title ??= item.Title;
                state.Locked ??= item.Locked;
                _store.SetThread(item.ThreadId, state);
                return ThreadUpdate.Unchanged;
            }

            // Page 1 holds the tournament post, which hosts edit (winners in bold, replays) without bumping the thread,
            // and tells whether the thread has pairings at all.
            var (firstPage, failure) = await GetThreadPageAsync(item.Url).ConfigureAwait(false);
            if (failure != null)
            {
                return failure.Value;
            }
            var readPages = new Dictionary<int, string>();
            bool hasPairings;
            try
            {
                readPages[1] = SmogonThreadScanner.ExtractImportantLines(item.Url, 1, firstPage!, out hasPairings);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Skipping {item.Url} until the next run, page 1 could not be parsed: {e.Message}");
                return ThreadUpdate.Failed;
            }
            var numberOfPages = SmogonPages.GetNumberOfPages(firstPage!);

            state ??= new ThreadCrawlState();
            var neededPagesBefore = !newThread && state.NeedsPagesAfterFirst(state.Url);
            var oldNumberOfPages = state.NumberOfPages;
            state.Url = item.Url;
            state.HasPairings = hasPairings;

            var pagesToRead = new SortedSet<int>();
            var candidates = new SortedSet<int>();
            var expectedPosts = new Dictionary<int, List<long>>();
            var extendToEnd = true;
            var how = "";
            if (state.NeedsPagesAfterFirst(item.Url))
            {
                // Pages that may hold posts since the last update, and pages never read (all of them when pages were not needed before)
                var recent = new SortedSet<int>();
                if (!newThread)
                {
                    for (var page = Math.Max(2, Math.Min(oldNumberOfPages, listedLastPage)); page <= Math.Max(listedLastPage, numberOfPages); page++)
                    {
                        recent.Add(page);
                    }
                }
                var backfill = new SortedSet<int>(newThread
                    ? Enumerable.Range(2, Math.Max(0, numberOfPages - 1))
                    : state.MissingPages.Where(page => page > 1 && page <= numberOfPages && !recent.Contains(page)));
                candidates.UnionWith(recent);
                candidates.UnionWith(backfill);

                if (_options.FullRefresh || SmogonThreadScanner.IsReplayThread(item.Url))
                {
                    // In a replay thread, pairings without replay links count as well.
                    pagesToRead.UnionWith(candidates);
                }
                else if (covered && neededPagesBefore && backfill.Count == 0)
                {
                    // Posts the listing shows after the search are not covered; they wait for the next run.
                    extendToEnd = false;
                    numberOfPages = Math.Min(numberOfPages, listedLastPage);
                    candidates.RemoveWhere(page => page > numberOfPages);
                    AddHitPages(newHits, candidates, pagesToRead, expectedPosts);
                    how = ", found by search";
                }
                else if (candidates.Count > DirectReadLimit && SearchAllowed)
                {
                    var (outcome, threadHits) = await _postSearch.SearchThreadAsync(item.ThreadId, Math.Max(1, candidates.Count / 2)).ConfigureAwait(false);
                    if (outcome == PostSearch.ThreadSearchOutcome.Found)
                    {
                        AddHitPages(threadHits.Where(hit => backfill.Contains(hit.Page) || ToTimestamp(hit.PostDate) > knownUntil), candidates, pagesToRead, expectedPosts);
                        how = ", found by search";
                    }
                    else
                    {
                        _searchUnavailable |= outcome == PostSearch.ThreadSearchOutcome.Unavailable;
                        pagesToRead.UnionWith(candidates);
                    }
                }
                else
                {
                    pagesToRead.UnionWith(candidates);
                }
            }

            var queue = pagesToRead.ToList();
            Console.WriteLine($"Reading {item.Url} (page {FormatPages(queue.Prepend(1).ToList())} of {numberOfPages}{how})");

            var missingPosts = new List<long>();
            var plannedPages = numberOfPages;
            for (var i = 0; i < queue.Count; i++)
            {
                var page = queue[i];
                if (page > numberOfPages)
                {
                    // The thread got shorter: the replay posts expected there are elsewhere now.
                    missingPosts.AddRange(queue.Skip(i).SelectMany(rest => expectedPosts.GetValueOrDefault(rest) ?? []));
                    break;
                }

                var pageUrl = SmogonPages.PageUrl(item.Url, page);
                var (site, pageFailure) = await GetThreadPageAsync(pageUrl).ConfigureAwait(false);
                if (pageFailure != null)
                {
                    return pageFailure.Value;
                }

                var actualPages = SmogonPages.GetNumberOfPages(site!);
                if (page > actualPages)
                {
                    // Posts were deleted and the requested page no longer exists; Smogon served the last page instead.
                    numberOfPages = actualPages;
                    if (extendToEnd && !queue.Contains(actualPages))
                    {
                        // Read the real last page next; the loop then stops at the missing pages.
                        queue.Insert(i + 1, actualPages);
                    }
                    missingPosts.AddRange(expectedPosts.GetValueOrDefault(page) ?? []);
                    continue;
                }
                numberOfPages = extendToEnd ? actualPages : Math.Min(actualPages, numberOfPages);
                if (extendToEnd && i == queue.Count - 1 && actualPages > Math.Max(page, plannedPages))
                {
                    // More pages than page 1 showed: posts since then, which the search may not have seen
                    for (var extra = Math.Max(page, plannedPages) + 1; extra <= actualPages; extra++)
                    {
                        queue.Add(extra);
                    }
                }
                missingPosts.AddRange((expectedPosts.GetValueOrDefault(page) ?? []).Where(postId => !site!.Contains($"data-content=\"post-{postId}\"")));

                try
                {
                    readPages[page] = SmogonThreadScanner.ExtractImportantLines(item.Url, page, site!);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Skipping {item.Url} until the next run, page {page} could not be parsed: {e.Message}");
                    return ThreadUpdate.Failed;
                }
            }

            // Posts deleted between search and read move a replay post to an earlier page; its link leads to where it is now.
            foreach (var postId in missingPosts)
            {
                var (site, postFailure) = await GetThreadPageAsync(item.Url + "post-" + postId).ConfigureAwait(false);
                if (postFailure != null)
                {
                    // Deleted, or the odd page is counted towards stopping the crawl
                    continue;
                }
                var page = SmogonPages.GetCurrentPage(site!);
                numberOfPages = Math.Min(numberOfPages, SmogonPages.GetNumberOfPages(site!));
                if (!readPages.ContainsKey(page))
                {
                    Console.WriteLine($"Post {postId} is on page {page} now");
                    try
                    {
                        readPages[page] = SmogonThreadScanner.ExtractImportantLines(item.Url, page, site!);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Skipping {item.Url} until the next run, page {page} could not be parsed: {e.Message}");
                        return ThreadUpdate.Failed;
                    }
                }
            }

            foreach (var (page, importantLines) in readPages)
            {
                _store.SetPage(item.ThreadId, page, importantLines);
                state.StoredPages.Add(page);
                state.LegacyPages.Remove(page);
                state.SkippedPages.Remove(page);
            }
            // The search showed no replay posts on the other candidate pages.
            state.SkippedPages.UnionWith(candidates.Where(page => !state.StoredPages.Contains(page) && !state.LegacyPages.Contains(page)));
            foreach (var stalePage in state.StoredPages.Where(page => page > numberOfPages).ToList())
            {
                _store.RemovePage(item.ThreadId, stalePage);
                state.StoredPages.Remove(stalePage);
            }
            state.LegacyPages.RemoveWhere(page => page > numberOfPages);
            state.SkippedPages.RemoveWhere(page => page > numberOfPages);
            if (state.LegacyPages.Count == 0)
            {
                state.LegacyKey = null;
            }
            state.NumberOfPages = numberOfPages;
            state.LatestPostTimestamp = item.LatestPostTimestamp;
            state.Title = item.Title;
            state.Locked = item.Locked;
            state.ForumUrl = forumUrl;
            _store.SetThread(item.ThreadId, state);
            return ThreadUpdate.Updated;
        }

        /// <summary>Adds the candidate pages that hold one of the replay posts <paramref name="hits"/>, and the posts expected there.</summary>
        private static void AddHitPages(IEnumerable<SearchHit> hits, SortedSet<int> candidates, SortedSet<int> pagesToRead, Dictionary<int, List<long>> expectedPosts)
        {
            foreach (var hit in hits)
            {
                if (hit.PostId is not long postId || !candidates.Contains(hit.Page))
                {
                    continue;
                }
                pagesToRead.Add(hit.Page);
                if (!expectedPosts.TryGetValue(hit.Page, out var posts))
                {
                    expectedPosts[hit.Page] = posts = [];
                }
                posts.Add(postId);
            }
        }

        /// <summary>A thread page, or why the thread is skipped: gone (404, 403, 410), not readable or not a thread page.</summary>
        private async Task<(string? Site, ThreadUpdate? Failure)> GetThreadPageAsync(string pageUrl)
        {
            string site;
            try
            {
                site = await GetPageAsync(pageUrl).ConfigureAwait(false);
            }
            catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
            {
                Console.WriteLine($"Skipping {pageUrl}: HTTP {(int)e.StatusCode!}");
                return (null, ThreadUpdate.Gone);
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine($"Skipping {pageUrl} until the next run: {e.Message}");
                return (null, ThreadUpdate.Failed);
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
                return (null, ThreadUpdate.Failed);
            }
            return (site, null);
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
            if (state.LegacyPages.Contains(1))
            {
                state.HasPairings = SmogonThreadScanner.HasPairings(item.Url, legacy.CollectedLinks[0]);
            }

            if ((legacy.CollectedLinks.Count == item.LastPage || !state.NeedsPagesAfterFirst(item.Url))
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
