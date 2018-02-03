using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UsersToTournamentMatches
{
    class Match
    {

        public List<string> replays = new List<string>();
        public String firstUser = null;
        public String secondUser = null;
        public Thread thread;
        public bool finished = false;
        public DateTime postDate = DateTime.Now;

        public String winner = null;

        public override string ToString()
        {
            return firstUser + " vs. " + ((secondUser == null) ? "???" : secondUser) + " in " + thread;
        }

    }
}
