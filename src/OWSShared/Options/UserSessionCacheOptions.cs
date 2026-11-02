namespace OWSShared.Options
{
    public class UserSessionCacheOptions
    {
        public const string SectionName = "UserSessionCacheOptions";
        public const string DefaultConnectionString = "localhost:6379";
        public const string DefaultKeyPrefix = "user:session:";

        // Off by default: session caching only engages when explicitly enabled and a cache
        // is provisioned. When false, the caching layer is a pure pass-through to the DB.
        public bool Enabled { get; set; } = false;

        // How long a cached session is served before revalidating against the DB. Kept short
        // because the session record embeds character fields (position, selected character)
        // that change during play; session-mutating writes also invalidate explicitly. Must be
        // positive while Enabled (AddUserSessionCacheOptions refuses to start otherwise): a
        // non-positive TTL caches sessions with no expiry, so a missed invalidation never heals.
        public int SessionTtlSeconds { get; set; } = 60;

        /// <summary>Why these options are unusable, or null when they are fine.</summary>
        public string ValidationError()
        {
            return Enabled && SessionTtlSeconds <= 0
                ? $"{SectionName}:SessionTtlSeconds must be greater than 0 while session caching is enabled (was {SessionTtlSeconds})."
                : null;
        }

        public string ConnectionString { get; set; } = DefaultConnectionString;
        public string Password { get; set; }
        public int Database { get; set; }
        public string KeyPrefix { get; set; } = DefaultKeyPrefix;
    }
}
