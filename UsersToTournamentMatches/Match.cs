using System;
using System.Collections.Generic;

namespace UsersToTournamentMatches
{
    public class Match
    {

        public ICollection<string> Replays { get; set; } = new List<string>();
        public string FirstUser { get; set; }
        public string SecondUser { get; set; }
        public Thread Thread { get; set; }
        public bool Finished { get; set; }
        public DateTime PostDate { get; set; } = DateTime.Now;

        public string Winner { get; set; }

        public override string ToString()
        {
            return $"{FirstUser} vs. {SecondUser ?? "???"} in {Thread}";
        }

    }
}
