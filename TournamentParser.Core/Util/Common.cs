using System.Net.Http;

namespace TournamentParser.Util
{
    public class Common
    {
        private static HttpClient? _httpClient;
        public static HttpClient HttpClient
        {
            get
            {
                if (_httpClient == null)
                {
                    _httpClient = new HttpClient();
                }
                return _httpClient;
            }
            set => _httpClient = value;
        }

        public const string OfficialTournamentSite = "http://www.smogon.com/forums/forums/tournaments.34/";
        public const char Quotation = '"';
    }
}
