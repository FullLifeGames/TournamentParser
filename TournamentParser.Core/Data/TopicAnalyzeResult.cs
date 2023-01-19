using System;
using System.Collections.Generic;

namespace TournamentParser.Core.Data
{
    public class TopicAnalyzeResult
    {
        public IList<string> CollectedLinks = null!;
        public int NumberOfPages;
        public DateTime LastPost;
    }
}
