namespace KSP2MultiplayerRedux.Networking
{
    /// <summary>
    /// Holds information about a connected player.
    /// </summary>
    public class PlayerInfo
    {
        public int PeerId { get; set; }
        public string SteamName { get; set; } = "Unknown";
        public string PlayerIdString { get; set; } = "Unknown";
        public int Ping { get; set; }
        public bool IsHost { get; set; }
        public bool IsAdmin { get; set; }

        /// <summary>Current warp vote: true=approve, false=deny, null=pending</summary>
        public bool? WarpVote { get; set; }

        public PlayerInfo() { }

        public PlayerInfo(int peerId, string steamName, string playerIdString = "Unknown", bool isHost = false, bool isAdmin = false)
        {
            PeerId = peerId;
            SteamName = steamName;
            PlayerIdString = playerIdString;
            IsHost = isHost;
            IsAdmin = isAdmin;
            Ping = 0;
            WarpVote = null;
        }
    }
}
