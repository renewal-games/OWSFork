using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using OWSShared.Interfaces;

namespace OWSShared.Middleware
{
    public class StoreCustomerGUIDMiddleware : IMiddleware
    {
        // Fail closed: a request is rejected with 401 when its path matches ProtectedPathPrefixes,
        // does not match AnonymousPathPrefixes, and lacks a valid X-CustomerGUID header.
        // Services override these in Configure()/Program.cs before the pipeline starts serving.
        // gRPC hosts (Party, Chat, Guild) protect only "/api" because gRPC requests carry the
        // CustomerGUID inside the message, not the HTTP header. An empty prefix means "all paths".
        public static string[] ProtectedPathPrefixes { get; set; } = { "" };
        public static string[] AnonymousPathPrefixes { get; set; } = { "/swagger", "/health", "/api/System/Status" };

        private readonly IHeaderCustomerGUID _customerGuid;

        public StoreCustomerGUIDMiddleware(IHeaderCustomerGUID customerGuid)
        {
            _customerGuid = customerGuid;
        }

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            string headerValue = context.Request.Headers.FirstOrDefault(x =>
                string.Equals(x.Key, "X-CustomerGUID", StringComparison.OrdinalIgnoreCase)).Value.ToString();

            Guid.TryParse(headerValue, out Guid customerGuid);
            _customerGuid.CustomerGUID = customerGuid;

            if (customerGuid == Guid.Empty && RequiresCustomerGuid(context.Request.Path))
            {
                context.Response.Clear();
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }

            await next(context);
        }

        private static bool RequiresCustomerGuid(PathString path)
        {
            return ProtectedPathPrefixes.Any(prefix => prefix.Length == 0 || path.StartsWithSegments(prefix))
                && !AnonymousPathPrefixes.Any(prefix => path.StartsWithSegments(prefix));
        }
    }
}
