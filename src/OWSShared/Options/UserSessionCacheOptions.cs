namespace OWSShared.Options
{
    public class UserSessionCacheOptions
    {
        public const string SectionName = "UserSessionCacheOptions";
        public const string DefaultConnectionString = "localhost:6379";
        public const string DefaultKeyPrefix = "user:session:";

        public string ConnectionString { get; set; } = DefaultConnectionString;
        public string Password { get; set; }
        public int Database { get; set; }
        public string KeyPrefix { get; set; } = DefaultKeyPrefix;
    }
}
