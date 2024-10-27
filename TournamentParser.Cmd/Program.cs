using NeoSmart.Caching.Sqlite;
using Newtonsoft.Json;
using TournamentParser.Parser;

SQLitePCL.Batteries_V2.Init();

var tournament = new SmogonParser(
    new SqliteCache(
        new SqliteCacheOptions()
        {
            MemoryOnly = false,
            CachePath = "SmogonTournamentParser.db",
        }
    )
);
var nameUserTranslation = await tournament.GetMatchesForUsers().ConfigureAwait(false);

// write text directly to a file
using (var file = File.CreateText("output.txt"))
{
    foreach (var user in nameUserTranslation.Values.Where((user) => !user.Matches.IsEmpty))
    {
        file.WriteLine(user.ToString());
    }
}

// serialize JSON directly to a file
using (var file = File.CreateText("output.json"))
{
    var serializer = new JsonSerializer();
    serializer.Serialize(file, nameUserTranslation);
}
