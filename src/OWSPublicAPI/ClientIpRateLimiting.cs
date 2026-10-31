using System;
using System.Linq;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace OWSPublicAPI
{
    /// <summary>
    /// Per-client-IP throttling for the internet-facing API, and the forwarded-header handling that decides what the
    /// client IP is. Auth endpoints (login, register, Steam) get a tighter budget than general traffic to blunt brute force.
    /// </summary>
    public static class ClientIpRateLimiting
    {
        public const int AuthPermitLimit = 10;
        public const int GeneralPermitLimit = 60;
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

        public static IServiceCollection AddClientIpRateLimiter(this IServiceCollection services)
        {
            return services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                {
                    string clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    bool isAuthPath = httpContext.Request.Path.StartsWithSegments("/api/Users");
                    return RateLimitPartition.GetFixedWindowLimiter(
                        (isAuthPath ? "auth:" : "general:") + clientIp,
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = isAuthPath ? AuthPermitLimit : GeneralPermitLimit,
                            Window = Window,
                            QueueLimit = 0
                        });
                });
            });
        }

        /// <summary>
        /// Restores the real client IP from X-Forwarded-For, but only on connections from <paramref name="trustedProxies"/>
        /// (comma-separated IP addresses: the Caddy TLS proxy). With none configured the header is ignored and the socket
        /// peer is the client IP, which is right when the API port is published directly. Then applies the rate limiter.
        /// </summary>
        public static IApplicationBuilder UseClientIpRateLimiter(this IApplicationBuilder app, string trustedProxies)
        {
            IPAddress[] proxies = ParseTrustedProxies(trustedProxies);
            if (proxies.Length > 0)
            {
                var forwardedHeadersOptions = new ForwardedHeadersOptions
                {
                    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                    // Caddy appends the address it saw; anything to the left of that came from the client.
                    ForwardLimit = 1
                };
                // The defaults trust loopback. Only the configured proxy is trusted.
                forwardedHeadersOptions.KnownNetworks.Clear();
                forwardedHeadersOptions.KnownProxies.Clear();
                foreach (IPAddress proxy in proxies)
                {
                    forwardedHeadersOptions.KnownProxies.Add(proxy);
                }
                app.UseForwardedHeaders(forwardedHeadersOptions);
            }

            return app.UseRateLimiter();
        }

        public static IPAddress[] ParseTrustedProxies(string trustedProxies)
        {
            if (string.IsNullOrWhiteSpace(trustedProxies))
            {
                return Array.Empty<IPAddress>();
            }

            return trustedProxies
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(proxy => IPAddress.TryParse(proxy, out IPAddress address)
                    ? address
                    : throw new FormatException($"OWSPublicAPIConfig:TrustedProxies has an entry that is not an IP address: '{proxy}'"))
                .ToArray();
        }
    }
}
