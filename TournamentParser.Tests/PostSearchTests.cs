#nullable enable
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TournamentParser.Tests.TestSupport;
using TournamentParser.ThreadCollector;
using TournamentParser.Util;

namespace TournamentParser.Tests
{
    /// <summary>Smogon's search: URLs, result pages (real ones from 2026-11-13) and how searches page through dates.</summary>
    [TestFixture]
    public class PostSearchTests
    {
        private static string Fixture(string name)
            => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", name));

        [Test]
        public void Url_LimitsToForumsWithSubforumsAndDays()
        {
            var url = PostSearch.Url(PostSearch.ReplayQuery, PostSearch.Filter.Forums([34, 698]), new DateTime(2026, 11, 12), new DateTime(2026, 11, 13));

            Assert.That(url, Is.EqualTo("https://www.smogon.com/forums/search/1/?q=replay.pokemonshowdown.com&t=post&o=date"
                + "&c[newer_than]=2026-11-12&c[older_than]=2026-11-13&c[nodes][0]=34&c[nodes][1]=698&c[child_nodes]=1"));
        }

        [Test]
        public void Url_LimitsToAThread()
        {
            var url = PostSearch.Url(PostSearch.ReplayQuery, PostSearch.Filter.Thread(3721437), null, null);

            Assert.That(url, Is.EqualTo("https://www.smogon.com/forums/search/1/?q=replay.pokemonshowdown.com&t=post&o=date&c[thread]=3721437"));
        }

        [Test]
        public void Parse_ReadsTheHitsOfAResultPage()
        {
            var hits = SearchResultParser.Parse(Fixture("search-tournaments-replays-2026-11-13-page-1.html"), out var next);

            Assert.That(hits, Has.Count.EqualTo(10));
            Assert.That(hits[0], Is.EqualTo(new SearchHit(
                ThreadId: 3791054,
                ThreadUrl: "https://www.smogon.com/forums/threads/nu-homefield-round-10.3791054/",
                PostId: 11192416,
                Position: 2,
                PostDate: DateTimeOffset.FromUnixTimeSeconds(1794539422).UtcDateTime,
                ForumUrl: "https://www.smogon.com/forums/forums/smogon-metagames-circuits.474/")));
            Assert.That(hits[1].Position, Is.EqualTo(30));
            Assert.That(hits[1].Page, Is.EqualTo(2), "25 posts per page");
            Assert.That(hits.Select(hit => hit.PostDate), Is.Ordered.Descending);
            Assert.That(next, Is.EqualTo("https://www.smogon.com/forums/search/72812527/?page=2&q=replay.pokemonshowdown.com&t=post&c[child_nodes]=1&c[nodes][0]=34&o=date"));
        }

        [Test]
        public void Parse_LastPageOfAThreadSearch_HasNoNextPage()
        {
            var hits = SearchResultParser.Parse(Fixture("search-uu-open-xii-r2-replays-2026-11-13-page-8.html"), out var next);

            Assert.That(hits.Select(hit => hit.Position), Is.EqualTo(new[] { 10, 9, 7, 5, 2 }));
            Assert.That(hits.Select(hit => hit.ThreadId).Distinct(), Is.EqualTo(new[] { 3721437L }));
            Assert.That(next, Is.Null);
        }

        [Test]
        public void SearchWithoutResults_IsAnEmptyResult()
        {
            var html = Fixture("search-no-results-2026-11-13.html");
            Assert.That(SearchResultParser.Parse(html, out var next), Is.Empty);
            Assert.That(next, Is.Null);

            var result = new PostSearch(_ => Task.FromResult(html)).SearchForumsAsync([34], new DateTime(2026, 11, 12)).Result;

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Hits, Is.Empty);
        }

        /// <summary>A forum with the given number of replay posts per day, from 2026-10-01 on, 25 posts per thread page.</summary>
        private static FakeForum ForumWithReplayPosts(params (string Path, int[] PostsPerDay)[] forums)
        {
            var fake = new FakeForum();
            var threadId = 1;
            foreach (var (path, postsPerDay) in forums)
            {
                var forum = new FakeForum.Forum { Title = path, Path = path };
                var thread = new FakeForum.Thread { Id = threadId++, Slug = "tour" };
                var posts = postsPerDay.SelectMany((count, day) => Enumerable.Range(0, count).Select(i => new SyntheticPages.Post
                {
                    Author = "Cat",
                    PostId = thread.Id * 10000 + day * 1000 + i + 1,
                    DateTitle = FakeForum.ToDateTitle(new DateTime(2026, 10, 1).AddDays(day).AddMinutes(i)),
                    BodyLines = new List<string> { $"<a href=\"https://{FakeForum.ReplayMarker}/gen9ou-{day}-{i}\">replay</a>" },
                })).ToList();
                thread.Pages.AddRange(posts.Chunk(25).Select(page => page.ToList()));
                forum.Threads.Add(thread);
                fake.Sections.Add(forum);
            }
            return fake;
        }

        [Test]
        public void SearchCutOffAfter200Results_ContinuesWithTheDaysBefore()
        {
            var fake = ForumWithReplayPosts(("/forums/forums/tournaments.34/", [60, 60, 60, 60, 60]));
            var requests = new List<string>();
            var search = new PostSearch(url =>
            {
                requests.Add(url);
                return Task.FromResult(fake.SearchPage(url));
            });

            var result = search.SearchForumsAsync([34], new DateTime(2026, 9, 30)).Result!;

            Assert.That(result.Hits, Has.Count.EqualTo(300));
            Assert.That(result.Hits.Select(hit => hit.PostId).Distinct().Count(), Is.EqualTo(300), "each post once");
            Assert.That(result.Uncovered, Is.Empty);
            Assert.That(result.Hits.Single(hit => hit.PostId == 1 * 10000 + 4 * 1000 + 10).Page, Is.EqualTo(10), "post 250 is the last of page 10");
            // 200 results reach back into Oct 2, so the second search takes the days before Oct 3 (Oct 2 again, and Oct 1).
            Assert.That(requests.Where(url => !url.Contains("page=")), Is.EqualTo(new[]
            {
                PostSearch.Url(PostSearch.ReplayQuery, PostSearch.Filter.Forums([34]), new DateTime(2026, 9, 30), null),
                PostSearch.Url(PostSearch.ReplayQuery, PostSearch.Filter.Forums([34]), new DateTime(2026, 9, 30), new DateTime(2026, 10, 3)),
            }));
            Assert.That(requests, Has.Count.EqualTo(20 + 12));
        }

        [Test]
        public void ResultPages_AreReadBackToTheLastRunOnly()
        {
            var fake = ForumWithReplayPosts(("/forums/forums/tournaments.34/", [30, 30, 30]));
            var requests = new List<string>();
            var search = new PostSearch(url =>
            {
                requests.Add(url);
                return Task.FromResult(fake.SearchPage(url));
            });

            // 30 posts a day, one per minute from midnight on; the last run covered Oct 3 up to 00:20.
            var result = search.SearchForumsAsync([34], new DateTime(2026, 10, 2), new DateTime(2026, 10, 3, 0, 20, 0)).Result!;

            Assert.That(requests, Has.Count.EqualTo(2), "the second page reaches back before 00:20; without the limit, 6 pages");
            Assert.That(result.Hits.Min(hit => hit.PostDate), Is.LessThan(new DateTime(2026, 10, 3, 0, 20, 0)));
            Assert.That(result.Hits.Where(hit => hit.PostDate >= new DateTime(2026, 10, 3, 0, 20, 0)).Count(), Is.EqualTo(10));
        }

        [Test]
        public void ForumWithMoreThan200ResultsOnADay_IsUncovered_TheOtherForumsAreNot()
        {
            var fake = ForumWithReplayPosts(("/forums/forums/tournaments.34/", [210]), ("/forums/forums/ps-tournaments.698/", [5]));

            var result = new PostSearch(url => Task.FromResult(fake.SearchPage(url))).SearchForumsAsync([34, 698], new DateTime(2026, 9, 30)).Result!;

            Assert.That(result.Uncovered, Is.EqualTo(new[] { 34L }));
            Assert.That(result.Hits.Count(hit => hit.ForumUrl.EndsWith(".698/")), Is.EqualTo(5));
        }

        [Test]
        public void FloodControl_WaitsAndAsksAgain()
        {
            var results = Fixture("search-tournaments-replays-2026-11-13-page-1.html").Replace("pageNav-jump--next", "pageNav-jump--other");
            var answers = new Queue<string>(new[]
            {
                "<html><body><div class=\"blockMessage\">You must wait at least 4 seconds before performing this action.</div></body></html>",
                results,
            });
            var waits = new List<TimeSpan>();
            var search = new PostSearch(_ => Task.FromResult(answers.Dequeue()), wait =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            });

            var result = search.SearchForumsAsync([34], new DateTime(2026, 11, 12)).Result;

            Assert.That(waits, Is.EqualTo(new[] { TimeSpan.FromSeconds(5) }));
            Assert.That(result!.Hits, Has.Count.EqualTo(10));
        }

        [Test]
        public void SearchNotAllowedForGuests_IsNoResult()
        {
            var search = new PostSearch(_ => Task.FromResult("<html><body><div class=\"blockMessage\">You must be logged-in to do that.</div></body></html>"));

            Assert.That(search.SearchForumsAsync([34], null).Result, Is.Null);
            Assert.That(search.SearchThreadAsync(1, 5).Result.Outcome, Is.EqualTo(PostSearch.ThreadSearchOutcome.Unavailable));
        }

        [Test]
        public void BotCheck_StopsTheCrawl()
        {
            var search = new PostSearch(_ => Task.FromResult("<html><body><p id=\"m\">Checking your browser...</p><script>document.cookie = \"xf_smogon_gate=1\"</script></body></html>"));

            var error = Assert.Throws<AggregateException>(() => search.SearchForumsAsync([34], null).Wait())!;
            Assert.That(error.InnerException, Is.InstanceOf<SmogonAccessBlockedException>());
        }
    }
}
