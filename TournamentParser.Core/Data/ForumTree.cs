using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace TournamentParser.Data
{
    /// <summary>
    /// How the scanned forums nest and which forum every known thread sits in, taken from the crawl state
    /// (<see cref="ThreadCollector.SmogonThreadCollector.GetForumTree"/>). TournamentParser.Cmd writes it as forums.json.
    /// A forum's key is the last path segment of its URL: "tournaments.34", or "gen1" for a forum without node ID.
    /// </summary>
    public class ForumTree
    {
        /// <summary>Forum key to forum, ordered by key.</summary>
        [JsonProperty("forums")]
        public IDictionary<string, ForumTreeEntry> Forums { get; } = new SortedDictionary<string, ForumTreeEntry>(StringComparer.Ordinal);

        /// <summary>Thread ID to the key of the forum it sits in, ordered by thread ID.</summary>
        [JsonProperty("threads")]
        public IDictionary<long, string> Threads { get; } = new SortedDictionary<long, string>();
    }

    /// <summary>One forum of a <see cref="ForumTree"/>.</summary>
    public class ForumTreeEntry
    {
        /// <summary>Absolute forum URL with trailing slash.</summary>
        [JsonProperty("u")]
        public string Url { get; set; } = "";

        /// <summary>Key of the parent forum; null for the forums the crawl starts at.</summary>
        [JsonProperty("p")]
        public string? Parent { get; set; }

        /// <summary>The forum's title as Smogon shows it; null while the crawler has not seen the forum in a node list.</summary>
        [JsonProperty("n")]
        public string? Title { get; set; }
    }
}
