using Newtonsoft.Json;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using TournamentParser.Core.Data;
using TournamentParser.Data;
using TournamentParser.Finalizer;
using TournamentParser.Tests.TestSupport;
using TournamentParser.ThreadCollector;
using TournamentParser.ThreadScanner;
using TournamentParser.Util;

namespace TournamentParser.Tests
{
    /// <summary>
    /// Offline characterization tests: they pin the current observable behavior of
    /// TournamentParser.Core so performance refactorings can be verified not to change it.
    /// </summary>
    [TestFixture]
    public class CharacterizationTests
    {
        private HttpClient _originalClient = null!;

        [SetUp]
        public void SetUp()
        {
            _originalClient = Common.HttpClient;
        }

        [TearDown]
        public void TearDown()
        {
            Common.HttpClient = _originalClient;
        }

        private static string Fixture(string name)
            => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", name));

        #region RegexUtil

        [Test]
        public void Regex_RemovesCommasAndSpaces_AndLowercases()
        {
            var util = new RegexUtil();
            Assert.That(util.Regex("Foo, Bar"), Is.EqualTo("foobar"));
            Assert.That(util.Regex(null), Is.EqualTo(""));
            Assert.That(util.Regex("  A B  C, ,d  "), Is.EqualTo("abcd"));
        }

        [Test]
        public void RegexWithSpace_RemovesCommas_KeepsSpaces_AndLowercases()
        {
            var util = new RegexUtil();
            Assert.That(util.RegexWithSpace("Foo, Bar"), Is.EqualTo("foo bar"));
            Assert.That(util.RegexWithSpace(null), Is.EqualTo(""));
        }

        [Test]
        public void RegexWithABC_CutsLastSpaceSegment_ThenStripsNonAlphanumeric()
        {
            var util = new RegexUtil();
            Assert.That(util.RegexWithABC("Tour Round 1"), Is.EqualTo("tourround"));
            Assert.That(util.RegexWithABC("Alpha!7"), Is.EqualTo("alpha7"));
            Assert.That(util.RegexWithABC(null), Is.EqualTo(""));
        }

        [Test]
        public void StripHTML_RemovesTags_Brackets_Parens_AndNarrowNoBreakSpace()
        {
            var util = new RegexUtil();
            Assert.That(util.StripHTML("<b>Alice</b> vs <b>Bob</b>"), Is.EqualTo("Alice vs Bob"));
            Assert.That(util.StripHTML("Alice [OU] (won) vs Bob"), Is.EqualTo("Alice   vs Bob"));
            Assert.That(util.StripHTML("Alice vs Bob"), Is.EqualTo("Alicevs Bob"));
            Assert.That(util.StripHTML("  spaced  "), Is.EqualTo("spaced"));
        }

        [Test]
        public void StripHTML_AppliesReplacementsSequentially_TagsBeforeBrackets()
        {
            // Guards the order-dependence of the four chained replaces: a single-pass
            // alternation regex would produce "c>" here instead of "[a".
            var util = new RegexUtil();
            Assert.That(util.StripHTML("[a<b]c>"), Is.EqualTo("[a"));
        }

        [Test]
        public void RemovePositions_StripsLeadingSeedNumbers()
        {
            var util = new RegexUtil();
            Assert.That(util.RemovePositions("1. Alice"), Is.EqualTo("Alice"));
            Assert.That(util.RemovePositions("12. Foo Bar"), Is.EqualTo("Foo Bar"));
            Assert.That(util.RemovePositions("Alice"), Is.EqualTo("Alice"));
        }

        [Test]
        public void RemoveNumberReplays_StripsScoreSuffixes()
        {
            var util = new RegexUtil();
            Assert.That(util.RemoveNumberReplays("Alice - 1 | 2"), Is.EqualTo("Alice"));
            Assert.That(util.RemoveNumberReplays("Alice - 3"), Is.EqualTo("Alice"));
            Assert.That(util.RemoveNumberReplays("Alice"), Is.EqualTo("Alice"));
        }

        [Test]
        public void RemoveReactions_CutsReactionsBlock()
        {
            var util = new RegexUtil();
            Assert.That(util.RemoveReactions("great game\nreactions:\nlike x3"), Is.EqualTo("great game"));
            Assert.That(util.RemoveReactions("no reactions here"), Is.EqualTo("no reactions here"));
        }

        #endregion

        #region Scanner on synthetic pages

        private const string SyntheticThreadUrl = "https://www.smogon.com/forums/threads/test-tour-round-1.4242/";

        private static string BuildSyntheticPage()
        {
            return SyntheticPages.ThreadPage("Test Tour - Round 1", new[]
            {
                new SyntheticPages.Post
                {
                    Author = "TourHost",
                    UserId = 900,
                    DateTitle = "Mar 22, 2021 at 12:15 AM",
                    BodyLines = new List<string>
                    {
                        "<b>Alice</b> vs. Bob",
                        "<a href=\"https://replay.pokemonshowdown.com/gen8ou-1111\">game 1</a>",
                        "Alice vs. Carol",
                    },
                },
                new SyntheticPages.Post
                {
                    Author = "Alice",
                    UserId = 901,
                    DateTitle = "Mar 23, 2021 at 5:30 PM",
                    BodyLines = new List<string>
                    {
                        "i beat Bob easily today",
                        "<a href=\"https://replay.pokemonshowdown.com/gen8ou-2222\">win</a>",
                    },
                },
            });
        }

        private static SmogonThreadScanner ScanSyntheticThread(out FakeHttpMessageHandler fake)
        {
            fake = new FakeHttpMessageHandler();
            fake.Map(SyntheticThreadUrl + "page-1", BuildSyntheticPage());
            Common.HttpClient = new HttpClient(fake);

            var scanner = new SmogonThreadScanner();
            var result = scanner.AnalyzeTopic(SyntheticThreadUrl, CancellationToken.None).Result;
            Assert.That(result, Is.Not.Null, "synthetic scan must not fail");
            return scanner;
        }

        [Test]
        public void SyntheticScan_CreatesUsersAndMatchesFromVsLines()
        {
            var scanner = ScanSyntheticThread(out _);

            Assert.That(scanner.NameUserTranslation.Keys,
                Is.EquivalentTo(new[] { "alice", "bob", "carol", "tourhost" }));

            var alice = scanner.NameUserTranslation["alice"];
            Assert.That(alice.NormalName, Is.EqualTo("Alice"));
            Assert.That(alice.Id, Is.EqualTo(901));
            Assert.That(alice.ProfileLink, Is.EqualTo("/forums/members/alice.901/"));

            var host = scanner.NameUserTranslation["tourhost"];
            Assert.That(host.Matches, Is.Empty);
            Assert.That(host.Id, Is.EqualTo(900));
        }

        [Test]
        public void SyntheticScan_PinsMatchDetails_WinnerReplaysAndDates()
        {
            var scanner = ScanSyntheticThread(out _);
            var alice = scanner.NameUserTranslation["alice"];

            // Post 1: two announced matches; post 2: replay post creates one extra match.
            Assert.That(alice.Matches.Count, Is.EqualTo(3));

            var vsBob = alice.Matches.Single(m => m.SecondUser == "bob" && m.Winner != null);
            Assert.That(vsBob.FirstUser, Is.EqualTo("alice"));
            Assert.That(vsBob.Winner, Is.EqualTo("alice"));
            Assert.That(vsBob.Finished, Is.True);
            Assert.That(vsBob.Replays, Is.EqualTo(new[] { "https://replay.pokemonshowdown.com/gen8ou-1111" }));
            Assert.That(vsBob.PostDate, Is.EqualTo(new DateTime(2021, 3, 22, 0, 15, 0)));
            Assert.That(vsBob.Thread!.Name, Is.EqualTo("Test Tour - Round 1"));
            Assert.That(vsBob.Thread.Id, Is.EqualTo("4242"));

            var vsCarol = alice.Matches.Single(m => m.SecondUser == "carol");
            Assert.That(vsCarol.Winner, Is.Null);
            Assert.That(vsCarol.Replays, Is.Empty);

            // Post 2 by Alice mentions Bob and carries a replay: a new finished match is created.
            var replayMatch = alice.Matches.Single(m => m.SecondUser == "bob" && m.Winner == null);
            Assert.That(replayMatch.FirstUser, Is.EqualTo("alice"));
            Assert.That(replayMatch.Finished, Is.True);
            Assert.That(replayMatch.Replays, Is.EqualTo(new[] { "https://replay.pokemonshowdown.com/gen8ou-2222" }));
            Assert.That(replayMatch.PostDate, Is.EqualTo(new DateTime(2021, 3, 23, 17, 30, 0)));

            var bob = scanner.NameUserTranslation["bob"];
            Assert.That(bob.Matches.Count, Is.EqualTo(2));
            Assert.That(bob.Matches.Select(m => m), Does.Contain(replayMatch));
        }

        [Test]
        public void SyntheticScan_TopicAnalyzeResult_PinsPagesLinksAndLastPost()
        {
            var fake = new FakeHttpMessageHandler();
            fake.Map(SyntheticThreadUrl + "page-1", BuildSyntheticPage());
            Common.HttpClient = new HttpClient(fake);

            var scanner = new SmogonThreadScanner();
            var result = scanner.AnalyzeTopic(SyntheticThreadUrl, CancellationToken.None).Result!;

            Assert.That(result.NumberOfPages, Is.EqualTo(1));
            Assert.That(result.CollectedLinks, Has.Count.EqualTo(1));
            Assert.That(result.LastPost, Is.EqualTo(new DateTime(2021, 3, 23, 17, 30, 0)));
            Assert.That(fake.Requests, Is.EqualTo(new[] { SyntheticThreadUrl + "page-1" }));
        }

        [Test]
        public void SyntheticRescanFromCollectedLinks_ReproducesFreshScanState()
        {
            var scanner = ScanSyntheticThread(out var fake);
            var freshResult = new SmogonThreadScanner();
            Common.HttpClient = new HttpClient(fake);
            var analyzeResult = freshResult.AnalyzeTopic(SyntheticThreadUrl, CancellationToken.None).Result!;

            var rescanScanner = new SmogonThreadScanner();
            rescanScanner.AnalyzeTopic(SyntheticThreadUrl, analyzeResult.CollectedLinks, CancellationToken.None).Wait();

            Assert.That(rescanScanner.NameUserTranslation.Keys,
                Is.EquivalentTo(scanner.NameUserTranslation.Keys));
            Assert.That(rescanScanner.NameUserTranslation["alice"].Matches.Count,
                Is.EqualTo(scanner.NameUserTranslation["alice"].Matches.Count));
            Assert.That(rescanScanner.NameUserTranslation["bob"].Matches.Count,
                Is.EqualTo(scanner.NameUserTranslation["bob"].Matches.Count));
        }

        [Test]
        public void ScanThreads_WithoutCache_ScansViaHttp()
        {
            var fake = new FakeHttpMessageHandler();
            fake.Map(SyntheticThreadUrl + "page-1", BuildSyntheticPage());
            Common.HttpClient = new HttpClient(fake);

            var scanner = new SmogonThreadScanner();
            scanner.ScanThreads(new Dictionary<string, List<string>>
            {
                { "forum", new List<string> { SyntheticThreadUrl } },
            }).Wait();

            Assert.That(scanner.NameUserTranslation.Keys,
                Is.EquivalentTo(new[] { "alice", "bob", "carol", "tourhost" }));
        }

        [Test]
        public void ScanThreads_WritesAnalyzeResultToCache()
        {
            var fake = new FakeHttpMessageHandler();
            fake.Map(SyntheticThreadUrl + "page-1", BuildSyntheticPage());
            Common.HttpClient = new HttpClient(fake);
            var cache = new InMemoryDistributedCache();

            var scanner = new SmogonThreadScanner(cache);
            scanner.ScanThreads(new Dictionary<string, List<string>>
            {
                { "forum", new List<string> { SyntheticThreadUrl } },
            }).Wait();

            Assert.That(cache.Store.ContainsKey(SyntheticThreadUrl), Is.True);
            var cached = JsonConvert.DeserializeObject<TopicAnalyzeResult>(
                Encoding.UTF8.GetString(cache.Store[SyntheticThreadUrl]))!;
            Assert.That(cached.NumberOfPages, Is.EqualTo(1));
            Assert.That(cached.LastPost, Is.EqualTo(new DateTime(2021, 3, 23, 17, 30, 0)));
            Assert.That(cached.CollectedLinks, Has.Count.EqualTo(1));
        }

        [Test]
        public void ScanThreads_WithStaleCacheEntry_UsesCacheAndSkipsHttp()
        {
            // First scan to produce a cache entry (LastPost is 2021 => older than 6 months).
            var fake = new FakeHttpMessageHandler();
            fake.Map(SyntheticThreadUrl + "page-1", BuildSyntheticPage());
            Common.HttpClient = new HttpClient(fake);
            var cache = new InMemoryDistributedCache();
            new SmogonThreadScanner(cache).ScanThreads(new Dictionary<string, List<string>>
            {
                { "forum", new List<string> { SyntheticThreadUrl } },
            }).Wait();

            // Second scan with an empty fake handler: everything must come from the cache.
            var offlineFake = new FakeHttpMessageHandler();
            Common.HttpClient = new HttpClient(offlineFake);
            var scanner = new SmogonThreadScanner(cache);
            scanner.ScanThreads(new Dictionary<string, List<string>>
            {
                { "forum", new List<string> { SyntheticThreadUrl } },
            }).Wait();

            Assert.That(offlineFake.Requests, Is.Empty);
            Assert.That(scanner.NameUserTranslation.Keys,
                Is.EquivalentTo(new[] { "alice", "bob", "carol", "tourhost" }));
            Assert.That(scanner.NameUserTranslation["alice"].Matches.Count, Is.EqualTo(3));
        }

        [Test]
        public void SyntheticMultiPageScan_ProcessesPagesInOrder()
        {
            const string url = "https://www.smogon.com/forums/threads/multi-page.777/";
            var page1 = SyntheticPages.ThreadPage("Multi Page Tour", new[]
            {
                new SyntheticPages.Post
                {
                    Author = "HostOne",
                    UserId = 800,
                    DateTitle = "Jan 1, 2021 at 1:00 PM",
                    BodyLines = new List<string> { "Dave vs. Erin" },
                },
            }, totalPages: 3);
            var page2 = SyntheticPages.ThreadPage("Multi Page Tour", new[]
            {
                new SyntheticPages.Post
                {
                    Author = "HostOne",
                    UserId = 800,
                    DateTitle = "Jan 2, 2021 at 1:00 PM",
                    BodyLines = new List<string> { "Frank vs. Grace" },
                },
            }, totalPages: 3);
            var page3 = SyntheticPages.ThreadPage("Multi Page Tour", new[]
            {
                new SyntheticPages.Post
                {
                    Author = "HostOne",
                    UserId = 800,
                    DateTitle = "Jan 3, 2021 at 1:00 PM",
                    BodyLines = new List<string> { "Heidi vs. Ivan" },
                },
            }, totalPages: 3);

            var fake = new FakeHttpMessageHandler();
            fake.Map(url + "page-1", page1);
            fake.Map(url + "page-2", page2);
            fake.Map(url + "page-3", page3);
            Common.HttpClient = new HttpClient(fake);

            var scanner = new SmogonThreadScanner();
            var result = scanner.AnalyzeTopic(url, CancellationToken.None).Result!;

            Assert.That(result.NumberOfPages, Is.EqualTo(3));
            Assert.That(result.CollectedLinks, Has.Count.EqualTo(3));
            Assert.That(result.LastPost, Is.EqualTo(new DateTime(2021, 1, 3, 13, 0, 0)));
            // Page order must be preserved in the collected output (used for cached re-scans).
            Assert.That(result.CollectedLinks[0], Does.Contain("Dave vs. Erin"));
            Assert.That(result.CollectedLinks[1], Does.Contain("Frank vs. Grace"));
            Assert.That(result.CollectedLinks[2], Does.Contain("Heidi vs. Ivan"));
            // All three pages requested (in any order once fetching is parallelized).
            Assert.That(fake.Requests, Is.EquivalentTo(new[] { url + "page-1", url + "page-2", url + "page-3" }));

            // Post numbers: only posts 1-2 create matches from vs-lines; on later pages
            // PostNumber continues at (page-1)*25, so their vs-lines create no users/matches.
            Assert.That(scanner.NameUserTranslation.Keys, Is.EquivalentTo(
                new[] { "dave", "erin", "hostone" }));
            Assert.That(scanner.NameUserTranslation["dave"].Matches.Count, Is.EqualTo(1));
            Assert.That(scanner.NameUserTranslation["erin"].Matches.Count, Is.EqualTo(1));
        }

        #endregion

        #region Scanner on real fixture pages

        private const string FixtureThreadUrl = "https://www.smogon.com/forums/threads/official-smogon-tournament-xvii-finals-won-by-empo.3680402/";

        private static SmogonThreadScanner ScanFixtureThread(out FakeHttpMessageHandler fake, out TopicAnalyzeResult result)
        {
            fake = new FakeHttpMessageHandler();
            for (var page = 1; page <= 5; page++)
            {
                fake.Map(FixtureThreadUrl + "page-" + page, Fixture($"ost-xvii-finals-page-{page}.html"));
            }
            Common.HttpClient = new HttpClient(fake);

            var scanner = new SmogonThreadScanner();
            result = scanner.AnalyzeTopic(FixtureThreadUrl, CancellationToken.None).Result!;
            Assert.That(result, Is.Not.Null, "fixture scan must not fail");
            return scanner;
        }

        [Test]
        public void FixtureScan_PinsOverallShape()
        {
            var scanner = ScanFixtureThread(out var fake, out var result);

            Assert.That(result.NumberOfPages, Is.EqualTo(5));
            Assert.That(result.CollectedLinks, Has.Count.EqualTo(5));
            Assert.That(fake.Requests, Is.EquivalentTo(
                Enumerable.Range(1, 5).Select(p => FixtureThreadUrl + "page-" + p)));

            var playingUsers = scanner.Users.Where(user => !user.Matches.IsEmpty).ToList();
            Assert.That(playingUsers.Count, Is.EqualTo(2));
            Assert.That(playingUsers.Select(u => u.Name), Is.EquivalentTo(new[] { "empo", "lord_enz" }));

            var match = playingUsers.First(u => u.Name == "empo").Matches.First();
            Assert.That(match.Replays.Count, Is.EqualTo(3));
        }

        [Test]
        public void FixtureRescanFromCollectedLinks_ReproducesFreshScanState()
        {
            var scanner = ScanFixtureThread(out _, out var result);

            var rescanScanner = new SmogonThreadScanner();
            rescanScanner.AnalyzeTopic(FixtureThreadUrl, result.CollectedLinks, CancellationToken.None).Wait();

            Assert.That(rescanScanner.NameUserTranslation.Keys,
                Is.EquivalentTo(scanner.NameUserTranslation.Keys));
            foreach (var (name, user) in scanner.NameUserTranslation)
            {
                var rescanUser = rescanScanner.NameUserTranslation[name];
                Assert.That(rescanUser.Matches.Count, Is.EqualTo(user.Matches.Count), $"match count for {name}");
                Assert.That(
                    rescanUser.Matches.Sum(m => m.Replays.Count),
                    Is.EqualTo(user.Matches.Sum(m => m.Replays.Count)),
                    $"replay count for {name}");
            }
        }

        #endregion

        #region Collector on synthetic pages

        [Test]
        public void Collector_FindsSubforumsAndThreads()
        {
            var fake = new FakeHttpMessageHandler();
            fake.MapBothSchemes("www.smogon.com/forums/",
                SyntheticPages.ForumMainPage("Tournaments", "/forums/forums/tour-alpha.100/", "Tour Alpha"));
            var listing = SyntheticPages.ForumListingPage("/forums/threads/alpha-round-1.111/");
            fake.MapBothSchemes("www.smogon.com/forums/forums/tour-alpha.100/", listing);
            fake.MapBothSchemes("www.smogon.com/forums/forums/tour-alpha.100/page-1", listing);
            Common.HttpClient = new HttpClient(fake);

            var collector = new SmogonThreadCollector();
            var threads = collector.GetGeneralThreadsForForums("Tournaments").Result;

            Assert.That(threads, Has.Count.EqualTo(1));
            var (forumUrl, threadUrls) = threads.Single();
            Assert.That(forumUrl, Does.EndWith("/forums/forums/tour-alpha.100/"));
            Assert.That(threadUrls, Has.Count.EqualTo(1));
            Assert.That(threadUrls[0], Does.EndWith("/forums/threads/alpha-round-1.111/"));
            Assert.That(threadUrls[0], Does.StartWith("http"));
        }

        [Test]
        public void Collector_ProducesHttpsUrls()
        {
            var fake = new FakeHttpMessageHandler();
            fake.MapBothSchemes("www.smogon.com/forums/",
                SyntheticPages.ForumMainPage("Tournaments", "/forums/forums/tour-alpha.100/", "Tour Alpha"));
            var listing = SyntheticPages.ForumListingPage("/forums/threads/alpha-round-1.111/");
            fake.MapBothSchemes("www.smogon.com/forums/forums/tour-alpha.100/", listing);
            fake.MapBothSchemes("www.smogon.com/forums/forums/tour-alpha.100/page-1", listing);
            Common.HttpClient = new HttpClient(fake);

            var threads = new SmogonThreadCollector().GetGeneralThreadsForForums("Tournaments").Result;

            var (forumUrl, threadUrls) = threads.Single();
            Assert.That(forumUrl, Does.StartWith("https://"));
            Assert.That(threadUrls[0], Does.StartWith("https://"));
            Assert.That(fake.Requests, Has.All.StartWith("https://"));
        }

        #endregion

        #region HTTP configuration

        [Test]
        public void Common_OfficialTournamentSite_UsesHttps()
        {
            Assert.That(Common.OfficialTournamentSite, Does.StartWith("https://"));
        }

        [Test]
        public void Common_HttpClientHandler_EnablesAutomaticDecompression()
        {
            using var handler = Common.CreateHttpClientHandler();
            Assert.That(handler.AutomaticDecompression, Is.EqualTo(System.Net.DecompressionMethods.All));
        }

        #endregion

        #region Finalizer

        [Test]
        public void Finalize_RemovesIrrelevantMatches_AndInfersWinnersFromLaterRounds()
        {
            var thread1 = new Data.Thread { Name = "Tour Round 1" };
            var thread1Again = new Data.Thread { Name = "Tour Round 1" };
            var thread2 = new Data.Thread { Name = "Other Cup 3" };

            var m1 = new TournamentMatch { FirstUser = "alice", SecondUser = "bob", Thread = thread1, PostDate = new DateTime(2021, 1, 1) };
            var m2 = new TournamentMatch { FirstUser = "alice", SecondUser = "carol", Thread = thread1Again, PostDate = new DateTime(2021, 2, 1) };
            var m3 = new TournamentMatch { FirstUser = "alice", SecondUser = "dave", Thread = thread2, PostDate = new DateTime(2021, 3, 1) };
            var m4 = new TournamentMatch { FirstUser = "alice", SecondUser = "eve", Thread = thread1, Irrelevant = true };

            var alice = new User { Name = "alice" };
            alice.Matches.Add(m1);
            alice.Matches.Add(m2);
            alice.Matches.Add(m3);
            alice.Matches.Add(m4);

            new SmogonFinalizer().Finalize(new Dictionary<string, User> { { "alice", alice } });

            Assert.That(alice.Matches.Count, Is.EqualTo(3));
            Assert.That(alice.Matches, Does.Not.Contain(m4));

            // m1 has a later match (m2) in the same normalized thread => alice advanced => won m1.
            Assert.That(m1.Winner, Is.EqualTo("alice"));
            Assert.That(m1.Finished, Is.True);
            // m2 is the latest in its thread, m3 is alone in its thread: no inference.
            Assert.That(m2.Winner, Is.Null);
            Assert.That(m3.Winner, Is.Null);
        }

        [Test]
        public void Finalize_DoesNotOverrideExistingWinners()
        {
            var thread = new Data.Thread { Name = "Tour Round 1" };
            var m1 = new TournamentMatch { FirstUser = "alice", SecondUser = "bob", Thread = thread, PostDate = new DateTime(2021, 1, 1), Winner = "bob" };
            var m2 = new TournamentMatch { FirstUser = "alice", SecondUser = "carol", Thread = thread, PostDate = new DateTime(2021, 2, 1) };

            var alice = new User { Name = "alice" };
            alice.Matches.Add(m1);
            alice.Matches.Add(m2);

            new SmogonFinalizer().Finalize(new Dictionary<string, User> { { "alice", alice } });

            Assert.That(m1.Winner, Is.EqualTo("bob"));
        }

        #endregion

        #region Data types

        [Test]
        public void User_ToString_PinsFormat()
        {
            var user = new User { Name = "alice", Id = 5 };
            user.Matches.Add(new TournamentMatch
            {
                FirstUser = "alice",
                SecondUser = "bob",
                Thread = new Data.Thread { Name = "T" },
            });

            Assert.That(user.ToString(), Is.EqualTo(
                "The user 'alice' with the id 5 has the following matches:\r\n" +
                "alice vs. bob in T\r\n"));
        }

        #endregion
    }
}
