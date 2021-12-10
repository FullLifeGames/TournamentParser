namespace UsersToTournamentMatches
{
    public class Thread
    {
        public string Link { get; set; }
        public string Name { get; set; }
        public bool Locked { get; set; }

        public override string ToString()
        {
            return Name;
        }

    }
}
