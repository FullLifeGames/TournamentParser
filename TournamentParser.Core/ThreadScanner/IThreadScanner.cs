using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using TournamentParser.Data;

namespace TournamentParser.ThreadScanner
{
    public interface IThreadScanner
    {
        public ConcurrentBag<User> Users { get; }
        public IDictionary<string, User> NameUserTranslation { get; }
        public IDictionary<int, User> IdUserTranslation { get; }
        public IDictionary<string, string> UserWithSpaceTranslation { get; }

        Task ScanThreads(IDictionary<string, List<string>> threadsForForums);
        Task AnalyzeTopic(string url, System.Threading.CancellationToken ct);
    }
}
