using System.Collections.Generic;
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

        /// <summary>
        /// Enumerates the '\n'-separated segments of <paramref name="text"/> with the exact
        /// semantics of text.Split('\n'), but one line at a time, so whole forum pages are
        /// never materialized as arrays of line strings.
        /// </summary>
        public static IEnumerable<string> EnumerateLines(string text)
        {
            var start = 0;
            while (true)
            {
                var newline = text.IndexOf('\n', start);
                if (newline < 0)
                {
                    yield return text[start..];
                    yield break;
                }
                yield return text[start..newline];
                start = newline + 1;
            }
        }

        private const string InvalidFileNameChars = "\\/:*?\"<>|";

        private const int MaxSanitizedFileNameLength = 100;

        /// <summary>
        /// Makes a user name safe to use as a file name on any platform by replacing
        /// reserved and control characters with underscores and capping the length.
        /// Overlong names are truncated with a stable hash suffix so distinct names
        /// do not collide.
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
            if (sanitized.Length > MaxSanitizedFileNameLength)
            {
                var hash = System.Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
                return sanitized.ToString(0, MaxSanitizedFileNameLength - 9) + "-" + hash;
            }
            return sanitized.ToString();
        }
    }
}
