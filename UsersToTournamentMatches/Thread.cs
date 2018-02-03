using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UsersToTournamentMatches
{
    class Thread
    {

        public String link;
        public String name;
        public bool locked = false;

        public override string ToString()
        {
            return name;
        }

    }
}
