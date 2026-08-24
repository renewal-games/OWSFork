using System;

namespace OWSData.Models.Composites
{
    /// <summary>
    /// Flat view of a character for the management console character grids.
    /// Deliberately narrow: identity, ownership and the moderation flags only.
    /// </summary>
    public class AdminCharacterSummary
    {
        public int CharacterID { get; set; }
        public Guid? UserGUID { get; set; }
        public string CharName { get; set; }
        public string Email { get; set; }
        public int CharacterLevel { get; set; }
        public string MapName { get; set; }
        public string ClassName { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsModerator { get; set; }
        // Makes GetClientServerIp hand this character 127.0.0.1 instead of the zone server's
        // real IP, so a client on the same machine as the zone server can connect.
        public bool IsInternalNetworkTestUser { get; set; }
        public DateTime LastActivity { get; set; }
        public DateTime CreateDate { get; set; }
    }
}
