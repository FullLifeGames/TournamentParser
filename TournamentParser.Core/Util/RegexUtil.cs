using System.Text.RegularExpressions;

namespace TournamentParser.Util
{
    public partial class RegexUtil
    {
        [GeneratedRegex("[, ]")]
        private static partial Regex CommaSpaceRegex();
        public string Regex(string? toFilter)
        {
            if (toFilter == null)
            {
                return "";
            }
            toFilter = CommaSpaceRegex().Replace(toFilter, "");
            return toFilter.ToLower();
        }

        [GeneratedRegex("[,]")]
        private static partial Regex CommaRegex();
        public string RegexWithSpace(string? toFilter)
        {
            if (toFilter == null)
            {
                return "";
            }
            toFilter = CommaRegex().Replace(toFilter, "");
            return toFilter.ToLower();
        }

        [GeneratedRegex("[^a-zA-Z0-9]")]
        private static partial Regex NonAlphanumericRegex();
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
            toFilter = NonAlphanumericRegex().Replace(toFilter, "");
            return toFilter.ToLower();
        }

        [GeneratedRegex("<.*?>")]
        private static partial Regex HtmlRegex();
        [GeneratedRegex("\\[.*?\\]")]
        private static partial Regex EckigRegex();
        [GeneratedRegex("\\(.*?\\)")]
        private static partial Regex RundRegex();
        [GeneratedRegex("\u202F")] // narrow no-break space
        private static partial Regex WeirdSpaceRegex();
        public string StripHTML(string inputString)
        {
            // The four replacements must stay sequential in this order: e.g. "[a<b]c>"
            // yields "[a" here but "c>" with a combined single-pass alternation.
            return WeirdSpaceRegex().Replace(RundRegex().Replace(EckigRegex().Replace(HtmlRegex().Replace(inputString, ""), ""), ""), "").Trim();
        }

        [GeneratedRegex("([0-9]+\\.) ")]
        private static partial Regex PositionRegex();
        public string RemovePositions(string userString)
        {
            return PositionRegex().Replace(userString, "");
        }

        [GeneratedRegex("(- [0-9]( \\| [0-9])*)")]
        private static partial Regex NumberReplayRegex();
        public string RemoveNumberReplays(string userString)
        {
            return NumberReplayRegex().Replace(userString, "").Trim();
        }

        [GeneratedRegex("reactions:\n.*")]
        private static partial Regex ReactionRegex();
        public string RemoveReactions(string lineString)
        {
            return ReactionRegex().Replace(lineString, "").Trim();
        }
    }
}
