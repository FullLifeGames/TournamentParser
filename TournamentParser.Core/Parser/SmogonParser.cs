using Microsoft.Extensions.Caching.Distributed;
using TournamentParser.Finalizer;
using TournamentParser.ThreadCollector;
using TournamentParser.ThreadScanner;

namespace TournamentParser.Parser
{
    public class SmogonParser : Parser
    {
        public SmogonParser() : this(null) { }

        /// <param name="cache">
        /// Keeps the crawl state between runs; after the first run only changes are requested. Without a cache,
        /// every run reads all forums and threads again.
        /// </param>
        public SmogonParser(IDistributedCache? cache) : this(cache, null) { }

        public SmogonParser(IDistributedCache? cache, CollectorOptions? collectorOptions)
        {
            var store = new CrawlStateStore(cache ?? new ProcessLocalCache());
            _smogonThreadCollector = new SmogonThreadCollector(store, collectorOptions);
            _smogonThreadScanner = new SmogonThreadScanner(store);
        }

        // One instance per parser: the collector remembers which forums it handled in this run.
        private readonly SmogonThreadCollector _smogonThreadCollector;
        public override IThreadCollector ThreadCollector => _smogonThreadCollector;

        // Needs to be a private variable, otherwise the Concurrent structures inside do not hold their value
        private readonly SmogonThreadScanner _smogonThreadScanner;
        public override IThreadScanner ThreadScanner => _smogonThreadScanner;

        public override IFinalizer Finalizer => new SmogonFinalizer();
    }
}
