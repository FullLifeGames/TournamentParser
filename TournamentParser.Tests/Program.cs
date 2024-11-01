﻿using NeoSmart.Caching.Sqlite;
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

            Assert.That(threads.Count > 0);
            Assert.That(nonTourThreads.Count > 0);
        }

        [Test]
        public void Single_Scanner_Simple_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/official-smogon-tournament-xvii-finals-won-by-empo.3680402/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.That(playingUsers.Count() == 2);
            Assert.That(playingUsers.First().Matches.First().Replays.Count == 3);
            Assert.That(!tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Scanner_Broken_Simple_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/oupl-vii-replays-and-usage-statistics.3731530/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            var count = playingUsers.Count();
            Assert.That(count >= 2);
            Assert.That(playingUsers.First().Matches.First().Replays.Count == 3);
            Assert.That(!tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Scanner_SmogTour_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/smogon-tour-30-playoffs-finals-won-by-empo.3673513/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.That(playingUsers.Count() > 10 && playingUsers.Count() < 20);
            var empoMatches = tournament.ThreadScanner.NameUserTranslation["empo"].Matches;
            Assert.That(empoMatches.First((match) =>
                    string.Equals(match.SecondUser, "soulwind", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(match.FirstUser, "soulwind", System.StringComparison.OrdinalIgnoreCase)
                )
                .Replays.Count == 3
            );
            Assert.That(!tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Scanner_Callous_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/callous-invitational-7-teams-replays-and-usage-statistics.3722746/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.That(playingUsers.Count() > 10);
            Assert.That(tournament.ThreadScanner.NameUserTranslation.Any(x => x.Value.Matches
                .First().Replays.Any())
            );
            Assert.That(!tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Scanner_OLT_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/smogons-official-ladder-tournament-v-replay-thread.3640819/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.That(playingUsers.Count() > 20);
            Assert.That(playingUsers.First().Matches.First().Replays.Count > 0);
            Assert.That(!tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Scanner_OLT_Cache_Test()
        {
            SQLitePCL.Batteries_V2.Init();

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
            Assert.That(playingUsers.Count() > 20);
            Assert.That(playingUsers.First().Matches.First().Replays.Count > 0);
            Assert.That(!tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Scanner_SmogTour_Missing_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/smogon-tour-season-29-playoffs-finals-won-by-abr.3664063/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.That(playingUsers.Count() > 15);
            Assert.That(playingUsers.First().Matches.First().Replays.Count > 0);
            Assert.That(!tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Replay_Thread_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/spl-xiii-replays.3695657/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.That(playingUsers.Count() > 15);
            Assert.That(playingUsers.Count((user) => user.Matches.Any((match) => match.Replays.Count > 0)) > 100);
            var singleUser = playingUsers.First((user) => user.Name == "xray");
            Assert.That(singleUser.Matches.First().Replays.Count > 0);
            Assert.That(!tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Scanner_Doubles_Correct_Info_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/ss-doubles-ou-2021-fall-seasonal-round-6.3691483/", new CancellationToken()).Wait();
            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/2021-doubles-invitational.3694498/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.That(playingUsers.Count() > 15);
            Assert.That(playingUsers.Any((user) => user.Matches.Any((match) => match.Replays.Count > 0)));
            Assert.That(playingUsers.Where((user) => user.Name == "crunchman").First().Matches.Count == 1);
        }

        [Test]
        public void Single_Scanner_Stress_Test()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/official-smogon-tournament-xvii-round-1-d.3676237/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.That(playingUsers.Count() > 100);
            Assert.That(!tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        public void Single_Scanner_Stress_Test_2()
        {
            var tournament = new SmogonParser();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/the-uu-open-xii-round-2-replays-mandatory-no-exceptions.3721437/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.That(playingUsers.Count() > 100);
            // TODO: FIX
            //Assert.That(playingUsers.Count((user) => user.Matches.Any((match) => match.Replays.Count > 0)) > 100);
            Assert.That(!tournament.ThreadScanner.NameUserTranslation.IsEmpty);
        }

        [Test]
        [Explicit]
        public void Map_Full()
        {
            var matches = new SmogonParser().GetMatchesForUsers().Result;

            Assert.That(matches.Count > 0);
        }
    }
}
