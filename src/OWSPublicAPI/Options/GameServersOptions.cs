using System.Collections.Generic;

namespace OWSPublicAPI.Options
{
    public class GameServerEntry
    {
        public string Name { get; set; }
        public string Region { get; set; }
        // "online" or "maintenance"
        public string Status { get; set; } = "online";
        // "low", "medium", "high" or "full". Config-stubbed for now; a real value would
        // count active UserSessions per server behind this same contract.
        public string Population { get; set; } = "low";
        // URL the client times a request against to display latency.
        public string PingHost { get; set; }
    }

    public class GameServersOptions
    {
        public const string SectionName = "GameServersConfig";

        public List<GameServerEntry> Servers { get; set; } = new List<GameServerEntry>();
    }
}
