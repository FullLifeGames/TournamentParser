using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using TournamentParser.Core.Data;
using TournamentParser.Data;

namespace TournamentParser.ThreadScanner
{
    public interface IThreadScanner
    {
        public ConcurrentBag<User> Users { get; }
        public ConcurrentDictionary<string, User> NameUserTranslation { get; }
        public ConcurrentDictionary<int, User> IdUserTranslation { get; }
        public ConcurrentDictionary<string, string> UserWithSpaceTranslation { get; }

        Task ScanThreads(IDictionary<string, List<string>> threadsForForums);
        Task<TopicAnalyzeResult?> AnalyzeTopic(string url, System.Threading.CancellationToken ct);
    }
}
