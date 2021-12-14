using Newtonsoft.Json;
using System.Collections.Concurrent;

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
        public ConcurrentBag<Match> Matches { get; set; } = new ConcurrentBag<Match>();

        public override string ToString()
        {
            var output = $"The user '{Name ?? ""}' with the id {Id} has the following matches:\r\n";

            foreach(var match in Matches)
            {
                output += match + "\r\n";
            }

            return output;
        }
        
    }
}
