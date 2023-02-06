using System;
using System.Collections.Generic;
using System.Text;
using TournamentParser.Data;

namespace TournamentParser.Core.Data
{
    public class LineDataHandler
    {
        public bool BlockStarted { get; set; }
        public string BlockText { get; set; } = "";
        public bool PostStarted { get; set; }
        public int PostLikes { get; set; }
        public int PostNumber { get; set; }
        public DateTime PostDate { get; set; } = DateTime.Now;
        public string PostedBy { get; set; } = "";
        public string LastLine { get; set; } = "";
        public string PostLink { get; set; } = "";
        public bool LikeStarted { get; set; }
        public bool TimerHeader { get; set; }
        public StringBuilder FullPost { get; set; } = new StringBuilder("");
        public IList<string> FullImportantSiteBits { get; set; } = new List<string>();
        public bool TakePost { get; set; }
        public bool CanTakeReplay { get; set; }
        public int DataUserId { get; set; } = -1;
        public string UserLink { get; set; } = "";
        public TournamentMatch? LastMatch { get; set; } = null;
    }
}
