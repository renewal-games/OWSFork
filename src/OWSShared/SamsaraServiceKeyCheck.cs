using System;

namespace OWSShared
{
    /// <summary>
    /// The zone servers' server-to-server key (X-Samsara-Service-Key), read from a server-only env var and never present
    /// in client configs. Fail closed: outside the Development environment, an unset key rejects every caller rather
    /// than opening the gate, so a missing env var on a deploy surfaces as refusals.
    /// </summary>
    public static class SamsaraServiceKeyCheck
    {
        public const string Header = "X-Samsara-Service-Key";
        public const string EnvironmentVariable = "OWS_SAMSARA_SERVICE_KEY";
        private static string Configured;
        private static bool IsDevelopmentEnvironment;

        static SamsaraServiceKeyCheck() => ReadEnvironment();

        /// <summary>True when OWS_SAMSARA_SERVICE_KEY is set, so callers are checked against it.</summary>
        public static bool IsConfigured => !string.IsNullOrEmpty(Configured);

        public static bool IsValid(string provided)
        {
            if (string.IsNullOrEmpty(Configured)) return IsDevelopmentEnvironment;
            return !string.IsNullOrEmpty(provided)
                   && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                       System.Text.Encoding.UTF8.GetBytes(provided),
                       System.Text.Encoding.UTF8.GetBytes(Configured));
        }

        // Read once per process. Tests call it again after changing the environment, since a test host does not reset it.
        internal static void ReadEnvironment()
        {
            Configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
            IsDevelopmentEnvironment = string.Equals(
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);
        }
    }
}
