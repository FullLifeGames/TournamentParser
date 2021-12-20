using Newtonsoft.Json;

namespace TournamentParser.Data
{
    public class Thread
    {
        [JsonProperty("i")]
        public string Id { get; set; }
        [JsonProperty("n")]
        public string Name { get; set; }
        [JsonProperty("l")]
        public bool Locked { get; set; }

        public override string ToString() => Name;
    }
}
