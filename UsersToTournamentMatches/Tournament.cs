using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace UsersToTournamentMatches
{
    internal class Tournament
    {
        private WebClient client;

        private string officialTournamentSite = "http://www.smogon.com/forums/forums/tournaments.34/";
        private string ostForum = "http://www.smogon.com/forums/forums/official-smogon-tournament.463/";

        private string circuitTournaments = "http://www.smogon.com/forums/forums/circuit-tournaments.351/";
        private string teamTournaments = "http://www.smogon.com/forums/forums/team-tournaments.468/";

        private List<User> users = new List<User>();
        private Dictionary<string, User> nameUserTranslation = new Dictionary<string, User>();
        private Dictionary<int, User> idUserTranslation = new Dictionary<int, User>();
        private Dictionary<string, string> userWithSpaceTranslation = new Dictionary<string, string>();

        public Tournament()
        {
            client = new WebClient();
            client.Encoding = Encoding.UTF8;
        }

        private static Regex rgx = new Regex("[, ]");
        private static string Regex(string toFilter)
        {
            toFilter = rgx.Replace(toFilter, "");
            return toFilter.ToLower();
        }

        private static Regex rgxWithSpace = new Regex("[,]");
        private static string RegexWithSpace(string toFilter)
        {
            toFilter = rgxWithSpace.Replace(toFilter, "");
            return toFilter.ToLower();
        }

        public Dictionary<string, List<string>> GetThreadsForForums()
        {
            Dictionary<string, string> tournamentToLinks = new Dictionary<string, string>();
            string smogonMain = client.DownloadString("http://www.smogon.com/forums/");
            bool scanStartOne = false;

            foreach (string line in smogonMain.Split('\n'))
            {
                if (scanStartOne)
                {
                    if (line.Contains("class=\"subNodeLink subNodeLink--forum"))
                    {
                        string tourName = line.Substring(line.IndexOf(">") + 1);
                        tourName = tourName.Substring(0, tourName.IndexOf("<"));

                        string tourUrl = line.Substring(line.IndexOf("\"") + 1);
                        tourUrl = tourUrl.Substring(0, tourUrl.IndexOf("\""));
                        tourUrl = "http://www.smogon.com" + tourUrl;

                        tournamentToLinks.Add(tourName, tourUrl);
                    }
                    else if (line.Contains("node-stats\""))
                    {
                        scanStartOne = false;
                    }
                }
                else if (line.Contains(">Tournaments<"))
                {
                    scanStartOne = true;
                }
            }

            if (!tournamentToLinks.ContainsKey("Official Smogon Tournament"))
            {
                tournamentToLinks.Add("Official Smogon Tournament", ostForum);
            }
            if (!tournamentToLinks.ContainsKey("Circuit Tournaments"))
            {
                tournamentToLinks.Add("Circuit Tournaments", circuitTournaments);
            }
            if (!tournamentToLinks.ContainsKey("Team Tournaments"))
            {
                tournamentToLinks.Add("Team Tournaments", teamTournaments);
            }
            tournamentToLinks.Add("Standard Tournament Forums", officialTournamentSite);

            Dictionary<string, List<string>> threadsForForums = new Dictionary<string, List<string>>();
            foreach (KeyValuePair<string, string> kv in tournamentToLinks)
            {
                threadsForForums.Add(kv.Value, new List<string>());
                string site = client.DownloadString(kv.Value);
                int pages = 1;
                if (site.Contains("<nav class=\"pageNavWrapper"))
                {
                    string temp = site;
                    while (temp.Contains("pageNav-page"))
                    {
                        temp = temp.Substring(temp.IndexOf("pageNav-page") + "pageNav-page".Length);
                    }
                    temp = temp.Substring(temp.IndexOf(">") + 1);
                    temp = temp.Substring(temp.IndexOf(">") + 1);
                    temp = temp.Substring(0, temp.IndexOf("<"));
                    pages = int.Parse(temp);
                }

                Console.WriteLine("Looking for scannable tournament threads in: " + kv.Value);
                int beforeCount = threadsForForums[kv.Value].Count;
                for (int pageCount = 1; pageCount <= pages; pageCount++)
                {
                    site = client.DownloadString(kv.Value + "page-" + pageCount);

                    foreach (string line in site.Split('\n'))
                    {
                        if (line.Contains("data-preview-url"))
                        {
                            string tempInside = line.Substring(line.IndexOf("data-preview-url") + "data-preview-url".Length);
                            tempInside = tempInside.Substring(tempInside.IndexOf("\"") + 1);
                            if (!tempInside.Contains("/preview"))
                            {
                                continue;
                            }
                            tempInside = tempInside.Substring(0, tempInside.IndexOf("/preview") + 1);
                            string url = "http://www.smogon.com" + tempInside;
                            threadsForForums[kv.Value].Add(url);
                        }
                    }
                }
                int afterCount = threadsForForums[kv.Value].Count;
                Console.WriteLine("Found " + (afterCount - beforeCount) + " scannable tournament threads in: " + kv.Value);
                Console.WriteLine();
            }
            return threadsForForums;
        }


        public Dictionary<string, User> GetMatchesForUsers()
        {
            Dictionary<string, List<string>> threadsForForums = GetThreadsForForums();

            int totalCount = 0;
            foreach (KeyValuePair<string, List<string>> kv in threadsForForums)
            {
                totalCount += kv.Value.Count;
            }

            foreach (KeyValuePair<string, List<string>> kv in threadsForForums)
            {
                foreach (string url in kv.Value)
                {
                    Console.WriteLine("Currently Scanning: " + url);
                    int beforeCount = users.Count;
                    AnalyzeTopic(url, client);
                    int afterCount = users.Count;
                    Console.WriteLine("Added " + (afterCount - beforeCount) + " Users");
                    Console.WriteLine();
                }
            }

            return nameUserTranslation;

        }

        private void AnalyzeTopic(string url, WebClient client)
        {
            try
            {
                string site = client.DownloadString(url);
                int pages = 1;
                if (site.Contains("<nav class=\"pageNavWrapper"))
                {
                    string temp = site;
                    while (temp.Contains("pageNav-page"))
                    {
                        temp = temp.Substring(temp.IndexOf("pageNav-page") + "pageNav-page".Length);
                    }
                    temp = temp.Substring(temp.IndexOf(">") + 1);
                    temp = temp.Substring(temp.IndexOf(">") + 1);
                    temp = temp.Substring(0, temp.IndexOf("<"));
                    pages = int.Parse(temp);
                }

                Dictionary<string, List<Match>> currentlyUserToMatch = new Dictionary<string, List<Match>>();
                Thread thread = new Thread();
                thread.link = url;
                for (int pageCount = 1; pageCount <= pages; pageCount++)
                {
                    site = client.DownloadString(url + "page-" + pageCount);

                    bool blockStarted = false;
                    string blockText = "";

                    bool postStarted = false;
                    int postNumber = 0 + (pageCount - 1) * 25;
                    string postLink = "";
                    int postLikes = 0;
                    DateTime postDate = DateTime.Now;

                    string postedBy = "";

                    bool likeStarted = false;

                    bool timerHeader = false;

                    string lastLine = "";

                    bool canTakeReplay = false;

                    int dataUserId = -1;
                    string userLink = "";

                    StringBuilder fullPost = new StringBuilder("");

                    List<string> currentTeams = new List<string>();

                    bool takePost = false;

                    foreach (string line in site.Split('\n'))
                    {
                        HandleLine(url, pageCount, ref blockStarted, ref blockText, ref postStarted, ref postNumber, ref postLink, ref postLikes, ref postDate, ref postedBy, ref likeStarted, ref timerHeader, currentTeams, line, ref lastLine, ref canTakeReplay, ref dataUserId, ref userLink, thread, currentlyUserToMatch, ref fullPost, ref takePost);
                    }
                }
            }
            catch (WebException e)
            {
                Console.WriteLine("WebException bei: " + url);
                Console.WriteLine(e.Message);
                Console.ReadLine();
            }
        }

        private void HandleLine(string url, int pageCount, ref bool blockStarted, ref string blockText, ref bool postStarted, ref int postNumber, ref string postLink, ref int postLikes, ref DateTime postDate, ref string postedBy, ref bool likeStarted, ref bool timerHeader, List<string> currentTeams, string line, ref string lastLine, ref bool canTakeReplay, ref int dataUserId, ref string userLink, Thread thread, Dictionary<string, List<Match>> currentlyUserToMatch, ref StringBuilder fullPost, ref bool takePost)
        {
            if(line.Contains("<h1 class=\"p-title-value\">"))
            {
                thread.name = line.Substring(0, line.LastIndexOf("<"));
                thread.name = thread.name.Substring(thread.name.LastIndexOf(">") + 1);
            }
            else if(line.Contains("<article class=\"message-body js-selectToQuote\">"))
            {
                takePost = true;
            }
            else if(line.Contains("<dd class=\"blockStatus-message blockStatus-message--locked\">"))
            {
                thread.locked = true;
            }
            else if (line.Contains("<article class=\"message message--post js-post js-inlineModContainer"))
            {
                postStarted = true;
                fullPost = new StringBuilder("");
                postNumber++;
                canTakeReplay = true;
            }
            else if (line.Contains("<header class=\"message-attribution\">"))
            {
                timerHeader = true;
            }
            else if (line.Contains("data-date-string=\"") && timerHeader)
            {
                string temp = line.Substring(line.IndexOf("data-date-string=\"") + "data-date-string=\"".Length);
                temp = temp.Substring(temp.IndexOf("title") + "title".Length);
                temp = temp.Substring(temp.IndexOf("\"") + 1);
                temp = temp.Substring(0, temp.IndexOf("\""));
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
                    if (!idUserTranslation.ContainsKey(dataUserId))
                    {
                        if (!nameUserTranslation.ContainsKey(Regex(postedBy)))
                        {
                            User newUser = new User
                            {
                                id = dataUserId,
                                normalName = postedBy,
                                name = Regex(postedBy),
                                profileLink = "http://www.smogon.com" + userLink
                            };

                            users.Add(newUser);
                            nameUserTranslation.Add(Regex(postedBy), newUser);
                            userWithSpaceTranslation.Add(Regex(postedBy), RegexWithSpace(postedBy));
                            idUserTranslation.Add(dataUserId, newUser);
                        }
                        else
                        {
                            User existingUser = nameUserTranslation[Regex(postedBy)];

                            if (existingUser.profileLink == null)
                            {
                                existingUser.id = dataUserId;
                                existingUser.profileLink = "http://www.smogon.com" + userLink;
                                idUserTranslation.Add(dataUserId, existingUser);
                            }
                            else
                            {
                                Console.WriteLine("Strange behavior with User: " + existingUser.name);
                            }
                        }
                    }
                    else
                    {
                        if (!nameUserTranslation.ContainsKey(Regex(postedBy)))
                        {
                            User existingUser = idUserTranslation[dataUserId];

                            if (existingUser.name == null)
                            {
                                existingUser.normalName = postedBy;
                                existingUser.name = Regex(postedBy);
                                nameUserTranslation.Add(Regex(postedBy), existingUser);
                                userWithSpaceTranslation.Add(Regex(postedBy), RegexWithSpace(postedBy));
                            }
                            else
                            {
                                Console.WriteLine("Strange behavior with User: " + existingUser.name);
                            }
                        }
                        else
                        {
                            // Just exists already, proceed as normal
                        }
                    }

                    User currentUser = nameUserTranslation[Regex(postedBy)];
                    string fullPostString = fullPost.ToString();
                    string regexFullPost = Regex(StripHTML(fullPostString));
                    string regexWithSpaceFullPost = RegexWithSpace(StripHTML(fullPostString)).Replace(RegexWithSpace(currentUser.normalName), "").Replace(Regex(currentUser.normalName), "");
                    if (fullPostString.Contains("replay.pokemonshowdown.com/") && postNumber != 1)
                    {
                        bool notExistingMatch = true;
                        Match match = new Match();
                        if (currentlyUserToMatch.ContainsKey(Regex(postedBy)))
                        {
                            foreach (Match currentMatch in currentlyUserToMatch[Regex(postedBy)])
                            {
                                if (nameUserTranslation[currentMatch.firstUser] != currentUser && regexWithSpaceFullPost.Contains(" " + currentMatch.firstUser + " ") || regexWithSpaceFullPost.Contains(" " + userWithSpaceTranslation[currentMatch.firstUser] + " "))
                                {
                                    match = currentMatch;
                                    notExistingMatch = false;
                                    break;
                                }
                                if (currentMatch.secondUser != null && nameUserTranslation[currentMatch.secondUser] != currentUser && regexWithSpaceFullPost.Contains(" " + currentMatch.secondUser + " ") || regexWithSpaceFullPost.Contains(" " + userWithSpaceTranslation[currentMatch.secondUser] + " "))
                                {
                                    match = currentMatch;
                                    notExistingMatch = false;
                                    break;
                                }
                            }
                            if (notExistingMatch && currentlyUserToMatch[Regex(postedBy)].Count == 1)
                            {
                                match = currentlyUserToMatch[Regex(postedBy)][0];
                                notExistingMatch = false;
                            }
                            if (notExistingMatch)
                            {
                                match = new Match
                                {
                                    firstUser = nameUserTranslation[Regex(postedBy)].name,
                                    thread = thread,
                                    finished = true,
                                };


                                // Order by name length of the users so that short common terms are ignored longer and bigger names can be respected
                                users = users.OrderByDescending(u => u.name.Length).ToList();

                                foreach (User user in users)
                                {
                                    if (user != nameUserTranslation[match.firstUser] && (regexWithSpaceFullPost.Contains(" " + user.name + " ") || regexWithSpaceFullPost.Contains(" " + userWithSpaceTranslation[user.name] + " ")))
                                    {
                                        match.secondUser = user.name;
                                        notExistingMatch = false;
                                        break;
                                    }
                                }
                                match.postDate = postDate;
                                nameUserTranslation[match.firstUser].matches.Add(match);
                                if (match.secondUser != null)
                                {
                                    nameUserTranslation[match.secondUser].matches.Add(match);
                                }
                            }
                            
                            string tempLine = fullPostString;
                            while (tempLine.Contains("replay.pokemonshowdown.com/"))
                            {
                                tempLine = tempLine.Substring(tempLine.IndexOf("replay.pokemonshowdown.com/"));
                                int quot = tempLine.Contains("\"") ? tempLine.IndexOf("\"") : int.MaxValue;
                                int arrow = tempLine.Contains("<") ? tempLine.IndexOf("<") : int.MaxValue;
                                string link;
                                if (arrow < quot)
                                {
                                    link = tempLine.Substring(0, tempLine.IndexOf("<"));
                                }
                                else
                                {
                                    link = tempLine.Substring(0, tempLine.IndexOf("\""));
                                }
                                if (!match.replays.Contains("https://" + link))
                                {
                                    match.replays.Add("https://" + link);
                                    match.finished = true;
                                }
                                if (arrow < quot)
                                {
                                    tempLine = tempLine.Substring(tempLine.IndexOf("<"));
                                }
                                else
                                {
                                    tempLine = tempLine.Substring(tempLine.IndexOf("\""));
                                }
                            }
                        }
                    }
                }
            }
            else if (line.Contains("class=\"username \" dir=\"auto\" data-user-id=\""))
            {
                string tempValue = "";
                tempValue = line.Substring(line.IndexOf("data-user-id") + 5);
                tempValue = tempValue.Substring(tempValue.IndexOf("\"") + 1);
                tempValue = tempValue.Substring(0, tempValue.IndexOf("\""));
                dataUserId = int.Parse(tempValue);

                tempValue = "";
                tempValue = line.Substring(line.IndexOf("href") + 5);
                tempValue = tempValue.Substring(tempValue.IndexOf("\"") + 1);
                tempValue = tempValue.Substring(0, tempValue.IndexOf("\""));
                userLink = tempValue;
            }
            else if (line.Contains("data-author=\""))
            {
                postedBy = line.Substring(line.IndexOf("data-author") + 5);
                postedBy = postedBy.Substring(postedBy.IndexOf("\"") + 1);
                postedBy = postedBy.Substring(0, postedBy.IndexOf("\""));
            }
            else if ((StripHTML(line).Contains(" vs. ") || StripHTML(line).Contains(" vs ")) && (postNumber == 1 || postNumber == 2))
            {
                Match match = new Match();

                if (thread.locked)
                {
                    match.finished = true;
                }

                string toFilterFor = "vs";

                if(StripHTML(line).Contains(" vs. "))
                {
                    toFilterFor = "vs.";
                }

                string preparedLine = FilterOutTierDefinition(StripHTML(FilterBannedTerms(line)));

                if (preparedLine != toFilterFor && preparedLine.Contains(" " + toFilterFor + " "))
                {
                    string userOne = preparedLine.Substring(0, preparedLine.IndexOf(" " + toFilterFor + " "));
                    string userTwo = preparedLine.Substring(preparedLine.IndexOf(" " + toFilterFor + " ") + (" " + toFilterFor + " ").Length);

                    if (nameUserTranslation.ContainsKey(Regex(userOne)))
                    {
                        match.firstUser = nameUserTranslation[Regex(userOne)].name;
                    }
                    else
                    {
                        User firstUser = new User();
                        firstUser.normalName = userOne;
                        firstUser.name = Regex(userOne);
                        match.firstUser = firstUser.name;
                        users.Add(firstUser);
                        nameUserTranslation.Add(Regex(userOne), firstUser);
                        userWithSpaceTranslation.Add(Regex(userOne), RegexWithSpace(userOne));
                    }
                    nameUserTranslation[match.firstUser].matches.Add(match);
                    if (currentlyUserToMatch.ContainsKey(match.firstUser))
                    {
                        currentlyUserToMatch[match.firstUser].Add(match);
                    }
                    else
                    {
                        currentlyUserToMatch.Add(match.firstUser, new List<Match>() { match });
                    }

                    if (nameUserTranslation.ContainsKey(Regex(userTwo)))
                    {
                        match.secondUser = nameUserTranslation[Regex(userTwo)].name;
                    }
                    else
                    {
                        User secondUser = new User();
                        secondUser.normalName = userTwo;
                        secondUser.name = Regex(userTwo);
                        match.secondUser = secondUser.name;
                        users.Add(secondUser);
                        nameUserTranslation.Add(Regex(userTwo), secondUser);
                        userWithSpaceTranslation.Add(Regex(userTwo), RegexWithSpace(userTwo));
                    }
                    nameUserTranslation[match.secondUser].matches.Add(match);
                    if (currentlyUserToMatch.ContainsKey(match.secondUser))
                    {
                        currentlyUserToMatch[match.secondUser].Add(match);
                    }
                    else
                    {
                        currentlyUserToMatch.Add(match.secondUser, new List<Match>() { match });
                    }

                    if (line.Contains("<b>") || line.Contains("</b>"))
                    {
                        match.finished = true;
                        string winnerName = line;
                        if (winnerName.Contains("<b>"))
                        {
                            winnerName = winnerName.Substring(winnerName.IndexOf("<b>") + 3);
                        }
                        if (winnerName.Contains("</b>"))
                        {
                            winnerName = winnerName.Substring(0, winnerName.IndexOf("</b>"));
                        }
                        winnerName = StripHTML(winnerName);
                        if (nameUserTranslation.ContainsKey(Regex(winnerName)))
                        {
                            User winner = nameUserTranslation[Regex(winnerName)];
                            match.winner = winner.name;
                        }
                    }

                    if (line.Contains("replay.pokemonshowdown.com/"))
                    {
                        string tempLine = line;
                        while (tempLine.Contains("replay.pokemonshowdown.com/"))
                        {
                            tempLine = tempLine.Substring(tempLine.IndexOf("replay.pokemonshowdown.com/"));
                            int quot = tempLine.Contains("\"") ? tempLine.IndexOf("\"") : int.MaxValue;
                            int arrow = tempLine.Contains("<") ? tempLine.IndexOf("<") : int.MaxValue;
                            string link;
                            if (arrow < quot)
                            {
                                link = tempLine.Substring(0, tempLine.IndexOf("<"));
                            }
                            else
                            {
                                link = tempLine.Substring(0, tempLine.IndexOf("\""));
                            }
                            if (!match.replays.Contains("https://" + link))
                            {
                                match.replays.Add("https://" + link);
                                match.finished = true;
                            }
                            if (arrow < quot)
                            {
                                tempLine = tempLine.Substring(tempLine.IndexOf("<"));
                            }
                            else
                            {
                                tempLine = tempLine.Substring(tempLine.IndexOf("\""));
                            }
                        }
                    }

                    match.thread = thread;
                    match.postDate = postDate;
                }
                
                Match toKeep = null;
                Match toLoose = null;
                foreach (Match otherMatch in nameUserTranslation[match.firstUser].matches)
                {
                    if(otherMatch != match)
                    {
                        if(otherMatch.firstUser == match.firstUser && otherMatch.secondUser == match.secondUser)
                        {
                            // A week between the matches post date is apart => VERY strong correlation, so assuming they are the same matches
                            if (otherMatch.postDate.AddDays(7) <= match.postDate && otherMatch.postDate.AddDays(-7) >= match.postDate)
                            {
                                if(otherMatch.postDate > match.postDate)
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
                if(toKeep != null && toLoose != null)
                {
                    foreach (string replay in toLoose.replays)
                    {
                        if (!toKeep.replays.Contains(replay))
                        {
                            toKeep.replays.Add(replay);
                        }
                    }
                    if(toKeep.winner == null && toLoose.winner != null)
                    {
                        toKeep.winner = toLoose.winner;
                    }
                    nameUserTranslation[toKeep.firstUser].matches.Remove(toLoose);
                    nameUserTranslation[toKeep.secondUser].matches.Remove(toLoose);
                    currentlyUserToMatch[toKeep.firstUser].Remove(toLoose);
                    currentlyUserToMatch[toKeep.firstUser].Add(toKeep);
                    currentlyUserToMatch[toKeep.secondUser].Remove(toLoose);
                    currentlyUserToMatch[toKeep.secondUser].Add(toKeep);
                }

            }
            if (takePost && !line.Contains("/likes\""))
            {
                fullPost = fullPost.Append(line).Append("\n");
            }
        }

        private string FilterBannedTerms(string v)
        {
            return v.Replace("(activity)", "");
        }

        private string FilterOutTierDefinition(string line)
        {
            string tempLine = line;
            if(line.IndexOf(":") < line.Replace(".", "").IndexOf(" vs "))
            {
                tempLine = tempLine.Substring(tempLine.IndexOf(":") + 1);
            }
            return tempLine;
        }

        private Regex htmlRegex = new Regex("<.*?>");
        private Regex eckigRegex = new Regex("\\[.*?\\]");
        private string StripHTML(string inputString)
        {
            return eckigRegex.Replace(htmlRegex.Replace(inputString, ""), "").Trim();
        }
    }
}