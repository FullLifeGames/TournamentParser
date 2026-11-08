using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TournamentParser.Tests.TestSupport
{
    /// <summary>
    /// Builds minimal XenForo-shaped thread pages containing exactly the line
    /// markers SmogonThreadScanner keys on.
    /// </summary>
    public static class SyntheticPages
    {
        public class Post
        {
            public string Author { get; set; } = "";
            public int UserId { get; set; }
            /// <summary>e.g. "Mar 22, 2021 at 12:15 AM" (parsed as "MMM d, yyyy h:mm tt" after "at " removal)</summary>
            public string DateTitle { get; set; } = "Mar 22, 2021 at 12:15 AM";
            public List<string> BodyLines { get; set; } = new();
        }

        public static string ThreadPage(string title, IEnumerable<Post> posts, int totalPages = 1, bool locked = false)
        {
            var sb = new StringBuilder();
            sb.Append("<html>\n");
            sb.Append($"<h1 class=\"p-title-value\">{title}</h1>\n");
            if (locked)
            {
                sb.Append("\t\t\t\t\t\t<dd class=\"blockStatus-message blockStatus-message--locked\">\n");
                sb.Append("\t\t\t\t\t\t\tNot open for further replies.\n");
                sb.Append("\t\t\t\t\t\t</dd>\n");
            }
            if (totalPages > 1)
            {
                sb.Append("<nav class=\"pageNavWrapper pageNavWrapper--mixed\">\n");
                sb.Append(string.Join("", Enumerable.Range(1, totalPages)
                    .Select(p => $"<li class=\"pageNav-page\"><a href=\"/threads/x/page-{p}\">{p}</a></li>")));
                sb.Append('\n');
                sb.Append("</nav>\n");
            }
            foreach (var post in posts)
            {
                var lowerName = post.Author.ToLower().Replace(" ", "-");
                sb.Append("\t<article class=\"message message--post js-post js-inlineModContainer  \"\n");
                sb.Append($"\t\tdata-author=\"{post.Author}\"\n");
                sb.Append("\t<header class=\"message-attribution message-attribution--split\">\n");
                // Mirrors current XenForo output: no data-date-string attribute, full date in title.
                sb.Append($"<time class=\"u-dt\" dir=\"auto\" datetime=\"2021-03-22T00:15:03-0400\" data-timestamp=\"1616386503\" data-date=\"Mar 22, 2021\" data-time=\"12:15 AM\" title=\"{post.DateTitle}\">\n");
                sb.Append($"\t\t\t<h4 class=\"message-name\"><a href=\"/forums/members/{lowerName}.{post.UserId}/\" class=\"username \" dir=\"auto\" data-user-id=\"{post.UserId}\" data-xf-init=\"member-tooltip\">{post.Author}</a></h4>\n");
                sb.Append("\t\t<article class=\"message-body js-selectToQuote\">\n");
                foreach (var bodyLine in post.BodyLines)
                {
                    sb.Append(bodyLine).Append('\n');
                }
                sb.Append("\t\t</article>\n");
                sb.Append("\t</article>\n");
            }
            sb.Append("</html>\n");
            return sb.ToString();
        }
    }
}
