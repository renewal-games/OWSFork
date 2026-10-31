using System;
using System.Runtime.CompilerServices;

namespace OWSTests
{
    /// <summary>
    /// The one X-Samsara-Service-Key for the whole test process. OWSShared.SamsaraServiceKeyCheck reads
    /// OWS_SAMSARA_SERVICE_KEY once, so every test that needs a configured key shares this value, set before any test runs.
    /// </summary>
    internal static class TestServiceKey
    {
        public const string Key = "test-service-key";

        [ModuleInitializer]
        internal static void SetServiceKey()
        {
            Environment.SetEnvironmentVariable("OWS_SAMSARA_SERVICE_KEY", Key);
        }
    }
}
