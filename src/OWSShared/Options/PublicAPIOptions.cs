using System;
using System.Collections.Generic;
using System.Text;

namespace OWSShared.Options
{
    public class PublicAPIOptions
    {
        public const string SectionName = "OWSPublicAPIConfig";
        public double SecondsToWaitForServerSpinUp { get; set; }
        public double SecondsToWaitInBetweenSpinUpPolling { get; set; }
        public double SecondsToWaitBeforeFirstPollForSpinUp { get; set; }
        public string OWSInstanceMangementBaseAddress { get; set; }
        // Comma-separated IPs of reverse proxies whose X-Forwarded-For is trusted (the Caddy TLS proxy). Empty: none.
        public string TrustedProxies { get; set; }
    }
}
