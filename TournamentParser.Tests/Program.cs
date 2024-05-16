using NeoSmart.Caching.Sqlite;
using NUnit.Framework;
using System.Linq;
using System.Threading;
using TournamentParser.Parser;

namespace TournamentParser.Tests
{
    [TestFixture]
    public class TestTournamentParser
    {
        [Test]
        [Explicit]
        public void Collector_Test()
        {
            var tournament = new SmogonParser();
            var threads = tournament.ThreadCollector.GetThreadsForForums().Result;
            var nonTourThreads = tournament.ThreadCollector.GetNonTourThreadsForForums().Result;

            Assert.IsTrue(threads.Count > 0);
            Assert.IsTrue(nonTourThreads.Count > 0);
        }

        [Test]
        public void Single_Scanner_Simple_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/official-smogon-tournament-xvii-finals-won-by-empo.3680402/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.IsTrue(playingUsers.Count() == 2);
            Assert.IsTrue(playingUsers.First().Matches.First().Replays.Count == 3);
            Assert.IsFalse(tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Scanner_SmogTour_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/smogon-tour-30-playoffs-finals-won-by-empo.3673513/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.IsTrue(playingUsers.Count() > 10 && playingUsers.Count() < 20);
            Assert.IsTrue(tournament.ThreadScanner.NameUserTranslation["empo"].Matches
                .First((match) =>
                    string.Equals(match.SecondUser, "soulwind", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(match.FirstUser, "soulwind", System.StringComparison.OrdinalIgnoreCase)
                )
                .Replays.Count == 3
            );
            Assert.IsFalse(tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Scanner_Callous_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/callous-invitational-7-teams-replays-and-usage-statistics.3722746/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.IsTrue(playingUsers.Count() > 10);
            Assert.IsTrue(tournament.ThreadScanner.NameUserTranslation.Any(x => x.Value.Matches
                .First().Replays.Any())
            );
            Assert.IsFalse(tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Scanner_OLT_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/smogons-official-ladder-tournament-v-replay-thread.3640819/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.IsTrue(playingUsers.Count() > 20);
            Assert.IsTrue(playingUsers.First().Matches.First().Replays.Count > 0);
            Assert.IsFalse(tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Scanner_OLT_Cache_Test()
        {
            var tournament = new SmogonParser(
                new SqliteCache(
                    new SqliteCacheOptions()
                    {
                        MemoryOnly = true,
                        CachePath = "SmogonTournamentParser.db",
                    }
                )
            );

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/smogons-official-ladder-tournament-v-replay-thread.3640819/", new CancellationToken()).Wait();

            tournament.ThreadScanner.Users.Clear();
            tournament.ThreadScanner.NameUserTranslation.Clear();
            tournament.ThreadScanner.IdUserTranslation.Clear();
            tournament.ThreadScanner.UserWithSpaceTranslation.Clear();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/smogons-official-ladder-tournament-v-replay-thread.3640819/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.IsTrue(playingUsers.Count() > 20);
            Assert.IsTrue(playingUsers.First().Matches.First().Replays.Count > 0);
            Assert.IsFalse(tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Scanner_SmogTour_Missing_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/smogon-tour-season-29-playoffs-finals-won-by-abr.3664063/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.IsTrue(playingUsers.Count() > 15);
            Assert.IsTrue(playingUsers.First().Matches.First().Replays.Count > 0);
            Assert.IsFalse(tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Replay_Thread_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/spl-xiii-replays.3695657/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.IsTrue(playingUsers.Count() > 15);
            Assert.IsTrue(playingUsers.Count((user) => user.Matches.Any((match) => match.Replays.Count > 0)) > 100);
            var singleUser = playingUsers.First((user) => user.Name == "xray");
            Assert.IsTrue(singleUser.Matches.First().Replays.Count > 0);
            Assert.IsFalse(tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Scanner_Doubles_Correct_Info_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/ss-doubles-ou-2021-fall-seasonal-round-6.3691483/", new CancellationToken()).Wait();
            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/2021-doubles-invitational.3694498/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.IsTrue(playingUsers.Count() > 15);
            Assert.IsTrue(playingUsers.Any((user) => user.Matches.Any((match) => match.Replays.Count > 0)));
            Assert.IsTrue(playingUsers.First((user) => user.Name == "crunchman").Matches.Count == 1);
        }

        [Test]
        public void Single_Scanner_Stress_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/official-smogon-tournament-xvii-round-1-d.3676237/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.IsTrue(playingUsers.Count() > 100);
            Assert.IsFalse(tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Scanner_Stress_Test_2()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/the-uu-open-xii-round-2-replays-mandatory-no-exceptions.3721437/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.IsTrue(playingUsers.Count() > 100);
            Assert.IsTrue(playingUsers.Count((user) => user.Matches.Any((match) => match.Replays.Count > 0)) > 100);
            Assert.IsFalse(tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        [Explicit]
        public void Map_Full()
        {
            var matches = new SmogonParser().GetMatchesForUsers().Result;

            Assert.IsTrue(matches.Count > 0);
        }
    }
}
