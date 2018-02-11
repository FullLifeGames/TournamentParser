using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UsersToTournamentMatches
{
    class User
    {

        public int id = -1;
        public String name = null;
        public String profileLink = null;
        public String normalName = null;
        public List<Match> matches = new List<Match>();

        public override string ToString()
        {
            string output = "The user '" + ((name == null) ? "" : name) + "' with the id " + id + " has the following matches:\r\n";

            foreach(Match match in matches)
            {
                output += match + "\r\n";
            }

            return output;
        }
        
    }
}
