using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace TournamentParser.Tests.TestSupport
{
    /// <summary>
    /// A small Smogon forum in XenForo markup: forum index, forum listings (sorted by latest post, with their subforums)
    /// and thread pages. Change the model between runs and call <see cref="Serve"/> again to publish the new state.
    /// </summary>
    public class FakeForum
    {
        public const string BaseUrl = "https://www.smogon.com";
        public const string IndexUrl = BaseUrl + "/forums/";

        public class Thread
        {
            public long Id { get; set; }
            public string Slug { get; set; } = "thread";
            public string Title { get; set; } = "Thread";
            public bool Locked { get; set; }
            public bool Sticky { get; set; }
            /// <summary>Posts per page; page 1 starts with the tournament post.</summary>
            public List<List<SyntheticPages.Post>> Pages { get; set; } = new();

            public string Path => $"/forums/threads/{Slug}.{Id}/";
            public string Url => BaseUrl + Path;

            public SyntheticPages.Post LatestPost => Pages.Last(page => page.Count > 0).Last();

            /// <summary>The post date titles are read as UTC.</summary>
            public long LatestPostTimestamp => ToTimestamp(LatestPost.DateTitle);

            public string PageUrl(int page) => page <= 1 ? Url : Url + "page-" + page;
        }

        public class Forum
        {
            public string Title { get; set; } = "Forum";
            public string Path { get; set; } = "/forums/forums/forum.1/";
            public List<Thread> Threads { get; } = new();
            public List<Forum> SubForums { get; } = new();

            public string Url => BaseUrl + Path;

            public long LatestPostTimestamp => Threads.Select(thread => thread.LatestPostTimestamp)
                .Concat(SubForums.Select(subForum => subForum.LatestPostTimestamp))
                .DefaultIfEmpty(0)
                .Max();

            public IEnumerable<Forum> Descendants => SubForums.SelectMany(subForum => subForum.Descendants.Prepend(subForum));

            public string PageUrl(int page) => page <= 1 ? Url : Url + "page-" + page;
        }

        /// <summary>Sections shown as nodes on the forum index.</summary>
        public List<Forum> Sections { get; } = new();

        public int ThreadsPerListingPage { get; set; } = 3;

        public IEnumerable<Forum> AllForums => Sections.SelectMany(section => section.Descendants.Prepend(section));

        public static long ToTimestamp(string dateTitle) =>
            new DateTimeOffset(DateTime.ParseExact(dateTitle.Replace("at ", ""), "MMM d, yyyy h:mm tt", CultureInfo.GetCultureInfo("en-US")), TimeSpan.Zero)
                .ToUnixTimeSeconds();

        public static string ToDateTitle(DateTime utc) => utc.ToString("MMM d, yyyy 'at' h:mm tt", CultureInfo.GetCultureInfo("en-US"));

        /// <summary>Maps every page of the forum in <paramref name="http"/>.</summary>
        public void Serve(FakeHttpMessageHandler http)
        {
            http.Map(IndexUrl, IndexPage());
            foreach (var forum in AllForums)
            {
                var pageCount = ListingPageCount(forum);
                for (var page = 1; page <= pageCount; page++)
                {
                    http.Map(forum.PageUrl(page), ListingPage(forum, page));
                }
                foreach (var thread in forum.Threads)
                {
                    for (var page = 1; page <= thread.Pages.Count; page++)
                    {
                        http.Map(thread.PageUrl(page), ThreadPage(thread, page));
                    }
                }
            }
        }

        public static string ThreadPage(Thread thread, int page) =>
            SyntheticPages.ThreadPage(WebUtility.HtmlEncode(thread.Title), thread.Pages[page - 1], thread.Pages.Count, thread.Locked);

        public string IndexPage()
        {
            var sb = new StringBuilder();
            sb.Append("<html data-template=\"forum_list\">\n<body data-template=\"forum_list\">\n");
            sb.Append("<div class=\"block block--category block--category1\">\n");
            foreach (var section in Sections)
            {
                AppendNode(sb, section, section.Descendants);
            }
            sb.Append("</div>\n</body>\n</html>\n");
            return sb.ToString();
        }

        private int ListingPageCount(Forum forum) => Math.Max(1, (forum.Threads.Count(thread => !thread.Sticky) + ThreadsPerListingPage - 1) / ThreadsPerListingPage);

        public string ListingPage(Forum forum, int page)
        {
            var pageCount = ListingPageCount(forum);
            var sb = new StringBuilder();
            sb.Append("<html data-template=\"forum_view\">\n<body data-template=\"forum_view\">\n");
            if (pageCount > 1)
            {
                AppendPageNav(sb, forum.Path, pageCount);
            }
            if (page == 1)
            {
                sb.Append("<div class=\"block block--nodes\">\n");
                foreach (var subForum in forum.SubForums)
                {
                    AppendNode(sb, subForum, subForum.SubForums);
                }
                sb.Append("</div>\n");
            }

            sb.Append("<div class=\"structItemContainer\">\n");
            var threads = new List<Thread>();
            if (page == 1)
            {
                threads.AddRange(forum.Threads.Where(thread => thread.Sticky).OrderByDescending(thread => thread.LatestPostTimestamp));
            }
            threads.AddRange(forum.Threads.Where(thread => !thread.Sticky)
                .OrderByDescending(thread => thread.LatestPostTimestamp)
                .Skip((page - 1) * ThreadsPerListingPage)
                .Take(ThreadsPerListingPage));
            foreach (var thread in threads)
            {
                AppendThreadItem(sb, thread);
            }
            sb.Append("</div>\n</body>\n</html>\n");
            return sb.ToString();
        }

        private static void AppendNode(StringBuilder sb, Forum forum, IEnumerable<Forum> subNodeLinks)
        {
            sb.Append($"\t<div class=\"node node--id{Math.Abs(forum.Path.GetHashCode()) % 1000} node--depth2 node--forum node--unread\">\n");
            sb.Append("\t\t<div class=\"node-body\">\n");
            sb.Append("\t\t\t\t<h3 class=\"node-title\">\n");
            sb.Append($"\t\t\t\t\t<a href=\"{forum.Path}\" data-xf-init=\"element-tooltip\" data-shortcut=\"node-description\">{WebUtility.HtmlEncode(forum.Title)}</a>\n");
            sb.Append("\t\t\t\t</h3>\n");
            sb.Append("\t\t<div class=\"node-subNodesFlat\">\n\t\t\t<ol class=\"node-subNodeFlatList\">\n");
            foreach (var subForum in subNodeLinks)
            {
                sb.Append($"\t<li>\n\t\t<a href=\"{subForum.Path}\" class=\"subNodeLink subNodeLink--forum \">\n");
                sb.Append($"\t\t\t<i class=\"fa--xf far fa-comments  subNodeLink-icon\"><svg><use href=\"/icons.svg#comments\"></use></svg></i>{WebUtility.HtmlEncode(subForum.Title)}\n");
                sb.Append("\t\t</a>\n\t</li>\n");
            }
            sb.Append("\t\t\t</ol>\n\t\t</div>\n");
            sb.Append("\t\t\t<div class=\"node-extra\">\n");
            var latest = forum.LatestPostTimestamp;
            if (latest > 0)
            {
                var date = DateTimeOffset.FromUnixTimeSeconds(latest).UtcDateTime;
                sb.Append($"\t\t\t\t<li><time  class=\"node-extra-date u-dt\" dir=\"auto\" datetime=\"{date:yyyy-MM-ddTHH:mm:ssZ}\" data-timestamp=\"{latest}\" title=\"{ToDateTitle(date)}\">x</time></li>\n");
            }
            else
            {
                sb.Append("\t\t\t\t<span class=\"node-extra-placeholder\">None</span>\n");
            }
            sb.Append("\t\t\t</div>\n\t\t</div>\n\t</div>\n");
        }

        private static void AppendThreadItem(StringBuilder sb, Thread thread)
        {
            sb.Append($"\t<div class=\"structItem structItem--thread js-inlineModContainer js-threadListItem-{thread.Id}\" data-author=\"Host\">\n");
            sb.Append("\t\t<div class=\"structItem-cell structItem-cell--main\" data-xf-init=\"touch-proxy\">\n");
            sb.Append("\t\t\t<ul class=\"structItem-statuses\">\n");
            if (thread.Locked)
            {
                sb.Append("\t\t\t\t<li><i class=\"structItem-status structItem-status--locked\" aria-hidden=\"true\" title=\"Locked\"></i></li>\n");
            }
            if (thread.Sticky)
            {
                sb.Append("\t\t\t\t<li><i class=\"structItem-status structItem-status--sticky\" aria-hidden=\"true\" title=\"Sticky\"></i></li>\n");
            }
            sb.Append("\t\t\t</ul>\n");
            sb.Append("\t\t\t<div class=\"structItem-title\">\n");
            sb.Append($"\t\t\t\t<a href=\"{thread.Path}\" class=\"\" data-tp-primary=\"on\" data-xf-init=\"preview-tooltip\" data-preview-url=\"{thread.Path}preview\"\n\t\t\t\t\t>\n\t\t\t\t\t{WebUtility.HtmlEncode(thread.Title)}\n\t\t\t\t</a>\n");
            sb.Append("\t\t\t</div>\n");
            if (thread.Pages.Count > 1)
            {
                sb.Append("\t\t\t<span class=\"structItem-pageJump\">\n");
                for (var page = Math.Max(2, thread.Pages.Count - 2); page <= thread.Pages.Count; page++)
                {
                    sb.Append($"\t\t\t\t<a href=\"{thread.Path}page-{page}\">{page}</a>\n");
                }
                sb.Append("\t\t\t</span>\n");
            }
            sb.Append("\t\t</div>\n");
            sb.Append("\t\t<div class=\"structItem-cell structItem-cell--latest\">\n");
            var latest = thread.LatestPostTimestamp;
            var date = DateTimeOffset.FromUnixTimeSeconds(latest).UtcDateTime;
            sb.Append($"\t\t\t<a href=\"{thread.Path}latest\" rel=\"nofollow\"><time  class=\"structItem-latestDate u-dt\" dir=\"auto\" datetime=\"{date:yyyy-MM-ddTHH:mm:ssZ}\" data-timestamp=\"{latest}\" data-date=\"x\" data-time=\"x\" title=\"{thread.LatestPost.DateTitle}\">x</time></a>\n");
            sb.Append("\t\t</div>\n\t</div>\n");
        }

        private static void AppendPageNav(StringBuilder sb, string path, int pageCount)
        {
            sb.Append("<nav class=\"pageNavWrapper pageNavWrapper--mixed\">\n<ul class=\"pageNav-main\">\n");
            for (var page = 1; page <= pageCount; page++)
            {
                sb.Append($"<li class=\"pageNav-page \"><a href=\"{path}page-{page}\">{page}</a></li>\n");
            }
            sb.Append("</ul>\n</nav>\n");
        }
    }
}
