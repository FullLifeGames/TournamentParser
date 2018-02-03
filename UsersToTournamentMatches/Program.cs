using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UsersToTournamentMatches
{
    class Program
    {
        static void Main(string[] args)
        {
            Tournament tournament = new Tournament();
            Dictionary<string, User> nameUserTranslation = tournament.GetMatchesForUsers();

            StreamWriter sw = new StreamWriter("output.txt");
            foreach(User user in nameUserTranslation.Values)
            {
                if(user.matches.Count > 0)
                {
                    sw.WriteLine(user);
                }
            }
            sw.Close();

            string json = JsonConvert.SerializeObject(nameUserTranslation);

            sw = new StreamWriter("output.json");
            sw.Write(json);
            sw.Close();
        }
    }
}
