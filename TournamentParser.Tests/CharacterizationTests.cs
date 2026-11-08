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
        public void StripHTML_HandlesTagsInsideQuotedAttributeValues()
        {
            // XenForo user mentions embed the bold winner markup inside the attribute:
            // data-username="<b>TyCarter</b>". The tag regex must not stop at the ">"
            // of the embedded <b>, or a stray "> survives and corrupts the line.
            var util = new RegexUtil();
            Assert.That(
                util.StripHTML("<a data-username=\"<b>Ty</b>\"><b>Ty</b></a> won"),
                Is.EqualTo("Ty won"));
            Assert.That(
                util.StripHTML("<a data-username=\"Zerkas\">Zerkas</a>  vs  <a data-username=\"<b>TyCarter</b>\"><b>TyCarter</b></a><br />"),
                Is.EqualTo("Zerkas  vs  TyCarter"));
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

        private static string BuildSyntheticPage() => SyntheticPages.ThreadPage("Test Tour - Round 1", BuildSyntheticPosts());

        private static List<SyntheticPages.Post> BuildSyntheticPosts()
        {
            return new List<SyntheticPages.Post>
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
            };
        }

        private static SmogonThreadScanner ScanSyntheticThread(out FakeHttpMessageHandler fake)
        {
            fake = new FakeHttpMessageHandler();
            fake.Map(SyntheticThreadUrl, BuildSyntheticPage());
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
            fake.Map(SyntheticThreadUrl, BuildSyntheticPage());
            Common.HttpClient = new HttpClient(fake);

            var scanner = new SmogonThreadScanner();
            var result = scanner.AnalyzeTopic(SyntheticThreadUrl, CancellationToken.None).Result!;

            Assert.That(result.NumberOfPages, Is.EqualTo(1));
            Assert.That(result.CollectedLinks, Has.Count.EqualTo(1));
            Assert.That(result.LastPost, Is.EqualTo(new DateTime(2021, 3, 23, 17, 30, 0)));
            // Page 1 without suffix: ".../page-1" answers with a redirect, which costs a second request.
            Assert.That(fake.Requests, Is.EqualTo(new[] { SyntheticThreadUrl }));
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
            fake.Map(SyntheticThreadUrl, BuildSyntheticPage());
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
        public void ScanThreads_WithCrawlState_ParsesStoredPagesWithoutRequests()
        {
            var forum = new FakeForum();
            var section = new FakeForum.Forum { Title = "Tournaments", Path = "/forums/forums/tournaments.34/" };
            forum.Sections.Add(section);
            section.Threads.Add(new FakeForum.Thread
            {
                Id = 4242,
                Slug = "test-tour-round-1",
                Title = "Test Tour - Round 1",
                Pages = { BuildSyntheticPosts() },
            });
            var fake = new FakeHttpMessageHandler();
            forum.Serve(fake);
            var cache = new InMemoryDistributedCache();
            var threads = new SmogonThreadCollector(cache, new CollectorOptions { HttpClient = new HttpClient(fake) })
                .GetGeneralThreadsForForums("Tournaments").Result;

            var offlineFake = new FakeHttpMessageHandler();
            Common.HttpClient = new HttpClient(offlineFake);
            var scanner = new SmogonThreadScanner(cache);
            scanner.ScanThreads(threads).Wait();

            Assert.That(offlineFake.Requests, Is.Empty);
            Assert.That(scanner.NameUserTranslation.Keys,
                Is.EquivalentTo(new[] { "alice", "bob", "carol", "tourhost" }));
            Assert.That(scanner.NameUserTranslation["alice"].Matches.Count, Is.EqualTo(3));
        }

        [Test]
        public void ScanThreads_ThreadUnknownToTheCrawlState_IsReadFromSmogon()
        {
            var fake = new FakeHttpMessageHandler();
            fake.Map(SyntheticThreadUrl, BuildSyntheticPage());
            Common.HttpClient = new HttpClient(fake);

            var scanner = new SmogonThreadScanner(new InMemoryDistributedCache());
            scanner.ScanThreads(new Dictionary<string, List<string>>
            {
                { "forum", new List<string> { SyntheticThreadUrl } },
            }).Wait();

            Assert.That(fake.Requests, Is.EqualTo(new[] { SyntheticThreadUrl }));
            Assert.That(scanner.NameUserTranslation.Keys,
                Is.EquivalentTo(new[] { "alice", "bob", "carol", "tourhost" }));
        }

        [Test]
        public void AnalyzeTopic_BotCheckInsteadOfThreadPage_ReturnsNull()
        {
            var fake = new FakeHttpMessageHandler();
            fake.Map(SyntheticThreadUrl, "<html><body><p id=\"m\">Checking your browser...</p><script>document.cookie = \"xf_smogon_gate=1\"</script></body></html>");
            Common.HttpClient = new HttpClient(fake);

            var scanner = new SmogonThreadScanner();
            Assert.That(scanner.AnalyzeTopic(SyntheticThreadUrl, CancellationToken.None).Result, Is.Null);
            Assert.That(scanner.NameUserTranslation, Is.Empty);
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
            fake.Map(url, page1);
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
            // One page after the other: all pages live on one host.
            Assert.That(fake.Requests, Is.EqualTo(new[] { url, url + "page-2", url + "page-3" }));

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
                fake.Map(page == 1 ? FixtureThreadUrl : FixtureThreadUrl + "page-" + page, Fixture($"ost-xvii-finals-page-{page}.html"));
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
            Assert.That(fake.Requests, Is.EqualTo(
                Enumerable.Range(1, 5).Select(p => p == 1 ? FixtureThreadUrl : FixtureThreadUrl + "page-" + p)));

            var playingUsers = scanner.Users.Where(user => !user.Matches.IsEmpty).ToList();
            Assert.That(playingUsers.Count, Is.EqualTo(2));
            Assert.That(playingUsers.Select(u => u.Name), Is.EquivalentTo(new[] { "empo", "lord_enz" }));

            var match = playingUsers.First(u => u.Name == "empo").Matches.First();
            Assert.That(match.Replays.Count, Is.EqualTo(3));
        }

        [Test]
        public void FixtureScan_ParsesPostDatesFromModernTimeMarkup()
        {
            // Current XenForo pages have no data-date-string attribute; the post date must
            // be read from the <time> tag's title attribute.
            ScanFixtureThread(out _, out var result);
            Assert.That(result.LastPost, Is.EqualTo(new DateTime(2021, 3, 31, 19, 51, 0)));
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

        [Test]
        public void OltFixtureScan_JsonLdTextBlockDoesNotCreateGarbageUsers()
        {
            // XenForo's JSON-LD block now exposes the first post as a one-line "text": "..."
            // field (formerly "articleBody"); it contains "x vs y" content and must not be
            // parsed as a match line.
            const string url = "https://www.smogon.com/forums/threads/smogons-official-ladder-tournament-v-replay-thread.3640819/";
            var fake = new FakeHttpMessageHandler();
            fake.Map(url, Fixture("olt-v-replay-page-1.html"));
            Common.HttpClient = new HttpClient(fake);

            var scanner = new SmogonThreadScanner();
            var result = scanner.AnalyzeTopic(url, CancellationToken.None).Result;
            Assert.That(result, Is.Not.Null);

            var playingUsers = scanner.Users.Where(user => !user.Matches.IsEmpty).ToList();
            Assert.That(playingUsers.Select(u => u.Name), Has.None.Contains("\n"),
                "JSON-LD text block was parsed as a match line");
            // 35, not 36: "Ojama vs Zuchtrest - 1 | 2 | 3&#8203;" used to spawn a duplicate
            // "zuchtrest&#8203;" user; since scores after " - " are cut from names, the finals
            // match belongs to the real "zuchtrest".
            Assert.That(playingUsers, Has.Count.EqualTo(35));
            Assert.That(playingUsers.Select(u => u.Name), Does.Contain("zuchtrest"));
            Assert.That(playingUsers.Select(u => u.Name), Does.Not.Contain("zuchtrest&#8203;"));
            Assert.That(playingUsers, Has.All.Matches<User>(u => u.Matches.Any(m => m.Replays.Count > 0)),
                "every real OLT participant has at least one replay");
        }

        [Test]
        public void RetroCupFixture_PredictionCommentaryDoesNotCreateGarbageUsers()
        {
            // Post 1 contains prediction paragraphs like
            // "Zokuru vs Vani - this is an intetesting one, ..." on a single line; the text
            // after the dash is commentary, not part of the second user's name.
            var scanner = new SmogonThreadScanner();
            scanner.AnalyzeTopic(
                "https://www.smogon.com/forums/threads/retro-cup-of-pokemon-2025-quarterfinals-tiebreaker-at-46.3772101/",
                new List<string> { Fixture("retro-cup-2025-qf-tiebreaker-page-1.html") },
                CancellationToken.None).Wait();

            Assert.That(scanner.NameUserTranslation.Keys.Select(k => k.Length).Max(),
                Is.LessThanOrEqualTo(50), "no user name may exceed the forum's username limit");
            Assert.That(scanner.NameUserTranslation, Does.ContainKey("zokuru"));
            Assert.That(scanner.NameUserTranslation, Does.ContainKey("vani"));
            Assert.That(scanner.NameUserTranslation["vani"].Matches
                .Any(m => m.FirstUser == "zokuru" || m.SecondUser == "zokuru"),
                Is.True, "the pairing itself must still be recognized");
        }

        [Test]
        public void SyntheticScan_OverlongVsLineCreatesNoUsers()
        {
            const string url = "https://www.smogon.com/forums/threads/cap-test.5555/";
            var page = SyntheticPages.ThreadPage("Cap Test Tour", new[]
            {
                new SyntheticPages.Post
                {
                    Author = "Host",
                    UserId = 700,
                    DateTitle = "Jan 1, 2021 at 1:00 PM",
                    BodyLines = new List<string>
                    {
                        "Alice vs " + new string('x', 120),
                    },
                },
            });
            var fake = new FakeHttpMessageHandler();
            fake.Map(url, page);
            Common.HttpClient = new HttpClient(fake);

            var scanner = new SmogonThreadScanner();
            scanner.AnalyzeTopic(url, CancellationToken.None).Wait();

            Assert.That(scanner.NameUserTranslation.Keys, Is.EquivalentTo(new[] { "host" }));
        }

        [Test]
        public void SyntheticScan_ScoreSuffixAfterDashIsNotPartOfTheName()
        {
            const string url = "https://www.smogon.com/forums/threads/score-test.6666/";
            var page = SyntheticPages.ThreadPage("Score Test Tour", new[]
            {
                new SyntheticPages.Post
                {
                    Author = "Host",
                    UserId = 700,
                    DateTitle = "Jan 1, 2021 at 1:00 PM",
                    BodyLines = new List<string>
                    {
                        "Karl vs. Lena - 2-0",
                    },
                },
            });
            var fake = new FakeHttpMessageHandler();
            fake.Map(url, page);
            Common.HttpClient = new HttpClient(fake);

            var scanner = new SmogonThreadScanner();
            scanner.AnalyzeTopic(url, CancellationToken.None).Wait();

            Assert.That(scanner.NameUserTranslation.Keys, Is.EquivalentTo(new[] { "karl", "lena", "host" }));
        }

        [Test]
        public void UuOpenFixtureScan_ParsesPairingsWithBoldedWinners()
        {
            // Pairing lines mark the winner in <b> tags that XenForo also embeds inside
            // the mention's data-username attribute; those matches must still parse.
            const string url = "https://www.smogon.com/forums/threads/the-uu-open-xii-round-2-replays-mandatory-no-exceptions.3721437/";
            var fake = new FakeHttpMessageHandler();
            for (var page = 1; page <= 5; page++)
            {
                fake.Map(page == 1 ? url : url + "page-" + page, Fixture($"uu-open-xii-r2-page-{page}.html"));
            }
            Common.HttpClient = new HttpClient(fake);

            var scanner = new SmogonThreadScanner();
            var result = scanner.AnalyzeTopic(url, CancellationToken.None).Result;
            Assert.That(result, Is.Not.Null);

            // Zerkas vs TyCarter with TyCarter bolded as the winner.
            Assert.That(scanner.NameUserTranslation, Does.ContainKey("tycarter"));
            var tyCarter = scanner.NameUserTranslation["tycarter"];
            Assert.That(tyCarter.Matches, Is.Not.Empty);
            var pairing = tyCarter.Matches.Single(m => m.FirstUser == "zerkas" || m.SecondUser == "zerkas");
            Assert.That(pairing.Winner, Is.EqualTo("tycarter"));

            var playingUsers = scanner.Users.Where(user => !user.Matches.IsEmpty).ToList();
            Assert.That(playingUsers.Count(u => u.Matches.Any(m => m.Replays.Count > 0)),
                Is.GreaterThan(100));
        }

        #endregion

        #region Collector

        [Test]
        public void Collector_FindsSubforumsAndThreads_WithHttpsUrls()
        {
            var forum = new FakeForum();
            var section = new FakeForum.Forum { Title = "Tournaments", Path = "/forums/forums/tournaments.34/" };
            var alpha = new FakeForum.Forum { Title = "Tour Alpha", Path = "/forums/forums/tour-alpha.100/" };
            section.SubForums.Add(alpha);
            forum.Sections.Add(section);
            alpha.Threads.Add(new FakeForum.Thread { Id = 111, Slug = "alpha-round-1", Title = "Alpha Round 1", Pages = { BuildSyntheticPosts() } });
            var fake = new FakeHttpMessageHandler();
            forum.Serve(fake);

            var threads = new SmogonThreadCollector(new InMemoryDistributedCache(), new CollectorOptions { HttpClient = new HttpClient(fake) })
                .GetGeneralThreadsForForums("Tournaments").Result;

            Assert.That(threads.Keys, Is.EqualTo(new[]
            {
                "https://www.smogon.com/forums/forums/tournaments.34/",
                "https://www.smogon.com/forums/forums/tour-alpha.100/",
            }));
            Assert.That(threads["https://www.smogon.com/forums/forums/tour-alpha.100/"],
                Is.EqualTo(new[] { "https://www.smogon.com/forums/threads/alpha-round-1.111/" }));
            Assert.That(fake.Requests, Has.All.StartWith("https://"));
        }

        [Test]
        public void ForumIndexFixture_FindsTheScannedSectionsByTheirNodes()
        {
            // The forum index of Nov 8, 2026. The text marker ">Tournaments<" used to match a thread prefix first, which
            // pulled VGC, BSS, TCG and Bug Reports into the tournament forums; "Battle Stadium" no longer exists.
            var index = Fixture("forum-index-2026-11-08.html");

            var nodes = ForumPageParser.ParseNodes(index);
            var tournaments = nodes.Single(node => node.Title == "Tournaments");
            Assert.That(tournaments.Url, Is.EqualTo("https://www.smogon.com/forums/forums/tournaments.34/"));
            Assert.That(tournaments.LatestPostTimestamp, Is.EqualTo(1794154379));
            Assert.That(nodes.Single(node => node.Title == "Smogon Metagames").Url, Is.EqualTo("https://www.smogon.com/forums/forums/smogon-metagames.725/"));
            Assert.That(nodes.Single(node => node.Title == "Ruins of Alph").LatestPostTimestamp, Is.GreaterThan(0));
            Assert.That(nodes.Select(node => node.Title), Does.Contain("Pokémon Showdown!"), "HTML entities are decoded");

            var links = ForumPageParser.ParseSubNodeLinks(index);
            Assert.That(links.Single(link => link.Title == "PS! Tournaments").Url, Is.EqualTo(SmogonThreadCollector.PsTournamentsUrl));
            Assert.That(links.Single(link => link.Title == "Battle Stadium Singles").Url,
                Is.EqualTo("https://www.smogon.com/forums/forums/battle-stadium-singles.532/"));
            Assert.That(links.Select(link => link.Url), Has.None.Contains("/link-forums/"), "link forums are skipped");
        }

        [Test]
        public void ForumListingFixture_ParsesThreadsAndSubforums()
        {
            var listing = Fixture("tournaments-listing-2026-11-08-page-1.html");

            var subForums = ForumPageParser.ParseNodes(listing);
            Assert.That(subForums, Has.Count.EqualTo(12));
            var circuits = subForums.Single(node => node.Title == "Circuit Tournaments");
            Assert.That(circuits.Url, Is.EqualTo("https://www.smogon.com/forums/forums/circuit-tournaments.351/"));
            Assert.That(circuits.LatestPostTimestamp, Is.EqualTo(1794167746));

            var threads = ForumPageParser.ParseThreads(listing);
            Assert.That(threads, Has.Count.EqualTo(52), "53 items, one of them a moved-thread redirect");
            Assert.That(threads.TakeWhile(thread => thread.Sticky).Count(), Is.EqualTo(13));
            var first = threads[0];
            Assert.That(first.ThreadId, Is.EqualTo(3788984));
            Assert.That(first.Url, Is.EqualTo("https://www.smogon.com/forums/threads/programming-note-for-hosts-images-in-tournament-ops.3788984/"));
            Assert.That(first.Title, Is.EqualTo("Programming Note for Hosts: Images in Tournament OPs"));
            Assert.That(first.Locked, Is.True);
            Assert.That(first.LatestPostTimestamp, Is.EqualTo(1790547298));
            Assert.That(first.LatestPostDisplayDate, Is.EqualTo("Sep 27, 2026 at 6:14 PM"));
            Assert.That(threads.Select(thread => thread.Title), Has.None.Contains("&#039;").And.None.Contains("&amp;"));
            Assert.That(threads.Where(thread => !thread.Sticky).Select(thread => thread.LatestPostTimestamp),
                Is.Ordered.Descending, "listings are sorted by latest post");
            Assert.That(threads.Max(thread => thread.LastPage), Is.GreaterThan(1), "page jumps give the last page");
        }

        [Test]
        public void ExtractImportantLines_KeepsTheSameLinesAsAFullScan()
        {
            // The crawl state stores these lines per page; parsing them must give what parsing the whole thread gives.
            var scanner = ScanFixtureThread(out _, out var result);
            for (var page = 1; page <= 5; page++)
            {
                Assert.That(
                    SmogonThreadScanner.ExtractImportantLines(FixtureThreadUrl, page, Fixture($"ost-xvii-finals-page-{page}.html")),
                    Is.EqualTo(result.CollectedLinks[page - 1]), $"page {page}");
            }

            const string uuUrl = "https://www.smogon.com/forums/threads/the-uu-open-xii-round-2-replays-mandatory-no-exceptions.3721437/";
            var fake = new FakeHttpMessageHandler();
            for (var page = 1; page <= 5; page++)
            {
                fake.Map(page == 1 ? uuUrl : uuUrl + "page-" + page, Fixture($"uu-open-xii-r2-page-{page}.html"));
            }
            Common.HttpClient = new HttpClient(fake);
            var uuResult = new SmogonThreadScanner().AnalyzeTopic(uuUrl, CancellationToken.None).Result!;
            for (var page = 1; page <= 5; page++)
            {
                Assert.That(
                    SmogonThreadScanner.ExtractImportantLines(uuUrl, page, Fixture($"uu-open-xii-r2-page-{page}.html")),
                    Is.EqualTo(uuResult.CollectedLinks[page - 1]), $"UU Open page {page}");
            }
        }

        #endregion

        #region HTTP configuration

        [Test]
        public void Common_OfficialTournamentSite_UsesHttps()
        {
            Assert.That(Common.OfficialTournamentSite, Does.StartWith("https://"));
        }

        [Test]
        public void PoliteHttpMessageHandler_EnablesAutomaticDecompression()
        {
            using var handler = PoliteHttpMessageHandler.CreateDefaultInnerHandler();
            Assert.That(handler.AutomaticDecompression, Is.EqualTo(System.Net.DecompressionMethods.All));
        }

        [Test]
        public void SmogonPages_BotCheckIsRecognized()
        {
            Assert.That(SmogonPages.IsBotCheck("<p id=\"m\">Checking your browser...</p><script>document.cookie = \"xf_smogon_gate=\" + n</script>"), Is.True);
            Assert.That(SmogonPages.IsBotCheck(Fixture("ost-xvii-finals-page-1.html")), Is.False);
            Assert.That(SmogonPages.IsBotCheck(Fixture("ost-xvii-finals-page-1.html").Replace("</article>", "my browser is stuck at Checking your browser, cookie xf_smogon_gate</article>")),
                Is.False, "a post about the gate is no bot check");
        }

        [Test]
        public void SmogonPages_ThreadIdsAndPageUrls()
        {
            Assert.That(SmogonPages.TryGetThreadId("https://www.smogon.com/forums/threads/spl-xiii-replays.3695657/", out var id), Is.True);
            Assert.That(id, Is.EqualTo(3695657));
            Assert.That(SmogonPages.TryGetThreadId("https://www.smogon.com/forums/threads/3695657/", out id), Is.True);
            Assert.That(id, Is.EqualTo(3695657));
            Assert.That(SmogonPages.TryGetThreadId("https://www.smogon.com/forums/forums/tournaments.34/", out _), Is.False);
            Assert.That(SmogonPages.PageUrl("https://www.smogon.com/forums/threads/a.1/", 1), Is.EqualTo("https://www.smogon.com/forums/threads/a.1/"));
            Assert.That(SmogonPages.PageUrl("https://www.smogon.com/forums/threads/a.1/", 3), Is.EqualTo("https://www.smogon.com/forums/threads/a.1/page-3"));
        }

        [Test]
        public void Common_EnumerateLines_MatchesStringSplitExactly()
        {
            foreach (var text in new[] { "", "a", "a\nb", "a\n", "\n", "a\n\nb", "line1\nline2\n" })
            {
                Assert.That(Common.EnumerateLines(text), Is.EqualTo(text.Split('\n')), $"input: '{text}'");
            }
        }

        [Test]
        public void Common_SanitizeFileName_ReplacesInvalidCharacters()
        {
            Assert.That(Common.SanitizeFileName("alice"), Is.EqualTo("alice"));
            Assert.That(Common.SanitizeFileName("sacri'"), Is.EqualTo("sacri'"));
            Assert.That(Common.SanitizeFileName("a<b>c:d\"e/f\\g|h?i*j"), Is.EqualTo("a_b_c_d_e_f_g_h_i_j"));
            Assert.That(Common.SanitizeFileName("tab\tandcontrol"), Is.EqualTo("tab_andcontrol"));
            Assert.That(Common.SanitizeFileName(""), Is.EqualTo("_"));
        }

        [Test]
        public void Common_SanitizeFileName_CapsOverlongNamesDeterministically()
        {
            // Garbage or pathological names must never produce a file name exceeding OS limits.
            var longNameA = new string('a', 300);
            var longNameB = new string('a', 299) + "b";

            var sanitizedA = Common.SanitizeFileName(longNameA);
            Assert.That(sanitizedA.Length, Is.LessThanOrEqualTo(100));
            Assert.That(sanitizedA, Does.StartWith("aaaa"));
            Assert.That(Common.SanitizeFileName(longNameA), Is.EqualTo(sanitizedA), "must be deterministic");
            Assert.That(Common.SanitizeFileName(longNameB), Is.Not.EqualTo(sanitizedA),
                "different names must not collide after truncation");
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
