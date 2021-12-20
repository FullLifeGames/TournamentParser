using System.Collections.Generic;
using System.Threading.Tasks;

namespace TournamentParser.ThreadCollector
{
    public interface IThreadCollector
    {
        Task<IDictionary<string, List<string>>> GetGeneralThreadsForForums(
            string filter, IDictionary<string, string> additionals = null);
        Task<IDictionary<string, List<string>>> GetThreadsForForums();
        Task<IDictionary<string, List<string>>> GetNonTourThreadsForForums();
    }
}
