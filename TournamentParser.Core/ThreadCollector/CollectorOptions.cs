using System;
using System.Net.Http;

namespace TournamentParser.ThreadCollector
{
    public class CollectorOptions
    {
        /// <summary>Ignore the stored state: walk every listing page and read every thread page again.</summary>
        public bool FullRefresh { get; set; }

        /// <summary>
        /// Forums with a post younger than this are listed in every run, and their listing is walked as far as threads
        /// with such posts go, so that locks and title changes ("won by ...") of recently active threads are noticed even
        /// without a new post. <see cref="TimeSpan.Zero"/> turns this off.
        /// </summary>
        public TimeSpan RecentActivityWindow { get; set; } = TimeSpan.FromDays(7);

        /// <summary>
        /// Upper limit of requests per run (retries not counted); 0 means no limit. When it is reached, the crawl stops with a
        /// <see cref="Util.RequestBudgetExhaustedException"/> and the next run continues from there.
        /// </summary>
        public int MaxRequests { get; set; }

        /// <summary>HttpClient for all requests; defaults to <see cref="Util.Common.HttpClient"/>.</summary>
        public HttpClient? HttpClient { get; set; }
    }
}
