#nullable enable
using Newtonsoft.Json;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using TournamentParser.Core.Data;
using TournamentParser.Data;
using TournamentParser.Parser;
using TournamentParser.Tests.TestSupport;
using TournamentParser.ThreadCollector;
using TournamentParser.ThreadScanner;
using TournamentParser.Util;

namespace TournamentParser.Tests
{
    /// <summary>Offline tests of the incremental crawl against a <see cref="FakeForum"/>.</summary>
    [TestFixture]
    public class IncrementalCrawlTests
    {
        private const string Tournaments = "/forums/forums/tournaments.34/";
        private const string TourAlpha = "/forums/forums/tour-alpha.100/";
        private const string TourBeta = "/forums/forums/tour-beta.101/";

        private static readonly DateTime OldStart = new(2021, 3, 1, 12, 0, 0);

        private FakeForum _forum = null!;
        private FakeHttpMessageHandler _http = null!;
        private InMemoryDistributedCache _cache = null!;

        [SetUp]
        public void SetUp()
        {
            _forum = new FakeForum();
            _http = new FakeHttpMessageHandler();
            _cache = new InMemoryDistributedCache();
        }

        #region Helpers

        /// <summary>
        /// A tournament thread: page 1 has three pairings (Ann won, Cat vs Dan gets replays on later pages, Eve vs Fay stays open),
        /// every later page one replay post by Cat.
        /// </summary>
        private static FakeForum.Thread TourThread(long id, int pages, DateTime start, string? title = null, string? slug = null)
        {
            var thread = new FakeForum.Thread { Id = id, Slug = slug ?? "tour-" + id, Title = title ?? $"Tour {id} - Round 1" };
            var date = start;
            string NextDate()
            {
                date = date.AddHours(1);
                return FakeForum.ToDateTitle(date);
            }
            thread.Pages.Add(new List<SyntheticPages.Post>
            {
                new()
                {
                    Author = "Host" + id,
                    UserId = (int)id * 10,
                    DateTitle = NextDate(),
                    BodyLines = new List<string> { $"<b>Ann{id}</b> vs. Ben{id}", $"Cat{id} vs. Dan{id}", $"Eve{id} vs. Fay{id}" },
                },
            });
            for (var page = 2; page <= pages; page++)
            {
                thread.Pages.Add(new List<SyntheticPages.Post> { ReplayPost(id, "Cat", page, NextDate()) });
            }
            return thread;
        }

        /// <summary>A discussion thread: no pairings in posts 1 and 2, one chat post on every later page.</summary>
        private static FakeForum.Thread ChatThread(long id, int pages, DateTime start)
        {
            var thread = new FakeForum.Thread { Id = id, Slug = "discussion-" + id, Title = $"Discussion {id}" };
            var date = start;
            string NextDate()
            {
                date = date.AddHours(1);
                return FakeForum.ToDateTitle(date);
            }
            thread.Pages.Add(new List<SyntheticPages.Post> { ChatPost(id, "Host", NextDate()), ChatPost(id, "Gil", NextDate()) });
            for (var page = 2; page <= pages; page++)
            {
                thread.Pages.Add(new List<SyntheticPages.Post> { ChatPost(id, "Gil", NextDate()) });
            }
            return thread;
        }

        private static SyntheticPages.Post ChatPost(long threadId, string author, string dateTitle) => new()
        {
            Author = author + threadId,
            UserId = (int)threadId * 10 + (author == "Host" ? 6 : 7),
            DateTitle = dateTitle,
            BodyLines = new List<string> { "good luck everyone" },
        };

        private static SyntheticPages.Post ReplayPost(long threadId, string player, int number, string dateTitle) => new()
        {
            Author = player + threadId,
            UserId = (int)threadId * 10 + 3,
            DateTitle = dateTitle,
            BodyLines = new List<string>
            {
                $"gg Dan{threadId}",
                $"<a href=\"https://replay.pokemonshowdown.com/gen9ou-{threadId}{number:000}\">replay</a>",
            },
        };

        /// <summary>
        /// A new post is always the newest post of the whole forum, and recent: the replay search of the next run reaches back to
        /// the day before the last run.
        /// </summary>
        private string Newest()
        {
            var latest = DateTimeOffset.FromUnixTimeSeconds(_forum.AllForums.Max(forum => forum.LatestPostTimestamp)).UtcDateTime;
            var recent = DateTime.UtcNow.AddHours(-3);
            return FakeForum.ToDateTitle((latest > recent ? latest : recent).AddMinutes(1));
        }

        /// <summary>The replay search of a run after one today, over the forums with the given node IDs (and their subforums).</summary>
        private static string SearchUrl(params long[] nodeIds) =>
            PostSearch.Url(PostSearch.ReplayQuery, PostSearch.Filter.Forums(nodeIds), DateTime.UtcNow.Date.AddDays(-1), null);

        /// <summary>The replay search of a run in the Tournaments section.</summary>
        private static readonly string TournamentsSearch = SearchUrl(34);

        private FakeForum.Forum Section(string title, string path)
        {
            var forum = new FakeForum.Forum { Title = title, Path = path };
            _forum.Sections.Add(forum);
            return forum;
        }

        private static FakeForum.Forum Sub(FakeForum.Forum parent, string title, string path)
        {
            var forum = new FakeForum.Forum { Title = title, Path = path };
            parent.SubForums.Add(forum);
            return forum;
        }

        private CollectorOptions Options(Action<CollectorOptions>? configure = null)
        {
            var options = new CollectorOptions { HttpClient = new HttpClient(_http) };
            configure?.Invoke(options);
            return options;
        }

        /// <summary>One run of the collector over the current state of the fake forum.</summary>
        private IDictionary<string, List<string>> Crawl(string section = "Tournaments", CollectorOptions? options = null)
        {
            _forum.Serve(_http);
            _http.ClearRequests();
            return new SmogonThreadCollector(_cache, options ?? Options()).GetGeneralThreadsForForums(section).Result;
        }

        private SmogonThreadScanner Scan(IDictionary<string, List<string>> threads)
        {
            var scanner = new SmogonThreadScanner(_cache);
            scanner.ScanThreads(threads).Wait();
            return scanner;
        }

        /// <summary>Everything the output contains about users and matches, in a comparable form.</summary>
        private static List<string> Summary(SmogonThreadScanner scanner) => scanner.NameUserTranslation
            .OrderBy(kv => kv.Key)
            .Select(kv => kv.Key + ": " + string.Join(" | ", kv.Value.Matches
                .Select(m => $"{m.FirstUser} vs {m.SecondUser} w={m.Winner} f={m.Finished} r={string.Join(",", m.Replays.OrderBy(r => r))} t={m.Thread?.Name}")
                .OrderBy(s => s)))
            .ToList();

        /// <summary>The users with matches and their matches; posters without matches may be missing since 3.0.</summary>
        private static List<string> MatchSummary(SmogonThreadScanner scanner) => Summary(scanner).Where(line => !line.EndsWith(": ")).ToList();

        /// <summary>
        /// The <see cref="MatchSummary"/> of a scan that reads every page of every thread, as 2.x did: the collector takes all
        /// pages over from 2.x entries.
        /// </summary>
        private List<string> CompleteMatchSummary(string section = "Tournaments")
        {
            var http = new FakeHttpMessageHandler();
            _forum.Serve(http);
            var cache = new InMemoryDistributedCache();
            foreach (var thread in _forum.AllForums.SelectMany(forum => forum.Threads))
            {
                StoreLegacyEntry(thread, cache: cache);
            }
            var threads = new SmogonThreadCollector(cache, new CollectorOptions { HttpClient = new HttpClient(http), UseSearch = false })
                .GetGeneralThreadsForForums(section).Result;
            Assert.That(http.Requests, Has.None.Contains("/threads/"), "every page comes from the 2.x entries");
            var scanner = new SmogonThreadScanner(cache);
            scanner.ScanThreads(threads).Wait();
            return MatchSummary(scanner);
        }

        /// <summary>A scan of the same forum without any stored state, for comparison.</summary>
        private List<string> FreshSummary()
        {
            var http = new FakeHttpMessageHandler();
            _forum.Serve(http);
            var cache = new InMemoryDistributedCache();
            var threads = new SmogonThreadCollector(cache, new CollectorOptions { HttpClient = new HttpClient(http) })
                .GetGeneralThreadsForForums("Tournaments").Result;
            var scanner = new SmogonThreadScanner(cache);
            scanner.ScanThreads(threads).Wait();
            return Summary(scanner);
        }

        private string[] Requests => _http.Requests.ToArray();

        private static string Url(string path) => FakeForum.BaseUrl + path;

        /// <summary>Stores a thread the way version 2.x did: the TopicAnalyzeResult under the thread URL.</summary>
        private void StoreLegacyEntry(FakeForum.Thread thread, Func<TopicAnalyzeResult, TopicAnalyzeResult>? change = null, string? key = null,
            InMemoryDistributedCache? cache = null)
        {
            var http = new FakeHttpMessageHandler();
            for (var page = 1; page <= thread.Pages.Count; page++)
            {
                http.Map(thread.PageUrl(page), FakeForum.ThreadPage(thread, page));
            }
            var original = Common.HttpClient;
            Common.HttpClient = new HttpClient(http);
            try
            {
                var result = new SmogonThreadScanner().AnalyzeTopic(thread.Url, CancellationToken.None).Result!;
                result = change?.Invoke(result) ?? result;
                (cache ?? _cache).Store[key ?? thread.Url] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(result));
            }
            finally
            {
                Common.HttpClient = original;
            }
        }

        #endregion

        [Test]
        public void FirstRunReadsEverything_SecondRunWithoutChangesOnlyTheIndex()
        {
            var section = Section("Tournaments", Tournaments);
            var alpha = Sub(section, "Tour Alpha", TourAlpha);
            var t1 = TourThread(1, 3, OldStart);
            var t2 = TourThread(2, 1, OldStart.AddDays(1));
            alpha.Threads.Add(t1);
            section.Threads.Add(t2);

            var first = Crawl();
            Assert.That(Requests, Is.EqualTo(new[]
            {
                FakeForum.IndexUrl,
                Url(Tournaments),
                t2.Url,
                Url(TourAlpha),
                t1.Url, t1.Url + "page-2", t1.Url + "page-3",
            }), "page 1 is requested without the page-1 suffix, which would redirect");
            var firstSummary = Summary(Scan(first));

            var second = Crawl();
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl }));
            Assert.That(second, Is.EqualTo(first));
            Assert.That(Summary(Scan(second)), Is.EqualTo(firstSummary));
            Assert.That(firstSummary, Is.EqualTo(FreshSummary()));
            Assert.That(firstSummary, Has.Some.StartsWith("ann1: ann1 vs ben1 w=ann1"));
        }

        [Test]
        public void NewPost_ReadsPageOneAndTheLastPage()
        {
            var section = Section("Tournaments", Tournaments);
            var alpha = Sub(section, "Tour Alpha", TourAlpha);
            var t1 = TourThread(1, 3, OldStart);
            alpha.Threads.Add(t1);
            section.Threads.Add(TourThread(2, 1, OldStart.AddDays(1)));
            Crawl();

            t1.Pages[2].Add(ReplayPost(1, "Cat", 99, Newest()));
            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[]
            {
                FakeForum.IndexUrl,
                TournamentsSearch,
                Url(Tournaments),
                Url(TourAlpha),
                t1.Url, t1.Url + "page-3",
            }));
            var summary = Summary(Scan(threads));
            Assert.That(summary, Has.Some.Contains("gen9ou-1099"));
            Assert.That(summary, Is.EqualTo(FreshSummary()));
        }

        [Test]
        public void NewPage_ReadsPageOneAndThePageWithTheNewReplayPost()
        {
            var section = Section("Tournaments", Tournaments);
            var t1 = TourThread(1, 3, OldStart);
            section.Threads.Add(t1);
            Crawl();

            t1.Pages.Add(new List<SyntheticPages.Post> { ReplayPost(1, "Cat", 4, Newest()) });
            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[]
            {
                FakeForum.IndexUrl,
                TournamentsSearch,
                Url(Tournaments),
                t1.Url, t1.Url + "page-4",
            }), "the search shows that page 3 got no replay post");
            Assert.That(Summary(Scan(threads)), Is.EqualTo(FreshSummary()));
        }

        [Test]
        public void NewPage_WithoutSearch_ReadsPageOneTheLastKnownPageAndTheNewPage()
        {
            var section = Section("Tournaments", Tournaments);
            var t1 = TourThread(1, 3, OldStart);
            section.Threads.Add(t1);
            var options = Options(o => o.UseSearch = false);
            Crawl(options: options);

            t1.Pages.Add(new List<SyntheticPages.Post> { ReplayPost(1, "Cat", 4, Newest()) });
            var threads = Crawl(options: options);

            Assert.That(Requests, Is.EqualTo(new[]
            {
                FakeForum.IndexUrl,
                Url(Tournaments),
                t1.Url, t1.Url + "page-3", t1.Url + "page-4",
            }));
            Assert.That(Summary(Scan(threads)), Is.EqualTo(FreshSummary()));
        }

        [Test]
        public void DeletedPosts_DropThePagesThatNoLongerExist()
        {
            var section = Section("Tournaments", Tournaments);
            var t1 = TourThread(1, 3, OldStart);
            section.Threads.Add(t1);
            Crawl();

            var newest = Newest();
            t1.Pages.RemoveAt(2);
            t1.Pages[1].Add(ReplayPost(1, "Cat", 50, newest));
            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), t1.Url, t1.Url + "page-2" }));
            Assert.That(_cache.Store.Keys, Has.None.EqualTo("tp:v3:page:1:3"));
            Assert.That(Summary(Scan(threads)), Is.EqualTo(FreshSummary()));
        }

        [Test]
        public void UnchangedSubforum_IsNotRequested()
        {
            var section = Section("Tournaments", Tournaments);
            var alpha = Sub(section, "Tour Alpha", TourAlpha);
            var beta = Sub(section, "Tour Beta", TourBeta);
            var t1 = TourThread(1, 1, OldStart);
            alpha.Threads.Add(t1);
            beta.Threads.Add(TourThread(2, 1, OldStart.AddDays(1)));
            Crawl();

            t1.Pages[0].Add(ReplayPost(1, "Cat", 2, Newest()));
            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), Url(TourAlpha), t1.Url }));
            Assert.That(threads.Keys, Is.EquivalentTo(new[] { Url(Tournaments), Url(TourAlpha), Url(TourBeta) }),
                "the skipped subforum still contributes its stored threads");
            Assert.That(Summary(Scan(threads)), Is.EqualTo(FreshSummary()));
        }

        [Test]
        public void ListingWalk_StopsAtTheFirstUnchangedThread()
        {
            var section = Section("Tournaments", Tournaments);
            var threads = Enumerable.Range(1, 9).Select(id => TourThread(id, 1, OldStart.AddDays(id))).ToList();
            section.Threads.AddRange(threads);
            Crawl();
            Assert.That(Requests.Count(r => r.StartsWith(Url(Tournaments))), Is.EqualTo(3), "first run walks all three listing pages");

            // The oldest thread gets a post and moves to the top of page 1.
            threads[0].Pages[0].Add(ReplayPost(1, "Cat", 2, Newest()));
            Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), threads[0].Url }));
        }

        [Test]
        public void LockWithoutNewPost_IsNoticedForRecentlyActiveThreads()
        {
            var section = Section("Tournaments", Tournaments);
            var start = DateTime.UtcNow.AddDays(-2);
            var thread = TourThread(1, 2, new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0));
            section.Threads.Add(thread);
            var before = Summary(Scan(Crawl()));
            Assert.That(before, Has.Some.StartsWith("eve1: eve1 vs fay1 w= f=False"), "pairing without result is open");

            thread.Locked = true;
            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), thread.Url }),
                "page 1 shows the lock; the search found no new replay posts");
            var after = Summary(Scan(threads));
            Assert.That(after, Has.Some.StartsWith("eve1: eve1 vs fay1 w= f=True"), "matches of a locked thread are finished");
            Assert.That(after, Is.EqualTo(FreshSummary()));
        }

        [Test]
        public void TitleChangeWithoutNewPost_IsNoticedForRecentlyActiveThreads()
        {
            var section = Section("Tournaments", Tournaments);
            var start = DateTime.UtcNow.AddDays(-2);
            var thread = TourThread(1, 2, new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0));
            section.Threads.Add(thread);
            Crawl();

            thread.Title = "Tour 1 - Finals [Won by Ann1]";
            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), thread.Url }));
            var summary = Summary(Scan(threads));
            Assert.That(summary, Has.Some.Contains("t=Tour 1 - Finals [Won by Ann1]"));
            Assert.That(summary, Has.None.Contains("t=Tour 1 - Round 1"), "the stored page 2 still shows the old title");
            Assert.That(summary, Is.EqualTo(FreshSummary()));
        }

        [Test]
        public void WithoutRecentActivityWindow_AnUnchangedForumIsSkippedEvenIfActive()
        {
            var section = Section("Tournaments", Tournaments);
            var start = DateTime.UtcNow.AddDays(-2);
            section.Threads.Add(TourThread(1, 1, new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0)));
            Crawl();

            Crawl(options: Options(o => o.RecentActivityWindow = TimeSpan.Zero));
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl }));

            Crawl();
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments) }),
                "with the default window, the recently active forum is listed again");
        }

        [Test]
        public void QuietForum_IsListedOnlyWhileItHasOpenTournamentThreads()
        {
            var section = Section("Tournaments", Tournaments);
            var alpha = Sub(section, "Tour Alpha", TourAlpha);
            var start = DateTime.UtcNow.AddDays(-2);
            var recent = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0);
            var tour = TourThread(1, 1, recent);
            alpha.Threads.Add(tour);
            section.Threads.Add(ChatThread(2, 1, recent));
            Crawl();

            Crawl();
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), Url(TourAlpha) }),
                "the open tournament thread may still be locked or renamed");

            tour.Locked = true;
            Crawl();
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), Url(TourAlpha), tour.Url }));

            Crawl();
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl }), "a discussion thread and a locked tournament thread need no checks");
        }

        [Test]
        public void MovedThread_IsReturnedOnceUnderItsNewForum()
        {
            var section = Section("Tournaments", Tournaments);
            var alpha = Sub(section, "Tour Alpha", TourAlpha);
            var beta = Sub(section, "Tour Beta", TourBeta);
            var moved = TourThread(1, 1, OldStart);
            alpha.Threads.Add(moved);
            alpha.Threads.Add(TourThread(2, 1, OldStart.AddDays(-1)));
            beta.Threads.Add(TourThread(3, 1, OldStart.AddDays(-2)));
            Crawl();

            alpha.Threads.Remove(moved);
            beta.Threads.Add(moved);
            beta.Threads.Add(TourThread(4, 1, OldStart.AddDays(5)));
            var threads = Crawl();

            Assert.That(threads[Url(TourAlpha)], Has.None.EqualTo(moved.Url));
            Assert.That(threads[Url(TourBeta)], Has.One.EqualTo(moved.Url));
            Assert.That(threads.Values.SelectMany(urls => urls).Count(url => url == moved.Url), Is.EqualTo(1));
        }

        [Test]
        public void BotCheck_StopsTheCrawl_AndTheNextRunReadsTheThread()
        {
            var section = Section("Tournaments", Tournaments);
            var thread = TourThread(1, 2, OldStart);
            section.Threads.Add(thread);
            _forum.Serve(_http);
            _http.Map(thread.Url + "page-2", "<html><body><p id=\"m\">Checking your browser...</p><script>document.cookie = \"xf_smogon_gate=1\"</script></body></html>");

            var collector = new SmogonThreadCollector(_cache, Options());
            var error = Assert.Throws<AggregateException>(() => collector.GetGeneralThreadsForForums("Tournaments").Wait())!;
            Assert.That(error.InnerException, Is.InstanceOf<SmogonAccessBlockedException>());

            var threads = Crawl();
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, Url(Tournaments), thread.Url, thread.Url + "page-2" }));
            Assert.That(threads[Url(Tournaments)], Is.EqualTo(new[] { thread.Url }));
        }

        [Test]
        public void FailedThread_IsRetriedInTheNextRun()
        {
            var section = Section("Tournaments", Tournaments);
            var thread = TourThread(1, 1, OldStart);
            section.Threads.Add(thread);
            section.Threads.Add(TourThread(2, 1, OldStart.AddDays(1)));
            _forum.Serve(_http);
            _http.MapStatus(thread.Url, HttpStatusCode.InternalServerError);
            var first = new SmogonThreadCollector(_cache, Options()).GetGeneralThreadsForForums("Tournaments").Result;
            Assert.That(first[Url(Tournaments)], Has.None.EqualTo(thread.Url));

            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), thread.Url }));
            Assert.That(threads[Url(Tournaments)], Has.One.EqualTo(thread.Url));
        }

        [Test]
        public void RequestBudget_StopsTheCrawl_AndTheNextRunContinues()
        {
            var section = Section("Tournaments", Tournaments);
            var thread = TourThread(1, 3, OldStart);
            section.Threads.Add(thread);
            _forum.Serve(_http);

            var collector = new SmogonThreadCollector(_cache, Options(o => o.MaxRequests = 3));
            var error = Assert.Throws<AggregateException>(() => collector.GetGeneralThreadsForForums("Tournaments").Wait())!;
            Assert.That(error.InnerException, Is.InstanceOf<RequestBudgetExhaustedException>());
            Assert.That(_http.Requests, Has.Count.EqualTo(3));

            var threads = Crawl();
            Assert.That(threads[Url(Tournaments)], Is.EqualTo(new[] { thread.Url }));
        }

        [Test]
        public void UnknownForum_StopsInsteadOfDroppingItsThreads()
        {
            Section("Tournaments", Tournaments);
            _forum.Serve(_http);

            var collector = new SmogonThreadCollector(_cache, Options());
            var error = Assert.Throws<AggregateException>(() => collector.GetGeneralThreadsForForums("Battle Stadium").Wait())!;
            Assert.That(error.InnerException, Is.InstanceOf<UnexpectedPageException>());
        }

        [Test]
        public void ForumListingInsteadOfIndex_StopsTheCrawl()
        {
            Section("Tournaments", Tournaments);
            _forum.Serve(_http);
            _http.Map(FakeForum.IndexUrl, "<html><body>Something went wrong</body></html>");

            var collector = new SmogonThreadCollector(_cache, Options());
            var error = Assert.Throws<AggregateException>(() => collector.GetGeneralThreadsForForums("Tournaments").Wait())!;
            Assert.That(error.InnerException, Is.InstanceOf<UnexpectedPageException>());
        }

        #region Relevance and search

        private static string ThreadSearchUrl(long threadId) => PostSearch.Url(PostSearch.ReplayQuery, PostSearch.Filter.Thread(threadId), null, null);

        private ThreadCrawlState StoredThread(long threadId) =>
            JsonConvert.DeserializeObject<ThreadCrawlState>(Encoding.UTF8.GetString(_cache.Store[$"tp:v3:thread:{threadId}"]))!;

        [Test]
        public void ThreadWithoutPairings_OnlyPageOneIsRead()
        {
            var section = Section("Tournaments", Tournaments);
            var chat = ChatThread(1, 3, OldStart);
            chat.Pages[2].Add(ReplayPost(1, "Cat", 1, FakeForum.ToDateTitle(OldStart.AddDays(1))));
            section.Threads.Add(chat);
            section.Threads.Add(TourThread(2, 2, OldStart));

            var threads = Crawl();

            Assert.That(Requests, Has.Member(chat.Url).And.None.StartsWith(chat.Url + "page-"));
            Assert.That(StoredThread(1).HasPairings, Is.False);
            Assert.That(threads[Url(Tournaments)], Has.Member(chat.Url));
            Assert.That(MatchSummary(Scan(threads)), Is.EqualTo(CompleteMatchSummary()), "a replay post without pairings is no match");

            Crawl();
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl }), "the other pages do not count as missing");
        }

        [Test]
        public void ThreadWithoutPairings_IsReadAgainOnlyForReplayPosts()
        {
            var section = Section("Tournaments", Tournaments);
            var chat = ChatThread(1, 3, OldStart);
            section.Threads.Add(chat);
            Crawl();

            chat.Pages[2].Add(ChatPost(1, "Gil", Newest()));
            var threads = Crawl();
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments) }));
            Assert.That(threads[Url(Tournaments)], Is.EqualTo(new[] { chat.Url }));

            chat.Pages[2].Add(ReplayPost(1, "Cat", 1, Newest()));
            Crawl();
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), chat.Url }),
                "the host may have added pairings to the first post");
        }

        [Test]
        public void ThreadWithoutPairings_TitleChange_IsNoticedWithTheNextPostInItsForum()
        {
            var section = Section("Tournaments", Tournaments);
            var start = DateTime.UtcNow.AddDays(-2);
            var recent = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0);
            var chat = ChatThread(1, 3, recent);
            var other = ChatThread(2, 1, recent);
            section.Threads.AddRange(new[] { chat, other });
            Crawl();

            chat.Title = "Discussion 1 - Round 1";
            Crawl();
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl }), "a forum without open tournament threads is not listed for title changes");

            other.Pages[0].Add(ChatPost(2, "Gil", Newest()));
            Crawl();
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), chat.Url }),
                "the host may have turned the thread into a tournament");
        }

        [Test]
        public void ThreadWithoutPairings_GetsPairings_ReadsTheOtherPages()
        {
            var section = Section("Tournaments", Tournaments);
            var chat = ChatThread(1, 3, OldStart);
            section.Threads.Add(chat);
            Crawl();

            // The host edits the pairings into the first post, and a player posts a replay.
            chat.Pages[0][0].BodyLines.Add("<b>Cat1</b> vs. Dan1");
            chat.Pages[2].Add(ReplayPost(1, "Cat", 1, Newest()));
            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), chat.Url, chat.Url + "page-2", chat.Url + "page-3" }));
            var summary = MatchSummary(Scan(threads));
            Assert.That(summary, Has.Some.Contains("gen9ou-1001"));
            Assert.That(summary, Is.EqualTo(CompleteMatchSummary()));
        }

        [Test]
        public void ThreadWithPairings_ReadsOnlyTheNewPagesWithReplayPosts()
        {
            var section = Section("Tournaments", Tournaments);
            var tour = TourThread(1, 2, OldStart);
            section.Threads.Add(tour);
            Crawl();

            tour.Pages.Add(new List<SyntheticPages.Post> { ChatPost(1, "Gil", Newest()) });
            tour.Pages.Add(new List<SyntheticPages.Post> { ReplayPost(1, "Cat", 4, Newest()) });
            tour.Pages.Add(new List<SyntheticPages.Post> { ChatPost(1, "Gil", Newest()) });
            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), tour.Url, tour.Url + "page-4" }));
            Assert.That(StoredThread(1).SkippedPages, Is.EqualTo(new[] { 3, 5 }));
            Assert.That(MatchSummary(Scan(threads)), Is.EqualTo(CompleteMatchSummary()));

            tour.Pages[4].Add(ChatPost(1, "Gil", Newest()));
            threads = Crawl();
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments) }), "no replay post, no read");
            Assert.That(MatchSummary(Scan(threads)), Is.EqualTo(CompleteMatchSummary()));
        }

        [Test]
        public void ThreadWithPairings_WinnerBoldedWithoutReplay_ShowsUpAtTheLock()
        {
            var section = Section("Tournaments", Tournaments);
            var start = DateTime.UtcNow.AddDays(-2);
            var tour = TourThread(1, 1, new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0));
            section.Threads.Add(tour);
            Crawl();

            tour.Pages[0][0].BodyLines[2] = "<b>Eve1</b> vs. Fay1";
            tour.Pages[0].Add(ChatPost(1, "Gil", Newest()));
            var threads = Crawl();
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments) }));
            Assert.That(MatchSummary(Scan(threads)), Has.Some.StartsWith("eve1: eve1 vs fay1 w= "), "the edit is not seen yet");

            tour.Locked = true;
            threads = Crawl();
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), tour.Url }));
            var summary = MatchSummary(Scan(threads));
            Assert.That(summary, Has.Some.StartsWith("eve1: eve1 vs fay1 w=eve1 f=True"));
            Assert.That(summary, Is.EqualTo(CompleteMatchSummary()));
        }

        [Test]
        public void ThreadWithPairings_ThatGotShorter_IsRead()
        {
            var section = Section("Tournaments", Tournaments);
            var tour = TourThread(1, 3, OldStart);
            section.Threads.Add(tour);
            Crawl();

            tour.Pages.RemoveAt(2);
            tour.Pages[1].Add(ChatPost(1, "Gil", Newest()));
            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), tour.Url }));
            Assert.That(_cache.Store.Keys, Has.None.EqualTo("tp:v3:page:1:3"));
            Assert.That(MatchSummary(Scan(threads)), Is.EqualTo(CompleteMatchSummary()));
        }

        [Test]
        public void ReplayThread_ReadsEveryNewPage()
        {
            // In a replay thread, pairings in any post count, with or without replays.
            var section = Section("Tournaments", Tournaments);
            var replays = TourThread(1, 2, OldStart, slug: "tour-1-replays");
            section.Threads.Add(replays);
            Crawl();

            replays.Pages.Add(new List<SyntheticPages.Post>
            {
                new() { Author = "Host1", UserId = 10, DateTitle = Newest(), BodyLines = new List<string> { "<b>Gus1</b> vs. Hal1" } },
            });
            replays.Pages.Add(new List<SyntheticPages.Post> { ReplayPost(1, "Cat", 4, Newest()) });
            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[]
            {
                FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), replays.Url, replays.Url + "page-2", replays.Url + "page-3", replays.Url + "page-4",
            }));
            var summary = MatchSummary(Scan(threads));
            Assert.That(summary, Has.Some.StartsWith("gus1: gus1 vs hal1 w=gus1"));
            Assert.That(summary, Is.EqualTo(CompleteMatchSummary()));
        }

        [Test]
        public void NewLongThread_SearchesForItsReplayPostsFirst()
        {
            var section = Section("Tournaments", Tournaments);
            var tour = TourThread(1, 1, OldStart);
            var date = OldStart.AddDays(1);
            for (var page = 2; page <= 12; page++)
            {
                date = date.AddHours(1);
                tour.Pages.Add(new List<SyntheticPages.Post>
                {
                    page is 4 or 9 ? ReplayPost(1, "Cat", page, FakeForum.ToDateTitle(date)) : ChatPost(1, "Gil", FakeForum.ToDateTitle(date)),
                });
            }
            section.Threads.Add(tour);

            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[]
            {
                FakeForum.IndexUrl, Url(Tournaments), tour.Url, ThreadSearchUrl(1), tour.Url + "page-4", tour.Url + "page-9",
            }));
            Assert.That(MatchSummary(Scan(threads)), Is.EqualTo(CompleteMatchSummary()));

            Crawl();
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl }), "pages without replay posts do not count as missing");
        }

        [Test]
        public void NewLongThread_WithReplaysOnMostPages_IsReadWithoutWaitingForTheSearch()
        {
            var section = Section("Tournaments", Tournaments);
            var tour = TourThread(1, 1, OldStart);
            var date = OldStart.AddDays(1);
            for (var page = 2; page <= 12; page++)
            {
                tour.Pages.Add(Enumerable.Range(1, 5).Select(i => ReplayPost(1, "Cat", page * 10 + i, FakeForum.ToDateTitle(date = date.AddHours(1)))).ToList());
            }
            section.Threads.Add(tour);

            var threads = Crawl();

            // 55 replay posts take 6 result pages, more than half of the 11 pages to read.
            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, Url(Tournaments), tour.Url, ThreadSearchUrl(1) }
                .Concat(Enumerable.Range(2, 11).Select(page => tour.Url + "page-" + page))));
            Assert.That(MatchSummary(Scan(threads)), Is.EqualTo(CompleteMatchSummary()));
        }

        [Test]
        public void SearchNotAvailable_ReadsThePagesDirectly()
        {
            var section = Section("Tournaments", Tournaments);
            var tour = TourThread(1, 3, OldStart);
            section.Threads.Add(tour);
            Crawl();

            _http.Map(TournamentsSearch, "<html><body><div class=\"blockMessage\">You must be logged-in to do that.</div></body></html>");
            tour.Pages[2].Add(ReplayPost(1, "Cat", 99, Newest()));
            var longTour = TourThread(2, 8, DateTime.UtcNow.AddHours(-2));
            section.Threads.Add(longTour);
            var threads = Crawl();

            Assert.That(Requests.Where(url => url.Contains("/search/")), Is.EqualTo(new[] { TournamentsSearch }), "no thread search either");
            Assert.That(Requests, Has.Member(tour.Url + "page-3").And.Member(longTour.Url + "page-8"));
            Assert.That(MatchSummary(Scan(threads)), Is.EqualTo(CompleteMatchSummary()));
        }

        [Test]
        public void PostAfterTheSearchStarted_IsReadWithoutTheSearch()
        {
            var section = Section("Tournaments", Tournaments);
            var tour = TourThread(1, 3, OldStart);
            section.Threads.Add(tour);
            Crawl();

            // The fake search does not find posts dated in the future, like a post sent while the search runs.
            tour.Pages[2].Add(ReplayPost(1, "Cat", 99, FakeForum.ToDateTitle(DateTime.UtcNow.AddMinutes(30))));
            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), tour.Url, tour.Url + "page-3" }));
            Assert.That(MatchSummary(Scan(threads)), Has.Some.Contains("gen9ou-1099"));
        }

        [Test]
        public void PostNotYetInTheSearchIndex_IsReadWithoutTheSearch()
        {
            var section = Section("Tournaments", Tournaments);
            var tour = TourThread(1, 3, OldStart);
            section.Threads.Add(tour);
            Crawl();

            _forum.SearchIndexLag = TimeSpan.FromMinutes(10);
            var now = DateTime.UtcNow;
            tour.Pages[2].Add(ReplayPost(1, "Cat", 99, FakeForum.ToDateTitle(new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(-5))));
            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), tour.Url, tour.Url + "page-3" }));
            Assert.That(MatchSummary(Scan(threads)), Has.Some.Contains("gen9ou-1099"));
        }

        [Test]
        public void PostsDeletedAfterTheSearch_TheReplayPostIsFoundThroughItsLink()
        {
            var section = Section("Tournaments", Tournaments);
            var tour = TourThread(1, 3, OldStart);
            section.Threads.Add(tour);
            Crawl();

            var replay = ReplayPost(1, "Cat", 99, Newest());
            tour.Pages[2].Add(replay);
            var moved = false;
            _http.BeforeResponse = url =>
            {
                if (url == tour.Url && !moved)
                {
                    // Posts on page 2 were deleted after the search: the replay post moved up to page 2.
                    moved = true;
                    tour.Pages[2].Remove(replay);
                    tour.Pages[1].Add(replay);
                    _forum.Serve(_http);
                }
            };
            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[]
            {
                FakeForum.IndexUrl, TournamentsSearch, Url(Tournaments), tour.Url, tour.Url + "page-3", tour.Url + "post-" + replay.PostId,
            }));
            var summary = MatchSummary(Scan(threads));
            Assert.That(summary, Has.Some.Contains("gen9ou-1099"));
            Assert.That(summary, Is.EqualTo(CompleteMatchSummary()));
        }

        [Test]
        public void MixedForum_MatchesStayTheSameAsAScanOfEveryPage()
        {
            var section = Section("Tournaments", Tournaments);
            var alpha = Sub(section, "Tour Alpha", TourAlpha);
            var tour = TourThread(1, 3, OldStart);
            var chat = ChatThread(2, 3, OldStart);
            chat.Pages[1].Add(ReplayPost(2, "Cat", 1, FakeForum.ToDateTitle(OldStart.AddDays(1))));
            var replays = TourThread(3, 2, OldStart, slug: "tour-3-replays");
            var longTour = TourThread(4, 9, OldStart);
            section.Threads.AddRange(new[] { tour, chat });
            alpha.Threads.AddRange(new[] { replays, longTour });

            var threads = Crawl();
            Assert.That(MatchSummary(Scan(threads)), Is.EqualTo(CompleteMatchSummary()));

            tour.Pages[2].Add(ReplayPost(1, "Eve", 50, Newest()));
            chat.Pages[2].Add(ChatPost(2, "Gil", Newest()));
            longTour.Pages[8].Add(ReplayPost(4, "Eve", 51, Newest()));
            longTour.Pages.Add(new List<SyntheticPages.Post> { ChatPost(4, "Gil", Newest()) });
            replays.Pages.Add(new List<SyntheticPages.Post>
            {
                new() { Author = "Host3", UserId = 30, DateTitle = Newest(), BodyLines = new List<string> { "Gus3 vs. <b>Hal3</b>" } },
            });
            threads = Crawl();
            Assert.That(MatchSummary(Scan(threads)), Is.EqualTo(CompleteMatchSummary()));

            tour.Locked = true;
            chat.Pages[0][0].BodyLines.Add("<b>Cat2</b> vs. Dan2");
            chat.Pages[2].Add(ReplayPost(2, "Cat", 2, Newest()));
            longTour.Pages[9].Add(ReplayPost(4, "Cat", 52, Newest()));
            threads = Crawl();
            Assert.That(MatchSummary(Scan(threads)), Is.EqualTo(CompleteMatchSummary()));
            Assert.That(Requests.Count(url => url.Contains("/search/")), Is.EqualTo(1), "one search per run covers all forums");
        }

        #endregion

        #region Migration of 2.x entries

        [Test]
        public void LegacyEntry_CoveringTheLatestPost_IsTakenOverWithoutRequests()
        {
            var section = Section("Tournaments", Tournaments);
            var thread = TourThread(1, 3, OldStart);
            section.Threads.Add(thread);
            StoreLegacyEntry(thread);
            var legacyBytes = _cache.Store[thread.Url].ToArray();

            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, Url(Tournaments) }));
            Assert.That(Summary(Scan(threads)), Is.EqualTo(FreshSummary()));
            Assert.That(_cache.Store[thread.Url], Is.EqualTo(legacyBytes), "the 2.x entry is never changed");
        }

        [Test]
        public void LegacyEntry_UnderTheHttpUrl_IsTakenOver()
        {
            // 2.x used http:// URLs as cache keys until July 2026.
            var section = Section("Tournaments", Tournaments);
            var thread = TourThread(1, 2, OldStart);
            section.Threads.Add(thread);
            var httpKey = "http://" + thread.Url["https://".Length..];
            StoreLegacyEntry(thread, key: httpKey);

            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, Url(Tournaments) }));
            Assert.That(Summary(Scan(threads)), Is.EqualTo(FreshSummary()));
        }

        [Test]
        public void LegacyEntries_UnderBothUrls_TheOneWithContentWins()
        {
            // The https entry was written while Smogon answered with its bot check.
            var section = Section("Tournaments", Tournaments);
            var thread = TourThread(1, 2, OldStart);
            section.Threads.Add(thread);
            StoreLegacyEntry(thread, key: "http://" + thread.Url["https://".Length..]);
            _cache.Store[thread.Url] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new TopicAnalyzeResult
            {
                CollectedLinks = new List<string> { "" },
                NumberOfPages = 1,
                LastPost = DateTime.Now,
            }));

            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, Url(Tournaments) }));
            Assert.That(Summary(Scan(threads)), Is.EqualTo(FreshSummary()));
        }

        [Test]
        public void LegacyEntry_WithScanTimeInsteadOfPostDate_IsTakenOverWithoutRequests()
        {
            // Before July 2026, 2.x could not read post dates and stored DateTime.Now as the last post.
            var section = Section("Tournaments", Tournaments);
            var thread = TourThread(1, 2, OldStart);
            section.Threads.Add(thread);
            StoreLegacyEntry(thread, result =>
            {
                result.LastPost = DateTime.Now;
                return result;
            });

            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, Url(Tournaments) }));
            Assert.That(Summary(Scan(threads)), Is.EqualTo(FreshSummary()));
        }

        [Test]
        public void LegacyEntry_OlderThanTheLatestPost_ReadsPageOneAndTheLastPage()
        {
            var section = Section("Tournaments", Tournaments);
            var thread = TourThread(1, 3, OldStart);
            section.Threads.Add(thread);
            StoreLegacyEntry(thread);
            thread.Pages[2].Add(ReplayPost(1, "Cat", 77, Newest()));

            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, Url(Tournaments), thread.Url, thread.Url + "page-3" }));
            var summary = Summary(Scan(threads));
            Assert.That(summary, Has.Some.Contains("gen9ou-1077"));
            Assert.That(summary, Is.EqualTo(FreshSummary()), "page 2 still comes from the 2.x entry");
        }

        [Test]
        public void LegacyEntry_WithStoredBotCheck_IsReadAgain()
        {
            // 2.x stored the bot check as a one-page thread without content.
            var section = Section("Tournaments", Tournaments);
            var thread = TourThread(1, 3, OldStart);
            section.Threads.Add(thread);
            _cache.Store[thread.Url] = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new TopicAnalyzeResult
            {
                CollectedLinks = new List<string> { "" },
                NumberOfPages = 1,
                LastPost = DateTime.Now,
            }));

            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, Url(Tournaments), thread.Url, thread.Url + "page-2", thread.Url + "page-3" }));
            Assert.That(Summary(Scan(threads)), Is.EqualTo(FreshSummary()));
        }

        [Test]
        public void LegacyEntry_WithOneEmptyPage_ReadsThatPage()
        {
            var section = Section("Tournaments", Tournaments);
            var thread = TourThread(1, 4, OldStart);
            section.Threads.Add(thread);
            StoreLegacyEntry(thread, result =>
            {
                result.CollectedLinks[1] = "";
                return result;
            });

            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, Url(Tournaments), thread.Url, thread.Url + "page-2", thread.Url + "page-4" }));
            Assert.That(Summary(Scan(threads)), Is.EqualTo(FreshSummary()));
        }

        [Test]
        public void LegacyEntry_OfALockedThread_KeepsTheLockWithoutRequests()
        {
            var section = Section("Tournaments", Tournaments);
            var thread = TourThread(1, 1, OldStart);
            thread.Locked = true;
            section.Threads.Add(thread);
            StoreLegacyEntry(thread);

            Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, Url(Tournaments) }));
        }

        [Test]
        public void LegacyEntry_LockedSinceTheScan_ReadsPageOne()
        {
            var section = Section("Tournaments", Tournaments);
            var thread = TourThread(1, 1, OldStart);
            section.Threads.Add(thread);
            StoreLegacyEntry(thread);
            thread.Locked = true;

            Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, Url(Tournaments), thread.Url }));
        }

        [Test]
        public void LegacyEntry_OfAThreadWithoutPairings_NeedsOnlyPageOne()
        {
            var section = Section("Tournaments", Tournaments);
            var chat = ChatThread(1, 3, OldStart);
            section.Threads.Add(chat);
            StoreLegacyEntry(chat);
            chat.Pages.Add(new List<SyntheticPages.Post> { ChatPost(1, "Gil", Newest()) });

            var threads = Crawl();

            Assert.That(Requests, Is.EqualTo(new[] { FakeForum.IndexUrl, Url(Tournaments), chat.Url }));
            Assert.That(MatchSummary(Scan(threads)), Is.EqualTo(CompleteMatchSummary()));
        }

        [Test]
        public void LegacyCoversLatestPost_ComparesScanTimesInUtcAndDisplayDatesToTheMinute()
        {
            var item = new ThreadListItem
            {
                LatestPostTimestamp = new DateTimeOffset(2026, 7, 1, 12, 0, 30, TimeSpan.Zero).ToUnixTimeSeconds(),
                LatestPostDisplayDate = "Jul 1, 2026 at 8:00 AM",
            };

            Assert.That(SmogonThreadCollector.LegacyCoversLatestPost(new DateTime(2026, 7, 1, 8, 0, 0), item), Is.True);
            Assert.That(SmogonThreadCollector.LegacyCoversLatestPost(new DateTime(2026, 7, 1, 7, 59, 0), item), Is.False);
            Assert.That(SmogonThreadCollector.LegacyCoversLatestPost(new DateTime(2026, 7, 1, 12, 0, 31, DateTimeKind.Utc), item), Is.True);
            Assert.That(SmogonThreadCollector.LegacyCoversLatestPost(new DateTime(2026, 7, 1, 12, 0, 29, DateTimeKind.Utc), item), Is.False);
            Assert.That(SmogonThreadCollector.LegacyCoversLatestPost(
                new DateTimeOffset(2026, 7, 1, 14, 1, 0, TimeSpan.FromHours(2)).LocalDateTime, item), Is.True);
        }

        #endregion

        #region Whole parser

        private void StandardSections()
        {
            Section("Tournaments", Tournaments);
            var showdown = Section("Pokémon Showdown!", "/forums/forums/pok%C3%A9mon-showdown.209/");
            Sub(showdown, "PS! Tournaments", "/forums/forums/ps-tournaments.698/");
            Section("Smogon Metagames", "/forums/forums/smogon-metagames.725/");
            Section("Ruins of Alph", "/forums/forums/ruins-of-alph.31/");
            var cartridge = Section("Cartridge & Card Metagames", "/forums/forums/cartridge-card-metagames.457/");
            Sub(cartridge, "Battle Stadium Singles", "/forums/forums/battle-stadium-singles.532/");
            Sub(cartridge, "Video Game Championships", "/forums/forums/video-game-championships.513/");
        }

        [Test]
        public void SmogonParser_CoversTheConfiguredForums_AndOnlyRequestsChanges()
        {
            StandardSections();
            var bss = _forum.AllForums.Single(f => f.Title == "Battle Stadium Singles");
            var vgc = _forum.AllForums.Single(f => f.Title == "Video Game Championships");
            var ps = _forum.AllForums.Single(f => f.Title == "PS! Tournaments");
            _forum.AllForums.Single(f => f.Title == "Tournaments").Threads.Add(TourThread(1, 2, OldStart));
            ps.Threads.Add(TourThread(2, 1, OldStart));
            bss.Threads.Add(TourThread(3, 1, OldStart));
            vgc.Threads.Add(TourThread(4, 1, OldStart));
            _forum.AllForums.Single(f => f.Title == "Smogon Metagames").Threads.Add(TourThread(5, 1, OldStart));
            _forum.AllForums.Single(f => f.Title == "Ruins of Alph").Threads.Add(TourThread(6, 1, OldStart));
            _forum.Serve(_http);

            var users = new SmogonParser(_cache, Options()).GetMatchesForUsers().Result;

            Assert.That(users.Keys, Is.SupersetOf(new[] { "ann1", "ann2", "ann3", "ann5", "ann6" }));
            Assert.That(users.Keys, Has.None.EqualTo("ann4"), "VGC is not scanned");
            Assert.That(_http.Requests, Has.None.StartsWith(vgc.Url));

            _http.ClearRequests();
            var again = new SmogonParser(_cache, Options()).GetMatchesForUsers().Result;

            // Forums without a latest post on the index (subforum links) are listed (and searched) every run.
            Assert.That(_http.Requests, Is.EquivalentTo(new[] { FakeForum.IndexUrl, SearchUrl(698), ps.Url, SearchUrl(532), bss.Url }));
            Assert.That(again.Keys, Is.EquivalentTo(users.Keys));
        }

        #endregion
    }
}
