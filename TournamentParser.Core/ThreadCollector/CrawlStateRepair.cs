using Microsoft.Extensions.Caching.Distributed;
using System;
using System.Collections.Generic;
using System.Linq;
using TournamentParser.Data;

namespace TournamentParser.ThreadCollector
{
    /// <summary>
    /// Repairs a crawl state whose forums lost their thread lists: the run of 2026-11-26 read no thread from any listing,
    /// walked every listing to its end and so dropped every thread. Sends no request.
    /// </summary>
    public static class CrawlStateRepair
    {
        /// <summary>All keys of the crawl state start with this.</summary>
        public const string KeyPrefix = CrawlStateStore.KeyPrefix;

        /// <summary>
        /// Adds every stored thread to the forum whose listing showed it last (<see cref="ThreadCrawlState.ForumUrl"/>). The next
        /// run lists every forum once (NodeTimestamp 0, OpenThreadsLatestPost unknown) and searches from
        /// <paramref name="coveredUntil"/> on, or from an earlier CoveredUntil. <paramref name="keys"/> are the keys of
        /// <paramref name="cache"/>; others than forums and threads are skipped. Returns a line per forum and a summary.
        /// </summary>
        public static IList<string> RebuildForumThreads(IDistributedCache cache, IEnumerable<string> keys, DateTime coveredUntil, bool dryRun)
        {
            var store = new CrawlStateStore(cache);
            var forumUrls = new List<string>();
            var threads = new Dictionary<long, ThreadCrawlState>();
            foreach (var key in keys)
            {
                if (key.StartsWith(CrawlStateStore.ForumKeyPrefix, StringComparison.Ordinal))
                {
                    forumUrls.Add(key[CrawlStateStore.ForumKeyPrefix.Length..]);
                }
                else if (key.StartsWith(CrawlStateStore.ThreadKeyPrefix, StringComparison.Ordinal)
                    && long.TryParse(key[CrawlStateStore.ThreadKeyPrefix.Length..], out var threadId) && store.GetThread(threadId) is ThreadCrawlState thread)
                {
                    threads[threadId] = thread;
                }
            }
            var byForum = threads.Where(thread => thread.Value.ForumUrl.Length > 0).ToLookup(thread => thread.Value.ForumUrl);

            var lines = new List<string>();
            int before = 0, after = 0, withPages = 0;
            foreach (var url in forumUrls.Order(StringComparer.Ordinal))
            {
                if (store.GetForum(url) is not ForumCrawlState forum)
                {
                    continue;
                }
                var count = forum.Threads.Count;
                foreach (var (threadId, thread) in byForum[url])
                {
                    forum.Threads.TryAdd(threadId, thread.Url);
                }
                forum.NodeTimestamp = 0;
                forum.OpenThreadsLatestPost = null;
                if (forum.CoveredUntil > coveredUntil)
                {
                    forum.CoveredUntil = coveredUntil;
                }
                if (!dryRun)
                {
                    store.SetForum(url, forum);
                }
                lines.Add($"{url}: {count} -> {forum.Threads.Count} thread(s)");
                before += count;
                after += forum.Threads.Count;
                withPages += forum.Threads.Keys.Count(threadId => threads.TryGetValue(threadId, out var thread) && thread.HasContent);
            }
            var stored = forumUrls.ToHashSet();
            var unplaced = threads.Count(thread => !stored.Contains(thread.Value.ForumUrl));
            lines.Add($"{lines.Count} forum(s): {before} -> {after} thread(s), {withPages} of them with stored pages; {unplaced} of {threads.Count} stored thread(s) "
                + $"name no stored forum. NodeTimestamp 0, OpenThreadsLatestPost unknown, CoveredUntil {coveredUntil:yyyy-MM-ddTHH:mm:ssZ} at the latest."
                + (dryRun ? " Dry run: nothing was written." : ""));
            return lines;
        }
    }
}
