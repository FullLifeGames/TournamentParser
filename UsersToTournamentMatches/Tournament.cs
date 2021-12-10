using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UsersToTournamentMatches
{
    public class Tournament
    {
        private readonly HttpClient _client;
        private readonly RegexUtil _regexUtil;

        private readonly string _officialTournamentSite = "http://www.smogon.com/forums/forums/tournaments.34/";
        private const char _quotation = '"';

        private readonly ConcurrentBag<User> _users = new();
        private readonly IDictionary<string, User> _nameUserTranslation = new ConcurrentDictionary<string, User>();
        private readonly IDictionary<int, User> _idUserTranslation = new ConcurrentDictionary<int, User>();
        private readonly IDictionary<string, string> _userWithSpaceTranslation = new ConcurrentDictionary<string, string>();

        public Tournament(HttpClient? httpClient = null)
        {
            if (httpClient != null)
            {
                _client = httpClient;
            } 
            else
            {
                _client = new HttpClient();
            }
            _regexUtil = new RegexUtil();
        }

        public async Task<IDictionary<string, User>> GetMatchesForUsers()
        {
            var threadsForForums = await GetThreadsForForums();
            var nonTourThreadsForForums = await GetNonTourThreadsForForums();

            var totalCount = 
                threadsForForums.Sum((thread) => thread.Value.Count)
                + nonTourThreadsForForums.Sum((thread) => thread.Value.Count);

            await ScanThreads(threadsForForums);
            await ScanThreads(nonTourThreadsForForums);

            foreach (var user in _nameUserTranslation.Values)
            {
                user.Matches = new ConcurrentBag<Match>(user.Matches.Where((match) => !match.Irrelevant));
                foreach (var match in user.Matches)
                {
                    if (match.Winner == null)
                    {
                        var threadName = _regexUtil.RegexWithABC(match.Thread.Name);
                        foreach (var matchCompare in user.Matches)
                        {
                            if (match != matchCompare && threadName == _regexUtil.RegexWithABC(matchCompare.Thread.Name))
                            {
                                if (match.PostDate < matchCompare.PostDate)
                                {
                                    match.Winner = user.Name;
                                    match.Finished = true;
                                }
                            }
                        }
                    }
                }
            }

            return _nameUserTranslation;

        }

        public async Task<IDictionary<string, List<string>>> GetGeneralThreadsForForums(
            string filter, Dictionary<string, string>? additionals = null)
        {
            var tournamentToLinks = new Dictionary<string, string>();
            var smogonMain = await _client.GetStringAsync("http://www.smogon.com/forums/");
            var scanStartOne = false;

            foreach (var line in smogonMain.Split('\n'))
            {
                if (scanStartOne)
                {
                    if (line.Contains("class=\"subNodeLink subNodeLink--forum"))
                    {
                        var tourName = line[(line.IndexOf(">") + 1)..];
                        tourName = tourName[..tourName.IndexOf("<")];

                        var tourUrl = line[(line.IndexOf(_quotation) + 1)..];
                        tourUrl = tourUrl[..tourUrl.IndexOf(_quotation)];
                        tourUrl = "http://www.smogon.com" + tourUrl;

                        tournamentToLinks.Add(tourName, tourUrl);
                    }
                    else if (line.Contains("node-stats\""))
                    {
                        scanStartOne = false;
                    }
                }
                else if (line.Contains($">{ filter }<"))
                {
                    scanStartOne = true;
                }
            }

            if (additionals != null)
            {
                foreach (var additional in additionals)
                {
                    if (!tournamentToLinks.ContainsKey(additional.Key))
                    {
                        tournamentToLinks.Add(additional.Key, additional.Value);
                    }
                }
            }

            IDictionary<string, List<string>> threadsForForums = new ConcurrentDictionary<string, List<string>>();

            await Parallel.ForEachAsync(tournamentToLinks, async (kv, ct) =>
                {
                    threadsForForums.Add(kv.Value, new List<string>());
                    var site = await _client.GetStringAsync(kv.Value, ct);
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

                    Console.WriteLine("Looking for scannable tournament threads in: " + kv.Value);
                    var beforeCount = threadsForForums[kv.Value].Count;
                    for (var pageCount = 1; pageCount <= pages; pageCount++)
                    {
                        site = await _client.GetStringAsync(kv.Value + "page-" + pageCount, ct);

                        foreach (var line in site.Split('\n'))
                        {
                            if (line.Contains("data-preview-url"))
                            {
                                var tempInside = line[(line.IndexOf("data-preview-url") + "data-preview-url".Length)..];
                                tempInside = tempInside[(tempInside.IndexOf(_quotation) + 1)..];
                                if (!tempInside.Contains("/preview"))
                                {
                                    continue;
                                }
                                tempInside = tempInside[..(tempInside.IndexOf("/preview") + 1)];
                                var url = "http://www.smogon.com" + tempInside;
                                threadsForForums[kv.Value].Add(url);
                            }
                        }
                    }
                    var afterCount = threadsForForums[kv.Value].Count;
                    Console.WriteLine("Found " + (afterCount - beforeCount) + " scannable tournament threads in: " + kv.Value);
                    Console.WriteLine();
                }
            );

            return threadsForForums;
        }

        public async Task<IDictionary<string, List<string>>> GetThreadsForForums()
        {
            return await GetGeneralThreadsForForums("Tournaments", new Dictionary<string, string>()
            {
                { "Standard Tournament Forums", _officialTournamentSite }
            });
        }

        public async Task<IDictionary<string, List<string>>> GetNonTourThreadsForForums()
        {
            return await GetGeneralThreadsForForums("Smogon Metagames");
        }

        public async Task ScanThreads(IDictionary<string, List<string>> threadsForForums)
        {
            await Parallel.ForEachAsync(
                threadsForForums.SelectMany(thread => thread.Value),
                async (url, ct) =>
                {
                    Console.WriteLine("Currently Scanning: " + url);
                    var beforeCount = _users.Count;
                    await AnalyzeTopic(url, ct);
                    var afterCount = _users.Count;
                    Console.WriteLine("Added " + (afterCount - beforeCount) + " Users on " + url);
                    Console.WriteLine();
                }
            );
        }

        private async Task AnalyzeTopic(string url, CancellationToken ct)
        {
            try
            {
                var site = await _client.GetStringAsync(url, ct);
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

                var currentlyUserToMatch = new Dictionary<string, List<Match>>();
                var thread = new Thread();
                var id = url[(url.LastIndexOf(".")+1)..];
                if (id.Contains('/'))
                {
                    id = id[..id.IndexOf("/")];
                }
                thread.Id = id;
                for (var pageCount = 1; pageCount <= pages; pageCount++)
                {
                    site = await _client.GetStringAsync(url + "page-" + pageCount, ct);

                    var postStarted = false;
                    var postNumber = 0 + (pageCount - 1) * 25;
                    var postDate = DateTime.Now;

                    var postedBy = "";

                    var timerHeader = false;

                    var canTakeReplay = false;

                    var dataUserId = -1;
                    var userLink = "";

                    var fullPost = new StringBuilder("");

                    var takePost = false;

                    foreach (var line in site.Split('\n'))
                    {
                        HandleLine(url, ref postStarted, ref postNumber, ref postDate, ref postedBy, ref timerHeader, line, ref canTakeReplay, ref dataUserId, ref userLink, thread, currentlyUserToMatch, ref fullPost, ref takePost);
                    }
                }
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine("HttpRequestException bei: " + url);
                Console.WriteLine(e.Message);
            }
        }

        private void HandleLine(string url, ref bool postStarted, ref int postNumber, ref DateTime postDate, ref string postedBy, ref bool timerHeader, string line, ref bool canTakeReplay, ref int dataUserId, ref string userLink, Thread thread, Dictionary<string, List<Match>> currentlyUserToMatch, ref StringBuilder fullPost, ref bool takePost)
        {
            if (line.Contains("\"articleBody\": \""))
            {

            }
            else if (line.Contains("<h1 class=\"p-title-value\">"))
            {
                thread.Name = line[..line.LastIndexOf("<")];
                thread.Name = thread.Name[(thread.Name.LastIndexOf(">") + 1)..];
            }
            else if (line.Contains("<article class=\"message-body js-selectToQuote\">"))
            {
                takePost = true;
            }
            else if (line.Contains("<dd class=\"blockStatus-message blockStatus-message--locked\">"))
            {
                thread.Locked = true;
            }
            else if (line.Contains("<article class=\"message message--post js-post js-inlineModContainer"))
            {
                postStarted = true;
                fullPost = new StringBuilder("");
                postNumber++;
                canTakeReplay = true;
            }
            else if (line.Contains("<header class=\"message-attribution message-attribution--split\">"))
            {
                timerHeader = true;
            }
            else if (line.Contains("data-date-string=\"") && timerHeader)
            {
                var temp = line[(line.IndexOf("data-date-string=\"") + "data-date-string=\"".Length)..];
                temp = temp[(temp.IndexOf("title") + "title".Length)..];
                temp = temp[(temp.IndexOf(_quotation) + 1)..];
                temp = temp[..temp.IndexOf(_quotation)];
                temp = temp.Replace("at ", "");
                postDate = DateTime.ParseExact(temp, "MMM d, yyyy h:mm tt", CultureInfo.GetCultureInfo("en-US"));
                timerHeader = false;
            }
            else if (line.StartsWith("\t</article>") && !url.Contains("-replay"))
            {
                postStarted = false;
                canTakeReplay = false;
                takePost = false;

                if (dataUserId != 0)
                {
                    if (!_idUserTranslation.ContainsKey(dataUserId))
                    {
                        if (!_nameUserTranslation.ContainsKey(_regexUtil.Regex(postedBy)))
                        {
                            var newUser = new User
                            {
                                Id = dataUserId,
                                NormalName = postedBy,
                                Name = _regexUtil.Regex(postedBy),
                                ProfileLink = userLink
                            };

                            _users.Add(newUser);
                            _nameUserTranslation.Add(_regexUtil.Regex(postedBy), newUser);
                            _userWithSpaceTranslation.Add(_regexUtil.Regex(postedBy), _regexUtil.RegexWithSpace(postedBy));
                            _idUserTranslation.Add(dataUserId, newUser);
                        }
                        else
                        {
                            var existingUser = _nameUserTranslation[_regexUtil.Regex(postedBy)];

                            if (existingUser.ProfileLink == null)
                            {
                                existingUser.Id = dataUserId;
                                existingUser.ProfileLink = userLink;
                                _idUserTranslation.Add(dataUserId, existingUser);
                            }
                            else
                            {
                                Console.WriteLine("Strange behavior with User: " + existingUser.Name);
                            }
                        }
                    }
                    else
                    {
                        if (!_nameUserTranslation.ContainsKey(_regexUtil.Regex(postedBy)))
                        {
                            var existingUser = _idUserTranslation[dataUserId];

                            if (existingUser.Name == null)
                            {
                                existingUser.NormalName = postedBy;
                                existingUser.Name = _regexUtil.Regex(postedBy);
                                _nameUserTranslation.Add(_regexUtil.Regex(postedBy), existingUser);
                                _userWithSpaceTranslation.Add(_regexUtil.Regex(postedBy), _regexUtil.RegexWithSpace(postedBy));
                            }
                            else
                            {
                                Console.WriteLine("Strange behavior with User: " + existingUser.Name);
                            }
                        }
                        else
                        {
                            // Just exists already, proceed as normal
                        }
                    }

                    var currentUser = _nameUserTranslation[_regexUtil.Regex(postedBy)];
                    var fullPostString = fullPost.ToString();
                    var regexFullPost = _regexUtil.Regex(_regexUtil.StripHTML(fullPostString));
                    var regexWithSpaceFullPost = _regexUtil.RegexWithSpace(_regexUtil.StripHTML(fullPostString)).Replace(_regexUtil.RegexWithSpace(currentUser.NormalName), "").Replace(_regexUtil.Regex(currentUser.NormalName), "");
                    if (fullPostString.Contains("replay.pokemonshowdown.com/") && postNumber != 1)
                    {
                        var notExistingMatch = true;
                        var match = new Match();
                        if (currentlyUserToMatch.ContainsKey(_regexUtil.Regex(postedBy)))
                        {
                            foreach (var currentMatch in currentlyUserToMatch[_regexUtil.Regex(postedBy)])
                            {
                                if (_nameUserTranslation[currentMatch.FirstUser] != currentUser && regexWithSpaceFullPost.Contains(" " + currentMatch.FirstUser + " ") || regexWithSpaceFullPost.Contains(" " + _userWithSpaceTranslation[currentMatch.FirstUser] + " "))
                                {
                                    match = currentMatch;
                                    notExistingMatch = false;
                                    break;
                                }
                                if (currentMatch.SecondUser != null && _nameUserTranslation[currentMatch.SecondUser] != currentUser && regexWithSpaceFullPost.Contains(" " + currentMatch.SecondUser + " ") || regexWithSpaceFullPost.Contains(" " + _userWithSpaceTranslation[currentMatch.SecondUser] + " "))
                                {
                                    match = currentMatch;
                                    notExistingMatch = false;
                                    break;
                                }
                            }
                            if (notExistingMatch && currentlyUserToMatch[_regexUtil.Regex(postedBy)].Count == 1)
                            {
                                match = currentlyUserToMatch[_regexUtil.Regex(postedBy)][0];
                                notExistingMatch = false;
                            }
                            if (notExistingMatch)
                            {
                                match = new Match
                                {
                                    FirstUser = _nameUserTranslation[_regexUtil.Regex(postedBy)].Name,
                                    Thread = thread,
                                    Finished = true,
                                };

                                // Order by name length of the users so that short common terms are ignored longer and bigger names can be respected
                                foreach (var user in _users.OrderByDescending(u => u.Name.Length))
                                {
                                    if (user != _nameUserTranslation[match.FirstUser] && (regexWithSpaceFullPost.Contains(" " + user.Name + " ") || regexWithSpaceFullPost.Contains(" " + _userWithSpaceTranslation[user.Name] + " ")))
                                    {
                                        match.SecondUser = user.Name;
                                        notExistingMatch = false;
                                        break;
                                    }
                                }
                                match.PostDate = postDate;
                                _nameUserTranslation[match.FirstUser].Matches.Add(match);
                                if (match.SecondUser != null)
                                {
                                    _nameUserTranslation[match.SecondUser].Matches.Add(match);
                                }
                            }

                            var tempLine = fullPostString;
                            while (tempLine.Contains("replay.pokemonshowdown.com/"))
                            {
                                tempLine = tempLine[tempLine.IndexOf("replay.pokemonshowdown.com/")..];
                                var quot = tempLine.Contains('"') ? tempLine.IndexOf(_quotation) : int.MaxValue;
                                var arrow = tempLine.Contains('<') ? tempLine.IndexOf("<") : int.MaxValue;
                                string link;
                                if (arrow < quot)
                                {
                                    link = tempLine[..tempLine.IndexOf("<")];
                                }
                                else
                                {
                                    link = tempLine[..tempLine.IndexOf(_quotation)];
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
                                    tempLine = tempLine[tempLine.IndexOf(_quotation)..];
                                }
                            }
                        }
                    }
                }
            }
            else if (line.Contains("class=\"username \" dir=\"auto\" data-user-id=\""))
            {
                var tempValue = "";
                tempValue = line[(line.IndexOf("data-user-id") + 5)..];
                tempValue = tempValue[(tempValue.IndexOf(_quotation) + 1)..];
                tempValue = tempValue[..tempValue.IndexOf(_quotation)];
                dataUserId = int.Parse(tempValue);

                tempValue = "";
                tempValue = line[(line.IndexOf("href") + 5)..];
                tempValue = tempValue[(tempValue.IndexOf(_quotation) + 1)..];
                tempValue = tempValue[..tempValue.IndexOf(_quotation)];
                userLink = tempValue;
            }
            else if (line.Contains("data-author=\""))
            {
                postedBy = line[(line.IndexOf("data-author") + 5)..];
                postedBy = postedBy[(postedBy.IndexOf(_quotation) + 1)..];
                postedBy = postedBy[..postedBy.IndexOf(_quotation)];
            }
            else if ((_regexUtil.StripHTML(line).Contains(" vs. ") || _regexUtil.StripHTML(line).Contains(" vs ")) && (postNumber == 1 || postNumber == 2 || url.Contains("-replay")))
            {
                var match = new Match();

                if (thread.Locked)
                {
                    match.Finished = true;
                }

                var toFilterFor = "vs";

                if (_regexUtil.StripHTML(line).Contains(" vs. "))
                {
                    toFilterFor = "vs.";
                }

                var preparedLine = FilterOutTierDefinition(_regexUtil.StripHTML(line));

                if (preparedLine != toFilterFor && preparedLine.Contains(" " + toFilterFor + " "))
                {
                    var userOne = preparedLine[..preparedLine.IndexOf(" " + toFilterFor + " ")];
                    var userTwo = preparedLine[(preparedLine.IndexOf(" " + toFilterFor + " ") + (" " + toFilterFor + " ").Length)..];

                    if (_nameUserTranslation.ContainsKey(_regexUtil.Regex(userOne)))
                    {
                        match.FirstUser = _nameUserTranslation[_regexUtil.Regex(userOne)].Name;
                    }
                    else
                    {
                        var firstUser = new User();
                        firstUser.NormalName = userOne;
                        firstUser.Name = _regexUtil.Regex(userOne);
                        match.FirstUser = firstUser.Name;
                        _users.Add(firstUser);
                        _nameUserTranslation.Add(_regexUtil.Regex(userOne), firstUser);
                        _userWithSpaceTranslation.Add(_regexUtil.Regex(userOne), _regexUtil.RegexWithSpace(userOne));
                    }
                    _nameUserTranslation[match.FirstUser].Matches.Add(match);
                    if (currentlyUserToMatch.ContainsKey(match.FirstUser))
                    {
                        currentlyUserToMatch[match.FirstUser].Add(match);
                    }
                    else
                    {
                        currentlyUserToMatch.Add(match.FirstUser, new List<Match>() { match });
                    }

                    if (_nameUserTranslation.ContainsKey(_regexUtil.Regex(userTwo)))
                    {
                        match.SecondUser = _nameUserTranslation[_regexUtil.Regex(userTwo)].Name;
                    }
                    else
                    {
                        var secondUser = new User();
                        secondUser.NormalName = userTwo;
                        secondUser.Name = _regexUtil.Regex(userTwo);
                        match.SecondUser = secondUser.Name;
                        _users.Add(secondUser);
                        _nameUserTranslation.Add(_regexUtil.Regex(userTwo), secondUser);
                        _userWithSpaceTranslation.Add(_regexUtil.Regex(userTwo), _regexUtil.RegexWithSpace(userTwo));
                    }
                    _nameUserTranslation[match.SecondUser].Matches.Add(match);
                    if (currentlyUserToMatch.ContainsKey(match.SecondUser))
                    {
                        currentlyUserToMatch[match.SecondUser].Add(match);
                    }
                    else
                    {
                        currentlyUserToMatch.Add(match.SecondUser, new List<Match>() { match });
                    }

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
                        if (_nameUserTranslation.ContainsKey(_regexUtil.Regex(winnerName)))
                        {
                            var winner = _nameUserTranslation[_regexUtil.Regex(winnerName)];
                            match.Winner = winner.Name;
                        }
                    }

                    if (line.Contains("replay.pokemonshowdown.com/"))
                    {
                        var tempLine = line;
                        while (tempLine.Contains("replay.pokemonshowdown.com/"))
                        {
                            tempLine = tempLine[tempLine.IndexOf("replay.pokemonshowdown.com/")..];
                            var quot = tempLine.Contains('"') ? tempLine.IndexOf(_quotation) : int.MaxValue;
                            var arrow = tempLine.Contains('<') ? tempLine.IndexOf("<") : int.MaxValue;
                            string link;
                            if (arrow < quot)
                            {
                                link = tempLine[..tempLine.IndexOf("<")];
                            }
                            else
                            {
                                link = tempLine[..tempLine.IndexOf(_quotation)];
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
                                tempLine = tempLine[tempLine.IndexOf(_quotation)..];
                            }
                        }
                    }

                    match.Thread = thread;
                    match.PostDate = postDate;
                }

                Match? toKeep = null;
                Match? toLoose = null;
                foreach (var otherMatch in _nameUserTranslation[match.FirstUser].Matches.Where((match) => !match.Irrelevant))
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
                    currentlyUserToMatch[toKeep.FirstUser].Remove(toLoose);
                    if (!currentlyUserToMatch[toKeep.FirstUser].Contains(toKeep))
                    {
                        currentlyUserToMatch[toKeep.FirstUser].Add(toKeep);
                    }
                    currentlyUserToMatch[toKeep.SecondUser].Remove(toLoose);
                    if (!currentlyUserToMatch[toKeep.SecondUser].Contains(toKeep))
                    {
                        currentlyUserToMatch[toKeep.SecondUser].Add(toKeep);
                    }
                }

            }
            if (takePost && !line.Contains("/likes\""))
            {
                fullPost = fullPost.Append(line).Append('\n');
            }
        }

        private string FilterOutTierDefinition(string line)
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