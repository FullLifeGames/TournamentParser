using Microsoft.Extensions.Caching.Distributed;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TournamentParser.Core.Data;
using TournamentParser.Data;
using TournamentParser.ThreadCollector;
using TournamentParser.Util;

namespace TournamentParser.ThreadScanner
{
    public class SmogonThreadScanner : IThreadScanner
    {
        /// <summary>Smogon shows 25 posts per thread page.</summary>
        private const int PostsPerPage = 25;

        public SmogonThreadScanner() : this((IDistributedCache?)null) { }

        /// <param name="cache">
        /// Crawl state written by <see cref="ThreadCollector.SmogonThreadCollector"/> with the same cache. <see cref="ScanThreads"/>
        /// parses the stored pages of a thread instead of requesting them.
        /// </param>
        public SmogonThreadScanner(IDistributedCache? cache) : this(cache == null ? null : new CrawlStateStore(cache)) { }

        private readonly CrawlStateStore? _store;
        internal SmogonThreadScanner(CrawlStateStore? store)
        {
            _store = store;
        }

        public ConcurrentBag<User> Users { get; } = [];
        public ConcurrentDictionary<string, User> NameUserTranslation { get; } = new ConcurrentDictionary<string, User>();
        public ConcurrentDictionary<int, User> IdUserTranslation { get; } = new ConcurrentDictionary<int, User>();
        public ConcurrentDictionary<string, string> UserWithSpaceTranslation { get; } = new ConcurrentDictionary<string, string>();

        private readonly RegexUtil _regexUtil = new();

        private const string ShowdownReplayString = "replay.pokemonshowdown.com/";

        /// <summary>
        /// Parses the given threads. Threads with pages in the crawl state are parsed from there without any request;
        /// other threads are read from Smogon (all pages, nothing is stored).
        /// </summary>
        public async Task ScanThreads(IDictionary<string, List<string>> threadsForForums)
        {
            var urls = threadsForForums.SelectMany(thread => thread.Value).Distinct().ToList();
            var fromState = 0;
            await Parallel.ForEachAsync(
                urls,
                Common.ParallelOptions,
                async (url, ct) =>
                {
                    var storedPages = _store?.LoadPages(url);
                    try
                    {
                        if (storedPages != null)
                        {
                            await AnalyzeTopic(url, storedPages, ct).ConfigureAwait(false);
                            System.Threading.Interlocked.Increment(ref fromState);
                        }
                        else
                        {
                            Console.WriteLine("Reading " + url);
                            await AnalyzeTopic(url, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // One thread the parser trips over must not cost the output of all others.
                        Console.WriteLine("Exception at: " + url);
                        Console.WriteLine(e.Message);
                    }
                }
            ).ConfigureAwait(false);
            Console.WriteLine($"Parsed {urls.Count} thread(s), {fromState} from the crawl state; {Users.Count} users known");
        }

        /// <summary>
        /// The lines of a thread page that parsing needs, joined by '\n': the lines this scanner keeps while it parses the page
        /// (post bodies and the markers around them). Parsing these lines gives the same result as parsing the whole page,
        /// so the crawl state stores them instead of the page.
        /// </summary>
        internal static string ExtractImportantLines(string url, int page, string html)
        {
            // Which lines are kept depends only on the page itself, so a throwaway scanner gives the same lines as the real one.
            var scanner = new SmogonThreadScanner();
            var lineDataHandler = new LineDataHandler()
            {
                PostNumber = (page - 1) * PostsPerPage
            };
            var thread = new Thread() { Id = GetThreadId(url) };
            var currentlyUserToMatch = new Dictionary<string, List<TournamentMatch>>();
            foreach (var line in Common.EnumerateLines(html))
            {
                scanner.HandleLine(url, line, thread, currentlyUserToMatch, lineDataHandler);
            }
            return string.Join('\n', lineDataHandler.FullImportantSiteBits);
        }

        private static string GetThreadId(string url)
        {
            var id = url[(url.LastIndexOf(".") + 1)..];
            if (id.Contains('/'))
            {
                id = id[..id.IndexOf("/")];
            }
            return id;
        }

        /// <summary>Reads all pages of a thread from Smogon and parses them; nothing is stored.</summary>
        public async Task<TopicAnalyzeResult?> AnalyzeTopic(string url, System.Threading.CancellationToken ct)
        {
            var pages = 1;
            var latestPost = DateTime.UnixEpoch;
            var collectedLinks = new List<string>();
            try
            {
                var currentlyUserToMatch = new Dictionary<string, List<TournamentMatch>>();
                var thread = new Thread
                {
                    Id = GetThreadId(url)
                };

                // Sequential on purpose: all pages live on one host, and parsing carries state across pages.
                for (var pageCount = 1; pageCount <= pages; pageCount++)
                {
                    var pageUrl = SmogonPages.PageUrl(url, pageCount);
                    var site = await Common.HttpClient.GetStringAsync(pageUrl, ct).ConfigureAwait(false);
                    SmogonPages.EnsureThreadPage(site, pageUrl);
                    if (pageCount == 1)
                    {
                        pages = SmogonPages.GetNumberOfPages(site);
                    }

                    var lineDataHandler = new LineDataHandler()
                    {
                        PostNumber = (pageCount - 1) * PostsPerPage
                    };
                    foreach (var line in Common.EnumerateLines(site))
                    {
                        HandleLine(url, line, thread, currentlyUserToMatch, lineDataHandler);
                    }
                    latestPost = lineDataHandler.PostDate;
                    collectedLinks.Add(string.Join('\n', lineDataHandler.FullImportantSiteBits));
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception at: " + url);
                Console.WriteLine(e.Message);
                return null;
            }

            return new TopicAnalyzeResult()
            {
                CollectedLinks = collectedLinks,
                LastPost = latestPost,
                NumberOfPages = pages,
            };
        }

        public Task AnalyzeTopic(string url, IList<string> collectedLinks, System.Threading.CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var currentlyUserToMatch = new Dictionary<string, List<TournamentMatch>>();
                var thread = new Thread
                {
                    Id = GetThreadId(url)
                };
                for (var pageCount = 0; pageCount < collectedLinks.Count; pageCount++)
                {
                    var site = collectedLinks[pageCount];

                    var lineDataHandler = new LineDataHandler()
                    {
                        PostNumber = pageCount * PostsPerPage
                    };

                    foreach (var line in Common.EnumerateLines(site))
                    {
                        HandleLine(url, line, thread, currentlyUserToMatch, lineDataHandler);
                    }
                }
            }, ct);
        }

        private void HandleLine(string url, string line, Thread thread, Dictionary<string, List<TournamentMatch>> currentlyUserToMatch, LineDataHandler lineDataHandler)
        {
            var keepLine = true;
            if (line.Contains("\"articleBody\": \"")
                || (line.Contains("\"text\": \"") && line.TrimStart().StartsWith("\"text\": \"")))
            {
                // XenForo's JSON-LD block inlines the whole first post as a single line;
                // the field used to be called "articleBody" and is now called "text".
                keepLine = false;
            }
            else if (line.Contains("<h1 class=\"p-title-value\">"))
            {
                thread.Name = line[..line.LastIndexOf("<")];
                thread.Name = thread.Name[(thread.Name.LastIndexOf(">") + 1)..];
            }
            else if (line.Contains("<article class=\"message-body js-selectToQuote\">"))
            {
                lineDataHandler.TakePost = true;
            }
            else if (line.Contains("<dd class=\"blockStatus-message blockStatus-message--locked\">"))
            {
                thread.Locked = true;
            }
            else if (line.Contains("<article class=\"message message--post js-post js-inlineModContainer"))
            {
                lineDataHandler.PostStarted = true;
                lineDataHandler.FullPost = new StringBuilder("");
                lineDataHandler.PostNumber++;
                lineDataHandler.CanTakeReplay = true;
            }
            else if (line.Contains("<header class=\"message-attribution message-attribution--split\">"))
            {
                lineDataHandler.TimerHeader = true;
            }
            else if (lineDataHandler.TimerHeader && line.Contains("<time") && line.Contains("title=\""))
            {
                // XenForo used to expose the post date via data-date-string; current markup
                // only carries it in the <time> tag's title attribute ("MMM d, yyyy at h:mm tt").
                var temp = line[(line.IndexOf("title=\"") + "title=\"".Length)..];
                temp = temp[..temp.IndexOf(Common.Quotation)];
                temp = temp.Replace("at ", "");
                if (DateTime.TryParseExact(temp, "MMM d, yyyy h:mm tt", CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var postDate))
                {
                    lineDataHandler.PostDate = postDate;
                }
                lineDataHandler.TimerHeader = false;
            }
            else if (line.StartsWith("\t</article>"))
            {
                lineDataHandler.PostStarted = false;
                lineDataHandler.CanTakeReplay = false;
                lineDataHandler.TakePost = false;

                DetermineIfPostAboutMatch(thread, currentlyUserToMatch, lineDataHandler);
            }
            else if (line.Contains("class=\"username \" dir=\"auto\" data-user-id=\""))
            {
                var tempValue = line[(line.IndexOf("data-user-id") + 5)..];
                tempValue = tempValue[(tempValue.IndexOf(Common.Quotation) + 1)..];
                tempValue = tempValue[..tempValue.IndexOf(Common.Quotation)];
                lineDataHandler.DataUserId = int.Parse(tempValue);

                tempValue = line[(line.IndexOf("href") + 5)..];
                tempValue = tempValue[(tempValue.IndexOf(Common.Quotation) + 1)..];
                tempValue = tempValue[..tempValue.IndexOf(Common.Quotation)];
                lineDataHandler.UserLink = tempValue;
            }
            else if (line.Contains("data-author=\""))
            {
                lineDataHandler.PostedBy = line[(line.IndexOf("data-author") + 5)..];
                lineDataHandler.PostedBy = lineDataHandler.PostedBy[(lineDataHandler.PostedBy.IndexOf(Common.Quotation) + 1)..];
                lineDataHandler.PostedBy = lineDataHandler.PostedBy[..lineDataHandler.PostedBy.IndexOf(Common.Quotation)];
            }
            else
            {
                var strippedLine = _regexUtil.StripHTML(line);
                if (strippedLine.Contains(GetMatchFilterString(strippedLine))
                    && IsReplayCollectionPost(url, lineDataHandler))
                {
                    var newMatch = DetermineMatch(line, strippedLine, thread, currentlyUserToMatch, lineDataHandler);
                    if (newMatch.FirstUser is not null || newMatch.SecondUser is not null)
                    {
                        lineDataHandler.LastMatch = newMatch;
                    }
                }
                else if (lineDataHandler.LastMatch != null && IsReplayCollectionPost(url, lineDataHandler) && line.Contains(ShowdownReplayString))
                {
                    AddReplays(line, lineDataHandler.LastMatch);
                }
                else
                {
                    keepLine = false;
                    // Reset if replay does not come directly afterwards
                    lineDataHandler.LastMatch = null;
                }
            }

            if (lineDataHandler.TakePost && !line.Contains("/likes\""))
            {
                lineDataHandler.FullPost = lineDataHandler.FullPost.Append(line).Append('\n');
                keepLine = true;
            }

            if (keepLine)
            {
                lineDataHandler.FullImportantSiteBits.Add(line);
            }
        }

        private static bool IsReplayCollectionPost(string url, LineDataHandler lineDataHandler)
        {
            return lineDataHandler.PostNumber == 1 || lineDataHandler.PostNumber == 2 || url.Contains("-replay");
        }

        private TournamentMatch DetermineMatch(string line, string strippedLine, Thread thread, Dictionary<string, List<TournamentMatch>> currentlyUserToMatch, LineDataHandler lineDataHandler)
        {
            var match = new TournamentMatch();

            if (thread.Locked)
            {
                match.Finished = true;
            }

            var toFilterFor = GetMatchFilterString(strippedLine);

            var preparedLine = FilterOutTierDefinitionAndStrip(strippedLine);

            if (preparedLine != toFilterFor && preparedLine.Contains(" " + toFilterFor + " "))
            {
                SetupUsers(currentlyUserToMatch, match, toFilterFor, preparedLine);

                DetermineWinner(line, match);

                AddReplays(line, match);

                match.Thread = thread;
                match.PostDate = lineDataHandler.PostDate;
            }
            else if (lineDataHandler.LastMatch is not null)
            {
                AddReplays(line, lineDataHandler.LastMatch);
            }

            return match;
        }

        /// <summary>XenForo user names cannot be longer than 50 characters.</summary>
        private const int MaxUserNameLength = 50;

        private void SetupUsers(Dictionary<string, List<TournamentMatch>> currentlyUserToMatch, TournamentMatch match, string toFilterFor, string preparedLine)
        {
            var separator = " " + toFilterFor + " ";
            var separatorIndex = preparedLine.IndexOf(separator);
            var userOne = preparedLine[..separatorIndex];
            var userTwo = preparedLine[(separatorIndex + separator.Length)..];

            // Pairing lines often carry a score or free-form commentary after a dash
            // ("A vs B - 2-0", "A vs B - this will be close..."); only the name before
            // the dash belongs to the second user.
            var dashIndex = userTwo.IndexOf(" - ");
            if (dashIndex >= 0)
            {
                userTwo = userTwo[..dashIndex];
            }

            userOne = _regexUtil.RemovePositions(userOne);
            userTwo = _regexUtil.RemovePositions(userTwo);

            userOne = _regexUtil.RemoveNumberReplays(userOne);
            userTwo = _regexUtil.RemoveNumberReplays(userTwo);

            var regexUserOne = _regexUtil.Regex(userOne);
            var regexUserTwo = _regexUtil.Regex(userTwo);

            if (regexUserOne.Length > MaxUserNameLength || regexUserTwo.Length > MaxUserNameLength)
            {
                // Longer than any real user name can be: the line is prose that happens
                // to contain a "vs", not a pairing.
                return;
            }

            if (NameUserTranslation.TryGetValue(regexUserOne, out User? firstUser))
            {
                match.FirstUser = firstUser.Name;
            }
            else
            {
                firstUser = new User
                {
                    NormalName = userOne,
                    Name = regexUserOne
                };
                match.FirstUser = firstUser.Name;

                var changed = false;
                NameUserTranslation.AddOrUpdate(regexUserOne, firstUser,
                    (_, existingUser) =>
                    {
                        firstUser = existingUser;
                        changed = true;
                        return existingUser;
                    }
                );
                if (!changed)
                {
                    Users.Add(firstUser);
                }
                UserWithSpaceTranslation.TryAdd(regexUserOne, _regexUtil.RegexWithSpace(userOne));
            }
            firstUser.Matches.Add(match);
            if (currentlyUserToMatch.TryGetValue(regexUserOne, out var userOneMatches))
            {
                userOneMatches.Add(match);
            }
            else
            {
                currentlyUserToMatch.Add(regexUserOne, new List<TournamentMatch>() { match });
            }

            if (NameUserTranslation.TryGetValue(regexUserTwo, out User? secondUser))
            {
                match.SecondUser = secondUser.Name;
            }
            else
            {
                secondUser = new User
                {
                    NormalName = userTwo,
                    Name = regexUserTwo
                };
                match.SecondUser = secondUser.Name;

                var changed = false;
                NameUserTranslation.AddOrUpdate(regexUserTwo, secondUser,
                    (_, existingUser) =>
                    {
                        secondUser = existingUser;
                        changed = true;
                        return existingUser;
                    }
                );
                if (!changed)
                {
                    Users.Add(secondUser);
                }
                UserWithSpaceTranslation.TryAdd(regexUserTwo, _regexUtil.RegexWithSpace(userTwo));
            }
            secondUser.Matches.Add(match);
            if (currentlyUserToMatch.TryGetValue(regexUserTwo, out var userTwoMatches))
            {
                userTwoMatches.Add(match);
            }
            else
            {
                currentlyUserToMatch.Add(regexUserTwo, new List<TournamentMatch>() { match });
            }
        }

        private void DetermineWinner(string line, TournamentMatch match)
        {
            if (line.Contains("<b>") || line.Contains("</b>"))
            {
                match.Finished = true;
                var winnerName = line;
                if (winnerName.Contains("<b>"))
                {
                    winnerName = winnerName[(winnerName.IndexOf("<b>") + 3)..];
                }
                if (winnerName.Contains("</b>"))
                {
                    winnerName = winnerName[..winnerName.IndexOf("</b>")];
                }
                winnerName = _regexUtil.StripHTML(winnerName);
                if (NameUserTranslation.TryGetValue(_regexUtil.Regex(winnerName), out User? winner))
                {
                    match.Winner = winner.Name;
                }
            }
        }

        private static void AddReplays(string line, TournamentMatch match)
        {
            if (line.Contains(ShowdownReplayString))
            {
                var tempLine = line;
                while (tempLine.Contains(ShowdownReplayString))
                {
                    tempLine = tempLine[tempLine.IndexOf(ShowdownReplayString)..];
                    var quot = tempLine.Contains(Common.Quotation) ? tempLine.IndexOf(Common.Quotation) : int.MaxValue;
                    var leftArrow = tempLine.Contains('<') ? tempLine.IndexOf("<") : int.MaxValue;
                    var link = tempLine;
                    if (leftArrow < quot)
                    {
                        link = tempLine[..tempLine.IndexOf("<")];
                    }
                    else if (quot != int.MaxValue)
                    {
                        link = tempLine[..tempLine.IndexOf(Common.Quotation)];
                    }
                    if (!match.Replays.Contains("https://" + link))
                    {
                        match.Replays.Add("https://" + link);
                        match.Finished = true;
                    }
                    if (leftArrow < quot)
                    {
                        tempLine = tempLine[tempLine.IndexOf("<")..];
                    }
                    else if (quot != int.MaxValue)
                    {
                        tempLine = tempLine[tempLine.IndexOf(Common.Quotation)..];
                    }
                    else
                    {
                        // Weird behavior
                        tempLine = tempLine[ShowdownReplayString.Length..];
                    }
                }
            }
        }

        private static string GetMatchFilterString(string strippedLine)
        {
            var toFilterFor = "vs";

            if (strippedLine.Contains(" vs. "))
            {
                toFilterFor = "vs.";
            }

            if (strippedLine.Contains(" VS "))
            {
                toFilterFor = "VS";
            }

            if (strippedLine.Contains(" VS. "))
            {
                toFilterFor = "VS.";
            }

            return toFilterFor;
        }

        private void DetermineIfPostAboutMatch(Thread thread, Dictionary<string, List<TournamentMatch>> currentlyUserToMatch, LineDataHandler lineDataHandler)
        {
            if (lineDataHandler.DataUserId != 0)
            {
                User? currentUser = null;
                var postedByRegex = _regexUtil.Regex(lineDataHandler.PostedBy);
                if (!IdUserTranslation.TryGetValue(lineDataHandler.DataUserId, out User? userForId))
                {
                    if (!NameUserTranslation.ContainsKey(postedByRegex))
                    {
                        var newUser = new User
                        {
                            Id = lineDataHandler.DataUserId,
                            NormalName = lineDataHandler.PostedBy,
                            Name = postedByRegex,
                            ProfileLink = lineDataHandler.UserLink
                        };

                        var changed = false;
                        NameUserTranslation.AddOrUpdate(postedByRegex, newUser,
                            (_, existingUser) =>
                            {
                                newUser = existingUser;
                                changed = true;
                                return existingUser;
                            }
                        );
                        if (!changed)
                        {
                            Users.Add(newUser);
                        }
                        currentUser = newUser;

                        UserWithSpaceTranslation.TryAdd(postedByRegex, _regexUtil.RegexWithSpace(lineDataHandler.PostedBy));
                        IdUserTranslation.TryAdd(lineDataHandler.DataUserId, newUser);
                    }
                    else
                    {
                        NameUserTranslation.TryGetValue(postedByRegex, out User? existingUser);
                        currentUser = existingUser;

                        if (existingUser is not null && existingUser.ProfileLink is null)
                        {
                            existingUser.Id = lineDataHandler.DataUserId;
                            existingUser.ProfileLink = lineDataHandler.UserLink;
                            IdUserTranslation.TryAdd(lineDataHandler.DataUserId, existingUser);
                        }
                        else
                        {
                            Console.WriteLine("Strange behavior with User: " + existingUser?.Name);
                        }
                    }
                }
                else
                {
                    if (!NameUserTranslation.ContainsKey(postedByRegex))
                    {
                        var existingUser = userForId;

                        if (existingUser.Name is null)
                        {
                            existingUser.NormalName = lineDataHandler.PostedBy;
                            existingUser.Name = postedByRegex;
                            NameUserTranslation.AddOrUpdate(postedByRegex, existingUser,
                            (_, existingUserData) =>
                            {
                                existingUser = existingUserData;
                                return existingUserData;
                            }
                        );
                            UserWithSpaceTranslation.TryAdd(postedByRegex, _regexUtil.RegexWithSpace(lineDataHandler.PostedBy));
                        }
                        else
                        {
                            Console.WriteLine("Strange behavior with User: " + existingUser.Name);
                        }
                        currentUser = existingUser;
                    }
                    else
                    {
                        // Just exists already, proceed as normal
                    }
                }

                if (currentUser is null)
                {
                    NameUserTranslation.TryGetValue(postedByRegex, out currentUser);
                }
                var fullPostString = lineDataHandler.FullPost.ToString();
                var strippedFullPost = _regexUtil.StripHTML(fullPostString);
                var regexWithSpaceFullPost = _regexUtil.RegexWithSpace(strippedFullPost)
                    .Replace(_regexUtil.RegexWithSpace(currentUser!.NormalName), "")
                    .Replace(_regexUtil.Regex(currentUser.NormalName), "");
                regexWithSpaceFullPost = _regexUtil.RemoveReactions(regexWithSpaceFullPost);
                if (fullPostString.Contains(ShowdownReplayString) && lineDataHandler.PostNumber != 1 && currentUser is not null)
                {
                    var notExistingMatch = true;
                    var match = new TournamentMatch();
                    if (currentlyUserToMatch.TryGetValue(postedByRegex, out var userMatches))
                    {
                        var postTokens = GetInnerTokens(regexWithSpaceFullPost);
                        foreach (var currentMatch in userMatches)
                        {
                            if (IsUserPartOfPost(currentMatch.FirstUser, currentUser, postTokens, regexWithSpaceFullPost)
                                || IsUserPartOfPost(currentMatch.SecondUser, currentUser, postTokens, regexWithSpaceFullPost))
                            {
                                match = currentMatch;
                                notExistingMatch = false;
                                break;
                            }
                        }
                        if (notExistingMatch && userMatches.Count == 1)
                        {
                            match = userMatches[0];
                            notExistingMatch = false;
                        }
                        if (notExistingMatch)
                        {
                            match = new TournamentMatch
                            {
                                FirstUser = currentUser.Name,
                                Thread = thread,
                                Finished = true,
                            };

                            // Order by name length of the users so that short common terms are ignored longer and bigger names can be respected
                            var closest = int.MaxValue;
                            User? closestUser = null;
                            if (match.FirstUser is not null
                                && NameUserTranslation.TryGetValue(match.FirstUser, out User? postingUser)
                                && postingUser.Name is not null)
                            {
                                foreach (var user in Users.OrderByDescending(u => u.Name?.Length))
                                {
                                    if (user != postingUser && UserMentionedInPost(user, postTokens, regexWithSpaceFullPost))
                                    {
                                        var closeIndex = regexWithSpaceFullPost.IndexOf(user?.Name ?? "notdefined");
                                        if (closest > closeIndex)
                                        {
                                            closest = closeIndex;
                                            closestUser = user;
                                        }
                                        if (closest == -1)
                                        {
                                            // IndexOf can never return less than -1, so the result is settled.
                                            break;
                                        }
                                    }
                                }
                            }

                            if (closestUser is not null)
                            {
                                match.SecondUser = closestUser.Name;
                                notExistingMatch = false;
                            }

                            match.PostDate = lineDataHandler.PostDate;
                            currentUser.Matches.Add(match);
                            if (match.SecondUser is not null && NameUserTranslation.TryGetValue(match.SecondUser, out User? secondUserVal))
                            {
                                secondUserVal.Matches.Add(match);
                            }
                        }

                        var tempLine = fullPostString;
                        while (tempLine.Contains(ShowdownReplayString))
                        {
                            tempLine = tempLine[tempLine.IndexOf(ShowdownReplayString)..];
                            var quot = tempLine.Contains('"') ? tempLine.IndexOf(Common.Quotation) : int.MaxValue;
                            var arrow = tempLine.Contains('<') ? tempLine.IndexOf("<") : int.MaxValue;
                            string link;
                            if (arrow < quot)
                            {
                                link = tempLine[..tempLine.IndexOf("<")];
                            }
                            else
                            {
                                link = tempLine[..tempLine.IndexOf(Common.Quotation)];
                            }
                            if (!match.Replays.Contains("https://" + link))
                            {
                                match.Replays.Add("https://" + link);
                                match.Finished = true;
                            }
                            if (arrow < quot)
                            {
                                tempLine = tempLine[tempLine.IndexOf("<")..];
                            }
                            else
                            {
                                tempLine = tempLine[tempLine.IndexOf(Common.Quotation)..];
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// All tokens of <paramref name="text"/> that are enclosed by spaces on both sides,
        /// so that <c>set.Contains(x)</c> is equivalent to <c>text.Contains(" " + x + " ")</c>
        /// for any space-free <paramref name="text"/> token x.
        /// </summary>
        private static HashSet<string> GetInnerTokens(string text)
        {
            var tokens = text.Split(' ');
            var innerTokens = new HashSet<string>();
            for (var i = 1; i < tokens.Length - 1; i++)
            {
                innerTokens.Add(tokens[i]);
            }
            return innerTokens;
        }

        private bool UserMentionedInPost(User compareUser, HashSet<string> postTokens, string regexWithSpaceFullPost)
        {
            // User.Name never contains spaces (RegexUtil.Regex strips them), so the
            // " name " containment check reduces to an inner-token lookup.
            var regexBool = postTokens.Contains(compareUser.Name ?? "");
            if (!regexBool && compareUser.Name is not null
                && UserWithSpaceTranslation.TryGetValue(compareUser.Name, out string? userName)
                && userName != compareUser.Name)
            {
                regexBool = regexWithSpaceFullPost.Contains(" " + userName + " ");
            }
            return regexBool;
        }

        private bool IsUserPartOfPost(string? userString, User compareUser, HashSet<string> postTokens, string regexWithSpaceFullPost)
        {
            if (userString is not null && NameUserTranslation.TryGetValue(userString, out User? parsedUser))
            {
                if (parsedUser.Name is not null)
                {
                    return parsedUser != compareUser && UserMentionedInPost(compareUser, postTokens, regexWithSpaceFullPost);
                }
            }
            return false;
        }

        private static string FilterOutTierDefinitionAndStrip(string line)
        {
            var tempLine = line;
            if (line.IndexOf(":") < line.Replace(".", "").IndexOf(" vs "))
            {
                tempLine = tempLine[(tempLine.IndexOf(":") + 1)..];
            }
            if (tempLine.Contains('>'))
            {
                tempLine = tempLine[(tempLine.IndexOf(">") + 1)..];
            }
            return tempLine;
        }
    }
}
