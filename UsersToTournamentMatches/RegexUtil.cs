using System.Text.RegularExpressions;

namespace UsersToTournamentMatches
{
    public class RegexUtil
    {

        private readonly Regex rgx = new("[, ]");
        public string Regex(string toFilter)
        {
            toFilter = rgx.Replace(toFilter, "");
            return toFilter.ToLower();
        }

        private readonly Regex rgxWithSpace = new("[,]");
        public string RegexWithSpace(string toFilter)
        {
            toFilter = rgxWithSpace.Replace(toFilter, "");
            return toFilter.ToLower();
        }

        private readonly Regex rgxWithAbc = new("[^a-zA-Z0-9]");
        public string RegexWithABC(string toFilter)
        {
            if (toFilter.Contains(" "))
            {
                toFilter = toFilter.Substring(0, toFilter.LastIndexOf(" "));
            }
            toFilter = rgxWithAbc.Replace(toFilter, "");
            return toFilter.ToLower();
        }

        private readonly Regex htmlRegex = new("<.*?>");
        private readonly Regex eckigRegex = new("\\[.*?\\]");
        private readonly Regex rundRegex = new("\\(.*?\\)");
        public string StripHTML(string inputString)
        {
            return rundRegex.Replace(eckigRegex.Replace(htmlRegex.Replace(inputString, ""), ""), "").Trim();
        }

    }
}
