using System.Net.Http;
using System.Threading.Tasks;
using TournamentParser.Core.Util;

namespace TournamentParser.Util
{
    public static class Common
    {
        private static HttpClient? _httpClient;
        public static HttpClient HttpClient
        {
            get
            {
                return _httpClient ??= new HttpClient(new HttpRetryMessageHandler(new HttpClientHandler()));
            }
            set => _httpClient = value;
        }

        public static ParallelOptions ParallelOptions { get; set; } = new();

        public const string OfficialTournamentSite = "http://www.smogon.com/forums/forums/tournaments.34/";
        public const char Quotation = '"';
    }
}
