using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace OWSShared.Middleware
{
    /// <summary>
    /// Server-to-server gate for the internal APIs (InstanceManagement, CharacterPersistence). X-CustomerGUID is the
    /// tenant id every game client sends, so it routes requests but proves nothing; these routes additionally need
    /// X-Samsara-Service-Key, the secret only the backend services, the instance launcher and the zone servers hold.
    /// </summary>
    public sealed class ServiceKeyGateSettings
    {
        public const string RequireEnvVar = "OWS_REQUIRE_SERVICE_KEY";

        /// <summary>When false, a caller that presents no key is let through (a wrong key is still rejected).</summary>
        public bool Required { get; init; } = true;

        public string ConfiguredKey { get; init; }

        public bool IsDevelopment { get; init; }

        public string[] ProtectedPathPrefixes { get; init; } = { "/api" };

        public string[] AnonymousPathPrefixes { get; init; } = { "/swagger", "/health", "/api/System/Status" };

        /// <summary>
        /// Required unless OWS_REQUIRE_SERVICE_KEY=false. Fail closed: outside Development, a required gate with no
        /// OWS_SAMSARA_SERVICE_KEY configured rejects every caller, so a missing env var surfaces as 401s.
        /// </summary>
        public static ServiceKeyGateSettings FromEnvironment()
        {
            return new ServiceKeyGateSettings
            {
                Required = !string.Equals(Environment.GetEnvironmentVariable(RequireEnvVar), "false", StringComparison.OrdinalIgnoreCase),
                ConfiguredKey = SamsaraServiceKeyCheck.ConfiguredKey,
                IsDevelopment = SamsaraServiceKeyCheck.IsDevelopment
            };
        }

        /// <summary>What startup should log about this posture outside Development, or null when it is the secure one.</summary>
        public string PostureWarning(string serviceName)
        {
            if (IsDevelopment) return null;
            if (!Required)
                return $"{serviceName} internal API: {RequireEnvVar}=false, so X-CustomerGUID (not a secret) is the only gate for callers that send no {SamsaraServiceKeyCheck.Header}. " +
                       $"Remove it once every zone server sends the key.";
            if (string.IsNullOrEmpty(ConfiguredKey))
                return $"{serviceName} internal API: OWS_SAMSARA_SERVICE_KEY is unset, so every {string.Join(", ", ProtectedPathPrefixes)} call is rejected with 401.";
            return null;
        }

        public bool IsAuthorized(PathString path, string providedKey)
        {
            if (!ProtectedPathPrefixes.Any(prefix => prefix.Length == 0 || path.StartsWithSegments(prefix))
                || AnonymousPathPrefixes.Any(prefix => path.StartsWithSegments(prefix)))
            {
                return true;
            }

            if (string.IsNullOrEmpty(ConfiguredKey))
            {
                return !Required || IsDevelopment;
            }

            if (SamsaraServiceKeyCheck.Matches(providedKey, ConfiguredKey))
            {
                return true;
            }

            // A wrong key is rejected whatever the toggle says, so a misconfigured caller fails now rather than when
            // the gate is switched on. An empty header counts as absence, not a wrong key.
            return !Required && string.IsNullOrEmpty(providedKey);
        }
    }

    public class RequireServiceKeyMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ServiceKeyGateSettings _settings;

        public RequireServiceKeyMiddleware(RequestDelegate next, ServiceKeyGateSettings settings)
        {
            _next = next;
            _settings = settings;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            context.Request.Headers.TryGetValue(SamsaraServiceKeyCheck.Header, out var provided);

            if (!_settings.IsAuthorized(context.Request.Path, provided.ToString()))
            {
                context.Response.Clear();
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }

            await _next(context);
        }
    }
}
