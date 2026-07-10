using System;
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
            // Thread names repeat across many users and matches; normalize each distinct name once.
            var normalizedThreadNames = new Dictionary<string, string>();
            string NormalizeThreadName(TournamentMatch match)
            {
                var rawName = match.Thread?.Name ?? "";
                if (!normalizedThreadNames.TryGetValue(rawName, out var normalized))
                {
                    normalized = _regexUtil.RegexWithABC(rawName);
                    normalizedThreadNames.Add(rawName, normalized);
                }
                return normalized;
            }

            foreach (var user in nameUserTranslation.Values)
            {
                var keptMatches = user.Matches.Where((match) => !match.Irrelevant).ToArray();
                user.Matches = new ConcurrentBag<TournamentMatch>(keptMatches);

                // A match without a recorded winner is won by this user if the user has a
                // later match in the same (normalized) thread: they must have advanced.
                var latestPostDates = new Dictionary<string, DateTime>();
                foreach (var match in keptMatches)
                {
                    var threadName = NormalizeThreadName(match);
                    if (!latestPostDates.TryGetValue(threadName, out var latest) || latest < match.PostDate)
                    {
                        latestPostDates[threadName] = match.PostDate;
                    }
                }
                foreach (var match in keptMatches)
                {
                    if (match.Winner == null && match.PostDate < latestPostDates[NormalizeThreadName(match)])
                    {
                        match.Winner = user.Name;
                        match.Finished = true;
                    }
                }
            }
        }
    }
}
