using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OWSPublicAPI;
using Xunit;

namespace OWSTests.PublicAPI
{
    /// <summary>
    /// The Public API's per-IP auth rate limit cannot be dodged by sending a different X-Forwarded-For on each request.
    /// The header counts only on connections from the configured proxy (Caddy).
    /// </summary>
    public class ClientIpRateLimitingTests
    {
        private const string LoginPath = "/api/Users/LoginAndCreateSession";
        private static readonly IPAddress DirectClient = IPAddress.Parse("203.0.113.5");
        private static readonly IPAddress Caddy = IPAddress.Parse("10.253.253.2");

        [Theory]
        [InlineData(null)]
        [InlineData("10.253.253.2")]
        public async Task DirectClient_RotatingXForwardedFor_StillHitsAuthLimit(string trustedProxies)
        {
            using IHost host = await StartHost(trustedProxies);

            List<HttpStatusCode> statuses = new();
            for (int i = 0; i <= ClientIpRateLimiting.AuthPermitLimit; i++)
            {
                statuses.Add(await Login(host, DirectClient, $"198.51.100.{i + 1}"));
            }

            Assert.All(statuses.GetRange(0, ClientIpRateLimiting.AuthPermitLimit), status => Assert.Equal(HttpStatusCode.Unauthorized, status));
            Assert.Equal(HttpStatusCode.TooManyRequests, statuses[ClientIpRateLimiting.AuthPermitLimit]);
        }

        [Fact]
        public async Task TrustedProxy_RateLimitsEachForwardedClientSeparately()
        {
            using IHost host = await StartHost("10.253.253.2");

            for (int i = 0; i <= ClientIpRateLimiting.AuthPermitLimit; i++)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, await Login(host, Caddy, $"198.51.100.{i + 1}"));
            }
        }

        [Fact]
        public async Task TrustedProxy_UsesOnlyTheAddressCaddyAppended()
        {
            using IHost host = await StartHost("10.253.253.2");

            // A client behind Caddy puts its own guess in front; Caddy appends the real peer, 198.51.100.7.
            for (int i = 0; i < ClientIpRateLimiting.AuthPermitLimit; i++)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, await Login(host, Caddy, $"192.0.2.{i + 1}, 198.51.100.7"));
            }
            Assert.Equal(HttpStatusCode.TooManyRequests, await Login(host, Caddy, "192.0.2.200, 198.51.100.7"));
        }

        [Fact]
        public async Task ServiceKey_IsNotLimited_WrongKeyIs()
        {
            using IHost host = await StartHost(null, "zone-key");

            for (int i = 0; i <= 3 * ClientIpRateLimiting.AuthPermitLimit; i++)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, await Login(host, DirectClient, null, "zone-key"));
            }
            for (int i = 0; i < ClientIpRateLimiting.AuthPermitLimit; i++)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, await Login(host, DirectClient, null, "wrong-key"));
            }
            Assert.Equal(HttpStatusCode.TooManyRequests, await Login(host, DirectClient, null, "wrong-key"));
        }

        [Fact]
        public void ParseTrustedProxies_RejectsNonAddresses()
        {
            Assert.Empty(ClientIpRateLimiting.ParseTrustedProxies(" "));
            Assert.Equal(new[] { Caddy, IPAddress.Parse("::1") }, ClientIpRateLimiting.ParseTrustedProxies("10.253.253.2, ::1"));
            Assert.Throws<FormatException>(() => ClientIpRateLimiting.ParseTrustedProxies("10.253.253.0/28"));
        }

        private static async Task<IHost> StartHost(string trustedProxies, string serviceKey = null)
        {
            IHost host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(services => services.AddClientIpRateLimiter(serviceKey))
                    .Configure(app =>
                    {
                        app.UseClientIpRateLimiter(trustedProxies);
                        // Stands in for a wrong-password login.
                        app.Run(context =>
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return Task.CompletedTask;
                        });
                    }))
                .Build();
            await host.StartAsync();
            return host;
        }

        private static async Task<HttpStatusCode> Login(IHost host, IPAddress peer, string xForwardedFor, string serviceKey = null)
        {
            HttpContext context = await host.GetTestServer().SendAsync(c =>
            {
                c.Connection.RemoteIpAddress = peer;
                c.Request.Method = HttpMethods.Post;
                c.Request.Path = LoginPath;
                if (xForwardedFor != null) c.Request.Headers["X-Forwarded-For"] = xForwardedFor;
                if (serviceKey != null) c.Request.Headers[OWSShared.SamsaraServiceKeyCheck.Header] = serviceKey;
            });
            return (HttpStatusCode)context.Response.StatusCode;
        }
    }
}
