using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Text;

namespace TournamentParser.Data
{
    public class User
    {
        [JsonProperty("i")]
        public int Id { get; set; } = -1;
        [JsonProperty("n")]
        public string? Name { get; set; }
        [JsonProperty("l")]
        public string? ProfileLink { get; set; }
        [JsonProperty("o")]
        public string? NormalName { get; set; }
        [JsonProperty("m")]
        public ConcurrentBag<TournamentMatch> Matches { get; set; } = new ConcurrentBag<TournamentMatch>();

        public override string ToString()
        {
            var output = new StringBuilder($"The user '{Name ?? ""}' with the id {Id} has the following matches:\r\n");

            foreach(var match in Matches)
            {
                output.Append(match).Append("\r\n");
            }

            return output.ToString();
        }
    }
}
