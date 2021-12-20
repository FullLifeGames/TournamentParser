using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TournamentParser.Data;
using TournamentParser.Finalizer;
using TournamentParser.ThreadCollector;
using TournamentParser.ThreadScanner;

namespace TournamentParser.Tournament
{
    public abstract class Tournament
    {
        public abstract IThreadCollector ThreadCollector
        {
            get;
        }
        public abstract IThreadScanner ThreadScanner
        {
            get;
        }
        public abstract IFinalizer Finalizer
        {
            get;
        }

        public async Task<IDictionary<string, User>> GetMatchesForUsers()
        {
            var threadsForForums = await ThreadCollector.GetThreadsForForums().ConfigureAwait(false);
            var nonTourThreadsForForums = await ThreadCollector.GetNonTourThreadsForForums().ConfigureAwait(false);

            var totalCount =
                threadsForForums.Sum((thread) => thread.Value.Count)
                + nonTourThreadsForForums.Sum((thread) => thread.Value.Count);

            await ThreadScanner.ScanThreads(threadsForForums).ConfigureAwait(false);
            await ThreadScanner.ScanThreads(nonTourThreadsForForums).ConfigureAwait(false);
            Finalizer.Finalize(ThreadScanner.NameUserTranslation);

            return ThreadScanner.NameUserTranslation;
        }
    }
}
