using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using TournamentParser.Data;
using TournamentParser.Util;

namespace TournamentParser.ThreadCollector
{
    /// <summary>Reads the forum index ("forum_list") and forum listing pages ("forum_view") of XenForo.</summary>
    internal static partial class ForumPageParser
    {
        private const string ThreadItemStart = "<div class=\"structItem structItem--thread";

        [GeneratedRegex("<div class=\"node node--id\\d+ node--depth\\d+ node--(\\w+)")]
        private static partial Regex NodeStartRegex();

        [GeneratedRegex("<h3 class=\"node-title\">\\s*<a href=\"([^\"]+)\"[^>]*>([^<]*)</a>")]
        private static partial Regex NodeTitleRegex();

        [GeneratedRegex("class=\"node-extra-date[^\"]*\"[^>]*data-timestamp=\"(\\d+)\"")]
        private static partial Regex NodeLatestDateRegex();

        [GeneratedRegex("<a href=\"([^\"]+)\" class=\"subNodeLink subNodeLink--forum[^\"]*\">(.*?)</a>", RegexOptions.Singleline)]
        private static partial Regex SubNodeLinkRegex();

        [GeneratedRegex("<[^>]*>")]
        private static partial Regex TagRegex();

        // XenForo leaves out the slug (".../threads/123/") when a title has no URL-safe characters.
        [GeneratedRegex("data-preview-url=\"(/forums/threads/(?:[^\"/]*?\\.)?(\\d+)/)preview\"\\s*>(.*?)</a>", RegexOptions.Singleline)]
        private static partial Regex ThreadLinkRegex();

        [GeneratedRegex("<time\\s+class=\"structItem-latestDate[^\"]*\"([^>]*)>")]
        private static partial Regex LatestDateRegex();

        [GeneratedRegex("data-timestamp=\"(\\d+)\"")]
        private static partial Regex TimestampRegex();

        [GeneratedRegex("title=\"([^\"]*)\"")]
        private static partial Regex TitleAttributeRegex();

        [GeneratedRegex("<span class=\"structItem-pageJump\">(.*?)</span>", RegexOptions.Singleline)]
        private static partial Regex PageJumpRegex();

        [GeneratedRegex("/page-(\\d+)\"")]
        private static partial Regex PageLinkRegex();

        /// <summary>
        /// Forum nodes with their latest post: on the forum index every section, on a forum listing its direct subforums.
        /// Link forums, pages and categories are skipped.
        /// </summary>
        public static IList<ForumNode> ParseNodes(string html)
        {
            var nodes = new List<ForumNode>();
            var starts = NodeStartRegex().Matches(html);
            for (var i = 0; i < starts.Count; i++)
            {
                if (starts[i].Groups[1].Value != "forum")
                {
                    continue;
                }
                var end = i + 1 < starts.Count ? starts[i + 1].Index : html.Length;
                var block = html[starts[i].Index..end];
                var title = NodeTitleRegex().Match(block);
                if (!title.Success)
                {
                    continue;
                }
                var latest = NodeLatestDateRegex().Match(block);
                nodes.Add(new ForumNode
                {
                    Title = WebUtility.HtmlDecode(title.Groups[2].Value).Trim(),
                    Url = AbsoluteUrl(title.Groups[1].Value),
                    LatestPostTimestamp = latest.Success ? long.Parse(latest.Groups[1].Value) : 0,
                });
            }
            return nodes;
        }

        /// <summary>The "Sub-forums:" links below a node; they carry no latest post.</summary>
        public static IList<ForumNode> ParseSubNodeLinks(string html)
        {
            var nodes = new List<ForumNode>();
            foreach (Match link in SubNodeLinkRegex().Matches(html))
            {
                nodes.Add(new ForumNode
                {
                    Title = WebUtility.HtmlDecode(TagRegex().Replace(link.Groups[2].Value, "")).Trim(),
                    Url = AbsoluteUrl(link.Groups[1].Value),
                });
            }
            return nodes;
        }

        /// <summary>The threads of a forum listing page, in listing order.</summary>
        public static IList<ThreadListItem> ParseThreads(string html)
        {
            var items = new List<ThreadListItem>();
            var start = html.IndexOf(ThreadItemStart);
            while (start != -1)
            {
                var next = html.IndexOf(ThreadItemStart, start + ThreadItemStart.Length);
                var block = next == -1 ? html[start..] : html[start..next];
                var item = ParseThread(block);
                if (item != null)
                {
                    items.Add(item);
                }
                start = next;
            }
            return items;
        }

        private static ThreadListItem? ParseThread(string block)
        {
            // Moved-thread redirects have no preview URL or latest post: skip them.
            var link = ThreadLinkRegex().Match(block);
            var latest = LatestDateRegex().Match(block);
            if (!link.Success || !latest.Success)
            {
                return null;
            }
            var timestamp = TimestampRegex().Match(latest.Groups[1].Value);
            if (!timestamp.Success)
            {
                return null;
            }

            var lastPage = 1;
            var pageJump = PageJumpRegex().Match(block);
            if (pageJump.Success)
            {
                foreach (Match page in PageLinkRegex().Matches(pageJump.Groups[1].Value))
                {
                    if (int.TryParse(page.Groups[1].Value, out var number) && number > lastPage)
                    {
                        lastPage = number;
                    }
                }
            }

            var displayDate = TitleAttributeRegex().Match(latest.Groups[1].Value);

            return new ThreadListItem
            {
                ThreadId = long.Parse(link.Groups[2].Value),
                Url = Common.SmogonBaseUrl + link.Groups[1].Value,
                Title = WebUtility.HtmlDecode(link.Groups[3].Value).Trim(),
                Sticky = block.Contains("structItem-status--sticky"),
                Locked = block.Contains("structItem-status--locked"),
                LatestPostTimestamp = long.Parse(timestamp.Groups[1].Value),
                LatestPostDisplayDate = displayDate.Success ? WebUtility.HtmlDecode(displayDate.Groups[1].Value) : "",
                LastPage = lastPage,
            };
        }

        private static string AbsoluteUrl(string href) =>
            href.StartsWith("http") ? href : Common.SmogonBaseUrl + href;
    }
}
