using Newtonsoft.Json;
using TournamentParser.Tournament;

var tournament = new SmogonTournament();
var nameUserTranslation = await tournament.GetMatchesForUsers().ConfigureAwait(false);

var output = "";
foreach (var user in nameUserTranslation.Values)
{
    if (!user.Matches.IsEmpty)
    {
        output += user + "\r\n";
    }
}

await File.WriteAllTextAsync("output.txt", output).ConfigureAwait(false);

var json = JsonConvert.SerializeObject(nameUserTranslation);

await File.WriteAllTextAsync("output.json", json).ConfigureAwait(false);
