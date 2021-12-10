using Newtonsoft.Json;
using System.IO;
using UsersToTournamentMatches;

var tournament = new Tournament();
var nameUserTranslation = await tournament.GetMatchesForUsers();

var output = "";
foreach (var user in nameUserTranslation.Values)
{
    if (user.Matches.Count > 0)
    {
        output = user + "\r\n";
    }
}

await File.WriteAllTextAsync("output.txt", output);

var json = JsonConvert.SerializeObject(nameUserTranslation);

await File.WriteAllTextAsync("output.json", json);
