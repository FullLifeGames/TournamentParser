using NeoSmart.Caching.Sqlite;
using Newtonsoft.Json;
using System.Globalization;
using TournamentParser.Parser;
using TournamentParser.ThreadCollector;
using TournamentParser.Util;

const string Usage = """
    Usage: TournamentParser.Cmd [--full] [--delay <seconds>] [--recent-days <days>] [--max-requests <count>]

      --full                  Ignore the stored state and read every listing and thread page again.
      --delay <seconds>       Pause between two requests to Smogon (default 2).
      --recent-days <days>    Forums and threads with posts younger than this are checked in every run
                              for locks and title changes (default 7, 0 turns it off).
      --max-requests <count>  Stop after this many requests; the next run continues from there
                              (default 0, no limit).

    The crawl state is kept in SmogonTournamentParser.db. After the first run, only new activity is read.
    Exit codes: 0 done, 1 error, 2 bot check or unexpected page, 3 request limit reached, 64 usage.
    Output files are written only after a complete run.
    """;

var collectorOptions = new CollectorOptions();
var httpOptions = new PoliteHttpOptions();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--full":
            collectorOptions.FullRefresh = true;
            break;
        case "--delay" when i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0:
            httpOptions.MinDelay = TimeSpan.FromSeconds(seconds);
            i++;
            break;
        case "--recent-days" when i + 1 < args.Length && int.TryParse(args[i + 1], out var days) && days >= 0:
            collectorOptions.RecentActivityWindow = TimeSpan.FromDays(days);
            i++;
            break;
        case "--max-requests" when i + 1 < args.Length && int.TryParse(args[i + 1], out var maxRequests) && maxRequests >= 0:
            collectorOptions.MaxRequests = maxRequests;
            i++;
            break;
        default:
            Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
            Console.Error.WriteLine(Usage);
            return 64;
    }
}

var httpHandler = new PoliteHttpMessageHandler(httpOptions);
Common.HttpClient = PoliteHttpMessageHandler.CreateClient(httpHandler);

SQLitePCL.Batteries_V2.Init();

try
{
    var tournament = new SmogonParser(
        new SqliteCache(
            new SqliteCacheOptions()
            {
                MemoryOnly = false,
                CachePath = "SmogonTournamentParser.db",
            }
        ),
        collectorOptions
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

    // serialize each user's data into its own JSON file
    var usersDirectory = Directory.CreateDirectory("users");
    foreach (var user in nameUserTranslation)
    {
        var fileName = Common.SanitizeFileName(user.Key) + ".json";
        using (var file = File.CreateText(Path.Combine(usersDirectory.FullName, fileName)))
        {
            var serializer = new JsonSerializer();
            serializer.Serialize(file, user.Value);
        }
    }
    return 0;
}
catch (Exception e) when (e is SmogonAccessBlockedException or UnexpectedPageException)
{
    // Threads read before the stop are stored; the next run continues from there.
    Console.Error.WriteLine(e.Message);
    return 2;
}
catch (RequestBudgetExhaustedException e)
{
    Console.Error.WriteLine(e.Message);
    return 3;
}
catch (Exception e)
{
    Console.Error.WriteLine(e);
    return 1;
}
finally
{
    Console.WriteLine(httpHandler.Statistics);
}
