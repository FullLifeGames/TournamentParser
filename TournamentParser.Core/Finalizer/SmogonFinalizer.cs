using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using TournamentParser.Data;
using TournamentParser.Util;

namespace TournamentParser.Finalizer
{
    public class SmogonFinalizer : IFinalizer
    {
        private readonly RegexUtil _regexUtil = new();

        public void Finalize(IDictionary<string, User> nameUserTranslation)
        {
            foreach (var user in nameUserTranslation.Values)
            {
                user.Matches = new ConcurrentBag<Match>(user.Matches.Where((match) => !match.Irrelevant));
                foreach (var match in user.Matches)
                {
                    if (match.Winner == null)
                    {
                        var threadName = _regexUtil.RegexWithABC(match.Thread?.Name);
                        foreach (var matchCompare in user.Matches)
                        {
                            if (match != matchCompare && threadName == _regexUtil.RegexWithABC(matchCompare.Thread?.Name))
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
        }
    }
}
