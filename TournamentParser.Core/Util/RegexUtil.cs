using System.Text.RegularExpressions;

namespace TournamentParser.Util
{
    public class RegexUtil
    {
        private readonly Regex rgx = new("[, ]");
        public string Regex(string? toFilter)
        {
            if (toFilter == null)
            {
                return "";
            }
            toFilter = rgx.Replace(toFilter, "");
            return toFilter.ToLower();
        }

        private readonly Regex rgxWithSpace = new("[,]");
        public string RegexWithSpace(string? toFilter)
        {
            if (toFilter == null)
            {
                return "";
            }
            toFilter = rgxWithSpace.Replace(toFilter, "");
            return toFilter.ToLower();
        }

        private readonly Regex rgxWithAbc = new("[^a-zA-Z0-9]");
        public string RegexWithABC(string? toFilter)
        {
            if (toFilter == null)
            {
                return "";
            }
            if (toFilter.Contains(' '))
            {
                toFilter = toFilter[..toFilter.LastIndexOf(" ")];
            }
            toFilter = rgxWithAbc.Replace(toFilter, "");
            return toFilter.ToLower();
        }

        private readonly Regex htmlRegex = new("<.*?>");
        private readonly Regex eckigRegex = new("\\[.*?\\]");
        private readonly Regex rundRegex = new("\\(.*?\\)");
        private readonly Regex weirdSpace = new(" ");
        public string StripHTML(string inputString)
        {
            return weirdSpace.Replace(rundRegex.Replace(eckigRegex.Replace(htmlRegex.Replace(inputString, ""), ""), ""), "").Trim();
        }

        private readonly Regex positionRegex = new("([0-9]+\\.) ");
        public string RemovePositions(string userString)
        {
            return positionRegex.Replace(userString, "");
        }

        private readonly Regex numberReplayRegex = new("(- [0-9]( \\| [0-9])*)");
        public string RemoveNumberReplays(string userString)
        {
            return numberReplayRegex.Replace(userString, "").Trim();
        }

        private readonly Regex reactionRegex = new("reactions:\n.*");
        public string RemoveReactions(string lineString)
        {
            return reactionRegex.Replace(lineString, "").Trim();
        }
    }
}
