namespace OWSData.Models
{
    /// <summary>
    /// Region tags shared by the login server list (GameServersConfig.Servers[].Region), the
    /// instance launcher (OWSInstanceLauncherOptions.ServerRegion), WorldServers.ServerRegion
    /// and Users.PreferredServerRegion. Values are free-form strings, compared with exact
    /// equality in SQL, so every producer must agree on spelling and casing.
    ///
    /// This is the one authoritative definition of the fallback region. It is duplicated as a
    /// literal in Databases/Postgres/SamsaraUpdates/AddServerRegionRouting_2026-09-16.sql
    /// (the WorldServers column default, which covers the deploy window before the API images
    /// are rebuilt) -- keep the two in sync.
    /// </summary>
    public static class ServerRegions
    {
        public const string Default = "NA-EAST";

        /// <summary>
        /// Resolves a region that may be absent. A launcher built before ServerRegion existed,
        /// a launcher with the key left blank, and a user who has never picked a server all
        /// land in the default region rather than being stranded with no host.
        /// </summary>
        public static string Normalize(string region)
            => string.IsNullOrWhiteSpace(region) ? Default : region.Trim();
    }
}
