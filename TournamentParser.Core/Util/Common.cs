using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using TournamentParser.Core.Util;

namespace TournamentParser.Util
{
    public static class Common
    {
        public static HttpClientHandler CreateHttpClientHandler() => new()
        {
            // Forum HTML compresses roughly 5-10x; without this every page is fetched uncompressed.
            AutomaticDecompression = DecompressionMethods.All,
        };

        private static HttpClient? _httpClient;
        public static HttpClient HttpClient
        {
            get
            {
                return _httpClient ??= new HttpClient(new HttpRetryMessageHandler(CreateHttpClientHandler()));
            }
            set => _httpClient = value;
        }

        public static ParallelOptions ParallelOptions { get; set; } = new();

        public const string SmogonBaseUrl = "https://www.smogon.com";
        public const string OfficialTournamentSite = SmogonBaseUrl + "/forums/forums/tournaments.34/";
        public const char Quotation = '"';

        private const string InvalidFileNameChars = "\\/:*?\"<>|";

        /// <summary>
        /// Makes a user name safe to use as a file name on any platform by replacing
        /// reserved and control characters with underscores.
        /// </summary>
        public static string SanitizeFileName(string name)
        {
            if (name.Length == 0)
            {
                return "_";
            }
            var sanitized = new StringBuilder(name.Length);
            foreach (var character in name)
            {
                sanitized.Append(character < ' ' || InvalidFileNameChars.Contains(character) ? '_' : character);
            }
            return sanitized.ToString();
        }
    }
}
