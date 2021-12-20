using TournamentParser.Finalizer;
using TournamentParser.ThreadCollector;
using TournamentParser.ThreadScanner;

namespace TournamentParser.Tournament
{
    public class SmogonTournament : Tournament
    {
        public override IThreadCollector ThreadCollector => new SmogonThreadCollector();

        private readonly SmogonThreadScanner threadScanner = new();
        public override IThreadScanner ThreadScanner => threadScanner;

        public override IFinalizer Finalizer => new SmogonFinalizer();
    }
}