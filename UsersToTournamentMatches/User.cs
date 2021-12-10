using System.Collections.Generic;

namespace UsersToTournamentMatches
{
    public class User
    {

        public int Id { get; set; } = -1;
        public string Name { get; set; }
        public string ProfileLink { get; set; }
        public string NormalName { get; set; }
        public ICollection<Match> Matches { get; set; } = new List<Match>();

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
