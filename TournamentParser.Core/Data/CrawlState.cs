using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TournamentParser.Data
{
    /// <summary>A forum as shown in a node list (forum index, or the subforums at the top of a forum listing).</summary>
    public class ForumNode
    {
        public string Title { get; set; } = "";
        /// <summary>Absolute forum URL with trailing slash, e.g. https://www.smogon.com/forums/forums/tournaments.34/</summary>
        public string Url { get; set; } = "";
        /// <summary>Unix timestamp (seconds) of the latest post in the forum and its subforums; 0 if the node list shows none.</summary>
        public long LatestPostTimestamp { get; set; }
    }

    /// <summary>One thread as listed on a forum listing page.</summary>
    public class ThreadListItem
    {
        public long ThreadId { get; set; }
        /// <summary>Absolute thread URL with trailing slash, e.g. https://www.smogon.com/forums/threads/some-title.123/</summary>
        public string Url { get; set; } = "";
        /// <summary>HTML-decoded thread title.</summary>
        public string Title { get; set; } = "";
        public bool Sticky { get; set; }
        public bool Locked { get; set; }
        /// <summary>Unix timestamp (seconds) of the latest post in the thread.</summary>
        public long LatestPostTimestamp { get; set; }
        /// <summary>The latest post date as Smogon displays it, e.g. "Nov 8, 2026 at 11:12 AM".</summary>
        public string LatestPostDisplayDate { get; set; } = "";
        public int LastPage { get; set; } = 1;
    }

    /// <summary>What the crawler remembers about a forum between runs.</summary>
    public class ForumCrawlState
    {
        /// <summary>Newest latest-post timestamp seen in the forum's own thread listing during the last run.</summary>
        public long Watermark { get; set; }

        /// <summary>
        /// Latest-post timestamp that the parent node list showed when this forum and all its subforums were last brought
        /// up to date. While the parent still shows it, nothing in the subtree changed. 0 means unknown.
        /// </summary>
        public long NodeTimestamp { get; set; }

        /// <summary>True once the whole listing was walked; afterwards only the changed part is read.</summary>
        public bool Complete { get; set; }

        public DateTime LastCrawl { get; set; }

        /// <summary>
        /// Posts of this forum and its subforums up to this time (UTC) are covered: read, or known not to matter. Set once the
        /// whole subtree is up to date; the next run's replay search starts a day before. Null until then.
        /// </summary>
        public DateTime? CoveredUntil { get; set; }

        /// <summary>Thread id to thread URL of every thread listed in this forum.</summary>
        public Dictionary<long, string> Threads { get; set; } = [];

        /// <summary>Threads that could not be read; they are tried again in the next run even if the listing walk stops before them.</summary>
        public Dictionary<long, ThreadListItem> RetryThreads { get; set; } = [];

        /// <summary>URLs of the direct subforums, as listed on the forum's first page.</summary>
        public List<string> SubForums { get; set; } = [];
    }

    /// <summary>What the crawler remembers about a thread between runs. The page contents are stored separately.</summary>
    public class ThreadCrawlState
    {
        public string Url { get; set; } = "";

        /// <summary>The forum whose listing showed this thread last; used to drop threads that were moved.</summary>
        public string ForumUrl { get; set; } = "";

        /// <summary>Unix timestamp (seconds) of the latest post the stored pages cover; 0 means unknown.</summary>
        public long LatestPostTimestamp { get; set; }

        /// <summary>HTML-decoded title when the thread was last read; null means unknown.</summary>
        public string? Title { get; set; }

        /// <summary>Whether the thread was locked when it was last read; null means unknown.</summary>
        public bool? Locked { get; set; }

        public int NumberOfPages { get; set; }

        /// <summary>Pages stored in their own cache entry by version 3.</summary>
        public SortedSet<int> StoredPages { get; set; } = [];

        /// <summary>
        /// Cache key of the version 2.x entry (the thread URL) that still provides the pages in <see cref="LegacyPages"/>.
        /// The 2.x entry is only read, never changed.
        /// </summary>
        public string? LegacyKey { get; set; }

        /// <summary>Pages taken from the 2.x entry because they hold real thread content there.</summary>
        public SortedSet<int> LegacyPages { get; set; } = [];

        /// <summary>
        /// Whether posts 1 and 2 held pairings when page 1 was last read; null means unknown. Without them (and outside replay
        /// threads), no page after the first can yield a match, so only page 1 is read.
        /// </summary>
        public bool? HasPairings { get; set; }

        /// <summary>Pages that Smogon's search showed without replay posts, so they were not read.</summary>
        public SortedSet<int> SkippedPages { get; set; } = [];

        /// <summary>Whether pages after the first can yield matches; true while unknown.</summary>
        public bool NeedsPagesAfterFirst(string url) => HasPairings != false || ThreadScanner.SmogonThreadScanner.IsReplayThread(url);

        /// <summary>
        /// Pages that are needed but neither stored, nor usable from the 2.x entry, nor known to hold no replay posts;
        /// the next update reads them.
        /// </summary>
        [JsonIgnore]
        public IEnumerable<int> MissingPages => Enumerable.Range(1, NeedsPagesAfterFirst(Url) ? NumberOfPages : Math.Min(1, NumberOfPages))
            .Where(page => !StoredPages.Contains(page) && !LegacyPages.Contains(page) && !SkippedPages.Contains(page));

        [JsonIgnore]
        public bool HasContent => NumberOfPages > 0 && (StoredPages.Count > 0 || LegacyPages.Count > 0);
    }
}
