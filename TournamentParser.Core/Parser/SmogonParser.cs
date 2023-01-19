using Microsoft.Extensions.Caching.Distributed;
using TournamentParser.Finalizer;
using TournamentParser.ThreadCollector;
using TournamentParser.ThreadScanner;

namespace TournamentParser.Parser
{
    public class SmogonParser : Parser
    {
        public SmogonParser() : this(null) { }

        private readonly IDistributedCache? _cache;
        public SmogonParser(IDistributedCache? cache)
        {
            _cache = cache;
            _smogonThreadScanner = new SmogonThreadScanner(_cache);
        }

        public override IThreadCollector ThreadCollector => new SmogonThreadCollector();

        // Needs to be a private variable, otherwise the Concurrent structures inside do not hold their value
        private readonly SmogonThreadScanner _smogonThreadScanner;
        public override IThreadScanner ThreadScanner => _smogonThreadScanner;

        public override IFinalizer Finalizer => new SmogonFinalizer();
    }
}