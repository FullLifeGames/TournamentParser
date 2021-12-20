using System.Net.Http;

namespace TournamentParser.Util
{
    public static class Common
    {
        private static HttpClient _httpClient;
        public static HttpClient HttpClient
        {
            get
            {
                return _httpClient ??= new HttpClient();
            }
            set => _httpClient = value;
        }

        public const string OfficialTournamentSite = "http://www.smogon.com/forums/forums/tournaments.34/";
        public const char Quotation = '"';
    }
}
