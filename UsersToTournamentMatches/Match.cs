using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace UsersToTournamentMatches
{
    public class Match
    {

        [JsonProperty("r")]
        public ICollection<string> Replays { get; set; } = new List<string>();
        [JsonProperty("f")]
        public string? FirstUser { get; set; }
        [JsonProperty("s")]
        public string? SecondUser { get; set; }
        [JsonProperty("t")]
        public Thread? Thread { get; set; }
        [JsonProperty("d")]
        public bool Finished { get; set; }
        [JsonProperty("p")]
        public DateTime PostDate { get; set; } = DateTime.Now;

        [JsonProperty("w")]
        public string? Winner { get; set; }

        [JsonIgnore]
        public bool Irrelevant { get; set; }

        public override string ToString()
        {
            return $"{FirstUser} vs. {SecondUser ?? "???"} in {Thread}";
        }

    }
}
