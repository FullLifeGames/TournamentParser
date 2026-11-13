using Microsoft.Extensions.Caching.Distributed;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TournamentParser.Core.Data;
using TournamentParser.Data;
using TournamentParser.Util;

namespace TournamentParser.ThreadCollector
{
    /// <summary>
    /// Keeps the incremental crawl state in an <see cref="IDistributedCache"/>: one entry per forum, per thread and per thread page.
    /// Entries of versions before 3.0 (thread URL to <see cref="TopicAnalyzeResult"/>) are read for the migration but never changed.
    /// </summary>
    internal class CrawlStateStore
    {
        private const string KeyPrefix = "tp:v3:";

        private readonly IDistributedCache _cache;

        public CrawlStateStore(IDistributedCache cache)
        {
            _cache = cache;
        }

        public ForumCrawlState? GetForum(string forumUrl) => Get<ForumCrawlState>(KeyPrefix + "forum:" + forumUrl);

        public void SetForum(string forumUrl, ForumCrawlState state) => Set(KeyPrefix + "forum:" + forumUrl, state);

        public ThreadCrawlState? GetThread(long threadId) => Get<ThreadCrawlState>(KeyPrefix + "thread:" + threadId);

        public void SetThread(long threadId, ThreadCrawlState state) => Set(KeyPrefix + "thread:" + threadId, state);

        /// <summary>The lines of a thread page that the scanner needs (see <c>SmogonThreadScanner.ExtractImportantLines</c>).</summary>
        public string? GetPage(long threadId, int page) => GetString(PageKey(threadId, page));

        public void SetPage(long threadId, int page, string importantLines) => _cache.SetString(PageKey(threadId, page), importantLines);

        public void RemovePage(long threadId, int page) => _cache.Remove(PageKey(threadId, page));

        /// <summary>Entry written by versions before 3.0, keyed by the thread URL.</summary>
        public TopicAnalyzeResult? GetLegacyThread(string threadUrl) => Get<TopicAnalyzeResult>(threadUrl);

        /// <summary>
        /// The stored pages of a thread in page order, ready for <c>SmogonThreadScanner.AnalyzeTopic(url, pages, ct)</c>.
        /// A page that is not available yet is an empty string, so later pages keep their post numbers.
        /// Null if the crawl state holds no content for the thread.
        /// </summary>
        public IList<string>? LoadPages(string threadUrl)
        {
            if (!SmogonPages.TryGetThreadId(threadUrl, out var threadId))
            {
                return null;
            }
            var state = GetThread(threadId);
            if (state == null || !state.HasContent)
            {
                return null;
            }

            TopicAnalyzeResult? legacy = null;
            if (state.LegacyKey != null && state.LegacyPages.Count > 0)
            {
                legacy = GetLegacyThread(state.LegacyKey);
            }

            var pages = new List<string>(state.NumberOfPages);
            for (var page = 1; page <= state.NumberOfPages; page++)
            {
                string? content = null;
                if (state.StoredPages.Contains(page))
                {
                    content = GetPage(threadId, page);
                }
                else if (state.LegacyPages.Contains(page) && legacy?.CollectedLinks != null && page <= legacy.CollectedLinks.Count)
                {
                    content = legacy.CollectedLinks[page - 1];
                }
                pages.Add(content ?? "");
            }
            return pages;
        }

        private static string PageKey(long threadId, int page) => KeyPrefix + "page:" + threadId + ":" + page;

        private string? GetString(string key)
        {
            try
            {
                return _cache.GetString(key);
            }
            catch (NullReferenceException)
            {
                // NeoSmart.Caching.Sqlite throws on some missing entries
                return null;
            }
        }

        private T? Get<T>(string key) where T : class
        {
            var json = GetString(key);
            if (json == null)
            {
                return null;
            }
            try
            {
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private void Set<T>(string key, T value) => _cache.SetString(key, JsonConvert.SerializeObject(value));
    }

    /// <summary>Process-local cache used when no persistent cache is passed in; nothing survives the run.</summary>
    internal class ProcessLocalCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _entries = new();

        public byte[]? Get(string key) => _entries.TryGetValue(key, out var value) ? value : null;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

        public void Refresh(string key) { }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => _entries.TryRemove(key, out _);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _entries[key] = value;

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }
}
