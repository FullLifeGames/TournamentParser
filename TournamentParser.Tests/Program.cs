using NUnit.Framework;
using System.Linq;
using System.Threading;
using TournamentParser.Tournament;

namespace TournamentParser.Tests
{
    [TestFixture]
    public class TestTournamentParser
    {
        [Test]
        [Explicit]
        public void Collector_Test()
        {
            var tournament = new SmogonTournament();
            var threads = tournament.ThreadCollector.GetThreadsForForums().Result;
            var nonTourThreads = tournament.ThreadCollector.GetNonTourThreadsForForums().Result;

            Assert.IsTrue(threads.Count > 0);
            Assert.IsTrue(nonTourThreads.Count > 0);
        }

        [Test]
        public void Single_Scanner_Simple_Test()
        {
            var tournament = new SmogonTournament();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/official-smogon-tournament-xvii-finals-won-by-empo.3680402/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.IsTrue(playingUsers.Count() == 2);
            Assert.IsTrue(playingUsers.First().Matches.First().Replays.Count == 3);
            Assert.IsTrue(tournament.ThreadScanner.NameUserTranslation.Count > 0);
        }

        [Test]
        public void Single_Scanner_SmogTour_Test()
        {
            var tournament = new SmogonTournament();

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
            Assert.IsTrue(tournament.ThreadScanner.NameUserTranslation.Count > 0);
        }

        [Test]
        public void Single_Scanner_OLT_Test()
        {
            var tournament = new SmogonTournament();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/smogons-official-ladder-tournament-v-replay-thread.3640819/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.IsTrue(playingUsers.Count() > 20);
            Assert.IsTrue(playingUsers.First().Matches.First().Replays.Count > 0);
            Assert.IsTrue(tournament.ThreadScanner.NameUserTranslation.Count > 0);
        }

        [Test]
        public void Single_Scanner_Stress_Test()
        {
            var tournament = new SmogonTournament();

            tournament.ThreadScanner.AnalyzeTopic("https://www.smogon.com/forums/threads/official-smogon-tournament-xvii-round-1-d.3676237/", new CancellationToken()).Wait();

            var playingUsers = tournament.ThreadScanner.Users.Where((user) => !user.Matches.IsEmpty);
            Assert.IsTrue(playingUsers.Count() > 100);
            Assert.IsTrue(tournament.ThreadScanner.NameUserTranslation.Count > 0);
        }

        [Test]
        [Explicit]
        public void Map_Full()
        {
            var matches = new SmogonTournament().GetMatchesForUsers().Result;

            Assert.IsTrue(matches.Count > 0);
        }
    }
}
