using System;
using System.Net.Http.Headers;

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
        private static readonly string Configured = Environment.GetEnvironmentVariable("OWS_SAMSARA_SERVICE_KEY");
        private static readonly bool IsDevelopmentEnvironment = string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);

        /// <summary>The key this process was started with, or null. Backend services send it on their internal calls.</summary>
        public static string ConfiguredKey => Configured;

        public static bool IsDevelopment => IsDevelopmentEnvironment;

        public static bool IsValid(string provided)
        {
            if (string.IsNullOrEmpty(Configured)) return IsDevelopmentEnvironment;
            return Matches(provided, Configured);
        }

        /// <summary>Constant-time comparison of a presented key against the configured one; false when either is empty.</summary>
        public static bool Matches(string provided, string configured)
        {
            return !string.IsNullOrEmpty(provided)
                   && !string.IsNullOrEmpty(configured)
                   && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                       System.Text.Encoding.UTF8.GetBytes(provided),
                       System.Text.Encoding.UTF8.GetBytes(configured));
        }

        /// <summary>
        /// Adds the service key to an outgoing internal request (InstanceManagement, CharacterPersistence). No-op when
        /// this process has no key, which is the Development setup; the receiving gate decides whether that is enough.
        /// </summary>
        public static void AddHeader(HttpHeaders headers, string key = null)
        {
            key ??= Configured;
            if (!string.IsNullOrEmpty(key))
            {
                headers.Remove(Header);
                headers.Add(Header, key);
            }
        }
    }
}
