using Microsoft.Extensions.Caching.Distributed;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using TournamentParser.Core.Data;
using TournamentParser.Data;
using TournamentParser.Util;

namespace TournamentParser.ThreadScanner
{
    public class SmogonThreadScanner : IThreadScanner
    {
        public SmogonThreadScanner() : this(null) { }

        private readonly IDistributedCache? _cache;
        public SmogonThreadScanner(IDistributedCache? cache)
        {
            _cache = cache;
        }

        public ConcurrentBag<User> Users { get; } = new();
        public ConcurrentDictionary<string, User> NameUserTranslation { get; } = new ConcurrentDictionary<string, User>();
        public ConcurrentDictionary<int, User> IdUserTranslation { get; } = new ConcurrentDictionary<int, User>();
        public ConcurrentDictionary<string, string> UserWithSpaceTranslation { get; } = new ConcurrentDictionary<string, string>();

        private readonly RegexUtil _regexUtil = new();

        private const string ShowdownReplayString = "replay.pokemonshowdown.com/";

        public async Task ScanThreads(IDictionary<string, List<string>> threadsForForums)
        {
            var toSet = new Dictionary<string, string>();

            await Parallel.ForEachAsync(
                threadsForForums.SelectMany(thread => thread.Value),
                Common.ParallelOptions,
                async (url, ct) =>
                {
                    Console.WriteLine("Currently Scanning: " + url);
                    var previousUsers = Users.Count;
                    TopicAnalyzeResult analyzeResult;
                    if (await LastIdIsCurrent(url))
                    {
                        analyzeResult = JsonConvert.DeserializeObject<TopicAnalyzeResult>(_cache!.GetString(url)!)!;
                        await AnalyzeTopic(url, analyzeResult.CollectedLinks, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        analyzeResult = await AnalyzeTopic(url, ct).ConfigureAwait(false);
                        if (!toSet.ContainsKey(url))
                        {
                            toSet.Add(url, JsonConvert.SerializeObject(analyzeResult));
                        }
                    }
                    var nowUsers = Users.Count;
                    Console.WriteLine("Added " + (nowUsers - previousUsers) + " Users on " + url);
                    Console.WriteLine();
                }
            ).ConfigureAwait(false);

            Console.WriteLine("Writing to cache");
            foreach (var settingValue in toSet)
            {
                _cache?.SetString(settingValue.Key, settingValue.Value);
            }
            Console.WriteLine("Wrote to cache");
        }

        private async Task<bool> LastIdIsCurrent(string fullUrl)
        {
            if (_cache == null)
            {
                return false;
            }

            string? cachedResults = null;
            try
            {
                cachedResults = _cache.GetString(fullUrl);
            }
            catch (NullReferenceException)
            {
            }
            if (cachedResults == null)
            {
                return false;
            }

            var analyzeResult = JsonConvert.DeserializeObject<TopicAnalyzeResult>(cachedResults);
            if (analyzeResult == null)
            {
                return false;
            }

            // If the last post is older than one year, assumption is that no new activity with relevant matches will be posted
            if (analyzeResult.LastPost < DateTime.Now.AddYears(-1))
            {
                return true;
            }

            var site = await Common.HttpClient.GetStringAsync(fullUrl + "page-" + analyzeResult.NumberOfPages).ConfigureAwait(false);
            var numberOfPages = GetNumberOfPages(site);
            if (numberOfPages != analyzeResult.NumberOfPages)
            {
                return false;
            }

            var lineDataHandler = new LineDataHandler();
            foreach (var line in site.Split('\n'))
            {
                if (line.Contains("<header class=\"message-attribution message-attribution--split\">"))
                {
                    lineDataHandler.TimerHeader = true;
                }
                else if (line.Contains("data-date-string=\"") && lineDataHandler.TimerHeader)
                {
                    var temp = line[(line.IndexOf("data-date-string=\"") + "data-date-string=\"".Length)..];
                    temp = temp[(temp.IndexOf("title") + "title".Length)..];
                    temp = temp[(temp.IndexOf("\"") + 1)..];
                    temp = temp[..temp.IndexOf("\"")];
                    temp = temp.Replace("at ", "");
                    lineDataHandler.PostDate = DateTime.ParseExact(temp, "MMM d, yyyy h:mm tt", CultureInfo.GetCultureInfo("en-US"));
                    lineDataHandler.TimerHeader = false;
                }
            }

            return lineDataHandler.PostDate == analyzeResult.LastPost;
        }

        private static int GetNumberOfPages(string site)
        {
            var pages = 1;
            if (site.Contains("<nav class=\"pageNavWrapper"))
            {
                var temp = site;
                while (temp.Contains("pageNav-page"))
                {
                    temp = temp[(temp.IndexOf("pageNav-page") + "pageNav-page".Length)..];
                }
                temp = temp[(temp.IndexOf(">") + 1)..];
                temp = temp[(temp.IndexOf(">") + 1)..];
                temp = temp[..temp.IndexOf("<")];
                pages = int.Parse(temp);
            }

            return pages;
        }

        public async Task<TopicAnalyzeResult> AnalyzeTopic(string url, System.Threading.CancellationToken ct)
        {
            var pages = 1;
            var latestPost = DateTime.UnixEpoch;
            var collectedLinks = new List<string>();
            try
            {
                var currentlyUserToMatch = new Dictionary<string, List<TournamentMatch>>();
                var thread = new Thread();
                var id = url[(url.LastIndexOf(".") + 1)..];
                if (id.Contains('/'))
                {
                    id = id[..id.IndexOf("/")];
                }
                thread.Id = id;
                for (var pageCount = 1; pageCount <= pages; pageCount++)
                {
                    var site = await Common.HttpClient.GetStringAsync(url + "page-" + pageCount, ct).ConfigureAwait(false);
                    if (pages == 1)
                    {
                        pages = GetNumberOfPages(site);
                    }

                    var lineDataHandler = new LineDataHandler()
                    {
                        PostNumber = (pageCount - 1) * 25
                    };

                    foreach (var line in site.Split('\n'))
                    {
                        HandleLine(url, line, thread, currentlyUserToMatch, lineDataHandler);
                    }
                    latestPost = lineDataHandler.PostDate;
                    collectedLinks.Add(string.Join('\n', lineDataHandler.FullImportantSiteBits));
                }
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine("HttpRequestException bei: " + url);
                Console.WriteLine(e.Message);
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
                var thread = new Thread();
                var id = url[(url.LastIndexOf(".") + 1)..];
                if (id.Contains('/'))
                {
                    id = id[..id.IndexOf("/")];
                }
                thread.Id = id;
                for (var pageCount = 0; pageCount < collectedLinks.Count; pageCount++)
                {
                    var site = collectedLinks[pageCount];

                    var lineDataHandler = new LineDataHandler()
                    {
                        PostNumber = pageCount * 25
                    };

                    foreach (var line in site.Split('\n'))
                    {
                        HandleLine(url, line, thread, currentlyUserToMatch, lineDataHandler);
                    }
                }
            }, ct);
        }

        private void HandleLine(string url, string line, Thread thread, Dictionary<string, List<TournamentMatch>> currentlyUserToMatch, LineDataHandler lineDataHandler)
        {
            var keepLine = true;
            if (line.Contains("\"articleBody\": \""))
            {
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
            else if (line.Contains("data-date-string=\"") && lineDataHandler.TimerHeader)
            {
                var temp = line[(line.IndexOf("data-date-string=\"") + "data-date-string=\"".Length)..];
                temp = temp[(temp.IndexOf("title") + "title".Length)..];
                temp = temp[(temp.IndexOf(Common.Quotation) + 1)..];
                temp = temp[..temp.IndexOf(Common.Quotation)];
                temp = temp.Replace("at ", "");
                lineDataHandler.PostDate = DateTime.ParseExact(temp, "MMM d, yyyy h:mm tt", CultureInfo.GetCultureInfo("en-US"));
                lineDataHandler.TimerHeader = false;
            }
            else if (line.StartsWith("\t</article>") && !url.Contains("-replay"))
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
            else if (
                (_regexUtil.StripHTML(line).Contains(GetMatchFilterString(line)))
                && IsReplayCollectionPost(url, lineDataHandler)
            )
            {
                var match = DetermineMatch(line, thread, currentlyUserToMatch, lineDataHandler);
                // Uncommented as this expects to remove replay thread matches
                //lineDataHandler.LastMatch = AnalyzeWhatToKeep(currentlyUserToMatch, match);
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

        private TournamentMatch AnalyzeWhatToKeep(Dictionary<string, List<TournamentMatch>> currentlyUserToMatch, TournamentMatch match)
        {
            TournamentMatch? toKeep = null;
            TournamentMatch? toLoose = null;
            if (match.FirstUser != null && NameUserTranslation.ContainsKey(match.FirstUser))
            {
                foreach (var otherMatch in NameUserTranslation[match.FirstUser].Matches.Where((match) => !match.Irrelevant))
                {
                    if (otherMatch != match)
                    {
                        if (otherMatch.FirstUser == match.FirstUser && otherMatch.SecondUser == match.SecondUser)
                        {
                            // A week between the matches post date is apart => VERY strong correlation, so assuming they are the same matches (or replays are the same => so probably the same game)
                            if ((otherMatch.PostDate.AddDays(7) >= match.PostDate && otherMatch.PostDate.AddDays(-7) <= match.PostDate) || (otherMatch.Replays.Count > 0 && otherMatch.Replays.SequenceEqual(match.Replays)))
                            {
                                if (otherMatch.PostDate > match.PostDate)
                                {
                                    toKeep = match;
                                    toLoose = otherMatch;
                                }
                                else
                                {
                                    toKeep = otherMatch;
                                    toLoose = match;
                                }
                                break;
                            }
                        }
                    }
                }
            }
            if (toKeep != null && toLoose != null)
            {
                foreach (var replay in toLoose.Replays)
                {
                    if (!toKeep.Replays.Contains(replay))
                    {
                        toKeep.Replays.Add(replay);
                    }
                }
                if (toKeep.Winner == null && toLoose.Winner != null)
                {
                    toKeep.Winner = toLoose.Winner;
                }
                toLoose.Irrelevant = true;
                if (toKeep.FirstUser != null && currentlyUserToMatch.ContainsKey(toKeep.FirstUser))
                {
                    currentlyUserToMatch[toKeep.FirstUser].Remove(toLoose);
                    if (!currentlyUserToMatch[toKeep.FirstUser].Contains(toKeep))
                    {
                        currentlyUserToMatch[toKeep.FirstUser].Add(toKeep);
                    }
                }
                if (toKeep.SecondUser != null && currentlyUserToMatch.ContainsKey(toKeep.SecondUser))
                {
                    currentlyUserToMatch[toKeep.SecondUser].Remove(toLoose);
                    if (!currentlyUserToMatch[toKeep.SecondUser].Contains(toKeep))
                    {
                        currentlyUserToMatch[toKeep.SecondUser].Add(toKeep);
                    }
                }
            }
            return toKeep ?? match;
        }

        private TournamentMatch DetermineMatch(string line, Thread thread, Dictionary<string, List<TournamentMatch>> currentlyUserToMatch, LineDataHandler lineDataHandler)
        {
            var match = new TournamentMatch();

            if (thread.Locked)
            {
                match.Finished = true;
            }

            var toFilterFor = GetMatchFilterString(line);

            var preparedLine = FilterOutTierDefinition(_regexUtil.StripHTML(line));

            if (preparedLine != toFilterFor && preparedLine.Contains(" " + toFilterFor + " "))
            {
                SetupUsers(currentlyUserToMatch, match, toFilterFor, preparedLine);

                DetermineWinner(line, match);

                AddReplays(line, match);

                match.Thread = thread;
                match.PostDate = lineDataHandler.PostDate;
            }

            return match;
        }

        private void SetupUsers(Dictionary<string, List<TournamentMatch>> currentlyUserToMatch, TournamentMatch match, string toFilterFor, string preparedLine)
        {
            var userOne = preparedLine[..preparedLine.IndexOf(" " + toFilterFor + " ")];
            var userTwo = preparedLine[(preparedLine.IndexOf(" " + toFilterFor + " ") + (" " + toFilterFor + " ").Length)..];

            userOne = _regexUtil.RemovePositions(userOne);
            userTwo = _regexUtil.RemovePositions(userTwo);

            userOne = _regexUtil.RemoveNumberReplays(userOne);
            userTwo = _regexUtil.RemoveNumberReplays(userTwo);

            var regexUserOne = _regexUtil.Regex(userOne);
            var regexUserTwo = _regexUtil.Regex(userTwo);

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
            if (currentlyUserToMatch.ContainsKey(regexUserOne))
            {
                currentlyUserToMatch[regexUserOne].Add(match);
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
            if (currentlyUserToMatch.ContainsKey(regexUserTwo))
            {
                currentlyUserToMatch[regexUserTwo].Add(match);
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
                if (NameUserTranslation.ContainsKey(_regexUtil.Regex(winnerName)))
                {
                    var winner = NameUserTranslation[_regexUtil.Regex(winnerName)];
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

        private string GetMatchFilterString(string line)
        {
            var toFilterFor = "vs";

            if (_regexUtil.StripHTML(line).Contains(" vs. "))
            {
                toFilterFor = "vs.";
            }

            if (_regexUtil.StripHTML(line).Contains(" VS "))
            {
                toFilterFor = "VS";
            }

            if (_regexUtil.StripHTML(line).Contains(" VS. "))
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
                if (!IdUserTranslation.ContainsKey(lineDataHandler.DataUserId))
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
                        var existingUser = IdUserTranslation[lineDataHandler.DataUserId];

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
                var regexFullPost = _regexUtil.Regex(_regexUtil.StripHTML(fullPostString));
                var regexWithSpaceFullPost = _regexUtil.RegexWithSpace(_regexUtil.StripHTML(fullPostString)).Replace(_regexUtil.RegexWithSpace(currentUser.NormalName), "").Replace(_regexUtil.Regex(currentUser.NormalName), "");
                regexWithSpaceFullPost = _regexUtil.RemoveReactions(regexWithSpaceFullPost);
                if (fullPostString.Contains(ShowdownReplayString) && lineDataHandler.PostNumber != 1 && currentUser is not null)
                {
                    var notExistingMatch = true;
                    var match = new TournamentMatch();
                    if (currentlyUserToMatch.ContainsKey(postedByRegex))
                    {
                        foreach (var currentMatch in currentlyUserToMatch[postedByRegex])
                        {
                            if (IsUserPartOfPost(currentMatch.FirstUser, currentUser, regexWithSpaceFullPost)
                                || IsUserPartOfPost(currentMatch.SecondUser, currentUser, regexWithSpaceFullPost))
                            {
                                match = currentMatch;
                                notExistingMatch = false;
                                break;
                            }
                        }
                        if (notExistingMatch && currentlyUserToMatch[postedByRegex].Count == 1)
                        {
                            match = currentlyUserToMatch[postedByRegex][0];
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
                            foreach (var user in Users.OrderByDescending(u => u.Name?.Length))
                            {
                                if (IsUserPartOfPost(match.FirstUser, user, regexWithSpaceFullPost))
                                {
                                    match.SecondUser = user.Name;
                                    notExistingMatch = false;
                                    break;
                                }
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

        private bool IsUserPartOfPost(string? userString, User compareUser, string regexWithSpaceFullPost)
        {
            if (userString is not null && NameUserTranslation.TryGetValue(userString, out User? parsedUser))
            {
                if (parsedUser.Name is not null)
                {
                    var returnBool = true;
                    returnBool = returnBool && parsedUser != compareUser;
                    var regexBool = regexWithSpaceFullPost.Contains(" " + compareUser.Name + " ");
                    if (compareUser.Name is not null && UserWithSpaceTranslation.TryGetValue(compareUser.Name, out string? userName))
                    {
                        regexBool = regexBool || regexWithSpaceFullPost.Contains(" " + userName + " ");
                    }
                    return returnBool && regexBool;
                }
            }
            return false;
        }

        private static string FilterOutTierDefinition(string line)
        {
            var tempLine = line;
            if (line.IndexOf(":") < line.Replace(".", "").IndexOf(" vs "))
            {
                tempLine = tempLine[(tempLine.IndexOf(":") + 1)..];
            }
            return tempLine;
        }
    }
}
